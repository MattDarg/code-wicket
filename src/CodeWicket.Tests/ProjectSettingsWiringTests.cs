using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How a project's settings file reaches the provider (issue #59): one resolution, shared by the
    /// session start and the session listing, with the log lines that make its answer diagnosable.
    /// </summary>
    public sealed class ProjectSettingsWiringTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-projwire-" + Guid.NewGuid().ToString("N"));

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

        private string WriteSettings(string json, params string[] parts)
        {
            var folder = Dir(parts.Concat(new[] { Branding.ProjectSettingsFolderName }).ToArray());
            var path = Path.Combine(folder, Branding.ProjectSettingsFileName);
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>A provider declaring Kiro's marker, so the fallback walk is exercised for real.</summary>
        private static AcpAgentProvider Provider() => new AcpAgentProvider(new AcpAgentConfig
        {
            ProviderId = "test",
            DisplayName = "Test",
            CliPath = "does-not-launch",
            WorkspaceMarkers = new[]
            {
                new WorkspaceMarker(".kiro", new[] { "steering", "specs", "agents", "settings", "hooks" }),
            },
        });

        // --- the reason this is one method ---------------------------------------------------------

        /// <summary>
        /// <b>The session start and the session listing must resolve the same directory.</b>
        /// <para>
        /// Once a checked-in file can redirect the working directory, an override reaching only one of
        /// them leaves the backend's <c>session/list</c> reading a different store - and that comes back
        /// as an EMPTY list, indistinguishable from the user having no conversations. Measured at two
        /// hours forty-one minutes of a picker reporting nothing in issue #108, cured only by restarting
        /// the window.
        /// </para>
        /// </summary>
        [Fact]
        public void TheListingRootAndTheSessionRootAgreeWhenAProjectRedirectsThem()
        {
            var repo = Dir("repo");
            Dir("repo", ".git");
            var elsewhere = Dir("repo", "elsewhere");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"../../elsewhere\"}", "repo", "src", "solution");

            var provider = Provider();
            var sessionRoot = provider.ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null).Root;
            var listingRoot = provider.ResolveListingRoot(solution, AgentWorkspaceScope.RepositoryRoot);

            Assert.Equal(elsewhere, sessionRoot.Root);
            Assert.True(
                WorkspaceRootLocator.SameRoot(sessionRoot.Root, listingRoot.Root),
                $"session root '{sessionRoot.Root}' and listing root '{listingRoot.Root}' must name the same directory");
            Assert.NotEqual(repo, sessionRoot.Root);
        }

        /// <summary>
        /// The realistic layout, end to end through the provider: the settings folder at the solution,
        /// the backend's marker at the repository root. With no override the marker walk still wins, so
        /// the new search cannot have quietly taken over the old one's job.
        /// </summary>
        [Fact]
        public void WithNoOverrideTheMarkerWalkStillDecides()
        {
            var repo = Dir("repo");
            Dir("repo", ".git");
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{}", "repo", "src", "solution");

            var resolution = Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            Assert.Equal(repo, resolution.Root.Root);
            Assert.True(resolution.Root.Widened);
            Assert.Equal(Path.Combine(repo, ".kiro"), resolution.Root.MarkerPath);
        }

        /// <summary>An override beats the marker walk, and the reason says which decided it.</summary>
        [Fact]
        public void AnOverrideBeatsTheMarkerWalkAndTheReasonSaysSo()
        {
            Dir("repo", ".git");
            Dir("repo", ".kiro", "steering");
            var elsewhere = Dir("repo", "elsewhere");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"../../elsewhere\"}", "repo", "src", "solution");

            var resolution = Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            Assert.Equal(elsewhere, resolution.Root.Root);
            Assert.Contains("agentWorkspaceRoot", resolution.Root.Reason, StringComparison.Ordinal);
            Assert.Null(resolution.Root.MarkerPath);
        }

        /// <summary>
        /// The anchor is the directory holding the settings FOLDER, not the solution root.
        /// <para>
        /// <b>The fixture has to put them in different places, or this test pins nothing</b> - which is
        /// how it was first written, and prove-check.ps1 said so. With the settings folder beside the
        /// solution the two anchors are the same directory, so measuring from either gives the same
        /// answer and the bug is invisible. That is also why the real defect is quiet: the two coincide
        /// in most projects, and only diverge once the settings folder sits above the solution.
        /// </para>
        /// <para>
        /// Here the folder is at <c>repo/src</c> and the solution at <c>repo/src/solution</c>, so
        /// "<c>..</c>" means <c>repo</c> measured from the folder and <c>repo/src</c> measured from the
        /// solution.
        /// </para>
        /// </summary>
        [Fact]
        public void TheOverrideIsMeasuredFromTheSettingsFolderNotTheSolution()
        {
            var repo = Dir("repo");
            Dir("repo", ".git");
            var src = Dir("repo", "src");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"..\"}", "repo", "src");

            var resolution = Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            Assert.Equal(repo, resolution.Root.Root);
            Assert.NotEqual(src, resolution.Root.Root);
        }

        /// <summary>
        /// <b>The override is backend-agnostic, including for a backend that declares no workspace
        /// marker at all</b> - which is Claude Code's shape, and every custom ACP agent's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #54 deliberately gave Claude no markers: it walks up for <c>CLAUDE.md</c>/<c>.claude</c>
        /// itself, so widening its working directory on a guess would break behaviour it already gets
        /// right. That reasoning does not extend to this key, and the difference is the same one that
        /// lets the override cross a repository boundary: the marker walk is OUR inference, while an
        /// <c>agentWorkspaceRoot</c> is a deliberate statement by the repository author.
        /// </para>
        /// <para>
        /// Worth pinning because the asymmetry is easy to misread as an oversight. For Kiro the
        /// override competes with a marker walk; for a marker-less backend it is the ONLY thing that
        /// can move the working directory, so the blast radius is larger and quieter - and Claude keys
        /// its own session store by a hash of exactly that path, which is what makes ResumeRootGuard
        /// matter there too.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheOverrideAppliesToABackendThatDeclaresNoWorkspaceMarker()
        {
            Dir("repo", ".git");
            Dir("repo", ".kiro", "steering");
            var elsewhere = Dir("repo", "elsewhere");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"../../elsewhere\"}", "repo", "src", "solution");

            // Claude Code's config shape: no WorkspaceMarkers at all.
            var markerless = new AcpAgentProvider(new AcpAgentConfig
            {
                ProviderId = "markerless",
                DisplayName = "Markerless",
                CliPath = "does-not-launch",
            });

            var resolution = markerless.ResolveAgentRoot(
                solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            Assert.Equal(elsewhere, resolution.Root.Root);

            // And the control: with no override it stays put, exactly as issue #54 requires - the
            // repo-root .kiro above must NOT widen a backend that declares no marker.
            File.Delete(Path.Combine(
                solution, Branding.ProjectSettingsFolderName, Branding.ProjectSettingsFileName));
            var unmoved = markerless.ResolveAgentRoot(
                solution, AgentWorkspaceScope.RepositoryRoot, log: null);
            Assert.Equal(solution, unmoved.Root.Root);
        }

        // --- what the user is told -----------------------------------------------------------------

        [Fact]
        public void ARefusedKeyBecomesANoticeAndChangesNothing()
        {
            Dir("repo", ".git");
            Dir("repo", ".kiro", "steering");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"allowedCommands\": [\"rm -rf /\"]}", "repo", "src", "solution");

            var resolution = Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            var notice = Assert.Single(resolution.Notices());
            Assert.Contains("allowedCommands", notice, StringComparison.Ordinal);
            // The marker walk still decided, exactly as if the file had been empty.
            Assert.Equal(Path.Combine(_root, "repo"), resolution.Root.Root);
        }

        [Fact]
        public void AnUnknownKeyIsLoggedButNeverBecomesANotice()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"steeringBudgetKb\": 64}", "repo", "src", "solution");

            var lines = new List<string>();
            var resolution = Provider().ResolveAgentRoot(
                solution, AgentWorkspaceScope.RepositoryRoot, lines.Add);

            Assert.Empty(resolution.Notices());
            Assert.Contains(lines, l => l.Contains("steeringBudgetKb", StringComparison.Ordinal));
        }

        [Fact]
        public void ARefusedOverrideBecomesANotice()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"nowhere-at-all\"}", "repo", "src", "solution");

            var resolution = Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            var notice = Assert.Single(resolution.Notices());
            Assert.Contains("agentWorkspaceRoot", notice, StringComparison.Ordinal);
            Assert.Equal(solution, resolution.Root.Root);
        }

        /// <summary>
        /// Under the user's own <c>SolutionOnly</c> setting the override is refused AND says nothing to
        /// the user: from their seat the working directory is the solution folder, exactly as asked.
        /// </summary>
        [Fact]
        public void TheScopeSettingRefusesTheOverrideQuietly()
        {
            Dir("repo", ".git");
            Dir("repo", "elsewhere");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"../../elsewhere\"}", "repo", "src", "solution");

            var lines = new List<string>();
            var resolution = Provider().ResolveAgentRoot(
                solution, AgentWorkspaceScope.SolutionOnly, lines.Add);

            Assert.Empty(resolution.Notices());
            Assert.Equal(solution, resolution.Root.Root);
            Assert.Contains(lines, l => l.Contains("Solution folder only", StringComparison.Ordinal));
        }

        // --- the log pair ---------------------------------------------------------------------------

        /// <summary>
        /// <b>The first line is written BEFORE the search, and that ordering is the whole point of there
        /// being two.</b> A line reporting the RESULT is silent for a bug in the search itself, and then
        /// both lines are missing with nothing to say the read was even attempted. Saying what is about
        /// to be asked is what gives the second line's absence a meaning - the shape of the existing
        /// <c>[session-list]</c> and <c>[mcp]</c> pairs.
        /// </summary>
        [Fact]
        public void TheFirstLineSaysWhatIsAboutToBeAskedAndComesFirst()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{}", "repo", "src", "solution");

            var lines = new List<string>();
            Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, lines.Add);

            Assert.NotEmpty(lines);
            Assert.StartsWith("[project-settings] searching from", lines[0], StringComparison.Ordinal);
            Assert.Contains(solution, lines[0], StringComparison.Ordinal);
            Assert.Contains(nameof(AgentWorkspaceScope.RepositoryRoot), lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, l => l.StartsWith("[project-settings] found", StringComparison.Ordinal));
        }

        /// <summary>
        /// A miss is stated, not silent - "we applied nothing" and "there was nothing to apply" must be
        /// different output, or a collected log cannot tell a broken read from an absent file.
        /// </summary>
        [Fact]
        public void AMissIsStatedRatherThanSilent()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src", "solution");

            var lines = new List<string>();
            Provider().ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, lines.Add);

            Assert.Contains(lines, l => l.StartsWith("[project-settings] none found", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("[project-settings] applied: nothing", StringComparison.Ordinal));
        }

        /// <summary>
        /// A null logger is honoured - nothing is written and nothing throws. This is what lets the
        /// listing path stay quiet, since it runs once per picker open and the <c>[session-list]</c>
        /// lines already name the root it resolved.
        /// </summary>
        /// <remarks>
        /// <b>This pins the MECHANISM, not the call site, and the difference is worth stating.</b> The
        /// obvious test - capture <c>Console.Error</c> around <c>ResolveListingRoot</c> and assert it is
        /// empty - was written first and is unsound: <c>Console.SetError</c> is process-global while
        /// xUnit runs test CLASSES in parallel, so it captures whatever else happens to be running.
        /// Several classes drive <c>StartSessionAsync</c>, which logs exactly these lines, so it passed
        /// in a serial run and failed under the gate matrix, blaming a class it had nothing to do with.
        /// A check that fails depending on what runs beside it is worse than no check.
        /// <para>
        /// What is left unpinned is one visible argument at one call site (<c>log: null</c>). That is a
        /// deliberate trade: the alternative is a seam on the provider existing solely so a test can
        /// watch it, and the risk being bought off is log volume rather than correctness.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANullLoggerIsHonoured()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"allowedCommands\": []}", "repo", "src", "solution");

            var provider = Provider();

            // The same inputs that DO produce lines when a logger is supplied, so this cannot pass by
            // the path being one that had nothing to say.
            var noisy = new List<string>();
            provider.ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, noisy.Add);
            Assert.NotEmpty(noisy);

            var quiet = provider.ResolveAgentRoot(solution, AgentWorkspaceScope.RepositoryRoot, log: null);

            Assert.Equal(solution, quiet.Root.Root);
            Assert.Single(quiet.Notices());
        }
    }
}
