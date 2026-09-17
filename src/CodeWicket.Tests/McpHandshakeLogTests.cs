using System;
using System.Collections.Concurrent;
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
using CodeWicket.Engine.Mcp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The two unconditional <c>engine.log</c> lines the IDE MCP bridge writes about itself
    /// (issue #122, part 3): one naming the client that shook hands, one saying how many tools it was
    /// served.
    ///
    /// <para><b>Why this is driven over a real JSON-RPC pair rather than by calling the target's
    /// methods.</b> The whole value of these lines is that they are written on the path a live agent
    /// actually takes, and every way they could fail is a transport fact invisible to a direct call: a
    /// method whose parameter shape the client cannot satisfy is rejected before dispatch, so its body
    /// — and any log line inside it — never runs, with nothing raised anywhere. That is not a
    /// hypothetical: it is the exact defect (<c>tools/list</c> params mismatch, registering zero tools)
    /// that this logging exists to make visible, and it is why the handshake line is separate. Both
    /// client shapes that have caused it are exercised here.</para>
    /// </summary>
    [Collection(McpLogCollection.Name)]
    public class McpHandshakeLogTests
    {
        [Fact]
        public async Task TheHandshakeLineNamesTheClientAndItsProtocol()
        {
            using var harness = McpHarness.Start(TwoTools);

            await harness.InitializeAsync(new { protocolVersion = "2025-06-18", clientInfo = new { name = "kiro-cli", version = "2.16.2" } });

            var line = Assert.Single(harness.Lines, l => l.StartsWith("[mcp] handshake:", StringComparison.Ordinal));
            Assert.Contains("kiro-cli 2.16.2", line);
            Assert.Contains("protocol=2025-06-18", line);
        }

        /// <summary>
        /// A client that names itself nothing is reported as nothing. The alternative — inferring a
        /// name from the transport — would put a confident wrong answer in the one line someone reads
        /// when the tools did not arrive.
        /// </summary>
        [Fact]
        public async Task AnUnnamedClientIsSaidToBeUnnamedRatherThanGuessedAt()
        {
            using var harness = McpHarness.Start(TwoTools);

            await harness.InitializeAsync(new { protocolVersion = (string?)null });

            var line = Assert.Single(harness.Lines, l => l.StartsWith("[mcp] handshake:", StringComparison.Ordinal));
            Assert.Contains("unnamed client", line);
            Assert.Contains("protocol=unstated", line);
        }

        /// <summary>
        /// The count is the signal, and the names ride along: a wrong subset is the same defect one
        /// notch quieter than a wrong total, and a count alone cannot separate "nothing I needed" from
        /// "the tool I want, silently missing".
        /// </summary>
        [Fact]
        public async Task TheToolsLineCountsAndNamesWhatWasServed()
        {
            using var harness = McpHarness.Start(TwoTools);

            await harness.ListToolsAsync(withParams: true);

            var line = Assert.Single(harness.Lines, l => l.StartsWith("[mcp] tools/list:", StringComparison.Ordinal));
            Assert.Contains("served 2 tools", line);
            Assert.Contains("build_solution", line);
            Assert.Contains("get_diagnostics", line);
        }

        /// <summary>
        /// The failure the whole thing exists for: a connection that comes up and serves nothing looks
        /// healthy from every other angle, so the zero has to be stated rather than implied by a list
        /// that happens to be short.
        /// </summary>
        [Fact]
        public async Task AnEmptyCatalogIsReportedAsZeroToolsRatherThanNotMentioned()
        {
            using var harness = McpHarness.Start(Array.Empty<ToolDescriptor>());

            await harness.ListToolsAsync(withParams: true);

            var line = Assert.Single(harness.Lines, l => l.StartsWith("[mcp] tools/list:", StringComparison.Ordinal));
            Assert.Contains("served 0 tools", line);
        }

        /// <summary>
        /// Both client shapes reach the line. Kiro sends <c>tools/list</c> WITH a params payload,
        /// Claude Code with no params member at all, and each has historically been rejected before
        /// dispatch by a target that accepted only the other — which is precisely the state this line
        /// is meant to report on, so it must survive both.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TheToolsLineIsWrittenForEitherClientsParameterShape(bool withParams)
        {
            using var harness = McpHarness.Start(TwoTools);

            await harness.ListToolsAsync(withParams);

            Assert.Single(harness.Lines, l => l.StartsWith("[mcp] tools/list:", StringComparison.Ordinal));
        }

        /// <summary>
        /// The tools line names the client from the handshake, which is what lets one engine.log line
        /// answer "who registered these" on a machine running more than one backend.
        /// </summary>
        [Fact]
        public async Task TheToolsLineNamesTheClientItShookHandsWith()
        {
            using var harness = McpHarness.Start(TwoTools);

            await harness.InitializeAsync(new { protocolVersion = "2025-06-18", clientInfo = new { name = "claude-agent-acp", version = "0.63.0" } });
            await harness.ListToolsAsync(withParams: false);

            var line = Assert.Single(harness.Lines, l => l.StartsWith("[mcp] tools/list:", StringComparison.Ordinal));
            Assert.Contains("claude-agent-acp 0.63.0", line);
        }

        /// <summary>
        /// The same three facts the log lines report, published to the observer the chat pane's MCP
        /// panel is fed from (issue #122). Asserted on the live JSON-RPC path rather than by calling the
        /// handlers directly, for the reason the log lines are: a status nobody can reach is exactly as
        /// useful as a line nobody writes.
        /// <para>The load-bearing assertion is the FIRST one — after a handshake and before any
        /// <c>tools/list</c>, <c>ToolsServed</c> is <b>null and not zero</b>. That distinction is the
        /// whole of part 3 restated in the UI: "connected, catalog never asked for" is the failure that
        /// has actually happened here, and a zero would render it identically to a healthy server that
        /// genuinely has no tools.</para>
        /// </summary>
        [Fact]
        public async Task TheBridgeObserverSeparatesNotAskedYetFromServedNothing()
        {
            using var harness = McpHarness.Start(TwoTools);

            await harness.InitializeAsync(new { protocolVersion = "2025-06-18", clientInfo = new { name = "kiro-cli", version = "2.16.2" } });

            var afterHandshake = Assert.Single(harness.Statuses);
            Assert.True(afterHandshake.Connected);
            Assert.Null(afterHandshake.ToolsServed);
            Assert.Equal(0, afterHandshake.ToolCalls);

            await harness.ListToolsAsync(withParams: true);

            var afterList = harness.Statuses[^1];
            Assert.Equal(2, afterList.ToolsServed);
            Assert.Equal(new[] { "build_solution", "get_diagnostics" }, afterList.ToolNames);

            // This stub's catalog throws on invoke, and that is the useful case rather than an
            // inconvenience: the call is counted BEFORE the tool runs, because the fact being reported
            // is that the agent called one of our tools — which is true whether the tool then succeeds,
            // fails or throws. Counting afterwards would silently under-report exactly the calls
            // someone opening this panel is most likely trying to account for.
            await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(
                () => harness.CallToolAsync("build_solution"));

            var afterCall = harness.Statuses[^1];
            Assert.Equal(1, afterCall.ToolCalls);
            // The catalog is not un-served by a later call: the fold keeps what was already reported.
            Assert.Equal(2, afterCall.ToolsServed);
        }

        /// <summary>
        /// An empty catalog reports a real zero, which the null above must stay distinguishable from.
        /// This is the pair that makes either value mean anything.
        /// </summary>
        [Fact]
        public async Task AnEmptyCatalogReportsZeroToolsServedRatherThanNull()
        {
            using var harness = McpHarness.Start(System.Array.Empty<ToolDescriptor>());

            await harness.InitializeAsync(new { protocolVersion = "2025-06-18" });
            await harness.ListToolsAsync(withParams: false);

            Assert.Equal(0, harness.Statuses[^1].ToolsServed);
        }

        private static readonly IReadOnlyList<ToolDescriptor> TwoTools = new[]
        {
            new ToolDescriptor("build_solution", "Builds the solution", "{\"type\":\"object\"}"),
            new ToolDescriptor("get_diagnostics", "Reads the Error List", "{\"type\":\"object\"}"),
        };

        /// <summary>A real <see cref="McpToolServer"/> over an in-memory duplex pair, with the log sink captured.</summary>
        private sealed class McpHarness : IDisposable
        {
            private readonly McpToolServer _server;
            private readonly JsonRpc _client;
            private readonly ConcurrentQueue<string> _lines = new();
            private readonly ConcurrentQueue<McpBridgeStatus> _statuses = new();

            private McpHarness(IReadOnlyList<ToolDescriptor> tools)
            {
                var (clientEnd, serverEnd) = FullDuplexStream.CreatePair();
                _server = new McpToolServer(
                    serverEnd, serverEnd, new StubCatalog(tools), _lines.Enqueue, _statuses.Enqueue);
                _server.Start();

                _client = new JsonRpc(new NewLineDelimitedMessageHandler(clientEnd, clientEnd, NewFormatter()));
                _client.StartListening();
            }

            public static McpHarness Start(IReadOnlyList<ToolDescriptor> tools) => new(tools);

            public IReadOnlyList<string> Lines => _lines.ToArray();

            public IReadOnlyList<McpBridgeStatus> Statuses => _statuses.ToArray();

            public Task<JsonElement> InitializeAsync(object parameters) =>
                _client.InvokeWithParameterObjectAsync<JsonElement>("initialize", parameters);

            /// <param name="withParams">
            /// Kiro's shape (a params payload) when true, Claude Code's (no params member) when false.
            /// </param>
            public Task<JsonElement> ListToolsAsync(bool withParams) =>
                withParams
                    ? _client.InvokeWithParameterObjectAsync<JsonElement>(
                        "tools/list", new { _meta = new { progressToken = 0 } })
                    : _client.InvokeAsync<JsonElement>("tools/list");

            public Task<JsonElement> CallToolAsync(string name) =>
                _client.InvokeWithParameterObjectAsync<JsonElement>(
                    "tools/call", new { name, arguments = new { } });

            public void Dispose()
            {
                _client.Dispose();
                _server.Dispose();
            }

            private static SystemTextJsonFormatter NewFormatter() => new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };
        }

        private sealed class StubCatalog : IToolCatalog
        {
            public StubCatalog(IReadOnlyList<ToolDescriptor> tools) => Tools = tools;

            public IReadOnlyList<ToolDescriptor> Tools { get; }

            public Task<ToolResult> InvokeAsync(
                string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("These tests never invoke a tool.");
        }
    }
}
