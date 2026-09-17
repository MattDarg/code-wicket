using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// What <c>run_tests</c> says when its pre-run build fails (issue #296).
    /// <para>
    /// These assert WORDING, for the reason <c>FileWriteRefusalTests</c> does. The defect was a sentence
    /// that told the agent to "fix the build errors" beside <c>errors: []</c> — an instruction the payload
    /// could not back, on every build-only failure (<c>CS2001</c>, an MSBuild <c>&lt;Error&gt;</c>). So
    /// the pins are on the sentence agreeing with what rides beside it: it may point at <c>errors</c> only
    /// when there are some, at <c>buildOutput</c> only when it is present, and where neither is, it must
    /// name no cause and say that it cannot.
    /// </para>
    /// </summary>
    public sealed class TestRunBuildFailureTests
    {
        [Fact]
        public void WithErrors_PointsAtThem()
        {
            var message = TestRunBuildFailure.Message(projectsFailed: 1, errorCount: 3, hasBuildOutput: false);

            Assert.StartsWith("the solution did not build (1 project(s) failed); the build's errors are in 'errors'", message);
            Assert.Contains("fix them before running tests", message);
            Assert.DoesNotContain("buildOutput", message);
        }

        /// <summary>
        /// The issue's own shape: a build-only failure whose row did not reach the Error List. The sentence
        /// must not send the agent to an empty list, and must send it to the output that IS there.
        /// </summary>
        [Fact]
        public void NoErrorsButOutput_SaysErrorsIsEmptyAndPointsAtTheOutput()
        {
            var message = TestRunBuildFailure.Message(projectsFailed: 1, errorCount: 0, hasBuildOutput: true);

            Assert.DoesNotContain("fix the build errors", message);
            Assert.DoesNotContain("fix them", message);
            Assert.Contains("the build reported no error to the Error List, so 'errors' is empty", message);
            Assert.Contains("in 'buildOutput' — read the cause from there", message);
        }

        /// <summary>
        /// Nothing to read. The sentence names no cause, says it does not know, and refutes the two causes
        /// a model would otherwise supply — a flaky build and stale output.
        /// </summary>
        [Fact]
        public void NothingToRead_NamesNoCauseAndSaysSo()
        {
            var message = TestRunBuildFailure.Message(projectsFailed: 2, errorCount: 0, hasBuildOutput: false);

            Assert.StartsWith("the solution did not build (2 project(s) failed)", message);
            Assert.DoesNotContain("fix the build errors", message);
            Assert.DoesNotContain("'errors' is empty", message);
            Assert.DoesNotContain("in 'buildOutput'", message);
            Assert.Contains("the Build output pane could not be read, so this result does not know why", message);
            Assert.Contains("not evidence that the build is flaky or that its output is stale", message);
            Assert.Contains("ask the user what the Build output pane shows", message);
            // build_solution reads the build through the same reader, so sending the agent there would
            // most likely reproduce this shape and read as a loop.
            Assert.DoesNotContain("build_solution", message);
        }

        /// <summary>Every shape says no tests ran and closes running them another way.</summary>
        [Theory]
        [InlineData(3, false)]
        [InlineData(0, true)]
        [InlineData(0, false)]
        public void EveryShape_ClosesTheWorkaround(int errorCount, bool hasBuildOutput)
        {
            var message = TestRunBuildFailure.Message(projectsFailed: 1, errorCount, hasBuildOutput);

            Assert.EndsWith(
                "No tests were run: running them another way (dotnet test, vstest) would test the last successful "
                + "build's output, not the code on disk.",
                message);
        }
    }
}
