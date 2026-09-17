using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodeWicket.UI.Diagnostics;
using CodeWicket.UI.Markdown;

namespace CodeWicket.UI.Controls
{
    /// <summary>
    /// The transcript's <see cref="ItemsControl"/>, subclassed for one reason: to measure what
    /// realising and laying out its items costs (issue #86, the <c>[realise]</c> trace).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A subclass because there is no other seam.</b> The two facts wanted are "a container was
    /// prepared for this item" and "this container's own measure took N ms", and neither is reachable
    /// from outside: <c>ItemContainerGenerator.StatusChanged</c> is per BATCH and names no item, and a
    /// container's layout time is not observable at all without being the container. Every alternative
    /// hook measures something adjacent and reports it as though it were the thing.
    /// </para>
    /// <para>
    /// <b>It changes nothing when the trace is off</b>, which is the whole design constraint — this sits
    /// under every scroll of every install. Off, the additions are one static bool test per prepare and
    /// per layout pass; the container is a <see cref="ContentPresenter"/> subclass that overrides only
    /// the two layout methods and delegates both, so WPF's own behaviour is untouched.
    /// <c>IsItemItsOwnContainerOverride</c> is deliberately NOT overridden: the base answers "is this
    /// item a UIElement", and re-stating that in terms of our container type would change what the
    /// control does with a UIElement item to buy nothing.
    /// </para>
    /// <para>
    /// <b>The accumulator is per-control rather than static</b>, so two chat panes (a second VS window,
    /// the Desktop host beside a test) cannot merge their gestures into one line — the reverse of
    /// <c>MarkdownRenderCost.GlobalBuildMs</c>, which is process-wide precisely because it is only ever
    /// read as a difference.
    /// </para>
    /// </remarks>
    public class TranscriptItemsControl : ItemsControl
    {
        private readonly RealisationCost _cost = new RealisationCost();
        private DispatcherTimer? _flush;

        /// <summary>
        /// Rasterise each row once instead of re-rendering it as it moves — experimental, set by the
        /// host from config. See <c>ExtensionConfig.TranscriptBitmapCache</c> for what it is testing and
        /// what it costs.
        /// </summary>
        public bool BitmapCacheRows { get; set; }

        protected override DependencyObject GetContainerForItemOverride() => new TranscriptItemContainer(this);

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);

            // Applied per container rather than once on the panel: a cache on the panel would rasterise
            // the whole scrolling surface, which is the one thing that certainly cannot be reused.
            // RenderAtScale tracks the chat zoom, since this control carries the zoom LayoutTransform —
            // a bitmap rasterised at 1.0 and viewed at 1.5 is visibly soft.
            if (BitmapCacheRows && element is UIElement cached && cached.CacheMode is null)
            {
                var scale = (LayoutTransform as System.Windows.Media.ScaleTransform)?.ScaleX ?? 1.0;
                cached.CacheMode = new System.Windows.Media.BitmapCache(scale > 0 ? scale : 1.0)
                {
                    SnapsToDevicePixels = true,
                };
            }

            if (!RenderDiagnosticsLog.Enabled)
                return;

            var kind = KindOf(item);
            if (element is TranscriptItemContainer container)
                container.Kind = kind;

