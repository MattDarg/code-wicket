using System.Text;
using System.Text.RegularExpressions;

namespace CodeWicket.Core
{
    /// <summary>
    /// The one glob language every permission rule is written in — the command allow-list, the
    /// caution list, the path allow-list, and each of their session-remembered twins. <c>*</c> matches
    /// any run of characters, <c>?</c> any one, and <c>[*]</c> / <c>[?]</c> are those two characters
    /// taken literally. Nothing else is special.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why there is an escape</b> (pre-release security review, September 2026). The banner's "always"
    /// step seeds its editable pattern with the command the agent wrote, and that text is then
    /// compiled as a glob. A wildcard IN the command - <c>find . -name "*.cs"</c>, <c>git add *</c> -
    /// therefore turned into a wildcard in the RULE the moment the user accepted the default, and
    /// <c>find . -name "x"; calc; echo ".cs"</c> matched a rule the user believed was one literal
    /// command. The agent chooses the command text, so it chooses whether the seeded rule is wide.
    /// With an escape, the banner can seed exactly the command shown (<see cref="Escape"/>), and a
    /// rule is only ever as wide as the USER typed it.
    /// </para>
    /// <para>
    /// <b>Why the escape is <c>[*]</c> and not a backslash.</b> Every rule that reaches here is a
    /// Windows command line or an absolute Windows path, and both are full of backslashes that mean
    /// nothing of the kind; a backslash escape would have to be doubled everywhere a path appears and
    /// would silently change the meaning of every rule saved before it existed. A bracket pair never
    /// occurs in a path and is rare enough in a command that it reads as what it is. <c>[</c> on its
    /// own, or around anything but a wildcard, stays a literal bracket - this is not a character
    /// class, and widening it into one would be a second glob language nobody asked for.
    /// </para>
    /// <para>
    /// <b>Existing saved rules keep their meaning.</b> A <c>*</c> already in the user's config was
    /// written under the old rule and stays a wildcard; nobody can tell now whether it was meant
    /// literally, and quietly narrowing a rule is as much a surprise as quietly widening one.
    /// </para>
    /// <para>
    /// In Core because the banner (UI) writes the escaped form and the policy (Shell) reads it, and
    /// the two must agree on one grammar or a rule would not survive its own round trip. Same
    /// reasoning as the tool-rule normalizer (issue #129).
    /// </para>
    /// </remarks>
    public static class PermissionGlob
    {
        /// <summary>A glob that matches exactly <paramref name="text"/>: its wildcards taken literally.</summary>
        public static string Escape(string text) =>
            text.Replace("*", "[*]").Replace("?", "[?]");

        /// <summary>
        /// Whether <paramref name="glob"/> has a wildcard in it once escapes are read - the rules the
        /// shell-operator refusal applies to (<see cref="ShellOperators"/>). An escaped <c>[*]</c> is
        /// not one: that rule means exactly its text and is exempt like any other exact rule.
        /// </summary>
        public static bool HasWildcard(string glob)
        {
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '[' && i + 2 < glob.Length && glob[i + 2] == ']' && (glob[i + 1] == '*' || glob[i + 1] == '?'))
                    i += 2;
                else if (c == '*' || c == '?')
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The case-insensitive regex a glob compiles to. <paramref name="anchored"/> pins it to the
        /// whole string (<c>^…$</c>) - an allow rule - and false leaves it a substring match anywhere,
        /// the caution list's deliberately aggressive shape.
        /// </summary>
        public static Regex ToRegex(string glob, bool anchored)
        {
            var g = glob.Trim();
            var body = new StringBuilder(g.Length + 8);
            for (var i = 0; i < g.Length; i++)
            {
                var c = g[i];
                if (c == '[' && i + 2 < g.Length && g[i + 2] == ']' && (g[i + 1] == '*' || g[i + 1] == '?'))
                {
                    body.Append('\\').Append(g[i + 1]);
                    i += 2;
                }
                else if (c == '*')
                    body.Append(".*");
                else if (c == '?')
                    body.Append('.');
                else
                    body.Append(Regex.Escape(c.ToString()));
            }

            if (anchored)
                body.Insert(0, '^').Append('$');
            // Singleline, so `*` spans a line break. Without it a multi-line command line was refused
            // by ACCIDENT - `.*` stopping at the newline - and the user was told nothing; with it the
            // rule matches and ShellOperators withholds it explicitly, a line break being an operator
            // in every shell (pre-release security review, September 2026).
            return new Regex(body.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }
    }
}
