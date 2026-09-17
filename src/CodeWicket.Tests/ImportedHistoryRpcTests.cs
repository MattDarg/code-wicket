using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Engine;
using CodeWicket.Ipc;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Carrying an imported conversation across the shell&lt;-&gt;engine boundary (issue #108): the
    /// session start that harvests it, and the paged pull that fetches it.
    /// <para><b>Why a real round trip:</b> the same reason
    /// <see cref="BackendSessionListRpcTests"/> uses one — a DTO that does not survive the formatter
    /// comes back with default fields and no error anywhere, and the engine takes its parameters as a
    /// single object, so a mismatch is a method-not-found rather than a wrong answer. Both are
    /// transport facts.</para>
    /// <para>The pull exists BECAUSE of the transport: a large result deserializes from a NUL-padded
    /// span and throws (the defect <c>SetModelViaConfigOptionAsync</c> documents), so the transcript
    /// may not ride the start response however convenient that would be.</para>
    /// </summary>
    public class ImportedHistoryRpcTests
    {
        [Fact]
        public async Task AnImportedConversationCrossesInPagesAndInOrder()
        {
            using var harness = Harness.Start(new ImportingProvider(Script()));

            var start = await harness.StartAsync(Request(importHistory: true));
            Assert.Equal(4, start.ImportedHistoryCount);
            Assert.False(start.ImportedHistoryTruncated);

            // Two at a time, so an implementation that ignored Offset and answered from the top would
            // return four entries and still be caught by the order assertion below.
            var pulled = await harness.PullAllAsync(pageSize: 2);

            Assert.Equal(4, pulled.Count);
            Assert.Equal(ImportedTurnEntry.UserRole, pulled[0].Role);
            Assert.Equal("toolStart", pulled[1].Event!.Type);
            Assert.Equal("edit", pulled[2].Event!.Type);
            Assert.Equal("toolDone", pulled[3].Event!.Type);
        }

        [Fact]
        public async Task AUserEntryCarriesTextAndAnAgentEntryCarriesItsEvent()
        {
            // The shape the shell's persisted TranscriptEntry takes. An import is a transcript we did
            // not record, so what it produces has to be indistinguishable from one we did — a role
            // that arrived without its payload would replay as a blank bubble.
            using var harness = Harness.Start(new ImportingProvider(Script()));

            await harness.StartAsync(Request(importHistory: true));
            var pulled = await harness.PullAllAsync(pageSize: 10);

            Assert.Equal("Fix the uploader", pulled[0].Text);
            Assert.Null(pulled[0].Event);
            Assert.All(pulled.Skip(1), e =>
            {
                Assert.Equal(ImportedTurnEntry.AgentRole, e.Role);
                Assert.NotNull(e.Event);
                Assert.Null(e.Text);
            });
        }

        [Fact]
        public async Task ConsecutiveUserChunksBecomeOneMessage()
        {
            // ONE message the user sent replays as SEVERAL frames — measured, an image-plus-text
            // message arrives as two user_message_chunks — so without the join an ordinary send comes
            // back as two bubbles the user never typed separately. Joined engine-side so the wire list
            // is 1:1 with the shell's TranscriptEntry and no second rule has to exist over there.
            var split = new[]
            {
                ImportedTurnEntry.User("[image: shot.png]"),
                ImportedTurnEntry.User("fix this"),
                ImportedTurnEntry.Agent(new AgentEvent.AssistantTextDelta("Looking.")),
                ImportedTurnEntry.User("thanks"),
            };
            using var harness = Harness.Start(new ImportingProvider(split));

            var start = await harness.StartAsync(Request(importHistory: true));
            var pulled = await harness.PullAllAsync(pageSize: 10);

            // Three entries, not four: the two chunks join, and the agent entry between the second and
            // third user entry ends the run — joining across it would put a reply above the answer it
            // followed.
            Assert.Equal(3, start.ImportedHistoryCount);
            Assert.Equal(3, pulled.Count);
            Assert.Equal("[image: shot.png]\nfix this", pulled[0].Text);
            Assert.Equal("thanks", pulled[2].Text);
        }

        [Fact]
        public async Task NoImportRequestedReportsNullRatherThanZero()
        {
            // The UsageReport rule on another field: a null means NOT REPORTED, never a value. Zero is
            // its own answer — "asked, and the backend replayed nothing" — and collapsing the two
            // leaves a host unable to tell an import that found an empty conversation from one that
            // was never attempted.
            using var harness = Harness.Start(new ImportingProvider(Script()));

            var start = await harness.StartAsync(Request(importHistory: false));

            Assert.Null(start.ImportedHistoryCount);

            var page = await harness.TakeAsync(new TakeImportedHistoryRequest(0, 10));
            Assert.Empty(page.Entries);
            Assert.Equal(0, page.Total);
        }

        [Fact]
        public async Task AnImportThatFoundNothingReportsZero()
        {
            using var harness = Harness.Start(new ImportingProvider(Array.Empty<ImportedTurnEntry>()));

            var start = await harness.StartAsync(Request(importHistory: true));

            Assert.Equal(0, start.ImportedHistoryCount);
            Assert.False(start.ImportedHistoryTruncated);
        }

        /// <summary>
        /// The replay COUNT crosses beside the import (issue #185), on the same null-versus-zero rule:
        /// an ordinary resume that was answered in full reports how much came back, one answered with
        /// nothing reports zero, and a start that asked for no resume reports nothing at all. A field
        /// that did not survive the formatter would come back null and read as "not reported" — the
        /// one value that makes no claim — so the host's check would silently never fire.
        /// </summary>
        [Fact]
        public async Task AResumedStartReportsHowMuchTheBackendReplayed()
        {
            using var harness = Harness.Start(new ImportingProvider(Script()));

            var start = await harness.StartAsync(Request(importHistory: false));

            Assert.Equal(4, start.ReplayedHistoryCount);
        }

        [Fact]
        public async Task AResumeAnsweredWithNothingReportsZeroNotNull()
        {
            using var harness = Harness.Start(new ImportingProvider(Array.Empty<ImportedTurnEntry>()));

            var start = await harness.StartAsync(Request(importHistory: false));

            Assert.Equal(0, start.ReplayedHistoryCount);
        }

        [Fact]
        public async Task AStartWithNoResumeReportsNoReplayCount()
        {
            using var harness = Harness.Start(new ImportingProvider(Script()));

            var start = await harness.StartAsync(
                new StartSessionRequest("importing", null, @"C:\ws", "Prompt", null));

            Assert.Null(start.ReplayedHistoryCount);
        }

        [Fact]
        public async Task AnOversizeImportIsReportedTruncatedRatherThanCutSilently()
        {
            // A display cap may never quietly cut data (issue #83), and here what is being capped is a
            // whole conversation. The count reports what is actually available to pull, and the flag
            // is what stops "5000 entries" reading as "the conversation was 5000 entries long".
            var huge = Enumerable.Range(0, 5200)
                .Select(i => ImportedTurnEntry.Agent(new AgentEvent.AssistantTextDelta("chunk " + i)))
                .ToArray();
            using var harness = Harness.Start(new ImportingProvider(huge));

            var start = await harness.StartAsync(Request(importHistory: true));

            Assert.Equal(5000, start.ImportedHistoryCount);
            Assert.True(start.ImportedHistoryTruncated);
        }

        [Fact]
        public async Task AFreshSessionDropsThePreviousImport()
        {
            // The import belongs to the conversation that opened it. Held past the next start, a host
            // would be able to pull one session's transcript while holding another's id — and the
            // stale copy would look exactly like a real answer.
            using var harness = Harness.Start(new ImportingProvider(Script()));

            await harness.StartAsync(Request(importHistory: true));
            Assert.Equal(4, (await harness.TakeAsync(new TakeImportedHistoryRequest(0, 10))).Total);

            await harness.StartAsync(Request(importHistory: false));

            var after = await harness.TakeAsync(new TakeImportedHistoryRequest(0, 10));
            Assert.Empty(after.Entries);
            Assert.Equal(0, after.Total);
        }

        [Fact]
        public async Task PagingPastTheEndIsAnEmptyPageAndNotAnError()
        {
            // How a caller's paging loop terminates. Throwing here would make the ordinary end of a
            // read look like a transport failure.
            using var harness = Harness.Start(new ImportingProvider(Script()));
            await harness.StartAsync(Request(importHistory: true));

            var past = await harness.TakeAsync(new TakeImportedHistoryRequest(99, 10));

            Assert.Empty(past.Entries);
            Assert.Equal(4, past.Total);
        }

        [Fact]
        public void AnEntryMapsBothHalvesOntoTheWire()
        {
            // The mapping itself, without the transport. A user entry keeps its text and gains no
            // event; an agent entry keeps its event and gains no text — and the event goes through the
            // SAME ToDto a live turn's does, which is what makes a replayed transcript need no second
            // render path.
            var user = DtoMapping.ToDto(ImportedTurnEntry.User("hello"));
            var agent = DtoMapping.ToDto(
                ImportedTurnEntry.Agent(new AgentEvent.AssistantTextDelta("hi")));

            Assert.Equal("user", user.Role);
            Assert.Equal("hello", user.Text);
            Assert.Null(user.Event);

            Assert.Equal("agent", agent.Role);
            Assert.Null(agent.Text);
            Assert.Equal("text", agent.Event!.Type);
            Assert.Equal("hi", agent.Event.Text);
        }

        private static StartSessionRequest Request(bool importHistory) =>
            new("importing", null, @"C:\ws", "Prompt", "conv-1", importHistory);

        // The four shapes a host renders differently: a prompt, a tool call, an edit and a completion.
        // A single-kind script would pass a mapping that dropped everything but text.
        private static IReadOnlyList<ImportedTurnEntry> Script() => new[]
        {
            ImportedTurnEntry.User("Fix the uploader"),
            ImportedTurnEntry.Agent(new AgentEvent.ToolCallStarted(
                "t1", "Read Uploader.cs", "read", RawInputJson: "{\"path\":\"src/Uploader.cs\"}")),
            ImportedTurnEntry.Agent(new AgentEvent.EditProposed(
                "src/Uploader.cs", "old", "new", ToolCallId: "t1")),
            ImportedTurnEntry.Agent(new AgentEvent.ToolCallCompleted("t1", true, "done")),
        };

        // A backend whose session/load replay is whatever the test hands it. Reporting through
        // IImportedHistoryReport rather than a bespoke hook is the point: that is the interface the
        // real ACP session answers, so the engine's harvest is exercised exactly as it runs.
        private sealed class ImportingProvider : IAgentProvider
        {
            private readonly IReadOnlyList<ImportedTurnEntry> _replay;

            public ImportingProvider(IReadOnlyList<ImportedTurnEntry> replay) => _replay = replay;

            public string ProviderId => "importing";
            public string DisplayName => "Importing backend";
            public IReadOnlyList<ModelInfo> Models { get; } = Array.Empty<ModelInfo>();
            public AgentCapabilities Capabilities => AgentCapabilities.ResumeSession;

            public Task<IAgentSession> StartSessionAsync(
                SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default) =>
                Task.FromResult<IAgentSession>(new Session(
                    options.ResumeConversationId ?? "conv-new",
                    options.ImportHistory ? _replay : Array.Empty<ImportedTurnEntry>(),
                    // Replayed whether or not it was kept (issue #185): the count answers for the
                    // load, the import for what was retained. Null with no resume, as the real
                    // session answers.
                    options.ResumeConversationId is null ? null : _replay.Count));

            private sealed class Session : IAgentSession, IImportedHistoryReport, IResumeFallbackReport
            {
                public Session(string id, IReadOnlyList<ImportedTurnEntry> history, int? replayed)
                {
                    ConversationId = id;
                    ImportedHistory = history;
                    ReplayedHistoryCount = replayed;
                }

                public string ConversationId { get; }
                public IReadOnlyList<ImportedTurnEntry> ImportedHistory { get; }
                public string? ResumeFailureReason => null;
                public int? ReplayedHistoryCount { get; }

                public IAsyncEnumerable<AgentEvent> SendAsync(
                    PromptInput prompt, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException("These tests never prompt.");

                public Task CancelAsync() => Task.CompletedTask;
                public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

                public Task<SteerOutcome> SteerAsync(
                    PromptInput prompt, CancellationToken cancellationToken = default) =>
                    Task.FromResult(SteerOutcome.NotSupported);

                public ValueTask DisposeAsync() => default;
            }
        }

        /// <summary>
        /// An engine and a shell either end of an in-memory duplex pair — the same shape
        /// <see cref="BackendSessionListRpcTests"/> uses, including the tools-only shell half that
        /// answers the one callback <c>IpcIdeServices.CreateAsync</c> makes.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            private readonly JsonRpc _shellRpc;
            private readonly JsonRpc _engineRpc;
            private readonly EngineService _engine;

            private Harness(JsonRpc shellRpc, JsonRpc engineRpc, EngineService engine)
            {
                _shellRpc = shellRpc;
                _engineRpc = engineRpc;
                _engine = engine;
            }

            public static Harness Start(IAgentProvider provider)
            {
                var registry = new AgentProviderRegistry();
                registry.Register(provider);

                var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();

                var shellRpc = new JsonRpc(new NewLineDelimitedMessageHandler(shellEnd, shellEnd, NewFormatter()));
                shellRpc.AddLocalRpcTarget(new ToolsOnlyShell(), new JsonRpcTargetOptions());

                var engineService = new EngineService(registry);
                var engineRpc = new JsonRpc(new NewLineDelimitedMessageHandler(engineEnd, engineEnd, NewFormatter()));
                engineRpc.AddLocalRpcTarget(engineService, new JsonRpcTargetOptions());
                engineService.Rpc = engineRpc;

                engineRpc.StartListening();
                shellRpc.StartListening();

                return new Harness(shellRpc, engineRpc, engineService);
            }

            public Task<StartSessionResponse> StartAsync(StartSessionRequest request) =>
                _shellRpc.InvokeWithParameterObjectAsync<StartSessionResponse>(
                    RpcMethods.StartSession, request);

            public Task<TakeImportedHistoryResponse> TakeAsync(TakeImportedHistoryRequest request) =>
                _shellRpc.InvokeWithParameterObjectAsync<TakeImportedHistoryResponse>(
                    RpcMethods.TakeImportedHistory, request);

            public async Task<List<ImportedEntryDto>> PullAllAsync(int pageSize)
            {
                var all = new List<ImportedEntryDto>();
                while (true)
                {
                    var page = await TakeAsync(new TakeImportedHistoryRequest(all.Count, pageSize));
                    if (page.Entries.Count == 0)
                        return all;
                    all.AddRange(page.Entries);
                    if (all.Count >= page.Total)
                        return all;
                }
            }

            public void Dispose()
            {
                _shellRpc.Dispose();
                _engineRpc.Dispose();
                _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            private static SystemTextJsonFormatter NewFormatter() => new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };

            private sealed class ToolsOnlyShell
            {
                [JsonRpcMethod(RpcMethods.ToolsList)]
                public ToolListResponse ToolsList() => new(Array.Empty<ToolDescriptorDto>());
            }
        }
    }
}
