using CodeWicket.Ipc;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Folding successive usage reports into one view. The two rules pull in opposite directions and
    /// both matter: every field is a SNAPSHOT the newest report replaces — except cost, which the SDK
    /// reports per query() with no session total of its own, so it has to be accumulated
    /// (https://code.claude.com/docs/en/agent-sdk/cost-tracking).
    /// </summary>
    public sealed class UsageMergeTests
    {
        [Fact]
        public void Cost_accumulates_across_turns()
        {
            // Three turns at API-rate costs. Taking the newest would show the last turn's cost while
            // sitting in a panel of session-scoped figures.
            var merged = ChatViewModel.MergeUsage(null, new UsageDto { Cost = 0.10 });
            merged = ChatViewModel.MergeUsage(merged, new UsageDto { Cost = 0.25 });
            merged = ChatViewModel.MergeUsage(merged, new UsageDto { Cost = 0.05 });

            Assert.Equal(0.40, merged.Cost!.Value, 6);
        }

        [Fact]
        public void Reports_without_a_cost_leave_the_running_total_alone()
        {
            // Claude attaches cost to exactly ONE usage_update per turn; the mid-turn context snapshots
            // carry none. Treating those as zero — or re-adding the previous value — would corrupt it.
            var merged = ChatViewModel.MergeUsage(null, new UsageDto { Cost = 0.30 });
            merged = ChatViewModel.MergeUsage(merged, new UsageDto { ContextPercent = 12 });
            merged = ChatViewModel.MergeUsage(merged, new UsageDto { ContextPercent = 14 });

            Assert.Equal(0.30, merged.Cost!.Value, 6);
            Assert.Equal(14, merged.ContextPercent);
        }

        [Fact]
        public void Everything_other_than_cost_is_replaced_not_summed()
        {
            var merged = ChatViewModel.MergeUsage(
                new UsageDto { ContextPercent = 10, ContextUsedTokens = 1000, TotalTokens = 500 },
                new UsageDto { ContextPercent = 20, ContextUsedTokens = 2000, TotalTokens = 700 });

            Assert.Equal(20, merged.ContextPercent);
            Assert.Equal(2000, merged.ContextUsedTokens);
            Assert.Equal(700, merged.TotalTokens);
        }

        [Fact]
        public void Fields_the_newest_report_omits_keep_their_previous_value()
        {
            // The halves arrive on different frames: context fill mid-turn, the per-turn token split on
            // the prompt response. Replacing wholesale would blank one every time the other arrived.
            var merged = ChatViewModel.MergeUsage(
                new UsageDto { ContextPercent = 21.4, ContextWindowTokens = 100000 },
                new UsageDto { TotalTokens = 32351, CachedReadTokens = 28112 });

            Assert.Equal(21.4, merged.ContextPercent);
            Assert.Equal(100000, merged.ContextWindowTokens);
            Assert.Equal(32351, merged.TotalTokens);
            Assert.Equal(28112, merged.CachedReadTokens);
        }

        [Fact]
        public void The_first_report_is_taken_whole()
        {
            var incoming = new UsageDto { ContextPercent = 5, Cost = 0.02 };

            Assert.Same(incoming, ChatViewModel.MergeUsage(null, incoming));
        }
    }
}
