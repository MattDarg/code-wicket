using System;
using System.Collections.Generic;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The shared rule for resolving an agent-written file path against the files the IDE knows about.
    /// <para>
    /// These pin behaviour that was only ever verified in Visual Studio before, on a type extracted from
    /// <c>VsToolCatalog</c> precisely so it could be pinned: the tool catalog is net472 + VS SDK + Roslyn
    /// and the test project cannot reference it, so every rule below used to live somewhere no test could
    /// reach. The Roslyn part (enumerating a solution's documents) stays there; only the decision moved.
    /// </para>
    /// </summary>
    public sealed class FilePathMatchTests
    {
        private const string Root = @"C:\repo\sln";

        private static readonly string[] Solution =
        {
            @"C:\repo\sln\src\Engine\Program.cs",
            @"C:\repo\sln\src\Console\Program.cs",
            @"C:\repo\sln\src\Engine\EngineService.cs",
            @"C:\repo\sln\src\Engine\MyProgram.cs",
        };

        private static PathResolution Resolve(string? requested, string? root = Root)
            => FilePathMatch.Resolve(Solution, requested, root);

        // ---- Matches: the filter rule -------------------------------------------------------------

        [Theory]
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", @"C:\repo\sln\src\Engine\Program.cs")] // exact
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", @"Engine\Program.cs")]                 // suffix
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", "Engine/Program.cs")]                  // forward slashes
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", @"engine\program.CS")]                 // case-insensitive
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", "Program.cs")]                         // bare leaf
        public void Matches_acceptsTheSpellingsAgentsActuallyWrite(string candidate, string requested)
            => Assert.True(FilePathMatch.Matches(candidate, requested));

        /// <summary>
        /// The suffix has to begin at a separator. Without that, a request for Program.cs silently also
        /// means MyProgram.cs — and for apply_code_fix that is an edit to a file nobody named.
        /// </summary>
        [Fact]
        public void Matches_requiresTheSuffixToStartAtASeparator()
        {
            Assert.False(FilePathMatch.Matches(@"C:\repo\sln\src\Engine\MyProgram.cs", "Program.cs"));
            Assert.True(FilePathMatch.Matches(@"C:\repo\sln\src\Engine\MyProgram.cs", "MyProgram.cs"));
        }

        /// <summary>An absolute request names one file; matching it loosely would defeat the point of it.</summary>
        [Fact]
        public void Matches_neverTreatsAnAbsoluteRequestAsASuffix()
            => Assert.False(FilePathMatch.Matches(@"C:\other\sln\src\Engine\Program.cs", @"C:\repo\sln\src\Engine\Program.cs"));

        [Theory]
        [InlineData(null, "Program.cs")]
        [InlineData("", "Program.cs")]
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", null)]
        [InlineData(@"C:\repo\sln\src\Engine\Program.cs", "")]
        public void Matches_isFalseWhenEitherSideIsMissing(string? candidate, string? requested)
            => Assert.False(FilePathMatch.Matches(candidate, requested));

        // ---- Resolve: precision order -------------------------------------------------------------

        [Fact]
        public void Resolve_takesAnExactPathAsWritten()
            => Assert.Equal(@"C:\repo\sln\src\Engine\EngineService.cs",
                Resolve(@"C:\repo\sln\src\Engine\EngineService.cs").Path);

        /// <summary>
        /// The spelling that produced this whole seam: agents write workspace-relative paths, Roslyn's
        /// Document.FilePath is always absolute, and comparing them for equality refused the fix twice.
        /// </summary>
        [Fact]
        public void Resolve_combinesARelativePathWithTheRoot()
            => Assert.Equal(@"C:\repo\sln\src\Engine\EngineService.cs",
                Resolve(@"src\Engine\EngineService.cs").Path);

        [Fact]
        public void Resolve_fallsBackToAUniqueSuffixWhenThereIsNoRoot()
            => Assert.Equal(@"C:\repo\sln\src\Engine\EngineService.cs",
                Resolve(@"Engine\EngineService.cs", root: null).Path);

        [Fact]
        public void Resolve_matchesAUniqueBareLeafName()
            => Assert.Equal(@"C:\repo\sln\src\Engine\EngineService.cs", Resolve("EngineService.cs").Path);

        // ---- Resolve: ambiguity -------------------------------------------------------------------

        /// <summary>
        /// Two files, so the request cannot be acted on — and the CALLER needs to know which two. Reporting
        /// this as "not found" (what the code did before) describes the opposite problem and sends the
        /// caller looking for a file that is right there.
        /// </summary>
        [Fact]
        public void Resolve_reportsEveryCandidateWhenABareNameIsAmbiguous()
        {
            var resolution = Resolve("Program.cs");

            Assert.True(resolution.IsAmbiguous);
            Assert.False(resolution.IsMatch);
            Assert.Null(resolution.Path);
            Assert.Equal(
                new[] { @"C:\repo\sln\src\Engine\Program.cs", @"C:\repo\sln\src\Console\Program.cs" },
                resolution.AmbiguousMatches);
        }

        /// <summary>A longer fragment is the documented way out of the ambiguity, so it has to work.</summary>
        [Fact]
        public void Resolve_disambiguatesOnALongerFragment()
        {
            Assert.Equal(@"C:\repo\sln\src\Engine\Program.cs", Resolve(@"Engine\Program.cs").Path);
            Assert.Equal(@"C:\repo\sln\src\Console\Program.cs", Resolve("Console/Program.cs").Path);
        }

        /// <summary>
        /// A multi-targeted project surfaces one file once per target framework. Those are the same file,
        /// not a collision — reading them as ambiguous would refuse the commonest case in this solution.
        /// </summary>
        [Fact]
        public void Resolve_doesNotCallRepeatsOfOneFileAmbiguous()
        {
            var multiTargeted = new[]
            {
                @"C:\repo\sln\src\Ui\Shared.cs", // net472
                @"C:\repo\sln\src\Ui\Shared.cs", // net10.0
                @"C:\REPO\SLN\SRC\UI\Shared.cs", // and a differently-cased spelling of the same path
            };

            var resolution = FilePathMatch.Resolve(multiTargeted, "Shared.cs", Root);

            Assert.False(resolution.IsAmbiguous);
            Assert.Equal(@"C:\repo\sln\src\Ui\Shared.cs", resolution.Path);
        }

        /// <summary>An absolute path that matched nothing is wrong, not under-specified — never ambiguous.</summary>
        [Fact]
        public void Resolve_treatsAnUnmatchedAbsolutePathAsMissing()
        {
            var resolution = Resolve(@"C:\elsewhere\Program.cs");

            Assert.False(resolution.IsMatch);
            Assert.False(resolution.IsAmbiguous);
            Assert.Empty(resolution.AmbiguousMatches);
        }

        [Fact]
        public void Resolve_reportsNothingForAPathTheSolutionDoesNotHave()
        {
            var resolution = Resolve("NotHere.cs");

            Assert.False(resolution.IsMatch);
            Assert.False(resolution.IsAmbiguous);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Resolve_isEmptyWithoutARequest(string? requested)
            => Assert.False(FilePathMatch.Resolve(Solution, requested, Root).IsMatch);

        [Fact]
        public void Resolve_isEmptyWithoutCandidates()
            => Assert.False(FilePathMatch.Resolve(null, "Program.cs", Root).IsMatch);

        [Fact]
        public void Resolve_skipsCandidatesWithNoPath()
        {
            var withHoles = new string?[] { null, "", @"C:\repo\sln\src\Engine\EngineService.cs" };

            Assert.Equal(@"C:\repo\sln\src\Engine\EngineService.cs",
                FilePathMatch.Resolve(withHoles, "EngineService.cs", Root).Path);
        }

        // ---- The invariant the seam exists to hold ------------------------------------------------

        /// <summary>
        /// get_diagnostics' filter and apply_code_fix's document lookup must accept the same spellings.
        /// Them disagreeing is the original defect: a diagnostic could be FOUND at a relative path and
        /// then refused when a fix was requested at that same path. Both now route through this type, so
        /// the agreement is structural — this asserts the two entry points cannot answer differently.
        /// </summary>
        [Theory]
        [InlineData(@"C:\repo\sln\src\Engine\EngineService.cs")]
        [InlineData(@"src\Engine\EngineService.cs")]
        [InlineData("Engine/EngineService.cs")]
        [InlineData("EngineService.cs")]
        public void TheFilterAndTheLookupAgreeOnEverySpelling(string requested)
        {
            var resolved = Resolve(requested);
            Assert.True(resolved.IsMatch, $"lookup rejected '{requested}'");

            // The filter is asked the same question about the file the lookup chose.
            Assert.True(FilePathMatch.Matches(resolved.Path, requested), $"filter rejected '{requested}'");
        }
    }
}
