using System;
using System.IO;

namespace CodeWicket.Core
{
    // The two halves of a frame-aligned tee, and the reason a FrameLogSink can hold both directions
    // of a conversation in ONE file.
    //
    // THE BUFFERING IS THE MECHANISM, NOT AN OPTIMISATION. StreamJsonRpc does not guarantee
    // frame-aligned writes, and a read returns whatever happened to be in the pipe — so a tee that
    // forwarded each chunk straight to a shared file would interleave the two directions MID-FRAME
    // and produce a log parseable as neither. Holding bytes until a newline completes is what makes
    // one file safe, and it is also what lets the redaction see whole values: a token or a base64 run
    // split across two writes would otherwise slip through unmatched. '\n' cannot occur inside a
    // multi-byte UTF-8 sequence, so splitting there is decode-safe.
    //
    // THE RAW BYTE TEE IS A DIFFERENT INSTRUMENT AND IS DELIBERATELY STILL ONE. Shell.TeeStream
    // captures the engine channel for diagnosing stream CORRUPTION, where "each line is a whole
    // frame" is precisely the assumption under test — see its own note.

    /// <summary>
    /// A write-through stream that appends every frame it forwards to a shared log sink, stamped as
    /// <see cref="FrameLogSink.Outbound"/> — what this process WROTE, which on the ACP tee is
    /// client→agent and on the MCP tee is server→client. An outbound frame can carry the user's
    /// bearer token (the response to Kiro v3's <c>_kiro/auth/getAccessToken</c> host callback), so it
    /// buffers to frame boundaries and the sink's scrub sees whole values. The forwarded stream is
    /// untouched.
    /// </summary>
    public sealed class FrameTeeWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly FrameLogSink? _log;
        private readonly MemoryStream _pending = new MemoryStream();

        public FrameTeeWriteStream(Stream inner, FrameLogSink log)
        {
            _inner = inner;
            _log = log;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            if (_log is null) return;
            try
            {
                // Frames are newline-delimited JSON; hold bytes until a frame completes so the redact
                // scan always sees whole values ('\n' can't occur inside a multi-byte UTF-8 sequence,
                // so splitting there is decode-safe).
                _pending.Write(buffer, offset, count);
                FlushCompleteFrames();
            }
            catch { /* logging is best-effort */ }
        }

        private void FlushCompleteFrames()
        {
            var data = _pending.GetBuffer();
            var length = (int)_pending.Length;
            var lastNewline = -1;
            for (var i = length - 1; i >= 0; i--)
            {
                if (data[i] == (byte)'\n') { lastNewline = i; break; }
            }
            if (lastNewline < 0)
                return;

            WriteRedacted(data, 0, lastNewline + 1);

            var remaining = length - (lastNewline + 1);
            var rest = new byte[remaining];
            Array.Copy(data, lastNewline + 1, rest, 0, remaining);
            _pending.SetLength(0);
            _pending.Write(rest, 0, remaining);
        }

        private void WriteRedacted(byte[] data, int offset, int count) =>
            _log!.Write(FrameLogSink.Outbound, data, offset, count);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    // Flush a trailing partial frame (still redacted) so the tee isn't lossy at teardown.
                    if (_log is not null && _pending.Length > 0)
                        WriteRedacted(_pending.GetBuffer(), 0, (int)_pending.Length);
                }
                catch { /* ignore */ }
                // NOT disposed here: the sink is shared with the other direction, so the
                // first stream to close would silence the second. Owned by
                // the connection that opened the sink, which is what teardown actually ends.
                _pending.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A read-only pass-through stream that appends every byte it yields to a log file.
    /// <para>
    /// Frame-buffered, like its write-direction sibling and for the same reason: the elision that keeps
    /// a replayed image out of the log (see <see cref="FrameLogRedaction"/>) has to see whole values, and a
    /// read returns whatever happened to be in the pipe. A <c>session/load</c> replays the entire
    /// conversation as notifications — including, on the Claude adapter, every image the user ever
    /// pasted — so this direction is not the quiet one it looks like. The trailing partial frame is
    /// flushed on dispose, so nothing is lost at teardown.
    /// </para>
    /// </summary>
    public sealed class FrameTeeReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly FrameLogSink? _log;
        private readonly MemoryStream _pending = new MemoryStream();

        public FrameTeeReadStream(Stream inner, FrameLogSink log)
        {
            _inner = inner;
            _log = log;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0 && _log is not null)
            {
                try
                {
                    _pending.Write(buffer, offset, n);
                    FlushCompleteFrames();
                }
                catch { /* logging is best-effort */ }
            }
            return n;
        }

        // Writes every complete (newline-terminated) frame held so far and keeps the remainder. Same
        // shape as FrameTeeWriteStream.FlushCompleteFrames — '\n' cannot occur inside a multi-byte UTF-8
        // sequence, so splitting there is decode-safe.
        private void FlushCompleteFrames()
        {
            var data = _pending.GetBuffer();
            var length = (int)_pending.Length;
            var lastNewline = -1;
            for (var i = length - 1; i >= 0; i--)
            {
                if (data[i] == (byte)'\n') { lastNewline = i; break; }
            }
            if (lastNewline < 0)
                return;

            _log!.Write(FrameLogSink.Inbound, data, 0, lastNewline + 1);

            var remaining = length - (lastNewline + 1);
            var rest = new byte[remaining];
            Array.Copy(data, lastNewline + 1, rest, 0, remaining);
            _pending.SetLength(0);
            _pending.Write(rest, 0, remaining);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    // Flush a trailing partial frame so the tee isn't lossy at teardown — the same
                    // guarantee the write direction makes.
                    if (_log is not null && _pending.Length > 0)
                        _log.Write(FrameLogSink.Inbound, _pending.GetBuffer(), 0, (int)_pending.Length);
                }
                catch { /* ignore */ }
                // NOT disposed here: the sink is shared with the other direction, so the
                // first stream to close would silence the second. Owned by
                // the connection that opened the sink, which is what teardown actually ends.
                _pending.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
