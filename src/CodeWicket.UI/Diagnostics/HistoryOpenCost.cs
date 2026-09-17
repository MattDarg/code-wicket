using System;
using System.Globalization;
using System.Text;

namespace CodeWicket.UI.Diagnostics
{
    /// <summary>
    /// What opening the history picker costs, as one <c>[history]</c> line in <c>logs/render.log</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists because the obvious cost was fixed and the picker stayed slow.</b> Listing the
    /// saved conversations used to deserialize every session file in full — 12 MB across 29 files on
    /// this repo's own workspace, synchronously inside the <c>IsHistoryOpen</c> setter, which is the
    /// popup's <c>IsOpen</c> binding source. Reading headers only took that from 55 ms to 0.7 ms, and
    /// the picker was still reported slow. So the store read is now measured rather than assumed, and
    /// measured beside everything else on the path.
    /// </para>
    /// <para>
    /// <b><see cref="PaintMs"/> is the point of the line, not <see cref="ListMs"/>.</b> The rule from
    /// issue #86: a trace aimed at the suspected cost answers for the suspected cost and goes
    /// quiet: <c>[md-cost]</c> proved markdown was 1-2% of a drag and could not say what the other 98%
    /// was, because it was keyed on the thing already under suspicion. Everything this type measures
    /// directly — the store read, building the rows, the synchronous head of the backend listing — is
    /// work we wrote and can see. <c>paint</c> is the residue: the wall-clock from the setter returning
    /// to a callback posted at <c>Loaded</c> priority actually running, which is where the popup's own
    /// window creation, container generation and layout live. If the total is large and the named
    /// phases are small, the cost is WPF's and the next instrument is a different one.
    /// </para>
    /// <para>
    /// <b>Off unless the sink is installed</b> (<c>ExtensionConfig.LogRendering</c>, host-installed —
    /// see <see cref="Markdown.RenderDiagnosticsLog"/>). <see cref="Begin"/> returns null when it is
    /// not, so the cost with logging off is one static reference test per open.
    /// </para>
    /// <para>
    /// <b>WPF-free, and time is passed in</b>, on the same bargain as <see cref="RealisationCost"/>:
    /// the arithmetic and the formatting are what quietly go wrong in a diagnostic, and neither needs
    /// a dispatcher to be pinned by a unit test.
    /// </para>
    /// </remarks>
    internal sealed class HistoryOpenCost
    {
        /// <summary>Time spent in <c>ISessionStore.List</c> — the header read.</summary>
        public double ListMs { get; private set; }

        /// <summary>Building the row view-models and refilling the bound collection.</summary>
        public double RowsMs { get; private set; }

        /// <summary>
        /// The SYNCHRONOUS head of the backend listing — everything the fire-and-forget call runs
        /// before its first await, which is on the UI thread and therefore ahead of the paint however
        /// asynchronous the rest of it is.
        /// </summary>
        public double BackendsMs { get; private set; }

        /// <summary>Everything inside the setter, including the three phases above.</summary>
        public double SetterMs { get; private set; }

        /// <summary>
        /// Setter return → a <c>Loaded</c>-priority callback running. Not attributable to any code of
        /// ours, which is exactly why it is here: it is where the popup's own cost shows up.
        /// </summary>
        public double PaintMs { get; private set; }

        /// <summary>Saved conversations listed.</summary>
        public int SavedRows { get; private set; }

        /// <summary>Rows the picker is showing from the backend's own store at paint time (issue #108).</summary>
        public int CliRows { get; private set; }

        /// <summary>Null when nothing is listening, so an open costs a reference test.</summary>
        public static HistoryOpenCost? Begin() =>
            !Markdown.RenderDiagnosticsLog.Enabled ? null : new HistoryOpenCost();

        public void NoteList(double ms, int rows)
        {
            ListMs = ms;
            SavedRows = rows;
        }

        public void NoteRows(double ms) => RowsMs = ms;

        public void NoteBackends(double ms) => BackendsMs = ms;

        public void NoteSetter(double ms) => SetterMs = ms;

        public void NotePainted(double ms, int cliRows)
        {
            PaintMs = ms;
            CliRows = cliRows;
        }

