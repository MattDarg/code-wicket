using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeWicket.Providers.Acp
{
    // Data-transfer objects for the subset of Agent Client Protocol (v1) we use.
    // Property names serialize as camelCase (configured on the formatter). Shapes whose
    // contents we don't fully model are kept as JsonElement so they round-trip safely.

    // --- initialize ---
    internal sealed record InitializeParams(int ProtocolVersion, ClientCapabilities ClientCapabilities, Implementation? ClientInfo);

    internal sealed record ClientCapabilities(FsCapabilities Fs, bool Terminal);

    internal sealed record FsCapabilities(bool ReadTextFile, bool WriteTextFile);

    internal sealed record Implementation(string Name, string? Version);

    internal sealed record InitializeResult
    {
        public int ProtocolVersion { get; init; }
        public JsonElement? AgentCapabilities { get; init; }
        public Implementation? AgentInfo { get; init; }
        public JsonElement? AuthMethods { get; init; }

        /// <summary>
        /// Top-level extension metadata — a SIBLING of <see cref="AgentCapabilities"/>, not a member of
        /// it. Carries <c>steering.supported</c>, which is how an agent declares it accepts
        /// <c>_session/steering</c>. Needs the explicit name: the formatter camelCases, so the
        /// underscore would otherwise be lost and the property would silently never bind.
        /// </summary>
        [JsonPropertyName("_meta")]
        public JsonElement? Meta { get; init; }
    }

    /// <summary>
    /// Params for the <c>_session/steering</c> extension request — deliberately just the session and
    /// the user's text. A steer rides inside a turn whose prompt already carried the workspace-context
    /// block, so re-sending it would pay for the same snapshot twice in one turn.
    /// </summary>
    internal sealed record SteerParams(string SessionId, ContentBlock[] Prompt);

    // --- session/new and session/load ---
    // `_meta` is the ACP extension bag; a backend may read vendor-specific session setup from it (Kiro's
    // v3 engine parses `_meta.kiro` on session/new AND session/load — client-supplied agent definitions
    // among it). Null is omitted from the wire by the formatter's WhenWritingNull.
    internal sealed record NewSessionParams(
        string Cwd, McpServer[] McpServers, string[]? AdditionalDirectories,
        [property: JsonPropertyName("_meta")] JsonElement? Meta = null);

    internal sealed record LoadSessionParams(
        string SessionId, string Cwd, McpServer[] McpServers, string[]? AdditionalDirectories,
        [property: JsonPropertyName("_meta")] JsonElement? Meta = null);

    /// <summary>
    /// Params for <c>session/list</c> (issue #108). Both fields are optional in the schema and both are
    /// null-suppressed by the formatter, so an omitted cwd asks the backend for everything it has rather
    /// than for the sessions of a directory literally named "null".
    /// <para><c>Cwd</c> is what scopes the answer to this workspace, and it must be the directory the
    /// AGENT runs in rather than the solution folder - Claude keys its store by a hash of exactly that
    /// path, so the wrong one returns an empty list that is indistinguishable from having no sessions.</para>
    /// </summary>
    internal sealed record ListSessionsParams(string? Cwd, string? Cursor);

    /// <summary>
    /// A stdio MCP server the agent should connect to. Matches the ACP <c>mcpServers</c> stdio shape
    /// (<c>name</c>/<c>command</c>/<c>args</c>/<c>env</c>); the agent spawns <c>command</c> and speaks
    /// MCP over its stdio. <c>env</c> is required by the spec (may be empty).
    /// </summary>
    internal sealed record McpServer(string Name, string Command, string[] Args, EnvVariable[] Env);

    /// <summary>A name/value environment variable for a spawned MCP server (ACP <c>EnvVariable</c>).</summary>
    internal sealed record EnvVariable(string Name, string Value);

    internal sealed record NewSessionResult
    {
        public string SessionId { get; init; } = string.Empty;
        public JsonElement? ConfigOptions { get; init; }
        public JsonElement? Modes { get; init; }
    }

    // --- session/prompt ---
    internal sealed record PromptParams(string SessionId, ContentBlock[] Prompt);

    internal sealed record PromptResult
    {
        public string StopReason { get; init; } = string.Empty;

        /// <summary>
        /// Per-turn token accounting, when the backend attaches it (Claude Code does; Kiro doesn't).
        /// Left as a raw <see cref="JsonElement"/> — decoding backend shapes is <c>AcpMapper</c>'s job,
        /// and an absent property lands here as <see cref="JsonValueKind.Undefined"/> rather than
        /// failing the response that carries the turn's stop reason.
        /// </summary>
        public JsonElement Usage { get; init; }
    }

    /// <summary>
    /// A content block. We emit the <c>text</c> variant and, for attachments, the <c>image</c> one
    /// (issue #118): <c>{type:"image", data:&lt;base64&gt;, mimeType}</c>. The image shape carries no
    /// <c>text</c> and the text shape carries neither of the other two — the formatter's
    /// <c>WhenWritingNull</c> keeps each block to the fields its type actually defines, which is why
    /// one record can serve both without a text block's bytes changing at all.
    /// <para>
    /// Data-only is enough, and that is measured rather than assumed: the Claude adapter's own
    /// <c>dist/acp-agent.js</c> maps <c>chunk.data</c> straight onto an Anthropic
    /// <c>{type:"image", source:{type:"base64", data, media_type}}</c>, and only consults <c>uri</c>
    /// when there is no data and the uri is http(s). So we send no <c>uri</c>: a <c>file://</c> one
    /// would be ignored there and its handling elsewhere is unproven.
    /// </para>
    /// </summary>
    internal sealed record ContentBlock(string Type, string? Text)
    {
        /// <summary>Base64 payload for an <c>image</c> block; null on a text block.</summary>
        public string? Data { get; init; }

        /// <summary>Media type for an <c>image</c> block (e.g. "image/png"); null on a text block.</summary>
        public string? MimeType { get; init; }

        /// <summary>An ACP <c>image</c> content block carrying base64 data.</summary>
        public static ContentBlock Image(string mimeType, string data) =>
            new ContentBlock("image", null) { Data = data, MimeType = mimeType };
    }

    // --- session/set_model (standard ACP model selection; Kiro) ---
    internal sealed record SetModelParams(string SessionId, string ModelId);

    // --- session/set_mode (standard ACP; what a "mode" IS is the agent's business — Kiro's are agent
    // configs, Claude's are permission modes). The empty result is ignored; the agent announces the
    // switch with a current_mode_update.
    internal sealed record SetModeParams(string SessionId, string ModeId);

    // --- session/set_config_option (Claude Code: model selection moved into session config options) ---
    // Category "model" carries a select whose values are the available model ids; picking one is a
    // set_config_option with configId "model". The response echoes the full updated configOptions
    // (read back as a raw JsonElement — see SetModelViaConfigOptionAsync for why not a typed record).
    internal sealed record SetConfigOptionParams(string SessionId, string ConfigId, string Value);

    // --- session/cancel ---
    internal sealed record CancelParams(string SessionId);

    // --- session/update (agent -> client notification) ---
    /// <summary>
    /// A <c>session/update</c> notification's params, projected out of the raw <see cref="JsonElement"/>
    /// the handler receives. NOT used as a StreamJsonRpc parameter type: binding this record directly
    /// makes the formatter re-deserialize the message from a rented, NUL-padded span, which throws
    /// <c>'0x00' is invalid after a value</c> on any frame that doesn't fill the buffer exactly — and on
    /// a notification that exception is swallowed, so the frame vanishes with no error anywhere (Kiro
    /// v3's config_option_update, carrying the model selector, is one). Asking for a JsonElement hands
    /// back the already-parsed document instead. Same gotcha as SetModelViaConfigOptionAsync.
    /// </summary>
    internal sealed record SessionUpdateParams
    {
        public string SessionId { get; init; } = string.Empty;

        /// <summary>Polymorphic; discriminated by the inner "sessionUpdate" string.</summary>
        public JsonElement Update { get; init; }

        /// <summary>Projects the raw notification params; missing/misshapen members degrade to empty
        /// rather than throwing — a malformed frame must not take the connection down.</summary>
        public static SessionUpdateParams From(JsonElement parameters) => new()
        {
            SessionId = parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("sessionId", out var id) &&
                        id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty,
            Update = parameters.ValueKind == JsonValueKind.Object &&
                     parameters.TryGetProperty("update", out var update)
                ? update
                : default,
        };
    }

    // --- session/request_permission (agent -> client) ---
    internal sealed record RequestPermissionParams
    {
        public string SessionId { get; init; } = string.Empty;
        public JsonElement ToolCall { get; init; }
        public PermissionOptionDto[] Options { get; init; } = Array.Empty<PermissionOptionDto>();

        /// <summary>
        /// Agent-specific extension data. Kiro puts <c>trustOptions</c> here (each with a
        /// <c>setting_key</c> like <c>runtime_write_paths</c>/<c>allowedCommands</c>) which we use to
        /// infer the action's kind, since the tool call itself carries no <c>kind</c>.
        /// </summary>
        [JsonPropertyName("_meta")]
        public JsonElement? Meta { get; init; }
    }

    internal sealed record PermissionOptionDto(string OptionId, string Name, string Kind);

    internal sealed record RequestPermissionResult(PermissionOutcome Outcome);

    internal sealed record PermissionOutcome
    {
        /// <summary>"selected" or "cancelled".</summary>
        public string Outcome { get; init; } = string.Empty;
        public string? OptionId { get; init; }
    }

    // --- fs/read_text_file and fs/write_text_file (agent -> client) ---
    internal sealed record ReadTextFileParams
    {
        public string SessionId { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public int? Line { get; init; }
        public int? Limit { get; init; }
    }

    internal sealed record ReadTextFileResult(string Content);

    internal sealed record WriteTextFileParams
    {
        public string SessionId { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
    }

    /// <summary>Empty result object (serializes to {}).</summary>
    internal sealed record WriteTextFileResult;
}
