using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Shell
{
    /// <summary>A cached model choice persisted per provider (see <see cref="ExtensionConfig.DiscoveredModels"/>).</summary>
    public sealed class CachedModel
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double? RateMultiplier { get; set; }
    }

    /// <summary>
    /// User-level extension configuration: which backend to use by default, and (groundwork for the
    /// picker) the default model. Read from a JSON file so the choice is durable and editable without
    /// rebuilding — and crucially does NOT rely on <c>launchSettings.json</c> environment variables,
    /// which the VSSDK debug target never passes to the experimental devenv (so they don't reach the
    /// VSIX or the engine it spawns). A <c>CWKT_PROVIDER</c> env var still wins when set, for quick
    /// one-off debugging.
    /// </summary>
    public sealed class ExtensionConfig
    {
        /// <summary>Provider id: "kiro" (real kiro-cli backend) or "fake" (in-process test double).</summary>
        public string DefaultProvider { get; set; } = "kiro";

        /// <summary>Optional default model id passed to the provider; null lets the provider choose.</summary>
        public string? DefaultModel { get; set; }

        /// <summary>How permission requests are handled, a cumulative risk ladder:
        /// Prompt | AcceptReads | AcceptEdits | AcceptAll. (A legacy "ReadOnly" migrates to AcceptReads on load.)</summary>
        public string DefaultPermissionMode { get; set; } = "Prompt";

        /// <summary>
        /// Where a message typed mid-turn goes by default: <c>Queue</c> (after the turn ends) or
        /// <c>Steer</c> (at the agent's next step). The tray's pill still changes it for the session —
        /// this only decides which rung the chat window opens on.
        /// <para>Queue remains the shipped default, for the reason it was chosen: the next step may be
        /// seconds away, so the sooner rung sends earlier than someone typing "and then…" expects. The
        /// setting exists because that trade is a working style rather than a fact (issue #190).</para>
        /// </summary>
        public string DefaultMessageRelease { get; set; } = "Queue";

        /// <summary>
        /// Glob patterns for commands/actions to auto-approve without prompting (e.g. "git status").
        /// The grammar is <see cref="PermissionGlob"/>'s: <c>*</c>, <c>?</c>, and <c>[*]</c>/<c>[?]</c>
        /// for the literal characters - which is what the banner seeds, so a wildcard the AGENT put in
        /// its command never becomes a wildcard in the rule (pre-release security review, September 2026).
        /// </summary>
        public string[] AllowedCommands { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Glob patterns (aggressive, unanchored substring) for sensitive commands/actions that must
        /// ALWAYS be reviewed — even under an auto-approve mode. A match forces the permission banner
        /// (never auto-approved by allow-list or mode), flags it in red with the matched text called out,
        /// removes "Allow always", and requires an extra confirm before a one-time Allow. Keep this list
        /// short and high-signal (nothing ships by default): if it fires constantly the warning becomes
        /// noise. NOT a silent block — every match is reviewable. (Migrated from the retired
        /// <c>deniedCommands</c> hard-deny list.)
        /// </summary>
        public string[] AlwaysPromptCommands { get; set; } = Array.Empty<string>();

        /// <summary>Glob patterns for file paths whose edits are auto-approved without prompting
        /// (matched against the absolute path an edit targets, e.g. <c>C:\repo\src\*</c>).</summary>
        public string[] AllowedPaths { get; set; } = Array.Empty<string>();

        /// <summary>
        /// MCP tools auto-approved without prompting, one canonical <c>server/tool</c> per entry
        /// (<c>code-wicket/build_solution</c>) — including our own, so the stored list explains
        /// its own format to anyone editing it by hand. One rule holds on every backend even though each
        /// spells the same tool differently (<c>@code-wicket/build_solution</c> on Kiro,
        /// <c>mcp__code-wicket__build_solution</c> on Claude): the rule is matched by building
        /// both spellings from its own halves. Exact names, not globs — a rule names one tool and there
        /// is no scope to widen.
        /// <para>Keeping the server is also the collision guard: a rule for <c>github/create_issue</c>
        /// can never auto-approve OUR <c>create_issue</c>, nor the reverse (issue #129). A bare name
        /// typed by hand is taken as ours and gains the server on save. See
        /// <c>IdeMcpServer.TryNormalizeToolRule</c>.</para>
        /// </summary>
        public string[] AllowedTools { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Whether the built-in Kiro backend is available. Off = never registered by the engine, so
        /// it doesn't appear in the picker and can't be started — for installs where Kiro can never
        /// work. Default on. (Engine-consumed via the derived <c>CWKT_DISABLED_PROVIDERS</c>; applies
        /// on the next engine launch. Custom agents toggle by presence in <see cref="CustomAcpAgents"/>.)
        /// </summary>
        public bool KiroEnabled { get; set; } = true;

        /// <summary>
        /// Whether the built-in Claude Code backend is available. Off = never registered by the engine
        /// (not in the picker, can't be started) — for installs where Claude Code can never work.
        /// Default on. (Engine-consumed via the derived <c>CWKT_DISABLED_PROVIDERS</c>.)
        /// </summary>
        public bool ClaudeCodeEnabled { get; set; } = true;

        /// <summary>Path to the Kiro CLI; null/empty uses "kiro-cli" on PATH. (Engine-consumed.)</summary>
        public string? KiroCliPath { get; set; }

        // There is deliberately NO Kiro agent setting. One shipped pre-release ("Kiro agent config" →
        // kiro-cli acp --agent) and was removed on 2026-09-11 before release: its rationale over-claimed
        // (Kiro's default agent already loads mcp.json; only agent-SCOPED servers needed it), it was
        // the surface a future "Code Wicket owns the Kiro agent" feature would have to be exclusive
        // with, and on v2 it was a second route by which a repository could redefine the running agent.
        // The provider option (KiroProviderOptions.Agent) and the wire plumbing remain as an
        // implementation tool. The record: docs/engineering/kiro-agents.md.

        /// <summary>Optional Kiro agent engine to launch with (<c>kiro-cli acp --agent-engine</c>,
        /// e.g. <c>v1</c>/<c>v2</c>/<c>v3</c>); null/empty uses Kiro's default engine. (Engine-consumed,
        /// forwarded as <c>CWKT_KIRO_AGENT_ENGINE</c>.)</summary>
        public string? KiroAgentEngine { get; set; }

        /// <summary>Path to the Claude Code ACP adapter (npm bin <c>claude-agent-acp</c>);
        /// null/empty resolves it on PATH. (Engine-consumed.)</summary>
        public string? ClaudeAcpPath { get; set; }

        /// <summary>User-configured ACP agents beyond the built-ins — any stdio-ACP CLI added as a
        /// backend via configuration alone. (Engine-consumed, forwarded as <c>CWKT_CUSTOM_AGENTS</c>;
        /// applied on the next engine launch.)</summary>
        public CustomAcpAgent[] CustomAcpAgents { get; set; } = Array.Empty<CustomAcpAgent>();

        /// <summary>
        /// Extra environment variables set on the engine process — and, by inheritance, on every
        /// agent CLI the engine spawns (kiro-cli, claude-agent-acp, custom agents). The intended use
        /// is corporate-network plumbing the CLIs read from the environment: <c>HTTPS_PROXY</c> /
        /// <c>HTTP_PROXY</c> / <c>NO_PROXY</c>, and TLS-interception CA bundles (<c>AWS_CA_BUNDLE</c>
        /// for kiro-cli, <c>NODE_EXTRA_CA_CERTS</c> for the Node-based Claude adapter). Applied on the
        /// next engine launch (reopen the chat window). Reserved CWKT_* keys set by the host win over
        /// entries here.
        /// </summary>
        public Dictionary<string, string> EngineEnvironment { get; set; } = new();

        /// <summary>
        /// Cached model lists discovered from a live session, keyed by provider id. Some backends
        /// (Claude Code) only reveal their real model list once a session opens; caching it here lets
        /// the picker show the right models on the next launch instead of a bare seed, refreshed from
        /// the first session each run. (Engine-consumed, forwarded as <c>CWKT_MODEL_CACHE</c>.)
        /// </summary>
        public Dictionary<string, CachedModel[]> DiscoveredModels { get; set; } = new();

        /// <summary>Transcript zoom factor (Ctrl+MouseWheel in the chat); 1.0 = 100%. Persisted so the choice survives restarts.</summary>
        public double ChatZoom { get; set; } = 1.0;

        /// <summary>
        /// Resting height of the chat message box in device-independent pixels (pre-zoom), set by
        /// dragging the grip on its top edge. It is a floor, not a fixed size — content can still
        /// grow the box past it — so 0 (the default) means "start at one line and grow with what you
        /// type", i.e. the behaviour before the grip existed. Re-clamped against the pane at runtime,
        /// since a height chosen in a tall window must not crowd out the transcript in a short one.
        /// </summary>
        public double ChatInputHeight { get; set; }

        /// <summary>
        /// Offer the agent the <c>run_command</c> IDE tool: run a command line in the VS developer
        /// environment (msbuild, vstest.console, signing tools on PATH). <b>Default on</b> — it earns
        /// its place often enough that off-by-default mostly meant the capability went unused, and the
        /// permission model is what bounds it rather than the registration: <c>run_command</c> carries
        /// the top <c>ToolRisk.Command</c> tier, so every permission mode below AcceptAll prompts per
        /// invocation and nothing runs unasked. Off = a disabled install never advertises the tool at
        /// all. Applies when the chat window next opens (the tool list is snapshotted into the agent
        /// session at start).
        /// <para><b>Flipping this default does NOT reach existing installs</b>: <see cref="Update"/>
        /// serializes the whole object, so any config.json written before the flip already carries
        /// <c>"runCommandEnabled": false</c> and keeps it. That is deliberate — a stored <c>false</c>
        /// is indistinguishable from a deliberate opt-out, and overriding it would turn a
        /// command-running tool on behind the user's back.</para>
        /// </summary>
        public bool RunCommandEnabled { get; set; } = true;

        /// <summary>
        /// Mirror agent command output into a real VS terminal pane ("Code Wicket Terminal").
        /// Cosmetic on top of the chat's tool rows — the pane rides a VS-internal brokered service,
        /// so it is opt-in (default off) and degrades silently if the service misbehaves. VSIX-only
        /// (the standalone hosts have no VS terminal); applies live via <see cref="Changed"/>.
        /// </summary>
        public bool TerminalMirrorEnabled { get; set; }

        /// <summary>
        /// How far above the solution folder a backend may look for its workspace marker (Kiro's
        /// <c>.kiro</c>). <c>RepositoryRoot</c> (the default) lets a solution nested inside a repo pick
        /// up steering/agents/MCP that live at the repo root — see issue #54 and
        /// <c>WorkspaceRootLocator</c>, whose ceilings (repository root, never the user profile or a
        /// drive root) bound the search. <c>SolutionOnly</c> pins the agent to the solution folder.
        /// (Engine-consumed, forwarded as <c>CWKT_WORKSPACE_SCOPE</c>; applies when the chat window
        /// next opens, since the session's working directory is captured at start.)
        /// </summary>
        public string AgentWorkspaceScope { get; set; } = nameof(Core.AgentWorkspaceScope.RepositoryRoot);

        /// <summary>
        /// Total megabytes of attached content kept on disk (<c>%LOCALAPPDATA%\code-wicket\attachments</c>),
        /// oldest evicted first once the directory exceeds it. Zero or absent means the default.
        /// <para>
        /// <b>Named for the class of thing, not for images</b>, because that is what it is likely to
        /// grow to cover — pasted files, whatever else a message comes to carry. The limit is the ONLY
        /// thing that reclaims here: there is deliberately no age rule, since this holds conversation
        /// content rather than scratch (see <see cref="AttachmentStore"/>).
        /// </para>
        /// <para>Shell-consumed, read at each sweep, so a change applies the next time a chat window
        /// opens. Clamped up to a floor — a mistyped 0 must not empty the directory.</para>
        /// </summary>
        public int AttachmentStorageLimitMb { get; set; } = AttachmentStore.DefaultLimitMb;

        // --- Debug / advanced overrides (previously only settable via CWKT_* env vars) ---

        /// <summary>Use the disk-backed stub IIdeServices instead of the live VS-backed one.</summary>
        public bool UseStubIde { get; set; }

        // There is deliberately NO EngineExePath here any more. It was the one config.json field that
        // named an executable the SHELL would spawn, and config.json is a file an agent can write under
        // "Allow edits" (pre-release security review, September 2026) - a path in it was code execution as the
        // user on the next window open. The override lives in the CWKT_ENGINE_EXE environment variable
        // alone, which a tool call cannot set on devenv. An old file still carrying the key is ignored
        // by the deserializer, not refused.

        /// <summary>If true, the engine tees raw ACP JSON frames to <see cref="AcpLogFile"/>. (Engine-consumed.)</summary>
        public bool LogAcpFrames { get; set; }

        /// <summary>Debug: if true, tee the raw engine↔shell JSON-RPC channel to <see cref="EngineChannelLogFile"/>. (Shell-consumed.)</summary>
        public bool LogEngineChannel { get; set; }

        /// <summary>
        /// Whether the chat pane records what drawing itself costs, to <c>render.log</c>. Today that is
        /// one <c>[md-cost]</c> line per streamed message plus a <c>[render-env]</c> header (issue #86).
        /// <para>
        /// <b>Named for rendering rather than for markdown, and deliberately so.</b> Markdown streaming
        /// is the first render cost worth measuring here but has no claim to being the last, and a
        /// setting named after one feature is a setting the next investigation has to add a second of —
        /// leaving the user two checkboxes for one question. New render traces join this switch and the
        /// same file, under their own line prefix.
        /// </para>
        /// <para>
        /// <b>Off by default</b>, like the other diagnostic tees. It measures on the UI thread and
        /// appends to a file whose cost belongs to the machine rather than to us (AV/EDR interception,
        /// LocalAppData redirected onto a share, VDI — see <see cref="DiagnosticLog"/>), which is exactly
        /// the environment it exists to investigate: an always-on version would add unbounded UI-thread
        /// IO to the machine already reported as slow. The price is that a field report needs one round
        /// trip before any numbers exist.
        /// </para>
        /// <para>
        /// It does NOT enable <c>CWKT_MARKDOWN_RENDER_LOG</c>'s per-render trace, which appends on every
        /// rebuild and would supply most of the cost the aggregate then reports. Shell-consumed, read
        /// when the chat window opens.
        /// </para>
        /// </summary>
        public bool LogRendering { get; set; }

        /// <summary>
        /// Experimental: <c>VirtualizingPanel.CacheLength</c> on the transcript, in PAGES either side of
        /// the viewport. Null (the default) leaves WPF's own default alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Config-only and deliberately not in Unified Settings</b>, for the same reason as
        /// <c>UseStubIde</c>: it is a knob for one experiment, not something an
        /// install should be offered. It exists so a cache length can be A/B'd against the
        /// <c>[render-pass]</c> trace in the field without a rebuild per value — read when the chat
        /// window opens, so changing it needs a window reopen.
        /// </para>
        /// <para>
        /// <b>MEASURED, AND IT DOES NOT HELP — do not re-propose it without new evidence</b> (issue
        /// #86). The hypothesis was that a cache margin would stop one enormous item being unrealised
        /// and re-rendered. The <c>[render-pass]</c> trace was built to test exactly that and refuted it
        /// across 103 passes: cost is INDEPENDENT of realisation. Passes that realised nothing averaged
        /// 376.0ms against 385.2ms for those that did, and the most expensive pass in the log (2114.6ms)
        /// realised nothing at all. Cache length only changes WHEN things realise, so it has nothing to
        /// act on. Kept as a knob rather than deleted so the negative result has somewhere to live —
        /// this is the third time it has been considered, and the first time it was actually measured.
        /// </para>
        /// </remarks>
        public double? TranscriptCacheLengthPages { get; set; }

        /// <summary>
        /// Experimental: rasterise each transcript row once (<c>CacheMode = BitmapCache</c>) so scrolling
        /// re-composites a bitmap instead of re-rendering the row. Off by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>MEASURED, and it cuts BOTH ways — which is why it stays off</b> (issue #86). Cost is
        /// independent of realisation (see <see cref="TranscriptCacheLengthPages"/>), so it belongs to
        /// what is already on screen being re-rendered as it moves, which is what a bitmap cache
        /// removes. On that population it delivers: mean 355.3ms → 287.8ms and <b>max 2114.6ms →
        /// 806.0ms, a 62% cut</b>. But on a pass that has to RASTERISE a giant row it is 43% worse
        /// (421.1ms → 601.7ms), and each such row costs ~28MB of bitmap — measured at zoom 1.2, which
        /// implies that row renders at roughly 1000x7000 device pixels. Text stayed crisp at the cached
        /// zoom (field-confirmed). <b>Worth revisiting only once row height is bounded</b>; while one row
        /// is that tall it trades the wrong way.
        /// </para>
        /// <para>
        /// <b>Not a free win, which is why it is a flag and not a default.</b> A cached row is a bitmap:
        /// it costs memory, and it goes soft if it is scaled after rasterisation — so the cache is built
        /// at the chat's current zoom (<c>RenderAtScale</c>) and a row cached at one zoom and viewed at
        /// another is exactly the failure to look for. Judge it on the <c>[render-pass]</c> lines AND on
        /// whether the text still looks right.
        /// </para>
        /// </remarks>
        public bool TranscriptBitmapCache { get; set; }

        /// <summary>
        /// Raised after the config is written to disk (by any of the Save helpers below), so an
        /// already-running host can re-read live-applicable settings — e.g. the command allow/deny
        /// lists — instead of only picking them up on the next restart. In-process only.
        /// </summary>
        public static event Action? Changed;

        /// <summary>True when the in-process fake provider should be used instead of a real backend.</summary>
        public bool UseFakeProvider =>
            string.Equals(DefaultProvider, "fake", StringComparison.OrdinalIgnoreCase);

        /// <summary>Config file location: <c>%APPDATA%\code-wicket\config.json</c>.</summary>
        private static string? _pathOverride;

        public static string DefaultPath =>
            _pathOverride ?? Path.Combine(StoragePaths.Roaming, StoragePaths.SettingsFileName);

        /// <summary>
        /// Points every config read and write at <paramref name="path"/> instead of the user's real
        /// <c>%APPDATA%</c> file, for hosts that must not touch it.
        /// <para>
        /// This exists because they DID touch it. The Desktop host's headless self-checks drive real UI
        /// gestures, and some of those persist — the zoom checks alone wrote <c>chatZoom</c> four times
        /// per run — so every verification run silently reset the developer's own chat zoom on the same
        /// machine. It presented as "my zoom keeps disappearing" with no plausible cause, and cost a
        /// wrong diagnosis (a stale VS settings cache) before the harness was suspected.
        /// </para>
        /// <para>
        /// Call before any config access — the chat view reads it in its constructor. Interactive
        /// Desktop runs deliberately do NOT redirect: sharing the real config is what makes the host a
        /// faithful preview of the VSIX. Only the automated modes isolate.
        /// </para>
        /// </summary>
        public static void RedirectTo(string path) => _pathOverride = path;

        /// <summary>Where all diagnostic logs live: <c>%LOCALAPPDATA%\code-wicket\logs</c>.</summary>
        public static string LogDirectory => StoragePaths.LogDirectory;

        /// <summary>Always-on engine stderr log (startup banner + faults).</summary>
        public static string EngineStderrLogFile => Path.Combine(LogDirectory, "engine.log");

        /// <summary>Fixed file the engine tees raw ACP frames to when <see cref="LogAcpFrames"/> is on.</summary>
        public static string AcpLogFile => Path.Combine(LogDirectory, "acp.log");

        /// <summary>Fixed file the shell tees the raw engine↔shell channel to when <see cref="LogEngineChannel"/> is on.</summary>
        public static string EngineChannelLogFile => Path.Combine(LogDirectory, "engine-channel.log");

        /// <summary>Always-on diagnostic log for config read/parse failures (see <see cref="Load"/>).</summary>
        public static string ConfigErrorLogFile => Path.Combine(LogDirectory, "config-error.log");

        // Reads a top-level string[] straight from the raw config JSON — used to migrate a retired key
        // whose property no longer exists to bind to. Best-effort: any parse issue yields an empty array.
        private static string[] ReadLegacyStringArray(string json, string propertyName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var e in arr.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                            list.Add(s);
                    return list.ToArray();
                }
            }
            catch (JsonException) { }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Loads the config from <see cref="DefaultPath"/> (a missing or invalid file falls back to
        /// built-in defaults), then applies the <c>CWKT_PROVIDER</c> env override if present. Never
        /// throws — failing to read config must not stop the tool window from opening.
        /// </summary>
        public static ExtensionConfig Load()
        {
            var config = new ExtensionConfig();
            try
            {
                var path = DefaultPath;
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<ExtensionConfig>(
                        text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (loaded is not null)
                        config = loaded;

                    // Every collection back to non-null before ANYTHING reads one. STJ assigns null
                    // for an explicit JSON null - it does not fall back to the property initializer -
                    // so `"allowedCommands": null` in a hand-edited file made `config.AllowedCommands`
                    // null, and hand-editing is the escape hatch this file's own documentation
                    // recommends for the config-only settings.
                    //
                    // Done here rather than at each use because of where the uses are. The migration
                    // below runs OUTSIDE this try, so its dereference threw out of Load() entirely:
                    // ChatToolWindow calls it from async init with no catch of its own, leaving the pane
                    // on "Starting Code Wicket..." forever - the symptom AGENTS.md attributes to a
                    // missing net472 dependency - and writing no config-error line, because the throw
                    // escaped LogConfigError too. Update() calls Load() as well, so every settings save
                    // failed with it. One null in a text file, and the extension will not start.
                    Normalize(config);

                    // Back-compat: fold a retired hard-deny "deniedCommands" list into the new
                    // AlwaysPromptCommands (which reviews rather than silently blocks). Only when the new
                    // key isn't already present; read straight from the JSON since the property is gone.
                    if (config.AlwaysPromptCommands.Length == 0)
                        config.AlwaysPromptCommands = ReadLegacyStringArray(text, "deniedCommands");
                }
            }
            catch (Exception ex)
            {
                // Unreadable/locked/malformed config → defaults. Better a working window than a crash —
                // but this is a *silent* reset (and the next Save overwrites the file with defaults), so
                // record it (and preserve a corrupt file) rather than letting the user's settings vanish
                // with no trace. That silent reset is the exact "config wasn't loading" symptom seen when
                // switching between versions.
                LogConfigError(ex);
            }

            // Migrate the retired "ReadOnly" permission mode to its ladder equivalent so an older config
            // keeps working (and is rewritten on the next save).
            if (string.Equals(config.DefaultPermissionMode, "ReadOnly", StringComparison.OrdinalIgnoreCase))
                config.DefaultPermissionMode = "AcceptReads";

            MigrateToolRulesOutOfCommands(config);

            // Real env vars still win over the config file (last-resort machine/CI debugging).
            ApplyEnv("CWKT_PROVIDER", v => config.DefaultProvider = v);
            ApplyEnv("CWKT_ACP_LOG", _ => config.LogAcpFrames = true);
            ApplyEnv("CWKT_KIRO_CLI", v => config.KiroCliPath = v);
            ApplyEnv("CWKT_KIRO_AGENT_ENGINE", v => config.KiroAgentEngine = v);
            ApplyEnv("CWKT_CLAUDE_ACP", v => config.ClaudeAcpPath = v);
            ApplyEnv("CWKT_ENGINE_LOG", _ => config.LogEngineChannel = true);
            if (string.Equals(Environment.GetEnvironmentVariable("CWKT_IDE"), "stub", StringComparison.OrdinalIgnoreCase))
                config.UseStubIde = true;

            return config;
        }

        /// <summary>
        /// The <see cref="CustomAcpAgents"/> array serialized for the <c>CWKT_CUSTOM_AGENTS</c> env
        /// var on the engine process; null when there are none (so hosts skip the variable).
        /// </summary>
        public string? CustomAcpAgentsJson()
        {
            if (CustomAcpAgents.Length == 0)
                return null;
            try
            {
                return JsonSerializer.Serialize(
                    CustomAcpAgents, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch
            {
                return null; // Best effort — a bad entry must not stop the engine launching.
            }
        }

        /// <summary>
        /// The <see cref="DiscoveredModels"/> cache serialized for the <c>CWKT_MODEL_CACHE</c> env var
        /// on the engine process; null when empty (so hosts skip the variable).
        /// </summary>
        public string? DiscoveredModelsJson()
        {
            if (DiscoveredModels.Count == 0)
                return null;
            try
            {
                return JsonSerializer.Serialize(
                    DiscoveredModels, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch
            {
                return null; // Best effort — a bad entry must not stop the engine launching.
            }
        }

        /// <summary>Persists a provider's session-discovered model list to <see cref="DiscoveredModels"/>
        /// so the next launch seeds the picker with it. No-op on empty input; never throws.</summary>
        public static void SaveDiscoveredModels(string providerId, IReadOnlyList<CachedModel> models) => Update(config =>
        {
            if (string.IsNullOrWhiteSpace(providerId) || models is null || models.Count == 0)
                return;

            var array = new CachedModel[models.Count];
            for (var i = 0; i < models.Count; i++)
                array[i] = models[i];
            config.DiscoveredModels[providerId] = array;
        });

        private static void ApplyEnv(string name, Action<string> set)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                set(value!);
        }

        // Records a config *read* failure so a silent reset-to-defaults isn't invisible. A genuine parse
        // failure (corrupt JSON) also preserves the offending file as config.json.bad, since the next
        // Save would otherwise overwrite the user's settings with defaults.
        private static void LogConfigError(Exception ex)
        {
            var path = DefaultPath;

            // Only a JsonException means the file's *content* is corrupt; a lock/IO error is transient
            // and the file is likely fine, so don't clobber the last-known-bad copy with a good one.
            var preserved = false;
            if (ex is JsonException)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        Directory.CreateDirectory(LogDirectory);
                        File.Copy(path, path + ".bad", overwrite: true);
                        preserved = true;
                    }
                }
                catch { /* preservation is best-effort */ }
            }

            AppendConfigLog($"Failed to load {path}: {ex.GetType().Name}: {ex.Message}" +
                " (fell back to defaults" + (preserved ? "; original preserved as config.json.bad" : "") + ")");
        }

        // Appends one timestamped line to the config-error log. Best-effort: a logging/IO failure must
        // never throw out of Load()/Update() (both contract "never throws"). Retention (size cap +
        // roll) is DiagnosticLog's; this log deliberately isn't rolled per run — it only gets written
        // when something is actually wrong, and the whole history of that is worth keeping.
        // No timestamp here: DiagnosticLog.AppendLine stamps every line it writes.
        private static void AppendConfigLog(string message)
            => DiagnosticLog.AppendLine(ConfigErrorLogFile, message);

        /// <summary>
        /// Persists the chosen provider/model to <see cref="DefaultPath"/> so the picker selection
        /// survives restarts. Never throws — a failed write must not disrupt the chat session.
        /// </summary>
        public static void SaveSelection(string? providerId, string? modelId) => Update(config =>
        {
            if (!string.IsNullOrWhiteSpace(providerId))
                config.DefaultProvider = providerId!;
            config.DefaultModel = modelId;
        });

        /// <summary>Appends a command glob to the persisted allow-list (no-op if already present).</summary>
        public static void AddAllowedCommand(string glob) => Update(config =>
        {
            if (string.IsNullOrWhiteSpace(glob) ||
                Array.Exists(config.AllowedCommands, c => string.Equals(c, glob, StringComparison.OrdinalIgnoreCase)))
                return;

            var updated = new string[config.AllowedCommands.Length + 1];
            Array.Copy(config.AllowedCommands, updated, config.AllowedCommands.Length);
            updated[config.AllowedCommands.Length] = glob;
            config.AllowedCommands = updated;
        });

        /// <summary>Appends a path glob to the persisted edit allow-list (no-op if already present).</summary>
        public static void AddAllowedPath(string glob) => Update(config =>
        {
            if (string.IsNullOrWhiteSpace(glob) ||
                Array.Exists(config.AllowedPaths, c => string.Equals(c, glob, StringComparison.OrdinalIgnoreCase)))
                return;

            var updated = new string[config.AllowedPaths.Length + 1];
            Array.Copy(config.AllowedPaths, updated, config.AllowedPaths.Length);
            updated[config.AllowedPaths.Length] = glob;
            config.AllowedPaths = updated;
        });

        /// <summary>
        /// Appends an IDE tool to the persisted tool allow-list (no-op if already present, or if the
        /// name isn't one of ours). The name is stored UNWRAPPED — the caller may pass either backend's
        /// namespaced form, and a rule saved on Kiro has to work on Claude.
        /// </summary>
        public static void AddAllowedTool(string toolName) => Update(config =>
        {
            if (!IdeMcpServer.TryNormalizeToolRule(toolName, out var local) ||
                Array.Exists(config.AllowedTools, t => string.Equals(t, local, StringComparison.OrdinalIgnoreCase)))
                return;

            var updated = new string[config.AllowedTools.Length + 1];
            Array.Copy(config.AllowedTools, updated, config.AllowedTools.Length);
            updated[config.AllowedTools.Length] = local;
            config.AllowedTools = updated;
        });

        /// <summary>
        /// Moves MCP tool names that landed in the COMMAND allow-list over to the tool allow-list
        /// (issue #129). They got there because Kiro's default engine titles a tool call
        /// <c>"Running: @server/tool"</c>, the same convention it uses for shell commands, so the tool
        /// name resolved as the request's command and the banner offered to persist it as a command
        /// glob. The mapper no longer does that — which would leave those rules matching nothing on
        /// every backend, so a grant the user made and can still see in their settings would go quietly
        /// inert. Idempotent, and run on every load rather than gated on a version marker.
        /// <para>EVERY namespaced name moves, not just ours: both backends' spellings are a recognisable
        /// shape that no shell command shares, and a foreign rule has a safe home now that it keeps its
        /// server. Leaving those behind would rest on v1 tool rules
        /// being scoped to our own catalog — but that scoping only ever existed to stop BARE names
        /// colliding across servers, and a canonical <c>server/tool</c> rule cannot collide. A user who
        /// ticked "save permanently" for a tool would otherwise be left with a dead entry and no way to
        /// tell, which is the very failure this migration exists to prevent.</para>
        /// </summary>
        private static void MigrateToolRulesOutOfCommands(ExtensionConfig config)
        {
            if (config.AllowedCommands.Length == 0)
                return;

            var commands = new List<string>(config.AllowedCommands.Length);
            var tools = new List<string>(config.AllowedTools);

            foreach (var rule in config.AllowedCommands)
            {
                // Only a namespaced name moves. A bare name in the command list is a shell command
                // ("dotnet build") and stays one — there is nothing about it that says otherwise.
                if (IdeMcpServer.TryResolveLocalTool(rule, out _) ||
                    IdeMcpServer.TryParseNamespacedTool(rule, out _, out _))
                {
                    if (IdeMcpServer.TryNormalizeToolRule(rule, out var canonical) &&
                        !tools.Exists(t => string.Equals(t, canonical, StringComparison.OrdinalIgnoreCase)))
                        tools.Add(canonical);
                    continue;
                }

                commands.Add(rule);
            }

            // BOTH lists, not just the tools one. A rule that moves out of AllowedCommands does not
            // always land in AllowedTools - the canonical form may already be there, because the user
            // granted the tool again after a partial run, or because two command entries normalise to
            // the same rule. Gated on the tools list alone, those cases rewrote nothing at all and the
            // dead `@code-wicket/build_solution` entry stayed in the user's settings for good: the
            // "dead entry and no way to tell" outcome this migration exists to prevent, reached by the
            // migration itself.
            if (commands.Count != config.AllowedCommands.Length || tools.Count != config.AllowedTools.Length)
            {
                config.AllowedCommands = commands.ToArray();
                config.AllowedTools = tools.ToArray();
            }
        }

        /// <summary>
        /// Replaces any collection the deserializer set to null with an empty one.
        /// </summary>
        /// <remarks>
        /// A property initializer is not a defence: it runs before deserialization, and STJ then
        /// OVERWRITES it with null when the JSON says null. Only a key that is absent keeps the
        /// initializer's value - which is why this is invisible until someone writes an explicit null,
        /// and why every one of these properties looks safe at its declaration.
        /// <para>
        /// Applied to all of them rather than the two that were reachable. The reachable set is a
        /// property of today's call graph, and the next reader of a config collection should not have to
        /// know which ones were audited.
        /// </para>
        /// </remarks>
        private static void Normalize(ExtensionConfig config)
        {
            config.AllowedCommands ??= Array.Empty<string>();
            config.AlwaysPromptCommands ??= Array.Empty<string>();
            config.AllowedPaths ??= Array.Empty<string>();
            config.AllowedTools ??= Array.Empty<string>();
            config.CustomAcpAgents ??= Array.Empty<CustomAcpAgent>();
            config.EngineEnvironment ??= new Dictionary<string, string>();
            config.DiscoveredModels ??= new Dictionary<string, CachedModel[]>();
        }

        /// <summary>Persists the transcript zoom factor to <see cref="DefaultPath"/>.</summary>
        public static void SaveChatZoom(double zoom) => Update(config => config.ChatZoom = zoom);

        /// <summary>Persists the dragged height of the chat message box to <see cref="DefaultPath"/>.</summary>
        public static void SaveChatInputHeight(double height) => Update(config => config.ChatInputHeight = height);

        /// <summary>Persists the chosen permission mode to <see cref="DefaultPath"/>.</summary>
        public static void SavePermissionMode(string mode) => Update(config =>
        {
            if (!string.IsNullOrWhiteSpace(mode))
                config.DefaultPermissionMode = mode;
        });

        /// <summary>
        /// Loads the current config, applies a mutation, and writes it back. Never throws — a locked
        /// or unwritable config file must not disrupt the session. Used by the VS Options page.
        /// </summary>
        public static void Save(Action<ExtensionConfig> mutate) => Update(mutate);

        // Loads the current config, applies a mutation, and writes it back.
        private static void Update(Action<ExtensionConfig> mutate)
        {
            try
            {
                var config = Load();
                mutate(config);

                var path = DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(
                    config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // Best effort: a locked/unwritable config must not break the session — but log it, since
                // a swallowed write means the user's change silently didn't persist.
                AppendConfigLog($"Failed to save {DefaultPath}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // Notify after a successful write so live consumers re-read. A handler fault must not
            // surface as a failed save, so swallow it here.
            try { Changed?.Invoke(); }
            catch { /* a subscriber's reload failing must not break saving */ }
        }
    }
}
