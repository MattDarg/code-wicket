using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How render diagnostic lines reach the log (issue #86). Two properties, and both are about the
    /// WRITE rather than about the numbers: it must not happen on the caller's thread, and it must keep
    /// its order anyway.
    /// </summary>
    /// <remarks>
    /// The first is the defect this exists for. Callers are on the UI thread and
    /// <c>DiagnosticLog.AppendLine</c> opens, writes and closes a file per call under a process-wide
    /// lock, so its cost belongs to the machine — AV/EDR interception, LocalAppData redirected onto a
    /// share, VDI — which is precisely the environment this log is meant to investigate. Writing inline
    /// would have put machine-priced IO on the UI thread of the machine already reported as slow.
    /// <para>
    /// These share process-wide statics (<c>Sink</c>, the write queue, the once-per-process header), so
    /// they live in one class: xUnit runs classes in parallel and the tests within one in sequence.
    /// </para>
    /// </remarks>
    [Collection(RenderSinkCollection.Name)]
    public sealed class RenderDiagnosticsLogTests : IDisposable
    {
        public void Dispose()
        {
            RenderDiagnosticsLog.Flush();
            RenderDiagnosticsLog.Sink = null;
        }

        static MarkdownRenderEpisode Episode(int renders)
        {
            var cost = new MarkdownRenderCost();
            for (var i = 0; i < renders; i++)
                cost.NoteRender(new string('a', (i + 1) * 100), buildMs: 10, endedAtMs: 1000 + (i * 100));
            return cost.CloseIfIdle(nowMs: 99000)!;
        }

        [Fact]
        public void The_append_does_not_run_on_the_calling_thread()
        {
            // The sink blocks. If the write were inline, Write could not return until it was released —
            // so "the sink is still in there and we are out here" IS the property, and the thread ids
            // say it a second way.
            var inSink = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var sinkThread = 0;
            var callingThread = Environment.CurrentManagedThreadId;

            RenderDiagnosticsLog.Sink = _ =>
            {
                sinkThread = Environment.CurrentManagedThreadId;
                inSink.Set();
                release.Wait(10_000);
            };

            try
            {
                RenderDiagnosticsLog.Write("v1", Episode(renders: 4));

                Assert.True(inSink.Wait(10_000), "the queued write never ran");
                Assert.NotEqual(callingThread, sinkThread);
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public void Lines_keep_the_order_they_were_queued_in()
        {
            // A parallel task per line would race them into the file in an order that is not the order
            // they happened — which for a cost trace is worse than useless, since reading it means
            // reading a sequence. The environment header has to lead, too.
            var lines = new List<string>();
            RenderDiagnosticsLog.Sink = line =>
            {
                lock (lines)
                    lines.Add(line);
                // Uneven work: a later, faster line must still not overtake an earlier, slower one.
                Thread.Sleep(lines.Count % 3 == 0 ? 15 : 1);
            };

            for (var i = 0; i < 6; i++)
            {
                RenderDiagnosticsLog.Write("v" + i, Episode(renders: 3 + i));

                // A line from SOMEONE ELSE, mid-sequence. This is not hypothetical and not a
                // stress test: RenderDiagnosticsLog.Enabled is "a sink is installed", so every
                // product call site (MarkdownText, TranscriptItemsControl, DispatcherTrace) writes
                // into whatever list is current — and a UI test rendering markdown in a parallel
                // collection does exactly this. Injected deliberately so the tolerance is PINNED
                // rather than depending on the scheduler not overlapping the two, which is how this
                // check spent months failing roughly one run in four.
                if (i == 2)
                    RenderDiagnosticsLog.Write("someone-elses-viewer", Episode(renders: 3));
            }

            Assert.True(RenderDiagnosticsLog.Flush(10_000), "the queue did not drain");

            string[] written;
            lock (lines)
                written = lines.ToArray();

            // OURS, not everything the sink saw. RenderDiagnosticsLog.Enabled is "a sink is installed",
            // full stop — so while this test holds one, ANY concurrently-running test that renders a
            // markdown viewer writes into this list through the product's own call sites
            // (MarkdownText, TranscriptItemsControl, DispatcherTrace). Those classes never touch the
            // sink, so RenderSinkCollection cannot serialise them, and asserting on the raw count made
            // this fail intermittently with one extra line whenever the scheduler overlapped a UI class.
            //
            // Filtering costs nothing, because a foreign line INTERLEAVED does not violate the property
            // under test: what is pinned is that the lines this test queued keep the order it queued
            // them in, and that its header leads them. A stricter total was never measuring that.
            var mine = written
                .Where(l => l.StartsWith("[render-env] ", StringComparison.Ordinal) || IsOurs(l))
                .ToArray();

            Assert.StartsWith("[render-env] ", mine[0], StringComparison.Ordinal);
            Assert.Equal(7, mine.Length);                          // one header + six episodes
            Assert.Equal(
                Enumerable.Range(0, 6).Select(i => "v" + i).ToArray(),
                mine.Skip(1).Select(ViewerOf).ToArray());
        }

        [Fact]
        public void The_environment_header_is_written_once_per_sink()
        {
            var lines = new List<string>();
            RenderDiagnosticsLog.Sink = line => { lock (lines) lines.Add(line); };

            RenderDiagnosticsLog.Write("v1", Episode(renders: 3));
            RenderDiagnosticsLog.Write("v2", Episode(renders: 3));
            RenderDiagnosticsLog.Flush(10_000);

            lock (lines)
                Assert.Single(lines, l => l.StartsWith("[render-env]", StringComparison.Ordinal));
        }

        [Fact]
        public void Nothing_is_written_and_nothing_throws_without_a_sink()
        {
            // The default state, and the one every unit test and non-opted-in install is in: the log
            // writes nothing at all rather than defaulting itself on into the developer's log directory.
            RenderDiagnosticsLog.Sink = null;

            RenderDiagnosticsLog.Write("v1", Episode(renders: 5));

            Assert.True(RenderDiagnosticsLog.Flush(10_000));
        }

        // The six viewer ids this test writes under. A real viewer id is
        // RenderDiagnosticsLog.ViewerId's hex hash, so "v0".."v5" cannot collide with one.
        static readonly string[] OurViewers = Enumerable.Range(0, 6).Select(i => "v" + i).ToArray();

        static bool IsOurs(string line)
        {
            if (line.IndexOf("viewer=", StringComparison.Ordinal) < 0)
                return false;
            var viewer = ViewerOf(line);
            return Array.IndexOf(OurViewers, viewer) >= 0;
        }

        static string ViewerOf(string line)
        {
            var at = line.IndexOf("viewer=", StringComparison.Ordinal) + "viewer=".Length;
            var end = line.IndexOf(' ', at);
            return line.Substring(at, end - at);
        }
    }
}
