using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using CodeWicket.Probes;
using CodeWicket.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Issue #142: <c>apply_code_fix</c> could not apply any fix Visual Studio draws as a submenu.
    /// </summary>
    /// <remarks>
    /// These run against the REAL C# fix providers (see <see cref="CodeFixProbe"/>), because
    /// the defect is unreachable from a fake: it lives in what Roslyn does when a registered action is
    /// asked to produce its operations, and the Ide project holds Roslyn compile-only so that call is
    /// never made outside devenv. That is exactly how every generate-style fix stayed permanently
    /// broken under a full set of passing checks.
    /// <para>
    /// The suite deliberately pins the PREMISE as well as the fix. If a future Roslyn made a grouped
    /// action applicable, the flattening would become unnecessary rather than wrong — but a guard that
    /// quietly kept passing would leave nobody any way to notice, so
    /// <see cref="GroupedAction_IsRegisteredAndCannotBeApplied"/> fails loudly instead.
    /// </para>
    /// </remarks>
    public sealed class CodeFixActionsTests
    {
        // CS0103: an undeclared name. The real fixers register "Generate variable 'alpha'", a GROUP.
        private const string UndeclaredName = "class C { void M() { var z = alpha; } }";

        // CS0246: an unimported type. Registers the FLAT "using System.Text;" - the case that always
        // worked, and the control that says the harness is not simply failing everything.
        private const string UnimportedType = "class C { System.Object M() { StringBuilder b = null; return b; } }";

        [Fact]
        public void Harness_ConstructsTheRealProviders()
        {
            // If this is empty every other assertion here is vacuous.
            Assert.NotEmpty(CodeFixProbe.Providers);
        }

        /// <summary>
        /// The premise. A registered group cannot be applied — so collecting it and offering it, as
        /// the tool did before #142, is offering a choice that always fails.
        /// </summary>
        [Fact]
        public async Task GroupedAction_IsRegisteredAndCannotBeApplied()
        {
            var document = CodeFixProbe.DocumentFor(UndeclaredName);
            var diagnostic = await CodeFixProbe.FirstDiagnosticAsync(document, "CS0103");
            Assert.NotNull(diagnostic);

            var registered = await CodeFixProbe.RegisteredActionsAsync(document, diagnostic!);
            var groups = registered.Where(a => !a.NestedActions.IsDefaultOrEmpty).ToList();

            Assert.True(groups.Count > 0,
                "CS0103 no longer registers a grouped action. The flattening may now be unnecessary - " +
                "re-measure before assuming it is still load-bearing.");

            foreach (var group in groups)
            {
                var (applies, failure) = await CodeFixProbe.TryApplyAsync(group);
                Assert.False(applies, $"'{group.Title}' is a group yet produced changes - premise changed.");
                Assert.IsType<NotSupportedException>(failure);
            }
        }

        /// <summary>
        /// The fix, stated as the property that matters to a caller: <b>everything offered works</b>.
        /// This is the assertion that fails if the flattening is removed.
        /// </summary>
        [Theory]
        [InlineData("CS0103", UndeclaredName)]
        [InlineData("CS0246", UnimportedType)]
        public async Task EveryOfferedFix_CanActuallyBeApplied(string diagnosticId, string source)
        {
            var document = CodeFixProbe.DocumentFor(source);
            var diagnostic = await CodeFixProbe.FirstDiagnosticAsync(document, diagnosticId);
            Assert.NotNull(diagnostic);

            var offered = await CodeFixProbe.OfferedFixesAsync(document, diagnostic!);
            Assert.NotEmpty(offered);

            foreach (var fix in offered)
            {
                var (applies, failure) = await CodeFixProbe.TryApplyAsync(fix);
                Assert.True(applies,
                    $"offered fix '{fix.Title}' cannot be applied" +
                    (failure is null ? " (no ApplyChangesOperation)" : $": {failure.GetType().Name}: {failure.Message}"));
            }
        }

        /// <summary>
        /// What the user actually lost: the generate-style fixes were never reachable, because they
        /// exist only as children of the group. Flattening is what puts them in front of the agent.
        /// </summary>
        [Fact]
        public async Task Flattening_ReachesTheFixesBuriedInTheSubmenu()
        {
            var document = CodeFixProbe.DocumentFor(UndeclaredName);
            var diagnostic = await CodeFixProbe.FirstDiagnosticAsync(document, "CS0103");
            Assert.NotNull(diagnostic);

            var registered = await CodeFixProbe.RegisteredActionsAsync(document, diagnostic!);
            var offered = await CodeFixProbe.OfferedFixesAsync(document, diagnostic!);

            // More fixes offered than actions registered is the whole point: the choices live one level down.
            Assert.True(offered.Count > registered.Count,
                $"flattening produced no additional fixes (registered={registered.Count}, offered={offered.Count})");

            // Named, not just counted - "some leaf appeared" would pass on the wrong leaves.
            Assert.Contains(offered, a => a.Title.IndexOf("Generate field", StringComparison.Ordinal) >= 0);
            Assert.Contains(offered, a => a.Title.IndexOf("Generate property", StringComparison.Ordinal) >= 0);
            Assert.Contains(offered, a => a.Title.IndexOf("Generate local", StringComparison.Ordinal) >= 0);

            // And the unusable group itself is gone.
            Assert.DoesNotContain(offered, a => !a.NestedActions.IsDefaultOrEmpty);
        }

        /// <summary>
        /// The hazard flattening CREATES: a leaf that opens a dialog. Offline it throws for want of a
        /// service; inside devenv that service exists, so it would instead block an agent turn on a
        /// modal window. It must never be offered.
        /// </summary>
        [Fact]
        public async Task InteractiveLeaf_IsNotOffered()
        {
            var document = CodeFixProbe.DocumentFor(UnimportedType);
            var diagnostic = await CodeFixProbe.FirstDiagnosticAsync(document, "CS0246");
            Assert.NotNull(diagnostic);

            // ONE gather, both views of it. Registering a second time builds fresh CodeAction
            // instances, so a reference comparison across two gathers finds no match for any leaf and
            // "was not offered" becomes trivially true - the assertion below would pin nothing.
            var registered = await CodeFixProbe.RegisteredActionsAsync(document, diagnostic!);
            var allLeaves = registered.SelectMany(AllLeaves).ToList();
            var offered = CodeFixProbe.Offered(registered);

            // The dialog-opening leaf exists in the tree ...
            var interactive = allLeaves.Where(CodeFixActions.IsInteractive).ToList();
            Assert.True(interactive.Count > 0,
                "no dialog-opening leaf found for CS0246 - the provider set changed; re-measure IsInteractive.");

            // ... the comparison is capable of finding a leaf at all ...
            Assert.Contains(allLeaves, leaf => offered.Any(o => ReferenceEquals(o, leaf)));

            // ... and none of the interactive ones is offered.
            foreach (var leaf in interactive)
                Assert.DoesNotContain(offered, a => ReferenceEquals(a, leaf));
        }

        /// <summary>
        /// The 17-leaf sweep, made permanent. Across a spread of diagnostics, the two
        /// <c>IsInteractive</c> signals must still name exactly the leaves that cannot be applied — the
        /// evidence the OR was chosen on. A leaf that applies but is filtered costs the caller a
        /// choice; one that is offered but cannot be applied is the bug this suite exists for.
        /// </summary>
        [Fact]
        public async Task InteractiveSignals_StillAgreeWithApplicability()
        {
            var cases = new (string Id, string Source)[]
            {
                ("CS0103", UndeclaredName),
                ("CS0246", UnimportedType),
                ("CS0246", "class C { Nonexistent M() { return null; } }"),
                ("CS1061", "class C { void M(string s) { s.Frobnicate(); } }"),
                ("CS0029", "class C { int M() { return \"s\"; } }"),
                ("CS0161", "class C { int M() { } }"),
                ("CS0535", "interface I { void F(); } class C : I { }"),
            };

            var swept = 0;
            var disagreements = new List<string>();

            foreach (var (id, source) in cases)
            {
                var document = CodeFixProbe.DocumentFor(source);
                var diagnostic = await CodeFixProbe.FirstDiagnosticAsync(document, id);
                if (diagnostic is null)
                    continue;

                var registered = await CodeFixProbe.RegisteredActionsAsync(document, diagnostic);
                foreach (var leaf in registered.SelectMany(AllLeaves))
                {
                    swept++;
                    var (applies, _) = await CodeFixProbe.TryApplyAsync(leaf);
                    var flagged = CodeFixActions.IsInteractive(leaf);
                    if (applies == flagged)
                        disagreements.Add($"[{id}] \"{leaf.Title}\" applies={applies} flaggedInteractive={flagged}");
                }
            }

            Assert.True(swept >= 10, $"swept only {swept} leaves - too few for the sweep to mean anything.");
            Assert.True(disagreements.Count == 0,
                "IsInteractive no longer matches applicability:" + Environment.NewLine +
                string.Join(Environment.NewLine, disagreements));
        }

        // ---- Shape-only cases. Synthetic actions, so they pin the traversal itself rather than
        // ---- whatever the installed Roslyn happens to register today.

        [Fact]
        public void NestedGroups_FlattenToTheirLeaves()
        {
            var tree = Group("outer",
                Group("inner", Leaf("a"), Leaf("b")),
                Leaf("c"));

            Assert.Equal(new[] { "a", "b", "c" }, CodeFixActions.Applicable(tree).Select(x => x.Title));
        }

        [Fact]
        public void FlatAction_IsOfferedUnchanged()
        {
            var leaf = Leaf("using System.Text;");
            Assert.Equal(new[] { leaf }, CodeFixActions.Applicable(leaf));
        }

        /// <summary>
        /// At the depth cap the node is YIELDED, not dropped. Dropping it would make the tool report
        /// "no code fix is available", which is a different and false claim — the fix exists, we just
        /// declined to walk that far.
        /// </summary>
        [Fact]
        public void DepthCap_YieldsTheNodeRatherThanDroppingIt()
        {
            // Deeper than the cap, one child per level, so the cap is certain to be hit.
            var action = Leaf("bottom");
            for (var i = CodeFixActions.MaxNestingDepth + 2; i >= 1; i--)
                action = Group("level" + i, action);

            var applicable = CodeFixActions.Applicable(action).ToList();

            Assert.Single(applicable);
            Assert.Equal("level" + (CodeFixActions.MaxNestingDepth + 1), applicable[0].Title);
            Assert.False(applicable[0].NestedActions.IsDefaultOrEmpty); // still a group: yielded, not descended into
        }

        [Theory]
        [InlineData("Generate new type...", true)]     // ASCII ellipsis - the VS convention
        [InlineData("Generate new type…", true)]  // single-character ellipsis
        [InlineData("Generate class 'Foo'", false)]
        [InlineData("using System.Text;", false)]
        public void EllipsisSignal_IdentifiesADialogFix(string title, bool expected) =>
            Assert.Equal(expected, CodeFixActions.IsInteractive(Leaf(title)));

        /// <summary>
        /// The type-name half of the OR, pinned on its own. <b>It has to be tested synthetically,
        /// because on today's provider set the two signals are completely redundant</b> — measured by
        /// deleting each in turn, and the whole suite still passed either time. That redundancy is the
        /// evidence the OR was chosen on, and it is also what would let one arm be removed in a later
        /// edit without a single test objecting.
        /// </summary>
        [Fact]
        public void TypeNameSignal_IdentifiesADialogFix_EvenWithoutAnEllipsis()
        {
            var action = new GenerateTypeWithOptionsAction();

            Assert.DoesNotContain("...", action.Title, StringComparison.Ordinal); // the other signal is silent
            Assert.True(CodeFixActions.IsInteractive(action));
        }

        [Fact]
        public void TypeNameSignal_DoesNotFlagAnOrdinaryAction()
        {
            Assert.False(CodeFixActions.IsInteractive(new PlainGeneratedAction()));
        }

        /// <summary>Stands in for Roslyn's internal <c>...WithOptions</c> actions, which open a dialog.</summary>
        private sealed class GenerateTypeWithOptionsAction : CodeAction
        {
            public override string Title => "Generate new type";
        }

        private sealed class PlainGeneratedAction : CodeAction
        {
            public override string Title => "Generate class 'Foo'";
        }

        [Fact]
        public void InteractiveLeaf_IsDroppedFromItsGroup()
        {
            var tree = Group("Generate type", Leaf("Generate class 'Foo'"), Leaf("Generate new type..."));

            Assert.Equal(new[] { "Generate class 'Foo'" }, CodeFixActions.Applicable(tree).Select(x => x.Title));
        }

        private static CodeAction Leaf(string title) =>
            CodeAction.Create(title, _ => Task.FromResult<Document>(null!), title);

        private static CodeAction Group(string title, params CodeAction[] children) =>
            CodeAction.Create(title, ImmutableArray.Create(children), isInlinable: false);

        /// <summary>Every leaf in the tree, INCLUDING the interactive ones the tool filters out.</summary>
        private static IEnumerable<CodeAction> AllLeaves(CodeAction action)
        {
            ImmutableArray<CodeAction> nested;
            try { nested = action.NestedActions; }
            catch { nested = ImmutableArray<CodeAction>.Empty; }

            if (nested.IsDefaultOrEmpty)
            {
                yield return action;
                yield break;
            }
            foreach (var child in nested)
                foreach (var leaf in AllLeaves(child))
                    yield return leaf;
        }
    }
}
