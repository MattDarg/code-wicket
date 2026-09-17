using System.Windows;
using System.Windows.Media;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// The themed values a rendered markdown document needs, looked up ONCE per render instead of
    /// registered as a resource reference on every element that wants one (issue #86).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not guessed.</b> A 17,730-char message logged <c>parseMax=38.2 attachMax=1106.2</c>
    /// inside devenv: constructing every element of it cost ~40ms, and assigning the finished document
    /// to its viewer cost a full second. That message carries roughly <b>621</b>
    /// <see cref="System.Windows.FrameworkContentElement.SetResourceReference"/> registrations — 258
    /// inline code spans at two each, 105 hyperlinks at one, plus the document's own — every one made
    /// while the document is parentless and every one resolved when it meets a tree. In devenv that is
    /// ~1.8ms each, against ~0.012ms in a standalone host: the chat sits inside a tool-window pane, under
    /// the 28 <c>Chat.*</c> overrides the VSIX injects, above devenv's own <c>Application.Resources</c>.
    /// </para>
    /// <para>
    /// <b>The trade this makes.</b> A resource REFERENCE keeps tracking its key, so a theme change
    /// repaints an already-built document for free; a resolved VALUE does not. That is why the host must
    /// re-render visible messages when the theme changes (<c>VsTheme</c> already has the hook), and it is
    /// the whole cost of this change — paid once per theme switch, which is rare, instead of ~621 times
    /// per message realised, which happens on every scroll.
    /// </para>
    /// <para>
    /// <b>Null is a real answer and must stay one.</b> <see cref="Resolve"/> returns null when the source
    /// element cannot answer — which is exactly the case of a viewer rendering while OUT of the tree, a
    /// routine event under container recycling. The renderer then falls back to resource references, so
    /// such a document is no worse than it was before this existed and is still corrected by
    /// <c>MarkdownText.EnsureRerenderOnReattach</c>. Getting this backwards would bake a detached
    /// viewer's non-answers into a document as permanent values, which is the "the last message goes
    /// dense" bug made incurable.
    /// </para>
    /// </remarks>
    public sealed class MarkdownPalette
    {
        /// <summary>Body text (<c>Chat.Foreground</c>).</summary>
        public Brush? Foreground { get; private set; }

        /// <summary>Quoted text (<c>Chat.SubtleForeground</c>).</summary>
        public Brush? SubtleForeground { get; private set; }

        /// <summary>Rules, quote bars and table cell edges (<c>Chat.Border</c>).</summary>
        public Brush? Border { get; private set; }

        /// <summary>File and web links (<c>Chat.LinkForeground</c>).</summary>
        public Brush? LinkForeground { get; private set; }

        /// <summary>Inline code and code block fill (<c>Chat.CodeBackground</c>).</summary>
        public Brush? CodeBackground { get; private set; }

        /// <summary>Inline code and code block typeface (<c>Chat.CodeFontFamily</c>).</summary>
        public FontFamily? CodeFontFamily { get; private set; }

        /// <summary>The task-list checkbox style (<c>Chat.TaskCheckBox</c>).</summary>
        public Style? TaskCheckBox { get; private set; }

        /// <summary>
        /// Looks the values up against <paramref name="source"/> — a viewer that is in the tree, so the
        /// lookups walk the same dictionaries the resource references used to walk, once each.
        /// </summary>
        /// <returns>
        /// Null when the source cannot answer, which is how a detached render is detected: the renderer
        /// falls back to resource references, as it always did. <see cref="CodeFontFamily"/> is the test
        /// because it is the value whose absence was diagnosed on a live session — a detached render logs
        /// <c>codeFont=&lt;null&gt;</c>, and every inline code span silently falls back to the prose font.
        /// </returns>
        public static MarkdownPalette? Resolve(FrameworkElement? source)
        {
            if (source is null)
                return null;

            var palette = new MarkdownPalette
            {
                Foreground = source.TryFindResource("Chat.Foreground") as Brush,
                SubtleForeground = source.TryFindResource("Chat.SubtleForeground") as Brush,
                Border = source.TryFindResource("Chat.Border") as Brush,
                LinkForeground = source.TryFindResource("Chat.LinkForeground") as Brush,
                CodeBackground = source.TryFindResource("Chat.CodeBackground") as Brush,
                CodeFontFamily = source.TryFindResource("Chat.CodeFontFamily") as FontFamily,
                TaskCheckBox = source.TryFindResource("Chat.TaskCheckBox") as Style,
            };

            return palette.CodeFontFamily is null ? null : palette;
        }

        /// <summary>
        /// Whether <paramref name="other"/> resolved to the same values — i.e. whether a re-render would
        /// change anything.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Compared by VALUE, never by reference, and that is the whole difficulty.</b> The VSIX
        /// rebuilds every <c>Chat.*</c> brush from scratch on each refresh (<c>VsTheme.SetBrushes</c>
        /// blends and allocates a fresh <c>SolidColorBrush</c> per key), so reference equality says
        /// "changed" every single time and the gate would never close.
        /// </para>
        /// <para>
        /// Deliberately conservative where it cannot tell: an unrecognised <see cref="Brush"/> subclass
        /// falls back to reference equality, so the answer is "different" and the caller re-renders. A
        /// wrong "same" leaves the transcript in the old theme, which is a visible bug; a wrong
        /// "different" costs one re-render nobody sees.
        /// </para>
        /// </remarks>
        public bool Matches(MarkdownPalette? other) =>
            other is not null
            && SameBrush(Foreground, other.Foreground)
            && SameBrush(SubtleForeground, other.SubtleForeground)
            && SameBrush(Border, other.Border)
            && SameBrush(LinkForeground, other.LinkForeground)
            && SameBrush(CodeBackground, other.CodeBackground)
            && string.Equals(CodeFontFamily?.Source, other.CodeFontFamily?.Source, System.StringComparison.Ordinal)
            && ReferenceEquals(TaskCheckBox, other.TaskCheckBox);

        private static bool SameBrush(Brush? left, Brush? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is SolidColorBrush a && right is SolidColorBrush b)
                return a.Color == b.Color && a.Opacity.Equals(b.Opacity);
            return false;
        }
    }
}
