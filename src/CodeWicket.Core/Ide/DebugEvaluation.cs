using System;
using System.Collections.Generic;

namespace CodeWicket.Core.Ide
{
    /// <summary>What the debugger was able to say about one expression.</summary>
    public enum DebugValueKind
    {
        /// <summary>A real value.</summary>
        Value,

        /// <summary>
        /// Refused because producing it would run code in the debuggee — the debugger said so itself.
        /// </summary>
        NeedsCode,

        /// <summary>
        /// No value. Either the expression is wrong (not in scope, a syntax error) or it needed code and
        /// the guard stopped it — see <see cref="DebugEvaluation.MayNeedCode"/>, which is set when those
        /// two cannot be told apart.
        /// </summary>
        Unavailable,
    }

    /// <summary>One member of an expanded value.</summary>
    public sealed record DebugEvaluationChild(string Name)
    {
        public string? Type { get; init; }
        public string? Value { get; init; }
        public bool IsExpandable { get; init; }

        /// <summary>
        /// True for a getter, false for a field — read from <c>DBG_ATTRIB_PROPERTY</c>.
        /// </summary>
        /// <remarks>
        /// Reported so the agent can SEE which members a guarded read can reach, instead of discovering
        /// it one refusal at a time. An agent hit this on an exception: every property refused, every
        /// backing field readable, and nothing in the payload said which was which — so the only way
        /// through was to know that the CLR spells Message <c>_message</c>.
        /// </remarks>
        public bool IsProperty { get; init; }

        /// <summary>
        /// Why there is no <see cref="Value"/>, or null when there is one. See
        /// <see cref="DebugValueRules.WhyNoValue"/>.
        /// </summary>
        public string? ValueUnavailable { get; init; }

        /// <summary>
        /// The debugger's own words about a member it declined — never paraphrased, and never a
        /// substitute for <see cref="ValueUnavailable"/>, which is the machine-readable half.
        /// </summary>
        /// <remarks>
        /// A refused member used to keep its name and lose the sentence explaining it, which is the same
        /// loss the top-level result had already been fixed for. It is the only place the ambiguity in
        /// <see cref="DebugEvaluation.MayNeedCode"/> can be resolved: only the debugger can say whether the
        /// expression was bad or merely needed running.
        /// </remarks>
        public string? Message { get; init; }
    }

    /// <summary>
    /// What an expansion did, which is not the same question as what it FOUND (issue #73).
    /// </summary>
    /// <remarks>
    /// It was a bool — "did expansion yield members" — and a bool cannot tell <b>expanded and found
    /// nothing</b> (a null, whose summary is the only information there is) from <b>did not expand at
    /// all</b> (a member at the depth bound, whose summary may be a func-eval notice we have no way to
    /// recognise). Children are evaluated with expansion off, so every one of them took the first
    /// answer while being in the second state — and the debugger's refusal notice went back to the
    /// agent sitting in the value slot, where it reads as content rather than as an absence. Worse
    /// than the null bug it was introduced by, and the same shape: a field with three meanings held in
    /// two states.
    /// </remarks>
    public enum DebugExpansion
    {
        /// <summary>Not expanded — so nothing about its summary can be vouched for.</summary>
        NotAttempted,

        /// <summary>Expanded and found no real members: a null.</summary>
        YieldedNothing,

        /// <summary>Expanded and found members, which are the answer.</summary>
        YieldedMembers,

        /// <summary>
        /// Deliberately not expanded because the summary IS the answer — see
        /// <see cref="DebugValueRules.IsSummarizedArray"/>. Distinct from
        /// <see cref="NotAttempted"/> because the decision was OURS, made from the type, so there is
        /// something to vouch with.
        /// </summary>
        Summarized,
    }

    /// <summary>One evaluated expression, as it goes back to the agent (issue #73, rung 3).</summary>
    public sealed record DebugEvaluation(string Expression)
    {
        public DebugValueKind Kind { get; init; }
        public string? Type { get; init; }

        /// <summary>The value, when there is one to report. See <see cref="DebugValueRules"/>.</summary>
        public string? Value { get; init; }

        /// <summary>The debugger's own words when it declined — never paraphrased.</summary>
        public string? Message { get; init; }

        public bool IsExpandable { get; init; }

        /// <summary>
        /// True when the call was made under the guard and the failure could equally be a bad expression
        /// or one that needed code. Stated rather than guessed: the two are indistinguishable from the
        /// attributes, and picking one would be a confident answer we do not have.
        /// </summary>
        public bool MayNeedCode { get; init; }

