using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A file reference that resolves to nothing must not trigger a rebuild (issue #86).
    /// </summary>
    /// <remarks>
    /// The resolver calls back whether or not a reference resolved — correctly, since a caller waiting
    /// to re-render must never be stranded. But a reference known not to resolve renders as plain text
    /// on the rebuild exactly as it already does, so that rebuild reproduces the document it replaced:
    /// a full parse, a full attach, and on a large message its own second-long render pass, for no
    /// visible change. Whether the answer matters is the caller's question, so the test lives with the
    /// caller.
    /// </remarks>
    public sealed class MarkdownResolveRenderTests
    {
        [Fact]
        public void A_reference_that_resolves_to_nothing_does_not_rebuild() => RunSta(() =>
        {
            using var workspace = new TempWorkspace();
            var viewer = Render(workspace, "See NotARealFile.cs:12 for details.");
            var first = viewer.Document;

            // Let the off-thread resolution land and any rebuild it asks for run.
            Pump(TimeSpan.FromMilliseconds(600));

            Assert.Same(first, viewer.Document);
        });

        [Fact]
        public void A_reference_that_resolves_DOES_rebuild() => RunSta(() =>
        {
            // The control, and the reason the assertion above means something: without it the skip could
            // be "never rebuild" and the first test would still pass — while file references silently
            // stopped becoming links at all.
            using var workspace = new TempWorkspace();
            workspace.Write("Real.cs", "// content");
            var viewer = Render(workspace, "See Real.cs:12 for details.");
            var first = viewer.Document;

            // Waits for the REBUILD, not for a duration. The resolution runs off-thread and its cost is
            // the machine's - a directory walk against whatever the filesystem and the scanner are doing
            // at that moment - so a fixed budget is a bet on how busy the box is. At 600ms this failed
            // about four full runs in ten when the suite ran its collections in parallel, always here,
            // always passing on a re-run: a green test that reports the machine's load rather than the
            // product's behaviour. The ceiling is only a deadlock backstop; the common case returns in
            // the first few polls.
            PumpUntil(() => !ReferenceEquals(first, viewer.Document), TimeSpan.FromSeconds(15));

            Assert.NotSame(first, viewer.Document);
            Assert.Contains(
                viewer.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines),
                inline => inline is Hyperlink);
        });

        static FlowDocumentScrollViewer Render(TempWorkspace workspace, string markdown)
        {
            var viewer = new FlowDocumentScrollViewer();
            // Links FIRST, as the transcript sets them: a render with no link context resolves nothing
            // at all, so setting text first would render plain and then rebuild — the very cost under
            // test here, arriving from the other direction.
            MarkdownText.SetLinks(viewer, new FileLinkContext(
                new FileReferenceResolver(workspace.Root), (_, _) => System.Threading.Tasks.Task.CompletedTask));
            MarkdownText.SetText(viewer, markdown);
            return viewer;
        }

        /// <summary>Runs the dispatcher for a while, so posted resolutions and throttled rebuilds land.</summary>
        /// <summary>
        /// Pumps until <paramref name="condition"/> holds, or the ceiling elapses. Returning early on
        /// success is the point: a test that waits for a fixed period pays that period on every run and
        /// still fails on a slow one.
        /// <para>Only usable for asserting something DOES happen. The sibling test asserts a rebuild
        /// does NOT happen, and no amount of polling can establish that - it has to spend a fixed
        /// period and then look, which is why <see cref="Pump"/> stays.</para>
        /// </summary>
        static void PumpUntil(Func<bool> condition, TimeSpan ceiling)
        {
            var until = DateTime.UtcNow + ceiling;
            while (DateTime.UtcNow < until)
            {
                if (condition())
                    return;

                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(15);
            }
        }

        static void Pump(TimeSpan duration)
        {
            var until = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < until)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(15);
            }
        }

        sealed class TempWorkspace : IDisposable
        {
            internal TempWorkspace()
            {
                Root = Path.Combine(Path.GetTempPath(), "cwkt-resolve-" + Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(Root);
            }

            internal string Root { get; }

            internal void Write(string name, string content) =>
                File.WriteAllText(Path.Combine(Root, name), content);

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
            }
        }

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
