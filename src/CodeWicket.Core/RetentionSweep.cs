using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CodeWicket.Core
{
    /// <summary>
    /// What one <see cref="RetentionSweep.Run"/> pass did. Exists to be LOGGED: the pass is off-thread
    /// file work whose cost is entirely machine-specific (AV/EDR interception, LocalAppData redirected
    /// onto a share, VDI), so when a field report says the chat window hung, this line is the
    /// difference between attributing it and guessing. <see cref="Scanned"/> is every file the
    /// enumeration yielded, whether or not it survived.
    /// </summary>
    public readonly struct RetentionSweepResult
    {
        public RetentionSweepResult(int scanned, int deleted, long bytesReclaimed, TimeSpan elapsed)
        {
            Scanned = scanned;
            Deleted = deleted;
            BytesReclaimed = bytesReclaimed;
            Elapsed = elapsed;
        }

        /// <summary>Files the enumeration yielded — the part that costs even when nothing is deleted.</summary>
        public int Scanned { get; }

        /// <summary>Files actually deleted (a delete that failed on a live writer's lock isn't counted).</summary>
        public int Deleted { get; }

        /// <summary>Bytes those deletions reclaimed.</summary>
        public long BytesReclaimed { get; }

        /// <summary>Wall-clock for the whole pass, enumeration included.</summary>
        public TimeSpan Elapsed { get; }

        /// <summary>
        /// One log line's worth, e.g. <c>142 ms, 302 files, 63 deleted, 7.0 MB reclaimed</c>. Duration
        /// first because it's what a hang report is read against.
        /// </summary>
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} ms, {1} files, {2} deleted, {3:N1} MB reclaimed",
            Elapsed.TotalMilliseconds, Scanned, Deleted, BytesReclaimed / 1024d / 1024d);
    }

    /// <summary>
    /// The retention mechanic shared by every scratch directory we write into: drop anything older
    /// than <c>maxAge</c>, then evict until the directory fits <c>budgetBytes</c>.
    ///
    /// <b>Age and total size are the only things that delete a file.</b> There is deliberately no cap
    /// on file COUNT — a count is an arbitrary proxy for "don't accumulate" that evicts
    /// unconditionally; the two limits here measure what actually matters (staleness, disk). The
    /// argument in full, and the incident that produced it, is in <see cref="DiagnosticLog"/>.
    ///
    /// Two callers, same shape, different limits and different eviction order:
    /// <see cref="DiagnosticLog.Sweep"/> over the log directory (rolled history evicted before live
    /// primaries), and <c>VsEditApplier.SweepDiffScratch</c> over the diff viewer's before/after
    /// copies (plain oldest-first). Extracted rather than re-hand-rolled: the diff dir had no
    /// retention at all and was accumulating whole-file source snapshots indefinitely (issue #88),
    /// and a third ad-hoc pruner is how the log directory got into trouble in the first place.
    ///
    /// Best-effort throughout and never throws: a file held open by a live writer simply fails to
    /// delete, and the next pass retries.
    /// </summary>
    public static class RetentionSweep
    {
        /// <summary>
        /// Runs one retention pass over <paramref name="directory"/>. Non-recursive — every caller's
        /// scratch files are flat, and recursing would let one bad path argument reach far more than
        /// intended. A missing directory is a no-op, so callers must never pass a fallback location
        /// they don't exclusively own.
        /// </summary>
        /// <param name="directory">The directory to sweep. Only files directly in it are considered.</param>
        /// <param name="budgetBytes">Total size the survivors are brought under, evicting in <paramref name="evictionOrder"/>.</param>
        /// <param name="evictionOrder">
        /// Which survivor to evict first when over budget. Defaults to oldest-first
        /// (<see cref="CompareOldestFirst"/>).
        /// </param>
        /// <returns>
        /// What the pass did and how long it took — see <see cref="RetentionSweepResult"/>. Callers
        /// are free to ignore it; the hosts log it, because retention is off-thread file work whose
        /// cost is machine-specific and a field report of a hang is otherwise unattributable.
        /// </returns>
        /// <param name="maxAge">
        /// Files last written longer ago than this are deleted outright. <b>Null disables the age rule
        /// entirely</b>, leaving <paramref name="budgetBytes"/> as the only thing that evicts — which is
        /// right for a directory holding CONTENT rather than scratch: a log or a diff copy is dead once
        /// its moment passes, but an image the user attached to a conversation is as relevant as the
        /// conversation, and conversations here are kept indefinitely. A size bound degrades gracefully
        /// where a timer simply deletes.
        /// </param>
        public static RetentionSweepResult Run(
            string directory,
            TimeSpan? maxAge,
            long budgetBytes,
            Comparison<FileInfo>? evictionOrder = null)
        {
            var started = Stopwatch.StartNew();
            var scanned = 0;
            var deleted = 0;
            var reclaimed = 0L;
            RetentionSweepResult Done() =>
                new RetentionSweepResult(scanned, deleted, reclaimed, started.Elapsed);

            if (string.IsNullOrWhiteSpace(directory))
                return Done();
            try
            {
                if (!Directory.Exists(directory))
                    return Done();

                // DateTime.MinValue when the age rule is off: no file's write time can precede it, so
                // the branch below is unreachable without a second flag to keep in step with it.
                var cutoff = maxAge is { } age ? DateTime.UtcNow - age : DateTime.MinValue;
                var files = new List<FileInfo>();
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    scanned++;
                    FileInfo info;
                    try { info = new FileInfo(path); }
                    catch { continue; }

                    // Gone between the enumeration and the stat — most likely a roll renaming it,
                    // which since issue #100 can be running concurrently with this pass. Skipping it
                    // is load-bearing, not tidiness: a missing file reports LastWriteTimeUtc as 1601,
                    // which is older than ANY cutoff, so it would take the age branch below and
                    // delete that name — by then possibly a fresh primary the roll had just created.
                    if (!info.Exists)
                        continue;

                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        var aged = SafeLength(info);
                        if (TryDelete(info.FullName))
                        {
                            deleted++;
                            reclaimed += aged;
                        }
                        continue;
                    }
                    files.Add(info);
                }

                var total = 0L;
                foreach (var f in files)
                    total += SafeLength(f);
                if (total <= budgetBytes)
                    return Done();

                files.Sort(evictionOrder ?? CompareOldestFirst);

                foreach (var f in files)
                {
                    if (total <= budgetBytes)
                        break;
                    var size = SafeLength(f);
                    if (TryDelete(f.FullName))
                    {
                        total -= size;
                        deleted++;
                        reclaimed += size;
                    }
                }
            }
            catch { /* retention is best-effort */ }
            return Done();
        }

        /// <summary>The default eviction order: least recently written goes first.</summary>
        public static int CompareOldestFirst(FileInfo a, FileInfo b) =>
            a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false; // held open by a live writer — the next sweep retries
            }
        }

        private static long SafeLength(FileInfo info)
        {
            try { return info.Length; }
            catch { return 0; }
        }
    }
}
