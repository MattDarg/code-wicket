using System;
using System.IO;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins the shared age-and-budget retention pass that <see cref="DiagnosticLog.Sweep"/> and the
    /// diff scratch dir both run (issue #88: the diff dir had no retention at all and was accumulating
    /// whole-file source snapshots indefinitely).
    ///
    /// <see cref="DiagnosticLogTests"/> already pins the log-specific half — the limits and the
    /// rolled-history-before-primaries eviction order. What's worth pinning HERE is the mechanic
    /// itself, plus the two properties a caller with a different eviction order still depends on: a
    /// directory inside both limits is left completely alone, and a missing directory is a no-op
    /// (which is what makes it safe to hand this a path that may never have been created).
    /// </summary>
    public sealed class RetentionSweepTests : IDisposable
    {
        private static readonly TimeSpan Week = TimeSpan.FromDays(7);
        private const long Unlimited = long.MaxValue;

        private readonly string _dir;

        public RetentionSweepTests()
        {
            // Dev-only test artifact, so temp is fine here (the never-use-temp rule is about
            // agent-facing paths).
            _dir = Path.Combine(Path.GetTempPath(), "cwkt-sweep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }

        private string Write(string name, long bytes, TimeSpan age)
        {
            var path = Path.Combine(_dir, name);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                fs.SetLength(bytes);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }

        [Fact]
        public void Run_ReportsWhatItScannedDeletedAndReclaimed()
        {
            // The pass runs off-thread on a machine we can't see, so this result IS the field
            // diagnostic (issue #100). Both delete paths must count: the age cutoff and the budget
            // eviction. Scanned counts everything enumerated, survivors included — that's the part
            // that costs even on a pass that deletes nothing.
            Write("stale.log", 4096, TimeSpan.FromDays(8));   // aged out
            Write("big.log", 8192, TimeSpan.FromHours(2));    // evicted for budget
            Write("keep.log", 1024, TimeSpan.FromHours(1));

            var result = RetentionSweep.Run(_dir, Week, budgetBytes: 4096);

            Assert.Equal(3, result.Scanned);
            Assert.Equal(2, result.Deleted);
            Assert.Equal(4096 + 8192, result.BytesReclaimed);
            Assert.True(result.Elapsed >= TimeSpan.Zero);
            Assert.Contains("3 files, 2 deleted", result.ToString());
        }

        [Fact]
        public void Run_DeletesFilesPastTheAgeCutoff_AndKeepsTheRest()
        {
            var fresh = Write("Foo.before.cs", 10, TimeSpan.FromHours(1));
            var stale = Write("Bar.before.cs", 10, TimeSpan.FromDays(8));

            RetentionSweep.Run(_dir, Week, Unlimited);

            Assert.True(File.Exists(fresh));
            Assert.False(File.Exists(stale));
        }

        [Fact]
        public void Run_EvictsOldestFirstUntilTheDirectoryFitsTheBudget()
        {
            // All three are inside the age cutoff, so only the budget can select any of them.
            var oldest = Write("A.before.cs", 400, TimeSpan.FromHours(3));
            var middle = Write("B.before.cs", 400, TimeSpan.FromHours(2));
            var newest = Write("C.before.cs", 400, TimeSpan.FromHours(1));

            RetentionSweep.Run(_dir, Week, budgetBytes: 1000);

            Assert.False(File.Exists(oldest)); // 1200 -> 800, under budget, stops
            Assert.True(File.Exists(middle));
            Assert.True(File.Exists(newest));
        }

        [Fact]
        public void Run_LeavesADirectoryInsideBothLimitsCompletelyAlone()
        {
            // The property that makes an unconditional startup sweep safe: nothing is evicted merely
            // for being the oldest, or for the directory being non-empty. Only age and total size
            // delete anything — there is deliberately no cap on file COUNT.
            var paths = new string[20];
            for (var i = 0; i < paths.Length; i++)
                paths[i] = Write("F" + i + ".before.cs", 100, TimeSpan.FromDays(i % 7));

            RetentionSweep.Run(_dir, Week, budgetBytes: 1000000);

            foreach (var p in paths)
                Assert.True(File.Exists(p), Path.GetFileName(p) + " was evicted");
        }

        [Fact]
        public void Run_HonoursACustomEvictionOrder()
        {
            // What DiagnosticLog relies on: it evicts rolled history before live primaries, which
            // means the comparison must be able to override "oldest first" outright.
            var oldest = Write("A.log", 400, TimeSpan.FromHours(3));
            var newest = Write("B.log", 400, TimeSpan.FromHours(1));

            // Newest-first — the exact inverse of the default.
            RetentionSweep.Run(_dir, Week, budgetBytes: 500,
                (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            Assert.False(File.Exists(newest));
            Assert.True(File.Exists(oldest));
        }

        [Fact]
        public void Run_IsANoOpOnAMissingDirectory()
        {
            var missing = Path.Combine(_dir, "never-created");

            RetentionSweep.Run(missing, Week, budgetBytes: 0);

            Assert.False(Directory.Exists(missing)); // and, above all, did not throw
        }

        [Fact]
        public void Run_DoesNotRecurseIntoSubdirectories()
        {
            // The blast-radius guard. Every caller's scratch files are flat, and a sweep that
            // recursed would turn one bad path argument into a very large delete.
            var nested = Directory.CreateDirectory(Path.Combine(_dir, "nested"));
            var buried = Path.Combine(nested.FullName, "Old.before.cs");
            using (var fs = new FileStream(buried, FileMode.Create, FileAccess.Write))
                fs.SetLength(4000);
            File.SetLastWriteTimeUtc(buried, DateTime.UtcNow - TimeSpan.FromDays(30));

            RetentionSweep.Run(_dir, Week, budgetBytes: 0);

            Assert.True(File.Exists(buried));
        }
    }
}
