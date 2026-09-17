using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The backend telling the user something, on the three routes it uses (issues #208, #85).
    /// <para>
    /// All three were captured live and all three were dropped: a notification with no handler, a
    /// <c>_meta.kiro.kind</c> nobody read on a frame we already handle, and a stderr buffer nothing
    /// looked in until a failure that never came. The symptom in every case was silence, which is why
    /// each is pinned by a test that fails when the route goes quiet again.
    /// </para>
    /// </summary>
    public class BackendNoticeTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

        // ---- _kiro/system/notify (issue #208) ---------------------------------------------------

        /// <summary>Captured 2026-09-04, verbatim.</summary>
        [Fact]
        public void SystemNotifyIsRelayedWithItsLevel()
        {
            var notice = AcpMapper.MapSystemNotify(Json(
                """
                {"level":"warning",
                 "message":"The selected model is experiencing high load, rate limiting applied. Consider switching models."}
                """));

            Assert.NotNull(notice);
            Assert.Equal("warning", notice!.Level);
            Assert.Contains("rate limiting applied", notice.Message);
        }

        /// <summary>A frame with nothing to say is not a notice - an empty line in the transcript
        /// would be worse than the silence this fixes.</summary>
        [Fact]
        public void SystemNotifyWithNoMessageIsNotANotice()
        {
            Assert.Null(AcpMapper.MapSystemNotify(Json("""{"level":"warning"}""")));
        }

        // ---- _meta.kiro.kind = display_error (issue #208) ----------------------------------------

        /// <summary>
        /// Captured six times on 2026-09-02. The errorType rides as the level so the host escalates
        /// it, and the message is the backend's own sentence.
        /// </summary>
        [Fact]
        public void DisplayErrorIsSurfacedWithItsErrorType()
        {
            var notice = AcpMapper.MapKiroNotice(Json(
                """
                {"_meta":{"kiro":{"kind":"display_error","displayError":{
                    "errorType":"UsageLimitReachedError",
                    "message":"You've reached your monthly usage limit. Please return next month to continue building."}}}}
                """));

            Assert.NotNull(notice);
            Assert.Equal("UsageLimitReachedError", notice!.Level);
            Assert.Contains("monthly usage limit", notice.Message);
            Assert.Equal(NoticeKind.Error, ChatViewModel.NoticeKindFor(notice.Level));
        }

        /// <summary>
        /// Kiro sends the message twice - nested under displayError and flattened onto kiro - and
        /// neither is guaranteed. A refusal must not be dropped over its packaging.
        /// </summary>
        [Fact]
        public void DisplayErrorReadsTheFlattenedMessageWhenTheNestedOneIsAbsent()
        {
            var notice = AcpMapper.MapKiroNotice(Json(
                """{"_meta":{"kiro":{"kind":"display_error","message":"Refused."}}}"""));

            Assert.NotNull(notice);
            Assert.Equal("Refused.", notice!.Message);
        }

        // ---- the compaction pair (issue #85, Kiro half) ------------------------------------------

        /// <summary>
        /// The completed end is the one the user is told about, and the notice says what it MEANS -
        /// that the agent now holds less of this conversation than the transcript does - rather than
        /// that a job ran. That divergence is what issue #85 is about.
        /// </summary>
        [Fact]
        public void SummarizationCompletedTellsTheUserTheAgentHoldsLess()
        {
            var notice = AcpMapper.MapKiroNotice(Json(
                """{"_meta":{"kiro":{"kind":"summarization_completed"}}}"""));

            Assert.NotNull(notice);
            Assert.Contains("condensed", notice!.Message);
            Assert.Contains("no longer be in its memory", notice.Message);
            // Housekeeping, not a failure.
            Assert.Equal(NoticeKind.Info, ChatViewModel.NoticeKindFor(notice.Level));
        }

        /// <summary>
        /// The pair is ONE event to a reader. The started frame is ~12ms of warning nobody can act on,
        /// so announcing both would put two lines in the transcript for one thing that happened.
        /// </summary>
        [Fact]
        public void SummarizationStartedIsRecognisedAndSilent()
        {
            Assert.Null(AcpMapper.MapKiroNotice(Json(
                """{"_meta":{"kiro":{"kind":"summarization_started"}}}""")));
        }

        /// <summary>An ordinary context reading is not a notice - it is the ring's update, and the
        /// two ride the same frame type.</summary>
        [Fact]
        public void AnOrdinaryContextUsageFrameIsNotANotice()
        {
            Assert.Null(AcpMapper.MapKiroNotice(Json(
                """{"_meta":{"kiro":{"kind":"context_usage","contextUsage":{"usagePercentage":42.0}}}}""")));
        }

        /// <summary>
        /// The compaction frame carries no figures, but the reader must not stop at the first match:
        /// a future kind that carried both would lose the ring's update to show a notice.
        /// </summary>
        [Fact]
        public void AFrameCarryingBothAContextReadingAndANoticeYieldsBoth()
        {
            var update = Json(
                """
                {"sessionUpdate":"session_info_update","_meta":{"kiro":{
                    "kind":"display_error",
                    "displayError":{"errorType":"UsageLimitReachedError","message":"Refused."},
                    "contextUsage":{"usagePercentage":42.0}}}}
                """);

            var events = AcpMapper.Map(update).ToList();

            Assert.Contains(events, e => e is AgentEvent.UsageUpdated);
            Assert.Contains(events, e => e is AgentEvent.BackendNotice);
        }

        // ---- the level mapping -------------------------------------------------------------------

        /// <summary>
        /// An unrecognised level is informational, NEVER dropped. Dropping is the defect all of this
        /// exists to fix, and doing it for anything unfamiliar would be the same defect with a smaller
        /// blast radius.
        /// </summary>
        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("warning", false)]
        [InlineData("something-nobody-has-seen", false)]
        [InlineData("error", true)]
        [InlineData("UsageLimitReachedError", true)]
        public void AnUnknownLevelIsInformationalRatherThanDropped(string? level, bool escalated)
        {
            Assert.Equal(
                escalated ? NoticeKind.Error : NoticeKind.Info,
                ChatViewModel.NoticeKindFor(level));
        }

        // ---- CLI stderr (issue #208, third carrier) ----------------------------------------------

        private const string Throttled =
            """[ERROR] [KRS] HTTP 429 requestId=a2074b76-dbf0-41a9-a230-0a9105d630e0 body={"__type":"com.amazon.kiro.runtimeservice#ThrottlingException","message":"Too many requests, please wait before trying again.","reason":"CREDIT_CONSUMPTION_RATE_EXCEEDED","retryAfterMilliseconds":300000}""";

        /// <summary>
        /// The filter is not optional: measured 585 [INFO] against 8 [ERROR] across every captured
        /// engine.log, so relaying unfiltered would be 98.6% noise.
        /// </summary>
        [Fact]
        public void OnlyErrorLevelStderrIsSurfaced()
        {
            Assert.True(AgentStderrNotice.IsError(Throttled));
            Assert.False(AgentStderrNotice.IsError(
                "[INFO] [KRS] <-- GenerateAssistantResponseCommand done totalEvents=8"));
            Assert.False(AgentStderrNotice.IsError(null));
        }

        /// <summary>Captured 2026-09-07, verbatim - the bundled node agent's first load of
        /// <c>node:sqlite</c>, reported to the user as a backend error.</summary>
        private const string NodeSqliteWarning =
            "[ERROR] (node:66940) ExperimentalWarning: SQLite is an experimental feature and might change at any time";

        /// <summary>
        /// The level marker on a relayed line describes the pipe, not the content: node wrote this
        /// through <c>process.emitWarning</c>, kiro-cli stamped [ERROR] on it because it arrived on
        /// stderr, and the user got a red notice about a feature flag in someone else's runtime.
        /// </summary>
        [Fact]
        public void ARuntimeWarningTheCliMerelyForwardedIsNotAnError()
        {
            Assert.True(AgentStderrNotice.IsRelayedRuntimeWarning(NodeSqliteWarning));
            Assert.False(AgentStderrNotice.IsError(NodeSqliteWarning));
        }

        /// <summary>
        /// node's other emitWarning shape, which carries a bracketed code before the name. Pinned
        /// because the code sits exactly where the CLI's own subsystem tag would ([KRS]), so a
        /// pattern written against the captured line alone would miss it.
        /// </summary>
        [Fact]
        public void TheCodedWarningShapeIsRecognisedToo()
        {
            Assert.False(AgentStderrNotice.IsError(
                "[ERROR] (node:1234) [DEP0040] DeprecationWarning: The `punycode` module is deprecated."));
        }

        /// <summary>
        /// <b>The subtraction is an exclusion, not an allowlist - asserted in both directions.</b>
        /// An unrecognised error shape still surfaces, and so does a relayed line that is NOT node
        /// calling it a warning: the provenance test says who wrote it, and node writing an actual
        /// failure is still a failure. Requiring the CLI's own [KRS] shape would pass every other
        /// test here and make an unknown error silent, which is the incident this class exists for.
        /// </summary>
        [Theory]
        [InlineData("[ERROR] [KRS] connection reset by peer")]
        [InlineData("[ERROR] a subsystem nobody has written a parser for")]
        [InlineData("[ERROR] (node:66940) UnhandledPromiseRejection: the agent died")]
        public void AnythingNotIdentifiedAsAForeignWarningStillSurfaces(string line)
        {
            Assert.True(AgentStderrNotice.IsError(line));
        }

        /// <summary>
        /// The eight captured refusals were the SAME refusal, differing only in a request id and a
        /// retry window. Keyed on the raw line the user would have been told eight times.
        /// </summary>
        [Fact]
        public void RepeatsOfTheSameRefusalShareOneKey()
        {
            var second = Throttled
                .Replace("a2074b76-dbf0-41a9-a230-0a9105d630e0", "3ef05279-4d21-420c-bc4a-f6113aefb9ae")
                .Replace("300000", "3600000");

            Assert.Equal(AgentStderrNotice.DedupeKey(Throttled), AgentStderrNotice.DedupeKey(second));
        }

        /// <summary>...but a different problem is still a different notice.</summary>
        [Fact]
        public void ADifferentErrorIsNotDeduped()
        {
            Assert.NotEqual(
                AgentStderrNotice.DedupeKey(Throttled),
                AgentStderrNotice.DedupeKey("[ERROR] [KRS] connection reset by peer"));
        }

        /// <summary>
        /// The line is shown as the backend wrote it - numbers and all. The dedupe key blanks them to
        /// decide WHETHER to show it, and must never become what is shown.
        /// </summary>
        [Fact]
        public void TheNoticeShowsTheLineVerbatim()
        {
            var text = AgentStderrNotice.Format(Throttled, "engine.log");

            // The line, unaltered - which is the assertion, not a spot-check of two substrings. (The
            // first draft asserted the text carried no '#', meaning to prove the dedupe key's
            // placeholder had not leaked; the payload's own type name is
            // "…runtimeservice#ThrottlingException", so the check was testing the fixture, not the code.)
            Assert.Equal(Throttled.Trim(), text);
            Assert.Contains("CREDIT_CONSUMPTION_RATE_EXCEEDED", text);
            Assert.Contains("retryAfterMilliseconds", text);
        }

        /// <summary>Truncation announces itself and names where the rest is - a silently cut line
        /// reads as the whole story.</summary>
        [Fact]
        public void AnEnormousLineSaysThatItWasCut()
        {
            var text = AgentStderrNotice.Format("[ERROR] " + new string('x', 5000), "engine.log");
            Assert.True(text.Length < 5000);
            Assert.Contains("truncated", text);
            Assert.Contains("engine.log", text);
        }

        /// <summary>
        /// The handler is REGISTERED, under exactly that method name, and it reaches the mapper.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The mapper tests above pin nothing about this, and prove-check said so.</b> Removing the
        /// <c>[JsonRpcMethod]</c> attribute left every one of them green: they exercise
        /// <c>MapSystemNotify</c>, while the whole feature hangs off one attribute string. A
        /// notification with no matching handler is dropped with no error anywhere, so a wrong name
        /// fails exactly like the bug this fixes.
        /// </para>
        /// <para>
        /// <b>And the name is the known trap.</b> Kiro v3's extension namespace is <c>_kiro/*</c>
        /// while the default engine's is <c>_kiro.dev/*</c>; a handler bound to the wrong one is dead
        /// code that compiles, registers and never fires - which AGENTS.md records having already
        /// cost a v3 feature. Asserted as a literal for that reason.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheNotifyHandlerIsRegisteredUnderItsWireName()
        {
            var method = typeof(AcpClientTarget)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .SingleOrDefault(m => m.GetCustomAttributes(typeof(StreamJsonRpc.JsonRpcMethodAttribute), false)
                    .Cast<StreamJsonRpc.JsonRpcMethodAttribute>()
                    .Any(a => a.Name == "_kiro/system/notify"));

            Assert.True(method is not null,
                "no handler is registered for _kiro/system/notify - the notification will be dropped silently");

            // ...and it is wired to the mapper, not merely present.
            var events = new System.Collections.Generic.List<AgentEvent>();
            var target = new AcpClientTarget(null!, events.Add, () => "s");
            method!.Invoke(target, new object[] { Json("""{"level":"warning","message":"Slow down."}""") });

            var notice = Assert.IsType<AgentEvent.BackendNotice>(Assert.Single(events));
            Assert.Equal("Slow down.", notice.Message);
            Assert.Equal("warning", notice.Level);
        }

    }
}
