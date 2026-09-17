using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CodeWicket.UI.Controls
{
    /// <summary>
    /// Attached properties that fill a <see cref="TextBlock"/>'s inlines from <c>Source</c> text with every
    /// occurrence of <c>Term</c> highlighted (bold, in <c>Chat.ErrorForeground</c>). Used by the permission
    /// banner to call out the sensitive fragment inside the command text. A <see cref="TextBlock"/> is
    /// needed rather than a <see cref="TextBox"/> because only inline <see cref="Run"/>s can carry a
    /// per-substring brush. Matching is case-insensitive; an empty <c>Term</c> renders the source plain.
    /// </summary>
    public static class TextHighlighter
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(TextHighlighter), new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty TermProperty = DependencyProperty.RegisterAttached(
            "Term", typeof(string), typeof(TextHighlighter), new PropertyMetadata(null, OnChanged));

        public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
        public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);
        public static string? GetTerm(DependencyObject d) => (string?)d.GetValue(TermProperty);
        public static void SetTerm(DependencyObject d, string? value) => d.SetValue(TermProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock block)
                return;

            block.Inlines.Clear();
            var source = GetSource(block) ?? string.Empty;
            var term = GetTerm(block);

            if (string.IsNullOrEmpty(term))
            {
                block.Inlines.Add(new Run(source));
                return;
            }

            var i = 0;
            while (i < source.Length)
            {
                var idx = source.IndexOf(term, i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    block.Inlines.Add(new Run(source.Substring(i)));
                    break;
                }
                if (idx > i)
                    block.Inlines.Add(new Run(source.Substring(i, idx - i)));

                var hit = new Run(source.Substring(idx, term!.Length)) { FontWeight = FontWeights.Bold };
                // Brush via resource reference so the VSIX theme override flows through.
                hit.SetResourceReference(TextElement.ForegroundProperty, "Chat.ErrorForeground");
                block.Inlines.Add(hit);

                i = idx + term.Length;
            }
        }
    }
}
