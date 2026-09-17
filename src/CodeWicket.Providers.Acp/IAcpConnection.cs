using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CodeWicket.Core;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// The byte transport for an ACP connection: a pair of streams carrying newline-delimited
    /// JSON-RPC. Abstracting this lets the session run against a real <c>kiro-cli</c> process or,
    /// for tests, an in-memory duplex stream wired to a fake agent.
    /// </summary>
    internal interface IAcpConnection : IDisposable
    {
        /// <summary>Stream the client writes to (the agent's stdin).</summary>
        Stream Sending { get; }

        /// <summary>Stream the client reads from (the agent's stdout).</summary>
        Stream Receiving { get; }
    }

    /// <summary>An <see cref="IAcpConnection"/> backed by a launched ACP agent process (any CLI that
    /// speaks ACP over stdio). The launch command is data — see <see cref="AcpAgentConfig"/>.</summary>
    internal sealed class ProcessAcpConnection : IAcpConnection
    {
        // Agent shell tools run headless: a child process that opens an interactive editor blocks
        // forever on a terminal that doesn't exist and hangs the turn (issue #22 — Kiro's shell ran
        // "git rebase --continue", git spawned vim for the commit message, vim drew its screen into
        // the pipe and waited for keystrokes that could never arrive). Force git non-interactive on
        // the whole process tree. "true"/"cat" resolve inside git's own sh (Git for Windows bundles
        // them), so these work cross-platform.
        internal static readonly IReadOnlyList<KeyValuePair<string, string>> NonInteractiveEnvDefaults =
            new KeyValuePair<string, string>[]
            {
                new("GIT_EDITOR", "true"),          // accept commit messages as-is (rebase --continue, commit)
                new("GIT_SEQUENCE_EDITOR", "true"), // accept rebase todo lists as-is
                new("GIT_PAGER", "cat"),
                new("PAGER", "cat"),
                new("GIT_TERMINAL_PROMPT", "0"),    // fail fast instead of prompting for credentials
            };

        /// <summary>Applies <see cref="NonInteractiveEnvDefaults"/> to <paramref name="environment"/>,
        /// skipping any key already present — system env, the engine process's inherited env
        /// (<c>EngineEnvironment</c> config), and explicit config <c>Env</c> all win over defaults.</summary>
        internal static void ApplyNonInteractiveDefaults(IDictionary<string, string?> environment)
        {
            foreach (var kvp in NonInteractiveEnvDefaults)
                if (!environment.ContainsKey(kvp.Key))
                    environment[kvp.Key] = kvp.Value;
        }

        // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND. The only two codes that license the word
        // "found"; every other one keeps the OS's own sentence instead of being retold as absence.
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        /// <summary>
        /// What to say when the agent program could not be launched. Pure and internal so the wording
        /// can be asserted directly — the wording IS the behaviour here, the same reason
        /// <c>Core.Ide.FileWriteRefusal</c> is written and tested that way.
        /// <para><b>It names what was CHECKED, and claims absence only where the error code says so.</b>
        /// A bare name is resolved through PATH and a rooted one is not, so the two cannot be reported
        /// with the same sentence: telling a user their configured absolute path "is not on PATH"
        /// sends them to edit an environment variable that was never consulted. Anything that is not
        /// a not-found keeps the OS's own words — "Access is denied", a 16-bit image error — because a
        /// guess at what those mean is the thing the reader would then repeat with more confidence
        /// than we had.</para>
        /// </summary>
        internal static string DescribeLaunchFailure(
            System.ComponentModel.Win32Exception ex, string cliPath, string workingDirectory)
        {
            var where = string.IsNullOrEmpty(workingDirectory)
                ? string.Empty
                : $" (working directory '{workingDirectory}')";

            if (ex.NativeErrorCode is not (ErrorFileNotFound or ErrorPathNotFound))
                return $"'{cliPath}' could not be started: {ex.Message}{where}";

            // A name with no directory in it is what the OS resolves through PATH; anything else was
            // looked for exactly where it is spelled, so that is what the message has to say.
            return IsBareProgramName(cliPath)
                ? $"no program named '{cliPath}' was found on PATH{where}"
                : $"no program was found at '{cliPath}'{where}";
        }

        private static bool IsBareProgramName(string cliPath) =>
            cliPath.IndexOf(Path.DirectorySeparatorChar) < 0
            && cliPath.IndexOf(Path.AltDirectorySeparatorChar) < 0
            && !Path.IsPathRooted(cliPath);

        private readonly Process _process;
        private readonly Stopwatch _uptime = Stopwatch.StartNew();

        /// <summary>
        /// What this process has told us about itself — its stderr, its exit, its version. Held so a
        /// failure can be reported with the agent's own account beside it instead of a bare protocol
        /// word (issue #82). Owned here because this is the only class that has the process.
        /// </summary>
        public AgentDiagnostics Diagnostics { get; }

        public ProcessAcpConnection(
            string cliPath,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment,
            string workingDirectory,
            string providerId = "")
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            if (environment is not null)
                foreach (var kvp in environment)
                    startInfo.Environment[kvp.Key] = kvp.Value;

            // After the explicit config env, so anything already set (inherited or configured) wins.
            ApplyNonInteractiveDefaults(startInfo.Environment);

            Diagnostics = new AgentDiagnostics(providerId, cliPath);

            try
            {
                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start ACP agent '{cliPath}'.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The one message that named the program was unreachable in the case it exists for.
                // Process.Start THROWS for a program that is not there - the `??` above only covers a
                // null return, which means an already-running process was reused - so a missing CLI
                // arrived as a bare "The system cannot find the file specified", naming neither the
                // program, the path we looked at, nor the directory. Host-side that reads as "Engine
                // error: The system cannot find the file specified", a sentence with no subject; the
                // FileWriteRefusal lesson, which is that a reader supplies the subject and states it
                // more confidently than we would have.
                throw new InvalidOperationException(DescribeLaunchFailure(ex, cliPath, workingDirectory), ex);
            }

            // Drain the agent's stderr into ours (the engine's stderr lands in engine.log in the
            // VSIX). This is where agents report what they otherwise swallow — e.g. the Claude Code
            // SDK logs MCP server spawn/handshake failures here — and an undrained pipe can stall
            // the child once the buffer fills.
            //
            // Also kept in memory (Diagnostics), because the log is only useful to someone who knows
            // it exists — which is exactly what issue #82 established that a user does not.
            var name = Path.GetFileNameWithoutExtension(cliPath);
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                Diagnostics.Add(e.Data);
                Console.Error.WriteLine($"[{name}] {e.Data}");
            };
            _process.BeginErrorReadLine();

            // A CLI that dies leaves no mark otherwise: the failure surfaces as StreamJsonRpc's
            // "connection lost", which names neither the process nor why it went. An old CLI rejecting
            // its arguments (a clap parse error), a panic, or a missing runtime all land here — and
            // What it does NOT mean is that the agent is gone. kiro-cli.exe is a LAUNCHER for a
            // bundled node agent (image under \Kiro-Cli\), and node survives its parent holding the
            // stdio handles it inherited — measured 2026-08-08: a session answered a brand-new prompt
            // eight seconds after this line was written for it, and the CLI's own stderr kept arriving
            // on the same pipe three minutes later. So this reports A PROCESS, not the session, and
            // the wording has to keep them apart.
            try
            {
                _process.EnableRaisingEvents = true;
                _process.Exited += (_, _) =>
                {
                    int code;
                    try { code = _process.ExitCode; }
                    catch { return; } // raced teardown; nothing useful to say
                    Diagnostics.NoteExit(code);
                    Console.Error.WriteLine(
                        $"[{name}] process exited with code {code} after {_uptime.Elapsed.TotalSeconds:N1}s");
                };
            }
            catch
            {
                // Exit reporting is diagnostic only — a process that refuses to raise the event still
                // runs the session.
            }
        }

        // ONE sink for both directions (issue #211). It used to be two files - "<path>" and
        // "<path>.send" - and reading either alone looks complete while being half the conversation:
        // a request whose response is in the other file looks unanswered, and an outbound prompt looks
        // like it never happened. Created once and shared, so the two directions interleave in the
        // order they crossed the wire.
        private FrameLogSink? _frameSink;

        private FrameLogSink? FrameSink()
        {
            if (Environment.GetEnvironmentVariable("CWKT_ACP_LOG") is not { Length: > 0 } logPath)
                return null;
            return _frameSink ??= FrameLogSink.Open(logPath);
        }

        public Stream Sending =>
            FrameSink() is { } sending
                ? new FrameTeeWriteStream(_process.StandardInput.BaseStream, sending)
                : _process.StandardInput.BaseStream;

        public Stream Receiving =>
            FrameSink() is { } receiving
                ? new FrameTeeReadStream(_process.StandardOutput.BaseStream, receiving)
                : _process.StandardOutput.BaseStream;

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    // Tree-wide: npm-style .cmd shims (e.g. claude-agent-acp) wrap the real node
                    // process; killing only the shim would orphan the agent.
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            _process.Dispose();

            // The tee's owner disposes it - the rule DiagnosticLog states for every tee, and it
            // matters more now there is one file: an undisposed tee means the next run's roll fails
            // silently and two runs braid into one log. It also drains what is queued, since the
            // writes are deliberately off the protocol path and some may still be in flight.
            _frameSink?.Dispose();
        }
    }
}
