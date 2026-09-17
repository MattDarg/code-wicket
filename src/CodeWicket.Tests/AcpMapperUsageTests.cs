using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Usage/consumption reporting. Every frame here is copied verbatim from a real captured session
    /// (see the memory note <c>backend-usage-telemetry</c>) — the point of these tests is that the two
    /// backends report genuinely different things under different shapes, and that the ONE measure they
    /// share (context fill) comes out comparable.
    /// </summary>
    public sealed class AcpMapperUsageTests
    {
        static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

        static UsageReport MapOne(string updateJson)
        {
            var events = AcpMapper.Map(Json(updateJson)).ToList();
            return Assert.IsType<AgentEvent.UsageUpdated>(Assert.Single(events)).Usage;
        }

        [Fact]
        public void Claude_usage_update_derives_a_context_percentage_from_absolutes()
        {
            // Claude reports used/size; the percentage every backend can be compared on is derived here
            // rather than in the UI, so the host never has to know which backend it is talking to.
            var usage = MapOne("""
                {"sessionUpdate":"usage_update","used":28480,"size":1000000,
                 "cost":{"amount":0.5569930000000001,"currency":"USD"},
                 "_meta":{"_claude/rateLimit":{"status":"allowed","resetsAt":1785073800,
                   "rateLimitType":"five_hour","overageStatus":"rejected","isUsingOverage":false}}}
                """);

            Assert.Equal(28480, usage.ContextUsedTokens);
            Assert.Equal(1000000, usage.ContextWindowTokens);
            Assert.Equal(2.848, usage.ContextPercent!.Value, 3);
            Assert.Equal(0.556993, usage.Cost!.Value, 6);
            Assert.Equal("USD", usage.CostCurrency);
            Assert.Equal("allowed", usage.RateLimitStatus);
            Assert.Equal("five_hour", usage.RateLimitType);
            Assert.Equal(1785073800L, usage.RateLimitResetsAt);
        }

        [Fact]
        public void Claude_usage_update_without_cost_or_rate_limit_still_maps()
        {
            // The first usage_update of a session carries neither — cost and rate limit appear later, so
            // treating them as required would drop the earliest context readings entirely.
            var usage = MapOne("""{"sessionUpdate":"usage_update","used":26775,"size":1000000}""");

            Assert.Equal(26775, usage.ContextUsedTokens);
            Assert.Null(usage.Cost);
            Assert.Null(usage.RateLimitStatus);
        }

        [Fact]
        public void A_zero_window_yields_no_percentage_rather_than_a_divide_by_zero()
        {
            var usage = MapOne("""{"sessionUpdate":"usage_update","used":100,"size":0}""");

            Assert.Equal(100, usage.ContextUsedTokens);
            Assert.Null(usage.ContextPercent);
        }

        [Fact]
        public void Kiro_session_info_update_maps_percentage_and_breakdown()
        {
            var usage = MapOne("""
                {"sessionUpdate":"session_info_update","_meta":{"kiro":{
                  "contextUsage":{"usagePercentage":0.8},"kind":"context_usage",
                  "breakdown":{"contextFiles":{"tokens":0,"percent":0,"items":[]},
                    "tools":{"tokens":4241,"percent":0.4,"mcp":{"tokens":0,"percent":0},
                      "builtin":{"tokens":4241,"percent":0.4}},
                    "kiroResponses":{"tokens":0,"percent":0},
                    "yourPrompts":{"tokens":4356,"percent":0.4},
                    "sessionFiles":{"tokens":0,"percent":0,"items":[]}}}}}
                """);

            Assert.Equal(0.8, usage.ContextPercent);
            // Kiro reports no absolutes and no cost — the fields must stay null, not default to zero,
            // so a host can tell "not reported" from "nothing spent".
            Assert.Null(usage.ContextUsedTokens);
            Assert.Null(usage.Cost);

            // Ordered as authored, not as the backend serialised it, and wire names become readable.
            // A parent's parts follow it immediately, so the rows read as a total then its makeup.
            var labels = usage.Breakdown!.Select(b => b.Label).ToArray();
            Assert.Equal(
                new[] { "Your prompts", "Agent responses", "Tool definitions", "Built-in tools", "MCP tools", "Context files", "Session files" },
                labels);
            Assert.Equal(4356, usage.Breakdown![0].Tokens);
            Assert.Equal(4241, usage.Breakdown![2].Tokens);
        }

        [Fact]
        public void The_tools_category_splits_into_built_ins_and_MCP_and_the_parts_sum_to_the_parent()
        {
            // Verbatim from a live v3 capture (acp.log, 2026-09-01). This is the ONLY place the wire
            // says what MCP costs, and it is an aggregate across every MCP server including our own
            // bridge — there is no per-server figure anywhere in the frame, which is why the MCP
            // roster panel carries no cost row and this one lives beside the context ring instead.
            var usage = MapOne("""
                {"sessionUpdate":"session_info_update","_meta":{"kiro":{
                  "contextUsage":{"usagePercentage":0.9},"kind":"context_usage",
                  "breakdown":{"contextFiles":{"tokens":0,"percent":0,"items":[]},
                    "tools":{"tokens":10283,"percent":0.9,"mcp":{"tokens":4826,"percent":0.5},
                      "builtin":{"tokens":5457,"percent":0.5}},
                    "kiroResponses":{"tokens":0,"percent":0},
                    "yourPrompts":{"tokens":0,"percent":0}}}}}
                """);

            var byLabel = usage.Breakdown!.ToDictionary(b => b.Label, b => b.Tokens);
            Assert.Equal(10283, byLabel["Tool definitions"]);
            Assert.Equal(5457, byLabel["Built-in tools"]);
            Assert.Equal(4826, byLabel["MCP tools"]);

            // Both halves are emitted rather than mcp alone, so the split explains itself: the parts
            // visibly account for the parent, with no prose in a panel that has room for none.
            Assert.Equal(byLabel["Tool definitions"], byLabel["Built-in tools"] + byLabel["MCP tools"]);

            // The parts follow their parent directly rather than being appended at the end.
            var labels = usage.Breakdown!.Select(b => b.Label).ToList();
            Assert.Equal(labels.IndexOf("Tool definitions") + 1, labels.IndexOf("Built-in tools"));
            Assert.Equal(labels.IndexOf("Tool definitions") + 2, labels.IndexOf("MCP tools"));
        }

        [Fact]
        public void Kiro_metadata_notification_maps_the_context_percentage()
        {
            // Newer kiro-cli builds report context fill on a _kiro.dev/metadata notification (verbatim
            // frame shape) rather than a session_info_update — the percentage is at the params top level
            // and there is no per-category breakdown.
            Assert.True(AcpMapper.TryMapKiroMetadata(Json("""
                {"sessionId":"ee48a5fa-399a-49c4-ac38-8eef7f0c4008",
                 "contextUsagePercentage":21.639400482177734,"turnDurationMs":488150,"effort":"high"}
                """), out var usage));

            Assert.Equal(21.6394, usage.ContextPercent!.Value, 4);
            Assert.Null(usage.Breakdown);
            Assert.Null(usage.ContextUsedTokens);
        }

        [Fact]
        public void Kiro_metadata_without_a_percentage_does_not_map()
        {
            // A metadata frame carrying only effort/duration (no contextUsagePercentage) must not
            // surface as an empty report that would blank a live indicator.
            Assert.False(AcpMapper.TryMapKiroMetadata(Json("""
                {"sessionId":"s1","turnDurationMs":100,"effort":"high"}
                """), out _));
        }

        [Fact]
        public void Kiro_session_info_updates_that_are_not_usage_are_ignored()
        {
            // Kiro multiplexes several unrelated things through session_info_update; the turn-end one
            // carries no usage and must not surface as an empty report that blanks a live indicator.
            var events = AcpMapper.Map(Json("""
                {"sessionUpdate":"session_info_update","_meta":{"kiro":{
                  "turnEnd":{"stopReason":"end_turn"},"kind":"turn_end","stopReason":"end_turn"}}}
                """)).ToList();

            Assert.Empty(events);
        }

        [Fact]
        public void Kiro_summarize_and_truncate_thresholds_are_carried_when_present()
        {
            // These ride the session/new response rather than every update, but the same shape — and
            // they are what makes the gauge actionable: Kiro compacts the conversation at 80%.
            var usage = MapOne("""
                {"sessionUpdate":"session_info_update","_meta":{"kiro":{"contextUsage":{
                  "summarizationThreshold":80,"truncationThreshold":95,"usagePercentage":3.49}}}}
                """);

            Assert.Equal(3.49, usage.ContextPercent);
            Assert.Equal(80, usage.SummarizeThresholdPercent);
            Assert.Equal(95, usage.TruncateThresholdPercent);
        }

        [Fact]
        public void Prompt_response_usage_carries_the_cache_split()
        {
            // Cached reads/writes are billed differently from fresh input, so they stay separate rather
            // than being folded into InputTokens.
            var usage = AcpMapper.MapPromptUsage(Json("""
                {"inputTokens":3869,"outputTokens":370,"cachedReadTokens":28112,
                 "cachedWriteTokens":51299,"totalTokens":83650}
                """))!;

            Assert.Equal(3869, usage.InputTokens);
            Assert.Equal(370, usage.OutputTokens);
            Assert.Equal(28112, usage.CachedReadTokens);
            Assert.Equal(51299, usage.CachedWriteTokens);
            Assert.Equal(83650, usage.TotalTokens);
        }

        /// <summary>
        /// A count or a percentage quoted as a string still reads — the tolerance the accessors have
        /// always described and did not have.
        /// <para>
        /// The consequence of not having it is invisible rather than loud: the mapper returns no usage at
        /// all, so the context ring stops updating for the whole session and looks exactly like a backend
        /// that never reports usage. That collapses the one distinction <c>UsageReport</c> is built to
        /// keep — a null means NOT REPORTED, never zero.
        /// </para>
        /// </summary>
        [Fact]
        public void A_quoted_number_is_read_like_a_number()
        {
            var usage = MapOne("""
                {"sessionUpdate":"usage_update","used":"28480","size":"1000000"}
                """);

            Assert.Equal(28480, usage.ContextUsedTokens);
            Assert.Equal(1000000, usage.ContextWindowTokens);
            Assert.NotNull(usage.ContextPercent);
        }

        [Fact]
        public void A_quoted_percentage_is_read_invariantly()
        {
            // Invariant culture, not the machine's: "80.5" is eighty and a half wherever this runs, and
            // a comma decimal is not a thousands separator to be quietly swallowed.
            var usage = MapOne("""
                {"sessionUpdate":"session_info_update","_meta":{"kiro":{"contextUsage":{
                  "summarizationThreshold":"80","truncationThreshold":"95","usagePercentage":"80.5"}}}}
                """);

            Assert.Equal(80.5, usage.ContextPercent);
            Assert.Equal(80, usage.SummarizeThresholdPercent);
            Assert.Equal(95, usage.TruncateThresholdPercent);
        }

        [Fact]
        public void A_string_that_is_not_a_number_is_still_not_reported()
        {
            // The tolerance is for type drift, not for inventing figures: an unparseable value stays null
            // rather than becoming zero, which would read as "no context used at all".
            var usage = MapOne("""
                {"sessionUpdate":"usage_update","used":"lots","size":1000000}
                """);

            Assert.Null(usage.ContextUsedTokens);
        }

        [Fact]
        public void A_prompt_response_without_usage_maps_to_null()
        {
            // Kiro's prompt response has no usage object at all; PromptResult leaves the property
            // Undefined, which must not become an all-null report that overwrites live context figures.
            Assert.Null(AcpMapper.MapPromptUsage(default));
            Assert.Null(AcpMapper.MapPromptUsage(Json("""{"stopReason":"end_turn"}""")));
        }
    }
}
