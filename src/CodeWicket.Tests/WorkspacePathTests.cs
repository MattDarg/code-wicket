using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <see cref="WorkspacePath"/> decides what an edit card SHOWS. The absolute case was always right;
    /// the relative case was not, and it failed in the one way a path bug can hide — by returning the
    /// input unchanged, which still looks like a path.
    /// </summary>
    public sealed class WorkspacePathTests
    {
        private const string Root = @"C:\ws\dotnet";

        [Fact]
        public void AbsolutePathUnderRoot_IsShownRelative()
        {
            Assert.Equal("proj/Foo.cs", WorkspacePath.Relative(@"C:\ws\dotnet\proj\Foo.cs", Root));
        }

        /// <summary>
        /// The regression. An agent-reported path can already BE relative (Kiro v3 sends whatever the
        /// model typed), and it is relative to the same root. Handing it to <c>Path.GetFullPath</c>
        /// resolved it against the PROCESS working directory — devenv's install dir — so the prefix test
        /// never matched and the raw string was displayed. Two cards for one edit then showed a different
        /// number of leading segments, each measured from a different origin.
        /// </summary>
        [Fact]
        public void RelativePath_IsResolvedAgainstTheRoot_NotTheProcessDirectory()
        {
            Assert.Equal("proj/Foo.cs", WorkspacePath.Relative(@"proj/Foo.cs", Root));
            Assert.Equal("proj/Foo.cs", WorkspacePath.Relative(@"proj\Foo.cs", Root));
        }

        /// <summary>
        /// Both spellings of one file must display identically — that is what the user sees when the two
        /// rows are compared. Asserted on a spelling whose RAW form differs from the display form
        /// (backslashes, and a nested path): with a single POSIX-spelled segment the unfixed code returns
        /// the input unchanged and that happens to equal the right answer, so such a case would pass over
        /// the bug rather than catch it.
        /// </summary>
        [Theory]
        [InlineData(@"proj\Foo.cs")]
        [InlineData(@"proj\sub/Foo.cs")]
        [InlineData(@".\proj\sub\Foo.cs")]
        public void RelativeAndAbsoluteSpellings_DisplayTheSame(string relative)
        {
            var absolute = System.IO.Path.Combine(Root, relative);

            Assert.Equal(
                WorkspacePath.Relative(absolute, Root),
                WorkspacePath.Relative(relative, Root));
        }

        /// <summary>
        /// An edit outside the workspace stays absolute — that is deliberate, so it stays obvious. A
        /// sibling directory must not match on a bare prefix either (<c>ws2</c> against root <c>ws</c>).
        /// </summary>
        [Fact]
        public void OutsideTheRoot_StaysAbsolute()
        {
            Assert.Equal(@"C:\other\Foo.cs", WorkspacePath.Relative(@"C:\other\Foo.cs", Root));
            Assert.Equal(@"C:\ws\dotnet2\Foo.cs", WorkspacePath.Relative(@"C:\ws\dotnet2\Foo.cs", Root));
        }

        [Fact]
        public void NoRoot_LeavesThePathAlone()
        {
            Assert.Equal(@"proj/Foo.cs", WorkspacePath.Relative(@"proj/Foo.cs", null));
            Assert.Equal(@"proj/Foo.cs", WorkspacePath.Relative(@"proj/Foo.cs", ""));
        }

        /// <summary>
        /// The shared predicate, on the case that separates it from a bare prefix test: a SIBLING whose
        /// name extends the root's. Without the trailing separator, <c>dotnet2</c> is reported as inside
        /// <c>dotnet</c>, and everything under it is then displayed and transcribed with its first
        /// segment eaten — a path that still resolves, to the wrong file.
        /// </summary>
        [Fact]
        public void IsUnderRoot_DoesNotMatchASiblingByPrefix()
        {
            Assert.True(WorkspacePath.IsUnderRoot(@"C:\ws\dotnet\proj\Foo.cs", Root));
            Assert.False(WorkspacePath.IsUnderRoot(@"C:\ws\dotnet2\Foo.cs", Root));
            Assert.False(WorkspacePath.IsUnderRoot(@"C:\other\Foo.cs", Root));
        }
    }

    /// <summary>
    /// <see cref="WorkspacePath.ForPrompt"/> is the spelling handed to the AGENT — from a dropped file
    /// today. It shares the display rule so that the user and the agent can never read two different
    /// origins for one file, but it may NOT share the display fallback, which is what the last test here
    /// is about.
    /// </summary>
    public sealed class PromptPathTests
    {
        private const string Root = @"C:\ws\dotnet";

        /// <summary>
        /// The short form happens at all. It is unambiguous because the root IS the directory the agent
        /// measures its own relative paths from.
        /// </summary>
        [Fact]
        public void UnderTheRoot_IsRelative()
        {
            Assert.Equal("proj/Foo.cs", WorkspacePath.ForPrompt(@"C:\ws\dotnet\proj\Foo.cs", Root));
        }

        /// <summary>
        /// Outside the root the full path is the only unambiguous spelling. The sibling case is here too,
        /// because on this side of the rule a wrongly-shortened path does not merely display oddly — it is
        /// what the agent goes and opens.
        /// </summary>
        [Fact]
        public void OutsideTheRoot_StaysAbsolute()
        {
            Assert.Equal(@"C:\other\Foo.cs", WorkspacePath.ForPrompt(@"C:\other\Foo.cs", Root));
            Assert.Equal(@"C:\ws\dotnet2\Foo.cs", WorkspacePath.ForPrompt(@"C:\ws\dotnet2\Foo.cs", Root));
        }

        /// <summary>
        /// Unquoted, <c>C:\Program Files\x.txt</c> reaches the agent as two tokens — and the first of them
        /// names a directory that exists, which is how it gets acted on rather than questioned.
        /// </summary>
        [Fact]
        public void WhitespaceInThePath_IsQuoted()
        {
            Assert.Equal("\"my docs/x.txt\"", WorkspacePath.ForPrompt(@"C:\ws\dotnet\my docs\x.txt", Root));
            Assert.Equal("\"C:\\Program Files\\x.txt\"", WorkspacePath.ForPrompt(@"C:\Program Files\x.txt", Root));
        }

        /// <summary>
        /// The reverse, and not redundant: without it an unconditional "always quote" passes the test
        /// above, and every ordinary path then arrives wearing quotes it does not need.
        /// </summary>
        [Fact]
        public void PathWithoutWhitespace_IsNotQuoted()
        {
            Assert.Equal("proj/Foo.cs", WorkspacePath.ForPrompt(@"C:\ws\dotnet\proj\Foo.cs", Root));
        }

        /// <summary>
        /// Order of operations, pinned where it is observable: a root that itself contains a space. Quote
        /// first and the <c>"</c> defeats the prefix match, so the result comes back absolute — plausible
        /// enough to pass a glance, and needlessly long on every drop in such a workspace.
        /// </summary>
        [Fact]
        public void QuotingHappensAfterRelativising()
        {
            Assert.Equal(
                "proj/Foo.cs",
                WorkspacePath.ForPrompt(@"C:\my ws\dotnet\proj\Foo.cs", @"C:\my ws\dotnet"));
        }

        /// <summary>
        /// Given a root, a relative input is measured from THAT root rather than from the process working
        /// directory — the payload-side half of the rule the display side already learned. Emitting a path
        /// measured from devenv's install folder is issue #54: not an error, but a DIFFERENT REAL FILE,
        /// created by the write, so the edit reports clean while the file the user meant is untouched.
        /// </summary>
        [Fact]
        public void ARelativeInput_IsMeasuredFromTheRoot()
        {
            Assert.Equal("proj/Foo.cs", WorkspacePath.ForPrompt(@"proj\Foo.cs", Root));
            Assert.Equal("proj/Foo.cs", WorkspacePath.ForPrompt(@"proj/Foo.cs", Root));
        }

        /// <summary>
        /// The contract's limit, asserted so it stays a deliberate choice rather than a discovery. With
        /// neither a rooted path nor a root there is nothing to measure from: the only mechanism available
        /// is <c>GetFullPath</c>, which would silently adopt the PROCESS working directory — devenv's
        /// install folder — and manufacture a confident, wrong absolute path. Returning the caller's own
        /// string adds no false precision, and the drop path holds up the other end by refusing tokens
        /// that are not rooted (see <c>FileDropPathsTests.RelativeTokens_AreRefused</c>).
        /// </summary>
        [Fact]
        public void NoRootAndNothingToMeasureFrom_AddsNoFalsePrecision()
        {
            var spelled = WorkspacePath.ForPrompt(@"proj\Foo.cs", null);

            Assert.Equal(@"proj\Foo.cs", spelled);
            Assert.DoesNotContain(System.AppContext.BaseDirectory, spelled);
        }

        /// <summary>
        /// A <c>file://</c> URI and a lower-case drive letter reach the agent as the one spelling the
        /// transcript is already showing — the part <see cref="WorkspacePath.Relative"/> alone does not do,
        /// and the reason <see cref="WorkspacePath.ForPrompt"/> canonicalizes first.
        /// </summary>
        [Fact]
        public void ItSpellsAFileUriAndDriveCaseTheSameWayTheTranscriptDoes()
        {
            Assert.Equal("proj/Foo.cs", WorkspacePath.ForPrompt("file:///c%3A/ws/dotnet/proj/Foo.cs", Root));
            Assert.Equal("proj/Foo.cs", WorkspacePath.ForPrompt(@"c:\ws\dotnet\proj\Foo.cs", Root));
        }
    }
}
