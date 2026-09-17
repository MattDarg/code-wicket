using CodeWicket.Ipc;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The resume-strategy policy: how the first prompt on a restored conversation reconnects. Extracted
    /// from ChatViewModel so it's testable without a dispatcher/engine. Pins the four outcomes and the
    /// "only prompt when it's worth it" rule — small natively-resumable resumes silently; big prompts
    /// full-vs-summary; a cross-backend continuation prompts summary-only; everything else proceeds fresh.
    /// This logic has regressed before, so the boundaries are worth locking down.
    /// </summary>
    public sealed class ResumeDeciderTests
    {
        // Builds a session with the given number of user turns, each carrying `perTurnChars` of text, so
        // the transcript char count can be pushed either side of the summary threshold deterministically
        // (assistant turns are omitted by default to keep that char math exact — the resumable-path
        // decisions don't depend on them). `answered: true` adds an agent reply per turn, for the
        // summary-only branch, which proceeds fresh unless the agent actually said something.
        private static PersistedSession SessionWith(int userTurns, int perTurnChars = 10, bool answered = false)
        {
            var s = new PersistedSession { WorkspaceRootPath = "C:\\ws", ConversationId = "conv" };
            for (var i = 0; i < userTurns; i++)
            {
                s.Log.Add(new TranscriptEntry { Role = "user", Text = new string('x', perTurnChars) });
                if (answered)
                    s.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "ok" } });
            }
            return s;
        }

        [Fact]
        public void NullSession_ProceedsFresh()
        {
            Assert.Equal(ResumeDecision.ProceedFresh,
                ResumeDecider.Decide(null, isFirstContinuation: true, canResumeFull: true));
        }

        // Not the first-continuation moment (already started, or a strategy already chosen) → no prompt.
        [Fact]
        public void NotFirstContinuation_ProceedsFresh()
        {
            Assert.Equal(ResumeDecision.ProceedFresh,
                ResumeDecider.Decide(SessionWith(5), isFirstContinuation: false, canResumeFull: true));
        }

        // A restored session with no user turns has nothing to carry forward.
        [Fact]
        public void NoPriorUserTurns_ProceedsFresh()
        {
            var empty = new PersistedSession { WorkspaceRootPath = "C:\\ws" };
            empty.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "hi" } });

            Assert.Equal(ResumeDecision.ProceedFresh,
                ResumeDecider.Decide(empty, isFirstContinuation: true, canResumeFull: true));
        }

        // Small + natively-resumable → reload full context with no prompt.
        [Fact]
        public void SmallResumable_SilentFull()
        {
            Assert.Equal(ResumeDecision.SilentFull,
                ResumeDecider.Decide(SessionWith(2), isFirstContinuation: true, canResumeFull: true));
        }

        // Big + natively-resumable → prompt with both options (the tradeoff is worth surfacing).
        [Fact]
        public void BigResumable_PromptsFullOrSummary()
        {
            var big = SessionWith(userTurns: 1, perTurnChars: ResumeDecider.SummaryThresholdChars + 1);

            Assert.Equal(ResumeDecision.PromptFullOrSummary,
                ResumeDecider.Decide(big, isFirstContinuation: true, canResumeFull: true));
        }

        // Can't resume full (cross-backend / no id) but the agent did reply → prompt, summary-only,
        // regardless of size (it confirms the continuation semantics, not the cost).
        [Theory]
        [InlineData(2)]   // small
        [InlineData(1000)] // big
        public void NotResumable_WithAgentReplies_PromptsSummaryOnly(int userTurns)
        {
            Assert.Equal(ResumeDecision.PromptSummaryOnly,
                ResumeDecider.Decide(SessionWith(userTurns, answered: true), isFirstContinuation: true, canResumeFull: false));
        }

        // A conversation whose session died before the agent ever replied (e.g. the CLI failed on
        // startup) has user turns but nothing a summary could carry — the summary-only prompt would
        // just nag, so it proceeds fresh silently. Regression: a proxy-blocked kiro-cli produced a
        // one-line failed chat that prompted to summarize (2026-07-10).
        [Fact]
        public void NotResumable_NoAgentReply_ProceedsFresh()
        {
            Assert.Equal(ResumeDecision.ProceedFresh,
                ResumeDecider.Decide(SessionWith(1), isFirstContinuation: true, canResumeFull: false));
        }

        // The gate applies only to the summary-only branch: a native resume (conversation id) is
        // still valuable without recorded assistant text — backend-side context can exist regardless.
        [Fact]
        public void Resumable_NoAgentReply_StillResumesSilently()
        {
            Assert.Equal(ResumeDecision.SilentFull,
                ResumeDecider.Decide(SessionWith(1), isFirstContinuation: true, canResumeFull: true));
        }

        [Fact]
        public void HasAssistantContent_RequiresNonEmptyAssistantText()
        {
            Assert.False(ResumeDecider.HasAssistantContent(null));
            Assert.False(ResumeDecider.HasAssistantContent(SessionWith(2)));

            var whitespaceReply = SessionWith(1);
            whitespaceReply.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "  " } });
            Assert.False(ResumeDecider.HasAssistantContent(whitespaceReply));

            Assert.True(ResumeDecider.HasAssistantContent(SessionWith(1, answered: true)));
        }

        // Exactly at the threshold is NOT "big" (strictly greater) → still a silent full resume.
        [Fact]
        public void AtThreshold_IsNotBig_SilentFull()
        {
            var atThreshold = SessionWith(userTurns: 1, perTurnChars: ResumeDecider.SummaryThresholdChars);

            Assert.Equal(ResumeDecision.SilentFull,
                ResumeDecider.Decide(atThreshold, isFirstContinuation: true, canResumeFull: true));
        }

        // One char over the threshold flips to the prompt.
        [Fact]
        public void JustOverThreshold_Prompts()
        {
            var over = SessionWith(userTurns: 1, perTurnChars: ResumeDecider.SummaryThresholdChars + 1);

            Assert.Equal(ResumeDecision.PromptFullOrSummary,
                ResumeDecider.Decide(over, isFirstContinuation: true, canResumeFull: true));
        }

        [Fact]
        public void HasPriorTranscript_TrueOnlyWithAUserTurn()
        {
            Assert.False(ResumeDecider.HasPriorTranscript(null));
            Assert.False(ResumeDecider.HasPriorTranscript(new PersistedSession()));
            Assert.True(ResumeDecider.HasPriorTranscript(SessionWith(1)));
        }

        // Char count sums both the user text and the streamed assistant text.
        [Fact]
        public void TranscriptCharCount_SumsUserAndAssistantText()
        {
            var s = new PersistedSession();
            s.Log.Add(new TranscriptEntry { Role = "user", Text = "hello" });          // 5
            s.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "world!" } }); // 6
            s.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "toolStart" } }); // 0 text

            Assert.Equal(11, ResumeDecider.TranscriptCharCount(s));
        }

        // The threshold is overridable so callers/tests aren't pinned to the default.
        [Fact]
        public void CustomThreshold_IsHonoured()
        {
            var s = SessionWith(userTurns: 1, perTurnChars: 100);

            Assert.Equal(ResumeDecision.PromptFullOrSummary,
                ResumeDecider.Decide(s, isFirstContinuation: true, canResumeFull: true, thresholdChars: 50));
            Assert.Equal(ResumeDecision.SilentFull,
                ResumeDecider.Decide(s, isFirstContinuation: true, canResumeFull: true, thresholdChars: 500));
        }
    }
}
