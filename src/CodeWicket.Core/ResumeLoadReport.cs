namespace CodeWicket.Core
{
    /// <summary>
    /// What a requested resume actually brought back, as one line for the diagnostic log.
    /// </summary>
    /// <remarks>
    /// <para><b>Pure, and in Core, because the wording IS the behaviour</b> — the same reason
    /// <see cref="Ide.FileWriteRefusal"/> is. It reads the two members of
    /// <see cref="IResumeFallbackReport"/> together, which is the whole point: a replay count of zero
    /// means "the load reported success and nothing came back" ONLY when the load did not fail.</para>
    /// <para><b>Not testable at the call site.</b> The line goes to <c>Console.Error</c> from
    /// <c>EngineService</c>, and capturing that in this suite was tried and measured unsound:
    /// <c>Console.SetError</c> is process-global while xUnit runs test classes in parallel, so a capture
    /// picks up whatever else is driving a session start — it passed serially and failed under the gate
    /// matrix, blaming an unrelated class. Hence a pure function with the assertions on it instead.</para>
    /// </remarks>
    public static class ResumeLoadReport
    {
        /// <summary>The log line for a resume of <paramref name="resumeId"/>.</summary>
        /// <param name="resumeId">The conversation id the resume was asked for.</param>
        /// <param name="replayed">
        /// <see cref="IResumeFallbackReport.ReplayedHistoryCount"/>: null when the backend does not say,
        /// zero when the load reported success and replayed nothing.
        /// </param>
        /// <param name="refusedBecause">
        /// <see cref="IResumeFallbackReport.ResumeFailureReason"/>: the BACKEND's refusal, or null.
        /// </param>
        public static string Describe(string resumeId, int? replayed, string? refusedBecause) =>
            // The refusal is read FIRST, and that ordering is the whole fix. A refused load replays
            // nothing, so the count alone says zero and the sentence below would credit the backend with
            // a success it never reported - contradicting the provider's own "resume of 'X' failed,
            // starting fresh" line about the same episode.
            refusedBecause is { Length: > 0 }
                ? $"[resume] '{resumeId}': NOT loaded - the reload was refused, so the fresh session it fell through to holds none of it ({refusedBecause})"
                : replayed switch
                {
                    null => $"[resume] '{resumeId}': the backend does not report what it replayed",
                    0 => $"[resume] '{resumeId}': loaded with NOTHING replayed - the backend reported success for a conversation it does not hold",
                    var n => $"[resume] '{resumeId}': loaded, {n} conversation frame(s) replayed",
                };
    }
}
