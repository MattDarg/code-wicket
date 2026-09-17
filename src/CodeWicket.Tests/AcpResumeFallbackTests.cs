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
    /// What a session start does when the backend refuses it. Both behaviours here come from one live
    /// failure (Kiro, 2026-07-29): a stored conversation id created by the v3 agent engine was replayed
    /// against v2, which answered <c>-32603 "Internal error"</c> with <c>data: "Failed to start
    /// session: Session not found: sess_…"</c>. The start was fatal, and the word the user got was
    /// "Internal error" — the sentence that said what actually happened rode in <c>data</c> and was
    /// never read.
    /// </summary>
    public class AcpResumeFallbackTests
    {
        [Fact]
        public async Task ARefusedResumeStartsAFreshSessionAndSaysWhy()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { LoadSessionError = "Failed to start session: Session not found: sess_old" },
                resumeConversationId: "sess_old");

            // The whole point: the session is open and usable, on a NEW id.
            Assert.Equal("s1", harness.Session.ConversationId);
            Assert.Contains("session/new", harness.Agent.MethodsCalled);

            // And it says so — the transcript still shows the earlier conversation, so an agent that
            // silently has none of it reads as having forgotten rather than as never having been told.
            var report = Assert.IsAssignableFrom<IResumeFallbackReport>(harness.Session);
            Assert.NotNull(report.ResumeFailureReason);
            Assert.Contains("Session not found", report.ResumeFailureReason!);
        }

        [Fact]
        public async Task ASuccessfulResumeReportsNoFallback()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour(), resumeConversationId: "sess_old");

            Assert.Equal("sess_old", harness.Session.ConversationId);
            Assert.DoesNotContain("session/new", harness.Agent.MethodsCalled);
            Assert.Null(Assert.IsAssignableFrom<IResumeFallbackReport>(harness.Session).ResumeFailureReason);
        }

        [Fact]
        public async Task AFailedStartReportsTheReasonRatherThanTheProtocolWrapper()
        {
            // No resume here, so there is nothing to fall back to and the start genuinely fails. What
            // must not survive is surfacing StreamJsonRpc's "Internal error" on its own.
            var behaviour = new FakeAgentBehaviour { NewSessionError = "kiro-cli is not logged in" };

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Harness.StartAsync(behaviour));

            Assert.Contains("kiro-cli is not logged in", ex.Message);
        }

        [Fact]
        public async Task AnAuthFailureQuotesTheAgentsOwnSignInInstructions()
        {
            // An ACP agent states how to authenticate exactly once, in initialize's authMethods. Kiro's
            // is verbatim below; nothing else in the pipeline knows it, so without this the user is left
            // with "Auth refresh callback failed: You are not logged in" and no idea which login.
            var behaviour = new FakeAgentBehaviour
            {
                AuthMethods =
                    "[{\"id\":\"kiro-login\",\"name\":\"Kiro Login\",\"description\":"
                    + "\"Run 'kiro-cli login' in terminal to authenticate.\"}]",
                PromptError = "Auth refresh callback failed: You are not logged in.",
            };

            using var harness = await Harness.StartAsync(behaviour);

            AgentEvent.SessionError? error = null;
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("hi")))
                if (ev is AgentEvent.SessionError e)
                    error = e;

            Assert.NotNull(error);
            Assert.Contains("You are not logged in", error!.Message);
            Assert.Contains("Run 'kiro-cli login' in terminal", error.Message);
        }

        [Fact]
        public async Task AnOrdinaryFailureIsNotDecoratedWithSignInInstructions()
        {
            // Sending someone to fix their login when the failure was a rate limit is worse than saying
            // nothing, so the auth match is deliberately narrow.
            var behaviour = new FakeAgentBehaviour
            {
                AuthMethods = "[{\"id\":\"kiro-login\",\"description\":\"Run 'kiro-cli login'.\"}]",
                PromptError = "The request was throttled by the service",
            };

            using var harness = await Harness.StartAsync(behaviour);

            AgentEvent.SessionError? error = null;
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("hi")))
                if (ev is AgentEvent.SessionError e)
                    error = e;

            Assert.NotNull(error);
            Assert.Contains("throttled", error!.Message);
            Assert.DoesNotContain("kiro-cli login", error.Message);
        }

        /// <summary>
        /// <c>session/load</c> replays the whole conversation as <c>session/update</c> notifications
        /// before it responds, so a client can rebuild its UI from them. We rebuild ours from our own
        /// append-only log before the call is made, so the backend's copy is duplication — and it lands
        /// while no turn is open, where turn-less routing is deliberately unconditional for steering.
        /// <para>
        /// Emitting it appended the entire conversation to the transcript behind the user's new message
        /// and re-recorded every event: a resumed session's log went 610 entries → 1854 across two
        /// restarts, and the user's own message looked lost under ~200 events of replayed history.
        /// </para>
        /// </summary>
        [Fact]
        public async Task HistoryReplayedByASessionLoadIsNotEmittedAsLiveWork()
        {
            var emitted = new List<AgentEvent>();
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour
                {
                    LoadSessionHistory = new[] { "REPLAYED ONE", "REPLAYED TWO", "REPLAYED THREE" },
                },
                resumeConversationId: "sess_old",
                outOfTurnEvents: ev => { lock (emitted) emitted.Add(ev); });

            // The resume itself still has to work — a guard that suppressed the load would pass the
            // assertion below while breaking the feature.
            Assert.Equal("sess_old", harness.Session.ConversationId);
            Assert.DoesNotContain("session/new", harness.Agent.MethodsCalled);

            List<AgentEvent> seen;
            lock (emitted) seen = new List<AgentEvent>(emitted);
            Assert.True(seen.Count == 0, $"history leaked into the host as {seen.Count} live event(s)");
        }

        /// <summary>
        /// The frames dropped above are COUNTED on the way out (issue #185). A load that reports
        /// success and replays nothing is what Kiro v3 answers for an id it no longer holds (measured
        /// by <c>Console resume-unknown-id</c>, 2026-09-12), and the count is the only fact on this
        /// side of the wire that separates it from a load that worked.
        /// </summary>
        [Fact]
        public async Task ASuccessfulLoadReportsHowMuchOfTheConversationItReplayed()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour
                {
                    LoadSessionHistory = new[] { "REPLAYED ONE", "REPLAYED TWO", "REPLAYED THREE" },
                },
                resumeConversationId: "sess_old");

            Assert.Equal(3, Assert.IsAssignableFrom<IResumeFallbackReport>(harness.Session).ReplayedHistoryCount);
        }

        /// <summary>
        /// Zero, not null: the load was asked for and answered, and nothing came back. Null is reserved
        /// for "no resume was requested", and collapsing the two would hide the silent case behind the
        /// ordinary one — the same rule <c>ImportedHistoryCount</c> already carries.
        /// </summary>
        [Fact]
        public async Task ALoadThatReplaysNothingReportsZeroNotNull()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour(), resumeConversationId: "sess_old");

            var report = Assert.IsAssignableFrom<IResumeFallbackReport>(harness.Session);
            Assert.Null(report.ResumeFailureReason);
            Assert.Equal(0, report.ReplayedHistoryCount);
        }

        [Fact]
        public async Task AStartWithNoResumeReportsNoCount()
        {
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour());

            Assert.Null(Assert.IsAssignableFrom<IResumeFallbackReport>(harness.Session).ReplayedHistoryCount);
        }

        /// <summary>
        /// A load announces the session's commands whether or not it restored anything, so an
        /// announcement inside the window is not history — counted, it would make the empty load read
        /// as a full one, and the check built on the count would never fire on the case it exists for.
        /// </summary>
        [Fact]
        public async Task AnAnnouncementInsideTheLoadWindowIsNotHistory()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { LoadSessionAnnouncesCommands = true },
                resumeConversationId: "sess_old");

            Assert.Equal(0, Assert.IsAssignableFrom<IResumeFallbackReport>(harness.Session).ReplayedHistoryCount);
        }

        /// <summary>
        /// A dropped connection is the one failure whose own text says nothing anyone can act on —
        /// StreamJsonRpc's "The JSON-RPC connection with the remote party was lost before the request
        /// could complete" names neither the agent, nor what happened, nor what to do. That was the
        /// headline of a real failure seen live on 2026-08-08, which is issue #82's defect one layer
        /// along. The protocol text is not discarded: it belongs in the details panel, as evidence.
        /// </summary>
        [Fact]
        public async Task ALostConnectionIsReportedInPlainLanguage()
        {
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { DropConnectionOnPrompt = true });

            AgentEvent.SessionError? error = null;
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("hi")))
                if (ev is AgentEvent.SessionError e)
                    error = e;

            Assert.NotNull(error);
            Assert.Equal("The agent stopped responding. Start a new session to continue.", error!.Message);
            // The raw protocol text survives where it is useful rather than alarming.
            Assert.Contains("ConnectionLostException", error.Details!);
        }

        /// <summary>
        /// …and it names the version when one is known, because "which version was that?" is the first
        /// question any report of this gets — the fact that closed issue #82 in the first place.
        /// </summary>
        [Fact]
        public async Task ALostConnectionNamesTheAgentVersionWhenKnown()
        {
            var diagnostics = new AgentDiagnostics("kiro", @"C:\tools\kiro-cli.exe")
            {
                Version = "kiro-cli-chat 2.13.0",
            };

            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { DropConnectionOnPrompt = true }, diagnostics: diagnostics);

            AgentEvent.SessionError? error = null;
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("hi")))
                if (ev is AgentEvent.SessionError e)
                    error = e;

            Assert.Equal(
                "The agent stopped responding (kiro-cli-chat 2.13.0). Start a new session to continue.",
                error!.Message);
        }

        private sealed class FakeAgentBehaviour
        {
            /// <summary>Answer session/load with this as the error `data` (null = succeed).</summary>
            public string? LoadSessionError { get; init; }

            /// <summary>Answer session/new with this as the error `data` (null = succeed).</summary>
            public string? NewSessionError { get; init; }

            /// <summary>Answer session/prompt with this as the error `data` (null = succeed).</summary>
            public string? PromptError { get; init; }

            /// <summary>Raw JSON for initialize's authMethods array; null = omit the property.</summary>
            public string? AuthMethods { get; init; }

            /// <summary>
            /// Agent message chunks to replay as session/update notifications BEFORE answering
            /// session/load — which is what a real backend does, so a client can rebuild its UI.
            /// </summary>
            public IReadOnlyList<string> LoadSessionHistory { get; init; } = Array.Empty<string>();

            /// <summary>
            /// Announce the session's commands (an <c>available_commands_update</c>) inside the load
            /// window, ahead of any history — which is what a real load does whether or not it
            /// restored anything, and why a replay count must not count it (issue #185).
            /// </summary>
            public bool LoadSessionAnnouncesCommands { get; init; }

            /// <summary>Close the transport instead of answering session/prompt — the shape a backend
            /// that dies mid-turn presents, which StreamJsonRpc surfaces as ConnectionLostException.</summary>
            public bool DropConnectionOnPrompt { get; init; }
        }

        private sealed class Harness : IDisposable
        {
            private readonly string _root;

            private Harness(AcpAgentSession session, FakeAgent agent, string root)
            {
                Session = session;
                Agent = agent;
                _root = root;
            }

            public AcpAgentSession Session { get; }

            public FakeAgent Agent { get; }

            public static async Task<Harness> StartAsync(
                FakeAgentBehaviour behaviour,
                string? resumeConversationId = null,
                Action<AgentEvent>? outOfTurnEvents = null,
                AgentDiagnostics? diagnostics = null)
            {
                var (clientStream, serverStream) = FullDuplexStream.CreatePair();
                var agent = new FakeAgent(serverStream, behaviour);
                agent.Start();

                var root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                var session = new AcpAgentSession(
                    new SessionOptions
                    {
                        WorkspaceRootPath = root,
                        ResumeConversationId = resumeConversationId,
                        OutOfTurnEvents = outOfTurnEvents,
                    },
                    new StubIdeServices(root),
                    new StreamAcpConnection(clientStream),
                    diagnostics: diagnostics);

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

                return new Harness(session, agent, root);
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
        /// A newline-delimited JSON-RPC agent that can refuse any of the three calls a start/turn makes,
        /// answering the way a real backend does: <c>-32603 "Internal error"</c> with the real reason in
        /// the error <c>data</c>.
        /// </summary>
        private sealed class FakeAgent : IDisposable
        {
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

            private Task HandleAsync(string method, string id) => method switch
            {
                "initialize" => SendAsync(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":1"
                    + (_behaviour.AuthMethods is null ? string.Empty : $",\"authMethods\":{_behaviour.AuthMethods}")
                    + "}}"),

                "session/load" => _behaviour.LoadSessionError is { } loadError
                    ? SendErrorAsync(id, loadError)
                    : SendLoadWithHistoryAsync(id),

                "session/new" => _behaviour.NewSessionError is { } newError
                    ? SendErrorAsync(id, newError)
                    : SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"sessionId\":\"s1\"}}}}"),

                "session/prompt" when _behaviour.DropConnectionOnPrompt => DropAsync(),

                "session/prompt" => _behaviour.PromptError is { } promptError
                    ? SendErrorAsync(id, promptError)
                    : SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"stopReason\":\"end_turn\"}}}}"),

                _ => SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}"),
            };

            /// <summary>
            /// Replays the conversation as session/update notifications and only THEN answers the call,
            /// in that order — the ordering is the whole point, since it is what puts history on the wire
            /// at a moment when no turn of the client's is open.
            /// </summary>
            private async Task SendLoadWithHistoryAsync(string id)
            {
                if (_behaviour.LoadSessionAnnouncesCommands)
                    await SendAsync(
                        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":"
                        + "{\"sessionId\":\"sess_old\",\"update\":{\"sessionUpdate\":\"available_commands_update\","
                        + "\"availableCommands\":[{\"name\":\"compact\",\"description\":\"Compact the context\"}]}}}")
                        .ConfigureAwait(false);

                foreach (var text in _behaviour.LoadSessionHistory)
                    await SendAsync(
                        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":"
                        + "{\"sessionId\":\"sess_old\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\","
                        + "\"content\":{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text)
                        + "}}}}").ConfigureAwait(false);

                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}").ConfigureAwait(false);
            }

            /// <summary>Vanish mid-request, as a killed agent does.</summary>
            private Task DropAsync()
            {
                try { _stream.Dispose(); } catch { /* the point is that it goes away */ }
                return Task.CompletedTask;
            }

            /// <summary>The real shape: a generic wrapper message, with the reason in `data`.</summary>
            private Task SendErrorAsync(string id, string data) => SendAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32603,"
                + $"\"message\":\"Internal error\",\"data\":{JsonSerializer.Serialize(data)}}}}}");

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
