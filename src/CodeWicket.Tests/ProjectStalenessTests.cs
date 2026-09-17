using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the agent is told when a build compiled something other than the files it wrote (issue #257).
    /// <para>
    /// These assert WORDING, for the reason <c>FileWriteRefusalTests</c> does: the sentence is the whole
    /// of the behaviour and its reader is a model. Each sentence was chosen against a wrong move —
    /// "rewrite the file", "try a rebuild", "the namespace must be wrong" — and the Visual Studio rounds measured
    /// the one they most need to close: a rebuild goes red and the next build goes green again, which
    /// unexplained reads as "transient, fixed itself".
    /// </para>
    /// </summary>
    public sealed class ProjectStalenessTests
    {
        private const string Legacy = @"C:\ws\Legacy\Legacy.csproj";
        private const string Sdk = @"C:\ws\Modern\Modern.csproj";
        private const string Source = @"C:\ws\Legacy\Canary.cs";

        // ---- which files are candidates at all ----

        [Theory]
        [InlineData(@"C:\ws\a.cs", true)]
        [InlineData(@"C:\ws\a.vb", true)]
        [InlineData(@"C:\ws\a.fs", true)]
        [InlineData(@"C:\ws\a.cpp", true)]
        [InlineData(@"C:\ws\a.h", true)]
        [InlineData(@"C:\ws\a.xaml", true)]
        [InlineData(@"C:\ws\a.resx", true)]
        [InlineData(@"C:\ws\README.md", false)]
        [InlineData(@"C:\ws\appsettings.json", false)]
        [InlineData(@"C:\ws\a.csproj", false)]
        [InlineData(@"C:\ws\a.txt", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyCompiledSourcesAreCandidates(string? path, bool expected)
        {
            Assert.Equal(expected, ProjectStaleness.IsCompiledSource(path));
        }

        /// <summary>One rule, "ends in proj", so a project type not in anyone's list still counts.</summary>
        [Theory]
        [InlineData(@"C:\ws\a.csproj", true)]
        [InlineData(@"C:\ws\a.vbproj", true)]
        [InlineData(@"C:\ws\a.fsproj", true)]
        [InlineData(@"C:\ws\a.vcxproj", true)]
        [InlineData(@"C:\ws\a.sqlproj", true)]
        [InlineData(@"C:\ws\a.props", false)]
        [InlineData(@"C:\ws\a.cs", false)]
        [InlineData(@"C:\ws\project", false)]
        public void AProjectFileIsAnythingEndingInProj(string path, bool expected)
        {
            Assert.Equal(expected, ProjectStaleness.IsProjectFile(path));
        }

        [Theory]
        [InlineData(@"C:\ws\Directory.Build.props", true)]
        [InlineData(@"C:\ws\Directory.Build.targets", true)]
        [InlineData(@"C:\ws\a.csproj", false)]
        [InlineData(@"C:\ws\a.cs", false)]
        public void AnImportIsPropsOrTargets(string path, bool expected)
        {
            Assert.Equal(expected, ProjectStaleness.IsImportFile(path));
        }

        // ---- nothing to say ----

        [Fact]
        public void NoFactsProduceNoNotesAndNoSummary()
        {
            var notes = ProjectStaleness.Describe(null, null, null);

            Assert.Empty(notes);
            Assert.Null(ProjectStaleness.Summary(notes));
            Assert.Null(ProjectStaleness.Summary(null));
        }

        // ---- mode 2: the project file on disk names the file, the loaded project does not ----

        [Fact]
        public void AFileTheProjectFileOnDiskListsIsAStaleProject()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, listedInProjectFileOnDisk: true, projectFileWrittenAndNotReloaded: true) },
                null, null));

            Assert.Equal(ProjectStalenessKind.ProjectNotReloaded, note.Kind);
            Assert.Equal(Source, note.File);
            Assert.Equal(Legacy, note.Project);
            Assert.Contains("Canary.cs", note.Message);
            Assert.Contains("Legacy.csproj", note.Message);
            Assert.Contains("was not compiled", note.Message);
            Assert.Contains("in this conversation", note.Message);
            Assert.Contains("previous file list", note.Message);
        }

        /// <summary>
        /// The measured shape (green ×4, rebuild red, build green) has to be named, because a green →
        /// red → green oscillation with nothing changed is the #250 shape: absent a cause, the agent
        /// supplies "transient" and moves on.
        /// </summary>
        [Fact]
        public void AStaleProjectSaysWhatARebuildDoesAndDoesNot()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, true) }, null, null));

            Assert.Contains("rebuild would surface the file as a compile error", note.Message);
            Assert.Contains("repair nothing", note.Message);
            Assert.Contains("green again", note.Message);
            Assert.Contains("reloading the project", note.Message);
        }

        /// <summary>The two wrong moves it closes: rewriting the file, and editing the project file again.</summary>
        [Fact]
        public void AStaleProjectClosesTheWorkarounds()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, true) }, null, null));

            Assert.Contains("Do not rewrite the file", note.Message);
            Assert.Contains("edit the project file again", note.Message);
        }

        /// <summary>
        /// Where the ledger did not see the project-file edit (the user made it, or an earlier session
        /// did) the sentence still says reload, but does not claim the edit was the agent's.
        /// </summary>
        [Fact]
        public void AStaleProjectNotWrittenHereDoesNotClaimTheEdit()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, listedInProjectFileOnDisk: true, projectFileWrittenAndNotReloaded: false) },
                null, null));

            Assert.Equal(ProjectStalenessKind.ProjectNotReloaded, note.Kind);
            Assert.DoesNotContain("in this conversation", note.Message);
            Assert.Contains("reloading the project", note.Message);
        }

        // ---- the red phase: this build compiled the file, the loaded project still excludes it ----

        /// <summary>
        /// Observed in Visual Studio: through red builds the note said "was not compiled" beside an
        /// <c>errors[]</c> entry for the very file. A sentence the adjacent data refutes is the defect
        /// this type exists to prevent, so a file the build reported on gets the other half of the fact.
        /// </summary>
        [Fact]
        public void AFileThisBuildCompiledIsNotSaidToBeUncompiled()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, listedInProjectFileOnDisk: true, projectFileWrittenAndNotReloaded: true, compiledThisBuild: true) },
                null, null));

            Assert.Equal(ProjectStalenessKind.ProjectNotReloaded, note.Kind);
            Assert.Contains("WAS compiled by this build", note.Message);
            Assert.Contains("still does not contain it", note.Message);
            Assert.Contains("next incremental build will exclude it again", note.Message);
            Assert.Contains("not a fix", note.Message);
            Assert.Contains("reloading the project", note.Message);
            Assert.DoesNotContain("was not compiled", note.Message);
            Assert.DoesNotContain("says nothing about it", note.Message);
        }

        [Fact]
        public void AFileThisBuildCompiledStillClosesTheWorkarounds()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, true, compiledThisBuild: true) },
                null, null));

            Assert.Contains("Fix the errors this result reports", note.Message);
            Assert.Contains("do not rewrite the file", note.Message);
            Assert.Contains("edit the project file again", note.Message);
        }

        // ---- mode 1: in no project at all ----

        [Fact]
        public void AnOrphanUnderALegacyProjectSaysTheListIsExplicit()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, listedInProjectFileOnDisk: false, projectFileWrittenAndNotReloaded: false) },
                null, null));

            Assert.Equal(ProjectStalenessKind.NotInProject, note.Kind);
            Assert.Contains("in no loaded project", note.Message);
            Assert.Contains("was not compiled", note.Message);
            Assert.Contains("explicitly", note.Message);
            Assert.Contains("not an SDK-style project", note.Message);
            Assert.Contains("A rebuild will not change this", note.Message);
        }

        [Fact]
        public void AnOrphanUnderAnSdkProjectSaysToCheckTheDirectoryAndExclusions()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(@"C:\ws\Modern\Canary.cs", Sdk, ProjectStyle.Sdk, false, false) }, null, null));

            Assert.Equal(ProjectStalenessKind.NotInProject, note.Kind);
            Assert.Contains("wildcard", note.Message);
            Assert.Contains("not excluded", note.Message);
            Assert.Contains("A rebuild will not change this", note.Message);
        }

        [Fact]
        public void AnOrphanUnderNoProjectSaysSo()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(@"C:\ws\Elsewhere\Canary.cs", null, null, false, false) }, null, null));

            Assert.Equal(ProjectStalenessKind.NotInProject, note.Kind);
            Assert.Null(note.Project);
            Assert.Contains("under no loaded project's directory", note.Message);
            Assert.Contains("A rebuild will not change this", note.Message);
        }

        /// <summary>An unreadable project file is said to be unreadable, not assumed to be either dialect.</summary>
        [Fact]
        public void AnOrphanUnderAnUnreadableProjectNamesNoDialect()
        {
            var note = Single(ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, null, false, false) }, null, null));

            Assert.Contains("could not be read", note.Message);
            Assert.DoesNotContain("explicitly", note.Message);
            Assert.DoesNotContain("wildcard", note.Message);
        }

        // ---- a changed project file with no orphan hanging off it ----

        [Fact]
        public void AChangedProjectFileWithNoOrphanIsStillReported()
        {
            var note = Single(ProjectStaleness.Describe(null, new[] { new StaleProjectFile(Legacy) }, null));

            Assert.Equal(ProjectStalenessKind.ProjectNotReloaded, note.Kind);
            Assert.Equal(Legacy, note.File);
            Assert.Contains("Legacy.csproj", note.Message);
            Assert.Contains("in this conversation", note.Message);
            Assert.Contains("previous copy of the project file", note.Message);
            Assert.Contains("A rebuild will not take the change", note.Message);
            Assert.Contains("Do not edit the project file again", note.Message);
        }

        [Fact]
        public void AProjectAlreadyNamedByAnOrphanIsNotReportedTwice()
        {
            var notes = ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, true) },
                new[] { new StaleProjectFile(Legacy) },
                null);

            Assert.Single(notes);
            Assert.Equal(ProjectStalenessKind.ProjectNotReloaded, notes[0].Kind);
        }

        // ---- mode 3: a changed import ----

        [Fact]
        public void AChangedImportNamesTheLegacyProjectsAndSaysThereIsNoPrompt()
        {
            var note = Single(ProjectStaleness.Describe(
                null, null, new[] { new StaleImportFile(@"C:\ws\Directory.Build.props", new[] { Legacy }) }));

            Assert.Equal(ProjectStalenessKind.ImportNotReloaded, note.Kind);
            Assert.Contains("Directory.Build.props", note.Message);
            Assert.Contains("Legacy.csproj is not an SDK-style project", note.Message);
            Assert.Contains("does not prompt", note.Message);
            Assert.Contains("previous values", note.Message);
            Assert.Contains("A rebuild will not change this", note.Message);
            Assert.Contains("unload and reload the project,", note.Message);
        }

        [Fact]
        public void AChangedImportOverSeveralProjectsPluralises()
        {
            var note = Single(ProjectStaleness.Describe(
                null, null, new[] { new StaleImportFile(@"C:\ws\Directory.Build.props", new[] { Legacy, @"C:\ws\Other\Other.csproj" }) }));

            Assert.Contains("Legacy.csproj, Other.csproj are not SDK-style projects", note.Message);
            Assert.Contains("unload and reload the projects,", note.Message);
        }

        // ---- properties of every sentence ----

        /// <summary>
        /// The one guess the issue itself raised is "maybe a clean rebuild does?"; every shape answers
        /// it, and none hedges (the <c>FileWriteRefusal</c> rule — a hedge is what the reader replaces).
        /// </summary>
        [Fact]
        public void EveryNoteAnswersTheRebuildQuestionAndHedgesNothing()
        {
            var notes = ProjectStaleness.Describe(
                new[]
                {
                    new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, true),
                    new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, true, compiledThisBuild: true),
                    new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, true, false),
                    new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, false, false),
                    new OrphanedSourceFile(Source, Sdk, ProjectStyle.Sdk, false, false),
                    new OrphanedSourceFile(Source, Legacy, null, false, false),
                    new OrphanedSourceFile(Source, null, null, false, false),
                },
                new[] { new StaleProjectFile(@"C:\ws\Other\Other.csproj") },
                new[] { new StaleImportFile(@"C:\ws\Directory.Build.props", new[] { Legacy }) });

            Assert.Equal(9, notes.Count);
            // Every shape says what a rebuild does, except the one describing a build that just did it.
            Assert.All(notes.Where(n => !n.Message.Contains("WAS compiled")),
                n => Assert.Contains("rebuild", n.Message, System.StringComparison.OrdinalIgnoreCase));
            Assert.All(notes, n => Assert.DoesNotContain("usually", n.Message));
            Assert.All(notes, n => Assert.DoesNotContain("likely", n.Message));
            Assert.All(notes, n => Assert.DoesNotContain("probably", n.Message));
        }

        [Fact]
        public void TheSummaryJoinsEveryMessageInOrder()
        {
            var notes = ProjectStaleness.Describe(
                new[] { new OrphanedSourceFile(Source, Legacy, ProjectStyle.Legacy, false, false) },
                null,
                new[] { new StaleImportFile(@"C:\ws\Directory.Build.props", new[] { Legacy }) });

            var summary = ProjectStaleness.Summary(notes);

            Assert.NotNull(summary);
            Assert.Equal(string.Join(" ", notes.Select(n => n.Message)), summary);
            Assert.True(summary!.IndexOf("Canary.cs") < summary.IndexOf("Directory.Build.props"));
        }

        private static ProjectStalenessNote Single(IReadOnlyList<ProjectStalenessNote> notes)
        {
            Assert.Single(notes);
            return notes[0];
        }
    }
}
