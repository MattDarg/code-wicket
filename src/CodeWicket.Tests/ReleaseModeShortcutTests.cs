using System.Windows.Input;
using CodeWicket.UI.Views;
using Xunit;
using ModeKey = CodeWicket.UI.Views.ChatView.ReleaseModeKey;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Alt+Q, which flips the tray between End of turn and Next step. The whole decision is one function
    /// of the raw event fields, so these drive exactly what the key handler receives: an Alt chord arrives
    /// as <see cref="Key.System"/> with the letter in <c>SystemKey</c>. What it declines is the part worth
    /// pinning — a widened check turns a stray key into a silent change to how the next message is sent.
    /// </summary>
    public sealed class ReleaseModeShortcutTests
    {
        static ModeKey Resolve(Key key, Key systemKey, ModifierKeys modifiers, bool isRepeat = false) =>
            ChatView.ResolveReleaseModeKey(key, systemKey, modifiers, isRepeat);

        /// <summary>The event as WPF raises it for Alt+Q: the letter is in SystemKey, not Key.</summary>
        [Fact]
        public void AltQAsWpfDeliversItIsTheToggle() =>
            Assert.Equal(ModeKey.Toggle, Resolve(Key.System, Key.Q, ModifierKeys.Alt));

        /// <summary>
        /// Held down, the chord repeats. A repeat is claimed — so it does not fall through to anything
        /// else — and applied as nothing, or the mode would flip on every repeat and land wherever the
        /// count left it, each flip a potential release.
        /// </summary>
        [Fact]
        public void AnAutoRepeatIsSwallowedNotApplied() =>
            Assert.Equal(ModeKey.Swallow, Resolve(Key.System, Key.Q, ModifierKeys.Alt, isRepeat: true));

        [Theory]
        [InlineData(ModifierKeys.None)]
        [InlineData(ModifierKeys.Shift)]
        [InlineData(ModifierKeys.Control)]
        [InlineData(ModifierKeys.Alt | ModifierKeys.Shift)]
        [InlineData(ModifierKeys.Alt | ModifierKeys.Control)]
        public void OnlyAltAloneClaimsQ(ModifierKeys modifiers) =>
            Assert.Equal(ModeKey.None, Resolve(Key.System, Key.Q, modifiers));

        /// <summary>
        /// The neighbours that were refused: Alt+S is the Test menu's access key, Alt+M is a different
        /// letter however close, and Ctrl+M begins VS's global Go To View chord, so it never arrives at all.
        /// </summary>
        [Fact]
        public void TheRefusedKeysAreNotClaimed()
        {
            Assert.Equal(ModeKey.None, Resolve(Key.System, Key.S, ModifierKeys.Alt));
            Assert.Equal(ModeKey.None, Resolve(Key.System, Key.M, ModifierKeys.Alt));
            Assert.Equal(ModeKey.None, Resolve(Key.M, Key.None, ModifierKeys.Control));
        }

        /// <summary>A plain Q typed into the composer is text.</summary>
        [Fact]
        public void TypingQIsNotTheShortcut() =>
            Assert.Equal(ModeKey.None, Resolve(Key.Q, Key.None, ModifierKeys.None));

        /// <summary>
        /// SystemKey is read ONLY when the event says <see cref="Key.System"/>. Reading it whenever it holds
        /// something would let a stale SystemKey decide an event whose own key says otherwise — so a real Q
        /// with a stale S there is still Q, and a real S with a stale Q is not the shortcut.
        /// </summary>
        [Fact]
        public void TheSystemKeyIsReadOnlyForASystemEvent()
        {
            Assert.Equal(ModeKey.Toggle, Resolve(Key.Q, Key.S, ModifierKeys.Alt));
            Assert.Equal(ModeKey.None, Resolve(Key.S, Key.Q, ModifierKeys.Alt));
        }
    }
}
