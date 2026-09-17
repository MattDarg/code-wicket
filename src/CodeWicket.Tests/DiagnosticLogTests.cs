using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins the shared log retention policy. Before <see cref="DiagnosticLog"/> every writer chose its
    /// own mode ad hoc and nothing was ever reclaimed (a 25-day-old engine.log had reached 11 MB), so
    /// the invariants worth pinning are: a run starts clean, prior runs survive, nothing grows without
    /// bound, and the sweep reclaims both stale files and orphans.
    /// </summary>
    public sealed class DiagnosticLogTests : IDisposable
    {
        private readonly string _dir;

        public DiagnosticLogTests()
        {
            // Dev-only test artifact, so temp is fine here (the never-use-temp rule is about
            // agent-facing paths).
            _dir = Path.Combine(Path.GetTempPath(), "cwkt-diaglog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }

        private string Path_(string name) => Path.Combine(_dir, name);

        // Rolled history for <name>, newest last. The explicit primary exclusion is load-bearing:
        // Windows' "name.*" pattern still matches "name" itself (DOS 8.3 "no extension" semantics).
        private string[] RolledOf(string name) =>
            Directory.GetFiles(_dir, name + ".*")
                .Where(f => !string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public void StartRun_MovesThePrimaryAsideUnderAStampedName()
        {
            var log = Path_("engine.log");
            File.WriteAllText(log, "run one");

            DiagnosticLog.StartRun(log);

            Assert.False(File.Exists(log)); // primary is gone until the next write
            var rolled = RolledOf("engine.log");
            Assert.Single(rolled);
            Assert.Equal("run one", File.ReadAllText(rolled[0]));
        }

        [Fact]
        public void StartRun_KeepsEveryPriorRun_NoGenerationCap()
        {
            // The point of dropping the fixed generation depth: reopening the window several times in
            // one morning must not evict a run from ten minutes ago while the directory sits at a few
            // KB. Only age and total size delete a log.
            var log = Path_("engine.log");

            for (var i = 0; i < 6; i++)
            {
                File.WriteAllText(log, "run " + i);
                DiagnosticLog.StartRun(log);
            }

            var rolled = RolledOf("engine.log");
            Assert.Equal(6, rolled.Length);
            var contents = rolled.Select(File.ReadAllText).ToArray();
            for (var i = 0; i < 6; i++)
                Assert.Contains("run " + i, contents);
        }

        [Fact]
        public void StartRun_IsNoOpOnMissingOrEmptyLog()
        {
            var log = Path_("engine.log");

            DiagnosticLog.StartRun(log);          // missing
            File.WriteAllText(log, string.Empty);
            DiagnosticLog.StartRun(log);          // empty

            // Two hosts starting in one run must not litter the directory with empty stamped files.
            Assert.Empty(RolledOf("engine.log"));
        }

        [Fact]
        public void AppendLine_WritesAndKeepsAppendingWithinTheCap()
        {
            var log = Path_("engine.log");

            DiagnosticLog.AppendLine(log, "first");
            DiagnosticLog.AppendLine(log, "second");

            var lines = File.ReadAllLines(log);
            Assert.Equal(new[] { "first", "second" }, lines.Select(Unstamped));
            Assert.False(File.Exists(log + ".1"));
        }

        /// <summary>
        /// Every line carries its own time. Load-bearing for issue #82: the agent CLI's stderr reaches
        /// engine.log through this method and carried no time at all, so the one stream that has to be
        /// read against a hang, a startup phase, or the frames in acp.log couldn't be placed against
        /// any of them. Pinned as a shape (not an exact value) plus a parse, so it fails if the prefix
        /// is dropped or stops being a real timestamp.
        /// </summary>
        [Fact]
        public void AppendLine_StampsEveryLineWithAParsableLocalTime()
        {
            var log = Path_("engine.log");
            var before = DateTime.Now.AddSeconds(-5);

            DiagnosticLog.AppendLine(log, "[kiro-cli] MCP subsystem initialized");

            var line = File.ReadAllLines(log).Single();
            var match = Regex.Match(line, @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\] (.*)$");
            Assert.True(match.Success, $"no timestamp prefix on: {line}");
            Assert.Equal("[kiro-cli] MCP subsystem initialized", match.Groups[2].Value);

            var stamped = DateTime.ParseExact(
                match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            Assert.InRange(stamped, before, DateTime.Now.AddSeconds(5));
        }

        /// <summary>
        /// The stamps must read in the same order as the bytes. Computing the time at the call site
        /// instead of under the write lock would let two concurrent writers — engine stderr arrives on
        /// pool callbacks while the UI thread logs — land in the opposite order to their stamps, which
        /// makes the log lie about sequence, the one thing it exists to establish.
        /// </summary>
        [Fact]
        public void AppendLine_StampsAreOrderedWithTheWrites()
        {
            var log = Path_("engine.log");

            Parallel.For(0, 200, i => DiagnosticLog.AppendLine(log, "line " + i));

            var stamps = File.ReadAllLines(log)
                .Select(l => DateTime.ParseExact(
                    l.Substring(1, 23), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .ToList();

            Assert.Equal(200, stamps.Count);
            Assert.Equal(stamps.OrderBy(s => s), stamps);
        }

        [Fact]
        public void AppendLine_RollsOncePastTheSizeCap()
        {
            var log = Path_("engine.log");
            // Seed just over the cap so the next append must roll rather than grow the file.
            using (var fs = new FileStream(log, FileMode.Create, FileAccess.Write))
                fs.SetLength(DiagnosticLog.MaxFileBytes + 1);

            DiagnosticLog.AppendLine(log, "after the roll");

            Assert.Equal("after the roll", Unstamped(File.ReadAllText(log).TrimEnd('\r', '\n')));
            var rolled = RolledOf("engine.log");
            Assert.Single(rolled);
            Assert.Equal(DiagnosticLog.MaxFileBytes + 1, new FileInfo(rolled[0]).Length);
        }

        /// <summary>Strips the "[yyyy-MM-dd HH:mm:ss.fff] " prefix so a test can assert on the message
        /// a caller passed rather than on the clock.</summary>
        private static string Unstamped(string line) =>
            Regex.Replace(line, @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] ", string.Empty);

        [Fact]
        public void OpenTee_StartsAFreshPrimaryAndPreservesThePreviousRun()
        {
            var log = Path_("acp.log");
            File.WriteAllText(log, "previous run frames");

            using (var sink = DiagnosticLog.OpenTee(log, exclusive: true))
            {
                Assert.NotNull(sink);
                var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n");
                sink!.Write(bytes, 0, bytes.Length);
                sink.Flush();
            }

            Assert.Equal("{\"jsonrpc\":\"2.0\"}\n", File.ReadAllText(log));
            var rolled = RolledOf("acp.log");
            Assert.Single(rolled);
            Assert.Equal("previous run frames", File.ReadAllText(rolled[0]));
        }

        [Fact]
        public void OpenTee_Exclusive_RefusesASecondConcurrentWriter()
        {
            // The ACP tee relies on this: a throwaway engine/summarize session running alongside the
            // active one must degrade to no-tee rather than braid its frames into the same file.
            var log = Path_("acp.log");

            using var first = DiagnosticLog.OpenTee(log, exclusive: true);
            Assert.NotNull(first);

            var second = DiagnosticLog.OpenTee(log, exclusive: true);
            Assert.Null(second);
        }

        [Fact]
        public void OpenTee_RollsMidRunPastTheSizeCap()
        {
            var log = Path_("acp.log");
            var chunk = new byte[64 * 1024];

            using (var sink = DiagnosticLog.OpenTee(log, exclusive: false))
            {
                Assert.NotNull(sink);
                var written = 0L;
                while (written <= DiagnosticLog.MaxFileBytes)
                {
                    sink!.Write(chunk, 0, chunk.Length);
                    written += chunk.Length;
                }
                sink!.Flush();
            }

            // One long-running session can't run away: the overflow rolled instead of growing.
            Assert.True(new FileInfo(log).Length <= DiagnosticLog.MaxFileBytes);
            Assert.Single(RolledOf("acp.log"));
        }

        [Fact]
        public void Sweep_DeletesFilesPastTheAgeLimitIncludingOrphans()
        {
            var fresh = Path_("engine.log");
            var stale = Path_("engine.log.20260701-101500");
            // No writer will ever open this again — rotation can't reach it, only the sweep can.
            var orphan = Path_("buffer-refresh.log");

            File.WriteAllText(fresh, "now");
            File.WriteAllText(stale, "old");
            File.WriteAllText(orphan, "retired feature");
            var past = DateTime.UtcNow.AddDays(-(DiagnosticLog.MaxAgeDays + 1));
            File.SetLastWriteTimeUtc(stale, past);
            File.SetLastWriteTimeUtc(orphan, past);

            DiagnosticLog.Sweep(_dir);

            Assert.True(File.Exists(fresh));
            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(orphan));
        }

        [Fact]
        public void Sweep_EvictsRolledHistoryBeforePrimariesToMeetTheTotalBudget()
        {
            // Three files at half the budget each: over budget, and only the rolled one is safe to
            // drop (a primary may be held open by a live writer).
            var size = DiagnosticLog.TotalBudgetBytes / 2;
            var primaryA = Path_("engine.log");
            var primaryB = Path_("acp.log");
            var rolled = Path_("acp.log.20260725-120000");

            foreach (var (path, len) in new[] { (primaryA, size), (primaryB, size), (rolled, size) })
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                fs.SetLength(len);
            }
            // Make the rolled file the NEWEST, so only the rolled-history-first rule can select it.
            File.SetLastWriteTimeUtc(primaryA, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(primaryB, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(rolled, DateTime.UtcNow);

            DiagnosticLog.Sweep(_dir);

            Assert.False(File.Exists(rolled)); // evicted despite being newest
            Assert.True(File.Exists(primaryA));
            Assert.True(File.Exists(primaryB));
        }

        [Fact]
        public void Roll_SweepsSoTheBudgetBindsWithoutWaitingForStartup()
        {
            // Without this, a long debug session rolling every few minutes would accumulate all day
            // and nothing would reclaim until the next window open — the reason the generation cap
            // could be dropped in the first place. The pass is scheduled rather than run inline
            // (issue #100), so drain it — the guarantee is that the roll HANDS the directory to a
            // sweep, not that the caller waits for it.
            var log = Path_("engine.log");
            var oldRoll = Path_("engine.log.20260725-090000");
            using (var fs = new FileStream(oldRoll, FileMode.Create, FileAccess.Write))
                fs.SetLength(DiagnosticLog.TotalBudgetBytes);
            File.SetLastWriteTimeUtc(oldRoll, DateTime.UtcNow.AddHours(-3));

            // Push the primary over the per-file cap so the next append has to roll.
            using (var fs = new FileStream(log, FileMode.Create, FileAccess.Write))
                fs.SetLength(DiagnosticLog.MaxFileBytes + 1);

            using (var scheduler = new CapturingScheduler(_dir))
            {
                DiagnosticLog.AppendLine(log, "triggers the roll");
                scheduler.RunAll();
            }

            Assert.False(File.Exists(oldRoll)); // reclaimed by the roll's own sweep
            Assert.True(TotalBytes() <= DiagnosticLog.TotalBudgetBytes);
        }

        [Fact]
        public void Roll_SchedulesTheSweepInsteadOfRunningItOnTheCallersThread()
        {
            // Issue #100: every roll used to enumerate-and-stat the whole log directory inline, under
            // the global write lock, and the shell's rolls all land on one straight-line block on the
            // UI thread. A directory measured at 365 files / 42 MB was being walked up to four times
            // per chat-window open. The negative below is the check — nothing may have been swept by
            // the time the appending call returns.
            var log = Path_("engine.log");
            var stale = Path_("orphan.log");
            File.WriteAllText(stale, "older than the age limit");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-(DiagnosticLog.MaxAgeDays + 1)));

            using (var fs = new FileStream(log, FileMode.Create, FileAccess.Write))
                fs.SetLength(DiagnosticLog.MaxFileBytes + 1);

            using var scheduler = new CapturingScheduler(_dir);
            DiagnosticLog.AppendLine(log, "triggers the roll");

            // The move IS inline — it's ordered against the append that follows it.
            Assert.Single(RolledOf("engine.log"));
            Assert.Equal("triggers the roll", Unstamped(File.ReadAllText(log).TrimEnd('\r', '\n')));
            // The sweep is not: it was handed to the scheduler, untouched.
            Assert.Equal(1, scheduler.Pending);
            Assert.True(File.Exists(stale));

            scheduler.RunAll();
            Assert.False(File.Exists(stale));
        }

        [Fact]
        public void SweepInBackground_CoalescesRequestsQueuedForTheSameDirectory()
        {
            // A burst guard, and only that. Measured against a real thread pool it rarely fires: the
            // pool starts a pass within microseconds, so a window open's rolls each land after the
            // previous entry cleared and produce six passes, not one. What it protects is the case
            // where the pool is saturated and requests genuinely stack up behind one queued pass —
            // four concurrent enumerations of a directory on a redirected share compound.
            using var scheduler = new CapturingScheduler(_dir);

            DiagnosticLog.SweepInBackground(_dir);
            DiagnosticLog.SweepInBackground(_dir);
            DiagnosticLog.SweepInBackground(_dir);
            Assert.Equal(1, scheduler.Pending);

            // A different directory is a different pass — coalescing is per-directory, or a host
            // sweeping two scratch dirs would silently lose one of them.
            var other = Path.Combine(_dir, "nested");
            Directory.CreateDirectory(other);
            DiagnosticLog.SweepInBackground(other);
            Assert.Equal(2, scheduler.Pending);

            // Coalescing must not latch: once the pass has run, the next request schedules a new one.
            // (That the entry clears at the START of the pass rather than the end — the part that
            // keeps "every roll is followed by a sweep that can see it" true under real concurrency —
            // isn't observable from here, where the pass runs to completion synchronously.)
            scheduler.RunAll();
            DiagnosticLog.SweepInBackground(_dir);
            Assert.Equal(1, scheduler.Pending);
            scheduler.RunAll();
        }

        [Fact]
        public void EveryPassReportsItsDurationAndWhatItReclaimed()
        {
            // Retention is off-thread file work whose cost belongs to the machine, not to us, so a
            // "the chat window hung" report needs a number to be attributable at all (issue #100).
            var stale = Path_("orphan.log");
            File.WriteAllText(stale, new string('x', 4096));
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-(DiagnosticLog.MaxAgeDays + 1)));
            File.WriteAllText(Path_("engine.log"), "live");

            using var scheduler = new CapturingScheduler(_dir);
            DiagnosticLog.SweepInBackground(_dir);
            scheduler.RunAll();

            var line = Assert.Single(scheduler.Lines);
            Assert.StartsWith("[retention] " + Path.GetFileName(_dir) + ": ", line);
            Assert.Contains(" ms, 2 files, 1 deleted, ", line);
            // Nothing rolled, so there is no inline cost to attribute.
            Assert.DoesNotContain("inline", line);
        }

        [Fact]
        public void AReportAccountsForEveryRollItsPassStoodInFor()
        {
            // Opening the window rolls several logs into one coalesced pass. If the line only counted
            // the roll that happened to schedule it, the UI-thread half — the only part still on the
            // caller's thread, and so the part a hang report is about — would read as a fraction of
            // what it was.
            File.WriteAllText(Path_("engine.log"), "prior run");
            File.WriteAllText(Path_("acp.log"), "prior frames");

            using var scheduler = new CapturingScheduler(_dir);
            DiagnosticLog.StartRun(Path_("engine.log")); // rolls, and schedules the pass
            DiagnosticLog.StartRun(Path_("acp.log"));    // rolls, and coalesces into it
            Assert.Equal(1, scheduler.Pending);
            scheduler.RunAll();

            var line = Assert.Single(scheduler.Lines);
            Assert.Contains("+2 rolls inline", line);
        }

        /// <summary>
        /// The flake that made this file fail about twice in ten full-suite runs, reproduced
        /// deterministically: a pass handed to the REAL scheduler by an earlier test lands while a
        /// later test has the process-wide retention sink pointed at its own list.
        /// </summary>
        /// <remarks>
        /// Deterministic where the original was not, by holding the stray on a gate instead of hoping
        /// the pool is busy — which is why this never reproduced in isolation (0/15) and only showed up
        /// under a contended full-suite run. The stray sweeps a DIFFERENT directory, because that is
        /// what the fix keys on: a capture owns the lines its own directory produced, and "written
        /// while I was installed" is not a safe definition of ownership on a static sink.
        /// </remarks>
        [Fact]
        public void ACaptureIgnoresAPassItDidNotSchedule()
        {
            var strayDir = Path.Combine(_dir, "stray");
            Directory.CreateDirectory(strayDir);
            using var release = new ManualResetEventSlim();
            using var landed = new ManualResetEventSlim();

            // Scheduled the way the real code does — onto the pool, outside any capture — but pinned
            // in flight so the test decides when it lands rather than the pool.
            using (DiagnosticLog.OverrideSweepScheduler(work => Task.Run(() =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                work();
                landed.Set();
            })))
            {
                DiagnosticLog.SweepInBackground(strayDir);
            }

            using var scheduler = new CapturingScheduler(_dir);
            DiagnosticLog.SweepInBackground(_dir);
            scheduler.RunAll();

            release.Set();
            Assert.True(landed.Wait(TimeSpan.FromSeconds(10)), "the stray pass never ran");

            // One line, and it is ours: the stray reported on its own directory.
            var line = Assert.Single(scheduler.Lines);
            Assert.StartsWith("[retention] " + Path.GetFileName(_dir) + ": ", line);
        }

        /// <summary>
        /// Stands in for the thread pool so a test can assert on what has NOT run yet, and collects
        /// the retention lines the passes write. Against the real scheduler every check here would be
        /// a race with a background task.
        /// </summary>
        /// <remarks>
        /// <see cref="Lines"/> is defined by the DIRECTORY the pass reported on, not by "written while
        /// this scope was installed" — which is what it used to be, and is not a safe definition on a
        /// process-wide sink. Several tests here roll a log without a capture installed, so their pass
        /// goes to the real thread pool, and a full-suite run is contended enough that one can still be
        /// pending when a later test points <c>DiagnosticLog.RetentionLog</c> at its own list. The
        /// stray's line then lands in a collection that test believes it owns: measured as
        /// <c>Assert.Single() Failure: The collection contained 2 items</c>, roughly 2 runs in 10 and
        /// never in isolation (0/15). Each test owns a GUID-named directory, so the pass's own name is
        /// what tells them apart. Pinned by <see cref="ACaptureIgnoresAPassItDidNotSchedule"/>.
        /// </remarks>
        private sealed class CapturingScheduler : IDisposable
        {
            private readonly List<Action> _work = new List<Action>();
            private readonly List<string> _lines = new List<string>();
            private readonly IDisposable _scope;
            private readonly Action<string>? _previousLog;
            private readonly string _prefix;

            public CapturingScheduler(string directory)
            {
                _prefix = "[retention] " + Path.GetFileName(directory) + ": ";
                _scope = DiagnosticLog.OverrideSweepScheduler(w => _work.Add(w));
                _previousLog = DiagnosticLog.RetentionLog;
                // Locked because the writer may be a stray pass on the pool: the filter is what keeps
                // its line out, but the call still arrives on that thread.
                DiagnosticLog.RetentionLog = line =>
                {
                    if (!line.StartsWith(_prefix, StringComparison.Ordinal))
                        return;
                    lock (_lines)
                        _lines.Add(line);
                };
            }

            /// <summary>Retention lines for the directory this capture owns.</summary>
            public IReadOnlyList<string> Lines
            {
                get { lock (_lines) return _lines.ToArray(); }
            }

            public int Pending => _work.Count;

            public void RunAll()
            {
                var pending = _work.ToArray();
                _work.Clear();
                foreach (var w in pending)
                    w();
            }

            // Drain on the way out: a captured-but-never-run pass would leave its directory marked
            // as having one queued, and the next request for that path would coalesce into nothing.
            public void Dispose()
            {
                RunAll();
                DiagnosticLog.RetentionLog = _previousLog;
                _scope.Dispose();
            }
        }

        private long TotalBytes()
        {
            var total = 0L;
            foreach (var f in Directory.GetFiles(_dir))
                total += new FileInfo(f).Length;
            return total;
        }

        [Fact]
        public void Sweep_LeavesADirectoryInsideBudgetAlone()
        {
            var log = Path_("engine.log");
            File.WriteAllText(log, "small");

            DiagnosticLog.Sweep(_dir);

            Assert.True(File.Exists(log));
        }
    }
}
