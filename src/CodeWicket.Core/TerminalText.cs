using System.Text;

namespace CodeWicket.Core
{
    /// <summary>
    /// Turns raw terminal / CI-log text into what the terminal would actually have DISPLAYED:
    /// ANSI escapes removed (<see cref="AnsiText"/>) and carriage-return overwrites applied.
    ///
    /// The motivating case is a GitLab job trace pasted into the chat. The raw trace carries a
    /// colour escape around every command (<c>ESC[32;1m</c> … <c>ESC[0;m</c>), an erase-to-end-of-line
    /// after most of them (<c>ESC[0K</c>), fold markers written as
    /// <c>section_start:&lt;ts&gt;:&lt;name&gt;CR ESC[0K</c>, and progress output that rewrites one line
    /// with CR. None of that is text a terminal ever shows — ESC has no glyph in the chat font, so it
    /// renders as a missing-glyph box with the parameter bytes trailing after it, and a lone CR is
    /// not a line break, so overwritten lines run together.
    ///
    /// Cleaning is therefore not a reinterpretation of the paste, it's the terminal's own reading of
    /// it. The one genuinely ambiguous input is CR-only line endings (classic Mac), guarded below.
    /// </summary>
    public static class TerminalText
    {
        /// <summary>
        /// True when <see cref="Clean"/> would change something. Lets a caller leave ordinary text
        /// strictly alone (an ordinary Windows-newline paste is not "cleaned" into LF endings).
        /// </summary>
        public static bool NeedsCleaning(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            for (var i = 0; i < text!.Length; i++)
            {
                var c = text[i];
                if (c == '\x1b' || c == '\a')
                    return true;
                // A CR that isn't the CR of a CRLF pair is an overwrite, not a line ending.
                if (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
                    return true;
            }
            return false;
        }

        /// <summary>Strips escape sequences, then applies CR overwrites to what's left.</summary>
        /// <remarks>Order matters: the erase in <c>CR ESC[0K</c> sits between the CR and the text
        /// that replaced the line, so stripping first leaves the CR adjacent to its replacement.</remarks>
        public static string Clean(string? text) => ApplyCarriageReturns(AnsiText.Strip(text));

        /// <summary>
        /// Collapses each line to the text that survived its carriage returns. CRLF pairs are line
        /// endings and pass through untouched; only a CR *within* a line is an overwrite.
        /// </summary>
        public static string ApplyCarriageReturns(string? text)
        {
            if (string.IsNullOrEmpty(text) || text!.IndexOf('\r') < 0)
                return text ?? string.Empty;

            // No LF anywhere: every CR is a line terminator (classic-Mac endings), and overwrite
            // semantics would collapse the whole paste to its last line. This also catches a
            // single line whose overwrite we'd otherwise apply — deliberately, because when the
            // two readings are indistinguishable the non-lossy one (a spurious line break) is the
            // safer miss. Guessing the other way silently deletes content.
            if (text.IndexOf('\n') < 0)
                return text.Replace('\r', '\n');

            var sb = new StringBuilder(text.Length);
            var start = 0;
            while (true)
            {
                var newline = text.IndexOf('\n', start);
                AppendLine(sb, text, start, newline < 0 ? text.Length : newline);
                if (newline < 0)
                    break;
                sb.Append('\n');
                start = newline + 1;
            }
            return sb.ToString();
        }

        // Appends one line — text[start..end), no LF — with its CR overwrites applied. Everything
        // before the last CR was written to the same columns and then written over, so only the
        // final segment was ever on screen.
        //
        // This is the pragmatic rule (keep the last segment), not true terminal emulation: a real
        // terminal overwrites column by column and leaves a longer line's tail behind, which is
        // exactly why tools pair the CR with an erase. Once AnsiText has removed the erase we can
        // no longer tell overwrite from erase, so we take the last segment — the same thing
        // GitLab's own log view, GitHub Actions and Jenkins all effectively do.
        private static void AppendLine(StringBuilder sb, string text, int start, int end)
        {
            // A single trailing CR is the CR of a CRLF pair: preserved, so an ordinary
            // Windows-newline paste round-trips byte for byte.
            var crLf = end > start && text[end - 1] == '\r';
            if (crLf)
                end--;

            if (end > start)
            {
                var lastCr = text.LastIndexOf('\r', end - 1, end - start);
                if (lastCr >= 0)
                    start = lastCr + 1;
                sb.Append(text, start, end - start);
            }

            if (crLf)
                sb.Append('\r');
        }
    }
}
