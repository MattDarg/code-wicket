using System.Windows.Input;

namespace CodeWicket.UI.Views
{
    /// <summary>What pressing Enter in the composer means, given the modifiers held with it.</summary>
    public enum EnterGesture
    {
        /// <summary>Not ours: let the TextBox insert a line break.</summary>
        Newline,

        /// <summary>Queue for the end of the turn — the default, and the only non-destructive rung.</summary>
        Queue,

        /// <summary>Promote to the agent's next safe point.</summary>
        NextStep,

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
        /// One modifier per rung: Enter queues for the turn's end, Ctrl+Enter promotes to the next safe
        /// point, Ctrl+Shift+Enter cuts in now. Shift ALONE is a newline — and "alone" is the whole point,
        /// since Shift is also present on the top rung.
        /// </summary>
        public static EnterGesture For(ModifierKeys modifiers)
        {
            var ctrl = (modifiers & ModifierKeys.Control) != 0;
            var shift = (modifiers & ModifierKeys.Shift) != 0;

            if (shift && !ctrl)
                return EnterGesture.Newline;

            return ctrl
                ? shift ? EnterGesture.Now : EnterGesture.NextStep
                : EnterGesture.Queue;
        }
    }
}
