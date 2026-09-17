using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using CodeWicket.UI.Markdown;

namespace CodeWicket.UI.Diagnostics
{
    /// <summary>
    /// Times the UI thread's dispatcher operations and writes the <c>[dispatch]</c> trace (issue #86).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Installed by the HOST, never by itself</b>, and only where the render log is already on — the
    /// same rule as <see cref="RenderDiagnosticsLog"/>'s sink, and here it matters more: in devenv this
    /// hooks <i>Visual Studio's</i> dispatcher, not one of ours, so every operation any part of the IDE
    /// posts passes through it. That is the point — the question is what holds the UI thread during a
    /// drag, and the answer is not required to be our code — but it is also why it cannot be on by
    /// default, and why the per-operation work is kept to two clock reads and a dictionary bump.
    /// </para>
    /// <para>
    /// <b>Naming a callback needs a private field, so it is best-effort and cheap-first.</b>
    /// <see cref="DispatcherOperation"/> exposes its priority but not its delegate; the delegate is
    /// reachable only by reflection, which is why only operations over
    /// <see cref="DispatcherActivity.NameThresholdMs"/> are named and why every failure resolves to a
    /// bucket rather than an exception. If a future runtime renames the field the trace degrades to
    /// priorities alone — still enough to separate layout from formatting from input, which is the
    /// discrimination it was built for.
    /// </para>
    /// <para>
    /// <b>Self-time is tracked with a stack because operations nest.</b> An operation that pumps a nested
    /// dispatcher frame contains other operations in full, and charging it their time would push
    /// <c>busy</c> over the wall — precisely the impossible-number bug <c>MarkdownRenderCost</c> had to
    /// fix once already.
    /// </para>
    /// </remarks>
    public static class DispatcherTrace
    {
        private static readonly object Gate = new object();
        private static DispatcherHooks? _hooks;
        private static DispatcherActivity? _activity;
        private static DispatcherTimer? _flush;

        // The operations currently on the thread, innermost last, with the child time each has absorbed.
        private static readonly List<Frame> Stack = new List<Frame>();

        private sealed class Frame
        {
            internal DispatcherOperation? Operation;
            internal long Start;
            internal double ChildMs;

            /// <summary>
            /// Garbage-collection counts when this operation began, so a reported render pass can say
            /// whether a collection happened INSIDE it. A 2,114ms pass that realised nothing has to be
            /// explained by something other than content, and "the same stretch is smooth the first time
            /// and jerky the second" is the shape of accumulated state rather than of work.
            /// </summary>
            internal int Gen0;
            internal int Gen1;
            internal int Gen2;

            /// <summary>
            /// This operation is the trace's own flush tick. Its time still counts against its parent —
            /// the thread really was busy with it — but it must not be RECORDED as activity, or the
            /// instrument keeps resetting the idle clock it uses to decide the dispatcher has stopped,
            /// and no episode ever closes on silence.
            /// </summary>
            internal bool IsTrace;
        }

        /// <summary>
        /// Starts tracing <paramref name="dispatcher"/>. Idempotent; a second call is ignored so a host
        /// that re-opens its window cannot double-count every operation.
        /// </summary>
        public static void Install(Dispatcher? dispatcher)
        {
            if (dispatcher is null || !RenderDiagnosticsLog.Enabled)
                return;

            lock (Gate)
            {
                if (_hooks is not null)
                    return;

                _activity = new DispatcherActivity();
                _hooks = dispatcher.Hooks;
                _hooks.OperationStarted += OnStarted;
                _hooks.OperationCompleted += OnCompleted;
                _hooks.OperationAborted += OnCompleted;

                // The close is a timer for the same reason the other two traces' are: nothing on the
                // path can tell the last operation of a gesture from the next one not having arrived.
                _flush = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(DispatcherActivity.EpisodeGapMs),
                };
                _flush.Tick += (_, _) =>
                {
                    DispatcherEpisode? finished;
                    lock (Gate)
                    {
                        // The operation currently running IS this tick. Marking it here rather than
                        // trying to recognise it in the hook keeps the test cheap and exact.
                        if (Stack.Count > 0)
                            Stack[Stack.Count - 1].IsTrace = true;
                        finished = _activity?.CloseIfIdle(MarkdownRenderCost.NowMs());
                    }
                    Publish(finished);
                };
                _flush.Start();
            }
        }

        /// <summary>Stops tracing and drops the hooks, for a host tearing its window down.</summary>
        public static void Uninstall()
        {
            lock (Gate)
            {
                if (_hooks is null)
                    return;
                _hooks.OperationStarted -= OnStarted;
                _hooks.OperationCompleted -= OnCompleted;
                _hooks.OperationAborted -= OnCompleted;
                _hooks = null;
                _flush?.Stop();
                _flush = null;
                _activity = null;
                Stack.Clear();
            }
        }

        /// <summary>Diagnostics for the diagnostic: how far the hook chain actually got. Read by
        /// <c>--smoke</c>, which otherwise cannot tell "no operations" from "never subscribed".</summary>
        internal static int StartedCount;
        internal static int CompletedCount;
        internal static int NotedCount;

        private static void OnStarted(object? sender, DispatcherHookEventArgs e)
        {
            lock (Gate)
            {
                StartedCount++;
                Stack.Add(new Frame
                {
                    Operation = e.Operation,
                    Start = Stopwatch.GetTimestamp(),
                    Gen0 = GC.CollectionCount(0),
                    Gen1 = GC.CollectionCount(1),
                    Gen2 = GC.CollectionCount(2),
                });
            }
        }

