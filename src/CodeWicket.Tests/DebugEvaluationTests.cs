using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The rules behind <c>read_expression</c> (issue #73, rung 3). Every one of these was learned by
    /// measuring a live debugger, and every one of them fails SILENTLY when got wrong — which is why they
    /// live in Core rather than in the VS-only evaluator that reads them.
    /// </summary>
    public class DebugEvaluationTests
    {
        // SIDE_EFFECT is the debugger saying, unambiguously, that it declined because the expression runs
        // code. Measured: this.ToString() under the guard.
        [Fact]
        public void SideEffect_IsRefused()
        {
            Assert.Equal(
                DebugValueKind.NeedsCode,
                DebugValueRules.Classify(guarded: true, hasError: true, hasSideEffect: true));
        }

        // ...and it is the ATTRIBUTE that decides, not the HRESULT, which was S_OK for every refusal
        // observed. A classifier that never sees an hr cannot make that mistake - which is the point of
        // it taking flags rather than a result object.
        [Fact]
        public void NoFlags_IsAValue()
        {
            Assert.Equal(
                DebugValueKind.Value,
                DebugValueRules.Classify(guarded: true, hasError: false, hasSideEffect: false));
        }

        // ERROR without SIDE_EFFECT covers two different things the attributes cannot separate: a
        // property getter refused under the guard, and a plain expression error. Both are "no value".
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Error_IsUnavailable_WhicheverMode(bool guarded)
        {
            Assert.Equal(
                DebugValueKind.Unavailable,
                DebugValueRules.Classify(guarded, hasError: true, hasSideEffect: false));
        }

        // Which is why the ambiguity is STATED. Under the guard we cannot tell "your expression is wrong"
        // from "that would have run code", so the caller is told both are possible and the debugger's own
        // message says which.
        [Fact]
        public void AmbiguousFailure_IsFlagged_OnlyUnderTheGuard()
        {
            Assert.True(DebugValueRules.MayNeedCode(guarded: true, hasError: true, hasSideEffect: false));

            // Unguarded, an error is the expression's fault - nothing was stopping it.
            Assert.False(DebugValueRules.MayNeedCode(guarded: false, hasError: true, hasSideEffect: false));

            // And a side-effect refusal is not ambiguous at all, so it is not flagged as such.
            Assert.False(DebugValueRules.MayNeedCode(guarded: true, hasError: true, hasSideEffect: true));
        }

        /// <summary>
        /// THE subtle one. A guarded read of an object whose display string needs code comes back with no
        /// error flag, EXPANDABLE set, and its value replaced by a notice ("Implicit function evaluation
        /// is turned off by the user"). Nothing in the attributes separates that from a real
        /// DebuggerDisplay string, and matching the text would break in a localized VS — so the summary is
        /// dropped for that shape and the children answer instead.
        /// </summary>
        [Fact]
        public void GuardedExpandable_DoesNotReportItsSummary()
        {
            Assert.False(DebugValueRules.CanReportSummary(
                guarded: true, isExpandable: true, DebugExpansion.YieldedMembers));

            // Everything else keeps its value. A non-expandable result cannot be hiding the notice: one
            // that needed code carries ERROR and never reaches this question.
            Assert.True(DebugValueRules.CanReportSummary(true, false, DebugExpansion.NotAttempted));
            Assert.True(DebugValueRules.CanReportSummary(false, true, DebugExpansion.YieldedMembers));
            Assert.True(DebugValueRules.CanReportSummary(false, false, DebugExpansion.NotAttempted));
        }

        /// <summary>
        /// A NULL is reported as an expandable object of the declared type whose only child is a grouping
        /// row — so dropping its summary renders "there is nothing here" as "an object with nothing in
        /// it" to an agent reading it.
        /// </summary>
        /// <remarks>
        /// Solved by what expansion PRODUCED rather than by detecting null, because there is nothing to
        /// detect with: no attribute means null (all 45 read), and the value token is LANGUAGE-dependent
        /// — C# <c>null</c>, VB <c>Nothing</c> — so matching it would work in one language and quietly
        /// fail in the other.
        /// </remarks>
        [Fact]
        public void GuardedExpandable_ThatExpandedToNothing_KeepsItsSummary()
        {
            Assert.True(DebugValueRules.CanReportSummary(
                guarded: true, isExpandable: true, DebugExpansion.YieldedNothing));
        }

        /// <summary>
        /// The one the bool above could not express, and the bug it caused. A member is evaluated with
        /// expansion OFF, so "did not look" arrived wearing "looked and found nothing" — and the
        /// debugger's refusal notice went back to the agent in the VALUE slot, where it reads as content
        /// rather than as an absence. Reported live:
        /// <c>"_innerException": { "value": "{Implicit function evaluation is turned off by the user}" }</c>.
        /// </summary>
        [Fact]
        public void GuardedExpandable_ThatWasNeverExpanded_DropsItsSummary()
        {
            Assert.False(DebugValueRules.CanReportSummary(
                guarded: true, isExpandable: true, DebugExpansion.NotAttempted));
        }

        // ...but a value we deliberately did not expand BECAUSE the summary is the answer keeps it. The
        // difference is that the decision was ours, made from the type, so there is something to vouch
        // with - and without the distinction, summarizing an array would reinstate the bug above.
        [Fact]
        public void DeliberatelySummarized_KeepsItsSummary()
        {
            Assert.True(DebugValueRules.CanReportSummary(
                guarded: true, isExpandable: true, DebugExpansion.Summarized));
        }

        // ---- Why a member has no value (issue #73) ---------------------------------------------

        [Fact]
        public void AMemberWithAValue_HasNoReason()
        {
            Assert.Null(DebugValueRules.WhyNoValue(DebugValueKind.Value, hasValue: true, isExpandable: false));
        }

        // The three states the agent has to act on differently: run the other tool, drill in, or give up.
        [Fact]
        public void EachMissingValue_SaysWhichKindOfMissing()
        {
            Assert.Equal("needsCode",
                DebugValueRules.WhyNoValue(DebugValueKind.NeedsCode, false, isExpandable: false));
            Assert.Equal("notExpanded",
                DebugValueRules.WhyNoValue(DebugValueKind.Value, false, isExpandable: true));
            Assert.Equal("notReadable",
                DebugValueRules.WhyNoValue(DebugValueKind.Unavailable, false, isExpandable: false));
        }

        // ---- Arrays of primitives are summarized, not walked (issue #73) -----------------------

        // Reported live: _stackTrace expanded to 24 sbyte members with 24 omitted. The cost is the point -
        // every one of those members is a guarded re-evaluation out of a budget of 64.
        [Fact]
        public void PrimitiveArrays_AreSummarized()
        {
            Assert.True(DebugValueRules.IsSummarizedArray("sbyte[]"));
            Assert.True(DebugValueRules.IsSummarizedArray("byte[]"));
            Assert.True(DebugValueRules.IsSummarizedArray("System.Int32[]"));
        }

        // Anything whose elements are worth reading still expands - including string, deliberately.
        [Fact]
        public void OtherArrays_StillExpand()
        {
            Assert.False(DebugValueRules.IsSummarizedArray("string[]"));
            Assert.False(DebugValueRules.IsSummarizedArray("ConsoleApp1.Widget[]"));
            Assert.False(DebugValueRules.IsSummarizedArray("byte[][]"));
            Assert.False(DebugValueRules.IsSummarizedArray("System.Collections.Generic.List<byte>"));
            Assert.False(DebugValueRules.IsSummarizedArray(null));
            // VB spells its arrays Byte(), which simply does not match and keeps today's behaviour.
            Assert.False(DebugValueRules.IsSummarizedArray("Byte()"));
        }

        // ---- The debugger's polymorphic type form (issue #73) ----------------------------------

        // Measured on a live debugger: a member holding a subclass reports BOTH, and the runtime half is
        // the useful one - it is how the agent learns an inner exception's real type without a second
        // call. A naive read keeps the declared type and throws that away.
        [Fact]
        public void RuntimeType_IsTakenFromTheBracedHalf()
        {
            Assert.Equal(
                "System.InvalidOperationException",
                DebugValueRules.RuntimeTypeName("System.Exception {System.InvalidOperationException}"));
        }

        [Fact]
        public void APlainType_SurvivesUntouched()
        {
            Assert.Equal("sbyte[]", DebugValueRules.RuntimeTypeName("sbyte[]"));
            Assert.Null(DebugValueRules.RuntimeTypeName(null));
            Assert.Null(DebugValueRules.RuntimeTypeName("   "));
        }

        // A grouping row carries no type; every real member does. By TYPE and not by NAME, because
        // "Static members" and "Non-Public members" are localized.
        [Fact]
        public void GroupingRows_AreNotRealMembers()
        {
            Assert.False(DebugValueRules.IsRealMember(new DebugEvaluationChild("Static members")));
            Assert.False(DebugValueRules.IsRealMember(new DebugEvaluationChild("Non-Public members")));
            Assert.True(DebugValueRules.IsRealMember(
                new DebugEvaluationChild("_logger") { Type = "ILogger<HelloWorldService>" }));
        }

        // Flattening a grouping row must not become a second level of OBJECTS. The container walk has
        // its own bound; MaxDepth still means one level of members.
        [Fact]
        public void FlatteningContainers_DoesNotRelaxTheObjectDepth()
        {
            Assert.Equal(1, DebugEvalLimits.MaxDepth);
        }

        // A member says whether it is a getter, so "no value under the guard" is answerable without
        // trying it. Silent on fields - the majority - so the payload does not grow a flag per member.
        [Fact]
        public void AMember_SaysWhetherItIsAGetter()
        {
            var field = new DebugEvaluationChild("_message") { Type = "string", Value = "\"Test\"" };
            var getter = new DebugEvaluationChild("Message") { Type = "string", IsProperty = true };

            Assert.False(field.IsProperty);
            Assert.True(getter.IsProperty);

            // Both are real members: a getter with no value is still in scope, and dropping it would
            // make the object look emptier than it is.
            Assert.True(DebugValueRules.IsRealMember(field));
            Assert.True(DebugValueRules.IsRealMember(getter));
        }

        // A cut that does not announce itself reads as the whole value.
        [Fact]
        public void LongValue_SaysHowMuchItDropped()
        {
            var value = new string('x', DebugEvalLimits.MaxValueChars + 25);

            var clamped = DebugValueRules.ClampValue(value);

            Assert.NotNull(clamped);
            Assert.Contains("+25 more characters", clamped);
            Assert.StartsWith(new string('x', DebugEvalLimits.MaxValueChars), clamped);
        }

        // Expansion is ONE level, and the constant says so. An object graph has no bottom: the first
        // build of this expanded every expandable member, which walked `this` into its logger into its
        // factory and froze the IDE. The bound is enforced structurally (a parameter the caller must
        // pass), so this pins the number the remarks reason about.
        [Fact]
        public void ExpansionIsOneLevel()
        {
            Assert.Equal(1, DebugEvalLimits.MaxDepth);
        }

        // The backstop, which is NOT redundant with a correct depth rule - it is what makes the next bug
        // in the depth rule survivable, and what bounds the arithmetic even when every call behaves.
        [Fact]
        public void Budget_StopsAtTheCeiling()
        {
            var budget = new DebugEvalBudget();

            for (var i = 0; i < DebugEvalLimits.MaxEvaluationsPerCall; i++)
                Assert.True(budget.TryTake());

            Assert.False(budget.TryTake());
            Assert.True(budget.Exhausted);
        }

        // A cut that does not announce itself reads as the whole answer.
        [Fact]
        public void Budget_CountsWhatItRefused()
        {
            var budget = new DebugEvalBudget();
            while (budget.TryTake())
            {
            }

            Assert.Equal(1, budget.Skipped);
            budget.TryTake();
            Assert.Equal(2, budget.Skipped);
        }

        // Ten expressions each expanding a full member list must not be able to outrun the ceiling -
        // this is the arithmetic that froze the IDE, expressed as a number rather than a hope.
        [Fact]
        public void WorstCaseShape_CannotExceedTheBudget()
        {
            var worstCase = DebugEvalLimits.MaxExpressions * (1 + DebugEvalLimits.MaxChildren);
            Assert.True(worstCase > DebugEvalLimits.MaxEvaluationsPerCall,
                "if the worst case fits inside the budget the budget is not bounding anything");

            var budget = new DebugEvalBudget();
            var taken = 0;
            for (var i = 0; i < worstCase; i++)
            {
                if (budget.TryTake())
                    taken++;
            }

            Assert.Equal(DebugEvalLimits.MaxEvaluationsPerCall, taken);
        }

        // ---- What an expansion DROPPED (issue #73) ---------------------------------------------

        // Nothing lost is null, not an object saying zero. The payload renders it only when there is
        // something to render, so "no members were dropped" and "some were" cannot look alike.
        [Fact]
        public void NothingDropped_IsNull()
        {
            Assert.Null(DebugOmission.For(Array.Empty<string>(), DebugOmissionReason.MemberCap, false));
            Assert.Null(DebugOmission.For(null, DebugOmissionReason.MemberCap, false));
        }

        // The names are the point: a count declares the truncation and still leaves the reader unable to
        // tell "nothing I needed" from "the field I am looking for, silently dropped".
        [Fact]
        public void Dropped_MembersAreNamed()
        {
            var omission = DebugOmission.For(
                new[] { "_capacity", "_version", "_syncRoot" }, DebugOmissionReason.MemberCap, false);

            Assert.NotNull(omission);
            Assert.Equal(3, omission!.Count);
            Assert.Equal(new[] { "_capacity", "_version", "_syncRoot" }, omission.Names);
            Assert.False(omission.CountIsFloor);
        }

        // A truncated ENUMERATION is a loss even when the loop above it dropped nothing it could name -
        // otherwise the one case where the count is least trustworthy is the one that reports nothing.
        [Fact]
        public void TruncatedEnumeration_IsReportedEvenWithNoNames()
        {
            var omission = DebugOmission.For(Array.Empty<string>(), DebugOmissionReason.MemberCap, true);

            Assert.NotNull(omission);
            Assert.True(omission!.CountIsFloor);
        }

        // ...and when both happened, the count and the names are a FLOOR. Expanding a 1,000-element array
        // reads the first 200: reporting 176 as exact under-reports by an order of magnitude while looking
        // precise, which is worse than saying nothing.
        [Fact]
        public void TruncatedEnumeration_MakesTheCountAFloor()
        {
            var omission = DebugOmission.For(new[] { "[24]", "[25]" }, DebugOmissionReason.MemberCap, true);

            Assert.Equal(2, omission!.Count);
            Assert.True(omission.CountIsFloor);
        }

        // The member cap wins a tie, and the tie is reachable: the member that fills the cap can be the
        // one that exhausts the budget. Right way round because the cap is a property of THIS value and
        // would have stopped the expansion on its own.
        [Fact]
        public void BothBoundsAtOnce_BlamesTheMemberCap()
        {
            Assert.Equal(
                DebugOmissionReason.MemberCap,
                DebugOmission.ReasonFor(atMemberCap: true, budgetExhausted: true));
        }

        [Fact]
        public void BudgetAlone_BlamesTheBudget()
        {
            Assert.Equal(
                DebugOmissionReason.EvaluationBudget,
                DebugOmission.ReasonFor(atMemberCap: false, budgetExhausted: true));
        }

        // Named beside the enum so a value added later cannot reach the payload nameless.
        [Fact]
        public void EveryReason_HasAWireName()
        {
            foreach (DebugOmissionReason reason in Enum.GetValues(typeof(DebugOmissionReason)))
            {
                var name = DebugOmission.WireName(reason);
                Assert.False(string.IsNullOrEmpty(name));
                // Matches the payload's other string values, which are camelCase.
                Assert.True(char.IsLower(name[0]), reason + " is spelled " + name);
            }
        }

        [Fact]
        public void ShortValue_IsUntouched()
        {
            Assert.Equal("42", DebugValueRules.ClampValue("42"));
            Assert.Null(DebugValueRules.ClampValue(null));
        }
    }

    /// <summary>
    /// Two breakpoints on one line: the debugger offers <c>BreakpointLastHit</c>, one breakpoint, and
    /// nothing separates two sharing a file and a line (issue #73).
    /// </summary>
    public class AmbiguousBreakpointTests
    {
        private static DebugStateFormatter.BreakpointAtStop Mine(string? condition = null) =>
            new DebugStateFormatter.BreakpointAtStop("code-wicket:abc123", condition) { FromThisConversation = true };

        private static DebugStateFormatter.BreakpointAtStop Theirs(string? condition = null) =>
            new DebugStateFormatter.BreakpointAtStop(null, condition);

        // One breakpoint is the common case and must read exactly as it did before this existed.
        [Fact]
        public void OneBreakpoint_ReadsAsBefore()
        {
            var single = DebugStateFormatter.DescribeAmbiguousBreakpoints(new[] { Mine("depth == 5") });

            Assert.Equal(
                DebugStateFormatter.DescribeBreakpoint("code-wicket:abc123", "depth == 5", true),
                single);
        }

        // THE case. The user's unconditional breakpoint and the agent's conditional one on the same
        // line: naming either would be a coin toss, so neither is named.
        [Fact]
        public void TwoBreakpoints_RefuseToNameOne()
        {
            var text = DebugStateFormatter.DescribeAmbiguousBreakpoints(new[] { Theirs(), Mine("i == 47") });

            Assert.Contains("2 breakpoints are set on this line", text);
            Assert.Contains("cannot be determined", text);
            Assert.Contains("set by the user", text);
            Assert.Contains("set by this conversation", text);
            Assert.Contains("i == 47", text);
        }

        // The sharp risk is not misattribution but a false claim about the PROGRAM: a listed condition
        // must never read as one that was met, or the agent concludes i really was 47.
        [Fact]
        public void ListedCondition_IsNotPresentedAsMet()
        {
            var text = DebugStateFormatter.DescribeAmbiguousBreakpoints(new[] { Theirs(), Mine("i == 47") });

            Assert.Contains("may or may not have been met", text);
        }

        // With no conditions in play there is nothing to disclaim, and the caveat would be noise.
        [Fact]
        public void NoConditions_NoCaveat()
        {
            var text = DebugStateFormatter.DescribeAmbiguousBreakpoints(new[] { Theirs(), Mine() });

            Assert.DoesNotContain("may or may not have been met", text);
            Assert.Contains("cannot be determined", text);
        }

        [Fact]
        public void NoBreakpoints_SaysNothing()
        {
            Assert.Null(DebugStateFormatter.DescribeAmbiguousBreakpoints(
                new DebugStateFormatter.BreakpointAtStop[0]));
        }
    }

    /// <summary>
    /// The ambient half: the agent cannot ASK whether a debug session exists, so it is told — and told
    /// as little as possible, because everything else about the debugger it can now pull (issue #73).
    /// </summary>
    public class AmbientDebugStateTests
    {
        private static string Render(DebugSessionInfo session) =>
            WorkspaceContextFormatter.Format(
                new WorkspaceSnapshot { SolutionName = "S", DebugSession = session },
                workingDirectory: null) ?? string.Empty;

        // The common case is nobody debugging, and it must cost nothing: a line saying "no debug
        // session" on every prompt is exactly the noise the #95 tiering exists to avoid.
        [Fact]
        public void NoSession_SaysNothing()
        {
            var block = WorkspaceContextFormatter.Format(
                new WorkspaceSnapshot { SolutionName = "S" }, workingDirectory: null) ?? string.Empty;

            Assert.DoesNotContain("Debugger", block);
        }

        // Running, not stopped: the agent still needs to know, because the hazard here is EDITING - a
        // changed source file leaves the running binary no longer matching it.
        [Fact]
        public void Running_WarnsAboutEditing()
        {
            var block = Render(new DebugSessionInfo(IsStopped: false));

            Assert.Contains("Debugger: running", block);
            Assert.Contains("Editing source", block);
        }

        // Stopped: where, why, and which stop - the last being what lets a pushed <debug-state> block be
        // told apart from the situation now.
        [Fact]
        public void Stopped_NamesTheStopAndItsIdentity()
        {
            var block = Render(new DebugSessionInfo(IsStopped: true)
            {
                File = @"C:\src\HelloWorldService.cs",
                Line = 34,
                Method = "ConsoleApp1.HelloWorldService.Recurse",
                Reason = "Stopped at a breakpoint you set: check the recursion bottoms out",
                ProcessId = 4812,
                StopNumber = 3,
            });

            Assert.Contains("STOPPED at HelloWorldService.cs:34", block);
            Assert.Contains("Recurse", block);
            Assert.Contains("stop 3", block);
            Assert.Contains("process 4812", block);
            Assert.Contains("check the recursion bottoms out", block);
            Assert.Contains("read_expression", block);
        }

        // A stop with no location still reports the stop. Losing the whole line because one field is
        // missing would hide the fact the block exists to carry.
        [Fact]
        public void StoppedWithoutLocation_StillReportsTheStop()
        {
            var block = Render(new DebugSessionInfo(IsStopped: true) { StopNumber = 1 });

            Assert.Contains("Debugger: STOPPED", block);
        }
    }

}
