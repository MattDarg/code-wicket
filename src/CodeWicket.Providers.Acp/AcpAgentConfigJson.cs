using System;
using System.Collections.Generic;
using System.Text.Json;
using CodeWicket.Core;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Parses user-configured ACP agents from JSON (the shell's config.json <c>customAcpAgents</c>
    /// array, forwarded to the engine as the <c>CWKT_CUSTOM_AGENTS</c> env var) into
    /// <see cref="AcpAgentConfig"/>s ready for a plain <see cref="AcpAgentProvider"/>. The JSON
    /// shape is defined by <c>CodeWicket.Shell.CustomAcpAgent</c> — keep the two in sync.
    /// Never throws: malformed input or bad entries are reported via <paramref name="warn"/> and
    /// skipped, so one broken config line can't take down every backend.
    /// </summary>
    public static class AcpAgentConfigJson
    {
        // What a spec-ACP agent is assumed to support when the entry lists no capabilities.
        // ResumeSession/ModelSelection stay opt-in — advertising them for an agent that lacks
        // session/load or set_model breaks resume banners and the model picker.
        private const AgentCapabilities DefaultCapabilities =
            AgentCapabilities.ToolCalls
            | AgentCapabilities.ClientFileSystem
            | AgentCapabilities.Mcp
            | AgentCapabilities.Thinking
            | AgentCapabilities.Cancellation;

        public static IReadOnlyList<AcpAgentConfig> ParseList(string json, Action<string>? warn = null)
        {
            CustomAgentDto[]? entries;
            try
            {
                entries = JsonSerializer.Deserialize<CustomAgentDto[]>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException ex)
            {
                warn?.Invoke($"custom agents JSON is invalid ({ex.Message}); none registered");
                return Array.Empty<AcpAgentConfig>();
            }

            var configs = new List<AcpAgentConfig>();
            foreach (var entry in entries ?? Array.Empty<CustomAgentDto>())
            {
                if (entry is null)
                    continue;
                if (string.IsNullOrWhiteSpace(entry.ProviderId))
                {
                    warn?.Invoke("custom agent skipped: missing providerId");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(entry.CliPath))
                {
                    warn?.Invoke($"custom agent '{entry.ProviderId}' skipped: missing cliPath");
                    continue;
                }

                configs.Add(new AcpAgentConfig
                {
                    ProviderId = entry.ProviderId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                        ? entry.ProviderId.Trim()
                        : entry.DisplayName!.Trim(),
                    CliPath = entry.CliPath.Trim(),
                    LaunchArgs = entry.Args ?? Array.Empty<string>(),
                    Environment = entry.Env,
                    Models = ParseModels(entry.Models),
                    Capabilities = ParseCapabilities(entry.ProviderId, entry.Capabilities, warn),
                    // Probed-or-defaulted capabilities stay refreshable from initialize on session
                    // start; hand-declared ones are authoritative.
                    DiscoverCapabilities = entry.CapabilitiesDiscovered
                        || entry.Capabilities is null || entry.Capabilities.Length == 0,
                });
            }

            return configs;
        }

        private static IReadOnlyList<ModelInfo> ParseModels(CustomModelDto[]? models)
        {
            var parsed = new List<ModelInfo>();
            foreach (var m in models ?? Array.Empty<CustomModelDto>())
                if (m is not null && !string.IsNullOrWhiteSpace(m.Id))
                    parsed.Add(new ModelInfo(m.Id.Trim(), string.IsNullOrWhiteSpace(m.Name) ? m.Id.Trim() : m.Name!.Trim()));

            // The picker binds Models[0] as the default selection, so the list must never be empty.
            return parsed.Count > 0 ? parsed : new[] { new ModelInfo("default", "Default") };
        }

        private static AgentCapabilities ParseCapabilities(string providerId, string[]? names, Action<string>? warn)
        {
            if (names is null || names.Length == 0)
                return DefaultCapabilities;

            var caps = AgentCapabilities.None;
            foreach (var name in names)
            {
                if (IsNamedCapability(name, out var flag))
                    caps |= flag;
                else
                    warn?.Invoke($"custom agent '{providerId}': unknown capability '{name}' ignored");
            }

            return caps == AgentCapabilities.None ? DefaultCapabilities : caps;
        }

        /// <summary>
        /// True when <paramref name="name"/> is one capability spelled by NAME — the only form a config
        /// may declare.
        /// </summary>
        /// <remarks>
        /// <c>Enum.TryParse</c> alone is too generous for a declaration a user's config makes about what
        /// a CLI can do. It accepts NUMBERS, so <c>"64"</c> quietly grants <c>ModelSelection</c> to an
        /// agent with no <c>session/set_model</c> and <c>"9999"</c> sets bits no member defines; it
        /// accepts comma-separated LISTS, so one entry can grant several; and every one of those returns
        /// true, which is precisely the path that skips the "unknown capability … ignored" warning a bad
        /// entry is supposed to earn. So: reject anything that starts like a number, and require the
        /// parsed value to be a defined member — which also rules out a list, since a combination is not
        /// one. Same allowlist-by-construction discipline as <c>ProjectSettingsReader</c>: name a thing
        /// we know, or get nothing and be told.
        /// </remarks>
        private static bool IsNamedCapability(string? name, out AgentCapabilities flag)
        {
            flag = AgentCapabilities.None;
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return false;

            var first = trimmed![0];
            if (char.IsDigit(first) || first == '-' || first == '+')
                return false;

            return Enum.TryParse(trimmed, ignoreCase: true, out flag) && Enum.IsDefined(flag);
        }

        // JSON shapes mirroring CodeWicket.Shell.CustomAcpAgent (the authoring model).
        private sealed record CustomAgentDto
        {
            public string ProviderId { get; init; } = string.Empty;
            public string? DisplayName { get; init; }
            public string CliPath { get; init; } = string.Empty;
            public string[]? Args { get; init; }
            public Dictionary<string, string>? Env { get; init; }
            public string[]? Capabilities { get; init; }
            public CustomModelDto[]? Models { get; init; }
            public bool CapabilitiesDiscovered { get; init; }
        }

        private sealed record CustomModelDto
        {
            public string Id { get; init; } = string.Empty;
            public string? Name { get; init; }
        }
    }
}
