using System;
using System.Globalization;
using System.Threading.Tasks;
using CodeWicket.Ipc;

namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// Which backend session the engine holds, and whose it is: the warm slot, the one route to
    /// <c>engine/startSession</c>, the conversation a live session belongs to (issue #256), the turn a
    /// workspace move retired (issue #217), and where each live frame goes.
    /// </summary>
    /// <remarks>
    /// <para><b>Moved out of ChatViewModel</b>, and split into an Open session (adopted, never prompted) and
    /// a Prompted one (<see cref="HasSession"/>, <see cref="IsPrompted"/>). The turn lease
    /// (<see cref="Commit"/>) is taken immediately before the prompt, and its gate on
    /// <see cref="WarmStart"/> is the only one: the view-model has no <c>IsBusy</c> gate of its own.</para>
    /// <para><b>One synchronization context, no dispatcher.</b> Every member is called on the
    /// view-model's, and every await resumes there, as it did in the view-model. What needs the
    /// dispatcher - the off-screen owner's flush timer - is asked of the host.</para>
    /// </remarks>
    public sealed class SessionLifetime
    {
        private readonly IEngineConnection _engine;
        private readonly ISessionStore? _store;
        private readonly ILifetimeHost _host;
        private readonly IInvariantSink _invariants;
        private readonly Action<string>? _log;

        // Whether the pane on screen has adopted the engine's session (Open), and whether a prompt of its
        // conversation has gone out on it, or an import adopted its history (Prompted). They were one flag,
        // set at adoption, so a session that was opened and never prompted - a refused reload the user backed
        // out of - counted as started: its late opening frames were recorded as work, and after a failed
        // import the next send went in without being asked how to resume.
        private bool _sessionOpen;
        private bool _prompted;

        // A session a send opened (or took from the warm slot) and has not committed yet, with the token the
        // send was begun under. Once that token is stale, whatever the session still sends belongs to a
        // conversation no longer on screen and nobody's prompt, so it is dropped until the next start.
        private SessionToken? _uncommittedStart;

        // The latest engine start, so a warm start can chain behind one a send still has in flight: the
        // engine hosts one session at a time, and two starts would race its teardown.
        private Task? _startInFlight;

        // Which conversation the transcript is currently showing, and which one the turn now on the wire
        // was started for (issue #217). They are equal in every ordinary state; they come apart for
        // exactly as long as a turn OUTLIVES the conversation that opened it — which is what a workspace
        // move does, the user having left the solution the agent is still working in.
        //
        // The two are needed because an AgentEventDto carries nothing that says which session it came
        // from: the engine hosts one at a time and streams its events on one channel, so the only
        // correlation available is the host's own knowledge of which turn it started. Stamping the DTO
        // in the engine would be exact, but it is a Core -> DTO -> mapping change for a fact the host
        // already holds.
        //
        // Bumped by ConversationReplaced, which the view-model's ClearTranscript calls, rather than at
        // each site that replaces a conversation, so the next such path cannot forget it. Every other
        // one of those is gated on !IsBusy, so this only ever separates the two on the paths that
        // genuinely can run under a live turn.
        private int _transcriptEpoch;
        private int _liveTurnEpoch;

        // The send whose turn has not returned yet. Null between turns.
        private PromptedTurn? _outstandingTurn;

        // The conversation on screen, as the view-model's _persisted setter reports it.
        private PersistedSession? _displayed;

        // The conversation the engine's live session was started FOR, held from adoption until the
        // next session start replaces it (issue #256). Distinct from _displayed, which is the
        // conversation ON SCREEN: opening a saved conversation from the history picker is lazy and
        // display-first — it replays locally and issues no session/load — so the live session
        // outlives the swap, and anything it still emits (a background task's return, a steered
        // reply) belongs to this conversation and not to the one being read.
        //
        // The epoch above cannot cover this: it separates the two only while a TURN is on the wire,
        // and a background launch ends its turn at launch. Nor can the DTO — an AgentEventDto carries
        // no session identity — but the host does not need it to: the engine hosts one session at a
        // time, so every event on the channel belongs to whichever conversation started that session,
        // which is this field. The request and response are kept beside it so reopening the owner
        // can re-adopt the live session instead of resuming a conversation the backend never dropped.
        private PersistedSession? _liveOwner;
        private StartSessionRequest? _liveOwnerRequest;
        private StartSessionResponse? _liveOwnerStarted;
        // Whether the owner's session was prompted: an Open one (backed out of) has nothing of its own to route.
        private bool _liveOwnerPrompted;
        private bool _liveOwnerDirty;
        private int _routedOffScreen;
        // The owner was DELETED while its session was still live. Its events then belong nowhere:
        // recording them would recreate the file, and without this they would fall through to the
        // ordinary path and land in whatever conversation is on screen — the reported bug, one
        // gesture later. Cleared by the next session start, which is what actually ends the session.
        private bool _liveSessionDiscarded;

        // The backend of the session the ENGINE holds — the provider of the last StartSession request
        // made, set before the call because the engine tears the previous session down at the start of
        // it. Not the picker, which can move while a warm session for the old choice is still coming
        // up; not the view-model's _activeProviderId, which is null until a session is adopted. Null
        // before any start.
        private string? _engineSessionProviderId;
        private int _droppedFromAbandonedWarm;

        // The deleted owner's title, kept with the flag above (pre-release security review, September 2026): a permission
        // request from the discarded session still reaches the banner, and without this the banner
        // could name nobody and the user would be asked to approve work for a conversation they had
        // deleted, presented as the one they were reading. Cleared with the flag.
        private string? _discardedOwnerTitle;

        // A pre-opened backend session for a conversation that would start fresh anyway (issue #19):
        // session/new is free (no prompt, no metered usage) and kicks the backend's own MCP servers
        // into connecting, so the user's typing time becomes their warm-up window instead of the
        // first prompt racing them. The task never faults (failures resolve null; the real send
        // retries cold and surfaces the error) but it does REPORT — see WarmCoreAsync.
        // _warmRequest is what the warm session was opened with — the send reuses it only on an
        // exact match.
        private Task<StartSessionResponse?>? _warmTask;
        private StartSessionRequest? _warmRequest;

        public SessionLifetime(
            IEngineConnection engine, ISessionStore? store, ILifetimeHost host, IInvariantSink invariants,
            Action<string>? log = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _store = store;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _invariants = invariants ?? throw new ArgumentNullException(nameof(invariants));
            _log = log;
        }

        // ---- started ----------------------------------------------------------------------------

        /// <summary>
        /// The pane on screen has adopted the engine's session, prompted or not (Open or Prompted). Set at
        /// adoption; cleared by <see cref="Detach"/> and by the next engine start.
        /// </summary>
        public bool HasSession => _sessionOpen && !_superseded;

        /// <summary>
        /// A prompt of the conversation on screen has gone out on the pane's session (<see cref="Commit"/>),
        /// or an import adopted its history. Nothing counts a session as started before this, and nothing is
        /// recorded from it.
        /// </summary>
        public bool IsPrompted => _prompted;

        // Superseded: a picker change on a prompted pane. The session is still the engine's until the next
        // start disposes it, and still this conversation's, so it routes as Prompted; it no longer takes a prompt.
        private bool _superseded;

        /// <summary>A picker change moved the pane off its prompted session, which the engine still holds.</summary>
        public bool IsSuperseded => _superseded;

        // A reload this session was opened for, which the backend refused and the user then backed out of
        // (issue #268). A fact about the SESSION rather than about any send, so it dies with the session:
        // the commit that answers it clears it, and so does releasing the owner - but NOT detaching,
        // because a history round trip re-adopts the very session it belongs to (issue #256). It used to
        // live beside the delivery's sends, keyed by conversation id, which meant the question "would
        // prompting this send the message into an empty session?" was answered by something outside the
        // session it was about.
        private string? _refusedReload;

        // A recap that could not be produced (user decision, 2026-09-16). Same scope, and deliberately
        // INDEPENDENT of the refusal above rather than a field of it: the refused-reload route is not the
        // only way a recap fails, and while this rode on that route a recap failing on the #84 route marked
        // nothing at all - so backing out and sending again re-offered the recap that had just failed, as
        // the recommended answer. One fact, read by every banner that can offer a recap.
        //
        // Transient is deliberately not told from permanent (user, explicitly): a rate limit and a version
        // mismatch arrive identically, and guessing wrong removes a working feature from the conversation
        // for good. So the way back is a new session, and Detach clears this where it does NOT clear the
        // refusal - reopening the conversation is one of the ways back the user named.
        private bool _recapFailed;

        /// <summary>
        /// Why the reload of the conversation ON SCREEN was refused, if the pane still holds the session that
        /// refusal fell through to, or null. A send that would reload again meets this instead of retrying:
        /// the backend has already said it does not have the conversation, so asking twice costs a session
        /// start to be told the same thing.
        /// </summary>
        /// <remarks>
        /// Gated on the owner being the conversation DISPLAYED, not merely on the session existing: opening
        /// another conversation must not inherit this one's question, while reopening its own - which
        /// re-adopts the live session through a freshly loaded object (issue #256) - must keep it.
        /// </remarks>
        public string? RefusedReload =>
            _refusedReload is { } reason && HasSession && !IsLiveSessionOffScreen ? reason : null;

        /// <summary>
        /// Whether a recap has failed on this session, so no banner offers one again while it lives —
        /// the Choice banner, both resume-failure banners, and the moved-root one alike.
        /// </summary>
        /// <remarks>
        /// The way back is a new session: reopening the conversation, New, a backend change, a restart of
        /// the chat window. A transient failure therefore costs a reopen rather than the feature, which is
        /// the trade the alternative loses — telling transient from permanent is a guess, and guessing
        /// wrong takes a working recap away from a conversation permanently.
        /// </remarks>
        public bool RecapFailedThisSession => _recapFailed;

        /// <summary>Records that a recap could not be produced, whichever route asked for it.</summary>
        public void NoteRecapFailed() => _recapFailed = true;

        /// <summary>Records that this session is the fall-through from a reload the backend refused.</summary>
        public void NoteReloadRefused(string reason) => _refusedReload = reason;

        /// <summary>The question has been answered, so the session stops carrying it.</summary>
        public void ForgetRefusedReload() => _refusedReload = null;

        /// <summary>
        /// A way back has been taken — the pane is changing conversation — so a recap is on offer again.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT folded into <see cref="Detach"/>, though a conversation swap calls both.
        /// Detach is also how a session START lets go of the previous session, so clearing there
        /// discharged the failure during the failing send's own start, and the refused-reload banner then
        /// re-offered the recap that had just failed. Two callers, opposite meanings, one method: the
        /// clear has to sit with the caller that means it.
        /// </remarks>
        public void ForgetRecapFailure() => _recapFailed = false;

        /// <summary>
        /// The pane's session takes the next prompt: prompted, and not superseded. What a send and a steer read; routing
        /// reads <see cref="IsPrompted"/>, which a superseded session keeps, so its late work is recorded as work.
        /// </summary>
        public bool CanTakeNextPrompt => _prompted && !_superseded;

        /// <summary>
        /// A picker change on a prompted pane: no start, no warm start, no teardown. A late background return
        /// still reaches the conversation on screen, and the next send starts a new session, which disposes this one -
        /// unless the picker lands exactly back on it first (<see cref="IsSupersededSession"/>).
        /// </summary>
        public void Supersede()
        {
            if (_prompted)
                _superseded = true;
            // No recap clear here: the CALLER does it, on both branches, because the way back is the user's
            // gesture and this method sees only the pane's state.
            //
            // An earlier comment here claimed a prompted pane could not be carrying a failed recap, since
            // _prompted is set by Commit and Commit clears it. That was wrong, and the state it ruled out is
            // reachable: Supersede KEEPS _prompted, Adopt(prompted: true) sets it, and on the #84 route the
            // recap fails before any session start - so backing out leaves a prompted pane carrying one with
            // no Commit in between. Where the code and a confident comment disagree, the comment's claim is
            // what to delete.
        }

        /// <summary>
        /// Whether <paramref name="request"/> describes exactly the session this pane superseded (user decision,
        /// 2026-09-15), so taking it back continues the conversation where it runs. The whole request, never only the
        /// provider: a provider flip lands the model picker on the backend's default, which may not be the model the
        /// session runs.
        /// </summary>
        public bool IsSupersededSession(StartSessionRequest request) =>
            _superseded && _liveOwnerRequest is { } owner && RunningAs(owner) == RunningAs(request);

        // What a session RUNS, without how it was opened: a resume id, the directory that resume was checked against and
        // an import say how the backend got the conversation, not what it runs now. The pin stays - it is where the agent
        // runs - so a session opened where its history lives is never taken back by a request that is not pinned there.
        private static StartSessionRequest RunningAs(StartSessionRequest request) =>
            request with { ResumeConversationId = null, ResumeWorkingDirectory = null, ImportHistory = false };

        // EngineGone: terminal. Only a new view-model, on a new engine, leaves it.
        private EngineExit? _engineGone;

        /// <summary>
        /// The engine process has exited (issue #299), or null while it runs. Terminal: a warm start is then a no-op
        /// and a start fails at once with the exit, so nothing asks the dead engine for a session.
        /// </summary>
        public EngineExit? EngineGone => _engineGone;

        /// <summary>
        /// Enters EngineGone. Returns true the first time only: the exit's own report and a call that ran into it
        /// are two routes to one transition, and the caller says so once.
        /// </summary>
        /// <remarks>
        /// A turn still outstanding will never return in any useful sense, so its lease is voided, and the
        /// warm slot is dropped. Not <c>None</c>: that state means the next send starts a session, so New and a
        /// workspace move would warm-start into the dead engine and every send would try again.
        /// </remarks>
        public bool EngineExited(EngineExit exit)
        {
            if (exit is null)
                throw new ArgumentNullException(nameof(exit));
            if (_engineGone is not null)
                return false;

            _engineGone = exit;
            _outstandingTurn = null;
            _liveTurnEpoch = _transcriptEpoch;
            _warmTask = null;
            _warmRequest = null;
            _log?.Invoke("[lifetime] engine exited: no session will be started or prompted until the chat window restarts");
            return true;
        }

        /// <summary>The pane no longer holds the engine's session as its own: dropped, replaced, or swapped off screen.</summary>
        public void Detach()
        {
            _sessionOpen = false;
            _prompted = false;
            _superseded = false;
            // The refusal is deliberately NOT cleared here. This is the PANE letting go of a session, which
            // every conversation swap does, and the session itself lives on: opening another conversation
            // and coming back re-adopts the very session the refusal belongs to (issue #256), and clearing
            // here lost the question across that round trip - the next send then reloaded for real and was
            // refused a second time. It dies where the SESSION does: ReleaseLiveOwner, which every start
            // goes through, and Commit, which answers it.
            //
            // The failed recap is not cleared here either, and for a sharper reason: Detach has two callers
            // that mean OPPOSITE things. ClearForConversationSwap is the pane changing conversation, which
            // is one of the ways back and does discharge it; StartEngineSessionAsync detaches as part of
            // OPENING a session - including the very session a send opens straight after its recap failed,
            // when the user answers #84 with the full reload. Clearing here wiped the record mid-send, and
            // the refused-reload banner went on to offer the recap that had just failed (measured: two
            // tests, red on exactly that). The swap calls ForgetRecapFailure for itself.
        }

        // ---- the #217 epoch and the turn lease --------------------------------------------------

        /// <summary>
        /// The turn on the wire was started for a conversation this pane no longer shows (issue #217).
        /// True only between a workspace move made mid-turn and that turn's prompt actually returning.
        /// </summary>
        /// <remarks>
        /// This is the guard that does the work, and it is deliberately independent of the cancel beside
        /// it: <c>session/cancel</c> is best-effort — a backend may emit several more frames after
        /// answering it, an MCP call a cancel orphans never answers at all, and the cancel itself can
        /// fail — so nothing here may be conditioned on the agent having actually stopped.
        /// </remarks>
        public bool IsTurnRetired => _liveTurnEpoch != _transcriptEpoch;

        /// <summary>A committed turn has not returned its lease yet.</summary>
        public bool HasOutstandingTurn => _outstandingTurn is not null;

        /// <summary>Whether a send started under <paramref name="token"/> still belongs to the conversation on screen.</summary>
        public bool IsCurrent(SessionToken token) => token.Epoch == _transcriptEpoch;

        /// <summary>The token a send begun now is started under (issue #217).</summary>
        public SessionToken CurrentToken => new SessionToken(_transcriptEpoch);

        /// <summary>
        /// The conversation on screen was replaced. A turn committed for the old one no longer belongs
        /// anywhere on screen; keeping the two epochs in step when nothing is running is what stops this
        /// being a one-way switch: out-of-turn traffic (the MCP roster, the bridge's own status) has no
        /// turn behind it and must keep flowing.
        /// </summary>
        /// <remarks>
        /// "A turn running" is an outstanding lease, not the pane being busy. A send that
        /// has not committed holds no lease, so nothing is left retired on its account: its own continuations
        /// find their token stale and stop, and whatever its session still sends is dropped by that token
        /// (<see cref="Route"/>).
        /// </remarks>
        public void ConversationReplaced()
        {
            _transcriptEpoch++;
            if (_outstandingTurn is null)
                _liveTurnEpoch = _transcriptEpoch;
        }

        /// <summary>
        /// The send's prompt is about to go out: runs <paramref name="commitConversation"/> - the host records
        /// the message and applies what the session reported to the conversation - and marks the session
        /// Prompted for that conversation. Returns the turn's lease, or null for a stale token, in which
        /// case nothing is run, recorded or marked.
        /// </summary>
        /// <remarks>
        /// <para><b>Immediately before the prompt, in one synchronous block with it.</b> Taken at the start of
        /// the send instead, a session that was opened and then backed out of would count as prompted, and a
        /// send retired mid-way would already have written into its conversation.</para>
        /// <para><b>The conversation is the host's to commit, and is returned rather than passed</b> because a
        /// brand-new one does not exist until its first message is recorded, and the owner (issue #256) is
        /// that conversation. Until a reference to it exists from <c>Begin</c>, this callback is how the
        /// lifetime learns it without reaching for the log.</para>
        /// <para><b>A lease still outstanding is a missed return, and never breaks the pane.</b> It is
        /// voided as a retired return would be and reported as a breach, whose sink writes the one
        /// unconditional line and fails the test that caused it; the new lease is then issued as usual.</para>
        /// </remarks>
        public PromptedTurn? Commit(SessionToken token, Func<PersistedSession?> commitConversation)
        {
            if (commitConversation is null)
                throw new ArgumentNullException(nameof(commitConversation));

            if (!IsCurrent(token))
                return null;

            if (_outstandingTurn is { } stale)
            {
                var line = "[lifetime] stale turn lease voided: a turn committed "
                    + ((long)stale.Age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s ago for "
                    + (stale.Title is { } title ? "'" + title + "'" : "no conversation")
                    + " never returned; proceeding";
                _liveTurnEpoch = _transcriptEpoch;
                _outstandingTurn = null;
                _invariants.Breach(line);
            }

            var conversation = commitConversation();

            _sessionOpen = true;
            _prompted = true;
            _superseded = false;
            // Answered: a prompt of this conversation has gone out on this session, so there is no longer a
            // question about how to resume into it - nor about how to reconnect it, which is what a failed
            // recap withholds an answer to.
            ForgetRefusedReload();
            _recapFailed = false;
            _uncommittedStart = null;
            if (_liveOwnerStarted is not null)
            {
                _liveOwner = conversation;
                _liveOwnerPrompted = true;
            }

            _liveTurnEpoch = _transcriptEpoch;
            var turn = new PromptedTurn(this, token, conversation?.Title ?? _displayed?.Title);
            _outstandingTurn = turn;
            return turn;
        }

        internal TurnReturn ReturnTurn(PromptedTurn turn)
        {
            // The retired turn has now actually returned, and THAT is the signal (issue #217): after
            // it, everything on the channel belongs to the conversation on screen again.
            var wasRetired = !IsCurrent(turn.Token);
            if (wasRetired)
                _liveTurnEpoch = _transcriptEpoch;
            if (ReferenceEquals(_outstandingTurn, turn))
                _outstandingTurn = null;
            return new TurnReturn(wasRetired);
        }

        // ---- the conversation on screen ---------------------------------------------------------

        /// <summary>Which conversation is on screen. The view-model calls this from the one setter every change of it goes through.</summary>
        public void Display(PersistedSession? conversation) => _displayed = conversation;

        // ---- warm starts and session starts -----------------------------------------------------

        /// <summary>The request the warm session was opened with, or null.</summary>
        public StartSessionRequest? WarmRequest => _warmRequest;

        /// <summary>
        /// Pre-opens a backend session for <paramref name="request"/>. The mechanism only: whether the
        /// pane is certainly about to start fresh is the view-model's <c>WarmStartSession</c> to decide.
        /// Safe to call repeatedly; a matching warm stays, a changed request re-warms.
        /// </summary>
        public void WarmStart(StartSessionRequest request)
        {
            // EngineGone: there is no engine to warm a session in.
            if (_engineGone is not null)
                return;

            // Never while a turn is outstanding: a retired turn's return comes
            // first, and it is the return that posts the warm start for the new root.
            if (_outstandingTurn is not null)
                return;

            if (request == _warmRequest && _warmTask is not null)
                return; // already warm (or warming) for exactly this request

            var previous = _warmTask;
            var inFlight = _startInFlight;
            _warmRequest = request;
            _warmTask = WarmCoreAsync(previous, inFlight, request);
        }

        // Chains behind any previous warm, and behind a start a send still has in flight: the engine hosts
        // one session at a time, so interleaved StartSession calls would race its teardown. The second is a
        // send a workspace move retired before it prompted: it holds no lease, so the
        // move's warm start is no longer refused, and it replaces that send's session once the start is done.
        // Best-effort by design — a failure resolves null and the real send retries cold, surfacing the error
        // in the transcript then too.
        private async Task<StartSessionResponse?> WarmCoreAsync(
            Task<StartSessionResponse?>? previous, Task? inFlight, StartSessionRequest request)
        {
            if (previous is not null)
                await previous.ConfigureAwait(true); // never faults (see catch below)

            if (inFlight is not null)
            {
                try { await inFlight.ConfigureAwait(true); }
                catch { /* the start's own caller reports its failure */ }
            }

            try
            {
                _host.ClearMcpState(); // before the start: the roster arrives during it
                var started = await StartEngineSessionAsync(request).ConfigureAwait(true);
                _host.WarmStartSucceeded();
                return started;
            }
            catch (EngineExitedException exited)
            {
                // The exit, not this warm start's failure: the host makes the transition and names the exit once,
                // rather than naming a backend that "couldn't start" in an engine that is gone.
                _host.EngineExited(exited.Exit);
                return null;
            }
            catch (Exception ex)
            {
                _host.WarmStartFailed(request, ex);
                return null;
            }
        }

        /// <summary>
        /// Reuses the pre-warmed backend session when it matches what this send needs (same
        /// provider/model/workspace, fresh); otherwise starts one now — the engine tears the mismatched
        /// warm session down as part of starting the real one.
        /// </summary>
        /// <remarks>
        /// Matched on <see cref="StartSessionRequest"/> RECORD equality, so a field added to that record
        /// silently changes warm reuse.
        /// </remarks>
        /// <param name="request">What the send needs.</param>
        /// <param name="forSend">The token the send was begun under: the session it gets is uncommitted until
        /// <see cref="Commit"/>, and dropped from routing once this token is stale.</param>
        public Task<StartSessionResponse> TakeWarmOrStartAsync(StartSessionRequest request, SessionToken forSend)
        {
            // EngineGone: fails at once with the exit, rather than asking the dead engine.
            if (_engineGone is { } gone)
                return Task.FromException<StartSessionResponse>(new EngineExitedException(gone));

            // The whole take is the start in flight, the wait on the warm session included, so a warm start begun
            // meanwhile chains behind it. Recorded only around the engine call, a warm
            // start made while this send waited on a warm session that then failed went into the engine beside
            // this send's cold start, and a move could leave the engine holding the old root's session.
            var take = TakeWarmOrStartCoreAsync(request, forSend);
            _startInFlight = take;
            return take;
        }

        private async Task<StartSessionResponse> TakeWarmOrStartCoreAsync(StartSessionRequest request, SessionToken forSend)
        {
            var warmTask = _warmTask;
            var warmRequest = _warmRequest;
            _warmTask = null;
            _warmRequest = null;
            _uncommittedStart = forSend;

            if (warmTask is not null)
            {
                var warm = await warmTask.ConfigureAwait(true);
                if (warm is not null && request == warmRequest)
                {
                    _uncommittedStart = forSend;
                    return warm;
                }
            }

            _host.ClearMcpState(); // before the start: the roster arrives during it
            return await StartEngineSessionAsync(request, forSend).ConfigureAwait(true);
        }

        /// <summary>
        /// The one route to <c>engine/startSession</c>, because starting a session is what ENDS the
        /// previous one: the engine hosts one at a time and disposes the old inside the new start.
        /// So this is where the live owner is released — before the call, not after it, or the new
        /// session's opening frames (kiro-cli's <c>fetch_cloud_config</c>) arriving during the start
        /// would be recorded into the conversation the OLD session belonged to (issue #256).
        /// </summary>
        /// <param name="request">The session to start.</param>
        /// <param name="forSend">A send's token, when a send is starting it (see <see cref="TakeWarmOrStartAsync"/>).</param>
        /// <remarks>
        /// Starting a session also ends the pane's hold on the one it had: a refused or empty import used
        /// to leave the pane counting the session it replaced as started, and the next message went unasked
        /// into the import's.
        /// </remarks>
        public Task<StartSessionResponse> StartEngineSessionAsync(StartSessionRequest request, SessionToken? forSend = null)
        {
            // EngineGone: fails at once with the exit, and releases nothing - no session replaces the one it had.
            if (_engineGone is { } gone)
                return Task.FromException<StartSessionResponse>(new EngineExitedException(gone));

            ReleaseLiveOwner("replaced by a new session", sessionDisposed: true);
            Detach();
            _uncommittedStart = forSend;
            _liveSessionDiscarded = false;
            _discardedOwnerTitle = null;
            _engineSessionProviderId = request.ProviderId;
            _droppedFromAbandonedWarm = 0;
            var start = _engine.StartSessionAsync(request);
            _startInFlight = start;
            return start;
        }

        // ---- the live owner (issue #256) --------------------------------------------------------

        /// <summary>The conversation the live session was started for, or null.</summary>
        public PersistedSession? LiveOwner => _liveOwner;

        public StartSessionRequest? LiveOwnerRequest => _liveOwnerRequest;

        public StartSessionResponse? LiveOwnerStarted => _liveOwnerStarted;

        /// <summary>Whether the owner's session has been prompted, so a re-attach restores the same state.</summary>
        public bool LiveOwnerPrompted => _liveOwnerPrompted;

        /// <summary>
        /// The lifetime's half of adopting a session: it is the pane's, and from here until the next session
        /// start everything on the wire is the displayed conversation's. The owner is null when the host has no
        /// store, or the conversation is brand new: <see cref="Commit"/> names it then.
        /// </summary>
        /// <param name="request">What the session was opened with.</param>
        /// <param name="started">Its handshake.</param>
        /// <param name="prompted">
        /// Whether it is adopted already prompted: an import, whose history the backend holds, or a re-attach to
        /// an owner that was. A send's session is adopted Open, and becomes Prompted only at its commit.
        /// </param>
        public void Adopt(StartSessionRequest request, StartSessionResponse started, bool prompted)
        {
            _sessionOpen = true;
            _prompted = prompted;
            _superseded = false;
            // Not cleared here either, and for the same reason: a RE-ATTACH adopts the session the pane
            // already had (issue #256), so the refusal it carries is still this session's. Where the session
            // is genuinely new, the start that opened it released the previous owner and cleared it there.
            _liveOwner = _displayed;
            _liveOwnerRequest = request;
            _liveOwnerStarted = started;
            _liveOwnerPrompted = prompted;
            if (prompted)
                _uncommittedStart = null;
        }

        /// <summary>
        /// Whether opening <paramref name="conversationId"/> re-attaches to the live session rather than
        /// restoring it: the backend never dropped it, a history-picker open starting no session.
        /// </summary>
        public bool CanReattach(string conversationId) =>
            _liveOwner is not null
            && string.Equals(_liveOwner.Id, conversationId, StringComparison.Ordinal)
            && _liveOwnerRequest is not null && _liveOwnerStarted is not null;

        /// <summary>The owner is back on screen: closes its off-screen episode in the log.</summary>
        public void NoteReattached(PersistedSession session)
        {
            if (_routedOffScreen > 0)
                _log?.Invoke(
                    $"[out-of-turn] off screen: '{session.Title}' ({session.Id}) reopened"
                    + $" after {_routedOffScreen} event(s) recorded to it off screen");
            _routedOffScreen = 0;
        }

        /// <summary>
        /// A model switched in place. A re-attach re-adopts from the request the session was opened with
        /// (issue #256), so the switch has to reach it or reopening the conversation would report the old model.
        /// </summary>
        public void LiveOwnerModelSwitched(string modelId)
        {
            if (_liveOwnerRequest is not null)
                _liveOwnerRequest = _liveOwnerRequest with { ModelId = modelId };
        }

        /// <summary>
        /// A conversation was deleted. The live session may belong to it, on screen or not: released
        /// WITHOUT a flush, since the next off-screen save would otherwise recreate the file (issue #256),
        /// and tombstoned with its title (pre-release security review, September 2026).
        /// </summary>
        public void LiveOwnerDeleted(string conversationId)
        {
            if (_liveOwner is not null && string.Equals(_liveOwner.Id, conversationId, StringComparison.Ordinal))
            {
                _liveOwnerDirty = false;
                _discardedOwnerTitle = _liveOwner.Title;
                ReleaseLiveOwner("deleted", sessionDisposed: false);
                _liveSessionDiscarded = true;
            }
        }

        /// <summary>
        /// The engine's live session belongs to a conversation other than the one on screen (issue
        /// #256): a history-picker open replaced the transcript without touching the session. Compared
        /// by id, not reference — reopening the owner loads a fresh object for the same conversation.
        /// </summary>
        public bool IsLiveSessionOffScreen =>
            _liveOwner is not null && (_displayed is null || !string.Equals(_displayed.Id, _liveOwner.Id, StringComparison.Ordinal));

        /// <summary>
        /// Which conversation a permission request belongs to, when that is not the one on screen:
        /// the live owner while it is off screen (issue #256), or the tombstone of an owner the user
        /// deleted while its session ran on (pre-release security review, September 2026). Null in the ordinary case. One
        /// resolver, because with more than one live conversation (issue #150) "whose is this" is a
        /// question every banner asks, and the answer should come from one place.
        /// </summary>
        public (string? title, bool deleted) ResolveOrigin()
        {
            if (IsLiveSessionOffScreen)
                return (_liveOwner!.Title, false);
            if (_liveSessionDiscarded && _discardedOwnerTitle is { } deleted)
                return (deleted, true);
            return (null, false);
        }

        // ---- live frames ------------------------------------------------------------------------

        /// <summary>
        /// Where a live frame goes: the routing half of the view-model's <c>ApplyLive</c>, in the order it
        /// always ran. A synchronous decision with no await and no post; its only side effect is the
        /// abandoned-warm drop's one log line.
        /// </summary>
        /// <param name="ev">The frame.</param>
        /// <param name="pickedProviderId">The backend the picker is on now (the session request's when none is picked).</param>
        /// <param name="connectionFact">
        /// Whether the frame is a fact about the CONNECTION, which the session panel tracks independently
        /// of any transcript and which a swap deliberately keeps (the roster is cleared with the backend
        /// session, never with the transcript).
        /// </param>
        public FrameRoute Route(AgentEventDto ev, string? pickedProviderId, bool connectionFact)
        {
            // Everything the retired turn still emits is dropped, and dropped BEFORE all three of these:
            // rendering it puts the old solution's work in the new solution's chat (the reported bug),
            // recording it writes it into a conversation it was never part of, and the tool boundary
            // would open ledger entries against rows this transcript does not contain. Kept a SEPARATE
            // decision from the off-screen route below: with no store there is no owner to route to, and
            // this is then the only guard - which is why WorkspaceReloadTests carries the no-store case
            // (TheRetiredTurnsOutputNeverReachesTheNewChatWhenNothingIsSaved) beside the with-store one:
            // with this drop removed, the no-store case is the one that goes red on its own.
            if (IsTurnRetired)
                return FrameRoute.Drop;

            // A session a send opened and never committed, for a conversation that has since left the
            // screen. It was nobody's prompt, and the start that replaces it is chained behind it (WarmStart),
            // so what it sends meanwhile - its opening, its MCP roster - is dropped, connection facts included.
            // Before the split the busy pane's epoch retired these; a send holds no lease before its commit.
            if (_uncommittedStart is { } start && !IsCurrent(start))
                return FrameRoute.Drop;

            // A warm session nobody has adopted, for a backend the picker has since moved away from.
            // Measured at startup (2026-09-11): the default backend (Kiro) began warming, the last
            // conversation (Claude) was restored, and Kiro's opening frames — its setup tool row and
            // its MCP-connected notices — arrived three seconds later and were drawn into Claude's
            // transcript. Nothing owns those events (a warm session has no conversation), and their
            // session is already being replaced by the chained warm start for the picker's backend, so
            // they are dropped — connection facts included: a roster for a backend the pane is not on
            // is the same lie. Gated on the owner being null, so an ADOPTED session the picker later
            // moves away from keeps delivering (a late background return belongs to the conversation
            // on screen); and on the engine's provider rather than the warm request's, which is
            // already the new choice while the old session is still the one emitting.
            if (_liveOwner is null && _engineSessionProviderId is { } engineProvider
                && pickedProviderId is { } picked
                && !string.Equals(engineProvider, picked, StringComparison.OrdinalIgnoreCase))
            {
                if (_droppedFromAbandonedWarm++ == 0)
                    _log?.Invoke(
                        $"[out-of-turn] dropped '{ev.Type}' from the abandoned '{engineProvider}' warm session: "
                        + $"the picker is '{picked}', nothing has been sent, and its replacement is starting");
                return FrameRoute.Drop;
            }

            // A live session whose conversation is NOT the one on screen (issue #256). Its events are
            // that conversation's, so they go into its log and not onto this transcript — the one
            // exception being facts about the connection.
            if (_liveSessionDiscarded && !connectionFact)
                return FrameRoute.Drop;
            // An owner whose session was never prompted (a refused reload the user backed out of, then another
            // conversation opened) has nothing of its own to record, and nowhere on screen to be drawn.
            if (IsLiveSessionOffScreen && !connectionFact)
                return _liveOwnerPrompted ? FrameRoute.OffScreen : FrameRoute.Drop;

            return FrameRoute.Live;
        }

        /// <summary>
        /// A frame routed off screen is about to be recorded to the owner: returns the owner, and writes
        /// the episode's log line on its first event.
        /// </summary>
        /// <remarks>
        /// The line is written on the episode's first event only, and <see cref="ReleaseLiveOwner"/> or a
        /// re-attach writes its close with a count. This is the instrument the report asked for: the
        /// out-of-turn window's own line cannot fire here, being gated on the ON-SCREEN session having
        /// been prompted, so without this an episode leaves no trace in <c>engine.log</c> at all.
        /// </remarks>
        public PersistedSession NoteRoutedOffScreen(AgentEventDto ev)
        {
            var owner = _liveOwner!;
            if (_routedOffScreen++ == 0)
                _log?.Invoke(
                    $"[out-of-turn] off screen: '{ev.Type}'"
                    + (ev.ToolCallId is { Length: > 0 } id ? $" ({id})" : string.Empty)
                    + $" belongs to '{owner.Title}' ({owner.Id}); "
                    + (_displayed is null ? "no conversation" : $"'{_displayed.Title}' ({_displayed.Id})")
                    + " is on screen; recording to its own log");
            return owner;
        }

        // The checkpoint rule saves on turn boundaries, and an off-screen reply has none, so the owner
        // is flushed on the host's short timer as well — and synchronously before anything reads its file.
        public void MarkOwnerDirty()
        {
            _liveOwnerDirty = true;
            _host.ArmOwnerFlush();
        }

        public void FlushLiveOwner()
        {
            _host.DisarmOwnerFlush();
            if (!_liveOwnerDirty)
                return;
            _liveOwnerDirty = false;
            if (_liveOwner is not null && _store is not null)
                _store.Save(_liveOwner);
        }

        /// <summary>
        /// Forgets which conversation the live session belongs to, flushing anything recorded to it
        /// off screen first. Called where the session actually ends — the next start — and where the
        /// conversation does: deletion, after which a save would resurrect the file.
        /// </summary>
        private void ReleaseLiveOwner(string why, bool sessionDisposed)
        {
            FlushLiveOwner();
            if (_routedOffScreen > 0 && _liveOwner is not null)
                _log?.Invoke(
                    $"[out-of-turn] off screen: '{_liveOwner.Title}' ({_liveOwner.Id}) {why}"
                    + $" after {_routedOffScreen} event(s) recorded to it off screen");
            _routedOffScreen = 0;
            _liveOwner = null;
            _liveOwnerRequest = null;
            _liveOwnerStarted = null;
            _liveOwnerPrompted = false;
            // The refused reload described the session that is ending with this owner.
            //
            // The failed recap is deliberately NOT cleared here, though every start comes through here and
            // that looks like the natural home for "any new session is the way back". It also fires DURING
            // the start made by the send that has just recorded the failure: on the #84 route the recap
            // fails before any session exists, the user answers with the full reload, and that reload's own
            // start wiped the record moments later - so the refused-reload banner went on to offer the
            // recap that had just failed. Measured: two existing tests went red on exactly that.
            ForgetRefusedReload();
            _host.LiveOwnerReleased(sessionDisposed);
        }
    }
}
