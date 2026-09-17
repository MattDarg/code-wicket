using System;
using System.Text;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Pure text shaping for the terminal mirror (the VSIX-side pane rendering agent command
    /// output). Lives in Shell so the logic is unit-testable
    /// from the net10 test project; the VS-specific pane plumbing stays in the VSExtension.
    /// </summary>
    public static class TerminalMirrorText
    {
        /// <summary>
        /// The command an agent's <c>rawInput</c> says it is running, or null when it names none.
        /// </summary>
        /// <remarks>
        /// Reading one backend-specific key rather than decoding a shape: <c>command</c> means the same
        /// thing wherever it appears on an execute-kind call, and the permission path already resolves
        /// the subject from exactly this field. Anything unparseable is null, and the caller falls back
        /// to the row's title — a mirror is a convenience and must never throw into the event stream.
        /// </remarks>
        public static string? CommandIn(string? rawInputJson)
        {
            if (string.IsNullOrWhiteSpace(rawInputJson))
                return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rawInputJson!);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return null;
                if (!doc.RootElement.TryGetProperty("command", out var command))
                    return null;
                return command.ValueKind == System.Text.Json.JsonValueKind.String
                    ? command.GetString()
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>ANSI-colored separator written between mirrored commands.</summary>
        public static string Header(string? title)
        {
            var clean = SanitizeTitle(title);
            if (clean.Length == 0)
                clean = "command";
            return "\x1b[36m── " + clean + " ──\x1b[0m\r\n";
        }

        /// <summary>
        /// Terminal newline discipline: every bare LF becomes CRLF (existing CRLFs untouched; lone
        /// CRs left alone — they may be progress-bar rewrites the terminal should honor).
        /// </summary>
        public static string NormalizeNewlines(string? text)
        {
            if (string.IsNullOrEmpty(text) || text!.IndexOf('\n') < 0)
                return text ?? string.Empty;

            var sb = new StringBuilder(text.Length + 16);
            var prev = '\0';
            foreach (var ch in text)
            {
                if (ch == '\n' && prev != '\r')
                    sb.Append('\r');
                sb.Append(ch);
                prev = ch;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Titles come from agent-controlled tool metadata, so strip control characters (no ANSI
        /// injection into our pane, no line breaks inside the separator) and clamp the length.
        /// </summary>
        public static string SanitizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var sb = new StringBuilder(Math.Min(title!.Length, 80));
            foreach (var ch in title.Trim())
            {
                if (sb.Length >= 80)
                {
                    sb.Append('…');
                    break;
                }
                sb.Append(char.IsControl(ch) ? ' ' : ch);
            }
            return sb.ToString();
        }
    }
}
