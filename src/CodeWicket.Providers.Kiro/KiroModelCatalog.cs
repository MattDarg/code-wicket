using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using CodeWicket.Core;

namespace CodeWicket.Providers.Kiro
{
    /// <summary>
    /// Asks kiro-cli which models it can drive (<c>kiro-cli chat --list-models --format json</c>)
    /// and maps them to <see cref="ModelInfo"/>, ordered so the user's configured default is first.
    /// Best-effort and self-contained: any failure (CLI missing, not logged in, timeout, unexpected
    /// output) yields an empty list, so the caller can fall back to a single synthetic entry rather
    /// than surfacing an error.
    /// </summary>
    internal static class KiroModelCatalog
    {
        // Headroom for a proxied/TLS-intercepted round-trip: the probe reaches a service endpoint, and
        // 5s was tight enough to time out on slow corporate networks (stranding the picker on the
        // fallback for the engine's lifetime). Paired with the caller's cooldown-retry cache so a
        // timeout here self-heals on a later read rather than poisoning the list until VS restarts.
        private const int TimeoutMs = 10000;

        /// <summary>
        /// The catalog command. <c>--no-interactive</c> is load-bearing, not tidiness — named here so a
        /// test can pin it. Without it a signed-out (or expired-credential) kiro-cli answers by starting
        /// an OAuth login: it opens the browser and listens on 127.0.0.1:&lt;port&gt; for the redirect
        /// back. This shell-out is headless and tree-killed after 10s, so the listener dies mid-flow and
        /// the user's browser lands on a refused port — the "auth link that doesn't resolve", fired by
        /// nothing more than opening the chat window (this runs on <c>engine/listProviders</c>; no
        /// session is involved). With the flag it exits 1 in ~400ms with "Not logged in… run
        /// <c>kiro-cli login</c> first" and opens nothing, landing on the empty-list fallback below.
        /// </summary>
        internal static readonly string[] ListModelsArgs =
            { "chat", "--no-interactive", "--list-models", "--format", "json" };

        public static IReadOnlyList<ModelInfo> TryList(string cliPath)
        {
            var json = RunKiroJson(cliPath, ListModelsArgs);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<ModelInfo>();

            // This command needs credentials and just succeeded, so the session-launch gate
            // (KiroLoginState, which runs this very command) can skip its own probe. The picker
            // populates before any session starts, so on the normal path the gate spawns nothing.
            KiroLoginState.NoteSignedIn();

            try
            {
                var (models, listDefaultId) = Parse(json!);
                if (models.Count == 0)
                    return models;

                // The list JSON's `default_model` is a hardcoded service default ("auto") that ignores
                // the user's `chat.defaultModel` setting; the setting is the real per-user default (it's
                // what a session actually starts on). Prefer it, falling back to the list's default.
                var defaultId = TryGetConfiguredDefaultId(cliPath) ?? listDefaultId;
                MoveToFront(models, defaultId);
                return models;
            }
            catch
            {
                return Array.Empty<ModelInfo>();
            }
        }

        /// <summary>The user's configured default model (<c>chat.defaultModel</c>), or null if unavailable.</summary>
        private static string? TryGetConfiguredDefaultId(string cliPath)
        {
            var json = RunKiroJson(cliPath, "settings", "chat.defaultModel", "--format", "json");
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(json!);
                return doc.RootElement.ValueKind == JsonValueKind.String
                    ? doc.RootElement.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static (List<ModelInfo> Models, string? DefaultId) Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return (new List<ModelInfo>(), null);

            var result = new List<ModelInfo>();
            foreach (var m in models.EnumerateArray())
            {
                var id = m.TryGetProperty("model_id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id))
                    continue;

                var name = m.TryGetProperty("model_name", out var nameEl) ? nameEl.GetString() : null;
                int? contextWindow =
                    m.TryGetProperty("context_window_tokens", out var ctxEl)
                    && ctxEl.TryGetInt32(out var ctx)
                        ? ctx
                        : null;
                double? rateMultiplier =
                    m.TryGetProperty("rate_multiplier", out var rateEl)
                    && rateEl.TryGetDouble(out var rate)
                        ? rate
                        : null;

                result.Add(new ModelInfo(id!, string.IsNullOrEmpty(name) ? id! : name!)
                {
                    MaxInputTokens = contextWindow,
                    RateMultiplier = rateMultiplier,
                });
            }

            var defaultId = doc.RootElement.TryGetProperty("default_model", out var defEl)
                ? defEl.GetString()
                : null;
            return (result, defaultId);
        }

        // Surface the default first, so the UI's "first entry" default selection matches Kiro's,
        // regardless of the order the CLI happened to list them in.
        private static void MoveToFront(List<ModelInfo> models, string? id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            var i = models.FindIndex(m => m.Id == id);
            if (i > 0)
            {
                var item = models[i];
                models.RemoveAt(i);
                models.Insert(0, item);
            }
        }

        /// <summary>Runs kiro-cli with the given args and returns stdout, or null on any failure.
        /// Drains both stdio streams so a chatty stderr can't deadlock, and bounds it with a timeout.</summary>
        private static string? RunKiroJson(string cliPath, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(cliPath) ? "kiro-cli" : cliPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in args)
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null)
                    return null;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return null;
                }

                _ = stderr; // drained; discarded
                return process.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
