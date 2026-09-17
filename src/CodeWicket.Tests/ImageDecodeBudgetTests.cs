using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using CodeWicket.Core.Ide;
using CodeWicket.UI.Input;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The pre-release security review (September 2026): every cap on an image was on its ENCODED size, and a PNG
    /// of uniform pixels compresses so well that a file under a megabyte decoded to hundreds of
    /// megabytes on the dispatcher. The budget is on decoded PIXELS, read from the header. And on links:
    /// the containment test was lexical, so a junction inside the workspace reached outside it.
    /// <para>The bombs here are hand-encoded rather than rendered, because rendering one means
    /// allocating the very thing the guard exists to refuse.</para>
    /// </summary>
    public sealed class ImageDecodeBudgetTests : IDisposable
    {
        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cwkt-imgbudget-" + Guid.NewGuid().ToString("N"));

        public ImageDecodeBudgetTests() => Directory.CreateDirectory(_scratch);

        public void Dispose()
        {
            try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
        }

        // ---- the decoded-pixel budget ----

        [Fact]
        public void ATallNarrowBombIsRefusedFromItsHeader()
        {
            // 128 x 150,000 = 19.2 megapixels, over the 16 MP budget; ~20 KB on disk.
            var bomb = GrayPng(128, 150_000);
            Assert.True(bomb.Length < 256 * 1024, $"the bomb should be small on disk, was {bomb.Length} bytes");

            StaTest.Run(() =>
            {
                var root = Path.Combine(_scratch, "root");
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "logo.png");
                File.WriteAllBytes(path, bomb);

                Assert.Null(MarkdownImages.TryCreateImage(path, root));
                Assert.Null(MarkdownImages.TryCreateImage("data:image/png;base64," + Convert.ToBase64String(bomb), null));
            });
        }

        // The attachment path decodes bytes the user did not author either (a dropped file, a
        // picked file) and took the same shape of input; it takes the same budget.
        [Fact]
        public void TheAttachmentPathRefusesTheSameBomb()
        {
            var path = Path.Combine(_scratch, "bomb.png");
            File.WriteAllBytes(path, GrayPng(128, 150_000));

            StaTest.Run(() => Assert.False(ClipboardImages.TryReadFile(path, out _)));
        }

        /// <summary>
        /// A refusal must name the check that actually failed. The budget landed on the attachment
        /// path while the picker still explained every refusal as a bad format or an unreadable file,
        /// so a valid PNG that was merely too large was told its own bytes were not a PNG — and each
        /// remedy that implies (re-save it, unlock it, fix the extension) fails, while the one that
        /// works was never mentioned. Found in Visual Studio while exercising the budget, 2026-09-10.
        /// <para>Both directions, and that is the whole design of this test: the format sentence must
        /// still appear where it is TRUE, or "does not say PNG, JPEG" would be satisfiable by a
        /// refusal that says nothing at all.</para>
        /// </summary>
        [Fact]
        public void ARefusalNamesTheCheckThatFailedAndNoOther()
        {
            var bomb = Path.Combine(_scratch, "bomb.png");
            File.WriteAllBytes(bomb, GrayPng(128, 150_000));

            var notAnImage = Path.Combine(_scratch, "notreally.png");
            File.WriteAllText(notAnImage, "this is prose, whatever the extension says");

            var ordinary = Path.Combine(_scratch, "fine.png");
            File.WriteAllBytes(ordinary, GrayPng(64, 64));

            StaTest.Run(() =>
            {
                Assert.False(ClipboardImages.TryReadFile(bomb, out _, out var tooBig));
                Assert.NotNull(tooBig);

                // The dimensions and the ceiling, so the user knows what to change and by how much.
                // Built through CurrentCulture rather than written out, because the message is.
                Assert.Contains(128.ToString("N0", CultureInfo.CurrentCulture), tooBig!, StringComparison.Ordinal);
                Assert.Contains(150_000.ToString("N0", CultureInfo.CurrentCulture), tooBig!, StringComparison.Ordinal);
                Assert.Contains("megapixel", tooBig!, StringComparison.Ordinal);

                // And NOT the format explanation. These bytes ARE a PNG; this is the assertion the
                // shipped bug fails.
                Assert.DoesNotContain("PNG, JPEG", tooBig!, StringComparison.Ordinal);
                Assert.DoesNotContain("extension", tooBig!, StringComparison.Ordinal);

                // The same sentence where it does hold, so the pair discriminates.
                Assert.False(ClipboardImages.TryReadFile(notAnImage, out _, out var wrongFormat));
                Assert.Contains("PNG, JPEG", wrongFormat!, StringComparison.Ordinal);
                Assert.DoesNotContain("megapixel", wrongFormat!, StringComparison.Ordinal);

                // A missing file says so, rather than borrowing either of the above.
                Assert.False(ClipboardImages.TryReadFile(
                    Path.Combine(_scratch, "no-such-file.png"), out _, out var missing));
                Assert.Contains("not found", missing!, StringComparison.Ordinal);

                // And nothing is refused when nothing failed — or every assertion above passes on a
                // path that simply refuses everything.
                Assert.True(ClipboardImages.TryReadFile(ordinary, out _, out var none));
                Assert.Null(none);
            });
        }

        /// <summary>
        /// The paste half of the same rule. Silence is right for a payload that was never an image —
        /// the paste falls through to the text on the clipboard, the ordinary case. It is wrong for
        /// one that WAS an image and was refused: nothing else the paste could have meant, and a
        /// clipboard holding a picture usually holds no text to fall through to, so the gesture
        /// produces nothing at all and the user is left believing they attached it (#118's rule).
        /// <para>All three directions, because "says nothing" and "says something" are each
        /// satisfiable by a path that always does one of them.</para>
        /// </summary>
        [Fact]
        public void APasteSaysWhyOnlyWhenThePayloadWasAnImage()
        {
            StaTest.Run(() =>
            {
                // A real PNG, over the budget: refused, and it says so.
                var bomb = new System.Windows.DataObject();
                bomb.SetData("PNG", new MemoryStream(GrayPng(128, 150_000)));
                Assert.False(ClipboardImages.TryRead(bomb, out _, out var refused));
                Assert.NotNull(refused);
                Assert.Contains("megapixel", refused!, StringComparison.Ordinal);

                // Not an image at all: silent, so the text paste behind it still happens.
                var prose = new System.Windows.DataObject();
                prose.SetData("PNG", new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes("not an image, whatever the format name says")));
                Assert.False(ClipboardImages.TryRead(prose, out _, out var silent));
                Assert.Null(silent);

                // And an ordinary one attaches with nothing to report.
                var fine = new System.Windows.DataObject();
                fine.SetData("PNG", new MemoryStream(GrayPng(64, 64)));
                Assert.True(ClipboardImages.TryRead(fine, out _, out var none));
                Assert.Null(none);
            });
        }

        /// <summary>
        /// The other half of the divider, and the one the sniff alone could not express: a file that
        /// EXISTS and cannot be read has no bytes to sniff, so it was treated as "not an image" and
        /// the paste fell silently through to a text paste that had nothing to paste. Observed on a
        /// OneDrive file with the network off (2026-09-11) — copy, paste, and the gesture did nothing
        /// at all.
        /// <para>Both directions again: a document that reads perfectly well and simply is not an
        /// image must STILL be silent, or the fix is just "report everything" and the ordinary text
        /// paste is broken.</para>
        /// </summary>
        [Fact]
        public void AFileThatCannotBeReadIsReportedButANonImageIsStillSilent()
        {
            var locked = Path.Combine(_scratch, "held-open.png");
            File.WriteAllBytes(locked, GrayPng(64, 64));

            var document = Path.Combine(_scratch, "notes.txt");
            File.WriteAllText(document, "an ordinary document, copied and pasted");

            // Exists, has a size, and will not open — the shape an un-hydratable cloud file takes.
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                StaTest.Run(() =>
                {
                    var dropped = new System.Windows.DataObject();
                    dropped.SetData(System.Windows.DataFormats.FileDrop, new[] { locked }, autoConvert: false);

                    Assert.False(ClipboardImages.TryRead(dropped, out _, out var refusal));
                    Assert.NotNull(refusal); // was null: the paste did nothing and said nothing
                    Assert.Contains("could not be opened", refusal!, StringComparison.Ordinal);

                    var text = new System.Windows.DataObject();
                    text.SetData(System.Windows.DataFormats.FileDrop, new[] { document }, autoConvert: false);

                    Assert.False(ClipboardImages.TryRead(text, out _, out var silent));
                    Assert.Null(silent);
                });
            }
        }

        [Fact]
        public void AnOrdinaryImageIsWellUnderTheBudget()
        {
            var path = Path.Combine(_scratch, "ok.png");
            File.WriteAllBytes(path, GrayPng(640, 480));

            StaTest.Run(() =>
            {
                Assert.NotNull(MarkdownImages.TryCreateImage(path, _scratch));
                Assert.True(ClipboardImages.TryReadFile(path, out _));
            });
        }

        // The other half of the budget finding: a tall image UNDER the budget still decoded at full height, because
        // only width was ever scaled. Both edges are capped now.
        [Fact]
        public void ATallImageUnderTheBudgetIsDecodedNoTallerThanTheEdgeCap()
        {
            var path = Path.Combine(_scratch, "tall.png");
            File.WriteAllBytes(path, GrayPng(100, 5_000)); // half a megapixel

            StaTest.Run(() =>
            {
                var image = MarkdownImages.TryCreateImage(path, _scratch);
                Assert.NotNull(image);
                var source = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(image!.Source);
                Assert.True(source.PixelHeight <= MarkdownImages.MaxDecodeEdge, $"decoded to {source.PixelHeight}px tall");
            });
        }

        // ---- a link inside the root ----

        /// <summary>
        /// A junction under the workspace naming a directory outside it. Lexically its files are under
        /// the root; on disk they are wherever the junction points, which for this scenario is a
        /// UNC share. Needs no privilege to create on Windows, which is also why a repository can carry
        /// one.
        /// </summary>
        [Fact]
        public void AJunctionInsideTheRootDoesNotReachOutsideIt()
        {
            var root = Path.Combine(_scratch, "root");
            var outside = Path.Combine(_scratch, "outside");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "y.png"), GrayPng(8, 8));
            var link = Path.Combine(root, "docs");
            if (!TryCreateJunction(link, outside))
                throw NotMeasured();

            Assert.True(ReparsePoints.IsLink(link));
            Assert.False(ReparsePoints.IsLink(root));
            Assert.False(ReparsePoints.IsLink(Path.Combine(root, "missing")));
            Assert.True(ReparsePoints.AnyBelow(root, Path.Combine(link, "y.png")));
            Assert.False(ReparsePoints.AnyBelow(root, Path.Combine(root, "plain", "y.png")));

            var through = Path.Combine(link, "y.png");
            Assert.True(File.Exists(through)); // so the null below is the link, not absence

            StaTest.Run(() => Assert.Null(MarkdownImages.TryCreateImage(through, root)));
        }

        // The same link, through the file-reference resolver's direct hit: a prompt-injected
        // `docs\y.png:1` must not become a one-click open of a file outside the root.
        [Fact]
        public async System.Threading.Tasks.Task TheResolverDoesNotFollowAJunctionEither()
        {
            var root = Path.Combine(_scratch, "root");
            var outside = Path.Combine(_scratch, "outside");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
            if (!TryCreateJunction(Path.Combine(root, "docs"), outside))
                throw NotMeasured();

            var resolver = new FileReferenceResolver(root);

            Assert.Null(await resolver.ResolveAsync(@"docs\secret.txt"));
        }

        // ---- helpers ----

        // A FAILURE naming the reason when no junction can be made, never a return: a test that returns
        // reports the same green as one that measured the guard. Why a failure and not a skip on this
        // xunit stack is recorded beside the same helper in PolicyPermissionHandlerTests.
        private const string NoJunction =
            "A junction could not be created here, so the link containment was not measured.";

        private static System.Exception NotMeasured() =>
            new System.InvalidOperationException(NoJunction);

        private static bool TryCreateJunction(string link, string target)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                process.WaitForExit(10_000);
                return process.ExitCode == 0 && Directory.Exists(link);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A valid 8-bit greyscale PNG of the given size, all black, written chunk by chunk so a huge
        /// one never exists as pixels here. Raw scanlines are a filter byte plus one byte per pixel.
        /// </summary>
        internal static byte[] GrayPng(int width, int height)
        {
            var raw = new byte[(long)height * (width + 1)];
            byte[] deflated;
            using (var compressed = new MemoryStream())
            {
                using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(raw, 0, raw.Length);
                deflated = compressed.ToArray();
            }

            var idat = new List<byte> { 0x78, 0x9C };
            idat.AddRange(deflated);
            idat.AddRange(BigEndian(Adler32(raw)));

            var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            var ihdr = new List<byte>();
            ihdr.AddRange(BigEndian((uint)width));
            ihdr.AddRange(BigEndian((uint)height));
            ihdr.AddRange(new byte[] { 8, 0, 0, 0, 0 }); // 8-bit, greyscale, deflate, no filter method, no interlace
            AddChunk(png, "IHDR", ihdr.ToArray());
            AddChunk(png, "IDAT", idat.ToArray());
            AddChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void AddChunk(List<byte> png, string type, byte[] data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            png.AddRange(BigEndian((uint)data.Length));
            var crcInput = new byte[typeBytes.Length + data.Length];
            typeBytes.CopyTo(crcInput, 0);
            data.CopyTo(crcInput, typeBytes.Length);
            png.AddRange(typeBytes);
            png.AddRange(data);
            png.AddRange(BigEndian(Crc32(crcInput)));
        }

        private static byte[] BigEndian(uint value) =>
            new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data)
        {
            var c = 0xFFFFFFFFu;
            foreach (var d in data)
                c = CrcTable[(c ^ d) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
