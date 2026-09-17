using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using CodeWicket.UI.Markdown;

namespace CodeWicket.UI.Controls
{
    /// <summary>
    /// Selectable plain text with colour emoji — the user/"thinking" message body. Replaces the old
    /// read-only TextBox (whose classic text stack renders emoji monochrome) while keeping its two
    /// contracts: native mouse selection + Copy (Ctrl+C and the context-menu item routed via
    /// ApplicationCommands.Copy), and shrink-wrapping to short content.
    ///
    /// Structure: a read-only borderless RichTextBox shows the text (rich content = colour emoji
    /// inlines), and a hidden twin TextBlock with the same inlines acts as the measurer — a
    /// RichTextBox never shrink-wraps its content (it fills the given width), so without the twin
    /// every short message would stretch its bubble to MaxWidth. The twin wraps naturally; the
    /// RichTextBox's width follows it.
    ///
    /// Text is literal (no markdown), matching the TextBox it replaces.
    /// </summary>
    public class SelectableEmojiText : Grid
    {
        // Re-own the inheritable text properties so XAML styles can set Foreground/FontStyle/... on
        // this control (a bare Grid has none) and the values flow down into the RichTextBox and the
        // measurer via property inheritance. Emoji inlines pin their own Foreground and ignore this.
        public static readonly DependencyProperty ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner(typeof(SelectableEmojiText));
        public static readonly DependencyProperty FontStyleProperty =
            TextElement.FontStyleProperty.AddOwner(typeof(SelectableEmojiText));
        // Emoji inlines bake FontSize at build time, so a size change after the text arrived
        // (XAML setter vs binding order isn't guaranteed) must rebuild the inlines.
        public static readonly DependencyProperty FontSizeProperty =
            TextElement.FontSizeProperty.AddOwner(typeof(SelectableEmojiText),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
                    FrameworkPropertyMetadataOptions.Inherits,
                    (d, _) => ((SelectableEmojiText)d).Rebuild()));
        public static readonly DependencyProperty FontFamilyProperty =
            TextElement.FontFamilyProperty.AddOwner(typeof(SelectableEmojiText));

