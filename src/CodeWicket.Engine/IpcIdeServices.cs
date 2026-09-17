using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;

namespace CodeWicket.Engine
{
    /// <summary>
    /// Engine-side proxy implementing <see cref="IIdeServices"/> by forwarding every call over
    /// JSON-RPC to the VS shell — the only process that can touch buffers/Roslyn/Error List.
    /// </summary>
    public sealed class IpcIdeServices : IIdeServices
    {
        private IpcIdeServices(JsonRpc rpc, string rootPath, IReadOnlyList<ToolDescriptor> tools)
        {
            Workspace = new IpcWorkspaceContext(rpc, rootPath);
            Edits = new IpcEditApplier(rpc);
            Tools = new IpcToolCatalog(rpc, tools);
            Permissions = new IpcPermissionHandler(rpc);
        }

        public IWorkspaceContext Workspace { get; }
        public IEditApplier Edits { get; }
        public IToolCatalog Tools { get; }
        public IPermissionHandler Permissions { get; }

        /// <summary>Builds the proxy, eagerly fetching the tool list (IToolCatalog.Tools is synchronous).</summary>
        public static async Task<IpcIdeServices> CreateAsync(JsonRpc rpc, string rootPath, CancellationToken cancellationToken = default)
        {
            var list = await rpc.InvokeWithCancellationAsync<ToolListResponse>(RpcMethods.ToolsList, arguments: null, cancellationToken)
                .ConfigureAwait(false);
            var tools = list.Tools.Select(DtoMapping.ToDescriptor).ToList();
            return new IpcIdeServices(rpc, rootPath, tools);
        }

        private sealed class IpcWorkspaceContext : IWorkspaceContext
        {
            private readonly JsonRpc _rpc;
            public IpcWorkspaceContext(JsonRpc rpc, string rootPath) { _rpc = rpc; RootPath = rootPath; }

            public string? RootPath { get; }

            public async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
            {
                var dto = await _rpc.InvokeWithCancellationAsync<WorkspaceSnapshotDto>(RpcMethods.WorkspaceCapture, arguments: null, cancellationToken)
                    .ConfigureAwait(false);
                return DtoMapping.ToSnapshot(dto);
            }
        }

        private sealed class IpcEditApplier : IEditApplier
        {
            private readonly JsonRpc _rpc;
            public IpcEditApplier(JsonRpc rpc) => _rpc = rpc;

            public async Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default)
            {
                var response = await _rpc.InvokeWithParameterObjectAsync<ReadFileResponse>(
                    RpcMethods.EditsRead, new ReadFileRequest(path, line, limit), cancellationToken).ConfigureAwait(false);
                return response.Content;
            }

            public async Task<FileWriteResult?> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default)
            {
                var response = await _rpc.InvokeWithParameterObjectAsync<WriteFileResponse>(
                    RpcMethods.EditsWrite, new WriteFileRequest(path, content), cancellationToken).ConfigureAwait(false);

                // A host that can't report leaves the fields null — pass that through as "no capture"
                // rather than inventing empty strings, which downstream would read as a real diff.
                return response?.ResolvedPath is null || response.OldText is null || response.NewText is null
                    ? null
                    : new FileWriteResult(response.ResolvedPath, response.OldText, response.NewText);
            }

            // Diff previews are raised by the shell from the UI (click handler), never by the engine,
            // so the engine-side proxy has nothing to forward.
            public Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class IpcToolCatalog : IToolCatalog
        {
            private readonly JsonRpc _rpc;
            public IpcToolCatalog(JsonRpc rpc, IReadOnlyList<ToolDescriptor> tools) { _rpc = rpc; Tools = tools; }

            public IReadOnlyList<ToolDescriptor> Tools { get; }

            public async Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                var dto = await _rpc.InvokeWithParameterObjectAsync<ToolResultDto>(
                    RpcMethods.ToolsInvoke, new InvokeToolRequest(toolName, argumentsJson), cancellationToken).ConfigureAwait(false);
                return DtoMapping.ToResult(dto);
            }
        }

        private sealed class IpcPermissionHandler : IPermissionHandler
        {
            private readonly JsonRpc _rpc;
            public IpcPermissionHandler(JsonRpc rpc) => _rpc = rpc;

            public async Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                var dto = await _rpc.InvokeWithParameterObjectAsync<PermissionDecisionDto>(
                    RpcMethods.PermissionsRequest, DtoMapping.ToDto(request), cancellationToken).ConfigureAwait(false);
                return DtoMapping.ToDecision(dto);
            }
        }
    }
}
