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
    /// Image attachments on the wire (issue #118). The shapes here are the ones captured from the real
    /// backends: <c>agentCapabilities.promptCapabilities.image</c> at <c>initialize</c> (kiro-cli
    /// 2.13/2.16, Kiro v3 and claude-agent-acp 0.63.0 all send it true), and an ACP content block of
    /// <c>{type:"image", data:&lt;base64&gt;, mimeType}</c> — data only, no <c>uri</c>, which is what
    /// the Claude adapter's own source maps onto an Anthropic base64 image source.
    /// </summary>
    public class AcpImageAttachmentTests
    {
        private const string OnePixelPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        [Fact]
        public async Task ImagePromptSupportIsDiscoveredFromPromptCapabilities()
        {
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesImages = true });

            Assert.True(harness.Session.DiscoveredImagePrompts);
        }

        [Fact]
        public async Task AnAgentThatSaysNothingAboutImagesIsTreatedAsNotSupportingThem()
        {
            // Explicit-true only. The fallback (save the image and name its path) works everywhere,
            // whereas a block the agent does not model may be dropped silently or fail the prompt — so
            // silence has to mean no.
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesImages = false });

            Assert.False(harness.Session.DiscoveredImagePrompts);
        }

        [Fact]
        public async Task AnAttachmentIsSentAsAnImageBlockAheadOfTheUsersText()
        {
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesImages = true });

            await harness.PromptAsync(new PromptInput("what is wrong with this dialog?")
            {
                Attachments = new[] { new PromptAttachment("image/png", OnePixelPngBase64) },
            });

            var blocks = harness.Agent.LastPromptBlocks;
            var image = FindBlock(blocks, "image");

            Assert.Equal(OnePixelPngBase64, image.GetProperty("data").GetString());
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());

            // Ordering is part of the contract: the picture and the sentence about it must arrive
            // adjacent and in the order the user assembled them, or a prompt referring to "this" is
            // pointing at nothing yet.
            Assert.True(
                IndexOfType(blocks, "image") < LastIndexOfType(blocks, "text"),
                "the image block must precede the user's text block");
        }

        [Fact]
        public async Task AnImageBlockCarriesNoTextFieldAndATextBlockCarriesNoImageFields()
        {
            // Pins the one-record-serves-both claim on ContentBlock: it holds because the formatter is
            // configured WhenWritingNull. Lose that and every text block starts shipping
            // "data":null,"mimeType":null, and every image block a "text":null.
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesImages = true });

            await harness.PromptAsync(new PromptInput("look")
            {
                Attachments = new[] { new PromptAttachment("image/png", OnePixelPngBase64) },
            });

            var image = FindBlock(harness.Agent.LastPromptBlocks, "image");
            Assert.False(image.TryGetProperty("text", out _));

            var text = FindBlock(harness.Agent.LastPromptBlocks, "text");
            Assert.False(text.TryGetProperty("data", out _));
            Assert.False(text.TryGetProperty("mimeType", out _));
        }

        [Fact]
        public async Task APromptWithNoAttachmentsCarriesOnlyTextBlocks()
        {
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesImages = true });

            await harness.PromptAsync(new PromptInput("just a question"));

            foreach (var block in harness.Agent.LastPromptBlocks)
                Assert.Equal("text", block.GetProperty("type").GetString());
        }

        [Fact]
        public async Task AttachmentsRideASteerToo()
        {
            // A steer deliberately drops the workspace-context block (the turn already paid for that
            // snapshot), but an attachment is the user's own content and belongs to THIS message.
            using var harness = await Harness.StartAsync(
                new FakeAgentBehaviour { AdvertisesImages = true, AdvertisesSteering = true });

            var outcome = await harness.Session.SteerAsync(new PromptInput("no, this bit")
            {
                Attachments = new[] { new PromptAttachment("image/png", OnePixelPngBase64) },
            });

            Assert.Equal(SteerOutcome.Injected, outcome);
            Assert.Equal(
                OnePixelPngBase64,
                FindBlock(harness.Agent.LastSteerBlocks, "image").GetProperty("data").GetString());
        }

        [Fact]
        public async Task AnAttachmentMissingItsDataOrMimeTypeIsNotPutOnTheWire()
        {
            // A half-built block would be rejected by the model API far from where it came from; the
            // guard is here, where the shape is still ours.
            using var harness = await Harness.StartAsync(new FakeAgentBehaviour { AdvertisesImages = true });

            await harness.PromptAsync(new PromptInput("look")
            {
                Attachments = new[]
                {
                    new PromptAttachment("image/png", string.Empty),
                    new PromptAttachment(string.Empty, OnePixelPngBase64),
                },
            });

            foreach (var block in harness.Agent.LastPromptBlocks)
                Assert.Equal("text", block.GetProperty("type").GetString());
        }

        private static JsonElement FindBlock(IReadOnlyList<JsonElement> blocks, string type)
        {
            foreach (var block in blocks)
            {
                if (block.GetProperty("type").GetString() == type)
                    return block;
            }

            Assert.Fail($"No '{type}' block in the prompt. Saw {blocks.Count} block(s).");
            return default;
        }

        private static int IndexOfType(IReadOnlyList<JsonElement> blocks, string type)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].GetProperty("type").GetString() == type)
                    return i;
            }
            return -1;
        }

        private static int LastIndexOfType(IReadOnlyList<JsonElement> blocks, string type)
        {
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i].GetProperty("type").GetString() == type)
                    return i;
            }
            return -1;
        }

        private sealed class FakeAgentBehaviour
        {
            /// <summary>Whether initialize carries <c>promptCapabilities.image: true</c>.</summary>
            public bool AdvertisesImages { get; init; }

            /// <summary>Whether initialize carries the top-level <c>_meta.steering.supported</c>.</summary>
            public bool AdvertisesSteering { get; init; }
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

            public static async Task<Harness> StartAsync(FakeAgentBehaviour behaviour)
            {
                var (clientStream, serverStream) = FullDuplexStream.CreatePair();
                var agent = new FakeAgent(serverStream, behaviour);
                agent.Start();

                var root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                var session = new AcpAgentSession(
                    new SessionOptions { WorkspaceRootPath = root },
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

                return new Harness(session, agent, root);
            }

            /// <summary>Drains a turn, so the prompt frame has certainly been written before we read it.</summary>
            public async Task PromptAsync(PromptInput prompt)
            {
                await foreach (var _ in Session.SendAsync(prompt, CancellationToken.None))
                {
                    // The fake ends the turn immediately; we only need the sequence to complete.
                }
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
        /// A newline-delimited JSON-RPC agent that advertises image support and records the content
        /// blocks of whatever it is prompted (or steered) with, exactly as they arrived on the wire.
        /// </summary>
        private sealed class FakeAgent : IDisposable
        {
            private const string SessionId = "s1";

            private readonly Stream _stream;
            private readonly FakeAgentBehaviour _behaviour;
            private readonly StreamWriter _writer;
            private readonly StreamReader _reader;
            private readonly object _gate = new object();
            private Task? _pump;
            private List<JsonElement> _promptBlocks = new List<JsonElement>();
            private List<JsonElement> _steerBlocks = new List<JsonElement>();

            public FakeAgent(Stream stream, FakeAgentBehaviour behaviour)
            {
                _stream = stream;
                _behaviour = behaviour;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }

            public IReadOnlyList<JsonElement> LastPromptBlocks
            {
                get { lock (_gate) return _promptBlocks.ToArray(); }
            }

            public IReadOnlyList<JsonElement> LastSteerBlocks
            {
                get { lock (_gate) return _steerBlocks.ToArray(); }
            }

            public void Start() => _pump = Task.Run(PumpAsync);

            private async Task PumpAsync()
            {
                try
                {
                    string? line;
                    while ((line = await _reader.ReadLineAsync()) is not null)
                    {
                        // Cloned, not held: the JsonDocument is disposed at the end of this iteration
                        // and its elements would take their backing memory with it.
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("method", out var methodEl)
                            || !root.TryGetProperty("id", out var idEl))
                        {
                            doc.Dispose();
                            continue;
                        }

                        await HandleAsync(methodEl.GetString() ?? string.Empty, idEl.GetRawText(), root)
                            .ConfigureAwait(false);
                        doc.Dispose();
                    }
                }
                catch
                {
                    // The stream closes when the session is disposed; nothing to report.
                }
            }

            private async Task HandleAsync(string method, string id, JsonElement request)
            {
                switch (method)
                {
                    case "initialize":
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":1,"
                            + "\"agentCapabilities\":{\"loadSession\":true"
                            + (_behaviour.AdvertisesImages
                                ? ",\"promptCapabilities\":{\"image\":true,\"audio\":false}"
                                : ",\"promptCapabilities\":{\"audio\":false}")
                            + "},\"authMethods\":[]"
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

                    case "session/prompt":
                        lock (_gate)
                            _promptBlocks = CapturePrompt(request);
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"stopReason\":\"end_turn\"}}}}")
                            .ConfigureAwait(false);
                        return;

                    case "_session/steering":
                        lock (_gate)
                            _steerBlocks = CapturePrompt(request);
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"outcome\":\"injected\"}}}}")
                            .ConfigureAwait(false);
                        return;

                    default:
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}").ConfigureAwait(false);
                        return;
                }
            }

            private static List<JsonElement> CapturePrompt(JsonElement request)
            {
                var blocks = new List<JsonElement>();
                if (request.TryGetProperty("params", out var parameters)
                    && parameters.TryGetProperty("prompt", out var prompt)
                    && prompt.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in prompt.EnumerateArray())
                        blocks.Add(block.Clone());
                }
                return blocks;
            }

            private async Task SendAsync(string json)
            {
                await _writer.WriteLineAsync(json).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }

            public void Dispose()
            {
                try { _stream.Dispose(); } catch { /* teardown */ }
                try { _pump?.Wait(1000); } catch { /* teardown */ }
            }
        }
    }
}
