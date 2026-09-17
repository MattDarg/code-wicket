using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Engine;
using CodeWicket.Engine.Mcp;
using CodeWicket.Providers.ClaudeCode;
using CodeWicket.Providers.Kiro;
using CodeWicket.Shell;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// When, after <c>session/new</c> returns, can a prompt rely on OUR MCP tools being there — and
    /// which signal says so?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported from the field on Kiro v3: the first prompt sometimes goes out before Code Wicket's
    /// own tools have registered, and the agent answers as if it had none. Never seen on Claude. The
    /// engine.log timeline says why: Claude's adapter connects MCP servers while <c>session/new</c> is
    /// in flight, Kiro v3 returns <c>session/new</c> first and connects them seconds later.
    /// </para>
    /// <para>
    /// This proof measures three candidate gates on a fresh session each, sending the same prompt —
    /// "call our echo tool with this token" — the instant the gate opens, and reporting whether the
    /// tool was actually invoked through the bridge:
    /// <list type="bullet">
    /// <item><c>immediate</c> — no gate: send the moment <c>session/new</c> returns (the field case).</item>
    /// <item><c>served</c> — OUR signal: the bridge has answered the backend's <c>tools/list</c>.</item>
    /// <item><c>announced</c> — the BACKEND's signal: it has announced our server connected (v2's
    /// <c>server_initialized</c>, v3's <c>_kiro/mcp/status</c> roster, Claude's equivalent).</item>
    /// </list>
    /// Each case also prints the timeline (bridge connected, tools served, backend announcement)
    /// relative to <c>session/new</c> returning, so the cost of each gate is a number.
    /// </para>
    /// <para>This is a MEASUREMENT: it exits 0 whenever every session opened. The verdict is the table.</para>
    /// </remarks>
    internal static class McpReadinessProof
    {
        private static readonly string UnderscoredServer = IdeMcpServer.Name.Replace('-', '_');

        internal static async Task<int> RunAsync(string[] args)
        {
            var backend = args.Length > 0 ? args[0].ToLowerInvariant() : "kiro";
            var cases = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (cases.Count == 0)
                cases = new List<string> { "immediate", "served", "announced" };
            var engine = args.FirstOrDefault(a => a.StartsWith("--engine=", StringComparison.Ordinal))?.Substring("--engine=".Length);

            Console.WriteLine($"== code-wicket console: MCP readiness gate measurement ({backend}{(engine is null ? "" : " " + engine)}) ==");
            Console.WriteLine();

            var results = new List<(string Case, string Verdict)>();
            var failedToOpen = false;
            foreach (var name in cases)
            {
                Console.WriteLine($"---- case: {name} ----");
                var (verdict, opened) = await RunCaseAsync(backend, engine, name).ConfigureAwait(false);
                results.Add((name, verdict));
                failedToOpen |= !opened;
                Console.WriteLine();
            }

            Console.WriteLine("==== summary ====");
            foreach (var (c, v) in results)
                Console.WriteLine($"{c,-10} {v}");
            return failedToOpen ? 1 : 0;
        }

        private static async Task<(string Verdict, bool Opened)> RunCaseAsync(string backend, string? engine, string gate)
        {
            var workDir = HostScratch.ResolveDir("console/mcp-ready-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = Stopwatch.StartNew();
                var timeline = new List<(long Ms, string What)>();
                void Note(string what) { lock (timeline) timeline.Add((clock.ElapsedMilliseconds, what)); }

                var catalog = new EchoCatalog();
                var served = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var announced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var (host, spec) = StartBridge(catalog, status =>
                {
                    if (status.Connected) Note("bridge: backend connected to our pipe");
                    if (status.ToolsServed is { } n && n > 0)
                    {
                        Note($"bridge: served tools/list ({n} tools)");
                        served.TrySetResult(true);
                    }
                });
                await using var _ = host;

                void OnEvent(AgentEvent ev)
                {
                    switch (ev)
                    {
                        case AgentEvent.McpServerConnected c when IdeMcpServer.IsOurServer(c.ServerName):
                            Note($"backend: announced our server connected ('{c.ServerName}')");
                            announced.TrySetResult(true);
                            break;
                        case AgentEvent.McpRosterUpdated r
                            when r.Roster.Servers.Any(s => s.IsConnected && IdeMcpServer.IsOurServer(s.Name)):
                            Note("backend: roster shows our server connected");
                            announced.TrySetResult(true);
                            break;
                        case AgentEvent.ToolCallStarted t:
                            Note($"tool call: {t.Title}");
                            break;
                    }
                }

                IAgentProvider provider = backend == "claude"
                    ? new ClaudeCodeAgentProvider()
                    : KiroProvider(engine);
                var ide = new ConsoleIdeServices(workDir);

                Note("session/new sent");
                IAgentSession session;
                try
                {
                    session = await provider.StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            McpServers = new[] { spec },
                            OutOfTurnEvents = OnEvent,
                            ModelId = backend == "claude" ? "sonnet" : null,
                        },
                        ide).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"session start FAILED: {ex.Message}");
                    return ($"START FAILED: {ex.Message}", false);
                }

                await using (session)
                {
                    var opened = clock.ElapsedMilliseconds;
                    Note("session/new returned");

                    // The gate under measurement. A bounded wait, because a signal that never comes is
                    // itself a result worth printing rather than a hang.
                    var waitStart = clock.ElapsedMilliseconds;
                    string gateResult;
                    switch (gate)
                    {
                        case "served":
                            gateResult = await WaitAsync(served.Task, TimeSpan.FromSeconds(30)).ConfigureAwait(false) ? "opened" : "TIMED OUT (30 s)";
                            break;
                        case "announced":
                            gateResult = await WaitAsync(announced.Task, TimeSpan.FromSeconds(30)).ConfigureAwait(false) ? "opened" : "TIMED OUT (30 s)";
                            break;
                        default:
                            gateResult = "none";
                            break;
                    }
                    var waited = clock.ElapsedMilliseconds - waitStart;
                    Note($"gate '{gate}' {gateResult} after {waited} ms; sending");

                    var token = "READY-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var prompt =
                        $"Use the 'echo' tool from the {UnderscoredServer} MCP server to echo exactly this text: {token}. " +
                        "Call the tool; do not repeat the text yourself. If you have no such tool, say so in one sentence.";
                    var text = new StringBuilder();
                    await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                    {
                        OnEvent(ev);
                        if (ev is AgentEvent.AssistantTextDelta d) text.Append(d.Text);
                    }
                    Note("turn ended");

                    var invoked = catalog.Invocations.Any(a => a.Contains(token, StringComparison.Ordinal));

                    Console.WriteLine($"workspace = {workDir}");
                    Console.WriteLine("timeline (ms after session/new was sent):");
                    lock (timeline)
                        foreach (var (ms, what) in timeline)
                            Console.WriteLine($"  {ms,7}  {what}");
                    Console.WriteLine($"agent said: {Truncate(text.ToString().Trim(), 200)}");
                    Console.WriteLine($"our tool invoked through the bridge: {invoked}");

                    var servedAt = FirstAt(timeline, "served tools/list");
                    var announcedAt = FirstAt(timeline, "backend:");
                    var verdict =
                        (invoked ? "TOOL CALLED" : "TOOL MISSING")
                        + $" — gate {gateResult}, waited {waited} ms; session/new returned at +{opened} ms"
                        + (servedAt is { } s ? $", tools served at +{s} ms" : ", tools never served")
                        + (announcedAt is { } a ? $", backend announced at +{a} ms" : ", backend never announced");
                    return (verdict, true);
                }
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        private static long? FirstAt(List<(long Ms, string What)> timeline, string marker)
        {
            lock (timeline)
                return timeline.Where(t => t.What.Contains(marker, StringComparison.Ordinal)).Select(t => (long?)t.Ms).FirstOrDefault();
        }

        private static async Task<bool> WaitAsync(Task<bool> signal, TimeSpan timeout)
        {
            var winner = await Task.WhenAny(signal, Task.Delay(timeout)).ConfigureAwait(false);
            return winner == signal;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        private static KiroAgentProvider KiroProvider(string? engine)
        {
            var options = new KiroProviderOptions { AgentEngine = engine };
            if (Environment.GetEnvironmentVariable("CWKT_KIRO_ARGS") is { Length: > 0 } extra)
                options.ExtraArgs = extra.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new KiroAgentProvider(options);
        }

        /// <summary>The same re-pointing the kiro-mcp proof does: the pipe host lives in this console,
        /// so the spec the backend launches has to be the engine assembly in relay mode.</summary>
        private static (McpPipeHost Host, McpServerSpec Spec) StartBridge(IToolCatalog catalog, Action<McpBridgeStatus> onStatus)
        {
            var (host, rawSpec) = McpPipeHost.Start(catalog, onStatus);
            var pipeName = rawSpec.Args[rawSpec.Args.Count - 1];
            var engineDll = typeof(EngineHost).Assembly.Location;
            var spec = new McpServerSpec(UnderscoredServer, "dotnet", new[] { engineDll, McpPipeHost.RelayFlag, pipeName });
            return (host, spec);
        }

        private sealed class EchoCatalog : IToolCatalog
        {
            private readonly List<string> _invocations = new();

            public IReadOnlyList<string> Invocations
            {
                get { lock (_invocations) return _invocations.ToList(); }
            }

            public IReadOnlyList<ToolDescriptor> Tools { get; } = new[]
            {
                new ToolDescriptor(
                    "echo",
                    "Echoes the input text back.",
                    "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}"),
            };

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                lock (_invocations) _invocations.Add(argumentsJson);
                return Task.FromResult(new ToolResult(IsError: false, argumentsJson));
            }
        }
    }
}
