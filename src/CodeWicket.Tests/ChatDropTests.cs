using CodeWicket.UI.Views;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Which drops the chat claims, and what the composer ends up holding. This is the feature: the
    /// reported bug was that a file dropped on the input box did nothing while one dropped anywhere else
    /// opened an editor tab, and both halves of that are decided by <see cref="ChatView.ClaimDrop"/>.
    /// </summary>
    /// <remarks>
    /// Pure and static, so no WPF tree and no <c>StaTest</c> — the <c>ComputeInputBounds</c> precedent.
    /// What these cannot reach is whether WPF raises the events at all, which is why the Desktop
    /// self-check asserts the mechanism (<c>AllowDrop</c> and the handler registrations) separately.
    /// </remarks>
    public sealed class ChatDropTests
    {
        /// <summary>
        /// Half the reported bug. A <c>TextBox</c>'s editor accepts only text formats, so it rejects a
        /// <c>FileDrop</c> outright and the drop appears to do nothing at all — the case a naive "leave the
        /// box alone" rule would reinstate.
        /// </summary>
        [Fact]
        public void FilesOverTheInputBox_AreClaimed()
        {
            Assert.Equal(
                ChatView.DropClaim.Files,
                ChatView.ClaimDrop(hasFiles: true, hasText: false, overInputBox: true));
        }

        /// <summary>
        /// The other half. Unclaimed, the drag routes past our content to Visual Studio, which opens the
        /// file in an editor tab — surprising enough on its own to be worth closing.
        /// </summary>
        [Fact]
        public void FilesOverTheTranscript_AreClaimed()
        {
            Assert.Equal(
                ChatView.DropClaim.Files,
                ChatView.ClaimDrop(hasFiles: true, hasText: false, overInputBox: false));
        }

        /// <summary>
        /// A mixed drag is still a file drag. A source that publishes a path alongside its text would
        /// otherwise have the text win, which drops a bare unspelled path into the box.
        /// </summary>
        [Fact]
        public void AMixedDrag_IsTreatedAsFiles()
        {
            Assert.Equal(
                ChatView.DropClaim.Files,
                ChatView.ClaimDrop(hasFiles: true, hasText: true, overInputBox: false));
        }

        /// <summary>
        /// Text over the box must be left alone, and it costs two things to get wrong: dragging a selection
        /// WITHIN the box stops moving it, and WPF's routing of a text drop through the paste pipeline goes
        /// away — so <c>DataObject.Pasting</c> never fires and dragged terminal output silently stops being
        /// cleaned, over the drag route only.
        /// </summary>
        [Fact]
        public void TextOverTheInputBox_IsLeftToTheTextBox()
        {
            Assert.Equal(
                ChatView.DropClaim.None,
                ChatView.ClaimDrop(hasFiles: false, hasText: true, overInputBox: true));
        }

        /// <summary>Text dropped on the transcript has nowhere else to go, so it comes to the composer.</summary>
        [Fact]
        public void TextOverTheTranscript_IsClaimed()
        {
            Assert.Equal(
                ChatView.DropClaim.TextIntoComposer,
                ChatView.ClaimDrop(hasFiles: false, hasText: true, overInputBox: false));
        }

        [Fact]
        public void ADragCarryingNeither_IsNotOurs()
        {
            Assert.Equal(
                ChatView.DropClaim.None,
                ChatView.ClaimDrop(hasFiles: false, hasText: false, overInputBox: false));
        }

        /// <summary>Dropped into the middle of a word, the path must not weld itself to the text either side.</summary>
        [Fact]
        public void InsertedMidWord_GainsSpacesOnBothSides()
        {
            var (insert, at, caret) = ChatView.ComposeInsertion("lookhere", 4, new[] { "a.cs" });

            Assert.Equal(" a.cs ", insert);
            Assert.Equal(4, at);
            Assert.Equal(4 + " a.cs".Length, caret);
        }

        /// <summary>
        /// The reverse, and not redundant: without it an unconditional "always pad" passes the test above
        /// and every drop against existing whitespace gains a double space.
        /// </summary>
        [Fact]
        public void InsertedAtAWordBoundary_AddsNoDoubleSpace()
        {
            // At the very end, against a space the user typed: no padding on either side.
            Assert.Equal("a.cs", ChatView.ComposeInsertion("look ", 5, new[] { "a.cs" }).Insert);

            // Between a space and a word: the trailing side still needs one, the leading side does not.
            Assert.Equal("a.cs ", ChatView.ComposeInsertion("look here", 5, new[] { "a.cs" }).Insert);

            // And between two spaces, neither does — the case that catches a rule padding unconditionally
            // on the side it happens to look at first.
            Assert.Equal("a.cs", ChatView.ComposeInsertion("look  here", 5, new[] { "a.cs" }).Insert);
        }

        /// <summary>An empty box must not gain a leading space — the commonest drop of all.</summary>
        [Fact]
        public void EmptyBox_GainsNoLeadingSpace()
        {
            var (insert, at, caret) = ChatView.ComposeInsertion("", 0, new[] { "a.cs" });

            Assert.Equal("a.cs", insert);
            Assert.Equal(0, at);
            Assert.Equal(4, caret);
        }

        /// <summary>
        /// A multi-select arrives as one space-separated run, in the order the user picked. No cap: they
        /// selected twenty, and a cap here would be a display limit quietly cutting data.
        /// </summary>
        [Fact]
        public void SeveralPaths_AreJoinedByOneSpaceInOrder()
        {
            Assert.Equal(
                "a.cs b.cs \"c d.cs\"",
                ChatView.ComposeInsertion("", 0, new[] { "a.cs", "b.cs", "\"c d.cs\"" }).Insert);
        }

        /// <summary>
        /// A drop that decoded nothing changes nothing — the caret does not move and no stray padding is
        /// left behind. The handler still marks the event handled, but that is its business, not this one's.
        /// </summary>
        [Fact]
        public void NoPayloads_InsertNothingAndLeaveTheCaret()
        {
            var (insert, at, caret) = ChatView.ComposeInsertion("hello", 3, new string[0]);

            Assert.Equal("", insert);
            Assert.Equal(3, at);
            Assert.Equal(3, caret);
        }

        /// <summary>An index past the end is clamped rather than throwing into a drop handler.</summary>
        [Fact]
        public void AnOutOfRangeIndex_IsClamped()
        {
            Assert.Equal(5, ChatView.ComposeInsertion("hello", 99, new[] { "a.cs" }).At);
            Assert.Equal(0, ChatView.ComposeInsertion("hello", -3, new[] { "a.cs" }).At);
        }

        /// <summary>
        /// Reported from the field (#62): dropping a second file landed it BEFORE the last character
        /// of the first.
        /// </summary>
        /// <remarks>
        /// <c>GetCharacterIndexFromPoint</c> answers a different question than its name suggests — it names
        /// the character CLOSEST to the point, never a gap between two. Dropped past the end of the text it
        /// returns the index of the FINAL character, so inserting there lands before it. Which side of that
        /// character the pointer fell on is the missing half, and nothing offline exercised the conversion
        /// because the checks passed either an explicit index or no point at all.
        /// </remarks>
        [Theory]
        // Pointer past the right edge of the last character of "ab" (each 10 wide): after it.
        [InlineData(1, 18.0, 10.0, 10.0, 2, 2)]
        // Just inside its left half: before it.
        [InlineData(1, 12.0, 10.0, 10.0, 2, 1)]
        // Exactly on the midpoint counts as before, so a drop is never nudged forward by a rounding tie.
        [InlineData(1, 15.0, 10.0, 10.0, 2, 1)]
        public void TheDropLandsOnTheSideOfTheCharacterThePointerIsOn(
            int nearest, double pointX, double charX, double charWidth, int textLength, int expected)
        {
            Assert.Equal(
                expected,
                ChatView.InsertionIndex(nearest, pointX, new System.Windows.Rect(charX, 0, charWidth, 12), textLength));
        }

        /// <summary>
        /// The end-of-text caret has a zero-width rect, so there is no side to be on; and a point that hit
        /// no character at all (an empty box) belongs at the end rather than throwing or landing at zero.
        /// </summary>
        [Fact]
        public void AZeroWidthOrMissingCharacter_LandsAtTheEnd()
        {
            Assert.Equal(5, ChatView.InsertionIndex(5, 99.0, new System.Windows.Rect(50, 0, 0, 12), 5));
            Assert.Equal(0, ChatView.InsertionIndex(-1, 4.0, System.Windows.Rect.Empty, 0));
            Assert.Equal(7, ChatView.InsertionIndex(-1, 4.0, System.Windows.Rect.Empty, 7));
        }
    }
}
