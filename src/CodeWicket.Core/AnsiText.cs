using System;
using System.Text;

namespace CodeWicket.Core
{
    /// <summary>
    /// Strips ANSI/VT escape sequences from terminal output — a ConPTY-run command's captured
    /// stream is full of color/cursor sequences the agent payload shouldn't carry. Lives in Core
    /// (a pure string utility with no deps) because both the Ide catalog and the test project need
    /// it, and Ide deliberately doesn't reference Shell.
    /// </summary>
    public static class AnsiText
    {
        /// <summary>Removes CSI (<c>ESC[</c>…), OSC (<c>ESC]</c>…BEL/ST), DCS-family and two-char
        /// escape sequences, plus stray BELs. Printable text, tabs and line endings pass through.</summary>
        public static string Strip(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            if (text!.IndexOf('\x1b') < 0 && text.IndexOf('\a') < 0)
                return text;

            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\a')
                    continue; // stray BEL
                if (c != '\x1b')
                {
                    sb.Append(c);
                    continue;
                }

                if (++i >= text.Length)
                    break;
                switch (text[i])
                {
                    case '[': // CSI: parameter/intermediate bytes until a final byte in @..~
                        while (++i < text.Length && (text[i] < '@' || text[i] > '~')) { }
                        break;
                    case ']': // OSC: terminated by BEL or ST (ESC \)
                        while (++i < text.Length)
                        {
                            if (text[i] == '\a')
                                break;
                            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                            {
                                i++;
                                break;
                            }
                        }
                        break;
                    case 'P': // DCS / SOS / PM / APC: terminated by ST
                    case 'X':
                    case '^':
                    case '_':
                        while (++i < text.Length &&
                               !(text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')) { }
                        i++; // consume the backslash of ST
                        break;
                    default:
                        // ESC + intermediates (0x20–0x2F, e.g. charset designation "ESC ( B") end
                        // at the first final byte (0x30–0x7E); a plain two-char escape has none.
                        while (i < text.Length && text[i] >= ' ' && text[i] <= '/')
                            i++;
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
