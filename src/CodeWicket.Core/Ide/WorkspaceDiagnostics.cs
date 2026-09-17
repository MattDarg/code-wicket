using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>Which band of the workspace-context block a diagnostic belongs to. Order IS priority.</summary>
    public enum DiagnosticTier
    {
        /// <summary>Any error, anywhere in the solution.</summary>
        Error,

        /// <summary>A project- or solution-level warning: NU/NETSDK/MSB and friends.</summary>
        ProjectLevel,

        /// <summary>A warning in the file the developer is looking at.</summary>
        ActiveFile,

        /// <summary>A warning in another open document.</summary>
        OpenFile,

        /// <summary>Everything else — counted, never listed.</summary>
        Elsewhere,
    }

    /// <summary>What the workspace-context block should say about diagnostics: the rows worth listing, and honest totals for everything else.</summary>
    public sealed record WorkspaceDiagnosticSelection(
        IReadOnlyList<DiagnosticInfo> Selected,
        int TotalErrors,
        int TotalWarnings,
        int Omitted);

    /// <summary>
    /// Chooses which diagnostics ride the per-prompt <c>&lt;workspace-context&gt;</c> block.
    /// </summary>
    /// <remarks>
    /// <b>The rule this encodes: ambient context carries what the agent cannot get by ASKING.</b> It can
    /// always call <c>get_diagnostics</c> for a file or a severity; what it cannot know is which files are
    /// open, which one is active, or that a project depends on a vulnerable package. So context supplies
    /// SHAPE and PROXIMITY, and tools supply detail and completeness. A solution-wide warning dump is the
    /// worst of both — not the shape, since fifty rows out of three hundred show nothing, and not proximate
    /// either — while costing tokens on every single prompt.
    /// <para>
    /// <b>Tiers are an ordering, not a cut-off.</b> Every diagnostic competes for one budget; priority
    /// decides who gets it first and leftover budget is spent on whatever remains. That is what makes the
    /// behaviour scale-free without a density threshold: two warnings in a solution are listed even if
    /// their files are closed, because nothing is competing for the budget, while three hundred fills with
    /// errors and the files actually being worked in and reports the tail as a number. A
    /// "list everything under N" switch was rejected — it changes the block's shape discontinuously for
    /// reasons the agent cannot observe.
    /// </para>
    /// <para>
    /// <b>Project-level rows are identified structurally, not by code prefix.</b> An NU/NETSDK/MSB
    /// allow-list needs maintaining and silently misses whatever the SDK adds next; the owning document
    /// does not. These are the strongest candidates for ambient context of anything here: few, stable,
    /// IntelliSense structurally cannot produce them, they live in no file anyone would open, and nobody
    /// thinks to ASK whether a package has a vulnerability.
    /// </para>
    /// <para>
    /// Lives in Core, away from the VS-bound project, for the reason <see cref="FilePathMatch"/> and
    /// <see cref="TableValue"/> do: the decision is pure and unit-testable without devenv, while collecting
    /// the diagnostics is the VS-coupled part and stays in <c>CodeWicket.Ide</c>. It also runs on the
    /// IDE side of the IPC boundary so only the selected rows cross it, never the whole solution's.
    /// </para>
    /// </remarks>
    public static class WorkspaceDiagnostics
    {
        /// <summary>Total rows the block will ever list, across all tiers. The budget referred to above.</summary>
        public const int MaxListed = 25;

        /// <summary>Per-tier ceilings, so one noisy tier cannot consume the whole budget and hide the others.</summary>
        public const int MaxErrors = 10;
        public const int MaxProjectLevel = 5;
        public const int MaxActiveFile = 8;
        public const int MaxOpenFiles = 8;

        /// <summary>
        /// Documents that are project/solution scaffolding rather than source. A diagnostic reported
        /// against one of these — or against nothing at all — is about the build's configuration, not
        /// about code someone is editing.
        /// </summary>
        private static readonly string[] ProjectLevelExtensions =
        {
            ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj",
            ".props", ".targets", ".sln", ".slnx", ".slnf",
        };

        /// <summary>Which band <paramref name="diagnostic"/> falls in. Errors outrank position: a build-breaker in a file nobody has open is exactly what wants surfacing.</summary>
        public static DiagnosticTier TierOf(DiagnosticInfo diagnostic, string? activeFilePath, IReadOnlyCollection<string>? openFilePaths)
        {
            if (diagnostic is null)
                throw new ArgumentNullException(nameof(diagnostic));

            if (diagnostic.Severity == DiagnosticSeverity.Error)
                return DiagnosticTier.Error;

            if (IsProjectLevel(diagnostic.FilePath))
                return DiagnosticTier.ProjectLevel;

            if (!string.IsNullOrEmpty(activeFilePath) && SamePath(diagnostic.FilePath, activeFilePath))
                return DiagnosticTier.ActiveFile;

            if (openFilePaths is not null && openFilePaths.Any(p => SamePath(diagnostic.FilePath, p)))
                return DiagnosticTier.OpenFile;

            return DiagnosticTier.Elsewhere;
        }

        /// <summary>True for a diagnostic owned by project/solution scaffolding, or by no document at all.</summary>
        public static bool IsProjectLevel(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return true; // reported against nothing => not about a source file

            string extension;
            try { extension = Path.GetExtension(filePath); }
            catch (ArgumentException) { return false; } // unparseable path: treat as source, not scaffolding

            return ProjectLevelExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Picks the rows to list, in tier order, and reports honest totals for the whole set.
        /// </summary>
        /// <remarks>
        /// The totals are computed over EVERYTHING, not over what survived, so the block can always say how
        /// much it is not showing. That distinction is the whole point: an unlabelled truncated list reads
        /// as complete, which is the failure this replaces — with the source dropdown on IntelliSense Only
        /// the section vanished entirely and the agent read a clean solution on every prompt.
        /// </remarks>
        public static WorkspaceDiagnosticSelection Select(
            IReadOnlyList<DiagnosticInfo>? all,
            string? activeFilePath,
            IReadOnlyList<string>? openFilePaths)
        {
            if (all is null || all.Count == 0)
                return new WorkspaceDiagnosticSelection(Array.Empty<DiagnosticInfo>(), 0, 0, 0);

            // Warning is the floor, so that everything COUNTED is everything selected from. Info-tier rows
            // (IDE####, CA#### suggestions such as "Use primary constructor") are excluded deliberately,
            // for two reasons. They are not ambient-context material - they repeat unchanged on every
            // prompt and consume budget a real warning could use; measured on one file, four of the eight
            // slots. And including them made the header LIE: they were listed and counted as omitted while
            // appearing in neither the error nor the warning total, so the block claimed 28 diagnostics
            // while implying 32. The agent has a better route to them anyway - get_diagnostics with
            // scope "analyzers" and severity "hidden", scoped to a file.
            all = all.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
            if (all.Count == 0)
                return new WorkspaceDiagnosticSelection(Array.Empty<DiagnosticInfo>(), 0, 0, 0);

            var totalErrors = all.Count(d => d.Severity == DiagnosticSeverity.Error);
            var totalWarnings = all.Count(d => d.Severity == DiagnosticSeverity.Warning);

            var perTierCap = new Dictionary<DiagnosticTier, int>
            {
                [DiagnosticTier.Error] = MaxErrors,
                [DiagnosticTier.ProjectLevel] = MaxProjectLevel,
                [DiagnosticTier.ActiveFile] = MaxActiveFile,
                [DiagnosticTier.OpenFile] = MaxOpenFiles,
                // All-or-nothing; see the loop below.
                [DiagnosticTier.Elsewhere] = MaxListed,
            };

            var taken = new Dictionary<DiagnosticTier, int>();
            var selected = new List<DiagnosticInfo>(Math.Min(MaxListed, all.Count));

            // Stable ordering within a tier: worst first, then by file and position, so the same solution
            // renders the same block twice running. An unstable list would read as churn to the agent.
            var ordered = all
                .Select(d => (Diagnostic: d, Tier: TierOf(d, activeFilePath, openFilePaths)))
                .OrderBy(x => (int)x.Tier)
                .ThenByDescending(x => (int)x.Diagnostic.Severity)
                .ThenBy(x => x.Diagnostic.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Diagnostic.Line)
                .ThenBy(x => x.Diagnostic.Column);

            var materialized = ordered.ToList();

            // The far tail is ALL-OR-NOTHING, unlike every tier above it. A partial slice of diagnostics
            // from files nobody has open is arbitrary — whichever ones happen to sort first — and it reads
            // as though those rows were chosen for a reason, while crowding out the error and the active
            // file that were. So either they all fit in what is left of the budget, or none are listed and
            // the count speaks for them. The proximate tiers keep their partial slices, because there a
            // slice IS informative: with a cascade the first few errors explain the rest.
            var nearCount = materialized.Count(x => x.Tier != DiagnosticTier.Elsewhere);
            var elsewhereCount = materialized.Count - nearCount;
            var listElsewhere = elsewhereCount > 0
                && elsewhereCount <= MaxListed - Math.Min(nearCount, MaxListed - 1);

            foreach (var (diagnostic, tier) in materialized)
            {
                if (selected.Count >= MaxListed)
                    break;
                if (tier == DiagnosticTier.Elsewhere && !listElsewhere)
                    continue;
                taken.TryGetValue(tier, out var already);
                if (already >= perTierCap[tier])
                    continue;
                taken[tier] = already + 1;
                selected.Add(diagnostic);
            }

            return new WorkspaceDiagnosticSelection(selected, totalErrors, totalWarnings, all.Count - selected.Count);
        }

        /// <summary>Path equality for the proximity tiers — the same comparison the rest of the IDE layer uses.</summary>
        private static bool SamePath(string? a, string? b)
            => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
               && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
