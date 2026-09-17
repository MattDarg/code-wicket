using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CodeWicket.Core;
using CodeWicket.Shell;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Opt-in trace of the layout inputs each assistant message is rendered against, for the report
    /// that the last message's text changes density (and re-wraps) while scrolling during a turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It answered that report, and the answer is <c>codeFont</c>:</b> a render that runs while the
    /// viewer is detached logs <c>connected=False</c> with <c>codeFont=&lt;null&gt;</c>, i.e.
    /// <c>Chat.CodeFontFamily</c> did not resolve, so every inline code span falls back to the prose
    /// font — narrower glyphs (the message re-wraps) without the mono font's taller leading (it packs
    /// tighter). <see cref="MarkdownText"/> now renders again on re-attach because of this trace. The two
    /// theories it RULED OUT are worth keeping, because both are the obvious first guess:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Not the document font.</b> <see cref="MarkdownFlowRenderer"/> pins <c>FontSize = 13</c> on
    /// every document, and <c>viewerFont</c>/<c>docFont</c> read the same in healthy and broken frames.
    /// <c>source</c> still earns its place — it is the <see cref="BaseValueSource"/> of an INHERITED
    /// lookup, so <c>Default</c> vs <c>Inherited</c> says whether the tree answered at all — but on a
    /// host whose environment font is the WPF default it cannot show a visible difference.
    /// </description></item>
    /// <item><description>
    /// <b>Not the width.</b> Detached renders log the same <c>width</c> as attached ones, so the document
    /// is not being measured against a different available width.
    /// </description></item>
    /// </list>
    /// <para>
    /// Gated on <c>CWKT_MARKDOWN_RENDER_LOG</c> (a real environment variable — launch env never reaches
    /// the experimental devenv, see AGENTS.md), because renders are throttled to roughly ten a second per
    /// streaming message and every one of them would write a line. Guard call sites with
    /// <see cref="Enabled"/> so the formatting cost is not paid when it is off. Writes through
    /// <see cref="DiagnosticLog"/> like every other log, so it self-rolls and the logs-directory sweep
    /// reclaims it.
    /// </para>
    /// <para>
    /// <b>Deliberately kept on its own switch, rather than joining <c>ExtensionConfig.LogRendering</c>.</b>
    /// This trace's per-render append is expensive enough to distort what it sits beside: enabled
    /// together with the <c>[md-cost]</c> aggregate, most of what that aggregate reports would be this
    /// one's file IO. A user asked to turn on render logging must not silently get the instrument that
    /// falsifies the measurement — so the deep trace stays a developer-only variable.
    /// </para>
    /// </remarks>
    internal static class MarkdownRenderDiagnostics
    {
        private const string EnableVariable = "CWKT_MARKDOWN_RENDER_LOG";

        private static bool? _enabled;

        /// <summary>Whether the trace is switched on. Read once — the environment cannot change under us.</summary>
        internal static bool Enabled =>
            _enabled ??= !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnableVariable));

        // Shares the pane's render diagnostics log — and its retention — with the [md-cost] aggregate.
        private static string LogFile => RenderDiagnosticsLog.LogFile;

        /// <summary>
        /// Records the layout inputs of one render. Best-effort and never throws: a diagnostic must not
        /// break the thing it is diagnosing, and this one sits on the streaming render path.
        /// </summary>
        internal static void Record(FlowDocumentScrollViewer viewer, FlowDocument document, string? text)
        {
            if (!Enabled)
                return;

            try
            {
                // Identifies which message a line belongs to. A container is RECYCLED, so this is the
                // identity of the host viewer rather than of the message — which is the point: the same
                // viewer serving different messages across a scroll is exactly the sequence to look at.
                var viewerId = viewer.GetHashCode().ToString("x8", CultureInfo.InvariantCulture);

                var fontSource = DependencyPropertyHelper
                    .GetValueSource(viewer, Control.FontFamilyProperty)
                    .BaseValueSource;

                // Connected to a rendering surface at all. A detached viewer is the state where the
                // inherited lookup above has no tree to answer from.
                var connected = PresentationSource.FromVisual(viewer) is not null;

                // Inline code spans take their typeface from a resource REFERENCE (MarkdownFlowRenderer
                // .StyleAsCode), and a detached element cannot resolve a resource any more than it can
                // resolve an inherited value — it falls back to the control default. A message dense with
                // `code` spans losing their code font re-wraps and re-leads exactly like a whole-document
                // family change, so the two have to be told apart rather than assumed to be one.
                var codeFont = viewer.TryFindResource("Chat.CodeFontFamily") as FontFamily;

                DiagnosticLog.AppendLine(LogFile, string.Format(
                    CultureInfo.InvariantCulture,
                    "[md-render] viewer={0} len={1} loaded={2} connected={3} width={4:F2} "
                        + "viewerFont={5} source={6} viewerSize={7:F2} docFont={8} codeFont={9}",
                    viewerId,
                    text?.Length ?? 0,
                    viewer.IsLoaded,
                    connected,
                    viewer.ActualWidth,
                    Describe(viewer.FontFamily),
                    fontSource,
                    viewer.FontSize,
                    Describe(document.FontFamily),
                    Describe(codeFont)));
            }
            catch { /* diagnostics are best-effort */ }
        }

        private static string Describe(FontFamily? family) => family?.Source ?? "<null>";
    }
}
