using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <see cref="AgentPath"/> is the single definition of "the same file" shared by the ACP mapper (whose
    /// answer keys the transcript, the write capture and the read cache) and the VS edit applier (whose
    /// answer decides where the bytes land). The two disagreeing is how one write became two cards, so
    /// the rule is pinned here rather than at either caller — and this is the only place it CAN be pinned,
    /// the applier being net472 + VS SDK and unreferenceable from a test project.
    /// </summary>
    public sealed class AgentPathTests
    {
        private const string Root = @"C:\ws\dotnet";

        /// <summary>
        /// The three spellings one v3 file_write arrives in. They must collapse to one string; each of
        /// these differing from the others is a separate transcript card for the same edit.
        /// </summary>
        [Theory]
        [InlineData("file:///c%3A/ws/dotnet/proj/Foo.cs")] // the finalized diff's percent-encoded URI
        [InlineData(@"c:\ws\dotnet\proj\Foo.cs")]          // decoded, lowercase drive
        [InlineData(@"proj/Foo.cs")]                       // rawInput: whatever the model typed
        [InlineData(@"proj\Foo.cs")]
        [InlineData(@"C:\ws\dotnet/proj/Foo.cs")]          // Path.Combine's mixed separators
        [InlineData(@"C:\ws\dotnet\.\proj\Foo.cs")]
        [InlineData(@"C:\ws\dotnet\proj\..\proj\Foo.cs")]
        public void EverySpellingOfOneFile_CollapsesToOne(string spelling)
        {
            Assert.Equal(@"C:\ws\dotnet\proj\Foo.cs", AgentPath.Canonical(spelling, Root));
        }

        /// <summary>
        /// With no root there is nothing to measure from, and inventing one is worse than leaving it:
        /// GetFullPath would resolve against the PROCESS working directory, which in the VS host is
        /// devenv's install folder. Separators still unify, so two relative spellings meet.
        /// </summary>
        [Fact]
        public void NoRoot_LeavesARelativePathRelative()
        {
            Assert.Equal(@"proj\Foo.cs", AgentPath.Canonical(@"proj/Foo.cs"));
            Assert.Equal(@"proj\Foo.cs", AgentPath.Canonical(@"proj\Foo.cs", ""));
            Assert.Equal(
                AgentPath.Canonical(@"proj/Foo.cs"),
                AgentPath.Canonical(@"proj\Foo.cs"));
        }

        /// <summary>
        /// The root only ever roots — an absolute path is already an answer, and re-rooting one would be
        /// how a path picks up a duplicated leading segment.
        /// </summary>
        [Fact]
        public void AnAbsolutePath_IgnoresTheRoot()
        {
            Assert.Equal(@"C:\other\Foo.cs", AgentPath.Canonical(@"C:\other\Foo.cs", Root));
            Assert.Equal(@"C:\ws\dotnet\proj\Foo.cs", AgentPath.Canonical(@"C:\ws\dotnet\proj\Foo.cs", Root));
        }

        /// <summary>
        /// WHICH root is used is the whole of the applier bug: the agent measures from its own cwd, and
        /// the solution root can sit below it when the workspace marker walk widens (#54). Same input,
        /// two roots, two different real files — neither call fails.
        /// </summary>
        [Fact]
        public void TheSameRelativePath_NamesADifferentFilePerRoot()
        {
            Assert.Equal(
                @"C:\ws\dotnet\proj\Foo.cs",
                AgentPath.Canonical(@"proj/Foo.cs", @"C:\ws\dotnet"));
            Assert.Equal(
                @"C:\ws\dotnet\sub\proj\Foo.cs",
                AgentPath.Canonical(@"proj/Foo.cs", @"C:\ws\dotnet\sub"));
        }

        /// <summary>
        /// The applier bug itself: it had only the solution root, so a relative agent path resolved a
        /// level too deep whenever the marker walk had widened. The agent's own cwd must win.
        /// </summary>
        [Fact]
        public void PreferredRoot_TakesTheAgentsCwdOverTheSolutionRoot()
        {
            Assert.Equal(@"C:\ws", AgentPath.PreferredRoot(@"C:\ws", @"C:\ws\dotnet", @"C:\devenv"));
            Assert.Equal(
                @"C:\ws\proj\Foo.cs",
                AgentPath.Canonical(@"proj/Foo.cs", AgentPath.PreferredRoot(@"C:\ws", @"C:\ws\dotnet")));
        }

        /// <summary>
        /// No session yet, or a backend that reports no working directory: the solution root is right
        /// whenever the two coincide, so it stays the fallback rather than the agent root being required.
        /// Falling through both lands on the caller's last resort, never on a stale root.
        /// </summary>
        [Fact]
        public void PreferredRoot_FallsBackInOrder()
        {
            Assert.Equal(@"C:\ws\dotnet", AgentPath.PreferredRoot(null, @"C:\ws\dotnet", @"C:\devenv"));
            Assert.Equal(@"C:\ws\dotnet", AgentPath.PreferredRoot("", @"C:\ws\dotnet", @"C:\devenv"));
            Assert.Equal(@"C:\devenv", AgentPath.PreferredRoot(null, null, @"C:\devenv"));
            Assert.Equal(@"C:\devenv", AgentPath.PreferredRoot("", "", @"C:\devenv"));
            Assert.Null(AgentPath.PreferredRoot(null, null, null));
        }

        [Fact]
        public void AUncPath_SurvivesIntact()
        {
            Assert.Equal(@"\\server\share\proj\Foo.cs", AgentPath.Canonical(@"\\server\share\proj\Foo.cs", Root));
        }

        /// <summary>
        /// Nothing here may throw: it sits on the tool-call path, and an exception mid-turn costs the
        /// frame. An unusable path comes back as itself.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("file://")]
        [InlineData("file:///")]
        [InlineData("not a uri://///")]
        public void JunkIsReturnedRatherThanThrown(string junk)
        {
            var result = AgentPath.Canonical(junk, Root);
            Assert.NotNull(result);
        }

        [Fact]
        public void IsRooted_TreatsTheUnusableAsRelative()
        {
            Assert.True(AgentPath.IsRooted(@"C:\ws"));
            Assert.True(AgentPath.IsRooted(@"\\server\share"));
            Assert.False(AgentPath.IsRooted(@"proj\Foo.cs"));
            Assert.False(AgentPath.IsRooted(""));
            Assert.False(AgentPath.IsRooted(null!));
        }
    }
}
