using System;
using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Which diagnostics ride the per-prompt workspace-context block (issue #95).
    /// <para>
    /// Pinned here because the failures are all silent ones: a list that omits without saying so reads as
    /// complete, and that is the exact defect being replaced — with the Error List's source dropdown on
    /// IntelliSense Only the whole Diagnostics section vanished, so the agent read a clean solution on
    /// every prompt rather than "diagnostics unavailable".
    /// </para>
    /// </summary>
    public sealed class WorkspaceDiagnosticsTests
    {
        private const string Active = @"C:\repo\src\Active.cs";
        private const string Open = @"C:\repo\src\Open.cs";
        private const string Closed = @"C:\repo\src\Closed.cs";
        private static readonly string[] OpenFiles = { Active, Open };

        private static DiagnosticInfo D(string file, DiagnosticSeverity severity = DiagnosticSeverity.Warning, int line = 1, string? code = "CS0000")
            => new DiagnosticInfo(file, line, 1, severity, "message", code);

        private static WorkspaceDiagnosticSelection Select(params DiagnosticInfo[] all)
            => WorkspaceDiagnostics.Select(all, Active, OpenFiles);

        // --- The tier rule ---

        [Fact]
        public void An_error_outranks_position_even_in_a_closed_file()
        {
            // A build-breaker nobody has open is exactly what wants surfacing.
            Assert.Equal(DiagnosticTier.Error,
                WorkspaceDiagnostics.TierOf(D(Closed, DiagnosticSeverity.Error), Active, OpenFiles));
        }

        [Theory]
        [InlineData(@"C:\repo\App.csproj")]
        [InlineData(@"C:\repo\Directory.Build.props")]
        [InlineData(@"C:\sdk\Microsoft.NET.EolTargetFrameworks.targets")]
        [InlineData(@"C:\repo\App.sln")]
        [InlineData(@"C:\repo\App.slnx")]
        public void Project_scaffolding_is_its_own_tier(string path)
            => Assert.Equal(DiagnosticTier.ProjectLevel, WorkspaceDiagnostics.TierOf(D(path), Active, OpenFiles));

        [Fact]
        public void A_diagnostic_owned_by_no_document_counts_as_project_level()
        {
            // NU/NETSDK rows sometimes carry no file at all. They are about the build, not about code.
            Assert.Equal(DiagnosticTier.ProjectLevel, WorkspaceDiagnostics.TierOf(D(string.Empty), Active, OpenFiles));
        }

        [Fact]
        public void Identification_is_structural_not_by_code_prefix()
        {
            // A NEW SDK diagnostic id must land in the project tier without anyone maintaining a list.
            var future = new DiagnosticInfo(@"C:\repo\App.csproj", 1, 1, DiagnosticSeverity.Warning, "brand new", "NETSDK9999");
            Assert.Equal(DiagnosticTier.ProjectLevel, WorkspaceDiagnostics.TierOf(future, Active, OpenFiles));

            // ...and a source file keeps its position tier whatever its code says.
            var nuInSource = new DiagnosticInfo(Active, 1, 1, DiagnosticSeverity.Warning, "not really", "NU1903");
            Assert.Equal(DiagnosticTier.ActiveFile, WorkspaceDiagnostics.TierOf(nuInSource, Active, OpenFiles));
        }

        [Fact]
        public void Active_open_and_elsewhere_are_distinguished()
        {
            Assert.Equal(DiagnosticTier.ActiveFile, WorkspaceDiagnostics.TierOf(D(Active), Active, OpenFiles));
            Assert.Equal(DiagnosticTier.OpenFile, WorkspaceDiagnostics.TierOf(D(Open), Active, OpenFiles));
            Assert.Equal(DiagnosticTier.Elsewhere, WorkspaceDiagnostics.TierOf(D(Closed), Active, OpenFiles));
        }

        // --- Tiers order, they do not exclude ---

        [Fact]
        public void A_couple_of_warnings_in_closed_files_are_still_listed()
        {
            // The tiers are an ORDERING against one budget, not a filter. Nothing is competing here, so
            // "Elsewhere" gets the budget — a bare count of 2 would be worse than what it replaces.
            var selection = Select(D(Closed, line: 10), D(Closed, line: 20));

            Assert.Equal(2, selection.Selected.Count);
            Assert.Equal(0, selection.Omitted);
        }

        [Fact]
        public void On_a_big_solution_the_budget_goes_to_errors_and_proximity_first()
        {
            var many = Enumerable.Range(1, 300).Select(i => D(Closed, line: i))
                .Concat(new[] { D(Active, line: 1), D(Open, line: 1), D(@"C:\repo\App.csproj") })
                .Concat(new[] { D(Closed, DiagnosticSeverity.Error, line: 999) })
                .ToArray();

            var selection = WorkspaceDiagnostics.Select(many, Active, OpenFiles);

            Assert.Contains(selection.Selected, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Contains(selection.Selected, d => d.FilePath == Active);
            Assert.Contains(selection.Selected, d => d.FilePath == Open);
            Assert.Contains(selection.Selected, d => d.FilePath.EndsWith(".csproj", StringComparison.Ordinal));
            Assert.True(selection.Selected.Count <= WorkspaceDiagnostics.MaxListed);
            Assert.True(selection.Omitted > 0);
        }

        [Fact]
        public void One_noisy_tier_cannot_consume_the_whole_budget()
        {
            // 100 errors must not crowd out the active file the developer is looking at.
            var errors = Enumerable.Range(1, 100).Select(i => D(Closed, DiagnosticSeverity.Error, line: i));
            var selection = WorkspaceDiagnostics.Select(errors.Append(D(Active, line: 5)).ToArray(), Active, OpenFiles);

            Assert.Equal(WorkspaceDiagnostics.MaxErrors, selection.Selected.Count(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Contains(selection.Selected, d => d.FilePath == Active);
        }

        [Fact]
        public void The_far_tail_is_all_or_nothing_never_an_arbitrary_slice()
        {
            // Rendered for real, a partial Elsewhere tier put 20 rows of "File0.cs, File1.cs, File10.cs..."
            // above the error and the active file — chosen by nothing but alphabetical order, and reading
            // as though they had been chosen for a reason. Either they all fit, or the count speaks.
            var many = Enumerable.Range(1, 300).Select(i => D(Closed, line: i))
                .Append(D(Active, line: 1))
                .Append(D(Closed, DiagnosticSeverity.Error, line: 999))
                .ToArray();

            var selection = WorkspaceDiagnostics.Select(many, Active, OpenFiles);

            Assert.DoesNotContain(selection.Selected,
                d => WorkspaceDiagnostics.TierOf(d, Active, OpenFiles) == DiagnosticTier.Elsewhere);
            Assert.Contains(selection.Selected, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Contains(selection.Selected, d => d.FilePath == Active);
        }

        // --- Totals are over EVERYTHING, which is what stops a truncated list reading as complete ---

        [Fact]
        public void Totals_count_the_whole_set_not_the_shown_rows()
        {
            var many = Enumerable.Range(1, 200).Select(i => D(Closed, line: i))
                .Concat(Enumerable.Range(1, 30).Select(i => D(Closed, DiagnosticSeverity.Error, line: i)))
                .ToArray();

            var selection = WorkspaceDiagnostics.Select(many, Active, OpenFiles);

            Assert.Equal(30, selection.TotalErrors);
            Assert.Equal(200, selection.TotalWarnings);
            Assert.Equal(many.Length - selection.Selected.Count, selection.Omitted);
        }

        [Fact]
        public void Nothing_in_means_nothing_claimed()
        {
            var empty = WorkspaceDiagnostics.Select(Array.Empty<DiagnosticInfo>(), Active, OpenFiles);
            Assert.Empty(empty.Selected);
            Assert.Equal(0, empty.TotalErrors);
            Assert.Equal(0, empty.TotalWarnings);
            Assert.Equal(0, empty.Omitted);

            var nothing = WorkspaceDiagnostics.Select(null, null, null);
            Assert.Empty(nothing.Selected);
        }

        [Fact]
        public void Info_tier_suggestions_are_excluded_so_the_totals_cannot_lie()
        {
            // Observed live: four IDE####/CA#### rows were listed and counted as omitted while appearing
            // in neither header total, so the block said "28 warnings" while implying 32 diagnostics
            // existed. Everything COUNTED must be everything selected from.
            var input = new[]
            {
                D(Active, DiagnosticSeverity.Warning, line: 1),
                new DiagnosticInfo(Active, 19, 23, DiagnosticSeverity.Info, "Make field readonly", "IDE0044"),
                new DiagnosticInfo(Active, 61, 12, DiagnosticSeverity.Info, "Use primary constructor", "IDE0290"),
                new DiagnosticInfo(Active, 5, 1, DiagnosticSeverity.Hidden, "unnecessary using", "CS8019"),
            };

            var selection = WorkspaceDiagnostics.Select(input, Active, OpenFiles);

            Assert.Single(selection.Selected);
            Assert.DoesNotContain(selection.Selected, d => d.Severity < DiagnosticSeverity.Warning);
            // The books balance: shown + omitted == errors + warnings, with nothing unaccounted.
            Assert.Equal(selection.TotalErrors + selection.TotalWarnings,
                         selection.Selected.Count + selection.Omitted);
        }

        // --- Stability: the same solution must render the same block twice running ---

        [Fact]
        public void Selection_is_deterministic_regardless_of_input_order()
        {
            var input = new[]
            {
                D(Closed, line: 3), D(Active, line: 2), D(@"C:\repo\App.csproj"),
                D(Open, line: 1), D(Closed, DiagnosticSeverity.Error, line: 4),
            };

            var forwards = WorkspaceDiagnostics.Select(input, Active, OpenFiles).Selected;
            var backwards = WorkspaceDiagnostics.Select(input.Reverse().ToArray(), Active, OpenFiles).Selected;

            Assert.Equal(
                forwards.Select(d => d.FilePath + ":" + d.Line),
                backwards.Select(d => d.FilePath + ":" + d.Line));
        }

        [Fact]
        public void Selected_rows_come_back_in_tier_order()
        {
            var selection = Select(
                D(Closed, line: 1), D(Open, line: 1), D(Active, line: 1),
                D(@"C:\repo\App.csproj"), D(Closed, DiagnosticSeverity.Error, line: 1));

            var tiers = selection.Selected.Select(d => WorkspaceDiagnostics.TierOf(d, Active, OpenFiles)).ToList();
            Assert.Equal(tiers.OrderBy(t => (int)t), tiers);
        }
    }
}
