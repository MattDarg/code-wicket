using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Scans the source tree for a citation of a file under the injection-script directory, and every published
    /// file for a script named there, either of which names something no reader outside its branch can open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is AGENTS.md's: <b>every identifier must resolve for a stranger.</b> What makes this class
    /// mechanical rather than a judgement is the injection-script procedure: a script is committed on the branch that
    /// writes it and DROPPED when that branch merges, so a source citation of one is dangling by
    /// construction — not eventually, but as soon as the branch it belongs to lands.
    /// </para>
    /// <para>
    /// <b>It was already broken when this was written</b>, which is the argument for a scan rather than a
    /// paragraph someone re-reads at merge time: five citations in four files named scripts the tree no
    /// longer held, having been pruned when the injection library moved out of the repository, and nothing
    /// reported it. A comment does not fail a build, and a script is only checked when someone runs it.
    /// </para>
    /// <para>
    /// <b>What to write instead.</b> Name what the script PROVES — the test it anchors — and say what was
    /// injected, in words: <i>"Proved by <c>StaInvariantScopeTests</c>, over an injected thread-static
    /// scope."</i> That survives the merge, and it is the half a reader wanted anyway; the script's
    /// filename told them nothing the sentence does not.
    /// </para>
    /// <para>
    /// <b>What each fact reads.</b> The first reads the C# under <c>src/</c> for the directory's path. The
    /// second reads every published file for a script's bare NAME, the form prose uses: the docs,
    /// <c>AGENTS.md</c>, the root documents, the directory's own README, and code and script comments alike.
    /// Neither reads the scripts themselves: a script naming a sibling is a citation that resolves for as
    /// long as both exist.
    /// </para>
    /// <para>
    /// <b>The needle is ASSEMBLED rather than spelled</b>, and so is the one in the script that proves
    /// this: a guard for a banned string is itself a file in the tree it scans, so spelling it out fails
    /// the guard on its own source the moment it is staged — green in the session that wrote it, red on
    /// the next run. An exemption for this file would be the wrong repair, since it would also exempt the
    /// one file whose citations nobody is watching.
    /// </para>
    /// </remarks>
    public sealed class InjectionScriptCitationTests
    {
        // The directory this forbids naming, in segments - assembled into the needle below, never spelled.
        private static readonly string[] Segments = { "scripts", "inject" };

        private static readonly string Needle = string.Join("/", Segments) + "/";

        // The scripts kept past a merge, because prove-check.ps1's own examples run them.
        private static readonly string[] ShippingExamples =
        {
            "hyperlinks-dont-enter-a-list",
            "protected-path-guard-removed",
        };

        [Fact]
        public void NoSourceFileCitesAnInjectionScript()
        {
            var root = RepoRoot();

            // The needle and the real directory are the same two segments, and this is what keeps them that
            // way: renamed or moved, a scan built on a stale spelling would find nothing and pass forever,
            // which is the quiet failure this whole guard exists to stop happening to someone else.
            Assert.True(
                Directory.Exists(Path.Combine(new[] { root }.Concat(Segments).ToArray())),
                $"The injection-script directory this scans for is not at '{Needle}'. If it moved, move "
                + "these segments with it; a scan that cannot find its own subject passes silently.");
            var offenders = Directory
                .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .SelectMany(path => File.ReadAllLines(path)
                    .Select((line, index) => (path, number: index + 1, line))
                    .Where(row => row.line.Contains(Needle, StringComparison.OrdinalIgnoreCase)
                               || row.line.Contains(Needle.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)))
                .Select(row => $"{Path.GetRelativePath(root, row.path)}:{row.number}: {row.line.Trim()}")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "A source file names an injection script, which is committed only on the branch that writes it and removed before that branch merges. "
                + "Name the TEST it anchors and say what was injected, in words, instead:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// No published file names a script in the injection-script directory, by name alone.
        /// </summary>
        /// <remarks>
        /// <para>Prose cites a script by its bare name far more often than by path, so the needle above never
        /// sees it — and so can a comment in any language the tree holds. So this reads every file
        /// <see cref="SourceTree.PublishedFiles"/> returns, which is also what keeps untracked scratch output
        /// out of it. The names are read from the TREE rather than kept as a list, because the time a citation
        /// must be caught is while its branch is under review: the scripts exist then, and are gone after the
        /// merge, when there is nothing left to check against. A script name is a long hyphenated phrase no
        /// ordinary sentence contains, so the match is exact, and it fires while it can still be fixed.</para>
        /// <para>The two shipping examples are exempt, being kept past a merge so that naming them resolves.
        /// The scripts themselves are not read; the directory's README is.</para>
        /// </remarks>
        [Fact]
        public void NoPublishedFileNamesAnInjectionScriptThatIsRemovedAtMerge()
        {
            var root = SourceTree.RepoRoot().FullName;
            var dir = Path.Combine(new[] { root }.Concat(Segments).ToArray());

            // The instrument must see its known-present subjects before its silence means anything: a moved
            // directory, or renamed examples, would otherwise yield no names and pass forever.
            foreach (var example in ShippingExamples)
                Assert.True(
                    File.Exists(Path.Combine(dir, example + ".py")),
                    $"The shipping example '{example}.py' is not in the injection-script directory. If the scripts "
                    + "moved, move this scan with them; one that cannot find its subjects passes silently.");

            var names = Directory.EnumerateFiles(dir, "*.py")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !ShippingExamples.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var offenders = SourceTree.PublishedFiles()
                .Where(file => !(string.Equals(file.DirectoryName, dir, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(file.Extension, ".py", StringComparison.OrdinalIgnoreCase)))
                .SelectMany(file => File.ReadAllLines(file.FullName)
                    .Select((line, index) => (file, number: index + 1, line)))
                .SelectMany(row => names
                    .Where(name => row.line.Contains(name!, StringComparison.OrdinalIgnoreCase))
                    .Select(name => $"{Path.GetRelativePath(root, row.file.FullName)}:{row.number}: {name}"))
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "A published file names an injection script, which is removed in its branch's last commit before "
                + "it merges, so the name will resolve to nothing. Say what it proved instead - the check it failed "
                + "and the mistake it injected, in words:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        // Walks up from the test assembly to the directory holding the solution.
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeWicket.slnx")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
