using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core
{
    /// <summary>
    /// A FIFO, single-at-a-time <see cref="SynchronizationContext"/> used as
    /// <c>JsonRpc.SynchronizationContext</c> for a StreamJsonRpc connection, so every inbound
    /// notification/request is dispatched in the order it arrived on the wire and
    /// <see cref="FlushAsync"/> can act as a barrier: once the sentinel it posts has run, everything
    /// queued ahead of it has been handled.
    /// <para>
    /// This is what closes issue #33 (a turn's last streamed chunks never reached the transcript).
    /// StreamJsonRpc completes the response to a request WE sent on a path that does NOT go through
    /// inbound dispatch, so a turn result could overtake the tail of the notifications that preceded
    /// it on the wire — and those late writes hit an already-completed turn channel and were dropped.
    /// The same shape exists on BOTH hops of the topology, which is why this lives in Core: the
    /// engine ↔ agent hop (<c>AcpAgentSession</c>, net10) took it first, and the shell ↔ engine hop
    /// (<c>EngineClient</c>, net472 in devenv and net10 in the Desktop host) lost the same race one
    /// hop later (issue #277) — the engine sends a turn's events in wire order and the shell's inbound
    /// dispatch could still apply the last one after the prompt's response had ended the turn.
    /// </para>
    /// <para>
    /// Non-blocking by construction: the pump invokes a callback and moves on, so an <c>async</c>
    /// handler that awaits (e.g. a permission request parked on the user) releases the pump at its
    /// first await and its continuation simply queues behind whatever arrived meanwhile. What it does
    /// NOT protect against is a handler that does synchronous work before that first await — that
    /// work runs on the pump and every later frame waits behind it — so a handler doing real work
    /// (IDE calls, a shellout) should leave the pump at once; see <c>ShellRpcTarget</c>.
    /// </para>
    /// </summary>
    public sealed class OrderedDispatchSynchronizationContext : SynchronizationContext
    {
        /// <summary>
        /// How long a drained pump waits for the next frame before releasing its thread. Comfortably
        /// longer than the gap between a turn's frames, far shorter than the gap between turns.
        /// </summary>
        private static readonly TimeSpan IdleLinger = TimeSpan.FromMilliseconds(250);

        private readonly object _gate = new object();
        private readonly Queue<KeyValuePair<SendOrPostCallback, object?>> _queue = new Queue<KeyValuePair<SendOrPostCallback, object?>>();
        private bool _pumping;
        private int _pumpThreadId;

        /// <inheritdoc />
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (d is null)
                throw new ArgumentNullException(nameof(d));

            lock (_gate)
            {
                _queue.Enqueue(new KeyValuePair<SendOrPostCallback, object?>(d, state));
                if (_pumping)
                {
                    // A pump is running — it may be idling between frames, so wake it.
                    Monitor.Pulse(_gate);
                    return;
                }
                _pumping = true;
            }

            // Nothing is draining the queue: start a pump. Long-running by intent — it lives for as
            // long as work keeps arriving, which for a streaming turn is the whole turn.
            try
            {
                Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                // LongRunning allocates a dedicated OS thread, so this CAN fail — and _pumping is
                // already latched true. Left latched, every later Post takes the "a pump is running"
                // branch and queues behind a pump that does not exist: the connection goes permanently
                // deaf to inbound notifications with nothing surfaced anywhere, and FlushAsync stops
                // completing, so every turn end pays the drain timeout instead. Release it so the next
                // Post can try again, then let the failure surface to the caller as it did before.
                lock (_gate) { _pumping = false; }
                throw;
            }
        }

        /// <inheritdoc />
        public override void Send(SendOrPostCallback d, object? state)
        {
            // Already on the pump: running inline preserves ordering and can't deadlock on itself.
            if (Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _pumpThreadId))
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim(false);
            Exception? failure = null;
            Post(
                _ =>
                {
                    try { d(state); }
                    catch (Exception ex) { failure = ex; }
                    finally { done.Set(); }
                },
                null);
            done.Wait();
            if (failure is not null)
                throw failure;
        }

        /// <inheritdoc />
        public override SynchronizationContext CreateCopy() => this;

        /// <summary>
        /// Completes once every callback queued before this call has run — the ordering barrier the
        /// end of a turn waits on.
        /// </summary>
        public Task FlushAsync()
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ => completed.TrySetResult(true), null);
            return completed.Task;
        }

        private void Pump()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            Volatile.Write(ref _pumpThreadId, Thread.CurrentThread.ManagedThreadId);

            // Whether the idle exit below already handed the pump back. The finally must not clear
            // _pumping unconditionally: on a normal exit another Post may have claimed it and started a
            // successor between that release and here, and clearing it then would let a THIRD pump start
            // beside the running one — losing the single-at-a-time ordering this whole type exists for.
            var released = false;
            try
            {
                while (true)
                {
                    KeyValuePair<SendOrPostCallback, object?> work;
                    lock (_gate)
                    {
                        // Idle briefly before giving up the thread. A streaming turn delivers frames a
                        // few milliseconds apart but faster than the pump empties the queue only in
                        // bursts, so exiting the moment it drains meant a brand-new OS thread for the
                        // very next frame — measured at one thread per frame (200 frames, 200 threads).
                        // Lingering collapses a whole turn onto one thread; a quiet session still lets
                        // it go rather than parking a thread per live session forever.
                        while (_queue.Count == 0)
                        {
                            if (!Monitor.Wait(_gate, IdleLinger) && _queue.Count == 0)
                            {
                                _pumping = false;
                                released = true;
                                return;
                            }
                        }
                        work = _queue.Dequeue();
                    }

                    // One bad handler must never stop the stream: StreamJsonRpc reports the failure
                    // for a request it dispatched, and a notification has nobody to report to.
                    try { work.Key(work.Value); }
                    catch { /* keep pumping */ }
                }
            }
            finally
            {
                // Any exit that ISN'T the idle one leaves _pumping latched true — the same permanent
                // deafness as a failed start, reached from the other side. Handler exceptions are caught
                // inside the loop, so this is the residual class (a Monitor.Wait failure, a thread abort):
                // rare, silent, and unrecoverable without this line.
                if (!released)
                    lock (_gate) { _pumping = false; }
                Volatile.Write(ref _pumpThreadId, 0);
                SetSynchronizationContext(previous);
            }
        }
    }
}
