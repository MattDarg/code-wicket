using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Asks a CLI what version it is, for backends that don't say so on the wire.
    ///
    /// <para><b>The wire is preferred and this is the fallback.</b> ACP's <c>initialize</c> result can
    /// carry <c>agentInfo</c>, and where it does (claude-agent-acp sends
    /// <c>{"name":"@agentclientprotocol/claude-agent-acp","version":"0.63.0"}</c>) that answer is free,
    /// needs no process, and describes the program actually on the other end of the pipe. kiro-cli
    /// sends none — verified against a captured <c>acp.log</c> — which is the whole reason this
    /// exists.</para>
    ///
    /// <para><b>Which args, if any, is DATA</b> (<see cref="AcpAgentConfig.VersionArgs"/>), not a
    /// per-agent branch. An agent that advertises <c>agentInfo</c> declares none and never spawns
    /// anything.</para>
    ///
    /// <para><b>Fire-and-forget, off the session-start path.</b> A user waits on session start, and a
    /// diagnostic must not add a process spawn to it. Started alongside the launch and left to land:
    /// by the time any failure has completed a round trip the value is there, and if it somehow isn't,
    /// the panel says "version unknown" rather than blocking to find out. The line it writes to
    /// <c>engine.log</c> is worth as much as the field — it means a log from a session that never
    /// failed still records what ran, which is what a later report is read against.</para>
    ///
    /// <para>Best-effort throughout: every failure resolves to no version at all. This is diagnostic
    /// garnish, and it must never be able to affect whether a session starts.</para>
    /// </summary>
    internal static class AgentVersionProbe
    {
        // Generous for the same reason KiroModelCatalog's is: a TLS-intercepting proxy or an on-access
        // scanner can make a trivial local spawn slow, and being slow must not be reported as failing.
        private const int TimeoutMs = 10000;

        // Clamp: this lands in a chat panel and a one-line answer is the contract. A CLI that prints a
        // banner gets its first line taken, not its essay.
        private const int MaxLength = 200;

        // Keyed by what was actually run. Cached for the host's lifetime INCLUDING failures: a version
        // does not change under a running engine, and re-spawning a process per session to re-learn
        // that we couldn't tell is worse than not knowing. A genuine upgrade needs a restart to take
        // effect anyway, since the running CLI is the old one.
        private static readonly ConcurrentDictionary<string, string?> Cache =
            new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Starts the probe if <paramref name="args"/> declares one, and files the answer into
        /// <paramref name="diagnostics"/>. Returns immediately.
        /// </summary>
        public static void Begin(string cliPath, IReadOnlyList<string>? args, AgentDiagnostics diagnostics)
        {
            if (args is null || args.Count == 0 || diagnostics is null)
                return;

            // The separator is written as an ESCAPE, not as a raw NUL byte. A NUL anywhere in
            // a source file makes git treat the whole file as binary: no line diff, and
            // invisible to git grep - which is how this file kept the pre-rename namespace
            // through a sweep that found every other one.
            var key = cliPath + "\0" + string.Join("\0", args);
            if (Cache.TryGetValue(key, out var cached))
            {
                if (cached is { Length: > 0 })
                    diagnostics.SetVersionIfUnknown(cached);
                return;
            }

            // Unobserved by design — nothing awaits this. The blanket catch inside Run is therefore
            // load-bearing: an escaped exception on a task nobody observes fails silently at
            // finalization (the shape issue #88 hit), so it is caught where it happens.
            _ = Task.Run(() =>
            {
                var version = Run(cliPath, args);
                Cache[key] = version;
                if (version is { Length: > 0 })
                {
                    diagnostics.SetVersionIfUnknown(version);
                    Console.Error.WriteLine($"[acp] '{diagnostics.ProviderId}' agent version: {version}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[acp] '{diagnostics.ProviderId}' agent version: unknown ('{cliPath} {string.Join(" ", args)}' gave no usable answer)");
                }
            });
        }

        private static string? Run(string cliPath, IReadOnlyList<string> args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cliPath,
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

                // Drain both before waiting, so a chatty CLI can't deadlock on a full pipe buffer.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var elapsed = Stopwatch.StartNew();
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    return null;
                }

                // The reads need the SAME budget, and it is not the same wait. A process exiting does not
                // close the pipes — its children inherited the handles, and this repo's own launcher is
                // exactly that shape: kiro-cli.exe spawns a bundled node agent that outlives it (see
                // ProcessAcpConnection, AgentDiagnostics.Describe). Against one of those, WaitForExit
                // returns true promptly and ReadToEndAsync then never completes, so GetAwaiter().GetResult()
                // parks a pool thread for the life of the process — unobserved, since nothing awaits
                // Begin's task, and repeated, since Cache[key] is only written on the way out.
                var remaining = TimeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (!Task.WhenAll(stdout, stderr).Wait(remaining > 0 ? remaining : 0))
                    return null;

                // Both streams: which one carries a version banner is not a contract, and reading only
                // stdout would miss a CLI that prints it to stderr. Exit code is NOT checked — some
                // CLIs answer --version and exit non-zero, and a version we can read is useful
                // regardless of what the process thought of being asked.
                return FirstLine(stdout.GetAwaiter().GetResult())
                    ?? FirstLine(stderr.GetAwaiter().GetResult());
            }
            catch
            {
                // Missing, not executable, blocked by policy — all "we can't tell", which is a state
                // this reports by staying silent rather than by inventing a failure.
                return null;
            }
        }

        private static string? FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            foreach (var line in text!.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;
                return trimmed.Length > MaxLength ? trimmed.Substring(0, MaxLength) + "…" : trimmed;
            }
            return null;
        }
    }
}
