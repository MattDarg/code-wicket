using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Attached property that renders a markdown string into a host <see cref="FlowDocumentScrollViewer"/>.
    /// Set <c>md:MarkdownText.Text</c> to a (streaming) markdown string and the viewer's document is
    /// rebuilt from it via <see cref="MarkdownFlowRenderer"/>.
    /// <para>
    /// <b>Rebuilds are coalesced, because one per delta is quadratic.</b> A rebuild re-parses the whole
    /// accumulated markdown and re-formats the whole document, so its cost grows with the message while
    /// the number of deltas grows with it too. Measured through this seam: a 30KB reply arriving in ~1900
    /// deltas cost <b>77 seconds</b> of UI-thread work against <b>71ms</b> to render the finished text
    /// once — and a 4KB reply already cost 3.5s. Worse, the work runs at a priority ABOVE WPF's own
    /// render pass (<see cref="DispatcherPriority.Render"/>), so while deltas keep arriving nothing
    /// repaints: the transcript showed the first word, froze with the CPU pinned, then dumped the whole
    /// reply at once when the stream stopped.
    /// </para>
    /// <para>
    /// So only the <i>first</i> paint of a message is synchronous (it is what makes the reply appear
    /// instantly, and several callers read <c>Document</c> straight after a single set); every later
    /// change is throttled to <see cref="MinRenderInterval"/> by a timer that stops itself once the text
    /// settles. Streamed prose is being read by a human, so a rebuild per delta buys nothing a rebuild
    /// every tenth of a second doesn't — and "as often as the UI can manage" is not a throttle at all:
    /// with gaps between deltas the pane simply goes back to rebuilding per delta, which is how this got
    /// expensive in the first place (measured on one 30KB reply streamed over 30s: 845 rebuilds and
    /// roughly half the UI thread with only the ceiling, against 202 once the floor was added).
    /// </para>
    /// <para>
    /// The timer runs at <see cref="DispatcherPriority.Background"/>, below Render, so painting wins.
    /// That also means a dispatcher that never idles would never tick it, so
    /// <see cref="MaxRenderInterval"/> is a second, synchronous escape on the delta path itself: however
    /// busy the queue is, a visible message still repaints rather than waiting for the turn to end.
    /// </para>
    /// <para>
    /// <c>md:MarkdownText.Links</c> optionally supplies the file-reference context, which makes
    /// <c>Foo.cs:42</c> in the agent's prose clickable. It is set alongside <c>Text</c> rather than being
    /// a static hook because the workspace root differs per session.
    /// </para>
    /// </summary>
    public static class MarkdownText
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(MarkdownText),
            new PropertyMetadata(null, OnRenderInputChanged));

        public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

        public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

        /// <summary>
        /// Where file references in the rendered prose resolve and open. Null (no host opener, e.g. the
        /// stub hosts) renders every reference as plain text.
        /// </summary>
        public static readonly DependencyProperty LinksProperty = DependencyProperty.RegisterAttached(
            "Links", typeof(FileLinkContext), typeof(MarkdownText),
            new PropertyMetadata(null, OnRenderInputChanged));

        public static FileLinkContext? GetLinks(DependencyObject element) => (FileLinkContext?)element.GetValue(LinksProperty);

        public static void SetLinks(DependencyObject element, FileLinkContext? value) => element.SetValue(LinksProperty, value);

        /// <summary>
        /// Shortest gap between rebuilds of a message already on screen — 10 updates a second, which
        /// reads as continuous. This is the throttle that bounds the cost of a long reply.
        /// </summary>
        /// <remarks>
        /// Internal rather than private so the <c>[render-env]</c> header can state it: a duty cycle read
        /// off a machine says nothing unless the throttle it was measured against is on the same line.
        /// </remarks>
        internal static readonly TimeSpan MinRenderInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Longest a message already on screen may go without a rebuild while its text keeps changing.
        /// Only bites when the dispatcher never goes idle, since the throttle timer could not tick then.
        /// </summary>
        internal static readonly TimeSpan MaxRenderInterval = TimeSpan.FromMilliseconds(150);

        private static readonly long MaxRenderIntervalTicks =
            (long)(Stopwatch.Frequency * MaxRenderInterval.TotalSeconds);

        // Property-set order isn't guaranteed (a style setter can land Links after Text), so both
        // inputs share one handler and the document is simply rebuilt whenever either arrives.
        private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FlowDocumentScrollViewer viewer)
                ScheduleRender(viewer, RenderCause.Input);
        }

        /// <summary>Per-viewer rebuild bookkeeping; one object rather than several boxed attached values.</summary>
        private sealed class RenderState
        {
            /// <summary>The text changed since the last rebuild, so a rebuild is owed.</summary>
            public bool Dirty;

            /// <summary>Something other than an empty document has been rendered, so throttling is safe.</summary>
            public bool HasRenderedContent;

            public long LastRenderTimestamp;

            /// <summary>Runs only while rebuilds are owed; stops itself once the text settles.</summary>
            public DispatcherTimer? Throttle;

            /// <summary>What asked for the rebuild the throttle owes — carried so a deferred render is
            /// attributed to its cause rather than to the timer that happened to deliver it.</summary>
            public RenderCause PendingCause;

            /// <summary>
            /// Render-cost accumulation for the message this viewer is currently showing (issue #86).
            /// Created on the first measured render and then reused, because it is the VIEWER that
            /// persists across messages — see <see cref="MarkdownRenderCost"/> on episodes.
            /// </summary>
            public MarkdownRenderCost? Cost;

            /// <summary>
            /// Closes a cost episode once the stream goes quiet, since nothing else would: the last
            /// render of a message is indistinguishable from the next one not having arrived yet. Ticks
            /// at a fixed interval and stops itself when there is no open episode, rather than being
            /// restarted per render — 10 timer restarts a second is exactly the sort of cost this trace
            /// exists to avoid adding.
            /// </summary>
            public DispatcherTimer? CostFlush;

            /// <summary>
            /// A render happened while the viewer was out of the tree, so a corrective one is owed when
            /// it goes back in (see <see cref="EnsureRerenderOnReattach"/>). Guards against subscribing
            /// more than once — streaming can render the same detached viewer repeatedly.
            /// </summary>
            public bool AwaitingReattach;
        }

        private static readonly DependencyProperty RenderStateProperty = DependencyProperty.RegisterAttached(
            "RenderState", typeof(RenderState), typeof(MarkdownText), new PropertyMetadata(null));

        private static RenderState StateOf(FlowDocumentScrollViewer viewer)
        {
            if (viewer.GetValue(RenderStateProperty) is RenderState existing)
                return existing;
            var created = new RenderState();
            viewer.SetValue(RenderStateProperty, created);
            return created;
        }

        /// <summary>
        /// Rebuild now or shortly, whichever keeps the pane responsive. Synchronous until the viewer is
        /// actually showing something — the first delta of a reply must not wait, and a caller that sets
        /// the text once (a restored transcript, an expanded tool result, the offline self-checks) reads
        /// <c>Document</c> immediately afterwards.
        /// </summary>
        private static void ScheduleRender(FlowDocumentScrollViewer viewer, RenderCause cause)
        {
            var state = StateOf(viewer);
            if (!state.HasRenderedContent
                || Stopwatch.GetTimestamp() - state.LastRenderTimestamp >= MaxRenderIntervalTicks)
            {
                Render(viewer, state, cause);
                return;
            }

            QueueRender(viewer, state, cause);
        }

        /// <summary>
        /// Note that a rebuild is owed and make sure the throttle is running, without ever rebuilding
        /// inline. The resolver's completion callbacks come through here: a reference can resolve from
        /// cache while we are still inside the render that asked for it, and re-entering the render from
        /// inside itself is a trap.
        /// </summary>
        private static void QueueRender(FlowDocumentScrollViewer viewer, RenderState state, RenderCause cause)
        {
            state.Dirty = true;
            // A deferred render is still owed to whatever asked for it, and the trace exists to say
            // which. Where two causes queue behind one tick the LATER one is recorded: the render that
            // eventually runs serves both, and attributing it to the earlier would hide the resolve
            // callbacks entirely, since those always land last.
            state.PendingCause = cause;
            if (state.Throttle is not null)
                return;

            var timer = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher)
            {
                Interval = MinRenderInterval,
            };
            state.Throttle = timer;
            timer.Tick += (_, _) =>
            {
                if (!state.Dirty)
                {
                    // Settled: stop rather than tick forever behind every message in the transcript.
                    timer.Stop();
                    state.Throttle = null;
                    return;
                }
                Render(viewer, state, state.PendingCause);
            };
            timer.Start();
        }

        private static void Render(FlowDocumentScrollViewer viewer, RenderState state, RenderCause cause)
        {
            state.Dirty = false;

            // Render cost trace (issue #86). Off unless a host installed the sink, and when off this is
            // the only thing added to the path: one static reference test per render.
            var costing = RenderDiagnosticsLog.Enabled;
            var buildStart = costing ? Stopwatch.GetTimestamp() : 0L;

            EnsureCopyIntercepted(viewer);

            var links = GetLinks(viewer);
            // Collects the references the renderer couldn't answer from cache — resolution is off the
            // UI thread, so they render as plain text now and the document is rebuilt once they land.
            var pending = links is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var text = GetText(viewer);
            // Through the firewall, never straight to the renderer: this runs on a dispatcher tick,
            // where an unhandled exception is a dead IDE rather than a broken message. See
            // MarkdownRenderFirewall — a user's crash dump is why it exists.
            // Resolve the themed values ONCE against this viewer, which is in the tree, instead of
            // letting every element in the document register its own resource reference and resolve at
            // attach. Measured: a 17,730-char message spent 1106ms of a 1132ms build on the assignment
            // alone, ~621 registrations at ~1.8ms each in devenv (issue #86). Null when the viewer is
            // out of the tree and can answer nothing — the renderer then behaves exactly as it always
            // did, and the corrective render below is what fixes such a document.
            var palette = MarkdownPalette.Resolve(viewer);

            var parseStart = costing ? Stopwatch.GetTimestamp() : 0L;
            var document = MarkdownRenderFirewall.Render(text, links, pending, palette);
            var parseEnd = costing ? Stopwatch.GetTimestamp() : 0L;

            // A FlowDocument does NOT inherit FontFamily from the element hosting it — it applies its
            // own class default, which is Georgia, a serif (measured: leaving it unset rendered the
            // assistant's replies in Georgia while the rest of the pane stayed sans). Binding it to
            // the viewer instead makes the typeface track whatever the host set on the chat root,
            // which for the VSIX is a live reference to VS's environment font. A resource lookup
            // would work too, but only by copying a value that the VSIX would then have to keep in
            // sync — the binding follows changes on its own.
            document.SetBinding(
                System.Windows.Documents.TextElement.FontFamilyProperty,
                new System.Windows.Data.Binding(nameof(Control.FontFamily)) { Source = viewer });

            // `attach` starts here. Assigning the document is where its elements first meet a real
            // tree — and with it the resource dictionaries every inline code span registered against
            // while it had no parent. Timed apart from the parse because the offline bench says the
            // two behave very differently in a live tree, and cannot say by how much in devenv's.
            var attachStart = costing ? Stopwatch.GetTimestamp() : 0L;
            viewer.Document = document;

            // Everything above is `build` — the parse and the FlowDocument construction, which is all
            // the work this call actually does. The assignment itself only marks the viewer dirty; the
            // formatting and line-breaking it owes are `frame`, and NoteRenderCost goes and times them.
            if (costing)
                NoteRenderCost(viewer, state, text, buildStart, parseStart, parseEnd, attachStart, cause);

            // Opt-in (CWKT_MARKDOWN_RENDER_LOG) trace of what this document was laid out against — see
            // MarkdownRenderDiagnostics. Note ActualWidth here is the width the LAST arrange gave this
            // viewer, since the new document has not been measured yet; that is the right comparison
            // anyway, because the question is whether successive renders of one message disagree.
            if (MarkdownRenderDiagnostics.Enabled)
                MarkdownRenderDiagnostics.Record(viewer, document, text);

            // The document above was built against whatever the tree could answer AT THIS MOMENT, and a
            // detached viewer answers nothing: every DynamicResource/SetResourceReference in the built
            // document (Chat.CodeFontFamily on inline code spans, Chat.Foreground, the code-block
            // brushes) resolves to null, and every inherited value (FontFamily) falls back to the WPF
            // default rather than the chat root's. So the message renders with its code spans in the
            // PROSE font — narrower glyphs, so it re-wraps, and without the mono font's taller leading,
            // so it packs tighter. That is the reported "the last message goes dense".
            //
            // It renders detached because the virtualising panel RECYCLES containers: a container is
            // pulled out of the tree, re-pointed at a different message, and the render fires before it
            // goes back in (proven with CWKT_MARKDOWN_RENDER_LOG against a live VS session: renders
            // logging connected=False and codeFont=<null>, including the last render of a message left
            // visibly compressed).
            //
            // What re-inserting the container does NOT do is redo the text FORMATTING. The resource
            // references themselves recover on their own — MarkdownReattachTests measured that, and it
            // is why this cannot be fixed by making the references more robust — but the document was
            // line-broken while those references were unresolved, and a reference that then re-resolves
            // to a value invalidates nothing that would re-break it. So the wrong layout is what stays
            // on screen: the message keeps the wrapping and leading it was formatted with.
            //
            // Hence a corrective render once the viewer is back in the tree. The detached render is NOT
            // skipped: several callers set the text once and read Document straight afterwards (see the
            // class remarks), so a document must always be assigned synchronously. The cost is one extra
            // build per detach/attach cycle — ~0.15ms of a ~1.4ms realisation, per the measurements in
            // ChatView.xaml — and only on containers that actually rendered while detached.
            //
            // Connectedness is the test, NOT IsLoaded: the trace shows plenty of connected-but-not-yet-
            // loaded renders resolving every value correctly, so gating on IsLoaded would re-render a
            // large slice of healthy ones for nothing.
            if (PresentationSource.FromVisual(viewer) is null)
                EnsureRerenderOnReattach(viewer, state);

            state.LastRenderTimestamp = Stopwatch.GetTimestamp();
            // An empty document is not yet "showing something": the next set is the one that puts the
            // message on screen, and it must not be deferred either.
            state.HasRenderedContent = !string.IsNullOrEmpty(text);

            if (pending is null || pending.Count == 0 || links is null)
                return;
            foreach (var reference in pending)
            {
                // Only a HIT can change what is on screen. The resolver calls back either way, by
                // design — "so a caller waiting to re-render is never stranded" — but a reference that
                // resolved to nothing renders as plain text on the rebuild exactly as it does now, so
                // the new document would be byte-identical. Skipping is therefore not a heuristic; the
                // documents are the same by construction. Whether the answer matters is the caller's
                // question, not the resolver's, which is why the test belongs here.
                var key = reference;
                links.Resolver.RequestResolve(key, () =>
                {
                    if (links.Resolver.TryGetResolved(key, out var full) && full is not null)
                        QueueRender(viewer, StateOf(viewer), RenderCause.Resolve);
                });
            }
        }

        /// <summary>
        /// Records what this render cost, and arranges for the layout pass it owes to be timed
        /// (issue #86). Only reached when a host installed the cost sink.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><c>frame</c> is timed to a callback posted just below <see cref="DispatcherPriority.Render"/>,
        /// which is what makes it a measurement at all.</b> Assigning <c>Document</c> returns immediately —
        /// WPF formats and line-breaks in the layout pass afterwards, and on a long message that is the
        /// larger half of the cost. A callback at <see cref="DispatcherPriority.Loaded"/> is the first
        /// thing the dispatcher runs once that pass is done, so the gap between the two is the pass. It
        /// is a proxy: anything else the dispatcher ran in between lands in it too, which is why the log
        /// labels it an upper bound and reports the exact build half beside it.
        /// </para>
        /// <para>
        /// A detached viewer is laid out against nothing, so its frame sample is near-zero. That is not
        /// noise to filter — a recycled container rendering off-tree genuinely costs nothing to lay out —
        /// but it is why the sample count is reported rather than assumed equal to the render count.
        /// </para>
        /// </remarks>
        private static void NoteRenderCost(
            FlowDocumentScrollViewer viewer, RenderState state, string? text,
            long buildStart, long parseStart, long parseEnd, long attachStart, RenderCause cause)
        {
            var buildEnd = Stopwatch.GetTimestamp();
            var cost = state.Cost ??= new MarkdownRenderCost();

            var closed = cost.NoteRender(
                text,
                MarkdownRenderCost.ToMs(buildEnd - buildStart),
                MarkdownRenderCost.ToMs(buildEnd),
                MarkdownRenderCost.ToMs(parseEnd - parseStart),
                MarkdownRenderCost.ToMs(buildEnd - attachStart),
                cause);
            // This render belonged to a different message than the last one (a gap, or a recycled
            // container handed text that is not the last one grown), so the previous message's line is
            // owed now.
            if (closed is not null)
                RenderDiagnosticsLog.Write(RenderDiagnosticsLog.ViewerId(viewer), closed);

            // Captured so the callback can take EVERY viewer's builds back out of the frame window, and
            // drop itself if a boundary has since closed the episode it belonged to. Both are the normal
            // case under a scroll, where realisation renders are synchronous and back to back — and the
            // builds landing in this window are mostly other containers being realised, not ours.
            var buildsAtPost = MarkdownRenderCost.GlobalBuildMs;
            var generation = cost.Generation;
            viewer.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => cost.NoteFrame(
                    MarkdownRenderCost.ToMs(Stopwatch.GetTimestamp() - buildEnd),
                    MarkdownRenderCost.GlobalBuildMs - buildsAtPost,
                    generation)));

            EnsureCostFlush(viewer, state);
        }

        /// <summary>
        /// Makes sure the episode a render just opened will eventually be written, however the message
        /// ends. Nothing on the render path can close one — the last render of a message looks exactly
        /// like the next one not having arrived yet — so the close is a timer, and only a timer.
        /// </summary>
        private static void EnsureCostFlush(FlowDocumentScrollViewer viewer, RenderState state)
        {
            if (state.CostFlush is not null)
                return;

            var timer = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(MarkdownRenderCost.EpisodeGapMs),
            };
            state.CostFlush = timer;
            timer.Tick += (_, _) =>
            {
                var cost = state.Cost;
                var finished = cost?.CloseIfIdle(MarkdownRenderCost.NowMs());
                if (finished is not null)
                    RenderDiagnosticsLog.Write(RenderDiagnosticsLog.ViewerId(viewer), finished);

                // Settled: stop rather than tick forever behind every message in the transcript, the
                // same bargain the throttle timer above makes. The next render re-arms it.
                if (cost is null || !cost.IsOpen)
                {
                    timer.Stop();
                    state.CostFlush = null;
                }
            };
            timer.Start();
        }

        /// <summary>
        /// Re-renders every markdown viewer under <paramref name="root"/>, for a host whose themed values
        /// have just changed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the bill for issue #86's fix, and it has to be paid by the host that changes the
        /// theme.</b> A document built from <see cref="MarkdownPalette"/> holds resolved VALUES, and a
        /// value does not track its key the way a resource reference does — so a theme switch repaints
        /// every other part of the pane and would leave already-built documents in the old colours.
        /// </para>
        /// <para>
        /// Only REALISED viewers need it, which is why walking the tree is sufficient and no registry of
        /// live viewers is kept: a container that is not realised has no document, and will build one
        /// against the new values when it is. Synchronous, because a theme change is rare and a pane that
        /// repaints in two stages looks broken.
        /// </para>
        /// </remarks>
        public static void RefreshThemedValues(DependencyObject? root)
        {
            if (root is null)
                return;

            // Only when the values actually changed. A host raises this for more than a theme switch —
            // the VSIX also hooks the classification format map, which fires during startup for the
            // editor's own late reaction, so the field log showed `causes=input:2,theme:2` on every
            // viewer realised at that moment: a full re-render of the transcript against values
            // identical to the ones it was already built with. The gate belongs HERE rather than in the
            // host because the trigger is "the themed values changed", which is a fact about the
            // palette, and any host that grows a second hook would otherwise have to rediscover this.
            //
            // A source that cannot answer yields null, which deliberately does NOT match, so an
            // unanswerable root re-renders exactly as it did before.
            if (root is FrameworkElement themed)
            {
                var current = MarkdownPalette.Resolve(themed);
                if (current is not null && current.Matches(LastPaletteOf(themed)))
                    return;
                themed.SetValue(LastPaletteProperty, current);
            }

            foreach (var viewer in MarkdownViewersUnder(root))
            {
                // Only viewers this class actually drives: one that has no render state was never given
                // markdown, so re-rendering it would assign an empty document over the host's own.
                if (viewer.GetValue(RenderStateProperty) is RenderState state)
                    Render(viewer, state, RenderCause.Theme);
            }
        }

        /// <summary>
        /// The palette the last <see cref="RefreshThemedValues"/> re-rendered a root against. Kept on the
        /// root itself rather than in a static, so two chat panes cannot suppress each other's refresh.
        /// </summary>
        private static readonly DependencyProperty LastPaletteProperty = DependencyProperty.RegisterAttached(
            "LastPalette", typeof(MarkdownPalette), typeof(MarkdownText), new PropertyMetadata(null));

        private static MarkdownPalette? LastPaletteOf(DependencyObject root) =>
            root.GetValue(LastPaletteProperty) as MarkdownPalette;

        private static IEnumerable<FlowDocumentScrollViewer> MarkdownViewersUnder(DependencyObject root)
        {
            if (root is FlowDocumentScrollViewer viewer)
                yield return viewer;

            var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < children; i++)
            {
                foreach (var found in MarkdownViewersUnder(
                    System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                {
                    yield return found;
                }
            }
        }

        /// <summary>
        /// Arranges for one more render when a viewer that rendered while detached is back in the tree,
        /// so its resource references and inherited values are resolved against the real chat root
        /// instead of against defaults.
        /// </summary>
        /// <remarks>
        /// <c>Loaded</c> is the signal because becoming connected IS being added to a loaded tree, and it
        /// fires again on every re-insertion — which is what a recycled container does. The handler is
        /// static, so subscribing cannot root a viewer that is never attached again (a discarded
        /// container, a test double): the delegate hangs off the viewer, not the other way round.
        /// </remarks>
        private static void EnsureRerenderOnReattach(FlowDocumentScrollViewer viewer, RenderState state)
        {
            if (state.AwaitingReattach)
                return;
            state.AwaitingReattach = true;
            viewer.Loaded += OnViewerReattached;
        }

        private static void OnViewerReattached(object sender, RoutedEventArgs e)
        {
            if (sender is not FlowDocumentScrollViewer viewer)
                return;

            viewer.Loaded -= OnViewerReattached;
            var state = StateOf(viewer);
            // Cleared BEFORE the render, so that a render which somehow still finds itself detached can
            // arm the next one rather than being the last word.
            state.AwaitingReattach = false;
            Render(viewer, state, RenderCause.Reattach);
        }

        // Selection-copy must go through ChatClipboard so embedded inlines (emoji, checkboxes,
        // images) contribute their plain-text stand-ins — WPF's native copy drops them. Wired once
        // per viewer (the attached flag guards against re-subscribing on every text delta).
        private static readonly DependencyProperty CopyInterceptedProperty = DependencyProperty.RegisterAttached(
            "CopyIntercepted", typeof(bool), typeof(MarkdownText), new PropertyMetadata(false));

        private static void EnsureCopyIntercepted(FlowDocumentScrollViewer viewer)
        {
            if ((bool)viewer.GetValue(CopyInterceptedProperty))
                return;
            viewer.SetValue(CopyInterceptedProperty, true);
            ChatClipboard.InterceptCopy(viewer, () => viewer.Selection);
        }
    }
}