        /// <summary>True when this call was allowed to run code in the debuggee.</summary>
        public bool RanCode { get; init; }

        public IReadOnlyList<DebugEvaluationChild> Children { get; init; } = Array.Empty<DebugEvaluationChild>();

        /// <summary>What the expansion stopped short of, or null when it did not. Stated, never silent.</summary>
        public DebugOmission? Omitted { get; init; }
    }

    /// <summary>Why an expansion stopped short of the members it could see.</summary>
    public enum DebugOmissionReason
    {
        /// <summary>The per-value member cap (<see cref="DebugEvalLimits.MaxChildren"/>).</summary>
        MemberCap,

        /// <summary>The call-wide evaluation budget (<see cref="DebugEvalLimits.MaxEvaluationsPerCall"/>).</summary>
        EvaluationBudget,
    }

    /// <summary>
    /// The members an expansion did not return — <b>named</b>, not merely counted (issue #73).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare <c>omittedMembers: 3</c> declares the truncation, which is the rule, and still leaves the
    /// reader unable to act on it: on a large object it is the difference between "nothing I needed" and
    /// "the field I am looking for, silently dropped" — to an agent reading the payload. The
    /// names cost nothing (they are already in hand from the enumeration; it is the guarded re-evaluation
    /// per member that is expensive) so they are not capped separately. The <b>enumeration</b> bounds
    /// them, which is what <see cref="CountIsFloor"/> exists to admit.
    /// </para>
    /// <para>
    /// <b>The reason is one value for the whole expansion, not one per member</b>, and that is a property
    /// of the loop rather than a simplification: whichever condition fires first freezes the shown count,
    /// so the other can no longer become true. Neither is reversible — the budget only ever climbs.
    /// </para>
    /// </remarks>
    public sealed record DebugOmission(int Count, DebugOmissionReason Reason)
    {
        /// <summary>The omitted members, in the order the debugger listed them.</summary>
        public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

        /// <summary>
        /// True when the enumeration itself was cut short, so <see cref="Count"/> is a lower bound and
        /// <see cref="Names"/> is not the whole of what was dropped.
        /// </summary>
        /// <remarks>
        /// Reachable on any large collection — expanding a 1,000-element array reads the first
        /// <c>HardEnumerationCap</c> and never sees the rest — where a count presented as complete would
        /// under-report the loss by an order of magnitude while looking exact.
        /// </remarks>
        public bool CountIsFloor { get; init; }

        /// <summary>How a reason is spelled on the wire, matching the payload's other string values.</summary>
        /// <remarks>
        /// Beside the enum rather than at the serializer, so adding a value cannot leave it nameless in
        /// the payload; the fallback is the enum's own name, which is readable rather than correct — a
        /// value that reaches here unnamed is a missed case, not a crash in the middle of an answer.
        /// </remarks>
        public static string WireName(DebugOmissionReason reason) => reason switch
        {
            DebugOmissionReason.MemberCap => "memberCap",
            DebugOmissionReason.EvaluationBudget => "evaluationBudget",
            _ => reason.ToString(),
        };


        /// <summary>
        /// Which of the two bounds stopped an expansion, given both answers at the moment it stopped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Out of the loop that asks it, for the reason <c>EnterGestures.For</c> is: inline, a two-branch
        /// tie-break sits on a path no offline test reaches, and the branch that is never taken is the one
        /// that is wrong.
        /// </para>
        /// <para>
        /// <b>The member cap wins a tie</b>, which is reachable — the member that fills the cap can be the
        /// one that exhausts the budget — and is the right way round: the cap is a property of THIS value
        /// and would have stopped the expansion on its own, while the budget is a call-wide accident of
        /// what was asked for alongside it.
        /// </para>
        /// </remarks>
        public static DebugOmissionReason ReasonFor(bool atMemberCap, bool budgetExhausted) =>
            atMemberCap ? DebugOmissionReason.MemberCap : DebugOmissionReason.EvaluationBudget;

