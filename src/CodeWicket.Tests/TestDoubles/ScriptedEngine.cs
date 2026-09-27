using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;

namespace CodeWicket.Tests
{
    /// <summary>
    /// One scriptable engine for the view-model tests of sessions, resume and message delivery, in
    /// place of the per-class <c>StubEngine</c>s those classes each grew. Everything it does is set
    /// by the test before (or while) the view-model drives it; nothing is inferred.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one double.</b> The session-lifetime refactor moves the code these tests drive
    /// from outside, so the tests are its specification - and eight near-copies of a fake engine are
    /// eight places for one of them to quietly script a backend the others do not. One double also
    /// carries the shapes the stubs never could: opening frames raised INSIDE the start (the
    /// <c>fetch_cloud_config</c> pair kiro-cli sends before <c>session/new</c> returns), late ones
    /// after it, and gates on the start, the summarize and the turn.</para>
    /// <para><b>Loud where the stubs were loud.</b> A class that never opens a session or never
    /// prompts sets <see cref="ForbidStarts"/> / <see cref="ForbidPrompts"/>, which throw as its stub
    /// did, so a test that starts doing either by accident still says so.</para>
    /// </remarks>
    internal sealed class ScriptedEngine : IEngineConnection, IAgentRootQuery, IEngineExitReport
    {
        /// <summary>How a prompt's turn ends.</summary>
        public enum TurnEnding
        {
            /// <summary>The prompt answers <c>end_turn</c> at once: nothing in the test is about a turn.</summary>
            Immediately,

            /// <summary>The turn stays open until <see cref="CompleteTurn"/>, as a live backend's does.</summary>
            WhenCompleted,
        }

        private TaskCompletionSource<PromptResponse>? _turn;

        public event Action<AgentEventDto>? AgentEvent;

        public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

        /// <summary>Raises a live frame, as the engine does, on the caller's thread.</summary>
        public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

        public event Action<EngineExit>? Exited;

        /// <summary>
        /// The engine process has exited (issue #299). Reported from a POOL thread, as
        /// <c>EngineClient</c> does, so the view-model's own marshalling is exercised rather than
        /// bypassed - a test that raised it inline would never cross the hop the real report crosses.
        /// </summary>
        /// <remarks>
        /// <para><b>And every call still outstanding FAULTS</b>, which a script that only raised the event
        /// could not express - so the one route where a send is given its message back twice was
        /// unreachable from the suite whatever the test asked. A dead pipe faults every RPC in flight; here
        /// that is the open turn, the summarize gate and the start gates.</para>
        /// <para><b>The report goes first, and the order is the product's.</b> In <c>EngineClient</c> the
        /// exit event is raised from a continuation registered at construction, while a call awaiting the
        /// same exit registers its own per call - so the report's dispatcher post is enqueued ahead of the
        /// faulting call's. Raising before faulting reproduces that rather than the luckier order.</para>
        /// <para><b><see cref="StartNeverCompletes"/> is deliberately left alone</b>: it models a call that
        /// never returns at all, which is a different thing from one the exit kills.</para>
        /// </remarks>
        public void RaiseExit(EngineExit exit)
        {
            Task.Run(() => Exited?.Invoke(exit)).GetAwaiter().GetResult();

            var dead = new EngineExitedException(exit);
            var turn = _turn;
            _turn = null;
            Kill(turn, dead);
            Kill(GateSummarize, dead);
            // IN PLACE, never dequeued. Draining the queue leaves a later start falling through to the
            // ungated answer, so the script would model an engine that exited and then happily opened a
            // session - the harness telling a story no backend can. GateStart already behaves this way
            // because it is not consumed; the queue has to be made to match it rather than the reverse.
            foreach (var gate in StartGates)
                Kill(gate, dead);
            Kill(GateStart, dead);
        }

        /// <summary>
        /// Faults one outstanding call, and OBSERVES the fault: a gate nothing is awaiting would otherwise
        /// leave an unobserved faulted task behind for whichever later test happens to trigger a collection.
        /// </summary>
        private static void Kill<T>(TaskCompletionSource<T>? source, Exception dead)
        {
            if (source is null || !source.TrySetException(dead))
                return;
            _ = source.Task.Exception;
        }

        // ---- catalogue ---------------------------------------------------------------------------

        /// <summary>What <see cref="ListProvidersAsync"/> answers. Empty unless the test adds one.</summary>
        public List<ProviderInfoDto> Providers { get; } = new();

