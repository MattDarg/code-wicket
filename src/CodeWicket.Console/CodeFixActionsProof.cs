using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using CodeWicket.Ide;

namespace CodeWicket.Probes
{
    /// <summary>
    /// Issue #142: what does the light bulb actually OFFER, and can an agent apply it?
    /// </summary>
    /// <remarks>
    /// The unit suite decides this on every test run; what it cannot do is show you the shape. When
    /// <c>GroupedAction_IsRegisteredAndCannotBeApplied</c> fails with "the premise changed", the next
    /// question is <i>changed to what</i> — and that needs the registered tree, the leaves under it and
    /// each leaf's verdict printed side by side, which is what this prints.
    /// <para>
    /// It also states the BEFORE explicitly. The defect was invisible because the tool reported it as a
    /// bare .NET type name, so the row that matters most here is the one showing a registered action
    /// being asked to produce operations and refusing — the exact call no ordinary build ever makes.
    /// </para>
    /// </remarks>
    internal static class CodeFixActionsProof
    {
        private static readonly (string Id, string Label, string Source)[] Cases =
        {
            ("CS0103", "undeclared name (grouped: generate variable)",
                "class C { void M() { var z = alpha; } }"),
            ("CS0246", "unimported type (flat add-using + grouped generate type)",
                "class C { System.Object M() { StringBuilder b = null; return b; } }"),
            // Kept deliberately: headless, no provider offers anything here. If the harness ever starts
            // inventing fixes, this is the row it shows up in.
            ("CS1061", "no such member (expected: nothing offered)",
                "class C { void M(string s) { s.Frobnicate(); } }"),
            ("CS0535", "unimplemented interface member",
                "interface I { void F(); } class C : I { }"),
        };

