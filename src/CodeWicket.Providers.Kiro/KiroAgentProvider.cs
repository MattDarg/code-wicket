using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;

namespace CodeWicket.Providers.Kiro
{
    /// <summary>
    /// The Kiro backend: a thin <see cref="AcpAgentProvider"/> that supplies Kiro's launch
    /// config (<c>kiro-cli acp</c>) and layers on Kiro's optional per-session launch commands
    /// (e.g. model selection). The CLI owns the agent loop, tools, and specs; the generic base
    /// handles the ACP protocol.
    /// </summary>
    public sealed class KiroAgentProvider : AcpAgentProvider
    {
        // Fallback shown only when the live catalog can't be fetched (CLI missing / not logged in),
        // so the picker is never empty and sessions still launch. Its id "default" is special-cased in
        // BuildLaunchArgs to launch without --model (Kiro then picks its own default).
        private static readonly ModelInfo FallbackModel = new("default", "Kiro default");

        // Populated on first access (the shell's provider picker) by shelling out to the CLI. Cached
        // best-effort so a missing/unauthenticated CLI falls back to the single entry above — but a
        // *failure* is cached only until the next read past the cooldown (see Models), so a transient
        // timeout (slow/proxied network at engine launch) self-heals instead of stranding the picker
        // on the fallback for the engine's whole lifetime.
        private readonly string _cliPath;
        private readonly object _modelGate = new();
        private IReadOnlyList<ModelInfo>? _cachedModels; // last result (may be the seed/fallback)
        private bool _haveLiveList;                       // true once a real (non-empty) catalog was fetched
        private DateTime _nextRetryUtc = DateTime.MinValue;

        // Last run's persisted model list (CWKT_MODEL_CACHE -> SeedModels), shown INSTEAD of the bare
        // "Kiro default" while the live shell-out hasn't yet succeeded — so a cold launch on a slow/proxied
        // network isn't stranded on a one-entry picker. A successful probe still supersedes it. Kiro's
        // Models override bypasses the base _models field, so we capture the seed here explicitly.
        private IReadOnlyList<ModelInfo>? _seededModels;

        // The in-flight background probe (null when none is running) and where to report a list that
        // differs from the seed the picker was filled from. See BeginModelRefresh.
        private Task? _refresh;
        private Action<IReadOnlyList<ModelInfo>>? _onModelsRefreshed;

        // Don't re-probe a failing CLI more often than this: bounds the pause when kiro-cli is genuinely
        // unreachable (only the first read past each window pays the shell-out timeout), while still
        // letting the list recover on its own once the network comes back.
        private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);

        // The optional Kiro agent config (kiro-cli acp --agent <name>); null/blank = Kiro's default agent.
        private readonly string? _agent;

        // The optional Kiro agent engine (kiro-cli acp --agent-engine <value>, e.g. v1/v2/v3);
        // null/blank = Kiro's default engine.
        private readonly string? _agentEngine;

        // Seams so the cooldown-retry cache is testable without shelling out to kiro-cli or waiting real
        // time: the catalog fetch and the clock. Production wires the real static probe + wall clock.
        private readonly Func<string, IReadOnlyList<ModelInfo>> _fetchModels;
        private readonly Func<DateTime> _utcNow;

        public KiroAgentProvider(KiroProviderOptions? options = null)
            : this(options ?? new KiroProviderOptions(), KiroModelCatalog.TryList, static () => DateTime.UtcNow)
        {
        }

        // Test seam (see the field comments): inject the catalog fetch + clock.
        internal KiroAgentProvider(
            KiroProviderOptions options,
            Func<string, IReadOnlyList<ModelInfo>> fetchModels,
            Func<DateTime> utcNow)
            : base(BuildConfig(options))
        {
            _cliPath = options.CliPath;
            _agent = string.IsNullOrWhiteSpace(options.Agent) ? null : options.Agent!.Trim();
            _agentEngine = string.IsNullOrWhiteSpace(options.AgentEngine) ? null : options.AgentEngine!.Trim();
            _fetchModels = fetchModels;
            _utcNow = utcNow;
        }

