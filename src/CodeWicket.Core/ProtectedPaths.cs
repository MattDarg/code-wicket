using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CodeWicket.Core.Ide;

namespace CodeWicket.Core
{
    /// <summary>
    /// The files the extension keeps for ITSELF — <c>config.json</c>, the saved conversations, the
    /// logs — as a containment test a permission policy can ask about a write's target, and a
    /// spelling test it can put a command line to. A hit is never auto-approved: not by a permission
    /// mode, not by an allow rule, not by a remembered "always". It is put to the user, flagged, with
    /// "Allow always" withheld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this cannot be a configured rule</b> (pre-release security review, September 2026). The mode
    /// ladder classified every edit as the Edit tier without looking at where it landed, so under
    /// "Allow edits" a prompt-injected agent could write <c>%APPDATA%\code-wicket\config.json</c> with
    /// no banner. That one file is re-read by the running policy on the next settings save — mode,
    /// command allow-list, path allow-list, all of it — and names the executables and environment
    /// the next engine launch spawns. An <c>AlwaysPromptCommands</c> entry naming the folder would
    /// stop it today, and would itself be in the file the first write replaces. So the rule is code:
    /// it has no representation an agent can edit.
    /// </para>
    /// <para>
    /// <b>Two routes, as for <see cref="EscalationFiles"/>, and only the first is a containment test.</b>
    /// A file-scoped request carries a path, judged by <see cref="Contains"/> against the roots.
    /// Reads are exempt there: what this escalates through is the state being REWRITTEN, and a read
    /// grants nothing. The policy applies it to any file-scoped request that is not a read, so an
    /// unkinded request with a path is held to it too — the conservative direction.
    /// </para>
    /// <para>
    /// A shell command carries only text, and <see cref="MatchCommandText"/> looks in it for ONE
    /// spelling: <c>code-wicket\config.json</c>, either separator, any case. The bare file name
    /// would be every project's <c>config.json</c>; the bare folder name is also the Local root's,
    /// with the agent's no-solution workspace under it, so every command run there would be flagged.
    /// Only <c>config.json</c>, because it is the file that is read back into the running policy;
    /// the rest of the state is the path route's. On a command line a read cannot be told from a
    /// write, so a line that only reads the file is flagged too, and a repository folder that
    /// happens to be called <c>code-wicket</c> with a <c>config.json</c> in it is flagged as well —
    /// both the direction the caution tier errs in.
    /// </para>
    /// <para>
    /// <b>The command route is a spelling test and nothing more.</b> A line that writes the file
    /// without spelling it that way (<c>cd $env:APPDATA\code-wicket; Set-Content config.json …</c>, a
    /// copy into the folder, a script) passes it, and so does anything the command launches. Under
    /// Prompt and Allow-edits every command that REACHES this policy is put to the user anyway, so the
    /// flag is a red call-out on a banner already shown; under Allow-all it catches the literal
    /// spelling, which is what a model writes when it wants the file. And like every rule here it sees
    /// only a command the backend ASKS about: one the backend approves itself never reaches us, and
    /// nothing records that it ran. The check that does not depend on the spelling is on the
    /// FILE rather than the request, and is not built.
    /// </para>
    /// <para>
    /// <b>The exemptions are the agent's own workspaces</b>, which live under the Local root when no
    /// solution is open (<see cref="StoragePaths.DefaultAgentWorkspace"/>,
    /// <see cref="StoragePaths.StubAgentWorkspace"/>). Every edit in that mode targets a file under
    /// our state root, and guarding those would prompt on each one — a rule that fires on ordinary
    /// work is a rule that trains click-through. Nothing under Roaming is exempt.
    /// </para>
    /// <para>
    /// Containment is <see cref="WorkspacePath.IsUnderRoot"/>'s: lexical, after
    /// <c>Path.GetFullPath</c>, with the root compared as a prefix WITH its separator (so a sibling
    /// <c>code-wicket2</c> is outside). <b>It resolves no reparse points, and that gap is closed by
    /// <see cref="LinkedWrite"/> rather than here</b>: a link is not something a lexical test can
    /// see, and following one to find out where it lands is the operation
    /// <see cref="Ide.ReparsePoints"/> exists to refuse. So the write that reaches this folder
    /// through a junction is caught as a LINKED write — on the same caution tier, with its own
    /// sentences — instead of being missed by this one.
    /// </para>
    /// <para>
    /// The sentences the user reads are here rather than in the UI because the wording is the
    /// behaviour (the same reasoning as <see cref="FileWriteRefusal"/>): a banner that said "matches
    /// your always-prompt rule" over a rule the user never wrote would send them to their settings to
    /// find it.
    /// </para>
    /// </remarks>
    public sealed class ProtectedPaths
    {
        // The settings file under its folder, not glued to a longer name on either side: not
        // `my-code-wicket\…`, not `config.json.bad` (which nothing reads back).
        private static readonly Regex SettingsFileSpelling = new(
            @"(?<![\w.-])" + Regex.Escape(Branding.StorageFolderName) + @"[\\/]"
                + Regex.Escape(StoragePaths.SettingsFileName) + @"(?![\w.-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string[] _roots;
        private readonly string[] _exempt;

        /// <param name="roots">Directories whose contents are the host's own state.</param>
        /// <param name="exempt">
        /// Directories under <paramref name="roots"/> that belong to the agent instead. A path under one
        /// of these is not protected, however deep.
        /// </param>
        public ProtectedPaths(IEnumerable<string> roots, IEnumerable<string>? exempt = null)
        {
            _roots = Clean(roots);
            _exempt = Clean(exempt);
        }

        /// <summary>
        /// The extension's own roots (<see cref="StoragePaths.Roaming"/>, <see cref="StoragePaths.Local"/>),
        /// less the agent workspaces under Local. Resolved on each call because the special folders can
        /// be redirected per process; never touches the file system.
        /// </summary>
        public static ProtectedPaths Default => new(
            new[] { StoragePaths.Roaming, StoragePaths.Local },
            new[] { StoragePaths.DefaultAgentWorkspace, StoragePaths.StubAgentWorkspace });

        /// <summary>A guard that protects nothing — for hosts and tests that opt out explicitly.</summary>
        public static ProtectedPaths None => new(Array.Empty<string>());

        /// <summary>
        /// Whether <paramref name="path"/> is inside a protected root and outside every exemption. A
        /// null, empty, relative or unresolvable path is reported false: the policy has no subject to
        /// judge, and the path allow-list already cannot match one either.
        /// </summary>
        public bool Contains(string? path)
        {
            if (string.IsNullOrEmpty(path) || !AgentPath.IsRooted(path!))
                return false;
            if (!_roots.Any(r => WorkspacePath.IsUnderRoot(path, r)))
                return false;
            return !_exempt.Any(e => WorkspacePath.IsUnderRoot(path, e));
        }

        /// <summary>
        /// The first spelling of the extension's settings file (<c>code-wicket\config.json</c>, either
        /// separator, any case) in <paramref name="text"/> — a command line, or the title and detail the
        /// policy scans for its caution list — as the exact substring matched, or null. Lexical; a
        /// spelling test, not a statement about which file a command touches. Always null for a guard
        /// with no roots (<see cref="None"/>), so a host that opts out opts out of both routes.
        /// </summary>
        public string? MatchCommandText(string? text)
        {
            if (_roots.Length == 0 || string.IsNullOrEmpty(text))
                return null;
            var m = SettingsFileSpelling.Match(text!);
            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// The banner's call-out for a flagged write here, in place of "Matches your always-prompt
        /// rule". The banner appends the path.
        /// </summary>
        public static string BannerReason => $"Writes {Branding.ProductName}'s own settings";

        /// <summary>
        /// The transcript row's account of why the user was asked, completing "You were asked because …".
        /// Says what the rule is and that nothing in the user's settings can lift it, so a reader who
        /// has an allow rule covering the folder is not sent looking for why it did not apply.
        /// </summary>
        public static string RowReason =>
            $"the file is part of {Branding.ProductName}'s own settings, which no permission mode or allow rule covers.";

        /// <summary>
        /// The banner's call-out for a flagged command line. "Names", not "writes": the line cannot be
        /// read that far.
        /// </summary>
        public static string CommandBannerReason => $"Names {Branding.ProductName}'s own settings file";

        /// <summary>The row's account for a flagged command line.</summary>
        public static string CommandRowReason =>
            $"the command names {Branding.ProductName}'s own settings file, and no permission mode or allow rule covers that.";

        private static string[] Clean(IEnumerable<string>? dirs) =>
            (dirs ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToArray();
    }
}
