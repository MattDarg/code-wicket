using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The chat's colour-emoji seam: EmojiText segmentation (which spans of a literal become
    /// emoji inlines vs plain Runs) and ChatClipboard round-trips (copied text must yield the
    /// real emoji characters / checkbox stand-ins, which WPF's stock TextRange.Text drops).
    /// WPF text elements demand an STA thread; xunit runs MTA, so each test body runs via RunSta.
    /// </summary>
    public class EmojiTextTests
    {
        // (isEmoji, text) per produced inline: Runs → (false, run text); emoji containers →
        // (true, their ChatClipboard stand-in, i.e. the original sequence).
        private static List<(bool IsEmoji, string Text)> Segment(string text)
        {
            var result = new List<(bool, string)>();
            var paragraph = new Paragraph();
            EmojiText.AppendTo(paragraph.Inlines, text, fontSize: 13);
            foreach (var inline in paragraph.Inlines)
            {
                if (inline is Run run)
                    result.Add((false, run.Text));
                else if (inline is InlineUIContainer container)
                    result.Add((true, ChatClipboard.GetCopyText(container) ?? string.Empty));
                else
                    result.Add((false, "<unexpected:" + inline.GetType().Name + ">"));
            }
            return result;
        }

        [Fact]
        public void PlainTextStaysOneRun() => RunSta(() =>
        {
            var segments = Segment("no emoji here, just text with punctuation!");
            var segment = Assert.Single(segments);
            Assert.False(segment.IsEmoji);
            Assert.Equal("no emoji here, just text with punctuation!", segment.Text);
        });

        [Fact]
        public void PlainAsciiSkipsTheRegex() =>
            // The fast path itself (no STA needed — pure scan). Note: digits/'#'/'*' CAN start an
            // emoji sequence (keycaps like 1️⃣), so the probe string must avoid them.
            Assert.False(EmojiText.MightContainEmoji("dotnet build succeeded with no errors!"));

        [Theory]
        [InlineData("✅")]              // single codepoint + emoji presentation
        [InlineData("❤️")]             // VS16 variation selector
        [InlineData("👍🏽")]             // skin-tone modifier
        [InlineData("👩‍👩‍👧‍👧")]       // ZWJ family sequence
        [InlineData("🇬🇧")]             // flag (regional indicator pair)
        [InlineData("1️⃣")]             // keycap sequence
        public void SingleSequenceBecomesOneEmojiInline(string sequence) => RunSta(() =>
        {
            var segments = Segment(sequence);
            var segment = Assert.Single(segments);
            Assert.True(segment.IsEmoji);
            Assert.Equal(sequence, segment.Text);
        });

        [Fact]
        public void MixedTextSplitsAroundTheEmoji() => RunSta(() =>
        {
            var segments = Segment("build ✅ done");
            Assert.Equal(new[] { (false, "build "), (true, "✅"), (false, " done") }, segments);
        });

        [Fact]
        public void AdjacentEmojiEachGetTheirOwnInline() => RunSta(() =>
        {
            var segments = Segment("🚀🚀");
            Assert.Equal(2, segments.Count);
            Assert.All(segments, s => Assert.True(s.IsEmoji));
            Assert.All(segments, s => Assert.Equal("🚀", s.Text));
        });

        [Fact]
        public void EmojiAtStartAndEnd() => RunSta(() =>
        {
            var segments = Segment("✅ middle ❌");
            Assert.Equal(new[] { (true, "✅"), (false, " middle "), (true, "❌") }, segments);
        });

        [Fact]
        public void ClipboardRoundTripsEmojiAndTaskList() => RunSta(() =>
        {
            // Through the real public seam (the same path the assistant bubble uses).
            var viewer = new FlowDocumentScrollViewer();
            MarkdownText.SetText(viewer, "Status ✅ and family 👩‍👩‍👧‍👧\n\n- [x] ticked\n- [ ] unticked");
            var doc = viewer.Document;
            Assert.NotNull(doc);

            var copied = ChatClipboard.GetText(new TextRange(doc!.ContentStart, doc.ContentEnd));
            Assert.Contains("Status ✅ and family 👩‍👩‍👧‍👧", copied);
            Assert.Contains("[x] ticked", copied);
            Assert.Contains("[ ] unticked", copied);
        });

        [Fact]
        public void ClipboardClampsToTheSelectionEnd() => RunSta(() =>
        {
            var doc = new FlowDocument(new Paragraph(new Run("abcdef")));
            // Symbol offsets: +1 into the paragraph, +1 into the run, then one per char.
            var end = doc.ContentStart.GetPositionAtOffset(2 + 3)!;
            Assert.Equal("abc", ChatClipboard.GetText(new TextRange(doc.ContentStart, end)));
        });

        [Fact]
        public void ClipboardKeepsParagraphBreaks() => RunSta(() =>
        {
            var viewer = new FlowDocumentScrollViewer();
            MarkdownText.SetText(viewer, "first\n\nsecond");
            var copied = ChatClipboard.GetText(new TextRange(viewer.Document!.ContentStart, viewer.Document.ContentEnd));
            Assert.Contains("first" + Environment.NewLine, copied);
            Assert.Contains("second", copied);
        });

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
