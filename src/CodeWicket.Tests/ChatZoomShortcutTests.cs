using System.Windows.Input;
using CodeWicket.UI.Views;
using Xunit;
using Shortcut = CodeWicket.UI.Views.ChatView.ZoomShortcut;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The chat's keyboard zoom (Ctrl+plus / Ctrl+minus / Ctrl+0). Several of these combinations sit on
    /// top of bindings in VS's global command table, so the rules about which ones we decline are the
    /// part worth pinning — a widened modifier check would quietly eat an IDE navigation command.
    /// </summary>
    public sealed class ChatZoomShortcutTests
    {
        static Shortcut Resolve(Key key, ModifierKeys modifiers = ModifierKeys.Control) =>
            ChatView.ResolveZoomShortcut(key, modifiers);

        [Theory]
        [InlineData(Key.OemPlus)]   // Ctrl+= — the unshifted "plus" key
        [InlineData(Key.Add)]       // Ctrl+NumPad+
        public void Ctrl_plus_zooms_in(Key key) => Assert.Equal(Shortcut.In, Resolve(key));

        [Fact]
        public void Shift_is_allowed_on_plus()
        {
            // Ctrl++ is physically Ctrl+Shift+= on most layouts, and it is what a user means when they
            // say "Ctrl and plus" — refusing Shift here would make the shortcut miss its obvious form.
            Assert.Equal(Shortcut.In, Resolve(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift));
        }

        [Theory]
        [InlineData(Key.OemMinus)]  // Ctrl+- — shadows Navigate Backward, but only while the chat has focus
        [InlineData(Key.Subtract)]  // Ctrl+NumPad-
        public void Ctrl_minus_zooms_out(Key key) => Assert.Equal(Shortcut.Out, Resolve(key));

        [Theory]
        [InlineData(Key.D0)]
        [InlineData(Key.NumPad0)]
        public void Ctrl_zero_resets(Key key) => Assert.Equal(Shortcut.Reset, Resolve(key));

        [Fact]
        public void Shift_is_refused_on_minus()
        {
            // Ctrl+Shift+- is Navigate Forward (global). Nothing about the chat pane earns the right to
            // eat it, and unlike plus there is no user expectation that Shift means the same gesture.
            Assert.Equal(Shortcut.None, Resolve(Key.OemMinus, ModifierKeys.Control | ModifierKeys.Shift));
        }

        [Fact]
        public void Shift_is_refused_on_the_reset()
        {
            Assert.Equal(Shortcut.None, Resolve(Key.D0, ModifierKeys.Control | ModifierKeys.Shift));
        }

        [Theory]
        [InlineData(Key.OemMinus)]  // Ctrl+Alt+- is Peek Navigate Backward
        [InlineData(Key.OemPlus)]   // Ctrl+Alt+= is Peek Navigate Forward
        [InlineData(Key.D0)]        // Ctrl+Alt+0 is a window layout
        public void Alt_is_refused_throughout(Key key) =>
            Assert.Equal(Shortcut.None, Resolve(key, ModifierKeys.Control | ModifierKeys.Alt));

        [Theory]
        [InlineData(ModifierKeys.None)]
        [InlineData(ModifierKeys.Shift)]
        [InlineData(ModifierKeys.Alt)]
        public void Without_control_nothing_is_claimed(ModifierKeys modifiers)
        {
            // A bare "-" typed into the message box must reach the message box.
            Assert.Equal(Shortcut.None, Resolve(Key.OemMinus, modifiers));
            Assert.Equal(Shortcut.None, Resolve(Key.OemPlus, modifiers));
            Assert.Equal(Shortcut.None, Resolve(Key.D0, modifiers));
        }

        [Theory]
        [InlineData(1.0, "100%")]
        [InlineData(1.25, "125%")]
        [InlineData(0.5, "50%")]
        [InlineData(3.0, "300%")]
        // Zoom moves in 0.1 steps from 1.0, so the scale accumulates binary floating-point error —
        // 1.0 - 0.1 - 0.1 - 0.1 is 0.7000000000000001, and 0.1 x 7 is 0.7000000000000001 too. The
        // readout must never show that.
        [InlineData(0.7000000000000001, "70%")]
        [InlineData(1.2000000000000002, "120%")]
        public void Zoom_formats_as_a_whole_percentage(double zoom, string expected) =>
            Assert.Equal(expected, ChatView.FormatZoom(zoom));

        [Fact]
        public void Unrelated_keys_are_left_alone()
        {
            // Ctrl+C / Ctrl+V / Ctrl+A over the transcript and the message box.
            Assert.Equal(Shortcut.None, Resolve(Key.C));
            Assert.Equal(Shortcut.None, Resolve(Key.V));
            Assert.Equal(Shortcut.None, Resolve(Key.A));
            Assert.Equal(Shortcut.None, Resolve(Key.D9));
        }
    }
}
