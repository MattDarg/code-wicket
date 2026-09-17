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
    /// The picker-change notice tells the truth about both of its halves (observed wrong live,
    /// 2026-09-12, on a Claude→Kiro flip mid-conversation).
    ///
    /// <para><b>The label half:</b> a provider flip repopulates the model picker to the new backend's
    /// default BEFORE the notice is built, so a label that prefers the model announces every backend
    /// switch as a model switch — "Switched to auto" over a flip whose whole point was Kiro. The
    /// salient fact, stated wrongly, at the one moment the user is checking the flip did what they
    /// meant.</para>
    ///
    /// <para><b>The suffix half:</b> "your next message starts a new session" was fixed text, and over
    /// a conversation with content it contradicts what the send actually does — offer the recap
    /// hand-off (or silently reload). A user reading it concluded no resume would be offered. The
    /// suffix now comes from the same <c>ResumeDecider</c> decision the send will make, so the notice
    /// and the banner cannot disagree; these tests pin both the derivation and the wiring.</para>
    /// </summary>
    public sealed class SwitchNoticeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-switch-notice-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        // ---- the wording, per decision -------------------------------------------------------

        [Fact]
        public void AFreshStartSaysNewSessionAndPromisesNothing()
        {
            var text = ChatViewModel.SwitchedSelectionNotice("Kiro", ResumeDecision.ProceedFresh);

            Assert.Equal("Switched to Kiro — your next message starts a new session.", text);
        }

        [Fact]
        public void ASilentFullReloadSaysTheConversationContinues()
        {
            var text = ChatViewModel.SwitchedSelectionNotice("Fast", ResumeDecision.SilentFull);

            Assert.Contains("continues this conversation", text, StringComparison.Ordinal);
        }

        [Fact]
        public void AFullOrSummaryChoiceSaysTheAskIsComing()
        {
            var text = ChatViewModel.SwitchedSelectionNotice("Kiro", ResumeDecision.PromptFullOrSummary);

            Assert.Contains("will ask", text, StringComparison.Ordinal);
            Assert.Contains("recap", text, StringComparison.Ordinal);
        }

        [Fact]
        public void ASummaryOnlyChoiceSaysTheRecapAskIsComing()
        {
            var text = ChatViewModel.SwitchedSelectionNotice("Kiro", ResumeDecision.PromptSummaryOnly);

            Assert.Contains("will ask", text, StringComparison.Ordinal);
            Assert.Contains("recap of this conversation", text, StringComparison.Ordinal);
        }

        // ---- the wiring ------------------------------------------------------------------------

        /// <summary>
        /// The reported shape: a backend flip announced as "Switched to auto". The selected MODEL is
        /// deliberately non-null here, because with it null the old preference order degrades to the
        /// provider name by accident and the regression is invisible.
        /// </summary>
        [Fact]
        public void ABackendSwitchNamesTheBackendNotTheModel() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            vm.SelectedModel = vm.SelectedProvider!.Models.Single();
            Drain();

            PromptWithReply(vm, engine, "which fruit?");

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            var notice = SwitchNotice(vm);
            Assert.Contains("Switched to Kiro", notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Opus", notice.Text, StringComparison.Ordinal);
        });

        /// <summary>
        /// A cross-backend flip over a conversation the agent replied to: the send will offer the
        /// recap hand-off, so the notice must say the ask is coming rather than claim a bare new
        /// session — the exact misreading observed live.
        /// </summary>
        [Fact]
        public void ACrossBackendSwitchSaysTheRecapChoiceIsComing() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            PromptWithReply(vm, engine, "which fruit?");

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            var notice = SwitchNotice(vm);
            Assert.Contains("will ask", notice.Text, StringComparison.Ordinal);
            Assert.Contains("recap", notice.Text, StringComparison.Ordinal);
        });

        /// <summary>
        /// And the negative: a conversation the agent never answered has nothing a recap could carry
        /// (the send proceeds fresh, silently — ResumeDecider's own rule), so promising an ask here
        /// would be the same defect mirrored.
        /// </summary>
        [Fact]
        public void ASwitchOverAnUnansweredConversationPromisesNoRecap() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "claude-code");
            Drain();

            Prompt(vm, engine, "hello?"); // turn completes with no assistant text

            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            Drain();

            var notice = SwitchNotice(vm);
            Assert.Contains("starts a new session", notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("recap", notice.Text, StringComparison.Ordinal);
        });

        /// <summary>
        /// The model-fallback route (same backend, no live model selection): the label correctly
        /// names the MODEL — that is what the user changed — and a small natively-resumable
        /// conversation says it continues, because the send will silently reload it in full.
        /// </summary>
        [Fact]
        public void AModelFallbackNamesTheModelAndSaysTheConversationContinues() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            AddProviders(vm);
            vm.SelectedProvider = vm.Providers.Single(p => p.Id == "kiro");
            vm.SelectedModel = vm.SelectedProvider!.Models.First(m => m.Id == "auto");
            Drain();

            PromptWithReply(vm, engine, "which fruit?");

            vm.SelectedModel = vm.SelectedProvider!.Models.First(m => m.Id == "fast");
            Drain();

            var notice = SwitchNotice(vm);
            Assert.Contains("Switched to Fast", notice.Text, StringComparison.Ordinal);
            Assert.Contains("continues this conversation", notice.Text, StringComparison.Ordinal);
        });

        // ---- helpers --------------------------------------------------------------------------

        private static NoticeItemViewModel SwitchNotice(ChatViewModel vm) =>
            Assert.Single(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.StartsWith("Switched to", StringComparison.Ordinal));

        /// <summary>Kiro resumes and has two models but no live model selection (the fallback route);
        /// Claude Code carries one model so the label bug has a model name to leak.</summary>
        private static void AddProviders(ChatViewModel vm)
        {
            vm.Providers.Add(new ProviderItemViewModel(
                "kiro", "Kiro",
                new List<ModelItemViewModel> { new("auto", "auto"), new("fast", "Fast") },
                supportsResume: true, supportsModelSelection: false));
            vm.Providers.Add(new ProviderItemViewModel(
                "claude-code", "Claude Code",
                new List<ModelItemViewModel> { new("opus", "Opus") },
                supportsResume: true, supportsModelSelection: false));
        }

        private static void Prompt(ChatViewModel vm, StubEngine engine, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
            engine.CompleteTurn();
            Drain();
        }

        private static void PromptWithReply(ChatViewModel vm, StubEngine engine, string text)
        {
            vm.InputText = text;
            vm.SendCommand.Execute(null);
            Drain();
            engine.Raise(new AgentEventDto { Type = "text", Text = "the fruit was a mango" });
            Drain();
            engine.CompleteTurn();
            Drain();
        }

        private ChatViewModel NewViewModel(StubEngine engine) => new(
            engine,
            new StartSessionRequest("fake", null, _dir, "Prompt", null),
            sessionStore: new FileSessionStore(_dir));

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        private sealed class StubEngine : IEngineConnection
        {
            private TaskCompletionSource<PromptResponse>? _turn;

            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            public void CompleteTurn() => _turn?.TrySetResult(new PromptResponse("end_turn"));

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse(request.ResumeConversationId ?? "conv-new"));

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
            {
                _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _turn.Task;
            }

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ListBackendSessionsResponse(
                    false, "not supported", Array.Empty<BackendSessionDto>()));

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("These tests import nothing.");

            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("These tests never summarize.");
        }
    }
}
