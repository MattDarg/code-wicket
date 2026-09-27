namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// Where a send is between the user pressing Enter and its prompt going out. One send is in
    /// one phase at a time, and the phase is what the pane reads to say why a control is refused.
    /// </summary>
    /// <remarks>
    /// The order is the order a send passes through them, not a ranking. A phase is either waiting on the
    /// USER (a banner is up and only an answer moves it) or waiting on the SYSTEM (an engine call is out and
    /// only Stop moves it); <see cref="SendPhases.WaitsOnUser"/> is that split, and everything the pane does
    /// differently between the two reads it rather than listing phases again.
    /// </remarks>
    public enum SendPhase
    {
        /// <summary>The Choice banner: how should this reopened conversation be continued?</summary>
        Deciding,

        /// <summary>The moved-root banner (issue #185), after the engine answered where the agent would run.</summary>
        CheckingRoot,

        /// <summary>The recap is being written (<c>engine/summarize</c>).</summary>
        Summarizing,

        /// <summary>The recap could not be produced, and the banner is asking what to do instead (issue #84).</summary>
        AskingRecapFailed,

        /// <summary>A backend session is being started or taken from the warm slot.</summary>
        Preparing,

        /// <summary>The backend refused the reload, and the banner is asking what to do instead (issue #268).</summary>
        AskingRefused,

        /// <summary>The first prompt is holding for our own IDE-tool bridge to serve its list.</summary>
        WaitingForTools,
    }

    /// <summary>
    /// What a pending send means for the controls a conversation change would reach: refused, and saying why.
    /// Pure, and in Shell rather than beside the view-model, because the wording IS the behaviour: a control
    /// that is disabled with no reason reads as broken, so the sentence is asserted rather than reviewed.
    /// </summary>
    public static class SendPhases
    {
        /// <summary>
        /// The reason New Session, the pickers and the history and import rows are refused while this send is
        /// pending. Two sentences, because there are two genuinely different situations and the recourse
        /// differs: one is answered by the banner on screen, the other only by waiting or by Stop.
        /// </summary>
        public const string AnswerTheQuestion = "Answer or cancel the question first.";

        /// <summary>The send is out and only Stop takes it back. See <see cref="AnswerTheQuestion"/>.</summary>
        public const string WaitOrStop = "Your message is still being sent. Wait, or press Stop.";

        /// <summary>
        /// Whether the send is waiting on an answer only the user can give, rather than on a call of ours.
        /// The working bar and Stop read this too: while a resume banner waits there is nothing running, so a
        /// bar saying the agent is working would not be true.
        /// </summary>
        public static bool WaitsOnUser(SendPhase phase) => phase switch
        {
            SendPhase.Deciding => true,
            SendPhase.CheckingRoot => true,
            SendPhase.AskingRecapFailed => true,
            SendPhase.AskingRefused => true,
            _ => false,
        };

        /// <summary>
        /// Why a control is refused while a send is in <paramref name="phase"/>. Derived from
        /// <see cref="WaitsOnUser"/> rather than switching over the phases a second time, so the sentence and
        /// the bar can never disagree about which situation the pane is in.
        /// </summary>
        public static string BlockReason(SendPhase phase) =>
            WaitsOnUser(phase) ? AnswerTheQuestion : WaitOrStop;
    }
}
