using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CodeWicket.Core;

namespace CodeWicket.Core
{
    /// <summary>
    /// A newline-delimited-JSON protocol tee: <b>one</b> file, <b>both</b> directions, one timestamp
    /// per frame (issue #211). Used by the <c>CWKT_ACP_LOG</c> tee and the <c>CWKT_MCP_LOG</c> one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It used to be two unstamped files, and both halves of that cost real mistakes.</b> Reading
    /// the agent→client file alone, three confident and wrong conclusions were drawn in one session:
    /// that a hundred <c>fs/read_text_file</c> requests had leaked (our replies were in the other
    /// file), that a turn had ended (the prompt that opened it was outbound), and a claim about
    /// <em>when</em> something happened, from a file that carried no times at all. The first two are
    /// structurally impossible once both directions share a file: a request whose response you cannot
    /// see looks unanswered, and an outbound prompt you cannot see looks like it never happened.
    /// </para>
    /// <para>
    /// <b>The reason recorded for leaving it unstamped did not survive being checked.</b> The tee was
    /// described as a raw byte stream that text would corrupt — but it has never been verbatim
    /// (<see cref="FrameLogRedaction"/> redacts bearer tokens and elides image base64 in flight), and it has
    /// always been frame-aligned, because those substitutions had to see whole values. The machinery
    /// for stamping at a safe boundary was already here, doing something harder. ACP is
    /// newline-delimited JSON, so a stamp per completed frame cannot land mid-message.
    /// </para>
    /// <para>
    /// <b>The stamp matches <see cref="DiagnosticLog"/>'s</b> so this file lines up against
    /// <c>engine.log</c> — which is what the stamping work in #82 assumed was already possible, its
    /// own note describing the goal as lining that stream up with <c>acp.log</c>.
    /// </para>
    /// <para>
    /// <b>Writes are QUEUED, never performed on the caller's thread</b>, and that is the one part
    /// worth being careful about rather than the format. This sits on the protocol path: the caller is
    /// the stream carrying frames to and from the agent, and an undrained pipe stalls the child once
    /// its buffer fills. The queue is the same shape <c>RenderDiagnosticsLog</c> uses for the same
    /// reason — serial, so frames keep their order, and chained rather than fired off, so two
    /// directions cannot interleave inside one line.
    /// </para>
    /// </remarks>
public sealed class FrameLogSink : IDisposable
    {
        /// <summary>
        /// What THIS PROCESS wrote. The direction that used to be in a separate file nobody read.
        /// </summary>
        /// <remarks>
        /// Defined by who is holding the pen, NOT by which party is the client — because the two users
        /// of this sink sit on opposite ends of that. On the ACP tee we are the client, so outbound is
        /// client→agent; on the MCP tee we are the SERVER, so outbound is server→client. "We wrote it"
        /// is the only reading that stays true in both files, and a reader who takes it as "client to
        /// server" gets the MCP log exactly backwards.
        /// </remarks>
        public const string Outbound = "->";

        /// <summary>What this process read. See <see cref="Outbound"/> for whose direction that is.</summary>
        public const string Inbound = "<-";

        private readonly Stream? _log;
        private readonly object _gate = new object();
        private Task _pending = Task.CompletedTask;

        private FrameLogSink(Stream? log) => _log = log;

        /// <summary>
        /// Opens the tee, or returns a sink that writes nothing. Best-effort throughout: a diagnostic
        /// must never be the thing that breaks the session it is describing.
        /// </summary>
        /// <remarks>
        /// <c>exclusive: true</c> is unchanged in meaning and simpler now there is one file: a second
        /// concurrent session (the throwaway summarize one, say) fails to open it, runs without
        /// teeing, and leaves the active session's log intact rather than the two braiding together.
        /// </remarks>
        /// <remarks>
        /// <b><see cref="FrameLogRedaction"/> is not optional and there is no switch for it.</b> It was
        /// briefly a delegate parameter defaulting to null, which made redaction OPT-IN — and the
        /// default of a knob that decides whether a bearer token reaches disk can only point one way.
        /// The existing tests caught it immediately, which is the only reason it is a paragraph here
        /// rather than a defect: a tee added later would have been written by someone with no reason
        /// to suspect the argument existed. If a caller ever genuinely needs raw frames, it can have a
        /// second factory that says so in its name.
        /// </remarks>
        public static FrameLogSink Open(string logPath) =>
            new FrameLogSink(DiagnosticLog.OpenTee(logPath, exclusive: true));

