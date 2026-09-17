using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A display clamp must never corrupt a structured payload (issue #83). <c>AcpMapper</c> caps tool
    /// result text at 2000 chars for the transcript row and the persisted log; the agent's echo of our
    /// <c>run_tests</c> result is not text but DATA the host parses into a card, and the cap was cutting
    /// it mid-string and appending a raw newline — so every test run past 2000 chars threw a
    /// <c>JsonReaderException</c> on a routine path and fell back to the side-channel copy's summary.
    /// <para>
    /// The exemption has to stay narrow in both directions: ordinary output still gets cut (that is what
    /// keeps a row chat-sized), and a payload past the structured bound is cut too rather than letting
    /// anything wearing the marker into the log unbounded.
    /// </para>
    /// </summary>
    public class TestRunPayloadClampTests
    {
        // --- the mapper's contract: what comes out is still parseable ---------------------------------

        /// <summary>
        /// The headline. A real run_tests payload is comfortably past the 2000-char cap (each failure
        /// carries a message and a stack trace), and it must reach the host whole.
        /// </summary>
        [Fact]
        public void ARunTestsPayloadPastTheCapIsNotClamped()
        {
            var payload = TestRunPayload();
            Assert.True(payload.Length > 2000, "the fixture must exceed the cap it is testing");

            var result = MapCompletion(payload);

            Assert.Equal(payload, result);
            // The defect's own signature: the cut left the JSON string open and the marker's newline
            // landed inside it. Parsing is the assertion that matters — the UI does exactly this.
            using var doc = JsonDocument.Parse(result!);
            Assert.Equal("testRun", doc.RootElement.GetProperty("resultKind").GetString());
        }

        /// <summary>
        /// The exemption is for our payload, not for length. Ordinary tool output — a build log, a
        /// directory listing — still gets cut, or a chatty tool buries the transcript and the log.
        /// </summary>
        [Fact]
        public void OrdinaryOutputPastTheCapIsStillClamped()
        {
            var result = MapCompletion(new string('x', 5000));

            Assert.NotNull(result);
            Assert.True(result!.Length < 5000);
            Assert.EndsWith("\n…", result, StringComparison.Ordinal);
        }

        /// <summary>
        /// Text that merely quotes the marker (this repo's own source, read back by the agent) is not a
        /// payload: the match is a strict prefix, so it clamps like any other output.
        /// </summary>
        [Fact]
        public void OutputThatOnlyMentionsTheMarkerIsStillClamped()
        {
            var result = MapCompletion("the tool emits {\"resultKind\":\"testRun\" first: " + new string('x', 5000));

            Assert.NotNull(result);
            Assert.EndsWith("\n…", result!, StringComparison.Ordinal);
        }

        /// <summary>
        /// The exemption is bounded. The payload is bounded at source (50 failures, capped message and
        /// stack trace), so something far past that did not come from the tool we think it did — clamp
        /// it like any other text rather than let arbitrary agent output into the persisted log because
        /// it opened with the right 22 characters. The card still comes from the side-channel copy.
        /// </summary>
        [Fact]
        public void APayloadPastTheStructuredBoundIsClampedToo()
        {
            var payload = TestRunPayload(failures: 400, messageChars: 1000);
            Assert.True(payload.Length > 128 * 1024, "the fixture must exceed the structured bound");

            var result = MapCompletion(payload);

            Assert.NotNull(result);
            Assert.EndsWith("\n…", result!, StringComparison.Ordinal);
        }

        // --- end to end: the mapper's output builds the card ------------------------------------------

        /// <summary>
        /// The echo ALONE must still PARSE. Deliberately no side-channel delivery first: that copy is
        /// full-fidelity by construction and would summarize the run whatever state the echo arrived in,
        /// hiding the corruption this test exists for — the fallback was why the defect stayed invisible
        /// in the first place.
        /// <para>
        /// The assertion is the row's summary rather than a card, because an echo is the AGENT's copy
        /// and no longer builds one (<c>ChatViewModel.IsHostsOwnDelivery</c>). It is not a weaker check:
        /// the summary is written from the parsed payload's own counts, so a clamped echo cannot produce
        /// it — the parse throws and the row falls back to the side-channel's remembered summary, which
        /// on this test is deliberately absent. "20 tests" on the row means the whole payload arrived.
        /// </para>
        /// </summary>
        [Fact]
        public void TheEchoedPayloadAloneStillParsesIntoTheRow() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t2", Title = "run_tests", Kind = "other" });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t2", Success = false, Message = MapCompletion(TestRunPayload()),
                // As the engine stamps it: this is the backend's echo, not the host's own delivery.
                HostAuthored = false,
            });
            DrainDispatcher();

            // The row says what happened instead of showing the JSON, and says it from the parsed counts.
            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Contains("20 tests", row.OutputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("12 failed", row.OutputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("resultKind", row.OutputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// The backstop still stands. A payload that genuinely doesn't parse — past the structured
        /// bound, or an old session log replaying a cut saved before the fix — must leave the row with
        /// the summary the side-channel copy remembered, never with broken JSON, and never throw.
        /// </summary>
        [Fact]
        public void AMalformedEchoFallsBackToTheSideChannelSummary() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            var payload = TestRunPayload();

            // The real order: the side-channel fires first, on the synthetic id, with no row to fill —
            // and carrying the flag the shell stamps on its own tool result, which is what makes it the
            // copy a card may be built from.
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "cwkt-testrun", Success = false, Message = payload,
                HostAuthored = true,
            });
            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "t2", Title = "run_tests", Kind = "other" });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "t2", Success = false, Message = payload.Substring(0, 120),
                HostAuthored = false,
            });
            DrainDispatcher();

            var card = Assert.Single(vm.Items.OfType<TestRunResultItemViewModel>());
            Assert.Equal(20, card.Total);
            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Contains("20 tests", row.OutputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("resultKind", row.OutputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        // --- fixtures --------------------------------------------------------------------------------

        /// <summary>
        /// A completed tool_call_update carrying <paramref name="resultText"/> as a string rawOutput
        /// (Claude's shape — the backend that echoes our MCP tool results back), mapped to the result
        /// text the host receives.
        /// </summary>
        private static string? MapCompletion(string resultText)
        {
            var frame = JsonSerializer.Serialize(new
            {
                sessionUpdate = "tool_call_update",
                toolCallId = "t2",
                status = "completed",
                rawOutput = resultText,
            });
            var update = JsonDocument.Parse(frame).RootElement.Clone();
            return AcpMapper.Map(update).OfType<AgentEvent.ToolCallCompleted>().Single().ResultText;
        }

        /// <summary>
        /// The run_tests result as <c>VsToolCatalog</c> serializes it: the marker FIRST (every reader
        /// keys off the prefix), then the counts, then the failing tests with their locations.
        /// </summary>
        private static string TestRunPayload(int failures = 12, int messageChars = 300)
        {
            var rows = Enumerable.Range(1, failures).Select(i => new
            {
                name = $"Ns.Tests.SomeFixture.Case{i}",
                outcome = "Failed",
                message = "Assert.Equal() Failure: " + new string('x', messageChars),
                stackTrace = $"   at Ns.Tests.SomeFixture.Case{i}() in C:\\ws\\Tests.cs:line {i}",
                file = "C:\\ws\\Tests.cs",
                line = i,
            }).ToArray();

            return JsonSerializer.Serialize(new
            {
                resultKind = "testRun",
                succeeded = false,
                total = 20,
                passed = 20 - failures,
                failed = failures,
                skipped = 0,
                failureCount = failures,
                truncatedFailures = false,
                failures = rows,
                resultsFile = "C:\\ws\\TestResults\\run-" + failures + ".trx",
            });
        }

        private static ChatViewModel NewViewModel(StubEngine engine) => new(
            engine,
            new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>Drives the view-model from the event stream; never opens a backend session.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never open a session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
