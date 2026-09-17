using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CodeWicket.Core;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Pins <see cref="StartupTimeline"/>, whose whole reason for existing is a failure mode the
    /// ordinary logging path cannot record: a startup phase that never returns, on the thread that
    /// would have written the line.
    ///
    /// So the load-bearing assertions here are the ones that hold <b>while the calling thread is
    /// blocked</b> — <see cref="APhaseThatNeverReturnsIsNamedWhileItsCallerIsBlocked"/> and
    /// <see cref="TheStallReportRepeatsForAsLongAsThePhaseStaysOpen"/>. Remove the timer from
    /// <c>Begin</c> and those two fail while every other test here still passes, which is the
    /// difference between this type and a stopwatch.
    ///
    /// The rest guard the ways an always-on watchdog could become noise or a leak: it must go quiet on
    /// <c>End</c>, go quiet on <c>Dispose</c> (the path a phase that THREW takes, which is the only
    /// reason the type is disposable), and follow the phase name rather than the first one it saw.
    ///
    /// Timing-based by necessity. Positive assertions poll with a generous ceiling so a loaded build
    /// agent doesn't fail them; negative ones wait a fixed ten intervals, which is the trade this kind
    /// of test always makes.
    /// </summary>
    public sealed class StartupTimelineTests
    {
        /// <summary>Stall interval for the tests — the real one is five seconds.</summary>
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

        /// <summary>How long a negative assertion waits before declaring the watchdog silent.</summary>
        private static readonly TimeSpan TenIntervals = TimeSpan.FromMilliseconds(400);

        // Thread-safe by construction: the point of this type is that lines arrive from a pool thread
        // while the test's own thread is asleep, so an unsynchronised List would be a race in the
        // harness rather than in the code under test.
        private sealed class Sink
        {
            private readonly object _gate = new object();
            private readonly List<string> _lines = new List<string>();

            public void Write(string line)
            {
                lock (_gate) _lines.Add(line);
            }

            public IReadOnlyList<string> Lines
            {
                get { lock (_gate) return _lines.ToArray(); }
            }

            public int Stalls(string phase) =>
                Lines.Count(l => l.Contains(phase + ": still running after", StringComparison.Ordinal));

            /// <summary>Polls until <paramref name="predicate"/> holds, or gives up and returns false.</summary>
            public bool WaitFor(Func<IReadOnlyList<string>, bool> predicate, int timeoutMs = 5000)
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < timeoutMs)
                {
                    if (predicate(Lines))
                        return true;
                    Thread.Sleep(10);
                }
                return predicate(Lines);
            }
        }

        private static StartupTimeline Timeline(Sink sink) =>
            new StartupTimeline(sink.Write, "startup", Interval);

        // -- the reason the type exists -------------------------------------------------------

        [Fact]
        public void APhaseThatNeverReturnsIsNamedWhileItsCallerIsBlocked()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("ide");

            // The caller never re-enters the timeline from here — exactly what a wedged UI thread does.
            // Anything in the sink was therefore written by something other than the thread running the
            // phase, which is the only way this failure can ever be recorded.
            //
            // POLLED, not slept, and this file's own summary already says why: positive assertions take
            // a generous ceiling, negative ones wait a fixed ten intervals. This was the lone positive
            // assertion still sleeping, and it flaked for exactly that reason — a fixed window asserts
            // the watchdog fired inside ONE interval's worth of scheduling, and a loaded machine starves
            // the timer thread for longer than that. Measured 2026-08-26: 2 failures in 6 runs under a
            // concurrent gate matrix, with nothing wrong with the code under test.
            //
            // The ceiling cannot weaken the claim. The poll reads the sink and never drives the
            // timeline, so the line still has to arrive from another thread — which is the whole
            // assertion. What it drops is the incidental extra claim that the watchdog is PROMPT, which
            // this test never meant to make and which TheStallReportCarriesHowLongThePhaseHasBeenRunning
            // covers properly by reading the reported duration.
            Assert.True(sink.WaitFor(_ => sink.Stalls("ide") > 0),
                "a phase still open after the stall interval must be named, without its caller yielding");
        }

        [Fact]
        public void TheStallReportRepeatsForAsLongAsThePhaseStaysOpen()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("ide");

            // Repetition is what separates "slow, then finished" from "never came back", and it is what
            // bounds the hang in the log when the process is killed rather than recovering.
            Assert.True(sink.WaitFor(_ => sink.Stalls("ide") >= 3),
                "the watchdog must keep reporting, not warn once");
        }

        [Fact]
        public void TheStallReportCarriesHowLongThePhaseHasBeenRunning()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("engine");
            Assert.True(sink.WaitFor(_ => sink.Stalls("engine") > 0));

            var line = sink.Lines.First(l => l.Contains("still running after", StringComparison.Ordinal));
            Assert.StartsWith("[startup] engine: still running after ", line, StringComparison.Ordinal);
            Assert.EndsWith(" ms", line, StringComparison.Ordinal);
        }

        // -- and the ways an always-on watchdog could become noise ----------------------------

        /// <summary>
        /// Note this pins the PROPERTY, not either mechanism: the implementation both disposes the
        /// timer and clears the phase, and each alone is enough to keep the sink quiet, so removing
        /// one leaves this passing. Removing both fails it. That redundancy is deliberate — see
        /// <c>ReportStall</c> — so a test that could only be satisfied one way would be pinning an
        /// implementation choice rather than the behaviour.
        /// </summary>
        [Fact]
        public void EndSilencesTheWatchdog()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("ide");
            timeline.End();

            Thread.Sleep(TenIntervals);

            Assert.Equal(0, sink.Stalls("ide"));
        }

        [Fact]
        public void DisposeSilencesAWatchdogWhosePhaseNeverEnded()
        {
            var sink = new Sink();
            var timeline = Timeline(sink);

            // The path a phase that THREW takes: no End is ever reached, and the host's catch block
            // disposes the timeline on its way to showing the failure.
            timeline.Begin("ide");
            timeline.Dispose();

            Thread.Sleep(TenIntervals);

            Assert.Equal(0, sink.Stalls("ide"));
        }

        [Fact]
        public void TheWatchdogFollowsTheCurrentPhaseNotTheFirstOne()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("ide");
            timeline.Begin("engine");

            Assert.True(sink.WaitFor(_ => sink.Stalls("engine") > 0));
            Assert.Equal(0, sink.Stalls("ide"));
        }

        // -- the ordinary path ----------------------------------------------------------------

        [Fact]
        public void EachPhaseIsReportedWhenItEnds()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("config");
            timeline.End();
            timeline.Begin("ide");
            timeline.End();

            var lines = sink.Lines;
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("[startup] config: ", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("[startup] ide: ", lines[1], StringComparison.Ordinal);
            Assert.All(lines, l => Assert.EndsWith(" ms", l, StringComparison.Ordinal));
        }

        [Fact]
        public void APhaseIsWrittenAsItEndsRatherThanSummarisedAtTheFinish()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("config");
            timeline.End();

            // The line is already there — no Total, no Dispose. A timeline that only reported at the
            // finish would have nothing to show for a startup that never reaches one.
            Assert.Single(sink.Lines);
        }

        [Fact]
        public void DetailIsAppendedForPhasesWhoseSizeExplainsTheirCost()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Begin("file index");
            timeline.End("3412 files");

            Assert.EndsWith(" ms (3412 files)", sink.Lines[0], StringComparison.Ordinal);
        }

        [Fact]
        public void TheTotalLineIsMarkedAsOne()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.Total("window ready");

            Assert.StartsWith("[startup] window ready: ", sink.Lines[0], StringComparison.Ordinal);
            Assert.EndsWith(" ms total", sink.Lines[0], StringComparison.Ordinal);
        }

        [Fact]
        public void TheLabelPrefixesEveryLineSoOneGrepCoversTheSubsystem()
        {
            var sink = new Sink();
            using var timeline = new StartupTimeline(sink.Write, "engine-start", Interval);

            timeline.Begin("providers");
            timeline.End();
            timeline.Total("ready");

            Assert.All(sink.Lines, l => Assert.StartsWith("[engine-start] ", l, StringComparison.Ordinal));
        }

        // -- best-effort ----------------------------------------------------------------------

        [Fact]
        public void ASinkThatThrowsNeverReachesTheCaller()
        {
            using var timeline = new StartupTimeline(_ => throw new InvalidOperationException("log is gone"),
                "startup", Interval);

            timeline.Begin("ide");
            timeline.End();
            timeline.Total("window ready");

            // Also from the watchdog's own thread, where an escaped exception would fault a pool
            // thread rather than merely losing a line.
            timeline.Begin("engine");
            Thread.Sleep(TenIntervals);
        }

        [Fact]
        public void WithNoSinkTheTimelineIsInert()
        {
            using var timeline = new StartupTimeline(null, "startup", Interval);

            timeline.Begin("ide");
            Thread.Sleep(TenIntervals);
            timeline.End();
            timeline.Total("window ready");

            Assert.True(timeline.Elapsed > TimeSpan.Zero);
        }

        [Fact]
        public void AnEndWithNoOpenPhaseIsIgnored()
        {
            var sink = new Sink();
            using var timeline = Timeline(sink);

            timeline.End();
            timeline.Begin("ide");
            timeline.End();
            timeline.End();

            Assert.Single(sink.Lines);
        }

        [Fact]
        public void UseAfterDisposeIsSilentRatherThanFatal()
        {
            var sink = new Sink();
            var timeline = Timeline(sink);
            timeline.Dispose();

            timeline.Begin("ide");
            Thread.Sleep(TenIntervals);
            timeline.End();

            Assert.Equal(0, sink.Stalls("ide"));
        }
    }
}
