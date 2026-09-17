namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// The sentence <c>run_tests</c> leads with when the build it runs first fails (issue #296).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sentence must match what the payload beside it can show.</b> It used to say "fix the build
    /// errors before running tests" unconditionally, over an error list read from Roslyn's live compiler —
    /// which structurally cannot see a build-only diagnostic (<c>CS2001</c>, an MSBuild <c>&lt;Error&gt;</c>,
    /// <c>NU</c>/<c>MSB</c>/<c>NETSDK</c>) — so a failed build arrived as <c>errorCount: 0</c> under an
    /// instruction to fix errors it had not been given. The rows now come from the build itself, the
    /// collector <c>build_solution</c> uses, but a failure that leaves no rows is still possible (a target
    /// crash, a failed build event, the Error List not converging in time), so the sentence is chosen by
    /// what the payload actually carries.
    /// </para>
    /// <para>
    /// <b>Where nothing names the cause, it says so and names none</b> (the <see cref="FileWriteRefusal"/>
    /// rule): a result that declines to say why gets a reason supplied, and the plausible inventions here —
    /// "stale obj, rebuild", "the tool is broken" — are both wrong. So each sentence says what was read,
    /// says no tests ran, and closes the one workaround that would do harm: running the tests another way
    /// runs them against the last build's output, which is not the code on disk.
    /// </para>
    /// <para>Pure and in Core so the wording is testable; the catalog that reads the rows is VS-bound.</para>
    /// </remarks>
    public static class TestRunBuildFailure
    {
        private const string NoTestsRan =
            " No tests were run: running them another way (dotnet test, vstest) would test the last successful "
            + "build's output, not the code on disk.";

        /// <summary>The <c>error</c> sentence for a pre-run build that failed.</summary>
        /// <param name="projectsFailed">The build manager's failed-project count.</param>
        /// <param name="errorCount">Error rows the BUILD reported (not Roslyn's view).</param>
        /// <param name="hasBuildOutput">Whether the Build output pane's tail rides the payload as <c>buildOutput</c>.</param>
        public static string Message(int projectsFailed, int errorCount, bool hasBuildOutput)
        {
            var failed = $"the solution did not build ({projectsFailed} project(s) failed)";

            if (errorCount > 0)
                return $"{failed}; the build's errors are in 'errors' — fix them before running tests."
                    + NoTestsRan;

            if (hasBuildOutput)
                return $"{failed}, and the build reported no error to the Error List, so 'errors' is empty. The "
                    + "failure is in the tail of Visual Studio's Build output pane, in 'buildOutput' — read the "
                    + "cause from there." + NoTestsRan;

            // No pointer at build_solution here: it reads the build through the same reader, so a re-run
            // most likely returns this same shape. The one source this result could not reach is the pane
            // itself, and the user can see it.
            return $"{failed}, but the build reported no error to the Error List and the Build output pane could "
                + "not be read, so this result does not know why. That is a gap in what could be read, not "
                + "evidence that the build is flaky or that its output is stale: ask the user what the Build "
                + "output pane shows." + NoTestsRan;
        }
    }
}
