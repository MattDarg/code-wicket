using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// Replays the frames a backend uses to tell the user something — through the REAL transport —
    /// and reports what the host would show (issues #208, #85).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because the three routes it covers cannot be triggered on demand.</b> A usage
    /// limit costs real money and a day's throttling; auto-compaction is gated on context size, which
    /// is roughly a million tokens to reach; and Kiro does not expose <c>/compact</c> over ACP at all
    /// (it offers three commands, and compaction is not among them). So a healthy session produces
    /// none of these frames, and "it looked fine" is what a broken build would look like too.
    /// </para>
    /// <para>
    /// <b>The frames below are captured bytes, not fixtures anyone invented.</b> Each is copied from a
    /// real <c>acp.log</c> with its provenance recorded beside it. That matters more than usual here:
    /// this whole area is about a backend's own words reaching the user, and a proof written against
    /// imagined frames would be the harness supplying the very thing under test.
    /// </para>
    /// <para>
    /// <b>And it is the transport that is under test, not the mapper.</b> The mapper has unit tests;
    /// what they cannot see is whether a handler is REGISTERED. That gap was real - a
    /// <c>prove-check</c> of the notify route came back PINS NOTHING, because the whole thing hangs on
    /// one <c>[JsonRpcMethod]</c> attribute string and removing it left every unit test green. The
    /// name is the known trap: v3's namespace is <c>_kiro/*</c> against the default engine's
    /// <c>_kiro.dev/*</c>, and a handler on the wrong one compiles, registers and never fires. Here
    /// the frames go over a real <see cref="JsonRpc"/> connection into a real
    /// <see cref="AcpClientTarget"/>, so a mis-registered method produces nothing and this says so.
    /// </para>
    /// </remarks>
    internal static class BackendNoticeReplayProof
    {
        private sealed record Case(
            string Label,
            string Provenance,
            string Method,
            string ParamsJson,
            bool ExpectNotice,
            string Expectation);

        // Every payload below is verbatim from a capture. Where one has been shortened it is only the
        // removal of frames around it, never a change to the frame itself.
        private static readonly Case[] Cases =
        {
            new(
                "rate limit (notify)",
                "acp.log 2026-09-04, the final frame before a 20-minute stall",
                "_kiro/system/notify",
                """
                {"level":"warning",
                 "message":"The selected model is experiencing high load, rate limiting applied. Consider switching models."}
                """,
                ExpectNotice: true,
                "shown as informational — a rate limit is a delay, and the backend called it a warning"),

            new(
                "monthly usage limit (display_error)",
                "acp.log 2026-09-02, six occurrences across six session logs, same request id",
                "session/update",
                """
                {"sessionId":"s1","update":{"sessionUpdate":"session_info_update","_meta":{"kiro":{
                    "displayError":{
                        "message":"You've reached your monthly usage limit. Please return next month to continue building.",
                        "errorType":"UsageLimitReachedError"},
                    "kind":"display_error",
                    "message":"You've reached your monthly usage limit. Please return next month to continue building."}}}}
                """,
                ExpectNotice: true,
                "shown as an error — the errorType rides as the level and the host escalates on it"),

            new(
                "compaction, started",
                "acp.log 2026-09-04, frame 2552, between readings of 80.03% and 20.00%",
                "session/update",
                """{"sessionId":"s1","update":{"sessionUpdate":"session_info_update","_meta":{"kiro":{"kind":"summarization_started"}}}}""",
                ExpectNotice: false,
                "SILENT on purpose — the pair is one event to a reader, and the started end is ~12ms of warning nobody can act on"),

            new(
                "compaction, completed",
                "acp.log 2026-09-04, frame 2553",
                "session/update",
                """{"sessionId":"s1","update":{"sessionUpdate":"session_info_update","_meta":{"kiro":{"kind":"summarization_completed"}}}}""",
                ExpectNotice: true,
                "shown — and it says what it MEANS, not that a job ran"),

            new(
                "an ordinary context reading",
                "acp.log 2026-09-04, the shape that carries the usage ring's update",
                "session/update",
                """{"sessionId":"s1","update":{"sessionUpdate":"session_info_update","_meta":{"kiro":{"kind":"context_usage","contextUsage":{"usagePercentage":42.0}}}}}""",
                ExpectNotice: false,
                "no notice — the control. Notices and the ring ride the same frame type, so a reader that announced this would announce every turn"),
        };

        public static async Task<int> RunAsync()
        {
            Console.WriteLine("== code-wicket console: backend notices, replayed over the real transport (issues #208, #85) ==");
            Console.WriteLine();
            Console.WriteLine("Each frame below is captured, not invented. What is under test is the TRANSPORT and the");
            Console.WriteLine("handler REGISTRATION — the mapper has unit tests, and they cannot see a missing attribute.");
            Console.WriteLine();

            var ok = true;
            foreach (var c in Cases)
            {
                var notices = await ReplayAsync(c.Method, c.ParamsJson).ConfigureAwait(false);
                var got = notices.Count > 0;
                var passed = got == c.ExpectNotice;
                ok &= passed;

                Console.WriteLine("-- " + c.Label + " --");
                Console.WriteLine("   from     : " + c.Provenance);
                Console.WriteLine("   method   : " + c.Method);
                Console.WriteLine("   expected : " + c.Expectation);
                if (got)
                {
                    foreach (var n in notices)
                    {
                        Console.WriteLine("   SHOWN    : [" + (n.Level ?? "no level") + "] " + One(n.Message));
                    }
                }
                else
                {
                    Console.WriteLine("   SHOWN    : (nothing)");
                }

                Console.WriteLine(passed ? "   => as expected" : "   => WRONG");
                Console.WriteLine();
            }

            Console.WriteLine(ok
                ? "PASS: every captured frame produced exactly what the host should show."
                : "FAIL: at least one captured frame was dropped, or announced when it should not have been.");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// Sends one frame from the AGENT side of a real JSON-RPC connection into a real
        /// <see cref="AcpClientTarget"/>, and returns the notices the host would receive.
        /// </summary>
        private static async Task<List<AgentEvent.BackendNotice>> ReplayAsync(string method, string paramsJson)
        {
            var events = new List<AgentEvent>();
            var (agentEnd, clientEnd) = FullDuplexStream.CreatePair();

            using var clientRpc = new JsonRpc(
                new NewLineDelimitedMessageHandler(clientEnd, clientEnd, Program.NewFormatter()));
            clientRpc.AddLocalRpcTarget(
                new AcpClientTarget(null!, e => { lock (events) events.Add(e); }, () => "s1"),
                new JsonRpcTargetOptions());
            clientRpc.StartListening();

            using var agentRpc = new JsonRpc(
                new NewLineDelimitedMessageHandler(agentEnd, agentEnd, Program.NewFormatter()));
            agentRpc.StartListening();

            await agentRpc.NotifyWithParameterObjectAsync(
                method, JsonDocument.Parse(paramsJson).RootElement).ConfigureAwait(false);

            // A notification is fire-and-forget, so there is nothing to await for its EFFECT. Poll to a
            // deadline rather than sleeping a fixed time: the expected-nothing cases have to wait out
            // the whole budget to mean anything, and the expected-something cases should not.
            //
            // What ends the wait is a NOTICE, not any event at all. One frame can raise several, and
            // stopping at the first of them left a notice that arrived behind another event unseen —
            // reported as "=> WRONG" against a host that had done exactly the right thing (#247).
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 2000)
            {
                lock (events)
                {
                    if (events.OfType<AgentEvent.BackendNotice>().Any())
                        break;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            lock (events)
                return events.OfType<AgentEvent.BackendNotice>().ToList();
        }

        // One line, bounded. The full text is what the user sees; this is the proof's own report.
        private static string One(string text)
        {
            var flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length > 140 ? flat.Substring(0, 140) + "…" : flat;
        }
    }
}