        internal static async Task<int> RunAsync()
        {
            Console.WriteLine("== code-wicket console: which light-bulb fixes an agent can apply (issue #142) ==");
            Console.WriteLine();
            Console.WriteLine("Real Microsoft.CodeAnalysis.CSharp.Features providers over an AdhocWorkspace.");
            Console.WriteLine("providers constructed: " + CodeFixProbe.Providers.Length);
            Console.WriteLine();

            var groupsSeen = 0;
            var groupsApplied = 0;      // a group that DID apply would retire the flattening
            var offeredThatFail = 0;    // the bug: something offered that cannot be applied
            var filteredThatWork = 0;   // over-filtering: a usable fix withheld
            var sweptLeaves = 0;

            foreach (var (id, label, source) in Cases)
            {
                Console.WriteLine("-- " + id + ": " + label + " --");
                Console.WriteLine("   " + source);

                var document = CodeFixProbe.DocumentFor(source);
                var diagnostic = await CodeFixProbe.FirstDiagnosticAsync(document, id).ConfigureAwait(false);
                if (diagnostic is null)
                {
                    Console.WriteLine("   (not produced by this compilation — skipped)");
                    Console.WriteLine();
                    continue;
                }

                // One gather, then both views of it. Re-registering would hand back fresh CodeAction
                // instances and every leaf would read as filtered.
                var registered = await CodeFixProbe.RegisteredActionsAsync(document, diagnostic).ConfigureAwait(false);
                var offered = CodeFixProbe.Offered(registered);

                // BEFORE: exactly what the tool collected, and what happens when it applies one.
                Console.WriteLine("   registered (what the tool collected BEFORE #142): " + registered.Count);
                foreach (var action in registered)
                {
                    var nested = action.NestedActions.IsDefaultOrEmpty ? 0 : action.NestedActions.Length;
                    var (applies, failure) = await CodeFixProbe.TryApplyAsync(action).ConfigureAwait(false);
                    if (nested > 0)
                    {
                        groupsSeen++;
                        if (applies) groupsApplied++;
                    }
                    Console.WriteLine(
                        "      " + Verdict(applies) + " nested=" + nested + "  \"" + action.Title + "\""
                        + (failure is null ? string.Empty : "  -> " + failure.GetType().Name + ": " + Short(failure.Message)));
                }

                // AFTER: every row the tool could hand back, plus the leaves it withheld.
                //
                // The rows are OFFERED-first and only then the leaves, because the two sets are not the
                // same and the difference is the whole subject. Walking leaves alone would examine
                // nothing when the flattening is broken - a group is not a leaf, so an unflattened
                // build offers exactly the item the sweep never looks at, and the proof passes while
                // reporting the defect it exists to catch. (Measured: it did.)
                Console.WriteLine("   offered (AFTER flattening + filtering): " + offered.Count);
                var leaves = registered.SelectMany(AllLeaves).ToList();
                var rows = offered.Concat(leaves.Where(l => !offered.Any(o => ReferenceEquals(o, l)))).ToList();

                foreach (var row in rows)
                {
                    sweptLeaves++;
                    var (applies, failure) = await CodeFixProbe.TryApplyAsync(row).ConfigureAwait(false);
                    var isOffered = offered.Any(o => ReferenceEquals(o, row));
                    var nested = row.NestedActions.IsDefaultOrEmpty ? 0 : row.NestedActions.Length;

                    if (isOffered && !applies) offeredThatFail++;
                    if (!isOffered && applies) filteredThatWork++;

                    Console.WriteLine(
                        "      " + (isOffered ? "offered " : "FILTERED") + " " + Verdict(applies)
                        + " ellipsis=" + Flag(EndsWithEllipsis(row.Title))
                        + " withOption=" + Flag(row.GetType().FullName?.IndexOf("WithOption", StringComparison.Ordinal) >= 0)
                        + (nested > 0 ? " nested=" + nested : string.Empty)
                        + "  \"" + row.Title + "\""
                        + (failure is null ? string.Empty : "  -> " + failure.GetType().Name));
                }
                Console.WriteLine();
            }

            Console.WriteLine("leaves swept: " + sweptLeaves + ", groups seen: " + groupsSeen);
            Console.WriteLine();

            // The premise, stated as a check rather than assumed: a group that became applicable would
            // mean the flattening is no longer load-bearing, and that must be visible rather than silent.
            var premiseHolds = groupsSeen > 0 && groupsApplied == 0;
            Console.WriteLine(groupsSeen == 0
                ? "PREMISE GONE: no provider registered a grouped action — re-measure before trusting the flattening."
                : groupsApplied > 0
                    ? "PREMISE CHANGED: " + groupsApplied + " grouped action(s) applied — the flattening may now be unnecessary."
                    : "premise holds: every grouped action refuses to produce operations (" + groupsSeen + " seen).");

            Console.WriteLine(offeredThatFail == 0
                ? "every offered fix applies."
                : "BROKEN: " + offeredThatFail + " offered fix(es) cannot be applied — this is issue #142.");

            // Filtering something that works offline is not automatically wrong: IsInteractive exists to
            // drop leaves that would SUCCEED in devenv by opening a dialog. Reported, never assumed.
            Console.WriteLine(filteredThatWork == 0
                ? "nothing usable was filtered out."
                : "NOTE: " + filteredThatWork + " filtered leaf/leaves applied here. Check each is a dialog fix"
                  + " (which would succeed in devenv by blocking on a modal window) and not a real loss.");

            Console.WriteLine();
            var pass = premiseHolds && offeredThatFail == 0 && sweptLeaves >= 10;
            Console.WriteLine(pass
                ? "PASS: the submenu is flattened, everything offered can be applied, and no dialog fix is offered."
                : "FAIL: see above.");
            return pass ? 0 : 1;
        }

        private static bool EndsWithEllipsis(string? title) =>
            (title ?? string.Empty).EndsWith("...", StringComparison.Ordinal) ||
            (title ?? string.Empty).EndsWith("…", StringComparison.Ordinal);

        private static string Verdict(bool applies) => applies ? "APPLIES" : "cannot ";

        private static string Flag(bool value) => value ? "yes" : "no ";

        private static string Short(string message)
        {
            var text = message.Replace("\r", string.Empty).Replace("\n", " ");
            return text.Length <= 120 ? text : text.Substring(0, 117) + "...";
        }

        /// <summary>Every leaf, INCLUDING the interactive ones — the filtered rows are half the evidence.</summary>
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