        public System.Windows.Media.Brush Foreground
        {
            get => (System.Windows.Media.Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public FontStyle FontStyle
        {
            get => (FontStyle)GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public System.Windows.Media.FontFamily FontFamily
        {
            get => (System.Windows.Media.FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(SelectableEmojiText),
            new PropertyMetadata(null, OnTextChanged));

        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        private readonly TextBlock _measurer;
        private readonly RichTextBox _box;

        /// <summary>True when the user has a non-empty selection — lets the context-menu "Copy"
        /// decide between copying the selection and copying the whole message.</summary>
        public bool HasSelection => !string.IsNullOrEmpty(_box.Selection?.Text);

        public SelectableEmojiText()
        {
            _measurer = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Hidden, // participates in layout, never shown
                IsHitTestVisible = false,
            };

            _box = new RichTextBox
            {
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            };
            _box.Document.PagePadding = new Thickness(0);

            // Text inside a RichTextBox does NOT reliably inherit Foreground/FontStyle/... across the
            // control boundary: RichTextBox's own theme style sets them (Foreground → a system colour),
            // and a theme-style setter outranks property inheritance — so the Runs ignore this control's
            // Style-driven Chat.Foreground and render near-background in the VS dark theme (they're still
            // there + selectable, just invisible). Bind the box's text properties to ours so they land as
            // LOCAL values (which beat the theme style) and the document content inherits them — the same
            // reason the assistant renderer sets its FlowDocument foreground explicitly. Covers the
            // "thinking" DataTrigger too (Chat.SubtleForeground + italic).
            BindText(Control.ForegroundProperty, nameof(Foreground));
            BindText(Control.FontStyleProperty, nameof(FontStyle));
            BindText(Control.FontFamilyProperty, nameof(FontFamily));

            Children.Add(_measurer);
            Children.Add(_box);

            // Enables the context-menu "Copy" (CommandTarget = this control): the binding's only
            // job is CanExecute so the item isn't greyed out — the actual copy happens in the
            // ChatClipboard preview interceptor below, which sees the command first (tunneling)
            // for both the menu item and Ctrl+C pressed inside the RichTextBox.
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (_, _) => { }, (_, e) => e.CanExecute = true));
            ChatClipboard.InterceptCopy(this, () => _box.Selection);
        }

        /// <summary>
        /// Extra width the RichTextBox needs over the measured text width to render it unwrapped: its
        /// editing surface reserves a column past the last glyph that the TextBlock's measurement
        /// doesn't account for. Sized to exactly the text width it wraps the final word off — which is
        /// what a short user message breaking mid-phrase in the transcript looks like.
        ///
        /// The figure is measured, not guessed: probing the smallest slack that keeps the box one line
        /// tall gives a flat 10px across text from 90px to 700px wide, i.e. a fixed reservation rather
        /// than anything that accumulates per glyph. Neither obvious way of asking for it at runtime
        /// works — the box's own DesiredSize under an unconstrained measure under-reports it by the
        /// same amount — so it's a constant here, with 2px of headroom.
        /// </summary>
        private const double CaretSlack = 12;

        // Shrink-wrapping. Only the measurer can say how wide the text wants to be — a RichTextBox fills
        // whatever width it's given, so its own size says nothing. The number to read is the measurer's
        // DESIRED size, taken here against the incoming constraint; reading its ActualWidth from a
        // SizeChanged handler (as this used to) reads the width it was ARRANGED at instead, which is the
        // container's — feeding the bubble's own width straight back into the box and pinning every
        // message, however short, at the bubble's MaxWidth.
        protected override Size MeasureOverride(Size constraint)
        {
            _measurer.Measure(constraint);
            var text = _measurer.DesiredSize;

            // The slack counts toward the width rather than overhanging it: it isn't dead space, it's
            // width the glyphs actually need — excluded, the bubble shrinks under its own last word
            // and clips it against the padding.
            var width = Math.Min(constraint.Width, text.Width + CaretSlack);
            _box.Width = width;
            _box.Measure(new Size(width, constraint.Height));

            return new Size(width, Math.Max(text.Height, _box.DesiredSize.Height));
        }

        // Binds a text property on the inner RichTextBox (and the measurer, so the shrink-wrap width
        // still matches the shown font) to this control's same property.
        private void BindText(DependencyProperty property, string sourcePath)
        {
            var binding = new System.Windows.Data.Binding(sourcePath) { Source = this };
            _box.SetBinding(property, binding);
            _measurer.SetBinding(property, binding);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((SelectableEmojiText)d).Rebuild();

        private void Rebuild()
        {
            var text = Text ?? string.Empty;

            _measurer.Inlines.Clear();
            BuildInlines(_measurer.Inlines, text, FontSize);

            var paragraph = new Paragraph { Margin = new Thickness(0) };
            BuildInlines(paragraph.Inlines, text, FontSize);
            _box.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
        }

        private static void BuildInlines(InlineCollection target, string text, double fontSize)
        {
            // Literal text: line endings become explicit LineBreaks (a Run's '\n' renders as a
            // space inside a FlowDocument paragraph), everything else rides the emoji seam.
            //
            // A lone CR breaks too. In terminal output it means "overwrite this line", so the
            // input seam (TerminalText, applied when the user pastes) has usually resolved it
            // already — but text reaching this control hasn't all been through there: a session
            // restored from disk replays user messages saved before that existed, and the agent's
            // "thinking" text renders here as well. Breaking is the honest fallback: it shows
            // everything, where leaving the CR inside the Run silently runs the lines together.
            // Applying the overwrite here instead would hide content, which a renderer must not do.
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\n' && c != '\r')
                    continue;
                EmojiText.AppendTo(target, text.Substring(start, i - start), fontSize);
                target.Add(new LineBreak());
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++; // CRLF is one break, not two
                start = i + 1;
            }
            if (start < text.Length)
                EmojiText.AppendTo(target, text.Substring(start), fontSize);
        }
    }
}
