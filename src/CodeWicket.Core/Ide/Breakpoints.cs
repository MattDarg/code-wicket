using System;
using System.Collections.Generic;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// One breakpoint the agent asked for (issue #73, rung 1).
    /// </summary>
    /// <remarks>
    /// <see cref="Reason"/> is the agent's own words for why this line is worth stopping at, and it is
    /// the part of the request the user cannot get from the gutter: a red dot they did not place says
    /// nothing about what it is for. It is carried through to the transcript card for that reason, not
    /// as decoration.
    /// </remarks>
    public sealed record BreakpointSpec(string File, int Line)
    {
        /// <summary>The condition that must hold for the debugger to stop, e.g. <c>order.Items.Count == 0</c>.</summary>
        public string? Condition { get; init; }

        /// <summary>Stop only on the Nth hit. Null means every hit.</summary>
        public int? HitCount { get; init; }

        /// <summary>Why the agent wants to stop here, in its own words.</summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Turns this into a TRACEPOINT: the message is printed to the Output window and execution
        /// continues, instead of the debugger stopping (issue #73, verified in Visual Studio, 2026-08-26).
        /// </summary>
        /// <remarks>
        /// <c>{expression}</c> is evaluated in the debuggee's scope AT THAT LINE, which is what makes
        /// the feature worth having — the agent instruments a loop with the values it wants, the user
        /// starts debugging once, and nobody steps. It also cannot be validated when the breakpoint is set,
        /// because the scope does not exist yet: a name that is not in scope prints an inline
        /// <c>error CS0103</c> into the message and lets the program carry on (measured). That is a good
        /// degradation — visible in the Output window, harmless to the run — but only if the caller
        /// knows to expect it, which is the tool description's job.
        /// </remarks>
        public string? PrintMessage { get; init; }
    }

    /// <summary>
    /// The outcome of preparing one requested batch: the specs to act on, anything worth telling the
    /// caller about what preparing them changed, and a refusal covering the whole call.
    /// </summary>
    /// <remarks>
    /// <see cref="Error"/> being non-null means <see cref="Specs"/> must not be acted on at all. That is
    /// the "couldn't run" half of the result contract (<c>docs/engineering/ide-services.md</c>): a malformed
    /// request is bad arguments, so nothing happens and the caller resends. It is deliberately NOT how a
    /// breakpoint that fails to BIND is reported — that one ran, and a batch where some bound and some
    /// did not is a legitimate partial result the catalog reports per row.
    /// </remarks>
    public sealed record BreakpointBatch(
        IReadOnlyList<BreakpointSpec> Specs,
        IReadOnlyList<string> Notes,
        string? Error);

    /// <summary>
    /// Validation, normalisation and identity for the breakpoint tools — <c>set_breakpoint</c>,
    /// <c>set_expression_breakpoint</c> and <c>clear_breakpoints</c> — everything about them that can be
    /// decided without Visual Studio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives in Core for the reason <see cref="CodeFixScopes"/> and <see cref="TestStackFrames"/> do:
    /// <c>VsToolCatalog</c> is net472 + the VS SDK and no test project can reference it, so a rule left
    /// in there is a rule no test can reach.
    /// </para>
    /// <para>
    /// Core takes no JSON dependency by design, so the catalog reads the arguments (as
    /// <c>ReadDiagnosticsArgs</c> already does) and hands the values here. The split is the same one
    /// <see cref="CodeFixScopes"/> makes: reading the request belongs to the caller, deciding about it
    /// belongs here.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What to do about a breakpoint already sitting at the location a spec names.
    /// </summary>
    /// <remarks>
    /// An enum rather than a pair of bools, and returned rather than inferred at the call site, because
    /// of how this went wrong: the host set a "conflict" string and then carried on into the Add
    /// anyway, so the tool reported a refusal it had not performed. A value the caller must SWITCH on
    /// cannot be quietly not-acted-upon the way a assigned-and-forgotten string can.
    /// </remarks>
    public enum ExistingBreakpointAction
    {
        /// <summary>Nothing there: place it.</summary>
        Place,

        /// <summary>
        /// Ours, so replace it — remove and re-add, which is what makes <c>set_breakpoint</c>
        /// idempotent per location and lets a condition be changed without clearing the whole batch.
        /// </summary>
        Replace,

        /// <summary>
        /// The USER's. Refuse, and report it with its details — never replace (that would silently
        /// discard a condition or hit count they set deliberately) and never add beside it.
        /// </summary>
        RefuseAsConflict,
    }

    public static class Breakpoints
    {
        /// <summary>
        /// What to do about a breakpoint the host found at a spec's location.
        /// </summary>
        /// <remarks>
        /// ONE BREAKPOINT PER LOCATION, which is what Visual Studio itself enforces. Measured
        /// 2026-08-28: clearing a breakpoint from the gutter removes EVERY breakpoint at that location,
        /// so a second one added beside the user's is invisible in the gutter, unattributable when it is
        /// hit, and silently destroyed by their ordinary click. It is a state only automation can
        /// create, so refusing it takes away nothing the user has today.
        /// <para>
        /// Deciding here rather than in the host is not about testability — the host still has to obey
        /// it, and no offline check can watch it do so. It is about the SHAPE. The rule was previously a
        /// string assigned in one branch and read many lines later, and the branch fell through to the
        /// Add: the tool answered "conflict — choose another line" while Visual Studio had already taken
        /// the breakpoint, stamped our tag on it, and (for a tracepoint) cleared BreakWhenHit on the
        /// location the user was watching. A returned outcome makes that particular mistake impossible
        /// to write.
        /// </para>
        /// <para>
        /// NOT one-per-LINE, and the distinction will matter later: VS allows several breakpoints on one
        /// line at different COLUMNS, for lambdas and chained calls. This tool takes no column, so
        /// everything it sets lands at the same location and is genuinely indistinguishable — but if a
        /// <c>column</c> argument is ever added, the host's lookup must match on column too, or this
        /// starts refusing breakpoints that would have been perfectly distinct.
        /// </para>
        /// </remarks>
        /// <param name="exists">Whether the host found a breakpoint at the location.</param>
        /// <param name="isOurs">Whether that breakpoint carries one of our tags.</param>
        public static ExistingBreakpointAction Decide(bool exists, bool isOurs) =>
            !exists ? ExistingBreakpointAction.Place
            : isOurs ? ExistingBreakpointAction.Replace
            : ExistingBreakpointAction.RefuseAsConflict;

        /// <summary>
        /// The most breakpoints one call may set. A batch exists so a handful of related stops cost one
        /// approval and one card, not so a model can instrument a file wholesale; past this the request
        /// is refused rather than trimmed, because a silently shortened batch would leave the agent
        /// believing it had instrumented lines it had not.
        /// </summary>
        public const int MaxPerCall = 20;

        /// <summary>
        /// The tool that places a PLAIN stop — <c>ToolRisk.Edit</c>, so it auto-approves at AcceptEdits.
        /// </summary>
        /// <remarks>
        /// Named here rather than only in the catalog because <see cref="Prepare"/> writes the refusal
        /// that sends a caller from one of these names to the other, and a refusal naming a tool that is
        /// not registered is worse than no refusal: the agent retries a name nothing answers to. One
        /// definition, the same argument <see cref="Key"/> already makes.
        /// </remarks>
        public const string PlainToolName = "set_breakpoint";

        /// <summary>
        /// The tool that may carry a condition or a tracepoint message — <c>ToolRisk.Command</c>, so
        /// every mode below AcceptAll prompts, every time.
        /// </summary>
        /// <remarks>
        /// A separate NAME rather than an argument on <see cref="PlainToolName"/>, for the reason
        /// <c>execute_expression</c> is a separate name from <c>read_expression</c>: the permission
        /// request for an MCP tool arrives before the <c>tool_call</c> frame carrying its arguments, so
        /// a tier resolved from the arguments is resolved from nothing. The remark on
        /// <c>IdeToolDescriptors.ExecuteExpression</c> records the measurement.
        /// </remarks>
        public const string ExpressionToolName = "set_expression_breakpoint";

        /// <summary>
        /// How two breakpoints are told apart — and therefore how we recognise ours later.
        /// </summary>
        /// <remarks>
        /// Paths compare case-insensitively because Windows does, and because the model's spelling of a
        /// path is its own choice and can change mid-conversation (the <see cref="AgentPath"/> lesson).
        /// One definition, shared by the setter and the clearer: two readers of one key that can disagree
        /// is exactly the defect <see cref="AgentPath"/> exists to prevent, and here it would surface as
        /// <c>clear_breakpoints</c> quietly declining to remove a breakpoint we had just set.
        /// </remarks>
        public static string Key(string file, int line) => (file ?? string.Empty) + "|" + line;

        /// <summary>The comparer <see cref="Key"/> values must be looked up with.</summary>
        public static StringComparer KeyComparer => StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// What every tag we write starts with — and the whole of the "did we set this" test.
        /// </summary>
        /// <remarks>
        /// The colon is part of the guard, not punctuation. A bare <c>code-wicket</c> prefix would
        /// also claim a tag written by some future <c>code-wicket-helper</c>, and a wrongly claimed
        /// breakpoint is one we would then happily DELETE. Same shape as the server half of an
        /// <c>AllowedTools</c> rule: the delimiter is what stops one name reaching into another's.
        /// </remarks>
        public const string TagPrefix = "code-wicket:";

        /// <summary>Stands in for the conversation when there isn't one yet. Matches no conversation.</summary>
        private const string UnknownConversation = "unknown";

        /// <summary>How much of a conversation id the tag carries.</summary>
        /// <remarks>
        /// Enough to tell a user's own conversations apart, which is all the tag is asked to do — it is
        /// never resolved back to a session. Ids are 32 hex characters (<c>Guid.ToString("N")</c>), so a
        /// full one would triple the tag's length to buy a uniqueness nothing needs.
        /// </remarks>
        private const int ShortIdLength = 8;

        /// <summary>
        /// The tag written on every breakpoint the agent sets: <c>code-wicket:&lt;short id&gt;</c>.
        /// </summary>
        /// <remarks>
        /// One field carrying two scopes. The PREFIX answers "did the agent set this", which is what
        /// clearing everything the agent has ever set needs; the whole tag answers "did THIS conversation
        /// set it". Neither needs a second marker, and a breakpoint cannot end up in one bucket and not
        /// the other.
        /// <para>
        /// It goes on the breakpoint rather than in a host-side list because breakpoints live in the
        /// <c>.suo</c> and outlive both the chat window and devenv (verified in Visual Studio, 2026-08-26, along with
        /// the tracepoint message, the condition and the hit count). A list that dies with the window
        /// cannot describe a thing that does not.
        /// </para>
        /// <para>
        /// It rides <c>Breakpoint2.Tag</c>, which VS persists and exports as <c>AutomationInfo</c> — a
        /// DIFFERENT slot from the breakpoint <c>Labels</c> shown in the Breakpoints window, confirmed
        /// from VS's own export (issue #73). A label would be the visible, filterable marker and would
        /// otherwise be preferable, but <c>Breakpoint2</c> exposes no way to write one, so this is not
        /// the better choice among two — it is the only one automation can reach.
        /// </para>
        /// </remarks>
        public static string Tag(string? conversationId) => TagPrefix + ShortConversationId(conversationId);

        /// <summary>
        /// Strips the prefix <paramref name="tag"/> carries, yielding the conversation half. False when
        /// the tag is not ours.
        /// </summary>
        /// <remarks>
        /// One function so "is this ours" and "whose is it" cannot disagree — the same one-definition
        /// argument the <see cref="Key"/> comment already makes. A second reader of the prefix is free
        /// to answer yes to the first question and something shifted to the second.
        /// </remarks>
        private static bool TryStripPrefix(string? tag, out string conversation)
        {
            conversation = string.Empty;
            if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            conversation = tag.Substring(TagPrefix.Length);
            return true;
        }

        /// <summary>The conversation half of a tag: lowercase, alphanumeric, capped.</summary>
        /// <remarks>
        /// Non-alphanumerics are dropped rather than escaped. Ids are GUIDs today and cannot contain the
        /// separator, but a tag whose suffix could reintroduce a colon would make the prefix test
        /// ambiguous — and a guard that holds only while nobody changes the id format is not a guard.
        /// </remarks>
        public static string ShortConversationId(string? conversationId)
        {
            if (conversationId is null)
                return UnknownConversation;

            var chars = new List<char>(ShortIdLength);
            foreach (var c in conversationId)
            {
                if (!char.IsLetterOrDigit(c))
                    continue;
                chars.Add(char.ToLowerInvariant(c));
                if (chars.Count == ShortIdLength)
                    break;
            }

            return chars.Count == 0 ? UnknownConversation : new string(chars.ToArray());
        }

        /// <summary>True when <paramref name="tag"/> is one of ours, from any conversation.</summary>
        public static bool IsAgentTag(string? tag) => TryStripPrefix(tag, out _);

        /// <summary>
        /// True when <paramref name="tag"/> is ours AND was written by <paramref name="conversationId"/>.
        /// </summary>
        /// <remarks>
        /// A null or unidentifiable conversation matches NOTHING, rather than matching the tags that also
        /// could not identify themselves. Two breakpoints that both failed to record where they came from
        /// are not thereby from the same place, and the cost of getting that wrong is deleting someone
        /// else's breakpoint.
        /// </remarks>
        public static bool IsFromConversation(string? tag, string? conversationId)
        {
            if (!TryStripPrefix(tag, out var written))
                return false;

            var id = ShortConversationId(conversationId);
            return id != UnknownConversation
                   && string.Equals(written, id, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates a requested batch, canonicalises each path against <paramref name="root"/> and
        /// collapses duplicate locations. Any invalid row refuses the WHOLE batch — see
        /// <see cref="BreakpointBatch.Error"/>.
        /// </summary>
        /// <param name="specs">The breakpoints as the agent asked for them.</param>
        /// <param name="root">
        /// The directory a relative path is measured from. Use <see cref="AgentPath.PreferredRoot"/>: the
        /// ACP session's working directory first, the solution root only as a fallback (issue #54).
        /// Getting this wrong does not error — it names a DIFFERENT REAL FILE, and a breakpoint on the
        /// wrong copy of a file simply never gets hit, which reads as the agent having guessed the wrong
        /// line.
        /// </param>
        /// <param name="allowExpressions">
        /// Whether the tool this call arrived on may place a <see cref="BreakpointSpec.Condition"/> or a
        /// <see cref="BreakpointSpec.PrintMessage"/>: true for <see cref="ExpressionToolName"/>, false for
        /// <see cref="PlainToolName"/>. It is a REQUIRED parameter and deliberately has no default —
        /// a caller that forgets which of the two names it is serving would forget in the unsafe
        /// direction, and the same argument put <c>MaxDepth</c> on the evaluator's recursive call rather
        /// than in a comment.
        /// </param>
        public static BreakpointBatch Prepare(IReadOnlyList<BreakpointSpec>? specs, string? root, bool allowExpressions)
        {
            if (specs is null || specs.Count == 0)
                return Refuse("'breakpoints' must contain at least one entry, each with 'file' and 'line'.");

            if (specs.Count > MaxPerCall)
            {
                return Refuse(
                    $"too many breakpoints in one call: {specs.Count}, and the limit is {MaxPerCall}. " +
                    "Split the request, or set fewer and more targeted stops.");
            }

            var prepared = new List<BreakpointSpec>(specs.Count);
            var notes = new List<string>();
            // Maps a location to where it sits in 'prepared', so a repeat replaces it in place and the
            // batch keeps the order the agent asked for.
            var seen = new Dictionary<string, int>(KeyComparer);

            for (var i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                if (spec is null)
                    return Refuse($"breakpoint {i + 1} is empty; each entry needs 'file' and 'line'.");

                // Explicit null/length checks rather than IsNullOrWhiteSpace: net472's overload carries no
                // [NotNullWhen], so the shared slice would warn on every use of 'file' below.
                var file = spec.File?.Trim();
                if (file is null || file.Length == 0)
                    return Refuse($"breakpoint {i + 1} has no 'file'.");

                if (spec.Line < 1)
                {
                    return Refuse(
                        $"breakpoint {i + 1} ({file}) has line {spec.Line}; 'line' is 1-based, so it must be 1 or greater.");
                }

                if (spec.HitCount is { } hits && hits < 1)
                {
                    return Refuse(
                        $"breakpoint {i + 1} ({file}) has hitCount {hits}; omit it to stop on every hit, " +
                        "or pass 1 or greater to stop on the Nth.");
                }

                var normalized = new BreakpointSpec(AgentPath.Canonical(file, root), spec.Line)
                {
                    // An empty condition is not a condition. Left as "" it would reach the debugger as
                    // "always" while showing on the card as a condition the user cannot read, so the two
                    // would be describing different breakpoints.
                    Condition = Blank(spec.Condition),
                    HitCount = spec.HitCount,
                    Reason = Blank(spec.Reason),
                    // Blank the same way, and for a sharper reason than the others: an empty message
                    // would still flip the breakpoint to "continue on hit", producing a breakpoint that
                    // prints nothing and never stops — indistinguishable, to the user staring at the
                    // gutter, from one that simply does not work.
                    PrintMessage = Blank(spec.PrintMessage),
                };

                // THE TIER RIDES THE NAME, and this is where the plain name is held to it. A condition
                // and a printMessage are not data the debugger stores: Visual Studio EVALUATES both
                // inside the debuggee when the line is reached — function evaluation, property getters,
                // method calls — which is exactly the capability execute_expression charges a prompt
                // for. PlainToolName is Edit-tier and auto-approves at AcceptEdits, so it may not carry
                // them; ExpressionToolName is Command-tier and may.
                //
                // Checked AFTER Blank, deliberately: an empty condition is not a condition (it is
                // already dropped a few lines up), so a caller that sends "" gets the plain stop it
                // asked for rather than a refusal about an expression it did not write.
                //
                // REFUSED, never stripped. A tool asked to place a condition that quietly places a plain
                // stop reports back a breakpoint the agent believes is filtered — and the first thing it
                // does with that belief is tell the user the loop only stops on iteration 47. That is
                // the apply_code_fix shape: the summary and the effect disagreeing, with only the
                // summary being read.
                if (!allowExpressions && (normalized.Condition is not null || normalized.PrintMessage is not null))
                {
                    var carried =
                        normalized.Condition is not null && normalized.PrintMessage is not null ? "a 'condition' and a 'printMessage'"
                        : normalized.Condition is not null ? "a 'condition'"
                        : "a 'printMessage'";

                    return Refuse(
                        $"breakpoint {i + 1} ({file}) carries {carried}, which {PlainToolName} does not place: " +
                        "Visual Studio evaluates both inside the user's own running program when the line " +
                        $"is reached. Nothing was set. Send the whole batch to {ExpressionToolName} instead " +
                        "— same arguments, and it asks the user before anything is placed.");
                }

                var key = Key(normalized.File, normalized.Line);
                if (seen.TryGetValue(key, out var at))
                {
                    // Not an error: one location holds one breakpoint, so a repeat is the agent revising
                    // itself and the later entry is the revision. Said out loud, because silently dropping
                    // a row the caller listed is how a condition it carefully set disappears.
                    notes.Add($"{normalized.File}:{normalized.Line} was listed more than once; kept the last entry.");
                    prepared[at] = normalized;
                    continue;
                }

                seen[key] = prepared.Count;
                prepared.Add(normalized);
            }

            return new BreakpointBatch(prepared, notes, Error: null);
        }

        private static BreakpointBatch Refuse(string error) =>
            new BreakpointBatch(Array.Empty<BreakpointSpec>(), Array.Empty<string>(), error);

        private static string? Blank(string? value)
        {
            var trimmed = value?.Trim();
            return trimmed is null || trimmed.Length == 0 ? null : trimmed;
        }
    }
}