        /// <summary>
        /// The omission for an expansion that dropped <paramref name="names"/> for
        /// <paramref name="reason"/>, or null when nothing was lost at all.
        /// </summary>
        /// <remarks>
        /// Null on the ordinary path, so the caller renders nothing rather than an object saying zero:
        /// "no members were dropped" and "some were" must not look alike at a glance.
        /// </remarks>
        public static DebugOmission? For(
            IReadOnlyList<string>? names,
            DebugOmissionReason reason,
            bool enumerationTruncated)
        {
            var dropped = names ?? Array.Empty<string>();
            if (dropped.Count == 0 && !enumerationTruncated)
                return null;

            return new DebugOmission(dropped.Count, reason)
            {
                Names = dropped,
                CountIsFloor = enumerationTruncated,
            };
        }
    }

    /// <summary>
    /// How much of an evaluation is worth carrying. Separate from <see cref="DebugStateLimits"/> because
    /// the two answer different questions: that one bounds a capture the user hands over wholesale, this
    /// one bounds a value the agent asked for BY NAME, so it can afford to be more generous per value and
    /// must be stricter about how many at once.
    /// </summary>
    public static class DebugEvalLimits
    {
        /// <summary>Expressions in one call. Enough for a related set, not a dump of the frame.</summary>
        public const int MaxExpressions = 10;

        /// <summary>Members returned per expanded value.</summary>
        public const int MaxChildren = 24;

        /// <summary>
        /// Levels of expansion. <b>One</b>, and it is load-bearing rather than a taste: an object graph
        /// has no natural bottom, and expanding a child that is itself expandable recurses through
        /// `this` → its logger → its factory → its providers with no end. At
        /// <see cref="MaxChildren"/> per level that is 24^depth evaluations, each a synchronous call
        /// into the debugger — which freezes the IDE without the bound
        /// (issue #73, measured 2026-08-28).
        /// </summary>
        public const int MaxDepth = 1;

        /// <summary>
        /// A hard ceiling on debugger calls in one tool invocation, independent of the shape of the
        /// data. <see cref="MaxDepth"/> already bounds the recursion; this bounds the ARITHMETIC —
        /// ten expressions each expanding 24 members is 250 synchronous evaluations, which is a frozen
        /// IDE even when every one of them is well behaved. A backstop is not redundant with a correct
        /// depth rule: it is what makes the next bug in the depth rule survivable.
        /// </summary>
        public const int MaxEvaluationsPerCall = 64;

        /// <summary>
        /// Milliseconds one evaluation may take. Deliberately short: this is spent on the UI thread, so
        /// the number is a freeze budget rather than a patience budget, and a value that needs longer
        /// than this is one the agent should not be waiting on either.
        /// </summary>
        public const uint TimeoutMs = 1500;

        /// <summary>
        /// Characters of one value — deliberately larger than <see cref="DebugStateLimits.MaxValueChars"/>,
        /// because this value was named by the agent rather than swept up with two dozen others.
        /// </summary>
        public const int MaxValueChars = 1000;
    }

    /// <summary>
    /// A ceiling on how many times one tool call may ask the debugger anything, and a count of what it
    /// therefore skipped.
    /// </summary>
    /// <remarks>
    /// Its own type because the alternative — a counter threaded through by hand — is what allows a
    /// caller to forget it on one path, and the path that forgot it here was the recursive one. Skips
    /// are COUNTED rather than silent: a member list that stops early and says nothing reads as the whole
    /// object, which is the reduction-must-be-stated rule this file already follows for values.
    /// </remarks>
    public sealed class DebugEvalBudget
    {
        private int _remaining = DebugEvalLimits.MaxEvaluationsPerCall;

        /// <summary>How many evaluations were refused because the budget ran out.</summary>
        public int Skipped { get; private set; }

        public bool Exhausted => _remaining <= 0;

        /// <summary>Takes one evaluation from the budget, or records a skip and refuses.</summary>
        public bool TryTake()
        {
            if (_remaining <= 0)
            {
                Skipped++;
                return false;
            }

            _remaining--;
            return true;
        }
    }

    /// <summary>
    /// The two decisions that turn the debugger's raw answer into an honest one, kept here because both
    /// were learned by measurement and both fail SILENTLY when got wrong (issue #73, 2026-08-27).
    /// </summary>
    public static class DebugValueRules
    {
        /// <summary>
        /// Classifies a result from the debugger's ATTRIBUTES — never from its HRESULT, which is
        /// <c>S_OK</c> for every refusal, and never from its message text, which is localized.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SIDE_EFFECT</c> is the only unambiguous signal: the debugger is saying it declined because
        /// the expression runs code. An <c>ERROR</c> without it covers two different things that the
        /// attributes cannot separate — a property getter refused under the guard
        /// (<c>this.ExecuteTask</c>) and a plain expression error (<c>depth +</c> → <c>error CS1733</c>) —
        /// so under the guard both are reported as unavailable WITH <see cref="DebugEvaluation.MayNeedCode"/>
        /// set, and the debugger's own message is passed through to say which.
        /// </para>
        /// </remarks>
        public static DebugValueKind Classify(bool guarded, bool hasError, bool hasSideEffect)
        {
            if (hasSideEffect)
                return DebugValueKind.NeedsCode;
            return hasError ? DebugValueKind.Unavailable : DebugValueKind.Value;
        }

