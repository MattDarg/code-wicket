using System.Windows.Input;

namespace CodeWicket.UI.Views
{
    /// <summary>What pressing Enter in the composer means, given the modifiers held with it.</summary>
    public enum EnterGesture
    {
        /// <summary>Not ours: let the TextBox insert a line break.</summary>
        Newline,

        /// <summary>
        /// Hold in the tray, released wherever the tray's mode says: the turn's end under End of turn, the next
        /// safe point under Next step. The mode starts at the configured default and the pill or Alt+Q
        /// changes it, so this rung is not "queue" — it is whatever the pill shows.
        /// </summary>
        Hold,

        /// <summary>Cut in now, interrupting whatever is running.</summary>
        Now,
    }

    /// <summary>
    /// The Enter ladder, as a function of the modifiers alone.
    /// <para>
    /// Lifted out of the key handler because it could not be tested there: the handler reads the live
    /// <see cref="Keyboard.Modifiers"/>, which a synthesized <c>PreviewKeyDown</c> cannot fake, so the
    /// Desktop checks drive the view-model's commands instead — and every rung passed while the top one
    /// was unreachable. Shift was tested for "newline" before Ctrl had been looked at, so Ctrl+Shift+Enter
    /// returned early and inserted a line break, leaving <c>SendNowCommand</c> dead code on the keyboard
    /// from the day it was added.
    /// </para>
    /// </summary>
    public static class EnterGestures
    {
        /// <summary>
        /// Enter holds at the tray's mode and Ctrl+Shift+Enter cuts in now. Ctrl+Enter is a plain Enter:
        /// it was the middle rung, promoting the whole tray to Steer and leaving the pill there, and it
        /// was retired for Alt+Q, which flips the mode both ways. A key that once sent must not start
        /// inserting line breaks, so it keeps sending. Shift ALONE is a newline — and "alone" is the
        /// whole point, since Shift is also present on the top rung.
        /// </summary>
        public static EnterGesture For(ModifierKeys modifiers)
        {
            var ctrl = (modifiers & ModifierKeys.Control) != 0;
            var shift = (modifiers & ModifierKeys.Shift) != 0;

            if (shift && !ctrl)
                return EnterGesture.Newline;

            return ctrl && shift ? EnterGesture.Now : EnterGesture.Hold;
        }
    }
}