        /// <summary>
        /// Captures the persisted seed (see <see cref="_seededModels"/>) so the picker shows last run's
        /// real models before the live shell-out succeeds. Also updates the base field for coherence,
        /// though our <see cref="Models"/> override is authoritative.
        /// </summary>
        public override void SeedModels(IReadOnlyList<ModelInfo> models)
        {
            base.SeedModels(models);
            if (models is { Count: > 0 })
                lock (_modelGate)
                    _seededModels = models;
        }

        /// <summary>
        /// The synthetic "default" plus every model the CLI reports it can drive. A successful (non-empty)
        /// catalog is cached for the engine's lifetime; a failed probe caches the fallback only until the
        /// next read past <see cref="RetryCooldown"/>, so a transient launch-time timeout self-heals.
        /// </summary>
        public override IReadOnlyList<ModelInfo> Models
        {
            get
            {
                lock (_modelGate)
                {
                    // A real list is authoritative for the engine's lifetime — models rarely change.
                    if (_haveLiveList)
                        return _cachedModels!;

                    // A probe is already running: answer from whatever we have rather than queueing a
                    // caller behind a CLI spawn. This is the normal path once the engine has invited the
                    // refresh at startup, and it is what keeps engine/listProviders off the shell-out.
                    if (_refresh is not null)
                        return _cachedModels ?? _seededModels ?? new[] { FallbackModel };

                    // No live list yet. Re-probe, but no more often than the cooldown so a persistently
                    // unreachable CLI doesn't pause every provider-list call. Holding the gate across the
                    // probe also collapses a concurrent stampede into a single shell-out.
                    if (_cachedModels is not null && _utcNow() < _nextRetryUtc)
                        return _cachedModels;

                    // We already know last run's real list, so there is nothing to WAIT for: answer from
                    // it and let the probe run behind the caller. The alternative — the original
                    // behaviour — spends ~1.5 s of kiro-cli on the path to opening the chat window
                    // (`--list-models` 967-1484 ms plus `settings chat.defaultModel` ~500 ms, timed) to
                    // re-derive a list we persisted ourselves and just handed back in.
                    if (_seededModels is not null)
                    {
                        _cachedModels = _seededModels;
                        StartRefreshLocked();
                        return _cachedModels;
                    }

                    // First ever launch on this machine (or a cache we never managed to write): there is
                    // no honest answer to give yet, so this one caller does pay the shell-out inline
                    // rather than showing a picker with one synthetic entry in it.

                    // The live catalog lists Kiro's default (e.g. "auto") first, so the UI's Models[0]
                    // default selection is Kiro's default model. Only fall back if it's empty.
                    var live = _fetchModels(_cliPath);
                    if (live.Count > 0)
                    {
                        _haveLiveList = true;
                        _cachedModels = live;
                    }
                    else
                    {
                        // Prefer last run's seeded list over the bare fallback, and keep retrying so a
                        // recovered network still supersedes it with the live catalog.
                        _cachedModels = _seededModels ?? new[] { FallbackModel };
                        _nextRetryUtc = _utcNow() + RetryCooldown;
                    }

                    return _cachedModels;
                }
            }
        }

        /// <summary>
        /// Starts the live catalog probe in the background and reports it when it lands. Called by the
        /// engine right after registration, so the ~1.5 s of kiro-cli runs alongside the host's own
        /// startup (`ide services` + `chat view` is 2.8-4.9 s of UI-thread work) instead of inside the
        /// <c>engine/listProviders</c> the picker is waiting on.
        ///
        /// <para><b>This probe is also the login gate.</b> <see cref="KiroLoginState.IsSignedOut"/> runs
        /// the very same <c>--list-models</c> command and is short-circuited by the
        /// <c>NoteSignedIn()</c> inside <see cref="KiroModelCatalog.TryList"/>. So deferring the fetch
        /// without running it at all would not remove a spawn — it would move one onto the session-start
        /// path. Starting it here keeps the short-circuit, which is why this is an eager background
        /// probe rather than lazy-on-first-dropdown.</para>
        /// </summary>
        public override void BeginModelRefresh(Action<IReadOnlyList<ModelInfo>>? onRefreshed = null)
        {
            IReadOnlyList<ModelInfo>? alreadyDiscovered = null;
            lock (_modelGate)
            {
                if (onRefreshed is not null)
                    _onModelsRefreshed = onRefreshed;

                if (_haveLiveList)
                {
                    // Discovered BEFORE anyone was listening, so returning here would strand it. The
                    // engine registers this callback deliberately after host.Start() (the answer
                    // arrives as a notification, so the connection has to be listening first) - which
                    // leaves a real window: an engine/listProviders landing in between takes the
                    // seeded branch and starts a probe with _onModelsRefreshed still null. If that
                    // probe finished first, _haveLiveList was set, this returned early, and the fresh
                    // list was never pushed - the picker showing last run's seed for the engine's
                    // whole lifetime while Models already held the right answer.
                    alreadyDiscovered = onRefreshed is not null ? Models : null;
                }
                else if (_refresh is null)
                {
                    StartRefreshLocked();
                }
            }

            // Outside the lock: the callback publishes to the engine host, and nothing it does
            // belongs under a gate a CLI probe can also be holding.
            if (alreadyDiscovered is { Count: > 0 })
                onRefreshed!(alreadyDiscovered);
        }

