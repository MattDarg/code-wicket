using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// A minimal in-process ACP agent (server side) used to prove the client mapping offline.
    /// On a prompt it streams text + a tool call, asks the client to write a file via the
    /// client-owned filesystem, then completes the turn.
    /// </summary>
    internal sealed class FakeAcpAgent
    {
        private readonly JsonRpc _rpc;

        public FakeAcpAgent(Stream stream, string writeRelativePath)
        {
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };

            var handler = new NewLineDelimitedMessageHandler(stream, stream, formatter);
            _rpc = new JsonRpc(handler);

            var target = new ServerTarget(writeRelativePath) { Rpc = _rpc };
            _rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions());
        }

        public void Start() => _rpc.StartListening();

        private sealed class ServerTarget
        {
            private readonly string _writePath;

            public ServerTarget(string writePath) => _writePath = writePath;

            public JsonRpc Rpc { get; init; } = default!;

            [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
            public object Initialize(JsonElement parameters) =>
                new { protocolVersion = 1, agentInfo = new { name = "fake-kiro", version = "0.0" } };

            [JsonRpcMethod("session/new", UseSingleObjectParameterDeserialization = true)]
            public object NewSession(JsonElement parameters) =>
                new { sessionId = "fake-session-1" };

            [JsonRpcMethod("session/prompt", UseSingleObjectParameterDeserialization = true)]
            public async Task<object> PromptAsync(JsonElement parameters)
            {
                var sessionId = parameters.GetProperty("sessionId").GetString();

                await SendTextAsync(sessionId, "Hello from the fake Kiro agent! ").ConfigureAwait(false);
                await SendTextAsync(sessionId, "Writing a file through the client-owned filesystem...").ConfigureAwait(false);

                await Rpc.NotifyWithParameterObjectAsync("session/update", new
                {
                    sessionId,
                    update = new { sessionUpdate = "tool_call", toolCallId = "tool-1", title = "Write greeting.txt", kind = "edit" },
                }).ConfigureAwait(false);

                // The client (host) performs the actual write — proving edits route through IEditApplier.
                await Rpc.InvokeWithParameterObjectAsync<JsonElement>("fs/write_text_file", new
                {
                    sessionId,
                    path = _writePath,
                    content = "Written by the fake Kiro agent through the host's IEditApplier.\n",
                }).ConfigureAwait(false);

                await Rpc.NotifyWithParameterObjectAsync("session/update", new
                {
                    sessionId,
                    update = new { sessionUpdate = "tool_call_update", toolCallId = "tool-1", status = "completed" },
                }).ConfigureAwait(false);

                return new { stopReason = "end_turn" };
            }

            private Task SendTextAsync(string? sessionId, string text) =>
                Rpc.NotifyWithParameterObjectAsync("session/update", new
                {
                    sessionId,
                    update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } },
                });
        }
    }
}
