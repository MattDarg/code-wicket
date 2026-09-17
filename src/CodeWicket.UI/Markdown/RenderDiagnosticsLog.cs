using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CodeWicket.Core;
using CodeWicket.Shell;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// The chat pane's rendering diagnostics log — <c>logs/render.log</c> — and the once-per-process
    /// <c>[render-env]</c> header that says what machine the numbers in it were taken on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not markdown-specific.</b> Markdown streaming is the first thing measured here
    /// (issue #86) and is currently the only one, but a render problem in this pane need not be a
    /// markdown one, and a log named after one feature is a log the next investigation writes a second
    /// copy of. Per-feature traces get their own line prefix — <c>[md-cost]</c>, <c>[md-render]</c> — and
    /// share the file, the environment header and the retention policy.
    /// </para>
    /// <para>
    /// <b>The sink is installed by the HOST, and nothing is written until it is</b> (same shape as
    /// <see cref="DiagnosticLog.RetentionLog"/>). Not a style choice: <c>ExtensionConfig.LogDirectory</c>
    /// is NOT covered by <c>ExtensionConfig.RedirectTo</c> — that redirects config and attachments, not
    /// logs — and the unit tests construct real viewers and render real markdown through them. A writer
    /// that switched itself on from inside this library would therefore put test output in the
    /// developer's own log directory. A host is the only place where the decision can be made with the
    /// redirects already applied.
    /// </para>
    /// <para>
    /// <b>Opt-in, via <c>ExtensionConfig.LogRendering</c>.</b> It measures on the UI thread and appends
    /// to a file whose cost belongs to the machine rather than to us (AV/EDR interception, LocalAppData
    /// redirected onto a share, VDI — see <see cref="DiagnosticLog"/>), which is precisely the
    /// environment it exists to investigate. So it is asked for, like the other two diagnostic tees. The
    /// price is that a field report needs a round trip before the numbers exist; the alternative was
    /// paying that cost on every install forever to save it.
    /// </para>
    /// <para>
    /// <b>What it costs while enabled</b>, since the point is measuring a machine that is already
    /// struggling: two <c>Stopwatch</c> reads and a list add per render, one dispatcher post per render
    /// (the <c>frame</c> probe), a 2s timer per streaming viewer, and one file append per MESSAGE —
    /// **queued, never performed on the caller's thread**, see <see cref="Post"/>. With no sink: one
    /// static reference test per render.
    /// </para>
    /// </remarks>
    public static class RenderDiagnosticsLog
    {
        private static readonly object Gate = new object();
        private static Action<string>? _sink;
        private static bool _envWritten;

        // The write queue. Its own lock, held only long enough to chain — never across a write, or the
        // UI thread could block on the IO this exists to move off it.
        private static readonly object WriteGate = new object();
        private static Task _pendingWrites = Task.CompletedTask;

        /// <summary>
        /// Where render diagnostic lines go. Null (the default) switches the whole thing off, including
        /// the per-render probes — see the remarks. Setting it re-arms the <c>[render-env]</c> header.
        /// </summary>
        public static Action<string>? Sink
        {
            get => _sink;
            set
            {
                lock (Gate)
                {
                    _sink = value;
                    _envWritten = false;
                }
            }
        }

        /// <summary>Whether to measure at all. Read once per render, so it is deliberately a field test.</summary>
        internal static bool Enabled => _sink is not null;

        /// <summary>Points the log at <c>logs/render.log</c>, so it rolls and is swept like every other.</summary>
        public static void EnableFileLog()
        {
            var file = LogFile;
            Sink = line => DiagnosticLog.AppendLine(file, line);
        }

        internal static string LogFile => Path.Combine(ExtensionConfig.LogDirectory, "render.log");

        /// <summary>
        /// Identifies which viewer a line belongs to, shared by every trace in this file so they can
        /// never disagree about identity. A container is RECYCLED, so this is the identity of the host
        /// viewer rather than of the message — see <see cref="MarkdownRenderCost"/> on episodes.
        /// </summary>
        internal static string ViewerId(object viewer) =>
            viewer.GetHashCode().ToString("x8", CultureInfo.InvariantCulture);

        /// <summary>Writes one finished message's costs.</summary>
        internal static void Write(string viewerId, MarkdownRenderEpisode episode) =>
            WriteLine(episode.Format(viewerId));

        /// <summary>
        /// Writes one already-formatted trace line, preceded by the environment header if it has not
        /// been taken yet. Best-effort and never throws: a diagnostic must not break the thing it is
        /// diagnosing, and this one sits on the streaming render and scroll paths.
        /// </summary>
        /// <remarks>
        /// The entry point every trace in this file shares — <c>[md-cost]</c>, <c>[realise]</c> — so none
        /// of them can end up with its own idea of when the header goes out or which thread the append
        /// happens on. Lines are FORMATTED by the caller, on the caller's thread, and WRITTEN elsewhere.
        /// The formatting side has to stay on the UI thread: <c>RenderCapability.Tier</c> is a property of
        /// the calling thread's rendering stack, so reading it from the pool would answer about the wrong
        /// thread. The writing side must not: see <see cref="Post"/>.
        /// </remarks>
        internal static void WriteLine(string line)
        {
            var sink = _sink;
            if (sink is null)
                return;

            try
            {
                // Null after the first call, so the header leads the file and appears exactly once —
                // and it is queued ahead of this episode because the queue is ordered. Guarded
                // separately because it is the only part of this that reads the WINDOWING stack: the
                // header is decoration and the measurement is the payload, so a host or a thread that
                // cannot answer for a render tier must cost us the header alone.
                string? environment = null;
                try { environment = TakeEnvironmentLine(); }
                catch { /* header is best-effort even by the standards of a diagnostic */ }

                if (environment is not null)
                    Post(sink, environment);
                Post(sink, line);
            }
            catch { /* diagnostics are best-effort */ }
        }

        /// <summary>
        /// Hands a line to the sink on a pool thread, in order, one at a time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The append may not run on the caller's thread, and that is the whole point of this method.</b>
        /// Callers are on the UI thread, and <see cref="DiagnosticLog.AppendLine"/> opens, writes and
        /// closes a file per call under a process-wide lock — so its cost belongs to the machine rather
        /// than to us (AV/EDR interception, LocalAppData redirected onto a share, VDI), which is exactly
        /// the environment this log exists to investigate. Writing inline would put unbounded machine-priced
        /// IO on the UI thread of the machine already reported as slow, and would additionally let a slow
        /// sweep or another process's write to the same lock block a render. It is the same rule that
        /// keeps the retention sweep off this thread (issues #100 and #88), applied to the writer.
        /// </para>
        /// <para>
        /// <b>Serial, not fire-and-forget</b>: each write is chained behind the last, so lines keep the
        /// order they were queued in — the environment header stays first, and episodes stay in sequence.
        /// A parallel <c>Task.Run</c> per line would race them into the file in an order that is not the
        /// order they happened. Pinned by <c>RenderDiagnosticsLogTests</c>.
        /// </para>
        /// <para>
        /// <b><see cref="TaskScheduler.Default"/> is named explicitly</b> rather than inherited: a
        /// continuation scheduled from inside a task running on a UI scheduler would come straight back
        /// to the thread this exists to keep off.
        /// </para>
        /// <para>
        /// The consequence to know when reading the file: <see cref="DiagnosticLog"/> stamps at WRITE
        /// time, which is now a little after the message finished. Ordering is preserved, so it can only
        /// compress a gap, never invert one — and <see cref="Flush"/> exists so a host can drain the
        /// queue at teardown rather than losing the last line to process exit.
        /// </para>
        /// </remarks>
        private static void Post(Action<string> sink, string line)
        {
            lock (WriteGate)
                _pendingWrites = _pendingWrites.ContinueWith(
                    _ =>
                    {
                        try { sink(line); }
                        catch { /* diagnostics are best-effort */ }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
        }

        /// <summary>
        /// Waits for queued lines to reach the sink, for hosts to call at teardown. Returns false if the
        /// queue did not drain in time — the caller is shutting down either way, so a stuck disk costs
        /// the last line rather than the exit.
        /// </summary>
        /// <remarks>
        /// Safe to call from the UI thread: every queued write runs on the pool
        /// (<see cref="TaskScheduler.Default"/>), so nothing being waited on needs this thread back.
        /// </remarks>
        public static bool Flush(int timeoutMs = 2000)
        {
            Task pending;
            lock (WriteGate)
                pending = _pendingWrites;

            try { return pending.Wait(timeoutMs); }
            catch { return false; }
        }

        /// <summary>
        /// The header that separates the two candidate causes behind issue #86, or null if it has
        /// already been taken. Per-message lines say what a render cost; only this says whether the
        /// machine is compositing in software, whether the pixels are being shipped to a remote session
        /// at all, and what the throttle was set to while those numbers were taken — none of which is
        /// recoverable from the lines themselves, and all of which changes what they mean.
        /// </summary>
        private static string? TakeEnvironmentLine()
        {
            lock (Gate)
            {
                if (_envWritten)
                    return null;
                _envWritten = true;
            }

            // The raw property is tier << 16, so 2 is "full hardware acceleration" and 0 is software
            // rendering — a GPU-less VDI's shape. This is what rules the compositor in or out.
            var tier = RenderCapability.Tier >> 16;

            return "[render-env] tier=" + tier.ToString(CultureInfo.InvariantCulture)
                + " remote=" + SystemParameters.IsRemoteSession
                + " cpus=" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)
                + " mdMinInterval=" + Interval(MarkdownText.MinRenderInterval)
                + " mdMaxInterval=" + Interval(MarkdownText.MaxRenderInterval)
                + " runtime=" + RuntimeInformation.FrameworkDescription
                + " | all durations ms; build is exact, frame is an upper bound (whatever else the"
                + " dispatcher ran between the document assignment and the post-layout callback lands"
                + " in it); duty = share of wall-clock the render path held the UI thread";
        }

        private static string Interval(TimeSpan value) =>
            value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "ms";
    }
}
