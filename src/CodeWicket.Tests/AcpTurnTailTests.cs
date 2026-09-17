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
    /// Issue #33: the last streamed chunks of a turn never reached the transcript. The ACP log proved
    /// the agent sent them, so the loss was client-side. These tests drive a real
    /// <see cref="AcpAgentSession"/> against an in-memory agent that blasts N text chunks and then
    /// immediately answers <c>session/prompt</c> — the exact shape of the reported frame sequence.
    /// </summary>
    public class AcpTurnTailTests
    {
        [Fact]
        public async Task EveryChunkSentBeforeTheTurnResultReachesTheConsumer()
        {
            // Sized to lose text reliably before the fix (the pre-fix run dropped all but the first
            // chunk of turn 0, and leaked a previous turn's tail into the next turn's transcript).
            const int turns = 50;
            const int chunksPerTurn = 200;

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            using var agent = new ChunkBlastAgent(serverStream, chunksPerTurn);
            agent.Start();

            var root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var session = new AcpAgentSession(
                new SessionOptions { WorkspaceRootPath = root },
                new StubIdeServices(root),
                new StreamAcpConnection(clientStream));

            await session.InitializeAsync(CancellationToken.None);

            var losses = new List<string>();
            for (var turn = 0; turn < turns; turn++)
            {
                var received = new StringBuilder();
                await foreach (var ev in session.SendAsync(new PromptInput("go")))
                    if (ev is AgentEvent.AssistantTextDelta delta)
                        received.Append(delta.Text);

                var expected = ChunkBlastAgent.ExpectedText(chunksPerTurn);
                if (received.ToString() != expected)
                    losses.Add($"turn {turn}: got {received.Length} of {expected.Length} chars ('…{Tail(received.ToString())}')");
            }

            await session.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }

            Assert.True(losses.Count == 0, "Streamed text was dropped:\n" + string.Join("\n", losses));
        }

        private static string Tail(string s) => s.Length <= 24 ? s : s.Substring(s.Length - 24);

        /// <summary>An <see cref="IAcpConnection"/> over one in-memory duplex stream.</summary>
        private sealed class StreamAcpConnection : IAcpConnection
        {
            private readonly Stream _stream;

            public StreamAcpConnection(Stream duplex) => _stream = duplex;

            public Stream Sending => _stream;

            public Stream Receiving => _stream;

            public void Dispose() => _stream.Dispose();
        }

        /// <summary>
        /// A minimal newline-delimited JSON-RPC "agent" that answers the handshake and, for every
        /// <c>session/prompt</c>, writes <c>chunks</c> <c>agent_message_chunk</c> notifications
        /// back-to-back and then the prompt result — no pause between the last chunk and the result,
        /// which is what makes the end-of-turn race observable.
        /// </summary>
        private sealed class ChunkBlastAgent : IDisposable
        {
            private readonly Stream _stream;
            private readonly int _chunks;
            private readonly StreamWriter _writer;
            private readonly StreamReader _reader;
            private Task? _pump;

            public ChunkBlastAgent(Stream stream, int chunks)
            {
                _stream = stream;
                _chunks = chunks;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }

            public static string ExpectedText(int chunks)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < chunks; i++)
                    sb.Append(Chunk(i));
                return sb.ToString();
            }

            private static string Chunk(int i) => " c" + i;

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
                        if (!root.TryGetProperty("method", out var methodEl))
                            continue;
                        var method = methodEl.GetString();
                        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
                        if (id is null)
                            continue; // a notification from the client needs no answer

                        switch (method)
                        {
                            case "initialize":
                                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":1}}}}");
                                break;
                            case "session/new":
                                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"sessionId\":\"s1\"}}}}");
                                break;
                            case "session/prompt":
                                for (var i = 0; i < _chunks; i++)
                                    await SendAsync(
                                        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
                                        "\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\"," +
                                        "\"text\":\"" + Chunk(i) + "\"}}}}");
                                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"stopReason\":\"end_turn\"}}}}");
                                break;
                            default:
                                await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                                break;
                        }
                    }
                }
                catch
                {
                    // The stream closes when the session is disposed; nothing to report.
                }
            }

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
