using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// Late-bound access to VS's internal <c>ITerminalService</c> — the terminal-mirror feature's only
    /// contact with the terminal contracts. HOW that service is
    /// acquired is itself a moving target and both known shapes are tried; see <see cref="ConnectAsync"/>.
    /// </summary>
    /// <remarks>
    /// EVERYTHING here is reflection ON PURPOSE (issue #51). The contracts DLL ships inside a VS
    /// install and has no NuGet package, so a compile-time reference makes the feature's existence
    /// depend on the BUILD machine: the released VSIX is built on a CI runner with no VS at all, so
    /// the conditional compilation this replaced took the whole mirror out of every published build
    /// — silently, since a compile-time absence can't log. Binding at runtime instead means
    /// the feature is always shipped and resolves against whatever devenv actually has loaded, on
    /// any VS edition or install path. The trade is no compile-time typing over an API that is
    /// internal (and therefore unstable) either way — so every lookup is explicit and every failure
    /// carries a reason string the log can show.
    /// The proxy is created from the type we resolve, so it is self-consistent; we deliberately bind
    /// the assembly by STRONG NAME into the default load context (not LoadFrom) so our type identity
    /// matches devenv's — the identity-split gotcha in AGENTS.md, reached here via LoadFrom only as
    /// a last resort.
    /// </remarks>
    internal sealed class VsTerminalService : IDisposable
    {
        private const string TerminalAssemblyName = "Microsoft.VisualStudio.Terminal";
        private const string ServiceTypeName = "Microsoft.VisualStudio.Terminal.ITerminalService";
        private const string VsServiceTypeName = "Microsoft.VisualStudio.Terminal.SVsTerminalService";
        private const string VsServiceFactoryTypeName = "Microsoft.VisualStudio.Terminal.IVsTerminalService";
        private const string BrokeredMonikerName = "Microsoft.VisualStudio.Terminal.TerminalService";
        private const string OptionsTypeName = "Microsoft.VisualStudio.Terminal.TerminalWindowOptions";
        private const string ContractsRelativePath = @"CommonExtensions\Microsoft\Terminal\Microsoft.VisualStudio.Terminal.dll";

        // Hand-rolled rather than TerminalServiceDescriptors.TerminalServiceDescriptor: that type's
        // static init pulls ServiceHub.Framework 4.10 types into our 4.8-baseline compilation
        // (CS1705). Conventions read off the live descriptor by reflection on VS 18.7 (2026-07-17);
        // in-proc the broker returns a local proxy, so these only matter as a match key.
        // Built per connect rather than held in a static field on purpose: a type initializer here
        // would drag ServiceHub into the load path of the cheap DescribeAvailability() probe, so a
        // broker problem could fault the startup diagnostic that exists to report problems.
        private static ServiceRpcDescriptor CreateDescriptor() => new ServiceJsonRpcDescriptor(
            new ServiceMoniker(BrokeredMonikerName, new Version(1, 0)),
            ServiceJsonRpcDescriptor.Formatters.MessagePack,
            ServiceJsonRpcDescriptor.MessageDelimiters.BigEndianInt32LengthHeader);

        private readonly object _service;
        private readonly Type _serviceType;
        private readonly MethodInfo _createRenderer;
        private readonly MethodInfo _createRendererWithOptions;
        private readonly Type _optionsType;
        private readonly MethodInfo _showNoActivate;
        private readonly MethodInfo _close;
        private readonly List<Tuple<EventInfo, Delegate>> _subscriptions = new List<Tuple<EventInfo, Delegate>>();

        private VsTerminalService(object service, Type serviceType, string acquiredVia)
        {
            _service = service;
            _serviceType = serviceType;
            AcquiredVia = acquiredVia;

            // Overload-rich API: pick the renderer overload that takes OUR stream, by shape.
            _createRenderer = FindMethod("CreateTerminalRendererAsync",
                p => p.Length == 5 && typeof(Stream).IsAssignableFrom(p[0].ParameterType));
            _showNoActivate = FindMethod("ShowNoActivateAsync", p => p.Length == 2 && p[0].ParameterType == typeof(Guid));
            _close = FindMethod("CloseAsync", p => p.Length == 2 && p[0].ParameterType == typeof(Guid));

            // The options overload is OPTIONAL — the mirror works without it, just noisily (see
            // CreateRendererAsync). A contract that no longer offers it must degrade, not throw.
            _optionsType = serviceType.Assembly.GetType(OptionsTypeName, throwOnError: false);
            if (_optionsType?.GetProperty("Focus") is null)
                _optionsType = null; // nothing here we couldn't already do
            if (_optionsType is not null)
                _createRendererWithOptions = TryFindMethod("CreateTerminalRendererAsync",
                    p => p.Length == 3
                         && typeof(Stream).IsAssignableFrom(p[1].ParameterType)
                         && p[2].ParameterType == _optionsType);

            Subscribe("TerminalClosed", nameof(OnTerminalClosedCore));
            Subscribe("TerminalResized", nameof(OnTerminalResizedCore));
        }

        /// <summary>
        /// Which of the two acquisition shapes actually produced the service (see
        /// <see cref="ConnectAsync"/>). Logged on connect: when this breaks again the first question
        /// is which shape THIS VS offers, and a log that records only the failure cannot answer it.
        /// </summary>
        public string AcquiredVia { get; }

        /// <summary>
        /// Why the pane had to be created with focus, or null when it wasn't. Reported by the caller's
        /// log — a focus steal is otherwise indistinguishable from the user clicking the pane.
        /// </summary>
        public string FocusSuppressionUnavailable { get; private set; }

        /// <summary>Raised with the closed pane's id (the user closed the terminal window).</summary>
        public Action<Guid> TerminalClosed { get; set; }

        /// <summary>Raised (terminalId, columns, rows) when a pane is resized.</summary>
        public Action<Guid, int, int> TerminalResized { get; set; }

        /// <summary>
        /// Cheap, load-free probe of whether the terminal contracts are present, for the startup log
        /// line — reads the file's metadata without pulling the assembly into the AppDomain (the
        /// mirror stays lazy; the pane's ~10s package load must not happen at window init).
        /// </summary>
        public static string DescribeAvailability()
        {
            var loaded = FindLoadedTerminalAssembly();
            if (loaded is not null)
                return "loaded (" + loaded.GetName().Version + "), offers " + DescribeShape(loaded);

            var probed = new List<string>();
            foreach (var path in ProbePaths())
            {
                probed.Add(path);
                if (!File.Exists(path))
                    continue;
                try { return "found " + path + " (" + AssemblyName.GetAssemblyName(path).Version + ")"; }
                catch (Exception ex) { return "found " + path + " but unreadable: " + ex.Message; }
            }
            return "NOT FOUND (probed: " + string.Join("; ", probed.ToArray()) + ")";
        }

        /// <summary>
        /// Which acquisition shape the loaded contracts declare — a free type lookup, and the thing
        /// that was missing from the log when 18.9 moved the service (the version did not change, so
        /// the startup line looked identical on the build where the feature worked and the one where
        /// it had silently stopped). Only offered for an already-loaded assembly: the probe's whole
        /// point is to stay load-free.
        /// </summary>
        private static string DescribeShape(Assembly contracts)
        {
            var vsService = contracts.GetType(VsServiceTypeName, throwOnError: false) is not null;
            var descriptors = contracts.GetType(
                "Microsoft.VisualStudio.Terminal.TerminalServiceDescriptors", throwOnError: false) is not null;
            if (vsService && descriptors)
                return "SVsTerminalService + brokered";
            if (vsService)
                return "SVsTerminalService";
            if (descriptors)
                return "brokered";
            return "NEITHER shape (VS contract changed?)";
        }

        /// <summary>
        /// Resolves the contracts, acquires the service, and wires the events. Throws with a reason
        /// (never returns null) — the caller logs it and disables the mirror for the session.
        /// </summary>
        /// <remarks>
        /// <para>
        /// There are TWO shapes, and which one a given VS offers is not something we can predict — so
        /// both are tried and the contracts assembly itself decides. VS 18.7 proffered
        /// <c>ITerminalService</c> as a BROKERED service under the moniker
        /// <c>Microsoft.VisualStudio.Terminal.TerminalService/1.0</c>. VS 18.9 DROPPED that
        /// registration: the terminal package's pkgdef no longer has the
        /// <c>[$RootKey$\BrokeredServices\…\TerminalService\1.0]</c> section (its sibling entries for
        /// <c>Terminal.Pty</c> and <c>Terminal.InternalSolutionService</c> are still there), the
        /// <c>TerminalServiceDescriptors</c> type is gone from the assembly, and the same contract is
        /// proffered as an ordinary async-queryable VS service instead — query
        /// <c>SVsTerminalService</c>, cast to <c>IVsTerminalService</c>, call
        /// <c>CreateTerminalService()</c>. Read off the shipped bits of 18.9.12112.369, against 18.7
        /// and VS 2022 17.14 (which has the descriptor type and no <c>SVsTerminalService</c> at all).
        /// </para>
        /// <para>
        /// The failure this replaces is worth remembering, because everything about it looked healthy:
        /// the contracts assembly resolved, its version was UNCHANGED (18.0.0.0 on both builds, so a
        /// version check would have caught nothing), every method we bind was still present with its
        /// old shape, and the broker itself answered — it just answered null, an unregistered moniker
        /// being an absence rather than an error. So a VS update killed the whole feature and left one
        /// line in the log ("ITerminalService not available from the broker") with no version, type or
        /// member to point at. Hence the aggregated failure text below: it names each shape it tried
        /// and why that one did not answer, so the next such move is one log line from a diagnosis.
        /// </para>
        /// <para>
        /// Order is deliberate — the VS-service shape first, since it is what current VS offers and its
        /// absence costs one type lookup, then the broker. Neither is VERSION-gated: the presence of
        /// the types decides, the same rule the ACP capabilities follow.
        /// </para>
        /// </remarks>
        public static async Task<VsTerminalService> ConnectAsync(CancellationToken cancellationToken)
        {
            var serviceType = ResolveServiceType();
            var failures = new List<string>();

            // Measured on 18.9 — a classic VS service that hands out the ITerminalService.
            var acquiredVia = "SVsTerminalService";
            var service = await TryGetFromVsServiceAsync(serviceType.Assembly, failures).ConfigureAwait(false);

            // Measured on 18.7 (and VS 2022 17.14) — the same contract, brokered.
            if (service is null)
            {
                acquiredVia = "broker";
                service = await TryGetFromBrokerAsync(serviceType, failures, cancellationToken).ConfigureAwait(false);
            }

            if (service is null)
                throw new InvalidOperationException(
                    ServiceTypeName + " could not be acquired from "
                    + serviceType.Assembly.GetName().FullName + " — " + string.Join("; ", failures.ToArray()));

            return new VsTerminalService(service, serviceType, acquiredVia);
        }

        /// <summary>
        /// The current shape (measured on 18.9): <c>SVsTerminalService</c> is a registered,
        /// async-queryable, free-threaded VS service whose <c>IVsTerminalService.CreateTerminalService()</c>
        /// returns the contract we want. Null (with a recorded reason) when this VS doesn't have it.
        /// Which build first offered it is deliberately not established — the types decide, so it
        /// never needs to be.
        /// </summary>
        private static async Task<object> TryGetFromVsServiceAsync(Assembly contracts, List<string> failures)
        {
            // The service GUID rides SVsTerminalService's own [Guid] attribute, read by GetServiceAsync
            // off the type — never a constant here, which would be a second thing to keep in step.
            var serviceKey = contracts.GetType(VsServiceTypeName, throwOnError: false);
            var factoryType = contracts.GetType(VsServiceFactoryTypeName, throwOnError: false);
            if (serviceKey is null || factoryType is null)
            {
                failures.Add(VsServiceTypeName + "/" + VsServiceFactoryTypeName
                    + " absent from the contracts (pre-18.9 shape)");
                return null;
            }

            object registered;
            try
            {
                registered = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(serviceKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add("SVsTerminalService query threw: " + Unwrap(ex));
                return null;
            }
            if (registered is null)
            {
                failures.Add("SVsTerminalService is not registered in this VS (terminal component missing?)");
                return null;
            }

            var create = factoryType.GetMethod("CreateTerminalService", Type.EmptyTypes);
            if (create is null)
            {
                failures.Add(VsServiceFactoryTypeName + ".CreateTerminalService() is missing (VS contract changed?)");
                return null;
            }

            try
            {
                var service = create.Invoke(registered, null);
                if (service is null)
                    failures.Add("CreateTerminalService() returned null");
                return service;
            }
            catch (Exception ex)
            {
                failures.Add("CreateTerminalService() threw: " + Unwrap(ex));
                return null;
            }
        }

        /// <summary>
        /// The older shape (measured on 18.7 and on VS 2022 17.14): the same contract proffered as a
        /// brokered service. Null (with a recorded reason) when the moniker isn't registered — which is
        /// exactly what a VS that has moved on looks like, an unknown moniker being an absence rather
        /// than an error.
        /// </summary>
        private static async Task<object> TryGetFromBrokerAsync(
            Type serviceType, List<string> failures, CancellationToken cancellationToken)
        {
            try
            {
                var container = await AsyncServiceProvider.GlobalProvider
                    .GetServiceAsync(typeof(SVsBrokeredServiceContainer)) as IBrokeredServiceContainer;
                if (container is null)
                {
                    failures.Add("SVsBrokeredServiceContainer unavailable");
                    return null;
                }
                var broker = container.GetFullAccessServiceBroker();

                // broker.GetProxyAsync<ITerminalService>(Descriptor, default, ct) — generic over a type
                // we only have at runtime, so the call and its ValueTask<T> unwrap are both reflected.
                var getProxy = typeof(IServiceBroker).GetMethod(nameof(IServiceBroker.GetProxyAsync))
                    .MakeGenericMethod(serviceType);
                var valueTask = getProxy.Invoke(broker,
                    new object[] { CreateDescriptor(), default(ServiceActivationOptions), cancellationToken });
                var task = (Task)valueTask.GetType().GetMethod("AsTask").Invoke(valueTask, null);
                await task.ConfigureAwait(false);
                var service = task.GetType().GetProperty("Result").GetValue(task);
                if (service is null)
                    failures.Add("the broker offers no " + BrokeredMonikerName + " 1.0");
                return service;
            }
            catch (Exception ex)
            {
                failures.Add("broker: " + Unwrap(ex));
                return null;
            }
        }

        // Everything here is Invoke'd, so a failure inside arrives wrapped.
        private static string Unwrap(Exception ex) =>
            (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;

        /// <summary>
        /// Creates a renderer pane fed by <paramref name="ptyStream"/> WITHOUT taking keyboard focus;
        /// returns its id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Focus has to be suppressed AT CREATION, and that is the whole reason this method is not the
        /// one-liner it used to be (issue #124). Read off the shipped IL of VS 18.7: the 5-argument
        /// overload builds a <c>TerminalWindowOptions</c> internally and sets only Name /
        /// AllowUserInput / AutoResize on it — while that type's constructor sets
        /// <c>Focus = true</c> — and hands it to <c>CreateRendererToolWindowAsync</c>, which passes
        /// <c>options.Focus</c> straight into <c>CreateToolWindowAsync</c>. So the pane takes the
        /// keyboard as it is built, and the <c>ShowNoActivateAsync</c> that follows can only reveal an
        /// already-created pane — it has nothing to give back. Typing into the chat box while a
        /// command ran therefore ended up in the terminal.
        /// </para>
        /// <para>
        /// The options overload is the only lever: it is our object that reaches
        /// <c>CreateToolWindowAsync</c>, so <c>Focus = false</c> is honoured. It stays OPTIONAL, and
        /// the old call is kept as the fallback rather than deleted, for the reason the whole class is
        /// reflection: this is an internal API and the shape can change under us. Losing focus
        /// politeness is a far better failure than losing the mirror, so anything unexpected — the
        /// overload missing, or the call throwing (it carries a contract type across the broker, where
        /// the plain overload carries only primitives) — falls back to what shipped before and records
        /// why.
        /// </para>
        /// </remarks>
        public async Task<Guid> CreateRendererAsync(
            Stream ptyStream, string name, bool allowUserInput, bool autoResize, CancellationToken cancellationToken)
        {
            if (_createRendererWithOptions is not null)
            {
                try
                {
                    var options = Activator.CreateInstance(_optionsType);
                    SetOption(options, "Name", name);
                    SetOption(options, "AllowUserInput", allowUserInput);
                    SetOption(options, "AutoResize", autoResize);
                    SetOption(options, "Focus", false);

                    var id = await (Task<Guid>)_createRendererWithOptions.Invoke(
                        _service, new object[] { cancellationToken, ptyStream, options });
                    FocusSuppressionUnavailable = null;
                    return id;
                }
                catch (Exception ex)
                {
                    // Unwrap: everything here is Invoke'd, so a failure inside arrives wrapped.
                    FocusSuppressionUnavailable = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
                }
            }
            else
            {
                FocusSuppressionUnavailable =
                    OptionsTypeName + " or its 3-argument CreateTerminalRendererAsync is missing (VS contract changed?)";
            }

            return await (Task<Guid>)_createRenderer.Invoke(
                _service, new object[] { ptyStream, cancellationToken, name, allowUserInput, autoResize });
        }

        private void SetOption(object options, string property, object value)
        {
            var info = _optionsType.GetProperty(property);
            if (info is null)
                throw new InvalidOperationException(OptionsTypeName + "." + property + " is missing (VS contract changed?)");
            info.SetValue(options, value);
        }

        /// <summary>
        /// Makes an already-created pane VISIBLE without activating it. Note this is not what keeps
        /// the focus — by the time it runs, creation has already decided that (see
        /// <see cref="CreateRendererAsync"/>).
        /// </summary>
        public Task ShowNoActivateAsync(Guid terminalId, CancellationToken cancellationToken)
            => (Task)_showNoActivate.Invoke(_service, new object[] { terminalId, cancellationToken });

        /// <summary>Closes the pane.</summary>
        public Task CloseAsync(Guid terminalId, CancellationToken cancellationToken)
            => (Task)_close.Invoke(_service, new object[] { terminalId, cancellationToken });

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                try { subscription.Item1.RemoveEventHandler(_service, subscription.Item2); }
                catch { /* proxy may already be dead */ }
            }
            _subscriptions.Clear();
            (_service as IDisposable)?.Dispose();
        }

        private static Type ResolveServiceType()
        {
            var assembly = FindLoadedTerminalAssembly();
            var failures = new List<string>();

            if (assembly is null)
            {
                foreach (var path in ProbePaths())
                {
                    if (!File.Exists(path))
                    {
                        failures.Add(path + " (missing)");
                        continue;
                    }

                    // Strong-name load first: same identity devenv's own binding produces (VS's
                    // codeBase pkgdef entries resolve it), so our types unify with the terminal
                    // package's. LoadFrom would risk a second identity whose types don't.
                    try { assembly = Assembly.Load(AssemblyName.GetAssemblyName(path)); }
                    catch (Exception ex) { failures.Add(path + " (strong-name load: " + ex.Message + ")"); }

                    if (assembly is null)
                    {
                        try { assembly = Assembly.LoadFrom(path); }
                        catch (Exception ex) { failures.Add(path + " (LoadFrom: " + ex.Message + ")"); }
                    }

                    if (assembly is not null)
                        break;
                }
            }

            if (assembly is null)
                throw new InvalidOperationException(
                    TerminalAssemblyName + " could not be resolved — " + string.Join("; ", failures.ToArray()));

            var type = assembly.GetType(ServiceTypeName, throwOnError: false);
            if (type is null)
                throw new InvalidOperationException(
                    ServiceTypeName + " is missing from " + assembly.GetName().FullName + " (VS contract changed?)");
            return type;
        }

        private static Assembly FindLoadedTerminalAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, TerminalAssemblyName, StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }
            return null;
        }

        // devenv's IDE directory, which owns CommonExtensions\Microsoft\Terminal. BaseDirectory is
        // the IDE dir in-proc; the module path is the belt-and-braces second guess (an unusual host,
        // or a shadow-copied AppDomain).
        private static IEnumerable<string> ProbePaths()
        {
            var roots = new List<string>();
            try { roots.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { /* ignore */ }
            try { roots.Add(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)); }
            catch { /* MainModule can be denied; BaseDirectory is the normal path */ }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                var path = Path.Combine(root, ContractsRelativePath);
                if (seen.Add(path))
                    yield return path;
            }
        }

        private MethodInfo FindMethod(string name, Func<ParameterInfo[], bool> matches) =>
            TryFindMethod(name, matches)
            ?? throw new InvalidOperationException(
                "ITerminalService." + name + " has no overload matching the expected shape (VS contract changed?)");

        /// <summary>As <see cref="FindMethod"/>, but null for an overload we can do without.</summary>
        private MethodInfo TryFindMethod(string name, Func<ParameterInfo[], bool> matches)
        {
            foreach (var method in _serviceType.GetMethods())
            {
                if (method.Name != name)
                    continue;
                ParameterInfo[] parameters;
                // Some overloads take types from assemblies we can't load in every host; reading
                // their parameters throws, and that must not hide the overload we DO want.
                try { parameters = method.GetParameters(); }
                catch { continue; }
                if (matches(parameters))
                    return method;
            }
            return null;
        }

        // EventHandler<TArgs> demands an exactly-matching signature, so the handler is a generic
        // method closed over the runtime args type — (object, TerminalClosedEventArgs) etc.
        private void Subscribe(string eventName, string handlerName)
        {
            var eventInfo = _serviceType.GetEvent(eventName);
            if (eventInfo is null)
                throw new InvalidOperationException("ITerminalService." + eventName + " is missing (VS contract changed?)");
            var argsType = eventInfo.EventHandlerType.GetGenericArguments()[0];
            var handlerMethod = typeof(VsTerminalService)
                .GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(argsType);
            var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, this, handlerMethod);
            eventInfo.AddEventHandler(_service, handler);
            _subscriptions.Add(Tuple.Create(eventInfo, handler));
        }

        private void OnTerminalClosedCore<TArgs>(object sender, TArgs e)
        {
            try { TerminalClosed?.Invoke(ReadGuid(e, "TerminalGuid")); }
            catch { /* mirror events are cosmetic */ }
        }

        private void OnTerminalResizedCore<TArgs>(object sender, TArgs e)
        {
            try { TerminalResized?.Invoke(ReadGuid(e, "TerminalGuid"), ReadInt(e, "MaxColumns"), ReadInt(e, "MaxRows")); }
            catch { /* mirror events are cosmetic */ }
        }

        private static Guid ReadGuid(object args, string property) => (Guid)ReadProperty(args, property);

        private static int ReadInt(object args, string property) => (int)ReadProperty(args, property);

        private static object ReadProperty(object args, string property)
            => args.GetType().GetProperty(property).GetValue(args);
    }
}
