using System;
using System.Linq;
using CodeWicket.Shell.Sessions;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Base class for plain (non-STA) tests of a class that reports invariant breaches. Hand
    /// <see cref="Breaches"/> to the class under test as its required <see cref="IInvariantSink"/>;
    /// any breach still recorded when the test ends fails it, naming each one.
    /// </summary>
    /// <remarks>
    /// <para><b>Not opt-in.</b> The check is in <see cref="Dispose"/>, which xunit runs after every
    /// test and reports a throw from as the test's failure - so a test cannot forget to look. A test
    /// that EXPECTS a breach asserts it through <see cref="BreachRecorder.Drain"/>, which leaves
    /// nothing behind. Proved by <c>InvariantRecorderTests.AnUndrainedBreachFailsTheTest</c>, over a
    /// <see cref="Dispose"/> injected to ignore its recorder.</para>
    /// <para><b>An instance, not a thread-bound list</b>: a breach raised on a pool thread after an
    /// await lands here too. View-model tests are covered by the same check in <c>StaTest.Run</c>,
    /// through <see cref="Invariants.Ambient"/>'s scope.</para>
    /// </remarks>
    public abstract class InvariantCheckedTest : IDisposable
    {
        protected BreachRecorder Breaches { get; } = new BreachRecorder();

        /// <summary>A derived class's own teardown, run before the breach check whatever it throws.</summary>
        protected virtual void Cleanup()
        {
        }

        public void Dispose()
        {
            try
            {
                Cleanup();
            }
            finally
            {
                var left = Breaches.Drain();
                if (left.Count > 0)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"{left.Count} invariant breach(es) recorded and not drained by the test:"
                        + string.Concat(left.Select(b => Environment.NewLine + "  - " + b)));
                }
            }
        }
    }
}
