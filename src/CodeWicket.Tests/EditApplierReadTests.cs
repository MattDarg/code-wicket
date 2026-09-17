using System;
using System.IO;
using System.Threading.Tasks;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A read of a file that isn't there must FAIL, not return an empty string.
    ///
    /// <para>Every applier used to return <c>""</c>, which hands the agent a fact it cannot check: an
    /// absent file and an empty one look identical. The damaging shape isn't the obvious one — a path
    /// that resolves slightly wrong reads as empty, the agent concludes the file is blank, writes a
    /// complete "fixed" version to that wrong path (a write CREATES, so it succeeds), and reports the
    /// edit done while the real file is untouched and nothing anywhere says so.</para>
    ///
    /// <para>Pinned against <see cref="StubIdeServices"/> because it is the applier the offline hosts
    /// actually run — the VS one is the same change and reachable only in a live Visual Studio instance, like the rest of that class. The
    /// contract is deliberately shared: the agent must never see read semantics that depend on which
    /// host it happens to be talking to.</para>
    /// </summary>
    public sealed class EditApplierReadTests : IDisposable
    {
        private readonly string _root;
        private readonly StubIdeServices _ide;

        public EditApplierReadTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cwkt-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _ide = new StubIdeServices(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort scratch cleanup */ }
        }

        [Fact]
        public async Task ReadingAMissingFileFailsInsteadOfReturningEmpty()
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => _ide.Edits.ReadTextFileAsync("Message/NotHere.cs"));

            // The resolved path, not the agent's argument: when the two differ, the difference IS the
            // bug being reported, and the agent's own string tells it nothing it didn't already know.
            Assert.Contains("NotHere.cs", ex.Message);
            Assert.Contains(_root, ex.Message);
        }

        [Fact]
        public async Task ReadingAnExistingFileStillReturnsItsText()
        {
            File.WriteAllText(Path.Combine(_root, "Here.cs"), "line1\nline2\nline3\n");

            Assert.Contains("line2", await _ide.Edits.ReadTextFileAsync("Here.cs"));
        }

        /// <summary>
        /// The boundary. A range past the end of a file that EXISTS is a legitimate negative — we
        /// looked, and there is nothing there — so it stays an empty result. Only "couldn't look" is an
        /// error. Failing this case too would teach an agent to read a harmless over-read as a broken
        /// file.
        /// </summary>
        [Fact]
        public async Task ARangePastTheEndOfAnExistingFileIsEmptyRatherThanAnError()
        {
            File.WriteAllText(Path.Combine(_root, "Short.cs"), "only one line\n");

            Assert.Equal(string.Empty, await _ide.Edits.ReadTextFileAsync("Short.cs", line: 500));
        }
    }
}
