namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// Where a class reports that one of its own invariants did not hold. The caller has ALREADY done
    /// its unconditional log line and self-healed before it reports: a breach never changes what the
    /// product does, it only makes the test that caused it fail.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a throw, and not <c>Debug.Assert</c>.</b> Most breach sites run inside
    /// fire-and-forget tasks, where an exception becomes an unobserved faulted task no test sees; and a
    /// Debug-only check would make the gates, which build Debug, measure different behaviour from what
    /// ships. The codebase's mechanism is an unconditional log line plus a sink tests can hand in.</para>
    /// <para>Tests hand in a <see cref="BreachRecorder"/>; view-model tests get one through
    /// <see cref="Invariants.Ambient"/>'s scope, which <c>StaTest.Run</c> opens.</para>
    /// </remarks>
    public interface IInvariantSink
    {
        void Breach(string message);
    }
}
