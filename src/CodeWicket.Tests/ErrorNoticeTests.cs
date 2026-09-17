using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #82: a backend failed with "dispatch error" and the user had nothing else to go on. The
    /// detail explaining it was already crossing the engine wire on <see cref="AgentEventDto.Details"/>
    /// and was dropped in the view-model one step short of the screen. These pin that it now arrives,
    /// survives a restore, and leaves ordinary notices alone.
    /// </summary>
    public sealed class ErrorNoticeTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-error-notice-" + Guid.NewGuid().ToString("N"));

        public ErrorNoticeTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void ANoticeWithoutDetailsOffersNoExpander()
        {
            var notice = new NoticeItemViewModel("MCP server 'aws-mcp' connected.");

            Assert.False(notice.HasDetails);
            Assert.Null(notice.Details);
        }

        /// <summary>Whitespace-only detail is the same as none: an expander that opens onto a blank box
        /// is worse than no expander, because it looks like the detail was lost.</summary>
        [Fact]
        public void WhitespaceDetailsCountAsNoDetails()
        {
            Assert.False(new NoticeItemViewModel("boom", NoticeKind.Error, "   \r\n ").HasDetails);
        }

        /// <summary>
        /// The panel exists to end up in a bug report. A copy that stopped at the one-line message
        /// would recreate #82 one step further along — the user has the detail on screen and still
        /// pastes only the word that says nothing.
        /// </summary>
        [Fact]
        public void CopyingAnErrorTakesTheDetailsWithIt()
        {
            var notice = new NoticeItemViewModel(
                "dispatch error", NoticeKind.Error, "kiro-cli: kiro-cli-chat 2.0.1");

            Assert.Contains("dispatch error", notice.CopyText!);
            Assert.Contains("kiro-cli-chat 2.0.1", notice.CopyText!);
        }

        [Fact]
        public void TheDetailPanelStartsCollapsed()
        {
            var notice = new NoticeItemViewModel("boom", NoticeKind.Error, "detail");

            Assert.False(notice.IsExpanded);
            Assert.Equal("Show details", notice.ExpandToolTip);

            notice.IsExpanded = true;
            Assert.Equal("Hide details", notice.ExpandToolTip);
        }

        /// <summary>
        /// The end-to-end shape, driven through the replay path so it covers persistence too: an error
        /// event's Details must reach the transcript item, and must still be there after the session is
        /// saved and restored. A restored transcript that collapsed back to the bare message would lose
        /// the detail exactly when a user goes looking for it — the day after.
        /// </summary>
        [Fact]
        public void AnErrorEventsDetailsReachTheTranscriptAndSurviveARestore() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_root, "sessions"));
            var workspace = Path.Combine(_root, "Solution");

            var session = new PersistedSession { WorkspaceRootPath = workspace, Title = "Failed" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "build it" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto
                {
                    Type = "error",
                    Message = "dispatch error",
                    Details = "kiro-cli: kiro-cli-chat 2.0.1\nJSON-RPC error code: -32603",
                },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("kiro", null, workspace, "Prompt", null),
                sessionStore: store);

            vm.RestoreMostRecentSession();

            var notice = vm.Items.OfType<NoticeItemViewModel>().Single(n => n.IsError);
            Assert.Equal("dispatch error", notice.Text);
            Assert.True(notice.HasDetails);
            Assert.Contains("kiro-cli-chat 2.0.1", notice.Details!);
            Assert.Contains("-32603", notice.Details!);
        });

        // ---- helpers ------------------------------------------------------------------------------

        private sealed class OfflineEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not summarize.");
        }

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
