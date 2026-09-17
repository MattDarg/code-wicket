using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Shell
{
    /// <summary>
    /// The stream handed to VS's terminal renderer: our <see cref="Push(string)"/> calls queue
    /// UTF-8 chunks, the terminal's reads block until data arrives (EOF after dispose). Doubles as
    /// the buffer while the pane is still being created (the first CreateTerminalRendererAsync
    /// takes ~10s loading the terminal package), so it is bounded: over <see cref="MaxBufferedBytes"/>
    /// the oldest unread chunks are dropped and a single marker line notes the gap — a runaway
    /// command must not grow unbounded memory. Deliberately a plain hand-rolled Stream (no
    /// Pipelines/Memory types) so nothing version-sensitive crosses into VS code.
    /// Terminal-side writes (user input) are swallowed; panes are created with allowUserInput false.
    /// </summary>
    public sealed class TerminalMirrorStream : Stream
    {
        /// <summary>Buffer cap. Generous for a creation window, small enough to be harmless.</summary>
        public const int MaxBufferedBytes = 1024 * 1024;

        private static readonly byte[] DropMarker =
            Encoding.UTF8.GetBytes("\r\n\x1b[33m[… older output dropped …]\x1b[0m\r\n");

        private readonly object _gate = new object();
        private readonly LinkedList<byte[]> _chunks = new LinkedList<byte[]>();
        private long _bufferedBytes;
        private int _readPos;      // offset into the first chunk (it is in-flight once > 0)
        private bool _completed;
        private TaskCompletionSource<bool>? _dataAvailable; // ReadAsync waiter; replaced per wake

        public void Push(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            PushBytes(Encoding.UTF8.GetBytes(text));
        }

        private void PushBytes(byte[] bytes)
        {
            lock (_gate)
            {
                if (_completed)
                    return;

                _chunks.AddLast(bytes);
                _bufferedBytes += bytes.Length;

                // Drop-oldest bound. The first chunk is skipped once partially read (the reader owns
                // it), and the chunk just added is never dropped — newest output always survives.
                var dropped = false;
                while (_bufferedBytes > MaxBufferedBytes && _chunks.Count > (_readPos > 0 ? 2 : 1))
                {
                    var oldest = _readPos > 0 ? _chunks.First!.Next! : _chunks.First!;
                    if (ReferenceEquals(oldest.Value, DropMarker))
                    {
                        // Already a gap marker at the drop point; remove it, and flag so it is
                        // re-added after the loop even if no further chunk gets dropped.
                        _chunks.Remove(oldest);
                        dropped = true;
                        continue;
                    }
                    _bufferedBytes -= oldest.Value.Length;
                    _chunks.Remove(oldest);
                    dropped = true;
                }
                if (dropped)
                {
                    if (_readPos > 0)
                        _chunks.AddAfter(_chunks.First!, DropMarker);
                    else
                        _chunks.AddFirst(DropMarker);
                }

                WakeReadersLocked();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                while (_chunks.Count == 0 && !_completed)
                    Monitor.Wait(_gate);
                return ReadLocked(buffer, offset, count);
            }
        }

        /// <summary>
        /// Truly async wait — no thread parks in <see cref="Monitor.Wait(object)"/> for the async
        /// path (the default Stream.ReadAsync would burn a threadpool thread blocking in our sync
        /// Read for as long as the pane is idle; the renderer's read loop should never own a thread).
        /// </summary>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource<bool> waiter;
                lock (_gate)
                {
                    if (_chunks.Count > 0 || _completed)
                        return ReadLocked(buffer, offset, count);
                    waiter = _dataAvailable ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                // Retire the shared waiter as well as cancelling it. It is cached in _dataAvailable so
                // concurrent readers share one wake, and only WakeReadersLocked ever cleared the field —
                // so a read cancelled while the pane is IDLE left an already-cancelled TCS sitting
                // there. The next ReadAsync, with a perfectly good token, took that same instance
                // through `??=` and awaited a task that was already cancelled: it threw instantly, and
                // went on throwing. The renderer's read loop treats that as the stream ending, so one
                // cancelled read killed the terminal mirror for the rest of the session — recoverable
                // only by new output happening to arrive and null the field first.
                using (cancellationToken.Register(() =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_dataAvailable, waiter))
                            _dataAvailable = null;
                    }

                    waiter.TrySetCanceled();
                }))
                {
                    await waiter.Task.ConfigureAwait(false);
                }
            }
        }

        // Both wake mechanisms, called with the gate held: the sync reader waits on the monitor,
        // the async reader on the TCS (RunContinuationsAsynchronously => no inline continuations
        // under our lock).
        private void WakeReadersLocked()
        {
            Monitor.PulseAll(_gate);
            var waiter = _dataAvailable;
            _dataAvailable = null;
            waiter?.TrySetResult(true);
        }

        private int ReadLocked(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0)
                return 0; // completed and drained -> EOF

            var chunk = _chunks.First!.Value;
            var n = Math.Min(count, chunk.Length - _readPos);
            Array.Copy(chunk, _readPos, buffer, offset, n);
            _readPos += n;
            if (_readPos >= chunk.Length)
            {
                _chunks.RemoveFirst();
                _readPos = 0;
                if (!ReferenceEquals(chunk, DropMarker))
                    _bufferedBytes -= chunk.Length;
            }
            return n;
        }

        /// <summary>
        /// Optional sink for terminal-side writes — the pane's user keystrokes when input is
        /// allowed (dump-verified path: <c>TerminalWindowBase.WriteUserInputAsync</c> → here).
        /// Phase 4 routes these to the active run_command pty. MUST never block: the writer can be
        /// the UI thread (the 2026-07-17 freeze). Null keeps input swallowed (the pre-Phase-4
        /// behavior).
        /// </summary>
        public Action<byte[]>? OnUserInput { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var sink = OnUserInput;
            if (sink is null || count <= 0)
                return;
            var copy = new byte[count];
            Array.Copy(buffer, offset, copy, 0, count);
            try { sink(copy); } catch { /* input routing is best-effort */ }
        }

        public override void Flush() { }

        // Every async/APM member is overridden so NOTHING routes through base Stream's internal
        // async-serialization semaphore. The 2026-07-17 devenv freeze was exactly that: the
        // renderer's read loop entered via base BeginReadInternal, took the shared semaphore, and
        // parked inside our blocking Read; the user's keystroke then hit WriteAsync on the UI
        // thread, which blocked in SemaphoreSlim.Wait behind the parked read (dump-verified stacks:
        // TerminalWindowBase.WriteUserInputAsync -> Stream.BeginWriteInternal -> SemaphoreSlim.Wait
        // vs BeginReadInternal -> TerminalMirrorStream.Read -> Monitor.ObjWait). With these
        // overrides, writes complete immediately and reads await without holding anything shared.
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count); // instant: forwards to OnUserInput or drops
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            var tcs = new TaskCompletionSource<int>(state);
            tcs.SetResult(0);
            callback?.Invoke(tcs.Task);
            return tcs.Task;
        }

        public override void EndWrite(IAsyncResult asyncResult) { }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            var tcs = new TaskCompletionSource<int>(state);
            ReadAsync(buffer, offset, count, CancellationToken.None).ContinueWith(
                t =>
                {
                    if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                    else if (t.IsCanceled) tcs.TrySetCanceled();
                    else tcs.TrySetResult(t.Result);
                    callback?.Invoke(tcs.Task);
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return tcs.Task;
        }

        public override int EndRead(IAsyncResult asyncResult) => ((Task<int>)asyncResult).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    _completed = true;
                    WakeReadersLocked(); // wake blocked readers (sync + async) so they return EOF
                }
            }
            base.Dispose(disposing);
        }
    }
}
