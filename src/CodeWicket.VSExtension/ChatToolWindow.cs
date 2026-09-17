using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using CodeWicket.UI.Views;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The chat tool window: hosts the shared WPF <see cref="ChatView"/>, builds the real
    /// VS-backed <see cref="VsIdeServices"/>, and launches the engine via <see cref="EngineClient"/>.
    /// Initialization is async (DTE/services need the UI thread and the window to be sited), so the
    /// window shows a status line first and swaps in the chat once ready.
    /// </summary>
    /// <remarks>
    /// The backend (and default model) come from the extension config file (see ExtensionConfig);
    /// the default is real Kiro. Debug overrides: CWKT_IDE=stub uses the disk-backed StubIdeServices
    /// instead of the live IDE; CWKT_PROVIDER overrides the configured provider ("kiro" or "fake").
    /// (launchSettings env does NOT reach the Exp devenv — that's why config is file-based.)
    /// </remarks>
    [Guid(ToolWindowGuidString)]
    public sealed class ChatToolWindow : ToolWindowPane
    {
        public const string ToolWindowGuidString = "1648c2e3-c10b-4315-8159-e7bdac21c566";

        private EngineClient _engine;

        // Kept so the workspace root can be re-resolved when the solution/folder opens or closes (the
        // running session's working directory is captured at start, so it must track those changes).
        private VsIdeServices _vsIde;
        // The agent's file writes, recorded by the shell's RPC target and read by the IDE's build and
        // test tools (issue #257). One instance shared by both halves, which live in this process.
        private readonly AgentWriteLedger _agentWrites = new AgentWriteLedger();
        private ChatViewModel _vm;

        // The chat control itself. Held because _host.Content is NEVER it: while starting it is a
        // TextBlock, on failure a TextBox, and on success the Grid that layers the view under the
        // loading overlay. `_host.Content as ChatView` was therefore null in every state, which made
        // FocusInput a no-op always.
        private ChatView _view;

        // Whether the pane is finished and UNCOVERED. Not the same question as "does a view-model
        // exist": _vm is assigned early and the opaque overlay stays up until the restore completes,
        // 53-945ms later and longer on a cold engine, so between the two the chat is live and invisible.
        // Both gestures below need the second answer, not the first.
        private bool _chatReady;

        // Whether an initialization is in flight, so a Restart cannot start a second one beside it.
        // UI-thread only.
        private bool _initializing;
        private IDisposable _workspaceSubscription;
        private PolicyPermissionHandler _permissionPolicy;
        private TerminalMirror _terminalMirror;
        private TerminalMirrorTee _terminalTee;

        // Stable host set as the pane Content once; we swap its inner content as init progresses.
        // (ToolWindowPane captures Content when the frame is created, so reassigning this.Content
        // later wouldn't update the display.)
        private readonly System.Windows.Controls.ContentControl _host;

        public ChatToolWindow() : base(null)
        {
            Caption = Branding.ChatWindowName;
            // The build stamp makes "which build am I actually running?" answerable at a glance —
            // stale-install confusion has burned us before (both experimental-hive and double-click installs).
            _host = new System.Windows.Controls.ContentControl
            {
                Content = MakeStatus($"Starting {Branding.ProductName}… (build {GetBuildStamp()})"),
            };
            Content = _host;
        }

        /// <remarks>
        /// The compile-time stamp, not the file's timestamp: a deploy rewrites every file's timestamp
        /// to the moment it was installed, which answers "when did I install this?" while reading as
        /// an answer to "which build is this?" (issue #136). Falls back to the install time only when
        /// there is no stamp to read, and says so.
        /// </remarks>
        private static string GetBuildStamp()
        {
            var stamp = BuildStamp.ForAssembly(typeof(ChatToolWindow).Assembly).Describe();
            if (!string.IsNullOrEmpty(stamp))
                return stamp;

            try
            {
                var location = typeof(ChatToolWindow).Assembly.Location;
                return "installed " + File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                return "unknown";
            }
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();

            // OnToolWindowCreated is synchronous; FileAndForget is the idiomatic way to launch the
            // async init (it logs faults to the activity log). VSSDK007 doesn't recognize it here.
#pragma warning disable VSSDK007 // Await/join tasks created from JoinableTaskFactory.RunAsync
            ThreadHelper.JoinableTaskFactory
                .RunAsync(InitializeAsync)
                .FileAndForget("code-wicket/toolwindow-init");
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// JIT-fault firewall around the real initialization. The body of <see cref="InitializeCoreAsync"/>
        /// touches CodeWicket.Shell/Core/UI and their dependency closure (StreamJsonRpc,
        /// Microsoft.Bcl.AsyncInterfaces, ...), so if any of those assemblies is missing from the
        /// installed VSIX, the CLR throws FileNotFoundException while JIT-compiling that method — i.e.
        /// at the *call site here*, not inside the callee's own try/catch. Keeping this wrapper's body
        /// restricted to framework + VSSDK types (Branding.ProductName is a const, inlined at compile
        /// time) means the wrapper itself always JITs, so a missing-dependency failure lands in this
        /// catch and is shown in the window instead of dying silently on the FileAndForget task
        /// (the "stuck on Starting..." hang, diagnosed 2026-07-09).
        /// </summary>
        private async Task InitializeAsync()
        {
            // Set on the UI thread before the first await, cleared in the finally, and read only by
            // RestartAsync on the same thread — so the pair needs no interlock and cannot be left
            // latched by a failure, which would disable Restart for the life of the session.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _initializing = true;
            try
            {
                await InitializeCoreAsync();
            }
            catch (Exception ex)
            {
                await ShowStartupFailureAsync(ex);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _initializing = false;
            }
        }

        /// <summary>
        /// Startup-failure sink: persists the full exception to a fixed log file (so it can be copied
        /// off a machine even when the UI can't be) and shows it in the window as selectable text.
        /// Framework/VSSDK types only — this must stay callable when our own assemblies won't load.
        /// </summary>
        private async Task ShowStartupFailureAsync(Exception ex)
        {
            var logPath = WriteStartupError(ex);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _host.Content = MakeErrorStatus(
                $"{Branding.ProductName} failed to start" +
                (logPath is null ? ":" : $" (details saved to {logPath}):") +
                "\n\n" + ex);
        }

        // Same directory as ExtensionConfig.LogDirectory, but deliberately NOT via that Shell type —
        // the whole point is to work when CodeWicket.Shell (or its dependencies) failed to load.
        //
        // Branding.StorageFolderName is safe here where StoragePaths.LogDirectory would not be: a
        // const string is baked into THIS assembly's IL at compile time, so reading it loads nothing,
        // while a static property is a call into Core. That is the whole reason the bare constant
        // exists alongside the resolver.
        private static string WriteStartupError(Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Branding.StorageFolderName, "logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "startup-error.log");
                File.AppendAllText(path,
                    $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} (build {GetBuildStamp()}) ==={Environment.NewLine}" +
                    ex + Environment.NewLine + Environment.NewLine);
                return path;
            }
            catch
            {
                return null; // logging is best-effort; the on-screen text still shows the exception
            }
        }

        private async Task InitializeCoreAsync()
        {
            try
            {
                // === Diagnostics first, before anything that can hang ===
                //
                // All diagnostic logs live under %LOCALAPPDATA%\code-wicket\logs with fixed names,
                // toggled in Options > Debug. Opening the log is deliberately the FIRST thing this
                // method does, ahead of every phase it is about to time: the failure being diagnosed
                // is a startup that never finishes, and a sink opened after the phase that hangs
                // records nothing about it. (It used to sit in the middle, after the IDE services were
                // built — i.e. after the most expensive phase and the prime suspect.) Nothing here
                // depends on config, so the move is free: the paths are static.
                //
                // The sweep is the directory-wide half of the retention policy (age + total budget,
                // and the only thing that reclaims logs whose feature was retired); the per-file
                // cap/roll is enforced by each writer. See DiagnosticLog.
                //
                // Scheduled, not run: from `ide services` this method is one straight-line synchronous
                // block on the main thread (VsIdeServices.CreateAsync switches to it and there is no
                // await between there and EngineClient.Launch below), and bulk file enumeration +
                // deletion must never run there — issue #100. The single yield above is before all of
                // that and is there precisely to hand the thread back for one frame. Nothing below depends on the pass having finished,
                // and the rolls it coalesces with are scheduled the same way from inside
                // DiagnosticLog. The directory is still created inline: it's one syscall, and the
                // roll a few lines down needs it to exist.
                Directory.CreateDirectory(ExtensionConfig.LogDirectory);
                // Always-on engine stderr log (startup banner + faults); plus the opt-in raw channel
                // tee for diagnosing stream corruption. Also receives permission-policy decisions.
                var stderrLog = ExtensionConfig.EngineStderrLogFile;
                // Retention reports itself into engine.log ("[retention] logs: 142 ms, 302 files,
                // …"). Wired BEFORE the first sweep is scheduled, or the startup pass — the
                // expensive one, and the one a slow-machine report is about — writes nothing.
                DiagnosticLog.RetentionLog = line => AppendLine(stderrLog, line);
                DiagnosticLog.SweepInBackground(ExtensionConfig.LogDirectory);
                // Pasted images (issue #118), on the same schedule and off the same thread. Wired from
                // the HOST rather than from the view-model that writes them, even though that is where
                // the diff-scratch precedent puts it: a view-model is constructed by the unit tests too,
                // and a sweep that runs before those redirect would delete the developer's own
                // attachments. The hosts are the only places where the redirect is guaranteed to have
                // happened first.
                AttachmentStore.SweepInBackground();
                // Tool-window init is the run boundary: roll the previous run's log to .1 so the
                // primary always holds the session you're about to debug. Note a second devenv (Exp)
                // opening its own chat window rolls this too — that's why generations are kept.
                DiagnosticLog.StartRun(stderrLog);
                AppendLine(stderrLog, $"=== session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                Action<string> engineLog = line => AppendLine(stderrLog, line);

                // Wired here rather than beside the chat view, because it must be live before the
                // FIRST render — a restored transcript renders during session restore, and the whole
                // point of the firewall is to report a failure we would otherwise only learn about
                // from a crash dump. Touching this type is safe: nothing in its surface can fail to
                // load (that is what it is for).
                CodeWicket.UI.Markdown.MarkdownRenderFirewall.Log = engineLog;

                // What a drag actually carried. Always on rather than behind a setting: a drop is a rare
                // user-initiated gesture, not a per-render cost — and the question it answers (which
                // formats does Solution Explorer publish?) can only be answered on a real Visual Studio.
                CodeWicket.UI.Input.FileDropPaths.Log = engineLog;

                // Phase timings for this window open, plus a watchdog that names a phase still
                // running from a pool thread — the only way a hung UI thread leaves an artifact at
                // all. Disposed however we leave here, so a phase that throws stops being reported.
                using var startup = new StartupTimeline(engineLog);

                // Let the "Starting …" placeholder actually PAINT before we take the UI thread for the
                // length of `ide services`.
                //
                // The placeholder has been set since the constructor and was never the problem — it
                // simply never rendered. OnToolWindowCreated launches this with JoinableTaskFactory,
                // which runs it inline on the UI thread, and every continuation in the chain posts back
                // at DispatcherPriority.Normal (9) — which OUTRANKS Render (7). So each await handed the
                // thread straight back to the next step of our own startup, and WPF got its first render
                // pass only once the whole chain finished: a blank pane for the 2.3-4 s of `ide
                // services`, then window and transcript appearing together. Exactly what a user reports
                // as "it loads all at once".
                //
                // Yielding at Background (4) is what fixes it: it is below Render and Loaded, so layout
                // and paint go first and our next phase resumes after. One frame's delay, in exchange
                // for the window looking alive for the several seconds it is not.
                //
                // Deliberately NOT a spinner or an indeterminate progress bar: `ide services` holds the
                // UI thread outright, so any animation would freeze mid-sweep for precisely the interval
                // it exists to cover — worse than honest static text, because a stalled spinner reads as
                // a hang. If this ever needs to look busier, the fix is to get that phase off the UI
                // thread, not to animate over it.
                // Timed like everything else, so the wait stays accounted for: an untimed await here
                // would land in `window ready` as a gap belonging to no phase, and on a machine mid
                // solution-load "how long until VS would give us a frame" is exactly the number worth
                // having. It should read a few ms.
                startup.Begin("first paint");
                // Switch first rather than relying on RunAsync still having us inline on the UI thread:
                // Dispatcher.Yield is static and binds to CurrentDispatcher, so off-thread it would
                // silently queue onto the wrong one. No-op when we are already there, which we are.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Background);
                startup.End();

                // Settings come from config.json (Tools > Options > Code Wicket), with CWKT_* env
                // vars still winning as a last resort. NOT from launchSettings env: the VSSDK debug
                // target launches the Exp devenv itself and never passes those vars through. See
                // ExtensionConfig.
                startup.Begin("config");
                var config = ExtensionConfig.Load();
                startup.End();
                var useStub = config.UseStubIde;
                var useFake = config.UseFakeProvider;

                // Chat rendering diagnostics — off unless asked for (issue #86). Installed from the host
                // rather than switched on inside the UI library, because ExtensionConfig.LogDirectory is
                // NOT covered by ExtensionConfig.RedirectTo: a library-side writer would also fire from
                // the unit tests, which render real markdown, into the developer's own logs.
                if (config.LogRendering)
                {
                    CodeWicket.UI.Markdown.RenderDiagnosticsLog.EnableFileLog();
                    // And what the UI thread spends its time on, which in devenv is VS's dispatcher
                    // rather than one of ours — deliberately, since #86's remaining ~70% is held by
                    // something neither the render nor the realisation trace can see, and it is not
                    // required to be our code.
                    CodeWicket.UI.Diagnostics.DispatcherTrace.Install(
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);
                }

                // Forward engine-consumed settings as environment variables on the engine process
                // (they run in the Kiro provider inside the engine, not here in the shell).
                // User-configured entries (proxy vars, CA bundles — see ExtensionConfig.EngineEnvironment)
                // go first so the host-reserved CWKT_* keys below win on a collision. The agent CLIs
                // inherit these from the engine process (devenv -> engine -> kiro-cli/claude-agent-acp).
                var engineEnv = new System.Collections.Generic.Dictionary<string, string>();
                foreach (var kv in config.EngineEnvironment)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
                        engineEnv[kv.Key] = kv.Value;
                if (config.LogAcpFrames)
                    engineEnv["CWKT_ACP_LOG"] = ExtensionConfig.AcpLogFile;
                if (!string.IsNullOrWhiteSpace(config.KiroCliPath))
                    engineEnv["CWKT_KIRO_CLI"] = config.KiroCliPath;
                if (!string.IsNullOrWhiteSpace(config.KiroAgentEngine))
                    engineEnv["CWKT_KIRO_AGENT_ENGINE"] = config.KiroAgentEngine;
                if (!string.IsNullOrWhiteSpace(config.ClaudeAcpPath))
                    engineEnv["CWKT_CLAUDE_ACP"] = config.ClaudeAcpPath;
                if (config.CustomAcpAgentsJson() is { } customAgents)
                    engineEnv["CWKT_CUSTOM_AGENTS"] = customAgents;
                // How far above the solution folder a backend may look for its workspace marker
                // (issue #54). Absent = the RepositoryRoot default, matching the other opt-out wire
                // contracts, so an existing install gets the fix without touching config.
                if (!string.IsNullOrWhiteSpace(config.AgentWorkspaceScope))
                    engineEnv["CWKT_WORKSPACE_SCOPE"] = config.AgentWorkspaceScope;
                // Built-in backends the user has switched off (enable checkboxes). We keep the engine's
                // opt-out wire contract (CWKT_DISABLED_PROVIDERS): absent = all on, so a new built-in
                // lights up automatically and custom agents stay enabled-by-presence.
                var disabledProviders = new System.Collections.Generic.List<string>();
                if (!config.KiroEnabled) disabledProviders.Add("kiro");
                if (!config.ClaudeCodeEnabled) disabledProviders.Add("claude-code");
                if (disabledProviders.Count > 0)
                    engineEnv["CWKT_DISABLED_PROVIDERS"] = string.Join(";", disabledProviders);
                if (config.DiscoveredModelsJson() is { } modelCache)
                    engineEnv["CWKT_MODEL_CACHE"] = modelCache;

                // Start the engine PROCESS now, before `ide services` — the phase that costs 2.3-4.0 s
                // on a window reopen and ~9.1 s when it runs during VS's own launch. Only the RPC
                // attach below needs IIdeServices; the spawn needs nothing but config.
                //
                // This is not about the engine's own boot: it is about what the engine does the moment
                // it is up. It builds its provider registry and starts each backend's model probe
                // straight away (AcpAgentProvider.BeginModelRefresh), and that probe is ~1.5 s of
                // kiro-cli which the session start has to WAIT for if it hasn't finished
                // (KiroAgentProvider.Preflight). Measured: when the probe had finished first, the
                // session's own spawn was 610 ms; when it had not, 1479-1888 ms. Spawning here buys
                // the whole of `ide services` as probe time.
                //
                // The process is UNOWNED until Attach: no EngineClient exists yet, so no Dispose can
                // reach it. Hence the try/catch below — every path out of here that doesn't reach
                // Attach must kill it. (Measured since: an unattached engine would in fact exit on its
                // own, because nothing ever writes to the stdin we redirected and its host.Completion
                // finishes when that closes. The explicit kill is therefore belt-and-braces for a child
                // that ignores EOF, not the only thing preventing a stray process — worth knowing
                // before treating a leak here as the likely cause of anything.)
                // The override is an ENVIRONMENT variable and no longer a config.json field. The file
                // is writable by an agent under "Allow edits" (pre-release security review, September 2026),
                // and a path in it was an executable the next window open would spawn as the user;
                // devenv's environment is not something a tool call reaches. The debugging use it
                // served (an unbundled engine) is what CWKT_ENGINE_EXE has always been for.
                var enginePath = ResolveEnginePath(
                    Environment.GetEnvironmentVariable("CWKT_ENGINE_EXE"), engineLog);
                startup.Begin("engine spawn");
                var pendingEngine = EngineClient.Spawn(enginePath, useFake, engineLog, engineEnv);
                startup.End();

                // Set the moment Attach hands ownership to _engine, which Dispose/TeardownSession can
                // then reach. Until it flips, the catch below is the ONLY thing that can reap the
                // process — and everything between here and there (VsIdeServices.CreateAsync, the
                // permission chain, the chat view) can throw.
                var attached = false;
                try
                {
                    // The phase that can freeze the IDE (a VS update leaves the MEF composition
                    // cache cold, and this runs inline on the UI thread). The composition itself is lazy
                    // now — see VsIdeServices.CreateAsync — so what this measures is DTE plus the handful
                    // of already-composed services, and a number that is suddenly large here says the
                    // laziness stopped being enough.
                    startup.Begin("ide services");
                    IIdeServices ide;
                    string workDir;
                    string workspaceNotice = null;
                    if (useStub)
                    {
                        // Stable per-user dir, NOT temp (same principle as VsIdeServices.DefaultWorkspaceRoot;
                        // kept separate from it so stub experiments don't mix with real default-workspace files).
                        // Temp was confusing in practice: agent errors quoted a C:\Windows\Temp\... cwd.
                        // Named in StoragePaths so the permission guard's exemption and this cwd agree.
                        workDir = StoragePaths.StubAgentWorkspace;
                        Directory.CreateDirectory(workDir);
                        ide = new StubIdeServices(workDir, solutionName: "VS");
                    }
                    else
                    {
                        _vsIde = await VsIdeServices.CreateAsync(config.RunCommandEnabled, _agentWrites);
                        ide = _vsIde;
                        workDir = _vsIde.Workspace.RootPath;
                        if (_vsIde.IsDefaultWorkspace)
                            workspaceNotice = DefaultWorkspaceNotice(workDir);
                    }
                    startup.End(useStub ? "stub" : _vsIde.IsDefaultWorkspace ? "default workspace" : null);


                    // Permission chain: a policy layer (command lists + mode + remembered "always" choices)
                    // sits in front of the UI router, which shows the chat banner (falling back to the VS
                    // message box until the view-model attaches its prompt below). The policy logs each
                    // decision to engine.log so command allow/deny matching is observable.
                    var permissionRouter = new UiPermissionRouter(ide.Permissions);
                    var permissionPolicy = new PolicyPermissionHandler(
                        permissionRouter, ParseMode(config.DefaultPermissionMode), engineLog);
                    permissionPolicy.SetCommandPolicy(config.AllowedCommands, config.AlwaysPromptCommands);
                    permissionPolicy.SetPathPolicy(config.AllowedPaths);
                    permissionPolicy.SetToolPolicy(config.AllowedTools);
                    // Author-declared tool risks (from the real VS catalog) so the mode ladder can auto-allow
                    // our IDE tools at the right rung — resolved in-proc here, where permissions terminate, so
                    // no risk data need cross the engine wire. Read before ide is wrapped below.
                    permissionPolicy.SetToolRisks(ide.Tools.Tools);
                    // "Save this rule permanently" on the banner writes the glob to the config allow-list
                    // (which OnConfigChanged then re-applies to the running policy).
                    permissionPolicy.PersistAllowedCommand = ExtensionConfig.AddAllowedCommand;
                    permissionPolicy.PersistAllowedPath = ExtensionConfig.AddAllowedPath;
                    permissionPolicy.PersistAllowedTool = ExtensionConfig.AddAllowedTool;
                    // The root the link guard measures "below the workspace" from. Set here as well as
                    // on each session start below, so a request arriving before any session has
                    // reported its working directory is still judged against a real tree.
                    permissionPolicy.AgentWorkspaceRoot = workDir;
                    _permissionPolicy = permissionPolicy;
                    ide = new PermissionOverrideIdeServices(ide, permissionPolicy);

                    // Re-apply config-driven policy (the command allow/deny lists) when settings are saved,
                    // so an Options edit takes effect on the running session without reopening the window.
                    ExtensionConfig.Changed += OnConfigChanged;

                    startup.Begin("engine attach");
                    _engine = EngineClient.Attach(pendingEngine, ide, engineLog,
                        rawLogPath: config.LogEngineChannel ? ExtensionConfig.EngineChannelLogFile : null,
                        writes: _agentWrites);
                    attached = true;
                    startup.End();

                    // Ask for the provider catalog NOW, and await the answer further down. The engine's
                    // first request is the one that pays for its net10 closure loading lazily behind the
                    // RPC path — 2044 ms in one collected session, against 11 ms for the very next call on
                    // that same connection — and the clock on it starts when the request does, not when the
                    // process does (the engine had already logged "started" 830 ms before that request went
                    // out). Issued here it runs while the chat view is built on the UI thread instead of
                    // after it, so the wait the user actually sees is what remains. Awaited by
                    // InitializeAndRestoreAsync below; ChatViewModel.InitializeAsync owns the failure path.
                    var providerCatalog = _engine.ListProvidersAsync();

                    // Honor the configured default backend (previously hardcoded fake-or-kiro, silently
                    // ignoring e.g. DefaultProvider "claude-code"). If the configured id isn't registered
                    // (disabled / unknown), the picker falls back to the first available provider.
                    var providerId = string.IsNullOrWhiteSpace(config.DefaultProvider) ? "kiro" : config.DefaultProvider;
                    var request = new StartSessionRequest(providerId, config.DefaultModel, workDir, config.DefaultPermissionMode, null);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    startup.Begin("chat view");
                    var vm = new ChatViewModel(_engine, request,
                        openDiff: (p, oldText, newText, line) => ide.Edits.ShowDiffPreviewAsync(p, oldText, newText, line),
                        persistSelection: ExtensionConfig.SaveSelection,
                        setPermissionMode: name =>
                        {
                            if (Enum.TryParse<PermissionMode>(name, ignoreCase: true, out var m))
                                permissionPolicy.Mode = m;
                            ExtensionConfig.SavePermissionMode(name);
                        },
                        onNewSession: permissionPolicy.ResetRemembered,
                        workspaceNotice: workspaceNotice,
                        sessionStore: new FileSessionStore(),
                        persistModels: (id, models) => ExtensionConfig.SaveDiscoveredModels(
                            id, models.Select(m => new CachedModel { Id = m.Id, DisplayName = m.DisplayName, RateMultiplier = m.RateMultiplier }).ToList()),
                        // Test-results card "go to source" — only with the real IDE (the stub can't navigate).
                        openFile: _vsIde is not null ? (p, line) => _vsIde.OpenFileAtLineAsync(p, line) : null,
                        // Edit card "Open file" — lands on the change, which needs the file read to locate it.
                        openFileAtDiff: _vsIde is not null
                            ? (p, oldText, newText, line) => _vsIde.OpenFileAtDiffAsync(p, oldText, newText, line)
                            : null,
                        // Keep the applier's path resolution on the agent's own working directory: the
                        // session reports it at start, and it is not always the solution root (#54).
                        setAgentWorkingDirectory: _vsIde is not null
                            ? dir =>
                            {
                                _vsIde.SetAgentWorkingDirectory(dir);
                                // The link guard moves with it: judging a write for links below the
                                // PREVIOUS session's root would read the wrong tree, and after a
                                // workspace move that is a tree the agent is no longer in.
                                permissionPolicy.AgentWorkspaceRoot = dir;
                            }
                            : null,
                        // Which conversation is running, so breakpoints the agent sets carry it and
                        // clear_breakpoints can tell this conversation's from an earlier one's (#73).
                        setConversationId: _vsIde is not null
                            ? id => _vsIde.SetConversationId(id)
                            : null,
                        // Read through a delegate, and read on each panel open rather than captured
                        // here, so a log toggle changed in settings is reflected without reopening the
                        // window. The view-model never touches ExtensionConfig itself: LogDirectory is
                        // deliberately outside the automated runs' scratch redirect, so a direct read
                        // would leak a real user path into every screenshot artifact (issue #160).
                        sessionEnvironment: () =>
                        {
                            var cfg = ExtensionConfig.Load();
                            return new SessionEnvironment(
                                ExtensionConfig.LogDirectory,
                                ExtensionConfig.EngineStderrLogFile,
                                ExtensionConfig.AcpLogFile,
                                ExtensionConfig.EngineChannelLogFile,
                                System.IO.Path.Combine(ExtensionConfig.LogDirectory, "render.log"),
                                cfg.LogAcpFrames,
                                cfg.LogEngineChannel,
                                cfg.LogRendering,
                                cfg.KiroAgentEngine);
                        },
                        // What the pane DID with the frames acp.log records it receiving. Unconditional,
                        // like the MCP bridge's two lines and for the same reason (#122 part 3): the
                        // out-of-turn window is a guess made from the event stream, and a report that
                        // it guessed wrong is unanswerable from a wire log alone — that log shows what
                        // arrived, never what was made of it.
                        diagnosticLog: engineLog);
                    // Which rung the tray opens on (issue #190). Set here rather than read by the
                    // view-model, which never touches ExtensionConfig; the pill still changes it for
                    // the session, so this is a preset and not a policy.
                    vm.PendingReleaseMode = PendingReleaseModes.Parse(config.DefaultMessageRelease);
                    permissionRouter.Prompt = vm.RequestPermissionAsync;
                    // Registration order IS the order the Add menu offers them, and the menu's own rule
                    // decides it: the item that is usable most often goes highest, which is why
                    // "Image…" heads the whole list. The Output window nearly always has something in
                    // it; the debugger is stopped rarely.
                    RegisterOutputPaneSource(vm, _vsIde);
                    RegisterDebugContextSource(vm, _vsIde);
                    _vm = vm;
                    var view = new ChatView { DataContext = vm };
                    _view = view;
                    VsTheme.Apply(view); // override the UI's default palette with live VS theme colors

                    // The chat goes in UNDERNEATH a still-visible loading message, rather than replacing it.
                    //
                    // Replacing it here is what produced the second gap: the pane swapped to a real but
                    // EMPTY chat, and then `provider catalog` + `picker` + `session restore` ran on the UI
                    // thread before anything appeared in it — 53-945 ms of empty window in the collected
                    // sessions, most of it the `provider catalog` await (during which the UI thread is free,
                    // which is exactly why WPF had time to paint the emptiness). Nothing was usable in that
                    // window either: ChatViewModel.InitializeAsync holds the send gate shut across the whole
                    // of it, so the composer that appeared could not be typed into.
                    //
                    // Overlaying rather than deferring the swap is deliberate. If we simply held the
                    // placeholder and assigned the view at the end, the view would not be in the visual tree
                    // while the transcript is restored into it — so its first layout and render (item
                    // containers, markdown) would all fall AFTER the swap, and the gap would move rather
                    // than close. Underneath the overlay it is sited from the same moment as before, so the
                    // restore renders behind the message and lifting it reveals a finished pane.
                    // Held as a local and captured, deliberately not in a field: RestartAsync re-enters this
                    // method while the previous run's fire-and-forget restore may still be in flight, and a
                    // field would let that stale run's reveal uncover the NEW overlay — showing a half-built
                    // chat. Captured, each run can only ever lift its own cover.
                    var loadingOverlay = MakeLoadingOverlay(
                        $"Starting {Branding.ProductName}… (build {GetBuildStamp()})");
                    var layers = new System.Windows.Controls.Grid();
                    layers.Children.Add(view);
                    layers.Children.Add(loadingOverlay); // last child = on top
                    _host.Content = layers;
                    startup.End();

                    // The other phase a "it just sits there" report can mean: the first directory walk
                    // behind the blue file links. It is off the UI thread and cancellable now,
                    // so it can no longer freeze anything — but it is still the thing a user watches not
                    // happening, and its cost belongs to the machine (repo size, network- or sync-backed
                    // storage, on-access scanning) rather than to us. Sink wired here rather than through
                    // the view-model's constructor: it is the HOST that knows where diagnostics go, and
                    // the Desktop host deliberately doesn't wire one.
                    if (vm.FileLinks is { } fileLinks)
                        fileLinks.Resolver.IndexLog = engineLog;

                    // The window is usable from here. Written as a total because its ABSENCE is the
                    // signal: a log with phase lines and no "window ready" is a startup that never
                    // finished, and the last phase named is where it stopped.
                    startup.Total("window ready");

                    // Track solution/folder open/close so the agent's working directory follows it (the
                    // session's cwd is captured at start; a change resets it so the next prompt restarts in
                    // the new root). Only meaningful with the real IDE — the stub has a fixed workspace.
                    // The second callback is what tells a solution RELOAD apart from a close: see
                    // ChatViewModel.UpdateWorkspaceRoot.
                    _workspaceSubscription = _vsIde?.SubscribeWorkspaceChanged(
                        (root, isDefault) =>
                            _vm.UpdateWorkspaceRoot(root, isDefault ? DefaultWorkspaceNotice(root) : null, isDefault),
                        () => _vm.NoteSolutionOpening());

                    // Populate the provider/model picker from the engine, then restore this workspace's most
                    // recent conversation (fire-and-forget; the picker fills in once loaded, and SendAsync
                    // falls back to the configured default meanwhile).
                    _ = InitializeAndRestoreAsync(
                        vm, providerCatalog, engineLog,
                        onReady: () =>
                        {
                            loadingOverlay.Visibility = System.Windows.Visibility.Collapsed;
                            _chatReady = true;
                        });

                    // How each call was permitted, onto the row that made it. Wired here rather than at
                    // the policy's construction because the view-model does not exist yet at that point;
                    // permissions terminate in this same process, so nothing crosses the engine wire.
                    permissionPolicy.OutcomeReported = vm.NotePermissionOutcome;

                    // Terminal mirror (opt-in cosmetics): agent command output tees into a real VS
                    // terminal pane. Lazy — no pane (and no broker traffic) until something is written.
                    // The tee folds the same event stream the view-model consumes (second subscriber;
                    // TerminalMirror.Write no-ops while disabled, so an off toggle costs nothing).
                    // The availability probe is load-free, so this line costs nothing and makes "the
                    // terminal never shows" answerable from a collected log alone (issue #51).
                    string terminalContracts;
                    try { terminalContracts = VsTerminalService.DescribeAvailability(); }
                    catch (Exception ex) { terminalContracts = "probe failed: " + ex.Message; }
                    engineLog($"[terminal-mirror] setting={(config.TerminalMirrorEnabled ? "on" : "off")}; VS terminal contracts: {terminalContracts}");
                    if (config.TerminalMirrorEnabled && terminalContracts.StartsWith("NOT FOUND", StringComparison.Ordinal))
                        vm.ShowNotice(
                            "Terminal mirror is on but this Visual Studio install has no terminal component, so no pane will appear. " +
                            "Command output still shows in the chat.",
                            NoticeKind.Error);
                    _terminalMirror = new TerminalMirror(
                        engineLog,
                        onUnavailable: reason => _vm?.ShowNotice(
                            $"Terminal mirror unavailable — no pane will appear this session ({reason}). " +
                            "Command output still shows in the chat.",
                            NoticeKind.Error))
                    {
                        Enabled = config.TerminalMirrorEnabled,
                    };
                    _terminalTee = new TerminalMirrorTee(_terminalMirror.WriteHeader, _terminalMirror.Write);
                    _engine.AgentEvent += _terminalTee.OnEvent;

                    // Our own shellouts (run_tests' dotnet invocations, run_command) tee into the same
                    // pane. Wired in-proc on the concrete catalog — never on the IPC wire (Write no-ops
                    // while the mirror is disabled, so this costs nothing when the setting is off).
                    if (_vsIde?.Tools is VsToolCatalog vsTools)
                    {
                        vsTools.LiveCommandHeader = _terminalMirror.WriteHeader;
                        vsTools.LiveCommandLine = line => _terminalMirror.Write(line + "\n");
                        // Phase 4 interactivity: pty VT output renders verbatim; pane keystrokes and
                        // resizes route to the active run_command session.
                        vsTools.LiveCommandRawOutput = _terminalMirror.WriteRaw;
                        _terminalMirror.UserInputSink = vsTools.SendCommandPtyInput;
                        _terminalMirror.SizeChanged = vsTools.SetTerminalSize;
                    }

                    // ConPTY factory for run_command: independent of the terminal-mirror feature (TTY
                    // semantics benefit the tool even with no pane; without a pane nobody types, and
                    // raw output simply has no sink).
                    if (_vsIde?.Tools is VsToolCatalog vsToolsPty)
                        vsToolsPty.CommandPtyFactory = ConPtyCommandSession.TryStart;
                }
                catch
                {
                    // A window that failed to open must not leave an engine running behind it. Only
                    // when Attach never happened: after it, _engine owns the process and the normal
                    // teardown path (Dispose / RestartAsync) is what closes it.
                    if (!attached)
                        pendingEngine.Kill();
                    throw;
                }
            }
            catch (Exception ex)
            {
                await ShowStartupFailureAsync(ex);
            }
        }

        // Loads the provider/model catalog, then restores the workspace's most recent conversation so
        // reopening the window (or restarting VS) shows where the user left off. Both touch UI state,
        // so this runs on the main thread (ConfigureAwait(true) resumes on the captured context).
        //
        // Timed on its own timeline rather than the one above: this runs AFTER the window is usable, so
        // folding it into "window ready" would misreport when the user got a working pane. It is on the
        // UI thread for the whole of the replay, though, which makes it the other place a "the IDE
        // stopped responding" report can land — and a long transcript is the input we cannot see.
        private static async System.Threading.Tasks.Task InitializeAndRestoreAsync(
            ChatViewModel vm, Task<ListProvidersResponse> providerCatalog, Action<string> log, Action onReady)
        {
            using var startup = new StartupTimeline(log);
            // In a finally, and this is not defensive tidiness: onReady lifts the cover off the chat, so
            // any path that skipped it would leave the user staring at a loading message over a window
            // that is finished and working. A failure here must show the chat, not hide it — the
            // view-model puts its own errors in the transcript, where they are only readable once the
            // overlay is gone.
            try
            {
                // Two phases, because they fail and slow down for unrelated reasons and one name could not
                // say which: the engine's first call (its lazy assembly loading, mostly overlapped by the
                // chat view above — this measures only what is left of it), then what the view-model does
                // with the answer, which is not free either. Building the pickers persists each backend's
                // model list, and ExtensionConfig.Update is a whole config.json read + serialize + write +
                // Changed fanout PER PROVIDER, on the UI thread; ~500 ms sat here in a collected session.
                // Setting the selection is also what releases the warm session start, so a slow phase here
                // delays the backend coming up (issue #19's whole point) without appearing to.
                // VSTHRD003 is exactly right about what this is — a task started outside this context —
                // and that IS the change: the point is for the engine's first call to be in flight before
                // anyone awaits it. Its deadlock case doesn't apply, because nothing here blocks the UI
                // thread on it (every await yields) and the RPC completes on a pool thread that never
                // needs the UI thread to finish.
#pragma warning disable VSTHRD003 // Awaiting a task started outside this context — deliberate, see above
                startup.Begin("provider catalog");
                // Observed here only to close the phase at the right moment — the await below sees the same
                // task, and ChatViewModel.InitializeAsync is what reports the failure in the transcript.
                try { await providerCatalog.ConfigureAwait(true); } catch { /* reported below */ }
                startup.End();

                startup.Begin("picker");
                await vm.InitializeAsync(providerCatalog).ConfigureAwait(true);
                startup.End();
#pragma warning restore VSTHRD003

                startup.Begin("session restore");
                // Reported rather than escaping. This runs on a fire-and-forget task, and the `finally`
                // below lifts the loading overlay whatever happened — so a throw here (the session store
                // reads a log that can be corrupt, locked, or half-written) gave the user a working but
                // EMPTY chat with the exception reaching nobody at all. Their last conversation is simply
                // not there, and absence is the only symptom, which is the failure this file's logging
                // rules exist to prevent. The provider catalog above already had its own catch; this was
                // the half that did not.
                try
                {
                    vm.RestoreMostRecentSession();
                }
                catch (Exception ex)
                {
                    log?.Invoke("[toolwindow] restoring the last conversation failed: " + ex);
                }

                startup.End();

                startup.Total("session restored");
            }
            finally
            {
                onReady();
            }
        }

        // Settings were saved (Options page / header) — re-read the live-applicable policy so command
        // allow/deny edits apply without reopening the window. (Mode also re-syncs in case it changed
        // outside the header dropdown; setting it only when different avoids clearing remembered choices.)
        private void OnConfigChanged()
        {
            if (_permissionPolicy is null)
                return;

            var c = ExtensionConfig.Load();
            _permissionPolicy.SetCommandPolicy(c.AllowedCommands, c.AlwaysPromptCommands);
            _permissionPolicy.SetPathPolicy(c.AllowedPaths);
            _permissionPolicy.SetToolPolicy(c.AllowedTools);

            var mode = ParseMode(c.DefaultPermissionMode);
            if (_permissionPolicy.Mode != mode)
                _permissionPolicy.Mode = mode;

            // Live-apply the terminal-mirror toggle (off closes the pane; on recreates on next output).
            if (_terminalMirror is not null)
                _terminalMirror.Enabled = c.TerminalMirrorEnabled;
        }

        /// <summary>
        /// Puts the caret in the chat's message box (the keyboard shortcut's payoff — see
        /// <see cref="ShowChatWindowCommand"/>). No-op while the window is still showing its startup
        /// status or a startup failure, since there's no input box to focus yet.
        /// </summary>
        /// <remarks>
        /// Reads the field rather than <c>_host.Content</c>, which is never a <see cref="ChatView"/>:
        /// a TextBlock while starting, a TextBox on failure, and on success the Grid layering the view
        /// under the loading overlay. The cast therefore yielded null in every state and this did
        /// nothing at all — the pane came forward on Ctrl+\,Ctrl+W and the caret stayed where it was,
        /// which is the entire payoff of the shortcut.
        /// <para>
        /// Gated on <see cref="_chatReady"/> rather than on the view existing, so the caret is never put
        /// into a box the overlay is still covering — typing into an invisible composer is worse than
        /// the shortcut appearing not to have focused anything.
        /// </para>
        /// </remarks>
        public void FocusInput()
        {
            if (_chatReady)
                _view?.FocusInput();
        }

        /// <summary>
        /// Offers the debugger's break state to the composer (issue #73, rung 2), so the user can hand
        /// the agent the call stack and locals they are looking at.
        ///
        /// <para>Rung 1 lets the agent instrument a run and read back what it asked to be printed; it
        /// cannot see anything it did not think to ask for in advance. This is the other half: the
        /// evidence the agent could not have anticipated, at the moment the user is looking at it.</para>
        ///
        /// <para>Registered as a SOURCE rather than wired to a button, because the composer's menu and
        /// the VS Debug-menu command both reach it through the registry - one path from a gesture to a
        /// chip. Skipped entirely with the stub IDE, which has no debugger, and that is what hides the
        /// composer's "Add" button rather than leaving it offering something that cannot work.</para>
        /// </summary>
        private static void RegisterDebugContextSource(ChatViewModel vm, VsIdeServices ide)
        {
            if (ide is null)
                return;

            vm.RegisterContextSource(new ChatContextSource(
                DebugContextSourceId,
                "Debug context",
                "The call stack and locals the debugger is showing.",
                captureAsync: async root =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var capture = ide.CaptureDebugState();
                    return capture is null
                        ? null
                        : new ChatContextCapture(
                            HostPromptBlocks.DebugState,
                            DebugStateFormatter.Label(capture),
                            DebugStateFormatter.Format(capture, root));
                },
                // Read on the UI thread by construction: both callers are menus opening, and a menu
                // opens on the thread its window lives on.
                canCapture: () => ThreadHelper.CheckAccess() && ide.IsDebuggerStopped(),
                unavailableReason: "The debugger isn't stopped, so there is no call stack to attach. "
                    + "Break at a line first."));
        }

        /// <summary>
        /// Registers the Output-window capture (the push half of the output story — there is no tool
        /// that reads an arbitrary pane, so this gesture is the only route those lines have).
        /// </summary>
        /// <remarks>
        /// Registered beside the debug source and in the same shape, deliberately: both are "hand over
        /// what you are looking at", both are bounded, and both are reviewable in the composer before
        /// they go. The difference is only in what the unavailable state means — a debugger that is not
        /// stopped has no stack, while an Output window with nothing in it has nothing to send.
        /// </remarks>
        private static void RegisterOutputPaneSource(ChatViewModel vm, VsIdeServices ide)
        {
            if (ide is null)
                return;

            vm.RegisterContextSource(new ChatContextSource(
                OutputPaneSourceId,
                "Output window",
                "What the Output window is showing — your selection in it, or the last "
                    + OutputCapture.MaxTailLines + " lines.",
                captureAsync: async _ =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var capture = ide.CaptureOutputPane();
                    return capture is null
                        ? null
                        : new ChatContextCapture(
                            HostPromptBlocks.OutputWindow, capture.Label, capture.Text);
                },
                // On the UI thread by construction, as the debug source is: both callers are menus
                // opening, and a menu opens on the thread its window lives on.
                canCapture: () => ThreadHelper.CheckAccess() && ide.HasOutputPaneContent(),
                unavailableReason: "The Output window has nothing in it to send. Open it "
                    + "(View → Output) and pick a pane with output in it."));
        }

        /// <summary>
        /// The output source's id. It IS the prompt block's tag name, on the same rule as
        /// <see cref="DebugContextSourceId"/>: the id, the wire tag and the persisted kind are one
        /// string rather than three that have to agree.
        /// </summary>
        internal static string OutputPaneSourceId => HostPromptBlocks.OutputWindow.Name;

        /// <summary>
        /// The debug source's id, shared by the registration above and the VS commands that address
        /// it. It IS the prompt block's tag name, so the id, the wire tag and the persisted kind are
        /// one string rather than three that have to agree.
        /// </summary>
        internal static string DebugContextSourceId => HostPromptBlocks.DebugState.Name;

        /// <summary>
        /// Runs the debug-context gesture from a VS command. Returns false when the pane has no
        /// view-model yet - a window still starting up - so the caller can say so rather than the
        /// click doing nothing.
        /// </summary>
        internal bool AddDebugContext()
        {
            // _chatReady, not just _vm. The view-model is assigned early and the opaque overlay comes
            // down only when the restore finishes, so testing _vm alone returned true for a pane the
            // user cannot see — the chip landed in a hidden composer and the caller skipped its "still
            // starting" message. A menu click that silently does nothing, which is the report this
            // command exists to answer.
            if (!_chatReady || _vm is not { } vm)
                return false;

            _ = vm.AddContextAsync(DebugContextSourceId);
            return true;
        }

        private static string DefaultWorkspaceNotice(string root) =>
            "No solution open. Working in the default workspace:\n" + root;

        private static PermissionMode ParseMode(string mode) => PermissionModeParser.Parse(mode);

        // Serializes concurrent appends (engine stderr arrives on threadpool callbacks concurrently
        // with permission-policy and lifecycle logging) and enforces the shared retention policy —
        // see DiagnosticLog, which owns the lock, the FileShare.ReadWrite open that tolerates a second
        // devenv appending the same file, and the size cap.
        private static void AppendLine(string path, string line) => DiagnosticLog.AppendLine(path, line);

        /// <summary>
        /// An opaque full-pane cover carrying the status text, sitting on top of the chat while it is
        /// still being populated. Opaque is the load-bearing part — a transparent layer would show the
        /// half-built transcript through it, which is the thing it exists to hide — so it paints the VS
        /// tool-window background rather than relying on the pane behind it.
        /// </summary>
        private static System.Windows.FrameworkElement MakeLoadingOverlay(string text)
        {
            var bg = Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(
                Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundColorKey);
            return new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(bg.A, bg.R, bg.G, bg.B)),
                Child = MakeStatus(text),
            };
        }

        // Centred rather than parked in the top-left corner: this is the whole pane for the seconds
        // before the chat view exists, and centred reads as "loading", where a lone line of text at the
        // top reads as a window that has finished and has nothing in it.
        private static System.Windows.Controls.TextBlock MakeStatus(string text)
        {
            var block = new System.Windows.Controls.TextBlock
            {
                Margin = new System.Windows.Thickness(24),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                TextAlignment = System.Windows.TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Text = text,
            };

            // Bigger, but still the user's Environment Font — VS ships paired size/weight keys for
            // exactly this, so we scale WITH Tools > Options > Environment > Fonts and Colors rather
            // than pinning a number that ignores it. Same rule as the chat pane (chrome scales with the
            // environment font) and the same mechanism: a live resource REFERENCE, never a copied
            // value, or it is one Fonts-and-Colors change behind forever.
            //
            // Unguarded, unlike VsTheme.SetEnvironmentFont's TryFindResource checks: this block is
            // built in the constructor and is not in a visual tree yet, so a lookup here would find
            // nothing and skip the bump permanently. A resource reference re-resolves when the element
            // is sited, and an unresolvable key simply leaves the inherited default.
            try
            {
                block.SetResourceReference(
                    System.Windows.Documents.TextElement.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);
                block.SetResourceReference(
                    System.Windows.Documents.TextElement.FontSizeProperty, VsFonts.Environment133PercentFontSizeKey);
                // The weight is paired with the size on purpose: VS's larger environment sizes are
                // designed with a lighter weight, and taking the size alone reads as heavy.
                block.SetResourceReference(
                    System.Windows.Documents.TextElement.FontWeightProperty, VsFonts.Environment133PercentFontWeightKey);
            }
            catch
            {
                // Best-effort like the rest of the theming: the WPF default stays and the text is
                // still readable. A placeholder must never be the thing that breaks startup.
            }

            // Use the VS tool-window text color so the status/error is readable in any theme.
            var fg = Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(
                Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextColorKey);
            block.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(fg.A, fg.R, fg.G, fg.B));
            return block;
        }

        // Error variant of MakeStatus: a read-only TextBox so the exception text is SELECTABLE — a
        // TextBlock isn't, and an error that cannot be copied off the broken machine costs a diagnosis
        // round-trip. Scrolls for long stack traces.
        private static System.Windows.Controls.TextBox MakeErrorStatus(string text)
        {
            var box = new System.Windows.Controls.TextBox
            {
                Margin = new System.Windows.Thickness(12),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Text = text,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                BorderThickness = new System.Windows.Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            };

            var fg = Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(
                Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextColorKey);
            box.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(fg.A, fg.R, fg.G, fg.B));
            return box;
        }

        /// <summary>
        /// Resolves the engine: the <c>CWKT_ENGINE_EXE</c> environment override wins; otherwise the
        /// bundled engine the VSIX ships under "engine\".
        /// </summary>
        private static string ResolveEnginePath(string overridePath, Action<string> log = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                if (File.Exists(overridePath))
                    return overridePath;

                // Said out loud, because falling through silently is the one stale-engine confusion
                // this file otherwise works hard to make visible. A developer points CWKT_ENGINE_EXE at
                // a build output, mistypes it or rebuilds to another configuration, and the window
                // comes up perfectly - on the BUNDLED engine, with the doc saying the override wins.
                log?.Invoke(
                    "[toolwindow] the configured engine path does not exist, using the bundled engine: "
                    + overridePath);
            }

            var extensionDir = Path.GetDirectoryName(typeof(ChatToolWindow).Assembly.Location);
            var bundled = Path.Combine(extensionDir ?? string.Empty, "engine", Branding.EngineExeName);
            if (File.Exists(bundled))
                return bundled;

            throw new FileNotFoundException(
                $"Could not find {Branding.EngineExeName} (expected under the extension's engine\\ folder, " +
                "or set CWKT_ENGINE_EXE to its full path).", bundled);
        }

        /// <summary>
        /// Rebuilds the whole session in place: tears everything down, then re-runs the normal
        /// initialization. This is what "Restart (apply settings)" needs, because every
        /// engine-launch-scoped setting (provider registration, custom agents, EngineEnvironment and
        /// the rest of the CWKT_* environment) is read in <see cref="InitializeCoreAsync"/> and
        /// forwarded when the engine process is launched — so only a new engine picks them up.
        /// </summary>
        /// <remarks>
        /// It has to be done in place: VS <em>caches</em> tool-window panes for the package's lifetime
        /// and closing a tool window's frame only HIDES it, so <see cref="Dispose"/> doesn't run until
        /// VS shuts down. The previous close-and-reopen implementation therefore hid and re-showed the
        /// same live pane — the engine (and the agent CLI under it) survived, and the command silently
        /// applied nothing while looking like it had worked. Teardown is shared with
        /// <see cref="Dispose"/> rather than duplicated, which is what keeps the two honest.
        /// </remarks>
        public async Task RestartAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // One initialization at a time. RestartCommand enables itself as soon as FindToolWindow
            // returns the pane — including while the FIRST init is still in flight, which is seconds of
            // "Starting…" the user is looking at. Clicking Restart then tore down nothing (_engine is
            // still null) and started a second InitializeCoreAsync beside the first: both spawn an
            // engine, both Attach, and the winner overwrites _engine/_vm/_terminalTee/_host.Content —
            // leaving the loser's engine process, and the agent CLI under it, running with no owner and
            // no Dispose path, plus a duplicate ExtensionConfig.Changed subscription that TeardownSession's
            // single -= cannot remove. Two quick clicks do the same thing.
            //
            // Checked on the UI thread, which is the only place it is written, so no interlock is needed.
            if (_initializing)
                return;

            TeardownSession();
            _host.Content = MakeStatus($"Restarting {Branding.ProductName}…");

            // Carries its own JIT-fault firewall and failure sink, so a restart that fails reports
            // exactly like a failed first start instead of leaving the pane on "Restarting…".
            await InitializeAsync();
        }

        /// <summary>
        /// The single teardown path, shared by <see cref="Dispose"/> and <see cref="RestartAsync"/>.
        /// Deliberately one implementation: a second, subtly-different teardown is how a "restart"
        /// ends up leaving the engine process running.
        /// </summary>
        private void TeardownSession()
        {
            // The pane is no longer usable, whatever is still on screen. Cleared here rather than at
            // each caller so a restart cannot leave FocusInput and AddDebugContext believing in the
            // conversation they just tore down.
            _chatReady = false;
            _view = null;

            ExtensionConfig.Changed -= OnConfigChanged;

            if (_workspaceSubscription is not null)
            {
                // Tool-window disposal is on the UI thread, where Unadvise is required.
                try { _workspaceSubscription.Dispose(); }
                catch { /* best effort on teardown */ }
                _workspaceSubscription = null;
            }

            if (_engine is not null && _terminalTee is not null)
            {
                try { _engine.AgentEvent -= _terminalTee.OnEvent; }
                catch { /* best effort on teardown */ }
            }
            _terminalTee = null;

            if (_engine is not null)
            {
                try { _engine.Dispose(); }
                catch { /* best effort on teardown */ }
                _engine = null;
            }

            if (_terminalMirror is not null)
            {
                try { _terminalMirror.Dispose(); }
                catch { /* best effort on teardown */ }
                _terminalMirror = null;
            }

            // Neither the view-model nor the IDE facade is IDisposable, so there's nothing to close —
            // but the references are dropped so a stale one can't be reached by a surviving callback
            // (or mistaken for live state by the next initialization).
            _vm = null;
            _vsIde = null;
            _permissionPolicy = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                TeardownSession();
                // Render diagnostics are written on a pool thread, so the last message's line may still
                // be queued. Draining it here is the difference between a report that ends mid-session
                // and one that ends where the user closed the pane. Bounded: a stuck disk costs that
                // line, not the shutdown.
                CodeWicket.UI.Diagnostics.DispatcherTrace.Uninstall();
                CodeWicket.UI.Markdown.RenderDiagnosticsLog.Flush();
            }

            base.Dispose(disposing);
        }
    }
}
