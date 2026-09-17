using System;
using System.Collections.Generic;
using System.IO;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The bounded upward search for a project's checked-in settings folder (issue #59).
    /// <para>
    /// The load-bearing test here is <see cref="TheTwoSearchesAnswerDifferentDirectoriesForTheSameTree"/>.
    /// Everything else pins a ceiling; that one pins the reason this type exists at all - merging it
    /// with <see cref="WorkspaceRootLocator"/> would reproduce issue #54 on the realistic layout, and
    /// it would do so quietly, because both searches would still return a real directory.
    /// </para>
    /// </summary>
    public sealed class ProjectSettingsLocatorTests : IDisposable
    {
        private static readonly IReadOnlyList<WorkspaceMarker> KiroMarkers = new[]
        {
            new WorkspaceMarker(".kiro", new[] { "steering", "specs", "agents", "settings", "hooks" }),
        };

        // Dev-only scratch, so temp is fine here (the never-use-temp rule is about agent-facing paths).
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-projset-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine(_root, Path.Combine(parts));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Creates a settings folder under <paramref name="parts"/> carrying a settings file.</summary>
        private string WithSettings(params string[] parts)
        {
            var folder = Dir(Combine(parts, Branding.ProjectSettingsFolderName));
            File.WriteAllText(Path.Combine(folder, Branding.ProjectSettingsFileName), "{}");
            return folder;
        }

        /// <summary>Creates a settings folder carrying only a non-empty steering directory.</summary>
        private string WithSteeringOnly(params string[] parts)
        {
            var folder = Dir(Combine(parts, Branding.ProjectSettingsFolderName));
            var steering = Directory.CreateDirectory(
                Path.Combine(folder, Branding.ProjectSteeringFolderName)).FullName;
            File.WriteAllText(Path.Combine(steering, "conventions.md"), "# conventions");
            return folder;
        }

        private static string[] Combine(string[] parts, string tail)
        {
            var all = new string[parts.Length + 1];
            Array.Copy(parts, all, parts.Length);
            all[parts.Length] = tail;
            return all;
        }

        // --- the happy paths --------------------------------------------------------------------

        [Fact]
        public void FolderBesideTheSolution_IsFound()
        {
            var folder = WithSettings("repo", "src", "solution");
            var solution = Dir("repo", "src", "solution");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.True(result.Found);
            Assert.Equal(folder, result.FolderPath);
            Assert.Equal(Path.Combine(folder, Branding.ProjectSettingsFileName), result.SettingsFilePath);
            Assert.Contains("found at", result.Reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void FolderAboveTheSolution_IsFound(int levels)
        {
            Dir("repo", ".git");
            var folder = WithSettings("repo");
            var solution = levels == 1 ? Dir("repo", "src") : Dir("repo", "a", "b", "src");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.True(result.Found);
            Assert.Equal(folder, result.FolderPath);
        }

        [Fact]
        public void NearestWins()
        {
            Dir("repo", ".git");
            WithSettings("repo");
            var nearest = WithSettings("repo", "src", "solution");
            var solution = Dir("repo", "src", "solution");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.Equal(nearest, result.FolderPath);
        }

        // --- the content contract ---------------------------------------------------------------

        /// <summary>
        /// A folder holding ONLY steering is a real hit, even though nothing reads steering yet
        /// (issue #59 part 2). The contract is fixed now precisely so it cannot move later: defined
        /// when part 2 ships instead, this same tree would resolve to a different folder across two
        /// releases under nearest-wins, silently changing which file governs on upgrade.
        /// </summary>
        [Fact]
        public void SteeringOnlyFolder_IsAHitWithNoSettingsFile()
        {
            Dir("repo", ".git");
            var folder = WithSteeringOnly("repo", "src", "solution");
            var solution = Dir("repo", "src", "solution");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.True(result.Found);
            Assert.Equal(folder, result.FolderPath);
            Assert.Null(result.SettingsFilePath);
            Assert.NotNull(result.SteeringPath);
        }

        /// <summary>The upgrade hazard the contract above prevents, stated as its own test.</summary>
        [Fact]
        public void SteeringOnlyFolder_ShadowsASettingsFileAboveIt()
        {
            Dir("repo", ".git");
            WithSettings("repo");
            var nearest = WithSteeringOnly("repo", "src", "solution");
            var solution = Dir("repo", "src", "solution");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.Equal(nearest, result.FolderPath);
            Assert.Null(result.SettingsFilePath);
        }

        [Fact]
        public void EmptyFolder_IsIgnoredAndTheWalkContinuesPastIt()
        {
            Dir("repo", ".git");
            var above = WithSettings("repo");
            Dir("repo", "src", Branding.ProjectSettingsFolderName); // stray and empty
            var solution = Dir("repo", "src");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.Equal(above, result.FolderPath);
        }

        [Fact]
        public void FolderWithAnEmptySteeringDirectory_IsNotAHit()
        {
            Dir("repo", ".git");
            Dir("repo", "src", Branding.ProjectSettingsFolderName, Branding.ProjectSteeringFolderName);
            var solution = Dir("repo", "src");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.False(result.Found);
        }

        // --- the ceilings -----------------------------------------------------------------------

        [Fact]
        public void RepositoryRootIsAnInclusiveCeiling_AFolderAboveItIsNotFound()
        {
            WithSettings("outer");
            Dir("outer", "repo", ".git");
            var solution = Dir("outer", "repo", "src");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.False(result.Found);
            Assert.Contains("repository root", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void AFolderAtTheRepositoryRootItselfIsFound()
        {
            Dir("repo", ".git");
            var folder = WithSettings("repo");
            var solution = Dir("repo", "src");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.Equal(folder, result.FolderPath);
        }

        /// <summary>
        /// The disaster this shares with <see cref="WorkspaceRootLocator"/>: a settings folder created
        /// once in the profile directory would otherwise configure every solution the user ever opens.
        /// Driven through the real profile path, with a real folder present, so it cannot pass by the
        /// folder simply being absent.
        /// </summary>
        [Fact]
        public void TheUserProfileIsNeverASourceOfProjectSettings()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile))
                return; // no resolvable profile on this machine; the other ceilings still bound the walk

            var folder = Path.Combine(profile, Branding.ProjectSettingsFolderName);
            var created = !Directory.Exists(folder);
            var settings = Path.Combine(folder, Branding.ProjectSettingsFileName);
            var wroteSettings = false;
            try
            {
                Directory.CreateDirectory(folder);
                if (!File.Exists(settings))
                {
                    File.WriteAllText(settings, "{}");
                    wroteSettings = true;
                }

                // A solution nested under the profile, with no repository marker anywhere between.
                var solution = Directory.CreateDirectory(
                    Path.Combine(profile, "cwkt-projset-" + Guid.NewGuid().ToString("N"), "sln")).FullName;
                try
                {
                    var result = ProjectSettingsLocator.Find(solution);

                    Assert.False(result.Found);
                    Assert.NotEqual(folder, result.FolderPath);
                }
                finally
                {
                    try { Directory.Delete(Path.GetDirectoryName(solution)!, recursive: true); }
                    catch { /* best effort */ }
                }
            }
            finally
            {
                try
                {
                    if (wroteSettings) File.Delete(settings);
                    if (created) Directory.Delete(folder, recursive: true);
                }
                catch { /* best effort */ }
            }
        }

        [Fact]
        public void DepthCapBounds_TheNoRepositoryMarkerCase()
        {
            // Deeper than MaxSearchDepth, with no repository marker to stop the walk earlier.
            var parts = new List<string> { "top" };
            WithSettings("top");
            for (var i = 0; i < WorkspaceRootLocator.MaxSearchDepth + 2; i++)
                parts.Add("d" + i);

            var result = ProjectSettingsLocator.Find(Dir(parts.ToArray()));

            Assert.False(result.Found);
            Assert.Contains("search depth", result.Reason, StringComparison.Ordinal);
        }

        // --- misses say so, and never look like hits ---------------------------------------------

        [Fact]
        public void AMissReturnsNoFolder_NotTheSolutionRoot()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src");

            var result = ProjectSettingsLocator.Find(solution);

            Assert.False(result.Found);
            Assert.Null(result.FolderPath);
            Assert.Null(result.SettingsFilePath);
            Assert.NotEqual(string.Empty, result.Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ABlankSolutionRootIsHandled(string? solutionRoot)
        {
            var result = ProjectSettingsLocator.Find(solutionRoot);

            Assert.False(result.Found);
            Assert.Equal("no workspace root", result.Reason);
        }

        // --- the reason this type exists ----------------------------------------------------------

        /// <summary>
        /// <b>The two searches must answer DIFFERENT directories for the same tree.</b>
        /// <para>
        /// This is the realistic layout from issue #59: per-project settings live with the project, so
        /// the settings folder sits at the solution, while Kiro's marker sits at the repository root
        /// because steering is repo-wide. A single nearest-marker-wins search over the union of both
        /// markers picks the solution folder, pins the agent's working directory there, and loses the
        /// repo-root steering - issue #54 reproduced exactly, by the feature built on top of its fix.
        /// </para>
        /// <para>
        /// It would also be quiet: both searches still return a real, existing directory, so nothing
        /// throws and nothing looks wrong. Only asserting both answers at once catches it.
        /// </para>
        /// </summary>
        [Fact]
        public void TheTwoSearchesAnswerDifferentDirectoriesForTheSameTree()
        {
            var repo = Dir("repo");
            Dir("repo", ".git");
            Dir("repo", ".kiro", "steering");
            var settingsFolder = WithSettings("repo", "src", "solution");
            var solution = Dir("repo", "src", "solution");

            var agentRoot = WorkspaceRootLocator.Resolve(solution, KiroMarkers);
            var projectSettings = ProjectSettingsLocator.Find(solution);

            // The agent widens to the repository root, for Kiro's marker.
            Assert.True(agentRoot.Widened);
            Assert.Equal(repo, agentRoot.Root);
            Assert.Equal(Path.Combine(repo, ".kiro"), agentRoot.MarkerPath);

            // The settings folder resolves at the solution, two levels below it.
            Assert.Equal(settingsFolder, projectSettings.FolderPath);
            Assert.NotEqual(agentRoot.Root, Path.GetDirectoryName(projectSettings.FolderPath));
        }

        /// <summary>
        /// The mirror, and the half that keeps PRESENCE from granting: the settings folder is never a
        /// workspace marker, so its mere existence can never move the agent's working directory.
        /// </summary>
        [Fact]
        public void TheSettingsFolderIsNeverAWorkspaceMarker()
        {
            Dir("repo", ".git");
            WithSettings("repo");
            var solution = Dir("repo", "src");

            var agentRoot = WorkspaceRootLocator.Resolve(solution, KiroMarkers);

            Assert.False(agentRoot.Widened);
            Assert.Equal(solution, agentRoot.Root);
            Assert.Null(agentRoot.MarkerPath);
        }

        /// <summary>
        /// The shared refusal predicate is one implementation, not two: both searches must give the
        /// same answer for the same directory, or the profile hole reopens in whichever copy drifts.
        /// </summary>
        [Fact]
        public void TheProfileRefusalIsSharedWithTheWorkspaceSearch()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
                return;

            Assert.True(WorkspaceRootLocator.IsRefusedAsWorkspace(profile));
            Assert.True(WorkspaceRootLocator.IsRefusedAsWorkspace(Path.GetPathRoot(profile)!));
            Assert.False(WorkspaceRootLocator.IsRefusedAsWorkspace(Dir("repo", "src")));
        }
    }
}
