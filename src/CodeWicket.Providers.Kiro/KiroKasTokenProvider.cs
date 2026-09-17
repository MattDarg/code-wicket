using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Providers.Kiro
{
    /// <summary>
    /// Host-side implementation of Kiro's <c>_kiro/auth/getAccessToken</c> ACP callback. The v3
    /// agent engine (KAS) doesn't read the CLI's credential store itself — it delegates token
    /// refresh to its ACP host and fails every model call with <c>TokenExpiredError</c> when the
    /// host doesn't answer. Kiro's own TUI host resolves the callback by shelling out to the
    /// hidden <c>kiro-cli chat _ get-kas-token</c> command and relaying its payload
    /// (<c>{accessToken, expiresAt, profileArn, authMethod?, provider?}</c>); we do exactly the
    /// same, against the same configured CLI the ACP session was launched from — so the token
    /// comes from the user's existing <c>kiro-cli login</c>, no separate auth flow.
    /// </summary>
    internal static class KiroKasTokenProvider
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        public static async Task<JsonElement> GetAsync(string cliPath, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("chat");
            psi.ArgumentList.Add("_");
            psi.ArgumentList.Add("get-kas-token");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to start '{cliPath}' for get-kas-token");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

                // WHOSE cancellation, which the catch alone could not tell: `timeout` is linked to the
                // caller's token, so an ordinary teardown - the user closing the chat window while a v3
                // auth refresh is in flight - arrived here indistinguishable from the 30s deadline and
                // was rethrown as "get-kas-token timed out". That put a network/CLI problem in the
                // transcript that did not exist, and converted a cancellation into a fault the ACP
                // layer can no longer recognise as one. The process is killed either way.
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("get-kas-token timed out");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            // Read the payload BEFORE trusting the exit code. kiro-cli reports this command's failures
            // as a JSON envelope on STDOUT — {"kind":"error","data":{"message":"You are not logged in.
            // Please log in with `kiro-cli login`."}} — while exiting 1 with an EMPTY stderr. Checking
            // the exit code first therefore discards the one line that says what to do and throws
            // "get-kas-token exited 1: " instead, which is precisely what reached the user's transcript
            // (via Kiro's "Auth refresh callback failed: …") when the v3 engine's auth callback failed.
            if (TryGetReportedError(stdout, out var reported))
                throw new InvalidOperationException(reported);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"get-kas-token exited {process.ExitCode}: {Clamp(stderr)}");

            return ParsePayload(stdout);
        }

        /// <summary>
        /// Reads kiro-cli's own error envelope (<c>{kind:"error", data:{message}}</c>) off stdout.
        /// Only an explicit <c>kind:"error"</c> frame's message is relayed: that is human guidance
        /// ("You are not logged in…"), never a credential — which is why this may quote a VALUE where
        /// <see cref="ParsePayload"/> deliberately reports only property names.
        /// </summary>
        internal static bool TryGetReportedError(string stdout, out string message)
        {
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(trimmed); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("kind", out var kind)
                        || kind.ValueKind != JsonValueKind.String
                        || !string.Equals(kind.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var text = doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("message", out var m)
                        && m.ValueKind == JsonValueKind.String
                            ? m.GetString()
                            : null;

                    message = string.IsNullOrWhiteSpace(text)
                        ? "get-kas-token reported an error with no message"
                        : Clamp(text!);
                    return true;
                }
            }

            message = string.Empty;
            return false;
        }

        /// <summary>
        /// Extracts the token payload from the command's stdout. Accepts the bare payload
        /// (<c>accessToken</c> at the root) or an envelope (<c>{kind, data:{accessToken…}}</c> — the
        /// shape Kiro's TUI unwraps as <c>output.data</c>). Error messages never include property
        /// VALUES (the token must not leak into logs), only the shape that failed to parse.
        /// </summary>
        // internal so the stdout shapes kiro-cli actually emits can be asserted without spawning it.
        internal static JsonElement ParsePayload(string stdout)
        {
            // The shape of the last JSON object that was not the token, for the failure message. Kept
            // rather than thrown on, which is the fix: every other rejection in this loop CONTINUES to
            // the next line, and this one did not. kiro-cli writes {"kind":…,"data":…} envelopes — the
            // file says so above — so any preamble, warning or telemetry frame ahead of the token frame
            // failed the whole parse with "returned JSON without an accessToken" while the real payload
            // sat on the very next line. Every model call then fails with TokenExpiredError, and the
            // message points at the wrong frame.
            string? lastShape = null;

            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(trimmed); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("accessToken", out _))
                        return root.Clone();
                    if (root.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("accessToken", out _))
                        return data.Clone();

                    lastShape = string.Join(",", root.EnumerateObject().Select(p => p.Name));
                }
            }

            // Named after the whole of stdout has been read, so the shape reported is the last frame
            // that could plausibly have been the token rather than the first thing that wasn't.
            throw new InvalidOperationException(
                lastShape is null
                    ? "get-kas-token produced no JSON payload on stdout"
                    : $"get-kas-token returned JSON without an accessToken (properties: {lastShape})");
        }

        private static string Clamp(string text) =>
            text.Length <= 500 ? text.Trim() : text.Substring(0, 500).Trim() + "…";
    }
}
