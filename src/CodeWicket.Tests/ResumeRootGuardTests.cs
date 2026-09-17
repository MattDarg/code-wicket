using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A conversation may only be resumed into the working directory it belongs to (issue #59), and
    /// the one exception is the user's own pin (issue #185).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The premise this guard was built on is about something else.</b> An observation
    /// in Visual Studio read as "Kiro scopes its sessions by working directory": asked to load a real session id from
    /// a directory that session does not belong to, it returned a brand new EMPTY session wearing the
    /// requested id. Re-measured 2026-09-12 (<c>Console resume-cross-root</c>), both Kiro engines
    /// CARRY a conversation across roots and Claude refuses cleanly — the empty session was Kiro v3's
    /// answer to an id it no longer HELD (<c>Console resume-unknown-id</c>), which the replay count
    /// now catches post-hoc. The guard stays as the safety net under the host's ask, for a host that
    /// cannot ask or a caller that forgot to. The original observation, kept as the record:
    /// </para>
    /// <code>
    /// sess_920d6be8…   title "New Session"
    ///                  createdAt      2026-09-02T21:00:36.729Z   (the moment of the load)
    ///                  lastModifiedAt 2026-09-02T21:00:36.743Z   (14 ms later)
    ///                  workspacePaths [ …\testing-root ]          (the NEW root)
    /// </code>
    /// <para>
    /// Because the load succeeded, <c>ResumeFailureReason</c> stayed null and nothing was reported.
    /// The user read ninety entries of their own transcript beside an agent that had never heard of
    /// any of it - the failure presenting as success, which is the one shape this codebase keeps
    /// getting bitten by.
    /// </para>
    /// <para>
    /// Nothing else could catch it. The conversation was intact in our store, the session id was
    /// correct, and Kiro still held the original 64 days back. Only comparing the directory the
    /// conversation BELONGS to against the one this session will RUN in separates the two.
    /// </para>
    /// </remarks>
    public sealed class ResumeRootGuardTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-resumeroot-" + Guid.NewGuid().ToString("N"));

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

        private static AcpAgentProvider Provider() => new AcpAgentProvider(new AcpAgentConfig
        {
            ProviderId = "test",
            DisplayName = "Test",
            CliPath = "does-not-launch",
        });

        /// <summary>
        /// <b>The rule the engine actually calls</b>, not a local restatement of it. Written the
        /// other way first, and it pinned nothing: re-deriving the comparison here would have kept
        /// passing with the guard removed from the engine entirely.
        /// </summary>
        private static bool WouldRefuse(string? conversationRoot, string? sessionRoot) =>
            ResumeRootGuard.Refuse(conversationRoot, sessionRoot) is not null;

        // --- a pinned directory (issue #185) ------------------------------------------------------

        /// <summary>
        /// A pin resolves to itself and searches for nothing: no marker walk, no project settings
        /// file — the pin exists to override what either would have said. Widened is a fact about the
        /// solution, not about how the root was chosen, so it is still reported.
        /// </summary>
        [Fact]
        public void APinnedRootResolvesToItselfAndSearchesNothing()
        {
            var elsewhere = Dir("repo", "elsewhere");
            var solution = Dir("repo", "src", "solution");

            var resolution = AcpAgentProvider.PinnedResolution(elsewhere, solution);

            Assert.Equal(elsewhere, resolution.Root.Root);
            Assert.True(resolution.Root.Widened);
            Assert.Null(resolution.Root.MarkerPath);
            Assert.Equal(ResumeRootGuard.PinnedReason, resolution.Root.Reason);
            Assert.False(resolution.Location.Found);
            Assert.Empty(resolution.Issues);
            Assert.False(AcpAgentProvider.PinnedResolution(solution, solution).Root.Widened);
        }

        /// <summary>
        /// The reason has to say the root was CHOSEN for this conversation — it is what the
        /// working-directory notice prints, and any other wording silently contradicts a project
        /// settings file that would have put the agent elsewhere.
        /// </summary>
        [Fact]
        public void ThePinnedReasonSaysItWasChosenForThisConversation()
        {
            Assert.Contains("chosen for this conversation", ResumeRootGuard.PinnedReason, StringComparison.Ordinal);
        }

        /// <summary>
        /// A pin is a NEW way to set a root — read back from a persisted file, naming a directory that
        /// may since have gone — so it takes the locator's own two refusals. And a pin the guard
        /// accepts is, by construction, not refused as a cross-root load.
        /// </summary>
        [Fact]
        public void APinIsRefusedWhereTheLocatorWouldRefuseItAndAcceptedOtherwise()
        {
            var solution = Dir("repo", "src", "solution");

            Assert.Null(ResumeRootGuard.RefusePin(solution));
            Assert.False(WouldRefuse(conversationRoot: solution, sessionRoot: solution));

            Assert.Contains("no longer exists", ResumeRootGuard.RefusePin(Path.Combine(_root, "gone"))!, StringComparison.Ordinal);
            Assert.Contains("never a workspace", ResumeRootGuard.RefusePin(Path.GetPathRoot(_root)!)!, StringComparison.Ordinal);
            Assert.NotNull(ResumeRootGuard.RefusePin(null));
            Assert.NotNull(ResumeRootGuard.RefusePin(string.Empty));
        }

        // --- the case that reached Visual Studio -----------------------------------------------------

        /// <summary>
        /// A project settings file moves the working directory under an existing conversation. The
        /// conversation's own root and the session's root now differ, and the resume must be refused
        /// BEFORE the backend gets a chance to answer it with a fresh empty session.
        /// </summary>
        [Fact]
        public void AnOverrideThatMovesTheRootMakesAnExistingConversationUnresumable()
        {
            Dir("repo", ".git");
            var elsewhere = Dir("repo", "elsewhere");
            var solution = Dir("repo", "src", "solution");
            WriteSettings("{\"agentWorkspaceRoot\": \"../../elsewhere\"}", "repo", "src", "solution");

            // Where this session will run, resolved exactly as the engine resolves it.
            var sessionRoot = Provider()
                .ResolveListingRoot(solution, AgentWorkspaceScope.RepositoryRoot).Root;
            Assert.Equal(elsewhere, sessionRoot);

            // The conversation was created before the override existed, so it belongs to the solution.
            Assert.True(WouldRefuse(conversationRoot: solution, sessionRoot: sessionRoot));
        }

        /// <summary>Without an override the roots agree, so an ordinary resume is untouched.</summary>
        [Fact]
        public void WithoutAnOverrideAnOrdinaryResumeIsNotRefused()
        {
            Dir("repo", ".git");
            var solution = Dir("repo", "src", "solution");

            var sessionRoot = Provider()
                .ResolveListingRoot(solution, AgentWorkspaceScope.RepositoryRoot).Root;

            Assert.Equal(solution, sessionRoot);
            Assert.False(WouldRefuse(conversationRoot: solution, sessionRoot: sessionRoot));
        }

        /// <summary>
        /// Spelling must not decide this. The conversation's root is read from a persisted file and
        /// the session's is freshly resolved, so the two arrive by different routes - which is exactly
        /// why <see cref="WorkspaceRootLocator.SameRoot"/> exists rather than a string comparison.
        /// </summary>
        [Theory]
        [InlineData("repo\\src\\solution", "repo/src/solution")]
        [InlineData("repo\\src\\solution", "repo\\src\\solution\\")]
        [InlineData("repo\\src\\solution", "repo\\src\\..\\src\\solution")]
        public void TheSameDirectorySpelledDifferentlyIsNotAMismatch(string a, string b)
        {
            var solution = Dir("repo", "src", "solution");

            Assert.False(WouldRefuse(
                Path.Combine(_root, a.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(_root, b.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(Directory.Exists(solution));
        }

        // --- what the user is told ------------------------------------------------------------------

        /// <summary>
        /// The refusal travels on the field the host already renders, so the existing
        /// "Couldn't open that conversation" notice covers this with no new surface - and, unlike the
        /// backend's silence, it says which two directories disagreed.
        /// </summary>
        [Fact]
        public void TheRefusalRidesTheFieldTheHostAlreadyShows()
        {
            var response = new StartSessionResponse(
                "c1",
                ResumeFailureReason:
                    "it belongs to a different working directory ('C:\\repo\\src\\solution'), and this "
                    + "session runs in 'C:\\repo\\elsewhere'.");

            Assert.NotNull(response.ResumeFailureReason);
            Assert.Contains("different working directory", response.ResumeFailureReason!, StringComparison.Ordinal);
        }

        /// <summary>
        /// A conversation recorded before the working directory was persisted answers null, and null
        /// must mean "cannot check" rather than "no mismatch": refusing every pre-existing conversation
        /// on the strength of a field they never had would be a worse bug than the one being fixed.
        /// </summary>
        [Fact]
        public void AConversationWithNoRecordedRootIsNotRefused()
        {
            var request = new StartSessionRequest(
                "kiro", null, AppContext.BaseDirectory, "Prompt", "sess_old", ResumeWorkingDirectory: null);

            Assert.Null(request.ResumeWorkingDirectory);
            Assert.NotNull(request.ResumeConversationId);
        }

        /// <summary>The field is absent unless a resume is actually being asked for.</summary>
        [Fact]
        public void TheFieldIsAbsentOnAFreshStart()
        {
            var request = new StartSessionRequest(
                "kiro", null, AppContext.BaseDirectory, "Prompt", null);

            Assert.Null(request.ResumeWorkingDirectory);
        }

        /// <summary>
        /// Appended last and defaulted, so every existing construction site keeps compiling and keeps
        /// meaning what it did - inserting it mid-record would silently re-bind the arguments after it.
        /// </summary>
        [Fact]
        public void TheFieldIsLastSoOlderCallSitesKeepTheirMeaning()
        {
            var parameters = typeof(StartSessionRequest)
                .GetConstructors()
                .Single(c => c.GetParameters().Length > 1)
                .GetParameters();

            Assert.Equal(nameof(StartSessionRequest.PinnedWorkingDirectory), parameters[^1].Name);
        }

        /// <summary>
        /// Unknown never refuses. A conversation recorded before the working directory was persisted
        /// has no answer to give, and refusing every one of those on the strength of a field they never
        /// carried would be a worse bug than the one this prevents.
        /// </summary>
        [Theory]
        [InlineData(null, @"C:\repo")]
        [InlineData("", @"C:\repo")]
        [InlineData(@"C:\repo", null)]
        [InlineData(@"C:\repo", "")]
        public void AnUnknownRootNeverRefuses(string? conversationRoot, string? sessionRoot)
        {
            Assert.Null(ResumeRootGuard.Refuse(conversationRoot, sessionRoot));
        }

        /// <summary>
        /// The refusal names both directories AND the one remedy that exists.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both directories, because the whole failure is that two of them disagreed - "couldn't open
        /// that conversation" alone leaves the reader with the same puzzle the backend's silence did.
        /// </para>
        /// <para>
        /// <b>And only remedies that exist.</b> So the sentence does not offer a
        /// summary hand-off, which WOULD cross roots cleanly - a summary is text rather than backend
        /// state - because it is not offered once a resume has been refused: the refusal
        /// clears the resume and the session starts fresh. Naming a control the product does not have
        /// is worse than naming none: it sends the reader hunting through the UI for it. Pinned on the
        /// setting key, which is the thing they can actually go and change.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheRefusalNamesBothDirectoriesAndWhatIsInEffect()
        {
            var refusal = ResumeRootGuard.Refuse(
                @"C:\repo\src\solution", @"C:\repo\elsewhere",
                @"agentWorkspaceRoot '../../elsewhere' from C:\repo\.code-wicket\settings.json");

            Assert.NotNull(refusal);
            Assert.Contains(@"C:\repo\src\solution", refusal!, StringComparison.Ordinal);
            Assert.Contains(@"C:\repo\elsewhere", refusal!, StringComparison.Ordinal);
            Assert.Contains("agentWorkspaceRoot", refusal!, StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>And it prescribes nothing, because the remedy is directional.</b>
        /// </summary>
        /// <remarks>
        /// "Remove agentWorkspaceRoot" is right when the conversation predates an override now in
        /// force, and exactly backwards when the conversation was created UNDER one that has since
        /// been removed - the same mismatch reached from the other side, which is how Visual Studio hit it.
        /// Naming what is IN EFFECT reads correctly both ways round; an instruction does not.
        /// </remarks>
        [Fact]
        public void TheRefusalReadsCorrectlyWhenTheOverrideWasRemoved()
        {
            // The conversation ran under an override that is now gone, so the session is back at the
            // solution. Telling the user to REMOVE the override would be nonsense here.
            var refusal = ResumeRootGuard.Refuse(
                @"C:\repo\elsewhere", @"C:\repo\src\solution", "solution folder");

            Assert.NotNull(refusal);
            Assert.Contains(@"C:\repo\elsewhere", refusal!, StringComparison.Ordinal);
            Assert.Contains("solution folder", refusal!, StringComparison.Ordinal);
            Assert.DoesNotContain("remove", refusal!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
