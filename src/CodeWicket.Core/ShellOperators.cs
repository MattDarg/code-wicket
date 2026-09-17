namespace CodeWicket.Core
{
    /// <summary>
    /// The characters that can make one command line into two - or into a file write - in ANY of the
    /// shells a backend might hand the line to. A wildcard allow rule never applies to a line that
    /// contains one; the user is asked instead, and told why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A refusal, never a detector</b> (pre-release security review, September 2026). A saved
    /// <c>git *</c> rule - the widening the banner itself suggests - matched
    /// <c>git status &amp;&amp; powershell -enc …</c> whole, because <c>.*</c> is happy to cover an
    /// operator. The obvious fix, splitting the line on operators and checking each part, cannot be
    /// done honestly: which shell runs the line is the backend's choice and not ours, the three in
    /// play (bash, PowerShell, cmd) disagree about what an operator is, and quoting decides whether
    /// any of them counts. A parser that got that wrong would fail silently in the dangerous
    /// direction, and would still be blind to arguments that execute with no operator at all
    /// (<c>git -c core.pager=calc</c>, <c>find -exec</c>, <c>bash -c</c>). So the claim made here is
    /// the small one that can be kept: a rule with a wildcard in it never auto-approves a line that
    /// contains one of these characters, quoting ignored. It errs only towards the banner.
    /// </para>
    /// <para>
    /// <b>The set is the UNION over the shells, and it is code, not config.</b> The union is what
    /// makes a fixed list practical - nothing here needs to know which shell it is - and it is
    /// stable in a way a grammar is not. It is not configurable because the only safe edit is an
    /// addition, and the caution list (<c>AlwaysPromptCommands</c>, substring-matched against the
    /// command) already is that; a floor that config can lower is a floor an attacker can lower.
    /// Chosen broad on purpose (user, 2026-09-10: "broader is safer"): <c>(</c> closes PowerShell's
    /// argument subexpression, <c>&gt;</c> and <c>&lt;</c> close redirection - a write to any file
    /// by a rule that named a program - at the cost of a banner on
    /// <c>git commit -m "fix (typo)"</c> under a <c>git *</c> rule. <c>$(</c> and backticks are
    /// substitution; <c>&amp;</c>, <c>|</c>, <c>;</c> and a line break are separators everywhere.
    /// </para>
    /// <para>
    /// <b>Exact rules are exempt</b>, because they already match the whole string the user approved,
    /// operators and all; this narrows only what a wildcard grants. And it is NOT the caution tier:
    /// the line falls through to the mode and then to the ORDINARY banner, with one sentence saying
    /// which rule was not applied and why. A red banner here would train the click-through the red
    /// exists to prevent.
    /// </para>
    /// </remarks>
    public static class ShellOperators
    {
        /// <summary>
        /// The first operator in <paramref name="command"/>, spelled as the user will recognise it
        /// (<c>&amp;&amp;</c>, <c>||</c>, <c>$(</c>, "a line break"), or null when the line carries none.
        /// </summary>
        public static string? Find(string? command)
        {
            if (string.IsNullOrEmpty(command))
                return null;

            for (var i = 0; i < command!.Length; i++)
            {
                switch (command[i])
                {
                    case '&': return Doubled(command, i, '&') ? "&&" : "&";
                    case '|': return Doubled(command, i, '|') ? "||" : "|";
                    case ';': return ";";
                    case '\n':
                    case '\r': return "a line break";
                    case '`': return "`";
                    case '(': return i > 0 && command[i - 1] == '$' ? "$(" : "(";
                    case '>': return ">";
                    case '<': return "<";
                }
            }

            return null;
        }

        private static bool Doubled(string s, int i, char c) => i + 1 < s.Length && s[i + 1] == c;

        /// <summary>
        /// What the banner says when a wildcard rule was withheld. Names the rule and the operator, so
        /// the user is not left asking why a rule they can see in their settings did not apply.
        /// </summary>
        public static string WithheldSentence(string glob, string @operator) =>
            $"Your rule “{glob}” was not applied because the command contains {Spell(@operator)}. "
            + "A rule with a wildcard never covers a line with a shell operator in it.";

        /// <summary>
        /// The one-line hint under an editable command glob: what <c>*</c> grants, and what it never does.
        /// </summary>
        public const string WideningHint =
            "* matches anything and ? one character ([*] and [?] for the characters themselves). "
            + "A rule with a wildcard never applies to a line containing a shell operator "
            + "(& | ; > < ( ` or a line break) — that always asks.";

        private static string Spell(string op) => op == "a line break" ? op : "“" + op + "”";
    }
}
