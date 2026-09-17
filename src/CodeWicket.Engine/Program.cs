using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Engine.Mcp;
using CodeWicket.Providers.Acp;
using CodeWicket.Providers.ClaudeCode;
using CodeWicket.Providers.Kiro;

namespace CodeWicket.Engine
{
    /// <summary>
    /// Real entrypoint: the VS shell launches this process and talks JSON-RPC over its stdio.
    /// stdout/stdin carry the protocol; logging must go to stderr to keep the channel clean.
    /// Pass "--fake" to use the in-process fake provider instead of kiro-cli.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // Relay mode: the agent spawned us as a stdio MCP server. Pump bytes to the running
            // engine's pipe (see McpPipeHost) instead of starting the shell↔engine JSON-RPC host.
            var relayIndex = Array.IndexOf(args, McpPipeHost.RelayFlag);
            if (relayIndex >= 0)
            {
                if (relayIndex + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"[engine] {McpPipeHost.RelayFlag} requires a pipe name");
                    return 1;
                }
                return await McpStdioRelay.RunAsync(args[relayIndex + 1]).ConfigureAwait(false);
            }

            // Our stderr is drained into the shell's engine.log, so retention reports from THIS
            // process land in the same file as the shell's. It matters that both are wired: the ACP
            // and MCP tees roll in here, not in the shell, and acp.log is the log that actually
            // reaches the 5 MB cap mid-session.
            DiagnosticLog.RetentionLog = line => Console.Error.WriteLine(line);

            // Register every backend the engine can drive; the shell picks per session via the
            // picker (StartSessionRequest.ProviderId).
            var registry = new AgentProviderRegistry();

            // Backends a site/user has switched off. Opt-out wire contract: the shell derives this
            // semicolon list from the per-built-in enable checkboxes (unchecked → id here); absent =
            // all on. A disabled backend is never registered, so it neither appears in the picker nor
            // can be started — for installs where e.g. Kiro or Claude can never work.
            var disabled = new HashSet<string>(
                (Environment.GetEnvironmentVariable("CWKT_DISABLED_PROVIDERS") ?? string.Empty)
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (disabled.Count > 0)
                Console.Error.WriteLine($"[engine] disabled providers: {string.Join(", ", disabled)}");

            // The in-process test double is OPT-IN: only registered when the shell asked for it
            // (--fake, i.e. DefaultProvider is explicitly "fake"). End-user installs never see it.
            var useFake = args.Contains("--fake");
            if (useFake)
                registry.Register(new FakeAgentProvider());

            // The shell forwards the configured Kiro CLI path as CWKT_KIRO_CLI and the engine choice as
            // CWKT_KIRO_AGENT_ENGINE. There is no agent env: KiroProviderOptions.Agent is an
            // implementation tool (proofs, a future feature), not a setting — docs/engineering/kiro-agents.md.
            if (!disabled.Contains("kiro"))
            {
                var kiroOptions = new KiroProviderOptions();
                var kiroCli = Environment.GetEnvironmentVariable("CWKT_KIRO_CLI");
                if (!string.IsNullOrWhiteSpace(kiroCli))
                    kiroOptions.CliPath = kiroCli!;
                var kiroAgentEngine = Environment.GetEnvironmentVariable("CWKT_KIRO_AGENT_ENGINE");
                if (!string.IsNullOrWhiteSpace(kiroAgentEngine))
                    kiroOptions.AgentEngine = kiroAgentEngine!;
                registry.Register(new KiroAgentProvider(kiroOptions));
            }

            // The shell forwards the configured Claude Code adapter path as CWKT_CLAUDE_ACP;
            // unset resolves the npm bin ("claude-agent-acp") via PATH.
            if (!disabled.Contains("claude-code"))
            {
                var claudeAcp = Environment.GetEnvironmentVariable("CWKT_CLAUDE_ACP");
                var claudeOptions = string.IsNullOrWhiteSpace(claudeAcp)
                    ? new ClaudeCodeProviderOptions()
                    : new ClaudeCodeProviderOptions { CliPath = claudeAcp };
                registry.Register(new ClaudeCodeAgentProvider(claudeOptions));
            }

            // User-configured ACP agents (config.json customAcpAgents, forwarded by the shell as a
            // JSON array). Registered after the built-ins, which win on an id collision.
            var customJson = Environment.GetEnvironmentVariable("CWKT_CUSTOM_AGENTS");
            if (!string.IsNullOrWhiteSpace(customJson))
            {
                foreach (var config in AcpAgentConfigJson.ParseList(
                    customJson!, w => Console.Error.WriteLine($"[engine] {w}")))
                {
                    if (disabled.Contains(config.ProviderId))
                        continue;
                    if (registry.Get(config.ProviderId) is not null)
                    {
                        Console.Error.WriteLine(
                            $"[engine] custom agent '{config.ProviderId}' skipped: id already registered");
                        continue;
                    }
                    registry.Register(new AcpAgentProvider(config));
                }
            }

            // Seed provider model lists from the shell's persisted cache (forwarded as CWKT_MODEL_CACHE,
            // a JSON map of providerId -> [{id,displayName,rateMultiplier}]). Backends that only reveal
            // their models via a live session (Claude Code) then show the real list before one opens.
            SeedModelCache(registry, Environment.GetEnvironmentVariable("CWKT_MODEL_CACHE"));

            var defaultProviderId = useFake ? "fake" : "kiro";

            var sending = Console.OpenStandardOutput();
            var receiving = Console.OpenStandardInput();

            await using var host = new EngineHost(sending, receiving, registry, defaultProviderId);
            host.Start();
            Console.Error.WriteLine($"[engine] started; providers=[{string.Join(", ", registry.Providers.Select(p => p.ProviderId))}], default='{defaultProviderId}'");

            // Invite every provider whose model list costs a live probe to run it NOW, in the
            // background. Offered to all of them and answered only by the ones with something to
            // discover, so this stays free of "if kiro" — the same rule the model-selection and
            // workspace-marker mechanics follow. Deliberately AFTER Start(): the answer arrives as a
            // notification, so the connection has to be listening before the probe can land.
            foreach (var provider in registry.Providers.OfType<AcpAgentProvider>())
            {
                var providerId = provider.ProviderId;
                provider.BeginModelRefresh(models =>
                {
                    Console.Error.WriteLine($"[engine] '{providerId}' model list refreshed ({models.Count} models)");
                    host.NotifyProviderModels(providerId, models);
                });
            }

            await host.Completion.ConfigureAwait(false);
            return 0;
        }

        /// <summary>
        /// Applies the shell's persisted model cache (CWKT_MODEL_CACHE, a JSON object mapping providerId
        /// to an array of {id, displayName, rateMultiplier}) onto the matching <see cref="AcpAgentProvider"/>s.
        /// Best-effort: malformed JSON or unknown providers are skipped, never fatal to startup.
        /// </summary>
        private static void SeedModelCache(AgentProviderRegistry registry, string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json!);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return;

                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (registry.Get(entry.Name) is not AcpAgentProvider provider ||
                        entry.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;

                    var models = new System.Collections.Generic.List<ModelInfo>();
                    foreach (var m in entry.Value.EnumerateArray())
                    {
                        if (m.ValueKind != System.Text.Json.JsonValueKind.Object ||
                            !m.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
                            continue;

                        var name = m.TryGetProperty("displayName", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String
                            ? dn.GetString()!
                            : id.GetString()!;
                        double? rate = m.TryGetProperty("rateMultiplier", out var r) &&
                            r.ValueKind == System.Text.Json.JsonValueKind.Number ? r.GetDouble() : null;
                        models.Add(new ModelInfo(id.GetString()!, name) { RateMultiplier = rate });
                    }

                    provider.SeedModels(models);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[engine] ignoring malformed CWKT_MODEL_CACHE: {ex.Message}");
            }
        }
    }
}