        /// <summary>
        /// Waits for the background model probe before running the login gate, because they are the
        /// SAME kiro-cli command and the gate short-circuits on the flag the probe sets
        /// (<see cref="KiroLoginState.NoteSignedIn"/>, called from
        /// <see cref="KiroModelCatalog.TryList"/>).
        ///
        /// <para><b>Measured regression this fixes.</b> While the probe ran inline inside
        /// <c>listProviders</c> it had always finished before any session started, so the gate spawned
        /// nothing. Moving it to the background broke that: the session now starts 243–1243 ms in
        /// against a ~1.5 s probe, so the gate saw the flag unset and spawned a SECOND identical CLI
        /// process alongside the first. Across eight collected sessions the session's own spawn went
        /// from ~500 ms to 1479–1802 ms, and Kiro's readiness — the thing the whole change was supposed
        /// to improve — did not move. Waiting for a probe we already started is strictly cheaper than
        /// duplicating it.</para>
        ///
        /// <para>Bounded, and a timeout falls through to the gate's own probe rather than failing the
        /// launch: this is an optimisation, and it must never be the reason a session can't open.
        /// Blocking here is safe — the preflight already blocked on a CLI spawn, and it runs on the
        /// engine's session-start path, never the UI thread.</para>
        /// </summary>
        protected override string? Preflight()
        {
            WaitForPendingModelRefresh();
            return base.Preflight();
        }

        // Test seam. Preflight is protected, and the WIRING is what regressed - a test that called the
        // wait directly passed with the override gutted, which pins nothing.
        internal string? PreflightForTests() => Preflight();

        internal void WaitForPendingModelRefresh()
        {
            // Already established: the gate will short-circuit, so there is nothing to wait for.
            if (KiroLoginState.IsKnownSignedIn)
                return;

            var probe = PendingModelRefresh;
            if (probe is null)
                return;

            // Whichever comes FIRST: login being established, or the probe ending.
            //
            // The gate only needs to know we are signed in, and KiroModelCatalog.TryList establishes
            // that on the FIRST of its two CLI calls (`--list-models`), then spends ~500 ms more on
            // `settings chat.defaultModel` purely to order the picker. Waiting for the probe as a whole
            // therefore waited out a call the gate has no interest in — measured 500 ms of a ~1.9 s
            // wait, on the path to the user's first prompt.
            //
            // Losing the serialisation is the trade: the ACP session now spawns while that second call
            // may still be running. It is one lightweight settings read against a process launch, where
            // before the change the overlap was two full `--list-models` probes racing each other.
            //
            // The probe task is still a wait target because a probe that FAILS never signals — that is
            // the path where the gate must run its own check, and it can only do that once the probe
            // has stopped occupying the CLI.
            try { Task.WaitAny(new[] { probe, KiroLoginState.SignedIn }, ProbeWaitTimeout); }
            catch { /* never the reason a session fails to launch */ }
        }

        // Comfortably past KiroModelCatalog's own 10 s per-process cap, so this only ever expires when
        // the probe is genuinely wedged rather than merely slow.
        private static readonly TimeSpan ProbeWaitTimeout = TimeSpan.FromSeconds(15);

        // Test seam: the in-flight probe, so a test can await the background refresh instead of sleeping
        // on it. Null once it has completed (or before one starts), which is itself an answer.
        internal Task? PendingModelRefresh
        {
            get { lock (_modelGate) return _refresh; }
        }

