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
    /// A conversation whose working directory has moved is ASKED about before anything is recorded
    /// (issues #59, #185), and each answer does what it says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A unilateral fork — a new conversation on every mismatch (#183) — rests on one observation in
    /// Visual Studio that was about an unknown id rather than the root (re-measured 2026-09-12,
    /// <c>Console resume-cross-root</c> / <c>resume-unknown-id</c>). It forks conversations that would
    /// have resumed, and the user who opened THAT conversation on purpose gets a new one. The fork survives as the "Start fresh" answer; the two
    /// outcomes it could not offer — run it where its history lives, or carry a recap here — are the
    /// other two.
    /// </para>
    /// <para>
    /// The pin on the ask is the same one the other resume banners carry: nothing is prompted and
    /// nothing is recorded while the banner is up, because an answer given afterwards could not take
    /// a sent message back. And it alone has a Cancel, because it alone runs before the commit.
    /// </para>
    /// </remarks>
    public sealed class ResumeRootPrecheckTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-precheck-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private FileSessionStore Store() => new FileSessionStore(_root);

        /// <summary>A conversation already saved under <paramref name="conversationRoot"/>.</summary>
        private PersistedSession Saved(FileSessionStore store, string conversationRoot)
        {
            var session = new PersistedSession
            {
                WorkspaceRootPath = _root,
                AgentWorkingDirectory = conversationRoot,
                ConversationId = "sess_original",
                ProviderId = "fake",
                Title = "Earlier work",
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "the earlier message" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "assistant",
                Event = new AgentEventDto { Type = "text", Text = "the earlier answer" },
            });
            store.Save(session);
            return session;
        }

        private ChatViewModel Restored(IEngineConnection engine, FileSessionStore store)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.RestoreMostRecentSession();
            return vm;
        }

        private static void Send(ChatViewModel vm, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static string Notices(ChatViewModel vm) =>
            string.Join(" | ", vm.Items.OfType<NoticeItemViewModel>().Select(n => n.Text))
            + (vm.HasPendingResume ? " [banner: " + vm.PendingResume!.Heading + "]" : string.Empty)
            + " [input: '" + vm.InputText + "']";

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        // --- the ask ------------------------------------------------------------------------------

        /// <summary>
        /// The defect the fork left: the user is told, not asked. Now the send parks on a banner with
        /// the earlier conversation still on screen, nothing started and nothing recorded — the old
        /// conversation is untouched on disk, which is what keeps every answer reversible.
        /// </summary>
        [Fact]
        public void AMovedRootAsksBeforeAnythingIsRecorded() => RunSta(() =>
        {
            var store = Store();
            var saved = Saved(store, @"C:\repo\src\solution");
            var engine = new RootQueryEngine(@"C:\repo\elsewhere");
            var vm = Restored(engine, store);

            Send(vm, "carry on");

            Assert.True(vm.HasPendingResume);
            Assert.Equal("This conversation ran in a different directory", vm.PendingResume!.Heading);
            Assert.Contains(@"C:\repo\src\solution", vm.PendingResume.Detail, StringComparison.Ordinal);
            Assert.Contains(@"C:\repo\elsewhere", vm.PendingResume.Detail, StringComparison.Ordinal);
            Assert.Contains("test", vm.PendingResume.Detail, StringComparison.Ordinal); // the stub's reason
            Assert.True(vm.PendingResume.AllowFull);
            Assert.True(vm.PendingResume.AllowSummary);
            Assert.True(vm.PendingResume.AllowFresh);
            Assert.True(vm.PendingResume.AllowCancel);
            Assert.Equal("Open it where its history lives", vm.PendingResume.FullLabel);
            // Every button explains itself; the recap one was the one nobody had thought about.
            Assert.False(string.IsNullOrEmpty(vm.PendingResume.FullTooltip));
            Assert.False(string.IsNullOrEmpty(vm.PendingResume.SummaryTooltip));
            Assert.False(string.IsNullOrEmpty(vm.PendingResume.FreshTooltip));

            Assert.Equal(0, engine.StartCount);
            Assert.Empty(engine.Prompts);
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("the earlier message", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("carry on", StringComparison.Ordinal));

            var reloaded = store.Load(_root, saved.Id);
            Assert.Equal(2, reloaded!.Log.Count);
        });

        /// <summary>
        /// "Open it where its history lives": the reload goes out PINNED to the recorded directory,
        /// in the same conversation. The pin rides the request beside the resume id — the engine
        /// compares against it instead of against where the marker walk would have put the agent.
        /// </summary>
        [Fact]
        public void OpeningItWhereItsHistoryLivesPinsTheReload() => RunSta(() =>
        {
            var store = Store();
            var saved = Saved(store, @"C:\repo\src\solution");
            var engine = new RootQueryEngine(@"C:\repo\elsewhere");
            var vm = Restored(engine, store);

            Send(vm, "carry on");
            vm.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Equal("sess_original", engine.LastStart!.ResumeConversationId);
            Assert.Equal(@"C:\repo\src\solution", engine.LastStart.PinnedWorkingDirectory);
            Assert.Single(engine.Prompts);

            // Same conversation, with the earlier messages still above the new one.
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("the earlier message", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("carry on", StringComparison.Ordinal));

            // For this OPEN only: nothing about the choice reaches the file - asserted on the bytes,
            // since the store may hand the same loaded object back.
            Assert.All(
                Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories),
                file => Assert.DoesNotContain("PinWorkingDirectoryThisOpen", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
        });

        /// <summary>
        /// A reopen asks again. A remembered pin was built first and reverted (user, 2026-09-12): it
        /// pinned every later reopen with no way back but editing the session file — the remedy
        /// issue #185 was filed against — and for a small conversation, where no banner precedes
        /// the full reload, it took the summary and fresh routes away entirely.
        /// </summary>
        [Fact]
        public void AReopenAsksAgainRatherThanRememberingThePin() => RunSta(() =>
        {
            var store = Store();
            Saved(store, @"C:\repo\src\solution");
            var first = Restored(new RootQueryEngine(@"C:\repo\elsewhere"), store);
            Send(first, "carry on");
            first.PendingResume!.ResumeFullCommand.Execute(null);
            Drain();

            var engine = new RootQueryEngine(@"C:\repo\elsewhere");
            var reopened = Restored(engine, store);
            Send(reopened, "and again");

            Assert.True(reopened.HasPendingResume);
            Assert.Equal("This conversation ran in a different directory", reopened.PendingResume!.Heading);
            Assert.Equal(0, engine.StartCount);
        });

        /// <summary>
        /// "Resume from summary": no reload is attempted at all — the session starts fresh in the new
        /// directory and the message carries a recap of the conversation above it, re-rooted by issue
        /// #184's origin line. The conversation continues in place, as a cross-backend summary does.
        /// </summary>
        [Fact]
        public void ResumingFromASummaryCarriesARecapHereInTheSameConversation() => RunSta(() =>
        {
            var store = Store();
            Saved(store, @"C:\repo\src\solution");
            var engine = new RootQueryEngine(@"C:\repo\elsewhere");
            var vm = Restored(engine, store);

            Send(vm, "carry on");
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();

            Assert.True(engine.LastStart is not null, "no session started; pane says: " + Notices(vm));
            Assert.Null(engine.LastStart!.ResumeConversationId);
            Assert.Null(engine.LastStart.PinnedWorkingDirectory);
            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains("conversation-summary", prompt, StringComparison.Ordinal);
            Assert.Contains(@"working directory was C:\repo\src\solution", prompt, StringComparison.Ordinal);
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("the earlier message", StringComparison.Ordinal));
        });

        /// <summary>
        /// "Start fresh": the #183 fork, unchanged — a new conversation, the notice naming both
        /// directories and what is in effect, and the old conversation left alone on disk.
        /// </summary>
        [Fact]
        public void StartingFreshForksAndLeavesTheOldConversationAlone() => RunSta(() =>
        {
            var store = Store();
            var saved = Saved(store, @"C:\repo\src\solution");
            var engine = new RootQueryEngine(@"C:\repo\elsewhere");
            var vm = Restored(engine, store);

            Send(vm, "carry on");
            vm.PendingResume!.ResumeFreshCommand.Execute(null);
            Drain();

            Assert.True(engine.LastStart is not null, "no session started; pane says: " + Notices(vm));
            Assert.Null(engine.LastStart!.ResumeConversationId);
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("the earlier message", StringComparison.Ordinal));
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("carry on", StringComparison.Ordinal));

            var notice = Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Started a new conversation", StringComparison.Ordinal));
            Assert.True(notice.IsError);
            Assert.Contains(@"C:\repo\src\solution", notice.Text, StringComparison.Ordinal);
            Assert.Contains(@"C:\repo\elsewhere", notice.Text, StringComparison.Ordinal);
            Assert.Contains("test", notice.Text, StringComparison.Ordinal);
            Assert.Contains("Earlier work", notice.Text, StringComparison.Ordinal);

            var reloaded = store.Load(_root, saved.Id);
            Assert.Equal("sess_original", reloaded!.ConversationId);
            Assert.Equal(@"C:\repo\src\solution", reloaded.AgentWorkingDirectory);
            Assert.Contains(reloaded.Log, e => e.Text == "the earlier message");
        });

        /// <summary>Cancel: the message goes back to the composer and nothing has happened.</summary>
        [Fact]
        public void CancelPutsTheMessageBackAndStartsNothing() => RunSta(() =>
        {
            var store = Store();
            Saved(store, @"C:\repo\src\solution");
            var engine = new RootQueryEngine(@"C:\repo\elsewhere");
            var vm = Restored(engine, store);

            Send(vm, "carry on");
            vm.PendingResume!.CancelCommand.Execute(null);
            Drain();

            Assert.False(vm.HasPendingResume);
            Assert.Equal("carry on", vm.InputText);
            Assert.Equal(0, engine.StartCount);
        });

        // --- the cases that must NOT ask ----------------------------------------------------------

        /// <summary>
        /// Matching roots resume normally. Without this the ask would fire on every resume and the
        /// feature would be a nag rather than a guard.
        /// </summary>
        [Fact]
        public void AMatchingRootResumesWithoutAsking() => RunSta(() =>
        {
            var store = Store();
            Saved(store, @"C:\repo\src\solution");
            var engine = new RootQueryEngine(@"C:\repo\src\solution");
            var vm = Restored(engine, store);

            Send(vm, "carry on");

            Assert.False(vm.HasPendingResume);
            Assert.Equal("sess_original", engine.LastStart!.ResumeConversationId);
            Assert.Null(engine.LastStart.PinnedWorkingDirectory);
        });

        /// <summary>
        /// Spelling must not ask. The recorded root is read from a file and the resolved one comes
        /// off the wire, so the two arrive by different routes.
        /// </summary>
        [Fact]
        public void ADifferentSpellingOfTheSameRootDoesNotAsk() => RunSta(() =>
        {
            var store = Store();
            Saved(store, @"C:\repo\src\solution");
            var vm = Restored(new RootQueryEngine(@"C:\repo\src\solution\"), store);

            Send(vm, "carry on");

            Assert.False(vm.HasPendingResume);
        });

        /// <summary>
        /// A conversation recorded before the working directory was persisted answers null, and null
        /// is "cannot check" rather than "moved" - asking every one of those would be a worse bug than
        /// the one this prevents.
        /// </summary>
        [Fact]
        public void AConversationWithNoRecordedRootDoesNotAsk() => RunSta(() =>
        {
            var store = Store();
            var saved = Saved(store, @"C:\repo\src\solution");
            saved.AgentWorkingDirectory = null;
            store.Save(saved);
            var vm = Restored(new RootQueryEngine(@"C:\repo\elsewhere"), store);

            Send(vm, "carry on");

            Assert.False(vm.HasPendingResume);
        });

        /// <summary>
        /// An engine that cannot answer the question degrades to the old behaviour rather than asking
        /// on a guess - the engine's own guard still refuses the resume one step later.
        /// </summary>
        [Fact]
        public void AnEngineThatCannotBeAskedDoesNotAsk() => RunSta(() =>
        {
            var store = Store();
            Saved(store, @"C:\repo\src\solution");
            var vm = Restored(new SilentEngine(), store);

            Send(vm, "carry on");

            Assert.False(vm.HasPendingResume);
        });

        // --- doubles --------------------------------------------------------------------------------

        /// <summary>An engine that answers the root query with a fixed directory.</summary>
        private sealed class RootQueryEngine : SilentEngine, IAgentRootQuery
        {
            private readonly string _root;

            public RootQueryEngine(string root) => _root = root;

            public int RootQueries { get; private set; }

            public Task<ResolveAgentRootResponse> ResolveAgentRootAsync(
                ResolveAgentRootRequest request, CancellationToken cancellationToken = default)
            {
                RootQueries++;
                return Task.FromResult(new ResolveAgentRootResponse(_root, "test"));
            }
        }

        /// <summary>An engine that does NOT implement the query - the degradation case.</summary>
        private class SilentEngine : IEngineConnection
        {
            public List<string> Prompts { get; } = new();

            public int StartCount { get; private set; }

            public StartSessionRequest? LastStart { get; private set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    // Resumable, or no full reload is ever decided and the pre-check is moot.
                    new ProviderInfoDto(
                        "fake", "Fake", new List<ModelInfoDto>(), new List<string> { "ResumeSession" }),
                }));

            // A reload that works: the requested id comes back with the conversation replayed, so
            // the post-hoc check (issue #185) has nothing to say and the ask under test here is the
            // only banner in play.
            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
            {
                StartCount++;
                LastStart = request;
                return Task.FromResult(new StartSessionResponse(
                    request.ResumeConversationId ?? "c1",
                    ReplayedHistoryCount: request.ResumeConversationId is null ? null : 2));
            }

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
            {
                Prompts.Add(text);
                return Task.FromResult(new PromptResponse("end_turn"));
            }

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
                => Task.FromResult(new SummarizeResponse("the earlier work, recapped"));
        }
    }
}
