using System.Threading.Tasks;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Where the edit card's right-click "Open file" lands. It used to pass line 0 unconditionally, so
    /// it opened every file at the top — including the ones whose edit the backend had told us the line
    /// of. The card can't do better on its own: locating the change needs the file, which only the host
    /// can read, so the fix is a host callback with the reported line as the floor rather than the answer.
    /// (The locating rule itself is <see cref="CodeWicket.Core.Ide.DiffSideBuilder.LocateLine"/>,
    /// pinned in <see cref="DiffSideBuilderTests"/>.)
    /// </summary>
    public sealed class EditCardNavigationTests
    {
        private const string Path = @"C:\ws\src\Foo.cs";

        [Fact]
        public void OpenFile_AsksTheHostToLocateTheEdit()
        {
            (string Path, string Old, string New, int? Line)? located = null;
            var plainOpens = 0;

            var card = new EditItemViewModel(
                Path, "ORIGINAL", "CHANGED", openDiff: null, workspaceRoot: @"C:\ws",
                openFile: (_, _) => { plainOpens++; return Task.CompletedTask; },
                line: 12, intent: null, operation: null,
                openFileAtDiff: (p, o, n, l) => { located = (p, o, n, l); return Task.CompletedTask; });

            card.OpenFileCommand.Execute(null);

            // The diff — not just the path — has to reach the host, or it has nothing to locate with.
            Assert.Equal((Path, "ORIGINAL", "CHANGED", (int?)12), located);
            // And the top-of-file opener must not ALSO fire: two navigations, the second landing at the
            // top, would undo the first and look exactly like the bug.
            Assert.Equal(0, plainOpens);
        }

        /// <summary>
        /// A host that can't locate (the Desktop/stub hosts, and any future one) still gets the reported
        /// line. This is the half that was purely a bug: the line was already on the card and thrown away.
        /// </summary>
        [Fact]
        public void OpenFile_FallsBackToTheReportedLine()
        {
            (string Path, int Line)? opened = null;

            var card = new EditItemViewModel(
                Path, "ORIGINAL", "CHANGED", openDiff: null, workspaceRoot: @"C:\ws",
                openFile: (p, l) => { opened = (p, l); return Task.CompletedTask; },
                line: 12);

            card.OpenFileCommand.Execute(null);

            Assert.Equal((Path, 12), opened);
        }

        /// <summary>With no reported line and no locator, the top is all that's left — and is honest.</summary>
        [Fact]
        public void OpenFile_FallsBackToTheTopWhenNothingIsKnown()
        {
            (string Path, int Line)? opened = null;

            var card = new EditItemViewModel(
                Path, "ORIGINAL", "CHANGED", openDiff: null, workspaceRoot: @"C:\ws",
                openFile: (p, l) => { opened = (p, l); return Task.CompletedTask; });

            card.OpenFileCommand.Execute(null);

            Assert.Equal((Path, 0), opened);
        }

        /// <summary>
        /// The menu item is offered whenever EITHER opener is wired — a host that supplied only the
        /// diff-aware one must not end up with a card that can't be opened at all.
        /// </summary>
        [Fact]
        public void OpenFile_IsOfferedWithOnlyTheDiffAwareOpener()
        {
            var card = new EditItemViewModel(
                Path, "ORIGINAL", "CHANGED", openDiff: null, workspaceRoot: @"C:\ws",
                openFileAtDiff: (_, _, _, _) => Task.CompletedTask);

            Assert.True(card.CanOpenFile);
        }

        [Fact]
        public void OpenFile_IsNotOfferedWithNoOpenerAtAll()
            => Assert.False(new EditItemViewModel(Path, "ORIGINAL", "CHANGED", openDiff: null).CanOpenFile);
    }
}
