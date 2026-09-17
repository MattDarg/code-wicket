using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CodeActions;

// Compiled into the net472 Ide assembly (Nullable=disable) AND linked into the net10 test and
// console projects (Nullable=enable), so the file pins its own setting rather than inheriting two
// different ones. Nothing here touches VS: the only dependency is Roslyn's CodeAction, which is
// what lets the guard for issue #142 run against the real fixers off a live devenv.
#nullable disable

namespace CodeWicket.Ide
{
    /// <summary>
    /// Which of a code-fix provider's registered actions can actually be <i>applied</i> — the seam
    /// between what Visual Studio draws in the light-bulb menu and what an agent can invoke without a
    /// human present.
    /// </summary>
    /// <remarks>
    /// Issue #142. Split out of <see cref="VsToolCatalog"/> so it can be linked into projects that
    /// hold a <b>real Roslyn runtime</b>. The Ide assembly references Roslyn compile-only
    /// (<c>ExcludeAssets="runtime"</c>) because devenv supplies the assemblies and its live MEF set of
    /// fix providers — which also means nothing in an ordinary build ever calls
    /// <c>GetOperationsAsync</c> on a grouped action, and that is exactly how the defect survived. The
    /// file is pure (no VS, no WPF), so linking it is what makes the behaviour provable offline.
    /// </remarks>
    internal static class CodeFixActions
    {
        /// <summary>
        /// Runaway guard on a light-bulb submenu tree. The tree is immutable and built at
        /// construction, so a cycle cannot be formed; this bounds a pathological depth, nothing more.
        /// </summary>
        internal const int MaxNestingDepth = 8;

        /// <summary>
        /// The applicable fixes reachable from a registered action: the action itself when it is a
        /// leaf, otherwise its nested children, recursively. An action carrying
        /// <see cref="CodeAction.NestedActions"/> is a <b>menu</b>, not a fix — Roslyn's
        /// <c>CodeActionWithNestedActions</c> throws <see cref="NotSupportedException"/> out of
        /// <c>GetChangedDocumentAsync</c>, so there is no way to apply one, and its children are what
        /// the caller means by the choice.
        /// </summary>
        /// <remarks>
        /// Measured against the real fixers (<c>Microsoft.CodeAnalysis.CSharp.Features</c> 4.14 over an
        /// <c>AdhocWorkspace</c>): <c>"Generate variable 'alpha'"</c> registers with <c>nested=5</c> and
        /// throws, handing the agent <c>"the fix could not be computed:
        /// Microsoft.CodeAnalysis.CodeActions.CodeAction+CodeActionWithNestedActions"</c> — a bare type
        /// name — while the five children (<c>Generate field/read-only field/property/local/parameter</c>)
        /// apply normally and were never collected, so <c>fixTitle</c> could not reach them either. Every
        /// generate-style fix was therefore unreachable. A flat action (<c>"using System.Text;"</c>,
        /// <c>nested=0</c>) applies, which is why add-using has always worked and is the core proven in VS.
        /// <para>
        /// The group is DROPPED rather than kept alongside its children: it cannot be applied, so offering
        /// it is offering a choice that fails, and the children name themselves well enough to stand alone.
        /// One knock-on is deliberate — two same-named CS0103 on a line used to share a single title and
        /// auto-apply straight into the throw; flattened they present five genuinely different fixes and
        /// the ambiguity guard asks, which is the right question and one it could not previously see.
        /// </para>
        /// <para>
        /// At the depth cap the node is yielded rather than dropped — it will fail with a message,
        /// whereas dropping it would report "no code fix is available", which is a different and false
        /// claim.
        /// </para>
        /// </remarks>
        internal static IEnumerable<CodeAction> Applicable(CodeAction action, int depth = 0)
        {
            ImmutableArray<CodeAction> nested;
            try { nested = action.NestedActions; }
            catch { nested = ImmutableArray<CodeAction>.Empty; } // same tolerance as ProvidersFor

            if (nested.IsDefaultOrEmpty || depth >= MaxNestingDepth)
            {
                if (!IsInteractive(action))
                    yield return action;
                yield break;
            }

            foreach (var child in nested)
                foreach (var leaf in Applicable(child, depth + 1))
                    yield return leaf;
        }

        /// <summary>
        /// A fix that cannot be applied without a human — it opens a dialog. Excluded from the offered
        /// fixes, because flattening the menus above is what first makes these reachable at all.
        /// </summary>
        /// <remarks>
        /// <b>The headless failure is the harmless one; devenv is where this bites.</b> Offline,
        /// <c>"Generate new type..."</c> throws <c>InvalidOperationException: Service of type
        /// 'IGenerateTypeOptionsService' is required ... but is not available from 'Custom' workspace</c>.
        /// Inside Visual Studio that service IS available, so the same action would not fail — it would
        /// raise a MODAL dialog on the UI thread in the middle of an agent turn and wait for a person. That
        /// is the line-endings-dialog failure mode, arrived at from a different direction, and it is a
        /// hazard this flattening would otherwise have introduced rather than one it inherited.
        /// <para>
        /// Two signals, ORed, because there is no public API for "this action needs UI" — the type
        /// implements an internal options-service interface. The ellipsis is the Windows/VS convention for
        /// "opens a dialog" and survives a runtime rename; <c>WithOption</c> is Roslyn's own type-naming and
        /// survives localization. Swept across 17 leaves from six diagnostics, both agreed exactly, flagging
        /// the two <c>"Generate new type..."</c> leaves and nothing else — so ORing costs nothing today and
        /// degrades better than either alone tomorrow.
        /// </para>
        /// <para>
        /// Deliberately biased: dropping a fix that would have worked costs the caller one choice it can
        /// route around, while admitting one that blocks devenv on a dialog hangs the turn with no way out
        /// from the agent's side. Nothing is lost against the status quo either way — before flattening,
        /// none of these leaves was reachable at all.
        /// </para>
        /// </remarks>
        internal static bool IsInteractive(CodeAction action)
        {
            var title = action.Title ?? string.Empty;
            if (title.EndsWith("...", StringComparison.Ordinal) || title.EndsWith("…", StringComparison.Ordinal))
                return true;

            string typeName;
            try { typeName = action.GetType().FullName ?? string.Empty; }
            catch { return false; }
            return typeName.IndexOf("WithOption", StringComparison.Ordinal) >= 0;
        }
    }
}
