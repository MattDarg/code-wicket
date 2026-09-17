namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// The line-range half of <see cref="IEditApplier.ReadTextFileAsync"/>, shared by every applier.
    ///
    /// <para>It lives here because the three appliers had three behaviours: the VS one honoured
    /// <c>line</c>/<c>limit</c>, while the stub and Console ones ignored both and returned the whole
    /// file. That is the same defect as returning "" for a missing file, one step along — the agent
    /// asks for lines 40-60, is handed 4000 lines, and has no way to tell that its range was dropped.
    /// Which host it is talking to must never change what a read means.</para>
    /// </summary>
    public static class TextRange
    {
        /// <summary>
        /// Returns the <paramref name="limit"/> lines of <paramref name="content"/> starting at 1-based
        /// <paramref name="line"/>. Both null returns the content untouched — that is the common read
        /// and it must not pay for normalization it doesn't need.
        ///
        /// <para>A start past the end yields empty rather than throwing: the file was found, that range
        /// simply holds nothing. "Looked and there is nothing there" is an answer; only "couldn't look"
        /// is an error, and that distinction is the whole reason a missing FILE throws instead.</para>
        ///
        /// <para>The same holds for a range that is nonsense rather than merely empty, and BOTH ends of
        /// it are agent-supplied and unvalidated on every hop between the wire and here. A
        /// <paramref name="line"/> below 1 clamps to the first line; a negative
        /// <paramref name="limit"/> is zero lines, not "no limit" — handing back the whole file for a
        /// bad range is precisely the defect this type exists to prevent, and it is the silent one.</para>
        /// </summary>
        public static string Slice(string content, int? line, int? limit)
        {
            if (line is null && limit is null)
                return content;
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var start = System.Math.Max(1, line ?? 1) - 1;
            if (start >= lines.Length)
                return string.Empty;

            var available = lines.Length - start;
            var count = System.Math.Max(0, System.Math.Min(limit ?? available, available));
            return string.Join("\n", lines, start, count);
        }
    }
}
