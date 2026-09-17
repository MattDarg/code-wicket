using System.Linq;

namespace CodeWicket.Shell
{
    /// <summary>How the first prompt on a restored conversation should reconnect to the backend.</summary>
    public enum ResumeDecision
    {
        /// <summary>Not a resume situation (or nothing worth carrying) — start fresh, no prompt.</summary>
        ProceedFresh,

        /// <summary>Small + natively-resumable — silently reload the full backend context (no prompt).</summary>
        SilentFull,

        /// <summary>Big + natively-resumable — ask the user: reload full context vs resume from a summary.</summary>
        PromptFullOrSummary,

        /// <summary>Not natively resumable here (cross-backend) — confirm continuing from a summary only.</summary>
        PromptSummaryOnly,
    }

    /// <summary>
    /// The pure policy for how a restored conversation's next prompt reconnects — extracted from the
    /// chat view-model so it can be unit-tested without a dispatcher or a live engine. The view-model
    /// owns the surrounding state (whether a session has started, the picked provider); this owns only
    /// the decision, so the "when do we prompt vs resume silently" rule can't quietly regress.
    /// </summary>
    public static class ResumeDecider
    {
        /// <summary>
        /// Transcript characters above which resuming the full context is heavy enough that resuming
        /// from a summary is the suggested default (~1K tokens).
        /// </summary>
        public const int SummaryThresholdChars = 4000;

        /// <summary>True when the session has any user turn worth carrying forward into a resume.</summary>
        public static bool HasPriorTranscript(PersistedSession? session) =>
            session is not null && session.Log.Any(e => e.Role == "user");

        /// <summary>
        /// True when the agent actually replied — any assistant text in the log (the same filter the
        /// summary transcript is built from, so false here means a summary would be empty). A
        /// conversation whose session failed before the agent answered (e.g. a CLI that died on
        /// startup) has user turns but nothing a summary could carry.
        /// </summary>
        public static bool HasAssistantContent(PersistedSession? session) =>
            session is not null && session.Log.Any(e =>
                e.Event is { Type: "text" } ev && !string.IsNullOrWhiteSpace(ev.Text));

        /// <summary>Total characters of user + assistant text in the saved log — a token-weight proxy.</summary>
        public static int TranscriptCharCount(PersistedSession session)
        {
            var count = 0;
            foreach (var entry in session.Log)
            {
                count += entry.Text?.Length ?? 0;
                count += entry.Event?.Text?.Length ?? 0;
            }

            return count;
        }

        /// <summary>
        /// Decides how the first prompt on a restored conversation reconnects. Prompts only when the
        /// choice is worth it — a big full-vs-summary tradeoff, or a cross-backend continuation to make
        /// explicit; a small natively-resumable conversation reloads its full context silently.
        /// </summary>
        /// <param name="session">The restored session (its transcript weighs the choice); null → fresh.</param>
        /// <param name="isFirstContinuation">
        /// The caller's gate: a restored conversation whose backend session hasn't started yet and hasn't
        /// already picked a strategy. False → this isn't the first-continuation moment, so start fresh.
        /// </param>
        /// <param name="canResumeFull">
        /// Whether the backend can natively reload this conversation (same backend, advertises resume,
        /// carries a conversation id). False → only a summary continuation is possible.
        /// </param>
        /// <param name="thresholdChars">Char count above which full resume is "big". Defaults to <see cref="SummaryThresholdChars"/>.</param>
        public static ResumeDecision Decide(
            PersistedSession? session,
            bool isFirstContinuation,
            bool canResumeFull,
            int thresholdChars = SummaryThresholdChars)
        {
            if (!isFirstContinuation || !HasPriorTranscript(session))
                return ResumeDecision.ProceedFresh;

            if (!canResumeFull)
            {
                // Summary is the only option here — but it's only worth confirming when the agent
                // actually said something (a failed session's transcript would summarize to nothing,
                // so prompting would just nag; observed with a session whose CLI died on startup).
                // Native resume above doesn't need this gate: a conversation id means backend-side
                // context exists even if no assistant text was recorded locally.
                return HasAssistantContent(session)
                    ? ResumeDecision.PromptSummaryOnly
                    : ResumeDecision.ProceedFresh;
            }

            return TranscriptCharCount(session!) > thresholdChars
                ? ResumeDecision.PromptFullOrSummary
                : ResumeDecision.SilentFull;
        }
    }
}