        /// <summary>
        /// Records one or more COMPLETE frames. The callers buffer to the newline boundary before
        /// calling in, so every frame here is whole — which is what lets each be stamped individually
        /// rather than the buffer being stamped as a lump.
        /// </summary>
        public void Write(string direction, byte[] data, int offset, int count)
        {
            if (_log is null || count <= 0)
                return;

            try
            {
                var text = FrameLogRedaction.Scrub(Encoding.UTF8.GetString(data, offset, count));
                var stamped = Stamp(direction, text, DateTime.Now);
                if (stamped.Length == 0)
                    return;

                var bytes = Encoding.UTF8.GetBytes(stamped);
                lock (_gate)
                {
                    // Chained, so the file sees frames in the order they crossed the wire even though
                    // the two directions are written from different threads.
                    _pending = _pending.ContinueWith(
                        _ => WriteQueued(bytes), TaskScheduler.Default);
                }
            }
            catch
            {
                // Logging is best-effort.
            }
        }

        /// <summary>
        /// Prefixes each frame in <paramref name="text"/> with the time and the direction. Pure, so the
        /// awkward parts — a buffer carrying several frames, a trailing newline, a blank line — are
        /// unit-testable without a file or a process.
        /// </summary>
        public static string Stamp(string direction, string text, DateTime now)
        {
            var when = "[" + now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] "
                + direction + " ";

            var sb = new StringBuilder(text.Length + 32);
            foreach (var line in text.Split('\n'))
            {
                // Split leaves an empty tail after the final newline, and a frame stream is mostly
                // newline-terminated - so this is the ordinary case, not an edge one.
                var frame = line.TrimEnd('\r');
                if (frame.Length == 0)
                    continue;

                sb.Append(when).Append(frame).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Removes what <see cref="Stamp"/> adds; returns the line unchanged when it is not one of
        /// ours, so a log written before this - or by an older build - still reads.
        /// </summary>
        /// <remarks>
        /// <b>Beside the writer on purpose.</b> The format has exactly two participants and they are
        /// the two that must never disagree; a strip living in the host that happens to parse this
        /// file is a second definition of the format, one edit away from being wrong.
        /// <para>The prefix is stripped rather than parsed into its parts: nothing asks for the time
        /// or the direction programmatically today, and a reader returning a tuple nobody used would
        /// be a shape invented for its own sake. Both are there to be READ.</para>
        /// </remarks>
        public static string StripPrefix(string line)
        {
            if (line.Length == 0 || line[0] != '[')
                return line;

            var close = line.IndexOf(']');
            if (close < 0)
                return line;

            var rest = line.Substring(close + 1).TrimStart();
            if (rest.StartsWith(Outbound + " ", StringComparison.Ordinal)
                || rest.StartsWith(Inbound + " ", StringComparison.Ordinal))
            {
                return rest.Substring(Outbound.Length + 1);
            }

            // A stamp with no direction is not one of ours - leave the line alone rather than eat a
            // payload that merely happens to open with a bracket.
            return line;
        }

        private void WriteQueued(byte[] bytes)
        {
            try
            {
                _log!.Write(bytes, 0, bytes.Length);
                _log.Flush();
            }
            catch
            {
                // The session outlives its diagnostics.
            }
        }

        /// <summary>
        /// Drains what is queued and closes the file. <b>The tee's owner must dispose it</b>, or the
        /// next run's roll silently fails and two runs braid into one file — the rule
        /// <see cref="DiagnosticLog"/> already states for every tee.
        /// </summary>
        public void Dispose()
        {
            Task pending;
            lock (_gate)
                pending = _pending;

            try
            {
                pending.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // A queued write that faulted has already swallowed its own error.
            }

            _log?.Dispose();
        }
    }
}
