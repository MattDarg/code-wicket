using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The steering doc routes to the area docs, and the routing still works.
    /// </summary>
    /// <remarks>
    /// Most of the rules moved out of the steering doc, which is loaded into every session, and into the
    /// area docs, which are opened on demand. What is left in their place is a table of pointers — so a
    /// pointer that stops resolving, or a doc nothing points at, silently costs a reader every rule in it.
    /// <para>
    /// These are facts, not judgements: a link resolves or it does not. Deliberately NOT guarded here are
    /// entry length and house style — a cap is a number someone picked, and a list of banned phrasings can
    /// only ever match the wording already removed, so both would report green over the thing they exist
    /// to find. Prose is held to its standard by review (<c>REVIEW.md</c>), not by a regex.
    /// </para>
    /// </remarks>
    public sealed class SteeringDocRoutingTests
    {
        /// <summary>
        /// The steering doc: <c>AGENTS.md</c> if the restructure has happened, else <c>CLAUDE.md</c>. The
        /// size floor skips the pointer stub the rename leaves behind, which would otherwise be scanned
        /// in place of the real file.
        /// </summary>
        private static FileInfo SteeringDoc()
        {
            var root = SourceTree.RepoRoot();
            foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
            {
                var candidate = new FileInfo(Path.Combine(root.FullName, name));
                if (candidate.Exists && candidate.Length > 4_096)
                    return candidate;
            }

            throw new FileNotFoundException("No steering doc (AGENTS.md or CLAUDE.md) under " + root.FullName);
        }

        /// <summary>
        /// The area docs. Everything here is published; the maintainer's working material is under
        /// <c>.claude/</c>, which ships with nothing and is named by nothing that does.
        /// </summary>
        private static DirectoryInfo AreaDocs()
        {
            var dir = new DirectoryInfo(Path.Combine(SourceTree.RepoRoot().FullName, "docs", "engineering"));
            if (!dir.Exists || !dir.EnumerateFiles("*.md").Any())
                throw new DirectoryNotFoundException("No area docs under " + dir.FullName);

            return dir;
        }

        private static string[] MarkdownLinks(string text) =>
            Regex.Matches(text, @"\]\((?<target>[^)\s#]+\.md)(?:#[^)]*)?\)")
                .Select(m => m.Groups["target"].Value)
                .Where(t => !t.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        [Fact]
        public void EveryLinkResolves()
        {
            var doc = SteeringDoc();
            var root = SourceTree.RepoRoot();

            var broken = MarkdownLinks(File.ReadAllText(doc.FullName))
                .Where(t => !File.Exists(Path.Combine(root.FullName, t.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();

            Assert.True(
                broken.Count == 0,
                $"{doc.Name} links files that do not exist, so the pointer half of \"a claim and a pointer\" "
                + "is dead:\n  " + string.Join("\n  ", broken));
        }

        /// <summary>
        /// Every area doc is reachable from the routing table. A doc nobody links is a doc nobody opens,
        /// and unlike a broken link that failure is silent — it is the one the split introduced.
        /// </summary>
        [Fact]
        public void EveryAreaDocIsReachableFromTheRoutingTable()
        {
            var doc = SteeringDoc();
            var text = File.ReadAllText(doc.FullName);

            var unreachable = AreaDocs().EnumerateFiles("*.md")
                .Select(f => f.Name)
                .Where(n => !text.Contains(n, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                unreachable.Count == 0,
                $"An area doc is not linked from {doc.Name}, so nothing routes a reader to it and the rules "
                + "in it are unreachable in a session that never opens it:\n  " + string.Join("\n  ", unreachable));
        }

        /// <summary>
        /// REVIEW.md routes through the steering doc's table rather than carrying its own. Two mappings
        /// drift, and the stale one would be the copy nobody has loaded. Naming a doc in prose or in a
        /// worked example is fine; a LINK is what makes it a second table.
        /// </summary>
        [Fact]
        public void TheReviewGuidanceRoutesThroughTheSteeringDoc()
        {
            var review = new FileInfo(Path.Combine(SourceTree.RepoRoot().FullName, "REVIEW.md"));
            Assert.True(review.Exists, "REVIEW.md is missing: the split relies on a review that reads the area docs.");

            var text = File.ReadAllText(review.FullName);
            var steering = SteeringDoc().Name;
            Assert.True(
                text.Contains(steering, StringComparison.OrdinalIgnoreCase),
                $"REVIEW.md does not mention {steering}, so it no longer routes a reviewer to the table that "
                + "says which area doc to read.");

            var areaDocs = AreaDocs().EnumerateFiles("*.md").Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var linked = MarkdownLinks(text)
                .Select(Path.GetFileName)
                .Where(name => name is not null && areaDocs.Contains(name))
                .ToList();

            Assert.True(
                linked.Count == 0,
                "REVIEW.md links an area doc directly, which makes it a second routing table beside the one "
                + $"in {steering}. Route through that table instead:\n  " + string.Join("\n  ", linked));
        }
    }
}
