using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Engine;
using CodeWicket.Engine.Mcp;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Providers.ClaudeCode;
using CodeWicket.Providers.Kiro;
using CodeWicket.Shell;

namespace CodeWicket.ConsoleHost
{
    internal static class Program
    {
        // How a live agent spells our MCP server. Kiro v3 normalizes server names to underscores, so
        // the proofs ask for that form — DERIVED from the const rather than written out, because
        // these strings go to a real backend: a stale one asks for a server that does not exist and
        // the proof fails opaquely, which is the failure a proof is supposed to make visible.
        private static readonly string UnderscoredServer = IdeMcpServer.Name.Replace('-', '_');

        private static async Task<int> Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0].TrimStart('-') : "fake";
            return mode switch
            {
                "kiro" => await RunRealKiroAsync().ConfigureAwait(false),
                "claude" => await RunRealClaudeCodeAsync().ConfigureAwait(false),
                "claude-models" => await RunRealClaudeModelsProofAsync().ConfigureAwait(false),
                "claude-steer" => await RunRealClaudeSteerProofAsync().ConfigureAwait(false),
                "claude-steer-tools" => await RunRealClaudeSteerToolsProofAsync().ConfigureAwait(false),
                "claude-steer-boundary" => await RunRealClaudeSteerBoundaryProofAsync(
                    args.Length > 1 ? args[1] : null).ConfigureAwait(false),
                "claude-edit" => await RunRealClaudeCodeEditProofAsync().ConfigureAwait(false),
                "custom-agent" => await RunCustomAgentProofAsync().ConfigureAwait(false),
                "kiro-edit" => await RunRealKiroEditProofAsync().ConfigureAwait(false),
                "kiro-read" => await RunRealKiroReadProofAsync().ConfigureAwait(false),
                "kiro-steering" => await RunRealKiroSteeringProofAsync().ConfigureAwait(false),
                "project-settings" => await ProjectSettingsProof.RunAsync(
                    args.Length > 1 ? args[1] : null).ConfigureAwait(false),
                "legacy-tests" => await LegacyTestProjectProof.RunAsync(
                    args.Length > 1 ? args[1] : null).ConfigureAwait(false),
                "resume-cross-root" => await ResumeCrossRootProof.RunAsync(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null).ConfigureAwait(false),
                "resume-unknown-id" => await ResumeCrossRootProof.RunAsync(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null,
                    ResumeCrossRootProof.Leg.UnknownId).ConfigureAwait(false),
                "kiro-image" => await RunImagePromptProofAsync("kiro").ConfigureAwait(false),
                "claude-image" => await RunImagePromptProofAsync("claude").ConfigureAwait(false),
                "kiro-v3-models" => await RunRealKiroV3ModelsProofAsync().ConfigureAwait(false),
                "kiro-tasks" => await RunRealKiroTasksProofAsync(
                    args.Length > 1 ? args[1] : null).ConfigureAwait(false),
                "kiro-resume" => await RunRealKiroResumeProofAsync(
                    args.Length > 1 ? args[1] : null).ConfigureAwait(false),
                "engine" => await RunEngineIpcAsync().ConfigureAwait(false),
                "engine-claude" => await RunEngineClaudeAsync().ConfigureAwait(false),
                "claude-mcp" => await RunRealClaudeMcpProofAsync().ConfigureAwait(false),
                "mcp-bridge" => await RunMcpBridgeAsync().ConfigureAwait(false),
                "kiro-mcp" => await RunRealKiroMcpProofAsync().ConfigureAwait(false),
                "kiro-vstools" => await RunRealKiroVsToolsProofAsync().ConfigureAwait(false),
                "clientfs-errors" => await RunClientFsErrorProofAsync().ConfigureAwait(false),
                "claude-subagents" => await RunRealClaudeSubagentsProofAsync().ConfigureAwait(false),
                "claude-sessions" => await RunClaudeSessionsProofAsync(
                    args.Length > 1 ? args[1] : null,
                    args.Length > 2 ? args[2] : null).ConfigureAwait(false),
                "codefix-actions" => await Probes.CodeFixActionsProof.RunAsync().ConfigureAwait(false),
                "notice-replay" => await BackendNoticeReplayProof.RunAsync().ConfigureAwait(false),
                "backend-gate" => await BackendGateProof.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "mcp-ready" => await McpReadinessProof.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                _ => await RunFakeAsync().ConfigureAwait(false),
            };
        }

        internal static SystemTextJsonFormatter NewFormatter() => new()
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            },
        };

        /// <summary>
        /// Issue #111: what does a failed client-fs call actually LOOK LIKE to the agent?
        ///
        /// <para>The applier's exception crosses <b>two</b> StreamJsonRpc hops before the agent sees it
        /// — shell to engine, then engine to agent — and the engine rethrows what the first hop handed
        /// it. That is the same wrapping <c>AcpAgentSession.DescribeError</c> exists to undo for our own
        /// errors, where <c>Message</c> is a generic protocol word and the reason rides in <c>data</c>.
        /// Nobody had looked at what survives.</para>
        ///
        /// <para><b>Why this matters more than the equivalent in issue #82:</b> the reader is the AGENT,
        /// not a person. A human given a useless error asks a question; an agent either retries the
        /// identical write or concludes the edit succeeded and tells the user it fixed the file.</para>
        ///
        /// <para>The stack below is the real one — real <see cref="ShellRpcTarget"/>, real
        /// <see cref="IpcIdeServices"/>, real <see cref="AcpClientTarget"/> — wired over in-memory
        /// duplex streams, with only the applier faked so each failure can be provoked deterministically.
        /// A read-only file, a denied directory and a locked handle are not reproducible on demand across
        /// machines, and the TRANSPORT is what is under test here, not the IO.</para>
        /// </summary>
        private static async Task<int> RunClientFsErrorProofAsync()
        {
            Console.WriteLine("== code-wicket console: client-fs failures as the AGENT sees them (issue #111) ==");
            Console.WriteLine();

            var cases = new (string Label, string Method, Exception Thrown)[]
            {
                ("write / access denied", "fs/write_text_file",
                    new UnauthorizedAccessException("Access to the path 'C:\\repo\\locked.cs' is denied.")),
                ("write / file in use", "fs/write_text_file",
                    new IOException("The process cannot access the file 'C:\\repo\\open.cs' because it is being used by another process.")),
                ("write / invalid path", "fs/write_text_file",
                    new ArgumentException("Illegal characters in path.", "path")),
                ("read / file not found", "fs/read_text_file",
                    new FileNotFoundException("File not found: C:\\repo\\gone.cs", "C:\\repo\\gone.cs")),
            };

            var allCarriedTheReason = true;
            foreach (var (label, method, thrown) in cases)
            {
                var seen = await CaptureAgentSideFailureAsync(method, thrown).ConfigureAwait(false);
                var carried = seen.Contains(thrown.Message, StringComparison.Ordinal);
                allCarriedTheReason &= carried;

                Console.WriteLine("-- " + label + " --");
                Console.WriteLine("   applier threw : " + thrown.GetType().Name + ": " + thrown.Message);
                Console.WriteLine("   agent received: " + seen);
                Console.WriteLine(carried
                    ? "   => the reason SURVIVED both hops"
                    : "   => the reason was LOST; the agent cannot tell what went wrong");
                Console.WriteLine();
            }

            Console.WriteLine(allCarriedTheReason
                ? "PASS: every client-fs failure reaches the agent with its reason intact."
                : "FAIL: at least one failure reached the agent as a bare protocol error.");
            return allCarriedTheReason ? 0 : 1;
        }

        /// <summary>
        /// Drives one client-fs call through the real two-hop stack and returns, as one line, everything
        /// the agent can see about the failure — message, JSON-RPC code and error data. Whatever this
        /// string omits is genuinely unavailable to a backend.
        /// </summary>
        private static async Task<string> CaptureAgentSideFailureAsync(string method, Exception thrown)
        {
            var workDir = HostScratch.ResolveDir("console/clientfs");

            // Hop 1: shell <-> engine. The shell side is the real target over a faked applier.
            var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();
            var shellIde = new ThrowingIdeServices(workDir, thrown);
            using var shellRpc = new JsonRpc(new NewLineDelimitedMessageHandler(shellEnd, shellEnd, NewFormatter()));
            shellRpc.AddLocalRpcTarget(new ShellRpcTarget(shellIde, _ => { }, _ => { }), new JsonRpcTargetOptions());
            shellRpc.StartListening();

            using var engineRpc = new JsonRpc(new NewLineDelimitedMessageHandler(engineEnd, engineEnd, NewFormatter()));
            engineRpc.StartListening();
            var ipcIde = await IpcIdeServices.CreateAsync(engineRpc, workDir).ConfigureAwait(false);

            // Hop 2: engine <-> agent. The engine side is the real ACP client target over that proxy.
            var (agentEnd, clientEnd) = FullDuplexStream.CreatePair();
            using var clientRpc = new JsonRpc(new NewLineDelimitedMessageHandler(clientEnd, clientEnd, NewFormatter()));
            clientRpc.AddLocalRpcTarget(
                new AcpClientTarget(ipcIde, _ => { }, () => "s1"), new JsonRpcTargetOptions());
            clientRpc.StartListening();

            using var agentRpc = new JsonRpc(new NewLineDelimitedMessageHandler(agentEnd, agentEnd, NewFormatter()));
            agentRpc.StartListening();

            try
            {
                await agentRpc.InvokeWithParameterObjectAsync<JsonElement>(
                    method,
                    new { sessionId = "s1", path = "C:\\repo\\target.cs", content = "// text" }).ConfigureAwait(false);
                return "(no error — the call SUCCEEDED, which is itself a failure of this proof)";
            }
            catch (RemoteInvocationException ex)
            {
                var data = ex.DeserializedErrorData ?? ex.ErrorData;
                return "code=" + ex.ErrorCode + " message=\"" + ex.Message + "\" data=" + DescribeData(data);
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string DescribeData(object? data)
        {
            if (data is null)
                return "(none)";
            // CommonErrorData is what StreamJsonRpc attaches when it serializes an exception; its
            // ToString() is just the type name, so unpack the fields an agent could actually read.
            var text = data switch
            {
                JsonElement je => je.ToString(),
                StreamJsonRpc.Protocol.CommonErrorData common =>
                    "{type=" + common.TypeName + ", message=" + common.Message
                    + ", stack=" + (string.IsNullOrEmpty(common.StackTrace) ? "none" : "present") + "}",
                _ => data.ToString() ?? string.Empty,
            };
            text = text.Replace("\r", string.Empty).Replace("\n", " ");
            return text.Length > 400 ? text.Substring(0, 400) + "…" : text;
        }

        /// <summary>An <see cref="IIdeServices"/> whose edit applier always fails the same way, so a
        /// specific failure can be provoked without depending on machine state.</summary>
        private sealed class ThrowingIdeServices : IIdeServices
        {
            public ThrowingIdeServices(string rootPath, Exception thrown)
            {
                var inner = new StubIdeServices(rootPath);
                Workspace = inner.Workspace;
                Tools = inner.Tools;
                Permissions = inner.Permissions;
                Edits = new ThrowingEditApplier(thrown);
            }

            public IWorkspaceContext Workspace { get; }
            public IEditApplier Edits { get; }
            public IToolCatalog Tools { get; }
            public IPermissionHandler Permissions { get; }

            private sealed class ThrowingEditApplier : IEditApplier
            {
                private readonly Exception _thrown;

                public ThrowingEditApplier(Exception thrown) => _thrown = thrown;

                public Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default) =>
                    throw _thrown;

                public Task<FileWriteResult?> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
                    throw _thrown;

                public Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default) =>
                    Task.CompletedTask;
            }
        }

        /// <summary>Proves the engine↔shell IPC boundary and the in-engine MCP server, offline.</summary>
        private static async Task<int> RunEngineIpcAsync()
        {
            Console.WriteLine("== code-wicket console: engine + IPC boundary (offline proof) ==");

            var roundTripOk = await ProveEngineRoundTripAsync().ConfigureAwait(false);
            Console.WriteLine();
            var importOk = await ProveImportedHistoryPullAsync().ConfigureAwait(false);
            Console.WriteLine();
            var mcpOk = await ProveMcpServerAsync().ConfigureAwait(false);
            Console.WriteLine();

            var passed = roundTripOk && importOk && mcpOk;
            Console.WriteLine(passed
                ? "PASS: engine IPC round-trip + imported-history pull + MCP server verified."
                : "FAIL: see output above.");
            return passed ? 0 : 1;
        }

        /// <summary>
        /// The imported-history hop (issue #108): start a session with
        /// <see cref="StartSessionRequest.ImportHistory"/>, then PULL the captured conversation in
        /// pages over <c>engine/takeImportedHistory</c>.
        /// <para>The payload size is the point of the leg, not decoration. The transcript is paged
        /// rather than returned with the session because a large StreamJsonRpc result comes back
        /// deserialized from a NUL-padded span and throws — so the one number that says whether that
        /// could ever be collapsed back into the response is how many bytes the entries actually
        /// weigh. Printed for the fake's four entries here; the real figure is a live import's, and
        /// this leg is what makes the units comparable.</para>
        /// <para>Order is asserted rather than counts alone: a page assembled by anything other than
        /// the offset would still return the right number of entries.</para>
        /// </summary>
        private static async Task<bool> ProveImportedHistoryPullAsync()
        {
            Console.WriteLine("-- imported history pull (engine/takeImportedHistory, issue #108) --");

            var workDir = HostScratch.ResolveDir("console/engine-import");
            var ide = new ConsoleIdeServices(workDir);

            var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();

            var registry = new AgentProviderRegistry();
            registry.Register(new FakeAgentProvider());
            await using var engine = new EngineHost(engineEnd, engineEnd, registry, "fake");
            engine.Start();

            var shellService = new FakeShellService(ide);
            using var shellRpc = new JsonRpc(new NewLineDelimitedMessageHandler(shellEnd, shellEnd, NewFormatter()));
            shellRpc.AddLocalRpcTarget(shellService, new JsonRpcTargetOptions());
            shellRpc.StartListening();

            const string ImportedId = "cli-conversation-42";
            var start = await shellRpc.InvokeWithParameterObjectAsync<StartSessionResponse>(
                RpcMethods.StartSession,
                new StartSessionRequest("fake", null, workDir, "Prompt", ImportedId, ImportHistory: true))
                .ConfigureAwait(false);

            Console.WriteLine($"  conversation id        : {start.ConversationId}");
            Console.WriteLine($"  ImportedHistoryCount   : "
                + (start.ImportedHistoryCount?.ToString() ?? "(null — no import requested)"));
            Console.WriteLine($"  ImportedHistoryTruncated: {start.ImportedHistoryTruncated}");

            // Two pages of two, so the offset is exercised rather than one call taking everything.
            var pulled = new List<ImportedEntryDto>();
            var total = start.ImportedHistoryCount ?? 0;
            var payloadBytes = 0;
            var pages = 0;
            while (pulled.Count < total)
            {
                var page = await shellRpc.InvokeWithParameterObjectAsync<TakeImportedHistoryResponse>(
                    RpcMethods.TakeImportedHistory,
                    new TakeImportedHistoryRequest(pulled.Count, 2)).ConfigureAwait(false);
                if (page.Entries.Count == 0)
                    break;
                pulled.AddRange(page.Entries);
                pages++;
            }

            // What the entries WEIGH on the wire, measured on the same formatter options the engine
            // serialises with - the figure that decides whether the pull could ever collapse into the
            // start response.
            payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
                pulled, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }).Length;

            Console.WriteLine($"  pulled                 : {pulled.Count} entr(ies) over {pages} page(s)");
            Console.WriteLine($"  PAYLOAD BYTES          : {payloadBytes} bytes"
                + (pulled.Count > 0 ? $" ({payloadBytes / pulled.Count} bytes/entry)" : string.Empty));
            foreach (var e in pulled)
                Console.WriteLine($"      {e.Role,-5}  {e.Event?.Type ?? "(text)"}  {Preview(e.Text ?? e.Event?.Title)}");

            // The id has to be the one we asked to resume: the fake echoes it, so a session that
            // ignored ResumeConversationId would show up here rather than as a right-sized transcript
            // belonging to the wrong conversation.
            var idEchoed = start.ConversationId == ImportedId;
            var countReported = start.ImportedHistoryCount == 4;
            var pulledAll = pulled.Count == 4;

            // Wire ORDER, not just the count. The fake's script is user -> tool start -> edit ->
            // completion, which is also the one thing a page assembled off anything but the offset
            // would get wrong while still returning four rows.
            var orderKept = pulledAll
                && pulled[0].Role == "user"
                && pulled[1].Event?.Type == "toolStart"
                && pulled[2].Event?.Type == "edit"
                && pulled[3].Event?.Type == "toolDone";

            // The user entry carries TEXT and no event, the agent entries the reverse - the shape the
            // shell's persisted TranscriptEntry takes, and the whole reason the DTO has both fields.
            var userShaped = pulledAll
                && pulled[0].Role == "user"
                && !string.IsNullOrEmpty(pulled[0].Text)
                && pulled[0].Event is null
                && pulled.Skip(1).All(e => e.Role == "agent" && e.Event is not null);

            if (!idEchoed)
                Console.WriteLine($"  FAIL: conversation id is '{start.ConversationId}', expected '{ImportedId}'.");
            if (!countReported)
                Console.WriteLine($"  FAIL: ImportedHistoryCount was {start.ImportedHistoryCount?.ToString() ?? "null"}, expected 4.");
            if (!pulledAll)
                Console.WriteLine($"  FAIL: pulled {pulled.Count} entries, expected 4.");
            if (pulledAll && !orderKept)
                Console.WriteLine("  FAIL: the pages did not come back in wire order.");
            if (pulledAll && !userShaped)
                Console.WriteLine("  FAIL: the entries are not shaped as user-text / agent-event.");

            return idEchoed && countReported && pulledAll && orderKept && userShaped;
        }

        private static string Preview(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var oneLine = text!.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length <= 60 ? oneLine : oneLine.Substring(0, 60) + "…";
        }

        private static async Task<bool> ProveEngineRoundTripAsync()
        {
            Console.WriteLine("-- shell <-> engine round-trip (FakeAgentProvider) --");

            var workDir = HostScratch.ResolveDir("console/engine");
            var ide = new ConsoleIdeServices(workDir);

            var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();

            var registry = new AgentProviderRegistry();
            registry.Register(new FakeAgentProvider());
            await using var engine = new EngineHost(engineEnd, engineEnd, registry, "fake");
            engine.Start();

            var shellService = new FakeShellService(ide);
            using var shellRpc = new JsonRpc(new NewLineDelimitedMessageHandler(shellEnd, shellEnd, NewFormatter()));
            shellRpc.AddLocalRpcTarget(shellService, new JsonRpcTargetOptions());
            shellRpc.StartListening();

            var start = await shellRpc.InvokeWithParameterObjectAsync<StartSessionResponse>(
                RpcMethods.StartSession, new StartSessionRequest("fake", null, workDir, "Prompt", null)).ConfigureAwait(false);
            Console.WriteLine($"started session: {start.ConversationId}");

            // Live model switch (engine/setModel -> session.SetModelAsync) — the mid-session /model path.
            await shellRpc.InvokeWithParameterObjectAsync(
                RpcMethods.SetModel, new SetModelRequest("fake-smart")).ConfigureAwait(false);
            Console.WriteLine("set model on live session: fake-smart");

            var prompt = await shellRpc.InvokeWithParameterObjectAsync<PromptResponse>(
                RpcMethods.Prompt, new PromptRequest("hello")).ConfigureAwait(false);
            Console.WriteLine($"prompt stop reason: {prompt.StopReason}");

            var wrote = ide.WrittenFiles.Count > 0;
            Console.WriteLine(wrote
                ? $"  edit marshaled back to shell host: {ide.WrittenFiles[0]}"
                : "  NO edit routed back to the shell!");

            return wrote && prompt.StopReason == "end_turn";
        }

        private static async Task<bool> ProveMcpServerAsync()
        {
            Console.WriteLine("-- in-engine MCP server (backed by IToolCatalog) --");

            var (clientEnd, serverEnd) = FullDuplexStream.CreatePair();
            var server = new McpToolServer(serverEnd, serverEnd, new EchoToolCatalog());
            server.Start();

            using var client = new JsonRpc(new NewLineDelimitedMessageHandler(clientEnd, clientEnd, NewFormatter()));
            client.StartListening();

            // clientInfo is sent so the proof's output carries the handshake line in the shape a real
            // agent produces (issue #122, part 3). That line and the tool count beside it are what
            // engine.log holds when someone is asking why the IDE tools never showed up.
            await client.InvokeWithParameterObjectAsync<JsonElement>(
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    clientInfo = new { name = "console-proof", version = "0.1" },
                }).ConfigureAwait(false);

            var tools = await client.InvokeAsync<JsonElement>("tools/list").ConfigureAwait(false);
            var toolNames = tools.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString())
                .ToList();
            Console.WriteLine($"  tools/list -> [{string.Join(", ", toolNames)}]");

            var call = await client.InvokeWithParameterObjectAsync<JsonElement>(
                "tools/call", new { name = "echo", arguments = new { text = "hi" } }).ConfigureAwait(false);
            var text = call.GetProperty("content")[0].GetProperty("text").GetString();
            Console.WriteLine($"  tools/call echo -> {text}");

            return toolNames.Contains("echo") && text is not null && text.Contains("hi");
        }

        /// <summary>
        /// Proves the full MCP bridge transport offline, exactly as Kiro would use it: host the tool
        /// catalog over a named pipe (<see cref="McpPipeHost"/>), spawn the engine in
        /// <c>--mcp-stdio</c> relay mode as a child process, and drive an MCP client over that child's
        /// stdio — standing in for Kiro. Asserts tools/list + tools/call round-trip through the relay.
        /// </summary>
        private static async Task<int> RunMcpBridgeAsync()
        {
            Console.WriteLine("== code-wicket console: MCP bridge transport (offline proof) ==");
            Console.WriteLine("-- McpPipeHost (named pipe) <- engine --mcp-stdio relay <- MCP client (as Kiro) --");

            var (host, spec) = McpPipeHost.Start(new EchoToolCatalog());
            await using var _ = host;

            // The spec's command targets whichever process hosts the pipe (this console). For the
            // proof we instead launch the real engine assembly in relay mode against the same pipe.
            var pipeName = spec.Args[spec.Args.Count - 1];
            var engineDll = typeof(EngineHost).Assembly.Location;

            using var relay = new Process
            {
                StartInfo =
                {
                    FileName = "dotnet",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            relay.StartInfo.ArgumentList.Add(engineDll);
            relay.StartInfo.ArgumentList.Add(McpPipeHost.RelayFlag);
            relay.StartInfo.ArgumentList.Add(pipeName);
            relay.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"  [relay] {e.Data}"); };
            relay.Start();
            relay.BeginErrorReadLine();

            try
            {
                using var client = new JsonRpc(new NewLineDelimitedMessageHandler(
                    relay.StandardInput.BaseStream, relay.StandardOutput.BaseStream, NewFormatter()));
                client.StartListening();

                await client.InvokeWithParameterObjectAsync<JsonElement>("initialize", new { protocolVersion = "2025-06-18" }).ConfigureAwait(false);

                var tools = await client.InvokeAsync<JsonElement>("tools/list").ConfigureAwait(false);
                var toolNames = tools.GetProperty("tools").EnumerateArray()
                    .Select(t => t.GetProperty("name").GetString())
                    .ToList();
                Console.WriteLine($"  tools/list -> [{string.Join(", ", toolNames)}]");

                var call = await client.InvokeWithParameterObjectAsync<JsonElement>(
                    "tools/call", new { name = "echo", arguments = new { text = "through the bridge" } }).ConfigureAwait(false);
                var text = call.GetProperty("content")[0].GetProperty("text").GetString();
                Console.WriteLine($"  tools/call echo -> {text}");

                var passed = toolNames.Contains("echo") && text is not null && text.Contains("through the bridge");
                Console.WriteLine();
                Console.WriteLine(passed
                    ? "PASS: MCP tool call round-tripped through the engine stdio relay + named pipe."
                    : "FAIL: see output above.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { if (!relay.HasExited) relay.Kill(); } catch { /* best-effort */ }
            }
        }

        /// <summary>Drives the real provider against an in-memory fake ACP agent (no Kiro needed).</summary>
        private static async Task<int> RunFakeAsync()
        {
            Console.WriteLine("== code-wicket console: fake ACP agent (offline proof) ==");

            var workDir = HostScratch.ResolveDir("console/acp");
            var ide = new ConsoleIdeServices(workDir);

            var (clientStream, serverStream) = FullDuplexStream.CreatePair();
            var fake = new FakeAcpAgent(serverStream, "greeting.txt");
            fake.Start();

            var session = new AcpAgentSession(
                new SessionOptions { WorkspaceRootPath = workDir },
                ide,
                new InMemoryAcpConnection(clientStream));

            await session.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"initialized; conversation id = {session.ConversationId}");
            Console.WriteLine();

            await foreach (var ev in session.SendAsync(new PromptInput("Say hello and write a file.")))
                PrintEvent(ev);

            await session.DisposeAsync().ConfigureAwait(false);

            var passed = ide.WrittenFiles.Count > 0;
            Console.WriteLine();
            Console.WriteLine(passed
                ? $"PASS: agent edit routed through host IEditApplier -> {ide.WrittenFiles[0]}"
                : "FAIL: no edit was routed through the host.");
            return passed ? 0 : 1;
        }

        /// <summary>
        /// Proves that a *real* kiro-cli connects to the IDE tool catalog we advertise via
        /// <c>session/new</c> mcpServers and actually calls one of our tools. Hosts the catalog over a
        /// named pipe (<see cref="McpPipeHost"/>), points Kiro at the engine relay, and asks Kiro to
        /// use the echo tool; asserts our catalog recorded the invocation. Requires kiro-cli + auth.
        /// </summary>
        private static async Task<int> RunRealKiroMcpProofAsync()
        {
            Console.WriteLine("== code-wicket console: real kiro-cli MCP tool proof ==");

            var workDir = HostScratch.ResolveDir("console/kiro-mcp-" + Guid.NewGuid().ToString("N"));

            var catalog = new RecordingEchoCatalog();
            var (host, spec) = StartKiroMcpBridge(catalog);
            await using var _ = host;

            var ide = new ConsoleIdeServices(workDir);
            var provider = CreateKiroProvider();

            // Kiro's MCP servers connect asynchronously, so the readiness notices land BEFORE the first
            // prompt — out of turn, on a path the SendAsync loop below cannot see (issue #19). Collected
            // here because how the backend reports them is per-ENGINE: the default engine sends one
            // `_kiro.dev/mcp/server_initialized` per server, v3 sends none at all and publishes a
            // repeated `_kiro/mcp/status` roster instead (issue #97), and the user's transcript is
            // supposed to look the same either way.
            var connected = new List<string>();
            void NoteMcp(AgentEvent ev)
            {
                if (ev is AgentEvent.McpServerConnected c)
                {
                    lock (connected) connected.Add(c.ServerName);
                    Console.WriteLine($"  [mcp] {c.ServerName} connected");
                }
            }

            var token = "BRIDGE-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await using var session = await provider
                .StartSessionAsync(
                    new SessionOptions
                    {
                        WorkspaceRootPath = workDir,
                        McpServers = new[] { spec },
                        OutOfTurnEvents = NoteMcp,
                    },
                    ide)
                .ConfigureAwait(false);

            Console.WriteLine($"workspace = {workDir}");
            Console.WriteLine($"conversation id = {session.ConversationId}");
            Console.WriteLine();

            var prompt =
                $"Use the 'echo' tool from the {UnderscoredServer} MCP server to echo exactly this text: {token}. " +
                "Call the tool, do not just repeat the text yourself.";
            await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
            {
                PrintEvent(ev);
                NoteMcp(ev);
            }

            var invoked = catalog.Invocations.Any(a => a.Contains(token));
            // Our own bridge is the one server this proof can be sure exists, so it is what the notice
            // assertion names — a run on a machine with no user-configured MCP servers still means
            // something. Kiro renames it to the spec id (underscores), hence a contains rather than an
            // equality check.
            var announced = connected.Any(n => n.Replace('-', '_').Contains(UnderscoredServer));

            Console.WriteLine();
            Console.WriteLine($"  tool invocations recorded: {catalog.Invocations.Count}");
            foreach (var a in catalog.Invocations)
                Console.WriteLine($"    - {a}");
            Console.WriteLine($"  MCP servers announced as connected: [{string.Join(", ", connected)}]");

            var passed = invoked && announced;
            Console.WriteLine(passed
                ? "PASS: real Kiro connected to our MCP server, announced it, and called the echo tool through the bridge."
                : invoked
                    ? "FAIL: the tool call worked, but no MCP server was ever announced as connected — "
                      + "this engine reports readiness by a frame we do not handle (issue #97)."
                    : "FAIL: Kiro did not invoke our tool with the expected token.");
            return passed ? 0 : 1;
        }

        /// <summary>
        /// Proves real kiro-cli discovers and invokes a *no-argument* tool whose descriptor matches
        /// the production <c>VsToolCatalog</c> (build_solution / get_diagnostics). VsToolCatalog itself
        /// needs a live DTE, so this stands in with the identical descriptors + canned results — it
        /// de-risks the in-Visual-Studio path (does Kiro accept the names + empty-args schema and call them?).
        /// </summary>
        private static async Task<int> RunRealKiroVsToolsProofAsync()
        {
            Console.WriteLine("== code-wicket console: real kiro-cli VS-tools shape proof ==");

            var workDir = HostScratch.ResolveDir("console/kiro-vstools-" + Guid.NewGuid().ToString("N"));

            var catalog = new StubVsToolCatalog();
            var (host, spec) = StartKiroMcpBridge(catalog);
            await using var _ = host;

            var ide = new ConsoleIdeServices(workDir);
            var provider = CreateKiroProvider();

            await using var session = await provider
                .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir, McpServers = new[] { spec } }, ide)
                .ConfigureAwait(false);

            Console.WriteLine($"conversation id = {session.ConversationId}");
            Console.WriteLine();

            var prompt =
                $"Build the current solution using the build_solution tool from the {UnderscoredServer} " +
                "MCP server, then tell me whether it succeeded. Call the tool — do not guess.";
            await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                PrintEvent(ev);

            var passed = catalog.Invoked.Contains("build_solution");
            Console.WriteLine();
            Console.WriteLine($"  tools invoked: [{string.Join(", ", catalog.Invoked)}]");
            Console.WriteLine(passed
                ? "PASS: real Kiro discovered and invoked the no-arg build_solution tool through the bridge."
                : "FAIL: Kiro did not invoke build_solution.");
            return passed ? 0 : 1;
        }

        /// <summary>Sets up the named-pipe MCP bridge and the ACP spec that points Kiro at the engine relay.</summary>
        private static (McpPipeHost Host, McpServerSpec Spec) StartKiroMcpBridge(IToolCatalog catalog)
        {
            var (host, rawSpec) = McpPipeHost.Start(catalog);
            // The host's spec command targets whichever process hosts the pipe (this console), which
            // isn't a relay. Re-point it at the engine assembly in relay mode against the same pipe.
            // In production the spec command is the engine apphost exe; `dotnet <engineDll>` is equivalent.
            var pipeName = rawSpec.Args[rawSpec.Args.Count - 1];
            var engineDll = typeof(EngineHost).Assembly.Location;
            var spec = new McpServerSpec(
                UnderscoredServer,
                "dotnet",
                new[] { engineDll, McpPipeHost.RelayFlag, pipeName });
            return (host, spec);
        }

        /// <summary>An echo tool catalog that records every invocation's arguments, for the MCP proof.</summary>
        private sealed class RecordingEchoCatalog : IToolCatalog
        {
            public List<string> Invocations { get; } = new();

            public IReadOnlyList<ToolDescriptor> Tools { get; } = new[]
            {
                new ToolDescriptor(
                    "echo",
                    "Echoes the input text back.",
                    "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}"),
            };

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                Invocations.Add(argumentsJson);
                return Task.FromResult(toolName == "echo"
                    ? new ToolResult(IsError: false, $"{{\"echoed\":{argumentsJson}}}")
                    : new ToolResult(IsError: true, "{\"error\":\"unknown tool\"}"));
            }
        }

        /// <summary>
        /// Stand-in for the real <c>VsToolCatalog</c> (which needs a live DTE): the identical no-arg
        /// build_solution / get_diagnostics descriptors with canned results, so the live tool shapes can
        /// be exercised against real Kiro offline. Keep these descriptors in sync with VsToolCatalog.
        /// </summary>
        private sealed class StubVsToolCatalog : IToolCatalog
        {
            public List<string> Invoked { get; } = new();

            public IReadOnlyList<ToolDescriptor> Tools { get; } = new[]
            {
                new ToolDescriptor(
                    "build_solution",
                    "Build the current Visual Studio solution exactly as the IDE builds it and report whether it succeeded.",
                    "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
                new ToolDescriptor(
                    "get_diagnostics",
                    "Return the current Visual Studio Error List: errors and warnings with file, line, column and message.",
                    "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
            };

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                Invoked.Add(toolName);
                return Task.FromResult(toolName switch
                {
                    "build_solution" => new ToolResult(IsError: false, "{\"succeeded\":true,\"projectsFailed\":0,\"errorCount\":0,\"warningCount\":0,\"errors\":[]}"),
                    "get_diagnostics" => new ToolResult(IsError: false, "{\"count\":0,\"diagnostics\":[]}"),
                    _ => new ToolResult(IsError: true, "{\"error\":\"unknown tool\"}"),
                });
            }
        }

        /// <summary>
        /// Proves the config-only custom-agent path end-to-end against a real adapter
        /// (claude-agent-acp standing in for "some ACP CLI the user configured"): (1) the save-time
        /// probe (AcpAgentProbe) launches it and completes a real initialize handshake, discovering
        /// loadSession; (2) the same JSON parses through AcpAgentConfigJson into a plain
        /// AcpAgentProvider whose capabilities refresh from initialize on session start (the entry
        /// declares no capabilities, so the baseline lacks ResumeSession until discovery adds it).
        /// </summary>
        private static async Task<int> RunCustomAgentProofAsync()
        {
            Console.WriteLine("== code-wicket console: config-only custom ACP agent proof ==");

            const string entryJson =
                "[{\"providerId\":\"my-claude\",\"displayName\":\"My Claude (custom)\"," +
                "\"cliPath\":\"claude-agent-acp.cmd\"}]";

            // (1) The settings-save probe: real process, real initialize.
            var parsedOk = CustomAcpAgent.TryParseList(entryJson, out var agents, out var parseError);
            if (!parsedOk || agents.Length != 1)
            {
                Console.WriteLine($"FAIL: entry JSON did not parse: {parseError}");
                return 1;
            }

            var probe = AcpAgentProbe.Probe(agents[0]);
            Console.WriteLine($"probe ok        : {probe.Ok}{(probe.Ok ? string.Empty : $" ({probe.Error})")}");
            Console.WriteLine($"probe agentInfo : {probe.AgentInfo ?? "(none)"}");
            Console.WriteLine($"probe loadSession: {probe.LoadSession}");
            if (!probe.Ok)
                return 1;

            // (2) The engine path: JSON -> AcpAgentConfig -> plain provider -> session-start discovery.
            var configs = AcpAgentConfigJson.ParseList(entryJson, w => Console.WriteLine($"  [warn] {w}"));
            if (configs.Count != 1)
            {
                Console.WriteLine("FAIL: AcpAgentConfigJson did not yield the entry.");
                return 1;
            }

            var provider = new AcpAgentProvider(configs[0]);
            var before = provider.Capabilities;
            Console.WriteLine($"caps before session: {before}");

            var workDir = HostScratch.ResolveDir("console/custom-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ide = new ConsoleIdeServices(workDir);
                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                var after = provider.Capabilities;
                Console.WriteLine($"caps after session : {after}");

                var sawText = false;
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "Reply with one short sentence. Do not use any tools.")))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta) sawText = true;
                }

                var discovered = after.HasFlag(AgentCapabilities.ResumeSession) == probe.LoadSession
                    && !before.HasFlag(AgentCapabilities.ResumeSession);
                var passed = sawText && discovered;
                Console.WriteLine();
                Console.WriteLine(passed
                    ? "PASS: probe validated the entry, and session-start discovery updated ResumeSession to match initialize."
                    : $"FAIL: sawText={sawText}, capsBefore={before}, capsAfter={after}, probeLoadSession={probe.LoadSession}.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Drives a real Claude Code ACP adapter. Requires the npm bin claude-agent-acp on PATH
        /// (npm i -g @agentclientprotocol/claude-agent-acp) and a Claude Code login (or
        /// ANTHROPIC_API_KEY). Proves the second ACP backend end-to-end over the shared ACP layer.
        /// </summary>
        private static async Task<int> RunRealClaudeCodeAsync()
        {
            Console.WriteLine("== code-wicket console: real Claude Code (claude-agent-acp) ==");

            var workDir = HostScratch.ResolveDir("console/claude-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = new ClaudeCodeAgentProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                Console.WriteLine($"initialized; conversation id = {session.ConversationId}");
                Console.WriteLine();

                var sawText = false;
                var stopReason = "(none)";
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "Reply with one short sentence introducing yourself. Do not use any tools.")))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta) sawText = true;
                    if (ev is AgentEvent.TurnCompleted c) stopReason = c.StopReason ?? stopReason;
                }

                var passed = sawText && stopReason == "end_turn";
                Console.WriteLine();
                Console.WriteLine(passed
                    ? "PASS: Claude Code streamed a reply over the shared ACP layer."
                    : $"FAIL: sawText={sawText}, stopReason={stopReason}.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Proves Claude Code model selection over the shared ACP layer: the session discovers its live
        /// model list (ACP session/new config options, category "model"), reports the current model, and
        /// switches to a different one via session/set_config_option — after which a turn still completes.
        /// Requires the claude-agent-acp bin on PATH and a Claude Code login (or ANTHROPIC_API_KEY).
        /// </summary>
        /// <summary>
        /// Proves mid-turn steering against a real Claude Code session (issue #70): the ACP
        /// <c>_session/steering</c> extension request, which delivers a follow-up into the turn that is
        /// ALREADY RUNNING instead of cancelling it or waiting for it to end.
        /// <para>
        /// The assertion that matters is not that the request succeeds — it is that the turn is
        /// <em>pre-empted rather than queued behind</em>. So the prompt starts a long, dull task, the
        /// steer asks for a specific word, and the proof requires exactly one <c>TurnCompleted</c>: a
        /// steer that were really being queued would need a second turn, and the count would say so.
        /// </para>
        /// <para>
        /// <b>Measured 2026-07-31 (adapter 0.63.0), and it is not what "injected" sounds like:</b> the
        /// steer pre-empts the generation mid-sentence (the counting stopped at "8 - a cube,"), the
        /// interrupted turn then closes with <c>stopReason=end_turn</c>, and the steered message's OWN
        /// reply arrives <em>after</em> that close, out-of-turn, as plain <c>AssistantTextDelta</c>s
        /// (preceded by a <c>UsageUpdated</c>). So the out-of-turn sink is load-bearing for the ORDINARY
        /// injected path, not merely for the <c>startedNewTurn</c> race it was reasoned about as — the
        /// first run of this proof wired no sink and scored the reply as missing, which is exactly how a
        /// host that dropped turn-less events would behave. Hence the check accepts the word from either
        /// stream but reports which, because that is the fact worth not re-deriving.
        /// </para>
        /// Requires the claude-agent-acp bin on PATH and a Claude Code login (or ANTHROPIC_API_KEY).
        /// Note an adapter older than ~0.63.0 does not advertise steering at all, which this reports as
        /// a skip rather than a failure — the capability is a property of the installed build.
        /// </summary>
        private static async Task<int> RunRealClaudeSteerProofAsync()
        {
            Console.WriteLine("== code-wicket console: real Claude Code mid-turn steering ==");

            const string SteerWord = "PINEAPPLE";
            var workDir = HostScratch.ResolveDir("console/claude-steer-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = new ClaudeCodeAgentProvider();

                // Out-of-turn output is NOT just the race path here — measured 2026-07-31, a steer that
                // pre-empts a single-shot response ends the turn it interrupted and streams its own
                // reply afterwards, with no turn open. Without this sink the steered answer is invisible.
                var outOfTurn = new StringBuilder();
                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            OutOfTurnEvents = ev =>
                            {
                                if (ev is AgentEvent.AssistantTextDelta d)
                                    lock (outOfTurn) outOfTurn.Append(d.Text);

                                // Timestamped and verbatim: the open question this proof exists to
                                // settle is whether ANY end-of-stream signal follows a steered reply,
                                // and "we stopped watching once we saw the word" cannot answer it.
                                var detail = ev switch
                                {
                                    AgentEvent.AssistantTextDelta t => $"\"{t.Text.Replace("\n", "\\n")}\"",
                                    AgentEvent.TurnCompleted c => $"stopReason={c.StopReason}",
                                    _ => string.Empty,
                                };
                                Console.WriteLine(
                                    $"  [out-of-turn {DateTime.Now:HH:mm:ss.fff}] {ev.GetType().Name} {detail}");
                            },
                        },
                        ide)
                    .ConfigureAwait(false);

                // Discovered in the handshake, so this reflects the adapter actually installed here.
                var advertised = provider.Capabilities.HasFlag(AgentCapabilities.Steering);
                Console.WriteLine($"adapter advertises steering: {advertised}");
                if (!advertised)
                {
                    Console.WriteLine(
                        "SKIP: this claude-agent-acp build does not advertise _meta.steering.supported "
                        + "(added around 0.63.0). Update the adapter and re-run.");
                    return 0;
                }

                var firstDelta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var steerOutcome = SteerOutcome.NotSupported;
                Exception? steerFailure = null;

                // Steer once the turn is demonstrably under way. Concurrent with the enumeration below
                // on purpose — that IS the feature: the turn keeps streaming throughout.
                var steerTask = Task.Run(async () =>
                {
                    try
                    {
                        await firstDelta.Task.ConfigureAwait(false);
                        await Task.Delay(1500).ConfigureAwait(false);
                        Console.WriteLine();
                        Console.WriteLine($">> steering mid-turn: asking for {SteerWord}");
                        steerOutcome = await session.SteerAsync(new PromptInput(
                            $"Change of plan: stop counting immediately and reply with just the word {SteerWord}."))
                            .ConfigureAwait(false);
                        Console.WriteLine($">> steer outcome: {steerOutcome}");
                    }
                    catch (Exception ex)
                    {
                        steerFailure = ex;
                    }
                });

                var transcript = new StringBuilder();
                var turnsCompleted = 0;
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "Count slowly from 1 to 40. Put each number on its own line, with a short comment "
                    + "about the number. Do not use any tools.")))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta delta)
                    {
                        transcript.Append(delta.Text);
                        firstDelta.TrySetResult(true);
                    }

                    if (ev is AgentEvent.TurnCompleted)
                        turnsCompleted++;
                }

                firstDelta.TrySetResult(true); // a turn that ended before any text must not hang the steer
                await steerTask.ConfigureAwait(false);

                // Keep listening for a fixed window rather than stopping at the first sight of the word.
                // Whether anything at all marks the END of a steered reply is the open question here, and
                // a loop that exits on the word can only ever report that the word arrived.
                Console.WriteLine();
                Console.WriteLine("== observing out-of-turn traffic for 30s (looking for any end signal) ==");
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Console.WriteLine("== observation window closed ==");

                var inTurn = transcript.ToString().Contains(SteerWord, StringComparison.Ordinal);
                string outOfTurnText;
                lock (outOfTurn)
                    outOfTurnText = outOfTurn.ToString();
                var afterTurn = outOfTurnText.Contains(SteerWord, StringComparison.Ordinal);

                var sawSteerWord = inTurn || afterTurn;
                var injected = steerOutcome == SteerOutcome.Injected;

                Console.WriteLine();
                Console.WriteLine($"steered reply arrived: inTurn={inTurn} afterTurn={afterTurn}");

                Console.WriteLine();
                if (steerFailure is not null)
                {
                    Console.WriteLine($"FAIL: the steer request threw: {steerFailure}");
                    return 1;
                }

                if (steerOutcome == SteerOutcome.StartedNewTurn)
                {
                    // Legitimate (the turn beat the steer), but it proves nothing about injection, so it
                    // is neither a pass nor a failure — re-run with a longer prompt.
                    Console.WriteLine(
                        "INCONCLUSIVE: the turn finished before the steer landed, so the adapter started a "
                        + "new turn for it. That path is real but is not what this proof is measuring; re-run.");
                    return 2;
                }

                var passed = injected && sawSteerWord && turnsCompleted == 1;
                Console.WriteLine(passed
                    ? $"PASS: the steer was injected into the running turn (no cancel), the agent answered "
                      + $"\"{SteerWord}\" ({(inTurn ? "on the turn's own stream" : "out-of-turn, after the "
                      + "pre-empted turn closed")}), and exactly one turn completed."
                    : $"FAIL: outcome={steerOutcome}, sawSteerWord={sawSteerWord}, turnsCompleted={turnsCompleted}.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Proves what a steered reply may CONTAIN, which <c>claude-steer</c> deliberately does not: that
        /// one asks for a word, so it only ever exercised text.
        /// <para>
        /// The question this settles is whether the out-of-turn sink is carrying the whole event surface
        /// or just prose. A steer instructs the agent to write a file, so the reply has to come back as
        /// tool calls plus a permission request — and the pre-empted turn has already closed by then, so
        /// every one of those arrives with no turn channel open. If the tool events were dropped there,
        /// the user would be shown a permission prompt for a tool call that has no row in the transcript,
        /// and the agent's edit would land on disk with no card. Permission requests are the half an
        /// event-stream check cannot see: they are JSON-RPC <em>requests</em> on their own path, so they
        /// arrive regardless — which is what makes the asymmetry dangerous rather than merely lossy, and
        /// is why <c>EmitToCurrentTurn</c> now routes turn-less events unconditionally.
        /// </para>
        /// Same prerequisites as <c>claude-steer</c>: the claude-agent-acp bin on PATH, a Claude Code
        /// login, and an adapter new enough to advertise steering (older ones report a skip).
        /// </summary>
        private static async Task<int> RunRealClaudeSteerToolsProofAsync()
        {
            Console.WriteLine("== code-wicket console: what a steered Claude reply contains ==");

            const string SteerWord = "PINEAPPLE";
            const string SteerFile = "STEERED.txt";
            var workDir = HostScratch.ResolveDir("console/claude-steer-tools-" + Guid.NewGuid().ToString("N"));
            var started = DateTime.Now;

            // One ordered log for all three arrival paths (turn stream, out-of-turn sink, permission
            // requests), because the ordering BETWEEN them is the finding — a permission prompt landing
            // after its turn closed is the thing that has no transcript row to attach to.
            var timeline = new List<(DateTime At, string Source, string Detail)>();
            void Note(string source, string detail)
            {
                lock (timeline) timeline.Add((DateTime.Now, source, detail));
            }

            try
            {
                var permissions = new RecordingPermissionHandler(Note);
                var ide = new ConsoleIdeServices(workDir, permissions);
                var provider = new ClaudeCodeAgentProvider();

                // Anything reaching this sink had NO turn open — that is the definitive test, and a
                // stronger one than comparing timestamps against the turn's close.
                var outOfTurnToolEvents = 0;
                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            OutOfTurnEvents = ev =>
                            {
                                if (ev is AgentEvent.ToolCallStarted or AgentEvent.ToolCallUpdated
                                    or AgentEvent.ToolCallCompleted or AgentEvent.EditProposed)
                                {
                                    Interlocked.Increment(ref outOfTurnToolEvents);
                                }

                                Note("out-of-turn", DescribeEvent(ev));
                            },
                        },
                        ide)
                    .ConfigureAwait(false);

                var advertised = provider.Capabilities.HasFlag(AgentCapabilities.Steering);
                Console.WriteLine($"adapter advertises steering: {advertised}");
                if (!advertised)
                {
                    Console.WriteLine(
                        "SKIP: this claude-agent-acp build does not advertise _meta.steering.supported "
                        + "(added around 0.63.0). Update the adapter and re-run.");
                    return 0;
                }

                var firstDelta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var steerOutcome = SteerOutcome.NotSupported;
                Exception? steerFailure = null;

                var steerTask = Task.Run(async () =>
                {
                    try
                    {
                        await firstDelta.Task.ConfigureAwait(false);
                        await Task.Delay(1500).ConfigureAwait(false);
                        Console.WriteLine();
                        Console.WriteLine($">> steering mid-turn: asking it to WRITE {SteerFile}");
                        Note("steer", $"sending steer (write {SteerFile})");
                        steerOutcome = await session.SteerAsync(new PromptInput(
                            "Change of plan: stop counting immediately. Instead, create a file named "
                            + $"{SteerFile} in the current working directory whose entire contents are the "
                            + $"single word {SteerWord}. Then reply DONE."))
                            .ConfigureAwait(false);
                        Console.WriteLine($">> steer outcome: {steerOutcome}");
                        Note("steer", $"outcome={steerOutcome}");
                    }
                    catch (Exception ex)
                    {
                        steerFailure = ex;
                    }
                });

                DateTime? turnClosedAt = null;
                var turnsCompleted = 0;
                var inTurnToolEvents = 0;
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "Count slowly from 1 to 40. Put each number on its own line, with a short comment "
                    + "about the number. Do not use any tools.")))
                {
                    if (ev is AgentEvent.AssistantTextDelta)
                        firstDelta.TrySetResult(true);

                    if (ev is AgentEvent.ToolCallStarted or AgentEvent.ToolCallUpdated
                        or AgentEvent.ToolCallCompleted or AgentEvent.EditProposed)
                    {
                        inTurnToolEvents++;
                        Note("turn", DescribeEvent(ev));
                    }

                    if (ev is AgentEvent.TurnCompleted completed)
                    {
                        turnsCompleted++;
                        turnClosedAt ??= DateTime.Now;
                        Note("turn", $"TurnCompleted stopReason={completed.StopReason}");
                    }
                }

                firstDelta.TrySetResult(true);
                await steerTask.ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine("== observing for 30s (the steered work runs with no turn open) ==");
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                var filePath = Path.Combine(workDir, SteerFile);
                var fileCreated = File.Exists(filePath);
                var fileContent = fileCreated ? File.ReadAllText(filePath).Trim() : string.Empty;

                Console.WriteLine();
                Console.WriteLine("== timeline ==");
                lock (timeline)
                {
                    foreach (var (at, source, detail) in timeline)
                    {
                        var marker = turnClosedAt is not null && at > turnClosedAt ? "  " : "* ";
                        Console.WriteLine(
                            $"{marker}+{(at - started).TotalSeconds,6:0.00}s  {source,-12} {detail}");
                    }
                }

                Console.WriteLine("  ('*' = before the pre-empted turn closed)");

                var permissionsAfterClose = permissions.Requests
                    .Count(r => turnClosedAt is not null && r.At > turnClosedAt);

                Console.WriteLine();
                Console.WriteLine($"tool events on the turn's own stream : {inTurnToolEvents}");
                Console.WriteLine($"tool events via the out-of-turn sink : {outOfTurnToolEvents}");
                Console.WriteLine($"permission requests (total)          : {permissions.Requests.Count}");
                Console.WriteLine($"permission requests after turn close : {permissionsAfterClose}");
                Console.WriteLine($"{SteerFile} written                  : {fileCreated} "
                    + $"{(fileCreated ? $"(content: \"{fileContent}\")" : string.Empty)}");

                Console.WriteLine();
                if (steerFailure is not null)
                {
                    Console.WriteLine($"FAIL: the steer request threw: {steerFailure}");
                    return 1;
                }

                if (steerOutcome == SteerOutcome.StartedNewTurn)
                {
                    // The agent finished counting before the steer landed, so the work ran as its own
                    // turn with a channel open — real, but not the case being measured.
                    Console.WriteLine(
                        "INCONCLUSIVE: the turn finished before the steer landed, so this exercised the "
                        + "startedNewTurn path rather than injection into a live turn; re-run.");
                    return 2;
                }

                var passed = steerOutcome == SteerOutcome.Injected
                    && turnsCompleted == 1
                    && outOfTurnToolEvents > 0
                    && permissionsAfterClose > 0
                    && fileCreated;

                Console.WriteLine(passed
                    ? "PASS: the steered work came back as tool calls AND a permission request, all after "
                      + "the pre-empted turn had closed — so a host that dropped turn-less events would "
                      + "have shown a permission prompt for a tool call with no transcript row, and "
                      + "written the file with no edit card."
                    : $"FAIL: outcome={steerOutcome}, turnsCompleted={turnsCompleted}, "
                      + $"outOfTurnToolEvents={outOfTurnToolEvents}, "
                      + $"permissionsAfterClose={permissionsAfterClose}, fileCreated={fileCreated}.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Settles what a steer does to a tool call that is ALREADY RUNNING, and whether the answer
        /// depends on how that tool is DELIVERED (issue #70's open question).
        /// <para>
        /// The adapter's own source documents <c>priority:"now"</c> as interrupting a single-shot
        /// response <em>"or slotting in between a multi-step turn's tool calls"</em> — the very boundary
        /// our tray implements client-side. Our one measurement disagreed: a steer sent during a
        /// <c>run_tests</c> call returned <c>AbortError: interrupt</c> and the run was lost.
        /// </para>
        /// <para>
        /// This cannot be resolved by reading: the field is not in the published Agent SDK surface at
        /// all (no <c>SDKUserMessage</c> shape, no mention of the field, no documented mid-turn
        /// injection), so the adapter comment — the claim under test — is the only description of the
        /// semantics that exists. Hence a measurement.
        /// </para>
        /// <para>
        /// Two phases, identical but for what is in flight when the steer lands: a deliberately slow
        /// <b>MCP</b> tool over our named-pipe bridge, and the agent's own <b>built-in</b> Bash. Note
        /// what this does and does not vary — <b>both are driven by the same SDK</b> (the adapter
        /// depends on <c>@anthropic-ai/claude-agent-sdk</c>), so the axis is delivery, not ownership;
        /// reading the result as "the SDK protects its own tools" is the available wrong answer, and it
        /// was reached once. Each phase records both halves — whether OUR side ran to completion, and
        /// what the AGENT reported for the call — because they can disagree, and the gap between them
        /// is the second finding: the call is orphaned, not cancelled.
        /// </para>
        /// Same prerequisites as <c>claude-steer</c>: the claude-agent-acp bin on PATH, a Claude Code
        /// login, and an adapter advertising steering.
        /// </summary>
        /// <param name="only">
        /// <c>mcp</c> or <c>bash</c> to run a single phase. Each phase costs a real Claude session and
        /// ~90s, so re-running one after a fix should not re-bill the other.
        /// </param>
        private static async Task<int> RunRealClaudeSteerBoundaryProofAsync(string? only = null)
        {
            Console.WriteLine("== code-wicket console: what a steer does to a RUNNING tool call ==");
            Console.WriteLine();

            if (only is not null && only.Equals("retention", StringComparison.OrdinalIgnoreCase))
                return await RunSteerRetentionPhaseAsync().ConfigureAwait(false);
            if (only is not null && only.Equals("compare", StringComparison.OrdinalIgnoreCase))
                return await RunSteerVsCancelPhaseAsync().ConfigureAwait(false);
            if (only is not null && only.Equals("kiro", StringComparison.OrdinalIgnoreCase))
                return await RunKiroCancelPhaseAsync("kiro").ConfigureAwait(false);
            if (only is not null && only.Equals("kiro-v3", StringComparison.OrdinalIgnoreCase))
                return await RunKiroCancelPhaseAsync("kiro-v3").ConfigureAwait(false);

            var runMcp = only is null || only.Equals("mcp", StringComparison.OrdinalIgnoreCase);
            var runBash = only is null || only.Equals("bash", StringComparison.OrdinalIgnoreCase);

            // A sub-mode this method does not know selects NO phase, and the partial-run branch below
            // then dereferences two nulls — so a typo was reported as an InvalidOperationException from
            // the middle of a proof rather than as the typo it is (#247). Named here, where the valid
            // set is, instead of near the crash.
            if (!runMcp && !runBash)
            {
                Console.WriteLine(
                    $"unknown sub-mode '{only}'. Valid: mcp, bash, retention, compare, kiro, kiro-v3 — "
                    + "or omit it to run mcp and bash and compare them.");
                return 2;
            }

            SteerBoundaryOutcome? ours = runMcp
                ? await RunSteerBoundaryPhaseAsync(mcpBridged: true).ConfigureAwait(false)
                : null;
            if (runMcp && runBash)
                Console.WriteLine();
            SteerBoundaryOutcome? theirs = runBash
                ? await RunSteerBoundaryPhaseAsync(mcpBridged: false).ConfigureAwait(false)
                : null;

            Console.WriteLine();
            Console.WriteLine("== verdict ==");
            Console.WriteLine($"  MCP tool (ours)     : {ours?.ToString() ?? "not run"}");
            Console.WriteLine($"  built-in tool (Bash): {theirs?.ToString() ?? "not run"}");
            Console.WriteLine();

            if (ours is { Inconclusive: true } || theirs is { Inconclusive: true })
            {
                Console.WriteLine(
                    "INCONCLUSIVE: a phase that ran never got a steer into a live tool call (the call "
                    + "finished first, or never started). Re-run; if it repeats, lengthen the tool.");
                return 2;
            }

            // A single-phase re-run reports that phase rather than claiming the comparison failed —
            // the whole point of the filter is fixing one half without re-billing the other.
            if (ours is null || theirs is null)
            {
                var ran = (ours ?? theirs)!.Value;
                Console.WriteLine(
                    $"PARTIAL: only one phase ran. {(ours is null ? "built-in" : "MCP")} call "
                    + $"{(ran.CallSurvived ? "SURVIVED" : "was DESTROYED")} — run both for the comparison.");
                return 0;
            }

            // The hypothesis under test is the asymmetry, so name which of the three outcomes happened
            // rather than pass/fail — every one of them is a real finding about a real backend.
            if (ours.Value.CallSurvived && theirs.Value.CallSurvived)
            {
                Console.WriteLine(
                    "RESULT: BOTH survived. The adapter's 'slots in between tool calls' holds for our "
                    + "tools too, and the run_tests loss had some other cause — the hypothesis in "
                    + "chat-ui.md is WRONG and the tray's stated reason needs re-deriving. (The tray "
                    + "itself still stands: a steer that lands between calls is not a promise about "
                    + "one that lands inside a model's own generation.)");
                return 0;
            }

            if (!ours.Value.CallSurvived && theirs.Value.CallSurvived)
            {
                Console.WriteLine(
                    "RESULT: ASYMMETRIC — the built-in call survived, the MCP call did not. Both are driven "
                    + "by the same SDK (the adapter vendors @anthropic-ai/claude-agent-sdk), so the axis "
                    + "is DELIVERY, not ownership: the interrupt reaches the in-flight MCP JSON-RPC "
                    + "request, which is cancellable at the transport. Why the built-in survived is NOT "
                    + "established. Every IDE tool we expose is MCP, so all of ours are on the dying "
                    + "side — which is where the expensive calls are.");
                return 0;
            }

            if (!ours.Value.CallSurvived && !theirs.Value.CallSurvived)
            {
                Console.WriteLine(
                    "RESULT: BOTH aborted. 'Slotting in between a multi-step turn's tool calls' means "
                    + "between calls, not into one — a steer arriving mid-call destroys it whoever owns "
                    + "it. The tray's original reason was right and the narrowing was over-cautious.");
                return 0;
            }

            Console.WriteLine(
                "RESULT: INVERTED — ours survived and the SDK's did not. Nothing predicted this; "
                + "capture both transcripts before theorising.");
            return 0;
        }

        /// <summary>
        /// Compares the two ways a held message can be delivered mid-turn — a <b>steer</b> and a
        /// <b>cancel + send</b> — on one backend, at the same moment, against the same in-flight work.
        /// <para>
        /// This is the question the whole boundary series was really for. If the two are equivalent from
        /// the user's seat, then Claude's steering is a *mechanism* rather than a capability, Kiro's
        /// cancel + send is not a lesser path, and the two backends have no business behaving
        /// differently in the UI — only in how the delivery is implemented.
        /// </para>
        /// <para>
        /// Both runs interrupt an in-flight MCP call (ours — the case that matters, and the one a steer
        /// destroys) and both deliver a message that can only be answered from earlier conversation, so
        /// the comparison covers the thing a cancel is most suspected of costing: whether the agent
        /// keeps its bearings. What is recorded is what a user would actually notice — the answer, and
        /// whether it arrived in a real turn — plus the turn accounting the host has to cope with.
        /// </para>
        /// </summary>
        private static async Task<int> RunSteerVsCancelPhaseAsync()
        {
            Console.WriteLine("== steer vs cancel+send: same gesture, same backend, same moment ==");
            Console.WriteLine();

            var viaSteer = await RunDeliveryComparisonAsync(useSteer: true).ConfigureAwait(false);
            Console.WriteLine();
            var viaCancel = await RunDeliveryComparisonAsync(useSteer: false).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("== comparison ==");
            Console.WriteLine($"{"",-26}| {"steer",-30}| cancel + send");
            Console.WriteLine(new string('-', 92));
            Console.WriteLine($"{"in-flight MCP call",-26}| {Short2(viaSteer.CallFate),-30}| {Short2(viaCancel.CallFate)}");
            Console.WriteLine($"{"answered from context",-26}| {viaSteer.ContextKept,-30}| {viaCancel.ContextKept}");
            Console.WriteLine($"{"reply arrived in a turn",-26}| {viaSteer.ReplyInTurn,-30}| {viaCancel.ReplyInTurn}");
            Console.WriteLine($"{"turns completed",-26}| {viaSteer.TurnsCompleted,-30}| {viaCancel.TurnsCompleted}");
            Console.WriteLine($"{"reply",-26}| {Short(viaSteer.Reply),-30}| {Short(viaCancel.Reply)}");
            Console.WriteLine();

            if (!viaSteer.Valid || !viaCancel.Valid)
            {
                Console.WriteLine(
                    "INCONCLUSIVE: a delivery did not land while our tool was actually executing "
                    + $"(steer={viaSteer.Valid}, cancel={viaCancel.Valid}), so the two runs did not "
                    + "interrupt the same thing. Re-run.");
                return 2;
            }

            var sameFate = string.Equals(viaSteer.CallFate, viaCancel.CallFate, StringComparison.Ordinal);
            Console.WriteLine(sameFate && viaSteer.ContextKept == viaCancel.ContextKept
                ? "RESULT: EQUIVALENT where the user can see — same fate for the running call, same "
                  + "ability to answer from context. The remaining differences are turn accounting, "
                  + "which is the host's problem: the mechanism may differ per backend, the behaviour "
                  + "must not."
                : "RESULT: they DIFFER where the user can see. Compare the fates above — note that a "
                  + "call left with NO terminal frame is worse than one destroyed with an explicit "
                  + "error: the transcript row never settles, and the agent may report that nothing "
                  + "ran when it did. Parity is reachable, but the host has to supply what the wire "
                  + "does not.");
            return 0;

            static string Short2(string s) => s.Length > 28 ? s.Substring(0, 28) + "…" : s;
            static string Short(string s)
            {
                var flat = s.Replace("\r", string.Empty).Replace("\n", " ").Trim();
                return flat.Length > 28 ? flat.Substring(0, 28) + "…" : flat;
            }
        }

        /// <summary>
        /// The check that gates the parity fix: when we cancel to make room for a held message, does
        /// <b>Kiro</b> settle the tool call that was in flight?
        /// <para>
        /// Claude's adapter does not — the call gets no terminal frame at all, so the transcript row
        /// never leaves "working" and the agent may report that nothing ran when our tool has in fact
        /// started. Whether that is a property of the cancel path or of one adapter decides where the
        /// fix belongs: a host-side sweep of open rows on turn end (if both omit it) or a
        /// backend-specific gap (if Kiro settles them). Same host code, same catalog, same timing —
        /// only the provider differs.
        /// </para>
        /// Requires kiro-cli on PATH and an authenticated Builder ID login.
        /// </summary>
        private static async Task<int> RunKiroCancelPhaseAsync(string backend)
        {
            Console.WriteLine($"== {backend}: does a cancel settle the tool call that was in flight? ==");
            Console.WriteLine();

            var kiro = await RunDeliveryComparisonAsync(useSteer: false, backend).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("== verdict ==");
            Console.WriteLine($"  interrupted a live call : {kiro.Valid}");
            Console.WriteLine($"  in-flight call fate     : {kiro.CallFate}");
            Console.WriteLine($"  answered from context   : {kiro.ContextKept}");
            Console.WriteLine();

            if (!kiro.Valid)
            {
                Console.WriteLine(
                    "INCONCLUSIVE: the cancel did not land while our tool was executing — Kiro may not "
                    + "have called it. Check the timeline above and re-run.");
                return 2;
            }

            var settled = !kiro.CallFate.StartsWith("NO TERMINAL FRAME", StringComparison.Ordinal);
            Console.WriteLine(settled
                ? $"RESULT: {backend} DOES settle the call on cancel, with its own reason. Whether a "
                  + "backend does this is per-ENGINE, not per-vendor and not a property of cancelling: "
                  + "measured, Kiro's default engine omits the frame and v3 supplies it, on the same "
                  + "CLI version. The host sweep stays — it only touches rows still Running, so a "
                  + "backend that settles keeps its own wording."
                : $"RESULT: {backend} leaves the call unsettled, so the row would never leave "
                  + "\"Running\" without the host sweep. Do NOT generalise this to every backend: "
                  + "Kiro's v3 engine does settle it. Which engine is running decides.");
            return 0;
        }

        /// <summary>What one delivery of a held message looked like — see <see cref="RunSteerVsCancelPhaseAsync"/>.</summary>
        private readonly record struct DeliveryComparison(
            string CallFate, bool ContextKept, bool ReplyInTurn, int TurnsCompleted, string Reply,
            bool Valid);

        /// <summary>
        /// One half of the delivery comparison: establish a fact, start a long MCP call, then interrupt
        /// it either by steering or by cancelling and sending. The interrupting message asks for the
        /// fact back, so a delivery that loses the conversation shows up as a wrong answer rather than
        /// as a subtle difference nobody can name.
        /// </summary>
        private static async Task<DeliveryComparison> RunDeliveryComparisonAsync(
            bool useSteer, string backend = "claude")
        {
            var label = backend + " / " + (useSteer ? "steer" : "cancel + send");
            Console.WriteLine($"-- delivery: {label} --");

            var codename = "ORCHID-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
            var workDir = HostScratch.ResolveDir("console/steer-vs-cancel-" + Guid.NewGuid().ToString("N"));
            var started = DateTime.Now;
            var timeline = new List<(DateTime At, string Source, string Detail)>();
            void Note(string source, string detail)
            {
                lock (timeline) timeline.Add((DateTime.Now, source, detail));
            }

            McpPipeHost? host = null;
            try
            {
                var callRunning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var catalog = new SlowProbeCatalog(
                    TimeSpan.FromSeconds(25), Note, onStarted: () => callRunning.TrySetResult(true));
                var (h, spec) = StartKiroMcpBridge(catalog);
                host = h;

                var permissions = new RecordingPermissionHandler(Note);
                var ide = new ConsoleIdeServices(workDir, permissions);
                IAgentProvider provider = backend switch
                {
                    // v3 is a different agent engine behind the same CLI version, and it settles a
                    // cancelled call where the default engine does not — so it needs its own phase.
                    "kiro-v3" => new KiroAgentProvider(new KiroProviderOptions { AgentEngine = "v3" }),
                    "kiro" => CreateKiroProvider(),
                    _ => new ClaudeCodeAgentProvider(),
                };

                var outOfTurnText = new StringBuilder();
                // Three-valued on purpose. A boolean "destroyed?" reads a MISSING terminal frame as
                // "survived", which is the opposite of the truth: a call with no completion leaves the
                // transcript row spinning forever. The cancel path produces exactly that.
                string? probeFate = null;
                var turnsCompleted = 0;

                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            McpServers = new[] { spec },
                            OutOfTurnEvents = ev =>
                            {
                                if (ev is AgentEvent.AssistantTextDelta d)
                                    lock (outOfTurnText) outOfTurnText.Append(d.Text);
                                Note("out-of-turn", DescribeEvent(ev));
                            },
                        },
                        ide)
                    .ConfigureAwait(false);

                // Turn 1 establishes a fact that only the conversation can supply, so "did this delivery
                // keep the agent's bearings" has a checkable answer instead of a vibe.
                await foreach (var ev in session.SendAsync(new PromptInput(
                    $"Remember this for later: the project codename is {codename}. Reply OK and nothing else.")))
                {
                    if (ev is AgentEvent.TurnCompleted)
                        Note("turn-1", "established the codename");
                }

                var probeCallId = string.Empty;
                const string Ask = "Change of plan — stop that and just tell me: what is the project codename?";

                var probeRunningAtDelivery = false;
                var deliver = Task.Run(async () =>
                {
                    if (!await callRunning.Task.ConfigureAwait(false))
                        return;
                    await Task.Delay(3500).ConfigureAwait(false);

                    // Ground truth, not inference: our own catalog says the call is executing.
                    probeRunningAtDelivery = catalog.Started > 0 && catalog.Completed == 0;
                    Note("deliver", $"our tool executing at delivery: {probeRunningAtDelivery}");

                    if (useSteer)
                    {
                        Note("deliver", "steering");
                        var outcome = await session.SteerAsync(new PromptInput(Ask)).ConfigureAwait(false);
                        Note("deliver", $"steer outcome={outcome}");
                    }
                    else
                    {
                        // Exactly what ChatViewModel.SendPendingNow does on a backend without steering:
                        // cancel to make room, then send as an ordinary prompt.
                        Note("deliver", "cancelling");
                        await session.CancelAsync().ConfigureAwait(false);
                        Note("deliver", "cancelled; the prompt follows once the turn closes");
                    }
                });

                await foreach (var ev in session.SendAsync(new PromptInput(
                    $"Use the 'slow_probe' tool from the {UnderscoredServer} MCP server with reason "
                    + "\"compare\". Call the tool — do not simulate it. It takes about 25 seconds; "
                    + "wait for it and report what it returned.")))
                {
                    if (ev is AgentEvent.ToolCallStarted s)
                    {
                        Note("turn-2", DescribeEvent(ev));

                        // The delivery is armed by our CATALOG (see onStarted), not by this event:
                        // arming on "first tool call" fired into the agent's own preliminary
                        // ToolSearch call, and matching on the title would not survive a backend that
                        // names its calls differently. Our tool executing is the same fact everywhere.
                        if (s.Title.Contains("slow_probe", StringComparison.Ordinal))
                            probeCallId = s.ToolCallId;
                    }
                    else if (ev is AgentEvent.ToolCallCompleted c)
                    {
                        if (c.ToolCallId == probeCallId)
                            probeFate = c.Success ? "completed" : $"destroyed ({c.ResultText})";
                        Note("turn-2", $"{DescribeEvent(ev)} result={c.ResultText}");
                    }
                    else if (ev is AgentEvent.TurnCompleted)
                    {
                        turnsCompleted++;
                        Note("turn-2", DescribeEvent(ev));
                    }
                }

                callRunning.TrySetResult(false);
                await deliver.ConfigureAwait(false);

                var reply = string.Empty;
                var replyInTurn = false;
                if (useSteer)
                {
                    // The steered reply has no turn to arrive on; it lands in the out-of-turn sink.
                    Console.WriteLine("   observing 30s for the steered reply...");
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    lock (outOfTurnText) reply = outOfTurnText.ToString();
                }
                else
                {
                    // The cancelled turn has closed, so the message goes as an ordinary prompt — which
                    // is also what gets it a <workspace-context> block the steer never carries.
                    Console.WriteLine("   sending the held message as an ordinary prompt...");
                    var text = new StringBuilder();
                    await foreach (var ev in session.SendAsync(new PromptInput(Ask)))
                    {
                        if (ev is AgentEvent.AssistantTextDelta d)
                            text.Append(d.Text);
                        else if (ev is AgentEvent.TurnCompleted)
                            turnsCompleted++;
                    }

                    reply = text.ToString();
                    replyInTurn = true;
                }

                Console.WriteLine();
                Console.WriteLine("   timeline:");
                lock (timeline)
                {
                    foreach (var (at, source, detail) in timeline)
                        Console.WriteLine($"     +{(at - started).TotalSeconds,6:0.00}s  {source,-12} {detail}");
                }

                var contextKept = reply.Contains(codename, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine();
                Console.WriteLine($"   codename planted : {codename}");
                Console.WriteLine($"   call fate        : {probeFate ?? "NO TERMINAL FRAME (row hangs)"}");
                Console.WriteLine($"   reply            : \"{reply.Replace("\n", " ").Trim()}\"");
                Console.WriteLine($"   context kept     : {contextKept}");

                Console.WriteLine($"   interrupted a live call : {probeRunningAtDelivery}");

                return new DeliveryComparison(
                    probeFate ?? "NO TERMINAL FRAME (row hangs)", contextKept, replyInTurn,
                    turnsCompleted, reply, Valid: probeRunningAtDelivery);
            }
            finally
            {
                if (host is not null)
                    await host.DisposeAsync().ConfigureAwait(false);
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// The two follow-on questions the boundary proof raises about the surviving built-in call:
        /// does the REST of the turn still happen, and does the model still HAVE the result?
        /// <para>
        /// The boundary phase showed a built-in Bash call completing normally 26s after the steer — but
        /// the model never carried out the rest of its instruction (it was told to report the output and
        /// replied only "READY"), and the turn closed the instant the tool result landed. Two readings
        /// fit: the steer waits for a safe boundary and then takes over, or it simply bites at the next
        /// moment there is any generation to pre-empt — which, for a model blocked on a tool, is the
        /// instant the result arrives. They differ on what happens to the REST of a multi-step turn.
        /// </para>
        /// <para>
        /// So: one turn with two built-in commands, steered during the first. If the second never runs,
        /// the "bites at the next generation" reading holds and a surviving call is no safe haven — the
        /// work after it is lost either way.
        /// </para>
        /// <para>
        /// Then the sharper question: the call returned real output on the wire, but did the MODEL keep
        /// it? A plain follow-up asks for a random token the command printed. Two properties make the
        /// answer mean something, and both are needed: the token is
        /// random hex, so a correct answer cannot be a plausible guess the way "Average = 0ms" could be
        /// — AND it is never stated in the prompt, only written to a file the command cats. Putting it
        /// in the instruction text lets the model answer from the
        /// instruction, which proves nothing about the tool result.
        /// </para>
        /// </summary>
        private static async Task<int> RunSteerRetentionPhaseAsync()
        {
            Console.WriteLine("-- phase: built-in call, does the turn continue and is the result kept? --");

            var token = "TK" + Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
            var workDir = HostScratch.ResolveDir("console/steer-retention-" + Guid.NewGuid().ToString("N"));
            var started = DateTime.Now;
            var timeline = new List<(DateTime At, string Source, string Detail)>();
            void Note(string source, string detail)
            {
                lock (timeline) timeline.Add((DateTime.Now, source, detail));
            }

            try
            {
                var permissions = new RecordingPermissionHandler(Note);
                var ide = new ConsoleIdeServices(workDir, permissions);
                var provider = new ClaudeCodeAgentProvider();

                var toolStartsAfterSteer = new List<(DateTime At, string Title)>();
                DateTime? steeredAt = null;
                void RecordStart(AgentEvent ev)
                {
                    if (ev is AgentEvent.ToolCallStarted or AgentEvent.ToolCallUpdated
                        && steeredAt is not null)
                    {
                        var title = ev switch
                        {
                            AgentEvent.ToolCallStarted s => s.Title,
                            AgentEvent.ToolCallUpdated u => u.Title ?? string.Empty,
                            _ => string.Empty,
                        };
                        lock (toolStartsAfterSteer) toolStartsAfterSteer.Add((DateTime.Now, title));
                    }
                }

                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            OutOfTurnEvents = ev =>
                            {
                                RecordStart(ev);
                                Note("out-of-turn", DescribeEvent(ev));
                            },
                        },
                        ide)
                    .ConfigureAwait(false);

                if (!provider.Capabilities.HasFlag(AgentCapabilities.Steering))
                {
                    Console.WriteLine("SKIP: adapter does not advertise steering.");
                    return 2;
                }

                var callRunning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var steerOutcome = SteerOutcome.NotSupported;

                var steerTask = Task.Run(async () =>
                {
                    if (!await callRunning.Task.ConfigureAwait(false))
                        return;
                    await Task.Delay(4000).ConfigureAwait(false);
                    Note("steer", "sending steer during the FIRST command");
                    steeredAt = DateTime.Now;
                    steerOutcome = await session
                        .SteerAsync(new PromptInput("Actually, never mind all that — just reply READY."))
                        .ConfigureAwait(false);
                    Note("steer", $"outcome={steerOutcome}");
                    Console.WriteLine($"   steer outcome: {steerOutcome}");
                });

                // The token lives in a FILE the command cats, and never appears in the prompt — the
                // first version of this phase put it in the instruction text, so the model could answer
                // from the instruction and a correct answer proved nothing about the tool result. It
                // must be learnable ONLY by reading the output.
                File.WriteAllText(Path.Combine(workDir, "token.txt"), token);

                // The read comes FIRST and the wait after: the agent's shell sandbox blocks a
                // wait-then-command sequence (that is what killed the original `sleep 25 && echo`),
                // and this ordering also puts the token in the output before the steer lands.
                var prompt =
                    "Do these two things in order with the Bash tool. First run exactly: "
                    + "cat token.txt && ping -n 20 127.0.0.1 — and wait for it to finish. Then run "
                    + "exactly: echo SECOND-STEP-RAN. Finally tell me what the first command printed "
                    + "before the ping output, and confirm the second command ran.";

                var firstCallSucceeded = (bool?)null;
                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    RecordStart(ev);
                    if (ev is AgentEvent.ToolCallStarted or AgentEvent.ToolCallUpdated
                        or AgentEvent.ToolCallCompleted or AgentEvent.TurnCompleted)
                    {
                        Note("turn", DescribeEvent(ev));
                    }

                    if (ev is AgentEvent.ToolCallStarted)
                        callRunning.TrySetResult(true);
                    if (ev is AgentEvent.ToolCallCompleted c)
                        firstCallSucceeded ??= c.Success;
                }

                callRunning.TrySetResult(false);
                await steerTask.ConfigureAwait(false);

                Console.WriteLine("   observing 40s for the steered work...");
                await Task.Delay(TimeSpan.FromSeconds(40)).ConfigureAwait(false);

                // A PLAIN prompt, not a steer: the question is what the conversation retained, so it
                // must be asked the ordinary way.
                Console.WriteLine("   asking, on a fresh turn, for the token...");
                var reply = new StringBuilder();
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "What did the first command print on the line before the ping output began? "
                    + "Reply with just that text if you have it, or exactly NONE if you do not.")))
                {
                    if (ev is AgentEvent.AssistantTextDelta d)
                        reply.Append(d.Text);
                }

                var answer = reply.ToString().Trim();
                List<(DateTime At, string Title)> afterSteer;
                lock (toolStartsAfterSteer) afterSteer = toolStartsAfterSteer.ToList();
                var secondStepRan = afterSteer.Any(t => t.Title.Contains("SECOND-STEP-RAN", StringComparison.Ordinal));
                var tokenRecalled = answer.Contains(token, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine();
                Console.WriteLine("   timeline:");
                lock (timeline)
                {
                    foreach (var (at, source, detail) in timeline)
                        Console.WriteLine($"     +{(at - started).TotalSeconds,6:0.00}s  {source,-12} {detail}");
                }

                Console.WriteLine();
                Console.WriteLine($"   token planted            : {token}");
                Console.WriteLine($"   first call succeeded     : {firstCallSucceeded}");
                Console.WriteLine($"   tool starts after steer  : {afterSteer.Count}");
                Console.WriteLine($"   second command ran       : {secondStepRan}");
                Console.WriteLine($"   follow-up answer         : \"{answer.Replace("\n", " ")}\"");
                Console.WriteLine($"   token recalled           : {tokenRecalled}");

                Console.WriteLine();
                if (steerOutcome == SteerOutcome.StartedNewTurn)
                {
                    Console.WriteLine("INCONCLUSIVE: the turn beat the steer; nothing was pre-empted.");
                    return 2;
                }

                Console.WriteLine(secondStepRan
                    ? "  the rest of the turn STILL RAN — the steer waited for the whole multi-step "
                      + "sequence, not just the running call."
                    : "  the rest of the turn was LOST — the steer bit at the next generation, so a "
                      + "surviving call is no safe haven: everything after it goes.");
                Console.WriteLine(tokenRecalled
                    ? "  the model KEPT the result — the tool output is in the conversation and can be "
                      + "asked for afterwards, so a steer during a built-in call costs the commentary, "
                      + "not the work."
                    : "  the model does NOT have the result — the call ran, its output reached the wire, "
                      + "and the conversation kept none of it. The whole call was wasted.");
                return 0;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>The outcome of one steer-boundary phase — see <see cref="RunSteerBoundaryPhaseAsync"/>.</summary>
        private readonly record struct SteerBoundaryOutcome(
            bool Inconclusive, bool CallSurvived, string Detail)
        {
            public override string ToString() => Detail;
        }

        /// <summary>
        /// One half of the steer-boundary proof: start a long tool call, steer while it is provably
        /// running, and report whether that call survived. <paramref name="mcpBridged"/> selects whose
        /// tool is in flight — ours over the MCP bridge, or the SDK's built-in Bash.
        /// </summary>
        private static async Task<SteerBoundaryOutcome> RunSteerBoundaryPhaseAsync(bool mcpBridged)
        {
            var label = mcpBridged ? "MCP (ours, over the bridge)" : "built-in (the agent's own Bash)";
            Console.WriteLine($"-- phase: {label} --");

            var workDir = HostScratch.ResolveDir("console/steer-boundary-" + Guid.NewGuid().ToString("N"));
            var started = DateTime.Now;
            var timeline = new List<(DateTime At, string Source, string Detail)>();
            void Note(string source, string detail)
            {
                lock (timeline) timeline.Add((DateTime.Now, source, detail));
            }

            McpPipeHost? host = null;
            try
            {
                // The tool has to still be running when the steer lands, so it outlasts the steer delay
                // by a wide margin — a race here would show up as "inconclusive", never as a false
                // reading, because the phase asserts the call was live at steer time.
                var catalog = new SlowProbeCatalog(TimeSpan.FromSeconds(25), Note);
                McpServerSpec[] servers = Array.Empty<McpServerSpec>();
                if (mcpBridged)
                {
                    var (h, spec) = StartKiroMcpBridge(catalog);
                    host = h;
                    servers = new[] { spec };
                }

                var permissions = new RecordingPermissionHandler(Note);
                var ide = new ConsoleIdeServices(workDir, permissions);
                var provider = new ClaudeCodeAgentProvider();

                // The pre-empted turn closes before the steered work runs, so the tool call's own
                // completion can arrive on either path. Both are recorded; neither is assumed.
                // The arrival TIME is part of the record, not decoration: a call that fails before the
                // steer went out failed on its own, and reading that as an abort is the available wrong
                // answer here — the first run of this proof made exactly that mistake.
                var callCompletions = new List<(DateTime At, string Id, bool Success, string? Error, string? Result)>();
                void RecordCompletion(AgentEvent ev)
                {
                    if (ev is AgentEvent.ToolCallCompleted c)
                        lock (callCompletions)
                            callCompletions.Add((DateTime.Now, c.ToolCallId, c.Success, c.ErrorText, c.ResultText));
                }

                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            McpServers = servers,
                            OutOfTurnEvents = ev =>
                            {
                                RecordCompletion(ev);
                                Note("out-of-turn", DescribeEvent(ev));
                            },
                        },
                        ide)
                    .ConfigureAwait(false);

                if (!provider.Capabilities.HasFlag(AgentCapabilities.Steering))
                {
                    Console.WriteLine("SKIP: this adapter does not advertise _meta.steering.supported.");
                    return new SteerBoundaryOutcome(true, false, "skipped (no steering capability)");
                }

                // "The call is running" is taken from the tool call actually opening — not from a timer —
                // so the steer cannot land before there is anything to interrupt.
                var callRunning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var steerOutcome = SteerOutcome.NotSupported;
                Exception? steerFailure = null;
                DateTime? steeredAt = null;

                var steerTask = Task.Run(async () =>
                {
                    try
                    {
                        var id = await callRunning.Task.ConfigureAwait(false);
                        if (id.Length == 0)
                            return;

                        // Long enough that the call is unambiguously mid-flight, short enough that it
                        // cannot have finished (the tool runs 25s).
                        await Task.Delay(4000).ConfigureAwait(false);
                        Note("steer", "sending steer while the call is running");
                        steeredAt = DateTime.Now;
                        steerOutcome = await session
                            .SteerAsync(new PromptInput("Actually, never mind that — just reply READY."))
                            .ConfigureAwait(false);
                        Note("steer", $"outcome={steerOutcome}");
                        Console.WriteLine($"   steer outcome: {steerOutcome}");
                    }
                    catch (Exception ex)
                    {
                        steerFailure = ex;
                    }
                });

                var prompt = mcpBridged
                    ? $"Call the 'slow_probe' tool from the {UnderscoredServer} MCP server with reason "
                      + "\"boundary\". It takes about 25 seconds; wait for it and report what it returned. "
                      + "Do not use any other tool."
                    // NOT `sleep`: the agent's own shell sandbox blocks a foreground sleep outright
                    // ("Blocked: sleep 25 followed by…"), which settles the call ~3s before the steer
                    // and measures nothing. `ping -n` idles the same length without tripping that rule.
                    : "Run this exact shell command with the Bash tool and wait for it to finish: "
                      + "ping -n 30 127.0.0.1. Report the last line of its output. "
                      + "Do not use any other tool.";

                // The id the steer is armed on, and the ONLY one the verdict may be computed against.
                // These were two different calls: arming took the first start while `toolCallId` took
                // every start, so a preliminary call — which Claude does open, see RunSteerCompareAsync —
                // left the phase reporting the fate of a call the steer never touched, and reporting it
                // as a pass (#247).
                var toolCallId = string.Empty;
                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    RecordCompletion(ev);

                    if (ev is AgentEvent.ToolCallStarted s)
                    {
                        Note("turn", DescribeEvent(ev));

                        // Bridged, the 25s call is OURS and its name is the same fact on every backend
                        // (the sibling phase arms the same way). Unbridged there is only the agent's own
                        // Bash, so the first call is it — and if that turns out to be a preliminary one,
                        // it settles before the steer and the phase says so rather than scoring it.
                        if (toolCallId.Length == 0
                            && (!mcpBridged || s.Title.Contains("slow_probe", StringComparison.Ordinal)))
                        {
                            toolCallId = s.ToolCallId;
                            callRunning.TrySetResult(s.ToolCallId);
                        }
                    }
                    else if (ev is AgentEvent.ToolCallCompleted or AgentEvent.ToolCallUpdated
                             or AgentEvent.TurnCompleted)
                    {
                        Note("turn", DescribeEvent(ev));
                    }
                }

                callRunning.TrySetResult(string.Empty); // the turn ended without a tool call
                await steerTask.ConfigureAwait(false);

                // The steered work runs turn-less, and so may the interrupted call's completion.
                Console.WriteLine("   observing 40s for the call's fate...");
                await Task.Delay(TimeSpan.FromSeconds(40)).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine("   timeline:");
                lock (timeline)
                {
                    foreach (var (at, source, detail) in timeline)
                        Console.WriteLine($"     +{(at - started).TotalSeconds,6:0.00}s  {source,-12} {detail}");
                }

                if (steerFailure is not null)
                    return new SteerBoundaryOutcome(true, false, $"steer threw: {steerFailure.Message}");

                if (toolCallId.Length == 0)
                    return new SteerBoundaryOutcome(true, false, mcpBridged
                        ? "the agent never opened the slow_probe call"
                        : "the agent never opened a tool call");

                if (steeredAt is null)
                    return new SteerBoundaryOutcome(true, false, "the steer never went out");

                if (steerOutcome == SteerOutcome.StartedNewTurn)
                    return new SteerBoundaryOutcome(
                        true, false, "startedNewTurn — the turn beat the steer, nothing was interrupted");

                List<(DateTime At, string Id, bool Success, string? Error, string? Result)> completions;
                lock (callCompletions) completions = callCompletions.ToList();
                var own = completions.Where(c => c.Id == toolCallId).ToList();
                var invoked = catalog.Started > 0;
                var ranToCompletion = catalog.Completed > 0;

                Console.WriteLine();
                if (mcpBridged)
                    Console.WriteLine($"   our tool: started={invoked} ranToCompletion={ranToCompletion}");
                Console.WriteLine($"   completions for the steered call: {own.Count}");
                foreach (var c in own)
                {
                    Console.WriteLine(
                        $"     +{(c.At - started).TotalSeconds:0.00}s success={c.Success} "
                        + $"error={Trim(c.Error)} result={Trim(c.Result)}");
                }

                if (own.Count == 0)
                {
                    // No completion either way is itself a finding: the row would hang in the UI.
                    return new SteerBoundaryOutcome(
                        false, false, "the call NEVER completed on the wire (no terminal frame at all)");
                }

                // Only a completion that arrives AFTER the steer can have been caused by it.
                var afterSteer = own.Where(c => c.At >= steeredAt.Value).ToList();
                if (afterSteer.Count == 0)
                {
                    var pre = own[0];
                    return new SteerBoundaryOutcome(
                        true,
                        false,
                        $"the call settled {(steeredAt.Value - pre.At).TotalSeconds:0.00}s BEFORE the steer "
                        + $"(success={pre.Success}, result={Trim(pre.Result)}) — nothing was interrupted, "
                        + "so this phase measured nothing");
                }

                var survived = afterSteer.Any(c => c.Success);
                var why = string.Join(" | ", afterSteer
                    .Where(c => !c.Success)
                    .Select(c => Trim(c.Error) is "(none)" ? Trim(c.Result) : Trim(c.Error)));
                return new SteerBoundaryOutcome(
                    false,
                    survived,
                    survived
                        ? "the call COMPLETED despite the steer"
                        : $"the call FAILED {(afterSteer[0].At - steeredAt.Value).TotalSeconds:0.00}s "
                          + $"after the steer: {why}");

            static string Trim(string? s)
            {
                if (string.IsNullOrWhiteSpace(s))
                    return "(none)";

                var flat = s.Replace("\r", string.Empty).Replace("\n", " ").Trim();
                return flat.Length > 200 ? flat.Substring(0, 200) + "…" : flat;
            }
            }
            finally
            {
                if (host is not null)
                    await host.DisposeAsync().ConfigureAwait(false);
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// A deliberately slow MCP tool, so a steer can be landed while it is provably in flight.
        /// Counts starts and completions separately: our side finishing says nothing about what the
        /// agent did with the call, and the gap between the two is the interesting part.
        /// </summary>
        private sealed class SlowProbeCatalog : IToolCatalog
        {
            private readonly TimeSpan _duration;
            private readonly Action<string, string> _note;
            private readonly Action? _onStarted;
            private int _started;
            private int _completed;

            public SlowProbeCatalog(TimeSpan duration, Action<string, string> note, Action? onStarted = null)
            {
                _duration = duration;
                _note = note;
                _onStarted = onStarted;
            }

            public int Started => Volatile.Read(ref _started);

            public int Completed => Volatile.Read(ref _completed);

            public IReadOnlyList<ToolDescriptor> Tools { get; } = new[]
            {
                new ToolDescriptor(
                    "slow_probe",
                    "A long-running diagnostic probe. Takes about 25 seconds to complete.",
                    "{\"type\":\"object\",\"properties\":{\"reason\":{\"type\":\"string\"}},\"required\":[\"reason\"]}"),
            };

            public async Task<ToolResult> InvokeAsync(
                string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                if (toolName != "slow_probe")
                    return new ToolResult(IsError: true, "{\"error\":\"unknown tool\"}");

                Interlocked.Increment(ref _started);
                _note("our-tool", "slow_probe STARTED");
                _onStarted?.Invoke();
                try
                {
                    await Task.Delay(_duration, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Worth distinguishing: a cancelled token would mean the abort reached our side,
                    // not just the agent's view of the call.
                    _note("our-tool", "slow_probe CANCELLED (token fired)");
                    throw;
                }

                Interlocked.Increment(ref _completed);
                _note("our-tool", "slow_probe COMPLETED");
                return new ToolResult(IsError: false, "{\"probe\":\"ok\",\"elapsedSeconds\":25}");
            }
        }

        private static string DescribeEvent(AgentEvent ev) => ev switch
        {
            AgentEvent.ToolCallStarted t => $"ToolCallStarted {t.Title} (kind={t.Kind})",
            AgentEvent.ToolCallUpdated t => $"ToolCallUpdated {t.Title} (kind={t.Kind})",
            AgentEvent.ToolCallCompleted t => $"ToolCallCompleted success={t.Success}",
            AgentEvent.EditProposed e => $"EditProposed {e.Path}",
            AgentEvent.AssistantTextDelta t => $"AssistantTextDelta \"{t.Text.Replace("\n", "\\n")}\"",
            AgentEvent.TurnCompleted c => $"TurnCompleted stopReason={c.StopReason}",
            _ => ev.GetType().Name,
        };

        /// <summary>Auto-allows like the console default, but keeps the arrival time of every request.</summary>
        private sealed class RecordingPermissionHandler : IPermissionHandler
        {
            private readonly Action<string, string> _note;
            private readonly List<(DateTime At, string Title)> _requests = new();

            public RecordingPermissionHandler(Action<string, string> note) => _note = note;

            public IReadOnlyList<(DateTime At, string Title)> Requests
            {
                get { lock (_requests) return _requests.ToList(); }
            }

            public Task<PermissionDecision> RequestAsync(
                PermissionRequest request, CancellationToken cancellationToken = default)
            {
                lock (_requests) _requests.Add((DateTime.Now, request.Title));
                _note("permission", $"request '{request.Title}' (kind={request.Kind}, tool={request.ToolName})");

                var choice = request.Options.FirstOrDefault(o =>
                                 o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways)
                             ?? request.Options.FirstOrDefault();
                return Task.FromResult(new PermissionDecision(choice?.OptionId ?? "allow", Cancelled: choice is null));
            }
        }

        private static async Task<int> RunRealClaudeModelsProofAsync()
        {
            Console.WriteLine("== code-wicket console: real Claude Code model selection ==");

            var workDir = HostScratch.ResolveDir("console/claude-models-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = new ClaudeCodeAgentProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                if (session is not IModelDiscovery discovery)
                {
                    Console.WriteLine("FAIL: session does not implement IModelDiscovery.");
                    return 1;
                }

                var models = discovery.DiscoveredModels;
                Console.WriteLine($"discovered {models.Count} model(s); current = {discovery.CurrentModelId ?? "(none)"}");
                foreach (var m in models)
                    Console.WriteLine($"  - {m.Id}  ({m.DisplayName})");
                Console.WriteLine();

                if (models.Count == 0 || string.IsNullOrEmpty(discovery.CurrentModelId))
                {
                    Console.WriteLine("FAIL: expected a non-empty model list and a current model id.");
                    return 1;
                }

                // Switch to a different model than the current one, then prove a turn still completes.
                var target = models.FirstOrDefault(m => m.Id != discovery.CurrentModelId) ?? models[0];
                Console.WriteLine($"switching model -> {target.Id} ({target.DisplayName})");
                await session.SetModelAsync(target.Id).ConfigureAwait(false);
                Console.WriteLine($"after switch: current = {discovery.CurrentModelId ?? "(none)"}");
                Console.WriteLine();

                var sawText = false;
                var stopReason = "(none)";
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "Reply with one short sentence. Do not use any tools.")))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta) sawText = true;
                    if (ev is AgentEvent.TurnCompleted c) stopReason = c.StopReason ?? stopReason;
                }

                var switched = discovery.CurrentModelId == target.Id;
                var passed = switched && sawText && stopReason == "end_turn";
                Console.WriteLine();
                Console.WriteLine(passed
                    ? $"PASS: discovered {models.Count} models, switched to {target.Id}, and completed a turn."
                    : $"FAIL: switched={switched}, sawText={sawText}, stopReason={stopReason}.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Proves model selection against Kiro's <b>v3</b> agent engine, which reaches it by a different
        /// route than every other backend we drive: its <c>session/new</c> response carries no models at
        /// all, it answers <c>session/set_model</c> with "Method not found" (the method kiro.dev's ACP
        /// docs list — those describe the default v2 engine), and it instead publishes the model selector
        /// in a <c>config_option_update</c> notification that lands AFTER the session opens. So this
        /// asserts the two things that notification unlocks: the requested launch model is applied once
        /// the selector arrives (v3 rejects the <c>--model</c> flag, so there is no other route), and a
        /// mid-session switch reaches <c>session/set_config_option</c>. Requires kiro-cli on PATH + login.
        /// </summary>
        private static async Task<int> RunRealKiroV3ModelsProofAsync()
        {
            Console.WriteLine("== code-wicket console: Kiro v3 model selection (config options) ==");

            // Requested at launch, so the pending-apply path is under test. Kept off Kiro's own default
            // ("auto") so "applied" can't be confused with "never touched"; if the catalog ever drops it,
            // the proof says so rather than failing an assertion about a model that no longer exists.
            const string requested = "claude-haiku-4.5";

            var workDir = HostScratch.ResolveDir("console/kiro-v3-models-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = new KiroAgentProvider(new KiroProviderOptions { AgentEngine = "v3" });

                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions { WorkspaceRootPath = workDir, ModelId = requested }, ide)
                    .ConfigureAwait(false);

                if (session is not IModelDiscovery discovery)
                {
                    Console.WriteLine("FAIL: session does not implement IModelDiscovery.");
                    return 1;
                }

                // Nothing carries the selector on the session/new response, so wait for the notification
                // rather than reading discovery straight away (this is exactly the race the fix closes).
                Console.WriteLine($"session/new discovered {discovery.DiscoveredModels.Count} model(s) (expected 0 on v3)");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (discovery.DiscoveredModels.Count == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(250).ConfigureAwait(false);

                var models = discovery.DiscoveredModels;
                Console.WriteLine($"after config_option_update: {models.Count} model(s); current = {discovery.CurrentModelId ?? "(none)"}");
                foreach (var m in models)
                    Console.WriteLine($"  - {m.Id}  ({m.DisplayName})");
                Console.WriteLine();

                if (models.Count == 0)
                {
                    Console.WriteLine("FAIL: no models discovered — the config_option_update notification never reached the session.");
                    return 1;
                }

                // The launch model: applied when the selector arrived, or reported as unavailable.
                var requestable = models.Any(m => m.Id == requested);
                var launchApplied = !requestable || discovery.CurrentModelId == requested;
                Console.WriteLine(requestable
                    ? $"launch model {requested}: applied={launchApplied} (current = {discovery.CurrentModelId ?? "(none)"})"
                    : $"launch model {requested}: no longer in Kiro's catalog — skipped (current = {discovery.CurrentModelId ?? "(none)"})");

                // The mid-session switch — the path that failed with "Method not found": session/set_model.
                var target = models.FirstOrDefault(m => m.Id != discovery.CurrentModelId) ?? models[0];
                Console.WriteLine($"switching model -> {target.Id} ({target.DisplayName})");
                await session.SetModelAsync(target.Id).ConfigureAwait(false);
                var switched = discovery.CurrentModelId == target.Id;
                Console.WriteLine($"after switch: current = {discovery.CurrentModelId ?? "(none)"}");
                Console.WriteLine();

                var sawText = false;
                var stopReason = "(none)";
                string? backendError = null;
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "Reply with one short sentence. Do not use any tools.")))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta) sawText = true;
                    if (ev is AgentEvent.TurnCompleted c) stopReason = c.StopReason ?? stopReason;
                    if (ev is AgentEvent.SessionError e) backendError ??= e.Message;
                }

                var selection = launchApplied && switched;
                var turnRan = sawText && stopReason == "end_turn";
                Console.WriteLine();
                if (selection && !turnRan && backendError is not null)
                {
                    // The backend refused the turn for its own reasons (an exhausted plan is the one
                    // seen in practice, and it streams its refusal as assistant text — so "saw text"
                    // alone can't stand in for a completed turn). Model selection is still proven.
                    Console.WriteLine($"PASS: v3 published {models.Count} models by notification, applied the launch model, and switched to {target.Id}.");
                    Console.WriteLine($"      (turn not run — the backend refused it: {backendError})");
                    return 0;
                }

                var passed = selection && turnRan;
                Console.WriteLine(passed
                    ? $"PASS: v3 published {models.Count} models by notification, applied the launch model, switched to {target.Id}, and completed a turn."
                    : $"FAIL: launchApplied={launchApplied}, switched={switched}, sawText={sawText}, stopReason={stopReason}.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Issue #126: what does a real <c>session/load</c> replay do to us, and does the backend's own
        /// replay mark line up with what we suppressed?
        ///
        /// <para>Establishes a fact in one session, drops it, then resumes the same conversation id in a
        /// fresh session and asks for the fact back. Three things are asserted, and the middle one is the
        /// point: the agent kept the context (so the resume really happened), <b>no event escaped from the
        /// replay</b> (the history must not be appended behind the user's next message — issue #33), and
        /// the wire tee shows the replay was there to escape from, counted by its <c>_meta.kiro.replay</c>
        /// marks. Without that last number "no events leaked" is equally consistent with a resume that
        /// replayed nothing at all, which is the false pass to avoid.</para>
        ///
        /// <para>Engine-parameterised (<c>kiro-resume v3</c>). The marker is v3's; the default engine
        /// replays too, so the leak assertions apply there as well and only the mark count differs.</para>
        /// </summary>
        private static async Task<int> RunRealKiroResumeProofAsync(string? engine)
        {
            var label = string.IsNullOrWhiteSpace(engine) ? "default engine" : engine!;
            Console.WriteLine($"== code-wicket console: Kiro session/load replay ({label}, issue #126) ==");

            var workDir = HostScratch.ResolveDir("console/kiro-resume-" + Guid.NewGuid().ToString("N"));
            var capture = Path.Combine(workDir, "acp.log");
            var teeOwned = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CWKT_ACP_LOG"));
            if (teeOwned)
                Environment.SetEnvironmentVariable("CWKT_ACP_LOG", capture);

            try
            {
                var codename = "ORCHID-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
                var ide = new ConsoleIdeServices(workDir);
                IAgentProvider provider = string.IsNullOrWhiteSpace(engine)
                    ? CreateKiroProvider()
                    : new KiroAgentProvider(new KiroProviderOptions { AgentEngine = engine });

                string conversationId;
                await using (var first = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false))
                {
                    conversationId = first.ConversationId ?? string.Empty;
                    Console.WriteLine($"session 1 conversation id = {conversationId}");
                    await foreach (var ev in first.SendAsync(new PromptInput(
                        $"Remember this for later: the project codename is {codename}. Reply OK and nothing else.")))
                    {
                        if (ev is AgentEvent.TurnCompleted)
                            Console.WriteLine("  session 1: codename established");
                    }
                }

                if (conversationId.Length == 0)
                {
                    Console.WriteLine("FAIL: the first session reported no conversation id to resume.");
                    return 1;
                }

                // Everything the resume emits, in order. A replay that leaked would show up here as the
                // whole first conversation — which is exactly how issue #33 presented.
                //
                // Split, not counted: a resume legitimately reports the resumed conversation's CURRENT
                // context fill, and measured here it does — one from session/load's own
                // _meta.contextUsage and one from an unmarked context_usage frame that follows the
                // response. Neither is history and both are wanted (the usage ring should show the fill
                // of the conversation you just reopened), so gating on "no events at all" would fail a
                // correct resume. Same line the working window already draws: usage is telemetry, not
                // work. What must never escape is transcript CONTENT.
                var duringResume = new List<string>();
                var contentLeaks = new List<string>();
                await using var resumed = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            ResumeConversationId = conversationId,
                            OutOfTurnEvents = ev =>
                            {
                                lock (duringResume)
                                {
                                    duringResume.Add(DescribeEvent(ev));
                                    if (ev is not AgentEvent.UsageUpdated)
                                        contentLeaks.Add(DescribeEvent(ev));
                                }
                            },
                        },
                        ide)
                    .ConfigureAwait(false);

                // The replay lands before session/load returns, but the tail can overtake the response —
                // so give it a moment to leak before declaring that it didn't.
                await Task.Delay(2000).ConfigureAwait(false);

                var resumeFailure = (resumed as IResumeFallbackReport)?.ResumeFailureReason;
                if (resumeFailure is not null)
                {
                    Console.WriteLine($"FAIL: the backend refused the resume: {resumeFailure}");
                    return 1;
                }

                var answer = new StringBuilder();
                await foreach (var ev in resumed.SendAsync(new PromptInput(
                    "What is the project codename? Reply with just the codename.")))
                {
                    if (ev is AgentEvent.AssistantTextDelta d)
                        answer.Append(d.Text);
                }

                var marked = CountReplayMarkedFrames(capture);
                var keptContext = answer.ToString().Contains(codename, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine();
                Console.WriteLine($"  frames marked _meta.kiro.replay on the wire : {marked}");
                Console.WriteLine($"  events emitted during the resume            : {duringResume.Count}");
                foreach (var d in duringResume.Take(10))
                    Console.WriteLine($"      - {d}");
                Console.WriteLine($"  of those, transcript CONTENT               : {contentLeaks.Count}");
                Console.WriteLine($"  agent still knows the codename              : {keptContext} ({answer.ToString().Trim()})");

                // The mark count is evidence, not a gate: the default engine may not mark at all, and a
                // marked count of zero there says nothing about whether the replay happened.
                var passed = keptContext && contentLeaks.Count == 0;
                Console.WriteLine();
                if (marked == 0)
                    Console.WriteLine("NOTE: no frame carried _meta.kiro.replay — either this engine does not mark "
                        + "its replay, or nothing was replayed. The leak assertion below still holds.");
                Console.WriteLine(passed
                    ? $"PASS: {label} resumed the conversation ({marked} marked frame(s) on the wire) and "
                      + "none of the replayed content reached the transcript."
                    : keptContext
                        ? $"FAIL: the resume worked but {contentLeaks.Count} content event(s) escaped from the "
                          + $"replay (issue #33): {string.Join(", ", contentLeaks.Take(5))}"
                        : "FAIL: the agent did not know the codename — the resume did not carry the context.");
                return passed ? 0 : 1;
            }
            finally
            {
                if (teeOwned)
                    Environment.SetEnvironmentVariable("CWKT_ACP_LOG", null);
                // The capture is the evidence, so the workspace deliberately survives the run.
            }
        }

        /// <summary>
        /// Counts <c>session/update</c> frames in a CWKT_ACP_LOG tee that the backend marked as replayed
        /// history (<c>_meta.kiro.replay</c>). Deliberately a raw substring count on the frame: the point
        /// is to know the replay was on the wire, and re-parsing it here would only mirror the mapper.
        /// </summary>
        private static int CountReplayMarkedFrames(string capturePath)
        {
            if (!File.Exists(capturePath))
                return 0;

            using var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var count = 0;
            foreach (var line in ReadLines(reader))
            {
                // The marker sits in the frame's own _meta, so "replay":true on a session/update line is
                // it. `replayId` (live text) does not match, having a different shape after the name.
                if (line.Contains("\"replay\":true", StringComparison.Ordinal) ||
                    line.Contains("\"replay\": true", StringComparison.Ordinal))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Issue #119: what does Kiro's task list look like on the wire, per agent engine?
        ///
        /// <para>The plan card is driven off Kiro's built-in task-list tool, and every shape the mapper
        /// recognises (<c>rawInput.tasks</c> / <c>completed_task_ids</c>, <c>rawOutput.items[].Json.tasks</c>)
        /// was read off a v2 capture. v3 is a different agent engine, so none of that is safe to assume —
        /// this drives a genuinely multi-step turn on whichever engine is named, prints every plan event
        /// the mapper produced, and then prints the raw task-tool frames from the wire tee beside them so
        /// a mapping that silently produced nothing is visible rather than inferred.</para>
        ///
        /// <para>Assertions are deliberately about the card the user would see, not about a JSON shape:
        /// a plan arrived at all, its final state has every task ticked off, and the task tool did not
        /// also leak a generic tool row (the card stands in for those). Requires kiro-cli + login.</para>
        /// </summary>
        /// <summary>
        /// Claude Code sub-agents on the wire (issue #125): prompt a real fan-out and assert that every
        /// call a sub-agent made is attributed to the row that launched it, and that a background launch
        /// is reported as a launch rather than as work that finished.
        /// <para>Like <c>kiro-tasks</c>, this prints the RAW <c>_meta.claudeCode</c> shapes beside the
        /// mapped events, because the failure this guards is an absence: a shape we stopped recognising
        /// produces a transcript that still renders, just flat and wrong. "No child was nested" and "no
        /// sub-agent ran" are the same output unless the frames are on screen next to the verdict.</para>
        /// <para>Deliberately asserts the BACKEND FACT the host rests on — that a child call names its
        /// parent — rather than any host behaviour, which the offline tests already pin against these
        /// same frames.</para>
        /// </summary>
        private static async Task<int> RunRealClaudeSubagentsProofAsync()
        {
            Console.WriteLine("== code-wicket console: Claude Code sub-agent nesting on the wire (issue #125) ==");

            var workDir = HostScratch.ResolveDir("console/claude-subagents-" + Guid.NewGuid().ToString("N"));
            var capture = Path.Combine(workDir, "acp.log");

            var teeOwned = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CWKT_ACP_LOG"));
            if (teeOwned)
                Environment.SetEnvironmentVariable("CWKT_ACP_LOG", capture);

            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = new ClaudeCodeAgentProvider();

                var launches = new List<AgentEvent.ToolCallStarted>();
                var children = new List<AgentEvent.ToolCallStarted>();
                var orphans = new List<AgentEvent.ToolCallStarted>();
                var completions = new List<AgentEvent.ToolCallCompleted>();
                var launchIds = new HashSet<string>(StringComparer.Ordinal);
                var sync = new object();

                void Record(AgentEvent ev)
                {
                    lock (sync)
                    {
                        switch (ev)
                        {
                            case AgentEvent.ToolCallStarted t when t.IsSubagentLaunch:
                                launches.Add(t);
                                launchIds.Add(t.ToolCallId);
                                break;
                            case AgentEvent.ToolCallStarted t when t.ParentToolCallId is not null:
                                children.Add(t);
                                break;
                            case AgentEvent.ToolCallStarted t:
                                orphans.Add(t);
                                break;
                            case AgentEvent.ToolCallCompleted c:
                                completions.Add(c);
                                break;
                        }
                    }
                }

                // Sub-agent work arrives out of turn on the async flavour — the children keep coming
                // after the turn that launched them has closed.
                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions { WorkspaceRootPath = workDir, OutOfTurnEvents = Record },
                        ide)
                    .ConfigureAwait(false);

                Console.WriteLine($"workspace = {workDir}");
                Console.WriteLine($"conversation id = {session.ConversationId}");
                Console.WriteLine();

                // The prompt has to make delegating the obvious move: two independent read-only
                // questions, explicitly asked for in parallel. A single question is answered directly
                // and no sub-agent is ever launched, which would prove nothing either way.
                var prompt =
                    "Launch two sub-agents IN PARALLEL to explore this directory, and wait for both:\n"
                    + "1. One to list every file here and describe what each contains.\n"
                    + "2. One to report the directory's total size and newest file.\n"
                    + "Each sub-agent must actually read the filesystem. Summarise both answers when they return.";

                var stopReason = "(none)";
                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    PrintEvent(ev);
                    Record(ev);
                    if (ev is AgentEvent.TurnCompleted c)
                        stopReason = c.StopReason ?? stopReason;
                }

                // An async fan-out keeps working after the turn closes; give it room to stream children.
                await Task.Delay(20_000).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"-- raw _meta.claudeCode shapes from the wire ({Path.GetFileName(capture)}) --");
                foreach (var line in ClaudeSubagentShapes(capture))
                    Console.WriteLine("  " + line);

                List<AgentEvent.ToolCallStarted> launchList;
                List<AgentEvent.ToolCallStarted> childList;
                List<AgentEvent.ToolCallStarted> orphanList;
                List<AgentEvent.ToolCallCompleted> completionList;
                HashSet<string> knownLaunches;
                lock (sync)
                {
                    launchList = launches.ToList();
                    childList = children.ToList();
                    orphanList = orphans.ToList();
                    completionList = completions.ToList();
                    knownLaunches = new HashSet<string>(launchIds, StringComparer.Ordinal);
                }

                // Every child must point at a row we actually saw open, or the host has an id it cannot
                // resolve and the call lands top-level anyway — nesting that does not nest.
                var unresolved = childList.Where(c => !knownLaunches.Contains(c.ParentToolCallId!)).ToList();
                var backgroundLaunches = completionList.Where(c => c.LaunchedInBackground).ToList();

                // The count the verdict actually rests on: how many opening tool_call frames CARRIED a
                // parent. Without it, "we attributed nothing" and "there was nothing to attribute" are
                // the same output — and they are opposite findings, one a mapper regression and one a
                // model that chose not to delegate.
                var wireParented = CountWireParentedCalls(capture);

                Console.WriteLine();
                Console.WriteLine($"  sub-agent launches     : {launchList.Count} [{string.Join(", ", launchList.Select(l => l.Title))}]");
                Console.WriteLine($"  parented calls ON WIRE : {wireParented}");
                Console.WriteLine($"  nested child calls     : {childList.Count}");
                Console.WriteLine($"  unattributed children  : {unresolved.Count}"
                    + (unresolved.Count == 0 ? string.Empty : $" [{string.Join(", ", unresolved.Select(u => u.ParentToolCallId))}]"));
                Console.WriteLine($"  top-level (main agent) : {orphanList.Count} [{string.Join(", ", orphanList.Select(o => o.Title))}]");
                Console.WriteLine($"  launched-not-finished  : {backgroundLaunches.Count}");
                foreach (var launched in backgroundLaunches)
                    Console.WriteLine($"      {launched.ToolCallId} resultText={(launched.ResultText is null ? "null (blob dropped)" : "PRESENT — " + launched.ResultText.Length + " chars")}");
                Console.WriteLine($"  stopReason             : {stopReason}");

                // The blob must never reach a row: the adapter's own text asks that no part of it be
                // quoted, and we would be putting it on screen.
                var blobLeaked = backgroundLaunches.Any(c => c.ResultText is not null);
                var delegated = launchList.Count > 0 && wireParented > 0;
                var attributedAll = childList.Count >= wireParented && unresolved.Count == 0;
                var passed = delegated && attributedAll && !blobLeaked;

                Console.WriteLine();
                if (!delegated)
                {
                    Console.WriteLine("INCONCLUSIVE: the model answered without delegating, so there was no fan-out to attribute. "
                        + "Re-run; if it keeps answering directly the prompt needs to make delegation the cheaper option.");
                    return 2;
                }

                Console.WriteLine(passed
                    ? $"PASS: {childList.Count} sub-agent call(s) across {launchList.Count} launch(es), every one attributed to its row."
                    : $"FAIL: the wire carried {wireParented} parented call(s) and the mapper attributed {childList.Count}"
                      + (blobLeaked ? "; a launch receipt also reached a row as its result" : string.Empty)
                      + ". See the raw shapes above.");
                return passed ? 0 : 1;
            }
            finally
            {
                if (teeOwned)
                    Environment.SetEnvironmentVariable("CWKT_ACP_LOG", null);
                // The capture is the evidence, so the workspace deliberately survives the run.
            }
        }

        /// <summary>
        /// How many opening <c>tool_call</c> frames in a capture named a parent. This is the wire's own
        /// answer to "how many calls should have nested", read independently of anything the mapper did
        /// — which is what lets the proof tell a mapper regression apart from a model that answered
        /// without delegating.
        /// </summary>
        private static int CountWireParentedCalls(string capturePath)
        {
            if (!File.Exists(capturePath))
                return 0;

            var count = 0;
            using var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            foreach (var line in ReadLines(reader))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;

                JsonElement root;
                try { root = JsonDocument.Parse(trimmed).RootElement.Clone(); }
                catch (JsonException) { continue; }

                if (root.TryGetProperty("params", out var parameters) &&
                    parameters.TryGetProperty("update", out var update) &&
                    update.TryGetProperty("sessionUpdate", out var kind) &&
                    kind.GetString() == "tool_call" &&
                    update.TryGetProperty("_meta", out var meta) &&
                    meta.TryGetProperty("claudeCode", out var claudeCode) &&
                    claudeCode.ValueKind == JsonValueKind.Object &&
                    claudeCode.TryGetProperty("parentToolUseId", out _))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// The distinct <c>_meta.claudeCode</c> shapes a capture contains, with a count each — what the
        /// mapper was GIVEN, summarised rather than dumped (a fan-out is hundreds of frames, and it is
        /// the SHAPES that say whether a decode still applies).
        /// </summary>
        private static IEnumerable<string> ClaudeSubagentShapes(string capturePath)
        {
            if (!File.Exists(capturePath))
            {
                yield return "(no capture — CWKT_ACP_LOG produced no file)";
                yield break;
            }

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            using (var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                foreach (var line in ReadLines(reader))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] != '{')
                        continue;

                    JsonElement root;
                    try { root = JsonDocument.Parse(trimmed).RootElement.Clone(); }
                    catch (JsonException) { continue; }

                    if (!root.TryGetProperty("params", out var parameters) ||
                        !parameters.TryGetProperty("update", out var update) ||
                        !update.TryGetProperty("_meta", out var meta) ||
                        !meta.TryGetProperty("claudeCode", out var claudeCode) ||
                        claudeCode.ValueKind != JsonValueKind.Object)
                        continue;

                    var kind = update.TryGetProperty("sessionUpdate", out var k) ? k.GetString() : "?";
                    var keys = string.Join("+", claudeCode.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
                    var key = kind + "  {" + keys + "}";
                    counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }

            if (counts.Count == 0)
            {
                yield return "(no frame on the wire carried _meta.claudeCode at all — "
                    + "either the adapter stopped sending it, or no session/update was captured)";
                yield break;
            }

            foreach (var entry in counts)
                yield return $"{entry.Value,4}  {entry.Key}";
        }

        private static async Task<int> RunRealKiroTasksProofAsync(string? engine)
        {
            var label = string.IsNullOrWhiteSpace(engine) ? "default engine" : engine!;
            Console.WriteLine($"== code-wicket console: Kiro task list on the wire ({label}, issue #119) ==");

            var workDir = HostScratch.ResolveDir("console/kiro-tasks-" + Guid.NewGuid().ToString("N"));
            var capture = Path.Combine(workDir, "acp.log");

            // The proof reads the raw frames back, so it tees them itself unless the caller already has.
            var teeOwned = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CWKT_ACP_LOG"));
            if (teeOwned)
                Environment.SetEnvironmentVariable("CWKT_ACP_LOG", capture);

            try
            {
                var ide = new ConsoleIdeServices(workDir);
                IAgentProvider provider = string.IsNullOrWhiteSpace(engine)
                    ? CreateKiroProvider()
                    : new KiroAgentProvider(new KiroProviderOptions { AgentEngine = engine });

                // Plan frames can land out of turn (a task ticked off after the turn's last message).
                var plans = new List<AgentEvent.PlanUpdated>();
                var toolRows = new List<string>();
                void Record(AgentEvent ev)
                {
                    switch (ev)
                    {
                        case AgentEvent.PlanUpdated p:
                            lock (plans) plans.Add(p);
                            Console.WriteLine($"\n  [plan] \"{p.Title ?? "(no title)"}\" — {p.Items.Count} task(s)");
                            foreach (var item in p.Items)
                                Console.WriteLine($"          [{item.Status}] {item.Description}");
                            break;
                        case AgentEvent.ToolCallStarted t:
                            lock (toolRows) toolRows.Add(t.Title);
                            break;
                    }
                }

                await using var session = await provider
                    .StartSessionAsync(
                        new SessionOptions
                        {
                            WorkspaceRootPath = workDir,
                            OutOfTurnEvents = Record,
                        },
                        ide)
                    .ConfigureAwait(false);

                Console.WriteLine($"workspace = {workDir}");
                Console.WriteLine($"conversation id = {session.ConversationId}");
                Console.WriteLine();

                // Three steps that must happen in order and each leave a trace on disk, so the agent has
                // a real reason to track them — a "list three facts" prompt gets answered in one message
                // with no task list at all.
                var prompt =
                    "Track this work as a task list with exactly three tasks, and mark each one complete "
                    + "as you finish it:\n"
                    + "1. Create a file notes.txt containing the single line: one\n"
                    + "2. Append a second line to notes.txt: two\n"
                    + "3. Read notes.txt back and tell me its final contents.\n"
                    + "Do all three, in order.";

                var stopReason = "(none)";
                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    PrintEvent(ev);
                    Record(ev);
                    if (ev is AgentEvent.TurnCompleted c)
                        stopReason = c.StopReason ?? stopReason;
                }

                // A plan frame can trail the turn's completion; give the tail a moment before judging.
                await Task.Delay(1500).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"-- raw task-tool frames from the wire ({Path.GetFileName(capture)}) --");
                foreach (var frame in TaskToolFrames(capture))
                    Console.WriteLine("  " + frame);

                Console.WriteLine();
                Console.WriteLine($"plan events: {plans.Count}; tool rows: [{string.Join(", ", toolRows)}]");

                var sawPlan = plans.Count > 0;
                // "Tasks complete" is the whole of issue #119: the card's final state must not leave a
                // finished task sitting pending. An empty final list is Kiro v2's "all done" disposal,
                // which the host renders by ticking everything off — so it counts as complete.
                var final = plans.Count > 0 ? plans[plans.Count - 1] : null;
                var finishedClean = final is not null &&
                    (final.Items.Count == 0 || final.Items.All(i => i.Status == PlanItemStatus.Completed));
                // The card replaces the tool row; a leaked row means the same work is shown twice.
                var leaked = toolRows.Where(LooksLikeTaskTool).ToList();

                Console.WriteLine();
                Console.WriteLine($"  plan surfaced          : {sawPlan}");
                Console.WriteLine($"  final state all done   : {finishedClean}"
                    + (final is null ? string.Empty : $" ({final.Items.Count} task(s) in the last frame)"));
                Console.WriteLine($"  task tool row leaked   : {(leaked.Count > 0 ? string.Join(", ", leaked) : "no")}");
                Console.WriteLine($"  stopReason             : {stopReason}");

                var passed = sawPlan && finishedClean && leaked.Count == 0;
                Console.WriteLine(passed
                    ? $"PASS: {label} drove the plan card to a fully-completed state."
                    : "FAIL: see the raw frames above — the mapper did not turn this engine's task list "
                      + "into a completed plan card.");
                return passed ? 0 : 1;
            }
            finally
            {
                if (teeOwned)
                    Environment.SetEnvironmentVariable("CWKT_ACP_LOG", null);
                // The capture is the evidence, so the workspace deliberately survives the run.
            }
        }

        /// <summary>
        /// A tool row that belongs to Kiro's task list, by the titles the two engines use. Only for
        /// reporting a leak in the proof above — the mapper recognises the tool by its input shape.
        /// </summary>
        private static bool LooksLikeTaskTool(string title) =>
            title.IndexOf("task", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("todo", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Pulls the task-tool <c>session/update</c> frames out of a CWKT_ACP_LOG tee, one line each,
        /// clamped for readability. This is the half of the proof that shows what the mapper was GIVEN,
        /// so a shape it failed to recognise is on screen next to the events it did produce.
        /// </summary>
        private static IEnumerable<string> TaskToolFrames(string capturePath)
        {
            if (!File.Exists(capturePath))
            {
                yield return "(no capture — CWKT_ACP_LOG produced no file)";
                yield break;
            }

            // The tee still holds the file open for writing, so read it sharing that handle.
            using var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var found = 0;
            foreach (var line in ReadLines(reader))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;
                if (trimmed.IndexOf("task", StringComparison.OrdinalIgnoreCase) < 0 &&
                    trimmed.IndexOf("todo", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                found++;
                yield return trimmed.Length <= 1200 ? trimmed : trimmed.Substring(0, 1200) + "…";
            }

            if (found == 0)
                yield return "(no frame on the wire mentioned a task or todo at all)";
        }

        /// <summary>
        /// Issue #108: does Claude Code's CLI actually hand over the conversations it has stored, and
        /// does the mapper read them?
        ///
        /// <para>Costs nothing and is re-runnable: <c>initialize</c> plus <c>session/list</c>, no
        /// <c>session/new</c> and no prompt, so nothing is created, nothing is metered and there is no
        /// conversation to tear down. That is also the whole shape the picker uses.</para>
        ///
        /// <para>Lists against the CURRENT directory rather than the scratch workspace, and that is the
        /// point rather than a convenience: Claude keys its store by a hash of the cwd, so a freshly
        /// created scratch folder is guaranteed to have nothing in it and the proof would report an
        /// empty list on every run, for a reason that has nothing to do with whether the code works.
        /// The repo you are standing in is where a terminal conversation would have happened.</para>
        ///
        /// <para>The verdict rests on a count read back from the wire tee independently of anything the
        /// mapper did, because the failure this guards is an ABSENCE: a capability that stopped being
        /// advertised, a response shape that changed, or a cwd we asked from that the backend keys
        /// nothing under all produce the same empty list. "We parsed nothing" and "there was nothing to
        /// parse" are the same output and opposite findings — one a regression, one an empty store —
        /// and only the raw frame count separates them.</para>
        ///
        /// <para>Requires the <c>claude-agent-acp</c> CLI and the user's Claude login.</para>
        /// </summary>
        private static async Task<int> RunClaudeSessionsProofAsync(
            string? workspaceOverride, string? sessionIdOverride)
        {
            Console.WriteLine("== code-wicket console: Claude Code session/list (issue #108) ==");

            // The scratch dir holds the capture only. The workspace we ASK about is separate — see the
            // doc comment: a new folder has no stored conversations by construction.
            var scratch = HostScratch.ResolveDir("console/claude-sessions-" + Guid.NewGuid().ToString("N"));
            var capture = Path.Combine(scratch, "acp.log");
            var workspace = string.IsNullOrWhiteSpace(workspaceOverride)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(workspaceOverride!);

            var teeOwned = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CWKT_ACP_LOG"));
            if (teeOwned)
                Environment.SetEnvironmentVariable("CWKT_ACP_LOG", capture);

            try
            {
                var ide = new ConsoleIdeServices(scratch);
                var provider = new ClaudeCodeAgentProvider();

                Console.WriteLine($"workspace asked about = {workspace}");
                Console.WriteLine($"capture               = {capture}");
                Console.WriteLine();

                var result = await provider
                    .ListBackendSessionsAsync(workspace, AgentWorkspaceScope.RepositoryRoot, ide)
                    .ConfigureAwait(false);

                // What the handshake CLAIMED, read straight off the tee rather than from the result —
                // the result folds "not advertised" and "advertised but empty" into the same shape, and
                // those are the two findings this proof exists to keep apart.
                var advertised = WireSessionListCapability(capture);

                // The wire's own answer to "how many conversations were sent", independent of the
                // mapper: session/list responses seen, and sessionId occurrences inside them.
                var (wireResponses, wireIds) = CountWireListedSessions(capture);

                Console.WriteLine($"  sessionCapabilities.list advertised : {advertised}");
                Console.WriteLine($"  session/list responses ON WIRE      : {wireResponses}");
                Console.WriteLine($"  sessionIds ON WIRE                  : {wireIds}");
                Console.WriteLine($"  supported (as reported)             : {result.Supported}"
                    + (result.Reason is null ? string.Empty : $" — {result.Reason}"));
                Console.WriteLine($"  sessions MAPPED                     : {result.Sessions.Count}");

                foreach (var s in result.Sessions.Take(5))
                {
                    var when = s.UpdatedAt?.ToString("u") ?? "(no time reported)";
                    var title = s.Title is null
                        ? "(no title reported)"
                        : s.Title.Length <= 60 ? s.Title : s.Title.Substring(0, 60) + "…";
                    Console.WriteLine($"      {s.Id}  {when}  {title}");
                }
                if (result.Sessions.Count > 5)
                    Console.WriteLine($"      … and {result.Sessions.Count - 5} more");

                Console.WriteLine();

                // The backend refused outright — not installed, signed out, or no session list. That is
                // a real failure of the list half: the picker has nothing to show and a reason to print.
                if (!result.Supported)
                {
                    Console.WriteLine($"FAIL (backend half): {provider.DisplayName} did not offer a session "
                        + $"list — {result.Reason ?? "no reason given"}. "
                        + $"The handshake advertised sessionCapabilities.list = {advertised}.");
                    return 1;
                }

                // The mapper is the half under test whenever the wire carried something. A response that
                // arrived with ids in it and mapped to nothing is a decode that stopped applying.
                if (wireIds > 0 && result.Sessions.Count == 0)
                {
                    Console.WriteLine($"FAIL (mapper half): the wire carried {wireIds} sessionId(s) across "
                        + $"{wireResponses} response(s) and the mapper produced none. The response shape "
                        + "has changed — read the capture above.");
                    return 1;
                }

                if (result.Sessions.Count == 0)
                {
                    // Not a pass: the list half was never exercised. Says which of the two reasons it is,
                    // because they need different things done about them.
                    Console.WriteLine("NOTHING TO LIST: the backend answered and had no stored conversations "
                        + $"for this cwd ({wireResponses} response(s), no sessionId on the wire). "
                        + "Run `claude` in this directory, hold a short conversation, then re-run — "
                        + "an empty store proves nothing either way.");
                    return 2;
                }

                // Every id the wire carried should have become a row. Fewer means entries were dropped;
                // the mapper skips a malformed one deliberately, so this reports rather than assumes.
                var mappedAll = result.Sessions.Count >= wireIds;
                if (!mappedAll)
                {
                    Console.WriteLine($"FAIL (mapper half): the wire carried {wireIds} sessionId(s) and the "
                        + $"mapper produced {result.Sessions.Count}; "
                        + $"{wireIds - result.Sessions.Count} entr(y/ies) were dropped.");
                    return 1;
                }

                Console.WriteLine($"list half PASS: {provider.DisplayName} listed {result.Sessions.Count} "
                    + $"stored conversation(s) for this cwd ({wireIds} sessionId(s) on the wire), "
                    + "handshake only.");
                Console.WriteLine();

                // Second half: OPEN one of those and keep what session/load replays (issue #108).
                // Newest by default, since that is the id the picker would have offered first - but
                // overridable, because the newest is very often the conversation the operator is
                // SITTING IN, and a backend will not load a conversation it currently has open
                // (measured: an Internal error, and the load falls back to a fresh session).
                var target = sessionIdOverride is { Length: > 0 }
                    ? result.Sessions.FirstOrDefault(s =>
                          string.Equals(s.Id, sessionIdOverride, StringComparison.OrdinalIgnoreCase))
                      ?? new BackendSessionInfo(sessionIdOverride, null, null, workspace)
                    : result.Sessions
                        .OrderByDescending(s => s.UpdatedAt ?? DateTimeOffset.MinValue)
                        .First();

                return await ProveImportedReplayAsync(provider, ide, workspace, target, scratch)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (teeOwned)
                    Environment.SetEnvironmentVariable("CWKT_ACP_LOG", null);
                // The capture is the evidence, so the workspace deliberately survives the run.
            }
        }

        /// <summary>
        /// The import half of <c>claude-sessions</c> (issue #108): open the newest stored conversation
        /// with <see cref="SessionOptions.ImportHistory"/> and keep what <c>session/load</c> replays,
        /// instead of dropping it.
        /// <para>The verdict rests on a per-<c>sessionUpdate</c>-kind breakdown read from the tee
        /// INDEPENDENTLY of anything the import did, for the reason the list half already has a raw
        /// count: "we mapped nothing" and "there was nothing to map" are the same output and opposite
        /// findings — a conversation with no turns in it, an adapter that stopped replaying, and an
        /// import gate that stopped firing all produce an empty transcript. Only the raw frames
        /// separate them, and the breakdown by KIND is what says which shapes are being missed rather
        /// than merely how many.</para>
        /// <para>It still sends no prompt: a load is the backend re-reading its own store, so this
        /// stays free and re-runnable.</para>
        /// </summary>
        private static async Task<int> ProveImportedReplayAsync(
            ClaudeCodeAgentProvider provider, ConsoleIdeServices ide, string workspace,
            BackendSessionInfo target, string scratch)
        {
            Console.WriteLine("-- import half: keeping what session/load replays (issue #108) --");
            Console.WriteLine($"  loading   : {target.Id}");

            // Its OWN capture file. The list half's handshake and session/list are in the other one,
            // and a breakdown counting those would report frames that have nothing to do with the
            // replay. The tee path is read when the connection is created, so setting it here is
            // enough to separate them.
            var importCapture = Path.Combine(scratch, "acp-import.log");
            var previousTee = Environment.GetEnvironmentVariable("CWKT_ACP_LOG");
            Environment.SetEnvironmentVariable("CWKT_ACP_LOG", importCapture);

            try
            {
                await using var session = await provider.StartSessionAsync(
                    new SessionOptions
                    {
                        WorkspaceRootPath = workspace,
                        ResumeConversationId = target.Id,
                        ImportHistory = true,
                    },
                    ide).ConfigureAwait(false);

                var imported = (session as IImportedHistoryReport)?.ImportedHistory
                    ?? (IReadOnlyList<ImportedTurnEntry>)Array.Empty<ImportedTurnEntry>();

                // What the engine will put on the wire, not what the capture holds: one message the
                // user sent replays as several chunks, so the joined list is the honest count of
                // MESSAGES against the wire's count of frames.
                var joined = ImportedTurnEntry.Coalesce(imported);
                var users = joined.Count(e => e.Role == ImportedTurnEntry.UserRole);
                var agents = joined.Count(e => e.Role == ImportedTurnEntry.AgentRole);

                var wireKinds = CountWireUpdateKinds(importCapture);
                var wireUpdates = wireKinds.Sum(k => k.Value);

                Console.WriteLine($"  cwd asked from : {(session as IWorkspaceRootReport)?.WorkspaceRoot?.Root ?? workspace}");
                Console.WriteLine($"  session/update frames ON WIRE, by kind:");
                if (wireKinds.Count == 0)
                    Console.WriteLine("      (none)");
                foreach (var kind in wireKinds.OrderByDescending(k => k.Value))
                    Console.WriteLine($"      {kind.Value,5}  {kind.Key}");
                Console.WriteLine($"  entries MAPPED : {joined.Count} ({users} user, {agents} agent)"
                    + (imported.Count == joined.Count
                        ? string.Empty
                        : $"; {imported.Count} before consecutive user chunks were joined"));

                foreach (var entry in joined.Take(5))
                {
                    var what = entry.Role == ImportedTurnEntry.UserRole
                        ? Preview(entry.Text)
                        : entry.Event is null ? "(no event)" : Preview(DescribeEvent(entry.Event));
                    Console.WriteLine($"      {entry.Role,-5}  {what}");
                }
                if (joined.Count > 5)
                    Console.WriteLine($"      … and {joined.Count - 5} more");

                Console.WriteLine();

                // The backend refused the load and we started fresh instead. There is no replay to
                // read, and reporting the resulting empty transcript as anything but a failure of this
                // half would hide the one thing that went wrong.
                var fallback = (session as IResumeFallbackReport)?.ResumeFailureReason;
                if (fallback is not null)
                {
                    Console.WriteLine($"FAIL (backend half): the backend would not load {target.Id} — {fallback}");
                    Console.WriteLine("  If that id is the conversation you are SITTING IN, this is expected: "
                        + "a backend will not load a conversation it currently has open. Name an older "
                        + "one — `claude-sessions \"\" <sessionId>` — and re-run.");
                    return 1;
                }

                // Frames arrived and none became an entry: the import gate or the mapper stopped
                // applying. This is the failure the raw breakdown exists to make visible.
                if (wireUpdates > 0 && joined.Count == 0)
                {
                    Console.WriteLine($"FAIL (import half): the wire carried {wireUpdates} session/update "
                        + "frame(s) during the load and the import produced no entries. Read the kinds "
                        + $"above against AcpClientTarget.ImportUpdate — capture: {importCapture}");
                    return 1;
                }

                if (joined.Count == 0)
                {
                    // Not a pass: nothing was exercised. A stored conversation can legitimately have
                    // no turns in it, so this says which it is rather than reporting a green run over
                    // an untested path.
                    Console.WriteLine($"NOTHING TO IMPORT: {target.Id} loaded and replayed no "
                        + "session/update frames at all. Hold a short conversation in `claude` in this "
                        + "directory and re-run — an empty conversation proves nothing either way.");
                    return 2;
                }

                Console.WriteLine($"PASS: loaded {target.Id} and kept {joined.Count} entr(ies) "
                    + $"({users} user, {agents} agent) from {wireUpdates} replayed session/update "
                    + "frame(s) — no prompt sent.");
                return 0;
            }
            finally
            {
                Environment.SetEnvironmentVariable("CWKT_ACP_LOG", previousTee);
                // The import capture is the evidence, so it deliberately survives the run.
            }
        }

        /// <summary>
        /// Every <c>session/update</c> frame in a tee, counted by its <c>sessionUpdate</c> kind. A raw
        /// scan, deliberately reading nothing the way the mapper reads it — re-decoding here would
        /// only mirror the code under test, and a mapper that decodes nothing would agree with itself.
        /// </summary>
        private static Dictionary<string, int> CountWireUpdateKinds(string capturePath)
        {
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!File.Exists(capturePath))
                return kinds;

            using var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            foreach (var line in ReadLines(reader))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;

                JsonElement root;
                try { root = JsonDocument.Parse(trimmed).RootElement.Clone(); }
                catch (JsonException) { continue; }

                if (!root.TryGetProperty("method", out var method)
                    || method.ValueKind != JsonValueKind.String
                    || method.GetString() != "session/update"
                    || !root.TryGetProperty("params", out var p)
                    || !p.TryGetProperty("update", out var update)
                    || update.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // An update with no kind at all is its own finding — a frame shape nothing downstream
                // can route — so it is counted rather than skipped.
                var kind = update.TryGetProperty("sessionUpdate", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString()!
                    : "(no sessionUpdate key)";
                kinds[kind] = kinds.TryGetValue(kind, out var n) ? n + 1 : 1;
            }

            return kinds;
        }

        /// <summary>
        /// What the <c>initialize</c> response claimed about <c>sessionCapabilities.list</c>, read off
        /// the tee. Separate from the provider's own parse on purpose: the provider folds a missing
        /// capability into the same refusal as a missing CLI, and this proof has to tell those apart.
        /// </summary>
        private static string WireSessionListCapability(string capturePath)
        {
            if (!File.Exists(capturePath))
                return "(no capture — CWKT_ACP_LOG produced no file)";

            using var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            foreach (var line in ReadLines(reader))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;

                JsonElement root;
                try { root = JsonDocument.Parse(trimmed).RootElement.Clone(); }
                catch (JsonException) { continue; }

                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("agentCapabilities", out var caps))
                {
                    if (!caps.TryGetProperty("sessionCapabilities", out var sessionCaps))
                        return "absent (no sessionCapabilities)";
                    if (!sessionCaps.TryGetProperty("list", out var list))
                        return "absent (no sessionCapabilities.list)";
                    return list.ToString();
                }
            }

            return "(no initialize response in the capture)";
        }

        /// <summary>
        /// The wire's own count of what <c>session/list</c> returned: how many responses came back, and
        /// how many <c>sessionId</c>s they carried between them. A raw scan on purpose — re-parsing the
        /// entries the way the mapper does would only mirror it, and then a mapper that decodes nothing
        /// would agree with itself.
        /// </summary>
        private static (int Responses, int SessionIds) CountWireListedSessions(string capturePath)
        {
            if (!File.Exists(capturePath))
                return (0, 0);

            var responses = 0;
            var ids = 0;

            using var stream = new FileStream(
                capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            foreach (var line in ReadLines(reader))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    continue;

                JsonElement root;
                try { root = JsonDocument.Parse(trimmed).RootElement.Clone(); }
                catch (JsonException) { continue; }

                // A session/list RESPONSE, not the request: the request carries no "sessions" array, and
                // matching on the method name would count the outbound frame as an answer.
                if (!root.TryGetProperty("result", out var result)
                    || result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("sessions", out var sessions)
                    || sessions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                responses++;

                // Counted by the key's presence rather than by array length: an entry the backend sent
                // without an id is one the mapper is right to drop, and counting it here would make a
                // correct drop look like a lost row.
                foreach (var entry in sessions.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("sessionId", out _))
                        ids++;
            }

            return (responses, ids);
        }

        /// <summary>
        /// Frames from a <c>CWKT_ACP_LOG</c> tee, with the log's own prefix removed.
        /// </summary>
        /// <remarks>
        /// Since issue #211 each line is <c>[timestamp] -&gt; {json}</c> or <c>[timestamp] &lt;- {json}</c>
        /// — one file carrying both directions, because reading one of two files looks complete while
        /// being half the conversation. Every proof that parses the tee comes through here, which is
        /// what kept that change to one writer and one reader.
        /// <para>The prefix is stripped rather than parsed: no proof asks about the time or the
        /// direction today, and a reader that returned a tuple nobody used would be a shape invented
        /// for its own sake. It degrades to the raw line, so a log written before this — or by a build
        /// that predates it — still reads.</para>
        /// </remarks>
        private static IEnumerable<string> ReadLines(StreamReader reader)
        {
            while (reader.ReadLine() is { } line)
                yield return FrameLogSink.StripPrefix(line);
        }



        /// <summary>
        /// Drives a real Claude Code session through the out-of-process engine exe — the exact VSIX
        /// topology (EngineClient + ShellRpcTarget + MCP pipe host + relay child) minus devenv. Both
        /// debug tees are enabled (CWKT_ACP_LOG on the engine, rawLogPath on the shell channel) and
        /// the proof asserts they captured bytes, reproducing/regressing the empty-acp.log bug.
        /// </summary>
        private static async Task<int> RunEngineClaudeAsync()
        {
            Console.WriteLine("== code-wicket console: real Claude Code via out-of-proc engine + tee logs ==");

            var workDir = HostScratch.ResolveDir("console/engine-claude-" + Guid.NewGuid().ToString("N"));
            var acpLog = Path.Combine(workDir, "acp.log");
            var channelLog = Path.Combine(workDir, "engine-channel.log");
            try
            {
                var enginePath = Path.Combine(AppContext.BaseDirectory, Branding.EngineExeName);
                var ide = new StubIdeServices(workDir, solutionName: "Console");

                await using var engine = EngineClient.Launch(
                    enginePath, ide,
                    log: line => Console.Error.WriteLine("  [engine] " + line),
                    env: new Dictionary<string, string> { ["CWKT_ACP_LOG"] = acpLog },
                    rawLogPath: channelLog);

                var sawText = false;
                engine.AgentEvent += ev =>
                {
                    if (ev.Type == "text") sawText = true;
                    Console.WriteLine($"  [event] {ev.Type}{(ev.Text is null ? string.Empty : ": " + ev.Text)}");
                };

                var start = await engine.StartSessionAsync(new StartSessionRequest(
                    "claude-code", null, workDir, "AcceptAll", null)).ConfigureAwait(false);
                Console.WriteLine($"started session: {start.ConversationId}");

                var prompt = await engine.PromptAsync(
                    "Reply with one short sentence introducing yourself. Do not use any tools.").ConfigureAwait(false);
                Console.WriteLine($"prompt stop reason: {prompt.StopReason}");

                long AcpBytes() => new FileInfo(acpLog) is { Exists: true } f ? f.Length : 0;
                long ChannelBytes() => new FileInfo(channelLog) is { Exists: true } f ? f.Length : 0;
                Console.WriteLine($"acp.log bytes: {AcpBytes()}; engine-channel.log bytes: {ChannelBytes()}");

                var passed = sawText && prompt.StopReason == "end_turn" && AcpBytes() > 0 && ChannelBytes() > 0;
                Console.WriteLine(passed
                    ? "PASS: turn streamed and both debug tees captured raw bytes."
                    : $"FAIL: sawText={sawText}, stopReason={prompt.StopReason}, acpLog={AcpBytes()}B, channelLog={ChannelBytes()}B.");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Proves the MCP bridge against real Claude Code, in the exact VSIX topology: the engine
        /// hosts the IDE tool catalog over the named pipe, Claude Code spawns the engine's
        /// --mcp-stdio relay, and the model is asked to call the "echo" tool. Passes only when the
        /// invocation round-trips into the host catalog. Both debug tees + the MCP frame tee are on
        /// and the agent's stderr streams through, so a failure leaves the full evidence trail.
        /// </summary>
        private static async Task<int> RunRealClaudeMcpProofAsync()
        {
            Console.WriteLine("== code-wicket console: real Claude Code MCP bridge proof ==");

            var workDir = HostScratch.ResolveDir("console/claude-mcp-" + Guid.NewGuid().ToString("N"));
            var acpLog = Path.Combine(workDir, "acp.log");
            var mcpLog = Path.Combine(workDir, "mcp.log");
            var catalog = new RecordingToolCatalog(new EchoToolCatalog());
            try
            {
                var enginePath = Path.Combine(AppContext.BaseDirectory, Branding.EngineExeName);
                var ide = new ToolsOverrideIdeServices(new StubIdeServices(workDir, solutionName: "Console"), catalog);

                await using var engine = EngineClient.Launch(
                    enginePath, ide,
                    log: line => Console.Error.WriteLine("  [engine] " + line),
                    env: new Dictionary<string, string>
                    {
                        ["CWKT_ACP_LOG"] = acpLog,
                        ["CWKT_MCP_LOG"] = mcpLog,
                    });

                var reply = new System.Text.StringBuilder();
                engine.AgentEvent += ev =>
                {
                    if (ev.Type == "text" && ev.Text is not null) reply.Append(ev.Text);
                    Console.WriteLine($"  [event] {ev.Type}{(ev.Text is null ? string.Empty : ": " + ev.Text)}");
                };

                var start = await engine.StartSessionAsync(new StartSessionRequest(
                    "claude-code", null, workDir, "AcceptAll", null)).ConfigureAwait(false);
                Console.WriteLine($"started session: {start.ConversationId}");

                var prompt = await engine.PromptAsync(
                    $"Call the MCP tool named \"echo\" (from the {UnderscoredServer} server) with the " +
                    "argument text=\"mcp-proof\", then repeat the tool's output verbatim. If the tool " +
                    "is not available to you, say exactly: NO MCP TOOL.").ConfigureAwait(false);
                Console.WriteLine($"prompt stop reason: {prompt.StopReason}");

                // The wire evidence, pass or fail: what session/new advertised, and both directions
                // of the MCP conversation (empty = the agent never spoke to the relay at all).
                //
                // BOTH tees are one file carrying both directions — ACP since #211, MCP since this
                // proof stopped needing two DumpLogs to show one conversation. The ".send" companions
                // they used to write are gone, and reading one that no longer exists is how the
                // session/new evidence line silently vanished from every run (#247).
                //
                // The frame wanted is still the outbound one, and it is the only one naming the method
                // — the response is correlated by id.
                if (ReadSharedLines(acpLog).FirstOrDefault(l => l.Contains("session/new")) is { } newFrame)
                    Console.WriteLine("session/new sent: " + (newFrame.Length > 500 ? newFrame.Substring(0, 500) + "…" : newFrame));
                DumpLog("mcp, both directions (-> = we sent)", mcpLog);

                var passed = catalog.LastTool == "echo" && prompt.StopReason == "end_turn";
                Console.WriteLine(passed
                    ? $"PASS: Claude Code invoked the IDE tool over the MCP bridge (echoed reply: {reply.ToString().Trim()})"
                    : $"FAIL: invoked tool = {catalog.LastTool ?? "(none)"}; stopReason={prompt.StopReason}; reply: {reply.ToString().Trim()}; evidence kept in {workDir}");

                if (passed)
                    try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
                return passed ? 0 : 1;
            }
            catch
            {
                Console.WriteLine($"evidence kept in {workDir}");
                throw;
            }
        }

        /// <summary>Reads a log another process may still hold open for writing.</summary>
        private static IEnumerable<string> ReadSharedLines(string path)
        {
            if (!File.Exists(path))
                yield break;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                yield return line;
        }

        private static void DumpLog(string label, string path)
        {
            var lines = ReadSharedLines(path).ToList();
            Console.WriteLine($"-- {label}: {lines.Count} frame(s) --");
            foreach (var line in lines)
                Console.WriteLine("   " + (line.Length > 400 ? line.Substring(0, 400) + "…" : line));
        }

        /// <summary>Wraps a host IIdeServices, substituting the tool catalog (proof plumbing).</summary>
        private sealed class ToolsOverrideIdeServices : IIdeServices
        {
            private readonly IIdeServices _inner;

            public ToolsOverrideIdeServices(IIdeServices inner, IToolCatalog tools)
            {
                _inner = inner;
                Tools = tools;
            }

            public IWorkspaceContext Workspace => _inner.Workspace;
            public IEditApplier Edits => _inner.Edits;
            public IToolCatalog Tools { get; }
            public IPermissionHandler Permissions => _inner.Permissions;
        }

        /// <summary>Records the last tool the agent invoked, so the proof can assert the round-trip.</summary>
        private sealed class RecordingToolCatalog : IToolCatalog
        {
            private readonly IToolCatalog _inner;

            public RecordingToolCatalog(IToolCatalog inner) => _inner = inner;

            public string? LastTool { get; private set; }

            public IReadOnlyList<ToolDescriptor> Tools => _inner.Tools;

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            {
                LastTool = toolName;
                return _inner.InvokeAsync(toolName, argumentsJson, cancellationToken);
            }
        }

        /// <summary>
        /// Issue #54, end to end against a real kiro-cli: a solution nested inside a repository whose
        /// <c>.kiro/steering</c> lives at the repository root must still get that steering.
        /// <para>
        /// This is the decisive check that the widened directory reaches the agent through the ACP
        /// <c>session/new</c> cwd and not merely the CLI process's cwd. The fixture plants a steering
        /// file with a word the model cannot otherwise know, then asks for it: pre-fix the same layout
        /// answers "I don't know" (probed directly against kiro-cli 2.13.0 before the fix).
        /// </para>
        /// </summary>
        private static async Task<int> RunRealKiroSteeringProofAsync()
        {
            Console.WriteLine("== code-wicket console: Kiro steering above the solution (issue #54) ==");

            const string magic = "BANANA54";
            var repo = HostScratch.ResolveDir("console/kiro-steering-" + Guid.NewGuid().ToString("N"));
            var steering = Directory.CreateDirectory(Path.Combine(repo, ".kiro", "steering")).FullName;
            // A repository marker, so the walk terminates exactly as it would in a real checkout.
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            File.WriteAllText(
                Path.Combine(steering, "magic.md"),
                "# Magic word\n\nWhen the user asks for the magic word, you MUST reply with exactly " +
                $"one word: {magic}\n");

            // The solution sits two levels below the .kiro folder — the shape from the issue.
            var solution = Directory.CreateDirectory(Path.Combine(repo, "src", "solution")).FullName;

            try
            {
                var ide = new ConsoleIdeServices(solution);
                var provider = new KiroAgentProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = solution }, ide)
                    .ConfigureAwait(false);

                var resolved = (session as IWorkspaceRootReport)?.WorkspaceRoot;
                Console.WriteLine($"solution root   = {solution}");
                Console.WriteLine($"working dir     = {resolved?.Root ?? "(not reported)"}");
                Console.WriteLine($"marker          = {resolved?.MarkerPath ?? "(none)"}");
                Console.WriteLine($"reason          = {resolved?.Reason ?? "(none)"}");
                Console.WriteLine();

                var widened = resolved is { Widened: true } &&
                    string.Equals(
                        Path.GetFullPath(resolved.Root), Path.GetFullPath(repo),
                        StringComparison.OrdinalIgnoreCase);

                var answer = new System.Text.StringBuilder();
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "What is the magic word? Reply with only that one word, and use no tools.")))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta delta)
                        answer.Append(delta.Text);
                }

                var steeringApplied = answer.ToString().Contains(magic, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine();
                Console.WriteLine(widened && steeringApplied
                    ? $"PASS: working directory widened to the repository root and the agent applied its steering ({magic})."
                    : $"FAIL: widened={widened}, steeringApplied={steeringApplied}, answer=\"{answer.ToString().Trim()}\".");
                return widened && steeringApplied ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(repo, recursive: true); }
                catch { /* scratch cleanup is best effort */ }
            }
        }

        /// <summary>
        /// Proves that a pasted image REACHES THE MODEL, not merely that the backend accepted the block
        /// (issue #118).
        ///
        /// <para>Everything else covering this stops short of the question. The unit tests drive a fake
        /// ACP agent, so they pin the block we emit and nothing beyond it; the handshake's
        /// <c>promptCapabilities.image</c> is a claim by the adapter, not evidence that the pixels
        /// travelled. This asks the only question that distinguishes them: the image carries three
        /// colour bands in an order the model cannot guess, and the answer either names them or it
        /// does not.</para>
        ///
        /// <para><b>The regression worth catching is the SILENT one.</b> If a backend stops advertising
        /// image support, the host degrades to saving the file and naming its path — which still works,
        /// so nobody notices we stopped sending images at all. Same if an adapter starts requiring
        /// <c>uri</c> alongside <c>data</c>, or accepts the block and discards it. Each of those leaves
        /// the feature apparently healthy.</para>
        ///
        /// <para>Order, not colour, is the unguessable part: primaries are named consistently by every
        /// model, while their arrangement is 1-in-6. The raw reply is always printed, because a model
        /// that cannot see the image usually says so, and that sentence is the most useful output this
        /// proof produces.</para>
        /// </summary>
        private static async Task<int> RunImagePromptProofAsync(string backend)
        {
            Console.WriteLine($"== code-wicket console: does a pasted image reach the model? ({backend}, issue #118) ==");
            Console.WriteLine();

            // Deliberately NOT the canonical red/green/blue: the order is what proves the image was
            // read rather than guessed.
            var bands = new (string Name, byte R, byte G, byte B)[]
            {
                ("green", 0x00, 0xA0, 0x00),
                ("blue", 0x00, 0x00, 0xC0),
                ("red", 0xC0, 0x00, 0x00),
            };

            var png = EncodeBandedPng(240, 240, bands);
            var workDir = HostScratch.ResolveDir("console/image-" + Guid.NewGuid().ToString("N"));

            try
            {
                var ide = new ConsoleIdeServices(workDir);
                IAgentProvider provider = backend.Equals("kiro", StringComparison.OrdinalIgnoreCase)
                    ? new KiroAgentProvider()
                    : new ClaudeCodeAgentProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                // Read AFTER the session opened: the capability comes from the handshake, so the value
                // on the provider beforehand predates the answer.
                var declared = provider.Capabilities.HasFlag(AgentCapabilities.ImagePrompts);
                Console.WriteLine($"promptCapabilities.image = {declared}");
                Console.WriteLine($"image                    = {png.Length} bytes, 240x240, bands top-to-bottom: "
                    + string.Join(", ", Array.ConvertAll(bands, b => b.Name)));
                Console.WriteLine();

                var answer = new System.Text.StringBuilder();
                var prompt = new PromptInput(
                    "Look at the attached image. It is divided into three equal horizontal colour bands. "
                    + "Reply with only those three colour names, top to bottom, separated by commas. "
                    + "Use no tools. If you cannot see an image, say exactly: NO IMAGE.")
                {
                    Attachments = new[] { new PromptAttachment("image/png", Convert.ToBase64String(png)) },
                };

                await foreach (var ev in session.SendAsync(prompt).ConfigureAwait(false))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.AssistantTextDelta delta)
                        answer.Append(delta.Text);
                }

                var reply = answer.ToString().Trim();
                var sawImage = NamesBandsInOrder(reply, bands);

                Console.WriteLine();
                Console.WriteLine($"reply = \"{reply}\"");
                Console.WriteLine();
                Console.WriteLine(sawImage
                    ? $"PASS: {backend} forwarded the image — the model named the bands in the order only the picture carries."
                    : $"FAIL: the model did not name the bands in order (declared={declared}). Either the image "
                      + "never reached it, or it could not read it.");
                return sawImage ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch { /* scratch cleanup is best effort */ }
            }
        }

        /// <summary>
        /// Whether the reply names every band, in the image's own top-to-bottom order. Position-based
        /// rather than an exact-string match, because a model is entitled to answer "green, blue, red"
        /// or "Green, Blue and Red." — the ORDER is the claim under test, not the phrasing.
        /// </summary>
        private static bool NamesBandsInOrder(string reply, (string Name, byte R, byte G, byte B)[] bands)
        {
            var searchFrom = 0;
            foreach (var band in bands)
            {
                var at = reply.IndexOf(band.Name, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    return false;
                searchFrom = at + band.Name.Length;
            }
            return true;
        }

        /// <summary>
        /// A PNG of horizontal colour bands, hand-encoded so the proof carries no image dependency —
        /// this host is net10.0 with no WPF, and pulling in a drawing library for one test image would
        /// be a package on the ship graph for a diagnostic.
        /// <para>Truecolour (8-bit RGB), one filter byte per scanline, zlib-compressed as the format
        /// requires. Deterministic: the same bands always produce the same bytes.</para>
        /// </summary>
        private static byte[] EncodeBandedPng(int width, int height, (string Name, byte R, byte G, byte B)[] bands)
        {
            var raw = new byte[height * (1 + width * 3)];
            var at = 0;
            for (var y = 0; y < height; y++)
            {
                raw[at++] = 0; // filter: none
                var band = bands[Math.Min(y * bands.Length / height, bands.Length - 1)];
                for (var x = 0; x < width; x++)
                {
                    raw[at++] = band.R;
                    raw[at++] = band.G;
                    raw[at++] = band.B;
                }
            }

            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var zlib = new System.IO.Compression.ZLibStream(
                    buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(raw, 0, raw.Length);
                }
                compressed = buffer.ToArray();
            }

            var header = new byte[13];
            WriteBigEndian(header, 0, width);
            WriteBigEndian(header, 4, height);
            header[8] = 8; // bit depth
            header[9] = 2; // colour type: truecolour
            // 10..12 stay zero: deflate, adaptive filtering, no interlace.

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                WriteChunk(png, "IHDR", header);
                WriteChunk(png, "IDAT", compressed);
                WriteChunk(png, "IEND", Array.Empty<byte>());
                return png.ToArray();
            }
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, data.Length);
            output.Write(length, 0, 4);

            var body = new byte[4 + data.Length];
            for (var i = 0; i < 4; i++)
                body[i] = (byte)type[i];
            Array.Copy(data, 0, body, 4, data.Length);
            output.Write(body, 0, body.Length);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, unchecked((int)Crc32(body)));
            output.Write(crc, 0, 4);
        }

        private static void WriteBigEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        // The PNG chunk CRC. Hand-rolled for the same reason the encoder is: System.IO.Hashing is a
        // package, and this is fifteen lines.
        private static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// Proves how a real Claude Code routes file writes. Unlike Kiro (which writes disk itself
        /// and only mirrors a diff), spec-ACP agents are expected to route writes through the
        /// client's <c>fs/write_text_file</c> — i.e. our IEditApplier. Passes if the file lands via
        /// either path, and reports which one, so the VSIX diff/undo behavior per backend is known.
        /// </summary>
        private static async Task<int> RunRealClaudeCodeEditProofAsync()
        {
            Console.WriteLine("== code-wicket console: real Claude Code edit routing proof ==");

            var workDir = HostScratch.ResolveDir("console/claude-edit-" + Guid.NewGuid().ToString("N"));
            const string fileName = "hello.txt";
            const string marker = "hello from claude code via acp";
            var expectedPath = Path.Combine(workDir, fileName);

            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = new ClaudeCodeAgentProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                Console.WriteLine($"workspace = {workDir}");
                Console.WriteLine($"conversation id = {session.ConversationId}");
                Console.WriteLine();

                var prompt =
                    $"Create a file named {fileName} in the current workspace directory whose entire " +
                    $"contents are exactly: {marker}\nUse your file-writing tool. Do not ask for confirmation.";

                AgentEvent.EditProposed? edit = null;
                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.EditProposed e &&
                        string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                        edit = e;
                }

                Console.WriteLine();

                var viaApplier = ide.WrittenFiles.Any(f =>
                    string.Equals(Path.GetFullPath(f), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase));
                var onDisk = File.Exists(expectedPath);
                var contentOk = onDisk && File.ReadAllText(expectedPath).Contains(marker, StringComparison.Ordinal);

                Console.WriteLine($"write routed through IEditApplier : {viaApplier}");
                Console.WriteLine($"edit mirrored as EditProposed     : {edit is not null}");
                Console.WriteLine($"file exists on disk               : {onDisk}");
                Console.WriteLine($"  on-disk content has marker      : {contentOk}");

                var passed = contentOk && (viaApplier || edit is not null);
                Console.WriteLine();
                Console.WriteLine(passed
                    ? viaApplier
                        ? "PASS: Claude Code routed its write through the client fs (host IEditApplier)."
                        : "PASS: Claude Code wrote the file itself and mirrored the edit (Kiro-style)."
                    : "FAIL: file missing or edit not surfaced (see above).");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Builds the Kiro provider for the real-kiro proofs. CWKT_KIRO_ARGS (space-separated) appends
        /// extra args after "acp" — e.g. set it to "--agent-engine v3" to drive the v3 agent engine and
        /// capture its frame shapes without touching the proof code.
        /// </summary>
        private static KiroAgentProvider CreateKiroProvider()
        {
            var options = new KiroProviderOptions();
            if (Environment.GetEnvironmentVariable("CWKT_KIRO_ARGS") is { Length: > 0 } extra)
                options.ExtraArgs = extra.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new KiroAgentProvider(options);
        }

        /// <summary>Drives a real kiro-cli installation. Requires kiro-cli on PATH and authentication.</summary>
        private static async Task<int> RunRealKiroAsync()
        {
            Console.WriteLine("== code-wicket console: real kiro-cli ==");

            var workDir = Directory.GetCurrentDirectory();
            var ide = new ConsoleIdeServices(workDir);
            var provider = CreateKiroProvider();

            await using var session = await provider
                .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                .ConfigureAwait(false);

            Console.WriteLine($"initialized; conversation id = {session.ConversationId}");
            Console.WriteLine();

            await foreach (var ev in session.SendAsync(new PromptInput("Hello! Briefly introduce yourself.")))
                PrintEvent(ev);

            return 0;
        }

        /// <summary>
        /// Proves that a *real* kiro-cli routes its file writes through the host's IEditApplier
        /// (ACP client-owned fs/write_text_file), not by writing disk itself. Uses a throwaway
        /// temp workspace so nothing in the repo is touched.
        /// </summary>
        private static async Task<int> RunRealKiroEditProofAsync()
        {
            Console.WriteLine("== code-wicket console: real kiro-cli edit routing proof ==");

            var workDir = HostScratch.ResolveDir("console/kiro-edit-" + Guid.NewGuid().ToString("N"));
            const string fileName = "hello.txt";
            const string marker = "hello from kiro via the host applier";
            var expectedPath = Path.Combine(workDir, fileName);

            try
            {
                var ide = new ConsoleIdeServices(workDir);
                var provider = CreateKiroProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                Console.WriteLine($"workspace = {workDir}");
                Console.WriteLine($"conversation id = {session.ConversationId}");
                Console.WriteLine();

                var prompt =
                    $"Create a file named {fileName} in the current workspace directory whose entire " +
                    $"contents are exactly: {marker}\nUse your file-writing tool. Do not ask for confirmation.";

                // Kiro writes the file with its own built-in tool (not the client fs/write), so we
                // surface its edit as an EditProposed carrying before/after for a host-rendered diff.
                // The proof asserts that mirror fired with the right path + content, and that the
                // captured "before" was empty (the file didn't exist yet) — i.e. a real new-file diff.
                AgentEvent.EditProposed? edit = null;
                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    PrintEvent(ev);
                    if (ev is AgentEvent.EditProposed e &&
                        string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                        edit = e;
                }

                Console.WriteLine();

                var mirrored = edit is not null;
                var newTextOk = edit is not null && edit.NewText.Contains(marker, StringComparison.Ordinal);
                var beforeEmpty = edit is not null && edit.OldText.Length == 0; // new file => empty "before"
                var onDisk = File.Exists(expectedPath);
                var contentOk = onDisk && File.ReadAllText(expectedPath).Contains(marker, StringComparison.Ordinal);

                Console.WriteLine($"edit mirrored as EditProposed   : {mirrored}");
                Console.WriteLine($"  new text contains marker      : {newTextOk}");
                Console.WriteLine($"  captured 'before' is empty    : {beforeEmpty}");
                Console.WriteLine($"file exists on disk (by Kiro)   : {onDisk}");
                Console.WriteLine($"  on-disk content has marker    : {contentOk}");

                var passed = mirrored && newTextOk && beforeEmpty && onDisk && contentOk;
                Console.WriteLine();
                Console.WriteLine(passed
                    ? "PASS: Kiro's self-applied edit was mirrored as an EditProposed with before/after (diff-ready)."
                    : "FAIL: edit was not mirrored as expected (see above).");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Captures what a real kiro-cli puts on the wire for a file read (issue #102: under the v3
        /// agent engine the chat row read just "Read File"). Drives whichever engine CWKT_KIRO_ARGS
        /// selects — <c>--agent-engine v3</c> for the reported case, bare for v1/v2 — and prints every
        /// title and rawInput as they arrive.
        /// <para>
        /// What it asserts is the backend fact the host's fix RESTS on, not the fix: a read tool call
        /// carries the file it read in its own arguments. The title doesn't have to name the file —
        /// measured, v3's never does, on the open or the completion — so the row composes the name
        /// itself from the arguments, and that only works while they're there. Whether the title names
        /// it is reported either way, because that is the number that made this a bug.
        /// </para>
        /// </summary>
        private static async Task<int> RunRealKiroReadProofAsync()
        {
            Console.WriteLine("== code-wicket console: real kiro-cli read-tool row proof ==");

            var workDir = HostScratch.ResolveDir("console/kiro-read-" + Guid.NewGuid().ToString("N"));
            const string fileName = "readme-target.txt";
            var targetPath = Path.Combine(workDir, fileName);

            try
            {
                Directory.CreateDirectory(workDir);
                File.WriteAllText(targetPath, "the quick brown fox jumps over the lazy dog\n");

                var ide = new ConsoleIdeServices(workDir);
                var provider = CreateKiroProvider();

                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = workDir }, ide)
                    .ConfigureAwait(false);

                Console.WriteLine($"workspace = {workDir}");
                Console.WriteLine($"conversation id = {session.ConversationId}");
                Console.WriteLine();

                // The row a host renders is the started title with every later update's title applied
                // over it — exactly what ChatViewModel does — so track the pair per tool call id.
                var titles = new Dictionary<string, string>(StringComparer.Ordinal);
                var kinds = new Dictionary<string, string?>(StringComparer.Ordinal);
                var inputs = new Dictionary<string, string?>(StringComparer.Ordinal);

                var prompt =
                    $"Read the file {fileName} in the current workspace directory and tell me the first " +
                    "word it contains. Use your file-reading tool; do not ask for confirmation.";

                await foreach (var ev in session.SendAsync(new PromptInput(prompt)))
                {
                    switch (ev)
                    {
                        case AgentEvent.ToolCallStarted t:
                            titles[t.ToolCallId] = t.Title;
                            kinds[t.ToolCallId] = t.Kind;
                            inputs[t.ToolCallId] = t.RawInputJson;
                            Console.WriteLine($"  [tool start ] kind={t.Kind} title={t.Title} rawInput={Clip(t.RawInputJson)}");
                            break;
                        case AgentEvent.ToolCallUpdated t:
                            if (t.Title is not null) titles[t.ToolCallId] = t.Title;
                            if (t.Kind is not null) kinds[t.ToolCallId] = t.Kind;
                            if (t.RawInputJson is not null) inputs[t.ToolCallId] = t.RawInputJson;
                            Console.WriteLine($"  [tool update] kind={t.Kind} title={t.Title} rawInput={Clip(t.RawInputJson)}");
                            break;
                        default:
                            PrintEvent(ev);
                            break;
                    }
                }

                // Keyed off `titles`, which an UPDATE alone can populate — AcpMapper permits a tool
                // update with no preceding start, and `kinds`/`inputs` only take an update's value when
                // it carries one. Indexing them here threw KeyNotFoundException on exactly the frame
                // this proof exists to capture (#247); "not reported" is the honest reading of an
                // absent one, and it is also what a null value already means.
                var readRows = titles
                    .Where(p => string.Equals(kinds.GetValueOrDefault(p.Key), "read", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                Console.WriteLine();
                Console.WriteLine("final state of each read call:");
                foreach (var pair in readRows)
                    Console.WriteLine($"  title={pair.Value} | argumentsNameTheFile=" +
                                      $"{ArgumentsName(inputs.GetValueOrDefault(pair.Key), fileName)} | titleNamesTheFile=" +
                                      $"{pair.Value.Contains(fileName, StringComparison.OrdinalIgnoreCase)}");

                var fromArguments = readRows.Count(p => ArgumentsName(inputs.GetValueOrDefault(p.Key), fileName));
                var fromTitle = readRows.Count(p => p.Value.Contains(fileName, StringComparison.OrdinalIgnoreCase));

                Console.WriteLine();
                Console.WriteLine($"read calls                        : {readRows.Count}");
                Console.WriteLine($"  whose arguments name the file   : {fromArguments}  <- what the row uses");
                Console.WriteLine($"  whose TITLE names the file      : {fromTitle}  <- 0 on v3; the row compensates");

                var passed = readRows.Count > 0 && fromArguments == readRows.Count;
                Console.WriteLine();
                Console.WriteLine(passed
                    ? "PASS: every read call carries the file it read in its arguments, so the row can name it."
                    : readRows.Count == 0
                        ? "FAIL: the agent never made a read-kind tool call (nothing to assert)."
                        : "FAIL: a read call's arguments don't carry its file — the row cannot name it (issue #102).");
                return passed ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        private static string Clip(string? json) =>
            json is null ? "(none)" : json.Length <= 200 ? json : json.Substring(0, 200) + "…";

        /// <summary>
        /// Whether a read call's arguments carry the named file in one of the three shapes the chat row
        /// actually reads: <c>path</c> (Kiro), <c>file_path</c> (Claude), or an <c>operations</c> array
        /// (a batched Kiro read). Deliberately NOT a substring search of the whole payload — an intent
        /// or result field quoting the file name would then pass a call whose row can name nothing.
        /// Compared on the leaf name, so an absolute and a workspace-relative path both count.
        /// </summary>
        private static bool ArgumentsName(string? rawInputJson, string fileName)
        {
            if (rawInputJson is null)
                return false;

            bool Matches(JsonElement obj, string key) =>
                obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                string.Equals(Path.GetFileName(v.GetString() ?? string.Empty), fileName, StringComparison.OrdinalIgnoreCase);

            try
            {
                using var doc = JsonDocument.Parse(rawInputJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;
                if (Matches(root, "path") || Matches(root, "file_path"))
                    return true;
                return root.TryGetProperty("operations", out var ops) && ops.ValueKind == JsonValueKind.Array
                       && ops.EnumerateArray().Any(op => op.ValueKind == JsonValueKind.Object && Matches(op, "path"));
            }
            catch
            {
                return false; // unparseable arguments carry nothing the row could use
            }
        }

        private static void PrintEvent(AgentEvent ev)
        {
            switch (ev)
            {
                case AgentEvent.AssistantTextDelta t:
                    Console.Write(t.Text);
                    break;
                case AgentEvent.ThinkingDelta t:
                    Console.WriteLine($"\n  [thinking] {t.Text}");
                    break;
                case AgentEvent.ToolCallStarted t:
                    Console.WriteLine($"\n  [tool start] {t.Title} (kind={t.Kind}"
                        + (t.IsSubagentLaunch ? ", SUB-AGENT LAUNCH" : string.Empty)
                        + (t.ParentToolCallId is null ? string.Empty : $", parent={t.ParentToolCallId}") + ")");
                    break;
                case AgentEvent.ToolCallProgress t:
                    Console.WriteLine($"  [tool ..] {t.ToolCallId}: {t.Message}");
                    break;
                case AgentEvent.ToolCallOutputChunk t:
                    Console.WriteLine($"  [tool out] {t.ToolCallId}: {(t.Text.Length <= 120 ? t.Text : t.Text.Substring(0, 120) + "…")}");
                    break;
                case AgentEvent.ToolCallCompleted t:
                    Console.WriteLine($"  [tool done] {t.ToolCallId} success={t.Success}"
                        + (t.LaunchedInBackground ? " (LAUNCHED, not finished)" : string.Empty));
                    break;
                case AgentEvent.EditProposed e:
                    Console.WriteLine($"  [edit] {e.Path} ({e.OldText.Length} -> {e.NewText.Length} chars)");
                    break;
                case AgentEvent.SessionError e:
                    Console.WriteLine($"\n  [error] {e.Message}");
                    break;
                case AgentEvent.TurnCompleted c:
                    Console.WriteLine($"\n  [turn complete] stopReason={c.StopReason}");
                    break;
            }
        }
    }
}