        /// <summary>Adds a backend with no models and the given capabilities (for example
        /// <c>"ResumeSession"</c>, or <c>"Mcp"</c> so the first prompt waits for our bridge).</summary>
        public ScriptedEngine WithProvider(string id, string name, params string[] capabilities)
        {
            Providers.Add(new ProviderInfoDto(id, name, new List<ModelInfoDto>(), capabilities.ToList()));
            return this;
        }

        public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ListProvidersResponse(Providers.ToList()));

        // ---- sessions ----------------------------------------------------------------------------

        /// <summary>Every start request, in order.</summary>
        public List<StartSessionRequest> Starts { get; } = new();

        public int StartCount => Starts.Count;

        /// <summary>The conversation id the most recent start asked to resume, if any.</summary>
        public string? ResumedConversationId => Starts.Count == 0 ? null : Starts[Starts.Count - 1].ResumeConversationId;

        /// <summary>When set, a start waits on it before answering: the window a workspace move, a
        /// Stop or a history open lands in.</summary>
        public TaskCompletionSource<bool>? GateStart { get; set; }

        /// <summary>Every start stays pending for the life of the test.</summary>
        public bool StartNeverCompletes { get; set; }

        /// <summary>A start throws, as a class whose tests never open a session wants.</summary>
        public bool ForbidStarts { get; set; }

        /// <summary>Every start fails with this, counted as a start: an engine that has exited, for example
        /// (<see cref="EngineExitedException"/>).</summary>
        public Exception? StartFails { get; set; }

        /// <summary>Answers a resume with a fall-through to a fresh session and the backend's reason,
        /// as kiro-cli v2 did for a v3 id (issue #268).</summary>
        public bool RefuseResume { get; set; }

        public string RefusalReason { get; set; } = "Session not found: sess_old";

        /// <summary>Accepts a resume and replays nothing of it, as Kiro v3 does for an id it no longer
        /// holds (issue #185).</summary>
        public bool EmptyReplay { get; set; }

        /// <summary>What the handshake says about steering.</summary>
        public bool SupportsSteering { get; set; }

        /// <summary>
        /// Whether the handshake says this backend takes images. OFF by default, which is the harder
        /// case and the one most checks want: without it a picture goes as a TEXT fallback naming the
        /// file, so a check counting what rode the prompt sees nothing and reads a lost attachment and a
        /// backend that cannot carry one as the same thing.
        /// </summary>
        public bool SupportsImages { get; set; }

        /// <summary>The directory every start says the agent runs in, or null for "not reported".</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Raised inside every start, before it returns.</summary>
        public List<AgentEventDto> OpeningFrames { get; } = new();

        /// <summary>Raised after a start has returned, once <see cref="LateOpeningGate"/> completes.</summary>
        public List<AgentEventDto> LateOpeningFrames { get; } = new();

        public TaskCompletionSource<bool>? LateOpeningGate { get; set; }

        public Task<StartSessionResponse> StartSessionAsync(
            StartSessionRequest request, CancellationToken cancellationToken = default)
        {
            if (ForbidStarts)
                throw new InvalidOperationException("This script opens no session.");

            if (StartFails is { } fails)
            {
                Starts.Add(request);
                return Task.FromException<StartSessionResponse>(fails);
            }

            Starts.Add(request);
            var number = Starts.Count;

            // The engine hosts one session and disposes the old one inside the next start, so two starts in the
            // engine at once race that teardown. Counted rather than thrown: a test asserts it is zero.
            if (_pendingStarts.Exists(pending => !pending.IsCompleted))
                OverlappingStarts++;

            Task<StartSessionResponse> start;
            if (StartNeverCompletes)
                start = new TaskCompletionSource<StartSessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            else if (StartGates.Count > 0)
                start = AnswerAfter(StartGates.Dequeue(), request, number);
            else if (GateStart is { } gate)
                start = AnswerAfter(gate, request, number);
            else
                start = Task.FromResult(Answer(request, number));

            _pendingStarts.Add(start);
            return start;
        }

        private readonly List<Task<StartSessionResponse>> _pendingStarts = new();

        /// <summary>
        /// One gate per start, in order, taken before <see cref="GateStart"/>: each start waits on its own, and a
        /// gate answered <c>false</c> makes that start fail, as a backend that cannot open a session does.
        /// </summary>
        public Queue<TaskCompletionSource<bool>> StartGates { get; } = new();

        /// <summary>How many starts began while an earlier start was still pending in the engine.</summary>
        public int OverlappingStarts { get; private set; }

        private async Task<StartSessionResponse> AnswerAfter(
            TaskCompletionSource<bool> gate, StartSessionRequest request, int number)
        {
            if (!await gate.Task.ConfigureAwait(true))
                throw new InvalidOperationException("The scripted start failed.");
            return Answer(request, number);
        }

