using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The breach recorder every later check leans on, proved blind spot by blind spot. The session
    /// lifetime and prompt delivery classes report their invariants here instead of throwing, so a
    /// recorder that silently missed a breach would turn every one of those checks green.
    /// </summary>
    public sealed class InvariantRecorderTests : InvariantCheckedTest
    {
        /// <summary>
        /// The shape the first, thread-static design missed: a fire-and-forget task that hops to a pool
        /// thread and reports there. Asserted to have hopped, so a green run cannot be one that never
        /// left the test's thread.
        /// </summary>
        [Fact]
        public void ABreachAfterAThreadHopIsRecorded()
        {
            var testThread = Environment.CurrentManagedThreadId;
            var hopThread = 0;
            using var raised = new ManualResetEventSlim(false);

            _ = RaiseAfterAHopAsync();

            // Blocks this thread, so the continuation below cannot run on it.
            Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "the breach was never raised");
            Assert.NotEqual(testThread, hopThread);
            Assert.Equal(new[] { "raised after a hop" }, Breaches.Drain());

            async Task RaiseAfterAHopAsync()
            {
                await Task.Run(() => { }).ConfigureAwait(false);
                hopThread = Environment.CurrentManagedThreadId;
                Breaches.Breach("raised after a hop");
                raised.Set();
            }
        }

        /// <summary>
        /// The end-of-test check is not opt-in: a breach left in the recorder fails the test in
        /// <see cref="InvariantCheckedTest.Dispose"/>, and says which breach.
        /// </summary>
        [Fact]
        public void AnUndrainedBreachFailsTheTest()
        {
            var test = new Probe();
            test.Report("left behind by the test");

            var failure = Assert.Throws<Xunit.Sdk.XunitException>(() => test.Dispose());

            Assert.Contains("left behind by the test", failure.Message, StringComparison.Ordinal);
        }

        /// <summary>A drained breach is an expected one, and leaves nothing to fail on.</summary>
        [Fact]
        public void ADrainedBreachDoesNotFailTheTest()
        {
            var test = new Probe();
            test.Report("expected by the test");
            test.Take();

            test.Dispose();
        }

        private sealed class Probe : InvariantCheckedTest
        {
            public void Report(string message) => Breaches.Breach(message);

            public void Take() => Breaches.Drain();
        }
    }
}
