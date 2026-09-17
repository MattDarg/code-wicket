using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A theme refresh re-renders realised markdown only when the themed values actually changed
    /// (issue #86).
    /// </summary>
    /// <remarks>
    /// The palette fix moved documents from resource references to resolved values, which means a host
    /// has to re-render them when the theme changes. The field log then showed
    /// <c>causes=input:2,<b>theme:2</b></c> on every viewer realised during startup: the VSIX hooks the
    /// classification format map as well as <c>ThemeChanged</c>, and the editor's own late reaction
    /// fires it, so the whole transcript was re-rendered against values identical to the ones it had
    /// just been built with. The gate is by VALUE because <c>VsTheme.SetBrushes</c> allocates a fresh
    /// brush per key on every refresh — reference equality would report "changed" every time and close
    /// nothing.
    /// </remarks>
    public sealed class MarkdownThemeRefreshTests
    {
        [Fact]
        public void Re_resolved_but_unchanged_values_match() => RunSta(() =>
        {
            // The startup case, exactly as the VSIX produces it: same colours, brand new Brush objects.
            var first = MarkdownPalette.Resolve(Themed(0x20));
            var second = MarkdownPalette.Resolve(Themed(0x20));

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.False(ReferenceEquals(first!.CodeBackground, second!.CodeBackground));
            Assert.True(first.Matches(second));
        });

        [Fact]
        public void A_real_theme_change_does_not_match() => RunSta(() =>
        {
            var dark = MarkdownPalette.Resolve(Themed(0x20));
            var light = MarkdownPalette.Resolve(Themed(0xF0));

            Assert.False(dark!.Matches(light));
        });

        [Fact]
        public void A_palette_never_matches_nothing() => RunSta(() =>
        {
            // Null is what an unanswerable root resolves to, and it must NOT suppress the re-render:
            // a wrong "same" leaves the transcript in the old theme, a visible bug, while a wrong
            // "different" costs one re-render nobody sees.
            Assert.False(MarkdownPalette.Resolve(Themed(0x20))!.Matches(null));
        });

        [Fact]
        public void An_unchanged_theme_does_not_rebuild_the_document() => RunSta(() =>
        {
            // The behaviour the trace measured, end to end: refresh twice with nothing changing and the
            // second must leave the built document alone. Identity of the FlowDocument is the observable
            // — a re-render assigns a new one.
            var root = Themed(0x20);
            var viewer = new FlowDocumentScrollViewer();
            root.Child = viewer;
            MarkdownText.SetText(viewer, "Some prose with `inline code` in it.");

            MarkdownText.RefreshThemedValues(root);
            var afterFirst = viewer.Document;
            MarkdownText.RefreshThemedValues(root);

            Assert.Same(afterFirst, viewer.Document);
        });

        [Fact]
        public void A_changed_theme_still_rebuilds_the_document() => RunSta(() =>
        {
            // The control, and the reason the assertion above means something: without it the gate could
            // be "never refresh" and both would pass.
            var root = Themed(0x20);
            var viewer = new FlowDocumentScrollViewer();
            root.Child = viewer;
            MarkdownText.SetText(viewer, "Some prose with `inline code` in it.");

            MarkdownText.RefreshThemedValues(root);
            var afterFirst = viewer.Document;
            root.Resources["Chat.CodeBackground"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            MarkdownText.RefreshThemedValues(root);

            Assert.NotSame(afterFirst, viewer.Document);
        });

        /// <summary>A themed root whose brushes are freshly allocated, as the VSIX rebuilds them.</summary>
        static Border Themed(byte level)
        {
            var element = new Border();
            element.Resources["Chat.Foreground"] = new SolidColorBrush(Color.FromRgb(level, level, level));
            element.Resources["Chat.SubtleForeground"] = new SolidColorBrush(Color.FromRgb(level, level, level));
            element.Resources["Chat.Border"] = new SolidColorBrush(Color.FromRgb(level, level, level));
            element.Resources["Chat.LinkForeground"] = new SolidColorBrush(Color.FromRgb(level, level, level));
            element.Resources["Chat.CodeBackground"] = new SolidColorBrush(Color.FromRgb(level, level, level));
            element.Resources["Chat.CodeFontFamily"] = new FontFamily("Cascadia Mono");
            return element;
        }

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