        // Caller holds _modelGate. The fetch itself runs OFF the gate (inside the task) — holding it
        // across a CLI spawn is exactly what the inline path does, and re-creating that here would put
        // every Models reader back behind the shell-out we just moved off their thread.
        private void StartRefreshLocked() => _refresh = Task.Run(RefreshModels);

        private void RefreshModels()
        {
            IReadOnlyList<ModelInfo> live;
            try { live = _fetchModels(_cliPath); }
            catch { live = Array.Empty<ModelInfo>(); } // a diagnostic probe must never fault the engine

            Action<IReadOnlyList<ModelInfo>>? notify = null;
            IReadOnlyList<ModelInfo>? refreshed = null;
            lock (_modelGate)
            {
                _refresh = null;
                if (live.Count > 0)
                {
                    _haveLiveList = true;
                    _cachedModels = live;
                    // Only worth telling the host when it actually differs from what the picker already
                    // shows — the steady state is a cache that was right, and a redundant push would
                    // rebuild the model list under a user who may have the dropdown open.
                    if (!SameModels(_seededModels, live))
                    {
                        notify = _onModelsRefreshed;
                        refreshed = live;
                    }
                }
                else
                {
                    // Keep whatever the picker is already showing and let the cooldown allow a retry, so
                    // a network that comes back still supersedes the seed.
                    _cachedModels ??= _seededModels ?? new[] { FallbackModel };
                    _nextRetryUtc = _utcNow() + RetryCooldown;
                }
            }

            if (notify is not null && refreshed is not null)
                try { notify(refreshed); } catch { /* the host's problem, never ours */ }
        }

