using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace CodeWicket.Core
{
    /// <summary>
    /// Times the phases of a host's startup into a diagnostic log, and — the part that matters —
    /// reports a phase that is <i>still running</i> from a thread that isn't the one running it.
    ///
    /// <b>Why this exists.</b> A wedged UI thread writes nothing, so the log simply stops and its
    /// silence names no phase. Every suspect for one — a cold MEF composition cache after a VS
    /// update, a first directory walk over a network- or sync-backed repo, endpoint security
    /// inspecting either — is invisible to us and expensive only on the reporter's machine, which is
    /// exactly the shape of problem that has to diagnose itself in the field.
    ///
    /// <b>What it did NOT solve, recorded so nobody over-trusts it.</b> This was built for a specific
    /// "the whole IDE freezes when I switch to the chat tab" report. That report was later explained,
    /// and the cause was none of the suspects above: it was an assembly-name conflict with another
    /// extension (issue #103), and it was a <i>crash</i> rather than a hang — an unhandled
    /// <c>FileNotFoundException</c> on a dispatcher tick, which leaves a dump, not silence. Nothing
    /// here would have found it: rendering happens long after the last phase this times. The
    /// diagnostic that catches that class of failure is <c>MarkdownRenderFirewall</c>'s report.
    /// The hang gap is still real and still unattributable — the same reporter described a freeze as
    /// well as a crash and the two were never shown to be one event — but this is instrumentation
    /// waiting for a failure, not a fix for one we have diagnosed.
    ///
    /// Two properties follow from that, and they are the whole design:
    ///
    /// <list type="number">
    /// <item><b>A phase is written when it ENDS, not summarised at the finish.</b> A single "startup
    /// took N ms" line needs startup to have finished, which in the case being diagnosed is precisely
    /// what does not happen. Per-phase lines mean the log's last entry is always the last thing that
    /// completed, so even with the watchdog off the failure is bracketed.</item>
    /// <item><b>The stall report comes off a pool timer, never from the caller.</b> The phase that
    /// hangs is by definition the one that cannot report itself — anything the blocked thread was
    /// going to write is written after it unblocks, i.e. never. So <see cref="Begin"/> arms a
    /// <see cref="Timer"/>, and a phase still open after <see cref="DefaultStallInterval"/> is
    /// reported repeatedly for as long as it stays open. An indefinite hang therefore produces an
    /// indefinite series of lines naming it, which is the artifact the report needed and the log
    /// could not give.</item>
    /// </list>
    ///
    /// The repetition is deliberate rather than a one-shot warning: it distinguishes "slow, then
    /// finished" from "never came back", and it puts an upper bound on the hang in the log even when
    /// the process is killed. At one short line per interval it cannot meaningfully grow a log — the
    /// 5 MB roll in <see cref="DiagnosticLog"/> is years away at that rate.
    ///
    /// <b>Durations are milliseconds everywhere, including the multi-minute ones.</b> The numbers'
    /// job is to be compared — a fast machine's log against a slow one's — and a unit that switches
    /// at a threshold makes that a conversion exercise on the one line where it matters most.
    ///
    /// <b>What it still cannot see.</b> The watchdog reaches the log through the caller's sink, and
    /// <see cref="DiagnosticLog.AppendLine"/> serialises every writer in the process behind one lock.
    /// So a thread wedged <i>inside</i> a write takes the watchdog down with it — which is fine for
    /// the failures this was built for (they are all slow work the UI thread was made to wait on,
    /// holding no log lock), and worth knowing before reading silence as "the phase completed".
    ///
    /// Best-effort like everything else in this file's neighbourhood: a sink that throws is swallowed,
    /// a null sink writes nothing, and a mismatched <see cref="Begin"/>/<see cref="End"/> pair is
    /// tolerated. A diagnostic must not break the thing it is diagnosing.
    /// </summary>
    /// <remarks>
    /// Host-agnostic on purpose: it holds a stopwatch, a timer and an <see cref="Action{T}"/> sink, so
    /// the VSIX shell, the engine and the Desktop host can all use the same line shape. The sink has
    /// to be live before the first <see cref="Begin"/> — see <c>ChatToolWindow.InitializeCoreAsync</c>,
    /// where opening the log is deliberately the first thing that happens for this reason.
    /// </remarks>
    public sealed class StartupTimeline : IDisposable
    {
        /// <summary>
        /// How long a phase may run before the watchdog starts reporting it, and how often it repeats
        /// after that. Short enough that a hang is named while the user is still watching it, long
        /// enough that no ordinary startup phase trips it.
        /// </summary>
        public static readonly TimeSpan DefaultStallInterval = TimeSpan.FromSeconds(5);

        private readonly Action<string>? _log;
        private readonly string _label;
        private readonly TimeSpan _stallInterval;
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly object _gate = new object();

        private string? _phase;
        private long _phaseStartedAtMs;
        private Timer? _watchdog;
        private bool _disposed;

        /// <param name="log">
        /// Where lines go — typically the host's <c>engine.log</c> sink. Null disables the timeline
        /// entirely (no timer is ever armed), so a host that wants no startup diagnostics pays nothing.
        /// </param>
        /// <param name="label">
        /// Bracketed prefix on every line, matching the <c>[retention]</c> convention so one grep
        /// covers a whole subsystem.
        /// </param>
        /// <param name="stallInterval">
        /// Overrides <see cref="DefaultStallInterval"/>. Exists for the tests, which cannot wait five
        /// seconds per assertion.
        /// </param>
        public StartupTimeline(Action<string>? log, string label = "startup", TimeSpan? stallInterval = null)
        {
            _log = log;
            _label = string.IsNullOrWhiteSpace(label) ? "startup" : label;
            var interval = stallInterval ?? DefaultStallInterval;
            _stallInterval = interval > TimeSpan.Zero ? interval : DefaultStallInterval;
        }

        /// <summary>Time since the timeline was created — i.e. since startup began.</summary>
        public TimeSpan Elapsed => _total.Elapsed;

        /// <summary>
        /// Starts timing <paramref name="phase"/> and arms the stall watchdog for it. Ends any phase
        /// still open, so a caller that forgets an <see cref="End"/> loses that phase's line rather
        /// than leaving a timer running against a name that has moved on.
        /// </summary>
        public void Begin(string phase)
        {
            if (_log is null)
                return;

            Timer? previous = null;
            lock (_gate)
            {
                if (_disposed)
                    return;
                previous = _watchdog;
                _watchdog = null;
                _phase = string.IsNullOrWhiteSpace(phase) ? "(unnamed)" : phase;
                _phaseStartedAtMs = _total.ElapsedMilliseconds;
            }

            previous?.Dispose();

            // Armed outside the lock: the callback takes _gate, and Timer's construction can invoke it
            // on the pool before the constructor returns if the due time has already elapsed.
            var timer = new Timer(ReportStall, null, _stallInterval, _stallInterval);
            var stale = false;
            lock (_gate)
            {
                if (_disposed || _phase is null)
                    stale = true;
                else
                    _watchdog = timer;
            }
            if (stale)
                timer.Dispose();
        }

        /// <summary>
        /// Ends the open phase, writing its duration. <paramref name="detail"/> is appended in
        /// parentheses for the phases whose <i>size</i> explains their cost (a file count, a transcript
        /// size) — a number nobody can infer from the duration alone. No-op when no phase is open.
        /// </summary>
        public void End(string? detail = null)
        {
            if (_log is null)
                return;

            string? phase;
            long elapsed;
            Timer? timer;
            lock (_gate)
            {
                phase = _phase;
                _phase = null;
                elapsed = _total.ElapsedMilliseconds - _phaseStartedAtMs;
                timer = _watchdog;
                _watchdog = null;
            }

            timer?.Dispose();
            if (phase is null)
                return;

            Write(string.IsNullOrWhiteSpace(detail)
                ? Measure(phase, elapsed)
                : Measure(phase, elapsed) + " (" + detail + ")");
        }

        /// <summary>
        /// Writes the run total — the line whose ABSENCE is the signal, since it is only ever reached
        /// by a startup that finished. <paramref name="what"/> names the milestone reached, because a
        /// host reaches more than one worth timing (the window is usable well before the transcript
        /// has been restored into it).
        /// </summary>
        public void Total(string what) =>
            Write(Measure(what, _total.ElapsedMilliseconds) + " total");

        /// <summary>
        /// Stops the watchdog, whatever state it is in. The reason this type is
        /// <see cref="IDisposable"/> at all: a phase that throws never reaches its <see cref="End"/>,
        /// and without this its timer would keep naming it long after the host had shown the failure
        /// and moved on. Belongs in a <c>finally</c>.
        /// </summary>
        public void Dispose()
        {
            Timer? timer;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _phase = null;
                timer = _watchdog;
                _watchdog = null;
            }
            timer?.Dispose();
        }

        // Runs on a pool thread while the phase's own thread is — in the case this exists for —
        // making no progress at all. Reads the phase name under the lock and writes outside it, so a
        // slow sink can never become the thing that blocks End.
        private void ReportStall(object? state)
        {
            string? phase;
            long elapsed;
            lock (_gate)
            {
                // Belt and braces with the Dispose() in End: either alone silences the watchdog, and
                // both are wanted. Disposing a Timer does not recall a callback the pool has already
                // dispatched, so without this check a phase could still be reported as stalled a beat
                // after it completed — a line contradicting the one above it, in the log someone is
                // reading precisely because they don't yet know what finished.
                if (_disposed || _phase is null)
                    return;
                phase = _phase;
                elapsed = _total.ElapsedMilliseconds - _phaseStartedAtMs;
            }

            Write(phase + ": still running after " + Milliseconds(elapsed));
        }

        private static string Measure(string what, long elapsedMs) =>
            what + ": " + Milliseconds(elapsedMs);

        private static string Milliseconds(long value) =>
            value.ToString(CultureInfo.InvariantCulture) + " ms";

        private void Write(string line)
        {
            var log = _log;
            if (log is null)
                return;
            try { log("[" + _label + "] " + line); }
            catch { /* a diagnostic must never take startup down */ }
        }
    }
}
