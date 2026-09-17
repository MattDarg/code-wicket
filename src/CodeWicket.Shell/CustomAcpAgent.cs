using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CodeWicket.Shell
{
    /// <summary>
    /// A user-configured ACP agent (config.json <c>customAcpAgents</c>): any CLI that speaks ACP
    /// over stdio can be added as a backend without code. This class is the storage/wire shape —
    /// the shell serializes the array to the <c>CWKT_CUSTOM_AGENTS</c> env var on the engine
    /// process, and the engine parses it into <c>AcpAgentConfig</c>s
    /// (<c>AcpAgentConfigJson.ParseList</c> in CodeWicket.Providers.Acp — keep the two in sync).
    /// Plain class (STJ round-trip on net472; no records in Shell).
    /// </summary>
    public sealed class CustomAcpAgent
    {
        /// <summary>Stable provider id shown in pickers and persisted in sessions. Required;
        /// ids that collide with built-ins (kiro, claude-code, fake) are skipped.</summary>
        public string ProviderId { get; set; } = string.Empty;

        /// <summary>Human-readable picker name; defaults to <see cref="ProviderId"/>.</summary>
        public string? DisplayName { get; set; }

        /// <summary>The agent executable (resolved via PATH if not rooted). Required.
        /// npm bins on Windows need the <c>.cmd</c> extension spelled out.</summary>
        public string CliPath { get; set; } = string.Empty;

        /// <summary>Arguments that put the CLI into ACP mode (e.g. ["--experimental-acp"]).</summary>
        public string[] Args { get; set; } = Array.Empty<string>();

        /// <summary>Extra environment variables for the agent process (e.g. an API key).</summary>
        public Dictionary<string, string>? Env { get; set; }

        /// <summary>
        /// <c>AgentCapabilities</c> flag names (case-insensitive): ToolCalls, ClientFileSystem,
        /// Mcp, Thinking, Cancellation, ResumeSession, ModelSelection. Omitted/empty applies the
        /// spec-ACP baseline (everything except ResumeSession/ModelSelection).
        /// </summary>
        public string[]? Capabilities { get; set; }

        /// <summary>Static model list for the picker; omitted shows a single "default" entry.</summary>
        public CustomAcpAgentModel[]? Models { get; set; }

        /// <summary>
        /// True when <see cref="Capabilities"/> were filled in by the save-time probe
        /// (<see cref="AcpAgentProbe"/>) rather than typed by the user — probed capabilities stay
        /// refreshable from the agent's <c>initialize</c> on session start; hand-written ones are
        /// authoritative and never touched.
        /// </summary>
        public bool CapabilitiesDiscovered { get; set; }

        /// <summary>
        /// Parses a user-authored JSON array (blank = empty list) for the settings UI, reporting a
        /// human-readable <paramref name="error"/> instead of throwing so the UI can reject the
        /// value without clobbering the stored one.
        /// </summary>
        public static bool TryParseList(string? json, out CustomAcpAgent[] agents, out string error)
        {
            agents = Array.Empty<CustomAcpAgent>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(json))
                return true;

            try
            {
                var parsed = JsonSerializer.Deserialize<CustomAcpAgent[]>(
                    json!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                agents = parsed ?? Array.Empty<CustomAcpAgent>();
                return true;
            }
            catch (JsonException ex)
            {
                error = $"Custom ACP agents must be a JSON array: {ex.Message}";
                return false;
            }
        }

        /// <summary>Serializes for display in the settings UI (indented; empty string when none).</summary>
        public static string ToDisplayJson(CustomAcpAgent[] agents) =>
            agents.Length == 0
                ? string.Empty
                : JsonSerializer.Serialize(agents,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    });
    }

    /// <summary>A model entry for <see cref="CustomAcpAgent.Models"/>.</summary>
    public sealed class CustomAcpAgentModel
    {
        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }
    }
}
