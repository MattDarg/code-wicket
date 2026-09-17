using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Engine.Mcp
{
    /// <summary>
    /// Hosts the IDE <see cref="IToolCatalog"/> as an MCP server over a uniquely-named local pipe,
    /// and produces the <see cref="McpServerSpec"/> that tells the agent how to reach it.
    ///
    /// The agent (Kiro) only knows how to spawn <em>stdio</em> MCP servers, but the catalog lives in
    /// this already-running engine (it proxies tool calls back to Visual Studio over IPC). So the spec
    /// points the agent at this same engine executable in its <c>--mcp-stdio</c> relay mode; that thin
    /// child process connects to this pipe and pumps bytes between the agent's stdio and the
    /// <see cref="McpToolServer"/> hosted here. Each pipe connection gets its own server instance.
    /// </summary>
    public sealed class McpPipeHost : IAsyncDisposable
    {
        /// <summary>The argument that switches the engine into stdio↔pipe relay mode.</summary>
        public const string RelayFlag = "--mcp-stdio";

        private const string ServerName = IdeMcpServer.Name;

        private readonly IToolCatalog _tools;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<McpToolServer, byte> _servers = new();
        private readonly Action<McpBridgeStatus>? _onStatus;
        private Task? _acceptLoop;

        // The bridge's state folded across CONNECTIONS, which is the scope the user cares about: a
        // relay respawn opens a fresh pipe and a fresh McpToolServer, and a call count that reset with
        // it would tell the user their tools had been called fewer times than they had. The host
        // outlives every connection it accepts, so the running total lives here rather than in the
        // per-connection server, and each connection's report is folded in field-wise.
        private readonly object _statusLock = new();
        private bool _bridgeConnected;
        private int? _bridgeToolsServed;
        private IReadOnlyList<string>? _bridgeToolNames;
        private int _bridgeToolCallsBefore;

        private McpPipeHost(IToolCatalog tools, string pipeName, Action<McpBridgeStatus>? onStatus)
        {
            _tools = tools;
            _pipeName = pipeName;
            _onStatus = onStatus;
        }

        /// <summary>
        /// Folds one connection's report into the host-wide state and republishes. Tool calls
        /// accumulate across connections; everything else is newest-wins, except that a null
        /// <c>ToolsServed</c> never overwrites a real count — a reconnecting client that has not asked
        /// for the catalog yet has not un-served the tools it was already given.
        /// </summary>
        private void FoldAndPublish(McpToolServer connection, McpBridgeStatus fromConnection)
        {
            McpBridgeStatus folded;
            lock (_statusLock)
            {
                _bridgeConnected = fromConnection.Connected;
                if (fromConnection.ToolsServed is { } served)
                {
                    _bridgeToolsServed = served;
                    _bridgeToolNames = fromConnection.ToolNames;
                }

                // Record first, then total EVERY live connection — not the retired tally plus the one
                // that happened to report. _liveCalls exists precisely because more than one connection
                // can be live at once (a relay respawn overlaps the old one), and summing only the
                // reporter made the published count DROP when the new connection reported its first
                // status: 5 calls became 0, and nothing republished until the old connection finally
                // completed. A count that goes backwards is worse than no count, because the panel is
                // read as a record of what the agent has done.
                _liveCalls[connection] = fromConnection.ToolCalls;

                var live = 0;
                foreach (var tally in _liveCalls.Values)
                    live += tally;

                folded = new McpBridgeStatus(
                    _bridgeConnected,
                    _bridgeToolsServed,
                    _bridgeToolNames,
                    _bridgeToolCallsBefore + live);
            }

            _onStatus?.Invoke(folded);
        }

        // What each live connection has counted so far, so a connection that ends can retire its tally
        // into the running total without it being counted twice or lost.
        private readonly ConcurrentDictionary<McpToolServer, int> _liveCalls = new();

        /// <summary>
        /// Starts a pipe host for the catalog and returns it together with the spec to hand the agent.
        /// The accept loop runs until the host is disposed.
        /// </summary>
        public static (McpPipeHost Host, McpServerSpec Spec) Start(
            IToolCatalog tools, Action<McpBridgeStatus>? onStatus = null)
        {
            var pipeName = $"cwkt-mcp-{Guid.NewGuid():N}";
            var host = new McpPipeHost(tools, pipeName, onStatus);
            host._acceptLoop = Task.Run(() => host.AcceptLoopAsync(host._cts.Token));

            // Published BEFORE any agent connects, and it is what makes "can this pane show MCP status
            // at all" answerable immediately. A host exists from here on, so a non-null bridge status
            // means "we are hosting" while Connected:false means "no agent has handshaken yet" — two
            // different facts that a host gate evaluated only on the first handshake would conflate
            // with "this backend speaks no MCP". That matters because the session is warm-started
            // before the user types (issue #19), so this window is precisely when someone looks.
            onStatus?.Invoke(new McpBridgeStatus(Connected: false, ToolsServed: null, ToolNames: null, ToolCalls: 0));

            return (host, BuildSpec(pipeName));
        }

        /// <summary>The spec describing this engine in relay mode as a stdio MCP server.</summary>
        private static McpServerSpec BuildSpec(string pipeName)
        {
            var (command, leadingArgs) = ResolveSelfLaunch();
            var args = new List<string>(leadingArgs) { RelayFlag, pipeName };
            return new McpServerSpec(ServerName, command, args);
        }

        /// <summary>
        /// Resolves how to relaunch this engine. For a published apphost the process path is the exe
        /// itself; when run as <c>dotnet Engine.dll</c> the host is dotnet, so prepend the assembly path.
        /// </summary>
        private static (string Command, IReadOnlyList<string> LeadingArgs) ResolveSelfLaunch()
        {
            var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            var entryDll = Assembly.GetEntryAssembly()?.Location;

            if (processPath is not null
                && entryDll is { Length: > 0 }
                && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return (processPath, new[] { entryDll });
            }

            return (processPath ?? entryDll ?? "dotnet", Array.Empty<string>());
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp-pipe] cannot create pipe '{_pipeName}': {ex.Message}");
                    return;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mcp-pipe] accept failed: {ex.Message}");
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                // A relay connected: serve MCP over this pipe. The server's JSON-RPC connection
                // completes when the relay disconnects, at which point we drop it.
                McpToolServer? server = null;
                server = new McpToolServer(
                    pipe, pipe, _tools,
                    onStatus: status => FoldAndPublish(server!, status));
                _servers.TryAdd(server, 0);
                server.Start();
                _ = server.Completion.ContinueWith(
                    completed =>
                    {
                        // Retire this connection's tally into the running total before dropping it, so
                        // a relay respawn neither loses the calls it made nor double-counts them.
                        if (_liveCalls.TryRemove(server, out var counted))
                        {
                            lock (_statusLock)
                                _bridgeToolCallsBefore += counted;
                        }

                        _servers.TryRemove(server, out _);
                        server.Dispose();
                        pipe.Dispose();
                    },
                    TaskScheduler.Default);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            foreach (var server in _servers.Keys)
            {
                try { server.Dispose(); } catch { /* ignore */ }
            }
            _servers.Clear();

            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.ConfigureAwait(false); }
                catch { /* accept loop teardown is best-effort */ }
            }

            _cts.Dispose();
        }
    }
}
