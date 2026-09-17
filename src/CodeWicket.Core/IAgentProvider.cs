using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core.Ide;

namespace CodeWicket.Core
{
    /// <summary>
    /// A pluggable agent backend (e.g. Kiro via <c>kiro-cli acp</c>). A provider advertises the
    /// models it can drive and starts <see cref="IAgentSession"/>s. The provider owns the agent
    /// loop; the host supplies context, edit application, and tools via <see cref="IIdeServices"/>.
    /// </summary>
    public interface IAgentProvider
    {
        /// <summary>Stable, unique identifier (e.g. "kiro").</summary>
        string ProviderId { get; }

        /// <summary>Human-readable name for pickers (e.g. "Kiro").</summary>
        string DisplayName { get; }

        /// <summary>Models this provider can drive.</summary>
        IReadOnlyList<ModelInfo> Models { get; }

        /// <summary>Optional features this provider supports.</summary>
        AgentCapabilities Capabilities { get; }

        /// <summary>Starts a new (or resumed) conversation, wiring it to the host's IDE services.</summary>
        Task<IAgentSession> StartSessionAsync(SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implemented by providers whose backend has a CLI that can resume a conversation by id, so one
    /// started in the IDE can be carried on in a terminal (issue #108).
    /// <para>A side interface rather than a member of <see cref="IAgentProvider"/> for the same reason
    /// as the others: an in-process backend has no command line to offer, and a provider that cannot
    /// answer should not have to return null to say so.</para>
    /// </summary>
    public interface IResumeCommandTemplate
    {
        /// <summary>
        /// The CLI invocation that resumes a conversation, with <c>{id}</c> where its id goes. Null
        /// when this backend's flag has not been verified - an unverified command is worse than none,
        /// because its failure looks like the IDE's.
        /// </summary>
        string? ResumeCommandTemplate { get; }
    }

    /// <summary>
    /// Implemented by providers that can enumerate the conversations the backend's own CLI has stored
    /// for a workspace, before and without any session of ours (issue #108). The engine offers this to
    /// the host so a conversation begun in the terminal can be picked up in the IDE.
    /// <para>Optional in the <see cref="IModelDiscovery"/> style rather than a member of
    /// <see cref="IAgentProvider"/>: a provider with no CLI store behind it - the fake, an in-process
    /// backend - has no honest answer to give, and "returns empty" is a worse contract than "does not
    /// implement this".</para>
    /// </summary>
    public interface IBackendSessionCatalog
    {
        /// <summary>
        /// Lists what the backend has stored for <paramref name="workspaceRootPath"/>. Never throws for
        /// an ordinary refusal - a backend that cannot list, a CLI that is not installed, or one the
        /// user is signed out of all come back as <see cref="BackendSessionListResult.Supported"/> false
        /// with a reason, because every one of those is a sentence to show under an empty list rather
        /// than a failure to break a picker over.
        /// </summary>
        Task<BackendSessionListResult> ListBackendSessionsAsync(
            string workspaceRootPath, AgentWorkspaceScope scope, IIdeServices ide,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// The working directory a listing for <paramref name="workspaceRootPath"/> would run in,
        /// resolved without spawning anything so a caller can compare it against a cwd it already holds.
        /// <para><b>It exists because a session's cwd is captured at start and the workspace can move
        /// under it.</b> The engine answers a listing down an idle live session's connection when it can,
        /// and that session is rooted wherever it happened to open — a warm start that ran before the
        /// solution finished loading is rooted at the default workspace and stays that way for as long
        /// as it lives. <c>session/list</c> is scoped by cwd, so asking that session reads a different
        /// store, and for a root nobody has ever worked in the answer is an EMPTY list — indistinguishable
        /// from the user having no stored conversations. Measured 2026-08-28: two hours forty minutes of
        /// a picker reporting nothing, cured only by restarting the window.</para>
        /// <para>The same resolution the listing itself performs, so the check and the thing it guards
        /// can never disagree about which directory is the right one.</para>
        /// </summary>
        WorkspaceRootResult ResolveListingRoot(string workspaceRootPath, AgentWorkspaceScope scope);
    }

    /// <summary>
    /// The answer to a backend session listing: what was found, or why nothing was.
    /// <para><paramref name="Supported"/> false with an empty list and <paramref name="Supported"/> true
    /// with an empty list are DIFFERENT answers - "this backend cannot tell you" against "this backend
    /// has nothing here" - and the host says something different for each. Collapsing them is the same
    /// mistake as reading a null usage figure as zero.</para>
    /// </summary>
    public sealed record BackendSessionListResult(
        bool Supported, string? Reason, IReadOnlyList<BackendSessionInfo> Sessions)
    {
        public static BackendSessionListResult Unavailable(string reason) =>
            new(false, reason, Array.Empty<BackendSessionInfo>());

        public static BackendSessionListResult Found(IReadOnlyList<BackendSessionInfo> sessions) =>
            new(true, null, sessions);
    }

    /// <summary>Describes a single model a provider can drive.</summary>
    public sealed record ModelInfo(string Id, string DisplayName)
    {
        public int? MaxInputTokens { get; init; }
        public int? MaxOutputTokens { get; init; }

        /// <summary>Relative credit/token cost the backend charges for this model (1.0 = baseline). Null if unknown.</summary>
        public double? RateMultiplier { get; init; }
    }

    /// <summary>Optional backend features, used to gate UI and host behaviour.</summary>
    [Flags]
    public enum AgentCapabilities
    {
        None = 0,
        ToolCalls = 1 << 0,
        ClientFileSystem = 1 << 1,
        Mcp = 1 << 2,
        Thinking = 1 << 3,
        Cancellation = 1 << 4,
        ResumeSession = 1 << 5,

        /// <summary>The backend can switch models on a live session (ACP <c>session/set_model</c>),
        /// so a model change need not start a fresh conversation.</summary>
        ModelSelection = 1 << 6,

        /// <summary>
        /// The backend accepts a follow-up message <em>into the turn that is already running</em>
        /// rather than requiring it to wait for the turn to end — ACP's <c>_session/steering</c>
        /// extension request. This is what lets the user course-correct mid-turn without cancelling:
        /// no work is torn down and no context is re-established, which is the whole reason it beats
        /// stop-and-resend. Discovered per-session from <c>initialize</c>, never inferred from the
        /// agent's version (the Claude adapter gained it between 0.55.0 and 0.63.0, and both are in
        /// the wild).
        /// </summary>
        Steering = 1 << 7,

        /// <summary>
        /// The backend accepts <c>image</c> content blocks in a prompt (ACP
        /// <c>agentCapabilities.promptCapabilities.image</c>), so a pasted screenshot can be sent as
        /// image data rather than as a path the agent has to go and read. Discovered per-session from
        /// <c>initialize</c> like <see cref="Steering"/> and for the same reason — it describes the
        /// program on the other end of the pipe, which a version number does not.
        /// </summary>
        ImagePrompts = 1 << 8,

        /// <summary>
        /// The backend can enumerate the conversations its own CLI has stored for a working directory
        /// (ACP <c>session/list</c>, advertised as <c>agentCapabilities.sessionCapabilities.list</c>),
        /// so a session started in the terminal can be picked up here (issue #108). Discovered from the
        /// handshake like <see cref="Steering"/> and <see cref="ImagePrompts"/>, and for the same
        /// reason: it describes the copy on the other end of the pipe, which no version number does.
        /// </summary>
        SessionList = 1 << 9,
    }

    /// <summary>Per-session configuration.</summary>
    public sealed record SessionOptions
    {
        /// <summary>Which model to use; null lets the provider pick its default.</summary>
        public string? ModelId { get; init; }

        /// <summary>Absolute path to the workspace/solution root the agent operates on. This stays the
        /// IDE's notion of the workspace (the open solution/folder) even when the agent's own working
        /// directory is resolved higher — see <see cref="WorkspaceRootLocator"/>.</summary>
        public string WorkspaceRootPath { get; init; } = string.Empty;

        /// <summary>How far above <see cref="WorkspaceRootPath"/> the provider may look for the
        /// backend's workspace marker. Defaults to the repository root; the user setting can pin it to
        /// the solution folder.</summary>
        public AgentWorkspaceScope WorkspaceScope { get; init; } = AgentWorkspaceScope.RepositoryRoot;

        /// <summary>How tool/edit permissions are handled.</summary>
        public PermissionMode PermissionMode { get; init; } = PermissionMode.Prompt;

        /// <summary>If set, resume an existing conversation instead of starting fresh.</summary>
        public string? ResumeConversationId { get; init; }

        /// <summary>
        /// Run the agent in THIS directory, searching for nothing (issue #185): the user chose to keep
        /// a conversation where its history lives rather than where the workspace marker or a project
        /// settings file would put it. Meaningful only beside <see cref="ResumeConversationId"/>, and
        /// already validated by the host that set it — a directory that exists and is not one the
        /// locator refuses as a workspace.
        /// </summary>
        public string? PinnedWorkingDirectory { get; init; }

        /// <summary>
        /// Capture the conversation the backend replays while it loads
        /// <see cref="ResumeConversationId"/>, instead of dropping it (issue #108). Off by default,
        /// because an ordinary resume already has the transcript — rebuilt from our own append-only
        /// log before the load is even made — and ingesting the backend's copy on top of it appends
        /// the whole conversation a second time.
        /// <para>What it is for is the case where there is no log of ours: a conversation started in
        /// the backend's terminal CLI, where the replay is the ONLY account of what was said. So this
        /// is a property of where the conversation came from rather than of the backend, which is why
        /// it is an option on the session and not a capability.</para>
        /// </summary>
        public bool ImportHistory { get; init; }

        /// <summary>
        /// MCP servers the host wants exposed to the agent for this session (e.g. the IDE tool
        /// catalog). Providers that support MCP (see <see cref="AgentCapabilities.Mcp"/>) advertise
        /// these to the backend; others ignore them. The host owns the server processes.
        /// </summary>
        public IReadOnlyList<McpServerSpec> McpServers { get; init; } = Array.Empty<McpServerSpec>();

        /// <summary>
        /// Optional sink for status events raised while no prompt turn is streaming (e.g.
        /// <see cref="AgentEvent.McpServerConnected"/> — the backend's MCP servers connect
        /// asynchronously right after the session opens, before the first prompt). Turn-scoped
        /// events always flow through <see cref="IAgentSession.SendAsync"/>; without this sink,
        /// between-turn status is dropped. May be invoked from any thread.
        /// </summary>
        public Action<AgentEvent>? OutOfTurnEvents { get; init; }
    }

    /// <summary>
    /// A host-provided MCP server the agent should connect to, over stdio. The host is responsible
    /// for the launched process actually serving MCP (e.g. relaying to an in-process tool catalog).
    /// Provider-agnostic; the Kiro/ACP provider maps this onto the ACP <c>mcpServers</c> shape.
    /// </summary>
    public sealed record McpServerSpec(string Name, string Command, IReadOnlyList<string> Args)
    {
        /// <summary>Extra environment variables for the spawned server process. Optional.</summary>
        public IReadOnlyDictionary<string, string>? Env { get; init; }
    }

    /// <summary>
    /// How the host responds to permission requests — a cumulative risk ladder: each rung auto-approves
    /// everything the rung below it does, plus one more risk tier. Anything above the active rung's
    /// ceiling still prompts. (Replaces the old standalone <c>ReadOnly</c> mode; a persisted "ReadOnly"
    /// migrates to <see cref="AcceptReads"/> via <see cref="PermissionModeParser.Parse"/>.)
    /// </summary>
    public enum PermissionMode
    {
        /// <summary>Ask the user for every action.</summary>
        Prompt,

        /// <summary>Auto-approve read-only actions (reads, queries, builds); prompt for edits and commands.</summary>
        AcceptReads,

        /// <summary>Auto-approve reads and file edits; prompt for commands and anything unrecognised.</summary>
        AcceptEdits,

        /// <summary>Auto-approve all actions.</summary>
        AcceptAll,
    }

    /// <summary>Parses a <see cref="PermissionMode"/> name tolerantly: the retired <c>"ReadOnly"</c> maps
    /// to <see cref="PermissionMode.AcceptReads"/> and any unknown string to <see cref="PermissionMode.Prompt"/>.</summary>
    public static class PermissionModeParser
    {
        public static PermissionMode Parse(string? value)
        {
            if (string.Equals(value, "ReadOnly", StringComparison.OrdinalIgnoreCase))
                return PermissionMode.AcceptReads;
            return Enum.TryParse<PermissionMode>(value, ignoreCase: true, out var mode) ? mode : PermissionMode.Prompt;
        }
    }

    /// <summary>A user prompt plus optional pieces of editor context to attach.</summary>
    public sealed record PromptInput(string Text)
    {
        public IReadOnlyList<ContextRef> Context { get; init; } = Array.Empty<ContextRef>();

        /// <summary>
        /// Binary content sent alongside the text — today only images, and only ones that have no path
        /// to reference (issue #118). Files on disk are deliberately NOT attached: the agent can read
        /// those itself, and reading gets it the current bytes rather than a snapshot that goes stale
        /// the moment it edits them.
        /// </summary>
        public IReadOnlyList<PromptAttachment> Attachments { get; init; } = Array.Empty<PromptAttachment>();
    }

    /// <summary>
    /// One piece of binary content riding with a prompt, already base64-encoded because that is the
    /// form every consumer wants: ACP's <c>image</c> content block carries <c>data</c> as base64, and
    /// keeping bytes here would mean encoding on each hop across the engine wire.
    /// </summary>
    /// <param name="MimeType">e.g. "image/png". Sent verbatim as the block's <c>mimeType</c>.</param>
    /// <param name="Data">Base64 of the raw file bytes — no <c>data:</c> URI prefix.</param>
    public sealed record PromptAttachment(string MimeType, string Data);

    /// <summary>A lightweight reference to attach as context, e.g. ("file", "C:\foo.cs") or ("selection", "...").</summary>
    public sealed record ContextRef(string Kind, string Value);
}