        private StartSessionResponse Answer(StartSessionRequest request, int number)
        {
            foreach (var frame in OpeningFrames)
                Raise(frame);

            var resuming = request.ResumeConversationId is { Length: > 0 };
            var refused = RefuseResume && resuming;

            if (LateOpeningGate is { } late && LateOpeningFrames.Count > 0)
            {
                var frames = LateOpeningFrames.ToList();
                _ = late.Task.ContinueWith(_ =>
                {
                    foreach (var frame in frames)
                        Raise(frame);
                }, TaskScheduler.Default);
            }

            return new StartSessionResponse(
                refused ? "fresh-" + number : request.ResumeConversationId ?? "fresh-" + number,
                WorkingDirectory: WorkingDirectory,
                ResumeFailureReason: refused ? RefusalReason : null,
                SupportsSteering: SupportsSteering,
                SupportsImages: SupportsImages,
                ImportedHistoryCount: request.ImportHistory ? ImportedHistory.Count : null,
                ReplayedHistoryCount: resuming && !refused && EmptyReplay ? 0 : null);
        }

        /// <summary>What an import replays, paged back through <see cref="TakeImportedHistoryAsync"/>. Empty
        /// by default, which the view-model reads as an import that came back with nothing.</summary>
        public List<ImportedEntryDto> ImportedHistory { get; } = new();

        /// <summary>Says our IDE-tool bridge served <paramref name="toolsServed"/> tools: the signal the
        /// first prompt waits for on a backend that takes the bridge.</summary>
        public void ServeTools(int toolsServed = 17) => Raise(new AgentEventDto
        {
            Type = "mcpBridge",
            McpBridge = new McpBridgeDto(Connected: true, ToolsServed: toolsServed, ToolNames: null, ToolCalls: 0),
        });

        // ---- turns -------------------------------------------------------------------------------

        /// <summary>The text of every prompt, in order.</summary>
        public List<string> Prompts { get; } = new();

        /// <summary>
        /// What rode each prompt, in step with <see cref="Prompts"/>. Recorded because a check about a
        /// picture REACHING the agent has nothing else to read: the text says the message went, and says
        /// nothing at all about what the tray was holding with it.
        /// </summary>
        public List<IReadOnlyList<PromptAttachmentDto>> PromptAttachments { get; } = new();

        public TurnEnding Turns { get; set; } = TurnEnding.Immediately;

        /// <summary>A prompt throws, as a class whose tests never prompt wants.</summary>
        public bool ForbidPrompts { get; set; }

        public int CancelCount { get; private set; }

        /// <summary>Cancel ends the open turn, as a backend acknowledging <c>session/cancel</c> does.</summary>
        public bool CancelCompletesTurn { get; set; }

        /// <summary>The text of every steer, in order.</summary>
        public List<string> Steers { get; } = new();

        /// <summary>A steer throws instead of landing: the message never reaches the agent.</summary>
        public Exception? SteerFails { get; set; }

        /// <summary>
        /// When set, a steer waits on it before landing or failing - the window in which the message has
        /// been drawn and RECORDED (a steer records before it is sent) while the request is still out.
        /// </summary>
        public TaskCompletionSource<bool>? GateSteer { get; set; }

        public Task<PromptResponse> PromptAsync(
            string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            if (ForbidPrompts)
                throw new InvalidOperationException("This script is never prompted.");

            Prompts.Add(text);
            PromptAttachments.Add(attachments ?? Array.Empty<PromptAttachmentDto>());
            if (Turns == TurnEnding.Immediately)
                return Task.FromResult(new PromptResponse("end_turn"));

            _turn = new TaskCompletionSource<PromptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _turn.Task;
        }

        /// <summary>Ends the turn currently in flight, as a backend answering <c>session/prompt</c> does.</summary>
        public void CompleteTurn()
        {
            var turn = _turn;
            _turn = null;
            turn?.TrySetResult(new PromptResponse("end_turn"));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            CancelCount++;
            if (CancelCompletesTurn)
                CompleteTurn();
            return Task.CompletedTask;
        }

        public Task<SteerResponse> SteerAsync(
            string text, IReadOnlyList<PromptAttachmentDto>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            if (!SupportsSteering)
                throw new InvalidOperationException("This script's handshake offered no steering.");

            if (GateSteer is not null)
                return SteerAfterGateAsync(text);

            if (SteerFails is { } failure)
                return Task.FromException<SteerResponse>(failure);

            Steers.Add(text);
            // A steer pre-empts the turn it lands in, which closes with an ordinary end_turn ~10ms
            // later (measured on the wire). Modelled because the turn-end release trigger has to cope
            // with a turn that ends without the work being finished.
            CompleteTurn();
            return Task.FromResult(new SteerResponse(nameof(Core.SteerOutcome.Injected)));
        }

