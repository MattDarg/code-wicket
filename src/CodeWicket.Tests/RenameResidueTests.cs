using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Scans the tree for the separator-less spelling of the product name, which is not a form of it:
    /// the convention is one kebab token, <c>code-wicket</c> (<c>docs/engineering/BRANDING.md</c>).
    /// </summary>
    /// <remarks>
    /// This is a SOURCE scan because it has to be. The Tests project references neither
    /// <c>CodeWicket.Ide</c> nor <c>CodeWicket.VSExtension</c>, so the IDE-side log directory, the diff
    /// scratch dir, the default workspace, the stub workspace and the test-results root are unreachable
    /// by any unit test that could be written. Nothing else offline covers them at all.
    /// </remarks>
    public sealed class RenameResidueTests
    {
        /// <summary>
        /// The separator-less <c>codewicket</c>, which is not a form of this product's name.
        /// </summary>
        /// <remarks>
        /// It earns its place because the spelling had already appeared twice with nobody choosing it
        /// — seven activity-log event names and the icon asset filenames — which is what a form typed
        /// from memory rather than read off the table looks like.
        /// <para>
        /// Anchored on a following <c>-</c> or <c>/</c>, because bare <c>codewicket</c> under
        /// <see cref="RegexOptions.IgnoreCase"/> matches the legitimate PascalCase <c>CodeWicket</c>
        /// identity — the VSIX <c>Identity Id</c>, every <c>.vsct</c> IDSymbol and canonical name, and
        /// every namespace in the solution. The anchor is what separates the two.
        /// </para>
        /// <para>
        /// This guards a CONVENTION rather than a rename, so it has no allowlist and never shrinks.
        /// A guard that searched for a retired name would have to spell that name, which is why none
        /// is kept here: the retired spellings left the tree with the compatibility surface that
        /// carried them.
        /// </para>
        /// </remarks>
        private static readonly Regex SeparatorLess = new(
            @"codewicket[-/]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The shipping source tree. Shared with every other scan built on it.</summary>
        private static IEnumerable<FileInfo> Sources() => SourceTree.Files();

        [Fact]
        public void NoFileUsesTheSeparatorLessSpelling()
        {
            var offenders = new List<string>();
            foreach (var file in Sources())
            {
                // This file names the spelling in order to look for it.
                if (string.Equals(file.Name, "RenameResidueTests.cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                var lines = File.ReadAllLines(file.FullName);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (SeparatorLess.IsMatch(lines[i]))
                        offenders.Add($"{file.Name}({i + 1}): {lines[i].Trim()}");
                }
            }

            Assert.True(offenders.Count == 0,
                "These spell the product without its separator. The convention is one kebab token, "
                + "code-wicket — see docs/engineering/BRANDING.md:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// The scan states its discrimination directly: the two shapes it exists to catch, and the
        /// PascalCase identity it must never touch. Without the negative case the guard would fail the
        /// whole solution, every namespace in which carries that identity.
        /// </summary>
        [Fact]
        public void TheSeparatorLessScanCatchesTheRealShapesAndSparesTheIdentity()
        {
            Assert.True(Sources().Any(), "The source enumeration is empty; every scan over it is vacuous.");

            Assert.Matches(SeparatorLess, @"}).FileAndForget(""codewicket/openlogs"");");
            Assert.Matches(SeparatorLess, @"<Resource Include=""Resources\codewicket-128.png"" />");

            Assert.DoesNotMatch(SeparatorLess, @"<Identity Id=""CodeWicket.354bbd8d-441e-4cee-bef5-9c4a54217def""");
            Assert.DoesNotMatch(SeparatorLess, @"<IDSymbol name=""CodeWicketMenu"" value=""0x1000"" />");
            Assert.DoesNotMatch(SeparatorLess, @"namespace CodeWicket.Core.Ide");
            Assert.DoesNotMatch(SeparatorLess, @"<Resource Include=""Resources\code-wicket-128.png"" />");
        }
    }
}
