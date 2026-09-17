using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Resolving themed values once per render instead of once per element (issue #86).
    /// </summary>
    /// <remarks>
    /// A 17,730-char message spent <b>1106ms of a 1132ms build on the document assignment alone</b> —
    /// ~621 <c>SetResourceReference</c> registrations resolving when the document met the tree, at ~1.8ms
    /// each inside devenv. These pin the two halves that can silently regress: that a palette actually
    /// reaches the elements (otherwise the expensive path is quietly still in use and nothing looks
    /// wrong), and that a source which cannot answer yields NO palette rather than a palette of nulls
    /// (which would bake a detached viewer's non-answers in as permanent values — the "last message goes
    /// dense" bug made incurable).
    /// </remarks>
    public sealed class MarkdownPaletteTests
    {
        const string Markdown = "Some prose with `inline code` in it.";

        static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono");
        static readonly Brush CodeFill = Brushes.DarkSlateGray;

        static FrameworkElement Themed()
        {
            var element = new Border();
            element.Resources["Chat.Foreground"] = Brushes.White;
            element.Resources["Chat.SubtleForeground"] = Brushes.Gray;
            element.Resources["Chat.Border"] = Brushes.DimGray;
            element.Resources["Chat.LinkForeground"] = Brushes.SkyBlue;
            element.Resources["Chat.CodeBackground"] = CodeFill;
            element.Resources["Chat.CodeFontFamily"] = CodeFont;
            return element;
        }

        [Fact]
        public void An_element_that_cannot_answer_yields_no_palette() => RunSta(() =>
        {
            // A viewer rendering while OUT of the tree, which container recycling makes routine. The
            // renderer must fall back to resource references there, so that the corrective render on
            // re-attach can still fix the document. A palette full of nulls would defeat that.
            Assert.Null(MarkdownPalette.Resolve(new Border()));
            Assert.Null(MarkdownPalette.Resolve(null));
        });

        [Fact]
        public void A_themed_element_answers_with_its_own_values() => RunSta(() =>
        {
            var palette = MarkdownPalette.Resolve(Themed());

            Assert.NotNull(palette);
            Assert.Same(CodeFont, palette!.CodeFontFamily);
            Assert.Same(CodeFill, palette.CodeBackground);
            Assert.Same(Brushes.SkyBlue, palette.LinkForeground);
            Assert.Same(Brushes.White, palette.Foreground);
        });

        [Fact]
        public void A_palette_styles_inline_code_BEFORE_the_document_is_attached() => RunSta(() =>
        {
            // The whole point, and the only way to see it without a tree: with a palette the values are
            // already ON the run while the document is still parentless. A resource reference resolves
            // nothing until it meets a tree, which is exactly the cost being removed — so this assertion
            // fails if the renderer quietly keeps the reference path.
            var palette = MarkdownPalette.Resolve(Themed());

            var document = MarkdownFlowRenderer.ToFlowDocument(Markdown, palette: palette);

            var code = InlineCodeRun(document);
            Assert.Same(CodeFont, code.FontFamily);
            Assert.Same(CodeFill, code.Background);
        });

        [Fact]
        public void Without_a_palette_a_parentless_document_still_resolves_nothing() => RunSta(() =>
        {
            // The control, and the reason the assertion above means something: unchanged behaviour is a
            // run carrying no value at all until the document is attached.
            var document = MarkdownFlowRenderer.ToFlowDocument(Markdown);

            var code = InlineCodeRun(document);
            Assert.NotSame(CodeFont, code.FontFamily);
            Assert.NotSame(CodeFill, code.Background);
        });

        /// <summary>The run carrying the `inline code` span — the element there are hundreds of.</summary>
        static Run InlineCodeRun(FlowDocument document)
        {
            var run = document.Blocks.OfType<Paragraph>()
                .SelectMany(p => p.Inlines)
                .OfType<Run>()
                .FirstOrDefault(r => r.Text == "inline code");

            Assert.NotNull(run);
            return run!;
        }

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