        /// <summary>The gated steer: read <see cref="SteerFails"/> AFTER the wait, so a test can decide the
        /// outcome while the request is still out.</summary>
        private async Task<SteerResponse> SteerAfterGateAsync(string text)
        {
            await GateSteer!.Task.ConfigureAwait(true);
            if (SteerFails is { } failure)
                throw failure;

            Steers.Add(text);
            CompleteTurn();
            return new SteerResponse(nameof(Core.SteerOutcome.Injected));
        }

        public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        // ---- where the agent would run -----------------------------------------------------------

        /// <summary>
        /// Where a start would run the agent, for the moved-root question (issue #185). Empty by
        /// default, and empty is what <see cref="Core.ResumeRootGuard.Refuse"/> reads as "unknown",
        /// which never refuses - so implementing the query changes nothing for a class that does not
        /// set this, and a class that does gets the banner.
        /// </summary>
        public string AgentRoot { get; set; } = string.Empty;

        public Task<ResolveAgentRootResponse> ResolveAgentRootAsync(
            ResolveAgentRootRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ResolveAgentRootResponse(AgentRoot, "the script says so"));

        // ---- summarize ---------------------------------------------------------------------------

        /// <summary>When set, a summarize waits on it before answering.</summary>
        public TaskCompletionSource<bool>? GateSummarize { get; set; }

        public string SummaryText { get; set; } = "A recap of the earlier work.";

        /// <summary>The summarizer answers with nothing: issue #84's silent failure.</summary>
        public bool SummarizeReturnsNothing { get; set; }

        /// <summary>The summarizer fails with this.</summary>
        public Exception? SummarizeThrows { get; set; }

        public int SummarizeCount { get; private set; }

        /// <summary>
        /// A summarize is allowed at all. Off by default, and a summarize then THROWS, as the stubs of
        /// every class that never summarized did: a test whose send reaches the summarizer by accident
        /// must say so. Left on by default, dropping the summary-strategy guard from the send went green
        /// in OffScreenReplyRoutingTests, which is what earned the throw.
        /// </summary>
        public bool AllowSummarize { get; set; }

        public Task<SummarizeResponse> SummarizeAsync(
            SummarizeRequest request, CancellationToken cancellationToken = default)
        {
            if (!AllowSummarize)
                throw new InvalidOperationException("This script never summarizes (set AllowSummarize).");
            SummarizeRequests.Add(request);
            return SummarizeCoreAsync();
        }

        /// <summary>Every summarize request, in order.</summary>
        public List<SummarizeRequest> SummarizeRequests { get; } = new();

        private async Task<SummarizeResponse> SummarizeCoreAsync()
        {
            SummarizeCount++;
            if (GateSummarize is { } gate)
                await gate.Task.ConfigureAwait(true);
            if (SummarizeThrows is { } thrown)
                throw thrown;
            return new SummarizeResponse(SummarizeReturnsNothing ? string.Empty : SummaryText);
        }

        /// <summary>
        /// What this script is holding back right now, in words - for a harness that gives up waiting
        /// (<see cref="SingleThreadPump"/>) to say what the body was left waiting on instead of hanging.
        /// </summary>
        public string DescribePending()
        {
            var pending = new List<string>();
            if (StartNeverCompletes && Starts.Count > 0)
                pending.Add("a session start that never completes (StartNeverCompletes)");
            if (GateStart is { Task.IsCompleted: false } && Starts.Count > 0)
                pending.Add("a session start held by GateStart");
            if (_turn is { Task.IsCompleted: false })
                pending.Add("an open turn (TurnEnding.WhenCompleted; call CompleteTurn)");
            if (GateSummarize is { Task.IsCompleted: false } && SummarizeCount > 0)
                pending.Add("a summarize held by GateSummarize");
            if (LateOpeningGate is { Task.IsCompleted: false })
                pending.Add("late opening frames held by LateOpeningGate");
            return pending.Count == 0 ? "nothing this script holds back" : string.Join("; ", pending);
        }

        // ---- what these tests never reach --------------------------------------------------------

        // A fake with no handshake reports no session, which the panel renders as
        // "no agent session open yet" rather than as absent facts (issue #160).
        public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SessionInfoResponse?>(null);

        public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
            ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ListBackendSessionsResponse(
                false, "not supported", Array.Empty<BackendSessionDto>()));

        public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
            TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TakeImportedHistoryResponse(
                ImportedHistory.Skip(request.Offset).Take(request.Limit).ToList(), ImportedHistory.Count));
    }
}
