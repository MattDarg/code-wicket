using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.Desktop
{
    /// <summary>
    /// <c>--perf</c>: times the two gestures reported as slow on a long conversation — growing the
    /// message box by a line, and showing/dismissing the permission banner — against a swept transcript
    /// size, so "does this cost scale with history?" is answered by a number instead of a theory.
    ///
    /// <para>Both gestures change the height of an <c>Auto</c> row in the chat <c>Grid</c>, whose <c>*</c>
    /// row holds the (unvirtualised) transcript. The open question is whether that forces work
    /// proportional to the item count, and if so whether the cost lands in layout or in rasterisation —
    /// which is why layout and render are timed separately rather than as one "felt slowness".</para>
    ///
    /// <para>Render is timed through <see cref="RenderTargetBitmap"/>, which always rasterises on the CPU.
    /// That is deliberately the pessimistic path and the closest offline proxy for a GPU-less VDI, where
    /// WPF falls back to software rendering for real; the reported <c>renderTier</c> says which the host
    /// box would otherwise have used, so a dev-machine run isn't mistaken for a VDI one.</para>
    /// </summary>
    /// <summary>Which item types a seeded transcript is built from (see <c>SeedTranscript</c>).</summary>
    internal enum SeedKind
    {
        Mixed,
        User,
        Assistant,
        Tool,
    }

    internal static class PerfHarness
    {
        // Swept transcript sizes (items, not turns — a turn is ~3 items). Defaults span "short test
        // conversation" to "long working session"; override with --perf-items 10,50,200,500,1000.
        private static readonly int[] DefaultSizes = { 10, 50, 200, 500 };

        // Timed repetitions per gesture per size. The reported figure is the median (typing latency is
        // felt per keystroke, so the typical case is what matters) alongside the max, which is what a
        // stutter actually is.
        private const int DefaultIterations = 25;

        public static async Task RunAsync(ChatViewModel vm, Window window, string workDir, string[] args)
        {
            var path = Path.Combine(workDir, "perf-result.txt");
            var report = new StringBuilder();
            try
            {
                await vm.InitializeAsync().ConfigureAwait(true);

                var sizes = ParseSizes(App.PerfOptionValue(args, "--perf-items")) ?? DefaultSizes;
                var iterations = int.TryParse(App.PerfOptionValue(args, "--perf-iterations"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var it) && it > 0
                    ? it
                    : DefaultIterations;

                var title = Branding.ProductName + " chat — transcript-size performance sweep";
                report.AppendLine(title);
                report.AppendLine(new string('=', title.Length));
                report.AppendLine(Inv(
                    $"renderTier      : {(RenderCapability.Tier >> 16)} (0 = software rendering, as on a GPU-less VDI)"));
                report.AppendLine(Inv(
                    $"windowSize      : {window.ActualWidth:F0} x {window.ActualHeight:F0}"));
                report.AppendLine(Inv($"iterations      : {iterations} per gesture per size"));
                report.AppendLine();
                report.AppendLine("Layout = window.UpdateLayout() (measure + arrange) after the gesture.");
                report.AppendLine("Render = full-window RenderTargetBitmap rasterisation (always CPU-side).");
                report.AppendLine("Visuals = live elements in the window's visual tree (no virtualisation => grows with history).");
                report.AppendLine();
                report.AppendLine("Controls: idle = UpdateLayout with nothing dirtied (validates the harness);");
                report.AppendLine("inputEdit = text changed WITHOUT changing the box's line count, so no row resizes.");
                report.AppendLine("If inputEdit is cheap and inputGrow is not, the cost is specifically the Auto-row");
                report.AppendLine("height change propagating into the transcript's * row.");
                report.AppendLine();
                report.AppendLine("  items |  visuals | idle ms | inputEdit ms | inputGrow layout ms | banner layout ms  | render ms");
                report.AppendLine("        |          |  median |    median    |   median /   max    |  median /   max   |  median /   max");
                report.AppendLine(" -------+----------+---------+--------------+---------------------+-------------------+------------------");

                foreach (var size in sizes)
                {
                    SeedTranscript(vm, size);
                    // Let the seeded tree fully realise (and the auto-scroll settle) before timing, so the
                    // first measured gesture isn't paying for the seed's own layout.
                    window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    window.UpdateLayout();

                    var visuals = CountVisuals(window);
                    var idle = await MeasureIdleAsync(window, iterations).ConfigureAwait(true);
                    var inputEdit = await MeasureInputEditAsync(vm, window, iterations).ConfigureAwait(true);
                    var inputGrow = await MeasureInputGrowthAsync(vm, window, iterations).ConfigureAwait(true);
                    var banner = await MeasureBannerToggleAsync(vm, window, iterations).ConfigureAwait(true);
                    var render = MeasureRender(window, Math.Max(5, iterations / 3));

                    // Each fragment is wrapped separately: an addition of interpolated strings converts to an
                    // interpolated-string HANDLER as one unit, but not to FormattableString, so a single
                    // outer Inv(...) would concatenate under the current culture before it ever saw one.
                    report.AppendLine(
                        Inv($" {size,6} | {visuals,8} | {Median(idle),7:F2} | {Median(inputEdit),12:F2} ")
                        + Inv($"|  {Median(inputGrow),7:F2} / {inputGrow.Max(),6:F2}  ")
                        + Inv($"| {Median(banner),7:F2} / {banner.Max(),6:F2} | {Median(render),7:F2} / {render.Max(),6:F2}"));
                }

                report.AppendLine();
                report.AppendLine("Reading it: a median that grows roughly with the item count means the gesture");
                report.AppendLine("re-does work for the whole history (the unvirtualised transcript); a flat median");
                report.AppendLine("means WPF's cached-measure short-circuit is holding and the cost is elsewhere.");

                // Which item type carries the cost. Decides whether replacing the per-message
                // FlowDocument host would be enough on its own, or whether only bounding the realised
                // item count (virtualisation / a cap) can help.
                const int compositionSize = 200;
                report.AppendLine();
                report.AppendLine(Inv(
                    $"Cost by item type, at {compositionSize} items (same gesture: message box grows a line)"));
                report.AppendLine(" composition          |  visuals | inputGrow layout ms (median)");
                report.AppendLine(" ---------------------+----------+-----------------------------");
                foreach (var (label, kind) in new[]
                         {
                             ("user messages only", SeedKind.User),
                             ("assistant markdown  ", SeedKind.Assistant),
                             ("tool rows only", SeedKind.Tool),
                             ("mixed (realistic)", SeedKind.Mixed),
                         })
                {
                    SeedTranscript(vm, compositionSize, kind);
                    window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    window.UpdateLayout();

                    var visuals = CountVisuals(window);
                    var grow = await MeasureInputGrowthAsync(vm, window, iterations).ConfigureAwait(true);
                    report.AppendLine(Inv(
                        $" {label,-20} | {visuals,8} | {Median(grow),27:F2}"));
                }

                await MeasureScrollDragAsync(vm, window, report, workDir).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                report.AppendLine();
                report.AppendLine("FAILED: " + ex);
            }

            var text = report.ToString();
            File.WriteAllText(path, text);
            Console.WriteLine(text);
            App.Current.Shutdown(0);
        }

        /// <summary>
        /// Control: a layout pass with nothing invalidated. Must be ~0 at every size — if it isn't, the
        /// harness is measuring its own overhead rather than the gestures.
        /// </summary>
        private static async Task<List<double>> MeasureIdleAsync(Window window, int iterations)
        {
            var times = new List<double>(iterations);
            for (var i = 0; i < iterations; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                window.UpdateLayout();
                times.Add(sw.Elapsed.TotalMilliseconds);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            return times;
        }

        /// <summary>
        /// Control: edit the message-box text WITHOUT changing its line count, so the box keeps its
        /// height and no Grid row resizes. Isolates "any text edit invalidates the world" from "the
        /// Auto row's height change is what reaches the transcript".
        /// </summary>
        private static async Task<List<double>> MeasureInputEditAsync(ChatViewModel vm, Window window, int iterations)
        {
            var times = new List<double>(iterations);
            vm.InputText = "typing a prompt";
            window.UpdateLayout();

            for (var i = 0; i < iterations; i++)
            {
                // Same length and no newline, so the wrapped line count cannot change.
                vm.InputText = i % 2 == 0 ? "typing a prompt" : "typing a prompo";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                window.UpdateLayout();
                times.Add(sw.Elapsed.TotalMilliseconds);

                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            vm.InputText = string.Empty;
            window.UpdateLayout();
            return times;
        }

        /// <summary>
        /// One measured gesture: append a line to the message box (the box auto-grows, changing its
        /// <c>Auto</c> row's height) and time the resulting layout pass. Alternates grow/shrink so every
        /// iteration genuinely dirties layout rather than re-timing an already-valid tree.
        /// </summary>
        private static async Task<List<double>> MeasureInputGrowthAsync(ChatViewModel vm, Window window, int iterations)
        {
            var times = new List<double>(iterations);
            var baseline = "typing a prompt";
            vm.InputText = baseline;
            window.UpdateLayout();

            for (var i = 0; i < iterations; i++)
            {
                // Odd iterations grow the box, even ones shrink it back; both change the row height.
                vm.InputText = i % 2 == 0
                    ? baseline + "\nsecond line\nthird line\nfourth line"
                    : baseline;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                window.UpdateLayout();
                times.Add(sw.Elapsed.TotalMilliseconds);

                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            vm.InputText = string.Empty;
            window.UpdateLayout();
            return times;
        }

        /// <summary>
        /// The other measured gesture: raise a permission banner and dismiss it. The banner shares the
        /// chat Grid with the transcript, so showing/hiding it resizes the transcript's row — the same
        /// coupling the message box exercises, but with a much larger height delta.
        /// </summary>
        private static async Task<List<double>> MeasureBannerToggleAsync(ChatViewModel vm, Window window, int iterations)
        {
            var times = new List<double>(iterations);

            for (var i = 0; i < iterations; i++)
            {
                var id = "perf-" + i;
                var pending = vm.RequestPermissionAsync(new PermissionRequestDto(
                    id, "Running: dotnet build MySolution.sln", "execute",
                    "dotnet build MySolution.sln", "dotnet build MySolution.sln",
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                        new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    }));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                window.UpdateLayout();
                var show = sw.Elapsed.TotalMilliseconds;

                // Dismiss it the way the user does — click an option — and time the collapse. That click
                // is the ONLY thing that completes `pending`, so anything that stops it landing (no
                // banner, a different request at the head of the queue, or an option set with no allow
                // in it) would leave the await below hanging with no timeout to end it. Name the state
                // instead: RunAsync's catch writes it into perf-result.txt as a FAILED line (#247).
                var banner = vm.PendingPermission;
                var allow = banner?.Options.FirstOrDefault(o => o.IsAllow);
                if (banner is null || !string.Equals(banner.ToolCallId, id, StringComparison.Ordinal) || allow is null)
                    throw new InvalidOperationException(
                        "Permission banner could not be dismissed, so the toggle cannot be timed: " +
                        (banner is null
                            ? "no banner was showing after the request was raised."
                            : !string.Equals(banner.ToolCallId, id, StringComparison.Ordinal)
                                ? Inv($"the banner at the head of the queue is '{banner.ToolCallId}', not '{id}'.")
                                : "the banner offered no allow-style option."));

                allow.Command.Execute(null);
                sw.Restart();
                window.UpdateLayout();
                var dismiss = sw.Elapsed.TotalMilliseconds;

                // The pair is one round trip of the same coupling; report the worse half, which is what
                // the user notices.
                times.Add(Math.Max(show, dismiss));

                await pending.ConfigureAwait(true);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            return times;
        }

        /// <summary>
        /// Drags the scrollbar thumb, which the mouse wheel does not exercise: the wheel advances by a
        /// fixed delta from wherever it is, while a thumb drag maps a FRACTION of the track onto
        /// <c>ScrollableHeight</c> — an estimate under virtualisation, since unrealised items have no
        /// measured height and the panel substitutes an average.
        ///
        /// <para>Reported as two independent symptoms, because "jerky" can be either and they have
        /// opposite fixes. STUTTER is per-step layout time: every new offset realises a fresh set of
        /// items, and an assistant message costs a FlowDocument reformat to realise. TRACKING is whether
        /// content moves evenly with the thumb: <c>drift</c> is how far the extent estimate moves during
        /// the sweep (the thumb's own scale being redefined underneath the drag), and <c>backSteps</c>
        /// counts sweeps forward that moved content BACKWARDS — the visible lurch.</para>
        ///
        /// <para>Two passes over the same transcript separate cause from cost. WPF's
        /// VirtualizingStackPanel already caches the size of every container it has realised (in the
        /// ItemsControl's item storage), so pass 2 runs against a fully warm cache. If pass 2 is steady
        /// where pass 1 was not, the estimate is the problem and warming it is the fix; if both stutter
        /// equally, the cost is realisation itself and no amount of cached heights will touch it.</para>
        /// </summary>
        private static async Task MeasureScrollDragAsync(
            ChatViewModel vm, Window window, StringBuilder report, string workDir)
        {
            const int dragSize = 500;

            report.AppendLine();
            report.AppendLine(Inv($"Scrollbar drag, {dragSize} items"));

            if (window.Content is not CodeWicket.UI.Views.ChatView view)
            {
                report.AppendLine(" (skipped: window content is not a ChatView)");
                return;
            }

            SeedTranscript(vm, dragSize);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();

            var scroll = view.TranscriptScrollForDiagnostics;
            if (scroll is null || scroll.ScrollableHeight <= 0)
            {
                report.AppendLine(Inv(
                    $" (skipped: no scrollable transcript; scrollable={scroll?.ScrollableHeight ?? -1:F0})"));
                return;
            }

            report.AppendLine(" sweep                      | step layout ms          | whole | drift | pauses    | tracking");
            report.AppendLine("                            | median /   p90 /   max  | drag  |       | >50ms(n)  | back / worst");
            report.AppendLine(" ---------------------------+-------------------------+-------+-------+-----------+-------------");

            // Step count is the drag's SPEED: a 40-step sweep turns over ~12 items per step (a fast
            // fling), a 200-step one ~2.5 (a careful drag). They stress different things — the fling
            // realises a wholly new viewport every step with nothing to reuse, the careful drag mostly
            // reuses and pays for a couple of new items — so a single figure would describe neither.
            // The one row that must NOT be warmed up: it is the first pass over a fresh transcript, which
            // is the only chance to see the size cache cold. It therefore also carries cold JIT, so read
            // it only against its warm twin below and only for the DRIFT column.
            await SweepAsync("mixed, fast fling, cold", 40, warmUp: false).ConfigureAwait(true);
            await SweepAsync("mixed, fast fling, warm", 40).ConfigureAwait(true);
            await SweepAsync("mixed, careful drag", 200).ConfigureAwait(true);

            // NOTE: ScrollViewer.IsDeferredScrollingEnabled (the stock answer to an expensive-content
            // drag — thumb moves freely, content updates once on release) CANNOT be measured here and
            // was removed after it appeared to. It engages only for a real ScrollBar thumb gesture;
            // ScrollToVerticalOffset bypasses it, so the "deferred" row was re-measuring the undeferred
            // path and its near-identical numbers read as "makes no difference". Only a live Visual Studio instance answers it.

            // Same sweep per item type. If one kind carries the drag the way it carries the resize, the
            // cheaper fix is making THAT item cheaper to realise rather than realising fewer of them.
            foreach (var (label, kind) in new[]
                     {
                         ("user messages, fling", SeedKind.User),
                         ("assistant markdown, fling", SeedKind.Assistant),
                         ("tool rows, fling", SeedKind.Tool),
                     })
            {
                SeedTranscript(vm, dragSize, kind);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);
                window.UpdateLayout();
                await SweepAsync(label, 40).ConfigureAwait(true);
            }

            // The realistic shape: most replies are a few paragraphs, a few carry a whole file. Reported
            // as pauses rather than a median, because that is how the symptom presents — a drag that
            // catches two or three times rather than one that is uniformly heavy. Both directions,
            // since the live gesture is dragging UP from a restored transcript sitting at the bottom.
            // Every row here is warmed: the symptom survives repeated dragging, so a cold cache is not
            // what is being explained, and an unwarmed row would just re-measure JIT.
            SeedTranscript(vm, dragSize, SeedKind.Mixed, varied: true);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();

            await SweepAsync("varied sizes, drag down", 200).ConfigureAwait(true);
            await SweepAsync("varied sizes, drag up", 200, upward: true).ConfigureAwait(true);
            await SweepAsync("varied sizes, fling up", 40, upward: true).ConfigureAwait(true);

            // Which RENDERER is expensive, at equal text size. Thinking and user messages go through
            // SelectableEmojiText (a read-only RichTextBox); assistant messages go through the Markdig →
            // FlowDocument path. A transcript heavy in agent thinking is therefore mostly the former, so
            // the two are compared on identical text rather than on their usual content.
            foreach (var (label, role) in new[]
                     {
                         ("thinking (RichTextBox)", MessageRole.Thinking),
                         ("user (RichTextBox)", MessageRole.User),
                         ("assistant (FlowDocument)", MessageRole.Assistant),
                     })
            {
                SeedUniformText(vm, 200, role);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);
                window.UpdateLayout();
                await SweepAsync("equal text: " + label, 40).ConfigureAwait(true);
            }

            // Cost against message SIZE, holding the renderer fixed. This is the one that decides the
            // fix: if doubling the text doubles the cost, a big message is simply more text and the only
            // lever is not realising it mid-drag. If it more than doubles, one oversized element is
            // pathological on its own and splitting or capping it would pay off directly.
            foreach (var repeats in new[] { 4, 16, 64, 256 })
            {
                SeedUniformText(vm, 120, MessageRole.Thinking, repeats);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);
                window.UpdateLayout();
                await SweepAsync(
                    Inv($"thinking, ~{repeats * 140 / 1024.0:F1}KB each"),
                    40).ConfigureAwait(true);
            }

            // Same role, same byte size, differing only in whether the markdown carries fenced code.
            // Isolates the syntax highlighter — which re-tokenises on every realisation, not just every
            // streamed delta — from the plain cost of formatting a large document. These three rows sit
            // together deliberately: this is the comparison the whole question reduces to, and it must
            // not be read across blocks.
            foreach (var (label, body) in new[]
                     {
                         ("large assistant, prose only", LargeReply(withCode: false, withMarkup: false)),
                         ("large assistant, rich markup", LargeReply(withCode: false, withMarkup: true)),
                         ("large assistant, fenced code", LargeReply(withCode: true, withMarkup: true)),
                     })
            {
                vm.Items.Clear();
                for (var i = 0; i < 120; i++)
                    vm.Items.Add(new MessageItemViewModel(MessageRole.Assistant, body));
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);
                window.UpdateLayout();
                await SweepAsync(
                    Inv($"{label} (~{body.Length / 1024.0:F0}KB)"),
                    40).ConfigureAwait(true);
            }

            // CacheLength is how far BEYOND the viewport the panel realises. At the default of one page
            // either side a scroll step realises ~3 viewports rather than 1, so trimming it is the one
            // remaining lever that reduces work per step rather than making the work cheaper. It is a
            // trade, not a free win — the cache is what makes the NEXT wheel notch already-realised — so
            // it is swept rather than assumed, back on the realistic mixed transcript.
            SeedTranscript(vm, dragSize);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();

            var panel = view.TranscriptPanelForDiagnostics;
            if (panel is not null)
            {
                var original = VirtualizingPanel.GetCacheLength(panel);
                var originalUnit = VirtualizingPanel.GetCacheLengthUnit(panel);
                try
                {
                    foreach (var pages in new[] { 0d, 1d, 2d })
                    {
                        VirtualizingPanel.SetCacheLengthUnit(panel, VirtualizationCacheLengthUnit.Page);
                        VirtualizingPanel.SetCacheLength(panel, new VirtualizationCacheLength(pages, pages));
                        window.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.Background);

                        await SweepAsync(
                            $"cache {pages:F0}pg, fling" + (pages == 1 ? " (default)" : string.Empty), 40)
                            .ConfigureAwait(true);
                    }
                }
                finally
                {
                    VirtualizingPanel.SetCacheLengthUnit(panel, originalUnit);
                    VirtualizingPanel.SetCacheLength(panel, original);
                }
            }

            // Splits an assistant item's realisation cost into the half a cache could remove and the half
            // it could not. BUILDING the FlowDocument (Markdig parse + ColorCode + element construction)
            // happens once per realisation and its result is reusable; FORMATTING it (WPF measuring the
            // text into the viewport) has to happen wherever the document is placed. A cache is only
            // worth its memory if the build half dominates.
            var buildTimes = new List<double>(20);
            for (var i = 0; i < 20; i++)
            {
                var markdown = AssistantReply(i);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _ = CodeWicket.UI.Markdown.MarkdownFlowRenderer.ToFlowDocument(markdown);
                buildTimes.Add(sw.Elapsed.TotalMilliseconds);
            }

            report.AppendLine();
            report.AppendLine(
                Inv($"Assistant item: FlowDocument BUILD alone (no layout) = {Median(buildTimes):F2} ms median, ")
                + Inv($"{Percentile(buildTimes, 0.9):F2} p90"));
            report.AppendLine("Against the per-item realisation cost above (assistant fling median / ~12 items per");
            report.AppendLine("step), this is the share a document cache could remove; the remainder is WPF text");
            report.AppendLine("formatting, which a cache cannot avoid.");

            await MeasureFieldShapesAsync(vm, window, report, workDir).ConfigureAwait(true);

            report.AppendLine();
            report.AppendLine("Caveat on absolute figures: COMPARE WITHIN A BLOCK, not across. Each measured sweep is");
            report.AppendLine("preceded by a discarded one, which cut the ordering effect but did not remove it — the");
            report.AppendLine("same mixed/40-step configuration still reads ~23ms early in the run and ~13ms late. The");
            report.AppendLine("three blocks (cold-vs-warm-vs-speed, per-item-type, cache length) are each internally");
            report.AppendLine("consistent, and every conclusion below rests on a within-block difference.");
            report.AppendLine();
            report.AppendLine("Caveat on the tracking column: index is sampled by scanning realised containers, and");
            report.AppendLine("a container caught mid-recycle can report a stale index — so an isolated large");
            report.AppendLine("backStep in the fine sweep is not evidence of a lurch. The coarse sweeps (0 backSteps,");
            report.AppendLine("worst jump ≈ the even share) are the trustworthy reading.");

            report.AppendLine();
            report.AppendLine("Reading it: if warming the size cache were the fix, the cold row would show BOTH a large");
            report.AppendLine("drift and a much slower step than its warm twin. A large drift with a step time in the");
            report.AppendLine("same range instead means the estimate self-corrects (which WPF already does, in item");
            report.AppendLine("storage) while the cost stays where it was — in realising items on arrival. Compare a");
            report.AppendLine("step against the ~16ms frame budget: over it, the drag drops frames, and that is the");
            report.AppendLine("felt jerk. The speed rows say which drags exceed it; the per-item-type rows say what to");
            report.AppendLine("make cheaper if one has to be.");

            // Every sweep but the cold one is preceded by a discarded sweep of the same shape. Without it
            // a sweep's position in the run moved its own result by 2x — the identical mixed/40-step
            // configuration measured 33ms early and 17ms late — because JIT, the container recycling pool
            // and GC all settle as the run proceeds. The warm-up puts each measured sweep in the same
            // steady state, which is what makes the rows comparable to each OTHER.
            async Task SweepAsync(string label, int steps, bool warmUp = true, bool upward = false)
            {
                if (warmUp)
                    await RunSweepAsync(steps, upward).ConfigureAwait(true);

                var (times, extents, indices, dragMs) = await RunSweepAsync(steps, upward).ConfigureAwait(true);

                var minExtent = extents.Min();
                var maxExtent = extents.Max();
                var spread = maxExtent > 0 ? (maxExtent - minExtent) / maxExtent * 100 : 0;

                // A drag should only ever move the top item in the direction being dragged. Count the
                // steps that went the other way, and how far the biggest single jump was against the
                // even share a smooth drag would give. Deltas are signed by drag direction so an upward
                // sweep isn't scored as 200 backward steps.
                var backSteps = 0;
                var worstJump = 0;
                var evenShare = (double)dragSize / steps;
                for (var i = 1; i < indices.Count; i++)
                {
                    if (indices[i] < 0 || indices[i - 1] < 0)
                        continue;
                    var delta = (indices[i] - indices[i - 1]) * (upward ? -1 : 1);
                    if (delta < 0)
                        backSteps++;
                    worstJump = Math.Max(worstJump, Math.Abs(delta));
                }

                // A "pause" is a single step over ~3 frames' worth of layout — the discrete hitch a user
                // reports as the drag catching, as opposed to a uniformly heavy drag which reads as
                // sluggish. Counting them separately is what distinguishes "a few expensive items" from
                // "every item costs a bit too much", and those have different fixes.
                var pauses = times.Count(t => t > 50);

                report.AppendLine(
                    Inv($" {label,-26} | {Median(times),6:F2} / {Percentile(times, 0.9),5:F2} / {times.Max(),5:F2}  ")
                    + Inv($"| {dragMs,4:F0}ms | {spread,4:F1}% | {pauses,3} of {times.Count,-3} ")
                    + Inv($"| {backSteps,2} / {worstJump,3} (even {evenShare:F0})"));
            }

            /// <summary>One full pass of the thumb, timed per step.</summary>
            async Task<(List<double> Times, List<double> Extents, List<int> Indices, double DragMs)> RunSweepAsync(int steps, bool upward)
            {
                scroll.ScrollToVerticalOffset(upward ? scroll.ScrollableHeight : 0);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);

                var times = new List<double>(steps);
                var extents = new List<double>(steps);
                var indices = new List<int>(steps);
                var whole = System.Diagnostics.Stopwatch.StartNew();

                for (var i = 1; i <= steps; i++)
                {
                    // A real drag recomputes the offset from the CURRENT extent on every mouse move, so
                    // an extent that shifts mid-drag moves the content out from under a still thumb.
                    var fraction = (double)i / steps;
                    if (upward)
                        fraction = 1 - fraction;
                    scroll.ScrollToVerticalOffset(fraction * scroll.ScrollableHeight);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    window.UpdateLayout();
                    times.Add(sw.Elapsed.TotalMilliseconds);

                    extents.Add(scroll.ExtentHeight);
                    indices.Add(view.FirstRealizedIndexForDiagnostics());

                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                return (times, extents, indices, whole.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>Full-window CPU rasterisation, the software-rendering cost a VDI pays every frame.</summary>
        private static List<double> MeasureRender(Window window, int iterations)
        {
            var times = new List<double>(iterations);
            var w = (int)Math.Ceiling(window.ActualWidth);
            var h = (int)Math.Ceiling(window.ActualHeight);
            if (w <= 0 || h <= 0)
                return new List<double> { 0 };

            for (var i = 0; i < iterations; i++)
            {
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                rtb.Render(window);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            return times;
        }

        /// <summary>
        /// Builds a transcript of <paramref name="count"/> items in the shape a real working session has:
        /// a user prompt, an assistant reply carrying markdown and a fenced code block (the
        /// FlowDocument + ColorCode path, the expensive one), then a tool row with input and output
        /// detail. Synthetic rather than replayed from disk so the size is a controlled variable.
        /// </summary>
        private static void SeedTranscript(ChatViewModel vm, int count, SeedKind kind = SeedKind.Mixed, bool varied = false)
        {
            vm.Items.Clear();

            for (var i = 0; i < count; i++)
            {
                // A single-kind transcript isolates that item type's contribution; Mixed is the realistic
                // shape and the one the size sweep uses.
                var slot = kind switch
                {
                    SeedKind.User => 0,
                    SeedKind.Assistant => 1,
                    SeedKind.Tool => 2,
                    _ => i % 3,
                };

                switch (slot)
                {
                    case 0:
                        vm.Items.Add(new MessageItemViewModel(MessageRole.User,
                            $"Prompt {i / 3 + 1}: can you look at the diagnostics handling and tell me what's going on?"));
                        break;

                    case 1:
                        // A real session is not uniform: most replies are a few paragraphs, but every so
                        // often one carries a whole file or a long listing. Those outliers are what a
                        // median cannot see and what a drag hitches on, so the varied seed plants one
                        // every 15th message rather than making every item the same size.
                        var markdown = varied && i % 15 == 1
                            ? string.Concat(Enumerable.Range(0, 20).Select(k => AssistantReply(i + k)))
                            : AssistantReply(i);
                        vm.Items.Add(new MessageItemViewModel(MessageRole.Assistant, markdown));
                        break;

                    default:
                        var tool = new ToolItemViewModel("perf-tool-" + i, "get_diagnostics", "read")
                        {
                            Status = ToolStatus.Success,
                            Description = "Check the current compiler diagnostics for the file",
                            InputDetail = "scope: compiler\nfile: src/CodeWicket.Core/Diagnostics.cs\nseverity: warning",
                            OutputDetail = "3 diagnostics\nCS8602 (68,28): Dereference of a possibly null reference.\n"
                                + "CS0168 (91,17): The variable 'ex' is declared but never used.\n"
                                + "CS1998 (140,9): This async method lacks 'await' operators.",
                        };
                        vm.Items.Add(tool);
                        break;
                }
            }
        }

        /// <summary>
        /// A transcript of one role, every item carrying the SAME text, so a sweep over it isolates the
        /// cost of that role's renderer from the cost of its usual content. Deliberately plain prose:
        /// markdown syntax would give the assistant path extra work the RichTextBox path never does,
        /// which is the confound this exists to remove.
        /// </summary>
        private static void SeedUniformText(ChatViewModel vm, int count, MessageRole role, int repeats = 12)
        {
            var text = string.Concat(Enumerable.Repeat(
                "The diagnostics come from the live Roslyn workspace rather than the Error List, whose "
                + "IntelliSense half lags a just-applied edit and reports a stale span for it. ", repeats));

            vm.Items.Clear();
            for (var i = 0; i < count; i++)
                vm.Items.Add(new MessageItemViewModel(role, text));
        }

        /// <summary>
        /// A large assistant reply of roughly fixed size, built from three interchangeable paragraph
        /// kinds so the variants differ in MARKUP rather than in length. Padded back to a common length
        /// so a slower variant can't simply be a longer one.
        /// </summary>
        private static string LargeReply(bool withCode, bool withMarkup)
        {
            const string prose =
                "The diagnostics come from the live Roslyn workspace rather than the Error List, whose "
                + "IntelliSense half lags a just-applied edit and reports a stale span for it.\n\n";
            const string markup =
                "### Finding\n\nThe **live** workspace is authoritative, and the `full` scope adds the "
                + "Error List's non-compiler rows:\n\n- `compiler` is deterministic\n- `analyzers` adds "
                + "the project's own references\n- `full` adds the editor-hosted rows\n\n";
            const string code =
                "```csharp\nvar diagnostics = compilation.GetDiagnostics(cancellationToken)\n"
                + "    .Where(d => d.Severity >= floor && d.Location.IsInSource)\n"
                + "    .Select(d => new DiagnosticRow(d.Id, d.GetMessage(), Span(d)))\n"
                + "    .ToList();\n```\n\n";

            var unit = withCode ? code : withMarkup ? markup : prose;
            var builder = new StringBuilder();
            const int target = 12 * 1024;
            while (builder.Length < target)
                builder.Append(unit);
            return builder.ToString();
        }

        private static string AssistantReply(int seed) =>
            $"### Finding {seed / 3 + 1}\n\n"
            + "The diagnostics come from the **live Roslyn workspace**, not the Error List — that half "
            + "lags a just-applied edit. Here's the shape of it:\n\n"
            + "- `compiler` is deterministic but reflects the current in-memory analysis\n"
            + "- `analyzers` adds the project's own `AnalyzerReferences`\n"
            + "- `full` adds the Error List's non-compiler rows\n\n"
            + "```csharp\n"
            + "var diagnostics = compilation.GetDiagnostics(cancellationToken)\n"
            + "    .Where(d => d.Severity >= floor && d.Location.IsInSource)\n"
            + "    .Select(d => new DiagnosticRow(d.Id, d.GetMessage(), Span(d)))\n"
            + "    .ToList();\n"
            + "```\n\n"
            + "So a same-line pair survives de-duplication only when the *full* span differs.";

        private static int CountVisuals(DependencyObject root)
        {
            var n = 1;
            var children = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < children; i++)
                n += CountVisuals(VisualTreeHelper.GetChild(root, i));
            return n;
        }

        /// <summary>References in the field message that made it expensive.</summary>
        private const int FieldReferenceCount = 105;

        private const int BuildSamples = 15;

        /// <summary>
        /// Markdown BUILD cost by content shape, transcribed from a real conversation (issue #86).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>These shapes come from the field because the field contradicted what this harness was
        /// modelling.</b> A scrollbar drag on a real transcript logged one message of 24,525 chars
        /// building in <b>61ms</b> and another of 17,730 chars building in <b>821ms</b> — warm,
        /// repeatedly, same session, the SMALLER message 13x the cost. Every other sweep here varies
        /// size and fenced code, so none of them could have produced that: the expensive message has no
        /// fenced code at all, and is smaller.
        /// </para>
        /// <para>
        /// The two differ in one thing that plausibly matters — <b>105 file references against 1</b> —
        /// so these rows hold everything else still and vary that alone, ending with the same text
        /// rendered against a resolver that knows nothing, which takes the plain-text branch through an
        /// identical parse. If the resolved row is the expensive one, the cost is the link element and
        /// not the markdown, and the fix is in <c>AppendReference</c> rather than anywhere in Markdig.
        /// </para>
        /// <para>
        /// Build only, through <c>MarkdownRenderFirewall</c> — the same call the render path makes, so
        /// these numbers are directly comparable to <c>buildMax</c> in <c>render.log</c>. No dispatcher,
        /// no layout: a realisation pays this before WPF formats anything, which is why a drag that
        /// realises many messages pays it many times.
        /// </para>
        /// </remarks>
        private static async Task MeasureFieldShapesAsync(
            ChatViewModel vm, Window window, StringBuilder report, string workDir)
        {
            report.AppendLine();
            report.AppendLine("Markdown BUILD by content shape (shapes taken from the field, issue #86)");

            var links = vm.FileLinks;
            if (links is null)
            {
                report.AppendLine(" (skipped: no file-link context)");
                return;
            }

            // Real files, because a reference only becomes a Hyperlink once the resolver has resolved it
            // against something on disk. Bare filenames, as the field message writes them.
            var refDir = Path.Combine(workDir, "perf-refs");
            Directory.CreateDirectory(refDir);
            var names = new List<string>();
            for (var i = 0; i < FieldReferenceCount; i++)
            {
                var name = Inv($"PerfRef{i:D3}.cs");
                var full = Path.Combine(refDir, name);
                if (!File.Exists(full))
                    File.WriteAllText(full, "// perf harness reference target");
                names.Add(name);
            }

            links.Resolver.SetRoot(refDir);
            foreach (var name in names)
                links.Resolver.RequestResolve(name, () => { });

            // Bounded wait for the index walk and the resolutions. An unresolved run still measures
            // something, it just stops being the comparison this block exists for — so the count that
            // actually resolved is printed rather than assumed.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            var resolved = 0;
            while (DateTime.UtcNow < deadline)
            {
                resolved = names.Count(n => links.Resolver.TryGetResolved(n, out var p) && p is not null);
                if (resolved == names.Count)
                    break;
                await Task.Delay(50).ConfigureAwait(true);
            }

            // The same text against a resolver that knows nothing: every reference takes the plain-text
            // branch of AppendReference, so the parse, the bullets and the code spans are identical and
            // the only difference left is whether a Hyperlink gets built.
            var blind = new CodeWicket.UI.Markdown.FileLinkContext(
                new CodeWicket.UI.Markdown.FileReferenceResolver(null), (_, _) => Task.CompletedTask);

            report.AppendLine(Inv($" resolved {resolved} of {names.Count} references"));
            report.AppendLine(" shape                              |  chars | refs | build ms  median /   p90 /   max");
            report.AppendLine(" -----------------------------------+--------+------+----------------------------------");

            void Row(string label, string markdown, int refs, CodeWicket.UI.Markdown.FileLinkContext context)
            {
                var times = new List<double>(BuildSamples);
                for (var i = 0; i < BuildSamples; i++)
                {
                    var pending = new List<string>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _ = CodeWicket.UI.Markdown.MarkdownRenderFirewall.Render(markdown, context, pending);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                report.AppendLine(
                    Inv($" {label,-34} | {markdown.Length,6} | {refs,4} | ")
                    + Inv($"{Median(times),14:F1} / {Percentile(times, 0.9),5:F1} / {times.Max(),5:F1}"));
            }

            var withRefs = InventoryWithReferences(names, names.Count);
            Row("prose only", InventoryProse(), 0, links);
            Row("code spans, no refs (field A)", InventoryCodeSpans(), 0, links);
            Row("file refs, resolved (field B)", withRefs, names.Count, links);
            Row("file refs, same text, unresolved", withRefs, names.Count, blind);

            report.AppendLine();
            report.AppendLine(" reference count at fixed body:");
            foreach (var count in new[] { 0, 25, 50, FieldReferenceCount })
                Row(Inv($"  {count,3} resolved references"), InventoryWithReferences(names, count), count, links);

            report.AppendLine();
            report.AppendLine("Read the last two rows of the first block together: identical text, identical parse,");
            report.AppendLine("differing only in whether each reference becomes a Hyperlink. The scaling block says");
            report.AppendLine("whether that is linear per reference or a threshold effect. A drag realises these");
            report.AppendLine("shapes repeatedly, so whatever one build costs here is paid per message scrolled past.");

            MeasureAttachCost(window, report, links, withRefs);
        }

        /// <summary>
        /// Splits a realisation into BUILD, ASSIGN and LAYOUT, with the assignment done twice: into a
        /// viewer that is in the live tree and into one that is not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This block exists because the block above measured the wrong span.</b> Those rows time
        /// <c>MarkdownRenderFirewall.Render</c> alone and top out at 14ms median, against a field
        /// <c>buildMax</c> of 750–820ms warm and repeated — a 60x gap that no shape reproduced. But
        /// <c>[md-cost]</c>'s <c>build</c> spans from the render method's entry through
        /// <c>viewer.Document = document</c>, and the assignment is not in the rows above at all.
        /// </para>
        /// <para>
        /// <b>The tree is the variable</b>, which is why the assignment is measured both ways. Element
        /// construction happens with no parent in either case, so a <c>SetResourceReference</c> made
        /// during the build has nothing to resolve against — and this message carries hundreds of them
        /// (every inline code span registers two). Attaching the document to a viewer sited inside
        /// ChatView is where they meet a real merged-dictionary chain. If the in-tree column is the
        /// expensive one, the cost is resource resolution at attach time, and two consequences follow
        /// immediately: caching built documents would not help (a realisation re-attaches whatever it
        /// reuses), and the lever is the per-span references themselves.
        /// </para>
        /// <para>
        /// A document has one parent, so each sample builds its own — the build column here is
        /// incidental and is reported only to show the two are being timed apart.
        /// </para>
        /// <para>
        /// <b>ANSWERED, and the answer is that these numbers are 130x LOW.</b> The same split, reported
        /// from inside devenv by <c>[md-cost]</c>, gives the 17,730-char field message
        /// <c>parseMax=38.2 attachMax=1106.2</c> and <c>parseMax=42.1 attachMax=835.8</c> on two
        /// separate realisations — <b>attach is 95-98% of the build</b>, where this harness measures it
        /// at 7.7ms. Parse it gets right (13.8ms here against 38-42ms there, a bigger message). So read
        /// the ASSIGN columns for their RATIO and never for their magnitude: in-tree costing ~2.8x
        /// parentless is the real signal, and the standalone host cannot scale it up because it has
        /// neither devenv's tree depth nor the 28 injected VS-theme overrides that a chat message's
        /// ~621 <c>SetResourceReference</c> registrations resolve against.
        /// </para>
        /// </remarks>
        private static void MeasureAttachCost(
            Window window, StringBuilder report,
            CodeWicket.UI.Markdown.FileLinkContext links, string withRefs)
        {
            report.AppendLine();
            report.AppendLine("Realisation split: BUILD vs ASSIGN vs LAYOUT (issue #86)");

            if (window.Content is not CodeWicket.UI.Views.ChatView view || view.Content is not Panel host)
            {
                report.AppendLine(" (skipped: no ChatView panel to site a viewer in)");
                return;
            }

            // Sited inside ChatView so its DynamicResource lookups resolve against the same merged
            // dictionaries a real message's viewer sees. Sized, because an unsized element formats no
            // text and the layout column would measure nothing.
            var attached = new FlowDocumentScrollViewer { Width = 800, Height = 600 };
            if (view.TryFindResource("Chat.MarkdownDocument") is Style style)
                attached.Style = style;

            var detached = new FlowDocumentScrollViewer { Width = 800, Height = 600 };

            host.Children.Add(attached);
            try
            {
                window.UpdateLayout();

                report.AppendLine(" shape                              | build | assign detached | assign IN TREE | layout");
                report.AppendLine("                                    |median |          median |         median | median");
                report.AppendLine(" -----------------------------------+-------+-----------------+----------------+-------");

                void Split(string label, string markdown)
                {
                    var build = new List<double>(BuildSamples);
                    var assignFree = new List<double>(BuildSamples);
                    var assignTree = new List<double>(BuildSamples);
                    var layout = new List<double>(BuildSamples);

                    for (var i = 0; i < BuildSamples; i++)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var free = CodeWicket.UI.Markdown.MarkdownRenderFirewall.Render(
                            markdown, links, new List<string>());
                        build.Add(sw.Elapsed.TotalMilliseconds);

                        sw.Restart();
                        detached.Document = free;
                        assignFree.Add(sw.Elapsed.TotalMilliseconds);

                        var inTree = CodeWicket.UI.Markdown.MarkdownRenderFirewall.Render(
                            markdown, links, new List<string>());

                        sw.Restart();
                        attached.Document = inTree;
                        assignTree.Add(sw.Elapsed.TotalMilliseconds);

                        sw.Restart();
                        attached.UpdateLayout();
                        layout.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    report.AppendLine(
                        Inv($" {label,-34} | {Median(build),5:F1} | {Median(assignFree),15:F1} ")
                        + Inv($"| {Median(assignTree),14:F1} | {Median(layout),6:F1}"));
                }

                Split("file refs, resolved (field B)", withRefs);
                Split("code spans, no refs (field A)", InventoryCodeSpans());
                Split("prose only", InventoryProse());

                report.AppendLine();
                report.AppendLine("If ASSIGN IN TREE dwarfs ASSIGN DETACHED, the cost is resolving the document's");
                report.AppendLine("resource references against a live dictionary chain — which a document cache cannot");
                report.AppendLine("avoid, because a realisation re-attaches whatever it reuses. If both assigns are");
                report.AppendLine("cheap and LAYOUT carries it, the lever is text formatting and only realising fewer");
                report.AppendLine("items helps. Compare the total against render.log's buildMax, which spans build +");
                report.AppendLine("assign but NOT layout (layout is what that trace reports separately as frame).");
            }
            finally
            {
                host.Children.Remove(attached);
                attached.Document = null;
                detached.Document = null;
            }
        }

        /// <summary>
        /// The CHEAP field shape: an inventory dense in inline code spans and carrying no references —
        /// 24.5KB, 381 spans, measured at 61ms. The control for "many small styled inlines are not the
        /// thing that costs".
        /// </summary>
        private static string InventoryCodeSpans()
        {
            var text = new StringBuilder(
                "All seven sub-agents are back. Here is every tracked file, grouped by project.\n\n");
            for (var section = 0; section < 12; section++)
            {
                text.Append(Inv($"## Section {section}\n\n"));
                for (var item = 0; item < 11; item++)
                {
                    text.Append(Inv($"- `Item{section}_{item}` — uses `Namespace.Type{item}` and `Member{item}()`; "));
                    text.Append("standard implementation with the usual registration and disposal.\n");
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>
        /// The EXPENSIVE field shape: the same kind of inventory, but each bullet NAMES A FILE — 17.7KB,
        /// 258 spans, 105 references, measured at 821ms warm and repeatedly. Past
        /// <paramref name="referenceCount"/> a bullet keeps its shape and its code spans but names
        /// nothing resolvable, so the scaling block varies only the reference count.
        /// </summary>
        private static string InventoryWithReferences(List<string> names, int referenceCount)
        {
            var text = new StringBuilder(
                "Here is a complete inventory of every source file in the solution:\n\n---\n\n");
            for (var i = 0; i < names.Count; i++)
            {
                var subject = i < referenceCount ? names[i] : Inv($"Section{i} item");
                text.Append(Inv($"- `{subject}` — implements `Handler{i}` over `IService{i}`, "));
                text.Append("registered at startup and disposed with the scope.\n");
                if (i % 10 == 9)
                    text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>Same bulk, no inline code and no references: the floor the other two are read against.</summary>
        private static string InventoryProse()
        {
            var text = new StringBuilder();
            for (var i = 0; i < 106; i++)
            {
                text.Append(Inv($"- Item {i} implements the handler over the service interface, "));
                text.Append("registered at startup and disposed with the scope.\n");
            }
            return text.ToString();
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
                return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        /// <summary>
        /// The p-th percentile by nearest rank. A drag is felt as its worst frames, not its median, but a
        /// single max is one sample of noise — p90 is the "how often does it lurch" figure.
        /// </summary>
        private static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0)
                return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
            // Math.Clamp is net6+; Min/Max are in both BCLs. The empty case is already returned above, so
            // sorted.Count - 1 is never negative and the two orderings cannot disagree.
            return sorted[Math.Min(Math.Max(rank, 0), sorted.Count - 1)];
        }

        /// <summary>
        /// Invariant-culture interpolation, spelled the one way both TFMs have.
        ///
        /// <para>The harness builds its report with interpolated strings and must not let a comma-decimal
        /// locale into numbers meant to be compared across machines. On net10 that is
        /// <c>AppendLine(IFormatProvider, ...)</c> / <c>string.Create(IFormatProvider, ...)</c>, but both are
        /// interpolated-string-handler overloads added in net6 and neither exists in the net472 BCL — and
        /// net472 is the framework devenv runs, i.e. the one #86's sluggishness report is actually about.
        /// <see cref="FormattableString.Invariant"/> is in both, so the call sites stay identical on each.</para>
        ///
        /// <para>It allocates a <see cref="FormattableString"/> per call where the handler would not. That is
        /// deliberate and safe here: every caller is writing the REPORT, outside the <c>Measure*</c> methods
        /// that do the timing, so nothing this touches is on a measured path.</para>
        /// </summary>
        private static string Inv(FormattableString text) => FormattableString.Invariant(text);

        private static int[]? ParseSizes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var parsed = raw!.Split(',')
                .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1)
                .Where(v => v > 0)
                .ToArray();
            return parsed.Length > 0 ? parsed : null;
        }
    }
}
