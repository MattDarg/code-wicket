using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Scans everything the public repository will contain for a citation of the pre-release security
    /// review's own numbered findings, which name a document no reader outside this repository can open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule this enforces is AGENTS.md's: <b>every identifier must resolve for a stranger.</b> A
    /// number like the ones this catches carries no content the moment the review is not to hand — and
    /// they were worse than merely opaque, because they COLLIDED. One number meant the config.json write
    /// rule in two files and the test-filter injection in a third; another meant both the shell-operator
    /// refusal and the untrusted tool name. No per-file disambiguation was ever available, so the
    /// numbers were removed rather than glossed, and section-level provenance became a phrase that says
    /// when the review happened without pretending the reader can look it up.
    /// </para>
    /// <para>
    /// <b>The discrimination is a token PLUS a context word on the same line, never the token alone</b>,
    /// and that is the whole reason this guard is usable. <c>F1</c> and <c>F2</c> are .NET numeric
    /// format specifiers — <c>ToString("F1")</c>, <c>{Median(idle),7:F2}</c> — and there are dozens of
    /// them in the perf harness and the diagnostics cost types. <c>F5</c> and <c>F2</c> are also
    /// keyboard shortcuts, and the product prose that names them (the person starting their own debug
    /// run, VS's own Rename) is correct and must survive. A bare-token scan would fail ~76 legitimate
    /// lines on its first run and be switched off rather than heeded, which is the failure mode
    /// <see cref="UserPathLeakTests"/> records for a scan that cannot state its discrimination.
    /// </para>
    /// <para>
    /// <b>What it cannot separate, stated rather than hidden:</b> a line carrying a format specifier
    /// AND the standalone word "scan" or "finding" would flag, and would be a false positive. None
    /// exists in the tree (measured across 665 published files, 2026-09-16) and the shape is unlikely,
    /// but the guard would be wrong rather than merely noisy, so the next person to hit one should
    /// reword the line or narrow this rule — not add a file exemption, which would be a rubber stamp
    /// on exactly the lines a future change is about.
    /// </para>
    /// <para>
    /// <b>And what it cannot catch AT ALL, which is the more useful admission:</b> a bare section
    /// divider — <c>// ---- F4: the budget ----</c> — carries the number with no context word beside
    /// it, and no rule can flag that without failing every <c>{value:F1}</c> in the tree. Those were
    /// removed by hand. What this guard covers is the CITATION forms, the ones carrying provenance,
    /// which are also the ones that come back: a number reappears when somebody documents a fix, not
    /// when they label a region of a file. A check that cannot separate the two says so rather than
    /// claiming the coverage.
    /// </para>
    /// <para>
    /// It scans PROSE as well as code, for <see cref="SourceTree.PublishedFiles"/>' reason: the
    /// citations were denser in <c>docs/engineering/</c> than in <c>src/</c>, and a scan pointed at
    /// <c>src/</c> would have reported the tree clean while most of them shipped.
    /// </para>
    /// </remarks>
    public sealed class SecurityFindingNumberTests
    {
        /// <summary>
        /// A finding number as it was ever written: <c>F</c> and one or two digits, standing alone.
        /// </summary>
        private static readonly Regex FindingToken = new(@"\bF\d{1,2}\b", RegexOptions.Compiled);

        /// <summary>
        /// The words that turn the token above into a citation rather than a format specifier or a
        /// keystroke. Both are whole words: <c>scanned</c> is ordinary prose and must not qualify.
        /// </summary>
        private static readonly Regex ReviewContext = new(
            @"\bfindings?\b|\bscans?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The dated provenance the numbers travelled with. It is banned on its own, with no token
        /// beside it, because it dates a private document just as precisely as a number names one.
        /// </summary>
        private static readonly Regex DatedScan = new(
            @"\b\d{4}-\d{2}-\d{2}\s+scan\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Fact]
        public void NoPublishedFileCitesASecurityFindingNumber()
        {
            var offenders = new List<string>();
            foreach (var file in SourceTree.PublishedFiles())
            {
                var isSelf = file.Name == "SecurityFindingNumberTests.cs";
                var lines = File.ReadAllLines(file.FullName);
                for (var i = 0; i < lines.Length; i++)
                {
                    // This file's own negative cases must spell the banned forms; nothing else here may.
                    if (isSelf && lines[i].Contains("Assert.False(IsClean("))
                        continue;

                    if (!IsClean(lines[i]))
                        offenders.Add($"{Relative(file)}({i + 1}): {lines[i].Trim()}");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "These cite the pre-release security review's own finding numbers, which name a document "
                + "no reader outside this repository can open — and which collided with each other even "
                + "inside it. Write the mechanism instead, and date the review as "
                + "\"(pre-release security review, September 2026)\" once per section:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// The scan states its discrimination directly, because it has no allowlisted file to prove
        /// itself against: the citations it exists to catch, and the format specifiers and keystrokes
        /// already in the tree that it must never touch.
        /// </summary>
        [Fact]
        public void TheScanCatchesACitationAndSparesFormatSpecifiersAndKeystrokes()
        {
            // The instrument must prove it can see a known-present thing before its silence means
            // anything — the citations were densest in docs/, which a src/-only scan never reads.
            var published = SourceTree.PublishedFiles().Select(f => "/" + Relative(f)).ToList();
            Assert.Contains(published, f => f.StartsWith("/docs/") && f.EndsWith(".md"));
            Assert.Contains(published, f => f.StartsWith("/src/") && f.EndsWith(".cs"));
            Assert.DoesNotContain(published, f => f.Contains("/.claude/"));

            // ---- the citations, in every spelling they were written in ----
            Assert.False(IsClean(@"/// <b>Why this cannot be a configured rule</b> (security finding F1, 2026-09-09 scan)."));
            Assert.False(IsClean(@"- **The workspace block is FENCED, per prompt** (security finding F2)."));
            Assert.False(IsClean(@"The original shape (2026-09-09 scan, F15): N distinct blocks that cannot tokenize."));
            Assert.False(IsClean(@"/// (security findings F16 and F17, 2026-09-09 scan, plus the sub-agent attribution)"));

            // The dated provenance alone, with no number beside it, dates the same private document.
            Assert.False(IsClean(@"captured on the wire during the 2026-09-09 scan"));

            // ---- .NET numeric format specifiers: dozens in the perf harness alone ----
            Assert.True(IsClean(@"Inv($"" {size,6} | {visuals,8} | {Median(idle),7:F2} | {Median(inputEdit),12:F2} "")"));
            Assert.True(IsClean(@"private static string Ms(double value) => value.ToString(""F1"", CultureInfo.InvariantCulture);"));
            Assert.True(IsClean(@"$""thinking, ~{repeats * 140 / 1024.0:F1}KB each"","));

            // ---- keystrokes: the person running their own program, and VS's own Rename ----
            Assert.True(IsClean(@"the program continues on another thread, the user presses F5 - so a source"));
            Assert.True(IsClean(@"- **Running and debugging the extension needs Visual Studio itself** - start debugging (F5 by default)"));
            Assert.True(IsClean(@"matches VS's own C#-initiated F2 exactly (F2 only touches Razor)"));

            // ---- the replacement wording must itself be clean, or the fix could not be written ----
            Assert.True(IsClean(@"- **A SHELL COMMAND HAS NO TRUSTED NAME** (pre-release security review, September 2026)."));

            // "scanned" is ordinary prose and is not the word this rule looks for.
            Assert.True(IsClean(@"// scanned every published file before concluding the tree was clean"));
        }

        private static bool IsClean(string line) =>
            !((FindingToken.IsMatch(line) && ReviewContext.IsMatch(line)) || DatedScan.IsMatch(line));

        /// <summary>A repo-relative path, so an offender in docs/ is distinguishable from one in src/.</summary>
        private static string Relative(FileInfo file) =>
            Path.GetRelativePath(SourceTree.RepoRoot().FullName, file.FullName).Replace('\\', '/');
    }
}
