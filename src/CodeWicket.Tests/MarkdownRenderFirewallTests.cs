using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Documents;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins <see cref="MarkdownRenderFirewall"/>, which exists because a user's crash dump showed
    /// <c>FileNotFoundException at MarkdownFlowRenderer.Render</c> taking Visual Studio down three
    /// times in one day: another extension owns the unsigned <c>Markdig</c> name in that process, so
    /// our request for our own version was never satisfied, and the render runs on a dispatcher tick
    /// where an unhandled exception is a dead IDE rather than a broken message.
    ///
    /// The load-bearing assertion is <see cref="AFailingRendererNeverEscapesTheFirewall"/> — remove the
    /// <c>try</c> and it fails while everything else here still passes.
    ///
    /// <b>What these cannot pin</b> is the <c>NoInlining</c> attribute on the bridge method, which is
    /// the other half of the guard: a missing assembly surfaces while JIT-compiling the method that
    /// needs it, i.e. at that method's call site, so inlining the bridge would move the fault into
    /// <c>Render</c>'s own JIT and past the <c>try</c>. That is a property of the JIT, not of any
    /// observable behaviour, and no unit test reaches it — the comment on the attribute is the only
    /// guard, which is worth knowing before anyone "tidies" it away.
    /// </summary>
    public sealed class MarkdownRenderFirewallTests : IDisposable
    {
        private const string Markdown = "# Heading\n\nSome **bold** prose.";

        public MarkdownRenderFirewallTests()
        {
            MarkdownRenderFirewall.ResetReportedForTests();
            MarkdownRenderFirewall.Log = null;
            MarkdownRenderFirewall.RendererOverride = null;
        }

        public void Dispose()
        {
            MarkdownRenderFirewall.RendererOverride = null;
            MarkdownRenderFirewall.Log = null;
            MarkdownRenderFirewall.ResetReportedForTests();
        }

        /// <summary>The shape the real failure took: a resolution failure naming the assembly asked for.</summary>
        private static Exception MissingMarkdig() =>
            new FileNotFoundException(
                "Could not load file or assembly 'Markdig, Version=1.3.0.0, Culture=neutral, PublicKeyToken=null'",
                "Markdig, Version=1.3.0.0, Culture=neutral, PublicKeyToken=null");

        private static void FailWith(Exception ex) =>
            MarkdownRenderFirewall.RendererOverride = (_, _, _) => throw ex;

        // -- input the renderer must not choke on ------------------------------------------------

        /// <summary>
        /// A list numbered from zero renders as a list. CommonMark accepts `0.` as an ordered list, and
        /// WPF REFUSES StartIndex 0 ("'0' is not a valid value for property 'StartIndex'"), so the bound
        /// check let the renderer throw on ordinary agent output.
        /// </summary>
        /// <remarks>
        /// Asserted here rather than in a renderer test because the firewall is what makes the bug
        /// survivable and therefore invisible: nothing crashes, the exception is caught, and the ENTIRE
        /// message drops to plain text under "Markdown formatting is unavailable for this message" — a
        /// whole reply unformatted for one character of input. So the check is that the document still
        /// has a List in it, which is precisely what the fallback cannot produce.
        /// </remarks>
        [Fact]
        public void AListNumberedFromZeroStillRendersAsMarkdown()
        {
            RunSta(() =>
            {
                var document = MarkdownRenderFirewall.Render("0. zero\n1. one\n");

                Assert.Contains(document.Blocks, b => b is List);
            });
        }

        /// <summary>The control: an ordinary list, so the assertion above is not true of everything.</summary>
        [Fact]
        public void AnOrdinaryOrderedListStillRendersAsMarkdown()
        {
            RunSta(() =>
            {
                var document = MarkdownRenderFirewall.Render("1. one\n2. two\n");

                Assert.Contains(document.Blocks, b => b is List);
            });
        }

        // -- the reason it exists --------------------------------------------------------------

        [Fact]
        public void AFailingRendererNeverEscapesTheFirewall()
        {
            RunSta(() =>
            {
                FailWith(MissingMarkdig());

                // No assertion needed beyond "this returned": on the real path the caller is a
                // dispatcher tick, and an exception reaching it is the crash being fixed.
                var document = MarkdownRenderFirewall.Render(Markdown);

                Assert.NotNull(document);
            });
        }

        [Fact]
        public void TheMessageTextSurvivesAsPlainText()
        {
            RunSta(() =>
            {
                FailWith(MissingMarkdig());

                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                // Unformatted, but not lost - the content is the thing the user came for.
                Assert.Contains("Some **bold** prose.", text, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void TheNoticeNamesTheContestedAssembly()
        {
            RunSta(() =>
            {
                FailWith(MissingMarkdig());

                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.Contains("Markdig", text, StringComparison.Ordinal);
                Assert.Contains("another installed extension", text, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// A failure that is NOT a load failure must not claim one. Asserting a cause we don't have is
        /// how a user ends up uninstalling an innocent extension.
        /// </summary>
        [Fact]
        public void AFailureThatIsNotALoadConflictDoesNotClaimToBeOne()
        {
            RunSta(() =>
            {
                FailWith(new InvalidOperationException("something else broke"));

                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.DoesNotContain("another installed extension", text, StringComparison.Ordinal);
                Assert.Contains("plain text", text, StringComparison.Ordinal);
            });
        }

        // -- the diagnostic --------------------------------------------------------------------

        /// <summary>
        /// The report inventories what is actually loaded under the contested name. This is the whole
        /// point of logging it: from our side the conflict is invisible - we asked and were refused -
        /// and this names who holds the name and which folder they came from, which is what turns a
        /// crash dump into one grep.
        /// </summary>
        [Fact]
        public void TheReportSaysWhoHoldsTheContestedName()
        {
            RunSta(() =>
            {
                var lines = new List<string>();
                MarkdownRenderFirewall.Log = lines.Add;

                // An assembly certainly loaded in this process - the one under test.
                var self = typeof(MarkdownRenderFirewall).Assembly.GetName();
                FailWith(new FileNotFoundException("nope", self.Name + ", Version=99.0.0.0"));

                MarkdownRenderFirewall.Render(Markdown);

                var report = Assert.Single(lines);
                Assert.Contains("[markdown] rendering failed", report, StringComparison.Ordinal);
                Assert.Contains("loaded '" + self.Name + "'", report, StringComparison.Ordinal);
                Assert.Contains(self.Version!.ToString(), report, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// The load failure is not always the outermost exception, and the case where it isn't is
        /// likely rather than exotic: <c>MarkdownFlowRenderer</c> holds a static Markdig-typed
        /// pipeline, so a failure running its type initializer arrives as a
        /// <see cref="TypeInitializationException"/> wrapping the real cause. Reading only the top
        /// level loses the assembly name — and with it the loaded-copy inventory, which is the whole
        /// reason the report exists — and silently downgrades the user's notice to the generic
        /// wording, which reads as "we don't know" about the one thing we do know.
        /// </summary>
        [Fact]
        public void ALoadFailureIsFoundEvenWhenItIsWrapped()
        {
            RunSta(() =>
            {
                var lines = new List<string>();
                MarkdownRenderFirewall.Log = lines.Add;

                var self = typeof(MarkdownRenderFirewall).Assembly.GetName();
                var inner = new FileNotFoundException("nope", self.Name + ", Version=99.0.0.0");
                FailWith(new TypeInitializationException("CodeWicket.UI.Markdown.MarkdownFlowRenderer", inner));

                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.Contains(self.Name!, text, StringComparison.Ordinal);
                Assert.Contains("another installed extension", text, StringComparison.Ordinal);
                Assert.Contains("loaded '" + self.Name + "'", Assert.Single(lines), StringComparison.Ordinal);
            });
        }

        [Fact]
        public void TheFailureIsReportedOncePerProcess()
        {
            RunSta(() =>
            {
                var lines = new List<string>();
                MarkdownRenderFirewall.Log = lines.Add;
                FailWith(MissingMarkdig());

                MarkdownRenderFirewall.Render(Markdown);
                MarkdownRenderFirewall.Render(Markdown);
                MarkdownRenderFirewall.Render(Markdown);

                // The condition is a property of what is installed, so it is the same answer every
                // time; repeating it would bury the log under one line per rendered message.
                Assert.Single(lines);
            });
        }

        /// <summary>But every message still SAYS so, because every message really is unformatted.</summary>
        [Fact]
        public void EveryFallbackDocumentCarriesTheNoticeEvenThoughTheReportIsOnce()
        {
            RunSta(() =>
            {
                MarkdownRenderFirewall.Log = _ => { };
                FailWith(MissingMarkdig());

                MarkdownRenderFirewall.Render(Markdown);
                var second = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.Contains("Markdown formatting is unavailable", second, StringComparison.Ordinal);
            });
        }

        // -- best-effort -----------------------------------------------------------------------

        [Fact]
        public void ASinkThatThrowsDoesNotTakeTheFallbackDown()
        {
            RunSta(() =>
            {
                MarkdownRenderFirewall.Log = _ => throw new InvalidOperationException("log is gone");
                FailWith(MissingMarkdig());

                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.Contains("Some **bold** prose.", text, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void WithNoSinkTheFallbackStillRenders()
        {
            RunSta(() =>
            {
                FailWith(MissingMarkdig());

                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.Contains("Some **bold** prose.", text, StringComparison.Ordinal);
            });
        }

        // -- the ordinary path -----------------------------------------------------------------

        /// <summary>
        /// With a working renderer the firewall is transparent: real markdown, formatted, and no
        /// notice. Guards against the guard becoming the behaviour.
        /// </summary>
        [Fact]
        public void AWorkingRendererPassesStraightThrough()
        {
            RunSta(() =>
            {
                var text = PlainText(MarkdownRenderFirewall.Render(Markdown));

                Assert.DoesNotContain("Markdown formatting is unavailable", text, StringComparison.Ordinal);
                // Formatted: the bold markers are consumed by the renderer rather than shown.
                Assert.DoesNotContain("**bold**", text, StringComparison.Ordinal);
                Assert.Contains("bold", text, StringComparison.Ordinal);
            });
        }

        // ---------------------------------------------------------------------------------------

        private static string PlainText(FlowDocument document)
            => new TextRange(document.ContentStart, document.ContentEnd).Text;

        // WPF text elements demand an STA thread; xunit runs MTA.
        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
