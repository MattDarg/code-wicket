using CodeWicket.Ipc;

using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The three sentences a permission banner can carry when no transcript row stands beside it
    /// (pre-release security review, September 2026, plus the sub-agent attribution built with
    /// them): whose conversation, whose sub-agent, which file. Each is independent of the others and
    /// of the row; the view-model-level wiring is pinned in <c>OffScreenReplyRoutingTests</c> and
    /// <c>SubagentNestingTests</c>.
    /// </summary>
    public sealed class PermissionBannerAttributionTests
    {
        private static PermissionOptionDto[] Options() => new[]
        {
            new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
            new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
        };

        private static PermissionRequestDto Edit(string path, bool fromSubagentSession = false) =>
            new("t1", "Write File", "edit", null, null, Options(), Path: path, FromSubagentSession: fromSubagentSession);

        private static PermissionBannerViewModel Banner(
            PermissionRequestDto request, string? origin = null, bool originDeleted = false,
            string? subagentTitle = null, string? workspaceRoot = null) =>
            new(request, (_, _, _, _, _) => { }, null, null, origin, originDeleted, subagentTitle, workspaceRoot);

        // ---- the file ----

        [Fact]
        public void AFileScopedRequestNamesItsFileRelativeToTheRoot()
        {
            var banner = Banner(Edit(@"C:\ws\src\Program.cs"), workspaceRoot: @"C:\ws");

            Assert.True(banner.HasTargetPath);
            Assert.Equal("src/Program.cs", banner.TargetPath);
            Assert.Equal(@"C:\ws\src\Program.cs", banner.TargetPathTooltip);
        }

        // The scan's own example: a write outside the workspace shows where it really goes.
        [Fact]
        public void AFileOutsideTheRootShowsItsAbsolutePath()
        {
            var banner = Banner(Edit(@"C:\Users\someone\.ssh\authorized_keys"), workspaceRoot: @"C:\ws");

            Assert.Equal(@"C:\Users\someone\.ssh\authorized_keys", banner.TargetPath);
        }

        [Fact]
        public void WithNoRootThePathShowsAsItCame()
        {
            Assert.Equal(@"C:\ws\a.cs", Banner(Edit(@"C:\ws\a.cs")).TargetPath);
        }

        [Fact]
        public void ACommandRequestHasNoTargetPath()
        {
            var banner = Banner(new PermissionRequestDto("t1", "Running: git status", "execute", null, "git status", Options()));

            Assert.False(banner.HasTargetPath);
        }

        // ---- whose conversation ----

        [Fact]
        public void ADeletedOriginSaysSo()
        {
            var banner = Banner(Edit(@"C:\ws\a.cs"), origin: "Fix the build", originDeleted: true);

            Assert.Equal(PermissionBannerViewModel.DeletedOriginSentence("Fix the build"), banner.Origin);
            Assert.Contains("\u201cFix the build\u201d", banner.Origin);
            Assert.Contains("you deleted", banner.Origin);
            Assert.Contains("still running", banner.Origin);
            Assert.DoesNotContain("recorded in that conversation", banner.Origin); // there is no such conversation now
        }

        [Fact]
        public void ALiveOriginKeepsTheOffScreenSentence()
        {
            var banner = Banner(Edit(@"C:\ws\a.cs"), origin: "Fix the build");

            Assert.Equal(PermissionBannerViewModel.OriginSentence("Fix the build"), banner.Origin);
        }

        // ---- whose sub-agent ----

        [Fact]
        public void ANestedCallIsAttributedToItsLaunchByName()
        {
            var banner = Banner(Edit(@"C:\ws\a.cs"), subagentTitle: "Explore the test failures");

            Assert.True(banner.HasSubagent);
            Assert.Equal(PermissionBannerViewModel.SubagentSentence("Explore the test failures"), banner.Subagent);
            Assert.Contains("\u201cExplore the test failures\u201d", banner.Subagent);
        }

        // Kiro v2: no parent to climb, only the fact that the request came on a sub-agent's session.
        [Fact]
        public void ASubagentSessionRequestIsAttributedWithoutAName()
        {
            var banner = Banner(Edit(@"C:\ws\a.cs", fromSubagentSession: true));

            Assert.Equal(PermissionBannerViewModel.UnnamedSubagentSentence, banner.Subagent);
        }

        // A name beats the unnamed form when both facts are present.
        [Fact]
        public void ANamedLaunchWinsOverTheUnnamedForm()
        {
            var banner = Banner(Edit(@"C:\ws\a.cs", fromSubagentSession: true), subagentTitle: "Task");

            Assert.Equal(PermissionBannerViewModel.SubagentSentence("Task"), banner.Subagent);
        }

        [Fact]
        public void TheMainAgentsOwnWorkCarriesNoSubagentLine()
        {
            Assert.False(Banner(Edit(@"C:\ws\a.cs")).HasSubagent);
        }

        // ---- the v2 fact itself ----

        [Theory]
        [InlineData("sub-1", "primary", true)]
        [InlineData("primary", "primary", false)]
        [InlineData("sub-1", null, false)] // primary unknown yet: nothing can be said
        [InlineData("sub-1", "", false)]
        [InlineData(null, "primary", false)]
        public void ARequestOnAnotherSessionIsASubagents(string? sessionId, string? primary, bool expected) =>
            Assert.Equal(expected, CodeWicket.Providers.Acp.AcpMapper.IsSubagentSessionRequest(sessionId, primary));
    }
}
