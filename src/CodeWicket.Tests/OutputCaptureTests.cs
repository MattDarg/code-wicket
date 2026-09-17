using System;
using System.Linq;
using System.Text;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The Output-window capture the user hands over from the composer.
    /// <para>
    /// The WORDING is asserted here, which is unusual and deliberate — the same reason
    /// <c>FileWriteRefusalTests</c> does it. The provenance line exists to stop a model reasoning from
    /// absence when it has been given a fragment, so what it says IS the behaviour; a capture that
    /// carried the right lines under a sentence that implied they were the whole pane would pass every
    /// structural assertion and fail at the only job it has.
    /// </para>
    /// </summary>
    public class OutputCaptureTests
    {
        private static string Lines(int count, string prefix = "line") =>
            string.Join(Environment.NewLine, Enumerable.Range(1, count).Select(i => prefix + i));

        // --- what it took ---------------------------------------------------------------------

        [Fact]
        public void AShortPaneIsSentWholeAndSaysSo()
        {
            var capture = OutputCapture.Build("Build", Lines(3), selectedText: null);

            Assert.NotNull(capture);
            Assert.Equal(OutputCaptureKind.Tail, capture!.Kind);
            Assert.False(capture.Truncated);
            // "the whole pane" is a claim the other branches must never be able to make.
            Assert.Contains("Captured: the whole pane, 3 lines.", capture.Text, StringComparison.Ordinal);
            Assert.Contains("line1", capture.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void ALongPaneKeepsTheNEWESTLinesAndStatesTheTotal()
        {
            // The newest, because the oldest is plausible text describing a run nobody asked about —
            // OutputTail's own reason, inherited here.
            var capture = OutputCapture.Build("Build", Lines(500), selectedText: null);

            Assert.NotNull(capture);
            Assert.True(capture!.Truncated);
            Assert.Equal(OutputCapture.MaxTailLines, capture.Lines);
            Assert.Equal(500, capture.TotalLines);
            Assert.Contains("line500", capture.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("line1" + Environment.NewLine, capture.Text, StringComparison.Ordinal);
            Assert.Contains("the last 200 lines of 500 lines", capture.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void ASelectionWinsOverTheTailAndIsNamedAsAFragment()
        {
            var capture = OutputCapture.Build("Build", Lines(500), selectedText: "error CS0103\nat Foo()");

            Assert.NotNull(capture);
            Assert.Equal(OutputCaptureKind.Selection, capture!.Kind);
            Assert.Equal(2, capture.Lines);
            Assert.Contains("error CS0103", capture.Text, StringComparison.Ordinal);
            // The pane's own text must not come along: the user chose, and sending both would make the
            // choice meaningless while quadrupling the block.
            Assert.DoesNotContain("line400", capture.Text, StringComparison.Ordinal);
            Assert.Contains("the user SELECTED", capture.Text, StringComparison.Ordinal);
            Assert.Contains("fragment of the pane", capture.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void EverySHORTENEDShapeTellsTheReaderWhatToDoAboutIt()
        {
            // The line that stops a model concluding from absence. It belongs on every shape that left
            // something behind, and the recourse it names has to be one that exists: there is no tool
            // for an arbitrary output pane, so the answer is to ask the user.
            var tail = OutputCapture.Build("Build", Lines(500), selectedText: null)!;
            var selection = OutputCapture.Build("Build", Lines(500), selectedText: "just this")!;

            foreach (var text in new[] { tail.Text, selection.Text })
            {
                Assert.Contains("ask for it rather than concluding it is absent", text, StringComparison.Ordinal);
                Assert.Contains("the user has to send it", text, StringComparison.Ordinal);
            }

            // ...and NOT on the shape where nothing is missing, or the advice becomes noise that the
            // reader learns to skip on the one message where it mattered.
            var whole = OutputCapture.Build("Build", Lines(3), selectedText: null)!;
            Assert.DoesNotContain("ask for it", whole.Text, StringComparison.Ordinal);
        }

        // --- the caps -------------------------------------------------------------------------

        [Fact]
        public void ASelectionIsCappedFarHigherThanATailBecauseItWasChosen()
        {
            // Two caps doing two jobs: the tail's is a curation decision made FOR the user, the
            // selection's is a rail against Ctrl+A. Truncating 400 deliberately chosen lines to 200
            // would throw away half of an answer they had already given.
            var chosen = OutputCapture.Build("Build", paneText: null, selectedText: Lines(400))!;

            Assert.Equal(400, chosen.Lines);
            Assert.False(chosen.Truncated);
            Assert.True(OutputCapture.MaxSelectedLines > OutputCapture.MaxTailLines);
        }

        [Fact]
        public void EvenADeliberateSelectionHasARailAndSaysWhereItStopped()
        {
            var capture = OutputCapture.Build(
                "Build", paneText: null, selectedText: Lines(OutputCapture.MaxSelectedLines + 50))!;

            Assert.Equal(OutputCapture.MaxSelectedLines, capture.Lines);
            Assert.True(capture.Truncated);
            Assert.Contains("not even the whole of what they picked", capture.Text, StringComparison.Ordinal);
        }

        [Fact]
        public void LongLinesAreBoundedByCHARACTERSNotJustByLineCount()
        {
            // 200 lines of ordinary build output is ~15 KB; 200 lines of MSBuild at diagnostic
            // verbosity is megabytes, and a line count reports that as a small capture the whole way.
            var fat = new StringBuilder();
            for (var i = 0; i < 100; i++)
                fat.AppendLine(new string('x', 2000));

            var capture = OutputCapture.Build("Build", fat.ToString(), selectedText: null)!;

            Assert.True(capture.Text.Length <= OutputCapture.MaxChars + 1000, // + the header
                "the char backstop did not bite: " + capture.Text.Length);
            Assert.True(capture.Lines < 100);
            Assert.True(capture.Truncated);
        }

        [Fact]
        public void OneEnormousLineComesBackRatherThanNothing()
        {
            // Returning nothing would turn "this line is huge" into "the pane was empty", which reads
            // as a race and sends the caller looking for the wrong thing.
            var capture = OutputCapture.Build("Build", new string('x', OutputCapture.MaxChars * 2), null);

            Assert.NotNull(capture);
            Assert.Equal(1, capture!.Lines);
        }

        // --- what is not a capture ------------------------------------------------------------

        [Fact]
        public void AnEmptyOrWhitespacePaneIsNothingToHandOver()
        {
            // A race, not a misuse: the gesture is offered when the pane has content, and a build
            // starting clears it. Null lets the caller say so instead of attaching an empty chip that
            // claims evidence the message does not carry.
            Assert.Null(OutputCapture.Build("Build", null, null));
            Assert.Null(OutputCapture.Build("Build", "   \r\n  \n", null));
        }

        [Fact]
        public void AStrayCLICKIsNotASelection()
        {
            // The accident this has to survive: clicking in the pane leaves a zero-length selection.
            // It must fall back to the tail rather than capturing nothing at all.
            var capture = OutputCapture.Build("Build", Lines(5), selectedText: "   ")!;

            Assert.Equal(OutputCaptureKind.Tail, capture.Kind);
            Assert.Equal(5, capture.Lines);
        }

        [Fact]
        public void ThePaneIsNamedVerbatimAndAnUnnamedOneStillReads()
        {
            Assert.StartsWith("Output: Tests ·", OutputCapture.Build("Tests", "x", null)!.Label,
                StringComparison.Ordinal);
            Assert.StartsWith("Output: Output ·", OutputCapture.Build(" ", "x", null)!.Label,
                StringComparison.Ordinal);
        }

        [Fact]
        public void TheLabelSaysWhichSHAPEItIsSoASurprisingCaptureIsVisibleBeforeItIsSent()
        {
            Assert.Contains("selection", OutputCapture.Build("Build", Lines(9), "picked")!.Label,
                StringComparison.Ordinal);
            Assert.DoesNotContain("selection", OutputCapture.Build("Build", Lines(9), null)!.Label,
                StringComparison.Ordinal);
        }

        // --- the block it rides ---------------------------------------------------------------

        [Fact]
        public void TheBlockIsUserContributedSoAnImportKeepsIt()
        {
            // Same rule as the debug capture, and load-bearing for the same reason: Strip's removal set
            // is for blocks that say what the user did NOT say. This one carries what they handed over,
            // and on the import path it is the only surviving copy.
            Assert.Contains(HostPromptBlocks.OutputWindow, HostPromptBlocks.UserContributed);
            Assert.DoesNotContain(HostPromptBlocks.OutputWindow, HostPromptBlocks.All);

            var message = HostPromptBlocks.MidTurnMessage.Open + "sent now" + HostPromptBlocks.MidTurnMessage.Close
                + HostPromptBlocks.OutputWindow.Open + "Pane: Build" + HostPromptBlocks.OutputWindow.Close
                + "why did this fail?";

            var split = HostPromptBlocks.SplitLeading(message);

            Assert.Equal("why did this fail?", split.Text);
            var kept = Assert.Single(split.Kept);
            Assert.Same(HostPromptBlocks.OutputWindow, kept.Block);
            Assert.Equal("Pane: Build", kept.Body);
        }
    }
}
