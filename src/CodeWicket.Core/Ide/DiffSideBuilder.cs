using System;
using System.Collections.Generic;

namespace CodeWicket.Core.Ide
{
    /// <summary>The full-file before/after a hunk-only edit was widened to, and where it landed.</summary>
    /// <param name="Before">Whole-file content as it was before the edit, \n-normalized.</param>
    /// <param name="After">Whole-file content as it is now, \n-normalized.</param>
    /// <param name="Line">1-based line the edit was located at, for the diff window's caption.</param>
    public sealed record DiffSides(string Before, string After, int Line);

    /// <summary>
    /// Turns a hunk-only edit into a full-file before/after by locating the new text in the file's
    /// current content and swapping it back to the old text for the "before" side, so the diff window
    /// shows the change in context rather than a floating fragment.
    /// <para>
    /// A surgical edit (Claude's <c>Edit</c>, Kiro's <c>fsReplace</c>) reports only the changed hunk,
    /// and the file's current content already reflects every edit the agent made by the time the user
    /// clicks the card — so the "after" side is the file itself and only the "before" needs rebuilding.
    /// </para>
    /// <para>
    /// In Core, not the VS-bound Ide project, for the same reason as <see cref="LineEndings"/>: it is
    /// pure string work with a pile of bail-out conditions, and those are only worth anything if they
    /// can be tested without devenv. It is also the expensive part of opening a diff — a whole-file
    /// scan plus two substring allocations — which is why <c>VsEditApplier.ShowDiffPreviewAsync</c>
    /// runs it off the UI thread. Nothing here touches VS, I/O, or a thread-affine service.
    /// </para>
    /// </summary>
    public static class DiffSideBuilder
    {
        /// <summary>
        /// How far the located hunk may sit from the line the backend reported before the match is
        /// treated as spurious. Generous: legitimate shift (a later edit inserting lines above this
        /// one) can be large, so this only catches a gross mismatch — a coincidental identical string
        /// elsewhere in the file.
        /// </summary>
        public const int ReportedLineTolerance = 500;

        /// <summary>
        /// Returns the widened sides, or null — meaning the caller should fall back to showing the raw
        /// reported hunk. Null unless this can be done confidently:
        /// <list type="bullet">
        /// <item><paramref name="current"/> is available (the caller resolves it from the live buffer,
        /// else disk — only that half needs the UI thread); null means nothing to widen against;</item>
        /// <item>the edit is a genuine hunk — not a whole-file write, and not a pure deletion or new
        /// file (empty <paramref name="newText"/> or null <paramref name="oldText"/>);</item>
        /// <item>the new text is present, and the chosen occurrence is trustworthy: with a
        /// <paramref name="reportedLine"/> the occurrence nearest it wins (disambiguating repeated
        /// text), bailing if even the nearest is more than <see cref="ReportedLineTolerance"/> lines
        /// away; with no reported line a single occurrence is required.</item>
        /// </list>
        /// Line endings are normalized to <c>\n</c> for both the match and the emitted sides (the
        /// scratch files written from these are ours), so a <c>\n</c> hunk still matches a
        /// <c>\r\n</c> file.
        /// </summary>
        public static DiffSides? TryBuild(string? current, string? oldText, string? newText, int? reportedLine)
        {
            if (oldText is null || string.IsNullOrEmpty(newText))
                return null;

            if (current is null)
                return null;

            var currentN = current.Replace("\r\n", "\n");
            var newN = newText!.Replace("\r\n", "\n");
            var oldN = oldText.Replace("\r\n", "\n");

            // Whole-file write (new text IS the file) → nothing to widen.
            if (string.Equals(currentN, newN, StringComparison.Ordinal))
                return null;

            var chosen = LocateHunk(currentN, newN, reportedLine);
            if (chosen < 0)
                return null;

            var before = currentN.Substring(0, chosen) + oldN + currentN.Substring(chosen + newN.Length);
            return new DiffSides(before, currentN, LineAt(currentN, chosen));
        }

