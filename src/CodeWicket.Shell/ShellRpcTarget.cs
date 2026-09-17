using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;

namespace CodeWicket.Shell
{
    /// <summary>
    /// The shell's side of the engine↔shell JSON-RPC boundary: it receives streamed
    /// <c>shell/onAgentEvent</c> notifications (forwarded to the supplied callback for the UI)
    /// and serves the engine's <see cref="IIdeServices"/> callbacks from the supplied services.
    /// This is the production counterpart of the Console host's FakeShellService.
    /// </summary>
    /// <remarks>
    /// <para>Intentionally exposes no public events: StreamJsonRpc inspects an RPC target's public events
    /// and only supports EventHandler-shaped delegates, so the agent-event fan-out is a plain callback.</para>
    /// <para><b>Every handler here is entered on the connection's ordered dispatch pump</b> (issue #277;
    /// see <c>EngineClient</c> and <c>OrderedDispatchSynchronizationContext</c>), which exists so the
    /// agent-event notifications are handled in wire order and can be drained before a response is
    /// handed on. The pump runs one callback at a time, so whatever a handler does BEFORE its first
    /// await holds every frame behind it. The two notification handlers do nothing but hand the DTO to
    /// a callback that posts to the UI thread, which is what the pump is for. The request handlers do
    /// real work — a permission prompt parked on the user, a shellout, a workspace capture that
    /// switches to the VS main thread — and none of that is ours to know the synchronous cost of: the
    /// message-box permission fallback blocks its caller for as long as the dialog is open, and a tool
    /// may do real work before yielding. So each of them leaves the pump at once (<see cref="OffPump"/>)
    /// and runs on the pool, which is where StreamJsonRpc ran them before the pump existed — the
    /// pump is for ORDER, and these have no ordering claim on the event stream.</para>
    /// </remarks>
    internal sealed class ShellRpcTarget
    {
        private readonly IIdeServices _ide;
        private readonly Action<AgentEventDto> _onAgentEvent;
        private readonly Action<ProviderModelsDto> _onProviderModels;
        // The files the agent has written, for the IDE tools to check against the loaded projects
        // (issue #257). Recorded HERE because this is the one point every write passes through in
        // the shell process, before any UI filtering: the mirrored diff of a backend's own write
        // arrives as an agent event, and a client-fs write arrives as the WriteAsync call below.
        private readonly AgentWriteLedger? _writes;

        // In-flight tool invocations, so the host's cancel path can cut them short (issue #23):
        // clicking Stop cancels the agent's ACP turn, but the agent abandoning its MCP tools/call
        // never cancels OUR invocation — a hung run_tests shellout kept running (holding the
        // build-output DLLs) until its internal 15-minute timeout. Keyed by CTS; value unused.
        private readonly ConcurrentDictionary<CancellationTokenSource, byte> _activeToolInvocations = new();

        public ShellRpcTarget(
            IIdeServices ide, Action<AgentEventDto> onAgentEvent, Action<ProviderModelsDto> onProviderModels,
            AgentWriteLedger? writes = null)
        {
            _ide = ide;
            _onAgentEvent = onAgentEvent;
            _onProviderModels = onProviderModels;
            _writes = writes;
        }

        [JsonRpcMethod(RpcMethods.OnAgentEvent, UseSingleObjectParameterDeserialization = true)]
        public void OnAgentEvent(AgentEventDto ev)
        {
            // Every edit event names its file; the path is already canonical from the mapper, which
            // rooted it against the agent's cwd. Recorded before the fan-out so a retired or off-screen
            // turn's write — still a real write on disk — is not lost with the rendering.
            if (ev is { Type: "edit" } && !string.IsNullOrEmpty(ev.Path))
                _writes?.Record(ev.Path);
            _onAgentEvent(ev);
        }

        [JsonRpcMethod(RpcMethods.OnProviderModels, UseSingleObjectParameterDeserialization = true)]
        public void OnProviderModels(ProviderModelsDto models) => _onProviderModels(models);

        /// <summary>
        /// Runs IDE work off the ordered dispatch pump — see the type remarks. A plain pool hop, which
        /// is exactly the environment these handlers had before the pump: no ambient
        /// <see cref="SynchronizationContext"/>, so nothing inside them posts continuations back onto
        /// the pump either.
        /// </summary>
        private static Task<T> OffPump<T>(Func<Task<T>> work) => Task.Run(work);

        [JsonRpcMethod(RpcMethods.WorkspaceCapture)]
        public async Task<WorkspaceSnapshotDto> CaptureAsync() =>
            DtoMapping.ToDto(await OffPump(() => _ide.Workspace.CaptureAsync()).ConfigureAwait(false));

