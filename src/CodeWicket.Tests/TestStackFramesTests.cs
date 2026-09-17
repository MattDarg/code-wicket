using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The run_tests failure/test location split, pinned against REAL captured stack traces (probed
    /// 2026-07-25 by running deliberately-failing tests through each runner and reading the TRX):
    /// where it threw is the first in-workspace frame, where the test IS is the last one.
    /// </summary>
    public sealed class TestStackFramesTests
    {
        const string Root = @"C:\ws";

        // xUnit v2 under VSTest ("dotnet test"), asserting through a helper in another file.
        const string HelperTrace =
            "   at PlainTests.Assertions.ShouldEqual(Int32 expected, Int32 actual) in C:\\ws\\PlainTests\\Assertions.cs:line 9\r\n" +
            "   at PlainTests.CalculatorTests.Add_TwoNumbers_ReturnsSum() in C:\\ws\\PlainTests\\CalculatorTests.cs:line 8\r\n" +
            "   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)\r\n" +
            "   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)";

        // Reqnroll 2.4 + xUnit: the Then step definition throws, and the generated code-behind's #line
        // pragmas attribute the outermost frame to the .feature itself. Note the runner/infrastructure
        // frames BETWEEN the two user frames — the "last in-workspace frame" rule steps over them.
        const string ReqnrollTrace =
            "   at FeatureTests.Steps.CalculatorSteps.ThenTheResultShouldBe(Int32 expected) in C:\\ws\\FeatureTests\\Steps\\CalculatorSteps.cs:line 26\r\n" +
            "   at InvokeStub_Action`2.Invoke(Object, Span`1)\r\n" +
            "   at System.Reflection.MethodBaseInvoker.InvokeWithFewArgs(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters, CultureInfo culture)\r\n" +
            "--- End of stack trace from previous location ---\r\n" +
            "   at Reqnroll.Bindings.BindingInvoker.InvokeBindingAsync(IBinding binding, IContextManager contextManager, Object[] arguments, ITestTracer testTracer, DurationHolder durationHolder)\r\n" +
            "   at Reqnroll.Infrastructure.TestExecutionEngine.ExecuteStepMatchAsync(BindingMatch match, Object[] arguments, DurationHolder durationHolder)\r\n" +
            "   at Reqnroll.TestRunner.CollectScenarioErrorsAsync()\r\n" +
            "   at FeatureTests.Features.CalculatorArithmeticFeature.ScenarioCleanupAsync()\r\n" +
            "   at FeatureTests.Features.CalculatorArithmeticFeature.AddTwoNumbers() in C:\\ws\\FeatureTests\\Features\\Calculator.feature:line 9\r\n" +
            "--- End of stack trace from previous location ---";

        [Fact]
        public void Helper_FailureIsTheAssertionHelper()
        {
            var failure = TestStackFrames.Failure(HelperTrace, Root);
            Assert.Equal((@"C:\ws\PlainTests\Assertions.cs", 9), failure);
        }

        [Fact]
        public void Helper_TestIsTheTestMethodFrame()
        {
            var failure = TestStackFrames.Failure(HelperTrace, Root);
            var test = TestStackFrames.Test(HelperTrace, Root, failure);
            Assert.Equal((@"C:\ws\PlainTests\CalculatorTests.cs", 8), test);
        }

        // Passing the TRX's className changes nothing where position was already right — it just makes
        // the same answer a verified one instead of a guess.
        [Fact]
        public void Helper_ClassNameAgreesWithPosition()
        {
            var failure = TestStackFrames.Failure(HelperTrace, Root);
            var test = TestStackFrames.Test(HelperTrace, Root, failure, "PlainTests.CalculatorTests");
            Assert.Equal((@"C:\ws\PlainTests\CalculatorTests.cs", 8), test);
        }

        // The case position alone gets WRONG: the suite's base class drives each test, so the outermost
        // user frame is TestBase.Execute() — not the test. The TRX class name steps past it.
        [Fact]
        public void BaseClassRunner_ClassNameBeatsPosition()
        {
            const string trace =
                "   at Suite.Asserts.ShouldEqual(Int32 a, Int32 b) in C:\\ws\\Suite\\Asserts.cs:line 9\r\n" +
                "   at Suite.OrderTests.Totals_AreSummed() in C:\\ws\\Suite\\OrderTests.cs:line 31\r\n" +
                "   at Suite.TestBase.Execute(Action body) in C:\\ws\\Suite\\TestBase.cs:line 74\r\n" +
                "   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\Suite\Asserts.cs", 9), failure);

            // Position alone stops at the base-class runner…
            Assert.Equal((@"C:\ws\Suite\TestBase.cs", 74), TestStackFrames.Test(trace, Root, failure));
            // …the declaring class from the TRX finds the test itself.
            Assert.Equal((@"C:\ws\Suite\OrderTests.cs", 31),
                TestStackFrames.Test(trace, Root, failure, "Suite.OrderTests"));
        }

        // Compiler-generated frames (async state machine, lambda, local function) still carry the class,
        // so an async test doesn't fall off the verified path.
        [Fact]
        public void AsyncStateMachineFrame_StillMatchesTheClass()
        {
            const string trace =
                "   at Suite.Asserts.ShouldEqual(Int32 a, Int32 b) in C:\\ws\\Suite\\Asserts.cs:line 9\r\n" +
                "   at Suite.OrderTests.<Totals_AreSummedAsync>d__3.MoveNext() in C:\\ws\\Suite\\OrderTests.cs:line 31\r\n" +
                "   at Suite.TestBase.Execute(Func`1 body) in C:\\ws\\Suite\\TestBase.cs:line 74";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\Suite\OrderTests.cs", 31),
                TestStackFrames.Test(trace, Root, failure, "Suite.OrderTests"));
        }

        // TRX spells a nested class "Outer+Inner"; stack frames spell it "Outer.Inner".
        [Fact]
        public void NestedClass_PlusIsNormalisedToDot()
        {
            const string trace =
                "   at Suite.Asserts.ShouldEqual(Int32 a, Int32 b) in C:\\ws\\Suite\\Asserts.cs:line 9\r\n" +
                "   at Suite.Outer.Inner.Works() in C:\\ws\\Suite\\Outer.cs:line 44\r\n" +
                "   at Suite.TestBase.Execute(Action body) in C:\\ws\\Suite\\TestBase.cs:line 74";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\Suite\Outer.cs", 44),
                TestStackFrames.Test(trace, Root, failure, "Suite.Outer+Inner"));
        }

        // A test INHERITED from a base class reports the derived class in the TRX while the frame names
        // the base — and that base file is genuinely where the test is. So an unmatched class degrades to
        // the position rule rather than refusing to answer.
        [Fact]
        public void UnmatchedClassName_FallsBackToPosition()
        {
            const string trace =
                "   at Suite.Asserts.ShouldEqual(Int32 a, Int32 b) in C:\\ws\\Suite\\Asserts.cs:line 9\r\n" +
                "   at Suite.SharedContractTests.Round_Trips() in C:\\ws\\Suite\\SharedContractTests.cs:line 20";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\Suite\SharedContractTests.cs", 20),
                TestStackFrames.Test(trace, Root, failure, "Suite.SqlBackedTests"));
        }

        // The whole point of the feature: a failing Then tells you nothing without the scenario, and the
        // scenario is Gherkin — so the second jump must land in the .feature, not the generated code-behind.
        [Fact]
        public void Reqnroll_TestIsTheFeatureFileScenario()
        {
            var failure = TestStackFrames.Failure(ReqnrollTrace, Root);
            var test = TestStackFrames.Test(ReqnrollTrace, Root, failure);
            Assert.Equal((@"C:\ws\FeatureTests\Steps\CalculatorSteps.cs", 26), failure);
            Assert.Equal((@"C:\ws\FeatureTests\Features\Calculator.feature", 9), test);

            // And it's the VERIFIED answer too: Reqnroll's TRX className is the generated feature class,
            // which is exactly what that outermost frame names.
            Assert.Equal((@"C:\ws\FeatureTests\Features\Calculator.feature", 9),
                TestStackFrames.Test(ReqnrollTrace, Root, failure, "FeatureTests.Features.CalculatorArithmeticFeature"));
        }

        // The ordinary case: the assert is written inline in the test, so both rules find the same frame
        // and there is no second place to go. Null keeps the host from offering a jump to where you are.
        [Fact]
        public void AssertInTheTest_NoSeparateTestLocation()
        {
            const string trace =
                "   at Xunit.Assert.Equal[T](T expected, T actual)\r\n" +
                "   at MyTests.Thing.Works() in C:\\ws\\MyTests\\Thing.cs:line 12";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\MyTests\Thing.cs", 12), failure);
            Assert.Null(TestStackFrames.Test(trace, Root, failure));
        }

        // Same file, different line still counts as somewhere new (a helper further up the same test class).
        [Fact]
        public void SameFileDifferentLine_IsStillATestLocation()
        {
            const string trace =
                "   at MyTests.Thing.AssertOk() in C:\\ws\\MyTests\\Thing.cs:line 30\r\n" +
                "   at MyTests.Thing.Works() in C:\\ws\\MyTests\\Thing.cs:line 12";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\MyTests\Thing.cs", 30), failure);
            Assert.Equal((@"C:\ws\MyTests\Thing.cs", 12), TestStackFrames.Test(trace, Root, failure));
        }

        // Failure falls back to a frame outside the workspace (so the agent still gets *something*), but
        // the test location never does: an outermost frame in the runner's own code is plumbing, and
        // sending the user into a decompiled runner frame is worse than offering nothing.
        [Fact]
        public void OutsideWorkspace_TestLocationStaysNull()
        {
            const string trace =
                "   at Some.Package.Helper() in C:\\other\\Helper.cs:line 3\r\n" +
                "   at Some.Package.Runner() in C:\\other\\Runner.cs:line 7";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\other\Helper.cs", 3), failure);
            Assert.Null(TestStackFrames.Test(trace, Root, failure));
        }

        [Fact]
        public void NoSourceInfo_BothNull()
        {
            const string trace = "   at MyTests.Thing.Works()\r\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj)";
            Assert.Null(TestStackFrames.Failure(trace, Root));
            Assert.Null(TestStackFrames.Test(trace, Root, null));
            Assert.Null(TestStackFrames.Failure(null, Root));
            Assert.Null(TestStackFrames.Test(null, Root, null));
        }

        // The runtime localizes the frame wording ("in …:Zeile 42."), which is why the regex anchors on the
        // path shape instead of the words — the test location rule inherits that.
        [Fact]
        public void LocalizedFrames_StillResolveBothEnds()
        {
            const string trace =
                "   bei MyTests.Asserts.Gleich(Int32 a, Int32 b) in C:\\ws\\MyTests\\Asserts.cs:Zeile 5.\r\n" +
                "   bei MyTests.Thing.Works() in C:\\ws\\MyTests\\Thing.cs:Zeile 20.";
            var failure = TestStackFrames.Failure(trace, Root);
            Assert.Equal((@"C:\ws\MyTests\Asserts.cs", 5), failure);
            Assert.Equal((@"C:\ws\MyTests\Thing.cs", 20), TestStackFrames.Test(trace, Root, failure));
        }

        /// <summary>
        /// A RELATIVE frame path is judged against the workspace root, not against the process working
        /// directory. This copy of the prefix rule used to call <c>GetFullPath</c> on the frame path
        /// directly, which in the VS host resolves against devenv's install folder — so a relative frame
        /// was measured from an origin nothing else in the product uses, and reported outside the
        /// workspace. Converging on <see cref="WorkspacePath.IsUnderRoot"/> is what fixes it, and this is
        /// what makes that convergence load-bearing rather than a tidy-up.
        /// </summary>
        [Fact]
        public void ARelativeFramePath_IsJudgedAgainstTheWorkspace_NotTheProcessDirectory()
        {
            Assert.True(TestStackFrames.IsUnderRoot(@"MyTests\Thing.cs", Root));
            Assert.True(TestStackFrames.IsUnderRoot(@"MyTests/Thing.cs", Root));
        }
    }
}
