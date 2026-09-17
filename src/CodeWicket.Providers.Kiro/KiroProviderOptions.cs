using System;

namespace CodeWicket.Providers.Kiro
{
    /// <summary>Configuration for launching the Kiro CLI as an ACP agent.</summary>
    public sealed class KiroProviderOptions
    {
        /// <summary>Path to the Kiro CLI executable. Defaults to "kiro-cli" (resolved via PATH).</summary>
        public string CliPath { get; set; } = "kiro-cli";

        /// <summary>
        /// Optional Kiro agent config to run the ACP session as (<c>kiro-cli acp --agent &lt;name&gt;</c> on
        /// v1/v2; selected over ACP by <c>session/set_mode</c> on v3, which refuses the flag). <b>An
        /// implementation tool, not a setting</b>: the user-facing binding was removed before release
        /// (2026-09-11) and nothing in the extension sets this — the Console proofs do, and a designed
        /// feature would. The record and the design questions: <c>docs/engineering/kiro-agents.md</c>.
        /// Kiro loads MCP servers through the active agent config; the built-in default (<c>kiro_default</c>,
        /// used when this is null/blank) inherits the mcp.json servers but not servers scoped to a named
        /// agent — set this to a named agent to pick those up. Null/blank uses Kiro's default agent.
        /// </summary>
        public string? Agent { get; set; }

        /// <summary>
        /// Optional Kiro agent engine to launch the ACP session with (<c>kiro-cli acp --agent-engine
        /// &lt;value&gt;</c>, e.g. <c>v1</c>/<c>v2</c>/<c>v3</c>). Null/blank lets Kiro pick its own default.
        /// (In a plain terminal the engine is selected with a top-level flag like <c>--v3</c>, but under
        /// ACP it's the <c>acp --agent-engine</c> subflag.) Kept a free string so future engine values work
        /// without a code change.
        /// </summary>
        public string? AgentEngine { get; set; }

        /// <summary>
        /// Raw JSON for the <c>_meta</c> sent with every session open (see
        /// <c>AcpAgentConfig.SessionMeta</c>) — e.g. <c>{"kiro":{"customAgents":[…]}}</c> to hand the v3
        /// engine an agent definition of our own. Null sends none. Malformed JSON is ignored with a
        /// stderr line rather than refusing the provider.
        /// </summary>
        public string? SessionMetaJson { get; set; }

        /// <summary>Extra arguments appended after "acp" (e.g. "--agent", "my-agent").</summary>
        public string[] ExtraArgs { get; set; } = Array.Empty<string>();
    }
}
