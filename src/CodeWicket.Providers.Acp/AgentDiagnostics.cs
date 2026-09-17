using System;
using System.Collections.Generic;
using System.Text;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Everything we can say about the agent process, held for the moment something fails.
    ///
    /// <para><b>Why this exists (issue #82).</b> A user on kiro-cli 2.0.1 got "dispatch error" in the
    /// chat and nothing else. The CLI's own account of itself was on disk the whole time — its stderr
    /// is relayed into <c>engine.log</c> unconditionally — but nothing in the failing message said so,
    /// and the two settings that sound like they would help ("Log agent protocol frames", "Log engine channel bytes")
    /// both tee a different stream. So the fix is not to capture more; it is to put what we already
    /// have in front of the person reading the error.</para>
    ///
    /// <para><b>What this deliberately does NOT do: attribute.</b> An earlier design watermarked the
    /// stderr stream at request-send so an error could quote "the lines that arrived during the failing
    /// call". That machinery fakes a causal link the transport cannot support — stderr and stdout are
    /// separate pipes with separate buffering, so "the line before the error" is not a thing we can
    /// establish — and it then has to disclaim the link in the label anyway. Measured on real logs the
    /// premise was wrong too: agent stderr is a trickle (16 lines / 1.2 KB inside a 2.6 MB
    /// <c>engine.log</c>), so there is nothing to select from. We keep the session's output whole and
    /// label it for exactly what it is.</para>
    ///
    /// <para>What it turns out to be is <b>session context</b> rather than error cause — which auth
    /// path is live, whether the MCP subsystem came up, how many events the last turns produced. That
    /// is worth reading beside any failure, and it needs no causal claim to justify.</para>
    ///
    /// <para>Thread-safe: <see cref="Add"/> runs on process-pool callbacks while a turn reads.</para>
    /// </summary>
    internal sealed class AgentDiagnostics
    {
        /// <summary>
        /// Display bound, not a correlation device. The healthy case measured at 16 lines, so this is
        /// slack for the pathological one — an old or crashing CLI emitting a panic with a backtrace,
        /// or retrying in a loop. When it bites we say so and name the log holding the rest, because a
        /// silently truncated tail reads as the whole story.
        /// </summary>
        internal const int MaxLines = 400;

        /// <summary>Companion bound for a CLI that writes few but enormous lines (a serialized payload
        /// per line). Line count alone would let one of those fill the panel.</summary>
        internal const int MaxChars = 64 * 1024;

        private readonly object _gate = new object();
        private readonly Queue<string> _lines = new Queue<string>();
        private int _chars;
        private int _dropped;
        private int? _exitCode;
        private string? _version;

        public AgentDiagnostics(string providerId, string cliPath)
        {
            ProviderId = providerId ?? string.Empty;
            CliPath = cliPath ?? string.Empty;
        }

        /// <summary>The backend id this process is serving (e.g. "kiro").</summary>
        public string ProviderId { get; }

        /// <summary>The executable we launched, as configured — which is not always what ran: an npm
        /// bin shim wraps node, and the Claude adapter spawns its own vendored binary.</summary>
        public string CliPath { get; }

        /// <summary>
        /// What the agent says it is, e.g. "kiro-cli-chat 2.13.0" or
        /// "@agentclientprotocol/claude-agent-acp 0.63.0". Null until known, and it can stay null —
        /// see <see cref="AgentVersionProbe"/> for why this is best-effort.
        ///
        /// This is the single field that would have closed issue #82 on the first screenshot: the
        /// report was resolved by upgrading 2.0.1 → 2.16.0, and the version appeared in no message and
        /// no log.
        ///
        /// <para>Two writers, and the precedence between them is deliberate. Setting this directly is
        /// for what the agent said about ITSELF at <c>initialize</c>, which is authoritative — it is
        /// the thing on the other end of the pipe. <see cref="SetVersionIfUnknown"/> is for the
        /// <see cref="AgentVersionProbe"/> fallback, which runs a SEPARATE process and so can disagree
        /// (the Claude adapter spawns its own vendored binary, so the two are genuinely different
        /// programs). The wire wins; the probe only fills a hole.</para>
        /// </summary>
        public string? Version
        {
            get { lock (_gate) return _version; }
            set { lock (_gate) _version = value; }
        }

        /// <summary>Records a probed version, but never over the top of one the agent stated itself —
        /// the probe can land after <c>initialize</c> has already supplied the better answer.</summary>
        public void SetVersionIfUnknown(string version)
        {
            if (string.IsNullOrEmpty(version))
                return;
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_version))
                    _version = version;
            }
        }

        /// <summary>
        /// The agent's OWN log directory, when it volunteers one (Kiro publishes it at
        /// <c>initialize</c> under <c>_meta.kiro.logging.logDir</c>). Worth relaying verbatim: it is
        /// the one diagnostic surface we neither own nor duplicate, and a user who has exhausted our
        /// logs has somewhere left to look.
        /// </summary>
        public string? LogDirectory { get; set; }

        /// <summary>Set once the process is known to have exited. Null while it is alive — and null is
        /// NOT "exited cleanly", so it must never be rendered as an exit code of 0.</summary>
        public int? ExitCode
        {
            get { lock (_gate) return _exitCode; }
        }

        /// <summary>Records one line the agent wrote to its error stream, evicting oldest-first.</summary>
        /// <summary>
        /// Told about every stderr line as it arrives, so a backend's complaint can be surfaced when
        /// it happens rather than only when something later fails (issue #208).
        /// </summary>
        /// <remarks>
        /// An observer beside the buffer, the shape <c>McpToolServer</c> already uses beside its log
        /// sink. It exists because the buffer alone is a dead letter box: nothing read it until a
        /// SessionError attached it to the error panel, and the incident this fixes never produced
        /// one — the CLI was refused eight times, retried internally, and the session stayed up.
        /// <para>Called under no lock and on a process-pool callback, so a handler must be cheap and
        /// must not throw; it is invoked outside the gate below precisely so a slow one cannot block
        /// the drain and stall the child's pipe.</para>
        /// </remarks>
        public Action<string>? LineObserver { get; set; }

        public void Add(string line)
        {
            if (line is null)
                return;

            try
            {
                LineObserver?.Invoke(line);
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the session it is describing.
            }

            lock (_gate)
            {
                _lines.Enqueue(line);
                _chars += line.Length + 1;
                while (_lines.Count > MaxLines || (_chars > MaxChars && _lines.Count > 1))
                {
                    _chars -= _lines.Dequeue().Length + 1;
                    _dropped++;
                }
            }
        }

        /// <summary>Records the process's exit. Idempotent — the first observation wins, since a
        /// second reading of <c>ExitCode</c> after teardown says nothing new.</summary>
        public void NoteExit(int exitCode)
        {
            lock (_gate)
                _exitCode ??= exitCode;
        }

        /// <summary>
        /// The whole panel: the hard facts first, then the agent's own output under a heading that
        /// states what it is.
        ///
        /// <para>Always renders at least the identity line, including when the version isn't known —
        /// "version unknown" is itself an answer, and one the reader needs, because it distinguishes
        /// "the probe hasn't landed / this CLI won't say" from a version we simply forgot to show. The
        /// nullable return is a guard for a degenerate instance, not a case the callers produce.</para>
        /// </summary>
        /// <param name="errorCode">The JSON-RPC error code, when the failure carried one. Included
        /// because a code plus the agent's version is often enough to match a backend's own changelog
        /// or issue tracker without reproducing anything.</param>
        /// <param name="stack">Our own exception detail. Last, and clearly ours: it is the least
        /// interesting part for a backend failure and the most interesting one for a crash here.</param>
        public string? Describe(int? errorCode, string? stack)
        {
            string? output;
            int lines, dropped;
            lock (_gate)
            {
                lines = _lines.Count;
                dropped = _dropped;
                output = lines == 0 ? null : string.Join(Environment.NewLine, _lines);
            }

            var sb = new StringBuilder();

            var name = CliName();
            if (Version is { Length: > 0 } version)
                sb.Append(name).Append(": ").AppendLine(version);
            else
                sb.Append(name).AppendLine(": version unknown");

            if (CliPath is { Length: > 0 } path && !string.Equals(path, name, StringComparison.OrdinalIgnoreCase))
                sb.Append("path: ").AppendLine(path);

            if (errorCode is int code)
                sb.Append("JSON-RPC error code: ").AppendLine(code.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // "the process we launched", NOT "the agent". kiro-cli.exe is a LAUNCHER for a bundled
            // node agent, and the two have separate lifetimes: kill the launcher and node keeps the
            // inherited stdio pipes and serves the session anyway — measured 2026-08-08, where a
            // session answered a brand-new prompt eight seconds after this exit was recorded, and the
            // CLI's own stderr kept arriving on the same pipe three minutes later. So the exit is a
            // fact about a process, and only evidence about the SESSION when read next to the failure
            // it is printed under. Only rendered when there has been an exit: "still running" is the
            // normal case for a turn failure, and saying it every time trains the reader to skip the
            // block.
            if (ExitCode is int exit)
                sb.Append("launcher process exited with code ")
                  .AppendLine(exit.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (LogDirectory is { Length: > 0 } logDir)
                sb.Append("agent's own logs: ").AppendLine(logDir);

            if (output is not null)
            {
                sb.AppendLine();
                sb.Append("--- ").Append(name).Append(" output this session (").Append(lines).Append(
                    lines == 1 ? " line" : " lines");
                if (dropped > 0)
                    sb.Append(", ").Append(dropped).Append(" earlier dropped");
                sb.AppendLine(") ---");
                // The label carries the whole claim: this is what the CLI said, not why it failed.
                sb.AppendLine("Everything the CLI wrote to its error stream since this session started.");
                sb.AppendLine("Not necessarily related to the error above. Full copy in engine.log.");
                sb.AppendLine();
                sb.AppendLine(output);
            }

            if (stack is { Length: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("--- code-wicket exception detail ---");
                sb.AppendLine(stack);
            }

            var text = sb.ToString().TrimEnd();
            return text.Length == 0 ? null : text;
        }

        /// <summary>
        /// A short one-liner for the error MESSAGE itself, where there is room for one fact and no
        /// expander to put the rest in — the session-start path, whose failure crosses the engine wire
        /// as a plain exception message. The version is the fact chosen, because it is the one that
        /// resolved issue #82.
        /// </summary>
        public string? ShortSuffix()
        {
            var version = Version;
            if (string.IsNullOrEmpty(version))
                return null;
            return ExitCode is int exit
                ? $"{version}, exited with code {exit}"
                : version;
        }

        /// <summary>The executable's bare name, for headings: "kiro-cli", not the full path.</summary>
        private string CliName()
        {
            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(CliPath);
                return string.IsNullOrEmpty(name) ? (ProviderId is { Length: > 0 } id ? id : "agent") : name;
            }
            catch
            {
                return ProviderId is { Length: > 0 } id ? id : "agent";
            }
        }
    }
}
