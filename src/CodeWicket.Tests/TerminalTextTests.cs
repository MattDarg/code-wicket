using System;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using CodeWicket.Core;
using CodeWicket.UI.Controls;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Cleaning terminal / CI-log text pasted into the chat: escape-sequence removal plus
    /// carriage-return overwrites (<see cref="TerminalText"/>), and the renderer's fallback for a
    /// lone CR that never went through that seam (<see cref="SelectableEmojiText"/>).
    /// </summary>
    public class TerminalTextTests
    {
        // A GitLab job trace, in the shapes the raw endpoint actually emits: an erase after the
        // banner, a fold marker overwritten via CR, a colourised command echo, a progress line
        // rewritten in place, and a fold end that erases itself entirely.
        private const string GitLabTrace =
            "\x1b[0KRunning with gitlab-runner 16.11.0\r\n" +
            "section_start:1721654321:build_script\r\x1b[0K\x1b[32;1m$ dotnet build\x1b[0;m\r\n" +
            "  Determining projects to restore...\r\n" +
            " 12%\r 47%\r100% restored\r\n" +
            "section_end:1721654399:build_script\r\x1b[0K\r\n";

        [Fact]
        public void CleansAGitLabTrace()
        {
            Assert.Equal(
                "Running with gitlab-runner 16.11.0\r\n" +
                "$ dotnet build\r\n" +
                "  Determining projects to restore...\r\n" +
                "100% restored\r\n" +
                "\r\n",
                TerminalText.Clean(GitLabTrace));
        }

        [Fact]
        public void CleanedTraceKeepsNoControlCharacters()
        {
            var cleaned = TerminalText.Clean(GitLabTrace);
            Assert.DoesNotContain('\x1b', cleaned);
            Assert.DoesNotContain('\a', cleaned);
            // Every surviving CR is the CR of a CRLF pair.
            for (var i = 0; i < cleaned.Length; i++)
                Assert.True(cleaned[i] != '\r' || (i + 1 < cleaned.Length && cleaned[i + 1] == '\n'),
                    "lone CR survived at index " + i);
        }

        [Theory]
        // The fold markers are written to be erased — a terminal never shows them.
        [InlineData("section_start:1721654321:build_script\r\x1b[0KRunning\n", "Running\n")]
        // Progress rewritten in place collapses to its final frame.
        [InlineData("Downloading 12%\rDownloading 47%\rDownloading 100%\n", "Downloading 100%\n")]
        // Colour around a command echo.
        [InlineData("\x1b[32;1m$ make\x1b[0;m\n", "$ make\n")]
        // An OSC title sequence (BEL-terminated) and a stray BEL.
        [InlineData("\x1b]0;build\aok\a\n", "ok\n")]
        public void CleansIndividualSequences(string raw, string expected) =>
            Assert.Equal(expected, TerminalText.Clean(raw));

        [Theory]
        [InlineData("plain text")]
        [InlineData("windows\r\nnewlines\r\nhere\r\n")]
        [InlineData("unix\nnewlines\nhere\n")]
        [InlineData("")]
        public void OrdinaryTextIsUntouched(string text)
        {
            Assert.False(TerminalText.NeedsCleaning(text));
            Assert.Equal(text, TerminalText.Clean(text));
        }

        [Theory]
        [InlineData("has \x1b[0K an escape")]
        [InlineData("has \a a bell")]
        [InlineData("overwrite\rthis")]
        [InlineData("mixed\r\nlines\rwith an overwrite\r\n")]
        public void TerminalNoiseIsDetected(string text) =>
            Assert.True(TerminalText.NeedsCleaning(text));

        [Fact]
        public void CrOnlyLineEndingsAreTreatedAsLineEndings()
        {
            // Classic-Mac endings: overwrite semantics would collapse this to "third" alone.
            Assert.Equal("first\nsecond\nthird", TerminalText.ApplyCarriageReturns("first\rsecond\rthird"));
        }

        [Fact]
        public void SingleLineOverwriteWithoutAnyLineFeedPrefersTheNonLossyReading()
        {
            // Indistinguishable from the CR-only case above, so we keep both halves rather than
            // silently dropping one. A spurious break is the safe miss; deleting content isn't.
            Assert.Equal("12%\n100%", TerminalText.ApplyCarriageReturns("12%\r100%"));
        }

        [Fact]
        public void CrlfSurvivesAnOverwriteOnTheSameLine() =>
            Assert.Equal("done\r\nnext\r\n", TerminalText.ApplyCarriageReturns("busy\rdone\r\nnext\r\n"));

        [Fact]
        public void ALineThatOnlyErasesItselfBecomesEmpty() =>
            Assert.Equal("\r\nkept\r\n", TerminalText.ApplyCarriageReturns("gone\r\r\nkept\r\n"));

        [Fact]
        public void FinalLineWithoutATrailingNewlineIsKept() =>
            Assert.Equal("first\nlast", TerminalText.ApplyCarriageReturns("first\nbusy\rlast"));

        // --- the renderer fallback -------------------------------------------------------------
        // Text reaching the transcript hasn't all been through the paste seam (restored sessions,
        // agent "thinking" text), so a lone CR must still break the line rather than vanish into
        // the Run as whitespace. WPF text elements demand STA; xunit runs MTA.

        [Theory]
        [InlineData("one\ntwo", 1)]
        [InlineData("one\r\ntwo", 1)]      // CRLF is one break, not two
        [InlineData("one\rtwo", 1)]        // the fix: a lone CR used to render as whitespace
        [InlineData("one\rtwo\r\nthree\nfour", 3)]
        [InlineData("no breaks at all", 0)]
        public void LineEndingsBecomeLineBreaks(string text, int expectedBreaks) => RunSta(() =>
        {
            var control = new SelectableEmojiText { Text = text };
            var measurer = control.Children.OfType<TextBlock>().Single();
            Assert.Equal(expectedBreaks, measurer.Inlines.OfType<LineBreak>().Count());
            // Nothing is dropped: the visible text is the input minus its line endings.
            var runs = string.Concat(measurer.Inlines.OfType<Run>().Select(r => r.Text));
            Assert.Equal(text.Replace("\r\n", "").Replace("\r", "").Replace("\n", ""), runs);
        });

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
