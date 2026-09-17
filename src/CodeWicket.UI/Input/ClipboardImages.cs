using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodeWicket.UI.Input
{
    /// <summary>
    /// Pulls an image off a clipboard data object so it can be attached to a prompt (issue #118).
    ///
    /// <para><b>Format priority, not <c>Clipboard.GetImage()</c>.</b> The convenient call goes through
    /// <c>CF_DIB</c>, which carries no alpha channel in its common form — a Snipping Tool capture with
    /// rounded corners or a drop shadow comes back with those pixels filled black or garbled. Nearly
    /// every source that can produce a screenshot also publishes a real PNG under the registered
    /// <c>"PNG"</c> format (Snipping Tool, Chrome, Firefox, Paint.NET, Greenshot, ShareX), so that is
    /// asked for first and the DIB is the last resort rather than the first.</para>
    ///
    /// <para><b>Bytes are passed through untouched when they can be.</b> A screenshot is already a PNG;
    /// re-encoding it would cost fidelity for nothing. Re-encoding happens only when the image is
    /// bigger than <see cref="MaxEdgePixels"/> (where it also saves the model a great many tokens for
    /// detail it cannot resolve) or when the source was a raw bitmap with no file form of its own.</para>
    /// </summary>
    internal static class ClipboardImages
    {
        /// <summary>
        /// Longest edge we send. Claude's vision stack downsamples above roughly this, so anything
        /// larger is paid for in tokens and upload time and then discarded — and a 4K screen capture
        /// is comfortably past it. Images at or under this are never resampled.
        /// </summary>
        internal const int MaxEdgePixels = 1568;

        /// <summary>
        /// Hard ceiling on what we will hand to a backend, after any downscale. Well under the 5 MB
        /// per-image limit the Anthropic API documents, with room for the base64 expansion (4/3) on
        /// top. An image that is still over this is refused rather than silently truncated.
        /// </summary>
        internal const int MaxBytes = 3 * 1024 * 1024;

        /// <summary>
        /// The most we will read off disk to find out whether a file is an image at all. The sniff
        /// below needs a dozen bytes and <see cref="TryNormalize"/> refuses anything genuinely
        /// oversized, so this only stops a mis-picked ISO being pulled into memory to be rejected.
        /// </summary>
        private const long MaxFileBytes = 64L * 1024 * 1024;

        /// <summary>
        /// The Open dialog's filter for the composer's "Attach image" gesture. The extensions are the
        /// four formats <see cref="SniffMimeType"/> knows, so the picker offers exactly what a paste
        /// would accept — and "All files" is deliberately kept, because the extension is a hint and the
        /// MAGIC BYTES are what decide: an extensionless capture is readable and a .png that is really
        /// a bitmap is refused, whichever filter the file was shown under.
        /// </summary>
        public const string FileDialogFilter =
            "Images (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|All files (*.*)|*.*";

        /// <summary>The registered clipboard format name for a real PNG. Not a <c>DataFormats</c> constant.</summary>
        private const string PngFormat = "PNG";

        /// <summary>
        /// Whether the data object carries something that MIGHT be an attachable image — a cheap
        /// format-presence check, with no decode. Deliberately separate from <see cref="TryRead"/>:
        /// this answers <c>CanExecute</c>, which WPF's <c>CommandManager</c> re-queries on essentially
        /// every focus and input change, and decoding a multi-megabyte screenshot there would be paid
        /// for over and over just to grey a menu item in or out.
        /// <para>
        /// It may say yes where <see cref="TryRead"/> later says no (an undecodable payload, a format
        /// we do not accept). That direction is harmless — the paste then does nothing — whereas the
        /// reverse would grey out a paste that would have worked.
        /// </para>
        /// </summary>
        public static bool HasImage(IDataObject? data)
        {
            if (data is null)
                return false;

            try
            {
                return data.GetDataPresent(PngFormat)
                    || data.GetDataPresent(DataFormats.FileDrop)
                    || data.GetDataPresent(DataFormats.Bitmap, autoConvert: true);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the first image on the data object, or returns false when there is none (which is the
        /// ordinary case for a text paste and must stay cheap and silent). A malformed or undecodable
        /// payload also returns false: the paste then falls through to whatever else is on the
        /// clipboard, which is what the user would expect from a source we could not read.
        /// </summary>
        public static bool TryRead(IDataObject? data, out ClipboardImage image) =>
            TryRead(data, out image, out _);

        /// <summary>
        /// As above, and says why when it refuses something that WAS an image.
        /// <para>
        /// <b>The sniff is the divider, and it is what keeps an ordinary paste silent.</b> Bytes that
        /// are not one of the four formats leave this null: the paste then falls through to the text
        /// on the clipboard, which is the common case and must stay cheap and quiet. Bytes that ARE
        /// one and were refused anyway — over the pixel budget, truncated, too big even after
        /// resizing — set it, because at that point there is nothing else the paste could have meant
        /// and the fall-through has nothing to fall through to. Saying nothing there leaves the user
        /// believing they attached a picture they have not, which is the outcome issue #118 exists to
        /// rule out; the picker already refuses to be silent for the same reason.
        /// </para>
        /// </summary>
        public static bool TryRead(IDataObject? data, out ClipboardImage image, out string? refusal)
        {
            image = default!;
            refusal = null;
            if (data is null)
                return false;

            try
            {
                // Not one `||` chain: a refusal has to survive to the caller, and the later readers
                // would overwrite it with their own null. A recognised-but-refused payload also stops
                // the search — the other shapes on the data object are the same picture, so trying
                // them can only produce the same refusal or a worse-quality one.
                if (TryReadPngFormat(data, out image, out refusal) || refusal is not null)
                    return refusal is null;
                if (TryReadFileDrop(data, out image, out refusal) || refusal is not null)
                    return refusal is null;
                return TryReadBitmap(data, out image);
            }
            catch (Exception)
            {
                // A clipboard read can fail for reasons that have nothing to do with us — the owning
                // application closing mid-read, a transient OLE lock, an unsupported codec. None is
                // worth failing the paste over, and none of them is a fact about the user's file, so
                // nothing is claimed about one.
                refusal = null;
                return false;
            }
        }

        /// <summary>The registered PNG format: a stream of real PNG bytes, alpha intact.</summary>
        private static bool TryReadPngFormat(IDataObject data, out ClipboardImage image, out string? refusal)
        {
            image = default!;
            refusal = null;
            if (!data.GetDataPresent(PngFormat) || data.GetData(PngFormat) is not Stream stream)
                return false;

            var bytes = ReadAll(stream);
            if (bytes.Length == 0)
                return false;

            var attached = TryNormalize(bytes, "Pasted image", out image, out var why, out var reportable);
            refusal = reportable ? why : null;
            return attached;
        }

        /// <summary>
        /// An image file copied in Explorer (or dragged in). Read from disk rather than asking the
        /// shell to render it, so a JPEG stays a JPEG instead of being re-encoded to a larger PNG.
        /// </summary>
        private static bool TryReadFileDrop(IDataObject data, out ClipboardImage image, out string? refusal)
        {
            image = default!;
            refusal = null;
            if (!data.GetDataPresent(DataFormats.FileDrop) ||
                data.GetData(DataFormats.FileDrop) is not string[] paths)
                return false;

            foreach (var path in paths)
            {
                if (TryReadFileCore(path, out image, out var why, out var reportable))
                    return true;

                // A path that was never an image is skipped, exactly as before — a multi-file copy
                // can legitimately hold one picture among several documents. One the refusal is
                // genuinely ABOUT stops the loop and carries its reason out, because that is the
                // file the user was trying to paste.
                if (reportable)
                {
                    refusal = why;
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads one image FILE: a path the user picked in the composer's "Attach image" dialog, or one
        /// off a clipboard/drag <c>FileDrop</c>. Same read, same sniff, same normalization as a pasted
        /// image, because <b>the picker is a different GESTURE, not a different payload</b> — anything a
        /// paste would take it takes, and anything a paste would refuse it refuses, so the two can never
        /// drift into disagreeing about what an attachable image is.
        /// <para>
        /// Returns false for a file that is missing, unreadable, empty, absurdly large, or not one of
        /// the four formats the backends accept. The caller must SAY so: a picker that appeared to
        /// attach something and did not is the unacceptable outcome (issue #118's rule, one gesture
        /// over), which is why this reports rather than swallowing.
        /// </para>
        /// </summary>
        public static bool TryReadFile(string? path, out ClipboardImage image) =>
            TryReadFile(path, out image, out _);

        /// <summary>
        /// As above, and says WHY when it refuses — one sentence naming the check that failed, for a
        /// caller with a user to answer to (the composer's picker).
        /// <para>
        /// <b>The reason is per-refusal because the causes are not one cause.</b> A single message
        /// covering all of them has to enumerate, and an enumeration goes stale the moment a check is
        /// added: the decoded-pixel budget landed on this path while the picker still explained every
        /// refusal as a format or an unreadable file, so a valid PNG that was merely too large was
        /// told its bytes were not a PNG. Every remedy that implied — re-save it, unlock it, fix the
        /// extension — fails, and the one that works was not mentioned. Same rule as
        /// <c>Core.Ide.FileWriteRefusal</c>: say what was checked, and name no cause that does not
        /// hold.
        /// </para>
        /// </summary>
        public static bool TryReadFile(string? path, out ClipboardImage image, out string? refusal) =>
            TryReadFileCore(path, out image, out refusal, out _);

        /// <summary>
        /// The body of the two above, with the extra fact the PASTE path needs and the picker does
        /// not: whether the refusal is about THIS FILE, or merely about the payload not being an
        /// image. The picker reports either, because a file was chosen in a dialog and something has
        /// to be said about it; a paste reports only the first, so that copying a document and
        /// pasting still falls through to the ordinary text paste.
        /// </summary>
        /// <param name="reportable">
        /// True once we know the refusal is a fact about the user's file rather than "this was not an
        /// image": the bytes got past the sniff, OR they exist and could not be read at all.
        /// <para>
        /// <b>Both halves, and the second was the miss.</b> The sniff alone cannot divide these when
        /// the read itself failed — there are no bytes to sniff — so an image file that exists and
        /// could not be opened fell through to the text paste and the gesture did nothing whatever
        /// (observed on a OneDrive file with the network off, 2026-09-11). That is the same
        /// nothing-there / couldn't-look distinction issue #272 is about, one gesture over: a file we
        /// read and found not to be an image is an answer, and a file we could not look at is not.
        /// </para>
        /// </param>
        private static bool TryReadFileCore(
            string? path, out ClipboardImage image, out string? refusal, out bool reportable)
        {
            image = default!;
            refusal = null;
            reportable = false;
            if (string.IsNullOrEmpty(path))
                return false;

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    // Not reportable: a multi-file paste skips these and tries the next path, and a
                    // stale clipboard entry is not something to interrupt the user about.
                    refusal = "not found.";
                    return false;
                }
                if (info.Length == 0)
                {
                    refusal = "empty.";
                    return false;
                }
                if (info.Length > MaxFileBytes)
                {
                    // The file's own size is not printed: it is a long, FormatSize takes an int, and
                    // the useful half of the sentence is the ceiling either way.
                    refusal = "larger than the " + ClipboardImage.FormatSize((int)MaxFileBytes)
                        + " this will read from disk.";
                    return false;
                }
            }
            catch (Exception)
            {
                refusal = "could not be examined.";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                // Exists, has a size, and would not open. Whatever it is, it is not "you pasted
                // something that was not an image", so the paste says so rather than falling silent.
                reportable = true;
                refusal = IsCloudOnly(info)
                    ? "stored online only and could not be downloaded. Connect to the network, or use "
                        + "File Explorer's \"Always keep on this device\", then try again."
                    : "could not be opened for reading.";
                return false;
            }

            return TryNormalize(bytes, Path.GetFileName(path), out image, out refusal, out reportable);
        }

        // A OneDrive (or any cloud-provider) placeholder whose content is not on this machine. Read
        // from the ATTRIBUTES, which are local by definition - asking does not trigger a download, so
        // this is safe on the path where a download has just failed.
        //
        // Spelled as raw values because the net472 half of this library has no FileAttributes member
        // for either recall flag.
        private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        private static bool IsCloudOnly(FileInfo info)
        {
            try
            {
                var attributes = info.Attributes;
                return (attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;
            }
            catch (Exception)
            {
                return false; // if we cannot even read the attributes, claim nothing about why
            }
        }

        /// <summary>
        /// The DIB fallback, reached only when nothing published a file-shaped image. WPF converts
        /// <c>CF_DIB</c> to a <see cref="BitmapSource"/> for us; what it cannot recover is the alpha
        /// the format dropped on the way in, which is the whole reason this is last.
        /// </summary>
        private static bool TryReadBitmap(IDataObject data, out ClipboardImage image)
        {
            image = default!;
            if (!data.GetDataPresent(DataFormats.Bitmap, autoConvert: true) ||
                data.GetData(DataFormats.Bitmap, autoConvert: true) is not BitmapSource source)
                return false;

            // Before the downscale: a resampler is free to premultiply, which would fold the RGB away
            // against the zero alpha this is here to correct.
            var encoded = Encode(Downscale(RecoverUnsetAlpha(source)));
            if (encoded.Length == 0 || encoded.Length > MaxBytes)
                return false;

            image = new ClipboardImage("Pasted image", "image/png", encoded);
            return true;
        }

        /// <summary>
        /// Treats a fully transparent DIB as an UNSET alpha channel rather than as transparency, and
        /// drops the channel so the colour shows.
        ///
        /// <para>Measured, on a real ZoomIt capture (issue #118 follow-up): the clipboard carries
        /// <c>Bitmap, System.Drawing.Bitmap, DeviceIndependentBitmap, Format17</c> and no PNG, so it
        /// takes this fallback; WPF hands the DIB back as <c>Bgra32</c>, and of its 602,847 pixels
        /// <b>0 had alpha set and every single one had colour</b>. The picture was entirely present in
        /// the RGB channels and entirely invisible. Encoding that to PNG produced a blank image —
        /// which is worse than refusing the paste, because a blank picture is what reaches the agent.
        /// </para>
        ///
        /// <para>The condition is deliberately narrow: alpha nowhere set AND colour somewhere set.
        /// "Alpha nowhere set" alone would also match a genuinely empty image, where there is nothing
        /// to recover and dropping the channel yields black instead of blank — no better. Requiring
        /// colour means this only ever fires where the pixels really are there.</para>
        ///
        /// <para>Row by row with an early exit, so the ordinary case pays almost nothing: an opaque
        /// image has alpha set in its first pixel and returns on the first row, and only the defect
        /// being corrected reads the whole image — one row's buffer at a time, never the whole
        /// bitmap.</para>
        /// </summary>
        internal static BitmapSource RecoverUnsetAlpha(BitmapSource source)
        {
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
                return source; // no alpha channel to misread

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            if (width <= 0 || height <= 0)
                return source;

            var anyColour = false;
            try
            {
                var stride = width * 4;
                var row = new byte[stride];
                for (var y = 0; y < height; y++)
                {
                    source.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
                    for (var i = 0; i < stride; i += 4)
                    {
                        if (row[i + 3] != 0)
                            return source; // real alpha somewhere — leave it entirely alone
                        if (!anyColour && (row[i] != 0 || row[i + 1] != 0 || row[i + 2] != 0))
                            anyColour = true;
                    }
                }
            }
            catch (Exception)
            {
                return source; // an unreadable source is the decoder's problem, not ours
            }

            if (!anyColour)
                return source; // genuinely empty; there is nothing behind the transparency

            var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }

        /// <summary>
        /// Decides whether some candidate bytes are an image we can send, and whether they can go as
        /// they are. Pass-through when the format is one the backends take and the pixels are already
        /// within <see cref="MaxEdgePixels"/>; a re-encode to PNG otherwise.
        /// </summary>
        private static bool TryNormalize(
            byte[] bytes, string name, out ClipboardImage image, out string? refusal, out bool reportable)
        {
            image = default!;
            refusal = null;
            reportable = false;

            var mimeType = SniffMimeType(bytes);
            if (mimeType is null)
            {
                refusal = "not a PNG, JPEG, GIF or WebP. The file's own bytes decide that, not its extension.";
                return false;
            }

            // Past the sniff: these bytes ARE an image, whatever happens below, so every refusal from
            // here on is a fact about the user's file and the paste path shows it.
            reportable = true;

            BitmapSource decoded;
            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    // The decoded size, from the header, before any pixel exists (pre-release
                    // security review): a dropped or picked file is bytes the user did not author, and the same
                    // sub-megabyte PNG that stalls the transcript would stall the paste.
                    if (!Markdown.ImageDecodeBudget.TryProbe(stream, out var width, out var height))
                    {
                        refusal = TooManyPixels(width, height);
                        return false;
                    }
                    decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
            }
            catch (Exception)
            {
                // Sniffed as an image but the codec disagrees — truncated download, wrong extension.
                refusal = "starts like " + mimeType + " but could not be decoded; it may be truncated or corrupt.";
                return false;
            }

            var oversized = decoded.PixelWidth > MaxEdgePixels || decoded.PixelHeight > MaxEdgePixels;
            if (!oversized && bytes.Length <= MaxBytes)
            {
                image = new ClipboardImage(name, mimeType, bytes);
                return true;
            }

            var encoded = Encode(Downscale(decoded));
            if (encoded.Length == 0)
            {
                refusal = "could not be re-encoded after resizing.";
                return false;
            }
            if (encoded.Length > MaxBytes)
            {
                refusal = "still " + ClipboardImage.FormatSize(encoded.Length) + " after resizing, over the "
                    + ClipboardImage.FormatSize(MaxBytes) + " limit.";
                return false;
            }

            image = new ClipboardImage(EnsureExtension(name, ".png"), "image/png", encoded);
            return true;
        }

        /// <summary>
        /// The refusal for an image over <see cref="Markdown.ImageDecodeBudget.MaxPixels"/> (pre-release
        /// security review) — the cause the single old message could not express and actively contradicted:
        /// these bytes ARE one of the four formats, and the file is neither missing, locked nor empty.
        /// <para>Megapixels are counted in units of 1,048,576, the same arithmetic as the constant, so
        /// the figure and the limit cannot disagree with each other or with the code.</para>
        /// </summary>
        private static string TooManyPixels(int width, int height) =>
            string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} × {1:N0} is {2:0.#} megapixels, over the {3:0.#} megapixel limit. Resize it and try again.",
                width,
                height,
                (double)width * height / (1024 * 1024),
                (double)Markdown.ImageDecodeBudget.MaxPixels / (1024 * 1024));

        /// <summary>
        /// Scales the longest edge down to <see cref="MaxEdgePixels"/>, preserving aspect ratio.
        /// Returns the source untouched when it already fits — never upscales, which would cost bytes
        /// to invent detail that was never there.
        /// </summary>
        internal static BitmapSource Downscale(BitmapSource source)
        {
            var longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest <= MaxEdgePixels || longest == 0)
                return source;

            var scale = (double)MaxEdgePixels / longest;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
        }

        private static byte[] Encode(BitmapSource source)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var output = new MemoryStream())
                {
                    encoder.Save(output);
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Identifies the format from its magic bytes rather than a file extension or the clipboard's
        /// own claim about itself, because the media type is what the backend is told the bytes are —
        /// and a wrong one is rejected by the model API rather than by us, far from where it started.
        /// Only the four formats the backends accept; anything else is not an image as far as this is
        /// concerned.
        /// </summary>
        internal static string? SniffMimeType(byte[] bytes)
        {
            if (bytes is null || bytes.Length < 12)
                return null;

            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                return "image/png";

            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";

            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return "image/gif";

            // RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";

            return null;
        }

        private static string EnsureExtension(string name, string extension) =>
            name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? name
                : Path.GetFileNameWithoutExtension(name) + extension;

        private static byte[] ReadAll(Stream stream)
        {
            if (stream is MemoryStream memory)
                return memory.ToArray();

            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
    }

    /// <summary>
    /// An image lifted off the clipboard, ready to attach. A plain class, not a record — this project
    /// has no <c>IsExternalInit</c> polyfill and must not gain one (see AGENTS.md).
    /// </summary>
    internal sealed class ClipboardImage
    {
        public ClipboardImage(string name, string mimeType, byte[] bytes)
        {
            Name = name;
            MimeType = mimeType;
            Bytes = bytes;
        }

        /// <summary>For the user's eye only — the chip's label and the thumbnail's tooltip.</summary>
        public string Name { get; }

        public string MimeType { get; }

        public byte[] Bytes { get; }

        /// <summary>e.g. "412 KB", for the chip.</summary>
        public string SizeLabel => FormatSize(Bytes.Length);

        internal static string FormatSize(int bytes) =>
            bytes >= 1024 * 1024
                ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024.0 * 1024.0))
                : string.Format(CultureInfo.CurrentCulture, "{0:0} KB", Math.Max(1, bytes / 1024));
    }
}
