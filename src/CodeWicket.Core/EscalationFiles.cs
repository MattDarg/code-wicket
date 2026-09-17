using System;
using System.Text.RegularExpressions;

namespace CodeWicket.Core
{
    /// <summary>
    /// The files that decide what an agent may do WITHOUT asking — the backends' own permission
    /// configuration — as a test a permission policy can put a write's path, or a command line, to.
    /// A hit is never auto-approved: not by a permission mode, not by an allow rule, not by a
    /// remembered "always". It is put to the user, flagged, with "Allow always" withheld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it catches, and what it does not</b> (issue #269 follow-on). The backend's permission
    /// gate runs BEFORE ours, and its inputs include files inside the working tree: claude-agent-acp
    /// reads <c>permissions.defaultMode</c> and <c>permissions.allow</c> from
    /// <c>&lt;cwd&gt;/.claude/settings.json</c> and <c>.claude/settings.local.json</c> (the latter is
    /// gitignored, so it is exempt from the SDK's own escalation filter); Kiro takes a workspace
    /// <c>.kiro/agents/&lt;name&gt;.json</c> over a built-in or configured agent of the same name,
    /// trust rules included, and v3 persists its always-rules in <c>permissions.yaml</c> under
    /// <c>.kiro</c>. Once one of those carries an allow, the agent stops asking and our policy sees
    /// nothing: no banner, no row mark, no log line. What IS visible, on every backend that asks
    /// about writes at all, is the escalation's FIRST step — the write that puts the rule there. This
    /// guard flags that step. It is a tripwire, not a wall: the pin in <c>AcpAgentSession</c> is what
    /// keeps Claude's mode ours, Kiro has no lever, and a backend that never asks about writes walks
    /// past this as it walks past everything else.
    /// </para>
    /// <para>
    /// <b>Why this cannot be a configured rule</b> — the same reason <see cref="ProtectedPaths"/> is
    /// code: an <c>AlwaysPromptCommands</c> entry would do it today and lives in config.json, which
    /// is one edit away under the same mode. So the list is here, with no representation an agent
    /// can edit.
    /// </para>
    /// <para>
    /// <b>Two shapes, because the write has two routes.</b> A file-scoped request carries a path, and
    /// the path is matched at its END (<see cref="MatchPath"/>) — the files are named the same under
    /// any root, the user's home included, so <c>~/.claude/settings.json</c> counts as much as the
    /// workspace copy. A shell command carries text, and the text is matched ANYWHERE
    /// (<see cref="MatchText"/>), in the caution list's own unanchored style: a wrapping cannot hide
    /// the fragment, and the fragment returned is the exact spelling on screen so the banner can
    /// call it out. On a command line a read cannot be told from a write — <c>cat</c> and
    /// <c>echo &gt;</c> both name the file — so a command asks in both directions; a file-scoped
    /// read is exempt, as it is for <see cref="ProtectedPaths"/>: what escalates is the file being
    /// REWRITTEN, and a read grants nothing.
    /// </para>
    /// <para>
    /// The three sentences the user reads are here because the wording is the behaviour (the
    /// <see cref="ProtectedPaths"/> reasoning): "matches your always-prompt rule" over a rule the user
    /// never wrote would send them to their settings to look for it.
    /// </para>
    /// </remarks>
    public static class EscalationFiles
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Sep = @"[\\/]";

        // The bodies, without anchoring: each is a spelling of one file with either separator.
        // Anchored differently for a path (must END the path) and for a command line (anywhere,
        // but not glued to a preceding word so `x.claude/…` is not read as the folder).
        private const string ClaudeSettings = @"\.claude" + Sep + @"settings(?:\.local)?\.json";
        private const string KiroPermissions = @"\.kiro" + Sep + @"(?:[^\s""'<>|]*" + Sep + @")?permissions\.yaml";

        // `.kiro/agents` differs by route. A file-scoped write always names a FILE, so the path
        // route wants one under the folder. A command line can target the folder itself -
        // `copy evil.json .kiro\agents`, `rmdir /s .kiro\agents` - and the folder holds nothing
        // but agent definitions, so on that route the bare folder counts (measured in Visual Studio: the
        // file-only shape let `dir .kiro\agents` through as an ordinary command). `.claude` is
        // not treated the same way: it holds commands, docs and settings alike, so the folder
        // says nothing and the file names stay the rule.
        private static readonly Regex[] PathPatterns =
        {
            Anchored(ClaudeSettings),
            Anchored(@"\.kiro" + Sep + @"agents" + Sep + @"[^\s""'<>|]+"),
            Anchored(KiroPermissions),
        };

        private static readonly Regex[] TextPatterns =
        {
            Loose(ClaudeSettings),
            Loose(@"\.kiro" + Sep + @"agents(?:" + Sep + @"[^\s""'<>|]*)?(?![\w.-])"),
            Loose(KiroPermissions),
        };

        private static Regex Anchored(string body) => new(@"(?:^|" + Sep + ")" + body + "$", Opts);
        private static Regex Loose(string body) => new(@"(?<![\w.-])" + body, Opts);

        /// <summary>
        /// Whether <paramref name="path"/> names one of the escalation files. Lexical, on the spelling
        /// given — nothing here touches the disk. A null or empty path matches nothing.
        /// </summary>
        public static bool MatchPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            foreach (var p in PathPatterns)
                if (p.IsMatch(path!))
                    return true;
            return false;
        }

        /// <summary>
        /// The first mention of an escalation file, or of the <c>.kiro/agents</c> folder, in
        /// <paramref name="text"/> (a command line, or the title and detail the policy scans for its
        /// caution list), as the exact substring matched, or null. Case-insensitive; either separator.
        /// </summary>
        public static string? MatchText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            foreach (var p in TextPatterns)
            {
                var m = p.Match(text!);
                if (m.Success)
                    return m.Value;
            }
            return null;
        }

        /// <summary>The banner's call-out for a flagged write, in place of "Matches your always-prompt rule".</summary>
        public static string WriteBannerReason => "Writes a file that sets what the agent may do without asking";

        /// <summary>The banner's call-out for a flagged command line.</summary>
        public static string CommandBannerReason => "Names a file that sets what the agent may do without asking";

        /// <summary>
        /// The transcript row's account of why the user was asked about a write, completing "You were
        /// asked because …". Says what the file is and that nothing in the user's settings can lift it.
        /// </summary>
        public static string WriteRowReason =>
            "the file sets what the agent may do without asking, and no permission mode or allow rule covers a write to it.";

        /// <summary>The row's account for a flagged command line.</summary>
        public static string CommandRowReason =>
            "the command names a file that sets what the agent may do without asking, and no permission mode or allow rule covers that.";
    }
}