        /// <summary>
        /// The 1-based line an edit sits at in the file's CURRENT content — where "Open file" on an
        /// edit card should land — or <paramref name="reportedLine"/> (else 0, meaning the top) when it
        /// can't be found.
        /// <para>
        /// Locating the text beats trusting the reported line, and that is the whole point: the report
        /// is where the edit was WHEN IT WAS MADE, so anything inserted above it since has moved the
        /// code out from under it, and 70% of recorded edits carry no reported line at all. Finding the
        /// text in the file as it stands now is correct in both cases. The reported line survives only
        /// as the fallback and as the tie-breaker between repeated occurrences.
        /// </para>
        /// <para>
        /// Two shapes, because backends report edits both ways. A hunk (Claude's <c>Edit</c>) is found
        /// by <see cref="LocateHunk"/> — the same occurrence rule <see cref="TryBuild"/> splices by, so
        /// the diff window's caption and this can never name different lines. A whole-file write (a
        /// mirrored Kiro edit, where the new text IS the file) has no hunk to find, so the edit is the
        /// first line that actually changed; for a new file that is line 1, which is also where you'd
        /// want to land.
        /// </para>
        /// </summary>
        public static int LocateLine(string? current, string? oldText, string? newText, int? reportedLine)
        {
            var fallback = reportedLine is { } r && r > 0 ? r : 0;
            if (current is null || string.IsNullOrEmpty(newText))
                return fallback;

            var currentN = current.Replace("\r\n", "\n");
            var newN = newText!.Replace("\r\n", "\n");

            if (string.Equals(currentN, newN, StringComparison.Ordinal))
            {
                var changed = FirstChangedLine(oldText?.Replace("\r\n", "\n") ?? string.Empty, newN);
                return changed > 0 ? changed : fallback;
            }

            var at = LocateHunk(currentN, newN, reportedLine);
            return at >= 0 ? LineAt(currentN, at) : fallback;
        }

        /// <summary>
        /// Character index of the occurrence of <paramref name="newN"/> in <paramref name="currentN"/>
        /// that is the edit, or -1 when no occurrence can be trusted. Shared by <see cref="TryBuild"/>
        /// and <see cref="LocateLine"/> so the splice and the navigation can never disagree about which
        /// of several identical hunks the edit was. Both inputs must already be \n-normalized.
        /// <para>
        /// A non-positive report is NOT a report — the same reading <see cref="LocateLine"/> applies to
        /// its own fallback, and the two have to agree or the sharing buys nothing. Lines are 1-based,
        /// so 0 names no line, but the backend can still send it: <c>AcpMapper.ParseLocationLines</c>
        /// accepts any number the <c>locations</c> array carries. Taken as an anchor it measures the
        /// tolerance against the top of the file, which REJECTS a unique, correct match anywhere past
        /// line <see cref="ReportedLineTolerance"/> — the widened diff silently degrades to the bare
        /// hunk and "Open file" lands at the top.
        /// </para>
        /// </summary>
        private static int LocateHunk(string currentN, string newN, int? reportedLine)
        {
            // All occurrences of the new text (paired with their 1-based line).
            var matches = new List<(int index, int line)>();
            for (var at = currentN.IndexOf(newN, StringComparison.Ordinal); at >= 0;
                 at = currentN.IndexOf(newN, at + 1, StringComparison.Ordinal))
            {
                matches.Add((at, LineAt(currentN, at)));
            }

            if (matches.Count == 0)
                return -1; // a later edit moved past this hunk

            if (reportedLine is { } reported && reported > 0)
            {
                // Disambiguate by the reported line: nearest wins; a gross mismatch → don't trust it.
                var best = matches[0];
                foreach (var m in matches)
                    if (Math.Abs(m.line - reported) < Math.Abs(best.line - reported))
                        best = m;
                return Math.Abs(best.line - reported) > ReportedLineTolerance ? -1 : best.index;
            }

            // No reported line to disambiguate — only trust a unique match.
            return matches.Count > 1 ? -1 : matches[0].index;
        }

        /// <summary>
        /// 1-based line of the first character where <paramref name="a"/> and <paramref name="b"/>
        /// diverge, counted in <paramref name="b"/>; 0 when they're identical. A pure append diverges
        /// at the end of <paramref name="a"/>, which is the first appended line.
        /// </summary>
        private static int FirstChangedLine(string a, string b)
        {
            var min = Math.Min(a.Length, b.Length);
            var i = 0;
            while (i < min && a[i] == b[i])
                i++;
            return i == a.Length && i == b.Length ? 0 : LineAt(b, i);
        }

        /// <summary>1-based line number of the character at <paramref name="index"/> in \n-normalized text.</summary>
        private static int LineAt(string text, int index)
        {
            var line = 1;
            for (var i = 0; i < index; i++)
                if (text[i] == '\n')
                    line++;
            return line;
        }
    }
}
