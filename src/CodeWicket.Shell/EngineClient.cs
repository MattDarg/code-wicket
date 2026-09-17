using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Launches and supervises the out-of-process engine, owns the JSON-RPC connection over its
    /// stdio, serves the engine's <see cref="IIdeServices"/> callbacks (via <see cref="ShellRpcTarget"/>),
    /// and exposes session control plus the streamed <see cref="AgentEvent"/>s to a host UI.
    /// Events are raised on the JSON-RPC listener thread; UI hosts must marshal to their UI thread.
    /// </summary>
    public sealed class EngineClient : IEngineConnection, IAgentRootQuery, IDisposable, IAsyncDisposable
    {
        private static int _instanceCounter;

        // Null only for a connection over in-memory streams (tests, proofs): there is no process to
        // supervise, and every process-shaped member below must tolerate that.
        private readonly Process? _process;
        private readonly JsonRpc _rpc;
        private readonly int _id;
        private readonly Action<string>? _log;
        private ShellRpcTarget? _shellTarget; // set in Launch (created after the client)
        // The raw-channel tees, when enabled. Held so Dispose can close their log files: the shell is
        // long-lived and restarts the engine in place, so a leaked handle blocks the NEXT run's roll
        // (DiagnosticLog.Roll's File.Move fails and the runs braid into one file).
        private Stream? _recvTee;
        private Stream? _sendTee;

        // Inbound JSON-RPC dispatch, serialized in wire order — the shell hop's half of the issue #33
        // fix, owed since #33 and filed as #277. The engine sends a turn's shell/onAgentEvent
        // notifications in order and awaits each write before answering engine/prompt, so the wire
        // order is right; what this fixes is that StreamJsonRpc completes OUR request's response on a
        // path that does not go through inbound dispatch, so without this the response could land in
        // the host before the last notification that preceded it — a turn's final text, toolDone or
        // error applied after the host had already ended the turn (measured as #267: the error opened
        // the 45-second out-of-turn window over a failure already on screen). Every response we
        // receive is followed by a drain of this queue before it is handed to the caller, so a caller
        // holding a response holds every event that came before it.
        private readonly OrderedDispatchSynchronizationContext _dispatch = new OrderedDispatchSynchronizationContext();

        // Upper bound on that drain. A wedged handler must not hang the caller forever — losing the
        // ordering guarantee is far better than never completing. Same figure as the ACP hop's.
        private static readonly TimeSpan DispatchDrainTimeout = TimeSpan.FromSeconds(5);

        // Upper bound on waiting, after a call fails on a lost connection, for the process's exit to be
        // recorded (issue #299). The pipe closes when the process exits, so the two normally land within
        // the stderr drain of each other; this only bounds a connection lost to something else.
        private static readonly TimeSpan ExitAttributionTimeout = TimeSpan.FromSeconds(3);

        // The engine's exit, watched from before the process started. Null for a connection over
        // in-memory streams, like _process.
        private readonly ProcessWatch? _watch;

        private EngineClient(Process? process, ProcessWatch? watch, JsonRpc rpc, int id, Action<string>? log)
        {
            _process = process;
            _watch = watch;
            _rpc = rpc;
            _id = id;
            _log = log;

            // NOT a Process.Exited subscription made here. This constructor runs at Attach, and the one
            // exit that matters most — .NET's apphost refusing to start the engine — is over in 16-42 ms,
            // long before Attach (the spawn deliberately precedes `ide services`, which costs seconds). A
            // handler added after the event has fired is never invoked, so the exit is watched from Spawn
            // and only its recorded result is read here.
            watch?.Exited.Task.ContinueWith(
                t => EngineExited?.Invoke(t.Result.ExitCode ?? -1),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void RaiseAgentEvent(AgentEventDto ev) => AgentEvent?.Invoke(ev);

        private void RaiseProviderModels(ProviderModelsDto models) => ProviderModelsRefreshed?.Invoke(models);

        /// <summary>Raised on every streamed agent event (text/tool/edit/turn/error).</summary>
        public event Action<AgentEventDto>? AgentEvent;

        /// <inheritdoc />
        public event Action<ProviderModelsDto>? ProviderModelsRefreshed;

        /// <summary>Raised if the engine process exits (carries its exit code).</summary>
        public event Action<int>? EngineExited;

        /// <summary>
        /// Starts the engine executable and begins listening. <paramref name="useFake"/> passes
        /// <c>--fake</c> so the engine uses its in-process fake provider instead of kiro-cli.
        /// <paramref name="log"/> receives the engine's stderr lines.
        /// </summary>
        /// <param name="env">
        /// Extra environment variables to set on the engine process — for settings consumed inside the
        /// engine rather than the shell (e.g. <c>CWKT_ACP_LOG</c>, <c>CWKT_KIRO_CLI</c>).
        /// </param>
        /// <param name="rawLogPath">
        /// Debug only: when set, tees the raw engine↔shell JSON-RPC bytes to this file (engine→shell)
        /// and <c>&lt;path&gt;.send</c> (shell→engine), for diagnosing channel corruption. Null = off,
        /// in which case the streams are passed through untouched.
        /// </param>
        public static EngineClient Launch(
            string engineExecutablePath, IIdeServices ide, bool useFake = false, Action<string>? log = null,
            IReadOnlyDictionary<string, string>? env = null, string? rawLogPath = null) =>
            Attach(Spawn(engineExecutablePath, useFake, log, env), ide, log, rawLogPath);

        /// <summary>
        /// A started engine process that nothing is listening to yet — the first half of
        /// <see cref="Launch"/>, handed to <see cref="Attach"/> to become an <see cref="EngineClient"/>.
        /// </summary>
        /// <remarks>
        /// Exists so a host can get the process running EARLY, before it has an
        /// <see cref="IIdeServices"/> to attach with. The engine does not wait to be spoken to: it
        /// builds its provider registry and starts each backend's model probe as soon as it is up (see
        /// <c>Program</c> and <c>AcpAgentProvider.BeginModelRefresh</c>), so every millisecond it is
        /// alive earlier is probe time overlapped with the host's own startup instead of charged to the
        /// first session. Only <see cref="Attach"/> needs the IDE services, and only for the RPC target.
        ///
        /// <para><b>Owning one of these is a liability until it is attached or killed.</b> Nothing else
        /// tracks it — no <see cref="EngineClient"/> exists yet, so no <c>Dispose</c> can reach it — and
        /// an abandoned engine keeps running with a backend CLI potentially under it. A host that
        /// spawns must kill on every path that doesn't reach <see cref="Attach"/>.</para>
        /// </remarks>
        public sealed class PendingEngine
        {
            internal PendingEngine(Process process, int id, ProcessWatch watch)
            {
                Process = process;
                Id = id;
                Watch = watch;
            }

            internal Process Process { get; }

            internal int Id { get; }

            internal ProcessWatch Watch { get; }

            /// <summary>
            /// Kills an engine that will never be attached. Mirrors <see cref="EngineClient.Dispose"/>'s
            /// kill — plain <c>Kill()</c>, because this slice multi-targets net472 where the
            /// process-tree overload does not exist. The engine has no children of its own until a
            /// session opens, and this only ever runs before one could.
            /// </summary>
            public void Kill()
            {
                Watch.StoppedByHost = true;
                try
                {
                    if (!Process.HasExited)
                        Process.Kill();
                }
                catch { /* already gone */ }
                try { Process.Dispose(); } catch { /* already gone */ }
            }
        }

        /// <summary>
        /// Starts the engine process without wiring the RPC connection to it. See
        /// <see cref="PendingEngine"/> for why the halves are separable, and for the cleanup obligation
        /// that comes with holding one.
        /// </summary>
        public static PendingEngine Spawn(
            string engineExecutablePath, bool useFake = false, Action<string>? log = null,
            IReadOnlyDictionary<string, string>? env = null)
        {
            if (string.IsNullOrWhiteSpace(engineExecutablePath))
                throw new ArgumentException("Engine executable path is required.", nameof(engineExecutablePath));
            if (!File.Exists(engineExecutablePath))
                throw new FileNotFoundException("Engine executable not found.", engineExecutablePath);

            var id = Interlocked.Increment(ref _instanceCounter);
            log?.Invoke($"[lifecycle] EngineClient #{id} launching '{Path.GetFileName(engineExecutablePath)}'");
            LogRpcStack(log); // which StreamJsonRpc/Nerdbank.Streams we actually bound to (VS's shared copy?)

            var psi = new ProcessStartInfo
            {
                FileName = engineExecutablePath,
                Arguments = useFake ? "--fake" : string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(engineExecutablePath) ?? Environment.CurrentDirectory,
            };

            if (env is not null)
                foreach (var kv in env)
                    psi.Environment[kv.Key] = kv.Value;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var watch = new ProcessWatch();

            // stderr carries engine logs; drain it asynchronously so the child never blocks on a full buffer.
            // A tail is also kept (issue #299): when the program that ran is .NET's apphost refusing to
            // start the engine, these lines are .NET's whole account of why, and the pane quotes them.
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    watch.StderrClosed.TrySetResult(true);
                    return;
                }
                watch.AppendStderr(e.Data);
                log?.Invoke(e.Data);
            };

            // Subscribed BEFORE Start, never at Attach: the apphost's refusal is over in 16-42 ms and a
            // Process.Exited handler added after the event has fired is never invoked.
            process.Exited += (_, _) =>
            {
                var code = SafeExitCodeOrNull(process); // now: Dispose may dispose the Process right after
                log?.Invoke($"[lifecycle] EngineClient #{id} engine process exited (code={code?.ToString() ?? "unreadable"})");
                _ = Task.Run(async () =>
                {
                    // stderr's end-of-stream lands after the exit. Wait for it (bounded — a grandchild
                    // holding an inherited handle would keep the pipe open forever) so the account is
                    // whole and the classification line sits BELOW .NET's own lines in engine.log.
                    await Task.WhenAny(watch.StderrClosed.Task, Task.Delay(StderrDrainTimeout)).ConfigureAwait(false);
                    var exit = watch.Snapshot(code);
                    log?.Invoke(watch.StoppedByHost
                        ? $"[lifecycle] EngineClient #{id} engine exit: stopped by the host"
                        : $"[lifecycle] EngineClient #{id} engine exit: {EngineExitDescription.LogLine(exit)}");
                    watch.Exited.TrySetResult(exit);
                });
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start the engine process.");
            process.BeginErrorReadLine();
            log?.Invoke($"[lifecycle] EngineClient #{id} engine started (pid={SafePid(process)})");
            return new PendingEngine(process, id, watch);
        }

        // Bound on waiting for stderr's end after the process exits. The apphost's account arrives in
        // milliseconds; this only stops a held-open pipe from withholding the exit forever.
        private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Wires the RPC connection onto an already-started engine and begins listening. Split from
        /// <see cref="Spawn"/> because this is the only half that needs <paramref name="ide"/>.
        /// </summary>
        /// <remarks>
        /// Nothing the engine writes is lost by attaching late: it answers requests and sends no
        /// unsolicited stdout, and its stderr is already being drained from <see cref="Spawn"/>. The
        /// split is also why the RPC target cannot be late-bound instead — an unattached connection
        /// cannot be called into at all, where a mutable IDE reference on the target would merely
        /// happen to be set in time.
        /// </remarks>
        /// <param name="writes">
        /// Where the agent's file writes are recorded for the IDE tools (issue #257). Optional: the
        /// hosts with no real IDE have nothing to check them against.
        /// </param>
        public static EngineClient Attach(
            PendingEngine pending, IIdeServices ide, Action<string>? log = null, string? rawLogPath = null,
            AgentWriteLedger? writes = null)
        {
            if (pending is null)
                throw new ArgumentNullException(nameof(pending));

            var process = pending.Process;

            // sending = shell -> engine (stdin); receiving = engine -> shell (stdout, where corruption shows).
            return Attach(
                process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process, pending.Watch, pending.Id,
                ide, log, rawLogPath, writes);
        }

        /// <summary>
        /// The real connection over streams that are NOT a process's stdio — for tests and proofs that
        /// need the shell's actual transport (the ordered dispatch, the drain barrier, the RPC target)
        /// against an in-memory engine. Same shape as the Console's client-fs proof: the transport is
        /// what is under test, so the transport is real and only the far end is faked.
        /// </summary>
        /// <param name="watch">An exit to attribute failures to, for a check that has to control WHEN the
        /// exit is recorded relative to the connection being lost — which a real process cannot be made to
        /// do reliably (issue #299).</param>
        internal static EngineClient Attach(
            Stream sending, Stream receiving, IIdeServices ide, Action<string>? log = null, ProcessWatch? watch = null) =>
            Attach(sending, receiving, process: null, watch, Interlocked.Increment(ref _instanceCounter), ide, log, rawLogPath: null, writes: null);

        private static EngineClient Attach(
            Stream sending, Stream receiving, Process? process, ProcessWatch? watch, int id,
            IIdeServices ide, Action<string>? log, string? rawLogPath, AgentWriteLedger? writes)
        {
            TeeStream? recvTee = null, sendTee = null;
            if (!string.IsNullOrWhiteSpace(rawLogPath))
            {
                var dir = Path.GetDirectoryName(rawLogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                // Null sink (log unopenable) degrades to no teeing rather than failing the launch.
                if (DiagnosticLog.OpenTee(rawLogPath!, exclusive: false) is { } recvSink)
                    receiving = recvTee = new TeeStream(receiving, recvSink);
                if (DiagnosticLog.OpenTee(rawLogPath + ".send", exclusive: false) is { } sendSink)
                    sending = sendTee = new TeeStream(sending, sendSink);
            }

            var formatter = NewFormatter();

            // LOAD-BEARING: build the pipes over a private MemoryPool, NOT the default (which draws from
            // the process-global ArrayPool<byte>.Shared). Do not "simplify" this back to the Stream-based
            // handler ctor.
            //
            // Why: when this assembly is loaded in-proc in devenv (the VSIX), our StreamJsonRpc reference
            // binds to VS's already-loaded shared copy (confirmed via [probe] in the engine log:
            // ...\PublicAssemblies\StreamJsonRpc.2.x). StreamJsonRpc's SystemTextJsonFormatter keeps an
            // incoming result as a reference into the PipeReader's buffer and deserializes it lazily (in
            // the InvokeWithCancellationAsync<T> continuation). If that buffer came from the shared pool,
            // other in-proc consumers — recycling shared-pool buffers as that pool is designed for — can reclaim it between receipt
            // and the typed deserialize, so the result deserializes from another connection's bytes
            // (the intermittent "Could not load providers" corruption, diagnosed 2026-06-30).
            //
            // A private pool makes every buffer this connection rents a fresh allocation owned solely by
            // us, so nothing else in the process can recycle it underneath us. Our channel is low-volume,
            // so losing pooling is negligible. The engine side keeps StreamJsonRpc untouched — it's
            // out-of-process and shares nothing.
            //
            // The pipes are built with manual pumps over v1-era System.IO.Pipelines APIs
            // (Pipe/PipeOptions/FlushAsync/ReadAsync, all present since 4.5) instead of
            // PipeReader.Create(Stream, StreamPipeReaderOptions) / PipeWriter.Create (5.0-era).
            // History: a VS 18.3.1 machine threw MissingMethodException here at startup (2026-07-10).
            // The root cause was NOT API age — it was an assembly-identity split: our net472 slice
            // compiled against a newer System.Memory than that VS build's binding redirects cover, so
            // our MemoryPool<byte> and the one in VS's System.IO.Pipelines were different .NET types.
            // The REAL fix is the net472 baseline pin in this csproj (compile against the
            // Microsoft.VisualStudio.SDK-matched versions so every ref sweeps onto VS's own assembly
            // set); the v1-surface pump stays as defense-in-depth against future version-sensitive
            // API additions. No code-level trick can bridge an identity split — even reflection fails,
            // because an object's base type is fixed to one identity at load.
            var pool = new PrivateMemoryPool();
            var reader = PumpStreamToPipe(receiving, pool);
            var writer = PumpPipeToStream(sending, pool);
            var handler = new NewLineDelimitedMessageHandler(writer, reader, formatter);
            var rpc = new JsonRpc(handler);

            // When channel logging is on, route StreamJsonRpc's own verbose trace (every message in/out
            // plus disconnect causes) to the engine log — this is where a foreign frame or a dispose-time
            // fault on our connection would show up. Off by default (rawLogPath null) to avoid hot-path cost.
            if (!string.IsNullOrWhiteSpace(rawLogPath) && log is not null)
            {
                var trace = new TraceSource("cwkt.enginerpc", SourceLevels.Verbose);
                trace.Listeners.Clear(); // drop the DefaultTraceListener (OutputDebugString noise)
                trace.Listeners.Add(new ActionTraceListener(log));
                rpc.TraceSource = trace;
            }

            rpc.Disconnected += (_, e) =>
                log?.Invoke($"[lifecycle] EngineClient #{id} rpc disconnected: reason={e.Reason}; {e.Description}" +
                            (e.Exception is null ? string.Empty : $"; {e.Exception.GetType().Name}: {e.Exception.Message}"));

            var client = new EngineClient(process, watch, rpc, id, log);
            client._recvTee = recvTee;
            client._sendTee = sendTee;
            var target = new ShellRpcTarget(ide, client.RaiseAgentEvent, client.RaiseProviderModels, writes);
            client._shellTarget = target;
            rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions());
            // Dispatch inbound messages through our own FIFO rather than the thread pool, so a
            // response can drain them before it is handed on (see _dispatch / issue #277). Must be set
            // before StartListening.
            rpc.SynchronizationContext = client._dispatch;
            rpc.StartListening();

            return client;
        }

        public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithCancellationAsync<ListProvidersResponse>(RpcMethods.ListProviders, arguments: null, cancellationToken));

        public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<StartSessionResponse>(RpcMethods.StartSession, request, cancellationToken));

        public Task<PromptResponse> PromptAsync(
            string text,
            IReadOnlyList<PromptAttachmentDto>? attachments = null,
            CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<PromptResponse>(
                    RpcMethods.Prompt, new PromptRequest(text, attachments), cancellationToken));

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            // Kill in-flight IDE tool work first, synchronously (issue #23): the engine's
            // session/cancel stops the agent's turn, but the agent abandoning its MCP tools/call
            // never cancels our side — a hung run_tests would otherwise run to its 15-min timeout.
            _shellTarget?.CancelActiveToolInvocations();
            return AfterInboundDrainAsync(_rpc.InvokeAsync(RpcMethods.Cancel));
        }

        /// <summary>
        /// Delivers a follow-up message into the turn already running. Deliberately does NOT cancel
        /// in-flight IDE tool work the way <see cref="CancelAsync"/> does: steering is the opposite of
        /// stopping — the running turn, tool calls and all, is meant to survive it.
        /// </summary>
        public Task<SteerResponse> SteerAsync(
            string text,
            IReadOnlyList<PromptAttachmentDto>? attachments = null,
            CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<SteerResponse>(
                    RpcMethods.Steer, new SteerRequest(text, attachments), cancellationToken));

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync(RpcMethods.SetModel, new SetModelRequest(modelId), cancellationToken));

        public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<SummarizeResponse>(RpcMethods.Summarize, request, cancellationToken));

        public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
            ListBackendSessionsRequest request, CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<ListBackendSessionsResponse>(
                    RpcMethods.ListBackendSessions, request, cancellationToken));

        /// <inheritdoc/>
        public Task<ResolveAgentRootResponse> ResolveAgentRootAsync(
            ResolveAgentRootRequest request, CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<ResolveAgentRootResponse>(
                    RpcMethods.ResolveAgentRoot, request, cancellationToken));

        public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
            TakeImportedHistoryRequest request, CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithParameterObjectAsync<TakeImportedHistoryResponse>(
                    RpcMethods.TakeImportedHistory, request, cancellationToken));

        public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default) =>
            AfterInboundDrainAsync(
                _rpc.InvokeWithCancellationAsync<SessionInfoResponse?>(
                    RpcMethods.SessionInfo, Array.Empty<object>(), cancellationToken));

        /// <summary>
        /// Hands a response on only after every inbound message that preceded it on the wire has been
        /// dispatched (issue #277). Applied to EVERY request rather than to the one it was measured on
        /// (<c>engine/prompt</c>), because the question "does anything act on this response as if the
        /// notifications before it had landed?" has to be answered per call site otherwise, and the
        /// wrong answer is silent — a session's opening frames after its start response, a steer's
        /// acknowledgement after the turn text it displaced. The cost is one sentinel through a queue
        /// that is usually empty.
        /// <para>On every exit path, faulted and cancelled included: the host acts on a failure (the
        /// error card, <c>IsBusy</c> false) exactly as it acts on a result, and the trailing frames
        /// belong ahead of that too.</para>
        /// <para>And a call that failed because the engine process has EXITED says so (issue #299): the
        /// failure is replaced by an <see cref="EngineExitedException"/> carrying the exit code and
        /// stderr, where StreamJsonRpc's own "connection … lost" named neither the process nor why.
        /// Here because every call already passes through it — one place, not one per call site.</para>
        /// </summary>
        private async Task<T> AfterInboundDrainAsync<T>(Task<T> response)
        {
            try { return await response.ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (await ExitBehindFailureAsync(ex).ConfigureAwait(false) is { } exit)
                    throw new EngineExitedException(exit, ex);
                throw;
            }
            finally { await DrainInboundDispatchAsync().ConfigureAwait(false); }
        }

        /// <summary>The non-generic twin of <see cref="AfterInboundDrainAsync{T}"/>.</summary>
        private async Task AfterInboundDrainAsync(Task response)
        {
            try { await response.ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (await ExitBehindFailureAsync(ex).ConfigureAwait(false) is { } exit)
                    throw new EngineExitedException(exit, ex);
                throw;
            }
            finally { await DrainInboundDispatchAsync().ConfigureAwait(false); }
        }

        /// <summary>
        /// The engine's exit, when it is what a failed call ran into; otherwise null and the failure stands.
        /// <para><b>Only a lost connection is WAITED on.</b> The pipe closes when the process exits, so a
        /// call can fail a few milliseconds before the exit is recorded; an ordinary remote error from a
        /// live engine must not be delayed looking for an exit that is not coming. An exit already on
        /// record is used whatever the exception, since every call after it fails by construction.</para>
        /// <para><b>An exit we caused is not reported as one.</b> After <see cref="Dispose"/> or
        /// <see cref="PendingEngine.Kill"/> the failure is the host's own teardown.</para>
        /// </summary>
        private async Task<EngineExit?> ExitBehindFailureAsync(Exception failure)
        {
            if (_watch is null || _watch.StoppedByHost)
                return null;

            var exited = _watch.Exited.Task;
            if (!exited.IsCompleted)
            {
                if (!(failure is ConnectionLostException))
                    return null;
                if (await Task.WhenAny(exited, Task.Delay(ExitAttributionTimeout)).ConfigureAwait(false) != exited)
                    return null;
            }

            return _watch.StoppedByHost ? null : exited.Result;
        }

        /// <summary>
        /// Waits until every inbound message dispatched before now has been handled — the shell hop's
        /// copy of <c>AcpAgentSession.DrainInboundDispatchAsync</c>. Bounded: a stuck handler
        /// degrades to the pre-#277 ordering rather than hanging the caller, and says so in the log,
        /// since the symptom it then permits (a straggler after the turn) is otherwise unattributable.
        /// </summary>
        private async Task DrainInboundDispatchAsync()
        {
            try
            {
                var drained = _dispatch.FlushAsync();
                var first = await Task.WhenAny(drained, Task.Delay(DispatchDrainTimeout)).ConfigureAwait(false);
                if (first != drained)
                    _log?.Invoke($"[rpc] EngineClient #{_id} inbound drain did not complete within {DispatchDrainTimeout.TotalSeconds:0}s; a handler is wedged and a trailing event may land after this response");
            }
            catch
            {
                // Best-effort ordering: never fail a call over the barrier itself.
            }
        }

        private static SystemTextJsonFormatter NewFormatter() => new()
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            },
        };

        // Null, never -1 or 0, when the process will not say: an unreadable code is not a clean exit.
        private static int? SafeExitCodeOrNull(Process p)
        {
            try { return p.ExitCode; }
            catch { return null; }
        }

        /// <summary>
        /// The engine process's exit as observed from before it started: the stderr tail, whether the
        /// host stopped it, and the recorded <see cref="EngineExit"/> (issue #299). Shared by the
        /// <see cref="PendingEngine"/> and the <see cref="EngineClient"/> it becomes, because the exit
        /// can happen on either side of Attach.
        /// </summary>
        internal sealed class ProcessWatch
        {
            // A display bound, and the tail is what is kept: an apphost refusal is ~20 lines, while an
            // engine that ran for hours before dying has written its whole log here.
            internal const int StderrTailLines = 200;

            private readonly Queue<string> _tail = new Queue<string>();
            private int _dropped;
            private volatile bool _stoppedByHost;

            internal TaskCompletionSource<bool> StderrClosed { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<EngineExit> Exited { get; } =
                new TaskCompletionSource<EngineExit>(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Set before the host kills the engine, so its own teardown is never reported as
            /// the engine having exited.</summary>
            internal bool StoppedByHost
            {
                get => _stoppedByHost;
                set => _stoppedByHost = value;
            }

            internal void AppendStderr(string line)
            {
                lock (_tail)
                {
                    _tail.Enqueue(line);
                    if (_tail.Count > StderrTailLines)
                    {
                        _tail.Dequeue();
                        _dropped++;
                    }
                }
            }

            internal EngineExit Snapshot(int? exitCode)
            {
                lock (_tail)
                    return new EngineExit(exitCode, _tail.ToArray(), _dropped);
            }
        }

        private static int SafePid(Process p)
        {
            try { return p.Id; }
            catch { return -1; }
        }

        // Logs which StreamJsonRpc / Nerdbank.Streams / VS.Threading we actually bound to at runtime —
        // confirms (or refutes) that the in-proc shell is running on VS's shared copy.
        private static void LogRpcStack(Action<string>? log)
        {
            if (log is null)
                return;
            try
            {
                var sj = typeof(JsonRpc).Assembly;
                log($"[probe] StreamJsonRpc {sj.GetName().Version} @ {sj.Location}");
                // Which System.IO.Pipelines OUR code binds to (devenv's binding redirects decide; VS
                // servicing builds differ) — and whether it's the same copy StreamJsonRpc got. A split
                // (two Pipelines loaded) breaks type identity across the handler boundary.
                var pipelines = typeof(Pipe).Assembly;
                log($"[probe] System.IO.Pipelines {pipelines.GetName().Version} @ {pipelines.Location}");
                foreach (var name in new[] { "Nerdbank.Streams", "Microsoft.VisualStudio.Threading" })
                {
                    var asm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                    log(asm is null
                        ? $"[probe] {name}: not yet loaded"
                        : $"[probe] {name} {asm.GetName().Version} @ {asm.Location}");
                }
            }
            catch (Exception ex)
            {
                log($"[probe] failed: {ex.Message}");
            }
        }

        // Bridges the engine's stdout into a PipeReader for StreamJsonRpc, using only v1-era
        // System.IO.Pipelines APIs (see the version-skew comment in Launch). Segments come from the
        // private pool, preserving the isolation guarantee; the local read buffer is our own allocation.
        // EOF or a stream fault completes the pipe so the RPC connection observes disconnection.
        private static PipeReader PumpStreamToPipe(Stream stream, MemoryPool<byte> pool)
        {
            var pipe = new Pipe(new PipeOptions(pool, useSynchronizationContext: false));
            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        if (read <= 0)
                            break; // EOF: engine exited / stdout closed
                        pipe.Writer.Write(new ReadOnlySpan<byte>(buffer, 0, read));
                        var flushed = await pipe.Writer.FlushAsync().ConfigureAwait(false);
                        if (flushed.IsCompleted)
                            break; // reader (the RPC connection) is done with us
                    }
                    pipe.Writer.Complete();
                }
                catch (Exception ex)
                {
                    pipe.Writer.Complete(ex);
                }
            });
            return pipe.Reader;
        }

        // Bridges StreamJsonRpc's outbound PipeWriter onto the engine's stdin, v1 APIs only (see above).
        // Flushes the stream after each drained batch — stdin is line-delimited JSON-RPC, so the engine
        // must see complete frames promptly, not whenever a buffer happens to fill.
        private static PipeWriter PumpPipeToStream(Stream stream, MemoryPool<byte> pool)
        {
            var pipe = new Pipe(new PipeOptions(pool, useSynchronizationContext: false));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
                        var buffer = result.Buffer;
                        foreach (var segment in buffer)
                        {
                            // net472 streams lack the Memory<byte> overloads; the segments are
                            // array-backed (our private pool allocates arrays), so unwrap directly.
                            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(segment, out ArraySegment<byte> array))
                                await stream.WriteAsync(array.Array!, array.Offset, array.Count).ConfigureAwait(false);
                            else
                            {
                                var copy = segment.ToArray();
                                await stream.WriteAsync(copy, 0, copy.Length).ConfigureAwait(false);
                            }
                        }
                        pipe.Reader.AdvanceTo(buffer.End);
                        await stream.FlushAsync().ConfigureAwait(false);
                        if (result.IsCompleted)
                            break; // writer (the RPC connection) completed: nothing more to send
                    }
                    pipe.Reader.Complete();
                }
                catch (Exception ex)
                {
                    pipe.Reader.Complete(ex);
                }
            });
            return pipe.Writer;
        }

        // A MemoryPool that never shares storage with ArrayPool<byte>.Shared: every rental is a fresh
        // allocation owned solely by this connection. Used to isolate our pipes from VS's process-global
        // pool (which other in-proc consumers recycle as designed) — the source of the cross-connection frame corruption.
        // Our channel is low-volume, so the lost pooling is negligible.
        private sealed class PrivateMemoryPool : MemoryPool<byte>
        {
            public override int MaxBufferSize => int.MaxValue;

            public override IMemoryOwner<byte> Rent(int minBufferSize = -1) =>
                new Owner(minBufferSize <= 0 ? 4096 : minBufferSize);

            protected override void Dispose(bool disposing) { }

            private sealed class Owner : IMemoryOwner<byte>
            {
                public Owner(int size) => Memory = new byte[size];
                public Memory<byte> Memory { get; }
                public void Dispose() { }
            }
        }

        // Forwards StreamJsonRpc's TraceSource output to the engine log, line-buffered and thread-safe.
        private sealed class ActionTraceListener : TraceListener
        {
            private readonly Action<string> _log;
            private readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();
            private readonly object _gate = new object();

            public ActionTraceListener(Action<string> log) => _log = log;

            public override void Write(string? message)
            {
                lock (_gate)
                    _buffer.Append(message);
            }

            public override void WriteLine(string? message)
            {
                lock (_gate)
                {
                    _buffer.Append(message);
                    var line = _buffer.ToString();
                    _buffer.Clear();
                    try { _log("[rpc] " + line); }
                    catch { /* best effort */ }
                }
            }
        }

        /// <summary>Synchronous teardown (all underlying work is synchronous): dispose RPC, kill the engine.</summary>
        public void Dispose()
        {
            _log?.Invoke($"[lifecycle] EngineClient #{_id} disposing (rpc.Dispose then Kill)");
            // First, before anything can fail a call or end the process: what follows is our teardown,
            // not the engine exiting (issue #299).
            if (_watch is not null)
                _watch.StoppedByHost = true;
            // The tool shellouts run in THIS process (the in-proc IIdeServices), so killing the
            // engine below does not stop them — cancel explicitly or a test host outlives the window.
            _shellTarget?.CancelActiveToolInvocations();
            _rpc.Dispose();
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                        _process.Kill();
                }
                catch { /* already exited */ }

                _process.Dispose();
            }

            // Last, so the pumps have stopped: closes the tee log files (and the process stdio they
            // wrap). Best-effort — an in-flight pump write faults into TeeStream's own catch.
            try { _recvTee?.Dispose(); } catch { }
            try { _sendTee?.Dispose(); } catch { }
            _recvTee = _sendTee = null;

            _log?.Invoke($"[lifecycle] EngineClient #{_id} disposed");
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
