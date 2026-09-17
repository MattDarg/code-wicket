using System.Collections.Generic;
using System.Linq;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The test-results card's summary-first collapse: a broken suite's failure list must not bury the
    /// transcript, while a handful of failures stays visible without a click.
    /// </summary>
    public sealed class TestRunCardTests
    {
        static TestRunResultItemViewModel Card(int failureCount, bool truncated = false)
        {
            var failures = Enumerable.Range(1, failureCount)
                .Select(i => new TestFailureViewModel(
                    $"Ns.Tests.Case{i}", "Assert.True() Failure", @"C:\ws\Tests.cs", i, @"C:\ws", null))
                .ToList();
            return Card(failureCount, truncated, failures);
        }

        static TestRunResultItemViewModel Card(int failureCount, bool truncated, List<TestFailureViewModel> failures)
        {
            return new TestRunResultItemViewModel(
                succeeded: failureCount == 0, total: 20, passed: 20 - failureCount,
                failed: failureCount, skipped: 0, truncated: truncated, failures: failures);
        }

        // A few failures ARE the detail you wanted — hiding them behind a click would be a regression.
        [Theory]
        [InlineData(1)]
        [InlineData(TestRunResultItemViewModel.InlineFailureLimit)]
        public void SmallFailureList_StartsExpanded(int count)
            => Assert.True(Card(count).IsExpanded);

        // Past the limit the card leads with the summary; the chevron opens the list.
        [Fact]
        public void LargeFailureList_StartsCollapsed()
            => Assert.False(Card(TestRunResultItemViewModel.InlineFailureLimit + 1).IsExpanded);

        // An all-green run has nothing to expand, so no chevron is offered.
        [Fact]
        public void NoFailures_NothingToExpand()
        {
            var card = Card(0);
            Assert.False(card.HasFailures);
            Assert.False(card.IsExpanded);
        }

        // The "…more failures not shown" footnote only means something while the list is on screen.
        [Fact]
        public void TruncatedNote_FollowsExpansion()
        {
            var card = Card(TestRunResultItemViewModel.InlineFailureLimit + 1, truncated: true);
            Assert.False(card.ShowTruncatedNote);
            card.IsExpanded = true;
            Assert.True(card.ShowTruncatedNote);
        }

        // Collapsing is a display choice only: Copy (and the markdown export that shares this text)
        // still yields every failure, so a collapsed card never loses data on the way out.
        [Fact]
        public void CopyText_IncludesFailuresWhileCollapsed()
        {
            var card = Card(5);
            Assert.False(card.IsExpanded);
            var text = card.CopyText;
            Assert.NotNull(text);
            foreach (var f in card.Failures)
                Assert.Contains(f.Name, text!);
        }

        // A Reqnroll-shaped failure: it threw in a step definition, the test is the .feature scenario.
        // The row must offer the second jump, word it as the scenario, and keep both in the copy record.
        [Fact]
        public void FeatureTest_OffersTheScenarioAsASecondJump()
        {
            var failure = new TestFailureViewModel(
                "Add two numbers", "Assert.Equal() Failure", @"C:\ws\Steps\CalculatorSteps.cs", 26, @"C:\ws",
                openFile: (_, _) => System.Threading.Tasks.Task.CompletedTask,
                testFile: @"C:\ws\Features\Calculator.feature", testLine: 9);

            Assert.True(failure.HasTestLocation);
            Assert.True(failure.CanOpenTest);
            Assert.True(failure.HasContextActions);
            Assert.Equal("Go to scenario", failure.OpenTestHeader);
            Assert.Equal("Steps/CalculatorSteps.cs:26", failure.LocationLabel);
            Assert.Equal("Features/Calculator.feature:9", failure.TestLocationLabel);
            // Click goes to the scenario, and the row's label says so; the tooltip keeps both.
            Assert.True(failure.PrimaryIsTest);
            Assert.Equal("Features/Calculator.feature:9", failure.PrimaryLocationLabel);
            Assert.Equal("Test: Features/Calculator.feature:9\nFailed at: Steps/CalculatorSteps.cs:26",
                failure.LocationToolTip);
            Assert.Contains("test: Features/Calculator.feature:9",
                Card(1, truncated: false, new List<TestFailureViewModel> { failure }).CopyText!);
        }

        // A plain unit test whose helper threw: same second jump, worded for C#.
        [Fact]
        public void CodeTest_WordsTheSecondJumpAsTheTest()
        {
            var failure = new TestFailureViewModel(
                "Ns.Tests.Adds", null, @"C:\ws\Assertions.cs", 9, @"C:\ws",
                openFile: (_, _) => System.Threading.Tasks.Task.CompletedTask,
                testFile: @"C:\ws\CalculatorTests.cs", testLine: 8);
            Assert.Equal("Go to test", failure.OpenTestHeader);
            Assert.True(failure.CanOpenTest);
        }

        // The tool omits testFile when it would repeat the failure location, so the row must offer
        // nothing extra — and the click falls back to the throw site, which IS the test line there.
        [Fact]
        public void NoTestLocation_ClickFallsBackToTheFailure()
        {
            var withEditor = new TestFailureViewModel(
                "Ns.Tests.Adds", null, @"C:\ws\Tests.cs", 12, @"C:\ws",
                openFile: (_, _) => System.Threading.Tasks.Task.CompletedTask);
            Assert.False(withEditor.CanOpenTest);
            Assert.Null(withEditor.TestLocationLabel);
            Assert.False(withEditor.PrimaryIsTest);
            Assert.Equal("Tests.cs:12", withEditor.PrimaryLocationLabel);
            Assert.True(withEditor.CanOpen);
            Assert.True(withEditor.HasContextActions); // "Go to failure" still applies
        }

        // A host with no editor wired (Desktop without the callback) gets no menu at all rather than
        // a dead one — but the row still LABELS its location, which is information either way.
        [Fact]
        public void NoEditor_NoMenuButStillLabelled()
        {
            var noEditor = new TestFailureViewModel(
                "Ns.Tests.Adds", null, @"C:\ws\Tests.cs", 12, @"C:\ws", openFile: null,
                testFile: @"C:\ws\Other.cs", testLine: 3);
            Assert.False(noEditor.HasContextActions);
            Assert.False(noEditor.CanOpen);
            Assert.Equal("Other.cs:3", noEditor.PrimaryLocationLabel);
        }
    }
}
