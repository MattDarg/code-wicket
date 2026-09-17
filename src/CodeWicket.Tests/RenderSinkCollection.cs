using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Serialises every test that moves <c>RenderDiagnosticsLog.Sink</c>, which is one process-wide
    /// static. Same shape and same reason as <see cref="StoragePathCollection"/>: xUnit runs test
    /// classes in parallel, so two classes swapping one static interleave.
    /// </summary>
    /// <remarks>
    /// Found rather than foreseen. A new class asserted that a trace allocates nothing with no sink
    /// installed, setting and restoring the sink around it; run in parallel with the class that owns
    /// the sink, it made that one's queued lines arrive against a sink it had not installed, and the
    /// ORDERING test failed - eight lines where it expected its own. Neither class is wrong on its
    /// own, which is the signature of this defect: it is scheduling, not logic.
    /// <para>A collection rather than a fixture: nothing needs sharing, only ordering.</para>
    /// </remarks>
    [CollectionDefinition(Name)]
    public sealed class RenderSinkCollection
    {
        public const string Name = "render-sink";
    }
}
