using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Launch + identity configuration for an ACP agent backend — the data an
    /// <see cref="AcpAgentProvider"/> needs to spawn and describe a CLI that speaks ACP over stdio.
    /// Adding a new ACP agent (Claude Code, Gemini CLI, …) is normally just a new value of this record;
    /// only behavioural quirks (e.g. agent-specific launch flags) warrant a derived provider.
    /// </summary>
    public sealed record AcpAgentConfig
    {
        /// <summary>Stable provider id (e.g. "kiro").</summary>
        public string ProviderId { get; init; } = "acp";

        /// <summary>Human-readable name for pickers.</summary>
        public string DisplayName { get; init; } = "ACP agent";

        /// <summary>The agent executable (resolved via PATH if not rooted), e.g. "kiro-cli".</summary>
        public string CliPath { get; init; } = string.Empty;

        /// <summary>Launch arguments that put the CLI into ACP mode, e.g. <c>["acp"]</c>.</summary>
        public IReadOnlyList<string> LaunchArgs { get; init; } = Array.Empty<string>();

        /// <summary>Extra environment variables for the agent process (e.g. an API key). Optional.</summary>
        public IReadOnlyDictionary<string, string>? Environment { get; init; }

        /// <summary>
        /// Arguments that make this CLI print its version, e.g. <c>["--version"]</c>. Declared only for
        /// backends that do NOT report themselves in the <c>initialize</c> response's <c>agentInfo</c>
        /// — that answer is free and authoritative, so an agent supplying it (Claude Code) leaves this
        /// empty and no extra process is ever spawned. kiro-cli sends no <c>agentInfo</c>, which is why
        /// the fallback exists at all.
        ///
        /// <para>Empty (the default) means "don't ask". See <see cref="AgentVersionProbe"/> — the probe
        /// is fire-and-forget, so declaring this never slows a session start.</para>
        /// </summary>
        public IReadOnlyList<string> VersionArgs { get; init; } = Array.Empty<string>();

        /// <summary>
        /// How this backend's CLI is told to resume a conversation, with <c>{id}</c> where the
        /// conversation id goes - e.g. <c>claude --resume {id}</c>. Surfaced so a conversation started
        /// here can be carried on in a terminal (issue #108); the data already lives in the CLI's own
        /// store, so this is the whole of what that direction needs.
        /// <para><b>Null means the affordance is hidden, and that is the right default.</b> A command
        /// we have not verified is worse than no command: the user pastes it, the CLI rejects it or
        /// silently opens the wrong thing, and the failure looks like ours. Declare it only for a
        /// backend whose flag has actually been run.</para>
        /// <para>The command only - the host adds the <c>cd</c> line, because WHERE a resume must run
        /// from is a fact about the host's workspace rather than about the backend's syntax.</para>
        /// </summary>
        public string? ResumeCommandTemplate { get; init; }

        /// <summary>Models this agent can drive.</summary>
        public IReadOnlyList<ModelInfo> Models { get; init; } = Array.Empty<ModelInfo>();

        /// <summary>Optional features the backend supports.</summary>
        public AgentCapabilities Capabilities { get; init; } = AgentCapabilities.None;

        /// <summary>
        /// Directories that mark this backend's workspace root (Kiro's <c>.kiro</c>), so a solution
        /// nested below one still gets the backend's steering/agents/MCP — see
        /// <see cref="WorkspaceRootLocator"/> and issue #54. Empty (the default) keeps the agent's
        /// working directory pinned to the solution folder, which is right for backends that already
        /// walk up themselves: Claude Code finds <c>CLAUDE.md</c>/<c>.claude</c> on its own, so moving
        /// its cwd would change behaviour it currently gets right.
        /// </summary>
        public IReadOnlyList<WorkspaceMarker> WorkspaceMarkers { get; init; } = Array.Empty<WorkspaceMarker>();

        /// <summary>
        /// Optional host-side auth callback backing Kiro's <c>_kiro/auth/getAccessToken</c> client
        /// extension method: the v3 agent engine (<c>kiro-cli acp --agent-engine v3</c>) delegates
        /// token refresh to its ACP host instead of reading the CLI's own credential store, and fails
        /// every model call with <c>TokenExpiredError</c> when the host doesn't serve it. Returns the
        /// raw response payload (<c>{accessToken, expiresAt, profileArn?, authMethod?, provider?}</c>).
        /// Null (the default) leaves the method unhandled, which is fine for agents that never call it
        /// (Kiro's v2 engine, Claude Code).
        /// </summary>
        public Func<CancellationToken, Task<JsonElement>>? AuthTokenProvider { get; init; }

        /// <summary>
        /// Optional gate run immediately before the CLI is launched: returns a user-facing reason to
        /// refuse, or null to proceed. It exists for the case where launching would be actively worse
        /// than refusing — kiro-cli, given no credentials, starts an interactive browser login inside
        /// the headless process we spawn, so the page opens while whatever it needs from the user goes
        /// into a redirected pipe and the process is killed on a timeout. A refusal that names the fix
        /// ("run kiro-cli login") beats a browser tab that can never complete. Null (the default) means
        /// no gate, which is right for any agent that fails cleanly on its own.
        /// </summary>
        public Func<string?>? PreflightCheck { get; init; }

        /// <summary>
        /// When true, <see cref="Capabilities"/> is a best guess (defaulted or previously probed,
        /// not hand-declared) and the provider refreshes it from the agent's <c>initialize</c>
        /// response on each session start (currently <c>loadSession</c> → ResumeSession), catching
        /// agent updates. False (built-ins, user-declared) = the declaration is authoritative.
        /// </summary>
        public bool DiscoverCapabilities { get; init; }

        /// <summary>
        /// How the host's permission picker maps onto this agent's own permission modes (issue #269).
        /// When set, the session asserts the mapped mode via <c>session/set_config_option</c> (config
        /// category <c>mode</c>) after EVERY open — new, load, and the load-refused fallback — so the
        /// backend's mode is a function of the picker and never of a settings file in the working
        /// tree. A pin that does not land is SURFACED, not fatal: the session continues and says so.
        /// Null (the default) pins nothing. See <see cref="BackendPermissionModes"/> for why this is
        /// per-agent data rather than a protocol constant.
        /// </summary>
        public BackendPermissionModes? BackendModes { get; init; }

        /// <summary>
        /// Raw <c>_meta</c> sent with every <c>session/new</c> and <c>session/load</c>: the ACP
        /// extension bag a backend reads vendor-specific session setup from. Null (the default) sends
        /// none. Data rather than a hook, so a custom-agent config can carry one too.
        /// <para>What it is for today: Kiro's v3 engine accepts <c>_meta.kiro.customAgents</c>, agent
        /// definitions the CLIENT supplies — permission rules included — registered with origin
        /// "client" and overriding a same-named agent from any file, the workspace's included. That is
        /// the one Kiro lever a repository cannot shadow (issue #269).</para>
        /// </summary>
        public JsonElement? SessionMeta { get; init; }

        /// <summary>
        /// What this agent's ACP session "mode" is, as the session panel labels it — "Permission mode"
        /// on Claude, "Agent" on Kiro. Per-agent data for the same reason <see cref="BackendModes"/> is:
        /// ACP standardises the shape of a mode and not its meaning. Defaults to "Mode", which is the
        /// honest label for an agent nobody has described.
        /// </summary>
        public string ModeLabel { get; init; } = "Mode";
    }
}
