using System;
using System.IO;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The filter dialect resolved per test project, its translation into that framework's own flags, and
    /// the <c>dotnet</c> command line each project kind runs
    /// (<see cref="TestProjects.DetermineFilterDialect"/>, <see cref="TestRunCommands"/>).
    /// <para>
    /// These strings are what actually reach the shell, and the failure they guard is quiet: a filter flag
    /// a runner does not recognise usually does not error, it runs something other than what was asked
    /// for, and the results card looks ordinary either way. The dialects genuinely differ — VSTest takes a
    /// <c>FullyQualifiedName</c> expression, xUnit v3 takes <c>--filter-*</c> flags — so a translation
    /// applied to the wrong one silently widens or empties the run.
    /// </para>
    /// </summary>
    public sealed class TestRunFilterTests : IDisposable
    {
        // Dev-only scratch, so temp is fine here (the never-use-temp rule is about agent-facing paths).
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-testfilter-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private string Project(string relativePath, string inner)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">{inner}</Project>");
            return path;
        }

        private static string PackageRef(string id) =>
            $"<ItemGroup><PackageReference Include=\"{id}\" Version=\"1.0.0\" /></ItemGroup>";

        private static FilterSpec Structured(StructuredFilterKind kind, string value) =>
            new FilterSpec { Kind = kind, Value = value };

        /// <summary>A classified project standing in for one whose dialect is already resolved.</summary>
        private static TestProject Classified(FilterDialect dialect) =>
            new TestProject("X.csproj", TestProjectKind.Classic, dialect);

        // --- dialect resolution ---------------------------------------------------------------------

        [Fact]
        public void ClassicProject_UsesTheVsTestDialect()
        {
            var csproj = Project("T/T.csproj", PackageRef("xunit.v3"));

            // Classic wins outright: the project's packages are not consulted at all.
            Assert.Equal(FilterDialect.VsTest,
                TestProjects.DetermineFilterDialect(csproj, TestProjectKind.Classic));
        }

        [Fact]
        public void MtpXunitV3Project_UsesTheXunitV3Dialect()
        {
            var csproj = Project("T/T.csproj", PackageRef("xunit.v3"));

            Assert.Equal(FilterDialect.XunitV3,
                TestProjects.DetermineFilterDialect(csproj, TestProjectKind.Mtp));
        }

        [Theory]
        [InlineData("MSTest.TestAdapter")]
        [InlineData("NUnit")]
        public void MtpMsTestOrNUnitProject_UsesTheVsTestDialect(string packageId)
        {
            var csproj = Project("T/T.csproj", PackageRef(packageId));

            Assert.Equal(FilterDialect.VsTest,
                TestProjects.DetermineFilterDialect(csproj, TestProjectKind.Mtp));
        }

        /// <summary>
        /// An unrecognised MTP framework (e.g. TUnit) is <see cref="FilterDialect.Unknown"/>, which is what
        /// makes the structured-filter preflight fail loud instead of running the whole suite unfiltered.
        /// </summary>
        [Fact]
        public void UnrecognisedMtpFramework_IsUnknownDialect()
        {
            var csproj = Project("T/T.csproj", PackageRef("TUnit"));

            Assert.Equal(FilterDialect.Unknown,
                TestProjects.DetermineFilterDialect(csproj, TestProjectKind.Mtp));
        }

        // --- translation ----------------------------------------------------------------------------

        /// <summary>
        /// The raw escape hatch is an EXPRESSION and the option is ours, per dialect (pre-release
        /// security review): it used to be a verbatim option string, i.e. a piece of command line the agent wrote.
        /// </summary>
        [Theory]
        [InlineData(FilterDialect.VsTest, "--filter \"TestCategory=Fast\"")]
        [InlineData(FilterDialect.XunitV3, "--filter-query \"TestCategory=Fast\"")]
        [InlineData(FilterDialect.Unknown, null)]
        public void RawFilter_IsTheExpressionAndTakesTheDialectsOwnOption(FilterDialect dialect, string? expected)
        {
            var spec = new FilterSpec { Raw = "TestCategory=Fast" };

            Assert.Equal(expected, TestRunCommands.BuildFilterFragment(spec, dialect));
        }

        /// <summary>
        /// The finding's own payloads: each is one argument now, and cannot become an option.
        /// <para>Defence in depth, deliberately kept: a value carrying a quote is refused before it ever
        /// reaches here (<see cref="AQuoteIsRefused_BecauseTheValueMeetsASecondParser"/>), but the
        /// quoting is what makes the argv layer safe for every value that ISN'T refused — a space, a
        /// backslash, a <c>&amp;</c>. Each layer has to hold on its own.</para>
        /// </summary>
        [Theory]
        [InlineData(FilterDialect.VsTest)]
        [InlineData(FilterDialect.XunitV3)]
        public void AQuoteInAFilterValueCannotBreakOutIntoAnotherArgument(FilterDialect dialect)
        {
            const string payload = "x\" --test-adapter-path \\\\attacker\\share \"";

            var structured = TestRunCommands.BuildFilterFragment(
                Structured(StructuredFilterKind.Method, payload), dialect)!;
            var raw = TestRunCommands.BuildFilterFragment(new FilterSpec { Raw = payload }, dialect)!;

            // One option, then exactly one argument: the parser reads the quoted value back as a single
            // element, quote and all (CommandLineArgumentTests proves the round trip).
            foreach (var fragment in new[] { structured, raw })
            {
                var option = fragment.Substring(0, fragment.IndexOf(' '));
                var argv = CommandLineArgumentTests.Split("runner.exe " + fragment);
                Assert.Equal(new[] { "runner.exe", option, argv[2] }, argv);
                Assert.Contains(payload, argv[2]);
            }
        }

        /// <summary>
        /// VSTest's documented escapes are backslashes, and the only way to filter a parameterised test
        /// by its full name. They pass through untouched - a blanket backslash ban would refuse them.
        /// </summary>
        [Fact]
        public void VsTestBackslashEscapesSurviveTheRawFilter()
        {
            const string expr = @"FullyQualifiedName=Ns.C.M\(1\)";

            var argv = CommandLineArgumentTests.Split(
                "dotnet " + TestRunCommands.BuildFilterFragment(new FilterSpec { Raw = expr }, FilterDialect.VsTest));

            Assert.Equal(new[] { "dotnet", "--filter", expr }, argv);
        }

        // --- the refusal: what quoting cannot make safe --------------------------------------------

        [Theory]
        [InlineData("--test-adapter-path", FilterDialect.VsTest)]
        [InlineData("--test-adapter-path", FilterDialect.XunitV3)]
        [InlineData("-p:CustomBeforeMicrosoftCommonTargets=evil.targets", FilterDialect.VsTest)]
        [InlineData("/TestAdapterPath:\\\\attacker\\share", FilterDialect.VsTest)]
        [InlineData("  --filter TestCategory=Fast", FilterDialect.VsTest)]       // leading whitespace is not a disguise
        [InlineData("--filter \"TestCategory=Fast\"", FilterDialect.VsTest)]    // the OLD raw shape, refused with the contract
        [InlineData("--filter-query /Ns", FilterDialect.XunitV3)]
        public void ARawValueThatIsAnOptionIsRefused(string raw, FilterDialect dialect)
        {
            var reason = TestRunCommands.RefuseFilterValue(new FilterSpec { Raw = raw }, dialect);

            Assert.NotNull(reason);
            Assert.Contains("bare filter EXPRESSION", reason);
            Assert.Null(TestRunCommands.RefuseFilterValue(new FilterSpec(), dialect)); // no filter, no refusal
        }

        /// <summary>An xUnit v3 query begins with '/' by its own grammar; that dialect keeps the slash.</summary>
        [Theory]
        [InlineData("/Ns/Class/Method", FilterDialect.XunitV3, null)]
        [InlineData("/Ns/*/*[Category=Fast]", FilterDialect.XunitV3, null)]
        [InlineData("/Ns/Class/Method", FilterDialect.VsTest, "bare filter EXPRESSION")]
        [InlineData("FullyQualifiedName~Ns&TestCategory=Fast", FilterDialect.VsTest, null)]
        [InlineData(@"FullyQualifiedName=Ns.C.M\(1\)", FilterDialect.VsTest, null)]
        public void ALeadingSlashIsAQueryForXunitAndAnOptionForVsTest(string raw, FilterDialect dialect, string? refusedWith)
        {
            var reason = TestRunCommands.RefuseFilterValue(new FilterSpec { Raw = raw }, dialect);

            if (refusedWith is null) Assert.Null(reason);
            else Assert.Contains(refusedWith, reason);
        }

        [Theory]
        [InlineData(StructuredFilterKind.Method, "--test-adapter-path")]
        [InlineData(StructuredFilterKind.Class, "/p:Evil=1")]
        [InlineData(StructuredFilterKind.Namespace, "-x")]
        public void AStructuredValueThatIsAnOptionIsRefused(StructuredFilterKind kind, string value)
        {
            var reason = TestRunCommands.RefuseFilterValue(Structured(kind, value), FilterDialect.XunitV3);

            Assert.NotNull(reason);
            Assert.Contains("cannot begin with", reason);
        }

        [Theory]
        [InlineData("Ns.C.M\u0000 --evil")]
        [InlineData("Ns.C.M\n--evil")]
        public void AControlCharacterIsRefused(string value)
        {
            Assert.Contains("control character",
                TestRunCommands.RefuseFilterValue(new FilterSpec { Raw = value }, FilterDialect.VsTest));
            Assert.Contains("control character",
                TestRunCommands.RefuseFilterValue(Structured(StructuredFilterKind.Method, value), FilterDialect.VsTest));
        }

        /// <summary>
        /// <b>A quote is refused because <c>Quote</c> only reaches the FIRST parser.</b> Both payloads
        /// below were run against the real runner in Visual Studio while exercising this finding (2026-09-09) and neither broke
        /// out at the argv layer — but <c>dotnet test</c> hands <c>--filter</c> to MSBuild as a property,
        /// and there the embedded quote ends the property early. The first payload failed loudly
        /// (<c>MSB4177: Invalid property</c>, its injected half holding a space); the second ran the same
        /// 15 tests as the clean filter and set <c>CwktProbeProperty</c> in silence. The silent one is
        /// the shape that matters: <c>CustomBeforeMicrosoftCommonTargets</c> is a property too, and it
        /// runs an attacker's targets file.
        /// </summary>
        [Theory]
        [InlineData("x\" --test-adapter-path \\\\attacker\\share \"")]
        [InlineData("FullyQualifiedName~CommandLineArgumentTests\"CwktProbeProperty=1\"")]
        [InlineData("FullyQualifiedName~X\"CustomBeforeMicrosoftCommonTargets=C:\\evil.targets\"")]
        public void AQuoteIsRefused_BecauseTheValueMeetsASecondParser(string raw)
        {
            var reason = TestRunCommands.RefuseFilterValue(new FilterSpec { Raw = raw }, FilterDialect.VsTest);

            Assert.Contains("double quote", reason);
        }

        /// <summary>The structured values reach the same MSBuild property, so they take the same rule.</summary>
        [Fact]
        public void AQuoteInAStructuredValueIsRefusedToo()
        {
            var reason = TestRunCommands.RefuseFilterValue(
                Structured(StructuredFilterKind.Method, "Ns.C.M\"Prop=1\""), FilterDialect.XunitV3);

            Assert.Contains("double quote", reason);
        }

        /// <summary>
        /// The blanket quote ban is affordable only because no dialect's grammar uses one — the backslash
        /// ban tried first was not, and this pins the difference: VSTest's escapes still pass.
        /// </summary>
        [Theory]
        [InlineData(@"FullyQualifiedName=Ns.C.M\(1\)", FilterDialect.VsTest)]
        [InlineData(@"FullyQualifiedName~Ns.C&TestCategory=Fast", FilterDialect.VsTest)]
        [InlineData("/Ns/Class/Method[Category=Fast]", FilterDialect.XunitV3)]
        public void TheGrammarsThemselvesNeedNoQuote(string raw, FilterDialect dialect)
        {
            Assert.Null(TestRunCommands.RefuseFilterValue(new FilterSpec { Raw = raw }, dialect));
        }

        [Fact]
        public void AnOrdinaryStructuredValueIsNotRefused()
        {
            Assert.Null(TestRunCommands.RefuseFilterValue(Structured(StructuredFilterKind.Method, "Ns.C.M"), FilterDialect.VsTest));
            Assert.Null(TestRunCommands.RefuseFilterValue(Structured(StructuredFilterKind.Class, "Ns.C*"), FilterDialect.XunitV3));
        }

        [Fact]
        public void VsTestMethodFilter_IsAnExactMatch()
        {
            var fragment = TestRunCommands.BuildFilterFragment(
                Structured(StructuredFilterKind.Method, "Ns.C.M"), FilterDialect.VsTest);

            Assert.Equal("--filter \"FullyQualifiedName=Ns.C.M\"", fragment);
        }

        /// <summary>
        /// The trailing dot anchors the contains-match to a member of that class/namespace. Dropping it
        /// widens <c>filterClass Ns.Foo</c> onto <c>Ns.FooBar</c>'s tests — a filtered run that quietly
        /// executes more than it says.
        /// </summary>
        [Theory]
        [InlineData(StructuredFilterKind.Class)]
        [InlineData(StructuredFilterKind.Namespace)]
        public void VsTestClassAndNamespaceFilters_AreContainsMatchesAnchoredWithATrailingDot(
            StructuredFilterKind kind)
        {
            var fragment = TestRunCommands.BuildFilterFragment(Structured(kind, "Ns.Foo"), FilterDialect.VsTest);

            Assert.Equal("--filter \"FullyQualifiedName~Ns.Foo.\"", fragment);
        }

        [Theory]
        [InlineData(StructuredFilterKind.Method, "--filter-method \"V\"")]
        [InlineData(StructuredFilterKind.Class, "--filter-class \"V\"")]
        [InlineData(StructuredFilterKind.Namespace, "--filter-namespace \"V\"")]
        public void XunitV3Filters_UseTheirOwnFlags(StructuredFilterKind kind, string expected)
        {
            Assert.Equal(expected, TestRunCommands.BuildFilterFragment(Structured(kind, "V"), FilterDialect.XunitV3));
        }

        /// <summary>Never a fragment that looks filtered but isn't — the preflight rejects this case first.</summary>
        [Fact]
        public void UnknownDialect_ProducesNoFragment()
        {
            Assert.Null(TestRunCommands.BuildFilterFragment(
                Structured(StructuredFilterKind.Class, "Ns.C"), FilterDialect.Unknown));
        }

        [Fact]
        public void EmptySpec_ProducesNoFragment()
        {
            Assert.Null(TestRunCommands.BuildFilterFragment(new FilterSpec(), FilterDialect.VsTest));
        }

        // --- the zero-match hint --------------------------------------------------------------------

        [Fact]
        public void ZeroMatchHint_NamesTheStructuredArgumentThatMatchedNothing()
        {
            var hint = TestRunCommands.ZeroMatchFilterHint(
                Structured(StructuredFilterKind.Class, "Ns.C"), new[] { Classified(FilterDialect.VsTest) });

            Assert.Contains("filterClass", hint);
            Assert.Contains("Ns.C", hint);
        }

        [Fact]
        public void ZeroMatchHint_ExplainsExactMatchingWhenAnXunitV3ProjectIsInPlay()
        {
            var hint = TestRunCommands.ZeroMatchFilterHint(
                Structured(StructuredFilterKind.Namespace, "Ns"), new[] { Classified(FilterDialect.XunitV3) });

            Assert.Contains("EXACTLY", hint);
            Assert.Contains("'Ns*'", hint);
        }

        /// <summary>
        /// filterMethod is exact in BOTH dialects, so the xUnit v3 widening advice would be wrong for it.
        /// </summary>
        [Fact]
        public void ZeroMatchHint_DoesNotOfferTheWildcardForAMethodFilter()
        {
            var hint = TestRunCommands.ZeroMatchFilterHint(
                Structured(StructuredFilterKind.Method, "Ns.C.M"), new[] { Classified(FilterDialect.XunitV3) });

            Assert.DoesNotContain("EXACTLY", hint);
        }

        // --- the command line -----------------------------------------------------------------------

        [Fact]
        public void BuildTestCommand_ClassicUsesDotnetTestWithTheTrxLogger()
        {
            var args = TestRunCommands.BuildTestCommand(
                TestProjectKind.Classic, @"C:\s\T.csproj", "Debug", @"C:\out",
                filterFragment: null, runSettingsPath: null);

            Assert.StartsWith("test \"C:\\s\\T.csproj\"", args);
            Assert.Contains("--no-build", args);
            Assert.Contains("-c \"Debug\"", args);
            Assert.Contains("--logger \"trx;LogFileName=results.trx\"", args);
            Assert.Contains("--results-directory \"C:\\out\"", args);
            Assert.DoesNotContain("--settings", args);
        }

        [Fact]
        public void BuildTestCommand_ClassicAppliesRunSettingsWhenGiven()
        {
            var args = TestRunCommands.BuildTestCommand(
                TestProjectKind.Classic, @"C:\s\T.csproj", "Debug", @"C:\out",
                filterFragment: null, runSettingsPath: @"C:\s\a.runsettings");

            Assert.Contains("--settings \"C:\\s\\a.runsettings\"", args);
        }

        /// <summary>
        /// MTP options belong AFTER the <c>--</c> separator: before it they are <c>dotnet run</c>'s own
        /// arguments and never reach the test host. MTP also takes no runsettings, so --settings must not
        /// appear even when the solution has one.
        /// </summary>
        [Fact]
        public void BuildTestCommand_MtpPassesHostOptionsAfterTheSeparatorAndNeverRunSettings()
        {
            var args = TestRunCommands.BuildTestCommand(
                TestProjectKind.Mtp, @"C:\s\T.csproj", "Debug", @"C:\out",
                filterFragment: "--filter-method \"M\"", runSettingsPath: @"C:\s\a.runsettings");

            Assert.StartsWith("run --project \"C:\\s\\T.csproj\"", args);
            Assert.DoesNotContain("--settings", args);
            var separator = args.IndexOf(" -- ", StringComparison.Ordinal);
            Assert.True(separator > 0, "the -- separator is missing: " + args);
            Assert.Contains("--report-trx", args.Substring(separator));
            Assert.Contains("--filter-method \"M\"", args.Substring(separator));
        }

        [Theory]
        [InlineData(TestProjectKind.Classic)]
        [InlineData(TestProjectKind.Mtp)]
        public void BuildTestCommand_OmitsTheFilterWhenThereIsNone(TestProjectKind kind)
        {
            var args = TestRunCommands.BuildTestCommand(
                kind, @"C:\s\T.csproj", "Debug", @"C:\out", filterFragment: null, runSettingsPath: null);

            Assert.DoesNotContain("--filter", args);
        }
    }
}
