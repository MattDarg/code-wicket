using System;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;

namespace CodeWicket.Providers.ClaudeCode
{
    /// <summary>
    /// The Claude Code backend: a thin <see cref="AcpAgentProvider"/> that launches the official
    /// ACP adapter for the Claude Agent SDK (npm <c>@agentclientprotocol/claude-agent-acp</c>).
    /// The adapter owns the agent loop and auth (the user's Claude Code login / ANTHROPIC_API_KEY);
    /// the generic base handles the ACP protocol. Pure config — no launch flags beyond the bin.
    /// </summary>
    /// <remarks>
    /// Model selection rides ACP's "session config options" (category "model"), not
    /// <c>session/set_model</c>: the adapter surfaces the live model list in the <c>session/new</c>
    /// config options and switches via <c>session/set_config_option</c>. The generic ACP session
    /// handles both; we advertise <see cref="AgentCapabilities.ModelSelection"/> and seed a single
    /// "default" model — the real list is discovered per session and flows back to the picker.
    /// </remarks>
    public sealed class ClaudeCodeAgentProvider : AcpAgentProvider
    {
        public ClaudeCodeAgentProvider(ClaudeCodeProviderOptions? options = null)
            : base(BuildConfig(options ?? new ClaudeCodeProviderOptions()))
        {
        }

        private static AcpAgentConfig BuildConfig(ClaudeCodeProviderOptions options) => new()
        {
            ProviderId = "claude-code",
            DisplayName = "Claude Code",
            CliPath = ResolveCliPath(options.CliPath),
            LaunchArgs = options.ExtraArgs,
            // The `claude` CLI itself, not the ACP adapter this provider drives: the user is being
            // handed something to paste into a terminal, and `claude-agent-acp` speaks a protocol
            // rather than to a person. Its store is the SAME one, keyed by a hash of the working
            // directory - which is why the host pairs this with a `cd` (issue #108).
            ResumeCommandTemplate = "claude --resume {id}",
            Models = new[] { new ModelInfo("default", "Claude Code default") },
            Capabilities =
                AgentCapabilities.ToolCalls
                | AgentCapabilities.ClientFileSystem
                | AgentCapabilities.Mcp
                | AgentCapabilities.Thinking
                | AgentCapabilities.Cancellation
                | AgentCapabilities.ResumeSession
                | AgentCapabilities.ModelSelection,
            // The adapter resolves its permission mode from settings files that include
            // <cwd>\.claude\settings.local.json — inside the working tree, where the agent's own Write
            // tool reaches — and a `bypassPermissions` there starts the next session asking nothing
            // (issue #269; measured for bypassPermissions, acceptEdits and auto). "default" is the ask
            // mode; Claude Code 2.1.200 renamed it "Manual" on every surface and deliberately kept the
            // wire id, so the id is what to pin and the name is what to print (#270). "auto" is the
            // adapter's classifier mode, declared for issue #261 and mapped to nothing yet.
            // "Agent mode", not "Permission mode": the panel's copied report already carries OUR picker
            // as "Permission mode", and two lines with one label describing two different gates is the
            // confusion #269 was about. The tooltip says whose it is.
            ModeLabel = "Agent mode",
            BackendModes = new BackendPermissionModes(
                AskModeId: "default",
                AutoModeId: "auto",
                SettingsHint: "Claude Code reads it from `permissions.defaultMode` in `.claude/settings.local.json` "
                              + "or `.claude/settings.json` under the workspace, or `~/.claude/settings.json`."),
        };

        /// <summary>
        /// The npm bin is a <c>.cmd</c> shim on Windows; CreateProcess only appends <c>.exe</c>
        /// when searching PATH, so the extension must be explicit for the bare name to resolve.
        /// </summary>
        private static string ResolveCliPath(string? configured) =>
            !string.IsNullOrWhiteSpace(configured)
                ? configured!
                : OperatingSystem.IsWindows() ? "claude-agent-acp.cmd" : "claude-agent-acp";
    }
}
