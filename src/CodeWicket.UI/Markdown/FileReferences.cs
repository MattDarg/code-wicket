using System;
using System.Collections.Generic;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// One file reference found in agent prose: the span it occupies in the source text, the path
    /// exactly as written (the resolver's cache key — normalisation is part of the work being cached)
    /// and the line it named, if any.
    /// </summary>
    public readonly struct FileReferenceMatch
    {
        public FileReferenceMatch(int index, int length, string pathText, int? line)
        {
            Index = index;
            Length = length;
            PathText = pathText;
            Line = line;
        }

        /// <summary>Start of the whole reference (path + any line suffix) in the scanned string.</summary>
        public int Index { get; }

        /// <summary>Length of the whole reference, so the caller can slice the display text.</summary>
        public int Length { get; }

        /// <summary>The path portion as written, without the line suffix.</summary>
        public string PathText { get; }

        /// <summary>The line the reference named; null when it named only a file.</summary>
        public int? Line { get; }

        /// <summary>Where to open: the named line, or the top of the file.</summary>
        public int OpenLine => Line ?? 1;
    }

    /// <summary>
    /// Finds <c>path:line</c>-style file references in agent prose. Both backends emit that convention
    /// unprompted (measured across the saved sessions), so this recognises what they already write
    /// rather than inventing a markdown scheme.
    /// <para>
    /// Deliberately permissive about shape and silent about meaning: there is <b>no extension
    /// whitelist</b> (it would be a maintenance list that omits whatever the user's repo actually
    /// contains) — anything with a <c>name.ext</c> tail matches here and
    /// <see cref="FileReferenceResolver"/> is the gate. <c>System.Text.Json</c> matches the token
    /// shape and simply fails to resolve, which costs a dictionary entry and nothing else.
    /// </para>
    /// <para>
    /// Hand-written rather than a regex because it runs on model output on the render path: a single
    /// forward pass is <b>linear by construction</b>, where a pattern with a quantified path-character
    /// class ahead of the extension backtracks quadratically over a long run of word characters, and
    /// <c>RegexOptions.NonBacktracking</c> is .NET 7+ (unavailable on this project's net472 slice).
    /// </para>
    /// </summary>
    public static class FileReferenceMatcher
    {
        private static readonly FileReferenceMatch[] None = new FileReferenceMatch[0];

        /// <summary>Longest tail accepted as an extension; past this it isn't one.</summary>
        private const int MaxExtensionLength = 16;

        /// <summary>
        /// All references in <paramref name="text"/>, in order, non-overlapping. Never throws — the
        /// same contract <see cref="CodeHighlighter"/> holds, since this also runs over the partial,
        /// syntactically incomplete text of a mid-stream delta.
        /// </summary>
        public static IReadOnlyList<FileReferenceMatch> Scan(string? text)
        {
            // "a.b" is the shortest thing that could be one.
            if (text is null || text.Length < 3)
                return None;

            List<FileReferenceMatch>? found = null;
            var i = 0;
            while (i < text.Length)
            {
                if (!IsPathChar(text[i]))
                {
                    i++;
                    continue;
                }

                // Maximal run of path characters. ':' normally terminates the run (it introduces the
                // line suffix) — the one exception is a drive prefix, "C:\", recognised by position.
                var start = i;
                var j = i;
                while (j < text.Length)
                {
                    if (IsPathChar(text[j]))
                    {
                        j++;
                        continue;
                    }
                    if (text[j] == ':' && j == start + 1 && IsAsciiLetter(text[start])
                        && j + 1 < text.Length && IsSeparator(text[j + 1]))
                    {
                        j += 2;
                        continue;
                    }
                    break;
                }

                // Advance past the whole run whatever happens next, so a rejected candidate can never
                // be rescanned from its second character (that is where a linear scan turns quadratic).
                i = j;

                // Sentence punctuation the run swallowed: "see Foo.cs." and "under src/".
                var end = j;
                while (end > start && (text[end - 1] == '.' || IsSeparator(text[end - 1])))
                    end--;

                if (!HasFileExtension(text, start, end))
                    continue;

                var (line, referenceEnd) = ReadLineSuffix(text, end);
                found ??= new List<FileReferenceMatch>();
                found.Add(new FileReferenceMatch(
                    start, referenceEnd - start, text.Substring(start, end - start), line));
                if (referenceEnd > i)
                    i = referenceEnd;
            }

            return found ?? (IReadOnlyList<FileReferenceMatch>)None;
        }

        /// <summary>
        /// True when <c>[start, end)</c> ends in a <c>name.ext</c> tail. The extension must start with a
        /// letter, which is what keeps version numbers ("net8.0", "MessagePack 2.5.301") out — they match
        /// every other rule. A leading-dot name (".gitignore") is a name without an extension, so it is
        /// not matched: accepting it would also linkify every ".NET" in prose.
        /// </summary>
        private static bool HasFileExtension(string text, int start, int end)
        {
            var segment = start;
            for (var k = end - 1; k >= start; k--)
            {
                if (IsSeparator(text[k]))
                {
                    segment = k + 1;
                    break;
                }
            }
            if (segment >= end)
                return false;

            var dot = -1;
            for (var k = end - 1; k > segment; k--)
            {
                if (text[k] == '.')
                {
                    dot = k;
                    break;
                }
            }
            if (dot < 0)
                return false;

            var extensionLength = end - dot - 1;
            if (extensionLength < 1 || extensionLength > MaxExtensionLength)
                return false;
            if (!IsAsciiLetter(text[dot + 1]))
                return false;
            for (var k = dot + 2; k < end; k++)
            {
                if (!char.IsLetterOrDigit(text[k]) && text[k] != '_')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the line suffix at <paramref name="end"/>: <c>:42</c> (the dominant form), <c>:9-11</c>
        /// or <c>:8:52</c> (see <see cref="ReadTrailingLineDetail"/>), or the MSBuild <c>(42,7)</c> the
        /// agent writes when quoting a build error. Returns the line and the index just past the whole
        /// reference.
        /// </summary>
        private static (int? Line, int End) ReadLineSuffix(string text, int end)
        {
            if (end + 1 < text.Length && text[end] == ':' && IsDigit(text[end + 1]))
            {
                var k = end + 1;
                while (k < text.Length && IsDigit(text[k]))
                    k++;
                return TryLine(text, end + 1, k, out var line)
                    ? (line, ReadTrailingLineDetail(text, k))
                    : ((int?)null, end);
            }

            if (end + 2 < text.Length && text[end] == '(' && IsDigit(text[end + 1]))
            {
                var k = end + 1;
                while (k < text.Length && IsDigit(text[k]))
                    k++;
                if (!TryLine(text, end + 1, k, out var line))
                    return (null, end);
                if (k < text.Length && text[k] == ',')
                {
                    var column = k + 1;
                    while (column < text.Length && IsDigit(text[column]))
                        column++;
                    if (column == k + 1)
                        return (null, end);
                    k = column;
                }
                if (k < text.Length && text[k] == ')')
                    return (line, k + 1);
            }

            return (null, end);
        }

        /// <summary>
        /// Swallows what can follow the line in a <c>path:line</c> reference: a range end
        /// (<c>IdeMcpServer.cs:9-11</c>) or a column (<c>ATestClass.cs:8:52</c>). Returns the index just
        /// past whichever it found, else <paramref name="k"/> unchanged.
        /// </summary>
        /// <remarks>
        /// Both forms are in the corpus the matcher was scoped against — the hyphen range in 3 sessions,
        /// the column in 2 — and neither changes where we open: a range opens at its start, and the
        /// column is a position within the line the editor is already scrolling to. What they change is
        /// the <em>span</em>: without this the link stopped at the line and the tail rendered as plain
        /// text beside it, so one reference read as a link with debris after it. (An en dash and
        /// GitHub's <c>#L9</c> never appear in that corpus, so neither is recognised.)
        /// <para>
        /// A tail that isn't a plausible number is simply not swallowed, which leaves today's behaviour
        /// rather than inventing a rule for it.
        /// </para>
        /// </remarks>
        private static int ReadTrailingLineDetail(string text, int k)
        {
            if (k + 1 >= text.Length || (text[k] != '-' && text[k] != ':') || !IsDigit(text[k + 1]))
                return k;

            var m = k + 1;
            while (m < text.Length && IsDigit(text[m]))
                m++;
            return TryLine(text, k + 1, m, out _) ? m : k;
        }

        private static bool TryLine(string text, int from, int to, out int? line)
        {
            line = null;
            // A digit run long enough to overflow isn't a line number.
            if (to - from > 9 || !int.TryParse(text.Substring(from, to - from), out var value) || value <= 0)
                return false;
            line = value;
            return true;
        }

        // '~' and '+' are deliberately absent: they start runs far more often in prose than in the
        // paths we can resolve, and anything they'd add ("~/.kiro/...") is outside the workspace root
        // and therefore never linkable anyway.
        private static bool IsPathChar(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || IsSeparator(c);

        private static bool IsSeparator(char c) => c == '/' || c == '\\';

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool IsDigit(char c) => c >= '0' && c <= '9';
    }
}
