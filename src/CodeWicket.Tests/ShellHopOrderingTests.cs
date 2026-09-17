using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #277: the shell ↔ engine hop had neither half of the issue #33 fix. The engine sends a
    /// turn's <c>shell/onAgentEvent</c> notifications in wire order and awaits each write before it
    /// answers <c>engine/prompt</c>, so the wire is right — and the shell's inbound dispatch could
    /// still hand the response to the host before the last notification ahead of it had been raised.
    /// Seen live as #267: the turn's own error, applied after the prompt's response had cleared
    /// <c>IsBusy</c>, opened the 45-second out-of-turn window over a failure already on screen.
    /// <para>These drive a real <see cref="EngineClient"/> — the real transport, ordered dispatch and
    /// drain barrier — against an in-memory engine, the same shape as <see cref="AcpTurnTailTests"/>
    /// one hop down. Only the far end is faked, because the transport is what is under test.</para>
    /// <para><b>The first case is a race check</b>, so per verification.md its verifier is
    /// <c>run-gates.ps1</c> on a contended machine; a solo run that passes over broken code is not
    /// evidence, only a solo run that fails is.</para>
    /// </summary>
    public class ShellHopOrderingTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A caller holding a response holds every event that preceded it on the wire.
        /// <para><b>Each handler is given a cost, and that is what makes the check able to fail.</b>
        /// Measured 2026-09-12 (<c>prove-check.ps1</c>, both transport injections): with a free
        /// handler this passed over UNORDERED dispatch and over ordered-but-undrained dispatch alike,
        /// 50 turns × 200 events, not one out of place. A sub-microsecond handler is drained by the
        /// first pool thread faster than a second one can wake, so on a quiet machine the response
        /// never overtakes anything — the "two green runs are not evidence" shape verification.md
        /// records for races. In the field the handler does not need to be slow: the #267 capture
        /// lost by 5 ms to pool scheduling alone inside devenv. A quarter-millisecond spin per event
        /// stands in for that scheduling delay, and with it the losing side is deterministic.</para>
        /// </summary>
        [Fact]
        public async Task EveryEventSentBeforeTheResponseIsRaisedBeforeTheResponseReturns()
        {
            const int turns = 20;
            const int eventsPerTurn = 100;
            var handlerCost = TimeSpan.FromMilliseconds(0.25);

            var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();
            using var engine = new FakeEngine(engineEnd);
            engine.OnPrompt = async (id, turn) =>
            {
                for (var i = 0; i < eventsPerTurn; i++)
                    await engine.NotifyTextAsync(FakeEngine.Chunk(turn, i));
                await engine.AnswerPromptAsync(id);
            };
            engine.Start();

            var raised = new List<string>();
            var gate = new object();
            using var client = EngineClient.Attach(shellEnd, shellEnd, new StubIdeServices(Root()));
            client.AgentEvent += ev =>
            {
                // A spin, not a Sleep: Sleep's granularity is the OS timer's (up to 15 ms), which
                // would make this slow without making it any sharper.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.Elapsed < handlerCost)
                    Thread.SpinWait(20);
                lock (gate) raised.Add(ev.Text ?? string.Empty);
            };

            var losses = new List<string>();
            for (var turn = 0; turn < turns; turn++)
            {
                await client.PromptAsync("go");

                // Snapshot the instant the response is in hand: anything raised after this line is
                // exactly the straggler the barrier exists to prevent.
                string[] seen;
                lock (gate) { seen = raised.ToArray(); raised.Clear(); }

                var expected = Enumerable.Range(0, eventsPerTurn).Select(i => FakeEngine.Chunk(turn, i)).ToArray();
                if (!seen.SequenceEqual(expected))
                    losses.Add($"turn {turn}: {seen.Length} of {expected.Length} events had been raised when the response returned"
                               + (seen.Length == expected.Length ? " (and not in wire order)" : string.Empty)
                               + (seen.Length > 0 ? $"; last seen '{seen[seen.Length - 1].Trim()}'" : string.Empty));
            }

            Assert.True(losses.Count == 0, "A response overtook the events before it:\n" + string.Join("\n", losses));
        }

        /// <summary>
        /// The ordered pump runs one handler at a time, so a request handler that blocks would hold
        /// every event behind it — and the shell's handlers are the REAL ones on this hop, not an RPC
        /// proxy: the message-box permission fallback blocks its caller for as long as the dialog is
        /// open, and a tool may do real work before it first yields. Both leave the pump at once, and
        /// this is the re-proof the issue asked for on this hop rather than the assumption: a banner
        /// the user leaves open, or a shellout, must not stall the event stream behind it.
        /// </summary>
        [Theory]
        [InlineData("permission")]
        [InlineData("tool")]
        public async Task ABlockingRequestHandlerDoesNotStallTheEventsBehindIt(string request)
        {
            var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();
            using var engine = new FakeEngine(engineEnd);
            var answered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.OnResponse = (_, result) => answered.TrySetResult(result);
            engine.OnPrompt = async (id, _) =>
            {
                // The request first, then the events behind it, then the turn ends only once the
                // request has been answered — the shape of an agent asking and waiting.
                if (request == "permission")
                    await engine.RequestPermissionAsync();
                else
                    await engine.RequestToolAsync();
                for (var i = 0; i < 3; i++)
                    await engine.NotifyTextAsync(FakeEngine.Chunk(0, i));
                await answered.Task;
                await engine.AnswerPromptAsync(id);
            };
            engine.Start();

            using var release = new ManualResetEventSlim(false);
            var ide = new BlockingIdeServices(new StubIdeServices(Root()), release);
            using var client = EngineClient.Attach(shellEnd, shellEnd, ide);

            var threeArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            client.AgentEvent += _ => { if (Interlocked.Increment(ref count) == 3) threeArrived.TrySetResult(true); };

            var prompt = client.PromptAsync("go");

            // Handler entered — and parked, synchronously, as the worst-case fallback does.
            Assert.True(ide.Entered.Wait(Patience), "the request never reached the shell's handler");
            var arrived = await Task.WhenAny(threeArrived.Task, Task.Delay(Patience));
            Assert.True(arrived == threeArrived.Task,
                $"the events behind a parked {request} request did not arrive while it was open ({count} of 3); the handler is holding the dispatch pump");

            release.Set();
            var done = await Task.WhenAny(prompt, Task.Delay(Patience));
            Assert.True(done == prompt, "the turn did not end after the request was answered");
            Assert.True(answered.Task.IsCompleted, "the engine never received the request's answer");
        }

        private static string Root()
        {
            var root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>
        /// An <see cref="IIdeServices"/> whose permission prompt and tool invocation both BLOCK the
        /// calling thread until released — the message-box shape, deliberately the worst case rather
        /// than an async park, because a handler that blocks is the one that would wedge a pump.
        /// </summary>
        private sealed class BlockingIdeServices : IIdeServices, IPermissionHandler, IToolCatalog
        {
            private readonly ManualResetEventSlim _release;

            public BlockingIdeServices(IIdeServices inner, ManualResetEventSlim release)
            {
                Workspace = inner.Workspace;
                Edits = inner.Edits;
                _release = release;
            }

            public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

            public IWorkspaceContext Workspace { get; }
            public IEditApplier Edits { get; }
            public IToolCatalog Tools => this;
            public IPermissionHandler Permissions => this;

            IReadOnlyList<ToolDescriptor> IToolCatalog.Tools => Array.Empty<ToolDescriptor>();

            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                Entered.Set();
                _release.Wait(cancellationToken);
                return Task.FromResult(new PermissionDecision(request.Options[0].OptionId));
            }

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                Entered.Set();
                _release.Wait(cancellationToken);
                return Task.FromResult(new ToolResult(IsError: false, "{\"ok\":true}"));
            }
        }

        /// <summary>
        /// A minimal newline-delimited JSON-RPC "engine": answers <c>engine/prompt</c> however the
        /// test's <see cref="OnPrompt"/> says, can push notifications and requests at the shell, and
        /// hands the shell's answers to requests back through <see cref="OnResponse"/>. Frames are
        /// written back-to-back with no pause, which is what makes the end-of-turn race observable.
        /// </summary>
        private sealed class FakeEngine : IDisposable
        {
            private readonly Stream _stream;
            private readonly StreamWriter _writer;
            private readonly StreamReader _reader;
            private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
            private Task? _pump;
            private int _turn;
            private int _requestId;

            public FakeEngine(Stream stream)
            {
                _stream = stream;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }

            /// <summary>Given the prompt's request id (raw JSON) and the zero-based turn number.</summary>
            public Func<string, int, Task>? OnPrompt { get; set; }

            /// <summary>Given the id (raw JSON) and result of a response the shell sent to one of our requests.</summary>
            public Action<string, JsonElement>? OnResponse { get; set; }

            public static string Chunk(int turn, int i) => $" t{turn}c{i}";

            public void Start() => _pump = Task.Run(PumpAsync);

            public Task NotifyTextAsync(string text) =>
                SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"" + RpcMethods.OnAgentEvent + "\",\"params\":{\"type\":\"text\",\"text\":\""
                          + text + "\"}}");

            public Task AnswerPromptAsync(string id) =>
                SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"stopReason\":\"end_turn\"}}}}");

            public Task RequestPermissionAsync() =>
                SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"req-" + Interlocked.Increment(ref _requestId) + "\",\"method\":\""
                          + RpcMethods.PermissionsRequest + "\",\"params\":{\"toolCallId\":\"tc1\",\"title\":\"Write file\","
                          + "\"kind\":\"edit\",\"detail\":null,\"command\":null,"
                          + "\"options\":[{\"optionId\":\"allow\",\"label\":\"Allow\",\"kind\":\"allowOnce\"}]}}");

            public Task RequestToolAsync() =>
                SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"req-" + Interlocked.Increment(ref _requestId) + "\",\"method\":\""
                          + RpcMethods.ToolsInvoke + "\",\"params\":{\"name\":\"run_tests\",\"argumentsJson\":\"{}\"}}");

            private async Task PumpAsync()
            {
                try
                {
                    string? line;
                    while ((line = await _reader.ReadLineAsync()) is not null)
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;

                        if (!root.TryGetProperty("method", out var methodEl))
                        {
                            // The shell answering one of OUR requests. An error answer is surfaced as a
                            // result too, so a handler that threw fails the test loudly rather than by
                            // a wait that never ends.
                            if (id is not null && root.TryGetProperty("result", out var result))
                                OnResponse?.Invoke(id, result.Clone());
                            else if (id is not null && root.TryGetProperty("error", out var error))
                                OnResponse?.Invoke(id, error.Clone());
                            continue;
                        }

                        if (id is null)
                            continue; // a notification from the shell needs no answer

                        switch (methodEl.GetString())
                        {
                            case RpcMethods.Prompt:
                                var turn = _turn++;
                                // Off the read loop: a prompt handler that waits for the shell's answer
                                // to one of our requests would otherwise wait on the very loop that has
                                // to read it. Turns are serialised by the client awaiting each prompt.
                                if (OnPrompt is { } onPrompt)
                                    _ = Task.Run(async () => { try { await onPrompt(id, turn); } catch { /* stream closed */ } });
                                else
                                    await AnswerPromptAsync(id);
                                break;
                            default:
                                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                                break;
                        }
                    }
                }
                catch
                {
                    // The stream closes when the client is disposed; nothing to report.
                }
            }

            private async Task SendAsync(string json)
            {
                await _writeGate.WaitAsync();
                try
                {
                    await _writer.WriteAsync(json);
                    await _writer.WriteAsync('\n');
                    await _writer.FlushAsync();
                }
                finally
                {
                    _writeGate.Release();
                }
            }

            public void Dispose()
            {
                try { _stream.Dispose(); } catch { /* ignore */ }
                try { _pump?.Wait(1000); } catch { /* ignore */ }
            }
        }
    }
}
