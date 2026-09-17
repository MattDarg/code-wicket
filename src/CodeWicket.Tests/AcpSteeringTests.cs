using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Mid-turn steering — ACP's <c>_session/steering</c> extension request (issue #70). The wire
    /// shapes here are taken from the real <c>@agentclientprotocol/claude-agent-acp</c> adapter:
    /// support is advertised as a TOP-LEVEL <c>_meta.steering.supported</c> in <c>initialize</c> (a
    /// sibling of <c>agentCapabilities</c>, not a member of it), and the request answers with an
    /// <c>outcome</c> of <c>injected</c> or <c>startedNewTurn</c>.
    /// </summary>
    public class AcpSteeringTests
    {
        [Fact]
        public async Task SteeringIsDiscoveredFromTheTopLevelInitializeMeta()
        {
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesSteering = true });

            Assert.True(harness.Session.DiscoveredSteering);
        }

        [Fact]
        public async Task AnAgentThatSaysNothingAboutSteeringIsTreatedAsNotSupportingIt()
        {
            // Kiro's shape: it advertises its extension surface explicitly and steering isn't in it.
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesSteering = false });

            Assert.False(harness.Session.DiscoveredSteering);

            // And the guard is real, not just a flag: nothing is put on the wire, so a backend that
            // would answer -32601 is never asked.
            var outcome = await harness.Session.SteerAsync(new PromptInput("go left instead"));

            Assert.Equal(SteerOutcome.NotSupported, outcome);
            Assert.DoesNotContain("_session/steering", harness.Agent.MethodsCalled);
        }

        [Fact]
        public async Task AnInjectedSteerIsSentAsTheExtensionRequestAndReportedAsInjected()
        {
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesSteering = true });

            var outcome = await harness.Session.SteerAsync(new PromptInput("actually, use the other file"));

            Assert.Equal(SteerOutcome.Injected, outcome);
            Assert.Contains("_session/steering", harness.Agent.MethodsCalled);
        }

        [Fact]
        public async Task AnUnrecognisedOutcomeIsReadAsInjectedRatherThanAsAFailure()
        {
            // The protocol's contract is that a steer is never dropped, so on any successful response
            // the one thing we know is that the agent has the message. Guessing "injected" costs a
            // slightly wrong hint; treating it as an error would tell the user their message was lost.
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { AdvertisesSteering = true, SteerOutcomeValue = "somethingNew" });

            Assert.Equal(SteerOutcome.Injected, await harness.Session.SteerAsync(new PromptInput("hi")));
        }

        [Fact]
        public async Task ASteerThatStartsANewTurnIsReportedAsSuch()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { AdvertisesSteering = true, SteerOutcomeValue = "startedNewTurn" });

            Assert.Equal(SteerOutcome.StartedNewTurn, await harness.Session.SteerAsync(new PromptInput("hi")));
        }

        [Fact]
        public async Task TheOutputOfATurnStartedByASteerIsNotDropped()
        {
            // The turn ends between the user pressing Enter and the steer landing, so the backend runs a
            // whole turn we never prompted for. Its updates arrive with no turn channel of ours open.
            //
            // The agent emits the update BEFORE answering the steer, which is the ordering that used to
            // matter: the sink was armed by the steer, so a notification arriving ahead of the response
            // that armed it was lost (issue #33 — a response can overtake the notifications that
            // preceded it). It is now delivered because the sink is unconditional, which is the better
            // fix: there is no window to get wrong rather than a window we time correctly.
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour
            {
                AdvertisesSteering = true,
                SteerOutcomeValue = "startedNewTurn",
                UpdateBeforeSteerResponse = "the new turn's first words",
            });

            var outcome = await harness.Session.SteerAsync(new PromptInput("run the tests too"));

            Assert.Equal(SteerOutcome.StartedNewTurn, outcome);
            await harness.WaitForOutOfTurnTextAsync("the new turn's first words");
        }

        [Fact]
        public async Task TurnLessUpdatesReachTheHostEvenWhenTheSessionHasNeverSteered()
        {
            // This asserted the opposite until 2026-08-01, when the sink was scoped to sessions that had
            // steered. The case that overturned it is a backend answering session/prompt before it has
            // finished working (kirodotdev/Kiro#7724): the turn channel closes, the agent keeps sending,
            // and everything after that point is discarded — while the permission requests for it still
            // arrive, because those are JSON-RPC requests on a separate path. Silence here does not mean
            // "less output", it means approving a file write with no tool row, diff or edit card to read.
            //
            // Note this fake never steers and does not have to: the host's handling of a turn-less event
            // must not depend on how the session got into that state, since a backend bug is exactly the
            // case nobody arms a latch for.
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesSteering = true });

            await harness.Agent.SendUnsolicitedUpdateAsync("output after the turn channel closed");

            await harness.WaitForOutOfTurnTextAsync("output after the turn channel closed");
        }

        private sealed class FakeAgentBehaviour
        {
            /// <summary>Whether initialize carries <c>_meta.steering.supported</c>.</summary>
            public bool AdvertisesSteering { get; init; }

            /// <summary>The <c>outcome</c> string the steer request answers with.</summary>
            public string SteerOutcomeValue { get; init; } = "injected";

            /// <summary>When set, a session/update carrying this text is emitted immediately BEFORE the
            /// steer response, modelling the notification/response reordering of issue #33.</summary>
            public string? UpdateBeforeSteerResponse { get; init; }
        }

        private sealed class Harness : IDisposable
        {
            private readonly string _root;

            private Harness(AcpAgentSession session, FakeAgent agent, string root, List<AgentEvent> outOfTurn)
            {
                Session = session;
                Agent = agent;
                _root = root;
                OutOfTurnEvents = outOfTurn;
            }

            public AcpAgentSession Session { get; }

            public FakeAgent Agent { get; }

            /// <summary>Everything the session routed to <see cref="SessionOptions.OutOfTurnEvents"/>.</summary>
            public List<AgentEvent> OutOfTurnEvents { get; }

            public static async Task<Harness> StartAsync(FakeAgentBehaviour behaviour)
            {
                var (clientStream, serverStream) = FullDuplexStream.CreatePair();
                var agent = new FakeAgent(serverStream, behaviour);
                agent.Start();

                var root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                var outOfTurn = new List<AgentEvent>();
                var session = new AcpAgentSession(
                    new SessionOptions
                    {
                        WorkspaceRootPath = root,
                        OutOfTurnEvents = ev => { lock (outOfTurn) outOfTurn.Add(ev); },
                    },
                    new StubIdeServices(root),
                    new StreamAcpConnection(clientStream));

                try
                {
                    await session.InitializeAsync(CancellationToken.None);
                }
                catch
                {
                    await session.DisposeAsync();
                    agent.Dispose();
                    try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
                    throw;
                }

                return new Harness(session, agent, root, outOfTurn);
            }

            /// <summary>
            /// Waits for a delta carrying <paramref name="text"/> to reach the out-of-turn sink. The
            /// update crosses the inbound dispatch pump, so it need not have arrived by the time the
            /// steer response has.
            /// </summary>
            public async Task WaitForOutOfTurnTextAsync(string text)
            {
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    lock (OutOfTurnEvents)
                    {
                        if (OutOfTurnEvents.Exists(
                                e => e is AgentEvent.AssistantTextDelta d && d.Text.Contains(text)))
                            return;
                    }

                    await Task.Delay(20);
                }

                lock (OutOfTurnEvents)
                    Assert.Fail(
                        $"No out-of-turn delta containing \"{text}\". Saw: "
                        + string.Join(", ", OutOfTurnEvents.ConvertAll(e => e.GetType().Name)));
            }

            public void Dispose()
            {
                try { Session.DisposeAsync().AsTask().Wait(2000); } catch { /* teardown */ }
                Agent.Dispose();
                try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
            }
        }

        private sealed class StreamAcpConnection : IAcpConnection
        {
            private readonly Stream _stream;

            public StreamAcpConnection(Stream duplex) => _stream = duplex;

            public Stream Sending => _stream;

            public Stream Receiving => _stream;

            public void Dispose() => _stream.Dispose();
        }

        /// <summary>
        /// A newline-delimited JSON-RPC agent that advertises (or withholds) steering and answers the
        /// extension request, optionally emitting a session/update ahead of its response.
        /// </summary>
        private sealed class FakeAgent : IDisposable
        {
            private const string SessionId = "s1";

            private readonly Stream _stream;
            private readonly FakeAgentBehaviour _behaviour;
            private readonly StreamWriter _writer;
            private readonly StreamReader _reader;
            private readonly List<string> _methodsCalled = new();
            private Task? _pump;

            public FakeAgent(Stream stream, FakeAgentBehaviour behaviour)
            {
                _stream = stream;
                _behaviour = behaviour;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }

            public IReadOnlyList<string> MethodsCalled
            {
                get { lock (_methodsCalled) return _methodsCalled.ToArray(); }
            }

            public void Start() => _pump = Task.Run(PumpAsync);

            /// <summary>Emits an assistant-text update nobody prompted for.</summary>
            public Task SendUnsolicitedUpdateAsync(string text) => SendUpdateAsync(text);

            private async Task PumpAsync()
            {
                try
                {
                    string? line;
                    while ((line = await _reader.ReadLineAsync()) is not null)
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("method", out var methodEl)
                            || !root.TryGetProperty("id", out var idEl))
                            continue;

                        var method = methodEl.GetString() ?? string.Empty;
                        lock (_methodsCalled)
                            _methodsCalled.Add(method);

                        await HandleAsync(method, idEl.GetRawText()).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // The stream closes when the session is disposed; nothing to report.
                }
            }

            private async Task HandleAsync(string method, string id)
            {
                switch (method)
                {
                    case "initialize":
                        // The real 0.63.0 shape: _meta is a sibling of agentCapabilities, not inside it.
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":1,"
                            + "\"agentCapabilities\":{\"loadSession\":true},\"authMethods\":[]"
                            + (_behaviour.AdvertisesSteering
                                ? ",\"_meta\":{\"steering\":{\"supported\":true}}"
                                : string.Empty)
                            + "}}").ConfigureAwait(false);
                        return;

                    case "session/new":
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"sessionId\":\"{SessionId}\"}}}}")
                            .ConfigureAwait(false);
                        return;

                    case "_session/steering":
                        // Deliberately before the response: the turn's output can precede the outcome.
                        if (_behaviour.UpdateBeforeSteerResponse is { } text)
                            await SendUpdateAsync(text).ConfigureAwait(false);

                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":"
                            + $"{{\"outcome\":\"{_behaviour.SteerOutcomeValue}\"}}}}").ConfigureAwait(false);
                        return;

                    default:
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}").ConfigureAwait(false);
                        return;
                }
            }

            private Task SendUpdateAsync(string text) => SendAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{"
                + $"\"sessionId\":\"{SessionId}\",\"update\":{{\"sessionUpdate\":\"agent_message_chunk\","
                + $"\"content\":{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}}}}}}}");

            private async Task SendAsync(string json)
            {
                await _writer.WriteAsync(json);
                await _writer.WriteAsync('\n');
                await _writer.FlushAsync();
            }

            public void Dispose()
            {
                try { _stream.Dispose(); } catch { /* ignore */ }
                try { _pump?.Wait(1000); } catch { /* ignore */ }
            }
        }
    }
}
