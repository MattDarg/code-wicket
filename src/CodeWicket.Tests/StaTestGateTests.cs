using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The gate in <see cref="StaTest"/>, pinned directly.
    /// <para>It has to be pinned by something DETERMINISTIC. The bug it fixes was a race that showed up
    /// in roughly one full suite run in four, in a different test each time, and passed on every
    /// re-run — so "the suite went green" is not evidence the gate is doing anything, and neither is a
    /// hundred green runs. Asserting the mutual exclusion itself is the only check that fails the
    /// moment the gate is removed rather than eventually.</para>
    /// </summary>
    public class StaTestGateTests
    {
        [Fact]
        public void ConcurrentCallersNeverRunTwoStaBodiesAtOnce()
        {
            var running = 0;
            var peak = 0;

            Parallel.For(0, 8, _ => StaTest.Run(() =>
            {
                var now = Interlocked.Increment(ref running);

                // Record the high-water mark without a lock of its own, which would serialise the very
                // thing being measured.
                int seen;
                do
                {
                    seen = Volatile.Read(ref peak);
                    if (now <= seen)
                        break;
                }
                while (Interlocked.CompareExchange(ref peak, now, seen) != seen);

                // Long enough that genuinely parallel bodies would overlap: without the gate the eight
                // callers start within microseconds of each other.
                Thread.Sleep(25);
                Interlocked.Decrement(ref running);
            }));

            Assert.Equal(1, peak);
        }

        [Fact]
        public void TheBodyRunsOnAnStaThread()
        {
            // The gate must not have quietly changed what the helper is FOR. WPF text elements and the
            // clipboard both refuse to work off an STA thread.
            var state = ApartmentState.Unknown;

            StaTest.Run(() => state = Thread.CurrentThread.GetApartmentState());

            Assert.Equal(ApartmentState.STA, state);
        }

        [Fact]
        public void AFailingBodyStillFailsTheTest()
        {
            // The gate is released in a finally, so a throwing body must surface as a failure AND must
            // not wedge every STA test that follows it. If the release were missed, the whole suite
            // would hang here rather than report — which is why this test exists next to the other two.
            var thrown = Assert.Throws<Xunit.Sdk.XunitException>(
                () => StaTest.Run(() => throw new InvalidOperationException("boom")));

            Assert.Contains("boom", thrown.Message, StringComparison.Ordinal);

            // Proves the gate was released: this would deadlock otherwise.
            var ran = false;
            StaTest.Run(() => ran = true);
            Assert.True(ran);
        }
    }
}
