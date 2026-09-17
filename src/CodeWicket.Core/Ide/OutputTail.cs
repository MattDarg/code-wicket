using System;
using System.Collections.Generic;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Trimming and filtering for the Visual Studio output panes we read — the Build pane behind
    /// <c>build_solution</c>'s failure backstop, and the Debug pane behind <c>get_debug_output</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Small, but in Core because the way it can be wrong is invisible. Keeping the WRONG END of a long
    /// pane returns the oldest output rather than the newest — plausible text, correctly formatted, and
    /// describing a run that is not the one anybody asked about. Nothing downstream can tell; the agent
    /// simply reasons from the wrong values. The tool catalog is net472 + the VS SDK and no test project
    /// can reference it, so a rule left in there is a rule no test can reach.
    /// </para>
    /// <para>
    /// Shared by both readers rather than copied, so the two cannot disagree about how much they dropped
    /// or how they said so.
    /// </para>
    /// </remarks>
    public static class OutputTail
    {
        /// <summary>
        /// The last <paramref name="maxChars"/> of <paramref name="text"/>, with a note saying what was
        /// dropped. Returns the text unchanged when it already fits.
        /// </summary>
        /// <remarks>
        /// The note is not decoration. A silently shortened pane reads as the whole of a short run, so an
        /// agent looking for a value that scrolled off concludes it was never printed — the same class of
        /// mistake as a truncated diagnostic list with no total.
        /// </remarks>
        public static string Keep(string? text, int maxChars) => Keep(text, maxChars, out _);

        /// <summary>
        /// As <see cref="Keep(string,int)"/>, and reports whether anything was dropped.
        /// </summary>
        /// <remarks>
        /// <paramref name="trimmed"/> is an OUT parameter rather than something the caller works out from
        /// the returned string. Sniffing the result for the notice text re-derives a fact from a display
        /// string, which is how a payload's <c>truncated</c> flag and its own output drift apart the first
        /// time the wording changes — and the flag is what a reader trusts when deciding whether the value
        /// it wanted might have scrolled off.
        /// </remarks>
        public static string Keep(string? text, int maxChars, out bool trimmed)
        {
            trimmed = false;
            if (text is null || maxChars <= 0 || text.Length <= maxChars)
                return text ?? string.Empty;

            trimmed = true;
            var dropped = text.Length - maxChars;
            return "…(" + dropped + " earlier chars trimmed)\n" + text.Substring(dropped);
        }

        /// <summary>
        /// The last <paramref name="count"/> of <paramref name="lines"/>. A null or non-positive count
        /// keeps all of them.
        /// </summary>
        /// <remarks>
        /// The LAST, and only the last — there is deliberately no offset or window. An output pane is not
        /// a file: it is cleared when the next debug session starts, so a line number carried from one
        /// turn to the next silently addresses different content, with nothing to notice. Asking for less
        /// of the newest is safe; asking for a particular region is a stale-identifier bug waiting for a
        /// second debug session. Narrowing is what <see cref="LinesContaining"/> is for.
        /// </remarks>
        public static IReadOnlyList<string> LastLines(IReadOnlyList<string> lines, int? count)
        {
            if (lines is null)
                return Array.Empty<string>();
            if (count is not { } n || n <= 0 || lines.Count <= n)
                return lines;

            var kept = new List<string>(n);
            for (var i = lines.Count - n; i < lines.Count; i++)
                kept.Add(lines[i]);
            return kept;
        }

        /// <summary>
        /// The lines of <paramref name="text"/> containing <paramref name="needle"/>, case-insensitively.
        /// A null or empty needle keeps every line.
        /// </summary>
        /// <remarks>
        /// Case-insensitive because the caller is matching prose it wrote into a tracepoint message and
        /// then re-typed from memory a turn later; an exact-case miss would report "nothing was printed"
        /// about output that is sitting right there.
        /// </remarks>
        public static IReadOnlyList<string> LinesContaining(string? text, string? needle)
        {
            var lines = (text ?? string.Empty).Split('\n');
            if (string.IsNullOrEmpty(needle))
                return lines;

            var trimmed = needle!.Trim();
            if (trimmed.Length == 0)
                return lines;

            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (line.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                    kept.Add(line);
            }

            return kept;
        }
    }
}
