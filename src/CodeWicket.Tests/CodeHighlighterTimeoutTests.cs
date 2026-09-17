using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Documents;
using Markdig;
using Markdig.Syntax;
using CodeWicket.UI.Markdown;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #177: a 570-character truncated JSON block in an agent's reply froze Visual Studio
    /// outright. ColorCode's JSON master pattern nests quantifiers over the same character class
    /// (<c>[^"\\]*</c> both before and inside the repeating group), so a string literal with no
    /// closing quote has exponentially many ways to be split and no way to match — and ColorCode
    /// compiles that pattern with <c>new Regex(pattern)</c>, carrying no match timeout.
    ///
    /// <para><b>Every assertion about the hang here is a TIMING one, and it has to be.</b> The guard
    /// that was supposed to cover this input class is <c>CodeHighlighter</c>'s own <c>catch</c>,
    /// whose comment names it exactly ("runs on every streamed delta, over incomplete code") — and
    /// a hang raises nothing, so a <c>catch</c> is structurally unable to see it. "It didn't throw"
    /// is the one thing that was already true while the bug was live.</para>
    ///
    /// <para>The bodies therefore run through <see cref="StaTest.RunWithin"/> rather than
    /// <see cref="StaTest.Run"/>: with the fix removed these do not fail, they never return, and a
    /// suite that hangs tells <c>prove-check.ps1</c> nothing.</para>
    /// </summary>
    public class CodeHighlighterTimeoutTests
    {
        /// <summary>
        /// The reported payload, verbatim from the debugger's <c>runtext</c> field (company paths
        /// replaced with "acmecorp" placeholders by the reporter; the structure and escape depth,
        /// which are what the regex chokes on, are preserved). It is one line: the <c>\n</c> are
        /// literal two-character escapes inside a JSON string value, not line breaks.
        ///
        /// <para>It ends mid-token, at <c>\\\"/acme</c> — the truncation is the whole point. Do not
        /// "fix" the JSON: valid JSON tokenizes in about two milliseconds and pins nothing.</para>
        /// </summary>
        private const string TruncatedJson =
            """{"\n  \"template-name\": \"IAM_SERVICE_ROLE\",\n  \"template-version\": \"1.1.0-PROD\",""" +
            """\n  \"name\": \"AWS Dev Console Role\",\n  \"state-id\": \"aws-console-role-id\",""" +
            """\n  \"parameters\": {\n    \"ROLE_TYPE\": \"console\",\n    \"IAM_POLICIES\": \"[""" +
            """\n      \\\"/acmecorp/default/console-compute\\\",""" +
            """\n      \\\"/acmecorp/default/console-compute-ec2\\\",""" +
            """\n      \\\"/acmecorp/default/console-database\\\",""" +
            """\n      \\\"/acmecorp/default/console-security\\\",""" +
            """\n      \\\"/acmecorp/default/console-management\\\",""" +
            """\n      \\\"/acmecorp/default/console-messaging\\\",""" +
            """\n      \\\"/acmecorp/default/console-storage\\\",""" +
            """\n      \\\"/acme""";

        /// <summary>
        /// Generous next to <see cref="CodeHighlighter.TokenizeTimeout"/> (50ms) and next to a
        /// legitimate tokenize (well under a millisecond), and still nothing at all next to the
        /// unbounded case — the reported payload was measured running for over twenty seconds with
        /// no sign of stopping, and the user's IDE never recovered.
        /// </summary>
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How far under <see cref="CodeHighlighter.TokenizeTimeout"/> a timed-out tokenize is
        /// allowed to measure. See the assertion that spends it for why it is this size.
        /// </summary>
        private static readonly TimeSpan DeadlineTolerance = TimeSpan.FromMilliseconds(10);

        // -- the reason it exists ----------------------------------------------------------------

        [Fact]
        public void TruncatedJsonTokenizeTerminates()
        {
            var runs = new List<string>();

            var finished = StaTest.RunWithin(() => runs.AddRange(Highlight(TruncatedJson, "json")), Budget);

            Assert.True(finished, $"Tokenizing the reported payload did not return within {Budget}.");

            // The designed fallback: one plain Run holding the code verbatim, indistinguishable from
            // an untagged fence. Asserted as well as the timing because a timeout that returned
            // half a document, or dropped the text, would also "terminate".
            Assert.Equal(TruncatedJson, Assert.Single(runs));
        }

        [Fact]
        public void TruncatedJsonInAClosedFenceRendersThroughTheRealMarkdownPath()
        {
            // The stack in the report, end to end: MarkdownText -> firewall -> flow renderer ->
            // RenderCodeBlock -> CodeHighlighter. The fence is CLOSED, so skipping unclosed fences
            // does not cover this one - the timeout is the only thing standing between this input
            // and a frozen IDE, which is why this case is asserted separately from the one below.
            string? text = null;

            var finished = StaTest.RunWithin(
                () =>
                {
                    CodeHighlighter.ClearCacheForTests();
                    var document = MarkdownRenderFirewall.Render("```json\n" + TruncatedJson + "\n```");
                    text = new TextRange(document.ContentStart, document.ContentEnd).Text;
                },
                Budget);

            Assert.True(finished, $"Rendering a closed truncated-JSON fence did not return within {Budget}.");
            Assert.Contains("IAM_SERVICE_ROLE", text!, StringComparison.Ordinal);
        }

        [Fact]
        public void AFenceIsNotHighlightedUntilItCloses()
        {
            // A fence with no closing marker is not highlighted. While a reply streams, the arriving
            // block is unterminated on EVERY one of MarkdownText's ~10-a-second rebuilds, so without
            // this the syntactically incomplete text is re-fed to the tokenizer on every tick — and
            // it can never be answered from the cache, because the text differs each time. A timeout
            // alone would turn the hang into a sustained UI-thread tax rather than removing it.
            //
            // Asserted on WELL-FORMED json, and that is the whole design of this test. The reported
            // payload cannot discriminate here: it falls back to a single plain Run whether it was
            // skipped or tokenized-then-timed-out, so a check written on it passes over the bug
            // (measured — prove-check reported PINS NOTHING). Valid JSON separates the two outright,
            // because the only way to get several Runs is for the tokenizer to have run.
            const string Json = """{ "name": "value", "count": 42 }""";
            var whileStreaming = new List<string>();
            var onceSettled = new List<string>();

            Assert.True(StaTest.RunWithin(
                () =>
                {
                    CodeHighlighter.ClearCacheForTests();
                    whileStreaming.AddRange(CodeRunTexts(
                        MarkdownRenderFirewall.Render("```json\n" + Json), Json));
                    onceSettled.AddRange(CodeRunTexts(
                        MarkdownRenderFirewall.Render("```json\n" + Json + "\n```"), Json));
                },
                Budget));

            // Both directions, so this cannot pass by highlighting nothing ever.
            Assert.Equal(Json, Assert.Single(whileStreaming));
            Assert.True(
                onceSettled.Count > 1,
                $"A closed fence must highlight; got {onceSettled.Count} Run(s).");
        }

        [Fact]
        public void TruncatedJsonInAStreamingFenceReturnsImmediately()
        {
            var runs = new List<string>();

            var finished = StaTest.RunWithin(
                () =>
                {
                    CodeHighlighter.ClearCacheForTests();
                    runs.AddRange(CodeRunTexts(
                        MarkdownRenderFirewall.Render("```json\n" + TruncatedJson), "IAM_SERVICE_ROLE"));
                },
                Budget);

            Assert.True(finished, $"Rendering an unclosed truncated-JSON fence did not return within {Budget}.");
            Assert.Single(runs);
        }

        // -- the premise the fence rule rests on -------------------------------------------------

        [Fact]
        public void MarkdigReportsAClosingFenceCountOnlyWhenTheFenceIsClosed()
        {
            // RenderCodeBlock decides "still streaming" from ClosingFencedCharCount, so what the
            // PINNED Markdig actually puts there is load-bearing. Block.IsOpen is NOT it - everything
            // is closed by the time Parse returns, which is the available wrong answer here.
            Assert.Equal(0, ClosingFenceCount("```json\n{\"a\": 1"));
            Assert.True(ClosingFenceCount("```json\n{\"a\": 1}\n```") > 0);

            // The count reports a REAL closing marker, not "the parser closed this block off", so a
            // fence nested in a container is not mistaken for an unfinished one when the container
            // ends. Checked because the two readings coincide at the top level and diverge only
            // here: were the nested cases reporting 0, highlighting would have gone quietly missing
            // inside every blockquote and list item, with the tests above still green.
            Assert.True(ClosingFenceCount("> ```json\n> {\"a\": 1}\n> ```") > 0);
            Assert.True(ClosingFenceCount("- item\n  ```json\n  {\"a\": 1}\n  ```") > 0);
            Assert.Equal(0, ClosingFenceCount("> ```json\n> {\"a\": 1"));
            Assert.Equal(0, ClosingFenceCount("- item\n  ```json\n  {\"a\": 1"));

            // Tilde fences are the other spelling of the same block, and the agent does emit them.
            Assert.True(ClosingFenceCount("~~~json\n{\"a\": 1}\n~~~") > 0);
        }

        // -- what must not have changed ----------------------------------------------------------

        [Fact]
        public void WellFormedJsonStillHighlights()
        {
            // The timeout must bound the pathological case without touching the ordinary one. More
            // than one Run proves the tokenizer engaged rather than falling back, and the round-trip
            // proves it did not lose or reorder any of the text on the way.
            const string Json = """{ "name": "value", "count": 42, "ok": true, "none": null }""";
            var runs = new List<string>();

            Assert.True(StaTest.RunWithin(() => runs.AddRange(Highlight(Json, "json")), Budget));

            Assert.True(runs.Count > 1, $"Expected JSON to tokenize into several Runs, got {runs.Count}.");
            Assert.Equal(Json, string.Concat(runs));
        }

        [Fact]
        public void AnEmbeddedLanguageStillResolves()
        {
            // Supplying our own ILanguageParser means supplying the ILanguageRepository it uses to
            // resolve a language nested inside another - the JavaScript in an HTML <script>. Get that
            // wrong and embedded highlighting degrades silently, with every other test here green.
            var runs = new List<string>();

            Assert.True(StaTest.RunWithin(
                () => runs.AddRange(Highlight("<p>hi</p><script>var x = 42;</script>", "html")),
                Budget));

            // "var" is a JavaScript keyword and nothing in the HTML grammar produces it, so an
            // isolated run of it can only have come from the embedded language being resolved.
            Assert.Contains("var", runs);
        }

        [Fact]
        public void ABlockThatCannotTokenizePaysTheTimeoutOnceAndNotPerRender()
        {
            // The tax the cache exists to remove. A timeout ALONE turns issue #177's hang into a
            // sustained cost instead: MarkdownText re-renders the whole message on every throttled
            // tick, so a block that can never tokenize would spend TokenizeTimeout again on every
            // one of them — 50ms against a 100ms interval is half the UI thread, indefinitely.
            //
            // Measured on the failure path deliberately, because that is where the cache is worth
            // something and where the margin is unmistakable: the first call pays the full deadline,
            // a cached one pays nothing. Timing an ordinary block instead would measure mostly Run
            // construction, which is not cached and cannot be (each render needs its own Runs).
            var first = TimeSpan.Zero;
            var repeat = TimeSpan.Zero;

            Assert.True(StaTest.RunWithin(
                () =>
                {
                    CodeHighlighter.ClearCacheForTests();

                    var cold = Stopwatch.StartNew();
                    CodeHighlighter.AppendTo(new Paragraph().Inlines, TruncatedJson, "json");
                    first = cold.Elapsed;

                    var warm = Stopwatch.StartNew();
                    for (var i = 0; i < 10; i++)
                        CodeHighlighter.AppendTo(new Paragraph().Inlines, TruncatedJson, "json");
                    repeat = warm.Elapsed;
                },
                Budget));

            // Ten repeats must cost less than the single uncached one. Uncached they cost ten times
            // it, so the margin is an order of magnitude and no timing slack can close it.
            Assert.True(
                repeat < first,
                $"Ten repeats took {repeat.TotalMilliseconds:F1}ms against {first.TotalMilliseconds:F1}ms for the first — the failure is not being cached.");

            // The first call really did pay the deadline, so the comparison above is against the
            // real cost and not against a tokenize that happened to be fast today.
            //
            // Against the deadline MINUS a tolerance, because a match timeout is not a promise
            // about elapsed time on somebody else's clock: Regex polls its own at intervals
            // rather than checking continuously, so a Stopwatch started outside the call can read a
            // little under it. Measured 47.5ms against the 50ms deadline inside a full `dotnet
            // test` run and passed on its own moments later, so the flake was in this assertion and
            // never in the cache it guards. A standalone probe — 40 catastrophic matches under a
            // 50ms deadline — put the worst undershoot at 0.5ms on an idle machine, which is the
            // point: the margin moves with load, and there is no honest exact figure to assert.
            //
            // It can be this generous because the tolerance costs the check nothing. What the
            // assertion has to exclude is the block being SKIPPED, or tokenizing cleanly — both
            // sub-millisecond. 40ms against ~0ms is the same verdict 50ms against ~0ms was, so
            // tightening it back up would buy no discrimination and re-arm the flake.
            var floor = CodeHighlighter.TokenizeTimeout - DeadlineTolerance;
            Assert.True(
                first >= floor,
                $"Expected the uncached call to spend at least {floor.TotalMilliseconds}ms (the {CodeHighlighter.TokenizeTimeout.TotalMilliseconds}ms deadline less a {DeadlineTolerance.TotalMilliseconds}ms tolerance), took {first.TotalMilliseconds:F1}ms.");
        }

        // -- the render is bounded too (pre-release security review, September 2026) --------------------

        /// <summary>
        /// The cache above bounds a BLOCK's lifetime cost; this pins that a pane of ordinary blocks
        /// cannot undo it. With one dictionary cleared at capacity, sixty-four successes emptied the
        /// cache under the failure, and the deadline was paid again on the next rebuild - for exactly
        /// the input the cache was built for. Failures now live apart from successes.
        /// </summary>
        [Fact]
        public void ACachedFailureSurvivesAPaneFullOfSuccesses()
        {
            var repeat = TimeSpan.Zero;

            Assert.True(StaTest.RunWithin(
                () =>
                {
                    CodeHighlighter.ClearCacheForTests();
                    CodeHighlighter.AppendTo(new Paragraph().Inlines, TruncatedJson, "json"); // pays the deadline once

                    // More ordinary blocks than the success cache holds.
                    for (var i = 0; i < 80; i++)
                        CodeHighlighter.AppendTo(new Paragraph().Inlines, "{ \"n\": " + i + " }", "json");

                    var warm = Stopwatch.StartNew();
                    CodeHighlighter.AppendTo(new Paragraph().Inlines, TruncatedJson, "json");
                    repeat = warm.Elapsed;
                },
                Budget));

            // A hit is sub-millisecond; a re-paid deadline is at least the floor the test above
            // asserts. Half the deadline separates the two under any load this suite runs at.
            var ceiling = TimeSpan.FromTicks(CodeHighlighter.TokenizeTimeout.Ticks / 2);
            Assert.True(
                repeat < ceiling,
                $"The failure was re-paid after 80 successes: {repeat.TotalMilliseconds:F1}ms against a {ceiling.TotalMilliseconds}ms ceiling for a cache hit.");
        }

        /// <summary>
        /// Neither the deadline nor the cache bounds a RENDER: N distinct blocks that cannot tokenize
        /// cost N deadlines on the first render. The document budget does - at most two deadlines'
        /// worth of tokenizing per render, the rest plain and tried next time - and because each
        /// render caches what it tried, the document converges to free.
        /// <para>Timing, like every check in this class, and a bounded render is a small number
        /// against an unbounded one: sixteen blocks are at least 800ms unbounded and about 170ms
        /// bounded. The ceiling sits between with room for a cold JIT and a busy machine; where the
        /// subject is a bound rather than a hang, the verifier of record is <c>run-gates.ps1</c>,
        /// which runs this contended.</para>
        /// </summary>
        [Fact]
        public void ADocumentOfBlocksThatCannotTokenizeIsBoundedPerRenderAndConverges()
        {
            const int blocks = 16;
            var markdown = string.Concat(Enumerable.Range(0, blocks).Select(i =>
                "```json\n" + TruncatedJson + "-variant-" + i + "\n```\n\n"));

            var first = TimeSpan.Zero;
            var settled = TimeSpan.MaxValue;
            var renders = 0;

            Assert.True(StaTest.RunWithin(
                () =>
                {
                    // Warm the pipeline (Markdig, WPF documents, the compiled grammar) so the first
                    // timed render measures tokenizing and not JIT.
                    MarkdownRenderFirewall.Render("```json\n{ \"warm\": true }\n```\n\nplain text");
                    CodeHighlighter.ClearCacheForTests();

                    var sw = Stopwatch.StartNew();
                    MarkdownRenderFirewall.Render(markdown);
                    first = sw.Elapsed;

                    for (renders = 1; renders <= 40; renders++)
                    {
                        sw.Restart();
                        MarkdownRenderFirewall.Render(markdown);
                        settled = sw.Elapsed;
                        if (settled < CodeHighlighter.TokenizeTimeout)
                            break;
                    }
                },
                TimeSpan.FromSeconds(30)));

            // Budget + the one deadline that can overshoot it + Run construction for sixteen blocks.
            var ceiling = CodeHighlighter.DocumentTokenizeBudget + TimeSpan.FromTicks(CodeHighlighter.TokenizeTimeout.Ticks * 4);
            Assert.True(
                first < ceiling,
                $"The first render of {blocks} pathological blocks took {first.TotalMilliseconds:F1}ms against a {ceiling.TotalMilliseconds}ms ceiling — the render is not bounded.");
            Assert.True(
                settled < CodeHighlighter.TokenizeTimeout,
                $"After {renders} renders a render still costs {settled.TotalMilliseconds:F1}ms — the document is not converging to cached.");
        }

        // -- helpers -----------------------------------------------------------------------------

        private static int ClosingFenceCount(string markdown) =>
            Markdig.Markdown.Parse(markdown).Descendants<FencedCodeBlock>().Single().ClosingFencedCharCount;

        /// <summary>
        /// Highlights one block and returns the Runs' TEXT. Strings rather than the Runs themselves
        /// because a WPF text element belongs to the thread that made it, and these are made on the
        /// STA helper thread while the assertions run on xunit's — reading Run.Text afterwards
        /// throws "a different thread owns it", which is a fact about the harness and not about
        /// anything under test.
        /// </summary>
        private static List<string> Highlight(string code, string languageTag)
        {
            CodeHighlighter.ClearCacheForTests();
            var para = new Paragraph();
            CodeHighlighter.AppendTo(para.Inlines, code, languageTag);
            return para.Inlines.OfType<Run>().Select(r => r.Text).ToList();
        }

        /// <summary>
        /// The text of the Runs in the code paragraph, found by the fragment it must contain.
        /// Located by content rather than by position because a code block is not always the only
        /// paragraph in the document, and counting Runs across all of them would let prose Runs
        /// stand in for highlighting that never happened.
        /// </summary>
        private static List<string> CodeRunTexts(FlowDocument document, string contains) =>
            document.Blocks.OfType<Paragraph>()
                .Select(p => p.Inlines.OfType<Run>().Select(r => r.Text).ToList())
                .Where(texts => string.Concat(texts).Contains(contains, StringComparison.Ordinal))
                .SelectMany(texts => texts)
                .ToList();
    }
}
