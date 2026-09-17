using System;
using System.IO;
using System.Windows.Media.Imaging;
using CodeWicket.Ipc;
using CodeWicket.UI.Input;
using CodeWicket.UI.Mvvm;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// One image attached to a message — first as a chip in the composer, then as a thumbnail on the
    /// bubble it was sent with (issue #118). A plain class, not a record: this project has no
    /// <c>IsExternalInit</c> polyfill and must not gain one (see AGENTS.md).
    ///
    /// <para>It holds the image in whichever form it currently exists in, and those are different
    /// forms at different times. A freshly pasted attachment is <see cref="Bytes"/> and nothing else —
    /// it is written to disk only when the message is actually sent, so a paste the user removes
    /// leaves no file. A restored one is <see cref="FilePath"/> and nothing else, because the
    /// transcript stores the path rather than the bytes. Both render, and the send path only ever
    /// looks at the bytes.</para>
    /// </summary>
    public sealed class AttachmentViewModel : ObservableObject
    {
        // Bounds the thumbnail's decode. The stored file can be up to the 1568px we send; a chip and a
        // bubble thumbnail need a fraction of that, and decoding at full size would hold megabytes of
        // bitmap per message in a transcript that never drops one.
        private const int ThumbnailDecodeWidth = 320;

        private BitmapSource? _thumbnail;
        private bool _thumbnailDecoded;

        public AttachmentViewModel(
            string name,
            string mimeType,
            byte[]? bytes,
            string? filePath = null,
            Action<AttachmentViewModel>? remove = null)
        {
            Name = name;
            MimeType = mimeType;
            Bytes = bytes;
            FilePath = filePath;
            SizeLabel = bytes is null ? string.Empty : ClipboardImage.FormatSize(bytes.Length);
            RemoveCommand = remove is null ? null : new RelayCommand(() => remove(this));
        }

        /// <summary>Internal: <see cref="ClipboardImage"/> is an input-layer detail, and the paste seam
        /// is the only caller. The view-model itself is public because the templates bind to it.</summary>
        internal static AttachmentViewModel FromClipboard(ClipboardImage image, Action<AttachmentViewModel> remove) =>
            new AttachmentViewModel(image.Name, image.MimeType, image.Bytes, filePath: null, remove);

        /// <summary>For the user's eye only — the chip's label and the thumbnail's tooltip.</summary>
        public string Name { get; }

        public string MimeType { get; }

        /// <summary>e.g. "412 KB". Empty on a restored attachment, whose bytes we never re-read.</summary>
        public string SizeLabel { get; }

        /// <summary>The raw image, until the message is sent and it has been written to disk. Null on
        /// a restored attachment — which is also why a restored message cannot be re-sent with it.</summary>
        public byte[]? Bytes { get; }

        /// <summary>Where the image was saved once its message was sent; null until then.</summary>
        public string? FilePath { get; private set; }

        /// <summary>Only the composer's chips can be removed; a sent message's cannot.</summary>
        public RelayCommand? RemoveCommand { get; }

        public bool CanRemove => RemoveCommand is not null;

        /// <summary>
        /// The same attachment with its remove command dropped, for when it leaves the composer. Once a
        /// message is taken, its images are no longer the strip's to remove — and a button that is
        /// still shown but silently does nothing is worse than no button, which is exactly what
        /// reusing the composer's instance in the tray would have given.
        /// </summary>
        public AttachmentViewModel Detach() => new AttachmentViewModel(Name, MimeType, Bytes, FilePath);

        /// <summary>
        /// Decoded on first access and then cached, because the transcript rebuilds its visual tree
        /// freely (virtualization, zoom, theme changes) and a re-decode per realization would be paid
        /// for every time. Null when there is nothing to decode or the decode failed — the template
        /// falls back to a plain chip, exactly as the markdown renderer falls back to alt text.
        /// </summary>
        public BitmapSource? Thumbnail
        {
            get
            {
                if (_thumbnailDecoded)
                    return _thumbnail;

                _thumbnailDecoded = true;
                _thumbnail = Decode();
                return _thumbnail;
            }
        }

        public bool HasThumbnail => Thumbnail is not null;

        /// <summary>Records where the bytes were written, once the message carrying them was sent.</summary>
        public void NoteSaved(string path) => FilePath = path;

        /// <summary>
        /// The image bytes from wherever they still exist — memory for an attachment in this session,
        /// the saved file for a restored one — or null when neither survives (retention has reclaimed
        /// the file). Deliberately not cached: the only caller is the markdown export, which runs once
        /// per invocation, and holding every transcript image in memory to serve it would cost far more
        /// than re-reading.
        /// </summary>
        public byte[]? TryReadBytes()
        {
            if (Bytes is { Length: > 0 })
                return Bytes;

            try
            {
                if (FilePath is { Length: > 0 } path && File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                // Swept mid-read, locked, unreadable — the caller degrades to naming it.
            }

            return null;
        }

        /// <summary>
        /// The wire form. Null when the bytes are gone — a restored attachment is a picture of
        /// something already said, not something to say again.
        /// </summary>
        public PromptAttachmentDto? ToDto() =>
            Bytes is null ? null : new PromptAttachmentDto(Name, MimeType, Convert.ToBase64String(Bytes));

        private BitmapSource? Decode()
        {
            try
            {
                if (Bytes is { Length: > 0 })
                {
                    using (var stream = new MemoryStream(Bytes, writable: false))
                        return DecodeStream(stream);
                }

                if (FilePath is { Length: > 0 } path && File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                        return DecodeStream(stream);
                }
            }
            catch (Exception)
            {
                // Unsupported codec, a file swept by retention, a truncated write — all render as the
                // no-thumbnail chip rather than taking the transcript down.
            }

            return null;
        }

        private static BitmapSource DecodeStream(Stream stream)
        {
            // Probe the header first so DecodePixelWidth is only applied when it actually shrinks —
            // set unconditionally it UPSCALES a small image at decode time, which is slower and
            // blurrier than leaving it alone (the same trap MarkdownImages documents).
            var naturalWidth = BitmapDecoder
                .Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                .Frames[0].PixelWidth;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad: decode now and release the stream/file, so nothing holds a handle on a file the
            // retention sweep may want later.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (naturalWidth > ThumbnailDecodeWidth)
                image.DecodePixelWidth = ThumbnailDecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
