using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the user is actually told about a project's settings file (issue #59).
    /// <para>
    /// The wire half is pinned because of the shape that has shipped before: a field added to the
    /// producing side and the formatter, but not to the DTO, is dropped in transit <b>in silence</b>.
    /// Everything upstream keeps working and the user simply never hears about it - which for the one
    /// signal that says "this repository tried to grant itself permissions" is the worst possible way
    /// to fail.
    /// </para>
    /// </summary>
    public sealed class ProjectSettingsNoticeTests
    {
        [Fact]
        public void TheNoticesSurviveTheStartResponseRoundTrip()
        {
            var notices = new[]
            {
                "Ignored 'allowedCommands' from this project's settings file: ...",
                "Ignored 'agentWorkspaceRoot' in this project's settings file: ...",
            };

            var response = new StartSessionResponse(
                "conv-1",
                WorkingDirectory: @"C:\repo",
                WorkspaceRootReason: "agentWorkspaceRoot '..' from settings",
                ProjectSettingsNotices: notices);

            Assert.Equal(notices, response.ProjectSettingsNotices);
        }

        /// <summary>
        /// Absent rather than empty for a project with no settings file - the overwhelmingly common
        /// case, and the default must cost the wire nothing.
        /// </summary>
        [Fact]
        public void TheFieldDefaultsToAbsent()
        {
            var response = new StartSessionResponse("conv-1");

            Assert.Null(response.ProjectSettingsNotices);
        }

        /// <summary>
        /// The field is positional and last, so every existing construction site keeps compiling and
        /// keeps meaning what it did. Guards against someone inserting it mid-record, which would
        /// silently re-bind the arguments after it.
        /// </summary>
        [Fact]
        public void TheFieldIsLastSoOlderCallSitesKeepTheirMeaning()
        {
            var parameters = typeof(StartSessionResponse)
                .GetConstructors()
                .Single(c => c.GetParameters().Length > 1)
                .GetParameters();

            // The most recently appended field, whatever it is: the pin is on the DISCIPLINE (append,
            // never insert), and a new last field moves this name rather than breaking the rule.
            Assert.Equal(nameof(StartSessionResponse.ReplayedHistoryCount), parameters[^1].Name);
        }

        // --- how they are drawn -------------------------------------------------------------------

        /// <summary>
        /// <b>Every project-settings notice is drawn as an ERROR.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// Found in Visual Studio: the refusal for a nonexistent directory arrived in ordinary muted text,
        /// indistinguishable from the informational "where the agent is running" line directly above
        /// it. The infrastructure was already there (<see cref="NoticeKind"/>) and simply was not
        /// passed, so nothing failed - which is why this now drives the real view-model path rather
        /// than asserting on a hand-built notice, and asserts the <c>IsError</c> the template's trigger
        /// actually binds to.
        /// </para>
        /// <para>
        /// Uniform because the feature is SILENT ON SUCCESS: a file that parsed and applied raises
        /// nothing at all, so there is no informational member of this set to be miscoloured.
        /// <c>EveryKindTheUserSeesIsAProblem</c> fails if a later kind breaks that.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheNoticesAreDrawnAsErrors() => StaTest.Run(() =>
        {
            var store = new FileSessionStore(Path.Combine(
                Path.GetTempPath(), "cwkt-projnotice-" + Guid.NewGuid().ToString("N")));
            var engine = new NoticeEngine(new[]
            {
                "Ignored 'allowedCommands' from this project's settings file: ...",
                "Ignored 'agentWorkspaceRoot' in this project's settings file: '...' does not exist.",
            });

            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                sessionStore: store);

            // The notices are raised at ADOPTION, so a send is what surfaces them - opening the pane
            // is deliberately silent.
            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var notices = vm.Items.OfType<NoticeItemViewModel>()
                .Where(n => n.Text.StartsWith("Ignored ", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(2, notices.Count);
            Assert.All(notices, n => Assert.True(
                n.IsError,
                "a project settings notice is always a problem - the feature says nothing on success - "
                + "so it must be findable in a transcript the reader is scrolling past."));
        }, withDispatcherContext: true);

        /// <summary>
        /// <b>A refused resume is drawn as an error too.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// Same argument as the project-settings notices, and stronger here: the transcript goes on
        /// showing a conversation the agent cannot see, so this is precisely the notice that must
        /// survive being scrolled past. Reported from Visual Studio as still muted - which was an inconsistency
        /// rather than an oversight, since the notices beside it had already been reasoned into being
        /// errors on this exact argument and this one was left alone for being older code.
        /// </para>
        /// <para>
        /// Drives the real view-model path, like its neighbour, so it asserts the <c>IsError</c> the
        /// template's trigger binds to rather than a hand-built notice.
        /// </para>
        /// <para>
        /// The refusal now parks the send on a banner first (issue #268), so the answer is given here
        /// before the notice is read. Which one it is matters: this notice is the <b>no history</b>
        /// outcome, and it is red for exactly the reason above. Answering the other way sends a recap
        /// of those messages, and the same notice is then said plainly — <c>RefusedResumeTests</c>
        /// owns that half.
        /// </para>
        /// </remarks>
        [Fact]
        public void ARefusedResumeIsDrawnAsAnError() => StaTest.Run(() =>
        {
            var store = new FileSessionStore(Path.Combine(
                Path.GetTempPath(), "cwkt-resumenotice-" + Guid.NewGuid().ToString("N")));
            var engine = new NoticeEngine(
                Array.Empty<string>(),
                resumeFailure: "it belongs to a different working directory ('C:\\repo'), and this "
                               + "session runs in 'C:\\elsewhere'.");

            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null),
                sessionStore: store);

            vm.InputText = "hello";
            vm.SendCommand.Execute(null);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            // Nothing was saved here, so there is no transcript to build a recap from and the banner
            // is the one-answer confirmation. Answering it is what releases the send this asserts on.
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var notice = Assert.Single(
                vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Couldn't reload", StringComparison.Ordinal));

            Assert.True(
                notice.IsError,
                "the transcript keeps showing a conversation the agent cannot see, so this notice has "
                + "to survive being scrolled past.");
            Assert.Contains("different working directory", notice.Text, StringComparison.Ordinal);
        }, withDispatcherContext: true);

        /// <summary>An engine whose start response carries project settings notices and nothing else.</summary>
        private sealed class NoticeEngine : IEngineConnection
        {
            private readonly IReadOnlyList<string> _notices;
            private readonly string? _resumeFailure;

            public NoticeEngine(IReadOnlyList<string> notices, string? resumeFailure = null)
            {
                _notices = notices;
                _resumeFailure = resumeFailure;
            }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse(
                    "c1", ResumeFailureReason: _resumeFailure, ProjectSettingsNotices: _notices));

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new PromptResponse("end_turn"));

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new SteerResponse(nameof(Core.SteerOutcome.Injected)));

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
