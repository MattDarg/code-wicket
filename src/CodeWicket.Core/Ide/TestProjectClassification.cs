using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>How one test project is run — which decides the command line built for it.</summary>
    public enum TestProjectKind
    {
        /// <summary>Opts into the Microsoft Testing Platform runner; runs the test host directly via <c>dotnet run</c>.</summary>
        Mtp,

        /// <summary>Classic vstest (references Microsoft.NET.Test.Sdk); runs via <c>dotnet test</c>.</summary>
        Classic,

        /// <summary>
        /// An old-style (non-SDK) .NET Framework test project. Neither <c>dotnet test</c> nor
        /// <c>dotnet run</c> can run one, so it goes to <c>vstest.console.exe</c> from this Visual
        /// Studio install — what Test Explorer itself uses — against the project's BUILT OUTPUT
        /// ASSEMBLY, never the project file.
        /// </summary>
        LegacyVsTest,
    }

    /// <summary>
    /// Which MSBuild dialect a project file is written in. <b>This, and not which test framework is
    /// referenced, is what decides the runner</b> — an old-style project migrated to
    /// <c>PackageReference</c> still answers every modern signal and still cannot be run by
    /// <c>dotnet test</c>.
    /// </summary>
    public enum ProjectStyle
    {
        /// <summary>SDK-style: a <c>Sdk</c> attribute, a <c>&lt;Sdk&gt;</c> element, or an SDK <c>Import</c>.</summary>
        Sdk,

        /// <summary>Old-style: none of those three anywhere in a file we could parse.</summary>
        Legacy,
    }

    /// <summary>The filter syntax one project's test framework understands.</summary>
    public enum FilterDialect
    {
        /// <summary>A VSTest <c>FullyQualifiedName</c> expression.</summary>
        VsTest,

        /// <summary>xUnit v3's own <c>--filter-method</c>/<c>-class</c>/<c>-namespace</c> flags.</summary>
        XunitV3,

        /// <summary>Not recognised (e.g. TUnit) — a structured filter must fail loud rather than run unfiltered.</summary>
        Unknown,
    }

    /// <summary>One classified test project: where it is, how to run it, and how to filter it.</summary>
    public sealed class TestProject
    {
        public TestProject(
            string projectFile, TestProjectKind kind, FilterDialect dialect,
            ProjectStyle style = ProjectStyle.Sdk)
        {
            ProjectFile = projectFile;
            Kind = kind;
            Dialect = dialect;
            Style = style;
        }

        /// <summary>Full path to the project file (<c>.csproj</c>, <c>.vbproj</c>, …).</summary>
        public string ProjectFile { get; }

        public TestProjectKind Kind { get; }

        public FilterDialect Dialect { get; }

        /// <summary>The MSBuild dialect the project file is written in.</summary>
        public ProjectStyle Style { get; }
    }

    /// <summary>
    /// Which of a solution's projects are test projects, how each one runs, and which filter dialect it
    /// takes — decided by reading the project file (and the <c>Directory.Build.props</c> chain above it)
    /// as XML.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives in Core, away from the VS-bound project, for the reason <see cref="TableValue"/> and
    /// <see cref="FilePathMatch"/> do: <c>VsToolCatalog</c> is net472 + the VS SDK + Roslyn and no test
    /// project can reference it, so a rule left in there is a rule no test can reach. Enumerating the
    /// solution's projects is the VS-coupled half (DTE) and stays in <c>CodeWicket.Ide</c>; deciding
    /// what the resulting paths ARE is pure, and is here.
    /// </para>
    /// <para>
    /// The split earns its keep because the consequences of these rules are not symmetric. Failing to
    /// recognise a test project reports "no test projects found in the solution" — a solution's whole
    /// suite silently absent. Recognising one too eagerly is worse: an MTP project runs via
    /// <c>dotnet run</c>, so a misclassified web app would start a server that never exits and the tool
    /// call would hang to its timeout. Hence <see cref="IsMtpProject"/>'s two-part gate.
    /// </para>
    /// <para>
    /// Everything here reads XML off disk and is deliberately total: an unreadable or malformed project
    /// is "not a test project", never an exception. The caller runs the whole pass off the UI thread —
    /// on a large solution or a slow disk this is enough file I/O to freeze devenv.
    /// </para>
    /// </remarks>
    public static class TestProjects
    {
        /// <summary>
        /// Classifies project paths into test projects in ONE pass per project (no DTE — safe off the UI
        /// thread): MTP (opts into the Microsoft Testing Platform runner — runs via <c>dotnet run</c>)
        /// beats classic vstest (references Microsoft.NET.Test.Sdk — runs via <c>dotnet test</c>); anything
        /// else isn't a test project. The filter dialect is resolved here too, so the project/props XML
        /// isn't re-walked later by the preflight and the run loop.
        /// </summary>
        public static List<TestProject> Classify(IReadOnlyList<string> projectPaths)
        {
            var result = new List<TestProject>();
            foreach (var path in projectPaths)
            {
                if (!File.Exists(path))
                    continue;

                // STYLE FIRST, and this ordering is the whole rule. A non-SDK project that was migrated
                // to PackageReference answers `Microsoft.NET.Test.Sdk` exactly like an SDK-style one, so
                // asking which framework is referenced first classifies it Classic and hands it to
                // `dotnet test`, which cannot run it. Asking about the dialect first cannot make that
                // mistake, because the dialect is the thing the runner actually depends on.
                var style = DetermineProjectStyle(path);
                if (style is null)
                    continue; // unparseable: skipped, and never ASSUMED legacy - see DetermineProjectStyle

                TestProjectKind kind;
                if (style == ProjectStyle.Legacy)
                {
                    if (!LooksLikeLegacyTestProject(path))
                        continue;
                    kind = TestProjectKind.LegacyVsTest;
                }
                else if (IsMtpProject(path))
                    kind = TestProjectKind.Mtp;
                else if (DeclaresTestSdk(path))
                {
                    // A test SDK brings the framework, the adapter and the runner in with it, so a project
                    // using one carries NONE of the signals the other branches read - the PackageReference
                    // they look for is injected by the SDK's own targets and never appears in the project
                    // file. Without this branch such a project is silently not a test project, which is
                    // the reported bug wearing different clothes.
                    kind = UsesVsTestRunner(path) ? TestProjectKind.Classic : TestProjectKind.Mtp;
                }
                else if (MatchesUpTree(path, p => HasPackageRefPrefix(p, "Microsoft.NET.Test.Sdk")))
                    kind = TestProjectKind.Classic;
                else
                    continue;

                result.Add(new TestProject(path, kind, DetermineFilterDialect(path, kind), style.Value));
            }
            return result;
        }

        /// <summary>
        /// The filter dialect for a project: classic vstest always takes the VSTest <c>--filter</c> expression;
        /// an MTP project takes it only when it's a framework that exposes it (MSTest/NUnit via the VSTest
        /// bridge) — xUnit v3 uses its own <c>--filter-method/class/namespace</c> flags, and anything else
        /// (e.g. TUnit) is <see cref="FilterDialect.Unknown"/> (structured filters fail loud for it).
        /// </summary>
        internal static FilterDialect DetermineFilterDialect(string projectPath, TestProjectKind kind)
        {
            // vstest.console takes the same FullyQualifiedName expression as `dotnet test --filter`,
            // only spelled /TestCaseFilter: - so legacy sits with classic here.
            if (kind == TestProjectKind.Classic || kind == TestProjectKind.LegacyVsTest)
                return FilterDialect.VsTest;
            if (MatchesUpTree(projectPath, p => HasPackageRefPrefix(p, "xunit.v3")))
                return FilterDialect.XunitV3;
            if (MatchesUpTree(projectPath, p => HasPackageRefPrefix(p, "MSTest") || HasPackageRefPrefix(p, "NUnit")))
                return FilterDialect.VsTest;
            // Every test SDK we recognise is an MSTest one, which exposes the VSTest filter expression
            // through the bridge. Without this an MSTest.Sdk project reaches Unknown and a structured
            // filter fails loud against a framework that would have understood it perfectly well.
            if (DeclaresTestSdk(projectPath))
                return FilterDialect.VsTest;
            return FilterDialect.Unknown;
        }

        /// <summary>
        /// True when a project opts into the Microsoft Testing Platform runner AND looks like a test project.
        /// The runner property is resolved nearest-definition-wins (project file, then the imported
        /// <c>Directory.Build.props</c> chain — the property is commonly set repo-wide) so a project-level
        /// <c>false</c> overrides a props-level <c>true</c>, mirroring MSBuild precedence. The test-project gate matters because MTP
        /// projects run via <c>dotnet run</c>: misclassifying e.g. a web app would start a server that never
        /// exits. Conditioned property definitions are skipped for the same reason (we can't evaluate them,
        /// and guessing "true" risks running a non-test project).
        /// </summary>
        internal static bool IsMtpProject(string projectPath)
            => MtpRunnerEnabled(projectPath) && LooksLikeTestProject(projectPath);

        /// <summary>Resolves <c>UseMicrosoftTestingPlatformRunner</c> nearest-definition-wins up the tree.</summary>
        internal static bool MtpRunnerEnabled(string projectPath)
        {
            var enabled = false;
            MatchesUpTree(projectPath, path =>
            {
                var value = ReadUnconditionalProperty(path, "UseMicrosoftTestingPlatformRunner");
                if (value is null)
                    return false; // not defined in this file — keep walking up
                enabled = string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                return true; // nearest definition wins — stop walking
            });
            return enabled;
        }

        /// <summary>
        /// The last unconditional definition of <paramref name="propertyName"/> in the file, or null. A
        /// definition under a <c>Condition</c> (its own or an ancestor's) is ignored — we can't evaluate it.
        /// Last-wins within a file matches MSBuild's evaluation order.
        /// </summary>
        internal static string? ReadUnconditionalProperty(string projectOrPropsPath, string propertyName)
        {
            try
            {
                var doc = XDocument.Load(projectOrPropsPath);
                return doc.Descendants()
                    .Where(e => e.Name.LocalName == propertyName
                        && !e.AncestorsAndSelf().Any(a => a.Attribute("Condition") is not null))
                    .Select(e => e.Value)
                    .LastOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Package-id prefixes that mark a project as a test project (any test framework / test platform).</summary>
        internal static readonly string[] TestFrameworkPackagePrefixes =
            { "xunit", "MSTest", "NUnit", "TUnit", "Microsoft.Testing.", "Microsoft.NET.Test.Sdk" };

        /// <summary>
        /// True when the project (or a <c>Directory.Build.props</c> up the tree) declares itself a test
        /// project: <c>IsTestProject</c> true, or a PackageReference to a known test framework/platform.
        /// </summary>
        internal static bool LooksLikeTestProject(string projectPath)
            => MatchesUpTree(projectPath, LooksLikeTestProjectFile);

        private static bool LooksLikeTestProjectFile(string projectOrPropsPath)
        {
            try
            {
                var doc = XDocument.Load(projectOrPropsPath);
                if (doc.Descendants().Any(e => e.Name.LocalName == "IsTestProject"
                        && string.Equals((e.Value ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase)))
                    return true;
                return doc.Descendants().Any(e => e.Name.LocalName == "PackageReference"
                    && (string?)e.Attribute("Include") is { } id
                    && TestFrameworkPackagePrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }

        /// <summary>True when the project/props file has a <c>PackageReference</c> whose Include starts with <paramref name="idPrefix"/> (case-insensitive).</summary>
        internal static bool HasPackageRefPrefix(string projectOrPropsPath, string idPrefix)
        {
            try
            {
                var doc = XDocument.Load(projectOrPropsPath);
                return doc.Descendants().Any(e =>
                    e.Name.LocalName == "PackageReference"
                    && ((string?)e.Attribute("Include"))?.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) == true);
            }
            catch { return false; }
        }

        /// <summary>
        /// Project SDKs that make a project a test project on their own. <c>MSTest.Sdk</c> brings the
        /// framework, the adapter and a runner with it, so a project using it declares no test package of
        /// its own - the <c>Microsoft.NET.Test.Sdk</c> reference every other branch looks for is injected
        /// by the SDK's own targets and never appears in the project file.
        /// </summary>
        /// <remarks>
        /// Only SDKs that have been verified are listed. An SDK name guessed at would either do nothing or,
        /// worse, send an ordinary project to a test runner, and the eager direction is the one that hangs.
        /// </remarks>
        internal static readonly string[] TestProjectSdkNames = { "MSTest.Sdk" };

        /// <summary>True when the project declares one of <see cref="TestProjectSdkNames"/>.</summary>
        /// <remarks>
        /// No "looks like a test project" gate here, unlike <see cref="IsMtpProject"/>, and the asymmetry
        /// is deliberate: that gate exists because <c>UseMicrosoftTestingPlatformRunner</c> is commonly set
        /// repo-wide and so says little about any one project. Naming a test SDK is the opposite - it is
        /// per-project, and it is the strongest statement a project file can make about what it is.
        /// </remarks>
        internal static bool DeclaresTestSdk(string projectPath)
            => DeclaredSdkNames(projectPath).Any(name =>
                TestProjectSdkNames.Any(known => string.Equals(name, known, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// Every SDK name the project declares, from all three spellings, with any <c>/version</c> suffix
        /// removed (<c>Sdk="MSTest.Sdk/4.0.2"</c>) and each entry of a semicolon-separated list separated.
        /// </summary>
        internal static IEnumerable<string> DeclaredSdkNames(string projectPath)
        {
            XDocument doc;
            try { doc = XDocument.Load(projectPath); }
            catch { yield break; }

            var declarations = new List<string>();
            var rootSdk = (string?)doc.Root?.Attribute("Sdk");
            if (rootSdk != null)
                declarations.Add(rootSdk);
            foreach (var element in doc.Descendants())
            {
                if (element.Name.LocalName == "Sdk")
                {
                    var named = (string?)element.Attribute("Name");
                    if (named != null)
                        declarations.Add(named);
                }
                else if (element.Name.LocalName == "Import")
                {
                    var imported = (string?)element.Attribute("Sdk");
                    if (imported != null)
                        declarations.Add(imported);
                }
            }

            foreach (var declaration in declarations)
                foreach (var entry in declaration.Split(';'))
                {
                    var name = entry.Split('/')[0].Trim();
                    if (name.Length > 0)
                        yield return name;
                }
        }

        /// <summary>
        /// True when a test-SDK project opts into the VSTest runner (<c>dotnet test</c>) rather than the
        /// Microsoft Testing Platform one (<c>dotnet run</c>).
        /// </summary>
        /// <remarks>
        /// Read from <c>MSTest.Sdk</c>'s own targets rather than from documentation: <c>Sdk.props</c>
        /// defaults <c>UseVSTest</c> to <b>false</b>, and <c>Sdk.targets</c> imports <c>Runner.targets</c>
        /// when it is false and <c>VSTest.targets</c> when it is true - the latter being what adds the
        /// implicit <c>Microsoft.NET.Test.Sdk</c> reference. So the default is MTP and this property is the
        /// only thing that moves it, which is why the absence of a definition means MTP rather than nothing.
        /// </remarks>
        internal static bool UsesVsTestRunner(string projectPath)
        {
            var enabled = false;
            MatchesUpTree(projectPath, path =>
            {
                var value = ReadUnconditionalProperty(path, "UseVSTest");
                if (value is null)
                    return false; // not defined here - keep walking up
                enabled = string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                return true; // nearest definition wins, as everywhere else here
            });
            return enabled;
        }

        /// <summary>
        /// The MSBuild dialect <paramref name="projectPath"/> is written in, or <b>null when the file
        /// could not be parsed at all</b>.
        /// </summary>
        /// <remarks>
        /// Null is a third answer on purpose, and it is the safety-critical one. Legacy is recognised by
        /// the ABSENCE of an SDK marker, so a file we failed to read looks exactly like a legacy project
        /// to any rule spelled "no Sdk attribute". Returning Legacy there would send an unreadable project
        /// to vstest.console against an output assembly nobody has established exists. The caller skips a
        /// null, which is what every other reader here already does with a file it cannot parse.
        /// </remarks>
        public static ProjectStyle? DetermineProjectStyle(string projectPath)
        {
            try
            {
                var doc = XDocument.Load(projectPath);
                var root = doc.Root;
                if (root is null)
                    return null;

                // Three legal spellings, and a project needs only one of them to be SDK-style:
                //   <Project Sdk="Microsoft.NET.Sdk">        the common one
                //   <Project><Sdk Name="..." /></Project>    the element form
                //   <Import Sdk="Microsoft.NET.Sdk" ... />   the explicit-import form
                if (root.Attribute("Sdk") is not null)
                    return ProjectStyle.Sdk;
                if (root.Elements().Any(e => e.Name.LocalName == "Sdk"))
                    return ProjectStyle.Sdk;
                if (doc.Descendants().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null))
                    return ProjectStyle.Sdk;

                return ProjectStyle.Legacy;
            }
            catch { return null; }
        }

        /// <summary>The project type GUID Visual Studio has always stamped on an MSTest project.</summary>
        internal const string MsTestProjectTypeGuid = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";

        /// <summary>
        /// Assembly-name prefixes of the test frameworks a legacy project references directly rather than
        /// through a package. <c>Microsoft.VisualStudio.QualityTools.UnitTestFramework</c> is the MSTest
        /// assembly a Visual Studio unit-test project has always carried.
        /// </summary>
        internal static readonly string[] LegacyTestAssemblyPrefixes =
        {
            "Microsoft.VisualStudio.QualityTools.UnitTestFramework",
            "Microsoft.VisualStudio.TestPlatform.TestFramework",
            "xunit", "nunit.framework", "NUnit",
        };

        /// <summary>
        /// True when an old-style project looks like a test project. Four independent signals, because a
        /// legacy project may declare itself in any one of them and the cost of missing all four is a
        /// whole suite reported as "no test projects found": the MSTest <c>ProjectTypeGuids</c> stamp, a
        /// direct assembly <c>Reference</c> to a test framework, a <c>packages.config</c> naming one, and
        /// finally the modern signals - a migrated project still answers those.
        /// </summary>
        /// <remarks>
        /// Unlike the SDK-style readers this does NOT walk up to <c>Directory.Build.props</c>: old-style
        /// projects predate it and it is the SDK that imports it, so reading one here would let an
        /// unrelated props file in an ancestor directory make every legacy project in the tree a test
        /// project. <see cref="LooksLikeTestProject"/> does walk, which is correct for the signals it
        /// reads - they only exist in a world where the props file is imported.
        /// </remarks>
        internal static bool LooksLikeLegacyTestProject(string projectPath)
            => HasMsTestProjectTypeGuid(projectPath)
            || HasTestFrameworkAssemblyReference(projectPath)
            || PackagesConfigNamesATestFramework(projectPath)
            || LooksLikeTestProject(projectPath);

        /// <summary>True when <c>ProjectTypeGuids</c> carries the MSTest GUID, in any of its list positions.</summary>
        internal static bool HasMsTestProjectTypeGuid(string projectPath)
        {
            try
            {
                var doc = XDocument.Load(projectPath);
                return doc.Descendants().Any(e => e.Name.LocalName == "ProjectTypeGuids"
                    && (e.Value ?? string.Empty).IndexOf(MsTestProjectTypeGuid, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        /// <summary>
        /// True when the project has an assembly <c>Reference</c> to a known test framework. The Include is
        /// a full assembly display name (<c>Name, Version=..., PublicKeyToken=...</c>), so the simple name
        /// is taken from before the first comma rather than prefix-matching the whole string - matching the
        /// whole string would also match on a version or a public key token that happened to start the
        /// same way, and the simple name is the only part that identifies the assembly.
        /// </summary>
        internal static bool HasTestFrameworkAssemblyReference(string projectPath)
        {
            try
            {
                var doc = XDocument.Load(projectPath);
                return doc.Descendants().Any(e =>
                {
                    if (e.Name.LocalName != "Reference")
                        return false;
                    // Explicit null check, not IsNullOrEmpty: net472's overload carries no
                    // [NotNullWhen], so the flow analysis does not narrow through it and the
                    // dereference below warns on that TFM only.
                    var include = (string?)e.Attribute("Include");
                    if (include is null || include.Length == 0)
                        return false;
                    var simpleName = include.Split(',')[0].Trim();
                    return LegacyTestAssemblyPrefixes.Any(
                        prefix => simpleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                });
            }
            catch { return false; }
        }

        /// <summary>
        /// True when a <c>packages.config</c> beside the project names a test framework package. This is
        /// where a pre-PackageReference project records MSTest/xUnit/NUnit, so without reading it a legacy
        /// project using a NuGet test framework has no signal at all.
        /// </summary>
        internal static bool PackagesConfigNamesATestFramework(string projectPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
                if (string.IsNullOrEmpty(dir))
                    return false;
                var packagesConfig = Path.Combine(dir, "packages.config");
                if (!File.Exists(packagesConfig))
                    return false;
                var doc = XDocument.Load(packagesConfig);
                return doc.Descendants().Any(e => e.Name.LocalName == "package"
                    && (string?)e.Attribute("id") is { } id
                    && TestFrameworkPackagePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }

        /// <summary>
        /// Applies <paramref name="predicate"/> to the project file and the <c>Directory.Build.props</c>
        /// files above it that MSBuild actually imports — <b>the nearest one, and then only those it
        /// chains to</b>, not every one up to the root.
        /// <para>
        /// MSBuild imports exactly one: <c>Microsoft.Common.props</c> takes the first
        /// <c>Directory.Build.props</c> found walking up from the project directory and stops. A
        /// grandparent's is reached only when the nearer file opts in, which is what
        /// <see cref="ChainsUpward"/> looks for. Reading past a file that doesn't chain attributes
        /// properties and package references to a project MSBuild never gives them to, and it fails in
        /// the expensive direction: a repo whose ROOT props carries a test framework for one subtree,
        /// under a <c>src/Directory.Build.props</c> that doesn't chain, makes every project under
        /// <c>src</c> answer <see cref="LooksLikeTestProject"/> — and an MTP misclassification runs the
        /// thing via <c>dotnet run</c>. It also silently broke "nearest definition wins" for
        /// <see cref="MtpRunnerEnabled"/> and <see cref="UsesVsTestRunner"/>, whose whole contract is
        /// that they mirror MSBuild precedence.
        /// </para>
        /// </summary>
        internal static bool MatchesUpTree(string projectPath, Func<string, bool> predicate)
        {
            if (predicate(projectPath))
                return true;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
                while (!string.IsNullOrEmpty(dir))
                {
                    var props = Path.Combine(dir, "Directory.Build.props");
                    if (File.Exists(props))
                    {
                        if (predicate(props))
                            return true;
                        if (!ChainsUpward(props))
                            return false; // MSBuild's walk ended here, so ours does
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { /* unreadable path — no match */ }
            return false;
        }

        /// <summary>
        /// True when a <c>Directory.Build.props</c> imports another one above it — the documented
        /// chaining convention, whether spelled with <c>GetPathOfFileAbove</c> or as a plain relative
        /// path. Matched on the <c>Import</c>'s <c>Project</c> naming the file, which is precise enough
        /// because a props file has no other reason to name its own filename.
        /// <para>
        /// The import is routinely written under a <c>Condition</c>, and unlike
        /// <see cref="ReadUnconditionalProperty"/> a condition is not read as a reason to ignore it: a
        /// condition we cannot evaluate leaves the chain UNKNOWN, and continuing the walk is the answer
        /// that keeps the old, wider behaviour rather than inventing a new way to miss a test project.
        /// An unparseable file chains nothing — the predicate could not read it either.
        /// </para>
        /// </summary>
        private static bool ChainsUpward(string propsPath)
        {
            try
            {
                return XDocument.Load(propsPath).Descendants().Any(e =>
                    e.Name.LocalName == "Import"
                    && ((string?)e.Attribute("Project"))?.IndexOf(
                        "Directory.Build.props", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }
    }
}
