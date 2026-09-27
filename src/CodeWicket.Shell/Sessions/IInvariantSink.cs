namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// Where a class reports a breach: a runtime assertion that REPAIRS instead of throwing. The site
    /// has detected a condition that should never occur and has already repaired it before it reports -
    /// <see cref="SessionLifetime"/>'s <c>Commit</c> voids a stale lease, and <c>PromptDelivery</c>'s
    /// <c>EndPendingLeftByAClear</c> ends an orphaned send. The report records that a repair path ran;
    /// it never changes what the product does.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the report goes.</b> In the product it is one line in <c>engine.log</c>, and
    /// <see cref="Invariants.Logged(System.Action{string})"/> is that line's one writer: a breach site
    /// passes its whole sentence and writes nothing itself. In tests, ANY test that drives the code into
    /// the condition fails, whatever that test is about: <c>StaTest.Run</c> opens a recording scope
    /// through <see cref="Invariants.OpenScope"/>, which reaches the view-model through
    /// <see cref="Invariants.Ambient"/>, and <c>InvariantCheckedTest</c> hands its own recorder to the
    /// class under test; each fails the test on any breach still recorded when the test ends.</para>
    /// <para><b>Not a throw, and not <c>Debug.Assert</c>.</b> Most breach sites run inside
    /// fire-and-forget tasks, where an exception becomes an unobserved faulted task no test sees; and a
    /// Debug-only check would make the gates, which build Debug, measure different behaviour from what
    /// ships.</para>
    /// </remarks>
    public interface IInvariantSink
    {
        void Breach(string message);
    }
}
