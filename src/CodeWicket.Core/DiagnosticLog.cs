using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace CodeWicket.Core
{
    /// <summary>
    /// The single retention policy for every diagnostic log we write (engine.log, the ACP/MCP/channel
    /// byte tees, the config-error and run_tests logs). Before this existed each writer chose its own
    /// mode ad hoc — the ACP tee truncated per connection, everything else appended forever, so a
    /// 25-day-old <c>engine.log</c> had reached 11 MB and nothing ever reclaimed it.
    ///
    /// Policy, uniform across all of them:
    /// <list type="bullet">
    /// <item>a run starts fresh — <see cref="StartRun"/> rolls the primary aside to
    /// <c>&lt;name&gt;.yyyyMMdd-HHmmss</c>, so the primary always holds THIS run (what the raw byte
    /// tees want) while prior runs survive a restart (what a field bug report needs);</item>
    /// <item>a write past <see cref="MaxFileBytes"/> rolls mid-run, so no single file grows past the
    /// point of being readable;</item>
    /// <item><see cref="Sweep"/> — after every roll and at host startup — drops anything older than
    /// <see cref="MaxAgeDays"/>, then evicts oldest-first until the directory is under
    /// <see cref="TotalBudgetBytes"/>. Both of those trigger points schedule it through
    /// <see cref="SweepInBackground"/>, never inline: see that method for why.</item>
    /// </list>
    ///
    /// <b>Age and total size are the only things that delete a log.</b> There is deliberately no cap on
    /// the number of rolled generations: a count is an arbitrary proxy for "don't accumulate" that
    /// evicts unconditionally, so a fixed depth of 3 would drop a run from ten minutes ago just because
    /// the window was reopened four times, with the directory sitting at 2 MB. The two sweep limits
    /// measure what actually matters (staleness, disk), so they alone decide. <see cref="MaxFileBytes"/>
    /// is NOT a retention limit — it bounds a single *file* so it stays openable, and rolling it is
    /// what hands the bytes to the sweep.
    ///
    /// That's why the sweep runs on every roll and not just at startup: rolling is the only thing that
    /// creates files, so pairing them keeps the budget binding at all times. Otherwise a long debug
    /// session rolling every 5 MB would accumulate all day with nothing reclaiming until the next
    /// window open.
    ///
    /// Every method is best-effort and never throws: a diagnostic must not break the thing it's
    /// diagnosing.
    /// </summary>
    public static class DiagnosticLog
    {
        /// <summary>
        /// A single log file rolls aside once it passes this size. A readability bound, not a retention
        /// one — see the class remarks.
        /// </summary>
        public const long MaxFileBytes = 5L * 1024 * 1024;

        /// <summary>The sweep deletes anything in the log directory older than this.</summary>
        public const int MaxAgeDays = 7;

        /// <summary>Total size the sweep keeps the log directory under.</summary>
        public const long TotalBudgetBytes = 50L * 1024 * 1024;

        private static readonly object Gate = new object();

        // Scheduling state for SweepInBackground. Its own lock, taken only there and never held
        // across a sweep: Roll runs under Gate, so the one legal order is Gate -> SweepGate.
        private static readonly object SweepGate = new object();
        private static readonly Dictionary<string, PendingRolls> QueuedSweeps =
            new Dictionary<string, PendingRolls>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Where retention activity is reported — one line per pass, carrying its duration (see
        /// <see cref="RetentionSweepResult"/>). Null by default, so a host that sets nothing logs
        /// nothing; the VSIX and the engine both point it at their <c>engine.log</c> sink.
        ///
        /// It exists for the field report we can't reproduce. Retention is the one thing here that
        /// touches every file in a directory, its cost is set by the machine rather than by us
        /// (AV/EDR interception, LocalAppData redirected onto a share, VDI — issue #86), and after
        /// issue #100 most of it happens on a thread nobody is watching. A "the chat window hung"
        /// report with no numbers is unattributable; with this line it takes one grep.
        ///
        /// Assigned once at host startup, before the first sweep is scheduled — a sink set later
        /// misses the startup pass, which is the expensive one.
        /// </summary>
        public static Action<string>? RetentionLog { get; set; }

        // Inline roll cost accumulated while a pass sits queued, so a coalesced burst still reports
        // every roll it stood in for rather than only the one that scheduled it.
        private sealed class PendingRolls
        {
            public int Count;
            public double Milliseconds;

            public void Add(int rolls, double ms)
            {
                Count += rolls;
                Milliseconds += ms;
            }
        }

        /// <summary>
        /// Rolls <paramref name="path"/> so the caller's run starts with an empty primary log. No-op
        /// when the file is missing or already empty, so repeated calls in one run — e.g. two hosts
        /// sharing engine.log — don't litter the directory with empty stamped files.
        /// </summary>
        public static void StartRun(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                lock (Gate)
                {
                    if (LengthOf(path) > 0)
                        Roll(path);
                }
            }
            catch { /* retention is best-effort */ }
        }

        /// <summary>
        /// Appends one <see cref="Stamp"/>ed line, rolling first if the file has reached
        /// <see cref="MaxFileBytes"/>. Opens and closes per call with <see cref="FileShare.ReadWrite"/>
        /// so a second process appending the same log (the extension loaded in both the main and Exp
        /// devenv) can't make either side throw.
        /// </summary>
        public static void AppendLine(string path, string line)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                lock (Gate)
                {
                    EnsureDirectory(path);
                    if (LengthOf(path) >= MaxFileBytes)
                        Roll(path);

                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        // Stamped inside the lock, so the stamps read in the same order as the bytes.
                        // Computing it at the call site would let two concurrent callers (engine
                        // stderr arrives on pool callbacks) write in the opposite order to their
                        // stamps, which makes the log lie about sequence — the one thing it is for.
                        var bytes = Encoding.UTF8.GetBytes(Stamp() + line + Environment.NewLine);
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch { /* logging is best-effort */ }
        }

        /// <summary>
        /// The per-line time prefix. Owned here rather than left to each caller because this type
        /// already owns the log's shape (the lock, the roll, the retention), and because callers that
        /// stamped their own drifted into three different formats — while the highest-volume writer of
        /// all, the agent CLI's stderr relayed through <c>engine.log</c>, carried no time at all. A log
        /// whose lines can't be placed in time can't be read against a hang, a startup phase or the
        /// frames in <c>acp.log</c>.
        ///
        /// <b>It is write time at the sink, not origin time.</b> Anything that crossed a pipe to get
        /// here (the agent's stderr → the engine → the shell) is stamped on receipt, so the value is an
        /// upper bound on when it was said. Close enough to correlate against, and worth knowing before
        /// reading milliseconds off two processes.
        ///
        /// Absolute rather than relative: a rolled log is read on its own, and the date is what lines
        /// it up with a bug report. Local time for the same reason — it is compared against when the
        /// user says it happened.
        /// </summary>
        private static string Stamp() =>
            DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff] ", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Opens a self-rolling write sink for a byte tee, starting a fresh run (see
        /// <see cref="StartRun"/>). Returns null when the log can't be opened, so a tee degrades to
        /// not-teeing rather than taking the session down.
        /// </summary>
        /// <param name="path">Log file to write.</param>
        /// <param name="exclusive">
        /// When true the file is opened <see cref="FileShare.Read"/>, so a second concurrent writer's
        /// open fails outright instead of interleaving its bytes into the same file. The ACP tee needs
        /// this: the throwaway <c>engine/summarize</c> session runs alongside the active one, and two
        /// raw frame streams braided into one file is an unreadable log.
        /// </param>
        public static Stream? OpenTee(string path, bool exclusive)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                lock (Gate)
                {
                    EnsureDirectory(path);
                    if (LengthOf(path) > 0)
                        Roll(path);
                    return new RotatingSink(path, exclusive);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The retention pass — the ONLY thing that deletes a log. Drops anything older than
        /// <see cref="MaxAgeDays"/>, then evicts oldest-first until the directory fits
        /// <see cref="TotalBudgetBytes"/>. Rolled files go before primaries: they're closed by
        /// definition, whereas a primary may be held open by a live writer (in which case the delete
        /// simply fails and the next pass retries).
        ///
        /// Runs after every roll (see <see cref="Roll"/>) and once at host startup — both by way of
        /// <see cref="SweepInBackground"/>. The startup call is what reclaims ORPHANS — logs whose
        /// feature was retired, so no writer will ever open them again and no roll will ever reach
        /// them.
        ///
        /// This entry point is the SYNCHRONOUS one. It stays public for callers that need the pass to
        /// have finished when it returns — the unit tests, and any future host teardown — but a caller
        /// on a UI thread wants <see cref="SweepInBackground"/> instead.
        ///
        /// The age/budget mechanic itself lives in <see cref="RetentionSweep"/>, shared with the diff
        /// scratch dir; what stays here is the part that's specific to logs — the limits, and the
        /// eviction order below.
        /// </summary>
        public static RetentionSweepResult Sweep(string directory) =>
            RetentionSweep.Run(
                directory,
                TimeSpan.FromDays(MaxAgeDays),
                TotalBudgetBytes,
                RolledHistoryFirst);

        /// <summary>
        /// Schedules one <see cref="Sweep"/> of <paramref name="directory"/> on a background thread,
        /// coalescing with a pass already queued for the same directory. This is how every sweep we
        /// trigger ourselves is scheduled; <see cref="Sweep"/> is only called directly by a caller
        /// that needs to observe the result.
        ///
        /// <b>Off-thread because bulk file enumeration and deletion must never run on a UI thread</b>
        /// (issue #100), and that is a rule rather than a measurement result — the cost of touching a
        /// log directory is not ours to predict. On-access AV/EDR scanning hooks both enumerate and
        /// delete, LocalAppData can be caught by folder redirection onto a network share, and our
        /// users are not all on local SSDs (issue #86 is a VDI sluggishness report). A local timing
        /// establishes a floor and says nothing about the ceiling. The shell reached this code from
        /// <c>ChatToolWindow.InitializeCoreAsync</c>, which runs straight-line on the main thread, so
        /// every pass below was landing there: the startup sweep, plus one per roll from
        /// <see cref="StartRun"/> and the two <see cref="OpenTee"/> calls behind it — up to four full
        /// enumerate-and-stat passes over a directory measured at 365 files / 42 MB, per window open.
        ///
        /// It also gets the pass out from under <see cref="Gate"/>. <see cref="Roll"/> is called with
        /// that lock held, so a slow sweep used to block every <see cref="AppendLine"/> in the process
        /// for its whole duration.
        ///
        /// <b>Coalescing does not weaken the after-every-roll guarantee</b> (the reason the roll sweeps
        /// at all — see the class remarks). The queue entry is cleared when the pass STARTS, not when
        /// it finishes, so for any roll there is always a pass whose enumeration begins after it: a
        /// roll landing while an entry is still queued is covered by that pass, and one landing after
        /// the pass has started queues a fresh entry of its own.
        ///
        /// Which also means it <b>rarely fires</b>: measured on a live window open, the pool starts
        /// each pass before the next roll lands, giving six passes rather than one. It is a guard for
        /// a saturated pool, not the thing that fixed issue #100 — the deferral is.
        ///
        /// Every pass reports itself to <see cref="RetentionLog"/> — duration first, since that's what
        /// a hang report is read against.
        ///
        /// Best-effort like everything else here. The fault path needs
        /// <see cref="RetentionSweep.Run"/>'s blanket catch to be removed first — the point of the
        /// catch is that an escaped exception must not fault an unobserved task, which since .NET 4.5
        /// fails silently at finalization rather than crashing (the shape of failure the diff-scratch
        /// sweep hit in issue #88).
        /// </summary>
        public static void SweepInBackground(string directory) => Schedule(directory, rolls: 0, rollMs: 0);

        // Shared by the public entry point and by Roll, which additionally hands over what its own
        // inline File.Move cost so the pass can report the UI-thread half alongside its own.
        private static void Schedule(string directory, int rolls, double rollMs)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Action<Action> schedule;
            lock (SweepGate)
            {
                if (QueuedSweeps.TryGetValue(directory, out var queued))
                {
                    // A pass for this directory is queued and hasn't started; it will see us. Fold
                    // this roll's cost into it so the line it eventually writes accounts for every
                    // roll it stood in for, not just the one that happened to schedule it.
                    queued.Add(rolls, rollMs);
                    return;
                }
                QueuedSweeps[directory] = new PendingRolls { Count = rolls, Milliseconds = rollMs };
                schedule = _sweepScheduler;
            }

            try
            {
                schedule(() =>
                {
                    // Cleared at the START of the pass, not the end — that's what keeps the
                    // after-every-roll guarantee above true.
                    PendingRolls? stood;
                    lock (SweepGate)
                    {
                        QueuedSweeps.TryGetValue(directory, out stood);
                        QueuedSweeps.Remove(directory);
                    }

                    try { Report(LabelFor(directory), Sweep(directory), stood); }
                    catch { /* retention is best-effort */ }
                });
            }
            catch
            {
                // Couldn't even schedule it (pool exhausted, host shutting down). Drop the queue
                // entry or nothing would ever be scheduled for this directory again.
                lock (SweepGate)
                    QueuedSweeps.Remove(directory);
            }
        }

        /// <summary>
        /// Writes one <see cref="RetentionLog"/> line for a pass someone else ran — the diff scratch
        /// sweep, which shares <see cref="RetentionSweep"/> but not these limits. Same line shape, so
        /// one grep covers every scratch directory we reap.
        /// </summary>
        public static void ReportRetention(string label, RetentionSweepResult result) =>
            Report(label, result, null);

        private static void Report(string label, RetentionSweepResult result, PendingRolls? rolls)
        {
            var sink = RetentionLog;
            if (sink is null)
                return;
            try
            {
                var line = "[retention] " + label + ": " + result;
                if (rolls is { Count: > 0 })
                    line += string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        " (+{0} roll{1} inline, {2:N0} ms)",
                        rolls.Count, rolls.Count == 1 ? "" : "s", rolls.Milliseconds);
                sink(line);
            }
            catch { /* a broken sink must not take retention down with it */ }
        }

        // "…\code-wicket\logs" -> "logs". Enough to tell the directories apart in one line, and
        // it keeps a user path out of a log that gets pasted into issues.
        private static string LabelFor(string directory)
        {
            try { return Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)); }
            catch { return "?"; }
        }

        /// <summary>
        /// Where <see cref="SweepInBackground"/> puts the pass. A seam ONLY so the tests can pin that
        /// the caller's thread doesn't run it — the assertion that matters here is a negative ("the
        /// directory has NOT been swept by the time the call returns"), and against a real thread pool
        /// that is a race, not a check. Nothing in the product replaces it.
        /// </summary>
        private static Action<Action> _sweepScheduler = work => Task.Run(work);

        /// <summary>Test hook: swaps <see cref="_sweepScheduler"/> for the lifetime of the returned scope.</summary>
        internal static IDisposable OverrideSweepScheduler(Action<Action> scheduler)
        {
            lock (SweepGate)
            {
                var previous = _sweepScheduler;
                _sweepScheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
                return new SchedulerScope(previous);
            }
        }

        private sealed class SchedulerScope : IDisposable
        {
            private readonly Action<Action> _previous;
            public SchedulerScope(Action<Action> previous) => _previous = previous;
            public void Dispose()
            {
                lock (SweepGate)
                    _sweepScheduler = _previous;
            }
        }

        /// <summary>Rolled history first (closed by definition), each group oldest-first.</summary>
        private static int RolledHistoryFirst(FileInfo a, FileInfo b)
        {
            var byKind = IsRolled(b.Name).CompareTo(IsRolled(a.Name));
            return byKind != 0 ? byKind : a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
        }

        // A rolled file — <name>.yyyyMMdd-HHmmss, optionally -N when two rolls land in one second.
        // Recognising these is what lets the sweep evict history before live primaries.
        private static bool IsRolled(string fileName)
        {
            var dot = fileName.LastIndexOf('.');
            if (dot < 0)
                return false;

            var suffix = fileName.Substring(dot + 1);
            // yyyyMMdd '-' HHmmss = 8 + 1 + 6.
            if (suffix.Length < 15 || suffix[8] != '-')
                return false;
            for (var i = 0; i < 15; i++)
            {
                if (i == 8)
                    continue;
                if (suffix[i] < '0' || suffix[i] > '9')
                    return false;
            }
            if (suffix.Length == 15)
                return true;

            // …-N collision counter.
            if (suffix[15] != '-' || suffix.Length == 16)
                return false;
            for (var i = 16; i < suffix.Length; i++)
            {
                if (suffix[i] < '0' || suffix[i] > '9')
                    return false;
            }
            return true;
        }

        // Moves the primary aside under a timestamped name, then schedules a sweep — rolling is the
        // only thing that creates files, so this is where the age/budget limits get their chance to
        // bind. No generation shuffle: nothing is renamed twice and nothing is dropped by position.
        //
        // The move stays inline: it is ordered against the caller's own next act (StartRun and
        // OpenTee both hand back a sink that is written to immediately, and AppendLine appends right
        // after), so it is the one part that can't be deferred. The sweep has no such ordering, and
        // it is the expensive half — see SweepInBackground.
        private static void Roll(string path)
        {
            // Timed because this is the part that stayed on the caller's thread — the sweep it
            // schedules reports the two side by side, so a hang report can be attributed to the half
            // it actually came from rather than to "retention" in general.
            var inline = Stopwatch.StartNew();

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = path + "." + stamp;
            for (var n = 1; File.Exists(target) && n < 1000; n++)
                target = path + "." + stamp + "-" + n;

            try
            {
                if (File.Exists(path))
                    File.Move(path, target);
            }
            catch
            {
                // Locked (another devenv mid-write): it keeps its current name and the next roll
                // retries. Still sweep below — the directory may be over budget regardless.
            }

            inline.Stop();

            var dir = Path.GetDirectoryName(path);
            if (dir != null && dir.Length > 0)
                Schedule(dir, rolls: 1, rollMs: inline.Elapsed.TotalMilliseconds);
        }

        private static long LengthOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void EnsureDirectory(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && dir.Length > 0)
                    Directory.CreateDirectory(dir);
            }
            catch { /* the open below will fail and degrade to no logging */ }
        }

        /// <summary>
        /// Write-only sink that rolls itself once it passes <see cref="MaxFileBytes"/>. Rolling closes
        /// the handle first, so it works even under the exclusive share mode.
        /// </summary>
        private sealed class RotatingSink : Stream
        {
            private readonly string _path;
            private readonly FileShare _share;
            private readonly object _gate = new object();
            private FileStream? _stream;
            private long _written;

            public RotatingSink(string path, bool exclusive)
            {
                _path = path;
                _share = exclusive ? FileShare.Read : FileShare.ReadWrite;
                _stream = Open();
            }

            private FileStream Open() =>
                new FileStream(_path, FileMode.Create, FileAccess.Write, _share);

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0)
                    return;
                lock (_gate)
                {
                    if (_stream is null)
                        return;
                    try
                    {
                        if (_written + count > MaxFileBytes)
                        {
                            _stream.Dispose();
                            _stream = null;
                            lock (Gate)
                                Roll(_path);
                            _stream = Open();
                            _written = 0;
                        }

                        _stream.Write(buffer, offset, count);
                        _written += count;
                    }
                    catch
                    {
                        // A failed roll/write disables the sink for the rest of the run rather than
                        // throwing into the byte stream it's shadowing.
                        try { _stream?.Dispose(); } catch { }
                        _stream = null;
                    }
                }
            }

            public override void Flush()
            {
                lock (_gate)
                {
                    try { _stream?.Flush(); } catch { /* best-effort */ }
                }
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    lock (_gate)
                    {
                        try { _stream?.Dispose(); } catch { /* ignore */ }
                        _stream = null;
                    }
                }
                base.Dispose(disposing);
            }
        }
    }
}
