using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Serialises every test that moves a storage-path override — <c>ExtensionConfig</c>'s config
    /// path and <c>AttachmentStore</c>'s root. xUnit runs test classes in parallel, and each
    /// override is one process-wide static.
    /// </summary>
    /// <remarks>
    /// Every class here restores its override to null when it finishes. Interleaved with another
    /// class, that restore lands mid-test, and the other class's next read resolves the REAL
    /// <c>%APPDATA%</c> config or attachment root: a result that depends on the developer's machine,
    /// failing on one box and passing on the next, and a save made at that moment would write the
    /// developer's own file. That is the harness rule in AGENTS.md — automated runs must not touch
    /// the user's real config — defeated by scheduling rather than by any one test being wrong. So
    /// every class that sets either override belongs here.
    /// <para>
    /// A collection rather than a fixture: nothing needs sharing, only ordering.
    /// </para>
    /// </remarks>
    [CollectionDefinition(Name)]
    public sealed class StoragePathCollection
    {
        public const string Name = "storage-path";
    }
}
