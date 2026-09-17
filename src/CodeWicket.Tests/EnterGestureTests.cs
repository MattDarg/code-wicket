using System.Windows.Input;
using CodeWicket.UI.Views;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The composer's Enter ladder. Every rung was already exercised through the view-model's commands and
    /// every one of those checks passed while the top rung was unreachable from the keyboard: the handler
    /// tested Shift for "newline" before it had looked at Ctrl, so Ctrl+Shift+Enter returned early and
    /// inserted a line break instead. The decision is a function of the modifiers, so it is pinned as one —
    /// the handler itself reads the live <see cref="Keyboard.Modifiers"/>, which a synthesized key event
    /// cannot fake, and that is exactly why the gap existed.
    /// </summary>
    public class EnterGestureTests
    {
        [Theory]
        [InlineData(ModifierKeys.None, EnterGesture.Queue)]
        [InlineData(ModifierKeys.Control, EnterGesture.NextStep)]
        [InlineData(ModifierKeys.Control | ModifierKeys.Shift, EnterGesture.Now)]
        [InlineData(ModifierKeys.Shift, EnterGesture.Newline)]
        public void EachRungIsReachable(ModifierKeys modifiers, EnterGesture expected) =>
            Assert.Equal(expected, EnterGestures.For(modifiers));

        /// <summary>
        /// Shift alone is the newline, and "alone" is load-bearing — Shift is also held on the top rung.
        /// Stated separately from the table because it is the confusion that produced the bug, not just
        /// one row of it.
        /// </summary>
        [Fact]
        public void ShiftIsANewlineOnlyWithoutCtrl()
        {
            Assert.Equal(EnterGesture.Newline, EnterGestures.For(ModifierKeys.Shift));
            Assert.NotEqual(EnterGesture.Newline, EnterGestures.For(ModifierKeys.Shift | ModifierKeys.Control));
        }

        /// <summary>
        /// Alt is nobody's modifier here, so it must not silently promote a rung: Alt+Enter is Quick
        /// Actions in the editor and a user who hits it in the composer means an ordinary send at worst.
        /// </summary>
        [Theory]
        [InlineData(ModifierKeys.Alt, EnterGesture.Queue)]
        [InlineData(ModifierKeys.Alt | ModifierKeys.Control, EnterGesture.NextStep)]
        public void AltChangesNothing(ModifierKeys modifiers, EnterGesture expected) =>
            Assert.Equal(expected, EnterGestures.For(modifiers));
    }
}
