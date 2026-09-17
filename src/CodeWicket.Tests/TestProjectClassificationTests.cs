using System;
using System.IO;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Which of a solution's projects <c>run_tests</c> treats as test projects, and how each one runs
    /// (<see cref="TestProjects.Classify"/>).
    /// <para>
    /// These rules had no coverage at all until they moved out of <c>VsToolCatalog</c>, and their two
    /// failure directions are not symmetric. Missing a test project reports "no test projects found in
    /// the solution" — a whole suite silently absent. Claiming one too eagerly is worse: an MTP project
    /// runs via <c>dotnet run</c>, so a misclassified web app would start a server that never exits and
    /// the tool call would hang to its 15-minute timeout. That is what
    /// <see cref="MtpRequiresLooksLikeATestProject"/> guards.
    /// </para>
    /// </summary>
    public sealed class TestProjectClassificationTests : IDisposable
    {
        // Dev-only scratch, so temp is fine here (the never-use-temp rule is about agent-facing paths).
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-testproj-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        /// <summary>Writes a file under the scratch root, creating directories as needed.</summary>
        private string Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>An SDK-style project whose body is <paramref name="inner"/>.</summary>
        private string Project(string relativePath, string inner) =>
            Write(relativePath, $"<Project Sdk=\"Microsoft.NET.Sdk\">{inner}</Project>");

        private static TestProject? Single(string projectPath) =>
            TestProjects.Classify(new[] { projectPath }).SingleOrDefault();

        private const string TestSdkRef =
            "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.13.0\" /></ItemGroup>";

        // --- classic vstest -------------------------------------------------------------------------

        [Fact]
        public void SdkProjectWithTestSdkPackage_IsClassic()
        {
            var csproj = Project("Tests/Tests.csproj", TestSdkRef);

            var classified = Single(csproj);

            Assert.NotNull(classified);
            Assert.Equal(TestProjectKind.Classic, classified!.Kind);
            Assert.Equal(csproj, classified.ProjectFile);
        }

        [Fact]
        public void TestSdkPackageIsFoundUpTree()
        {
            Write("Directory.Build.props", $"<Project>{TestSdkRef}</Project>");
            var csproj = Project("Tests/Tests.csproj", "");

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        /// <summary>
        /// The negative that keeps the classifier from claiming ordinary projects. This is also the case
        /// old-style (non-SDK) test projects currently fall into — pinned here so that when they gain a
        /// kind of their own, the diff shows exactly which projects changed side.
        /// </summary>
        [Fact]
        public void ProjectWithNeitherSignal_IsNotClassified()
        {
            var csproj = Project("App/App.csproj",
                "<ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup>");

            Assert.Empty(TestProjects.Classify(new[] { csproj }));
        }

        // --- Microsoft Testing Platform -------------------------------------------------------------

        [Fact]
        public void MtpRunnerProperty_MakesATestProjectMtp()
        {
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + "<ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"1.0.0\" /></ItemGroup>");

            Assert.Equal(TestProjectKind.Mtp, Single(csproj)!.Kind);
        }

        [Fact]
        public void MtpRunnerProperty_IsInheritedFromDirectoryBuildProps()
        {
            Write("Directory.Build.props",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
            var csproj = Project("Tests/Tests.csproj",
                "<ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"1.0.0\" /></ItemGroup>");

            Assert.Equal(TestProjectKind.Mtp, Single(csproj)!.Kind);
        }

        /// <summary>
        /// Nearest definition wins, mirroring MSBuild precedence: a project-level <c>false</c> overrides a
        /// props-level <c>true</c>. Walking to the first definition found ANYWHERE instead would run a
        /// project that explicitly opted out through the MTP host.
        /// </summary>
        [Fact]
        public void MtpRunnerProperty_IsNearestDefinitionWins()
        {
            Write("Directory.Build.props",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + TestSdkRef);

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        /// <summary>
        /// A conditioned definition can't be evaluated without MSBuild, so it is ignored rather than
        /// guessed at — guessing "true" is the direction that runs a non-test project.
        /// </summary>
        [Fact]
        public void ConditionedRunnerProperty_IsIgnored()
        {
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup Condition=\"'$(Configuration)'=='Debug'\">"
                + "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + TestSdkRef);

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        [Fact]
        public void LastUnconditionalDefinitionInAFileWins()
        {
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + "<PropertyGroup><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + TestSdkRef);

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        /// <summary>
        /// The highest-consequence rule here. <c>UseMicrosoftTestingPlatformRunner</c> is commonly set
        /// repo-wide in <c>Directory.Build.props</c>; without the looks-like-a-test-project half of the
        /// gate, every project under it — a web app included — would be run with <c>dotnet run</c>.
        /// </summary>
        [Fact]
        public void MtpRequiresLooksLikeATestProject()
        {
            Write("Directory.Build.props",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
            var webApp = Project("Web/Web.csproj",
                "<ItemGroup><PackageReference Include=\"Serilog.AspNetCore\" Version=\"8.0.0\" /></ItemGroup>");

            Assert.Empty(TestProjects.Classify(new[] { webApp }));
        }

        [Fact]
        public void IsTestProjectProperty_SatisfiesTheMtpTestProjectGate()
        {
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>"
                + "<IsTestProject>true</IsTestProject></PropertyGroup>");

            Assert.Equal(TestProjectKind.Mtp, Single(csproj)!.Kind);
        }

        [Fact]
        public void MtpBeatsClassic_WhenBothSignalsArePresent()
        {
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + TestSdkRef);

            Assert.Equal(TestProjectKind.Mtp, Single(csproj)!.Kind);
        }

        // --- package matching -----------------------------------------------------------------------

        /// <summary>
        /// Package ids are versioned families, so the test-framework list is matched as a PREFIX —
        /// <c>xunit.v3.assert</c> counts as <c>xunit</c>.
        /// </summary>
        [Fact]
        public void PackageReferencePrefixMatches_NotExact()
        {
            var csproj = Project("Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + "<ItemGroup><PackageReference Include=\"xunit.v3.assert\" Version=\"1.0.0\" /></ItemGroup>");

            Assert.Equal(TestProjectKind.Mtp, Single(csproj)!.Kind);
        }

        // --- unreadable input -----------------------------------------------------------------------

        [Fact]
        public void MissingProjectFile_IsSkipped()
        {
            var missing = Path.Combine(_root, "Gone", "Gone.csproj");

            Assert.Empty(TestProjects.Classify(new[] { missing }));
        }

        [Fact]
        public void UnparseableProject_IsSkipped()
        {
            var csproj = Write("Broken/Broken.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>");

            Assert.Empty(TestProjects.Classify(new[] { csproj }));
        }

        [Fact]
        public void ClassifyReturnsOneEntryPerTestProject_AndSkipsTheRest()
        {
            var tests = Project("Tests/Tests.csproj", TestSdkRef);
            var app = Project("App/App.csproj", "");

            var classified = TestProjects.Classify(new[] { app, tests });

            Assert.Equal(new[] { tests }, classified.Select(p => p.ProjectFile));
        }

        // --- projects whose test-ness comes from their SDK ------------------------------------------
        //
        // Found against a real solution (2026-09-04), where a project VS's Test Explorer
        // ran perfectly well was classified as not a test project at all. A test SDK brings the framework,
        // the adapter and the runner in with it, so the project file declares NO test package: the
        // `Microsoft.NET.Test.Sdk` reference every other branch looks for is injected by the SDK's own
        // targets. That makes this the SILENT direction - the same "no test projects found in the
        // solution" the legacy work exists to remove, reached from the opposite end.

        /// <summary>A project whose only test signal is the SDK it names.</summary>
        private string SdkProject(string relativePath, string sdk, string inner = "") =>
            Write(relativePath, $"<Project Sdk=\"{sdk}\">{inner}</Project>");

        [Fact]
        public void MSTestSdkProject_IsATestProjectDespiteDeclaringNoTestPackage()
        {
            var csproj = SdkProject("Tests/Tests.csproj", "MSTest.Sdk/4.0.2");

            Assert.NotNull(Single(csproj));
        }

        /// <summary>
        /// The version suffix is part of the SDK reference (<c>MSTest.Sdk/4.0.2</c>), so matching the
        /// attribute whole would recognise no real project at all - every one of them pins a version.
        /// </summary>
        [Theory]
        [InlineData("MSTest.Sdk")]
        [InlineData("MSTest.Sdk/4.0.2")]
        [InlineData("Microsoft.NET.Sdk;MSTest.Sdk/4.0.2")]
        public void TheSdkNameIsMatchedWithoutItsVersionOrItsNeighbours(string sdk)
        {
            Assert.NotNull(Single(SdkProject("Tests/Tests.csproj", sdk)));
        }

        /// <summary>
        /// Read from MSTest.Sdk's own targets rather than from documentation: <c>Sdk.props</c> defaults
        /// <c>UseVSTest</c> to false and <c>Sdk.targets</c> imports the MTP runner in that case, so the
        /// absence of the property means MTP rather than meaning nothing.
        /// </summary>
        [Fact]
        public void MSTestSdkProject_DefaultsToTheMtpRunner()
        {
            var csproj = SdkProject("Tests/Tests.csproj", "MSTest.Sdk/4.0.2");

            Assert.Equal(TestProjectKind.Mtp, Single(csproj)!.Kind);
        }

        /// <summary>
        /// `UseVSTest` is the only thing that moves it, and it moves it to the runner that `dotnet test`
        /// drives. Getting this backwards runs a VSTest project through `dotnet run`, which is the
        /// direction that hangs rather than fails.
        /// </summary>
        [Fact]
        public void MSTestSdkProject_WithUseVsTest_IsClassic()
        {
            var csproj = SdkProject("Tests/Tests.csproj", "MSTest.Sdk/4.0.2",
                "<PropertyGroup><UseVSTest>true</UseVSTest></PropertyGroup>");

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        /// <summary>
        /// Every test SDK recognised here is an MSTest one, which exposes the VSTest filter expression
        /// through the bridge. Left to the package-reference readers the project has no framework signal
        /// at all and lands on <see cref="FilterDialect.Unknown"/>, where a structured filter fails loud
        /// against a framework that would have understood it.
        /// </summary>
        [Fact]
        public void MSTestSdkProject_TakesTheVsTestFilterDialect()
        {
            var csproj = SdkProject("Tests/Tests.csproj", "MSTest.Sdk/4.0.2");

            Assert.Equal(FilterDialect.VsTest, Single(csproj)!.Dialect);
        }

        /// <summary>
        /// The other direction, and the one that would hurt: naming an SDK must not be a test signal in
        /// itself. Every project in a modern solution declares one, so a rule matching too broadly here
        /// sends the whole solution to a test runner.
        /// </summary>
        [Fact]
        public void AnOrdinarySdkProject_IsNotATestProjectBecauseItNamesAnSdk()
        {
            Assert.Null(Single(SdkProject("App/App.csproj", "Microsoft.NET.Sdk")));
            Assert.Null(Single(SdkProject("Web/Web.csproj", "Microsoft.NET.Sdk.Web")));
        }

        // --- only the props chain MSBuild actually imports ------------------------------------------

        /// <summary>
        /// MSBuild imports ONE <c>Directory.Build.props</c>: the first found walking up. A nearer file
        /// that doesn't chain ends the walk, so a root file's package references never reach the
        /// project — and reading them anyway is how a web app becomes a test project. It's the
        /// dangerous direction, because an MTP misclassification runs the thing via <c>dotnet run</c>.
        /// </summary>
        [Fact]
        public void ARootPropsIsNotReadThroughANearerOneThatDoesNotChain()
        {
            Write("Directory.Build.props", $"<Project>{TestSdkRef}</Project>");
            Write("src/Directory.Build.props", "<Project />");
            var csproj = Project("src/Web/Web.csproj", "");

            Assert.Null(Single(csproj));
        }

        /// <summary>…and when the nearer file DOES chain, the root's reference applies as it always did.</summary>
        [Fact]
        public void ARootPropsIsReadThroughANearerOneThatChains()
        {
            Write("Directory.Build.props", $"<Project>{TestSdkRef}</Project>");
            Write("src/Directory.Build.props",
                "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', "
                + "'$(MSBuildThisFileDirectory)../'))\" /></Project>");
            var csproj = Project("src/Tests/Tests.csproj", "");

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        /// <summary>A plain relative import is the same chain, spelled the other way.</summary>
        [Fact]
        public void APlainRelativeImportChainsToo()
        {
            Write("Directory.Build.props", $"<Project>{TestSdkRef}</Project>");
            Write("src/Directory.Build.props", "<Project><Import Project=\"../Directory.Build.props\" /></Project>");
            var csproj = Project("src/Tests/Tests.csproj", "");

            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }

        /// <summary>
        /// The dialect half of the same over-reach, and the one that reaches the command line: an
        /// unchained root declaring xunit.v3 used to make an MSTest project answer
        /// <see cref="FilterDialect.XunitV3"/>, so <c>run_tests</c> built <c>--filter-method</c>
        /// arguments the MSTest runner rejects outright.
        /// </summary>
        [Fact]
        public void TheFilterDialectComesFromTheImportedChainOnly()
        {
            Write("Directory.Build.props",
                "<Project><ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"1.0.0\" /></ItemGroup></Project>");
            Write("src/Directory.Build.props", "<Project />");
            var csproj = Project("src/Tests/Tests.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>"
                + "</PropertyGroup><ItemGroup><PackageReference Include=\"MSTest\" Version=\"3.8.0\" /></ItemGroup>");

            var classified = Single(csproj);

            Assert.Equal(TestProjectKind.Mtp, classified!.Kind);
            Assert.Equal(FilterDialect.VsTest, classified.Dialect);
        }

        /// <summary>
        /// "Nearest definition wins" has to mean the nearest MSBuild would see. An unchained props file
        /// between the project and a repo-wide <c>UseMicrosoftTestingPlatformRunner</c> cuts it off, so
        /// the project keeps the default rather than a value it never receives.
        /// </summary>
        [Fact]
        public void TheRunnerPropertyStopsAtAPropsFileThatDoesNotChain()
        {
            Write("Directory.Build.props",
                "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>true"
                + "</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
            Write("src/Directory.Build.props", "<Project />");
            var csproj = Project("src/Tests/Tests.csproj", TestSdkRef);

            Assert.False(TestProjects.MtpRunnerEnabled(csproj));
            Assert.Equal(TestProjectKind.Classic, Single(csproj)!.Kind);
        }
    }
}
