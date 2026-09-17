using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Wraps a stream and copies every byte that flows through it to a side log, so the raw
    /// engine↔shell JSON-RPC channel can be captured for debugging (e.g. diagnosing stream
    /// corruption). Only used when raw logging is explicitly enabled; the normal path never wraps
    /// the streams, so production I/O is byte-for-byte unchanged. The sink is best-effort: a failed
    /// log write never disrupts the underlying stream.
    ///
    /// <para><b>This is the one tee still writing two files, and that is deliberate.</b> The ACP tee
    /// merged into one stamped file at #211 and the MCP tee followed, both of them frame-aligned
    /// through <see cref="Core.FrameLogSink"/>. Doing the same here would break the instrument. What
    /// this log exists to catch is a channel that is NOT well-formed — the <c>":' is an invalid
    /// start"</c> desync after a stale bundled engine, and the shared-<c>ArrayPool</c> reuse where a
    /// result deserialized from another connection's bytes — and a frame-aligned sink assumes
    /// "each line is a whole frame", which is exactly the assumption under test. It also splits on
    /// '\n' and drops blank lines, so it would normalise away the evidence. Two byte-exact files also
    /// answer "which direction saw the foreign bytes" without anyone having to trust a marker written
    /// by the code under suspicion.</para>
    ///
    /// <para>Nothing reads the <c>.send</c> half programmatically, so it costs nothing to leave. If
    /// this ever does merge, it needs its own frame-aligned path — not FrameLogSink.</para>
    /// </summary>
    internal sealed class TeeStream : Stream
    {
        private readonly Stream _inner;
        private readonly Stream _sink;
        private readonly object _gate = new object();

        public TeeStream(Stream inner, Stream sink)
        {
            _inner = inner;
            _sink = sink;
        }

        private void Tee(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return;
            try
            {
                lock (_gate)
                {
                    _sink.Write(buffer, offset, count);
                    _sink.Flush();
                }
            }
            catch
            {
                // Logging is best-effort and must never break the channel.
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Tee(buffer, offset, n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            Tee(buffer, offset, n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Tee(buffer, offset, count);
            _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Tee(buffer, offset, count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0)
            {
                var slice = buffer.Slice(0, n);
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)slice, out var seg) && seg.Array is not null)
                    Tee(seg.Array, seg.Offset, seg.Count);
                else
                    Tee(slice.ToArray(), 0, n);
            }
            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var seg) && seg.Array is not null)
                Tee(seg.Array, seg.Offset, seg.Count);
            else
                Tee(buffer.ToArray(), 0, buffer.Length);
            return _inner.WriteAsync(buffer, cancellationToken);
        }
#endif

        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => _inner.CanSeek;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
                try { _sink.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