        /// <summary>
        /// Whether the failure might be the guard rather than the expression. Only ever true under the
        /// guard, and only for the ambiguous shape above — an unguarded error is the expression's fault.
        /// </summary>
        public static bool MayNeedCode(bool guarded, bool hasError, bool hasSideEffect) =>
            guarded && hasError && !hasSideEffect;

        /// <summary>
        /// Whether the debugger's summary string may be reported as the value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No, for an expandable result under the guard</b> — and this is the subtlest thing the probe
        /// found. A guarded read of an object whose display string needs code comes back with
        /// <b>no error flag at all</b>, <c>EXPANDABLE</c> set, and the value replaced by a notice
        /// ("Implicit function evaluation is turned off by the user"). Nothing in the attributes separates
        /// that from a real <c>DebuggerDisplay</c> string, and matching the text would break in a
        /// localized VS. So the summary is dropped for that shape and the CHILDREN are the answer — which
        /// is also what the agent actually wanted: expanding <c>{ConsoleApp1.HelloWorldService}</c> is the
        /// case this whole rung exists for.
        /// </para>
        /// <para>
        /// Everything else keeps its value: a non-expandable result cannot be hiding a notice, because a
        /// value that needed code and was refused carries <c>ERROR</c> and never reaches here.
        /// </para>
        /// <para>
        /// <b>And the summary comes BACK when the expansion yielded nothing</b>, which is what a NULL
        /// looks like: the debugger reports it as an expandable object of the field's declared type whose
        /// only child is a grouping row, so dropping the summary rendered "there is nothing here" as "an
        /// object with nothing in it" — to an agent reading it. There is no attribute for null:
        /// all 45 values of <c>enum_DBG_ATTRIB_FLAGS</c> were read and none of them means it. Nor can the
        /// value text be matched, since the token is LANGUAGE-dependent rather than merely localized —
        /// C# says <c>null</c> and VB says <c>Nothing</c>. So the rule is about what expansion PRODUCED,
        /// which needs neither: when there are no real members, the summary is the only information there
        /// is, and it is passed through in the debugger's own words.
        /// </para>
        /// </remarks>
        public static bool CanReportSummary(bool guarded, bool isExpandable, DebugExpansion expansion)
        {
            if (!guarded || !isExpandable)
                return true;

            return expansion switch
            {
                // A null: the summary is the only information there is.
                DebugExpansion.YieldedNothing => true,

                // Recognised as a value whose summary IS the answer, by a rule of ours rather than by
                // trusting the debugger's string — so there is something to vouch with.
                DebugExpansion.Summarized => true,

                // Members are the answer, and the summary may be a notice.
                DebugExpansion.YieldedMembers => false,

                // We never looked, so we cannot tell a notice from content.
                _ => false,
            };
        }

        /// <summary>
        /// Why a member has no value, or null when it has one — the machine-readable half of the answer,
        /// with the debugger's own sentence beside it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It names what we KNOW, not what we suspect.</b> An agent reading the payload proposed
        /// <c>function-evaluation-disabled</c>, which would be the useful label and is one we cannot
        /// establish: that notice arrives with no error flag (the whole reason
        /// <see cref="CanReportSummary"/> exists), so nothing in the attributes separates it from a real
        /// <c>DebuggerDisplay</c> string, and matching its text breaks in a localized VS. What we can say
        /// is structural — the member was not expanded, so its summary is not vouchable — and that leads
        /// the reader to the same next step without inventing a cause.
        /// </para>
        /// </remarks>
        public static string? WhyNoValue(DebugValueKind kind, bool hasValue, bool isExpandable)
        {
            if (hasValue)
                return null;

            return kind switch
            {
                DebugValueKind.NeedsCode => "needsCode",
                DebugValueKind.Value when isExpandable => "notExpanded",
                DebugValueKind.Value => "notReported",
                _ => "notReadable",
            };
        }

