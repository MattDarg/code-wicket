using System.Collections.Generic;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The operator set a wildcard rule refuses to cover (pre-release security review, September 2026), and
    /// the two places the user meets it: the withheld-rule sentence on the banner, and the hint under
    /// the editable glob. The policy-side behaviour is pinned in <c>PolicyPermissionHandlerTests</c>.
    /// </summary>
    public sealed class ShellOperatorsTests
    {
        [Theory]
        [InlineData("git status && calc", "&&")]
        [InlineData("git status & calc", "&")]
        [InlineData("git status || calc", "||")]
        [InlineData("git log | findstr x", "|")]
        [InlineData("git status; calc", ";")]
        [InlineData("git status\ncalc", "a line break")]
        [InlineData("git status\r\ncalc", "a line break")]
        [InlineData("git status `calc`", "`")]
        [InlineData("git status $(calc)", "$(")]
        [InlineData("git status (calc)", "(")]
        [InlineData("git log > out.txt", ">")]
        [InlineData("git apply < patch", "<")]
        public void Find_NamesTheFirstOperator(string command, string expected) =>
            Assert.Equal(expected, ShellOperators.Find(command));

        // Quoting is deliberately ignored: this is a refusal, not a parser, and it errs towards the
        // banner. The false positive costs one review.
        [Fact]
        public void Find_IgnoresQuoting() =>
            Assert.Equal("(", ShellOperators.Find("git commit -m \"fix (typo)\""));

        [Theory]
        [InlineData("git status")]
        [InlineData("dotnet build CodeWicket.slnx -c Release")]
        [InlineData("find . -name \"*.cs\"")]
        [InlineData("")]
        [InlineData(null)]
        public void Find_IsNullForAPlainCommand(string? command) =>
            Assert.Null(ShellOperators.Find(command));

        [Fact]
        public void TheWithheldSentenceNamesTheRuleAndTheOperator()
        {
            var sentence = ShellOperators.WithheldSentence("git *", "&&");

            Assert.Contains("\u201cgit *\u201d", sentence);
            Assert.Contains("\u201c&&\u201d", sentence);
            Assert.Contains("was not applied", sentence);
            Assert.Contains("never covers a line with a shell operator", sentence);
            Assert.Contains("contains a line break", ShellOperators.WithheldSentence("git *", "a line break"));
        }

        // The banner carries the sentence as an ordinary note - not the red call-out, which is the
        // caution tier's and would train click-through here.
        [Fact]
        public void TheBannerShowsTheNoteWithoutFlagging()
        {
            var note = ShellOperators.WithheldSentence("git *", "&&");
            var banner = new PermissionBannerViewModel(
                new PermissionRequestDto(
                    "t1", "Running: git status && calc", "execute", null, "git status && calc",
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                        new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    },
                    RuleNote: note),
                (_, _, _, _, _) => { });

            Assert.True(banner.HasRuleNote);
            Assert.Equal(note, banner.RuleNote);
            Assert.False(banner.IsFlagged);
            Assert.Contains(banner.Options, o => o.Kind == "AllowAlways"); // still offered
        }

        [Fact]
        public void TheHintUnderACommandGlobSaysWhatAWildcardNeverCovers()
        {
            var banner = new PermissionBannerViewModel(
                new PermissionRequestDto(
                    "t1", "Running: git status", "execute", null, "git status",
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                    }),
                (_, _, _, _, _) => { });

            Assert.True(banner.HasPatternHint);
            Assert.Equal(ShellOperators.WideningHint, banner.PatternHint);
            Assert.Contains("never applies to a line containing a shell operator", ShellOperators.WideningHint);
        }

        [Fact]
        public void APathRuleHasNoOperatorHint()
        {
            var banner = new PermissionBannerViewModel(
                new PermissionRequestDto(
                    "t1", "Write File", "edit", null, null,
                    new[] { new PermissionOptionDto("allow_always", "Allow always", "AllowAlways") },
                    Path: @"C:\ws\a.cs"),
                (_, _, _, _, _) => { });

            Assert.False(banner.HasPatternHint);
        }
    }
}
