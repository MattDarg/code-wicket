using System;
using System.Collections.Generic;
using System.IO;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Attached images in the exported / copied transcript (issue #118).
    /// <para>
    /// The rule the whole feature rests on reaches here too: <b>a message must never read as though it
    /// carried no image.</b> The export broke it twice over — an image-only message was dropped
    /// entirely, and one with text kept the text and silently lost the picture.
    /// </para>
    /// </summary>
    public class TranscriptMarkdownImageTests : IDisposable
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        private readonly string _file = Path.Combine(
            Path.GetTempPath(), "cwkt-export-" + Guid.NewGuid().ToString("N") + ".png");

        public TranscriptMarkdownImageTests() => File.WriteAllBytes(_file, Png);

        public void Dispose()
        {
            try { File.Delete(_file); } catch { /* best-effort cleanup */ }
        }

        private static string Build(MessageItemViewModel message, TranscriptMarkdown.ImageExport mode) =>
            TranscriptMarkdown.Build("Session", new List<ChatItemViewModel> { message }, mode);

        private AttachmentViewModel Saved(string name = "snip.png") =>
            new AttachmentViewModel(name, "image/png", bytes: null, filePath: _file);

        [Fact]
        public void AnImageOnlyMessageSurvivesTheExport()
        {
            // It used to vanish: the renderer returned null on empty text, so the whole turn — not just
            // its picture — was absent from the document. Pasting a screenshot and pressing Enter is a
            // complete message, and the export has to agree.
            var message = new MessageItemViewModel(
                MessageRole.User, string.Empty, null, new[] { Saved() });

            var markdown = Build(message, TranscriptMarkdown.ImageExport.Link);

            Assert.Contains("## User", markdown);
            Assert.Contains("snip.png", markdown);
        }

        [Fact]
        public void AMessageWithBothKeepsItsTextAndItsImage()
        {
            var message = new MessageItemViewModel(
                MessageRole.User, "why is the button cut off?", null, new[] { Saved() });

            var markdown = Build(message, TranscriptMarkdown.ImageExport.Link);

            Assert.Contains("why is the button cut off?", markdown);
            Assert.Contains("![snip.png]", markdown);
        }

        [Fact]
        public void TheExportEmbedsTheImageSoTheFileStandsAlone()
        {
            // The exported file is chosen and kept by the user. A link into their own LocalAppData is
            // portable to nobody and is deleted by retention within days, so an export built from links
            // is an artifact that silently rots.
            var markdown = Build(
                new MessageItemViewModel(MessageRole.User, "look", null, new[] { Saved() }),
                TranscriptMarkdown.ImageExport.Embed);

            Assert.Contains("![snip.png](data:image/png;base64,", markdown);
            Assert.Contains(Convert.ToBase64String(Png), markdown);
        }

        [Fact]
        public void TheClipboardFormLinksRatherThanEmbedding()
        {
            // "Copy all" is pasted into an issue or a chat; megabytes of base64 there is hostile, and
            // the link's shorter life is the right trade for a payload that lives until the next paste.
            var markdown = Build(
                new MessageItemViewModel(MessageRole.User, "look", null, new[] { Saved() }),
                TranscriptMarkdown.ImageExport.Link);

            Assert.DoesNotContain("base64", markdown);
            Assert.Contains("![snip.png](file:///", markdown);
        }

        [Fact]
        public void AnImageWhoseFileIsGoneIsNamedRatherThanDropped()
        {
            // Retention sweeps the attachment directory, so a restored transcript outlives its pictures.
            // Saying nothing would have the document assert the message carried no image.
            var message = new MessageItemViewModel(
                MessageRole.User, "look", null,
                new[] { new AttachmentViewModel("gone.png", "image/png", bytes: null, filePath: "C:\\nope\\gone.png") });

            var markdown = Build(message, TranscriptMarkdown.ImageExport.Embed);

            Assert.Contains("gone.png", markdown);
            Assert.Contains("no longer available", markdown);
        }

        [Fact]
        public void TheNameIsEscapedSoItCannotEndTheAltTextEarly()
        {
            // The name comes from a dropped file, so a bracket in it is entirely possible and would
            // otherwise break the link syntax around it.
            var markdown = Build(
                new MessageItemViewModel(MessageRole.User, "x", null, new[] { Saved("shot [1].png") }),
                TranscriptMarkdown.ImageExport.Link);

            Assert.Contains("shot \\[1\\].png", markdown);
        }

        [Fact]
        public void AMessageWithNoTextAndNoImagesIsStillDropped()
        {
            // The original behaviour, kept: an empty bubble is noise, and only an attachment earns one
            // its place in the document.
            var markdown = Build(
                new MessageItemViewModel(MessageRole.User, "   "),
                TranscriptMarkdown.ImageExport.Embed);

            Assert.Equal(string.Empty, markdown);
        }

        [Fact]
        public void AnAssistantMessageIsUnaffected()
        {
            // Attachments only ever ride the user's own turns; the assistant path must render exactly
            // as it did before.
            var markdown = Build(
                new MessageItemViewModel(MessageRole.Assistant, "here is **why**"),
                TranscriptMarkdown.ImageExport.Embed);

            Assert.Contains("## Assistant", markdown);
            Assert.Contains("here is **why**", markdown);
            Assert.DoesNotContain("![", markdown);
        }
    }
}
