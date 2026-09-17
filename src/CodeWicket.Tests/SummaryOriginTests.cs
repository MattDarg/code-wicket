using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Engine;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A summary hand-off states which directory its paths were written against (issue #184).
    ///
    /// <para>The summary route is what carries a conversation onto a DIFFERENT backend, and the two
    /// backends can root differently for the same solution: Kiro widens to a repo-root
    /// <c>.kiro</c>, Claude Code deliberately never widens (issue #54). The summarizer runs at the
    /// TARGET provider's root while reading a transcript written under the SOURCE's, and a relative
    /// path carried into the recap verbatim then resolves against the wrong directory — silently,
    /// because in one direction the wrong path can EXIST, and a write creates it and reports clean.</para>
    ///
    /// <para>So the origin travels at three points, and each has its own test: the summarizer is told
    /// it and asked for absolute paths (upstream, where the whole transcript is still available); the
    /// recap block names it as the fallback for a path the model misses; and the request carries the
    /// PRIOR conversation's own agent root rather than the solution root — the wiring test, which
    /// pins the one distinction this issue is about, because the two values are identical on every
    /// machine where the backend never widened and the bug is invisible there by construction.</para>
    /// </summary>
    public sealed class SummaryOriginTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-summary-origin-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// The summarizer's half: told the origin, asked for absolute output. Absolute rather than
        /// re-based, because re-basing asks a model to do relative arithmetic between two
        /// directories while an absolute path is correct in any cwd. Said BEFORE the fenced data,
        /// like the fence sentence itself — an instruction after the data is an instruction the
        /// model reads having already formed its answer.
        /// </summary>
        [Fact]
        public void TheSummarizerIsToldTheTranscriptsRootAndAskedForAbsolutePaths()
        {
            var prompt = EngineService.BuildSummarizationPrompt(
                "User: why is Pages/Index.razor failing?", @"C:\repo");

            Assert.Contains(@"working directory C:\repo", prompt, StringComparison.Ordinal);
            Assert.Contains("absolute path", prompt, StringComparison.Ordinal);
            Assert.InRange(
                prompt.IndexOf(@"C:\repo", StringComparison.Ordinal),
                0, prompt.IndexOf("Pages/Index.razor", StringComparison.Ordinal));
        }

        /// <summary>
        /// No known root, no sentence about one. A host that cannot say where the transcript was
        /// written must not have the prompt claim a directory anyway — silence degrades to the old
        /// behaviour; a wrong claim is the bug this exists to fix, restated confidently.
        /// </summary>
        [Fact]
        public void NoKnownRootAddsNothingToTheSummarizersInstruction()
        {
            var prompt = EngineService.BuildSummarizationPrompt("User: hello", null);

            Assert.DoesNotContain("working directory", prompt, StringComparison.Ordinal);
        }

        /// <summary>
        /// The recap's half: the origin line is the FALLBACK for a path the summarizer failed to
        /// make absolute, so it sits with the notice line, before the summary text — read by the
        /// receiving agent before any path it qualifies. And it is host text inside the fence like
        /// the notice line, so the fence property must survive it: the close is still the last
        /// thing in the block.
        /// </summary>
        [Fact]
        public void TheRecapNamesItsOriginBeforeTheSummaryText()
        {
            var block = ChatViewModel.BuildSummaryBlock("we fixed the mapper", @"C:\repo");

            Assert.Contains(@"working directory was C:\repo", block, StringComparison.Ordinal);
            Assert.InRange(
                block.IndexOf(@"C:\repo", StringComparison.Ordinal),
                0, block.IndexOf("we fixed the mapper", StringComparison.Ordinal));

            var open = block.Substring(0, block.IndexOf('>') + 1);
            var close = "</" + open.Substring(1);
            Assert.EndsWith(close, block, StringComparison.Ordinal);
        }

        [Fact]
        public void ANullRootDrawsNoOriginLine()
        {
            var block = ChatViewModel.BuildSummaryBlock("recap", null);

            Assert.DoesNotContain("working directory", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// The wiring, which is where issue #184 actually lived: the request must carry the PRIOR
        /// conversation's recorded agent root (<c>PersistedSession.AgentWorkingDirectory</c>), never
        /// the solution root the session request holds. The two are set to differ here precisely
        /// because on an unwidened install they are equal and a regression to the solution root
        /// passes every assertion — the same reason the recap on the wire is asserted to name that
        /// root, pinning the block's call site and not only the builder.
        /// </summary>
        [Fact]
        public void TheSummaryHandOffCarriesTheConversationsOwnRootNotTheSolutions() => RunSta(() =>
        {
            var agentRoot = Path.Combine(_dir, "agent-root");
            var engine = new RecordingEngine();
            var vm = Resumable(engine, agentRoot);

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            Drain();

            Assert.True(vm.HasPendingResume);
            vm.PendingResume!.ResumeSummaryCommand.Execute(null);
            Drain();

            Assert.NotNull(engine.LastSummarize);
            Assert.Equal(agentRoot, engine.LastSummarize!.SourceWorkingDirectory);
            Assert.Equal(_dir, engine.LastSummarize.WorkspaceRootPath);

            var prompt = Assert.Single(engine.Prompts);
            Assert.Contains("working directory was " + agentRoot, prompt, StringComparison.Ordinal);
        });

        // ---- helpers --------------------------------------------------------------------------

        // A restored conversation big enough for the send-time banner to offer the summary, whose
        // recorded agent root differs from the solution root (the issue #54 layout).
        private ChatViewModel Resumable(RecordingEngine engine, string agentRoot)
        {
            var store = new FileSessionStore(_dir);
            var session = new PersistedSession
            {
                WorkspaceRootPath = _dir,
                ConversationId = "conv-1",
                ProviderId = "fake",
                Title = "Earlier work",
                AgentWorkingDirectory = agentRoot,
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = new string('u', 2500) });
            session.Log.Add(new TranscriptEntry
            {
                Role = "assistant",
                Event = new AgentEventDto { Type = "text", Text = new string('a', 2500) },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _dir, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.RestoreMostRecentSession();
            Drain();
            return vm;
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);

        /// <summary>
        /// A backend whose summarizer SUCCEEDS and records what it was asked — the counterpart of
        /// <c>SummaryResumeFallbackTests</c>' always-failing stub, because the fact under test here
        /// only exists on the path where a recap is actually produced and sent.
        /// </summary>
        private sealed class RecordingEngine : IEngineConnection
        {
            public List<string> Prompts { get; } = new();

            public SummarizeRequest? LastSummarize { get; private set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>
                {
                    new ProviderInfoDto(
                        "fake", "Fake", new List<ModelInfoDto>(), new List<string> { "ResumeSession" }),
                }));

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse(request.ResumeConversationId ?? "conv-new"));

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
            {
                LastSummarize = request;
                return Task.FromResult(new SummarizeResponse("the mapper was fixed in src/Mapper.cs"));
            }
        }
    }
}
