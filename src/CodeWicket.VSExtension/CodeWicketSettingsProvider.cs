using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// Backs the unified-settings "Code Wicket" region (see CodeWicket.registration.json) with
    /// our shared <c>config.json</c> instead of the VS settings store — so the native Settings UI and
    /// the standalone hosts and the chat-header pickers all share one settings model. Proffered as a
    /// VS service whose GUID matches the region's <c>callback.serviceId</c>.
    /// </summary>
    [Guid(ServiceGuidString)]
    public sealed class CodeWicketSettingsProvider : IExternalSettingsProvider, ICachingExternalSettingsProvider
    {
        public const string ServiceGuidString = "8E5B2F71-3C4D-4A6E-9B1F-2D7A6C8E4F30";

        // The Settings UI is PUSHED, never re-queried (issue #271). USX pulls a value once, when the
        // setting's row first connects to this provider, and again only when this event names it —
        // nothing re-asks when the page is shown, and the page's Refresh reaches only an
        // ICachingExternalSettingsProvider (read from Shell.UI.Internal: ExternalSettingBehavior pulls
        // in ConnectToProviderAsync and OnProviderSettingsChanged; ExternalSettingsRegionViewModel.Refresh
        // calls RefreshCacheAsync and nothing else). The comment this replaced said USX re-queried on
        // show; it did not, and an "always allow" rule the banner wrote was invisible until restart.
        //
        // So every config.json write (ExtensionConfig.Changed, in-process) and every Refresh reconciles
        // what this provider has SERVED against the store and raises this for the monikers that moved
        // — see OnConfigChanged and RefreshCacheAsync. Raised on the main thread, always posted: a row's
        // handler mutates an ObservableCollection, and an inline raise inside a caller's own flow is
        // the shape that misfires (see SaveFromDialog).
        public event EventHandler<ExternalSettingsChangedEventArgs> SettingValuesChanged;
        // No dynamic enums or messages in our registration — never raised.
        public event EventHandler<EnumSettingChoicesChangedEventArgs> EnumSettingChoicesChanged { add { } remove { } }
        public event EventHandler<DynamicMessageTextChangedEventArgs> DynamicMessageTextChanged { add { } remove { } }

        private readonly ServedSettings _served = new ServedSettings();

        // True on the thread that is saving config.json on behalf of the Settings UI itself, so the
        // Changed handler that fires INSIDE that save knows not to announce it. Thread-static rather
        // than an instance flag because the custom-agents save lands on a pool thread after its probe,
        // and a banner's save on another thread must not be silenced by it.
        //
        // Announcing our own save is not merely redundant, it misfires: USX pushes a value by calling
        // SetValueAsync and only THEN records the value as the provider's (lastKnownValueFromProvider)
        // and marks its source as the provider. A notification that lands between the two makes the
        // row re-pull while its value is still marked as the user's edit, and where what we stored is
        // not byte-identical to what was typed (allowedTools normalisation, the messageRelease
        // round-trip, the attachment clamp, custom-agent JSON re-serialised) USX reads that as a
        // backing-store CONFLICT and asks "keep your change or keep the backing store change?" over
        // the user's own save. Ordering is not ours to guarantee — the custom-agents path is async —
        // so the rule is simply: a dialog save is never announced. The cost is that a normalised value
        // shows as typed until the next change to it, which is what shipped before this fix too.
        [ThreadStatic] private static bool _savingFromDialog;

        private readonly JoinableTaskFactory _jtf;

        // Proffered once per package (CodeWicketPackage.AddService) and lives for the process, so the
        // subscription is never released. The package's JTF rather than ThreadHelper's so the
        // fire-and-forget below is the shape the commands use and VSSDK007 recognises.
        public CodeWicketSettingsProvider(JoinableTaskFactory jtf)
        {
            _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
            ExtensionConfig.Changed += OnConfigChanged;
        }

        /// <summary>
        /// A config.json write from anywhere in-process — the permission banner's "always" rules, the
        /// header pickers, the zoom — reconciles the served values and names what moved. The
        /// reconcile runs on a dialog save too, so the record tracks the store, but nothing is raised.
        /// </summary>
        private void OnConfigChanged()
        {
            var changed = _served.Reconcile(CurrentValues());
            if (_savingFromDialog || changed.Count == 0)
                return;
            RaiseSettingValuesChanged(changed);
        }

        /// <summary>
        /// USX calls this at provider init, when the page's Refresh is clicked, and BEFORE every push
        /// from the dialog — after which it re-pulls the setting being pushed and, if the store no
        /// longer holds what the row last saw, shows a conflict instead of overwriting (its
        /// ExternalSettingBehavior.PushValueToProviderAsync). That last call is what closes the
        /// issue's stale-overwrite question for a config.json edited OUTSIDE the process, which
        /// ExtensionConfig.Changed cannot see: a hand-edit made while the dialog is open is caught at
        /// the push. Our "cache" is the served record; refreshing it is a reconcile.
        /// </summary>
        public Task<ExternalSettingOperationResult> RefreshCacheAsync(CancellationToken cancellationToken)
        {
            var changed = _served.Reconcile(CurrentValues());
            if (changed.Count > 0)
                RaiseSettingValuesChanged(changed);
            return ExternalSettingOperationResult.SuccessResultTask();
        }

        // Nothing is pending: every SetValueAsync writes config.json before it returns.
        public Task<ExternalSettingOperationResult> CommitPendingChangesAsync(CancellationToken cancellationToken) =>
            ExternalSettingOperationResult.SuccessResultTask();

        private static Func<string, object> CurrentValues()
        {
            var c = ExtensionConfig.Load();
            return moniker => Read(c, Leaf(moniker));
        }

        private void RaiseSettingValuesChanged(IReadOnlyList<string> monikers)
        {
            var handler = SettingValuesChanged;
            if (handler is null)
                return;

            var args = monikers.Count == 1
                ? ExternalSettingsChangedEventArgs.Single(monikers[0])
                : ExternalSettingsChangedEventArgs.Multiple(monikers);

            _jtf.RunAsync(async () =>
            {
                await _jtf.SwitchToMainThreadAsync(alwaysYield: true);
                handler(this, args);
            }).FileAndForget("code-wicket/settings-changed");
        }

        // Every config.json write the Settings UI itself makes goes through here, so OnConfigChanged
        // can tell it from a write made underneath the page. Set around the Save alone, not the whole
        // SetValueAsync: the custom-agents path awaits a probe with ConfigureAwait(false) first.
        private static void SaveFromDialog(Action<ExtensionConfig> mutate)
        {
            _savingFromDialog = true;
            try { ExtensionConfig.Save(mutate); }
            finally { _savingFromDialog = false; }
        }

        // Raised after a successful save that follows a rejected one, so USX clears any error state it
        // holds for the region. Validation failures are also returned isTransient: true — a
        // non-transient failure makes USX grey the setting out pending this event, which locked the
        // field so the user couldn't correct their input (observed with engineEnvironment, 2026-07-10).
        public event EventHandler ErrorConditionResolved;

        private int _errorPending; // 1 after a rejected save; cleared (and the event raised) on the next success

        private void NoteFailure() => Interlocked.Exchange(ref _errorPending, 1);

        private void NoteSuccess()
        {
            if (Interlocked.Exchange(ref _errorPending, 0) == 1)
                ErrorConditionResolved?.Invoke(this, EventArgs.Empty);
        }

        // The boxed value a leaf currently has — GetValueAsync's answer before conversion, and what the
        // served record compares against on a reconcile, so the two can never disagree about what a
        // setting is worth. Null for a leaf the manifest does not bind.
        private static object Read(ExtensionConfig c, string leaf) =>
            leaf switch
            {
                // Pass the id through (normalized to the registration's enum); unknown ids fall
                // back to "kiro" so the Settings UI never shows an out-of-enum value. "fake" is one
                // of those: the in-process fake provider is developer plumbing, reached by hand-
                // editing config.json or by CWKT_PROVIDER, and it is deliberately not offered in the
                // Settings dropdown (an end user picking it would get a backend that answers with
                // canned text). A config that names it still RUNS it — this is what the page shows,
                // not what the window opens on.
                "provider" => c.DefaultProvider?.ToLowerInvariant() switch
                {
                    "claude-code" => "claude-code",
                    _ => "kiro",
                },
                "defaultModel" => c.DefaultModel ?? string.Empty,
                // Round-tripped through the parser on the way OUT as well as in, so a config written
                // before this setting existed (or hand-edited to something the tray has no rung for)
                // shows the Settings UI the mode the window will actually open on.
                "messageRelease" => PendingReleaseModes.ToName(
                    PendingReleaseModes.Parse(c.DefaultMessageRelease)),
                "permissionMode" => string.IsNullOrEmpty(c.DefaultPermissionMode) ? "Prompt" : c.DefaultPermissionMode,
                "kiroEnabled" => (object)c.KiroEnabled,
                "kiroCliPath" => c.KiroCliPath ?? string.Empty,
                "kiroAgentEngine" => c.KiroAgentEngine ?? string.Empty,
                "claudeCodeEnabled" => (object)c.ClaudeCodeEnabled,
                "claudeAcpPath" => c.ClaudeAcpPath ?? string.Empty,
                "engineEnvironment" => string.Join(
                    Environment.NewLine, c.EngineEnvironment.Select(kv => kv.Key + "=" + kv.Value)),
                "customAcpAgents" => CustomAcpAgent.ToDisplayJson(c.CustomAcpAgents),
                "allowedCommands" => string.Join(Environment.NewLine, c.AllowedCommands),
                "alwaysPromptCommands" => string.Join(Environment.NewLine, c.AlwaysPromptCommands),
                "allowedPaths" => string.Join(Environment.NewLine, c.AllowedPaths),
                "allowedTools" => string.Join(Environment.NewLine, c.AllowedTools),
                "runCommand" => (object)c.RunCommandEnabled,
                "agentWorkspaceScope" => AgentWorkspaceScopeParser.Parse(c.AgentWorkspaceScope).ToString(),
                "terminalMirror" => (object)c.TerminalMirrorEnabled,
                // useStubIde is deliberately absent: it remains in config.json and in ExtensionConfig
                // (the test hosts and a hand-edited config still use it), but it is developer plumbing
                // rather than a product setting, so it is not offered in the UI. (The engine path
                // override left config.json altogether - it is CWKT_ENGINE_EXE only; see ExtensionConfig.)
                // Clamped on the way OUT as well as in, so a hand-edited config.json shows the Settings
                // UI the value that is actually in force rather than the one it will not honour.
                "attachmentLimitMb" => (object)Math.Max(
                    AttachmentStore.MinimumLimitMb, c.AttachmentStorageLimitMb),
                "logAcpFrames" => (object)c.LogAcpFrames,
                "logEngineChannel" => (object)c.LogEngineChannel,
                "logRendering" => (object)c.LogRendering,
                _ => null,
            };

        public Task<ExternalSettingOperationResult<T>> GetValueAsync<T>(string moniker, CancellationToken cancellationToken)
        {
            var value = Read(ExtensionConfig.Load(), Leaf(moniker));

            if (value is null)
                return ExternalSettingOperationResult.FailureResultTask<T>(
                    $"Unknown setting '{moniker}'.", ExternalSettingsErrorScope.SingleSettingOnly, isTransient: false);

            // Converted rather than unboxed, for the reason SetValueAsync's AsInt already gives on the
            // way IN: "USX hands an integer property back boxed, and which integral type is not ours to
            // predict". The defence was taken on one side only — a bare (T) unbox throws
            // InvalidCastException if USX ever asks for the attachment limit as anything but int, and
            // it throws OUT of the provider, so the whole Advanced page fails to load rather than one
            // setting failing to read.
            try
            {
                var typed = value is T t ? t : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
                // Recorded under the moniker USX asked with: its AppliesTo is an exact match on that
                // same string, so a later change is announced in the spelling the row recognises.
                _served.Served(moniker, value);
                return ExternalSettingOperationResult.SuccessResultTask(typed);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return ExternalSettingOperationResult.FailureResultTask<T>(
                    $"Setting '{moniker}' could not be read as {typeof(T).Name}.",
                    ExternalSettingsErrorScope.SingleSettingOnly,
                    isTransient: false);
            }
        }

        public Task<ExternalSettingOperationResult> SetValueAsync<T>(string moniker, T value, CancellationToken cancellationToken)
        {
            var leaf = Leaf(moniker);

            // Structured JSON is validated up front so a typo rejects with a message instead of
            // silently clobbering the stored agents.
            if (leaf == "customAcpAgents")
                return SetCustomAcpAgentsAsync(AsString(value));

            // KEY=value lines, validated so a malformed line rejects the save with the line quoted.
            if (leaf == "engineEnvironment")
                return Task.FromResult(SetEngineEnvironment(AsString(value)));

            // The switch CHOOSES a mutation rather than performing one inside the save. Save writes
            // config.json and raises Changed unconditionally — it cannot see that the delegate it ran
            // changed nothing — so with the switch inside it, refusing an unrecognised leaf still cost
            // a load, a serialize, a file write and a full Changed fanout: every subscriber re-reading
            // permissions, re-syncing the engine environment and rebuilding the MCP roster, all for a
            // name we were about to reject. Deciding first also gives the two halves of this provider
            // one shape — GetValueAsync maps the leaf, checks the result, then acts.
            Action<ExtensionConfig> mutate = leaf switch
            {
                "provider" => c => c.DefaultProvider = AsString(value),
                "defaultModel" => c => c.DefaultModel = NullIfBlank(AsString(value)),
                "messageRelease" => c => c.DefaultMessageRelease = PendingReleaseModes.ToName(
                    PendingReleaseModes.Parse(AsString(value))),
                "permissionMode" => c => c.DefaultPermissionMode = AsString(value),
                "kiroEnabled" => c => c.KiroEnabled = value is bool ke && ke,
                "kiroCliPath" => c => c.KiroCliPath = NullIfBlank(AsString(value)),
                "kiroAgentEngine" => c => c.KiroAgentEngine = NullIfBlank(AsString(value)),
                "claudeCodeEnabled" => c => c.ClaudeCodeEnabled = value is bool cce && cce,
                "claudeAcpPath" => c => c.ClaudeAcpPath = NullIfBlank(AsString(value)),
                "allowedCommands" => c => c.AllowedCommands = SplitLines(AsString(value)),
                "alwaysPromptCommands" => c => c.AlwaysPromptCommands = SplitLines(AsString(value)),
                "allowedPaths" => c => c.AllowedPaths = SplitLines(AsString(value)),
                // Normalized on the way in to canonical server/tool, so a name pasted from a log
                // or an acp capture becomes the rule that actually matches and a bare name gains
                // our server; an unparseable line is dropped rather than stored as a rule that
                // could never fire (issue #129).
                "allowedTools" => c => c.AllowedTools = NormalizeToolRules(SplitLines(AsString(value))),
                "runCommand" => c => c.RunCommandEnabled = value is bool rc && rc,
                // USX hands an integer property back boxed, and which integral type is not ours to
                // predict — Convert rather than a pattern, so a long or a decimal from the UI does
                // not silently fall through and leave the setting unchanged.
                "attachmentLimitMb" => c => c.AttachmentStorageLimitMb = AsInt(value, c.AttachmentStorageLimitMb),
                // Round-tripped through the parser so an unrecognised value can never be persisted
                // (the enum's two names are the only things the engine's wire contract accepts).
                "agentWorkspaceScope" => c => c.AgentWorkspaceScope =
                    AgentWorkspaceScopeParser.Parse(AsString(value)).ToString(),
                "terminalMirror" => c => c.TerminalMirrorEnabled = value is bool tm && tm,
                "logAcpFrames" => c => c.LogAcpFrames = value is bool ab && ab,
                "logEngineChannel" => c => c.LogEngineChannel = value is bool eb && eb,
                "logRendering" => c => c.LogRendering = value is bool rb && rb,
                _ => null,
            };

            // Rejected the way GetValueAsync rejects the same moniker. Before this arm existed an
            // unrecognised leaf fell through and returned success, so the two halves of this provider
            // disagreed about the same name: the page reported the value could not be READ while
            // accepting every write of it, and the user was told their setting saved.
            //
            // Reachable through the rename rule AGENTS.md already states: a leaf name is the config
            // binding, so a registration.json rename that misses this switch lands exactly here.
            // Reject rather than a hand-rolled Failure: it is the established shape here, it notes the
            // failure for the page, and it carries isTransient: true — which AGENTS.md requires of
            // every Unified Settings failure, because false greys the setting out pending an event we
            // never raise.
            if (mutate is null)
                return Task.FromResult(Reject($"Unknown setting '{moniker}'."));

            SaveFromDialog(mutate);

            NoteSuccess();
            return ExternalSettingOperationResult.SuccessResultTask();
        }

        /// <summary>
        /// Saving custom agents probes each new/changed entry with a real ACP initialize handshake
        /// (<see cref="AcpAgentProbe"/>) — so a bad path or a CLI that doesn't speak ACP rejects the
        /// save with the reason, and entries without hand-written capabilities get the discovered
        /// ones persisted (marked <c>capabilitiesDiscovered</c> so session-start refresh may update
        /// them later). Escape hatch for offline editing: config.json directly, which never probes.
        /// </summary>
        private async Task<ExternalSettingOperationResult> SetCustomAcpAgentsAsync(string json)
        {
            if (!CustomAcpAgent.TryParseList(json, out var agents, out var error))
                return Reject(error);

            var previous = ExtensionConfig.Load().CustomAcpAgents;
            foreach (var agent in agents)
            {
                if (!NeedsProbe(agent, previous))
                    continue;

                var probe = await Task.Run(() => AcpAgentProbe.Probe(agent)).ConfigureAwait(false);
                if (!probe.Ok)
                    return Reject($"Agent '{agent.ProviderId}': {probe.Error}");

                if (agent.Capabilities is null || agent.Capabilities.Length == 0)
                {
                    agent.Capabilities = DiscoveredCapabilities(probe);
                    agent.CapabilitiesDiscovered = true;
                }
            }

            SaveFromDialog(c => c.CustomAcpAgents = agents);
            NoteSuccess();
            return ExternalSettingOperationResult.Success.Instance;
        }

        /// <summary>
        /// Parses the engine-environment setting (one <c>KEY=value</c> per line; see
        /// <see cref="ExtensionConfig.EngineEnvironment"/>) and persists it. A line without '=' or with
        /// an empty key, or a duplicate key (env var names are case-insensitive on Windows), rejects
        /// the save with the offending line so a typo can't silently drop a proxy setting.
        /// </summary>
        private ExternalSettingOperationResult SetEngineEnvironment(string text)
        {
            var env = new Dictionary<string, string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                var eq = line.IndexOf('=');
                var key = eq > 0 ? line.Substring(0, eq).Trim() : string.Empty;
                if (key.Length == 0)
                    return Reject($"'{line}': expected KEY=value.");
                if (!seen.Add(key))
                    return Reject($"'{key}' appears more than once.");

                env[key] = line.Substring(eq + 1).Trim();
            }

            SaveFromDialog(c => c.EngineEnvironment = env);
            NoteSuccess();
            return ExternalSettingOperationResult.Success.Instance;
        }

        // A rejected save from user input: isTransient TRUE is load-bearing — false makes USX grey the
        // setting out (pending ErrorConditionResolved), locking the field so the input can't be fixed.
        private ExternalSettingOperationResult Reject(string message)
        {
            NoteFailure();
            return new ExternalSettingOperationResult.Failure(
                message, ExternalSettingsErrorScope.SingleSettingOnly, isTransient: true);
        }

        // Probe when the entry is new, its launch config changed, or capabilities need discovering.
        // An unchanged entry re-saved (settings round-trip) skips the handshake.
        private static bool NeedsProbe(CustomAcpAgent agent, CustomAcpAgent[] previous)
        {
            if (agent.Capabilities is null || agent.Capabilities.Length == 0)
                return true;

            var old = Array.Find(previous, p =>
                string.Equals(p.ProviderId, agent.ProviderId, StringComparison.OrdinalIgnoreCase));
            return old is null
                || !string.Equals(old.CliPath, agent.CliPath, StringComparison.OrdinalIgnoreCase)
                || !(old.Args ?? Array.Empty<string>()).SequenceEqual(agent.Args ?? Array.Empty<string>());
        }

        private static string[] DiscoveredCapabilities(AcpAgentProbe.Result probe)
        {
            // The spec-ACP baseline plus whatever initialize advertised (loadSession → ResumeSession).
            var caps = new List<string> { "ToolCalls", "ClientFileSystem", "Mcp", "Thinking", "Cancellation" };
            if (probe.LoadSession)
                caps.Add("ResumeSession");
            return caps.ToArray();
        }

        // No dynamic enums/messages in our registration — these are never meaningfully called.
        public Task<ExternalSettingOperationResult<IReadOnlyList<EnumChoice>>> GetEnumChoicesAsync(string enumSettingMoniker, CancellationToken cancellationToken) =>
            ExternalSettingOperationResult.SuccessResultTask<IReadOnlyList<EnumChoice>>(Array.Empty<EnumChoice>());

        public Task<string> GetMessageTextAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);

        // The "%config.json%" link in backingStoreDescription calls this — open the file in the editor.
        public Task OpenBackingStoreAsync(CancellationToken cancellationToken)
        {
            try { Process.Start(new ProcessStartInfo(ExtensionConfig.DefaultPath) { UseShellExecute = true }); }
            catch { /* best effort */ }
            return Task.CompletedTask;
        }

        // The moniker may arrive as the full dotted path or the leaf key; match on the leaf either way.
        private static string Leaf(string moniker)
        {
            var i = moniker.LastIndexOf('.');
            return i >= 0 ? moniker.Substring(i + 1) : moniker;
        }

        private static string AsString(object value) => value?.ToString() ?? string.Empty;

        /// <summary>
        /// A numeric setting's value, whatever integral type USX boxed it as. Falls back to the current
        /// value rather than to zero: a value we could not read must leave the setting alone, not
        /// silently reset it — and here zero would mean an empty attachment directory.
        /// </summary>
        private static int AsInt(object value, int fallback)
        {
            if (value is int direct)
                return direct;
            try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string[] SplitLines(string s) =>
            (s ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToArray();

        // Tool rules are stored canonically (server/tool) so one rule holds on every backend and the
        // saved list shows its own format. A line the policy could never match is dropped here rather
        // than kept as a rule that silently does nothing — the setting shows what is in force, the same
        // principle as the clamped size limit.
        private static string[] NormalizeToolRules(string[] lines)
        {
            var kept = new List<string>(lines.Length);
            foreach (var line in lines)
                if (IdeMcpServer.TryNormalizeToolRule(line, out var local) &&
                    !kept.Exists(k => string.Equals(k, local, StringComparison.OrdinalIgnoreCase)))
                    kept.Add(local);
            return kept.ToArray();
        }
    }
}
