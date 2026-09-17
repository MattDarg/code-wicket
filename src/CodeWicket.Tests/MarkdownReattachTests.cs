using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A rendered assistant message must not be left holding the values a DETACHED viewer resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transcript's panel recycles containers, so a viewer is routinely pulled out of the tree,
    /// re-pointed at a different message and re-rendered before it goes back in. Everything the built
    /// document resolves from the tree is answered by defaults at that moment — inline code spans get the
    /// prose font instead of <c>Chat.CodeFontFamily</c>, which is narrower and shorter-leaded, so the
    /// message silently re-wraps and packs tighter than every other message in the pane.
    /// </para>
    /// <para>
    /// <b>Asserting the document was REBUILT, not that its fonts came good, is deliberate.</b> The obvious test — check the code span's font after re-attach — passes with
    /// the fix removed, because a <c>SetResourceReference</c> does recover by itself once the element is
    /// back in a tree. What does not recover is the text FORMATTING: the document was line-broken while
    /// those references were unresolved, and a reference re-resolving to a value invalidates nothing that
    /// would re-break it, so the message keeps the wrapping and leading it was formatted with. A rebuild
    /// is therefore the actual contract, and the only thing a test can hold <see cref="MarkdownText"/>
    /// to without reaching into WPF's formatting internals.
    /// </para>
    /// <para>
    /// WPF text elements demand STA (xunit runs MTA), and the resolution being tested needs a real
    /// PresentationSource — hence a shown window, positioned off-screen and undecorated so it can't
    /// flash into a developer's session.
    /// </para>
    /// </remarks>
    public class MarkdownReattachTests
    {
        private const string CodeFont = "Cascadia Mono, Consolas";

        [Fact]
        public void DetachedRenderIsCorrectedOnceTheViewerIsBackInTheTree() => RunSta(() =>
        {
            var host = new Window
            {
                Width = 400,
                Height = 300,
                Left = -10000,
                Top = -10000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            // Where the chat root keeps it in the real app, which is precisely why a detached element
            // cannot reach it: a lookup that fell through to Application.Resources would still succeed.
            host.Resources.Add("Chat.CodeFontFamily", new FontFamily(CodeFont));

            var viewer = new FlowDocumentScrollViewer();

            // Rendered while OUTSIDE any tree — the recycled-container case. The code span coming out in
            // the wrong font is what establishes that this render is formatted against the wrong metrics;
            // the assertion that matters is further down.
            MarkdownText.SetText(viewer, "prose with a `token` in it");
            var detachedDocument = viewer.Document;
            Assert.NotEqual(CodeFont, CodeSpanFont(viewer));

            host.Content = viewer;
            try
            {
                host.Show();
                DrainDispatcher();

                // Rebuilt, so it is line-broken against the real fonts rather than carrying the layout it
                // was formatted with while detached.
                Assert.NotSame(detachedDocument, viewer.Document);
                Assert.Equal(CodeFont, CodeSpanFont(viewer));
            }
            finally
            {
                host.Close();
            }
        });

        /// <summary>
        /// The font the rendered inline code span ended up with. Located by its text rather than by its
        /// styling, so it cannot accidentally find whatever it is asserting about.
        /// </summary>
        private static string? CodeSpanFont(FlowDocumentScrollViewer viewer) =>
            viewer.Document?.Blocks
                .OfType<Paragraph>()
                .SelectMany(p => p.Inlines)
                .OfType<Run>()
                .FirstOrDefault(r => r.Text == "token")
                ?.FontFamily?.Source;

        /// <summary>
        /// Lets queued work run down to Loaded — the priority the re-render is triggered from — so the
        /// assertion sees the settled document rather than racing it.
        /// </summary>
        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
