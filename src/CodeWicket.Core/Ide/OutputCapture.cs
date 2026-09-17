using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CodeWicket.Core.Ide
{
    /// <summary>Which of the two things a capture took, so the reader is never left to guess.</summary>
    public enum OutputCaptureKind
    {
        /// <summary>The tail of the pane — what is on screen now, bounded.</summary>
        Tail,

        /// <summary>The lines the user had selected in the pane when they asked.</summary>
        Selection,
    }

    /// <summary>
    /// What one Output-window pane looked like when the user handed it over, as a chip label and a
    /// wire block.
    /// <para>A plain class rather than a record for the reason the UI layer's types are: nothing here
    /// needs value semantics, and one <c>IsExternalInit</c> polyfill is one too many.</para>
    /// </summary>
    public sealed class OutputPaneCapture
    {
        internal OutputPaneCapture(string label, string text, OutputCaptureKind kind, int lines, int totalLines)
        {
            Label = label;
            Text = text;
            Kind = kind;
            Lines = lines;
            TotalLines = totalLines;
        }

        /// <summary>The chip's caption. Says which of the two shapes it is, and how big.</summary>
        public string Label { get; }

        /// <summary>The block's body: the provenance line, then the lines themselves.</summary>
        public string Text { get; }

        public OutputCaptureKind Kind { get; }

        /// <summary>How many lines went.</summary>
        public int Lines { get; }

        /// <summary>How many there were to choose from — the selection's own length, or the pane's.</summary>
        public int TotalLines { get; }

        /// <summary>Whether anything was left behind, which is what the provenance line has to say.</summary>
        public bool Truncated => Lines < TotalLines;
    }

    /// <summary>
    /// Turns a Visual Studio Output-window pane into the capture the composer attaches (the "Add ▾ →
    /// Output window" gesture). Pure, and in Core, for the reason <see cref="OutputTail"/> gives about
    /// itself: the tool catalog and the pane reader are net472 + the VS SDK, which no test project can
    /// reference, so a rule left there is a rule nothing can check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The provenance line is the feature, not the packaging.</b> An agent handed twelve selected
    /// lines of a build log and asked why the build failed will not find the error it expects, and a
    /// model given an incomplete account with no statement of its incompleteness supplies its own —
    /// measured on the wire in this repo on a different payload, where a hedged refusal came back as a
    /// confidently invented cause plus a workaround that would have overridden the user
    /// (<see cref="FileWriteRefusal"/>). So every capture says which shape it is and what it left
    /// behind, and where something IS missing it says what to do about it.
    /// </para>
    /// <para>
    /// <b>What to do about it is "ask the user", and that is deliberate.</b> There is no tool for
    /// reading an output pane other than the Debug one, so an agent cannot go and fetch the rest; a
    /// line implying it could would send it looking for a tool that is not there. If that tool is ever
    /// built, this wording is what changes with it.
    /// </para>
    /// </remarks>
    public static class OutputCapture
    {
        /// <summary>
        /// How many lines a TAIL carries. A pane is unbounded — a verbose build or a chatty extension
        /// runs to megabytes — and this block sits in a prompt, so a default has to be chosen; this is
        /// that choice, and the user made no such choice, which is what keeps it modest.
        /// </summary>
        public const int MaxTailLines = 200;

        /// <summary>
        /// How many lines a SELECTION carries. Deliberately an order of magnitude above
        /// <see cref="MaxTailLines"/>, because the two caps are doing different jobs: the tail's is a
        /// CURATION decision made on the user's behalf, while this one is a SAFETY RAIL against a
        /// gesture that was not as deliberate as it looks. A selection is intent — the user saw its
        /// extent and chose it, and truncating 400 chosen lines to 200 throws away half of an answer
        /// they had already given. But Ctrl+A is one keystroke and selects a megabyte, so "deliberate"
        /// cannot mean unbounded; it means the rail sits far enough out that only that gesture reaches
        /// it.
        /// </summary>
        public const int MaxSelectedLines = 2000;

        /// <summary>
        /// The backstop neither line cap can provide: a pane's lines are of unbounded LENGTH. Two
        /// hundred lines of ordinary build output is perhaps 15 KB, while two hundred lines of MSBuild
        /// at diagnostic verbosity — or one minified bundle printed to a pane — is megabytes, and the
        /// line count would report it as a small capture the whole way. Applied by dropping further
        /// leading lines, so the result stays in the one vocabulary the label and the provenance line
        /// already speak: it is still "the last N of M lines", just a smaller N.
        /// </summary>
        public const int MaxChars = 40_000;

        /// <summary>
        /// Builds the capture, or returns null when there is nothing to take — an empty pane, which is
        /// a race rather than a misuse (the gesture was offered when the pane had content, and a build
        /// can clear it between the menu opening and the click). The caller says so rather than
        /// attaching an empty chip that claims evidence the message does not carry.
        /// </summary>
        /// <param name="paneName">The pane's own name, e.g. "Build". Shown and sent verbatim.</param>
        /// <param name="paneText">Everything the pane holds.</param>
        /// <param name="selectedText">
        /// What the user had selected in it, if anything. <b>Used whenever it is non-empty after
        /// trimming</b>, which is a deliberately plain rule: the accident this needs to survive is a
        /// CLICK, which leaves a zero-length selection and trims away to nothing, while a selection
        /// with words in it was made on purpose. A cleverer guard (a minimum length, a whole-line
        /// requirement) would need a constant nobody can justify, and the label states which shape was
        /// taken anyway — so a surprising capture is visible in the composer before it is sent.
        /// </param>
        /// <param name="maxLines">
        /// Overrides the cap the kind would otherwise choose. For tests and for a caller with a
        /// tighter budget; the default is the whole point and should rarely be passed.
        /// </param>
        public static OutputPaneCapture? Build(
            string? paneName, string? paneText, string? selectedText, int? maxLines = null)
        {
            var name = string.IsNullOrWhiteSpace(paneName) ? "Output" : paneName!.Trim();
            var useSelection = !string.IsNullOrWhiteSpace(selectedText);
            var source = useSelection ? selectedText! : paneText;

            var all = SplitLines(source);
            if (all.Count == 0)
                return null;

            var cap = maxLines ?? (useSelection ? MaxSelectedLines : MaxTailLines);
            var kept = FitToChars(OutputTail.LastLines(all, cap), MaxChars);

            var kind = useSelection ? OutputCaptureKind.Selection : OutputCaptureKind.Tail;
            return new OutputPaneCapture(
                Label(name, kind, kept.Count, all.Count),
                Format(name, kind, kept, all.Count),
                kind,
                kept.Count,
                all.Count);
        }

        /// <summary>The chip's caption: the pane, the shape, and the size.</summary>
        private static string Label(string paneName, OutputCaptureKind kind, int lines, int total)
        {
            var what = kind == OutputCaptureKind.Selection
                ? (lines < total
                    ? $"selection, last {Count(lines)} of {Count(total)}"
                    : $"selection, {Count(lines)}")
                : (lines < total
                    ? $"last {Count(lines)} of {Count(total)}"
                    : Count(lines));

            return $"Output: {paneName} · {what}";
        }

        /// <summary>
        /// The block's body. The provenance line comes FIRST, so a reader that gets no further than the
        /// opening of the block still knows what it is holding.
        /// </summary>
        private static string Format(
            string paneName, OutputCaptureKind kind, IReadOnlyList<string> lines, int total)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Pane: " + paneName);
            sb.AppendLine("Captured: " + Provenance(kind, lines.Count, total));
            sb.AppendLine("---");
            foreach (var line in lines)
                sb.AppendLine(line);

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// One sentence saying what this is and, where something is missing, what to do about it.
        /// <para>Every shape states its totals — including the complete one, so that "this is all of
        /// it" and "this is what fitted" can never be read as the same claim.</para>
        /// </summary>
        private static string Provenance(OutputCaptureKind kind, int lines, int total)
        {
            const string missing = " If what you need is not here, ask for it rather than concluding it "
                + "is absent — this pane cannot be read with a tool, so the user has to send it.";

            if (kind == OutputCaptureKind.Selection)
            {
                return lines < total
                    ? $"the last {Count(lines)} of a {Count(total)} selection the user made in this pane. "
                        + "This is a FRAGMENT of the pane, and not even the whole of what they picked."
                        + missing
                    : $"{Count(lines)} the user SELECTED in this pane. This is a fragment of the pane, "
                        + "chosen deliberately — the rest of the pane is not here." + missing;
            }

            return lines < total
                ? $"the last {Count(lines)} of {Count(total)} in this pane. Earlier lines are not "
                    + "included." + missing
                : $"the whole pane, {Count(lines)}.";
        }

        /// <summary>
        /// Drops further leading lines until what is left fits <paramref name="maxChars"/>. The tail is
        /// what survives, for <see cref="OutputTail.LastLines"/>'s reason: the newest output is the
        /// output anybody asked about.
        /// <para>At least one line always comes back. A single line longer than the whole budget is a
        /// real shape — a minified bundle, one enormous MSBuild command — and returning nothing would
        /// turn "this line is huge" into "the pane was empty", which reads as a race and sends the
        /// caller looking for the wrong thing.</para>
        /// </summary>
        private static IReadOnlyList<string> FitToChars(IReadOnlyList<string> lines, int maxChars)
        {
            if (lines.Count == 0 || maxChars <= 0)
                return lines;

            var total = 0;
            for (var i = 0; i < lines.Count; i++)
                total += lines[i].Length + 1;
            if (total <= maxChars)
                return lines;

            var from = 0;
            while (from < lines.Count - 1 && total > maxChars)
            {
                total -= lines[from].Length + 1;
                from++;
            }

            var kept = new List<string>(lines.Count - from);
            for (var i = from; i < lines.Count; i++)
                kept.Add(lines[i]);
            return kept;
        }

        /// <summary>
        /// Splits into lines the way a pane's text arrives, without inventing a trailing empty one.
        /// A pane that holds only whitespace counts as empty — there is nothing there to hand over.
        /// </summary>
        private static IReadOnlyList<string> SplitLines(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            var normalized = text!.Replace("\r\n", "\n").Replace('\r', '\n');
            var split = normalized.Split('\n');

            var end = split.Length;
            while (end > 0 && string.IsNullOrWhiteSpace(split[end - 1]))
                end--;

            if (end == 0)
                return Array.Empty<string>();

            var lines = new List<string>(end);
            for (var i = 0; i < end; i++)
                lines.Add(split[i]);
            return lines;
        }

        private static string Count(int n) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + (n == 1 ? " line" : " lines");
    }
}
