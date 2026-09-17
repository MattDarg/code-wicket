using System;
using System.Collections.Generic;
using System.IO;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The bounded upward search for a backend's workspace marker (issue #54).
    /// <para>
    /// Kiro resolves its whole workspace from cwd with no upward walk of its own — probed live against
    /// kiro-cli 2.13.0: with <c>repo/.kiro/steering</c> present, a run from <c>repo/</c> applies the
    /// steering and a run from <c>repo/src/solution/</c> does not — so a nested solution loses steering,
    /// agent configs and workspace MCP. Widening the agent's working directory fixes that, but the
    /// guardrails are the whole safety story: <c>~/.kiro</c> exists on EVERY Kiro install (it is the
    /// global config directory), so an unbounded walk would terminate at the user's profile folder and
    /// silently make that the agent's workspace. These tests pin the ceilings, not just the happy path.
    /// </para>
    /// </summary>
    public sealed class WorkspaceRootLocatorTests : IDisposable
    {
        private static readonly IReadOnlyList<WorkspaceMarker> KiroMarkers = new[]
        {
            new WorkspaceMarker(".kiro", new[] { "steering", "specs", "agents", "settings", "hooks" }),
        };

        // Dev-only scratch, so temp is fine here (the never-use-temp rule is about agent-facing paths).
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-wsroot-" + Guid.NewGuid().ToString("N"));

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

        private static WorkspaceRootResult Resolve(
            string solutionRoot, AgentWorkspaceScope scope = AgentWorkspaceScope.RepositoryRoot) =>
            WorkspaceRootLocator.Resolve(solutionRoot, KiroMarkers, scope);

        // --- the bug: the marker is above the solution ---------------------------------------------

        [Fact]
        public void MarkerOneLevelUp_Widens()
        {
            var repo = Dir("repo");
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src");

            var result = Resolve(solution);

            Assert.True(result.Widened);
            Assert.Equal(repo, result.Root);
            Assert.Equal(Path.Combine(repo, ".kiro"), result.MarkerPath);
        }

        [Fact]
        public void MarkerThreeLevelsUp_Widens()
        {
            var repo = Dir("repo");
            Dir("repo", ".kiro", "specs");
            var solution = Dir("repo", "src", "apps", "web");

            var result = Resolve(solution);

            Assert.True(result.Widened);
            Assert.Equal(repo, result.Root);
        }

        // Nearest wins — the backends' own precedence. A .kiro beside the solution must beat a parent's,
        // otherwise widening would override a deliberately project-local workspace.
        [Fact]
        public void MarkerAtSolution_WinsOverParent_AndDoesNotWiden()
        {
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src");
            Dir("repo", "src", ".kiro", "steering");

            var result = Resolve(solution);

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
        }

        [Fact]
        public void NoMarkerAnywhere_ReturnsSolutionUnchanged()
        {
            var solution = Dir("repo", "src");

            var result = Resolve(solution);

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
            Assert.Null(result.MarkerPath);
        }

        // --- the "is it a real workspace" contract -------------------------------------------------

        // A stray empty .kiro must not capture the working directory.
        [Fact]
        public void EmptyMarkerDirectory_IsIgnored()
        {
            Dir("repo", ".kiro");
            var solution = Dir("repo", "src");

            var result = Resolve(solution);

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
        }

        // ...nor one holding only unrelated content (no steering/specs/agents/settings/hooks).
        [Fact]
        public void MarkerWithoutRequiredContent_IsIgnored()
        {
            Dir("repo", ".kiro", "cache");
            var solution = Dir("repo", "src");

            Assert.False(Resolve(solution).Widened);
        }

        // A marker declaring no content contract accepts any non-empty directory.
        [Fact]
        public void MarkerWithNoContentContract_AcceptsAnyEntry()
        {
            var repo = Dir("repo");
            Dir("repo", ".custom", "anything");
            var solution = Dir("repo", "src");

            var result = WorkspaceRootLocator.Resolve(
                solution, new[] { new WorkspaceMarker(".custom") });

            Assert.True(result.Widened);
            Assert.Equal(repo, result.Root);
        }

        // --- the ceilings (the load-bearing guardrails) --------------------------------------------

        // The repository root is the ceiling: a .kiro ABOVE it belongs to a different project.
        [Fact]
        public void StopsAtRepositoryRoot_MarkerAboveIsNotFound()
        {
            Dir("outer", ".kiro", "steering");
            var repo = Dir("outer", "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var solution = Dir("outer", "repo", "src");

            var result = Resolve(solution);

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
            Assert.Contains("repository root", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // A submodule/worktree checkout has .git as a FILE, and it is just as much a ceiling.
        [Fact]
        public void RepositoryMarkerAsFile_IsAlsoACeiling()
        {
            Dir("outer", ".kiro", "steering");
            var repo = Dir("outer", "repo");
            File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: ../.git/modules/repo");
            var solution = Dir("outer", "repo", "src");

            Assert.False(Resolve(solution).Widened);
        }

        // The ceiling is INCLUSIVE: the repository root itself is still checked for a marker.
        [Fact]
        public void RepositoryRootItself_IsStillCheckedForAMarker()
        {
            var repo = Dir("repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src");

            var result = Resolve(solution);

            Assert.True(result.Widened);
            Assert.Equal(repo, result.Root);
        }

        // The disaster case. ~/.kiro is the backend's GLOBAL config directory and exists on every
        // install, so a walk that accepts it would silently make the user's profile folder the agent's
        // workspace. This must hold even though the marker is genuinely there and fully populated.
        [Fact]
        public void UserProfile_IsNeverTheWorkspace_EvenWithARealMarker()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(string.IsNullOrEmpty(profile), "test needs a resolvable user profile");

            // On a real Kiro install ~/.kiro is already there; only create what's missing, and tear it
            // down with NON-recursive deletes so this can never remove a user's global Kiro config.
            var kiroDir = Path.Combine(profile, ".kiro");
            var steeringDir = Path.Combine(kiroDir, "steering");
            var createdKiro = !Directory.Exists(kiroDir);
            var createdSteering = !Directory.Exists(steeringDir);
            if (createdSteering)
                Directory.CreateDirectory(steeringDir);

            // A solution directly under the profile: the only ancestor holding a marker IS the profile.
            var probe = Path.Combine(profile, "cwkt-wsroot-probe-" + Guid.NewGuid().ToString("N"));
            var solution = Path.Combine(probe, "sln");
            Directory.CreateDirectory(solution);
            try
            {
                var result = Resolve(solution);

                Assert.False(result.Widened);
                Assert.Equal(solution, result.Root);
                Assert.Null(result.MarkerPath);
            }
            finally
            {
                try { Directory.Delete(probe, recursive: true); } catch { }
                // Non-recursive: succeeds only while still empty, so pre-existing content is untouchable.
                if (createdSteering) try { Directory.Delete(steeringDir); } catch { }
                if (createdKiro) try { Directory.Delete(kiroDir); } catch { }
            }
        }

        // Backstop for the no-repository-marker case: the walk is bounded even with nothing to stop it.
        [Fact]
        public void SearchDepth_IsCapped()
        {
            var parts = new List<string> { "repo" };
            Dir("repo", ".kiro", "steering");
            for (var i = 0; i <= WorkspaceRootLocator.MaxSearchDepth; i++)
                parts.Add("d" + i);

            var solution = Dir(parts.ToArray());

            var result = Resolve(solution);

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
            Assert.Contains("depth", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ...and exactly at the limit it still resolves, so the cap is off-by-one-free.
        [Fact]
        public void SearchDepth_ResolvesExactlyAtTheLimit()
        {
            var repo = Dir("repo");
            Dir("repo", ".kiro", "steering");

            var parts = new List<string> { "repo" };
            for (var i = 0; i < WorkspaceRootLocator.MaxSearchDepth; i++)
                parts.Add("d" + i);

            var result = Resolve(Dir(parts.ToArray()));

            Assert.True(result.Widened);
            Assert.Equal(repo, result.Root);
        }

        // --- short circuits ------------------------------------------------------------------------

        // The user setting pins the agent to the solution folder, with no I/O at all.
        [Fact]
        public void SolutionOnlyScope_NeverWidens()
        {
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src");

            var result = Resolve(solution, AgentWorkspaceScope.SolutionOnly);

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
        }

        // Claude Code (and custom agents) declare no marker: it already walks up for CLAUDE.md/.claude
        // itself, so moving its working directory would change behaviour it gets right today.
        [Fact]
        public void NoMarkersDeclared_NeverWidens()
        {
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src");

            var result = WorkspaceRootLocator.Resolve(solution, Array.Empty<WorkspaceMarker>());

            Assert.False(result.Widened);
            Assert.Equal(solution, result.Root);
        }

        [Fact]
        public void BlankSolutionRoot_IsHandled()
        {
            var result = WorkspaceRootLocator.Resolve(null, KiroMarkers);

            Assert.False(result.Widened);
            Assert.Equal(string.Empty, result.Root);
        }

        [Theory]
        [InlineData(null, AgentWorkspaceScope.RepositoryRoot)]
        [InlineData("", AgentWorkspaceScope.RepositoryRoot)]
        [InlineData("nonsense", AgentWorkspaceScope.RepositoryRoot)]
        [InlineData("solutiononly", AgentWorkspaceScope.SolutionOnly)]
        [InlineData("SolutionOnly", AgentWorkspaceScope.SolutionOnly)]
        public void ScopeParser_IsTolerant_AndDefaultsToRepositoryRoot(
            string? value, AgentWorkspaceScope expected)
        {
            Assert.Equal(expected, AgentWorkspaceScopeParser.Parse(value));
        }

        // The engine decides whether an idle session may answer a listing by comparing its captured cwd
        // against the root the listing wants. The three roots being compared are spelled by three
        // different writers - a session captured one at start, the host holds the solution's, and
        // Resolve produces a third - so a comparison that only handled one spelling would send every
        // listing down the cold path (slow, and nobody notices) or, the way that matters, would call a
        // stale root a match and read the wrong store.
        [Theory]
        [InlineData(@"C:\repo", @"C:\repo", true)]
        [InlineData(@"C:\repo", @"C:\repo\", true)]
        [InlineData(@"C:\repo\", @"c:\REPO", true)]
        [InlineData(@"C:\repo", @"C:\repo\src", false)]
        [InlineData(@"C:\repo", @"C:\other", false)]
        public void SameRoot_IgnoresSpelling_AndNotDepth(string a, string b, bool expected)
        {
            Assert.Equal(expected, WorkspaceRootLocator.SameRoot(a, b));
        }

        [Fact]
        public void SameRoot_TreatsAnUnknownRootAsNoMatch()
        {
            // Both halves matter, and the second is the load-bearing one. A session that never reported
            // a root gives null here, and the caller ACTS on a match by reusing that session - so two
            // nulls agreeing would reuse a session whose working directory nobody knows, which is the
            // exact failure this comparison exists to stop.
            Assert.False(WorkspaceRootLocator.SameRoot(null, @"C:\repo"));
            Assert.False(WorkspaceRootLocator.SameRoot(@"C:\repo", null));
            Assert.False(WorkspaceRootLocator.SameRoot(null, null));
            Assert.False(WorkspaceRootLocator.SameRoot("", ""));
        }
    }
}
