using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CodeWicket.Core.Ide;
using CodeWicket.Shell;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// Does <c>run_tests</c> actually run an old-style (non-SDK) .NET Framework test project?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing offline can answer this, and the reason is structural.</b> The unit suite pins which
    /// projects get classified and what command line is built for them, and both are strings. Whether
    /// that command line runs anything is a question about <c>vstest.console.exe</c>, a program that
    /// ships with Visual Studio and executes .NET Framework assemblies. The test project cannot invoke
    /// it and the shipping code deliberately does not carry it - the same shape as
    /// <c>codefix-actions</c>, which needs a Roslyn runtime <c>Ide</c> holds compile-only.
    /// </para>
    /// <para>
    /// <b>The regression it guards is the SILENT one.</b> Measured 2026-09-04 against VSTest 18.9.0:
    /// handed a <c>.csproj</c> rather than a built assembly, vstest.console prints "a total of 1 test
    /// files matched", exits <b>0</b>, and writes a TRX whose counters are all zero with
    /// <c>outcome="Completed"</c>. Every reader downstream of it is content with that, so the run lands
    /// as a green card for a project that ran nothing - a passing suite that executed no tests, which is
    /// the worst shape a test result can take. Phase 5 below runs that trap deliberately, so the proof
    /// fails if the hazard ever stops being real and the rule it justifies quietly loses its reason.
    /// </para>
    /// <para>
    /// The fixture is <b>built, not committed</b>: an old-style csproj with the MSTest
    /// <c>ProjectTypeGuids</c> and a bare assembly <c>Reference</c> to the framework Visual Studio
    /// ships, compiled by the MSBuild in this install. That makes the subject a genuine legacy project
    /// rather than a mock of one - which matters, because every rule here is about a project shape we
    /// cannot otherwise obtain.
    /// </para>
    /// <para>
    /// <b>What this proof cannot reach:</b> resolving the output assembly's path, which is DTE
    /// (<c>OutputPath</c> + <c>OutputFileName</c>) and needs a running devenv. The proof is handed the
    /// path instead. So a break in <c>ResolveLegacyOutputAssembliesAsync</c> is invisible here and only
    /// a live Visual Studio instance covers it - stated because the proof otherwise looks like end-to-end coverage of the whole
    /// feature, and it is end-to-end coverage of everything after that one hop.
    /// </para>
    /// </remarks>
    internal static class LegacyTestProjectProof
    {
        public static Task<int> RunAsync(string? workRoot)
        {
            // Every sibling proof resolves its work root the same way, and the rule it keeps is not a
            // tidiness one: MSBuild and vstest are LAUNCHED with this as their cwd, and nothing
            // agent-facing may run out of temp — corporate EDR flags activity there (AGENTS.md). The
            // default used to be Path.GetTempPath() (#247).
            var root = string.IsNullOrEmpty(workRoot)
                ? HostScratch.ResolveDir("console/legacy-proof-" + Guid.NewGuid().ToString("N"))
                : workRoot;
            Directory.CreateDirectory(root);

            var failures = new List<string>();
            void Check(string label, bool ok, string? detail = null)
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail is null ? "" : " - " + detail)}");
                if (!ok) failures.Add(label);
            }

            try
            {
                var ide = FindVsIdeDirectory();
                if (ide is null)
                {
                    Console.WriteLine("SKIP: could not locate a Visual Studio install (set VSAPPIDDIR or run from devenv).");
                    return Task.FromResult(0);
                }
                var vstest = Path.Combine(ide, @"CommonExtensions\Microsoft\TestWindow\vstest.console.exe");
                var msbuild = Path.GetFullPath(Path.Combine(ide, @"..\..\MSBuild\Current\Bin\MSBuild.exe"));
                var framework = Path.Combine(ide, @"PublicAssemblies\Microsoft.VisualStudio.QualityTools.UnitTestFramework.dll");
                foreach (var required in new[] { vstest, msbuild, framework })
                {
                    if (!File.Exists(required))
                    {
                        Console.WriteLine($"SKIP: this Visual Studio install has no {Path.GetFileName(required)} ({required}).");
                        return Task.FromResult(0);
                    }
                }
                Console.WriteLine($"vstest.console: {vstest}");

                // --- the fixture ------------------------------------------------------------------
                var projectFile = WriteFixture(root, framework);
                Console.WriteLine($"\n=== 1. building a real old-style MSTest project ===");
                var (buildExit, buildOut) = Run(msbuild,
                    $"\"{projectFile}\" /t:Rebuild /p:Configuration=Debug /v:minimal /nologo", root);
                var assembly = Path.Combine(root, "bin", "Debug", "LegacyTests.dll");
                Check("the legacy project built", buildExit == 0 && File.Exists(assembly),
                    buildExit == 0 ? assembly : buildOut);
                if (!File.Exists(assembly))
                    return Task.FromResult(Report(failures));

                // --- classification, by the shipping code -----------------------------------------
                Console.WriteLine("\n=== 2. TestProjects.Classify, on the real project file ===");
                var classified = TestProjects.Classify(new[] { projectFile });
                Check("the project is recognised at all", classified.Count == 1,
                    $"classified {classified.Count} - a 0 here is the reported bug: \"no test projects found\"");
                if (classified.Count == 1)
                {
                    var p = classified[0];
                    Console.WriteLine($"      kind={p.Kind} style={p.Style} dialect={p.Dialect}");
                    Check("kind is LegacyVsTest", p.Kind == TestProjectKind.LegacyVsTest, p.Kind.ToString());
                    Check("style is Legacy", p.Style == ProjectStyle.Legacy, p.Style.ToString());
                    Check("dialect is VsTest", p.Dialect == FilterDialect.VsTest, p.Dialect.ToString());
                }

                // --- the run ----------------------------------------------------------------------
                Console.WriteLine("\n=== 3. the built command line runs the assembly ===");
                var all = RunVsTest(vstest, assembly, root, new FilterSpec());
                Check("a TRX was produced", all.Trx != null);
                Check("all three tests ran", all.Total == 3, $"total={all.Total}");
                Check("all three passed", all.Passed == 3, $"passed={all.Passed}");

                // Both filter shapes, because they take different branches of the shared expression
                // builder and only a real run can say whether vstest.console accepted either.
                Console.WriteLine("\n=== 4. a structured CLASS filter reaches vstest.console ===");
                var byClass = RunVsTest(vstest, assembly, root,
                    new FilterSpec { Kind = StructuredFilterKind.Class, Value = "LegacyTests.AlphaTests" });
                Check("only that class ran", byClass.Total == 2, $"total={byClass.Total}");

                Console.WriteLine("\n=== 5. a structured METHOD filter reaches vstest.console ===");
                var byMethod = RunVsTest(vstest, assembly, root,
                    new FilterSpec { Kind = StructuredFilterKind.Method, Value = "LegacyTests.BetaTests.BetaPasses" });
                Check("exactly one test ran", byMethod.Total == 1, $"total={byMethod.Total}");

                // --- the hazard the assembly rule exists for --------------------------------------
                Console.WriteLine("\n=== 6. the trap: handing it the PROJECT FILE ===");
                Console.WriteLine("      what the shipping code never does, run here so the rule keeps its reason");
                var trap = RunVsTest(vstest, projectFile, root, new FilterSpec());
                Check("it exits 0 and runs NOTHING - a green card for an empty run",
                    trap.Exit == 0 && trap.Total == 0, $"exit={trap.Exit} total={trap.Total}");
                Check("and it is only distinguishable from a real run by the COUNT",
                    all.Total > 0 && trap.Total == 0, $"real={all.Total} trap={trap.Total}");

                return Task.FromResult(Report(failures));
            }
            finally
            {
                if (string.IsNullOrEmpty(workRoot))
                    try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
            }
        }

        private static int Report(List<string> failures)
        {
            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine("PROOF PASSED: an old-style .NET Framework test project is discovered, run and filtered.");
                return 0;
            }
            Console.WriteLine($"PROOF FAILED: {failures.Count} check(s) - {string.Join("; ", failures)}");
            return 1;
        }

        /// <summary>
        /// The MSTest project type GUID, stated here as a LITERAL rather than read from
        /// <c>TestProjects</c>. The fixture has to assert the value independently of the code under
        /// test: built from the product's own constant, a wrong constant would produce a fixture wearing
        /// the same wrong GUID, the match would succeed, and the proof would pass over exactly the bug it
        /// exists to catch.
        /// </summary>
        private const string MsTestProjectTypeGuid = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";

        /// <summary>
        /// Writes the old-style project. The <c>ProjectTypeGuids</c> stamp and the bare assembly
        /// <c>Reference</c> are the two signals a real legacy MSTest project carries, and the absence of
        /// any <c>Sdk</c> attribute is what makes it legacy - all three are the subject, not scaffolding.
        /// </summary>
        private static string WriteFixture(string root, string frameworkAssembly)
        {
            File.WriteAllText(Path.Combine(root, "Tests.cs"), @"
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegacyTests
{
    [TestClass] public class AlphaTests
    {
        [TestMethod] public void AlphaPasses() { Assert.AreEqual(2, 1 + 1); }
        [TestMethod] public void AlphaAlsoPasses() { Assert.IsTrue(true); }
    }

    [TestClass] public class BetaTests
    {
        [TestMethod] public void BetaPasses() { Assert.AreEqual(""x"", ""x""); }
    }
}");
            var projectFile = Path.Combine(root, "LegacyTests.csproj");
            File.WriteAllText(projectFile, $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"" Condition=""Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')"" />
  <PropertyGroup>
    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>
    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>
    <ProjectGuid>{{B7A1C0D2-4E3F-4A5B-9C8D-1E2F3A4B5C6D}}</ProjectGuid>
    <ProjectTypeGuids>{{{MsTestProjectTypeGuid}}};{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}</ProjectTypeGuids>
    <OutputType>Library</OutputType>
    <AssemblyName>LegacyTests</AssemblyName>
    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
  </PropertyGroup>
  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "">
    <OutputPath>bin\Debug\</OutputPath>
    <DebugType>full</DebugType>
    <DebugSymbols>true</DebugSymbols>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""System"" />
    <Reference Include=""Microsoft.VisualStudio.QualityTools.UnitTestFramework"">
      <HintPath>{frameworkAssembly}</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup><Compile Include=""Tests.cs"" /></ItemGroup>
  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />
</Project>");
            return projectFile;
        }

        /// <summary>
        /// Runs vstest.console through the SHIPPING argument builder, so a change to it is what this
        /// proof measures. Building the arguments here instead would make the proof agree with itself.
        /// </summary>
        private static (int Exit, int Total, int Passed, string? Trx) RunVsTest(
            string vstest, string target, string root, FilterSpec filter)
        {
            var resultsDir = Path.Combine(root, "results", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultsDir);
            var args = TestRunCommands.BuildLegacyVsTestArguments(
                target, resultsDir, TestRunCommands.BuildLegacyFilterExpression(filter), null);
            Console.WriteLine($"      args: {args}");

            var (exit, output) = Run(vstest, args, root);
            var trx = Directory.Exists(resultsDir)
                ? Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (trx is null)
            {
                Console.WriteLine($"      exit={exit}, NO TRX. output:\n{output}");
                return (exit, -1, -1, null);
            }
            var counters = XDocument.Load(trx).Descendants().First(e => e.Name.LocalName == "Counters");
            var total = int.Parse((string?)counters.Attribute("total") ?? "-1");
            var passed = int.Parse((string?)counters.Attribute("passed") ?? "-1");
            Console.WriteLine($"      exit={exit} total={total} passed={passed}");
            return (exit, total, passed, trx);
        }

        private static (int Exit, string Output) Run(string fileName, string arguments, string workingDirectory)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;

            // Both pipes are drained BEFORE the wait. Reading stdout to the end and only then reading
            // stderr deadlocks the moment the child fills the stderr buffer: nothing is draining it, so
            // the child blocks on the write, never exits, and takes the proof with it — the same shape
            // as the AgentVersionProbe deadlock, and MSBuild and vstest are both chatty on stderr.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            // Exiting does not close the pipes — MSBuild reuses worker nodes that outlive the
            // invocation and inherited these handles — so the reads get their own bounded grace rather
            // than the process wait standing in for them. Whatever arrived by then is the output: the
            // exit code is the verdict, and a lost tail of the log must not cost the run.
            Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(15));
            return (process.ExitCode, Text(stdout) + Text(stderr));

            static string Text(Task<string> read) =>
                read.IsCompletedSuccessfully ? read.GetAwaiter().GetResult() : string.Empty;
        }

        /// <summary>
        /// <c>Common7\IDE</c> of a Visual Studio install: the one hosting this process when there is one,
        /// then <c>VSAPPIDDIR</c>, then the newest install on the machine - the last only because this
        /// proof usually runs from a plain console with no VS around it, unlike the shipping code, which
        /// deliberately has no such fallback (it runs inside the install it wants).
        /// </summary>
        private static string? FindVsIdeDirectory()
        {
            var appIdDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
            if (!string.IsNullOrEmpty(appIdDir) && Directory.Exists(appIdDir))
                return appIdDir.TrimEnd(Path.DirectorySeparatorChar);

            foreach (var programFiles in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                var vsRoot = Path.Combine(programFiles ?? string.Empty, "Microsoft Visual Studio");
                if (!Directory.Exists(vsRoot))
                    continue;
                var found = Directory.EnumerateFiles(vsRoot, "devenv.exe", SearchOption.AllDirectories)
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
                if (found != null)
                    return Path.GetDirectoryName(found);
            }
            return null;
        }
    }
}
