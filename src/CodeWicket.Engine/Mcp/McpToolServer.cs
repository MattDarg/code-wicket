using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Engine.Mcp
{
    /// <summary>
    /// A minimal MCP server (JSON-RPC over a stream) that surfaces the IDE's <see cref="IToolCatalog"/>
    /// to Kiro. Tool calls route to the catalog, which marshals back into Visual Studio.
    /// </summary>
    public sealed class McpToolServer : System.IDisposable
    {
        private readonly JsonRpc _rpc;

        // The shared tee, when CWKT_MCP_LOG is on. Null otherwise, and null is the ordinary case.
        private readonly FrameLogSink? _frameLog;

        /// <param name="log">
        /// Where the two unconditional diagnostic lines go (issue #122, part 3). Defaults to stderr,
        /// which the shell drains into <c>engine.log</c> — the log that is always written. Injectable
        /// so a test can assert the lines reach the sink on the live JSON-RPC path, rather than
        /// asserting a formatter nobody calls.
        /// </param>
        /// <param name="onStatus">
        /// Optional observer of the same three facts the two log lines already report — handshook,
        /// tools served, a call happened — so the chat pane can show what our bridge is doing on a
        /// backend that reports no MCP status of its own (Claude Code reports none at all). Beside
        /// <paramref name="log"/> rather than derived from it: a diagnostic line is prose for a human
        /// reading a file, and parsing it back would make the log's wording load-bearing.
        /// </param>
        public McpToolServer(
            Stream sending, Stream receiving, IToolCatalog tools, System.Action<string>? log = null,
            System.Action<McpBridgeStatus>? onStatus = null)
        {
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };

            // Diagnostic: when CWKT_MCP_LOG is set, tee both directions of the MCP conversation
            // (initialize / tools/list / tools/call and our answers) into ONE stamped file - the shape
            // the ACP tee took in #211, for the same reason: a request whose response is in the other
            // file looks unanswered, and our own responses looked like they never happened. Off by
            // default.
            //
            // The tees are FRAME-ALIGNED (Core.FrameTee*), and that is what makes one file safe. The
            // raw byte tees this used to own forwarded whatever chunk the stream handed them, so
            // pointing two of THOSE at one path would have interleaved mid-frame and produced a log
            // parseable as neither direction. The marks are written from OUR seat - "->" is what this
            // process sent - which here is server->client, the opposite parties to the ACP log's.
            if (System.Environment.GetEnvironmentVariable("CWKT_MCP_LOG") is { Length: > 0 } logPath)
            {
                // Held so it can be disposed with the server: the tee's owner must dispose it, or the
                // next run's roll silently fails and two runs braid into one file (DiagnosticLog's
                // rule). Neither stream owns it - they share it, so the first to close would silence
                // the other.
                _frameLog = FrameLogSink.Open(logPath);
                receiving = new FrameTeeReadStream(receiving, _frameLog);
                sending = new FrameTeeWriteStream(sending, _frameLog);
            }

            var handler = new NewLineDelimitedMessageHandler(sending, receiving, formatter);
            _rpc = new JsonRpc(handler);
            _rpc.AddLocalRpcTarget(
                new McpTarget(tools, log ?? (line => System.Console.Error.WriteLine(line)), onStatus),
                new JsonRpcTargetOptions());
        }

        public Task Completion => _rpc.Completion;

        public void Start() => _rpc.StartListening();

        /// <summary>
        /// Disposes the RPC and, with it, the tee. <b>The sink is disposed HERE and by nothing else</b>
        /// — the two tee streams share it, so whichever closed first would silence the other, and an
        /// undisposed tee leaves the next run's roll to fail silently and braid two runs into one file
        /// (<see cref="DiagnosticLog"/>'s rule for every tee). Disposing it also drains what is still
        /// queued, the writes being deliberately off the protocol path.
        /// </summary>
        public void Dispose()
        {
            _rpc.Dispose();
            _frameLog?.Dispose();
        }

        private sealed class McpTarget
        {
            private readonly IToolCatalog _tools;
            private readonly System.Action<string> _log;
            private readonly System.Action<McpBridgeStatus>? _onStatus;

            // What we have observed on THIS connection. The running total across connections is folded
            // by McpPipeHost, which outlives them — see the note there.
            private bool _connected;
            private int? _toolsServed;
            private IReadOnlyList<string>? _toolNames;
            private int _toolCalls;

            /// <summary>
            /// Who we shook hands with, remembered from <c>initialize</c> so the tools line can name
            /// it. One pipe connection serves one client, so a plain field spans its whole lifetime.
            /// </summary>
            private string _client = UnnamedClient;

            private const string UnnamedClient = "an unnamed client";

            public McpTarget(IToolCatalog tools, System.Action<string> log, System.Action<McpBridgeStatus>? onStatus)
            {
                _tools = tools;
                _log = log;
                _onStatus = onStatus;
            }

            private void PublishStatus() =>
                _onStatus?.Invoke(new McpBridgeStatus(_connected, _toolsServed, _toolNames, _toolCalls));

            [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
            public object Initialize(JsonElement parameters = default)
            {
                _client = DescribeClient(parameters);

                // Unconditional (issue #122, part 3), and the reason there are TWO lines rather than
                // the one the issue asked for: the failure this exists to expose — a client that
                // registers zero tools — has always been a tools/list that never reached our method
                // at all, the params mismatch below being rejected by StreamJsonRpc before dispatch.
                // A line written INSIDE ToolsList would therefore have been silent for exactly the
                // bug it was added for. This one is what gives the other's ABSENCE a meaning:
                // handshake with no tools line = the client asked and we refused (or never asked);
                // no lines at all = nothing ever connected, a different fault in a different place.
                _log($"[mcp] handshake: {_client}, protocol={ProtocolVersion(parameters) ?? "unstated"}");

                // Deliberately leaves ToolsServed null rather than setting it to 0. That null IS the
                // part-3 asymmetry made visible in the pane: "connected, catalog not asked for yet" is
                // a real and distinct state from "connected, served nothing", and a zero here would
                // erase exactly the distinction those two log lines exist to preserve.
                _connected = true;
                PublishStatus();

                return new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = IdeMcpServer.Name, version = "0.1" },
                };
            }

            // The params object must be BOTH accepted and optional — clients differ: Kiro sends
            // tools/list with a params payload (e.g. {"_meta":{"progressToken":0}}), which
            // StreamJsonRpc rejects as an unexpected named argument without single-object
            // deserialization; Claude Code sends tools/list with NO params member at all, which a
            // required parameter rejects with -32602 ("argument not supplied") — and the client
            // then registers zero tools. Either failure looks like an empty tool registry.
            [JsonRpcMethod("tools/list", UseSingleObjectParameterDeserialization = true)]
            public object ToolsList(JsonElement parameters = default)
            {
                var served = _tools.Tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = ParseSchema(t.InputSchemaJson),
                }).ToArray();

                // The COUNT is the signal: a connection that comes up and serves nothing is the
                // failure that has actually happened here, and from every other angle it is
                // indistinguishable from a healthy one. The names ride along because they are free,
                // and a wrong SUBSET is the same defect one notch quieter than a wrong total — a
                // count alone leaves the reader unable to tell "nothing I needed" from "the tool I
                // want, silently missing".
                _log(served.Length == 0
                    ? $"[mcp] tools/list: served 0 tools to {_client} — the catalog is empty"
                    : $"[mcp] tools/list: served {served.Length} tool{(served.Length == 1 ? "" : "s")} "
                        + $"to {_client} ({string.Join(", ", served.Select(t => t.name))})");

                _connected = true;
                _toolsServed = served.Length;
                _toolNames = served.Select(t => t.name).ToArray();
                PublishStatus();

                return new { tools = served };
            }

            // `= default` and the ValueKind guard for the same reason tools/list carries them, spelled
            // out above: a client may send no params member at all, and a JsonElement that is Undefined
            // throws from TryGetProperty rather than answering false. Unguarded, that exception left the
            // handler instead of becoming a tool result the agent could read.
            [JsonRpcMethod("tools/call", UseSingleObjectParameterDeserialization = true)]
            public async Task<object> ToolsCallAsync(JsonElement parameters = default)
            {
                var call = parameters.ValueKind == JsonValueKind.Object ? parameters : default;
                var name = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("name", out var n)
                    ? n.GetString() ?? string.Empty
                    : string.Empty;
                var argumentsJson = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("arguments", out var a)
                    ? a.GetRawText()
                    : "{}";

                // Counted BEFORE the invoke: the fact being reported is that the agent called one of our
                // tools, which is true whether the tool then succeeds, fails or throws. Counting after
                // would silently under-report exactly the calls a reader is most likely looking for.
                _toolCalls++;
                PublishStatus();

                var result = await _tools.InvokeAsync(name, argumentsJson).ConfigureAwait(false);
                return new
                {
                    content = new object[] { new { type = "text", text = result.ContentJson } },
                    isError = result.IsError,
                };
            }

            /// <summary>
            /// The client's own account of itself from the <c>initialize</c> params
            /// (<c>clientInfo</c>). Never inferred: a client that names itself nothing is reported as
            /// nothing rather than guessed at from the transport, which would put a confident wrong
            /// name in the one line read when the tools didn't arrive.
            /// </summary>
            private static string DescribeClient(JsonElement parameters)
            {
                if (parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("clientInfo", out var info) &&
                    info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("name", out var name) &&
                    name.GetString() is { Length: > 0 } clientName)
                {
                    return info.TryGetProperty("version", out var version) &&
                           version.GetString() is { Length: > 0 } clientVersion
                        ? clientName + " " + clientVersion
                        : clientName;
                }

                return UnnamedClient;
            }

            /// <summary>The MCP protocol version the client opened with, or null when it stated none.</summary>
            private static string? ProtocolVersion(JsonElement parameters) =>
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("protocolVersion", out var protocol) &&
                protocol.GetString() is { Length: > 0 } value
                    ? value
                    : null;

            private static JsonNode ParseSchema(string json)
            {
                try
                {
                    return (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json)) ?? new JsonObject();
                }
                catch
                {
                    return new JsonObject();
                }
            }
        }

    }
}
