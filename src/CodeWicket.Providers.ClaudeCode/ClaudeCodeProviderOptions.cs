using System;

namespace CodeWicket.Providers.ClaudeCode
{
    /// <summary>Configuration for launching the Claude Code ACP adapter.</summary>
    public sealed class ClaudeCodeProviderOptions
    {
        /// <summary>
        /// Path to the adapter executable; null/empty resolves the npm bin ("claude-agent-acp",
        /// which is a .cmd shim on Windows) via PATH.
        /// </summary>
        public string? CliPath { get; set; }

        /// <summary>Extra arguments passed to the adapter. Normally none.</summary>
        public string[] ExtraArgs { get; set; } = Array.Empty<string>();
    }
}
