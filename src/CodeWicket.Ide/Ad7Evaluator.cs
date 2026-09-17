using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using CodeWicket.Core.Ide;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Evaluates expressions in the stopped debuggee through AD7 (issue #73, rung 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// AD7 rather than EnvDTE because only AD7 can ENFORCE "no code runs": measured 2026-08-27, EnvDTE's
    /// <c>, nse</c> specifier is inert (a bogus <c>, zzz</c> evaluated just as cleanly, so specifiers are
    /// not parsed on that path) and <c>GetExpression3</c>'s <c>allowAutoFuncEval:false</c> leaves an
    /// explicit call running. AD7 rather than Concord because Concord sits BELOW the AD7 adaptation layer
    /// and punishes a wrong argument with heap corruption rather than an HRESULT — it killed devenv once
    /// while this was being chosen.
    /// </para>
    /// <para>
    /// The interesting decisions are in <see cref="DebugValueRules"/>, in Core, where they are testable.
    /// What stays here is the part that needs a live debugger.
    /// </para>
    /// </remarks>
    public static class Ad7Evaluator
    {
        private const enum_EVALFLAGS Guard =
            enum_EVALFLAGS.EVAL_NOSIDEEFFECTS | enum_EVALFLAGS.EVAL_NOFUNCEVAL;

        private static readonly uint Timeout = DebugEvalLimits.TimeoutMs;
        private const uint Radix = 10;

        /// <summary>One frame the agent can name.</summary>
        public sealed class Frame
        {
            public int Index { get; set; }
            public string Method { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
            public bool HasSource => !string.IsNullOrEmpty(File);
            internal IDebugStackFrame2 Handle { get; set; }
        }

        /// <summary>
        /// The user-code frames, innermost first, numbered as the agent will name them.
        /// </summary>
        /// <remarks>
        /// Frames without a document context are kept and reported without a location rather than
        /// dropped, because a stack that silently omits its external frames misrepresents the call
        /// chain — and unlike EnvDTE, AD7 tells the truth here: measured, every external frame answers
        /// <c>E_FAIL</c> from <c>GetDocumentContext</c>, where the EnvDTE walk reports the PREVIOUS
        /// frame's location with no error at all (issue #73, finding 2).
        /// </remarks>
        /// <summary>
        /// Just the innermost frame, for callers that want a location rather than a stack.
        /// </summary>
        /// <remarks>
        /// The ambient workspace block reads this on EVERY prompt while the debugger is stopped, and
        /// <see cref="Frames"/> walks up to <see cref="DebugStateLimits.MaxFrames"/> of them with a
        /// document-context lookup each — three COM calls per frame for nineteen frames nobody asked
        /// for. Same walk, stopped at one.
        /// </remarks>
        public static Frame TopFrame()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var frames = Walk(1);
            return frames.Count > 0 ? frames[0] : null;
        }

        public static IReadOnlyList<Frame> Frames()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Walk(DebugStateLimits.MaxFrames);
        }

        private static IReadOnlyList<Frame> Walk(int max)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var frames = new List<Frame>();

            var thread = Ad7DebugEvents.LastThread;
            if (thread is null)
                return frames;

            IEnumDebugFrameInfo2 enumerator = null;
            if (thread.EnumFrameInfo(
                    enum_FRAMEINFO_FLAGS.FIF_FRAME | enum_FRAMEINFO_FLAGS.FIF_FUNCNAME |
                    enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_MODULE,
                    Radix, out enumerator) != VSConstants.S_OK || enumerator is null)
            {
                return frames;
            }

            enumerator.Reset();
            var buffer = new FRAMEINFO[1];
            while (frames.Count < max)
            {
                uint fetched = 0;
                if (enumerator.Next(1, buffer, ref fetched) != VSConstants.S_OK || fetched != 1)
                    break;

                var frame = new Frame
                {
                    Index = frames.Count + 1,
                    Method = buffer[0].m_bstrFuncName,
                    Handle = buffer[0].m_pFrame,
                };

                if (frame.Handle != null &&
                    frame.Handle.GetDocumentContext(out var document) == VSConstants.S_OK &&
                    document != null)
                {
                    if (document.GetName(enum_GETNAME_TYPE.GN_FILENAME, out var file) == VSConstants.S_OK)
                        frame.File = file;

                    var begin = new TEXT_POSITION[1];
                    var end = new TEXT_POSITION[1];
                    if (document.GetStatementRange(begin, end) == VSConstants.S_OK)
                        frame.Line = (int)begin[0].dwLine + 1; // AD7 lines are 0-based.
                }

                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>
        /// Evaluates each expression against <paramref name="frame"/>.
        /// </summary>
        /// <param name="allowSideEffects">
        /// When false (the default the tool applies) the debugger reads memory and runs nothing: fields,
        /// arithmetic, casts, indexers and expansion work; a property getter or an explicit call is
        /// refused, in the debugger's own words.
        /// </param>
        /// <param name="skipped">
        /// Evaluations the call's budget refused. Reported at the CALL level because that is the scope it
        /// belongs to; folded into any one expression's member count it would describe another
        /// expression's cost as this object's missing members.
        /// </param>
        public static IReadOnlyList<DebugEvaluation> Evaluate(
            Frame frame, IReadOnlyList<string> expressions, bool allowSideEffects, out int skipped)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var results = new List<DebugEvaluation>();
            skipped = 0;

            if (frame?.Handle is null ||
                frame.Handle.GetExpressionContext(out var context) != VSConstants.S_OK ||
                context is null)
            {
                return results;
            }

            var flags = allowSideEffects ? 0 : Guard;
            var budget = new DebugEvalBudget();
            foreach (var expression in expressions)
                results.Add(One(context, expression, flags, allowSideEffects, expand: true, budget));

            skipped = budget.Skipped;
            return results;
        }

        /// <param name="expand">
        /// Whether an expandable result should have its members read. FALSE for a member being
        /// re-evaluated, which is what bounds the walk to <see cref="DebugEvalLimits.MaxDepth"/>: an
        /// object graph has no bottom, and a member that expands its own members recurses through
        /// `this` → its logger → its factory with no end. Shipped once without this and it froze the
        /// IDE, so the bound is a PARAMETER rather than a convention — a caller cannot forget to pass
        /// it.
        /// </param>
        private static DebugEvaluation One(
            IDebugExpressionContext2 context,
            string text,
            enum_EVALFLAGS flags,
            bool ranCode,
            bool expand,
            DebugEvalBudget budget)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var guarded = !ranCode;

            if (!budget.TryTake())
            {
                return new DebugEvaluation(text)
                {
                    Kind = DebugValueKind.Unavailable,
                    Message = "Not evaluated: this call reached its limit of "
                              + DebugEvalLimits.MaxEvaluationsPerCall + " debugger evaluations. Ask for fewer expressions.",
                    RanCode = ranCode,
                };
            }

            if (context.ParseText(text, enum_PARSEFLAGS.PARSE_EXPRESSION, Radix,
                    out var expression, out var parseError, out _) != VSConstants.S_OK ||
                expression is null)
            {
                return new DebugEvaluation(text)
                {
                    Kind = DebugValueKind.Unavailable,
                    Message = string.IsNullOrEmpty(parseError) ? "The expression could not be parsed." : parseError,
                    RanCode = ranCode,
                };
            }

            // hr is NOT the signal - every refusal comes back S_OK with its verdict in the attributes.
            if (expression.EvaluateSync(flags, Timeout, null, out var property) != VSConstants.S_OK ||
                property is null)
            {
                return new DebugEvaluation(text)
                {
                    Kind = DebugValueKind.Unavailable,
                    Message = "The debugger did not return a result.",
                    RanCode = ranCode,
                };
            }

            var infos = new DEBUG_PROPERTY_INFO[1];
            if (property.GetPropertyInfo(
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE |
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE |
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_ATTRIB,
                    Radix, Timeout, null, 0, infos) != VSConstants.S_OK)
            {
                return new DebugEvaluation(text)
                {
                    Kind = DebugValueKind.Unavailable,
                    Message = "The debugger could not describe the result.",
                    RanCode = ranCode,
                };
            }

            var attributes = infos[0].dwAttrib;
            var hasError = (attributes & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) != 0;
            var sideEffect = (attributes & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_SIDE_EFFECT) != 0;
            var expandable = (attributes & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_OBJ_IS_EXPANDABLE) != 0;

            var kind = DebugValueRules.Classify(guarded, hasError, sideEffect);
            var evaluation = new DebugEvaluation(text)
            {
                Kind = kind,
                Type = Blank(infos[0].bstrType),
                IsExpandable = expandable,
                MayNeedCode = DebugValueRules.MayNeedCode(guarded, hasError, sideEffect),
                RanCode = ranCode,
            };

            if (kind != DebugValueKind.Value)
                return evaluation with { Message = Blank(infos[0].bstrValue) };

            var summary = DebugValueRules.ClampValue(Blank(infos[0].bstrValue));

            // An array of primitives is reported as its summary and never walked. Not a display rule: the
            // 528-byte blob an agent hit came back as 24 sbyte members with 24 more omitted, and each of
            // those members is a guarded RE-EVALUATION - so a byte array quietly spends a third of the
            // call's budget on rows nobody can read. Decided from the TYPE, which is ours to reason about,
            // rather than from the debugger's string, which is what makes the summary vouchable here.
            if (expandable && DebugValueRules.IsSummarizedArray(evaluation.Type))
            {
                return DebugValueRules.CanReportSummary(guarded, expandable, DebugExpansion.Summarized)
                    ? evaluation with { Value = summary }
                    : evaluation;
            }

            // Expand FIRST, then decide about the summary: whether it may be reported depends on what
            // expansion produced, and a null looks exactly like an object whose members we simply did
            // not fetch (see DebugValueRules.CanReportSummary).
            if (!expandable || !expand)
            {
                // NotAttempted, NOT "yielded nothing" - the distinction the bool this replaced could not
                // carry. A member at the depth bound is not a null, and reporting its summary as a value
                // handed the agent the debugger's own refusal notice sitting in the value slot.
                return DebugValueRules.CanReportSummary(guarded, expandable, DebugExpansion.NotAttempted)
                    ? evaluation with { Value = summary }
                    : evaluation;
            }

            var children = Children(property, context, flags, guarded, budget, out var omitted);
            var real = children.Count(DebugValueRules.IsRealMember);

            // omitted only - the budget's own skips are CALL-wide, and adding them here would report
            // another expression's cost as this object's missing members.
            evaluation = evaluation with { Children = children, Omitted = omitted };
            return DebugValueRules.CanReportSummary(
                    guarded, expandable, real > 0 ? DebugExpansion.YieldedMembers : DebugExpansion.YieldedNothing)
                ? evaluation with { Value = summary }
                : evaluation;
        }

        /// <summary>
        /// Expands one level of MEMBERS, stepping transparently through the debugger's grouping rows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><c>EnumChildren</c> ignores the guard</b> — measured by running a guarded expansion BEFORE
        /// anything unguarded had touched the object, which produced a display string the guarded direct
        /// read had just refused (so it is not a stale cache; the guarded-again pass proved that). It
        /// takes no evaluation flags of its own, so the only way to keep the promise is to ask it for
        /// names and types WITHOUT values, then evaluate each child by its full name under the same
        /// flags as its parent.
        /// </para>
        /// <para>
        /// <b>"Static members" and "Non-Public members" are not members</b> — they are display containers
        /// the expression evaluator synthesises so a tree is not swamped by private and static state,
        /// exactly as they appear in the Locals window (<c>Raw View</c> on a type with a
        /// <c>DebuggerTypeProxy</c> is the same thing). <c>EnumChildren</c> returns them mixed in with
        /// real members, and rendering them as rows put the actual fields one level down behind a door
        /// we drew as a wall: an agent could reach an exception's message only by
        /// knowing the CLR calls it <c>_message</c>. Its properties were all refused as getters while the
        /// backing fields were sitting in the expansion, unfetched.
        /// </para>
        /// <para>
        /// <b>Stepping through a container is NOT a level of depth</b>, and the distinction is
        /// load-bearing: <see cref="DebugEvalLimits.MaxDepth"/> bounds levels of OBJECTS, and a container
        /// is not one. Written as "one level of children" it would silently become two, which is the
        /// bound whose absence froze the IDE. Hence <paramref name="groupDepth"/>, which bounds the
        /// container walk on its own terms.
        /// </para>
        /// <para>
        /// The cap is applied AFTER flattening, so it bounds what the caller is shown rather than how
        /// far we looked — and the expensive half (a guarded re-evaluation per member) is what it
        /// actually limits.
        /// </para>
        /// <para>
        /// <b>What it drops, it NAMES</b> (see <see cref="DebugOmission"/>). The names are already in
        /// hand from the enumeration, so they are free; what the cap is really spending is the guarded
        /// re-evaluation each shown member costs.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<DebugEvaluationChild> Children(
            IDebugProperty2 property,
            IDebugExpressionContext2 context,
            enum_EVALFLAGS flags,
            bool guarded,
            DebugEvalBudget budget,
            out DebugOmission omitted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var raw = new List<DEBUG_PROPERTY_INFO>();
            var truncated = false;
            Collect(property, raw, groupDepth: MaxGroupDepth, truncated: ref truncated);

            var dropped = new List<string>();
            var reason = DebugOmissionReason.MemberCap;

            var children = new List<DebugEvaluationChild>();
            foreach (var info in raw)
            {
                var name = info.bstrName;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (children.Count >= DebugEvalLimits.MaxChildren || (guarded && budget.Exhausted))
                {
                    // Recorded at the FIRST omission, which is the one that actually caused it. The shown
                    // count freezes here, so whichever bound fired stays the true answer for the rest of
                    // the loop and asking again would give the same result - taken once because the
                    // reason is a fact about where the expansion STOPPED, not about each member after it.
                    if (dropped.Count == 0)
                    {
                        reason = DebugOmission.ReasonFor(
                            atMemberCap: children.Count >= DebugEvalLimits.MaxChildren,
                            budgetExhausted: guarded && budget.Exhausted);
                    }

                    dropped.Add(name);
                    continue;
                }

                var child = new DebugEvaluationChild(name)
                {
                    Type = Blank(info.bstrType),
                    IsExpandable = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_OBJ_IS_EXPANDABLE) != 0,
                    IsProperty = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_PROPERTY) != 0,
                };

                if (!guarded)
                {
                    child = child with { Value = DebugValueRules.ClampValue(Blank(info.bstrValue)) };
                }
                else if (!string.IsNullOrEmpty(info.bstrFullName))
                {
                    // Back through the guarded path, so a child's value is held to the same promise as
                    // its parent's. A child we cannot re-evaluate keeps its name and type: "in scope but
                    // not readable without running code" is a fact worth reporting, and dropping it would
                    // make the object look emptier than it is - which IsProperty now says outright,
                    // turning "discover it by refusal" into "see which members are readable".
                    //
                    // Re-PARSING bstrFullName is the weak point, and the reason DEBUGPROP_INFO_NOFUNCEVAL
                    // (0x20000) is worth a probe: a name that does not round-trip through the expression
                    // parser is reported notReadable, which is a FALSE NEGATIVE - readable, just
                    // unspellable. That flag appears to ask EnumChildren to withhold func-eval itself and
                    // would need no parsing at all. NOT taken on trust, and the risk is two questions
                    // rather than one: does it suppress, and does it REPORT that it suppressed. Yes then
                    // no is the worst outcome - safe, silent, and it feeds refusal notices back into the
                    // value slot, since WhyNoValue and DebugExpansion are both attribute-driven. The
                    // shape to adopt is a fallback, not a replacement.
                    var re = One(context, info.bstrFullName, flags, ranCode: false, expand: false, budget);
                    var value = re.Kind == DebugValueKind.Value ? re.Value : null;
                    child = child with
                    {
                        Value = value,
                        // A missing value is now readable: WHY it is missing, and the debugger's own
                        // sentence beside it. Both were being dropped - a refused member kept its name and
                        // lost the explanation, which is the loss the top-level result was already fixed
                        // for. The reason names what we know and never guesses a cause: see WhyNoValue.
                        ValueUnavailable = DebugValueRules.WhyNoValue(
                            re.Kind, hasValue: value is not null, isExpandable: child.IsExpandable),
                        Message = re.Message,
                    };
                }

                children.Add(child);
            }

            omitted = DebugOmission.For(dropped, reason, enumerationTruncated: truncated);
            return children;
        }

        /// <summary>
        /// How many levels of grouping rows to step through. Two, because a container can nest — a base
        /// class row holding its own non-public row — and because an unbounded walk of a structure the
        /// debugger synthesises is the same shape of mistake as an unbounded walk of an object graph.
        /// </summary>
        private const int MaxGroupDepth = 2;

        /// <summary>
        /// Reads one property's children into <paramref name="into"/>, replacing each grouping row with
        /// its own contents rather than emitting it.
        /// </summary>
        /// <param name="truncated">
        /// Set when an enumeration was cut short at <see cref="HardEnumerationCap"/>, at this level or any
        /// nested one — which makes every count above it a lower bound. Accumulated rather than returned,
        /// because a group whose own contents overflowed has lost members just as surely as the top level
        /// would have.
        /// </param>
        private static void Collect(
            IDebugProperty2 property, List<DEBUG_PROPERTY_INFO> into, int groupDepth, ref bool truncated)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // PROP so a container hands back the object needed to open it; VALUE only when code may
            // already run, since under the guard asking for it is the leak Children exists to close.
            var fields = enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
                         enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE |
                         enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_FULLNAME |
                         enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_ATTRIB |
                         enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP;

            var filter = Guid.Empty;
            if (property.EnumChildren(fields, Radix, ref filter,
                    enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_NONE, null, Timeout, out var enumerator) != VSConstants.S_OK ||
                enumerator is null)
            {
                return;
            }

            var buffer = new DEBUG_PROPERTY_INFO[1];
            var seen = 0;
            while (seen < HardEnumerationCap)
            {
                uint fetched = 0;
                if (enumerator.Next(1, buffer, out fetched) != VSConstants.S_OK || fetched != 1)
                    break;

                seen++;
                var info = buffer[0];

                // A grouping row carries no TYPE - which is what the debugger reports, and is checked
                // rather than its NAME because those labels are localized. See DebugValueRules.
                var isGroup = string.IsNullOrEmpty(info.bstrType) && !string.IsNullOrEmpty(info.bstrName);
                if (isGroup && groupDepth > 0 && info.pProperty != null)
                {
                    Collect(info.pProperty, into, groupDepth - 1, ref truncated);
                    continue;
                }

                into.Add(info);
            }

            // Told apart from a clean finish by asking the enumerator for one MORE than we kept: the loop
            // above exits identically whether the list ran out or the cap stopped it, and a cap presented
            // as a complete list is the silent reduction the payload exists to rule out.
            if (seen >= HardEnumerationCap)
            {
                uint spare = 0;
                if (enumerator.Next(1, buffer, out spare) == VSConstants.S_OK && spare == 1)
                    truncated = true;
            }
        }

        /// <summary>
        /// A ceiling on entries read from ONE enumeration, independent of what is later shown. Flattening
        /// means the raw list can be much longer than the cap the caller sees, and an enumeration that
        /// does not terminate would otherwise be bounded by nothing at all.
        /// </summary>
        private const int HardEnumerationCap = 200;

        private static string Blank(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
