using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.UI.Sessions
{
    /// <summary>
    /// What <see cref="PromptDelivery"/> asks of the pane it serves: the transcript, the composer, the
    /// tray, and the parts of starting a session that are the pane's to carry out and to draw.
    /// </summary>
    /// <remarks>
    /// <para>The send's decision half was moved out of ChatViewModel, and this is what it reaches for in
    /// the pane.</para>
    /// <para>Called on the pane's own synchronization context, which is the delivery's contract too.</para>
    /// </remarks>
    internal interface IDeliveryHost
    {
        // ---- the conversation on screen, and the request a session would be started with ----------

        /// <summary>The conversation being saved; setting it tells the host and the lifetime which one is on screen.</summary>
        PersistedSession? Persisted { get; set; }

        StartSessionRequest SessionRequest { get; }

        ProviderItemViewModel? SelectedProvider { get; }

        ModelItemViewModel? SelectedModel { get; }

        ISessionStore? Store { get; }

        bool CanResumeFull();

        string? ResumeWorkingDirectory();

        StartSessionRequest BuildStartRequest(string? resumeId);

        /// <summary>
        /// The pending send began, moved phase, or ended: everything gated on it is re-read.
        /// </summary>
        void PendingSendChanged();

        /// <summary>
        /// A send was ended by a conversation change, so the pane stops holding busy on its account at once -
        /// whatever it was still awaiting (user decision, 2026-09-15). Called only where a send really was
        /// pending, never for a change that lands during a turn.
        /// </summary>
        void SendEnded();

        // ---- the composer and the tray -------------------------------------------------------------

        string InputText { get; set; }

        ObservableCollection<AttachmentViewModel> PendingAttachments { get; }

        ObservableCollection<ContextItemViewModel> PendingContexts { get; }

        void RemoveAttachment(AttachmentViewModel attachment);

        void RemoveContext(ContextItemViewModel context);

        /// <summary>The composer's chips changed: their visibility and the send gestures' gate are re-read.</summary>
        void ComposerChipsChanged();

        IReadOnlyList<AttachmentViewModel> TakePendingAttachments();

        IReadOnlyList<ContextItemViewModel> TakePendingContexts();

        ObservableCollection<PendingMessageViewModel> PendingMessages { get; }

        void RaisePendingChanged();

        /// <summary>
        /// Puts the tray's mode back to the user's default: a replacement that carried nothing has put a
        /// different conversation in charge, and a flip belonged to the one it replaced.
        /// </summary>
        void ResetPendingReleaseToDefault();

        /// <summary>
        /// Holds what the tray has, and says which gesture did it. The send's own end is a release
        /// point, so a send taken back leaves the follow-ups typed behind it to go out alone unless the
        /// tray is held; the reason is what the tray says about it.
        /// </summary>
        void HoldTray(TrayHold reason);

        // ---- the transcript --------------------------------------------------------------------------

        ObservableCollection<ChatItemViewModel> Items { get; }

        ResumeChoiceViewModel? PendingResume { get; set; }

        /// <summary>A new user message ends the assistant bubble that was streaming, without touching the typing indicator.</summary>
        void ForgetStreamingAssistant();

        void SetStreaming(MessageItemViewModel? value);

        void ShowNotice(string text, NoticeKind kind);

        /// <param name="localId">The conversation's local id when the send allocated it before the conversation
        /// existed; null records into the conversation on screen under its own.</param>
        /// <param name="live">The backend session this message is going out on, stamped onto the conversation
        /// before the record's own save so that ONE write carries both; null where nothing was started.</param>
        void RecordUser(
            string text, IReadOnlyList<AttachmentViewModel>? attachments,
            IReadOnlyList<ContextItemViewModel>? contexts, int? insertAt, string? localId,
            StartSessionResponse? live);

        // ---- the session a send opens ----------------------------------------------------------------

        void AdoptLiveSession(StartSessionRequest request, StartSessionResponse started);

        void ApplyAgentWorkingDirectory(StartSessionResponse started, int noticeIndex);

        void ReportProjectSettings(StartSessionResponse started, int noticeIndex);

        /// <summary>
        /// Says what this send's message went out with after a refused reload. Called at the COMMIT and
        /// never before it: it describes a delivery, so it is not written until one happens.
        /// </summary>
        void ReportResumeFallback(string reason, int noticeIndex, bool resumedFromSummary);

        /// <summary>
        /// Holds the first prompt until the backend has taken our IDE tools. False when the wait timed out,
        /// which the SEND says at its commit - the notice is a claim about what the message went out with,
        /// so it is not written until one does.
        /// </summary>
        Task<bool> WaitForIdeToolsAsync();

        (string Text, IReadOnlyList<PromptAttachmentDto>? Wire) ResolveAttachments(
            IReadOnlyList<AttachmentViewModel> attachments, string outgoing);

        void BeginOutOfTurnWork();

        void CloseOutOfTurnWindow();

        // ---- a new conversation ----------------------------------------------------------------------

        /// <summary>
        /// What New does, and the ONE way delivery starts a conversation: drop the one on screen, clear
        /// the transcript, announce the new session, draw this notice and warm-start. The resume banners'
        /// fresh answers and the moved-root fork all come through here, each with its own notice, so a
        /// route cannot acquire a private copy of the steps and then drift from it.
        /// </summary>
        void StartNewConversation(NoticeItemViewModel notice);

        /// <summary>The pane's turn runner: sends, and holds the turn until it returns.</summary>
        Task SendCoreAsync(
            string text, string? preamble, string? deliveryNote,
            IReadOnlyList<AttachmentViewModel>? attachments, IReadOnlyList<ContextItemViewModel>? contexts);
    }
}
