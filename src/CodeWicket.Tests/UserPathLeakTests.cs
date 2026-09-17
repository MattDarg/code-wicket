using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Scans everything the public repository will contain for a path naming a real developer's user
    /// profile, so a fixture or a pasted log written against the machine it was authored on is caught
    /// here rather than read by a stranger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The discrimination is NOT "an absolute path under <c>C:\Users</c>" — six of those are correct
    /// and deliberate. A fixture often needs to look like a real Windows path (the log-directory
    /// readouts, the file-reference resolver's counter-example), and the convention this guard
    /// enforces is that the profile segment is a PLACEHOLDER. So the scan captures the user name and
    /// judges that, which makes it line-level: every file is scanned, and no file is ever exempt
    /// wholesale. A per-file allowlist would have been a rubber stamp on exactly the lines a future
    /// change is about — including on this file, which has to spell a real name to prove the scan
    /// catches one. Only its negative-case assertions are spared, so a real path added anywhere else
    /// in it still fails.
    /// </para>
    /// <para>
    /// It generalises past any one author. A literal search for the name that prompted it would pass
    /// forever on the next contributor's leak, which is the failure this is written to survive.
    /// </para>
    /// <para>
    /// <b>It scans prose as well as code</b>, which it did not always do — and the gap was not
    /// theoretical. Two real user paths reached <c>docs/engineering/</c> when the engineering notes
    /// moved into the published tree, and neither was visible to a scan pointed at <c>src/</c>. Only
    /// <c>.claude/</c> is skipped, because it is excluded from the import: the maintainer's own notes
    /// are free to name the maintainer's own machine.
    /// </para>
    /// <para>
    /// Unlike <see cref="RenameResidueTests"/> this has no dead-entry test. That list is a shrinking
    /// compatibility surface, where an entry outliving its reason is stale evidence; this one is a
    /// convention, where an unused placeholder is not stale but simply the next one somebody reaches
    /// for. It shrinks for nobody and is meant to be read as a menu.
    /// </para>
    /// </remarks>
    public sealed class UserPathLeakTests
    {
        /// <summary>
        /// An absolute Windows profile path, capturing the user-name segment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The name is bounded to path-name characters rather than "anything up to the next
        /// separator", because most occurrences here are inside doc comments and markup: a class of
        /// <c>[^\]+</c> would capture <c>someone&lt;/c&gt;</c> out of <c>&lt;c&gt;C:\Users\someone&lt;/c&gt;</c>,
        /// which matches no placeholder and fails the guard on a correct line.
        /// </para>
        /// <para>
        /// <b>The separator is one OR MORE, and either slash.</b> A path inside a fixture is often a
        /// path inside a string inside another format — a Windows path in a JSON payload written as a
        /// C# literal carries <c>\\</c> between every segment — and a single-backslash pattern reads
        /// straight past it. One published fixture leaked that way: the scan was green over
        /// <c>c:\\Users\\…</c> for as long as the escaped form existed. A forward slash is accepted
        /// for the same reason, since a path that has been through a URL or a POSIX-flavoured tool
        /// names the same profile.
        /// </para>
        /// </remarks>
        private static readonly Regex ProfilePath = new(
            @"[A-Za-z]:[\\/]+Users[\\/]+(?<name>[A-Za-z0-9._-]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The same path with its separators flattened to hyphens, which is how Claude Code names a
        /// project directory in its own store (<c>~/.claude/projects/C--Users-someone-...</c>).
        /// </summary>
        /// <remarks>
        /// This one is here because a leak in this spelling reached <c>docs/engineering/</c> and
        /// <see cref="ProfilePath"/> read straight past it: the two encodings share no literal
        /// characters between the drive letter and the name. <b>The encodings are enumerated, not
        /// inferred</b>, so a third one is a gap this scan cannot know it has — which is why the
        /// review checklist carries the same question rather than delegating it here.
        /// <para>
        /// The name cannot contain a hyphen in this form, because a hyphen is the separator. A user
        /// name with one in it reads as a shorter name and a longer tail, and is not caught.
        /// </para>
        /// </remarks>
        private static readonly Regex FlattenedProfilePath = new(
            @"[A-Za-z]--Users-(?<name>[A-Za-z0-9._]+)-",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static IEnumerable<Match> ProfileMatches(string line) =>
            ProfilePath.Matches(line).Concat(FlattenedProfilePath.Matches(line));

        /// <summary>
        /// The user names a fixture may spell. Four are in use; the rest are here so the next person
        /// needing one picks a stand-in off a list rather than typing their own name.
        /// </summary>
        private static readonly HashSet<string> Placeholders = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "someone", "me", "x", "you", "user", "username", "test", "example",
        };

        [Fact]
        public void NoPublishedFileNamesARealUserProfile()
        {
            var offenders = new List<string>();
            foreach (var file in SourceTree.PublishedFiles())
            {
                var isSelf = file.Name == "UserPathLeakTests.cs";
                var lines = File.ReadAllLines(file.FullName);
                for (var i = 0; i < lines.Length; i++)
                {
                    // This file's own negative cases must spell a real name; nothing else here may.
                    if (isSelf && lines[i].Contains("Assert.False(IsClean("))
                        continue;

                    foreach (var match in ProfileMatches(lines[i]))
                    {
                        if (NamesNobody(match.Groups["name"].Value))
                            continue;

                        offenders.Add($"{Relative(file)}({i + 1}): {lines[i].Trim()}");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "These name a real user profile in a path literal. This repository is published, so the "
                + "path says whose machine it was written on. Use one of the placeholder names instead ("
                + string.Join(", ", Placeholders.OrderBy(p => p)) + "):"
                + System.Environment.NewLine + string.Join(System.Environment.NewLine, offenders));
        }

        /// <summary>
        /// The scan has no allowlisted file to prove itself against, so it states its discrimination
        /// directly: the leak it exists to catch, and the four correct spellings already in the tree
        /// that it must never touch. Without the negative cases the guard would fail six legitimate
        /// fixtures on its first run, and be switched off rather than fixed.
        /// </summary>
        [Fact]
        public void TheScanCatchesARealNameAndSparesThePlaceholders()
        {
            Assert.True(SourceTree.Files().Any(), "The source enumeration is empty; every scan over it is vacuous.");

            // The instrument must prove it can see a known-present thing before its silence means
            // anything: the leak this scan was widened for was in docs/, not src/.
            // Both read the repo-relative path: a checkout under .claude/worktrees/ has .claude in every
            // full path, which is how the enumeration once came back empty.
            var published = SourceTree.PublishedFiles().Select(f => "/" + Relative(f)).ToList();
            Assert.Contains(published, f => f.StartsWith("/docs/") && f.EndsWith(".md"));
            Assert.DoesNotContain(published, f => f.Contains("/.claude/"));

            // What is scanned is whatever git calls TEXT, not a list of extensions: markup and an
            // extensionless dotfile must be in the set, and an image must not be.
            Assert.Contains(published, f => f == "/src/CodeWicket.UI/Views/ChatView.xaml");
            Assert.Contains(published, f => f == "/.gitattributes");
            Assert.DoesNotContain(published, f => f == "/docs/images/debugger.png");

            Assert.False(IsClean(@"const string p = @""C:\Users\mail\source\repos\App\Test.cs"";"));
            Assert.False(IsClean(@"AgentLogDirectory = @""D:\Users\jane\.kiro\logs\20260903T195141017"";"));

            // A Windows path inside a JSON payload inside a C# literal: the separators arrive
            // doubled, which the single-backslash pattern read straight past for as long as a real
            // one was sitting in AcpMapperOutputTests.
            Assert.False(IsClean(@"""message"": ""Created the c:\\Users\\mail\\source\\repos\\App\\temp.txt file."""));
            Assert.True(IsClean(@"""message"": ""Created the c:\\Users\\someone\\source\\repos\\App\\temp.txt file."""));

            // The same path after a round trip through a URL or a POSIX-flavoured tool.
            Assert.False(IsClean(@"file:///C:/Users/mail/source/repos/App/Test.cs"));
            Assert.True(IsClean(@"file:///C:/Users/someone/source/repos/App/Test.cs"));

            // The flattened encoding, which read straight past the original pattern.
            Assert.False(IsClean(@"~/.claude/projects/C--Users-mail-AppData-Local-code-wicket-workspace did not exist"));
            Assert.True(IsClean(@"~/.claude/projects/C--Users-someone-AppData-Local-code-wicket-workspace did not exist"));

            Assert.True(IsClean(@"AgentLogDirectory = @""C:\Users\someone\.kiro\logs\20260903T195141017"";"));
            Assert.True(IsClean(@"diagnostics.LogDirectory = @""C:\Users\me\.kiro\logs\20260807T075217588"";"));
            Assert.True(IsClean(@"AgentLogDirectory: @""C:\Users\x\.kiro\logs\20260901T222004803"";"));
            Assert.True(IsClean(@"// ""C:\Users\you\.aws\credentials:1"" can never become a one-click open."));

            // The markup case the name boundary exists for: a doc comment closing straight after the
            // placeholder must still read as that placeholder.
            Assert.True(IsClean(@"/// terminates at <c>C:\Users\someone</c> and silently makes the"));

            // An ellipsis is how prose writes "some user", and it names nobody. Dots INSIDE a name are
            // still a name.
            Assert.True(IsClean(@"# entries carry full `C:\Users\...` command lines and scratchpad GUIDs"));
            Assert.False(IsClean(@"var p = @""C:\Users\j.doe\source\App.cs"";"));

            // Paths that name no profile at all are not this guard's business.
            Assert.True(IsClean(@"var root = Path.Combine(localAppData, ""code-wicket"", ""workspace"");"));
            Assert.True(IsClean(@"// %LOCALAPPDATA%\code-wicket\logs, and ~/.kiro on every install."));
        }

        private static bool IsClean(string line) =>
            ProfileMatches(line).All(m => NamesNobody(m.Groups["name"].Value));

        /// <summary>
        /// A placeholder, or an ELISION. A name made only of dots is how prose writes "whoever it
        /// is" (<c>C:\Users\...</c>), and it cannot be a real profile: Win32 strips trailing dots from a
        /// path component, so no folder under <c>Users</c> is named that way. Accepting it loosens
        /// nothing a real path could use.
        /// </summary>
        private static bool NamesNobody(string name) =>
            Placeholders.Contains(name) || name.All(c => c == '.');

        /// <summary>A repo-relative path, so an offender in docs/ is distinguishable from one in src/.</summary>
        private static string Relative(FileInfo file) =>
            Path.GetRelativePath(SourceTree.RepoRoot().FullName, file.FullName).Replace('\\', '/');
    }
}
