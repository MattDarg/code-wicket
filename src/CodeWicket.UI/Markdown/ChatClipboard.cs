using System;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Clipboard fidelity for transcript text that embeds non-text inlines. WPF's stock
    /// <see cref="TextRange.Text"/> silently drops <see cref="InlineUIContainer"/> content, so a
    /// selection copied over an emoji (or task-list checkbox, or inline image) would lose it.
    /// Any embedded element carries its plain-text stand-in in the <c>CopyText</c> attached
    /// property (emoji → the real character sequence; checkbox → "[x] "; image → alt text), and
    /// <see cref="GetText"/> walks the range appending run text plus those stand-ins.
    /// <see cref="InterceptCopy"/> reroutes a control's ApplicationCommands.Copy through it — both
    /// Ctrl+C and the context-menu "Copy" ride the same routed command, so no menu changes needed.
    /// </summary>
    public static class ChatClipboard
    {
        public static readonly DependencyProperty CopyTextProperty = DependencyProperty.RegisterAttached(
            "CopyText", typeof(string), typeof(ChatClipboard), new PropertyMetadata(null));

        public static string? GetCopyText(DependencyObject element) => (string?)element.GetValue(CopyTextProperty);

        public static void SetCopyText(DependencyObject element, string? value) => element.SetValue(CopyTextProperty, value);

        /// <summary>
        /// Extracts the plain text of a range including the CopyText stand-ins of embedded elements.
        /// Modelled on Emoji.Wpf's own TextSelection walk: Text context appends the run text clamped
        /// to the range end; an ElementStart whose element carries CopyText appends the stand-in.
        /// </summary>
        public static string GetText(TextRange range)
        {
            var sb = new StringBuilder();
            for (var p = range.Start; p != null && p.CompareTo(range.End) < 0;
                 p = p.GetNextContextPosition(LogicalDirection.Forward))
            {
                switch (p.GetPointerContext(LogicalDirection.Forward))
                {
                    case TextPointerContext.Text:
                    {
                        var text = p.GetTextInRun(LogicalDirection.Forward);
                        // Clamp the final run to the selection end (a selection can end mid-run).
                        var remaining = p.GetOffsetToPosition(range.End);
                        if (remaining < text.Length)
                            text = text.Substring(0, remaining);
                        sb.Append(text);
                        break;
                    }

                    case TextPointerContext.ElementStart:
                        if (p.GetAdjacentElement(LogicalDirection.Forward) is DependencyObject element
                            && GetCopyText(element) is { Length: > 0 } stand)
                            sb.Append(stand);
                        break;

                    case TextPointerContext.ElementEnd:
                        // A paragraph boundary inside the range reads as a line break. (Forward =
                        // the element whose end tag is being crossed.)
                        if (p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
                            sb.Append(Environment.NewLine);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reroutes <paramref name="host"/>'s ApplicationCommands.Copy through <see cref="GetText"/>
        /// when <paramref name="selection"/> yields a non-empty range that actually contains an
        /// embedded stand-in. Everything else falls through to the control's native copy — which
        /// formats richer structures (e.g. tab-separated table cells) better than the plain walk,
        /// and keeps "Copy all" / empty-selection behavior untouched.
        /// </summary>
        public static void InterceptCopy(UIElement host, Func<TextRange?> selection)
        {
            CommandManager.AddPreviewExecutedHandler(host, (sender, e) =>
            {
                if (e.Command != ApplicationCommands.Copy)
                    return;
                var range = selection();
                if (range == null || range.IsEmpty || !ContainsCopyTextElement(range))
                    return;
                var text = GetText(range);
                if (text.Length == 0)
                    return;
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // The clipboard can be transiently locked by another process; copying is
                    // best-effort and must never crash the chat. Deliberately NOT handled: the
                    // viewer's own Copy is a second attempt at the same thing, and suppressing it
                    // turned a transient lock into a silent no-op — Ctrl+C appearing to work while
                    // the clipboard kept its previous contents, so the next paste is stale data the
                    // user believes they just copied.
                    return;
                }
                e.Handled = true;
            });
        }

        private static bool ContainsCopyTextElement(TextRange range)
        {
            for (var p = range.Start; p != null && p.CompareTo(range.End) < 0;
                 p = p.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
                    && p.GetAdjacentElement(LogicalDirection.Forward) is DependencyObject element
                    && GetCopyText(element) is { Length: > 0 })
                    return true;
            }
            return false;
        }
    }
}
