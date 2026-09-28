using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.Shell.Sessions;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.UI.Sessions
{
    /// <summary>
    /// What a send does when the resume the user chose did not happen — the summary could not be
    /// produced (issue #84), or the backend refused to reload the conversation (issue #268).
    /// </summary>
    internal enum ResumeFallback
    {
        /// <summary>Reload the backend's full conversation instead. Only offered when it is possible.</summary>
        Full,

        /// <summary>
        /// Send a recap built from our own copy of the transcript instead. The mirror of
        /// <see cref="Full"/>, offered on the other route (issue #268) — there the full reload is
        /// the thing that just failed, and the local recap is what is left.
        /// </summary>
        Summary,

        /// <summary>
        /// "Start new conversation": New, then the message sent there (see
        /// <see cref="PromptDelivery.StartOverInNewConversation"/>). Only where there is no earlier
        /// conversation to leave does it still mean what it used to - send with no history.
        /// </summary>
        Fresh,

        /// <summary>Nobody is going to answer: the conversation was replaced while the banner was up.</summary>
        Abandon,

        /// <summary>
        /// The user backed out at the banner's own Cancel. The message was never recorded, so it goes
        /// back to the input box (see <see cref="PromptDelivery.GiveBackUnsentMessage"/>).
        /// </summary>
        BackedOut,

        /// <summary>
        /// The same ending reached by Stop. Separate from <see cref="BackedOut"/> because the two are
        /// different gestures from the user's seat: Stop holds the tray as a Stop and moves the pill to
        /// Queue, while a Cancel means "as it was" and leaves that setting alone.
        /// </summary>
        Stopped,
    }

    /// <summary>
    /// What a transcript clear does with permission requests that are still open.
    /// The question is the SESSION, not the transcript: a request can only be answered while the session
    /// that asked it is the one the engine holds.
    /// </summary>
    internal enum OpenRequests
    {
        /// <summary>
        /// Cancel them. The session is being disposed — New, a workspace move, an import, a provider
        /// change that starts one — so nothing is left to answer, and the cancel is what unblocks a
        /// backend otherwise left waiting on a banner nobody can honour.
        /// </summary>
        Cancel,

        /// <summary>
        /// Keep them, and re-label each with whose it is. The session lives on: a history open starts
        /// none, and a delete tombstones the owner while its session runs. Issue #256's rule is asked and
        /// labelled, never cancelled.
        /// </summary>
        KeepAndLabel,
    }

    /// <summary>Why the conversation on screen is changing.</summary>
    internal enum ConversationChange
    {
        /// <summary>The transcript is cleared for a new conversation: New, a start-over, a fork, a delete on screen.</summary>
        TranscriptCleared,

        /// <summary>Another conversation replaces the one on screen: a history open, an import's render.</summary>
        ConversationSwapped,

        /// <summary>The solution the pane is scoped to moved.</summary>
        WorkspaceMoved,

        /// <summary>An import is about to replace the backend session.</summary>
        Import,

        /// <summary>A picker change drops the session.</summary>
        SelectionChanged,

        /// <summary>The engine process exited (issue #299): terminal, and nothing on screen is cleared.</summary>
        EngineExited,

        /// <summary>The transcript was cleared by a route that did not say it was changing the conversation.</summary>
        Unlisted,
    }

    /// <summary>
    /// One user message from Enter until its prompt goes out: whether and how a reopened conversation
    /// resumes, the banners that can ask about it, the session start it may need, and what backing out
    /// gives back. A steer, which goes into a turn already running, is the one phase-less send.
    /// </summary>
    /// <remarks>
    /// <para><b>Moved out of ChatViewModel</b>: this holds the fields and methods the view-model held.</para>
    /// <para><b>Where it stops.</b> The send's prologue flags, the prompt itself and the turn's catch and
    /// finally stay in the view-model's turn runner (<c>SendCoreAsync</c>), which calls
    /// <see cref="Begin"/> and then awaits <see cref="PrepareAsync"/>. That keeps the #253/#277
    /// dispatcher-ordering code where it was.</para>
    /// <para><b>One synchronization context, no dispatcher.</b> Every member is called on the pane's,
    /// and every await resumes there, as it did in the view-model.</para>
    /// </remarks>
    internal sealed class PromptDelivery
    {
        /// <summary>
        /// A message that has left the composer or the tray and has not begun a send yet: it is waiting
        /// on the resume-choice or the moved-root banner, both of which ask BEFORE anything is started
        /// or recorded.
        /// </summary>
        /// <remarks>
        /// <para><b>It carries the framing, and that is the whole reason it is not a string.</b> A tray
        /// release is the only send that has any — a preamble telling the agent its work was cut short,
        /// and the note on the bubble saying so — and a release could not reach these banners until it
        /// began deciding for itself. The moment it could, a bare text field would have sent the
        /// message on as though it had just been typed.</para>
        /// <para><b>Whether it OWNS its chips is the other thing only the message knows.</b> A typed
        /// message leaves its pictures and its IDE captures in the composer while the banner is up, so
        /// backing out returns the whole message by leaving them where they are; a released batch took
        /// its chips out of the tray when it was held, so this is the only thing holding them and a
        /// back-out has to put them back. Null means "still in the composer", and the send takes them at
        /// the moment it actually goes.</para>
        /// </remarks>
        private sealed class ParkedMessage
        {
            private ParkedMessage(
                bool stillInTheComposer, string text, string? preamble, string? deliveryNote,
                IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts)
            {
                StillInTheComposer = stillInTheComposer;
                Text = text;
                Preamble = preamble;
                DeliveryNote = deliveryNote;
                Attachments = attachments;
                Contexts = contexts;
            }

            /// <summary>
            /// A message the composer let go: its TEXT leaves the box when the send commits to a route,
            /// and its chips stay where they are until the send actually goes - which is what lets a
            /// back-out return the whole message by leaving them alone. It carries no framing, an Enter
            /// being nobody's interruption.
            /// </summary>
            public static ParkedMessage FromComposer(string text) => new ParkedMessage(
                stillInTheComposer: true, text, preamble: null, deliveryNote: null,
                Array.Empty<AttachmentViewModel>(), Array.Empty<ContextItemViewModel>());

            /// <summary>
            /// A batch the tray released: framed by the gesture that released it, and carrying the chips
            /// it took OUT of the tray when the messages were held. The composer holds none of it, so a
            /// back-out has to put the chips back and must not touch the box.
            /// </summary>
            public static ParkedMessage FromTray(
                string text, string? preamble, string? deliveryNote,
                IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts) =>
                new ParkedMessage(stillInTheComposer: false, text, preamble, deliveryNote, attachments, contexts);

            /// <summary>Where this message's text and chips are: in the composer, or held here.</summary>
            /// <remarks>
            /// <para><b>One fact, not two, and STATED rather than inferred.</b> A typed message leaves its
            /// text in the box and its chips beside it; a released batch took both out of the tray. They
            /// cannot disagree, so one flag answers for both - and the two factories above are the only
            /// places it is decided, which is what stops a third construction site guessing.</para>
            /// <para><b>It was inferred from the chips being absent, and that is the near-miss worth
            /// naming.</b> It read correctly, because a released batch with no pictures still owned an
            /// EMPTY list rather than none - but three expressions elsewhere each re-derived the same
            /// distinction, and a later change passing the composer's chips through would have had to
            /// find all of them. The one that would have been missed puts a second copy of every picture
            /// in the composer on a back-out.</para>
            /// </remarks>
            public bool StillInTheComposer { get; }

            public string Text { get; }

            public string? Preamble { get; }

            public string? DeliveryNote { get; }

            /// <summary>The chips this message took out of the tray; empty where the composer still holds them.</summary>
            public IReadOnlyList<AttachmentViewModel> Attachments { get; }

            public IReadOnlyList<ContextItemViewModel> Contexts { get; }
        }

        private readonly IDeliveryHost _host;
        private readonly SessionLifetime _lifetime;
        private readonly IEngineConnection _engine;
        private readonly IInvariantSink _invariants;

        private ResumeStrategy _resumeStrategy = ResumeStrategy.Fresh;

        // The send between Begin and its prompt, or null. Held so that Stop can reach it: during the
        // summarize, the session start and the IDE-tools wait there is no banner and no turn, so the
        // send object lived only in PrepareCoreAsync's frame and nothing outside could end it.
        private OutgoingSend? _pending;

        /// <summary>
        /// The message waiting behind the resume-choice or moved-root banner, or null. Everything
        /// <see cref="Begin"/> will need, because those two banners are ahead of the turn runner and the
        /// send has no <see cref="OutgoingSend"/> yet.
        /// </summary>
        private ParkedMessage? _parked;

        /// <summary>
        /// The in-flight send's wait on the summary-failure banner, or null when no send is parked.
        /// Only ever one: a send holds <c>IsBusy</c> for its whole duration, so a second cannot
        /// reach here while the first is waiting.
        /// </summary>
        private TaskCompletionSource<ResumeFallback>? _resumeFallback;

        /// <summary>
        /// Where the one pending send is, or null when none is: there is at most one. Set at every point a
        /// send waits - a banner, an engine call - and cleared when it prompts or ends.
        /// </summary>
        /// <remarks>
        /// Every phase, not only a send parked on a resume-FAILURE banner. Without it, the Choice and
        /// moved-root banners park their text in a field, so the pane reads as idle through them: a second
        /// Enter begins a second send rather than holding, and the first message's text is overwritten.
        /// </remarks>
        private SendPhase? _phase;

        public PromptDelivery(
            IDeliveryHost host, SessionLifetime lifetime, IEngineConnection engine, IInvariantSink invariants)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _invariants = invariants ?? throw new ArgumentNullException(nameof(invariants));
        }

        // The host's transcript, composer text, banner slot and resume check, under the names the moved code
        // always used for them.
        private ObservableCollection<ChatItemViewModel> Items => _host.Items;

        private string InputText
        {
            get => _host.InputText;
            set => _host.InputText = value;
        }

        private ResumeChoiceViewModel? PendingResume
        {
            set => _host.PendingResume = value;
        }

        private bool CanResumeFull() => _host.CanResumeFull();

        // ---- what the view-model reads and resets -------------------------------------------------

        /// <summary>How the next prompt on a restored conversation reconnects.</summary>
        internal ResumeStrategy Strategy => _resumeStrategy;

        /// <summary>A send is parked on a resume-failure banner (issue #84's, or issue #268's).</summary>
        internal bool HasParkedSend => _resumeFallback is not null;

        /// <summary>
        /// A send exists and has not prompted yet: true from the gesture that began it until it goes out
        /// or ends, in EVERY phase rather than only while a resume-failure banner is up. While it is true the
        /// tray holds every send gesture, and the controls a conversation change would reach are refused.
        /// </summary>
        internal bool HasPendingSend => _phase is not null;

        /// <summary>Which phase that send is in, for the reason a refused control gives.</summary>
        internal SendPhase? Phase
        {
            get => _phase;
            private set
            {
                if (_phase == value)
                    return;
                _phase = value;
                // One notification for everything that reads it - the reason, the working bar, and the
                // commands refused on its account. Raised here rather than at the dozen sites that move the
                // phase, so a phase added later cannot be the one that forgets to announce itself.
                _host.PendingSendChanged();
            }
        }

        /// <summary>Forgets the resume decision and any message parked behind the Choice or moved-root banner.</summary>
        internal void ResetResumeDecision()
        {
            _resumeStrategy = ResumeStrategy.Fresh;
            _parked = null;
        }

        /// <summary>
        /// Ends a pending send because the conversation on screen is changing.
        /// Synchronous, and called before anything on screen changes. The message goes back to the
        /// composer whole - text, images and captures - on every route that reaches here.
        /// </summary>
        /// <remarks>
        /// <para><b>Given back rather than dropped</b> (user decision, 2026-09-14). A typed, unsent draft
        /// already crosses a workspace move untouched; a message that had LEFT the box but never went out
        /// used to be discarded, taking its pasted image and its IDE capture with it and leaving nothing on
        /// screen to say so. Its consistent comparison is its own Cancel and Stop, which both give back.</para>
        /// <para><b>And with no notice</b> (same decision). The composer filling back up is the signal, as
        /// it is after a Cancel or a Stop, neither of which says anything either.</para>
        /// <para><b>Two forms, because a send before <see cref="Begin"/> has no object.</b> While the
        /// resume-choice or moved-root banner is up the message is parked in a field; from
        /// <see cref="Begin"/> onwards the send holds it. Either way the whole message comes back — a
        /// TYPED message's chips are still in the composer, deliberately, so leaving them there returns
        /// it whole, while a RELEASED batch brought its own out of the tray and the parked message is
        /// the only thing holding them.</para>
        /// </remarks>
        internal void EndPending(ConversationChange why)
        {
            // Read BEFORE the phase is cleared: it is what says whether a send was ended here at all, and
            // the pane may only be freed below when one was. A conversation change landing during a real
            // TURN reaches this too, and there IsBusy belongs to the turn on the wire, which must go on
            // holding the pane until it returns (issue #217).
            var wasPending = HasPendingSend;

            // The phase goes first and unconditionally: left set, a send the conversation change has just
            // ended would hold the tray and the pane's controls for the life of the window. And the send
            // itself with it: left set, StopPendingSend would answer for a send that no longer exists and
            // hand its message back a SECOND time, ahead of whatever has been typed since. That is latent
            // only because the working bar - and so Stop's CanExecute - is false in this window, which is a
            // property of the bar's definition rather than of anything here (scoped review, 2026-09-17).
            Phase = null;
            // Before the field is cleared, and before the transcript is. Most of these routes clear it a
            // moment later, so the removal is invisible there; the engine-exit route does NOT, and that is
            // the one where a "Summarizing…" line would be left standing over a chat nobody can send in.
            DropResumeNotices(_pending);
            var send = _pending;
            var parked = _parked;
            _pending = null;
            _parked = null;
            AbandonParkedSend();
            // And the banner the send was asking through, so nothing is left on screen offering buttons
            // that answer a send which no longer exists. This is what makes the host's PendingResume a
            // read-only projection: a route cannot dismiss a banner, only end the send that
            // raised it - and ending one takes its banner with it.
            PendingResume = null;

            // Before the pane is freed, as Stop's give-back is, so the box is full by the time anything
            // reacts to the send being over. Nothing is recorded before the prompt goes, so there is no log entry to undo.
            if (send is not null)
                GiveBackOnce(send);
            else
                GiveBackParked(parked);

            // And the pane stops reading busy on this send's account, whatever it was still awaiting: a
            // summarize, a session start, the wait for the IDE tools. That call still completes later and
            // its result is discarded, the send having ended; without this, the bar stays up for the whole
            // of that wait on behalf of a send that no longer exists - about 20 seconds, measured in Visual
            // Studio.
            if (wasPending)
                _host.SendEnded();
        }

        /// <summary>
        /// The transcript is being cleared with a send still pending, so some route changed the conversation
        /// without <see cref="EndPending"/>. Ended and reported, the report's sink writing the one
        /// engine.log line: never a silent no-op in the product, and never a crash.
        /// </summary>
        internal void EndPendingLeftByAClear()
        {
            if (!HasPendingSend)
                return;

            const string Line =
                "[delivery] transcript cleared with a send still pending: no BeginConversationChange on this route; ended";
            EndPending(ConversationChange.Unlisted);
            _invariants.Breach(Line);
        }

        /// <summary>
        /// A send parked on a resume-failure banner is waiting for an answer only that banner can give, so
        /// the banner going away for ANY reason releases it. Harmless where the choice has already resolved
        /// the wait.
        /// </summary>
        internal void AbandonParkedSend()
        {
            _resumeFallback?.TrySetResult(ResumeFallback.Abandon);
        }

        /// <summary>Whether <paramref name="send"/> is still the send the pane is waiting on.</summary>
        /// <remarks>
        /// Read at every point a conversation change is checked for, and for the same reason: a send
        /// that has been ended must not go on to prompt. Stop ends one without touching the epoch, so
        /// the epoch check alone let a stopped send carry on and send the message it had just handed
        /// back to the composer.
        /// </remarks>
        private bool StillPending(OutgoingSend send) => ReferenceEquals(_pending, send);

        /// <summary>
        /// Stop, where a send is pending and no turn is on the wire: end it and hand the message back.
        /// Returns false when there is no such send, leaving Stop to mean what it otherwise means.
        /// </summary>
        /// <remarks>
        /// <para><b>Found by running it in Visual Studio (2026-09-17).</b> The bar is up while the recap
        /// is written, so Stop is offered - and it was wired to cancel a TURN, of which there is none.
        /// It did nothing, and worse than nothing: an engine cancel with no turn outstanding lands on
        /// whatever the engine holds, which can be another conversation's work off screen (issue #256).
        /// The summarize cannot be cancelled either - EngineService.SummarizeAsync takes no token - so
        /// the only honest meaning is the banners': abort the send, give the message back, discard the
        /// call when it returns.</para>
        /// <para><b>The pane lets go AT ONCE</b>, rather than when the abandoned call returns: the bar
        /// stays up for the whole of that wait otherwise, about 20 seconds.</para>
        /// </remarks>
        internal bool StopPendingSend()
        {
            if (_pending is not { } send)
                return false;

            _pending = null;
            Phase = null;
            PendingResume = null;
            _resumeStrategy = ResumeStrategy.Fresh;
            DropResumeNotices(send);
            _host.HoldTray(TrayHold.Stopped);
            GiveBackOnce(send);
            _host.SendEnded();
            return true;
        }

        // ---- Enter ----------------------------------------------------------------------------------

        /// <summary>
        /// A message the composer let go while no turn is running: decides how a reopened conversation
        /// reconnects, asking where the choice is worth it, then sends.
        /// </summary>
        internal Task SendAsync(string text) =>
            DecideAndSendAsync(ParkedMessage.FromComposer(text));

        /// <summary>
        /// A batch the tray released, which is a send like any other and runs the resume decision like one —
        /// it just arrives with its payload already in hand and framed by the gesture that released it.
        /// </summary>
        /// <remarks>
        /// <para><b>This route used to enter below the resume decision</b>, going straight to the turn
        /// runner, so only Enter decided. On its own that was a send taking no decision; with a resume
        /// strategy that outlived the send it had been chosen for, it was a held batch silently
        /// reloading a whole conversation into the agent — counting toward usage, with no banner and no
        /// notice. Both halves are closed here: the release decides, and every ending resets the
        /// strategy (<see cref="CancelResume"/> included).</para>
        /// <para><b>The composer is not touched</b>, unlike the Enter route's: this message never came
        /// from it, and whatever is in the box is a draft the user is still writing.</para>
        /// </remarks>
        internal Task SendReleasedAsync(
            string text, string? preamble, string? deliveryNote,
            IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts) =>
            DecideAndSendAsync(ParkedMessage.FromTray(text, preamble, deliveryNote, attachments, contexts));

        private async Task DecideAndSendAsync(ParkedMessage message)
        {
            // First continuation of a restored conversation: decide how to reconnect. Prompt only when
            // the choice is worth it — a big full-vs-summary tradeoff, or to make a cross-backend
            // continuation explicit. Small + natively-resumable just resumes the full context silently.
            // The rule itself lives in ResumeDecider (pure + unit-tested); we own only the VM state.
            // A session that was opened and never prompted still leaves this the first continuation -
            // INCLUDING one whose refused reload the user backed out of. That send used to be routed straight
            // back to the refusal, skipping the decider, so the only question it could be asked was the one it
            // had already declined. Every send decides from scratch now: the refusal is a fact
            // on the session, and it is consulted where the answer would reload, not before the question.
            var firstContinuation = !_lifetime.CanTakeNextPrompt && _resumeStrategy == ResumeStrategy.Fresh;
            var resumeDecision = ResumeDecider.Decide(_host.Persisted, firstContinuation, CanResumeFull());
            if (resumeDecision is ResumeDecision.PromptFullOrSummary or ResumeDecision.PromptSummaryOnly)
            {
                // Defer the send behind the choice banner; ChooseResume re-issues it once picked.
                var allowFull = resumeDecision == ResumeDecision.PromptFullOrSummary;
                _parked = message;
                if (message.StillInTheComposer)
                    InputText = string.Empty;
                // The send exists from here, though nothing is busy and no bubble is drawn (user decision,
                // 2026-09-16: the bubble marks a send that has BEGUN, and this one is still being decided).
                Phase = SendPhase.Deciding;
                ShowResumeChoice(canFull: allowFull, large: allowFull);
                return;
            }

            if (resumeDecision == ResumeDecision.SilentFull)
                _resumeStrategy = ResumeStrategy.Full; // small + resumable → silent full-context resume

            // The box is emptied HERE and at the park above, which are the two points a message stops
            // being the composer's - never at this method's entry. Moved up there it would run before the
            // decision, and a throw anywhere in between would leave the user with an empty box and no
            // give-back, this being ahead of every route that has one: a window of two property reads and
            // a pure call, but a window that did not exist before.
            if (message.StillInTheComposer)
                InputText = string.Empty;
            await SendAfterRootCheckAsync(message).ConfigureAwait(true);
        }

        // Shows the resume-choice banner at send-time (the deferred send is stashed in _parked):
        // full-context vs summary for a big natively-resumable conversation, or a summary-only
        // confirmation when full reload can't apply (a different/unavailable backend).
        private void ShowResumeChoice(bool canFull, bool large)
        {
            var persisted = _host.Persisted;

            // Surface how big the conversation is so the user can weigh the full-vs-summary cost
            // (issue #15): a full reload of a large transcript is the expensive branch.
            var size = persisted is not null
                ? " (about " + FormatContextSize(ResumeDecider.TranscriptCharCount(persisted)) + " of history)"
                : string.Empty;

            var detail = !canFull
                ? "This conversation was on a different backend, so it can't reload its full context here" + size + " — it'll continue from a summary sent to the current agent (counts toward usage)."
                : "This is a long conversation" + size + ". Resuming its full context reloads every earlier message into the agent (counts toward usage); resuming from a summary sends a short recap instead.";

            // A recap that failed on this session is not offered again while it lives (user decision,
            // 2026-09-16), and the banner says so rather than quietly drawing one button fewer.
            var canRecap = !_lifetime.RecapFailedThisSession;
            if (!canRecap)
                detail += " " + ResumeChoiceViewModel.RecapWithheld;

            // With neither a full reload nor a recap on offer this banner drew Cancel alone, and every later
            // send landed on it again: the conversation could not be sent to at all.
            // A cross-backend conversation whose recap has failed is exactly that shape. Starting
            // a new conversation is the answer that stays true there, so it appears only where it is needed
            // rather than as a fourth button on every resume.
            var needsFresh = !canFull && !canRecap;
            if (needsFresh)
                detail += " Starting a new conversation sends this message there instead, and this one stays "
                          + "in the history picker.";

            PendingResume = ResumeChoiceViewModel.Choice(
                detail, allowFull: canFull, allowSummary: canRecap, allowFresh: needsFresh,
                summaryRecommended: large || !canFull,
                resumeFull: () => ChooseResume(ResumeStrategy.Full),
                resumeSummary: () => ChooseResume(ResumeStrategy.Summary),
                resumeFresh: () =>
                {
                    // Clearing the transcript ends the parked send along with the banner it sits on, so
                    // the message is carried across by hand. And nulling the conversation is what stops the
                    // decider handing back this very banner: with no prior transcript it proceeds fresh.
                    // The TRAY is ReplaceConversation's: this is New on the user's behalf, under a message
                    // they had already queued (scoped review, 2026-09-18).
                    //
                    // Taken out of the field BEFORE the clear, not merely read: the ending now hands a
                    // parked message back to the composer, and this one is going straight out into
                    // the new conversation instead. Left in the field it would do both.
                    var parked = _parked;
                    _parked = null;
                    ReplaceConversation(() => _host.StartNewConversation(
                        new NoticeItemViewModel(StartedOverNotice(persisted?.Title))));
                    _parked = parked;
                    ChooseResume(ResumeStrategy.Fresh);
                },
                cancel: CancelResume);
        }

        // Renders a transcript character count as a human-friendly size plus a rough token estimate
        // (~4 chars/token, the same ratio the SummaryThreshold comment uses). e.g. 12000 chars ->
        // "12.0K characters (~3.0K tokens)".
        private static string FormatContextSize(int chars)
        {
            static string Scale(int value, string unit)
                => value >= 1000
                    ? (value / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + "K " + unit
                    : value.ToString(CultureInfo.CurrentCulture) + " " + unit;

            return Scale(chars, "characters") + " (~" + Scale(chars / 4, "tokens") + ")";
        }

        // The user picked a resume strategy in the banner: dismiss it and re-issue the deferred send.
        private void ChooseResume(ResumeStrategy strategy)
        {
            _resumeStrategy = strategy;
            // Answered: the send leaves this phase, and whatever it does next sets its own.
            Phase = null;
            PendingResume = null;
            var parked = _parked;
            _parked = null;
            if (parked is not null)
                // A typed message's attachments were deliberately left in the composer while the banner
                // was up, so that backing out returns the whole message — text and pictures — rather
                // than half of it. Taken at the moment the send actually goes, which may be one more
                // banner away: a full reload chosen here still has the moved-root question ahead of it.
                // A released batch brought its own, and they go with it.
                _ = SendAfterRootCheckAsync(parked);
        }

        /// <summary>
        /// Backed out of the resume: dismiss the banner and put the pending message back in the input so
        /// the user can edit or resend it. The send is over, taken back before it began, with nothing
        /// started and nothing recorded.
        /// </summary>
        /// <remarks>
        /// <para><b>The resume it was asked about goes with it</b>, which is what
        /// <see cref="BackOutOfResume"/> already does for the two banners past <see cref="Begin"/> -
        /// the same rule one step earlier. Left standing, the answer the user had just walked
        /// away from decided the NEXT send: it is no longer the first continuation, so nothing asks
        /// again, and the full reload declined here ran silently on whatever was sent next.</para>
        /// <para><b>And the tray is held</b>, as it is when the other two banners are cancelled. No
        /// turn-end release is scheduled on this route - the turn runner never ran - so nothing would
        /// have sent the follow-ups anyway; what the hold buys is that the tray SAYS which gesture
        /// happened. Left alone it read "the agent isn't working", which is true and is not what the
        /// user just did, and naming a cause it had not checked is what issue #253 cost. Caught by the
        /// hosted --smoke run, not by the suite: only a drawn tray shows the sentence.</para>
        /// <para><b>The body is deliberately comment-free</b>, the reasoning having moved here: every
        /// statement in it is then a unique CODE anchor, which is what an injection proving one of them
        /// needs. A comment anchor dies when someone rewords a sentence, and nothing reports it.</para>
        /// </remarks>
        private void CancelResume()
        {
            Phase = null;
            PendingResume = null;
            _resumeStrategy = ResumeStrategy.Fresh;
            _host.HoldTray(TrayHold.BackedOut);
            var parked = _parked;
            _parked = null;
            GiveBackParked(parked);
        }

        /// <summary>
        /// The last question before a send commits: whether a full reload would run in a different
        /// directory from the one the conversation was made in (issue #185). Asked here, AFTER the
        /// resume strategy is settled and BEFORE the attachments leave the composer, so a parked send
        /// can be backed out whole — the same seam the resume-choice banner uses, and both routes to
        /// a full reload (the silent one and the banner's) pass through it.
        /// </summary>
        private async Task SendAfterRootCheckAsync(ParkedMessage message)
        {
            // Across the whole check, not only while its banner is up: the query is an engine round-trip with
            // nothing busy, so a send that held nothing here would leave the pane open to a New or a picker
            // change in the gap and come back to a conversation that had been replaced under it.
            Phase = SendPhase.CheckingRoot;
            if (!await AskIfRootMovedAsync(message).ConfigureAwait(true))
                return; // parked on the moved-root banner; its answer re-enters through ChooseResume

            // The framing goes with the message, because framing belongs to the gesture: a released batch keeps the preamble
            // and the note its gesture gave it, however many banners stood between. And the chips are
            // the message's own where it brought them, and the composer's where it did not - taken HERE,
            // at the moment the send actually goes, so a back-out one banner earlier left them in place.
            await _host.SendCoreAsync(
                message.Text, message.Preamble, message.DeliveryNote,
                attachments: message.StillInTheComposer ? _host.TakePendingAttachments() : message.Attachments,
                contexts: message.StillInTheComposer ? _host.TakePendingContexts() : message.Contexts)
                .ConfigureAwait(true);
        }

        /// <summary>
        /// Asks what to do when the conversation being resumed belongs to a working directory the
        /// agent would no longer run in (issues #59, #185), and parks the send until answered. True
        /// when there was nothing to ask.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The first cut (#183) made this a unilateral fork; the fork is now one of three answers.</b> It
        /// opened a new conversation on every mismatch, on the strength of one F5 observation: Kiro
        /// answering a cross-root load with an empty session wearing the requested id. Re-measured
        /// 2026-09-12 (<c>Console resume-cross-root</c>), both Kiro engines CARRY a conversation across
        /// roots and Claude refuses cleanly — the empty session was v3's answer to an id it no longer
        /// held, not to the root (<c>Console resume-unknown-id</c>; the post-hoc check in
        /// <c>ChatViewModel.ReloadFailure</c> is what covers that now). So the fork was forking conversations
        /// that would have resumed, and the user who opened THAT conversation on purpose got a new one.
        /// </para>
        /// <para>
        /// The three answers: <b>open it where its history lives</b> — the request pins the recorded
        /// directory, so the reload runs in the one place a reload works from, at full fidelity, and
        /// the choice is remembered on the conversation; <b>resume from a summary</b> — a recap here,
        /// re-rooted by issue #184, the only answer that still works once the original directory is
        /// gone; and <b>start a new conversation</b> — the #183 fork, unchanged, and now the answer every
        /// resume banner's fresh button gives. A full reload HERE is deliberately
        /// not offered: on Claude it fails, and on Kiro it succeeds into a session whose own history was
        /// written against the other directory, which no host-side fix can annotate.
        /// </para>
        /// <para>
        /// <b>It must run before the message is recorded</b>, which is why the mismatch is a QUERY
        /// rather than something learned from the start response, and why this banner has a Cancel:
        /// nothing has been committed yet. (The other resume banners have one too now, for the same
        /// reason - a first send into a reopened conversation records its message only once answered.) Discovering it afterwards
        /// would mean deleting a recorded message to reach a state the user never asked for - issue
        /// #84's argument against unwinding a send, which applies with equal force in reverse.
        /// </para>
        /// </remarks>
        private async Task<bool> AskIfRootMovedAsync(ParkedMessage message)
        {
            // Only when a FULL reload is what would have happened. A summary resume needs no
            // backend session, so it can legitimately continue here (its paths are re-rooted by
            // issue #184); and a conversation that was never resumable is issue #84's existing
            // start-fresh behaviour, not this.
            if (_lifetime.CanTakeNextPrompt || _resumeStrategy != ResumeStrategy.Full)
                return true;
            if (_host.Persisted is not { ConversationId: { Length: > 0 } } persisted)
                return true;
            if (persisted.AgentWorkingDirectory is not { Length: > 0 } conversationRoot)
                return true;
            // Answered on this open: the click re-enters through ChooseResume and lands here again,
            // and BuildStartRequest pins from the same flag. Without this the banner asked forever.
            if (persisted.PinWorkingDirectoryThisOpen)
                return true;
            if (_engine is not IAgentRootQuery query)
                return true; // Cannot ask: the engine's own guard still refuses, one step later.

            ResolveAgentRootResponse resolved;
            try
            {
                resolved = await query.ResolveAgentRootAsync(new ResolveAgentRootRequest(
                    _host.SelectedProvider?.Id ?? _host.SessionRequest.ProviderId,
                    _host.SessionRequest.WorkspaceRootPath)).ConfigureAwait(true);
            }
            catch
            {
                return true; // Best effort. A failed question must not stop the user sending.
            }

            if (ResumeRootGuard.Refuse(conversationRoot, resolved.Root) is null)
                return true;

            // Says what is in effect rather than what to do about it: the remedy is directional and
            // we do not always know which way. Removing agentWorkspaceRoot is right when the
            // conversation predates an override now in force, and exactly backwards when the
            // conversation was created UNDER one since removed. The reason string covers both.
            var why = string.IsNullOrWhiteSpace(resolved.Reason)
                ? string.Empty
                : $" ({resolved.Reason})";
            var detail =
                $"“{persisted.Title}” ran in {conversationRoot}, and here the agent would run in "
                + $"{resolved.Root}{why}. Its messages can only be reloaded where they were made. "
                + "Opening it there keeps the full context, for this conversation only; resuming from "
                + "a summary sends a short recap here instead (counts toward usage).";

            _parked = message;
            PendingResume = ResumeChoiceViewModel.RootMoved(
                detail, allowSummary: !_lifetime.RecapFailedThisSession,
                openWhereItLives: () =>
                {
                    // On the loaded conversation object, never in a field of ours and never on disk:
                    // a different conversation has its own answer, and a reopen asks again. It is
                    // for this OPEN, not forever - a remembered pin left no way back to the summary
                    // or fresh routes short of editing the session file.
                    persisted.PinWorkingDirectoryThisOpen = true;
                    ChooseResume(ResumeStrategy.Full);
                },
                resumeSummary: () => ChooseResume(ResumeStrategy.Summary),
                startFresh: () =>
                {
                    // The fork clears the transcript, and clearing ends a parked send along with the
                    // banner it was parked on - so the message is carried across by hand. The TRAY is
                    // ReplaceConversation's, and 1.0.0 shipped this route destroying it silently.
                    // Taken out of the field before the clear, for the reason the sibling site gives:
                    // an ending hands a parked message back to the composer, and this one is being sent.
                    var parked = _parked;
                    _parked = null;
                    // New's own path, with this route's own notice - the same call the resume banners'
                    // fresh answer makes. It used to be a private copy of New's steps, and a copy is
                    // what drifts: 1.0.0 shipped this route destroying the tray, which New's route had
                    // never done. The copy also never warm-started, and MEASURED that costs nothing
                    // either way - the resend follows within the click and takes the warm session
                    // instead of opening one, so the route starts exactly one session on both shapes
                    // (putting the copy back leaves the whole suite green). A difference a copy
                    // accumulates unnoticed is the reason to remove it, not a defect being fixed.
                    ReplaceConversation(() => _host.StartNewConversation(new NoticeItemViewModel(
                        ForkNotice(persisted.Title, conversationRoot, resolved), NoticeKind.Error)));
                    _parked = parked;
                    ChooseResume(ResumeStrategy.Fresh);
                },
                cancel: CancelResume);
            return false;
        }

        /// <summary>
        /// What the moved-root banner's "Start new conversation" says - #183's fork, now one of that
        /// banner's three answers rather than the only outcome. The conversation being resumed is left
        /// untouched in the history picker.
        /// </summary>
        /// <remarks>
        /// <para><b>It names the two directories</b>, which is what keeps it apart from
        /// <see cref="StartedOverNotice"/>: the other three fresh answers describe a transient failure
        /// the banner has just explained, and this one describes a durable fact about the environment
        /// that the sibling sentence cannot say.</para>
        /// <para><b>Deliberately NOT persisted</b>, like every notice: it explains a TRANSITION, and the
        /// conversation it lands in is an honest one from its first message.</para>
        /// </remarks>
        internal static string ForkNotice(string previousTitle, string conversationRoot, ResolveAgentRootResponse resolved)
        {
            var why = string.IsNullOrWhiteSpace(resolved.Reason)
                ? string.Empty
                : $" ({resolved.Reason})";

            return $"Started a new conversation. “{previousTitle}” ran in {conversationRoot}, and this "
                + $"session runs in {resolved.Root}{why} - so its earlier messages can't be reloaded "
                + "here. It is unchanged in the history picker.";
        }

        // ---- the send, up to its prompt ---------------------------------------------------------------

        /// <summary>One send between <see cref="Begin"/> and its prompt.</summary>
        internal sealed class OutgoingSend
        {
            public OutgoingSend(
                MessageItemViewModel userMessage, string text, string? preamble,
                IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts,
                bool starting, string? priorTranscript, string? priorRoot,
                PersistedSession? recordInto, int? recordAt, string localId)
            {
                LocalId = localId;
                UserMessage = userMessage;
                Text = text;
                Preamble = preamble;
                Attachments = attachments;
                Contexts = contexts;
                Starting = starting;
                PriorTranscript = priorTranscript;
                PriorRoot = priorRoot;
                RecordInto = recordInto;
                RecordAt = recordAt;
            }

            public MessageItemViewModel UserMessage { get; }

            /// <summary>
            /// The LOCAL id of the conversation this send belongs to, from the moment it began: the one on
            /// screen, or for a brand-new conversation an id allocated here, which the conversation takes when its
            /// first message is recorded. Attachments are filed under it.
            /// </summary>
            public string LocalId { get; }

            public string Text { get; }

            public string? Preamble { get; }

            public IReadOnlyList<AttachmentViewModel> Attachments { get; }

            public IReadOnlyList<ContextItemViewModel> Contexts { get; }

            /// <summary>No session had been adopted when the send began.</summary>
            public bool Starting { get; }

            /// <summary>The prior transcript, snapshotted before this message is recorded, so a summary excludes it.</summary>
            public string? PriorTranscript { get; }

            /// <summary>The directory the prior transcript's relative paths were written against (issue #184).</summary>
            public string? PriorRoot { get; }

            /// <summary>The conversation the message was shown in, and where in its log it belongs.</summary>
            public PersistedSession? RecordInto { get; }

            public int? RecordAt { get; }

            public bool Recorded { get; set; }

            /// <summary>
            /// A resume banner was answered "Start new conversation": carried out by the turn runner's
            /// finally, and only if this send is still the chat's (see <see cref="StartOverInNewConversation"/>).
            /// </summary>
            /// <remarks>
            /// A flag rather than the reason it used to carry. The notice it produces names no reason at all
            /// (user decision, 2026-09-19): the banner the user answered a click earlier stated it in full,
            /// and the notice is display-only, so it survives nothing and repeats what is still on screen.
            /// </remarks>
            public bool StartOverRequested { get; set; }

            /// <summary>
            /// The PROGRESS lines this send has written - "Summarizing…", "Resuming…" - which describe an
            /// attempt while it is still running, so its ending removes them: a send that never prompted
            /// must not leave the transcript saying the pane is summarizing, with no message under it.
            /// </summary>
            /// <remarks>
            /// <b>Only the progress lines.</b> A line claiming a DELIVERY is never here, because it is not
            /// written until there is one (see <see cref="CommitConversation"/>); and a line recording what
            /// HAPPENED is deliberately not here either - "Could not summarize" is the one place its reason
            /// is shown, and the explanation for what the user is looking at once the message is back in the
            /// composer.
            /// </remarks>
            private readonly List<NoticeItemViewModel> _resumeNotices = new List<NoticeItemViewModel>();

            public void NoteResumeNotice(NoticeItemViewModel notice) => _resumeNotices.Add(notice);

            /// <summary>Withdrawn while the send goes on, so the ending must not remove it a second time.</summary>
            public void ForgetResumeNotice(NoticeItemViewModel notice) => _resumeNotices.Remove(notice);

            /// <summary>Hands them over and forgets them, so an ending cannot remove one twice.</summary>
            public IReadOnlyList<NoticeItemViewModel> TakeResumeNotices()
            {
                var taken = _resumeNotices.ToArray();
                _resumeNotices.Clear();
                return taken;
            }

            /// <summary>
            /// The resume this send actually achieved, said in the transcript at its COMMIT and never
            /// before. Both lines claim a DELIVERY - "a condensed recap was sent" - so until the prompt
            /// goes there is nothing true to say, and saying it early meant a send that was then stopped or
            /// retired left the claim standing over a transcript with no message under it.
            /// </summary>
            public bool ResumedFromSummary { get; set; }

            /// <summary>The wait for our IDE tools timed out, so the message goes out with them unconfirmed.</summary>
            public bool IdeToolsNotConfirmed { get; set; }

            /// <summary>The refused reload this send fell through (issue #268), and whether a recap went with it.</summary>
            public string? ReloadFallbackReason { get; set; }

            public bool ReloadFallbackUsedRecap { get; set; }

            /// <summary>The session this send started and adopted, whose facts are applied to the conversation at its commit.</summary>
            public StartSessionResponse? Adopted { get; set; }

            /// <summary>The session the conversation now lives on, whose id and provider are stamped at the commit.</summary>
            public StartSessionResponse? Live { get; set; }

            /// <summary>
            /// The user backed out: the message goes back to the composer when the turn runner ends the send, in
            /// the same step that frees the pane, not a dispatcher post earlier.
            /// </summary>
            /// <remarks>An INTENT, and <see cref="GivenBack"/> is the FACT. They are not the same question.</remarks>
            public bool GiveBackPending { get; set; }

            /// <summary>
            /// This send's message has been handed back to the composer, so no other route may hand it back
            /// again (<see cref="PromptDelivery.GiveBackOnce"/>).
            /// </summary>
            /// <remarks>
            /// <b>Four routes reach one send, and two pairs of them can both fire.</b> Everywhere else the
            /// ending also REPLACES the conversation, and the epoch moving is what stops the second one - so
            /// "the ending gives back, and that is enough" is true exactly where it is not needed. An engine
            /// EXIT is the one in-app ending that leaves the transcript up, moves no epoch, and then faults
            /// the send's own call into the turn runner's catch; and a back-out marks the message for a
            /// give-back one or two dispatcher posts before the runner performs it, which a conversation
            /// change can land inside. <b>A doubled give-back is not cosmetic</b>: the text is prepended to
            /// itself and every chip is inserted a second time, so the next send carries the words and the
            /// image twice over, with nothing on screen saying where the copy came from.
            /// </remarks>
            public bool GivenBack { get; set; }
        }

        /// <summary>
        /// A send's first, synchronous half: the message on screen, what it snapshots before anything can
        /// replace the conversation, and - where nothing is left to ask - its record in the log.
        /// </summary>
        internal OutgoingSend Begin(
            string text, string? preamble, string? deliveryNote,
            IReadOnlyList<AttachmentViewModel>? attachments, IReadOnlyList<ContextItemViewModel>? contexts)
        {
            attachments ??= Array.Empty<AttachmentViewModel>();
            contexts ??= Array.Empty<ContextItemViewModel>();

            // BEFORE anything is recorded. See OpenNewConversationIfRootMovedAsync: once the
            // message is in the transcript it is too late to decide it belongs in a different one.
            // That question is now asked by AskIfRootMovedAsync, from SendAfterRootCheckAsync, so a
            // send that reaches here has already answered it - or never needed to.

            // Shown immediately for responsiveness; resume notices below insert *above* it (via its index)
            // so the transcript reads "...earlier messages... [resume notice] [this new message]".
            // The transcript and the saved log get the user's OWN words. The framing goes on the wire
            // only — it is ours, not theirs, and must never come back as something they appear to have
            // written (on a resume it would then be indistinguishable from their message).
            var userMessage = new MessageItemViewModel(
                MessageRole.User, text, deliveryNote, attachments, contexts);
            Items.Add(userMessage);
            _host.ForgetStreamingAssistant();

            var persisted = _host.Persisted;
            // Whether the send has a session to open: nothing has been prompted on the pane's. A session
            // that was opened and backed out of counts - it is Open, never Prompted - so this send goes down
            // the same route, where a refusal the session remembers stands in for the start.
            // A superseded session is prompted and still routed, but runs the backend the picker left: it starts.
            var starting = !_lifetime.CanTakeNextPrompt;
            // A reopened conversation may still be asked how to resume (issues #84, #268). Nothing is recorded
            // by any send until its prompt goes out (CommitConversation), so backing out leaves nothing in
            // the saved log looking sent.
            var askFirst = starting && persisted is not null;
            // Snapshot the prior transcript before this message is recorded, so a summary excludes it.
            var priorTranscript = askFirst && persisted is not null ? ChatViewModel.TranscriptText(persisted) : null;
            // And the directory its relative paths were written against, snapshotted WITH the
            // transcript it describes (issue #184) — the banner waits below can replace _persisted
            // before the summary is consumed, and by then ResumeWorkingDirectory() answers for the
            // wrong conversation.
            var priorRoot = priorTranscript is not null ? _host.ResumeWorkingDirectory() : null;

            // Where the message belongs in the log: here, beside what is on screen (see RecordUser's
            // insertAt). Only into the conversation it was shown in.
            var send = new OutgoingSend(
                userMessage, text, preamble, attachments, contexts,
                starting, priorTranscript, priorRoot,
                recordInto: persisted, recordAt: persisted?.Log.Count,
                // A conversation that does not exist yet still has an identity from here, so what the send
                // files before it is recorded belongs to it. It comes into existence at the commit: a send
                // mutates nothing on the conversation before then.
                localId: persisted?.Id ?? Guid.NewGuid().ToString("N"));

            return send;
        }

        /// <summary>
        /// Records the send's message into the conversation it was shown in, once - and, where this send
        /// opened the session the conversation now lives on, stamps that session on in the same write.
        /// </summary>
        /// <remarks>
        /// <b>One write, because the intermediate state is not a state a restore can read.</b> The stamp
        /// used to be a second save a few statements later, and in between the file held the first
        /// message with no conversation id: reopened from there the conversation has a transcript and
        /// nothing behind it, and the reload the user chose is gone. A new conversation is the only case
        /// that could show it, its id not existing until the session starts.
        /// </remarks>
        internal void RecordOnce(OutgoingSend send, StartSessionResponse? live = null)
        {
            if (send.Recorded)
                return;
            send.Recorded = true;
            _host.RecordUser(send.Text, send.Attachments, send.Contexts,
                insertAt: ReferenceEquals(_host.Persisted, send.RecordInto) ? send.RecordAt : null,
                localId: send.LocalId,
                live: live);
        }

        /// <summary>
        /// What a send does to its conversation, run by <see cref="SessionLifetime.Commit"/> immediately before
        /// the prompt goes out and never earlier, in one synchronous block with it: the adopted session's working directory and project
        /// settings notices, the message recorded, and the session's id and provider stamped. Returns the
        /// conversation, which exists from here even when the send began a brand-new one.
        /// </summary>
        /// <remarks>
        /// <para><b>Why here and not where each was learned.</b> The working directory was written onto the
        /// conversation at adoption, before a refusal was known, so a recap after backing out named the refused
        /// session's root as the transcript's origin. The message was recorded before the wait for the
        /// IDE tools, so a workspace move during it left a never-sent message saved, and one post before
        /// the prompt, so a move or Stop landing in that post acted on a message already written.</para>
        /// <para>The notices are still inserted above the message, which has been on screen since
        /// <see cref="Begin"/>; only the moment moved.</para>
        /// <para><b>"When it is true" is "when the delivery is all but certain", and the gap is worth
        /// naming.</b> This runs inside <see cref="SessionLifetime.Commit"/>, which re-checks the token, and
        /// the turn runner issues <c>PromptAsync</c> straight after with no await between - so no Stop,
        /// Cancel, conversation change or engine-exit report can interleave, each needing a dispatcher turn.
        /// What remains is `ResolveAttachments` throwing, and the prompt failing before it reaches the wire.
        /// In both the turn exists, so the message is NOT given back, and these lines stand beside an
        /// explicit error or exit card rather than over an empty transcript. That is strictly weaker than
        /// the defect this removed, and it is why the gap is accepted rather than chased.</para>
        /// </remarks>
        internal PersistedSession? CommitConversation(OutgoingSend send)
        {
            // The resume this send achieved, said only now that it HAS - the same rule, and the same block,
            // that already holds the working directory and the project settings.
            //
            // ORDER, since each of these inserts immediately above the message and so the last one in sits
            // closest to it: the two resume lines keep the places they had when they were written earlier.
            // The IDE-tools line does NOT - it used to be written during the wait, which put it above
            // "Resumed from a summary", and it now goes last. That is the chronological order of the facts
            // they report (what was resumed, then what the session had when the message went), so it is
            // kept rather than restored.
            if (send.ResumedFromSummary)
                InsertAboveMessage(send, new NoticeItemViewModel(
                    "Resumed from a summary — a condensed recap was sent instead of the full history."));
            if (send.ReloadFallbackReason is { } fallbackReason)
                _host.ReportResumeFallback(
                    fallbackReason, Items.IndexOf(send.UserMessage), send.ReloadFallbackUsedRecap);
            if (send.IdeToolsNotConfirmed)
                InsertAboveMessage(send, new NoticeItemViewModel(ChatViewModel.IdeToolsNotConfirmedNotice));

            if (send.Adopted is { } started)
            {
                // Where the agent is actually running (issue #54). Not when a session is merely pre-warmed - a
                // warm session the user never sends to must not announce anything - and not when it is adopted:
                // a session this send backs out of is not where the conversation runs.
                _host.ApplyAgentWorkingDirectory(started, Items.IndexOf(send.UserMessage));
                _host.ReportProjectSettings(started, Items.IndexOf(send.UserMessage));
            }

            // The conversation now lives on the current backend, so its (new) id and provider ride the
            // record's own write rather than a second one behind it - see RecordOnce for what the gap
            // between the two writes left on disk. Only where this send started the session: a send into
            // a conversation already running on one has nothing to stamp.
            RecordOnce(send, send.Starting ? send.Live : null);

            if (send.Starting)
            {
                PendingResume = null;
                _resumeStrategy = ResumeStrategy.Fresh;
            }

            return _host.Persisted;
        }

        /// <summary>
        /// Says in the transcript what this send is doing about the resume - above its message, which has
        /// been on screen since <see cref="Begin"/> - and hands the line to the send, so the send's ending
        /// takes it away again.
        /// </summary>
        private NoticeItemViewModel ShowResumeNotice(OutgoingSend send, string text)
        {
            var notice = InsertAboveMessage(send, new NoticeItemViewModel(text));
            send.NoteResumeNotice(notice);
            return notice;
        }

        /// <summary>
        /// Puts a notice immediately above this send's message, or at the end if the message is not on the
        /// transcript. The fallback is for consistency with the host's own inserts rather than for a route
        /// anybody has found: a negative index is what <c>IndexOf</c> answers for a message that is gone,
        /// and <c>Insert</c> throws on it where every neighbour degrades.
        /// </summary>
        private NoticeItemViewModel InsertAboveMessage(OutgoingSend send, NoticeItemViewModel notice)
        {
            var at = Items.IndexOf(send.UserMessage);
            Items.Insert(at < 0 ? Items.Count : at, notice);
            return notice;
        }

        /// <summary>
        /// Takes one back while the send goes ON: a claim about something that turned out not to have
        /// happened. Distinct from an ending, which takes them all.
        /// </summary>
        private void WithdrawResumeNotice(OutgoingSend send, NoticeItemViewModel notice)
        {
            Items.Remove(notice);
            send.ForgetResumeNotice(notice);
        }

        /// <summary>
        /// What every ending owes the transcript: the PROGRESS lines this send wrote. Left standing they
        /// describe a send that no longer exists - "Summarizing…" over a transcript with no message under
        /// it.
        /// </summary>
        /// <remarks>
        /// Called by every route that ends a send and by nothing else, so it is safe on a send that has
        /// already ended: <see cref="OutgoingSend.TakeResumeNotices"/> hands them over once.
        /// <para><b>Nothing here claims a DELIVERY, which is why this list is short.</b> A progress line
        /// has to precede what it describes - being on screen while it happens is its whole job - so it can
        /// only be taken back. A line saying what the message WENT OUT WITH has no such excuse and is not
        /// written until it goes (<see cref="CommitConversation"/>), which removes the need to withdraw it
        /// at all. A line recording what HAPPENED - "Could not summarize" - stays either way.</para>
        /// </remarks>
        private void DropResumeNotices(OutgoingSend? send)
        {
            if (send is null)
                return;
            foreach (var notice in send.TakeResumeNotices())
                Items.Remove(notice);
        }

        /// <summary>A send the user backed out of ends here: its message goes back to the composer.</summary>
        internal void GiveBackIfBackedOut(OutgoingSend send)
        {
            if (!send.GiveBackPending)
                return;
            send.GiveBackPending = false;
            GiveBackOnce(send);
        }

        /// <summary>
        /// Hands this send's message back to the composer, and never twice, whichever route arrives first
        /// (<see cref="OutgoingSend.GivenBack"/>). The ONE entry point for a send's give-back: the ending,
        /// Stop, the back-out the turn runner performs, and the turn runner's own exit catch.
        /// </summary>
        /// <remarks>
        /// <b>The guard is a fact on the SEND, not the epoch.</b> Everywhere the ending also replaces the
        /// conversation the epoch moves and the second route is refused by that - so an argument of the form
        /// "the conversation is being left" holds exactly where it is not needed, and fails on the two
        /// routes where it is: the engine exit, which leaves the transcript up and then faults the send's
        /// own call into the catch, and a back-out, whose give-back the runner performs a dispatcher post or
        /// two after the mark is set.
        /// </remarks>
        internal void GiveBackOnce(OutgoingSend send)
        {
            if (send.GivenBack)
                return;
            send.GivenBack = true;
            GiveBackUnsentMessage(send.UserMessage, send.Text, send.Attachments, send.Contexts);
        }

        /// <summary>
        /// A send's second half, between the turn runner taking the wire and the prompt going out: the
        /// resume the send carries, the session it starts or reuses, and every banner that can ask about
        /// either. Returns the prompt to send, or null where the send ends here. Changes nothing on the
        /// conversation: that is <see cref="CommitConversation"/>'s, at the prompt.
        /// </summary>
        /// <param name="send">The send <see cref="Begin"/> began.</param>
        /// <param name="epoch">The token the send was begun under.</param>
        internal async Task<string?> PrepareAsync(OutgoingSend send, SessionToken epoch)
        {
            _pending = send;
            try
            {
                return await PrepareCoreAsync(send, epoch).ConfigureAwait(true);
            }
            finally
            {
                // ONE discriminator for both halves, and it is ownership of the field rather than the epoch.
                // The send stops being pending here - it is about to prompt, or it ended - but only if it is
                // still the pending one: a conversation change ended it and cleared the phase then, and
                // a send begun since owns the field now.
                //
                // The epoch cannot tell those apart, because STOP ends a send without moving it: it clears
                // the field, hands the message back and frees the pane, all within the same conversation. So
                // a stopped send returning twenty seconds later found IsCurrent still true and cleared the
                // PHASE of the send begun after it, leaving that send's banner on screen with nothing holding
                // the tray or the pane's controls behind it (scoped review, 2026-09-17). Ownership
                // covers every case the epoch check covered and that one as well.
                if (ReferenceEquals(_pending, send))
                {
                    _pending = null;
                    Phase = null;
                }
            }
        }

        private async Task<string?> PrepareCoreAsync(OutgoingSend send, SessionToken epoch)
        {
            // EngineGone: the send fails at once with the exit, whatever route it came by (Enter, a tray
            // release, a banner's answer) and whether or not its pane needs a session started, so nothing below
            // reaches the dead engine. The turn runner gives the message back (user decision, 2026-09-15).
            if (_lifetime.EngineGone is { } gone)
                throw new EngineExitedException(gone);

            var text = send.Text;
            var preamble = send.Preamble;
            var contexts = send.Contexts;
            var userMessage = send.UserMessage;
            var starting = send.Starting;
            var priorTranscript = send.PriorTranscript;
            var priorRoot = send.PriorRoot;

            // Same rule as the summary block below: context for the agent only, never displayed and
            // never recorded as the user's words. The attached IDE context is the exception that
            // proves it - the user DID choose to hand that over, which is why it is recorded and
            // shown, and only the tags around it are ours.
            var contextBlocks = ChatViewModel.RenderContextBlocks(contexts);
            var outgoing = ChatViewModel.ComposeOutgoing(contextBlocks, preamble, text);
            if (starting)
            {
                string? summaryBlock = null;
                // The session this conversation now lives on, whose id is recorded below.
                StartSessionResponse? live = null;

                if (starting)
                {
                    string? resumeId = null;
                    // Whether a recap has failed is no longer tracked here. It is a fact on the SESSION
                    // (SessionLifetime.RecapFailedThisSession), set wherever a recap fails and read by
                    // every banner that could offer one. As a local it only ever covered the two failures
                    // happening in sequence within ONE send — summarize fails, the user picks the full
                    // reload instead, the backend refuses that too — and said nothing at all on the next
                    // send, which is how the Choice banner came to re-offer a recap that had just failed.
                    // The "this is being reloaded" notice, kept so it can be withdrawn if it turns out
                    // not to be true. It is written before the backend has been asked — it has to be,
                    // being what the transcript says while the start is in flight — and a refusal
                    // leaves it standing over a banner that flatly contradicts it, at the moment the
                    // user is reading both to decide. Display-only, like the session-opening notices
                    // DropSessionOpeningRows withdraws, so nothing outlives its removal.
                    NoticeItemViewModel? reloadingNotice = null;

                    if (_resumeStrategy == ResumeStrategy.Full && CanResumeFull())
                    {
                        resumeId = _host.Persisted!.ConversationId;
                        reloadingNotice = ShowResumeNotice(send,
                            "Resuming this conversation — its earlier messages are reloaded into the agent's context (they count toward usage).");
                    }
                    else if (_resumeStrategy == ResumeStrategy.Summary && !string.IsNullOrWhiteSpace(priorTranscript))
                    {
                        ShowResumeNotice(send, "Summarizing this conversation to resume from a recap…");
                        Phase = SendPhase.Summarizing;
                        var recap = await SummarizeTranscriptAsync(priorTranscript!, priorRoot).ConfigureAwait(true);
                        if (!_lifetime.IsCurrent(epoch) || !StillPending(send))
                            return null;
                        if (recap.Summary is { } summary)
                        {
                            summaryBlock = ChatViewModel.BuildSummaryBlock(summary, priorRoot);
                            send.ResumedFromSummary = true;
                        }
                        else
                        {
                            _lifetime.NoteRecapFailed();

                            // The user asked for a summarized resume and it did not happen. Proceeding
                            // here is what issue #84 is about: the send fell through to a fresh session
                            // carrying no history, which is one of the two real answers — but it was
                            // never offered, only taken. So ask, with the other one beside it.
                            Phase = SendPhase.AskingRecapFailed;
                            var fallback = await AskResumeFallbackAsync().ConfigureAwait(true);
                            _resumeFallback = null;

                            if (fallback is ResumeFallback.BackedOut or ResumeFallback.Stopped)
                            {
                                // "Summarizing…" goes with the send, as every line describing its attempt
                                // does; "Could not summarize" stays, being the one place the reason is shown.
                                BackOutOfResume(send, TrayHoldFor(fallback));
                                return null;
                            }

                            if (fallback == ResumeFallback.Abandon)
                                // The conversation this message belonged to was replaced while the
                                // banner was up (New Session, another conversation opened, a workspace
                                // change). No notice: whatever the user did to cause it is its own
                                // explanation, and every one of those paths has already cleared or
                                // replaced the transcript this notice would land in.
                                return null;

                            if (fallback == ResumeFallback.Fresh)
                            {
                                send.StartOverRequested = true;
                                return null;
                            }

                            resumeId = _host.Persisted!.ConversationId;
                            reloadingNotice = ShowResumeNotice(send,
                                "Resuming this conversation's full context instead — its earlier messages are reloaded into the agent (they count toward usage).");
                        }
                    }

                    // A reload this pane's session was already refused for, which the user backed out of.
                    // The backend has said it does not have the conversation, so retrying costs a
                    // session start to be told the same thing: the session that refusal fell through to is
                    // still the pane's, and this send uses it. That is also what makes "resume from a summary"
                    // free here - the recap rides this same session rather than opening another.
                    var remembered = _lifetime.RefusedReload;
                    StartSessionResponse started;
                    if (remembered is not null)
                    {
                        started = _lifetime.LiveOwnerStarted!;
                        // The session this send prompts is the one that backed-out send opened and never
                        // committed, so its working directory and notices are applied at THIS send's commit.
                        send.Adopted = started;
                    }
                    else
                    {
                        var request = _host.BuildStartRequest(resumeId);
                        Phase = SendPhase.Preparing;
                        started = await _lifetime.TakeWarmOrStartAsync(request, epoch).ConfigureAwait(true);
                        // Retired while the start was in flight (issue #217): adopting would hand the new
                        // root's pane the old root's session. The move's own warm start, chained behind this
                        // start, disposes it instead.
                        if (!_lifetime.IsCurrent(epoch) || !StillPending(send))
                            return null;
                        _host.AdoptLiveSession(request, started);
                        // Its working directory and project settings are applied at the commit.
                        send.Adopted = started;
                    }

                    live = started;

                    // The backend refused the reload and the provider fell through to a fresh session
                    // (issue #268). That fall-through is right — it is what keeps the pane usable —
                    // but the user's message riding it unasked is not: this is the case issue #84
                    // already decided, reached by the other route. Asked BEFORE the prompt goes,
                    // because no answer given afterwards could take it back.
                    //
                    // A remembered refusal is re-raised only where this send would have RELOADED. Answer the
                    // decider with a recap instead and there is nothing to re-ask: the recap is built from our
                    // own transcript and needs nothing of the conversation the backend disclaimed.
                    var refusal = remembered is not null
                        ? (resumeId is not null ? remembered : null)
                        : ChatViewModel.ReloadFailure(started, resumeId, priorTranscript);
                    if (refusal is not null)
                    {
                        // Withdrawn before the banner goes up, not after it is answered: it is a claim
                        // about something that did not happen, and the user is about to decide what
                        // happens instead while reading the transcript it sits in.
                        if (reloadingNotice is not null)
                            WithdrawResumeNotice(send, reloadingNotice);

                        // Whether a recap is still on offer is read from the session, not carried in: it is
                        // the same fact whether the recap failed on this send, on an earlier one, or on the
                        // other resume route entirely.
                        var (proceed, recapBlock) = await ResolveRefusalAsync(
                            refusal, epoch, send, priorTranscript, priorRoot).ConfigureAwait(true);
                        if (!proceed)
                            return null;
                        summaryBlock = recapBlock;
                    }
                }

                // Nothing is left to ask - unless a workspace move during any await above retired this send
                // (issue #217).
                if (!_lifetime.IsCurrent(epoch) || !StillPending(send))
                    return null;
                // Also on the backed-out route: that send returned before reaching this wait, so the
                // session's first real prompt is this one.
                Phase = SendPhase.WaitingForTools;
                send.IdeToolsNotConfirmed = !await _host.WaitForIdeToolsAsync().ConfigureAwait(true);
                if (!_lifetime.IsCurrent(epoch) || !StillPending(send))
                    return null;

                // Recorded, and stamped with this session's id and provider, at the commit.
                send.Live = live;

                // The summary is context for the agent only - display/record the user's own text.
                if (summaryBlock is not null)
                    outgoing = ChatViewModel.ComposeOutgoing(summaryBlock, contextBlocks, preamble, text);
            }

            return outgoing;
        }

        // ---- the resume banners ---------------------------------------------------------------------

        /// <summary>
        /// Parks the send on a banner offering what is actually left after a failed summary, and
        /// answers with what the user picked.
        ///
        /// <para><b>Why the send waits.</b> The message is on screen but NOT yet in the saved log - a
        /// first send into a reopened conversation records it only once nothing is left to ask - and
        /// the session has not been started, so the only open question is which context it goes out
        /// with. Backing out (the banner's Cancel, or Stop) takes it back into the input box, with
        /// nothing recorded to undo (F5, 2026-09-13: this banner had no Cancel, and Stop did nothing).</para>
        ///
        /// <para>The pattern is the permission banner's: an await inside the turn on a completion
        /// source the banner resolves. Which is also why <c>IsBusy</c> is left standing — a
        /// message typed while the banner is up parks in the tray rather than starting a second
        /// session — while the typing dots come down, because the agent is not working: we are.</para>
        /// </summary>
        private Task<ResumeFallback> AskResumeFallbackAsync()
        {
            var canFull = CanResumeFull();
            var detail = canFull
                ? "The recap couldn't be produced, so there's nothing to resume from. Resuming the full context reloads every earlier message into the agent (counts toward usage); starting a new conversation sends this message there instead, and this one stays in the history picker."
                : "The recap couldn't be produced, and this conversation was on a different backend, so its full context can't be reloaded here either. Starting a new conversation sends this message there, and this one stays in the history picker.";

            // What happens NEXT time, said here rather than discovered on the next send (user decision,
            // 2026-09-16). The recap is withheld for the life of this session, so a user who would
            // otherwise try again is told what actually gets them one.
            detail += " " + ResumeChoiceViewModel.RecapWayBack;

            _resumeFallback = new TaskCompletionSource<ResumeFallback>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PendingResume = ResumeChoiceViewModel.SummaryFailed(
                detail, allowFull: canFull,
                resumeFull: () => ChooseResumeFallback(ResumeFallback.Full),
                startFresh: () => ChooseResumeFallback(ResumeFallback.Fresh),
                cancel: () => ChooseResumeFallback(ResumeFallback.BackedOut));
            return _resumeFallback.Task;
        }

        /// <summary>
        /// Parks the send on a banner when the backend refused to reload the conversation and the
        /// session fell through to a fresh one (issue #268), and answers with what the user picked.
        ///
        /// <para><b>Why the send waits here too.</b> Here the session HAS been started - starting it is
        /// how the refusal was discovered - but the message is still not in the saved log, so backing
        /// out (Cancel, or Stop) takes it back into the input box with nothing to undo. The session
        /// stays and the refusal is a fact ON it (<c>SessionLifetime.RefusedReload</c>), so the next send
        /// on that session meets it again before its prompt rather than landing in it unasked.
        /// </para>
        ///
        /// <para><b>And it must be asked before the prompt, not reported after it.</b> The transcript
        /// on screen still shows the whole earlier conversation, so a message written against it — "can
        /// you retry?", "do the same for the other file" — is anaphoric, and sending it into an empty
        /// session spends a turn on an agent guessing at a task it was never given.</para>
        ///
        /// <para>The recap is offered because it is genuinely available: it is built from OUR copy of
        /// the transcript by an out-of-band engine call, so nothing about it depends on the backend
        /// still holding the conversation it has just said it does not have.</para>
        /// </summary>
        private Task<ResumeFallback> AskResumeRefusedAsync(
            string reason, bool canSummary, bool startsNewConversation, bool recapWithheld)
        {
            const string Opening =
                "The backend couldn't reload this conversation, so a new session was started and the "
                + "agent has none of the messages above. ";
            var detail = Opening + (canSummary && startsNewConversation
                ? "Resuming from a summary sends it a short recap of them with your message (counts toward "
                  + "usage); starting a new conversation sends your message there instead, and this one "
                  + "stays in the history picker."
                : startsNewConversation
                    ? "There is no recap to send instead, so starting a new conversation sends your message "
                      + "there; this one stays in the history picker."
                    : canSummary
                        ? "Resuming from a summary sends it a short recap of them with your message (counts "
                          + "toward usage); sending anyway sends your message on its own."
                        : "There is no recap to send instead, so sending anyway sends your message on its own.");

            // "No recap because one FAILED here" is not "no recap because there is nothing to recap", and
            // the banner said the same sentence for both (user decision, 2026-09-16). Only the first has a
            // way back, so only the first gets one — appended rather than replacing the line above, which
            // still says what the remaining buttons do.
            if (recapWithheld)
                detail += " " + ResumeChoiceViewModel.RecapWithheld;

            _resumeFallback = new TaskCompletionSource<ResumeFallback>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PendingResume = ResumeChoiceViewModel.ResumeRefused(
                detail, reason, allowSummary: canSummary, startsNewConversation: startsNewConversation,
                resumeSummary: () => ChooseResumeFallback(ResumeFallback.Summary),
                sendAnyway: () => ChooseResumeFallback(ResumeFallback.Fresh),
                cancel: () => ChooseResumeFallback(ResumeFallback.BackedOut));
            return _resumeFallback.Task;
        }

        /// <summary>
        /// Takes back a message the user backed out of on a resume banner (Cancel, or Stop): off the
        /// transcript, and into the composer with its images and IDE context, so it can be edited or
        /// sent again. It was never recorded, so there is nothing in the saved log to undo.
        /// </summary>
        /// <remarks>
        /// <b>What does NOT come back is the delivery framing.</b> A message the tray released (a held
        /// batch, <c>DeliverPendingAsync</c>) went out with a preamble and a delivery note saying
        /// how it was sent; given back, it is the joined text alone. Deliberate: the framing describes a
        /// gesture that is now undone, and whatever sends the text next frames it for its own gesture.
        /// </remarks>
        internal void GiveBackUnsentMessage(
            MessageItemViewModel userMessage, string text,
            IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts)
        {
            Items.Remove(userMessage);
            GiveBackParkedText(text);
            GiveBackChips(attachments, contexts);
        }

        /// <summary>
        /// The chips half of a give-back: back into the composer, ahead of anything attached since, for
        /// the same reason as the text.
        /// </summary>
        /// <remarks>
        /// Rebuilt with the composer's own remove commands, because taking them for the send detached
        /// them from it. Shared with the parked form, which reaches here only for a message that brought
        /// its chips with it: a typed one left them in the composer and there is nothing to put back.
        /// </remarks>
        private void GiveBackChips(
            IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts)
        {
            for (var i = attachments.Count - 1; i >= 0; i--)
            {
                var attachment = attachments[i];
                _host.PendingAttachments.Insert(0, new AttachmentViewModel(
                    attachment.Name, attachment.MimeType, attachment.Bytes, attachment.FilePath, _host.RemoveAttachment));
            }
            for (var i = contexts.Count - 1; i >= 0; i--)
            {
                var context = contexts[i];
                _host.PendingContexts.Insert(0, new ContextItemViewModel(
                    context.Kind, context.Label, context.Text, context.Block, _host.RemoveContext));
            }

            _host.ComposerChipsChanged();
        }

        /// <summary>
        /// Hands back a message that never reached <see cref="Begin"/> — parked on the resume-choice or
        /// the moved-root banner — whole, and with no framing (see
        /// <see cref="GiveBackUnsentMessage"/> for why the framing dies with the gesture).
        /// </summary>
        private void GiveBackParked(ParkedMessage? message)
        {
            if (message is null)
                return;

            GiveBackParkedText(message.Text);
            // Only where the message OWNS them, read from the one flag that says so. A typed message's
            // chips never left the composer, so putting them back would put a second copy of every
            // picture there.
            if (!message.StillInTheComposer)
                GiveBackChips(message.Attachments, message.Contexts);
        }

        /// <summary>
        /// The text half of a give-back, and the one rule for where it lands: ahead of anything typed
        /// since, which is the later thought. An image-only message has no text to put ahead, and must
        /// not start the draft with a blank line.
        /// </summary>
        /// <remarks>
        /// Called on its own for a send that never reached <see cref="Begin"/> - parked on the
        /// resume-choice or moved-root banner, where the chips are still in the composer, so the text is
        /// the only thing that left the box. Those routes used to ASSIGN it, which threw away whatever
        /// had been typed while the banner was up; the order is the same rule for every give-back.
        /// </remarks>
        private void GiveBackParkedText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            InputText = string.IsNullOrEmpty(InputText) ? text! : text + Environment.NewLine + InputText;
        }

        /// <summary>
        /// The user backed out of a resume banner (its Cancel, or Stop), so the send unwinds as though it
        /// had not been made: the message goes back to the composer, and the send's own changes go with it.
        /// </summary>
        /// <remarks>
        /// <para><b>The chosen resume strategy goes back to Fresh</b>. Kept, the next
        /// send reran the answer the user had just walked away from - the failed recap again, with no
        /// "Continue this conversation?" - or, after a provider change, went out with no history and no
        /// question at all.</para>
        /// <para><b>The tray is held, as Stop holds it</b>. The send's finally is a turn end, and
        /// its release would otherwise deliver a follow-up typed while the banner was up ahead of the
        /// message now back in the composer. The next send the user makes releases it, as after Stop.</para>
        /// <para><b>The message itself goes back when the turn runner ends the send</b>
        /// (<see cref="GiveBackIfBackedOut"/>), in the step that frees the pane: given back here, it sat in the
        /// composer for a dispatcher post while the pane still read busy.</para>
        /// </remarks>
        private void BackOutOfResume(OutgoingSend send, TrayHold hold)
        {
            _resumeStrategy = ResumeStrategy.Fresh;
            DropResumeNotices(send);
            _host.HoldTray(hold);
            send.GiveBackPending = true;
        }

        /// <summary>
        /// Which hold a banner's answer leaves on the tray. The two endings are identical for the SEND
        /// and different for the user: Stop is a gesture about the agent's work, a Cancel is about the
        /// message. The tray says which, and only Stop moves the pill.
        /// </summary>
        private static TrayHold TrayHoldFor(ResumeFallback answer) =>
            answer == ResumeFallback.Stopped ? TrayHold.Stopped : TrayHold.BackedOut;

        /// <summary>
        /// Asks the refused-reload banner (issue #268) and carries out the answer, for BOTH routes to it:
        /// the send that discovered the refusal, and a later send after the user backed out of it.
        /// </summary>
        /// <remarks>
        /// <para><b>One copy, because two drifted</b>: the second route lost the
        /// failed-recap gate and offered the recap again as recommended.</para>
        /// <para><b>A chosen recap that fails asks again, with the recap withheld</b>. It used
        /// to send with no history, on the reasoning that sending without history was the only other
        /// answer on offer. That stopped being true when the fresh answer became "Start new conversation",
        /// so taking it silently would be the #84 defect again. "Could not summarize" stays above the
        /// banner, saying why.</para>
        /// </remarks>
        /// <returns>
        /// Whether the send goes on, and the recap block it carries if the user chose one. A "Start new
        /// conversation" answer is written onto the send rather than returned: it is the same flag the
        /// recap-failure route sets, and returning it as a third outcome was a second way to say one
        /// thing, which drifted (the caller re-assigned what this had already decided).
        /// </returns>
        private async Task<(bool Proceed, string? SummaryBlock)> ResolveRefusalAsync(
            string refusal, SessionToken epoch, OutgoingSend send,
            string? priorTranscript, string? priorRoot)
        {
            var userMessage = send.UserMessage;

            // A new conversation only means something where there is an earlier one to leave, and a recap
            // only where there is a transcript to build one from: the same fact, both times.
            var hasEarlierConversation = !string.IsNullOrWhiteSpace(priorTranscript);

            while (true)
            {
                // Two different reasons for no recap, and only one of them is something the user can act
                // on: it FAILED on this session (there is a way back), or there is no earlier conversation
                // to build one from (there is not). The banner said the same sentence for both.
                var recapWithheld = _lifetime.RecapFailedThisSession;
                var canSummary = !recapWithheld && hasEarlierConversation;
                Phase = SendPhase.AskingRefused;
                var answer = await AskResumeRefusedAsync(
                    refusal, canSummary, hasEarlierConversation, recapWithheld).ConfigureAwait(true);
                _resumeFallback = null;

                switch (answer)
                {
                    case ResumeFallback.BackedOut:
                    case ResumeFallback.Stopped:
                        // The session the refusal fell through to stays live, and the refusal is recorded on that
                        // session rather than on this send, so the next send decides afresh and meets it again
                        // rather than retrying the reload.
                        _lifetime.NoteReloadRefused(refusal);
                        BackOutOfResume(send, TrayHoldFor(answer));
                        return (false, null);

                    case ResumeFallback.Abandon:
                        // The conversation this message belonged to was replaced while the banner was up, so
                        // there is nothing left to send it into and nothing to record. No notice: whatever
                        // the user did to cause it has already replaced the transcript it would land in.
                        return (false, null);

                    case ResumeFallback.Fresh when hasEarlierConversation:
                        _lifetime.ForgetRefusedReload();
                        send.StartOverRequested = true;
                        return (false, null);

                    case ResumeFallback.Summary:
                    {
                        Phase = SendPhase.Summarizing;
                        var recap = await SummarizeTranscriptAsync(priorTranscript!, priorRoot).ConfigureAwait(true);
                        if (!_lifetime.IsCurrent(epoch) || !StillPending(send))
                            return (false, null);
                        if (recap.Summary is { } summary)
                        {
                            _lifetime.ForgetRefusedReload();
                            send.ReloadFallbackReason = refusal;
                            send.ReloadFallbackUsedRecap = true;
                            return (true, ChatViewModel.BuildSummaryBlock(summary, priorRoot));
                        }

                        _lifetime.NoteRecapFailed();
                        continue;
                    }

                    default:
                        // "Send anyway": no earlier conversation to leave, so the old answer stands.
                        _lifetime.ForgetRefusedReload();
                        send.ReloadFallbackReason = refusal;
                        send.ReloadFallbackUsedRecap = false;
                        return (true, null);
                }
            }
        }

        /// <summary>Answers the parked send, then drops the banner (whose setter's abandon is a no-op by then).</summary>
        internal void ChooseResumeFallback(ResumeFallback choice)
        {
            _resumeFallback?.TrySetResult(choice);
            PendingResume = null;
        }

        // Asks the engine to summarize a transcript in an isolated pass; null on failure/empty.
        // sourceRoot is the directory the transcript's relative paths were written against — the PRIOR
        // conversation's agent root, never the solution root or the target provider's (issue #184):
        // the provider ids below name the backend being switched TO, so the summarizer runs at that
        // backend's root, and the two roots differ for the same solution wherever one backend widens
        // (issue #54) and the other does not.
        /// <returns>
        /// The recap, or null with the reason it failed - the same reason the notice this adds carries, so a
        /// caller that clears the transcript (starting a new conversation) can carry it forward (issue #82).
        /// </returns>
        private async Task<(string? Summary, string? Failure)> SummarizeTranscriptAsync(string transcript, string? sourceRoot)
        {
            if (string.IsNullOrWhiteSpace(transcript))
                return (null, "there was no conversation to summarize");

            try
            {
                var response = await _engine.SummarizeAsync(new SummarizeRequest(
                    _host.SelectedProvider?.Id ?? _host.SessionRequest.ProviderId,
                    _host.SelectedModel?.Id ?? _host.SessionRequest.ModelId,
                    _host.SessionRequest.WorkspaceRootPath,
                    transcript,
                    sourceRoot)).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(response.Summary))
                    return (response.Summary, null);

                // A call that succeeded and answered with nothing is still a failure, and it used to be
                // the silent one: the throwing path says why, this one said nothing at all and the
                // conversation went on without the recap the user asked for.
                const string Empty = "the backend returned an empty summary";
                Items.Add(new NoticeItemViewModel(
                    "Could not summarize the conversation: " + Empty + ".",
                    NoticeKind.Error));
                return (null, Empty);
            }
            catch (Exception ex)
            {
                Items.Add(new NoticeItemViewModel($"Could not summarize the conversation: {ex.Message}", NoticeKind.Error));
                return (null, ex.Message);
            }
        }

        /// <summary>
        /// Replaces the conversation on the user's behalf, carrying the tray across the replacement.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is a method and not a copy per route.</b> Every replacement route runs through
        /// <c>ClearTranscript</c>, which drops the held messages - right for a GESTURE, because New and
        /// loading another conversation are the user choosing to leave the conversation those messages
        /// belong to, and wrong here, because nobody chose anything: the host is replacing the
        /// conversation under words already typed to be sent. Most routes shipped WITHOUT the carry, one
        /// of them in 1.0.0, and the loss is SILENT - no notice, no composer text, no transcript row - so
        /// nothing but a test written for it would have found any of them.</para>
        /// <para><b>The put-back is INSIDE</b>, which is the ordering that was easy to get wrong: a
        /// caller doing it after re-issuing its send finds that a turn which ended at once has already
        /// released an empty tray and stranded the messages.</para>
        /// <para><b>The NEXT route must call this, and nothing enforces it</b> - it cannot be enforced
        /// while <c>ClearTranscript</c> is reachable on its own. So what the method buys is a name to
        /// reach for and one place where the rule is true, and what closes the gap is a CHECK per route,
        /// not this paragraph: the loss is silent, so nothing but a test written for it goes red.</para>
        /// </remarks>
        /// <param name="replace">The replacement itself: New, or the fork.</param>
        private void ReplaceConversation(Action replace) => CarryHeldMessagesAcross(replace);

        /// <summary>
        /// The same carry, for the host's own routes. <see cref="ReplaceConversation"/> is the name the
        /// three resume-banner routes call it by, where the conversation is genuinely being REPLACED; a
        /// workspace switch is the fourth caller and replaces nothing on its own behalf, so it says what
        /// it wants rather than borrowing that name.
        /// </summary>
        /// <remarks>
        /// <para><b>One implementation, deliberately.</b> Two spellings of a rule that shipped broken on two
        /// of its first three routes would be the same defect with more places to fix it.</para>
        /// <para><b>With nothing to carry, the mode goes back to the default</b>: the replacement has put a
        /// different conversation in charge of the next send, and a Next step flipped for the old one
        /// would otherwise steer the new one's first message with the pill hidden. With messages to carry
        /// the mode stays, being how those messages were meant to go.</para>
        /// </remarks>
        internal void CarryHeldMessagesAcross(Action replace)
        {
            var held = _host.PendingMessages.ToList();
            replace();
            if (held.Count == 0)
            {
                _host.ResetPendingReleaseToDefault();
                return;
            }

            foreach (var message in held)
                _host.PendingMessages.Add(message);
            _host.RaisePendingChanged();
        }

        /// <summary>
        /// What a "Start new conversation" answer says, on the THREE routes that produce it: the resume
        /// choice, the failed recap (issue #84) and the refused reload (issue #268).
        /// </summary>
        /// <remarks>
        /// <para><b>It names no reason</b> (user decision, 2026-09-19). The banner the user answered a click
        /// earlier carried the backend's own account in full, and this notice is display-only - it is never
        /// recorded, so it survives nothing and outlives nothing. Repeating the reason here spent a sentence
        /// on what the reader had just read.</para>
        /// <para><b>"Wasn't", not "couldn't"</b>. On two of the three routes something else WAS on
        /// offer - a full reload after a failed recap, a recap after a refused reload - and the user chose
        /// this over it, so a notice asserting the conversation could not be continued is simply false. The
        /// old wording put the choice in a parenthesis beside the impossibility and contradicted itself.</para>
        /// <para><b>The moved-root banner's "Start new conversation" is a FOURTH answer with the same label
        /// and deliberately NOT this sentence.</b> It carries <see cref="ForkNotice"/>, which names the
        /// two working directories - a durable fact about the environment rather than a transient
        /// failure, and the one thing this sentence cannot say.</para>
        /// </remarks>
        internal static string StartedOverNotice(string? previousTitle) =>
            $"Started a new conversation — “{previousTitle ?? PersistedSession.DefaultTitle}” wasn't continued "
            + "here. It is unchanged in the history picker.";

        /// <summary>
        /// A resume banner's "Start new conversation" (issues #84, #268): what New does, then this
        /// message sent there. The conversation that could not be resumed is left exactly as it was -
        /// the message was never recorded into it - and stays in the history picker.
        /// </summary>
        /// <remarks>
        /// <para><b>Through New's own path, deliberately.</b> The session a refused reload fell through
        /// to is not handed over: New replaces it with a warm one, which costs one extra session start
        /// on a rare path and in exchange reuses the tested route for session release, ownership (#256)
        /// and recording, rather than a display-only reset that keeps a live session.</para>
        /// <para><b>Called from the answered send's finally</b>, once IsBusy is down - so New may
        /// warm-start and the resend is an ordinary first send - and ONLY when that send was not retired
        /// meanwhile. It used to be posted from inside the send, and a workspace move
        /// in either gap delivered the message to the new solution's agent, and a click in the hop before
        /// the post ran was overtaken by it. The finally's epoch check drops a retired send's start-over
        /// with the rest of it, and nothing runs between it and this.</para>
        /// <para><b>The tray is carried across and put back BEFORE the resend</b>: New clears
        /// it, and a follow-up typed while the banner was up belongs with this message. Put back after,
        /// a resend whose turn ended at once had already released an empty tray, stranding it.</para>
        /// <para><b>The resend carries no framing</b>: framing belongs to the gesture. The message may have reached the send that
        /// started this over as a mid-turn delivery - an interrupt, or an aside - which puts a
        /// <c>&lt;mid-turn-message&gt;</c> block on the wire telling the agent its work was cut short and a
        /// note on the bubble saying so. That described a turn in the conversation being LEFT. Delivered
        /// here it is the first thing a brand-new agent reads, about work it never did, so the framing dies
        /// with the gesture that produced it and this is an ordinary first message.</para>
        /// <para><b>The fall-through session is not handed over.</b> New warm-starts, and a warm start opens
        /// a session rather than adopting the one the refusal fell through to - which the engine then
        /// disposes at that start. The cost is one extra session start on a rare path, and with it the
        /// user's own slow MCP servers may miss this first prompt (issue #19); the send still waits for OUR
        /// bridge, which is the one that matters. Accepted rather than fixed (user decision): handing the session
        /// over needs an unmeasured answer about whether an empty-load session keeps the requested backend
        /// id, and two conversations sharing one id is issues #185 and #108. <b>Waiting for the user's other
        /// MCP servers is not the fix</b> - that would be its own feature covering every session start.</para>
        /// </remarks>
        internal void StartOverInNewConversation(
            string text,
            IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts)
        {
            // Read BEFORE the replacement rather than inside the lambda, as the sibling call site does.
            // StartNewConversation nulls the conversation as its FIRST statement, so a read inside survives
            // only on C#'s argument-evaluation order: one statement inserted ahead of it turns every
            // start-over notice into the default title, silently and on every route.
            var previousTitle = _host.Persisted?.Title;

            ReplaceConversation(() => _host.StartNewConversation(
                new NoticeItemViewModel(StartedOverNotice(previousTitle))));

            _ = _host.SendCoreAsync(text, preamble: null, deliveryNote: null, attachments, contexts);
        }

        // ---- a steer ----------------------------------------------------------------------------------

        /// <summary>
        /// Delivers a mid-turn message into the running turn. The message is an ordinary part of the
        /// conversation — shown and persisted like any other user turn — so the only thing that makes
        /// it special is that no turn had to end first.
        /// </summary>
        /// <remarks>
        /// <para>The phase-less send: there is nothing to decide, prepare or commit, and no
        /// lease, the turn it goes into holding its own.</para>
        /// <para><b>Its failure unwinds through the one give-back</b>
        /// (<see cref="GiveBackUnsentMessage"/>). It had a second, partial copy of that rule - the text
        /// alone, joined with a bare newline, the image and the IDE capture dropped and the bubble left
        /// standing, so the message was on the transcript and in the composer at once and had lost half of
        /// itself on the way.</para>
        /// <para><b>And it is the one send that has to un-record.</b> Every other message is recorded only
        /// once its prompt has gone, so backing out leaves nothing behind. A steer is recorded BEFORE
        /// it is sent, because its place among the running turn's frames is only right there - so the
        /// failure takes the entry back out, matched by the entry itself rather than by position.</para>
        /// <para><b>The un-record is an in-memory removal plus a WRITE, and only the removal is
        /// unconditional.</b> A save is a creation rather than a correction, so writing a conversation the
        /// pane has left can put a DELETED one back; the catch says why, and it is the one place on this
        /// path where the narrower repair is the right one.</para>
        /// </remarks>
        internal async Task SteerAsync(
            string text,
            string? preamble = null,
            string? deliveryNote = null,
            IReadOnlyList<AttachmentViewModel>? attachments = null,
            IReadOnlyList<ContextItemViewModel>? contexts = null)
        {
            attachments ??= Array.Empty<AttachmentViewModel>();
            contexts ??= Array.Empty<ContextItemViewModel>();

            var userMessage = new MessageItemViewModel(
                MessageRole.User, text, deliveryNote, attachments, contexts);
            Items.Add(userMessage);
            _host.RecordUser(text, attachments, contexts, insertAt: null, localId: null, live: null);
            // The entry that record just appended, and the conversation it went into, held so a failure
            // can take out exactly what it wrote. Read here rather than searched for later: the turn this
            // steer lands in goes on recording frames of its own around it.
            var recordedInto = _host.Persisted;
            var recorded = recordedInto is { Log.Count: > 0 } c ? c.Log[c.Log.Count - 1] : null;

            // Break the assistant bubble that was streaming when the user typed. Without this the
            // agent's remaining deltas keep growing a message that now sits ABOVE the steer in the
            // transcript, so its reaction to the steer reads as if it preceded it. A fresh bubble opens
            // on the next delta and lands in the right place.
            _host.SetStreaming(null);

            // Armed BEFORE the request goes out, never in reaction to its outcome. Both outcomes mean the
            // agent is working with no turn channel of ours open, so the window opens either way and there
            // is nothing to learn from the answer first.
            //
            // Opening it afterwards was a race, not merely a late start. The turn this steer pre-empts
            // closes on its OWN response, on a different channel, and nothing orders the two continuations
            // — the #33 gotcha with two responses instead of a response and a notification. Whenever
            // SendCoreAsync's `finally { IsBusy = false; }` won, IsAgentWorking went false with this window
            // not yet open, for the length of the steer round-trip: dots off, Stop gone, pickers unlocked,
            // mid-steer, with the agent still editing the user's files. Issue #70 states the rule for the
            // ACP sink — open before the steer is sent, not in reaction to the outcome — and it holds
            // identically one layer up.
            _host.BeginOutOfTurnWork();

            try
            {
                // Attached context rides a steer, exactly as an image does: it belongs to the
                // message. The <workspace-context> block is the one thing a steer does not carry, and
                // that is the backend's doing rather than a choice of ours.
                var delivery = _host.ResolveAttachments(
                    attachments, ChatViewModel.ComposeOutgoing(ChatViewModel.RenderContextBlocks(contexts), preamble, text));
                var response = await _engine
                    .SteerAsync(delivery.Text, delivery.Wire)
                    .ConfigureAwait(true);

                // The turn ended between the user pressing Enter and the request landing, so the
                // backend started a fresh turn for the message rather than dropping it. Nothing else to
                // do — the output arrives out-of-turn and renders.
                if (string.Equals(response.Outcome, nameof(SteerOutcome.StartedNewTurn), StringComparison.OrdinalIgnoreCase))
                    _host.ShowNotice("That arrived just as the turn finished, so it's running as a new turn.", NoticeKind.Info);
            }
            catch (Exception ex)
            {
                // The message never reached the agent, so no out-of-turn work follows: close the window
                // again rather than leave the pane insisting on work that was never started. Any turn
                // still open keeps IsAgentWorking true on its own through IsBusy.
                _host.CloseOutOfTurnWindow();

                // Out of the log first, while the bubble it belongs to is still on screen: the entry was
                // written on the strength of a send that did not happen, and left there it comes back on
                // the next reload looking said, with no reply under it.
                if (recorded is not null && recordedInto is not null)
                {
                    // In memory unconditionally - it only ever makes a later, legitimate write of that
                    // object more truthful.
                    recordedInto.Log.Remove(recorded);

                    // The WRITE, and only into the conversation still ON SCREEN. A save is not a
                    // correction, it is a CREATION: the store does CreateDirectory + WriteAllText, so
                    // writing a conversation the pane has left recreates a file that may have been
                    // deleted - and deleting has no busy gate, so the delete that removes the file can
                    // be the very thing that fails the steer in flight. Issue #256's rule already names
                    // this hazard from the other side: the delete route releases the live owner WITHOUT
                    // a flush, precisely so that a later save cannot bring the file back.
                    //
                    // What this gives up, said plainly: a steer whose conversation is merely LEFT rather
                    // than deleted keeps its entry in the saved file. That is what the file held before
                    // this un-record existed at all, so it is a narrower repair and not a regression -
                    // and it is the right side to err on, a stale entry being recoverable and a deleted
                    // conversation reappearing not being.
                    if (ReferenceEquals(_host.Persisted, recordedInto))
                        _host.Store?.Save(recordedInto);
                }

                // Then the whole message back to the composer, through the one give-back: the user typed
                // it and it never reached the agent, so the box is the only honest place for it - and the
                // image and the capture belong to it exactly as the text does.
                GiveBackUnsentMessage(userMessage, text, attachments, contexts);
                Items.Add(new NoticeItemViewModel($"Couldn't send that mid-turn: {ex.Message}", NoticeKind.Error));
            }
        }
    }
}
