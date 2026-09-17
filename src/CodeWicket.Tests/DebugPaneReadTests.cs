using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the agent is told when the Debug output pane cannot be handed over (issue #272).
    /// <para>
    /// These assert WORDING for the reason <see cref="FileWriteRefusalTests"/> does: the sentence is the
    /// whole of the behaviour, and the measured cost of getting it wrong is a model supplying a cause of
    /// its own. <c>get_debug_output</c> returned a bare <c>E_FAIL</c> from an instance that had simply
    /// never run anything under its debugger, and it was reported as a defect in the tool.
    /// </para>
    /// <para>
    /// Nothing offline can reach the EnvDTE walk that decides WHICH of these is produced — that half is
    /// Reachable only in a live Visual Studio instance. What is pinned here is that the four answers stay four answers: that absence succeeds,
    /// that a failure to look is never dressed as an empty pane, and that neither absent sentence claims
    /// anything about a debugger this instance does not host.
    /// </para>
    /// </summary>
    public sealed class DebugPaneReadTests
    {
        private const string Hresult = "Unspecified error (Exception from HRESULT: 0x80004005 (E_FAIL))";

        /// <summary>
        /// THE test, and the whole of the issue. Both absent shapes succeed; both failure shapes do not.
        /// One <c>catch</c> over the walk made all four the last row, which is what put the considered
        /// absent answer out of reach in the only case it exists for.
        /// </summary>
        [Fact]
        public void AbsenceSucceedsAndOnlyAFailedLookFails()
        {
            Assert.Equal(DebugPaneOutcome.Absent, DebugPaneRead.NoOutputWindow(Hresult).Outcome);
            Assert.Equal(DebugPaneOutcome.Absent, DebugPaneRead.NoDebugPane().Outcome);
            Assert.Equal(DebugPaneOutcome.Unreadable, DebugPaneRead.SearchFailed(Hresult).Outcome);
            Assert.Equal(DebugPaneOutcome.Unreadable, DebugPaneRead.Unreadable(Hresult).Outcome);
        }

        /// <summary>
        /// An absent answer says it is the empty one. Without that the agent reads a summary beginning
        /// "No Debug output pane" beside an <c>isError:false</c> and still has to decide which it was —
        /// and the tool's own description promises that an empty result means nothing was written.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnAbsentAnswerSaysItIsNotAFailedRead(bool noWindow)
        {
            var read = noWindow ? DebugPaneRead.NoOutputWindow(Hresult) : DebugPaneRead.NoDebugPane();

            Assert.Contains("not a failed read", read.Summary);
            Assert.Null(read.Text);
        }

        /// <summary>
        /// And it names the instance. The session that produced this issue had the caller sitting in the
        /// debuggee, with the debugger's view of it in the OTHER Visual Studio — a fact invisible from
        /// where the agent stands, and the one line that closes the question rather than inviting a
        /// hypothesis about the tool.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnAbsentAnswerNamesWhichVisualStudioItRead(bool noWindow)
        {
            var read = noWindow ? DebugPaneRead.NoOutputWindow(Hresult) : DebugPaneRead.NoDebugPane();

            Assert.Contains("different instance", read.Summary);
        }

        /// <summary>
        /// Neither failure may read as an empty pane. This is the asymmetry that matters: "nothing was
        /// written" and "we could not look" lead to opposite next moves, and the second one silently
        /// wearing the first's clothes is how an agent concludes a tracepoint never fired.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AFailureForbidsReadingItAsAnEmptyPane(bool searchFailed)
        {
            var read = searchFailed ? DebugPaneRead.SearchFailed(Hresult) : DebugPaneRead.Unreadable(Hresult);

            Assert.Contains("do not read this as an empty pane", read.Summary);
            Assert.Contains(Hresult, read.Summary);
            Assert.Null(read.Text);
        }

        /// <summary>
        /// The two failures are not one failure. A search that did not finish has established nothing
        /// about whether a Debug pane exists; a pane that was found and would not be read has. Collapsing
        /// them would rebuild the defect one level down.
        /// </summary>
        [Fact]
        public void AFailedSearchAndAnUnreadablePaneSayDifferentThings()
        {
            var search = DebugPaneRead.SearchFailed(Hresult).Summary;
            var unreadable = DebugPaneRead.Unreadable(Hresult).Summary;

            Assert.NotEqual(search, unreadable);
            Assert.Contains("whether one exists is unknown", search);
            Assert.Contains("The Debug output pane is there", unreadable);
        }

        /// <summary>
        /// An absence inferred from a throw keeps the throw. The diagnosis behind this issue was reasoning
        /// from the code and was never reproduced under a debugger, so the underlying message is the one
        /// thing that identifies the failing getter next time — it rides as a detail on a SUCCESS, never
        /// as the error it used to be.
        /// </summary>
        [Fact]
        public void AnAbsenceInferredFromAThrowCarriesIt()
        {
            Assert.Equal(Hresult, DebugPaneRead.NoOutputWindow(Hresult).Detail);
            Assert.Equal(DebugPaneOutcome.Absent, DebugPaneRead.NoOutputWindow(Hresult).Outcome);
        }

        /// <summary>
        /// Where Visual Studio simply answered with nothing there is no failure to report, and inventing
        /// a detail would put a cause in the payload that nobody observed.
        /// </summary>
        [Fact]
        public void AnAbsenceWithNothingToReportCarriesNoDetail()
        {
            Assert.Null(DebugPaneRead.NoOutputWindow(detail: null).Detail);
            Assert.Null(DebugPaneRead.NoDebugPane().Detail);
        }

        /// <summary>
        /// An empty pane is a read, not an absence. The pane exists and holds nothing — the tool's
        /// documented "an empty result means nothing has been written" — and it must not fall into the
        /// branch that says no debug session has run.
        /// </summary>
        [Fact]
        public void AnEmptyPaneIsAReadAndNotAnAbsence()
        {
            var read = DebugPaneRead.Read(string.Empty);

            Assert.Equal(DebugPaneOutcome.Read, read.Outcome);
            Assert.Equal(string.Empty, read.Text);
        }

        /// <summary>
        /// Every answer carries a summary, so a caller can never serialize a null into the one field the
        /// agent is most likely to read.
        /// </summary>
        [Fact]
        public void EveryAnswerHasASummary()
        {
            Assert.NotNull(DebugPaneRead.NoOutputWindow(null).Summary);
            Assert.NotNull(DebugPaneRead.NoDebugPane().Summary);
            Assert.NotNull(DebugPaneRead.SearchFailed(Hresult).Summary);
            Assert.NotNull(DebugPaneRead.Unreadable(Hresult).Summary);
            Assert.NotNull(DebugPaneRead.Read("x").Summary);
        }
    }
}