        /// <summary>
        /// The line. <c>unnamed</c> is the setter time the three measured phases do not account for —
        /// present for the same reason <c>[dispatch]</c> reports its own unaccounted wall: a residue
        /// that is never shown is a residue nobody goes looking in.
        /// </summary>
        public string Format()
        {
            var unnamed = SetterMs - ListMs - RowsMs - BackendsMs;
            if (unnamed < 0)
                unnamed = 0;

            var sb = new StringBuilder("[history] open");
            sb.Append(" total=").Append(Ms(SetterMs + PaintMs));
            sb.Append(" setter=").Append(Ms(SetterMs));
            sb.Append(" list=").Append(Ms(ListMs));
            sb.Append(" rows=").Append(Ms(RowsMs));
            sb.Append(" backends=").Append(Ms(BackendsMs));
            sb.Append(" unnamed=").Append(Ms(unnamed));
            sb.Append(" paint=").Append(Ms(PaintMs));
            sb.Append(" saved=").Append(Int(SavedRows));
            sb.Append(" cli=").Append(Int(CliRows));
            return sb.ToString();
        }

        private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The other half of an open: the backend listing, as one <c>[history] backends</c> line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a separate line because it has a separate lifetime.</b> <see cref="HistoryOpenCost"/>
    /// closes when the popup paints; this outlives it - the listing is fire-and-forget, spawns a CLI
    /// on a cold backend and answers seconds later. Folding the two would mean either holding the open
    /// line back until the listing finished (which is the thing being measured as asynchronous) or
    /// reporting a figure that had not happened yet.
    /// </para>
    /// <para>
    /// <b>The work here is done on every open whichever tab is showing.</b> The unselected pane is
    /// Collapsed, so WPF skips its measure and no row is ever realised - but the row view-models are
    /// still built, marked and sorted regardless. That asymmetry is invisible from the open line and
    /// is exactly what this exists to price.
    /// </para>
    /// <para>
    /// <b><see cref="DedupeMs"/> is split out deliberately.</b> Deduping against the saved
    /// conversations re-reads the whole session store, once per answering backend, on top of the read
    /// the open itself just did. That was spotted by reading the code and never measured; a field is
    /// how it stops being a guess.
    /// </para>
    /// </remarks>
    internal sealed class HistoryBackendListCost
    {
        /// <summary>Call to completion, including the CLI spawn on a cold backend.</summary>
        public double WallMs { get; private set; }

        /// <summary>Re-reading the saved store to dedupe, summed over the backends that answered.</summary>
        public double DedupeMs { get; private set; }

        /// <summary>Building, marking and sorting the row view-models - paid whether or not the tab is shown.</summary>
        public double RowsMs { get; private set; }

        public int Asked { get; private set; }

        /// <summary>Backends answered from our cache rather than a spawn - why a reopen is quick.</summary>
        public int Cached { get; private set; }

        public int Failed { get; private set; }

        public int Listed { get; private set; }

        /// <summary>Rows held back as created-and-never-used (issue #108 follow-on).</summary>
        public int Hidden { get; private set; }

        /// <summary>True when a newer refresh superseded this one, so the figures are partial.</summary>
        public bool Abandoned { get; private set; }

        public static HistoryBackendListCost? Begin(int asked) =>
            !Markdown.RenderDiagnosticsLog.Enabled ? null : new HistoryBackendListCost { Asked = asked };

        public void AddDedupe(double ms) => DedupeMs += ms;

        public void AddRows(double ms) => RowsMs += ms;

        public void NoteFailed() => Failed++;

        public void NoteAbandoned() => Abandoned = true;

        public void Complete(double wallMs, int listed, int hidden, int cached)
        {
            WallMs = wallMs;
            Listed = listed;
            Hidden = hidden;
            Cached = cached;
        }

        public string Format()
        {
            var sb = new StringBuilder("[history] backends");
            sb.Append(" wall=").Append(Ms(WallMs));
            sb.Append(" dedupe=").Append(Ms(DedupeMs));
            sb.Append(" rows=").Append(Ms(RowsMs));
            sb.Append(" asked=").Append(Int(Asked));
            sb.Append(" cached=").Append(Int(Cached));
            sb.Append(" failed=").Append(Int(Failed));
            sb.Append(" listed=").Append(Int(Listed));
            sb.Append(" hidden=").Append(Int(Hidden));
            if (Abandoned)
                sb.Append(" abandoned=true");
            return sb.ToString();
        }

        private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

}
