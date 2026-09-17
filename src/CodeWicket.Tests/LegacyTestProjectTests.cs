using System;
using System.IO;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Old-style (non-SDK) .NET Framework test projects: recognising them, and building the
    /// <c>vstest.console.exe</c> command line that runs one.
    ///
    /// <para>
    /// Reported from a real legacy solution, where <c>run_tests</c> answered "no test projects found in
    /// the solution" while Visual Studio's own Test Explorer ran the project perfectly well. Two
    /// independent gaps sat behind that one message: the project was never <b>discovered</b>, and even
    /// discovered it could not have been <b>executed</b>, because <c>dotnet test</c> and <c>dotnet run</c>
    /// both refuse a non-SDK project.
    /// </para>
    ///
    /// <para>
    /// The rule these pin is <b>the runner follows project STYLE, not which test framework is
    /// referenced</b>. That distinction is invisible in the common cases and decisive in one:
    /// <see cref="NonSdkProjectUsingPackageReference_IsLegacyNotClassic"/>, where every modern signal is
    /// present on a project that still cannot be run by the modern runner.
    /// </para>
    /// </summary>
    public sealed class LegacyTestProjectTests : IDisposable
    {
        // Dev-only scratch, so temp is fine here (the never-use-temp rule is about agent-facing paths).
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-legacyproj-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private string Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private static TestProject? Single(string projectPath) =>
            TestProjects.Classify(new[] { projectPath }).SingleOrDefault();

        /// <summary>An old-style project: an MSBuild namespace, no Sdk attribute anywhere.</summary>
        private string LegacyProject(string relativePath, string inner) => Write(relativePath,
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
            + inner + "</Project>");

        private const string MsTestProjectTypeGuids =
            "<PropertyGroup><ProjectTypeGuids>"
            + "{3AC096D0-A1C2-E12C-1390-A8335801FDAB};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"
            + "</ProjectTypeGuids></PropertyGroup>";

        private const string QualityToolsReference =
            "<ItemGroup><Reference Include=\"Microsoft.VisualStudio.QualityTools.UnitTestFramework, "
            + "Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a\" /></ItemGroup>";

        // --- project style --------------------------------------------------------------------------

        [Theory]
        // The three legal ways to be SDK-style. A project needs only one of them.
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>")]
        [InlineData("<Project><Sdk Name=\"Microsoft.NET.Sdk\" /><PropertyGroup /></Project>")]
        [InlineData("<Project><Import Sdk=\"Microsoft.NET.Sdk\" Project=\"Sdk.props\" /></Project>")]
        public void EverySdkSpelling_IsRecognisedAsSdkStyle(string xml)
        {
            var csproj = Write("P/P.csproj", xml);

            Assert.Equal(ProjectStyle.Sdk, TestProjects.DetermineProjectStyle(csproj));
        }

        [Fact]
        public void AProjectWithNoSdkMarkerAnywhere_IsLegacyStyle()
        {
            var csproj = LegacyProject("Old/Old.csproj", "<PropertyGroup />");

            Assert.Equal(ProjectStyle.Legacy, TestProjects.DetermineProjectStyle(csproj));
        }

        /// <summary>
        /// The safety-critical third answer. Legacy is recognised by the ABSENCE of an SDK marker, so a
        /// file we could not read looks exactly like a legacy project to any rule spelled "no Sdk
        /// attribute". Answering Legacy there would send an unreadable project to vstest.console against
        /// an output assembly nobody established exists — so the style is null and the project is skipped,
        /// which is what every other reader here does with a file it cannot parse.
        /// </summary>
        [Fact]
        public void AnUnparseableProject_HasNoStyleAndIsNotAssumedLegacy()
        {
            var csproj = Write("Broken/Broken.csproj", "<Project><PropertyGroup></Project>");

            Assert.Null(TestProjects.DetermineProjectStyle(csproj));
            Assert.Null(Single(csproj));
        }

        // --- discovery ------------------------------------------------------------------------------

        [Fact]
        public void LegacyProjectWithTheMsTestProjectTypeGuid_IsALegacyTestProject()
        {
            var csproj = LegacyProject("Old/Old.csproj", MsTestProjectTypeGuids);

            Assert.Equal(TestProjectKind.LegacyVsTest, Single(csproj)!.Kind);
        }

        [Fact]
        public void LegacyProjectReferencingTheMsTestAssembly_IsALegacyTestProject()
        {
            var csproj = LegacyProject("Old/Old.csproj", QualityToolsReference);

            Assert.Equal(TestProjectKind.LegacyVsTest, Single(csproj)!.Kind);
        }

        /// <summary>
        /// A pre-PackageReference project records its test framework in <c>packages.config</c>, so without
        /// reading that file a legacy project using a NuGet-delivered framework has no signal at all.
        /// </summary>
        [Fact]
        public void LegacyProjectWhosePackagesConfigNamesATestFramework_IsALegacyTestProject()
        {
            var csproj = LegacyProject("Old/Old.csproj", "<PropertyGroup />");
            Write("Old/packages.config",
                "<packages><package id=\"NUnit\" version=\"3.13.3\" targetFramework=\"net48\" /></packages>");

            Assert.Equal(TestProjectKind.LegacyVsTest, Single(csproj)!.Kind);
        }

        /// <summary>
        /// The reference-name match is on the SIMPLE name, taken from before the first comma. An assembly
        /// display name carries a version and a public key token after it, and matching the whole string
        /// would let either of those decide the answer.
        /// </summary>
        [Fact]
        public void ReferenceMatching_UsesTheSimpleAssemblyNameNotTheDisplayName()
        {
            var matches = LegacyProject("A/A.csproj",
                "<ItemGroup><Reference Include=\"nunit.framework, Version=3.13.3.0\" /></ItemGroup>");
            var doesNot = LegacyProject("B/B.csproj",
                "<ItemGroup><Reference Include=\"Acme.Data, Version=1.0.0.0, PublicKeyToken=xunitlike\" /></ItemGroup>");

            Assert.Equal(TestProjectKind.LegacyVsTest, Single(matches)!.Kind);
            Assert.Null(Single(doesNot));
        }

        [Fact]
        public void ALegacyProjectThatIsNotATestProject_IsNotClassified()
        {
            var csproj = LegacyProject("App/App.csproj",
                "<ItemGroup><Reference Include=\"System.Web\" /></ItemGroup>");

            Assert.Null(Single(csproj));
        }

        /// <summary>
        /// The case the whole STYLE-not-FRAMEWORK rule exists for, and the one a framework-first reading
        /// gets wrong while looking right. This project carries a `Microsoft.NET.Test.Sdk`
        /// PackageReference — the exact signal that means Classic on an SDK-style project — but it is not
        /// SDK-style, so `dotnet test` cannot run it. Classified by its framework it becomes Classic and
        /// is handed to a runner that fails on it; classified by its style it goes to vstest.console,
        /// which can.
        /// </summary>
        [Fact]
        public void NonSdkProjectUsingPackageReference_IsLegacyNotClassic()
        {
            var csproj = LegacyProject("Hybrid/Hybrid.csproj",
                "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.13.0\" /></ItemGroup>");

            var classified = Single(csproj);

            Assert.Equal(TestProjectKind.LegacyVsTest, classified!.Kind);
            Assert.Equal(ProjectStyle.Legacy, classified.Style);
        }

        /// <summary>
        /// The same rule from the other side: the MTP runner property does NOT make a legacy project MTP.
        /// `dotnet run` is even less able to run a non-SDK project than `dotnet test` is, and this is the
        /// direction that hangs rather than fails.
        /// </summary>
        [Fact]
        public void MtpRunnerPropertyOnALegacyProject_DoesNotMakeItMtp()
        {
            var csproj = LegacyProject("Old/Old.csproj",
                "<PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>"
                + QualityToolsReference);

            Assert.Equal(TestProjectKind.LegacyVsTest, Single(csproj)!.Kind);
        }

        /// <summary>
        /// Old-style projects predate `Directory.Build.props` and it is the SDK that imports it, so the
        /// legacy signals are read from the project file alone. Walking up would let one props file in an
        /// ancestor directory turn every legacy project in the tree into a test project — and on a repo
        /// that mixes old and new projects, that ancestor is the solution root.
        /// </summary>
        [Fact]
        public void LegacySignalsAreNotInheritedFromDirectoryBuildProps()
        {
            Write("Directory.Build.props",
                "<Project>" + MsTestProjectTypeGuids + QualityToolsReference + "</Project>");
            var csproj = LegacyProject("App/App.csproj", "<PropertyGroup />");

            Assert.Null(Single(csproj));
        }

        [Fact]
        public void ALegacyTestProject_TakesTheVsTestFilterDialect()
        {
            var csproj = LegacyProject("Old/Old.csproj", MsTestProjectTypeGuids);

            Assert.Equal(FilterDialect.VsTest, Single(csproj)!.Dialect);
        }

        // --- the command line -----------------------------------------------------------------------

        /// <summary>
        /// Measured 2026-09-04 against VSTest 18.9.0: handed a `.csproj`, vstest.console prints "a total
        /// of 1 test files matched", exits 0, and writes a TRX whose counters are all zero with
        /// outcome="Completed". Every reader downstream of it is content with that, so the run would land
        /// as a GREEN CARD for a project that ran nothing. Hence the argument is the built assembly, and
        /// hence this check.
        /// </summary>
        [Fact]
        public void LegacyCommand_RunsTheBuiltAssemblyAndNeverTheProjectFile()
        {
            var args = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old\bin\Debug\Old.Tests.dll", @"C:\results", filterExpression: null, runSettingsPath: null);

            Assert.Contains("\"C:\\repo\\Old\\bin\\Debug\\Old.Tests.dll\"", args);
            Assert.DoesNotContain(".csproj", args);
        }

        [Fact]
        public void LegacyCommand_AsksForATrxInTheResultsDirectory()
        {
            var args = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old.Tests.dll", @"C:\results\abc", filterExpression: null, runSettingsPath: null);

            Assert.Contains("/logger:trx", args);
            Assert.Contains("/ResultsDirectory:\"C:\\results\\abc\"", args);
        }

        /// <summary>
        /// vstest.console infers both from the assembly itself — the PE header and
        /// <c>TargetFrameworkAttribute</c>. A value we supplied would override that inference with a
        /// guess, and getting it wrong is not an error: it runs, against the wrong runtime or bitness, and
        /// reports normally. There is nothing knowable here that the assembly does not state about itself.
        /// </summary>
        [Fact]
        public void LegacyCommand_NeverPassesPlatformOrFramework()
        {
            var args = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old.Tests.dll", @"C:\results", "FullyQualifiedName=N.C.M", @"C:\repo\a.runsettings");

            Assert.DoesNotContain("/Platform", args);
            Assert.DoesNotContain("/Framework", args);
        }

        [Fact]
        public void LegacyCommand_AppliesASolutionRunSettingsTheWayTheClassicRunnerDoes()
        {
            var withSettings = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old.Tests.dll", @"C:\results", null, @"C:\repo\a.runsettings");
            var without = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old.Tests.dll", @"C:\results", null, null);

            Assert.Contains("/Settings:\"C:\\repo\\a.runsettings\"", withSettings);
            Assert.DoesNotContain("/Settings:", without);
        }

        /// <summary>
        /// One expression, two spellings. `dotnet test` takes `--filter "expr"` and vstest.console
        /// `/TestCaseFilter:"expr"`, and the expression between the quotes must be byte-identical — it is
        /// the same VSTest syntax. Asserted by comparing the two builders' output rather than by
        /// restating the expected string, which would pass just as happily against two drifted copies.
        /// </summary>
        [Theory]
        [InlineData(StructuredFilterKind.Method, "N.C.M")]
        [InlineData(StructuredFilterKind.Class, "N.C")]
        [InlineData(StructuredFilterKind.Namespace, "N")]
        public void LegacyAndClassicFilters_AreTheSameExpression(StructuredFilterKind kind, string value)
        {
            var spec = new FilterSpec { Kind = kind, Value = value };

            var legacy = TestRunCommands.BuildLegacyFilterExpression(spec);
            var classic = TestRunCommands.BuildFilterFragment(spec, FilterDialect.VsTest);

            Assert.NotNull(legacy);
            Assert.Equal($"--filter \"{legacy}\"", classic);
        }

        [Fact]
        public void LegacyRawFilter_IsTheExpressionItself()
        {
            var spec = new FilterSpec { Raw = "TestCategory=Smoke" };

            Assert.Equal("TestCategory=Smoke", TestRunCommands.BuildLegacyFilterExpression(spec));
        }

        /// <summary>
        /// The expression is glued to <c>/TestCaseFilter:</c> as ONE argument however it is spelled
        /// (pre-release security review, September 2026): a quote in it used to close the hand-written quotes and start a new
        /// argument - a runner option of the agent's choosing.
        /// </summary>
        [Fact]
        public void LegacyCommand_KeepsTheFilterAsOneArgumentWhateverItContains()
        {
            const string payload = "x\" /TestAdapterPath:\\\\attacker\\share \"";

            var args = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old.Tests.dll", @"C:\results", payload, null);
            var argv = CommandLineArgumentTests.Split("vstest.console.exe " + args);

            Assert.Equal(4 + 1, argv.Length); // exe, assembly, /logger, /ResultsDirectory, ONE filter
            Assert.Equal("/TestCaseFilter:" + payload, argv[4]);
        }

        [Fact]
        public void LegacyCommand_OmitsTheFilterWhenThereIsNone()
        {
            Assert.Null(TestRunCommands.BuildLegacyFilterExpression(new FilterSpec()));

            var args = TestRunCommands.BuildLegacyVsTestArguments(
                @"C:\repo\Old.Tests.dll", @"C:\results", null, null);

            Assert.DoesNotContain("/TestCaseFilter", args);
        }

        /// <summary>
        /// Both of <see cref="TestRunCommands.BuildTestCommand"/>'s branches emit `dotnet`, and neither
        /// `dotnet test` nor `dotnet run` can run a non-SDK project — so a legacy project reaching it
        /// would be handed a command line that cannot work. It throws rather than falling through to the
        /// classic branch, which would produce a plausible-looking command and a failure the user would
        /// read as the project's own.
        /// </summary>
        [Fact]
        public void BuildTestCommand_RefusesALegacyProjectRatherThanEmittingDotnet()
        {
            Assert.Throws<ArgumentException>(() => TestRunCommands.BuildTestCommand(
                TestProjectKind.LegacyVsTest, @"C:\repo\Old\Old.csproj", "Debug", @"C:\results", null, null));
        }
    }
}
