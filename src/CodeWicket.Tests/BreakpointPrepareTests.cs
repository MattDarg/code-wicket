using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The decision half of <c>set_breakpoint</c>/<c>clear_breakpoints</c> (issue #73, rung 1), extracted
    /// from <c>VsToolCatalog</c> for the same reason <see cref="CodeFixScopeTests"/>' subject was: the tool
    /// catalog is net472 + the VS SDK and this project cannot reference it, so a rule left in there is a
    /// rule no test can reach. The DTE half — actually adding the breakpoint — stays there and needs a
    /// live devenv.
    /// </summary>
    public sealed class BreakpointPrepareTests
    {
        private const string Root = @"C:\ws";

        private static BreakpointSpec At(string file, int line) => new BreakpointSpec(file, line);

        /// <summary>
        /// Validation and normalisation are the same on both tool names, so the cases about them go
        /// through the one that may carry an expression. The cases about WHICH NAME MAY are at the
        /// bottom and pass the flag explicitly.
        /// </summary>
        private static BreakpointBatch Prepare(
            IReadOnlyList<BreakpointSpec>? specs, string? root, bool allowExpressions = true) =>
            Breakpoints.Prepare(specs, root, allowExpressions);

        // ---- what to do about a breakpoint already at the location -----------------------------

        /// <summary>
        /// The user's breakpoint is REFUSED, not replaced and not added beside.
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-28: clearing a breakpoint from the gutter removes EVERY breakpoint at that
        /// location, so a second one added beside the user's is invisible there, unattributable when it
        /// is hit, and destroyed by their ordinary click. The host had this as a string assigned in one
        /// branch and read forty lines later, and the branch fell through into the Add: the tool
        /// answered "conflict — choose another line" while VS had already taken the breakpoint and
        /// stamped our tag on it, putting a location the USER was watching inside clear_breakpoints'
        /// blast radius.
        /// <para>
        /// This pins the RULE. It cannot pin the host obeying it — nothing offline can reach DTE — which
        /// is why the fix was to make the outcome a value the caller must switch on rather than a flag
        /// it can forget to act on.
        /// </para>
        /// </remarks>
        [Fact]
        public void AUserOwnedBreakpointIsRefusedRatherThanReplacedOrDuplicated()
        {
            Assert.Equal(
                ExistingBreakpointAction.RefuseAsConflict,
                Breakpoints.Decide(exists: true, isOurs: false));
        }

        /// <summary>Ours is a revision: removed and re-added, which is what makes the tool idempotent.</summary>
        [Fact]
        public void OurOwnBreakpointIsReplaced()
        {
            Assert.Equal(ExistingBreakpointAction.Replace, Breakpoints.Decide(exists: true, isOurs: true));
        }

        /// <summary>An empty location is simply placed — and isOurs is meaningless when nothing is there.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnEmptyLocationIsPlaced(bool isOurs)
        {
            Assert.Equal(ExistingBreakpointAction.Place, Breakpoints.Decide(exists: false, isOurs));
        }

        [Fact]
        public void EmptyRequest_isRefused()
        {
            Assert.NotNull(Prepare(null, Root).Error);
            Assert.NotNull(Prepare(new List<BreakpointSpec>(), Root).Error);
        }

        /// <summary>
        /// A malformed row refuses the WHOLE batch and sets nothing — the "couldn't run" half of the result
        /// contract. Setting the valid rows and reporting the rest would be worse than it sounds: the agent
        /// reads a success, believes the file is instrumented, and only finds out at the breakpoint that
        /// never fires.
        /// </summary>
        [Fact]
        public void OneBadRow_refusesTheWholeBatch()
        {
            var batch = Prepare(new[] { At("Good.cs", 10), At("Bad.cs", 0) }, Root);

            Assert.NotNull(batch.Error);
            Assert.Empty(batch.Specs);
        }

        /// <summary>The refusal names the row and the file, so a model with twenty of them fixes the right one.</summary>
        [Fact]
        public void Refusal_namesTheOffendingRow()
        {
            var batch = Prepare(new[] { At("A.cs", 1), At("B.cs", 2), At("Bad.cs", -3) }, Root);

            Assert.Contains("breakpoint 3", batch.Error);
            Assert.Contains("Bad.cs", batch.Error);
        }

        [Fact]
        public void MissingFile_isRefused()
        {
            Assert.NotNull(Prepare(new[] { At("   ", 4) }, Root).Error);
        }

        [Fact]
        public void ZeroOrNegativeHitCount_isRefused()
        {
            var batch = Prepare(new[] { At("A.cs", 4) with { HitCount = 0 } }, Root);

            Assert.NotNull(batch.Error);
            // The message has to say what to do instead, or the model's next move is to try -1.
            Assert.Contains("omit it", batch.Error);
        }

        /// <summary>
        /// Over the cap the request is REFUSED, never trimmed. A silently shortened batch leaves the agent
        /// believing it instrumented lines it did not, and the breakpoints it is waiting on never fire.
        /// </summary>
        [Fact]
        public void OverTheCap_isRefusedAndSaysBothNumbers()
        {
            var many = Enumerable.Range(1, Breakpoints.MaxPerCall + 1).Select(i => At("A.cs", i)).ToList();

            var batch = Prepare(many, Root);

            Assert.NotNull(batch.Error);
            Assert.Empty(batch.Specs);
            Assert.Contains(many.Count.ToString(), batch.Error);
            Assert.Contains(Breakpoints.MaxPerCall.ToString(), batch.Error);
        }

        [Fact]
        public void AtTheCap_isAccepted()
        {
            var many = Enumerable.Range(1, Breakpoints.MaxPerCall).Select(i => At("A.cs", i)).ToList();

            var batch = Prepare(many, Root);

            Assert.Null(batch.Error);
            Assert.Equal(Breakpoints.MaxPerCall, batch.Specs.Count);
        }

        /// <summary>
        /// A relative path is rooted against the directory the AGENT measured it from. Getting this wrong
        /// does not error — it names a different real file, and the breakpoint simply never gets hit.
        /// </summary>
        [Fact]
        public void RelativePath_isRootedAgainstTheGivenRoot()
        {
            var batch = Prepare(new[] { At("proj/File.cs", 12) }, Root);

            Assert.Null(batch.Error);
            Assert.Equal(@"C:\ws\proj\File.cs", batch.Specs[0].File);
        }

        /// <summary>
        /// An empty condition is not a condition. Left as "" it reaches the debugger as "always" while the
        /// card shows a condition the user cannot read — two descriptions of different breakpoints.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankConditionAndReason_becomeNull(string blank)
        {
            var batch = Prepare(
                new[] { At("A.cs", 3) with { Condition = blank, Reason = blank } }, Root);

            Assert.Null(batch.Specs[0].Condition);
            Assert.Null(batch.Specs[0].Reason);
        }

        [Fact]
        public void ConditionAndReason_areTrimmedButKept()
        {
            var batch = Prepare(
                new[] { At("A.cs", 3) with { Condition = "  i == 47  ", Reason = " the loop skips one " } }, Root);

            Assert.Equal("i == 47", batch.Specs[0].Condition);
            Assert.Equal("the loop skips one", batch.Specs[0].Reason);
        }

        /// <summary>
        /// One location holds one breakpoint, so a repeat is the agent revising itself and the LAST entry
        /// is the revision — keeping the first would silently discard the condition it just corrected.
        /// </summary>
        [Fact]
        public void DuplicateLocation_keepsTheLastAndSaysSo()
        {
            var batch = Prepare(
                new[]
                {
                    At("A.cs", 7) with { Condition = "i == 1" },
                    At("B.cs", 2),
                    At("A.cs", 7) with { Condition = "i == 47" },
                },
                Root);

            Assert.Null(batch.Error);
            Assert.Equal(2, batch.Specs.Count);
            // Revised in place: the batch keeps the order the agent asked for.
            Assert.Equal(@"C:\ws\A.cs", batch.Specs[0].File);
            Assert.Equal("i == 47", batch.Specs[0].Condition);
            // Dropping a row the caller listed is never silent.
            Assert.Single(batch.Notes);
            Assert.Contains("A.cs", batch.Notes[0]);
        }

        /// <summary>
        /// The duplicate check runs on the CANONICAL path, so the same file under two spellings collapses.
        /// Without it the second add would silently do nothing in VS while the card claimed two breakpoints —
        /// the <see cref="AgentPath"/> split, reached through a different door.
        /// </summary>
        [Fact]
        public void DuplicateAcrossSpellings_isStillOneBreakpoint()
        {
            var batch = Prepare(
                new[] { At(@"C:\ws\proj\File.cs", 12), At("proj/File.cs", 12) }, Root);

            Assert.Single(batch.Specs);
            Assert.Single(batch.Notes);
        }

        /// <summary>
        /// The setter and the clearer must agree about what "the same breakpoint" is, or clear_breakpoints
        /// quietly declines to remove one we just set. Windows paths are case-insensitive, so the key is too.
        /// </summary>
        // --- Which NAME may carry an expression ---------------------------------------------------
        //
        // A condition and a printMessage are not stored data: Visual Studio evaluates both INSIDE the
        // user's process when the line is reached, so they are the capability execute_expression charges
        // a permission prompt for, arriving through a tool authored one tier lower. The tier rides the
        // NAME because the permission request arrives before the arguments do, so the plain name has to
        // refuse them — and refuse, never strip, or the agent reads back a filtered breakpoint that is
        // really a plain stop and tells the user the loop stops only on iteration 47.

        [Fact]
        public void PlainTool_refusesACondition()
        {
            var batch = Prepare(
                new[] { At("A.cs", 3) with { Condition = "i == 47" } }, Root, allowExpressions: false);

            Assert.NotNull(batch.Error);
            // Nothing is placed: the refusal covers the whole call, as every other Prepare refusal does.
            Assert.Empty(batch.Specs);
        }

        [Fact]
        public void PlainTool_refusesATracepointMessage()
        {
            var batch = Prepare(
                new[] { At("A.cs", 3) with { PrintMessage = "i={i}" } }, Root, allowExpressions: false);

            Assert.NotNull(batch.Error);
            Assert.Empty(batch.Specs);
        }

        /// <summary>
        /// The refusal has to carry three things or the agent's next move is wrong: which row, that
        /// NOTHING was set (so it does not tell the user the breakpoint is in place), and the name of the
        /// tool that will place it — the <c>retryWith</c> lesson from the expression tools, where naming
        /// the tool beat asserting a boolean.
        /// </summary>
        [Fact]
        public void PlainTool_refusalNamesTheRow_theOtherTool_andSaysNothingWasSet()
        {
            var batch = Prepare(
                new[] { At("A.cs", 1), At("Loop.cs", 12) with { Condition = "i == 47" } },
                Root,
                allowExpressions: false);

            Assert.NotNull(batch.Error);
            Assert.Contains("breakpoint 2", batch.Error);
            Assert.Contains("Loop.cs", batch.Error);
            Assert.Contains("Nothing was set", batch.Error);
            Assert.Contains(Breakpoints.ExpressionToolName, batch.Error);
            // The two names have to be different names, which is the whole mechanism.
            Assert.NotEqual(Breakpoints.PlainToolName, Breakpoints.ExpressionToolName);
        }

        /// <summary>
        /// The common case must not move. A plain stop — line, hit count, reason — is what the Edit tier
        /// was reasoned about, and it still goes through untouched.
        /// </summary>
        [Fact]
        public void PlainTool_stillPlacesAPlainStop()
        {
            var batch = Prepare(
                new[] { At("A.cs", 3) with { HitCount = 4, Reason = "the fourth order is the odd one" } },
                Root,
                allowExpressions: false);

            Assert.Null(batch.Error);
            var only = Assert.Single(batch.Specs);
            Assert.Equal(4, only.HitCount);
            Assert.Equal("the fourth order is the odd one", only.Reason);
        }

        /// <summary>
        /// An empty condition is not an expression — it is already dropped as one — so it must not become
        /// a refusal either. A caller that fills the field with "" gets the plain stop it asked for,
        /// which is what stops the gate firing on the safe path (the failure that sank the argument-shaped
        /// version of this guard).
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void PlainTool_blankExpressions_areNotAnEscalation(string blank)
        {
            var batch = Prepare(
                new[] { At("A.cs", 3) with { Condition = blank, PrintMessage = blank } },
                Root,
                allowExpressions: false);

            Assert.Null(batch.Error);
            Assert.Single(batch.Specs);
            Assert.Null(batch.Specs[0].Condition);
            Assert.Null(batch.Specs[0].PrintMessage);
        }

        /// <summary>
        /// The Command-tier name is where both live, and a plain row may ride along in the same batch —
        /// one call, one prompt.
        /// </summary>
        [Fact]
        public void ExpressionTool_takesConditionsTracepointsAndPlainRows()
        {
            var batch = Prepare(
                new[]
                {
                    At("A.cs", 3) with { Condition = "i == 47" },
                    At("B.cs", 9) with { PrintMessage = "count={order.Items.Count}" },
                    At("C.cs", 1),
                },
                Root,
                allowExpressions: true);

            Assert.Null(batch.Error);
            Assert.Equal(3, batch.Specs.Count);
            Assert.Equal("i == 47", batch.Specs[0].Condition);
            Assert.Equal("count={order.Items.Count}", batch.Specs[1].PrintMessage);
        }

        [Fact]
        public void Key_isCaseInsensitiveOnPath()
        {
            var set = new HashSet<string>(Breakpoints.KeyComparer)
            {
                Breakpoints.Key(@"C:\ws\A.cs", 7),
            };

            Assert.Contains(Breakpoints.Key(@"c:\WS\a.cs", 7), set);
            Assert.DoesNotContain(Breakpoints.Key(@"C:\ws\A.cs", 8), set);
        }
    }
}
