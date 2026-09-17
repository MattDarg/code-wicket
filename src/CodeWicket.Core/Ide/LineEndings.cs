namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Matches an agent's file content to the line endings a file already uses, before the edit is applied
    /// (<c>VsEditApplier.WriteTextFileAsync</c>).
    /// <para>
    /// Agents routinely send <c>"\n"</c> for a file that is CRLF on disk. Applied verbatim that leaves the
    /// document with MIXED endings, and three things go wrong: VS raises its modal "inconsistent line
    /// endings" dialog the next time the file is opened — on the UI thread, mid-turn, in front of the
    /// agent's own edit; the minimal-edit computation degenerates, because the common prefix stops at the
    /// first line break, so a one-line change rewrites the whole document (churning the undo stack, the diff
    /// preview and the git diff); and the file lands in source control as a whole-file rewrite.
    /// </para>
    /// <para>
    /// ACP's <c>fs/write_text_file</c> carries the FULL file, so rewriting the incoming content's endings
    /// normalizes the entire file — which means an already-mixed file is repaired by the next agent edit
    /// rather than being preserved as-is.
    /// </para>
    /// In Core, not the VS-bound Ide project, so the edge cases (mixed source, no line breaks at all, empty)
    /// are unit-testable without devenv.
    /// </summary>
    public static class LineEndings
    {
        /// <summary>
        /// Rewrites <paramref name="content"/>'s line endings to whichever ending is dominant in
        /// <paramref name="existing"/>. Returns <paramref name="content"/> unchanged when there is nothing to
        /// match against — either side empty, or an <paramref name="existing"/> with no line breaks at all
        /// (a new or single-line file, where whatever the agent sent is consistent by definition).
        /// A lone <c>\r</c> (classic Mac) is left alone: it is neither counted nor rewritten.
        /// </summary>
        public static string Match(string content, string existing)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(existing))
                return content;

            var crlf = 0;
            var lf = 0;
            for (var i = 0; i < existing.Length; i++)
            {
                if (existing[i] != '\n')
                    continue;
                if (i > 0 && existing[i - 1] == '\r')
                    crlf++;
                else
                    lf++;
            }

            if (crlf == 0 && lf == 0)
                return content;

            // Collapse to LF first so the content's own endings can be mixed too.
            var normalized = content.Replace("\r\n", "\n");
            return crlf >= lf ? normalized.Replace("\n", "\r\n") : normalized;
        }
    }
}