        private static bool SameModels(IReadOnlyList<ModelInfo>? a, IReadOnlyList<ModelInfo> b)
        {
            if (a is null || a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
                // Order matters as much as membership: Models[0] is the default the picker selects.
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static AcpAgentConfig BuildConfig(KiroProviderOptions options) => new()
        {
            ProviderId = "kiro",
            DisplayName = "Kiro",
            CliPath = options.CliPath,
            // "acp" puts the CLI into ACP mode; ExtraArgs are static extras (e.g. --agent <name>).
            LaunchArgs = new[] { "acp" }.Concat(options.ExtraArgs).ToList(),
            // kiro-cli sends no agentInfo at initialize (verified against a captured acp.log), so its
            // version has to be asked for. `--version` answers "kiro-cli-chat 2.13.0" and exits 0.
            // Diagnostic only, and fire-and-forget — see AgentVersionProbe. Issue #82 was a kiro-cli
            // 2.0.1 failure fixed by upgrading, where the version appeared nowhere.
            VersionArgs = new[] { "--version" },
            // No ResumeCommandTemplate: kiro-cli's resume flag has not been verified by anyone here, and
            // an unverified command is worse than none - the user pastes it, it fails or opens the wrong
            // conversation, and the failure looks like ours. Run it, then declare it.
            // Fallback list if the Models override is ever bypassed; the override is authoritative.
            Models = new[] { FallbackModel },
            // Kiro resolves its whole workspace from cwd with no upward walk of its own — steering,
            // agent configs and workspace mcp.json all live under <cwd>\.kiro — so a solution nested
            // below the folder holding .kiro silently loses all of it (issue #54). The required
            // entries keep a stray empty .kiro from capturing the working directory.
            WorkspaceMarkers = new[]
            {
                new WorkspaceMarker(".kiro", new[] { "steering", "specs", "agents", "settings", "hooks" }),
            },
            Capabilities =
                AgentCapabilities.ToolCalls
                | AgentCapabilities.ClientFileSystem
                | AgentCapabilities.Mcp
                | AgentCapabilities.Thinking
                | AgentCapabilities.Cancellation
                | AgentCapabilities.ResumeSession
                | AgentCapabilities.ModelSelection,
            // Kiro's v3 agent engine delegates token refresh to the ACP host (v2 never calls this).
            // Answered by shelling back into the same CLI, so auth rides the user's kiro-cli login.
            AuthTokenProvider = ct => KiroKasTokenProvider.GetAsync(options.CliPath, ct),
            // Refuse to launch a signed-out kiro-cli rather than let it open a browser login inside a
            // headless, timeout-killed process (see KiroLoginState — and note it probes with
            // `chat --no-interactive`, not `whoami`, because whoami calls a dead token a good login).
            // The message is the one Kiro itself gives in its ACP authMethods, so both routes agree.
            PreflightCheck = () => KiroLoginState.IsSignedOut(options.CliPath)
                ? "Kiro isn't signed in. Run 'kiro-cli login' in a terminal, then try again. "
                  + "See https://kiro.dev/docs/cli/authentication/"
                : null,
            SessionMeta = ParseSessionMeta(options.SessionMetaJson),
            // Kiro's ACP modes are its agents (kiro_default / kiro_planner on v2; Default, Spec and every
            // file-based agent on v3), so that is what the panel calls them.
            ModeLabel = "Agent",
        };

        private static JsonElement? ParseSessionMeta(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(json!);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                System.Console.Error.WriteLine($"[kiro] session meta ignored, not valid JSON: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds Kiro's optional per-session launch commands on top of the configured base args.
        /// Kiro's <c>acp</c> also accepts <c>--effort</c>, <c>--agent-engine</c>, and
        /// <c>--trust-all-tools</c>; wire those here as we surface them in <see cref="SessionOptions"/>.
        /// </summary>
        protected override IReadOnlyList<string> BuildLaunchArgs(SessionOptions options)
        {
            // Built ON the session-independent args rather than beside them, so --agent/--agent-engine
            // cannot be present for a session and absent for a session LIST. They select which store
            // kiro-cli reads: v3 keeps its own sessions, so listing without the flag reads a different
            // engine's history and reports an empty list, which is indistinguishable from the user
            // having none (issue #108).
            var args = new List<string>(BuildBaseLaunchArgs());

            // v3 rejects the --model launch flag ("not supported with --agent-engine=v3") and instead
            // selects models over ACP (session config options / session/set_model), which the generic
            // AcpAgentSession already drives from SessionOptions.ModelId. v1/v2 still take --model, so
            // only skip it for v3 — otherwise passing it kills the CLI at launch (issue: v3 crash).
            if (!IsV3(_agentEngine)
                && !string.IsNullOrEmpty(options.ModelId) && options.ModelId != "default")
            {
                args.Add("--model");
                args.Add(options.ModelId!);
            }

            return args;
        }

        /// <summary>
        /// Kiro's session-independent flags. <c>--agent</c> selects an agent config whose mcpServers
        /// (or includeMcpJson) the built-in default wouldn't see; <c>--agent-engine</c> selects the
        /// engine (v1/v2/v3). Both are configured rather than per-session, and both belong here rather
        /// than in <see cref="BuildLaunchArgs"/> - see that base method for why the engine flag in
        /// particular has to reach a listing process.
        /// </summary>
        protected override IReadOnlyList<string> BuildBaseLaunchArgs()
        {
            var args = new List<string>(Config.LaunchArgs);

            // v3 rejects --agent at launch ("not supported with --agent-engine=v3", exit 2 — measured
            // 2026-09-11 by the backend-gate proof), the same way it rejects --model, and with the flag
            // the session died before the handshake with a bare "stopped responding". Under v3 the
            // agent is selected over ACP instead (RequestedSessionModeId → session/set_mode), where a
            // wrong name is a notice rather than a dead session.
            if (_agent is not null && !IsV3(_agentEngine))
            {
                args.Add("--agent");
                args.Add(_agent);
            }

            if (_agentEngine is not null)
            {
                args.Add("--agent-engine");
                args.Add(_agentEngine);
            }

            return args;
        }

        /// <inheritdoc/>
        /// <remarks>v3 only: v1/v2 take the agent as a launch flag (proven to select it — the
        /// backend-gate proof's shadowed agent opened as <c>currentModeId</c>), so nothing is re-sent.
        /// On v3 every agent the engine knows is a session mode, workspace-defined ones included and
        /// tagged with their origin, so the same name selects the same thing by the other route.</remarks>
        protected override string? RequestedSessionModeId => IsV3(_agentEngine) ? _agent : null;

        // v3 is the only known engine that refuses --model (and --agent); kept a helper so the intent
        // is explicit.
        private static bool IsV3(string? engine) =>
            string.Equals(engine, "v3", StringComparison.OrdinalIgnoreCase);
    }
}