        /// <summary>
        /// Whether a value should be reported as its summary and NOT expanded — an array of primitives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured on an exception: <c>_stackTrace</c> came back as 24 individual
        /// <c>sbyte</c> members of a 528-byte blob, with 24 more omitted. The rendering is noise, and the
        /// COST is the real defect — every one of those members is a guarded re-evaluation, so a byte blob
        /// quietly spends a third of <see cref="DebugEvalLimits.MaxEvaluationsPerCall"/> and crowds out
        /// members that were asked for.
        /// </para>
        /// <para>
        /// <b>By element type, not by length.</b> A length threshold would need the length parsed out of
        /// a display string, and a small blob is no more useful than a large one; an agent that wants one
        /// element asks for <c>arr[3]</c>, which is an ordinary expression. Arrays of anything else still
        /// expand, because their elements are worth seeing.
        /// </para>
        /// <para>
        /// The polymorphic <c>Declared {Runtime}</c> form is trimmed first — measured on a live debugger,
        /// which reports a member's type as <c>System.Exception {System.InvalidOperationException}</c>.
        /// A language whose arrays are not spelled <c>[]</c> (VB's <c>Byte()</c>) simply does not match and
        /// keeps today's behaviour, which is the safe direction.
        /// </para>
        /// </remarks>
        public static bool IsSummarizedArray(string? type)
        {
            var name = RuntimeTypeName(type);
            if (name is null || !name.EndsWith("[]", StringComparison.Ordinal))
                return false;

            var element = name.Substring(0, name.Length - 2);
            var dot = element.LastIndexOf('.');
            if (dot >= 0)
                element = element.Substring(dot + 1);

            return Array.IndexOf(PrimitiveElements, element) >= 0;
        }

        // Both spellings, because the debugger reports whichever the language uses. Deliberately no
        // string: its elements are readable and worth seeing.
        private static readonly string[] PrimitiveElements =
        {
            "bool", "byte", "sbyte", "char", "short", "ushort", "int", "uint", "long", "ulong",
            "float", "double", "decimal",
            "Boolean", "Byte", "SByte", "Char", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
            "Single", "Double", "Decimal", "IntPtr", "UIntPtr",
        };

        /// <summary>
        /// The runtime type out of the debugger's <c>Declared {Runtime}</c> form, or the string unchanged
        /// when it is not in that form.
        /// </summary>
        /// <remarks>
        /// Measured: a polymorphic member comes back as <c>System.Exception
        /// {System.InvalidOperationException}</c>, which is the debugger telling us the runtime type for
        /// free — the most useful half, and the half a naive read of the type string throws away.
        /// </remarks>
        public static string? RuntimeTypeName(string? type)
        {
            var text = type?.Trim();
            if (string.IsNullOrEmpty(text) || text![text.Length - 1] != '}')
                return string.IsNullOrEmpty(text) ? null : text;

            var open = text.LastIndexOf('{');
            if (open < 0)
                return text;

            var inner = text.Substring(open + 1, text.Length - open - 2).Trim();
            return inner.Length == 0 ? text : inner;
        }

        /// <summary>
        /// Whether an expanded child is a real member rather than one of the debugger's grouping rows
        /// ("Static members", "Non-Public members").
        /// </summary>
        /// <remarks>
        /// By the absence of a TYPE, because that is what the debugger actually reports — a group comes
        /// back with an empty type and an empty value, every real member carries a type. Not by NAME:
        /// those group labels are localized, and matching them would work in English and quietly stop
        /// working everywhere else.
        /// </remarks>
        public static bool IsRealMember(DebugEvaluationChild child) =>
            child is not null && !string.IsNullOrEmpty(child.Type);

        /// <summary>
        /// Trims one value to <see cref="DebugEvalLimits.MaxValueChars"/>, saying how much it dropped.
        /// A cut that does not announce itself reads as the whole value — the rule
        /// <see cref="WorkspaceDiagnostics"/> and <see cref="DebugStateFormatter"/> already follow.
        /// </summary>
        public static string? ClampValue(string? value)
        {
            if (value is null || value.Length <= DebugEvalLimits.MaxValueChars)
                return value;

            var dropped = value.Length - DebugEvalLimits.MaxValueChars;
            return value.Substring(0, DebugEvalLimits.MaxValueChars)
                   + "… (+" + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + " more characters)";
        }
    }
}
