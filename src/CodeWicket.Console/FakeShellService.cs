using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// Stands in for the VS shell's side of the engine↔shell boundary: prints streamed agent
    /// events and serves the engine's <c>IIdeServices</c> callbacks from a backing IIdeServices.
    /// </summary>
    internal sealed class FakeShellService
    {
        private readonly IIdeServices _ide;

        public FakeShellService(IIdeServices ide) => _ide = ide;

        public List<AgentEventDto> Events { get; } = new();

        [JsonRpcMethod(RpcMethods.OnAgentEvent, UseSingleObjectParameterDeserialization = true)]
        public void OnAgentEvent(AgentEventDto ev)
        {
            Events.Add(ev);
            switch (ev.Type)
            {
                case "text": Console.Write(ev.Text); break;
                case "thinking": Console.WriteLine($"\n  [thinking] {ev.Text}"); break;
                case "toolStart": Console.WriteLine($"\n  [tool start] {ev.Title} (kind={ev.Kind})"); break;
                case "toolDone": Console.WriteLine($"  [tool done] {ev.ToolCallId} success={ev.Success}"); break;
                case "error": Console.WriteLine($"\n  [error] {ev.Message}"); break;
                case "turnDone": Console.WriteLine($"\n  [turn complete] stopReason={ev.StopReason}"); break;
            }
        }

        [JsonRpcMethod(RpcMethods.WorkspaceCapture)]
        public async Task<WorkspaceSnapshotDto> CaptureAsync() =>
            DtoMapping.ToDto(await _ide.Workspace.CaptureAsync().ConfigureAwait(false));

        [JsonRpcMethod(RpcMethods.EditsRead, UseSingleObjectParameterDeserialization = true)]
        public async Task<ReadFileResponse> ReadAsync(ReadFileRequest request) =>
            new(await _ide.Edits.ReadTextFileAsync(request.Path, request.Line, request.Limit).ConfigureAwait(false));

        [JsonRpcMethod(RpcMethods.EditsWrite, UseSingleObjectParameterDeserialization = true)]
        public async Task<WriteFileResponse> WriteAsync(WriteFileRequest request)
        {
            var result = await _ide.Edits.WriteTextFileAsync(request.Path, request.Content).ConfigureAwait(false);
            return result is null
                ? new WriteFileResponse(null, null, null)
                : new WriteFileResponse(result.ResolvedPath, result.OldText, result.NewText);
        }

        [JsonRpcMethod(RpcMethods.ToolsList)]
        public Task<ToolListResponse> ToolsListAsync() =>
            Task.FromResult(new ToolListResponse(_ide.Tools.Tools.Select(DtoMapping.ToDto).ToList()));

        [JsonRpcMethod(RpcMethods.ToolsInvoke, UseSingleObjectParameterDeserialization = true)]
        public async Task<ToolResultDto> ToolsInvokeAsync(InvokeToolRequest request) =>
            DtoMapping.ToDto(await _ide.Tools.InvokeAsync(request.Name, request.ArgumentsJson).ConfigureAwait(false));

        [JsonRpcMethod(RpcMethods.PermissionsRequest, UseSingleObjectParameterDeserialization = true)]
        public async Task<PermissionDecisionDto> PermissionAsync(PermissionRequestDto request) =>
            DtoMapping.ToDto(await _ide.Permissions.RequestAsync(DtoMapping.ToRequest(request)).ConfigureAwait(false));
    }

    /// <summary>A one-tool catalog used to exercise the MCP server end to end.</summary>
    internal sealed class EchoToolCatalog : IToolCatalog
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } = new[]
        {
            new ToolDescriptor(
                "echo",
                "Echoes the input text.",
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}"),
        };

        public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(toolName == "echo"
                ? new ToolResult(IsError: false, $"{{\"echoed\":{argumentsJson}}}")
                : new ToolResult(IsError: true, "{\"error\":\"unknown tool\"}"));
    }
}
