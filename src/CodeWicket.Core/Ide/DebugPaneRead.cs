namespace CodeWicket.Core.Ide
{
    /// <summary>Which of the three answers a Debug-pane read arrived at.</summary>
    public enum DebugPaneOutcome
    {
        /// <summary>Established that there is nothing to read. A true answer, so the tool succeeds.</summary>
        Absent,

        /// <summary>We could not look, or found the pane and could not read it. The tool fails.</summary>
        Unreadable,

        /// <summary>The Debug pane's text, which may legitimately be empty.</summary>
        Read,
    }

    /// <summary>
    /// The outcome of reading Visual Studio's Debug output pane, and the sentence we hand the agent for
    /// it (issue #272).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Nothing there" and "couldn't look" are different answers, and one <c>catch</c> over the whole
    /// walk collapses them.</b> <c>get_debug_output</c> already had the considered absent answer — no
    /// Debug pane means no debug session has run in this instance, which is true and useful — but the
    /// EnvDTE walk that reaches it sits under a single <c>catch (Exception)</c>, so anything throwing
    /// before the pane is found returned <c>"could not read the Debug output pane: Unspecified error
    /// (Exception from HRESULT: 0x80004005 (E_FAIL))"</c> instead. Observed 2026-09-10 in an Exp-hive
    /// instance that had never run anything under its own debugger: the absent branch was unreachable in
    /// exactly the case it was written for.
    /// </para>
    /// <para>
    /// <b>The cost is the <see cref="FileWriteRefusal"/> lesson at one remove.</b> Given a result naming
    /// no cause, a model invents one — here the bare HRESULT was read as a defect in the tool and
    /// reported as such, when the instance simply had no debug session to report on. So each answer below
    /// says what was CHECKED, and none of them names a cause it did not establish.
    /// </para>
    /// <para>
    /// <b>Absence is not one fact, and neither is failure.</b> Four sentences rather than two, because
    /// the walk can stop at four different points and the agent's next move differs at each: no Output
    /// window at all, an Output window with no Debug pane, a search that could not be completed, and our
    /// pane found but unreadable. The first two are the empty answer; the last two are failures, and they
    /// differ in whether a Debug pane is known to exist.
    /// </para>
    /// <para>
    /// <b>Every absent sentence names the instance.</b> The read is of the Debug pane belonging to the
    /// Visual Studio this extension is loaded in, and the case that produced this issue was a caller
    /// sitting in the debuggee, whose debugger's output lives in the other instance. That is the one line
    /// that closes the question, and it is worth repeating on both absent answers because it is invisible
    /// from where the agent stands.
    /// </para>
    /// <para>
    /// <b>Where the absence was inferred from a throw, the throw travels with it as a detail.</b> The
    /// diagnosis behind this issue is reasoning from the code and was never reproduced under a debugger,
    /// so discarding the underlying message would throw away the one thing that identifies the failing
    /// getter the next time somebody meets it. It rides as a note on a successful payload, never as an
    /// error, because the answer is still the absent one.
    /// </para>
    /// <para>
    /// Pure and in Core because, as with <see cref="FileWriteRefusal"/>, the wording IS the behaviour;
    /// the walk that produces it is net472 and VS-bound, and nothing offline can reach it.
    /// </para>
    /// </remarks>
    public readonly struct DebugPaneRead
    {
        private DebugPaneRead(DebugPaneOutcome outcome, string summary, string? text, string? detail)
        {
            Outcome = outcome;
            Summary = summary;
            Text = text;
            Detail = detail;
        }

        public DebugPaneOutcome Outcome { get; }

        /// <summary>The sentence handed to the agent. Never null.</summary>
        public string Summary { get; }

        /// <summary>The pane's text when <see cref="Outcome"/> is <see cref="DebugPaneOutcome.Read"/>, else null.</summary>
        public string? Text { get; }

        /// <summary>
        /// The underlying failure message, where there was one. Present on both failures and on an
        /// absence that was inferred from a throw rather than from a null.
        /// </summary>
        public string? Detail { get; }

        /// <summary>Visual Studio has no Output window in this instance, so there is no pane to read.</summary>
        /// <param name="detail">
        /// The message from asking for it, when the ask threw. Null where Visual Studio simply answered
        /// with nothing — there is no failure to report in that case.
        /// </param>
        public static DebugPaneRead NoOutputWindow(string? detail) => new DebugPaneRead(
            DebugPaneOutcome.Absent,
            "No Debug output pane: Visual Studio has no Output window in this instance, so nothing has "
            + "been written there to read. This is the empty answer, not a failed read — the pane appears "
            + "once something runs under this instance's debugger. It is the Debug pane of the Visual "
            + "Studio that Code Wicket is loaded in; a program being debugged from a different instance "
            + "writes to that instance's pane, which is not readable from here.",
            text: null,
            detail);

        /// <summary>The Output window's panes were searched and none of them was the Debug pane.</summary>
        public static DebugPaneRead NoDebugPane() => new DebugPaneRead(
            DebugPaneOutcome.Absent,
            "No Debug output pane — nothing has been run under the debugger in this Visual Studio "
            + "instance, so there is nothing to read. This is the empty answer, not a failed read. It is "
            + "the Debug pane of the Visual Studio that Code Wicket is loaded in; a program being debugged "
            + "from a different instance writes to that instance's pane, which is not readable from here.",
            text: null,
            detail: null);

        /// <summary>
        /// The Output window's panes could not be enumerated, so whether a Debug pane exists is unknown.
        /// </summary>
        public static DebugPaneRead SearchFailed(string detail) => new DebugPaneRead(
            DebugPaneOutcome.Unreadable,
            "Visual Studio's output panes could not be searched for the Debug pane, so nothing was read "
            + "and whether one exists is unknown: " + detail
            + " — do not read this as an empty pane. Ask the user what the Output window's Debug pane "
            + "shows if you need it.",
            text: null,
            detail);

        /// <summary>The Debug pane was found, and reading its text failed.</summary>
        public static DebugPaneRead Unreadable(string detail) => new DebugPaneRead(
            DebugPaneOutcome.Unreadable,
            "The Debug output pane is there, and reading its text failed, so nothing was read and nothing "
            + "was changed: " + detail
            + " — do not read this as an empty pane. Ask the user what its Debug pane shows if you need it.",
            text: null,
            detail);

        /// <summary>The pane's text. An empty string is a real answer: the pane is there and empty.</summary>
        public static DebugPaneRead Read(string text) => new DebugPaneRead(
            DebugPaneOutcome.Read,
            summary: string.Empty,
            text ?? string.Empty,
            detail: null);
    }
}
