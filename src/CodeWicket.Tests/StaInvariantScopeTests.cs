using System;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Shell.Sessions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <c>StaTest.Run</c> fails a view-model test that raised an invariant breach, wherever the breach
    /// was raised from. The view-model reports through <see cref="Invariants.Ambient"/>, whose scope is
    /// an execution-context value <c>StaTest.Run</c> opens on its STA thread - and the breach that
    /// matters is the one raised after an await that resumed somewhere else, which a thread-static
    /// scope cannot see.
    /// </summary>
    public sealed class StaInvariantScopeTests
    {
        /// <summary>
        /// The body BLOCKS until the pool continuation has reported: <c>StaTest.Run</c> joins as soon as
        /// the body returns, so without the wait the breach would race the end-of-run check and the
        /// injection that reverts the scope to thread-static would come back INCONCLUSIVE rather than
        /// load-bearing. And it asserts the hop happened, so a green run cannot be one that never left
        /// the STA thread.
        /// </summary>
        [Fact]
        public void ABreachOnAPoolContinuationFailsStaTestRun()
        {
            var staThread = 0;
            var continuationThread = 0;

            var failure = Assert.Throws<Xunit.Sdk.XunitException>(() => StaTest.Run(() =>
            {
                staThread = Environment.CurrentManagedThreadId;
                using var reported = new ManualResetEventSlim(false);

                _ = ReportAfterAHopAsync();

                Assert.True(reported.Wait(TimeSpan.FromSeconds(5)), "the breach was never raised");

                async Task ReportAfterAHopAsync()
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    continuationThread = Environment.CurrentManagedThreadId;
                    Invariants.Ambient.Breach("raised on a pool continuation");
                    reported.Set();
                }
            }));

            Assert.NotEqual(staThread, continuationThread);
            Assert.Contains("raised on a pool continuation", failure.Message, StringComparison.Ordinal);
        }

        /// <summary>The control: a body that raises nothing passes, so the check above is about the
        /// breach and not about the scope itself failing every run.</summary>
        [Fact]
        public void ABodyThatRaisesNothingPasses() => StaTest.Run(() => { });

        /// <summary>Outside any scope a breach reaches nothing: the product never opens one.</summary>
        [Fact]
        public void WithNoScopeOpenABreachIsDropped()
        {
            var recorder = new BreachRecorder();
            using (Invariants.OpenScope(recorder))
            {
            }

            Invariants.Ambient.Breach("no scope is open");

            Assert.Equal(0, recorder.Count);
        }
    }
}