        private static void OnCompleted(object? sender, DispatcherHookEventArgs e)
        {
            DispatcherEpisode? closed = null;
            string? renderPass = null;
            var renderPassMs = -1.0;
            try
            {
                var now = Stopwatch.GetTimestamp();
                lock (Gate)
                {
                    CompletedCount++;
                    var index = IndexOf(e.Operation);
                    if (index < 0)
                        return; // started before the hooks went on; not ours to attribute

                    var frame = Stack[index];
                    Stack.RemoveRange(index, Stack.Count - index);

                    var elapsed = MarkdownRenderCost.ToMs(now - frame.Start);
                    var self = elapsed - frame.ChildMs;
                    if (self < 0)
                        self = 0;

                    // Charge the whole span to the parent's children, not just our self-time: the parent
                    // was blocked for all of it.
                    if (Stack.Count > 0)
                        Stack[Stack.Count - 1].ChildMs += elapsed;

                    if (frame.IsTrace)
                        return;

                    var priority = PriorityOf(e.Operation);
                    // Render-priority operations are ALWAYS named, however cheap, because a render pass
                    // has to drain the realisation counter whether or not it is worth reporting — see
                    // RealisationsSinceRender on why draining only the slow ones would make the number
                    // mean something else. The name is cached per method, so the extra cost is one field
                    // read and a dictionary hit on ~120 operations a second.
                    var callback = self >= DispatcherActivity.NameThresholdMs || priority == "Render"
                        ? CallbackOf(e.Operation)
                        : null;
                    if (callback is not null && callback.StartsWith("MediaContext.", StringComparison.Ordinal)
                        && callback.EndsWith("RenderMessageHandler", StringComparison.Ordinal))
                    {
                        // Decide FIRST, then drain: a pass too cheap to report folds its counts forward
                        // instead of dropping them, so the drain has to know which it is.
                        renderPassMs = self >= RealisationsSinceRender.ReportPassMs ? self : -1;
                        renderPass = RealisationsSinceRender.Drain(renderPassMs >= 0);
                        if (renderPass is not null)
                        {
                            // Collections that happened INSIDE this pass, and the heap it ran against.
                            // Read here rather than at the write below so a slow queue cannot smear the
                            // heap reading onto a different moment.
                            renderPass += " gc=" + Int(GC.CollectionCount(0) - frame.Gen0)
                                + "/" + Int(GC.CollectionCount(1) - frame.Gen1)
                                + "/" + Int(GC.CollectionCount(2) - frame.Gen2)
                                + " heapMb=" + (GC.GetTotalMemory(false) / (1024.0 * 1024.0))
                                    .ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    NotedCount++;
                    // The frame's own start, NOT the completion less self-time: a pumping operation's
                    // span includes its children, and the episode has to cover the span or its wall can
                    // begin after work it is charged for (the duty=1301.5% in the field log).
                    closed = _activity?.Note(
                        priority, callback, self, MarkdownRenderCost.ToMs(now), MarkdownRenderCost.ToMs(frame.Start));
                }
            }
            catch { /* a diagnostic must never break the dispatcher it is watching */ }

            // Outside the lock, like Publish: the sink writes on a pool thread but the formatting and
            // the queueing are the caller's, and neither belongs under a lock the hooks contend on.
            // Drain returns non-null only for a pass it was told to report, so this is one condition now.
            if (renderPass is not null)
            {
                RenderDiagnosticsLog.WriteLine(
                    "[render-pass] ms=" + renderPassMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    + " " + renderPass);
            }

            Publish(closed);
        }

        private static string Int(int value) =>
            value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static int IndexOf(DispatcherOperation operation)
        {
            for (var i = Stack.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Stack[i].Operation, operation))
                    return i;
            }
            return -1;
        }

        private static void Publish(DispatcherEpisode? episode)
        {
            if (episode is not null)
                RenderDiagnosticsLog.WriteLine(episode.Format());
        }

        private static string PriorityOf(DispatcherOperation operation)
        {
            try { return operation.Priority.ToString(); }
            catch { return "?"; }
        }

        // The delegate lives in a private field; DispatcherOperation exposes no public way to it.
        private static readonly FieldInfo? MethodField =
            typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly Dictionary<MethodInfo, string> Names = new Dictionary<MethodInfo, string>();

        /// <summary>
        /// A name for the operation's callback, or null if this runtime does not let us reach it.
        /// Resolved once per method — a hot callback is hot precisely because it recurs.
        /// </summary>
        private static string? CallbackOf(DispatcherOperation operation)
        {
            try
            {
                if (MethodField?.GetValue(operation) is not Delegate callback)
                    return null;

                var method = callback.Method;
                if (Names.TryGetValue(method, out var known))
                    return known;

                // The declaring type is the useful half: a lambda's own name is compiler noise, while
                // its type names the class that posted the work — which is the answer being looked for.
                var declaring = method.DeclaringType;
                var owner = declaring?.Name ?? "?";
                if (owner.Length > 0 && owner[0] == '<' && declaring?.DeclaringType is not null)
                    owner = declaring.DeclaringType.Name; // compiler-generated closure: name its owner

                var name = owner + "." + method.Name;
                Names[method] = name;
                return name;
            }
            catch
            {
                return null;
            }
        }
    }
}
