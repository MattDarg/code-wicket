using System.Collections.Generic;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The glob language's escape and the one place that has to use it (pre-release security review,
    /// September 2026): a wildcard in the command the agent wrote must not become a wildcard in the rule the
    /// banner seeds, or approving one literal command approves a family the agent chose.
    /// </summary>
    public sealed class PermissionGlobTests
    {
        [Theory]
        [InlineData("git add *", "git add [*]")]
        [InlineData("find . -name \"*.cs\"", "find . -name \"[*].cs\"")]
        [InlineData("dir ??.txt", "dir [?][?].txt")]
        [InlineData("git status", "git status")]
        public void Escape_TakesEachWildcardLiterally(string command, string expected) =>
            Assert.Equal(expected, PermissionGlob.Escape(command));

        // The whole point: an escaped command compiles to a rule that matches that command and only it.
        [Fact]
        public void AnEscapedCommandMatchesItselfAndNothingWider()
        {
            var rule = PermissionGlob.ToRegex(PermissionGlob.Escape("find . -name \"*.cs\""), anchored: true);

            Assert.Matches(rule, "find . -name \"*.cs\"");
            Assert.Matches(rule, "FIND . -name \"*.CS\""); // still case-insensitive, like every rule
            Assert.DoesNotMatch(rule, "find . -name \"x\"; calc; echo \".cs\"");
            Assert.DoesNotMatch(rule, "find . -name \"a.cs\"");
        }

        [Fact]
        public void ABareWildcardStillWidens()
        {
            var rule = PermissionGlob.ToRegex("git *", anchored: true);

            Assert.Matches(rule, "git status");
            Assert.DoesNotMatch(rule, "gitk");
        }

        // Not a character class: brackets around anything else, or alone, are the characters themselves.
        [Theory]
        [InlineData("[abc]", "[abc]", true)]
        [InlineData("[abc]", "a", false)]
        [InlineData("a[", "a[", true)]
        [InlineData("[*", "[anything", true)] // an unclosed escape is a bracket and a WILDCARD
        [InlineData("[*", "anything", false)]
        public void BracketsAreLiteralExceptAroundAWildcard(string glob, string subject, bool matches) =>
            Assert.Equal(matches, PermissionGlob.ToRegex(glob, anchored: true).IsMatch(subject));

        [Fact]
        public void UnanchoredMatchesAsASubstring()
        {
            var rule = PermissionGlob.ToRegex("rm -rf", anchored: false);
            Assert.Matches(rule, "sudo rm -rf /");
        }

        // The banner seeds the pattern box with the command ESCAPED, so accepting the default means
        // exactly the command on screen. Widening to a glob is then the user's own edit.
        [Fact]
        public void TheBannerSeedsTheAlwaysPatternWithTheCommandEscaped()
        {
            var decisions = new List<(string option, string? command)>();
            var banner = new PermissionBannerViewModel(
                new PermissionRequestDto(
                    "t1", "Running: git add *", "execute", null, "git add *",
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                        new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    }),
                (option, command, _, _, _) => decisions.Add((option, command)));

            Assert.Equal("git add [*]", banner.Pattern);

            banner.Options[1].Command.Execute(null); // Allow always -> the editable step
            banner.ConfirmAlwaysCommand.Execute(null);

            var (_, remembered) = Assert.Single(decisions);
            Assert.Equal("git add [*]", remembered);
        }
    }
}