            // Identity as a hash, never a reference: see RealisationCost on why a diagnostic must not
            // root what it counts. RuntimeHelpers, not GetHashCode, because a view-model is free to
            // override equality and two equal items are still two items to the panel.
            // Also fed to the per-render-pass trace, which needs realisations attributed to the pass
            // that followed them rather than to a five-second window.
            RealisationsSinceRender.NoteRealised(kind);
            Publish(_cost.NoteRealised(
                kind,
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item),
                MarkdownRenderCost.NowMs()));
            EnsureFlush();
        }

        internal void NoteMeasure(string kind, double ms)
        {
            // The largest measure since the last render pass is how the one enormous message identifies
            // itself on a [render-pass] line: ~20ms against ~0.5ms for a tool row.
            RealisationsSinceRender.NoteMeasure(ms);
            Publish(_cost.NoteMeasure(kind, ms, MarkdownRenderCost.NowMs()));
        }

        internal void NoteArrange(string kind, double ms) =>
            Publish(_cost.NoteArrange(kind, ms, MarkdownRenderCost.NowMs()));

        private static void Publish(RealisationEpisode? episode)
        {
            if (episode is not null)
                RenderDiagnosticsLog.WriteLine(episode.Format());
        }

        /// <summary>
        /// Makes sure the episode a gesture opened will eventually be written. Nothing on the layout
        /// path can close one — the last measure of a drag is indistinguishable from the next not having
        /// arrived — so the close is a timer, and only a timer. Same bargain as the markdown trace's:
        /// it stops itself once there is nothing open rather than ticking behind an idle pane forever.
        /// </summary>
        private void EnsureFlush()
        {
            if (_flush is not null)
                return;

            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(RealisationCost.EpisodeGapMs),
            };
            _flush = timer;
            timer.Tick += (_, _) =>
            {
                Publish(_cost.CloseIfIdle(MarkdownRenderCost.NowMs()));
                if (!_cost.IsOpen)
                {
                    timer.Stop();
                    _flush = null;
                }
            };
            timer.Start();
        }

        // Type name → display kind, resolved once per type. The transcript's own view-models all end in
        // "ItemViewModel", so the suffix carries no information and its absence would be the only thing
        // distinguishing a foreign type — which is worth seeing.
        private static readonly Dictionary<Type, string> KindNames = new Dictionary<Type, string>();

        internal static string KindOf(object? item)
        {
            if (item is null)
                return "null";

            var type = item.GetType();
            lock (KindNames)
            {
                if (KindNames.TryGetValue(type, out var known))
                    return known;

                var name = type.Name;
                foreach (var suffix in new[] { "ItemViewModel", "ViewModel" })
                {
                    if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        name = name.Substring(0, name.Length - suffix.Length);
                        break;
                    }
                }

                KindNames[type] = name;
                return name;
            }
        }
    }

    /// <summary>
    /// One transcript row's container, which times its own layout so the cost can be attributed to the
    /// KIND of item it is showing.
    /// </summary>
    /// <remarks>
    /// The timing is around <c>base</c>, so it covers the whole subtree the data template built —
    /// including a message's <see cref="System.Windows.Controls.FlowDocumentScrollViewer"/> re-formatting
    /// its document, which is the cost <c>[md-cost]</c> could see the shadow of and never name.
    /// </remarks>
    internal sealed class TranscriptItemContainer : ContentPresenter
    {
        private readonly TranscriptItemsControl _owner;

        internal TranscriptItemContainer(TranscriptItemsControl owner) => _owner = owner;

        /// <summary>What is being shown, for attribution. Set when the container is prepared.</summary>
        internal string Kind { get; set; } = string.Empty;

        /// <summary>
        /// The kind to charge this pass to, resolving it if the container was prepared before the trace
        /// was switched on. Without the fallback those passes collect under an EMPTY name — measured, a
        /// nameless bucket holding five of the first run's measures — which is worse than a wrong label
        /// because a reader cannot tell whether it is a bug in the trace or a row type nobody named.
        /// </summary>
        private string AttributedKind =>
            Kind.Length > 0 ? Kind : Kind = TranscriptItemsControl.KindOf(Content);

        protected override Size MeasureOverride(Size constraint)
        {
            if (!RenderDiagnosticsLog.Enabled)
                return base.MeasureOverride(constraint);

            var start = Stopwatch.GetTimestamp();
            var size = base.MeasureOverride(constraint);
            _owner.NoteMeasure(AttributedKind, MarkdownRenderCost.ToMs(Stopwatch.GetTimestamp() - start));
            return size;
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (!RenderDiagnosticsLog.Enabled)
                return base.ArrangeOverride(arrangeSize);

            var start = Stopwatch.GetTimestamp();
            var size = base.ArrangeOverride(arrangeSize);
            _owner.NoteArrange(AttributedKind, MarkdownRenderCost.ToMs(Stopwatch.GetTimestamp() - start));
            return size;
        }
    }
}
