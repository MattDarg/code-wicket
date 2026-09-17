using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The rendering of a break-mode capture (issue #73, rung 2) — the block that goes on the wire and
    /// the label on the composer chip.
    /// <para>
    /// Almost every test here is about a REDUCTION being stated. The reader is a model reasoning about
    /// why a value is what it is, so a frame list silently cut reads as the whole stack, and a frame
    /// with no locals listed reads as a frame with no locals — claims about the program rather than
    /// about what we chose to carry. That is the <see cref="WorkspaceDiagnostics"/> rule, and it is the
    /// half of this feature most likely to be wrong in a way nobody notices.
    /// </para>
    /// </summary>
    public sealed class DebugStateFormatterTests
    {
        private const string Root = @"C:\ws";

        private static DebugFrame Frame(
            string method = "Ns.Type.Method(int depth)",
            string? file = @"C:\ws\src\Type.cs", int line = 33,
            IEnumerable<DebugLocal>? locals = null, int omittedLocals = 0) =>
            new DebugFrame(method)
            {
                File = file,
                Line = line,
                Locals = locals?.ToList() ?? new List<DebugLocal>(),
                OmittedLocals = omittedLocals,
            };

        private static DebugStateCapture Capture(
            IEnumerable<DebugFrame>? frames = null, int external = 0, int omittedFrames = 0,
            string? thread = null, string? stopReason = null) =>
            new DebugStateCapture
            {
                Frames = frames?.ToList() ?? new List<DebugFrame> { Frame() },
                ExternalFrames = external,
                OmittedFrames = omittedFrames,
                ThreadName = thread,
                StopReason = stopReason,
            };

        [Fact]
        public void TheHeaderNamesWhereItStopped()
        {
            var text = DebugStateFormatter.Format(Capture(thread: "Main Thread"), Root);

            Assert.Contains("Ns.Type.Method(int depth)", text);
            Assert.Contains("src/Type.cs:33", text);
            Assert.Contains("Main Thread", text);
        }

        /// <summary>Paths are shown relative to the workspace, like every other location we render.</summary>
        [Fact]
        public void PathsAreWorkspaceRelative()
        {
            var text = DebugStateFormatter.Format(Capture(), Root);

            Assert.DoesNotContain(@"C:\ws\src", text);
            Assert.Contains("src/Type.cs:33", text);
        }

        [Fact]
        public void LocalsAreRenderedWithTheirTypes()
        {
            var text = DebugStateFormatter.Format(
                Capture(new[]
                {
                    Frame(locals: new[]
                    {
                        new DebugLocal("depth") { Type = "int", Value = "9" },
                        new DebugLocal("order") { Type = "Order", Value = "{Order}" },
                    }),
                }),
                Root);

            Assert.Contains("depth (int) = 9", text);
            Assert.Contains("order (Order) = {Order}", text);
        }

        /// <summary>
        /// "None shown" and "none there" are different facts, and only the second is about the code. A
        /// frame we stopped reading must not read as a frame with nothing in it.
        /// </summary>
        [Fact]
        public void OmittedLocalsAreStatedAndDistinctFromHavingNone()
        {
            var stoppedReading = DebugStateFormatter.Format(
                Capture(new[] { Frame(locals: new[] { new DebugLocal("a") { Value = "1" } }, omittedLocals: 7) }), Root);
            var genuinelyEmpty = DebugStateFormatter.Format(Capture(new[] { Frame() }), Root);

            Assert.Contains("7 more not shown", stoppedReading);
            Assert.DoesNotContain("no locals in scope", stoppedReading);

            Assert.Contains("no locals in scope", genuinelyEmpty);
            Assert.DoesNotContain("more not shown", genuinelyEmpty);
        }

        /// <summary>A stack cut at the frame cap must not read as a stack that ended there.</summary>
        [Fact]
        public void OmittedFramesAreStated()
        {
            var text = DebugStateFormatter.Format(Capture(omittedFrames: 5), Root);

            Assert.Contains("5 more frames of your code not shown", text);
        }

        /// <summary>
        /// Stated even when there are none. An absent line would read as "the stack ends here", which is
        /// a claim about the program rather than about what was carried.
        /// </summary>
        [Fact]
        public void ExternalFramesAreAlwaysStatedIncludingNone()
        {
            Assert.Contains("External code: none", DebugStateFormatter.Format(Capture(), Root));
            Assert.Contains("External code: 9 frames", DebugStateFormatter.Format(Capture(external: 9), Root));
        }

        /// <summary>
        /// A long value is bounded and says how much it dropped — the capture rides a prompt, and one
        /// collection would otherwise be the whole of it.
        /// </summary>
        [Fact]
        public void ALongValueIsClampedAndSaysSo()
        {
            var huge = new string('x', DebugStateLimits.MaxValueChars + 500);
            var text = DebugStateFormatter.Format(
                Capture(new[] { Frame(locals: new[] { new DebugLocal("big") { Value = huge } }) }), Root);

            Assert.DoesNotContain(huge, text);
            Assert.Contains("(+500 chars)", text);
        }

        /// <summary>
        /// The debugger declining to evaluate something and the value being empty are different answers,
        /// and an agent reasoning about a null would act on the difference.
        /// </summary>
        [Fact]
        public void AnUnevaluatableValueSaysSoRatherThanShowingEmpty()
        {
            var text = DebugStateFormatter.Format(
                Capture(new[] { Frame(locals: new[] { new DebugLocal("locked") { Type = "Thing" } }) }), Root);

            Assert.Contains("locked (Thing) = (unavailable)", text);
        }

        /// <summary>A frame whose location could not be resolved still contributes its name.</summary>
        [Fact]
        public void AFrameWithNoLocationIsStillListed()
        {
            var text = DebugStateFormatter.Format(
                Capture(new[] { Frame(method: "Ns.Type.Mystery()", file: null, line: 0) }), Root);

            Assert.Contains("Ns.Type.Mystery()", text);
        }

        /// <summary>
        /// Nine frames on one line is what a recursion looks like, and the renderer must not collapse or
        /// dedupe them — the repetition IS the finding.
        /// </summary>
        /// <summary>
        /// Measured on the first real capture (in Visual Studio, 2026-08-26): 12 of 26 local lines were the SAME
        /// <c>this = {TheTypeTheMethodIsAlreadyNamedAfter}</c>, once per frame. Nearly half a payload
        /// that is bounded because it rides a prompt.
        /// </summary>
        [Fact]
        public void ThisIsDroppedWhenItsValueOnlyRepeatsItsType()
        {
            var block = DebugStateFormatter.Format(Capture(new[]
            {
                Frame(locals: new[]
                {
                    new DebugLocal("depth") { Type = "int", Value = "9" },
                    new DebugLocal("this") { Type = "Foo.Service", Value = "{Foo.Service}" },
                }),
            }), null);

            Assert.Contains("depth (int) = 9", block);
            Assert.DoesNotContain("this", block);
        }

        /// <summary>
        /// ...and only then. A type with a real ToString is the most useful line on the frame, and a
        /// local that is not `this` keeps its line because its NAME is informative even when its value
        /// is not - "service is in scope" is a fact, where "this exists" is implied by the frame.
        /// </summary>
        [Fact]
        public void AMeaningfulThisSurvives()
        {
            var block = DebugStateFormatter.Format(Capture(new[]
            {
                Frame(locals: new[]
                {
                    new DebugLocal("this") { Type = "Foo.Order", Value = "{Order Id=42 Status=Paid}" },
                    new DebugLocal("service") { Type = "Foo.Service", Value = "{Foo.Service}" },
                }),
            }), null);

            Assert.Contains("{Order Id=42 Status=Paid}", block);
            Assert.Contains("service (Foo.Service) = {Foo.Service}", block);
        }

        /// <summary>
        /// A frame whose ONLY local was the redundant `this` must not then claim it had none - that is
        /// a statement about the code rather than about what was carried.
        /// </summary>
        [Fact]
        public void AFrameLeftWithNothingToShowSaysSo()
        {
            var block = DebugStateFormatter.Format(Capture(new[]
            {
                Frame(locals: new[] { new DebugLocal("this") { Type = "Foo.Service", Value = "{Foo.Service}" } }),
            }), null);

            Assert.Contains("(no locals in scope)", block);
        }

        // ---- Why the debugger stopped (in Visual Studio, 2026-08-26, Break When Thrown) --------------------

        /// <summary>
        /// The debugger's own value form is two layers of punctuation deep: <c>{"Test"}</c>, braces
        /// because it is an object and quotes because the message is a string. Unwrapped and put
        /// beside the type it becomes the shape .NET prints exceptions in.
        /// </summary>
        [Fact]
        public void AnExceptionIsDescribedTheWayDotNetPrintsOne()
        {
            Assert.Equal(
                "Exception: System.Exception: Test",
                DebugStateFormatter.DescribeException("System.Exception", "{\"Test\"}"));
        }

        [Theory]
        // Only a MATCHED pair is stripped, so anything not wrapped survives untouched...
        [InlineData("System.Exception", "Test", "Exception: System.Exception: Test")]
        // ...and punctuation INSIDE the message is the message's, not the debugger's.
        [InlineData("Foo.Bar", "{\"a {b} c\"}", "Exception: Foo.Bar: a {b} c")]
        // Half an answer is still an answer.
        [InlineData("System.Exception", "", "Exception: System.Exception")]
        [InlineData("", "{\"Test\"}", "Exception: Test")]
        public void DescribeException_UnwrapsConservatively(string type, string value, string expected)
        {
            Assert.Equal(expected, DebugStateFormatter.DescribeException(type, value));
        }

        [Fact]
        public void NothingToDescribeIsNull()
        {
            Assert.Null(DebugStateFormatter.DescribeException(null, null));
        }

        /// <summary>
        /// On its own line, not in brackets on the end of the first. Parenthesised it read
        /// "at HelloWorldService.cs:40 (exception: {"Test"})" - the most important fact about the stop,
        /// buried in an aside on a line that is already long, and an exception message is unbounded so
        /// the aside can outgrow the sentence holding it.
        /// </summary>
        [Fact]
        public void TheStopReasonGetsItsOwnLine()
        {
            var block = DebugStateFormatter.Format(
                Capture(stopReason: "Exception: System.Exception: Test"), null);
            var lines = block.Replace("\r\n", "\n").Split('\n');

            Assert.DoesNotContain("(Exception:", block);
            Assert.Contains("Exception: System.Exception: Test", lines);
        }

        /// <summary>
        /// <c>$exception</c> is the debugger's exception SLOT, not a local: it resolves the same on
        /// every frame. The second real capture carried twelve identical copies of it under a header
        /// that already said the same thing in better form.
        /// </summary>
        [Fact]
        public void TheExceptionIsNotRepeatedOnEveryFrameWhenTheHeaderHasIt()
        {
            var block = DebugStateFormatter.Format(Capture(
                new[]
                {
                    Frame(locals: new[]
                    {
                        new DebugLocal("depth") { Type = "int", Value = "10" },
                        new DebugLocal("$exception") { Type = "System.Exception", Value = "{\"Test\"}" },
                    }),
                    Frame(locals: new[]
                    {
                        new DebugLocal("$exception") { Type = "System.Exception", Value = "{\"Test\"}" },
                    }),
                },
                stopReason: "Exception: System.Exception: Test"), null);

            Assert.Contains("depth (int) = 10", block);
            Assert.DoesNotContain("$exception", block);
            // The frame left with nothing else still says so rather than falling silent.
            Assert.Contains("(no locals in scope)", block);
        }

        /// <summary>
        /// ...and ONLY when the header has it. With no stop reason those lines are the only record of
        /// the exception there is, so dropping them would be a reduction rather than a de-duplication.
        /// </summary>
        [Fact]
        public void TheExceptionSurvivesOnTheFramesWhenTheHeaderCouldNotReadIt()
        {
            var block = DebugStateFormatter.Format(Capture(new[]
            {
                Frame(locals: new[]
                {
                    new DebugLocal("$exception") { Type = "System.Exception", Value = "{\"Test\"}" },
                }),
            }), null);

            Assert.Contains("$exception (System.Exception)", block);
        }

        /// <summary>
        /// A plain user breakpoint says so and nothing more — the common case, and it should stay
        /// quiet. It is a statement that a breakpoint is HERE, deliberately not a causal claim: the
        /// debugger only offers "last hit", so the caller gates it on the location matching.
        /// </summary>
        [Fact]
        public void AUserBreakpointIsNamedAndNothingIsInventedAboutIt()
        {
            Assert.Equal("Breakpoint", DebugStateFormatter.DescribeBreakpoint(null, null, false));
        }

        /// <summary>
        /// The half worth having: rung 1 tags what the agent sets, so rung 2 can tell it that this is
        /// the stop its OWN instrumentation asked for. That is the two rungs closing into a loop.
        /// </summary>
        [Fact]
        public void ABreakpointThisConversationSetSaysSo()
        {
            var described = DebugStateFormatter.DescribeBreakpoint(
                Breakpoints.Tag("conv-1"), "depth == 9", fromThisConversation: true);

            Assert.Contains("set by this conversation", described);
            Assert.Contains("condition: depth == 9", described);
        }

        /// <summary>
        /// Ours, but from a conversation that is not this one - a real state, since breakpoints
        /// outlive the chat window and devenv (rung 1's whole reason for persisting the tag).
        /// </summary>
        [Fact]
        public void AnAgentBreakpointFromAnotherConversationIsDistinguished()
        {
            var described = DebugStateFormatter.DescribeBreakpoint(
                Breakpoints.Tag("some-older-conversation"), null, fromThisConversation: false);

            Assert.Contains("earlier conversation", described);
        }

        /// <summary>A tag that is not ours is the user's breakpoint, whatever it says.</summary>
        [Fact]
        public void AForeignTagIsNotClaimedAsOurs()
        {
            Assert.Equal(
                "Breakpoint",
                DebugStateFormatter.DescribeBreakpoint("some-other-tool", null, false));
        }

        [Fact]
        public void ABreakpointReasonRendersOnItsOwnLineToo()
        {
            var block = DebugStateFormatter.Format(
                Capture(stopReason: "Breakpoint — set by this conversation (condition: depth == 9)"), null);
            var lines = block.Replace("\r\n", "\n").Split('\n');

            Assert.Contains("Breakpoint — set by this conversation (condition: depth == 9)", lines);
        }

        [Fact]
        public void RecursionRendersEveryFrame()
        {
            var frames = Enumerable.Range(0, 9).Select(_ => Frame(method: "Ns.Type.Recurse(int depth)"));

            var text = DebugStateFormatter.Format(Capture(frames), Root);

            Assert.Equal(9, text.Split('\n').Count(l => l.Contains("Ns.Type.Recurse")) - 1); // -1 for the header
        }

        [Fact]
        public void AnEmptyCaptureSaysSoRatherThanRenderingNothing()
        {
            var text = DebugStateFormatter.Format(new DebugStateCapture(), Root);

            Assert.Contains("no frames could be read", text);
        }

        // ---- the chip's label ----------------------------------------------------------------------

        /// <summary>
        /// Specific enough to tell two captures apart in one conversation — a chip reading only "Debug
        /// state" twice tells the user nothing about which is which.
        /// </summary>
        [Fact]
        public void TheLabelNamesTheMethodAndCountsEveryFrame()
        {
            var label = DebugStateFormatter.Label(Capture(omittedFrames: 3));

            Assert.Contains("Method", label);
            // The TYPE is dropped too, not just the namespace - measured on a rendered chip, where
            // "HelloWorldService.SayHelloWorld" cut the frame count off the end of the strip.
            Assert.DoesNotContain("Type.", label);
            // Counts what was CAPTURED plus what was omitted: the user's stack is that deep either way.
            Assert.Contains("4 frames", label);
        }

        /// <summary>A qualified name fills the chip and pushes out what varies - measured, not assumed.</summary>
        [Fact]
        public void TheLabelDropsTheNamespace()
        {
            var label = DebugStateFormatter.Label(
                Capture(new[] { Frame(method: "Very.Long.Namespace.Chain.Service.Handle(string s)") }));

            Assert.Contains("Handle", label);
            Assert.DoesNotContain("Service", label);
            Assert.DoesNotContain("Very.Long", label);
        }

        [Fact]
        public void AnEmptyCaptureStillHasALabel()
        {
            Assert.False(string.IsNullOrWhiteSpace(DebugStateFormatter.Label(new DebugStateCapture())));
        }
    }
}