        [JsonRpcMethod(RpcMethods.EditsRead, UseSingleObjectParameterDeserialization = true)]
        public async Task<ReadFileResponse> ReadAsync(ReadFileRequest request) =>
            new ReadFileResponse(
                await OffPump(() => _ide.Edits.ReadTextFileAsync(request.Path, request.Line, request.Limit)).ConfigureAwait(false));

        [JsonRpcMethod(RpcMethods.EditsWrite, UseSingleObjectParameterDeserialization = true)]
        public async Task<WriteFileResponse> WriteAsync(WriteFileRequest request)
        {
            var result = await OffPump(() => _ide.Edits.WriteTextFileAsync(request.Path, request.Content)).ConfigureAwait(false);
            // The client-fs route: the applier resolved the path, so prefer its spelling. A backend on
            // this route also reports the diff on its completed frame, so the entry is usually made
            // twice — idempotent by canonical path.
            _writes?.Record(result?.ResolvedPath ?? request.Path);
            return result is null
                ? new WriteFileResponse(null, null, null)
                : new WriteFileResponse(result.ResolvedPath, result.OldText, result.NewText);
        }

        [JsonRpcMethod(RpcMethods.ToolsList)]
        public Task<ToolListResponse> ToolsListAsync() =>
            Task.FromResult(new ToolListResponse(_ide.Tools.Tools.Select(DtoMapping.ToDto).ToList()));

        [JsonRpcMethod(RpcMethods.ToolsInvoke, UseSingleObjectParameterDeserialization = true)]
        public async Task<ToolResultDto> ToolsInvokeAsync(InvokeToolRequest request, CancellationToken cancellationToken = default)
        {
            // Linked, not the wire token directly: the wire token covers IPC-propagated cancellation
            // (the engine dropping the call), while CancelActiveToolInvocations covers the user's
            // Stop click — which the agent never relays to us.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeToolInvocations.TryAdd(cts, 0);
            ToolResult result;
            try
            {
                result = await OffPump(() => _ide.Tools.InvokeAsync(request.Name, request.ArgumentsJson, cts.Token)).ConfigureAwait(false);
            }
            finally
            {
                _activeToolInvocations.TryRemove(cts, out _);
            }

            // Side-channel: some backends (Kiro) don't relay our MCP tool result back in their ACP frames,
            // so the host can't render it from the transcript. Our structured results (run_tests, and the
            // breakpoint payloads since issue #73) are DATA the UI turns into a card — deliver them
            // straight to the chat here, where we hold the real result AND the channel that feeds the
            // transcript. Claude (which does echo the result) reaches this too, so the UI dedupes on each
            // payload's own unique field and shows it once.
            // Prefix match, not Contains: the tool serializes the marker as the FIRST property, and a
            // result that merely quotes the marker text (e.g. a file-read of this repo's own source)
            // must not fire a spurious event. The marker is Core's, shared with the mapper and the
            // view-model (see StructuredToolResult) — and this asks "is it ours" rather than naming the
            // kinds, so a new payload cannot be forgotten here and arrive with no card at all.
            var json = result.ContentJson;
            if (StructuredToolResult.SyntheticToolCallId(json) is { } syntheticId)
                _onAgentEvent(new AgentEventDto
                {
                    Type = "toolDone",
                    ToolCallId = syntheticId,
                    Success = !result.IsError,
                    Message = json,
                    // The one place a card's provenance is known, and therefore the only place it is
                    // asserted: these bytes are what our own IToolCatalog just returned, on this thread,
                    // for a call the host made. The marker above only says WHICH kind of payload this
                    // is - it is the agent's own tool result text on every other route, so a bare prefix
                    // match would let an agent that prints the marker forge a host-authored card. The
                    // view-model builds a card from this flag alone (ChatViewModel.IsHostsOwnDelivery).
                    HostAuthored = true,
                });

            return DtoMapping.ToDto(result);
        }

        /// <summary>
        /// Cancels every in-flight tool invocation (issue #23). Called by the host's stop path
        /// (<see cref="EngineClient.CancelAsync"/>) alongside the engine's <c>session/cancel</c>, and on
        /// dispose so a long shellout (run_tests) can't outlive the connection inside the host process.
        /// The cancelled tool returns an error result ("tool invocation was cancelled") to the agent,
        /// whose turn is being cancelled anyway.
        /// </summary>
        public void CancelActiveToolInvocations()
        {
            foreach (var cts in _activeToolInvocations.Keys)
            {
                // A racing completion may have disposed the CTS between the snapshot and here.
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        [JsonRpcMethod(RpcMethods.PermissionsRequest, UseSingleObjectParameterDeserialization = true)]
        public async Task<PermissionDecisionDto> PermissionAsync(PermissionRequestDto request) =>
            DtoMapping.ToDto(
                await OffPump(() => _ide.Permissions.RequestAsync(DtoMapping.ToRequest(request))).ConfigureAwait(false));
    }
}
