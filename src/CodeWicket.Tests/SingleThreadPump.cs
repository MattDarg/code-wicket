using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Runs an async test body under a single-threaded synchronization context on the calling thread,
    /// so every continuation that captures the context resumes there, one at a time, in post order.
    /// For plain tests of classes whose contract is "called on one synchronization context", without
    /// a WPF dispatcher or an STA thread.
    /// </summary>
    /// <remarks>
    /// <para><b>Ordering comes from this; breach recording must NOT depend on it.</b> Code under test
    /// that uses <c>ConfigureAwait(false)</c>, <c>Task.Run</c>, or a double completing on another thread
    /// leaves this context, and that is exactly what a refactor changes - so the breach recorder is an
    /// instance (<see cref="InvariantCheckedTest"/>), never something only this context can see.</para>
    /// <para><b>The body must await its own work.</b> The pump stops when the body's task completes;
    /// a continuation posted after that is dropped, the test being over.</para>
    /// <para><b>And it gives up.</b> A body left waiting on something the test never releases - a start
    /// or a turn <see cref="ScriptedEngine"/> holds - would otherwise block the pump forever, and a
    /// hung test run hands <c>prove-check</c> and <c>prove-manifest</c> no verdict at all. So the run is
    /// bounded, and the failure says what the body was waiting on when the caller can describe it.</para>
    /// </remarks>
    internal static class SingleThreadPump
    {
        public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

        /// <param name="body">The test's async work.</param>
        /// <param name="budget">How long the body may take; <see cref="DefaultBudget"/> when null.</param>
        /// <param name="waitingOn">Says what the body is waiting on, for the timeout message - for
        /// example <see cref="ScriptedEngine.DescribePending"/>.</param>
        public static void Run(Func<Task> body, TimeSpan? budget = null, Func<string>? waitingOn = null)
        {
            var limit = budget ?? DefaultBudget;
            var previous = SynchronizationContext.Current;
            var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = body();
                task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
                if (!context.RunUntilComplete(limit))
                {
                    var what = waitingOn?.Invoke();
                    throw new TimeoutException(
                        $"The test body did not finish within {limit.TotalMilliseconds:0} ms. It is waiting on: "
                        + (string.IsNullOrEmpty(what) ? "(not described - pass waitingOn)" : what));
                }
                task.GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private sealed class PumpContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                try
                {
                    _queue.Add((d, state));
                }
                catch (InvalidOperationException)
                {
                    // Posted after the body completed: the test is over (see the remarks).
                }
            }

            public override void Send(SendOrPostCallback d, object? state)
                => throw new NotSupportedException("The test pump runs posts only.");

            public override SynchronizationContext CreateCopy() => this;

            public void Complete() => _queue.CompleteAdding();

            /// <summary>True when the body finished and every post it made ran; false when the budget ran out first.</summary>
            public bool RunUntilComplete(TimeSpan budget)
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    var remaining = budget - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                        return _queue.IsCompleted;

                    if (_queue.TryTake(out var item, remaining))
                        item.Callback(item.State);
                    else if (_queue.IsCompleted)
                        return true;
                }
            }
        }
    }
}
