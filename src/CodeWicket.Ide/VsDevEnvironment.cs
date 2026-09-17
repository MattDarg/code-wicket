using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Captures the Visual Studio developer environment — the env block VsDevCmd.bat establishes
    /// (msbuild, vstest.console, signing tools on PATH) — for <c>run_command</c>'s spawned
    /// processes. VsDevCmd.bat is the canonical bootstrap: it is exactly what VS's own "Developer
    /// Command Prompt" terminal profile runs (<c>%VSAPPIDDIR%\..\Tools\VsDevCmd.bat</c>).
    /// Captured once per devenv session (~1 s, proven offline 2026-07-17: 87 vars, msbuild 18.x +
    /// vstest.console resolve) and cached — a VS install doesn't change under a running devenv.
    /// Failures are returned (not thrown) and NOT cached, so a transient problem can retry.
    /// </summary>
    internal static class VsDevEnvironment
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static IReadOnlyDictionary<string, string> _cached;

        /// <summary>Bounded so a wedged VsDevCmd can't hang the tool call indefinitely.</summary>
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Returns the captured env block, or (null, reason) when it can't be obtained.</summary>
        public static async Task<(IReadOnlyDictionary<string, string> Env, string Error)> GetAsync(
            CancellationToken cancellationToken)
        {
            var cached = _cached;
            if (cached is not null)
                return (cached, null);

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cached is not null)
                    return (_cached, null);

                var (env, error) = await CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (env is not null)
                    _cached = env;
                return (env, error);
            }
            finally
            {
                Gate.Release();
            }
        }

        // devenv.exe lives in Common7\IDE; VsDevCmd.bat in Common7\Tools. VSAPPIDDIR (set inside
        // devenv) is the fallback for hosts whose MainModule isn't devenv.
        private static string FindVsDevCmd()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    var candidate = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(exe), "..", "Tools", "VsDevCmd.bat"));
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                // MainModule can throw in odd host contexts; the env-var fallback below still applies.
            }

            var appIdDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
            if (!string.IsNullOrEmpty(appIdDir))
            {
                var candidate = Path.GetFullPath(Path.Combine(appIdDir, "..", "Tools", "VsDevCmd.bat"));
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static async Task<(IReadOnlyDictionary<string, string> Env, string Error)> CaptureAsync(
            CancellationToken cancellationToken)
        {
            var bat = FindVsDevCmd();
            if (bat is null)
                return (null, "could not locate VsDevCmd.bat next to this Visual Studio installation");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"\"{bat}\" -no_logo && set\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.Exited += (_, _) => { try { exited.TrySetResult(process.ExitCode); } catch { exited.TrySetCanceled(); } };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return (null, $"could not start cmd.exe to capture the developer environment: {ex.Message}");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (cancellationToken.Register(() => { try { process.Kill(); } catch { } exited.TrySetCanceled(); }))
            {
                var completed = await Task.WhenAny(exited.Task, Task.Delay(CaptureTimeout, cancellationToken)).ConfigureAwait(false);
                if (completed != exited.Task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { process.Kill(); } catch { }
                    return (null, $"VsDevCmd.bat did not finish within {CaptureTimeout.TotalSeconds:0}s");
                }
                var exitCode = await exited.Task.ConfigureAwait(false);
                try { process.WaitForExit(); } catch { } // flush the async readers

                var env = ParseEnvBlock(stdout.ToString());
                // VSCMD_VER is VsDevCmd's own success marker; without it the `set` output is just
                // the unmodified parent env (the bat failed part-way) — not a developer environment.
                if (exitCode != 0 || !env.ContainsKey("VSCMD_VER"))
                {
                    var detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                    if (detail.Length > 800)
                        detail = detail.Substring(0, 800) + "…";
                    return (null, $"VsDevCmd.bat failed (exit {exitCode}): {detail.Trim()}");
                }
                return (env, null);
            }
        }

        private static IReadOnlyDictionary<string, string> ParseEnvBlock(string setOutput)
        {
            // Env var names are case-insensitive on Windows (PATH vs Path).
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in setOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) // skip cmd noise and the leading-'=' hidden vars (=C:, =ExitCode)
                    env[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return env;
        }
    }
}
