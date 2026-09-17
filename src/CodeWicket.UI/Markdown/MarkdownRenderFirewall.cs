using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Stands between the chat transcript and the markdown renderer so a failure in the renderer
    /// cannot take the host process down.
    ///
    /// <b>This is not a precaution.</b> A user's crash dump showed
    /// <c>System.IO.FileNotFoundException at MarkdownFlowRenderer.Render(System.String)</c>, three
    /// times in one day, later reproduced on demand and traced by fusion log: another installed
    /// extension registers a pkgdef <c>codeBase</c> for the unsigned simple name
    /// <c>Markdig</c>, which captures that name for the whole devenv process. Our request is routed to
    /// their file — the only URL tried, our own folder never probed — and rejected, because for an
    /// unsigned assembly a <c>codeBase</c> outside the application base is illegal. The bind fails with
    /// nothing to fall back to. It surfaces here rather than at startup because it is thrown while
    /// JIT-compiling <see cref="RenderMarkdown"/>, and the render runs synchronously from a dependency
    /// property change during XAML template instantiation — on the dispatcher, where an unhandled
    /// exception is not a broken message but a dead IDE. Removing the capturable name is the actual
    /// fix — the parser is now the strong-named <c>Markdig.Signed</c> package — but that cure is not
    /// available to the <c>Emoji.Wpf</c> island this assembly also depends on: no signed variant
    /// exists and it cannot be merged either (pack URIs naming its own version, baked into BAML). So
    /// this is not a belt-and-braces wrapper, it is the ONLY protection those four have, and it is
    /// permanent. It exists because <b>the render path must not be able to kill Visual Studio for any
    /// reason</b>, including the next conflict we haven't thought of — and because it names the captor
    /// in the log, which is what turns a crash into a diagnosis.
    ///
    /// <b>Why the indirection through <see cref="RenderMarkdown"/>, and why it must not inline.</b> A
    /// missing assembly surfaces while JIT-compiling the method that first needs it, which means it is
    /// thrown at that method's <i>call site</i> rather than inside it — the same trap
    /// <c>ChatToolWindow.InitializeAsync</c> exists to handle. If the renderer call sat directly in
    /// <see cref="Render"/>, resolving Markdig would become part of <see cref="Render"/>'s own JIT and
    /// the <c>try</c> below would never get to run. Keeping the call one <c>NoInlining</c> frame away
    /// leaves <see cref="Render"/> itself free of any type that can fail to load, so the fault always
    /// lands where it can be caught. (The observed dump faulted deeper still, inside the renderer's own
    /// body — but that is a fact about which member Markdig was needed for first, not a guarantee, and
    /// the guard has to hold either way.)
    ///
    /// The fallback is deliberately <b>plain text with a visible notice</b>, not silence. Markdown that
    /// quietly renders unformatted looks like our bug and gives the user nothing to act on; the notice
    /// names the cause, and it repeats per message because every message really is unformatted.
    /// </summary>
    public static class MarkdownRenderFirewall
    {
        /// <summary>
        /// Optional sink for the one-time failure report. Null (the default) writes nothing; the VSIX
        /// points it at <c>engine.log</c>. The report carries the exception <i>and</i> an inventory of
        /// what is actually loaded under the contested name — which is the line that turns "VS crashed"
        /// into a one-line answer instead of a crash dump.
        /// </summary>
        public static Action<string>? Log;

        private static int _reported;

        /// <summary>
        /// Test seam: stands in for the renderer so a test can make it fail the way a missing assembly
        /// does, without needing to break the real one. Null = the real
        /// <see cref="MarkdownFlowRenderer"/>. The production path keeps its direct call.
        /// </summary>
        internal static Func<string?, FileLinkContext?, ICollection<string>?, FlowDocument>? RendererOverride;

        /// <summary>Test seam: the report is once per process, so a second test needs it rearmed.</summary>
        internal static void ResetReportedForTests() => System.Threading.Volatile.Write(ref _reported, 0);

        /// <summary>
        /// Renders <paramref name="markdown"/>, falling back to plain text if the renderer cannot run.
        /// </summary>
        public static FlowDocument Render(
            string? markdown, FileLinkContext? links = null, ICollection<string>? pendingReferences = null,
            MarkdownPalette? palette = null)
        {
            try
            {
                var overridden = RendererOverride;
                // The override is a test seam and takes no palette: a double that stands in for the
                // renderer has no themed values to resolve, and giving it some would only let a test
                // disagree with the real path about what it is standing in for.
                return overridden is null
                    ? RenderMarkdown(markdown, links, pendingReferences, palette)
                    : overridden(markdown, links, pendingReferences);
            }
            catch (Exception ex)
            {
                // Deliberately broad. Narrower catches would each be a bet that we can enumerate the
                // ways a renderer breaks, and the cost of losing that bet is the whole IDE — while the
                // cost of being too broad is one message rendered as plain text, with the reason on
                // screen and in the log.
                Report(ex);
                return Fallback(markdown, ex);
            }
        }

        // Must not inline: see the class remarks. The attribute is load-bearing, not a hint.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static FlowDocument RenderMarkdown(
            string? markdown, FileLinkContext? links, ICollection<string>? pendingReferences,
            MarkdownPalette? palette)
            => MarkdownFlowRenderer.ToFlowDocument(markdown, links, pendingReferences, palette);

        private static FlowDocument Fallback(string? markdown, Exception ex)
        {
            // Framework and WPF types only — this has to build when our own renderer's dependencies
            // are what failed.
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 13,
                TextAlignment = TextAlignment.Left,
            };
            doc.SetResourceReference(TextElement.ForegroundProperty, "Chat.Foreground");

            var notice = new Paragraph(new Run(NoticeFor(ex)))
            {
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 6),
            };
            notice.SetResourceReference(TextElement.ForegroundProperty, "Chat.WarningForeground");
            doc.Blocks.Add(notice);

            if (!string.IsNullOrEmpty(markdown))
                doc.Blocks.Add(new Paragraph(new Run(markdown)));

            return doc;
        }

        /// <summary>
        /// What the user is told. A load failure has a cause they can act on — and naming the assembly
        /// is what lets them find the other extension — so it gets its own wording; anything else says
        /// only what is true.
        /// </summary>
        private static string NoticeFor(Exception ex)
        {
            var assembly = ContestedAssemblyName(ex);
            return assembly is null
                ? "Markdown formatting is unavailable for this message; it is shown as plain text."
                : $"Markdown formatting is unavailable — another installed extension has loaded a " +
                  $"conflicting copy of {assembly}. This message is shown as plain text.";
        }

        /// <summary>
        /// Reports once per process. Repeating would add nothing: the condition is a property of what
        /// is installed, so it is the same answer for every message until the user changes something.
        /// </summary>
        private static void Report(Exception ex)
        {
            var log = Log;
            if (log is null)
                return;
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0)
                return;

            try
            {
                var report = new StringBuilder();
                report.Append("[markdown] rendering failed, falling back to plain text: ").Append(ex);

                var assembly = ContestedAssemblyName(ex);
                if (assembly is not null)
                    report.AppendLine().Append(DescribeLoaded(assembly));

                log(report.ToString());
            }
            catch { /* a diagnostic must never take the fallback down too */ }
        }

        /// <summary>
        /// The simple name of the assembly that could not be loaded, or null if this wasn't a load
        /// failure. <c>FileName</c> carries the full display name the runtime was asked for.
        ///
        /// <b>Walks the inner-exception chain</b>, because the load failure is not always the
        /// outermost exception and the case where it isn't is likely rather than exotic:
        /// <see cref="MarkdownFlowRenderer"/> holds a static Markdig-typed pipeline, so a failure
        /// while running its type initializer arrives as a <see cref="TypeInitializationException"/>
        /// with the real cause inside. Reading only the top level would then lose exactly the part
        /// worth reporting — the assembly name, and with it the loaded-copy inventory — and quietly
        /// downgrade the user's notice to the generic wording. The chain is short and this runs once.
        /// </summary>
        private static string? ContestedAssemblyName(Exception? ex)
        {
            for (var depth = 0; ex is not null && depth < 8; ex = ex.InnerException, depth++)
            {
                var fileName = ex switch
                {
                    FileNotFoundException notFound => notFound.FileName,
                    FileLoadException loadFailed => loadFailed.FileName,
                    _ => null,
                };
                if (string.IsNullOrEmpty(fileName))
                    continue;

                try { return new AssemblyName(fileName).Name; }
                catch { return fileName; }
            }
            return null;
        }

        /// <summary>
        /// Every assembly already loaded under the contested name, with version and location. This is
        /// the whole diagnostic: the conflict is invisible from our side — we asked for a version and
        /// were refused — and this line names who has it and where they came from, which points
        /// straight at the extension folder responsible.
        /// </summary>
        private static string DescribeLoaded(string simpleName)
        {
            try
            {
                var found = new List<string>();
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = loaded.GetName();
                    if (!string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var location = loaded.IsDynamic ? "(dynamic)" : SafeLocation(loaded);
                    found.Add($"{name.Version} from {location}");
                }

                return found.Count == 0
                    ? $"[markdown] no '{simpleName}' assembly is loaded in this process"
                    : $"[markdown] loaded '{simpleName}': " + string.Join("; ", found.ToArray());
            }
            catch (Exception ex)
            {
                return $"[markdown] could not inventory '{simpleName}': {ex.Message}";
            }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try { return assembly.Location; }
            catch { return "(location unavailable)"; }
        }
    }
}
