using System;
using System.IO;
using System.Text;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What the CWKT_ACP_LOG tees write (issue #118). An image's base64 is elided by SIZE, not because
    /// it is secret — the log directory runs to a fixed total budget and was measured at its ceiling,
    /// so a screenshot per prompt would evict the diagnostics the file exists for. The frame's shape
    /// survives; only the payload goes.
    /// </summary>
    public class FrameLogRedactionTests
    {
        private static string Base64Run(int length) => new string('A', length);

        [Fact]
        public void ALongBase64DataValueIsElidedAndReportsHowMuchItDropped()
        {
            var payload = Base64Run(4096);
            var frame =
                "{\"method\":\"session/prompt\",\"params\":{\"prompt\":[{\"type\":\"image\",\"data\":\""
                + payload + "\",\"mimeType\":\"image/png\"}]}}";

            var scrubbed = FrameLogRedaction.Scrub(frame);

            Assert.DoesNotContain(payload, scrubbed);
            // A bare marker would leave a reader unable to tell a thumbnail from a 4K capture, which is
            // the question this log gets read for when a prompt is unexpectedly large.
            Assert.Contains("<4096 base64 chars elided>", scrubbed);
        }

        [Fact]
        public void TheRestOfTheFrameSurvivesTheElision()
        {
            var frame =
                "{\"method\":\"session/prompt\",\"params\":{\"prompt\":[{\"type\":\"image\",\"data\":\""
                + Base64Run(2048) + "\",\"mimeType\":\"image/png\"},{\"type\":\"text\",\"text\":\"look at this\"}]}}";

            var scrubbed = FrameLogRedaction.Scrub(frame);

            Assert.Contains("\"method\":\"session/prompt\"", scrubbed);
            Assert.Contains("\"mimeType\":\"image/png\"", scrubbed);
            Assert.Contains("\"text\":\"look at this\"", scrubbed);
        }

        [Fact]
        public void AShortDataValueIsLeftAlone()
        {
            // "data" is a generic key. The elision is keyed on the VALUE being a long unbroken base64
            // run precisely so an unrelated short one keeps its contents — those are worth reading.
            const string frame = "{\"result\":{\"data\":\"abc123\"}}";

            Assert.Equal(frame, FrameLogRedaction.Scrub(frame));
        }

        [Fact]
        public void AFrameWithNothingToScrubIsReturnedUnchangedByReference()
        {
            // Not just equal — the SAME instance, which is how the tee knows it can write the original
            // bytes through without a re-encode.
            const string frame = "{\"method\":\"session/new\",\"params\":{\"cwd\":\"C:\\\\ws\"}}";

            Assert.Same(frame, FrameLogRedaction.Scrub(frame));
        }

        [Fact]
        public void AnAccessTokenIsStillRedactedAlongsideTheElision()
        {
            // Both substitutions on one frame: the token must not survive an image being present.
            var frame =
                "{\"result\":{\"accessToken\":\"eyJSECRET.PAYLOAD\",\"data\":\"" + Base64Run(2048) + "\"}}";

            var scrubbed = FrameLogRedaction.Scrub(frame);

            Assert.DoesNotContain("eyJSECRET", scrubbed);
            Assert.Contains("<REDACTED>", scrubbed);
            Assert.Contains("base64 chars elided", scrubbed);
        }

        /// <summary>
        /// The read direction buffers to the newline boundary for the same reason the write direction
        /// does: a value split across two reads would slip through unmatched. A read returns whatever
        /// was in the pipe, so this is not a theoretical split.
        /// </summary>
        [Fact]
        public void TheReadTeeElidesAFrameThatArrivesInPieces()
        {
            var logPath = Path.Combine(Path.GetTempPath(), "cwkt-readtee-" + Guid.NewGuid().ToString("N") + ".log");
            var payload = Base64Run(2048);
            var frame =
                "{\"update\":{\"content\":{\"type\":\"image\",\"data\":\"" + payload + "\"}}}\n";

            try
            {
                var source = new ChunkedStream(Encoding.UTF8.GetBytes(frame), chunkSize: 97);
                using (var sink = FrameLogSink.Open(logPath))
                using (var tee = new FrameTeeReadStream(source, sink))
                {
                    var buffer = new byte[256];
                    while (tee.Read(buffer, 0, buffer.Length) > 0)
                    {
                        // Drain; the tee logs as it yields.
                    }
                }

                var logged = File.ReadAllText(logPath);
                Assert.DoesNotContain(payload, logged);
                Assert.Contains("<2048 base64 chars elided>", logged);
            }
            finally
            {
                try { File.Delete(logPath); } catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void TheReadTeeFlushesATrailingPartialFrameOnDispose()
        {
            // No newline at all: the whole frame is a partial one, and losing it would make the tee
            // lossy exactly when a session dies mid-frame — the case worth logging.
            var logPath = Path.Combine(Path.GetTempPath(), "cwkt-readtee-" + Guid.NewGuid().ToString("N") + ".log");

            try
            {
                var source = new ChunkedStream(
                    Encoding.UTF8.GetBytes("{\"update\":\"no trailing newline\"}"), chunkSize: 8);
                using (var sink = FrameLogSink.Open(logPath))
                using (var tee = new FrameTeeReadStream(source, sink))
                {
                    var buffer = new byte[16];
                    while (tee.Read(buffer, 0, buffer.Length) > 0)
                    {
                    }
                }

                Assert.Contains("no trailing newline", File.ReadAllText(logPath));
            }
            finally
            {
                try { File.Delete(logPath); } catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>A read-only stream that hands back at most <c>chunkSize</c> bytes per Read, so a
        /// frame is guaranteed to arrive split.</summary>
        private sealed class ChunkedStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _chunkSize;
            private int _position;

            public ChunkedStream(byte[] data, int chunkSize)
            {
                _data = data;
                _chunkSize = chunkSize;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _data.Length - _position;
                if (remaining <= 0)
                    return 0;

                var n = Math.Min(Math.Min(count, _chunkSize), remaining);
                Array.Copy(_data, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
