using System.Collections.Generic;
using System.Linq;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The card that makes an agent's breakpoints visible in the conversation (issue #73).
    /// <para>
    /// It exists because the alternative is red dots appearing in the user's gutter with nothing in the
    /// chat saying so — the auto-opened-diff-window mistake again. So the tests are mostly about the two
    /// things the gutter cannot show: the condition, and why the agent chose that line.
    /// </para>
    /// </summary>
    public sealed class BreakpointCardTests
    {
        private const string Root = @"C:\ws";

        private static BreakpointRowViewModel Row(
            string file = @"C:\ws\src\Orders\OrderProcessor.cs", int line = 47,
            string? condition = null, string? printMessage = null, string? reason = null,
            string? status = "set", string? error = null,
            bool setByAgent = true, bool? thisConversation = null) =>
            new BreakpointRowViewModel(
                file, line, Root, openFile: null, condition, printMessage, reason, status, error,
                setByAgent, thisConversation);

        private static BreakpointCardItemViewModel Card(
            string action = "set", string summary = "Set 1 breakpoint.", bool isError = false,
            IEnumerable<BreakpointRowViewModel>? rows = null, IEnumerable<string>? notes = null) =>
            new BreakpointCardItemViewModel(action, summary, isError, rows ?? new[] { Row() }, notes);

        /// <summary>A handful IS the detail you wanted; past that the card becomes a wall.</summary>
        [Theory]
        [InlineData(1, true)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(20, false)]
        public void TheRowListOpensOnlyForASmallSet(int count, bool expectedOpen)
        {
            var card = Card(rows: Enumerable.Range(1, count).Select(i => Row(line: i)).ToList());

            Assert.Equal(expectedOpen, card.IsExpanded);
        }

        [Fact]
        public void AnEmptyCardDoesNotOfferAnEmptyList()
        {
            var card = Card(rows: new List<BreakpointRowViewModel>());

            Assert.False(card.HasRows);
            Assert.False(card.IsExpanded);
        }

        /// <summary>
        /// Copy carries every row whatever the card is showing. A collapsed card is a display state, not
        /// a shorter set of facts — the same rule the test-results card follows.
        /// </summary>
        [Fact]
        public void CopyCarriesEveryRowEvenWhenCollapsed()
        {
            var card = Card(rows: Enumerable.Range(1, 9).Select(i => Row(line: i, reason: $"reason {i}")).ToList());
            Assert.False(card.IsExpanded);

            var copied = card.CopyText;

            Assert.NotNull(copied);
            for (var i = 1; i <= 9; i++)
                Assert.Contains($"reason {i}", copied);
        }

        /// <summary>
        /// The condition and the reason are the card's whole reason for existing, so they have to survive
        /// into Copy — that is what gets pasted into an issue when the breakpoint turns out to be wrong.
        /// </summary>
        [Fact]
        public void CopyCarriesTheConditionAndTheReason()
        {
            var card = Card(rows: new[]
            {
                Row(condition: "order.Items.Count == 0", reason: "the empty-order path reaches the deref"),
            });

            Assert.Contains("order.Items.Count == 0", card.CopyText);
            Assert.Contains("the empty-order path reaches the deref", card.CopyText);
        }

        /// <summary>
        /// A tracepoint does NOT stop, and a user expecting a stop that never comes cannot tell why from
        /// the gutter — so the row has to say it is one, and Copy has to carry the message.
        /// </summary>
        [Fact]
        public void ATracepointSaysSoAndCarriesItsMessage()
        {
            var trace = Row(printMessage: "item {i}: total={running.Total}");
            var stop = Row(condition: "i == 47");

            Assert.True(trace.IsTracepoint);
            Assert.False(stop.IsTracepoint);
            Assert.Contains("item {i}: total={running.Total}", Card(rows: new[] { trace }).CopyText);
        }

        /// <summary>A failed row keeps its own error rather than being dropped from the card.</summary>
        [Fact]
        public void AFailedRowKeepsItsError()
        {
            var row = Row(status: "failed", error: "no executable statement on that line");

            Assert.True(row.HasError);
            Assert.Contains("no executable statement", Card(rows: new[] { row }).CopyText);
        }

        /// <summary>
        /// The notes are the tool's advice, and hiding them is how the user ends up believing their
        /// gutter is clean when breakpoints from an earlier conversation are still in it.
        /// </summary>
        [Fact]
        public void NotesSurviveIntoTheCardAndItsCopy()
        {
            var card = Card(
                action: "cleared", summary: "You set no breakpoints in this conversation.",
                rows: new List<BreakpointRowViewModel>(),
                notes: new[] { "2 breakpoints you set in earlier conversations remain — call again with scope 'all'." });

            Assert.True(card.HasNotes);
            Assert.Contains("earlier conversations remain", card.CopyText);
        }

        /// <summary>
        /// A cleared card must not wear a tick: nothing was placed, and a tick claims the opposite of
        /// what happened.
        /// </summary>
        [Fact]
        public void EachVerbGetsItsOwnMark()
        {
            var set = Card(action: "set").StatusGlyph;
            var cleared = Card(action: "cleared").StatusGlyph;
            var failed = Card(action: "set", isError: true).StatusGlyph;

            Assert.NotEqual(set, cleared);
            Assert.NotEqual(set, failed);
        }

        /// <summary>
        /// A row with no resolvable location must not offer a click that silently does nothing — and a
        /// host with no opener (the Desktop harness) must not offer one either.
        /// </summary>
        [Fact]
        public void ARowWithNowhereToGoCannotBeOpened()
        {
            Assert.False(Row(line: 0).CanOpen);
            Assert.False(Row().CanOpen); // no opener supplied by these tests
        }

        /// <summary>
        /// Three states, not two: "the user set it", "you set it here" and "you set it in an earlier
        /// conversation" are different answers, and only the last explains a breakpoint the user does not
        /// remember agreeing to.
        /// </summary>
        [Fact]
        public void OwnershipHasThreeStates()
        {
            Assert.Null(Row(setByAgent: false, thisConversation: null).ThisConversation);
            Assert.True(Row(setByAgent: true, thisConversation: true).ThisConversation);
            Assert.False(Row(setByAgent: true, thisConversation: false).ThisConversation);
        }
    }
}
