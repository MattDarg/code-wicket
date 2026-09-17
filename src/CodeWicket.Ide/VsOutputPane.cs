using System;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using CodeWicket.Core.Ide;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Reads the Output window's ACTIVE pane, for the "Add ▾ → Output window" gesture (the push half
    /// of the Output-pane story; there is no pull, see <see cref="OutputCapture"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The active pane, and no pane picker.</b> Which pane matters is a fact the agent cannot have
    /// and the user already expressed by looking at it — that is the whole reason this is a push
    /// rather than a tool. A submenu of panes would ask the user to answer a question they have
    /// already answered with the dropdown in the Output window itself.
    /// </para>
    /// <para>
    /// <b>Via DTE rather than <c>IVsOutputWindow</c>.</b> The shell interface addresses panes by GUID
    /// and has no notion of "the one showing", which is precisely the fact wanted here;
    /// <c>OutputWindow.ActivePane</c> is that fact, and its <c>TextDocument</c> carries both the text
    /// and the user's selection in it. The same <c>DTE2</c> this class is handed already backs the
    /// workspace snapshot and the debug capture.
    /// </para>
    /// <para>
    /// Everything here is UI-thread-only and best-effort: the Output window may not be open, may have
    /// no pane, and a pane can be cleared by a build starting between the menu opening and the click.
    /// Each of those is a null, which the caller reports rather than turning into an empty chip.
    /// </para>
    /// </remarks>
    internal static class VsOutputPane
    {
        /// <summary>
        /// Whether there is a pane worth offering — the window is open, a pane is active, and it has
        /// something in it. Cheap by contract, since it answers a menu that is about to open.
        /// </summary>
        /// <remarks>
        /// It reads the pane's TEXT rather than merely finding a pane, because an empty Build pane is
        /// the ordinary state before the first build and offering the gesture there would produce
        /// nothing. That read is a COM call over a string that can be large; it is bounded in practice
        /// by being the active pane only, and by running once per menu opening rather than on a poll —
        /// the same contract <c>IsDebuggerStopped</c> is written under.
        /// </remarks>
        public static bool HasContent(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return !string.IsNullOrWhiteSpace(ReadPaneText(dte, out _, out _));
        }

        /// <summary>
        /// The active pane as a capture, or null when there is nothing to take. The selection wins
        /// where there is one — see <see cref="OutputCapture.Build"/> for why that rule is as plain as
        /// it is.
        /// </summary>
        public static OutputPaneCapture Capture(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var text = ReadPaneText(dte, out var name, out var selected);
            return OutputCapture.Build(name, text, selected);
        }

        /// <summary>
        /// The active pane's name, whole text and current selection. Null text for every "there is
        /// nothing here" case, so callers never have to distinguish a closed window from an empty pane
        /// — neither has anything to hand over.
        /// </summary>
        private static string ReadPaneText(DTE2 dte, out string name, out string selected)
        {
            // Restated here rather than left to the public callers: VSTHRD010 does not track the fact
            // across a call, and every EnvDTE member below is a main-thread member.
            ThreadHelper.ThrowIfNotOnUIThread();

            name = null;
            selected = null;
            if (dte == null)
                return null;

            try
            {
                var window = dte.ToolWindows?.OutputWindow;
                var pane = window?.ActivePane;
                if (pane == null)
                    return null;

                name = pane.Name;

                var document = pane.TextDocument;
                if (document == null)
                    return null;

                // The selection first: reading it does not disturb it, but taking the whole document
                // walks EditPoints, and doing that first has moved a selection in other EnvDTE code.
                if (document.Selection is EnvDTE.TextSelection selection)
                    selected = selection.Text;

                var start = document.StartPoint?.CreateEditPoint();
                return start?.GetText(document.EndPoint);
            }
            catch (Exception)
            {
                // A tool window being torn down, a pane owned by an extension that has just unloaded,
                // or any of EnvDTE's ordinary COM disappointments. None is worth failing a menu over.
                name = null;
                selected = null;
                return null;
            }
        }
    }
}
