using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The usage panel's rate-limit label. It matters because a rate limit is the one ACCOUNT-scoped
    /// figure in a panel of session-scoped ones — Claude's five-hour window spans sessions entirely — so
    /// the row has to name its own scope rather than reading as another session statistic.
    /// </summary>
    public sealed class UsagePanelLabelTests
    {
        [Theory]
        [InlineData("five_hour", "Five-hour limit")]
        [InlineData("weekly", "Weekly limit")]
        // Unrecognised windows still read sensibly rather than being dropped or shown raw: the backend
        // is free to add one, and a panel that silently omits a limit you have hit is the worst outcome.
        [InlineData("thirty_day_rolling", "Thirty-day-rolling limit")]
        public void Names_the_limit_window(string rateLimitType, string expected) =>
            Assert.Equal(expected, ChatViewModel.FormatRateLimitLabel(rateLimitType));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Falls_back_when_the_window_is_unreported(string? rateLimitType) =>
            Assert.Equal("Rate limit", ChatViewModel.FormatRateLimitLabel(rateLimitType));
    }
}
