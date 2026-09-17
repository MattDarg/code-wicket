using System;
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
    /// Discovery of ACP <c>sessionCapabilities.list</c> — whether the backend can enumerate the
    /// conversations its own CLI has stored, so one started in the terminal can be picked up here
    /// (issue #108).
    /// <para><b>Why this needs its own tests rather than riding the capability parsers beside it:</b>
    /// <c>sessionCapabilities</c> is a map of sub-OBJECTS, not of booleans. The value that means "yes"
    /// is the empty object <c>{}</c>, so the <c>ValueKind == JsonValueKind.True</c> spelling used by
    /// <c>loadSession</c> and <c>promptCapabilities.image</c> a few lines away reads it as false.
    /// Nothing throws and nothing warns — the picker section would simply never appear, on a backend
    /// that supports listing perfectly well. <c>TheShapeTheAdapterActuallySends</c> is the case that
    /// fails under that spelling, and it carries the capability blob verbatim from claude-agent-acp
    /// 0.70.0 rather than a tidied-up version of it.</para>
    /// </summary>
    public class AcpSessionListTests
    {
        /// <summary>Verbatim from claude-agent-acp 0.70.0's initialize response.</summary>
        private const string RealSessionCapabilities =
            "{\"additionalDirectories\":{},\"close\":{},\"delete\":{},\"fork\":{},\"list\":{},\"resume\":{}}";

        [Fact]
        public async Task TheShapeTheAdapterActuallySends()
        {
            // The one that matters: `list` is an empty OBJECT. Rewrite the parse as ValueKind == True
            // and this is the test that goes red while every other case here still passes.
            using var harness = await Harness.StartAsync(RealSessionCapabilities);

            Assert.True(harness.Session.DiscoveredSessionList);
        }

        [Fact]
        public async Task AnAgentThatAdvertisesNoSessionCapabilitiesCannotList()
        {
            using var harness = await Harness.StartAsync(sessionCapabilitiesJson: null);

            Assert.False(harness.Session.DiscoveredSessionList);
        }

        [Fact]
        public async Task SessionCapabilitiesWithoutListDoesNotImplyListing()
        {
            // A backend can support part of the session surface and not this part of it, so the
            // presence of the section says nothing on its own.
            using var harness = await Harness.StartAsync("{\"fork\":{},\"resume\":{}}");

            Assert.False(harness.Session.DiscoveredSessionList);
        }

        [Fact]
        public async Task ABooleanTrueIsAcceptedToo()
        {
            // Nothing in the spec forces the sub-object shape, and an agent that says `list: true`
            // plainly means yes. The test is "declared at all", not "declared as an object".
            using var harness = await Harness.StartAsync("{\"list\":true}");

            Assert.True(harness.Session.DiscoveredSessionList);
        }

        [Theory]
        [InlineData("{\"list\":false}")]
        [InlineData("{\"list\":null}")]
        public async Task AnExplicitOptOutIsHonoured(string sessionCapabilitiesJson)
        {
            // Being generous about the shape must not become being generous about the answer: an agent
            // that names the capability in order to refuse it has said no.
            using var harness = await Harness.StartAsync(sessionCapabilitiesJson);

            Assert.False(harness.Session.DiscoveredSessionList);
        }

        private sealed class Harness : IDisposable
        {
            private readonly string _root;
            private readonly FakeAgent _agent;

            private Harness(AcpAgentSession session, FakeAgent agent, string root)
            {
                Session = session;
                _agent = agent;
                _root = root;
            }

            public AcpAgentSession Session { get; }

            public static async Task<Harness> StartAsync(string? sessionCapabilitiesJson)
            {
                var (clientStream, serverStream) = FullDuplexStream.CreatePair();
                var agent = new FakeAgent(serverStream, sessionCapabilitiesJson);
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

            public void Dispose()
            {
                try { Session.DisposeAsync().AsTask().Wait(2000); } catch { /* teardown */ }
                _agent.Dispose();
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
        /// A newline-delimited JSON-RPC agent whose only variable is the <c>sessionCapabilities</c>
        /// blob it advertises at <c>initialize</c>.
        /// </summary>
        private sealed class FakeAgent : IDisposable
        {
            private const string SessionId = "s1";

            private readonly Stream _stream;
            private readonly string? _sessionCapabilitiesJson;
            private readonly StreamWriter _writer;
            private readonly StreamReader _reader;
            private Task? _pump;

            public FakeAgent(Stream stream, string? sessionCapabilitiesJson)
            {
                _stream = stream;
                _sessionCapabilitiesJson = sessionCapabilitiesJson;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
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
                        {
                            continue;
                        }

                        await HandleAsync(methodEl.GetString() ?? string.Empty, idEl.GetRawText())
                            .ConfigureAwait(false);
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
                        var caps = "{\"loadSession\":true"
                            + (_sessionCapabilitiesJson is null
                                ? string.Empty
                                : ",\"sessionCapabilities\":" + _sessionCapabilitiesJson)
                            + "}";
                        await SendAsync(
                            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":1,"
                            + "\"agentCapabilities\":" + caps + ",\"authMethods\":[]}}").ConfigureAwait(false);
                        return;

                    case "session/new":
                        await SendAsync(
                            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"sessionId\":\"" + SessionId + "\"}}")
                            .ConfigureAwait(false);
                        return;

                    default:
                        await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}").ConfigureAwait(false);
                        return;
                }
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
