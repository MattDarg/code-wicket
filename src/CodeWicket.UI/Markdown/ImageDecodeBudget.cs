using System.IO;
using System.Windows.Media.Imaging;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// The one bound on how many PIXELS an image may decode to, read from the header before any
    /// pixel is decoded. Shared by the inline-image renderer and the clipboard/file attachment
    /// path, because both decode bytes the user did not author.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every earlier cap was on the ENCODED size, and PNG makes that almost meaningless</b>
    /// (pre-release security review, September 2026). A 100 × 2,000,000 image of uniform pixels is well
    /// under a megabyte deflated and about 800 MB decoded; a 960-wide one is 7.7 GB. The decode is
    /// synchronous on the dispatcher and the result was frozen into a cache, so a message that
    /// named such a file - or carried it as a <c>data:</c> URI - stalled devenv for seconds to
    /// minutes and could take it down with an out-of-memory that nothing else in the process was
    /// prepared for. And the width cap did not help, because it only ever bounded WIDTH: the
    /// natural width of the tall bomb is small, so no decode scaling was applied at all.
    /// </para>
    /// <para>
    /// The budget is 16 megapixels - 64 MB at 32 bpp, a ceiling for a synchronous decode that a
    /// 12-megapixel photograph or a 4K screenshot passes under. Read from the header alone
    /// (<see cref="BitmapCreateOptions.DelayCreation"/> with no caching decodes nothing), so a
    /// refused image costs its header and nothing more.
    /// </para>
    /// </remarks>
    internal static class ImageDecodeBudget
    {
        /// <summary>Most pixels an image may decode to. See the remarks for where the number comes from.</summary>
        internal const long MaxPixels = 16L * 1024 * 1024;

        /// <summary>
        /// Reads the first frame's natural size from <paramref name="stream"/>'s header, leaving the
        /// stream rewound. False when the frame would decode to more than <see cref="MaxPixels"/>;
        /// a malformed header throws, as it always did, for the caller's fallback.
        /// </summary>
        internal static bool TryProbe(Stream stream, out int width, out int height)
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            stream.Position = 0;
            return (long)width * height <= MaxPixels;
        }
    }
}
