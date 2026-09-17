using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeWicket.Shell;
using CodeWicket.UI.Input;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The two pure seams behind pasted images (issue #118): what a clipboard payload is decided to be,
    /// and where its bytes end up on disk. Both take untrusted input — a media type off the clipboard
    /// and a conversation id off the backend — and both put it into a filename or a wire field.
    /// </summary>
    public class AttachmentTests
    {
        private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        private static readonly byte[] JpegHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] GifHeader = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 };

        private static byte[] WebpHeader()
        {
            var bytes = new byte[12];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
            return bytes;
        }

        // --- format sniffing ------------------------------------------------------------------

        [Fact]
        public void TheMediaTypeComesFromTheMagicBytes()
        {
            // Not from a file extension and not from the clipboard's own claim: this value is what the
            // backend is told the bytes ARE, and a wrong one is rejected by the model API far from
            // where it started.
            Assert.Equal("image/png", ClipboardImages.SniffMimeType(PngHeader));
            Assert.Equal("image/jpeg", ClipboardImages.SniffMimeType(JpegHeader));
            Assert.Equal("image/gif", ClipboardImages.SniffMimeType(GifHeader));
            Assert.Equal("image/webp", ClipboardImages.SniffMimeType(WebpHeader()));
        }

        [Fact]
        public void SomethingThatIsNotOneOfTheFourAcceptedFormatsIsNotAnImage()
        {
            Assert.Null(ClipboardImages.SniffMimeType(Encoding.ASCII.GetBytes("%PDF-1.7 blah")));
            Assert.Null(ClipboardImages.SniffMimeType(Encoding.ASCII.GetBytes("<html><body>hi</body>")));
        }

        [Fact]
        public void ATruncatedPayloadIsNotAnImage()
        {
            // Shorter than the longest signature we test, so the sniff must bounds-check rather than
            // index off the end of a clipboard payload someone else produced.
            Assert.Null(ClipboardImages.SniffMimeType(new byte[] { 0x89, 0x50, 0x4E }));
            Assert.Null(ClipboardImages.SniffMimeType(Array.Empty<byte>()));
        }

        // --- the CanExecute gate --------------------------------------------------------------

        [Fact]
        public void AnImageOnlyClipboardIsRecognisedWithoutDecodingIt()
        {
            // The shape that breaks paste: a screen capture publishes NO text format, so WPF
            // reports the Paste command as unexecutable and never raises DataObject.Pasting. This gate
            // is what re-enables the command, and it has to see an image with nothing beside it.
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(PngHeader));

            Assert.True(ClipboardImages.HasImage(data));
        }

        [Fact]
        public void ATextOnlyClipboardIsNotAnImage()
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "just some text");

            Assert.False(ClipboardImages.HasImage(data));
            Assert.False(ClipboardImages.HasImage(null));
        }

        [Fact]
        public void TheGateAnswersOnFORMATSAndDoesNotDecode()
        {
            // Deliberately garbage under a real image format name. CanExecute is re-queried by WPF's
            // CommandManager on essentially every focus and input change, so decoding there would cost
            // a full megabyte-scale decode over and over just to grey a menu item in or out. Saying yes
            // here and no at TryRead is the harmless direction — the paste does nothing; the reverse
            // would grey out a paste that would have worked.
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(new byte[] { 1, 2, 3 }));

            Assert.True(ClipboardImages.HasImage(data));
            Assert.False(ClipboardImages.TryRead(data, out _));
        }

        // --- downscaling ----------------------------------------------------------------------

        [Fact]
        public void AnImageLargerThanTheCapIsScaledDownPreservingItsAspectRatio()
        {
            var source = Bitmap(3840, 2160);

            var scaled = ClipboardImages.Downscale(source);

            Assert.Equal(ClipboardImages.MaxEdgePixels, scaled.PixelWidth);
            // 1568 / 3840 * 2160 = 882
            Assert.Equal(882, scaled.PixelHeight);
        }

        [Fact]
        public void ThePortraitCaseScalesOnItsLongEdgeToo()
        {
            var scaled = ClipboardImages.Downscale(Bitmap(1000, 4000));

            Assert.Equal(ClipboardImages.MaxEdgePixels, scaled.PixelHeight);
            Assert.Equal(392, scaled.PixelWidth);
        }

        [Fact]
        public void AnImageWithinTheCapIsReturnedUntouched()
        {
            // Reference equality, not just size: an image that already fits must not be resampled at
            // all — that would cost fidelity to change nothing.
            var source = Bitmap(800, 600);

            Assert.Same(source, ClipboardImages.Downscale(source));
        }

        [Fact]
        public void AnImageExactlyAtTheCapIsNotUpscaledOrResampled()
        {
            var source = Bitmap(ClipboardImages.MaxEdgePixels, 400);

            Assert.Same(source, ClipboardImages.Downscale(source));
        }

        // --- the unset-alpha DIB (the ZoomIt case) --------------------------------------------

        [Fact]
        public void ADibWhoseAlphaWasNeverSetIsTreatedAsOpaqueRatherThanInvisible()
        {
            // Measured on a real ZoomIt capture: it publishes no PNG, so the DIB fallback runs, and WPF
            // hands the DIB back as Bgra32 with alpha zero everywhere and colour everywhere. Encoded as
            // it stands that produces a BLANK image — worse than refusing the paste, because a blank
            // picture is what would reach the agent.
            var source = Bgra32(64, 32, b: 0x40, g: 0x80, r: 0xC0, a: 0x00);

            var recovered = ClipboardImages.RecoverUnsetAlpha(source);

            Assert.NotSame(source, recovered);
            Assert.Equal(PixelFormats.Bgr32, recovered.Format);
            Assert.True(IsOpaque(recovered), "every pixel must be opaque after recovery");
        }

        [Fact]
        public void AnImageWithRealAlphaAnywhereIsLeftEntirelyAlone()
        {
            // One opaque pixel is enough to prove the channel was written, and from there the
            // transparency in the rest of the image is the author's and not ours to discard.
            var source = Bgra32(8, 8, b: 0x10, g: 0x20, r: 0x30, a: 0x00, opaqueFirstPixel: true);

            Assert.Same(source, ClipboardImages.RecoverUnsetAlpha(source));
        }

        [Fact]
        public void AGenuinelyEmptyImageIsNotDressedUpAsBlack()
        {
            // Alpha nowhere AND colour nowhere: there is nothing behind the transparency, and dropping
            // the channel would turn a blank image into a black one — no better, and a claim about
            // content that isn't there. This is why the condition needs both halves.
            var source = Bgra32(8, 8, b: 0, g: 0, r: 0, a: 0);

            Assert.Same(source, ClipboardImages.RecoverUnsetAlpha(source));
        }

        [Fact]
        public void AFormatWithNoAlphaChannelIsNeverTouched()
        {
            var source = Bitmap(16, 16); // Bgra32 all-zero would match; this is the no-alpha guard
            var bgr = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            bgr.Freeze();

            Assert.Same(bgr, ClipboardImages.RecoverUnsetAlpha(bgr));
        }

        [Fact]
        public void TheWholeDibPathProducesAVisibleImage()
        {
            // End to end through TryRead, which is where the blank PNG actually came from: a data
            // object carrying only a bitmap, exactly as a capture tool with no PNG format leaves it.
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, Bgra32(32, 24, b: 0x11, g: 0x22, r: 0x33, a: 0x00));

            Assert.True(ClipboardImages.TryRead(data, out var image));

            using var stream = new MemoryStream(image.Bytes);
            var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.True(IsOpaque(decoded), "the attached PNG must not be fully transparent");
        }

        /// <summary>A uniform Bgra32 bitmap, optionally with its first pixel made opaque.</summary>
        private static BitmapSource Bgra32(int width, int height, byte b, byte g, byte r, byte a,
            bool opaqueFirstPixel = false)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
            if (opaqueFirstPixel)
                pixels[3] = 0xFF;

            var source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }

        private static bool IsOpaque(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0xFF)
                    return false;
            }
            return true;
        }

        // --- storage --------------------------------------------------------------------------

        [Fact]
        public void TheFileNameIsContentAddressedSoTheSameImageIsWrittenOnce()
        {
            var first = AttachmentStore.FileName("abc", "image/png", new byte[] { 1, 2, 3 });
            var same = AttachmentStore.FileName("abc", "image/png", new byte[] { 1, 2, 3 });
            var different = AttachmentStore.FileName("abc", "image/png", new byte[] { 1, 2, 4 });

            Assert.Equal(first, same);
            Assert.NotEqual(first, different);
        }

        [Fact]
        public void TheConversationIdLeadsTheFileNameSoAListingGroupsByConversation()
        {
            // Grouping without subdirectories is deliberate: RetentionSweep is non-recursive, so a
            // nested layout would be swept at its top level only — i.e. never.
            Assert.StartsWith("conv1-", AttachmentStore.FileName("conv1", "image/png", new byte[] { 1 }));
        }

        [Fact]
        public void AConversationIdIsSanitizedBeforeItReachesAPath()
        {
            // The id comes from the backend, and it ends up in a filename.
            var name = AttachmentStore.FileName("../../evil\\id:x", "image/png", new byte[] { 1 });

            Assert.DoesNotContain("..", name);
            Assert.DoesNotContain("/", name);
            Assert.DoesNotContain("\\", name);
            Assert.DoesNotContain(":", name);
        }

        [Fact]
        public void AnEmptyConversationIdStillProducesAUsableName()
        {
            // Real case: the first message of a conversation is recorded before any session has opened.
            var name = AttachmentStore.FileName(string.Empty, "image/png", new byte[] { 1 });

            Assert.StartsWith("session-", name);
            Assert.EndsWith(".png", name);
        }

        [Fact]
        public void TheExtensionComesFromTheMediaTypeAndAnUnknownOneIsNotDerivedFromIt()
        {
            Assert.Equal(".png", AttachmentStore.Extension("image/png"));
            Assert.Equal(".jpg", AttachmentStore.Extension("image/jpeg"));
            Assert.Equal(".gif", AttachmentStore.Extension("image/gif"));
            Assert.Equal(".webp", AttachmentStore.Extension("image/webp"));

            // A mime type is untrusted input on its way into a filename, so an unrecognised one becomes
            // a fixed extension rather than anything built out of the string.
            Assert.Equal(".bin", AttachmentStore.Extension("image/../../evil"));
            Assert.Equal(".bin", AttachmentStore.Extension(null));
        }

        [Fact]
        public void SavingIsBestEffortAndReturnsNullRatherThanThrowingOnEmptyContent()
        {
            // The bytes have already gone to the agent by the time Save is called, so a failure here
            // costs the transcript its thumbnail and must never take the send down.
            Assert.Null(AttachmentStore.Save("c", "image/png", Array.Empty<byte>()));
            Assert.Null(AttachmentStore.Save("c", "image/png", null!));
        }

        // --- the file picker ------------------------------------------------------------------

        [Fact]
        public void APickedImageFileIsReadExactlyAsAPasteIs()
        {
            // The "Add - Image..." gesture is a different GESTURE, not a different payload: it lands on
            // TryReadFile, which sniffs, decodes and normalizes with the same code a paste does. What is
            // pinned here is the pass-through - a file already inside the size caps reaches the composer
            // byte for byte, so picking a PNG does not silently re-encode it.
            var bytes = Png(64, 48);
            var path = WriteTemp("screenshot.png", bytes);
            try
            {
                Assert.True(ClipboardImages.TryReadFile(path, out var image));
                Assert.Equal("image/png", image.MimeType);
                Assert.Equal("screenshot.png", image.Name);
                Assert.Equal(bytes, image.Bytes);
            }
            finally
            {
                DeleteTemp(path);
            }
        }

        [Fact]
        public void AFileThatIsNotAnImageIsRefusedHoweverItIsNamed()
        {
            // The dialog's filter is a hint and the user can defeat it with "All files", so the bytes
            // are what decide. Refusing here is what lets the picker SAY it attached nothing, rather
            // than sending a message that looks as though it carries a picture (issue #118's rule).
            var path = WriteTemp("not-really.png", Encoding.ASCII.GetBytes("%PDF-1.7 and then some"));
            try
            {
                Assert.False(ClipboardImages.TryReadFile(path, out _));
            }
            finally
            {
                DeleteTemp(path);
            }
        }

        [Fact]
        public void AMissingOrEmptyPickIsRefusedRatherThanThrowing()
        {
            // A file can vanish, be locked or be zero bytes between the pick and the read; none of
            // those is an exception's business, because the caller's job is to report the refusal.
            var empty = WriteTemp("empty.png", Array.Empty<byte>());
            try
            {
                Assert.False(ClipboardImages.TryReadFile(empty, out _));
                Assert.False(ClipboardImages.TryReadFile(
                    Path.Combine(Path.GetTempPath(), "cwkt-no-such-" + Guid.NewGuid().ToString("N") + ".png"),
                    out _));
                Assert.False(ClipboardImages.TryReadFile(null, out _));
                Assert.False(ClipboardImages.TryReadFile(string.Empty, out _));
            }
            finally
            {
                DeleteTemp(empty);
            }
        }

        private static string WriteTemp(string name, byte[] bytes)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cwkt-pick-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void DeleteTemp(string path)
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>Real PNG bytes, so the decode under test decodes something.</summary>
        private static byte[] Png(int width, int height)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Bitmap(width, height)));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }

        private static BitmapSource Bitmap(int width, int height)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            var source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }
    }
}
