using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// The colour-emoji seam. All transcript text that should show colour emoji funnels through
    /// <see cref="AppendTo"/>, which splits a string into plain <see cref="Run"/>s and rendered
    /// emoji inlines. The engine behind the seam is Emoji.Wpf (EmojiData's sequence scanner +
    /// EmojiInline's cached vector drawings); if WPF ever gains native colour-font rendering
    /// (dotnet/wpf#91) or the engine needs replacing, only this file changes.
    /// </summary>
    public static class EmojiText
    {
        /// <summary>
        /// Cheap first-pass gate: true only if some char could start an emoji sequence. The
        /// overwhelming majority of literals bail here without touching the regex — important
        /// because the assistant FlowDocument is rebuilt on every streamed delta. Also the lazy
        /// first touch of EmojiData (its static ctor parses the emoji list + font tables), so
        /// hosts that never render chat text pay nothing.
        /// </summary>
        public static bool MightContainEmoji(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (Emoji.Wpf.EmojiData.MatchStart.Contains(text[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Appends <paramref name="text"/> to <paramref name="target"/> as Runs interleaved with
        /// colour-emoji inlines. <paramref name="fontSize"/> sizes the emoji drawing (pass the
        /// surrounding text size — headings pass their larger size so emoji scale with them).
        /// </summary>
        public static void AppendTo(InlineCollection target, string text, double fontSize)
        {
            if (text.Length == 0 || !MightContainEmoji(text))
            {
                if (text.Length > 0)
                    target.Add(new Run(text));
                return;
            }

            var consumed = 0;
            foreach (System.Text.RegularExpressions.Match match in Emoji.Wpf.EmojiData.MatchOne.Matches(text))
            {
                if (match.Index > consumed)
                    target.Add(new Run(text.Substring(consumed, match.Index - consumed)));
                target.Add(CreateEmojiInline(match.Value, fontSize));
                consumed = match.Index + match.Length;
            }
            if (consumed < text.Length)
                target.Add(new Run(text.Substring(consumed)));
        }

        private static Inline CreateEmojiInline(string sequence, double fontSize)
        {
            var inline = new Emoji.Wpf.EmojiInline
            {
                Text = sequence,
                FontSize = fontSize,
                // Emoji are self-coloured. A non-black inherited Foreground (our themed
                // Chat.Foreground) would trigger EmojiInline's tint pixel-shader and wash the
                // glyph in that colour — pinning black suppresses the tint (Emoji.Wpf's own
                // substitution helper does the same).
                Foreground = Brushes.Black,
            };
            // Selection-copy fidelity: ChatClipboard.GetText appends this instead of dropping
            // the InlineUIContainer like WPF's stock TextRange.Text does.
            ChatClipboard.SetCopyText(inline, sequence);
            return inline;
        }

        /// <summary>
        /// Attached property for TextBlocks (tool-row titles, notices, crew/plan rows): renders the
        /// string with colour emoji in place of <c>Text</c>. Elements using this copy via their
        /// view-model's CopyText command (not selection), so no clipboard wiring is needed here.
        /// </summary>
        public static readonly DependencyProperty PlainTextProperty = DependencyProperty.RegisterAttached(
            "PlainText", typeof(string), typeof(EmojiText),
            new PropertyMetadata(null, OnPlainTextChanged));

        public static string? GetPlainText(DependencyObject element) => (string?)element.GetValue(PlainTextProperty);

        public static void SetPlainText(DependencyObject element, string? value) => element.SetValue(PlainTextProperty, value);

        private static void OnPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock textBlock)
                return;
            textBlock.Inlines.Clear();
            if (e.NewValue is string { Length: > 0 } text)
                AppendTo(textBlock.Inlines, text, textBlock.FontSize);
        }
    }
}
