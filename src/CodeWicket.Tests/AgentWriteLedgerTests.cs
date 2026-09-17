using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The record of what the agent has written, which the build and test tools check against the
    /// loaded projects (issue #257). One file, one entry, whatever spelling it arrived in — the two
    /// spellings measured on the wire are a Windows path and Kiro v3's percent-encoded file URI.
    /// </summary>
    public sealed class AgentWriteLedgerTests
    {
        [Fact]
        public void ARootedPathIsRecordedAndFound()
        {
            var ledger = new AgentWriteLedger();

            Assert.True(ledger.Record(@"C:\ws\proj\Foo.cs"));

            Assert.True(ledger.Contains(@"C:\ws\proj\Foo.cs"));
            Assert.Equal(1, ledger.Count);
        }

        /// <summary>
        /// The completed frame on Kiro v3 names the file as <c>file:///c%3A/…</c> while our own write
        /// names it as a Windows path; the mapper's canonicalizer folds them, and so must this.
        /// </summary>
        [Fact]
        public void TwoSpellingsOfOneFileAreOneEntry()
        {
            var ledger = new AgentWriteLedger();

            ledger.Record(@"C:\ws\proj\Foo.cs");
            ledger.Record("file:///c%3A/ws/proj/Foo.cs");
            ledger.Record(@"c:/ws/proj/sub/../Foo.cs");

            Assert.Equal(1, ledger.Count);
            Assert.True(ledger.Contains("file:///c%3A/ws/proj/Foo.cs"));
        }

        /// <summary>
        /// A relative path is refused, not rooted: the shell does not know the agent's cwd, and rooting
        /// it against anything else names a different file (issue #54). Refusing loses one candidate;
        /// guessing reports a file that was never written.
        /// </summary>
        [Theory]
        [InlineData("src/Foo.cs")]
        [InlineData(@"proj\Foo.cs")]
        [InlineData("Foo.cs")]
        [InlineData("")]
        [InlineData(null)]
        public void ARelativeOrEmptyPathIsRefused(string? path)
        {
            var ledger = new AgentWriteLedger();

            Assert.False(ledger.Record(path));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void ForgetRemovesOneEntryByAnySpelling()
        {
            var ledger = new AgentWriteLedger();
            ledger.Record(@"C:\ws\A.csproj");
            ledger.Record(@"C:\ws\B.cs");

            Assert.True(ledger.Forget("file:///C%3A/ws/A.csproj"));
            Assert.False(ledger.Forget(@"C:\ws\A.csproj"));

            Assert.False(ledger.Contains(@"C:\ws\A.csproj"));
            Assert.True(ledger.Contains(@"C:\ws\B.cs"));
        }

        [Fact]
        public void ClearEmptiesTheLedger()
        {
            var ledger = new AgentWriteLedger();
            ledger.Record(@"C:\ws\A.cs");
            ledger.Record(@"C:\ws\B.cs");

            ledger.Clear();

            Assert.Equal(0, ledger.Count);
            Assert.Empty(ledger.Snapshot());
        }

        /// <summary>
        /// An import above several projects is taken by each project's OWN reload and no other's, so
        /// the ledger orders the write and each reload against each other. Observed in Visual Studio without it:
        /// the import note kept naming a project that had demonstrably reloaded.
        /// </summary>
        [Fact]
        public void AReloadAfterAWriteIsSeenAndOneBeforeItIsNot()
        {
            var ledger = new AgentWriteLedger();
            const string Props = @"C:\ws\Directory.Build.props";
            const string A = @"C:\ws\A\A.csproj";
            const string B = @"C:\ws\B\B.csproj";

            ledger.NoteReloaded(A);          // before the write: does not count
            ledger.Record(Props);
            ledger.NoteReloaded(B);          // after the write: B has taken it

            Assert.False(ledger.WasReloadedSince(A, Props));
            Assert.True(ledger.WasReloadedSince("file:///c%3A/ws/B/B.csproj", Props));
        }

        [Fact]
        public void AWriteAfterAReloadMakesTheProjectStaleAgain()
        {
            var ledger = new AgentWriteLedger();
            const string Props = @"C:\ws\Directory.Build.props";
            const string A = @"C:\ws\A\A.csproj";

            ledger.Record(Props);
            ledger.NoteReloaded(A);
            Assert.True(ledger.WasReloadedSince(A, Props));

            ledger.Record(Props);            // written again: the reload predates this one
            Assert.False(ledger.WasReloadedSince(A, Props));
        }

        /// <summary>Unknown on either side is false: a reload nobody saw is not a reload.</summary>
        [Fact]
        public void AnUnrecordedWriteOrReloadIsNotReloadedSince()
        {
            var ledger = new AgentWriteLedger();
            ledger.Record(@"C:\ws\Directory.Build.props");

            Assert.False(ledger.WasReloadedSince(@"C:\ws\A\A.csproj", @"C:\ws\Directory.Build.props"));
            Assert.False(ledger.WasReloadedSince(@"C:\ws\A\A.csproj", @"C:\ws\never-written.props"));
            Assert.False(ledger.WasReloadedSince(null, @"C:\ws\Directory.Build.props"));

            ledger.NoteReloaded(@"C:\ws\A\A.csproj");
            ledger.Clear();
            ledger.Record(@"C:\ws\Directory.Build.props");
            Assert.False(ledger.WasReloadedSince(@"C:\ws\A\A.csproj", @"C:\ws\Directory.Build.props"));
        }

        [Fact]
        public void SnapshotIsCanonicalAndOldestFirst()
        {
            var ledger = new AgentWriteLedger();
            ledger.Record(@"c:\ws\first.cs");
            ledger.Record("file:///c%3A/ws/second.cs");

            var snapshot = ledger.Snapshot();

            Assert.Equal(new[] { @"C:\ws\first.cs", @"C:\ws\second.cs" }, snapshot);
        }
    }
}
