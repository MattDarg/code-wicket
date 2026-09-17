using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// Translates ACP wire shapes into the host-facing <see cref="AgentEvent"/> and
    /// <see cref="PermissionRequest"/> types. Kept in one place so it can be unit-tested
    /// and adjusted against Kiro's ACP build without touching session plumbing.
    /// </summary>
    internal static class AcpMapper
    {
        /// <summary>Maps a single <c>session/update</c> payload into zero or more agent events.</summary>
        /// <param name="contentCache">
        /// Optional per-session path→content cache (see <see cref="AcpClientTarget"/>). When supplied,
        /// file contents the agent reads are recorded and used to recover a real "before" for a
        /// degenerate whole-file edit; null disables this (events map as the agent reported them).
        /// </param>
        /// <param name="writeCapture">
        /// Optional per-session map of writes the host performed on the agent's behalf, keyed by
        /// normalized absolute path (see <see cref="AcpClientTarget"/>). When an edit's path matches, the
        /// captured before/after replaces the agent's reported diff — it's the transformation that
        /// actually hit the file. Null disables this (events map as the agent reported them).
        /// </param>
        /// <param name="agentRoot">
        /// The ACP session's cwd, used to root a relative path the agent reports (see
        /// <see cref="NormalizeLocalPath"/>). Null leaves relative paths relative — which is only safe
        /// when nothing downstream keys on the path, so real sessions always supply it.
        /// </param>
        /// <param name="subagentParents">
        /// Optional per-session map of a Kiro v3 sub-agent's subtask id to the <c>invoke_subagent_*</c>
        /// row that started it (see <see cref="CacheSubagentParent"/>). Null leaves v3 sub-agent calls
        /// unattributed — they render top-level, which is what they did before issue #125.
        /// </param>
        /// <param name="backgroundLaunches">
        /// Optional per-session set of tool call ids known to have been launched in the background (see
        /// <see cref="CacheBackgroundLaunch"/>). Null makes every completion a real one.
        /// </param>
        /// <param name="planToolCalls">
        /// Optional per-session set of tool call ids whose generic row was RETIRED into the plan card
        /// (see <see cref="CachePlanToolCall"/>). Null makes every plan-looking frame suppress its
        /// completion — the behaviour this had throughout, and exact on any engine that puts the
        /// arguments on the opening frame; see the <c>tool_call_update</c> case for why that must not
        /// be assumed of every engine.
        /// </param>
        public static IEnumerable<AgentEvent> Map(
            JsonElement update,
            IDictionary<string, string>? contentCache = null,
            IDictionary<string, FileWriteResult>? writeCapture = null,
            string? agentRoot = null,
            IDictionary<string, string>? subagentParents = null,
            ISet<string>? backgroundLaunches = null,
            ISet<string>? planToolCalls = null)
        {
            if (update.ValueKind != JsonValueKind.Object)
                yield break;
            if (!update.TryGetProperty("sessionUpdate", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
                yield break;

            // Record file contents the agent reads, so a later degenerate edit can recover its "before".
            if (kindEl.GetString() == "tool_call_update")
                CacheReadContent(update, contentCache, agentRoot);

            switch (kindEl.GetString())
            {
                case "agent_message_chunk":
                    if (TryGetContentText(update, out var text))
                        yield return new AgentEvent.AssistantTextDelta(text);
                    break;

                case "agent_thought_chunk":
                    if (TryGetContentText(update, out var thought))
                        yield return new AgentEvent.ThinkingDelta(thought);
                    break;

                case "tool_call":
                    // A task-list (plan) tool is rendered as one live plan card driven by the update's
                    // full list, so skip its generic "started" row.
                    if (IsPlanToolCall(update))
                        break;

                    // ACP's standard way to report file edits is the tool_call `content` array with
                    // `type:"diff"` entries (absolute path + oldText/newText) — not the client
                    // fs/write_text_file, which agents may or may not use. Prefer that surface so this
                    // works for any conformant ACP agent; only fall back to a generic tool row when the
                    // call carries no diff. (rawInput is provider-specific and its path may be relative.)
                    var startedEdits = false;
                    foreach (var edit in ExtractEdits(update, contentCache, writeCapture, agentRoot, subagentParents))
                    {
                        startedEdits = true;
                        yield return edit;
                    }
                    if (!startedEdits)
                    {
                        // Claude's own label beats the ACP title where it sent one: the title of a shell
                        // call is the raw command, and cc.title is the sentence describing it (#125).
                        var startTitle = ExtractClaudeTitle(update)
                            ?? GetString(update, "title") ?? GetString(update, "kind") ?? "tool";
                        // Same extraction the permission path uses. It tells the host that rawInput
                        // is a THIRD PARTY's schema, which is not a detail it can work out for
                        // itself — see issue #131. Resolved before the kind, which consults it.
                        var startToolName = ExtractToolName(update, startTitle);
                        yield return new AgentEvent.ToolCallStarted(
                            GetString(update, "toolCallId") ?? string.Empty,
                            startTitle,
                            ResolveToolKind(update, GetString(update, "kind"), startToolName),
                            update.TryGetProperty("rawInput", out var rawInput) ? StripMeta(rawInput) : null,
                            startToolName,
                            ResolveParentToolCallId(update, subagentParents),
                            IsSubagentLaunchCall(update));
                    }
                    break;

                case "tool_call_update":
                    var id = GetString(update, "toolCallId") ?? string.Empty;

                    // A plan tool's update carries the full task list — surface it as a PlanUpdated and
                    // suppress the generic completion row (the plan card stands in for it).
                    var planFrame = false;
                    if (TryExtractPlan(update, out var planTitle, out var planItems))
                    {
                        yield return new AgentEvent.PlanUpdated(planTitle, planItems);
                        planFrame = true;
                    }
                    else if (IsPlanToolCall(update))
                    {
                        // Kiro clears the task list the moment its last item completes: the final
                        // "complete" update's rawOutput carries tasks:[] (and an empty description), so
                        // TryExtractPlan can't see it. Surface that disposal as an empty PlanUpdated —
                        // "the plan finished, tick everything off" — instead of dropping the frame, or
                        // the card never shows the last item done (issue #17).
                        if (IsPlanCompletion(update))
                            yield return new AgentEvent.PlanUpdated(null, Array.Empty<PlanItem>());
                        planFrame = true;
                    }

                    if (planFrame)
                    {
                        // Whether this call HAS a generic row was decided ONCE, on its opening frame,
                        // and remembered (issue #190). It used to be re-decided here, from whether THIS
                        // frame looks like a plan — and the two readings need not agree: some adapters
                        // open a call with a placeholder title and EMPTY rawInput and stream the
                        // arguments in on an update (see below), so the opening frame started an
                        // ordinary row and this one then retired it into the plan card, leaving a start
                        // with no completion. A call the host believes is still running strands the
                        // tray's "next step" release for the rest of the turn. One open/close pair per
                        // id, decided once.
                        //
                        // Where no cache is supplied the old reading stands verbatim — every plan frame
                        // suppresses — which is exact on any engine that puts the arguments on the
                        // opening frame, and is what the frame-at-a-time unit tests exercise.
                        var retiredIntoPlanCard = planToolCalls is null || planToolCalls.Contains(id);

                        // A row WAS started for this call, so it has to be closed however its content is
                        // now being rendered. Closed and not enriched: the plan card is what says what
                        // the call did, and putting the task list on the row as well would draw it
                        // twice. A frame that has not settled yet leaves the call open, which is true.
                        if (!retiredIntoPlanCard)
                        {
                            var planStatus = GetString(update, "status");
                            if (planStatus == "completed" || planStatus == "failed")
                                yield return new AgentEvent.ToolCallCompleted(
                                    id, planStatus == "completed", null, ExtractErrorText(update), false);
                        }

                        break;
                    }

                    // Some agents (the Claude Code adapter) open a tool call with a placeholder
                    // title and empty rawInput, then send the real title/arguments on an update once
                    // the input has streamed in. Surface that enrichment so the host can fill in the
                    // already-rendered row.
                    var updatedTitle = ExtractClaudeTitle(update) ?? GetString(update, "title");
                    var updatedKind = GetString(update, "kind");
                    var updatedInput = GetRawInputObjectJson(update);
                    var updatedToolName = ExtractToolName(update, updatedTitle ?? string.Empty);
                    // The gate still tests the RAW kind: ResolveToolKind only ever rewrites a non-null
                    // "other", so it can neither create nor suppress an update event.
                    if (updatedTitle is not null || updatedKind is not null || updatedInput is not null)
                        yield return new AgentEvent.ToolCallUpdated(
                            id, updatedTitle,
                            ResolveToolKind(update, updatedKind, updatedToolName),
                            updatedInput, updatedToolName,
                            ResolveParentToolCallId(update, subagentParents),
                            IsSubagentLaunchCall(update));

                    // Diffs can also be finalized on an update; surface any here too. This is the frame
                    // that follows a client-fs write, so it's where the authoritative capture lands.
                    foreach (var edit in ExtractEdits(update, contentCache, writeCapture, agentRoot, subagentParents))
                        yield return edit;

                    var status = GetString(update, "status");
                    if (status == "completed" || status == "failed")
                    {
                        // A shell tool can "complete" yet have failed: Kiro reports the process exit code
                        // in its structured rawOutput, so a non-zero exit is a failure even at status
                        // "completed" (otherwise a failed build shows a green tick).
                        var success = status == "completed" && !ExitedNonZero(update);

                        // An async sub-agent "completes" at LAUNCH. Say so, and drop the result text
                        // while doing it: what the adapter returns here is a block of internal metadata
                        // whose own first sentence asks that no part of it be quoted — and it is a
                        // launch receipt, not an answer, so there is nothing in it a reader wants. The
                        // arguments (prompt, subagent_type, run_in_background) are still on the row's
                        // detail, where they came from the call rather than from its plumbing.
                        var launched = backgroundLaunches?.Contains(id) == true;
                        yield return new AgentEvent.ToolCallCompleted(
                            id,
                            success,
                            launched ? null : ExtractResultText(update),
                            ExtractErrorText(update),
                            launched);
                    }
                    else
                    {
                        // Live output: Kiro streams a running shell command's stdout/stderr as content[]
                        // text entries on not-yet-completed updates (proven in issue #22's capture), which
                        // were previously dropped — the row showed nothing until the call finished. Surface
                        // each chunk so the host can show the command running; the completion frame's
                        // structured rawOutput is a superset and replaces the accumulated text. (Diff-type
                        // content is handled by ExtractDiffEdits above and never matches here.)
                        if (ExtractContentText(update) is { } raw && ClampChunk(raw) is { } chunk)
                            yield return new AgentEvent.ToolCallOutputChunk(id, chunk);
                        if (status is not null)
                            yield return new AgentEvent.ToolCallProgress(id, status);
                    }
                    break;

                case "plan":
                    // The spec-standard plan channel: { entries:[{ content, priority, status }] }, the
                    // full list each time. Provider-agnostic — any conformant ACP agent gets the card.
                    // (Kiro doesn't use this; it drives the task-list tool handled above.)
                    if (TryExtractAcpPlan(update, out var planEntries))
                        yield return new AgentEvent.PlanUpdated(null, planEntries);
                    break;

                case "usage_update":
                    // Claude Code: { used, size, cost?:{amount,currency},
                    //                _meta:{ "_claude/rateLimit":{ status, rateLimitType, resetsAt } } }.
                    // Streamed repeatedly through a turn as the window fills.
                    if (TryMapClaudeUsage(update, out var claudeUsage))
                        yield return new AgentEvent.UsageUpdated(claudeUsage);

                    // A returning background sub-agent rides an ORDINARY usage frame, marked only by
                    // its origin (issue #125). Both are yielded: the frame carries real consumption
                    // figures as well, so reading it as a notification must not cost the usage ring its
                    // update. Nothing else on the frame identifies the task - see
                    // AgentEvent.BackgroundTaskReturned for why a count is nonetheless enough.
                    if (IsTaskNotification(update))
                        yield return new AgentEvent.BackgroundTaskReturned();
                    break;

                case "session_info_update":
                    // Kiro puts several unrelated things under this one update kind, discriminated by
                    // _meta.kiro.kind — context_usage is the one carrying consumption; turn_end and the
                    // rest are handled elsewhere or ignored.
                    if (TryMapKiroUsage(update, out var kiroUsage))
                        yield return new AgentEvent.UsageUpdated(kiroUsage);

                    // The kinds Kiro means for the USER: a refusal it asks us to display, and the end
                    // of a context compaction (issues #208, #85). Yielded BESIDE the usage above, not
                    // instead of it — a compaction's own frames carry no figures, but nothing
                    // guarantees a future kind will not, and a reader that stopped at the first match
                    // would drop the ring's update to show a notice.
                    if (MapKiroNotice(update) is { } kiroNotice)
                        yield return kiroNotice;
                    break;

                // Kiro's "_kiro.dev/*" extensions are not session updates; the ones we read have their own parsers below.
            }
        }

        /// <summary>
        /// <summary>
        /// Whether this frame is Claude announcing that a background sub-agent reported back.
        /// </summary>
        /// <remarks>
        /// Matched on <c>_meta."_claude/origin".kind == "task-notification"</c> and nothing else. The key
        /// is a literal with a slash in it, which is why it is read through the indexer rather than as a
        /// property name. An unrecognised origin is not a notification: a usage frame is otherwise
        /// ordinary, and mistaking one for a return would settle rows whose tasks are still running.
        /// </remarks>
        private static bool IsTaskNotification(JsonElement update) =>
            update.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("_claude/origin", out var origin)
            && origin.ValueKind == JsonValueKind.Object
            && origin.TryGetProperty("kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            && string.Equals(kind.GetString(), "task-notification", StringComparison.Ordinal);

        /// <summary>
        /// Claude Code's <c>usage_update</c>. Context fill arrives as absolutes, so the cross-backend
        /// percentage is derived here rather than in the UI — a zero/absent window size yields no
        /// percentage instead of a divide-by-zero.
        /// </summary>
        internal static bool TryMapClaudeUsage(JsonElement update, out UsageReport usage)
        {
            usage = new UsageReport();
            var used = GetInt(update, "used");
            var size = GetInt(update, "size");
            if (used is null && size is null)
                return false;

            var rateLimit = TryGetObject(update, "_meta", out var meta) &&
                            meta.TryGetProperty("_claude/rateLimit", out var rl) &&
                            rl.ValueKind == JsonValueKind.Object
                ? rl
                : (JsonElement?)null;

            TryGetObject(update, "cost", out var cost);

            usage = new UsageReport
            {
                ContextUsedTokens = used,
                ContextWindowTokens = size,
                ContextPercent = used is { } u && size is { } s && s > 0 ? u * 100.0 / s : null,
                Cost = GetDouble(cost, "amount"),
                CostCurrency = GetString(cost, "currency"),
                RateLimitStatus = rateLimit is { } r ? GetString(r, "status") : null,
                RateLimitType = rateLimit is { } r2 ? GetString(r2, "rateLimitType") : null,
                RateLimitResetsAt = rateLimit is { } r3 ? GetLong(r3, "resetsAt") : null,
            };
            return true;
        }

        /// <summary>
        /// The things Kiro tells the USER through <c>_meta.kiro.kind</c> on a
        /// <c>session_info_update</c> - a refusal it wants displayed, and the two ends of a context
        /// compaction (issues #208, #85). Null when the frame carries none of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One reader over the kinds, not a branch per kind</b>, for the reason #119 and #97 both
        /// cost: the shapes that carry a fact vary per engine and per release, and a reader that
        /// yields what it recognises can gain a kind without anything else moving. Both of these
        /// arrive on the SAME frame type we already handle, which is why they were dropped in the
        /// first place - <c>session_info_update</c> reaches our code and nothing looked at
        /// <c>kind</c>.
        /// </para>
        /// <para>
        /// <b><c>display_error</c> is named for the thing we were not doing.</b> Captured six times on
        /// 2026-09-02 across six session logs, same request id, carrying
        /// <c>UsageLimitReachedError</c> and "You've reached your monthly usage limit." The
        /// <c>errorType</c> is relayed as the level because it is the backend's own discriminator;
        /// nothing branches on its value, one observed member not being a taxonomy.
        /// </para>
        /// <para>
        /// <b>The compaction pair is a fact, not an inference</b> - which is the whole reason Kiro's
        /// half of #85 is worth doing and Claude's is not. Captured live 2026-09-04: context ran
        /// 80.03% -> 20.00% with <c>summarization_started</c> and <c>summarization_completed</c>
        /// between the two readings and nothing else in the window. Claude announces the same event as
        /// ordinary assistant prose ("Compacting..."), which is indistinguishable from a conversation
        /// ABOUT compaction - the literal string appears 14 times in the capture and exactly once is
        /// the announcement.
        /// </para>
        /// <para>
        /// <b>Only the COMPLETED end is surfaced.</b> The pair is one event to a reader, and the
        /// started frame is 12ms of warning nobody can act on; announcing both would put two lines in
        /// the transcript for one thing that happened. The started kind is still recognised so it is
        /// not mistaken for an unknown kind by whoever reads this next.
        /// </para>
        /// </remarks>
        internal static AgentEvent.BackendNotice? MapKiroNotice(JsonElement update)
        {
            if (!TryGetObject(update, "_meta", out var meta) || !TryGetObject(meta, "kiro", out var kiro))
                return null;

            var kind = GetString(kiro, "kind");
            if (string.IsNullOrEmpty(kind))
                return null;

            switch (kind)
            {
                case "display_error":
                {
                    // The nested object first, the flattened copy second: Kiro sends both, and the
                    // nested one carries errorType. Neither is guaranteed, so a message found either
                    // way is shown - the alternative is dropping a refusal over its packaging.
                    var message = TryGetObject(kiro, "displayError", out var displayError)
                        ? GetString(displayError, "message") ?? GetString(kiro, "message")
                        : GetString(kiro, "message");
                    if (string.IsNullOrWhiteSpace(message))
                        return null;

                    var errorType = TryGetObject(kiro, "displayError", out var typed)
                        ? GetString(typed, "errorType")
                        : null;
                    return new AgentEvent.BackendNotice(message!, errorType ?? "error");
                }

                case "summarization_completed":
                    // Said in the host's own words rather than the backend's, because the backend
                    // supplies none - the frame carries a kind and nothing else. What the user needs
                    // is not that a job ran but that the agent's memory of this conversation is now
                    // shorter than the transcript in front of them, which is the divergence #85 exists
                    // for: the pane keeps every message, the agent does not.
                    return new AgentEvent.BackendNotice(
                        "The agent condensed this conversation to free up context. "
                        + "It now works from a summary of the earlier messages plus the recent ones, "
                        + "so detail still visible above may no longer be in its memory.");

                case "summarization_started":
                    // Recognised and deliberately silent - see the remarks.
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Kiro's <c>_kiro/system/notify</c> - a level and a message, addressed to the user (issue
        /// #208). Null when the frame carries no message.
        /// </summary>
        /// <remarks>
        /// Captured once, 2026-09-04: <c>level=warning</c>, "The selected model is experiencing high
        /// load, rate limiting applied. Consider switching models." Worth knowing before trusting it:
        /// that fired alongside the FIRST of eight HTTP 429s and nothing was sent for the other seven,
        /// including the escalation from a five-minute backoff to an hour - and the cause it named was
        /// wrong, the real one being <c>CREDIT_CONSUMPTION_RATE_EXCEEDED</c> in the CLI's stderr. So
        /// this is relayed as what the backend said, and is not the only carrier we listen on.
        /// </remarks>
        internal static AgentEvent.BackendNotice? MapSystemNotify(JsonElement parameters)
        {
            var message = GetString(parameters, "message");
            return string.IsNullOrWhiteSpace(message)
                ? null
                : new AgentEvent.BackendNotice(message!, GetString(parameters, "level"));
        }

        /// <summary>
        /// Kiro's <c>session_info_update</c> with <c>_meta.kiro.kind == "context_usage"</c>. Kiro reports
        /// a percentage but no absolute token count and no cost, so most of the report stays null; its
        /// compensation is the per-category breakdown, which no other backend supplies.
        /// </summary>
        internal static bool TryMapKiroUsage(JsonElement update, out UsageReport usage)
        {
            usage = new UsageReport();
            if (!TryGetObject(update, "_meta", out var meta) || !TryGetObject(meta, "kiro", out var kiro))
                return false;
            if (!TryGetObject(kiro, "contextUsage", out var contextUsage))
                return false;

            var percent = GetDouble(contextUsage, "usagePercentage");
            if (percent is null)
                return false;

            usage = new UsageReport
            {
                ContextPercent = percent,
                Breakdown = MapKiroBreakdown(kiro),
                // Present on the session/new response rather than every update; harmless when absent.
                SummarizeThresholdPercent = GetDouble(contextUsage, "summarizationThreshold"),
                TruncateThresholdPercent = GetDouble(contextUsage, "truncationThreshold"),
            };
            return true;
        }

        /// <summary>
        /// Kiro's <c>_kiro.dev/metadata</c> notification, which newer kiro-cli builds use to report
        /// context fill instead of the <c>session_info_update</c> shape above: the percentage sits at
        /// the params top level as <c>contextUsagePercentage</c> (same 0–100 scale), alongside
        /// <c>effort</c>/<c>turnDurationMs</c> we don't consume. No per-category breakdown here — that
        /// only ever rode <c>session_info_update</c> — so the report carries the percentage alone, which
        /// <c>ChatViewModel.MergeUsage</c> folds in without clobbering a breakdown a prior frame supplied.
        /// </summary>
        internal static bool TryMapKiroMetadata(JsonElement parameters, out UsageReport usage)
        {
            usage = new UsageReport();
            var percent = GetDouble(parameters, "contextUsagePercentage");
            if (percent is null)
                return false;

            usage = new UsageReport { ContextPercent = percent };
            return true;
        }

        // Kiro's breakdown is an object of named categories, not an array, so the labels are mapped
        // explicitly: it keeps the display order stable and turns wire names into readable ones. An
        // unrecognised category is skipped rather than shown raw — a new key appearing upstream should
        // not put "sessionFilesV2" in front of a user.
        private static readonly (string Key, string Label)[] KiroBreakdownCategories =
        {
            ("yourPrompts", "Your prompts"),
            ("kiroResponses", "Agent responses"),
            ("tools", "Tool definitions"),
            ("contextFiles", "Context files"),
            ("sessionFiles", "Session files"),
        };

        // Sub-categories live INSIDE a parent category rather than beside it. `tools` splits into the
        // agent's own built-ins and the MCP servers' tool definitions — the only place the wire says
        // what MCP costs, and the reason the MCP roster panel carries no cost figure of its own: this
        // is an aggregate over EVERY MCP server, ours included, and nothing on the wire splits it per
        // server. Measured 2026-09-01 on kiro-cli v3:
        //   "tools":{"tokens":10283,"percent":0.9,"mcp":{"tokens":4826,...},"builtin":{"tokens":5457,...}}
        // so naming one server against that number would be a manufactured figure. BOTH halves are
        // emitted, not just mcp: the pair visibly sums to its parent, which is what lets the split
        // explain itself without a sentence of prose in a panel that has no room for one.
        private static readonly (string Parent, string Key, string Label)[] KiroBreakdownSubCategories =
        {
            ("tools", "builtin", "Built-in tools"),
            ("tools", "mcp", "MCP tools"),
        };

        private static IReadOnlyList<UsageBreakdownEntry>? MapKiroBreakdown(JsonElement kiro)
        {
            if (!TryGetObject(kiro, "breakdown", out var breakdown))
                return null;

            var entries = new List<UsageBreakdownEntry>();
            foreach (var (key, label) in KiroBreakdownCategories)
            {
                if (!TryGetObject(breakdown, key, out var entry))
                    continue;
                AddEntry(entry, label);

                // A parent's parts are emitted straight after it, so the rows read as a total
                // followed by what makes it up rather than as unrelated siblings.
                foreach (var (parent, subKey, subLabel) in KiroBreakdownSubCategories)
                {
                    if (!string.Equals(parent, key, StringComparison.Ordinal))
                        continue;
                    if (TryGetObject(entry, subKey, out var sub))
                        AddEntry(sub, subLabel);
                }
            }
            return entries.Count > 0 ? entries : null;

            void AddEntry(JsonElement obj, string rowLabel)
            {
                var tokens = GetInt(obj, "tokens");
                var percent = GetDouble(obj, "percent");
                // A category at zero is signal ("nothing is coming from context files"), so it's kept;
                // a category with neither figure is not.
                if (tokens is not null || percent is not null)
                    entries.Add(new UsageBreakdownEntry(rowLabel, tokens, percent));
            }
        }

        /// <summary>
        /// The <c>usage</c> object on Claude's <c>session/prompt</c> response — per-turn tokens with the
        /// cache split. A response, not a notification, so this is called by the session rather than
        /// from <see cref="Map"/>.
        /// </summary>
        public static UsageReport? MapPromptUsage(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
                return null;

            var report = new UsageReport
            {
                InputTokens = GetInt(usage, "inputTokens"),
                OutputTokens = GetInt(usage, "outputTokens"),
                CachedReadTokens = GetInt(usage, "cachedReadTokens"),
                CachedWriteTokens = GetInt(usage, "cachedWriteTokens"),
                TotalTokens = GetInt(usage, "totalTokens"),
            };
            return report.InputTokens is null && report.OutputTokens is null && report.TotalTokens is null
                ? null
                : report;
        }

        /// <summary>
        /// Parses a <c>_kiro.dev/subagent/list_update</c> notification (the full sub-agent roster on
        /// every change: <c>{ subagents:[{ sessionId, sessionName, initialQuery, status:{type,message},
        /// group, … }] }</c>) into a roster event. Null when the frame carries no usable entries —
        /// including the empty roster Kiro sends when the crew is disposed, which must NOT clear the
        /// host's card (the rows' final states stand).
        /// </summary>
        public static AgentEvent.SubagentsUpdated? MapSubagentList(JsonElement parameters)
        {
            if (!TryGetArray(parameters, "subagents", out var subagents))
                return null;

            var list = new List<SubagentInfo>();
            foreach (var entry in subagents.EnumerateArray())
            {
                var sessionId = GetString(entry, "sessionId");
                if (string.IsNullOrEmpty(sessionId))
                    continue;

                var status = "unknown";
                string? statusMessage = null;
                if (TryGetObject(entry, "status", out var statusObj))
                {
                    status = GetString(statusObj, "type") ?? "unknown";
                    statusMessage = GetString(statusObj, "message");
                }

                list.Add(new SubagentInfo(
                    sessionId!,
                    GetString(entry, "sessionName") ?? sessionId!,
                    GetString(entry, "initialQuery"),
                    status,
                    statusMessage,
                    GetString(entry, "group")));
            }

            return list.Count > 0 ? new AgentEvent.SubagentsUpdated(list) : null;
        }

        /// <summary>
        /// Parses a <c>_kiro.dev/mcp/server_initialized</c> notification (<c>{ sessionId, serverName }</c>,
        /// sent as each of Kiro's own MCP servers finishes connecting — they connect asynchronously
        /// after <c>session/new</c>, and a slow one lands mid- or post-turn) into the server's name.
        /// Null when the frame carries no usable name. Kiro re-sends the notification for the same
        /// server (observed live), so callers must dedupe by name.
        /// </summary>
        public static string? MapMcpServerInitialized(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return null;
            var name = GetString(parameters, "serverName");
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>
        /// Parses Kiro <b>v3</b>'s <c>_kiro/mcp/status</c> notification into the full roster. Frame
        /// shape from a live capture (our own server was named differently at capture; the SHAPE is what
        /// this parses and the reader is a pure echo, so the name here tracks what we are called now):
        /// <code>
        /// {"sessionId":"sess_...","servers":[
        ///   {"name":"aws-mcp","authType":"oauth","status":"connecting"},
        ///   {"name":"code-wicket","status":"connected",
        ///    "tools":[{"name":"build_solution","description":"...","disabled":false,"inputSchema":{}}]}]}
        /// </code>
        /// <para>v3 sends no <c>server_initialized</c> at all (issue #97): it publishes the whole roster
        /// as a snapshot, repeated as states change (measured live on kiro-cli 2.16.2: two frames with
        /// both servers <c>connecting</c>, then one per server as each came up). Every configured server
        /// is named — including ones still connecting, and including OUR OWN bridge — before
        /// <c>session/new</c> returns, which is what makes <see cref="McpRoster.NamesWholeConfiguredSet"/>
        /// true here and only here.</para>
        /// <para>Tool <em>descriptions</em> are deliberately dropped. They are the bulk of the frame (a
        /// single one runs to a paragraph) and nothing displays them, so carrying them would put
        /// kilobytes across the IPC boundary on every snapshot for nothing.</para>
        /// </summary>
        public static McpRoster? MapMcpRoster(JsonElement parameters)
        {
            if (!TryGetArray(parameters, "servers", out var servers))
                return null;

            var list = new List<McpServerStatus>();
            foreach (var server in servers.EnumerateArray())
            {
                if (server.ValueKind != JsonValueKind.Object)
                    continue;
                if (GetString(server, "name") is not { Length: > 0 } name)
                    continue;

                var status = GetString(server, "status");
                List<string>? toolNames = null;
                if (TryGetArray(server, "tools", out var tools))
                {
                    toolNames = new List<string>();
                    foreach (var tool in tools.EnumerateArray())
                        if (tool.ValueKind == JsonValueKind.Object &&
                            GetString(tool, "name") is { Length: > 0 } toolName)
                        {
                            toolNames.Add(toolName);
                        }
                }

                list.Add(new McpServerStatus(
                    name,
                    // The SAME predicate the readiness notice uses — MapConnectedMcpServers is now a
                    // projection over this method precisely so the two cannot drift apart.
                    IsConnectedStatus(status),
                    status,
                    GetString(server, "authType"),
                    toolNames?.Count,
                    toolNames));
            }

            return new McpRoster(list, NamesWholeConfiguredSet: true);
        }

        /// <summary>
        /// Wraps a single <c>_kiro.dev/mcp/server_initialized</c> frame as a one-server roster, for the
        /// default engine. <see cref="McpRoster.NamesWholeConfiguredSet"/> is <b>false</b>: that engine
        /// announces a server only when it comes up, so the frame is evidence about one server and says
        /// nothing whatever about how many others exist, or whether any of them are dead.
        /// </summary>
        public static McpRoster? MapInitializedServerRoster(JsonElement parameters)
        {
            if (MapMcpServerInitialized(parameters) is not { } name)
                return null;

            // "initialized" IS the connected signal on this engine — there is no status field to read.
            return new McpRoster(
                new[]
                {
                    new McpServerStatus(
                        name, IsConnected: true, RawStatus: null, AuthType: null,
                        ToolCount: null, ToolNames: null),
                },
                NamesWholeConfiguredSet: false);
        }

        // Only an explicit "connected" counts, so a state we have not seen — a failure, an auth prompt —
        // can never be announced as a working server.
        private static bool IsConnectedStatus(string? status) =>
            string.Equals(status, "connected", StringComparison.Ordinal);

        /// <summary>
        /// The names a <c>_kiro/mcp/status</c> frame reports as <c>connected</c>, for the issue-#19
        /// readiness notice. The snapshot repeats every already-connected server on every frame, so
        /// callers must dedupe by name exactly as they do for v2's re-sent notifications.
        /// <para><b>A projection over <see cref="MapMcpRoster"/> rather than a second parse</b>, so the
        /// notice and the roster panel can never disagree about which servers are working — the same
        /// reasoning that has the plan reader and the cleared-list reader share one set of candidates.</para>
        /// </summary>
        public static IEnumerable<string> MapConnectedMcpServers(JsonElement parameters) =>
            MapMcpRoster(parameters) is { } roster
                ? roster.Servers.Where(s => s.IsConnected).Select(s => s.Name)
                : Enumerable.Empty<string>();

        /// <summary>
        /// Reads a <c>user_message_chunk</c> — the user's own words, as the backend replayed them — for
        /// a conversation being IMPORTED from the backend's history (issue #108).
        /// <para><b>Deliberately not a case in <see cref="Map"/>, and this must stay true.</b> The
        /// Claude adapter emits <c>user_message_chunk</c> on a LIVE turn as well as on a replay (read in
        /// its <c>dist/acp-agent.js</c>), so a mapper case would echo the user's message straight back
        /// into the transcript and into the saved log on every ordinary send — and every offline test
        /// would still pass, since nothing offline sends the frame twice. Its only caller is inside the
        /// import gate, which is a place a live frame cannot reach.</para>
        /// <para>Two things happen to the text on the way out. Our own prompt framing is stripped
        /// (<see cref="HostPromptBlocks.Strip"/>) — importing a conversation WE created otherwise
        /// renders our workspace block or mid-turn wording as the user's words. And a content block
        /// that is not text is named rather than dropped: an image cannot be recovered from a replay
        /// (the bytes are not in the frame, and our attachment directory knows nothing of a
        /// conversation it never saw), so the honest answer is a marker saying one was there — issue
        /// #118's rule read backwards. False when there is nothing left to show.</para>
        /// </summary>
        internal static bool TryReadUserMessageText(JsonElement update, out string text)
        {
            text = string.Empty;
            if (update.ValueKind != JsonValueKind.Object ||
                GetString(update, "sessionUpdate") != "user_message_chunk" ||
                !update.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var blockType = GetString(content, "type");
            if (blockType == "text")
            {
                text = HostPromptBlocks.Strip(GetString(content, "text"));
                return text.Length > 0;
            }

            // Mirrors ChatViewModel.DescribeAttachments: name what was there, in the same shape a
            // summary hand-off uses, so a reader (person or agent) is never left reasoning from an
            // account with the subject silently removed.
            var mediaType = GetString(content, "mimeType");
            var label = blockType == "image" ? "image" : blockType ?? "content";
            text = string.IsNullOrEmpty(mediaType)
                ? $"[{label} attached: not recoverable from the backend's history]"
                : $"[{label} attached: {mediaType} — not recoverable from the backend's history]";
            return true;
        }

        /// <summary>
        /// True when a <c>session/update</c> is one of the frames a <c>session/load</c> is replaying at
        /// us — conversation HISTORY, not work happening now. Kiro's v3 engine marks every one with
        /// <c>_meta.kiro.replay: true</c> (that is what <c>replayMarking</c> in its <c>initialize</c>
        /// response advertises); measured 2026-08-10 on kiro-cli 2.16.2, all 100 frames of a real
        /// resume carried it and no live frame did.
        /// <para>Why it is worth reading when a load window already suppresses these: the window is an
        /// estimate of a boundary and the mark is a fact about a frame. The one failure it cannot cover
        /// is issue #33's own — a replay frame arriving <em>after</em> the flag clears is otherwise
        /// indistinguishable from live work, and gets appended behind the user's new message and
        /// re-recorded.</para>
        /// <para><b>Match <c>replay</c>, never <c>replayId</c>.</b> They read like the same field and
        /// mean opposite things: <c>replayId</c> appears on <em>live</em> <c>agent_message_chunk</c>s (a
        /// per-utterance id, suffixed <c>-say</c>) and was present on 12 live frames of the same session
        /// where the replay itself carried none. Keying on it would suppress live output and ingest
        /// history. Only a literal <c>true</c> counts, so a future engine that put something else under
        /// the name cannot quietly turn live work into history.</para>
        /// </summary>
        public static bool IsReplayedUpdate(JsonElement update)
        {
            return update.ValueKind == JsonValueKind.Object &&
                   TryGetObject(update, "_meta", out var meta) &&
                   TryGetObject(meta, "kiro", out var kiro) &&
                   kiro.TryGetProperty("replay", out var replay) &&
                   replay.ValueKind == JsonValueKind.True;
        }

        /// <summary>
        /// True when a <c>session/update</c> is a MESSAGE of the conversation — a chunk of either
        /// side's text, or a thought — which is what issue #185's replay count counts. Neither an
        /// announcement about the session (available commands, the current mode, a config option)
        /// nor a TOOL CALL, and the second exclusion was measured rather than reasoned (2026-09-12,
        /// kiro-cli 2.21.4 / KAS 0.63.3, <c>Console resume-unknown-id kiro v3</c>): for an id it does
        /// not hold, v3 opens a NEW session inside the load window and runs its
        /// <c>fetch_cloud_config</c> opening call there — the cloud settings sync every v3 session
        /// starts with, live, and then again replay-MARKED as that session's history — so a count
        /// that took tool calls read four frames of history from a conversation with none. A message
        /// chunk cannot be produced that way: a session opening yields tool calls and never a message,
        /// while a conversation with any history at all has at least its first user message.
        /// </summary>
        public static bool IsConversationContent(JsonElement update)
        {
            if (update.ValueKind != JsonValueKind.Object ||
                !update.TryGetProperty("sessionUpdate", out var kind) ||
                kind.ValueKind != JsonValueKind.String)
                return false;

            return kind.GetString() switch
            {
                "user_message_chunk" => true,
                "agent_message_chunk" => true,
                "agent_thought_chunk" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Extracts the final result a sub-agent is returning to its orchestrator from one of that
        /// sub-agent's own session updates: Kiro's wrap-up ("Summarizing") tool call carries it as
        /// <c>rawInput.taskResult</c>. Null for every other update — which is the point: a sub-agent
        /// session's stream (message chunks, tool churn) is otherwise dropped, since rendering it into
        /// the main transcript is what interleaved parallel crews into garbage.
        /// </summary>
        public static AgentEvent.SubagentResult? ExtractSubagentResult(string subagentSessionId, JsonElement update)
        {
            if (update.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(subagentSessionId))
                return null;

            if (TryGetObject(update, "rawInput", out var rawInput) &&
                rawInput.TryGetProperty("taskResult", out var result) &&
                result.ValueKind == JsonValueKind.String &&
                result.GetString() is { Length: > 0 } text)
            {
                // Roomier cap than tool rows (the result IS the sub-agent's deliverable), but still
                // bounded: it lands in the transcript and the persisted session log.
                if (text.Length > MaxSubagentResultChars)
                    text = text.Substring(0, MaxSubagentResultChars) + "\n…(truncated)";
                return new AgentEvent.SubagentResult(subagentSessionId, text);
            }

            return null;
        }

        private const int MaxSubagentResultChars = 20_000;

        // Prefix of the toolCallId Kiro v3 uses for the top-level "invoke a sub-agent" tool call
        // (e.g. "invoke_subagent_tooluse_ylMY…") — the row we keep, versus a sub-agent's own
        // internal tool churn which we drop (see IsSubagentInternalUpdate).
        private const string InvokeSubagentToolCallPrefix = "invoke_subagent_";

        /// <summary>
        /// True when a session/update belongs to the INTERNAL stream of a Kiro v3 sub-agent — its
        /// message chunks and its own tool churn — which should NOT render into the main transcript.
        /// v3 (unlike v2) doesn't multiplex sub-agents into separate ACP sessions; it runs them in the
        /// primary session and tags every sub-agent frame with <c>_meta.kiro.agentSubtaskId</c>. We drop
        /// those, EXCEPT the top-level <c>invoke_subagent_*</c> tool call/update (the "Sub-agent: …" row
        /// the user sees, whose completed <c>rawOutput</c> carries the deliverable). Without this the
        /// sub-agent's streamed summary duplicates the tool row AND concatenates into the main agent's
        /// message (the observed "…patternHere's the summary…" jam). v2's separate-session filter lives in
        /// <see cref="AcpClientTarget.OnSessionUpdate"/>; this is the v3 same-session equivalent.
        /// </summary>
        public static bool IsSubagentInternalUpdate(JsonElement update)
        {
            if (GetKiroSubtaskId(update) is null)
                return false;

            // Keep the top-level invoke_subagent row (it, too, is tagged with the subtask id).
            return !IsInvokeSubagentRow(update);
        }

        /// <summary>The <c>_meta.kiro.agentSubtaskId</c> tag v3 puts on every sub-agent frame; null when absent.</summary>
        private static string? GetKiroSubtaskId(JsonElement update)
        {
            if (update.ValueKind != JsonValueKind.Object ||
                !TryGetObject(update, "_meta", out var meta) ||
                !TryGetObject(meta, "kiro", out var kiro))
                return null;

            return GetString(kiro, "agentSubtaskId") is { Length: > 0 } id ? id : null;
        }

        /// <summary>
        /// True for a tool-call frame (the call, or any update to it) as opposed to prose, a plan, usage
        /// or any other <c>sessionUpdate</c> kind. Used to split a sub-agent's stream into the half that
        /// nests under its row and the half that is dropped.
        /// </summary>
        public static bool IsToolCallFrame(JsonElement update) =>
            update.ValueKind == JsonValueKind.Object &&
            GetString(update, "sessionUpdate") is "tool_call" or "tool_call_update";

        /// <summary>True for the top-level <c>invoke_subagent_*</c> call — the row a v3 sub-agent's work nests under.</summary>
        private static bool IsInvokeSubagentRow(JsonElement update) =>
            GetString(update, "toolCallId") is { } id &&
            id.StartsWith(InvokeSubagentToolCallPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Records which <c>invoke_subagent_*</c> tool call owns a v3 sub-agent's subtask id, so that
        /// sub-agent's own tool calls can be attributed back to the row the user sees.
        /// <para>Kiro v3 gives us a FLAT tag — every frame of a sub-agent carries the same
        /// <c>agentSubtaskId</c> and nothing says which call started it — but the invoking row is tagged
        /// with that id too, and it is the only frame whose <c>toolCallId</c> wears the
        /// <c>invoke_subagent_</c> prefix. So the correlation is derivable, and this is where it is
        /// derived; without it v3 could only DROP the internals (which is what it did before issue
        /// #125), where Claude could nest them.</para>
        /// <para>Kept per session by <see cref="AcpClientTarget"/> alongside the command/rawInput caches,
        /// for the same reason those are: the mapper is static, and this is a fact about one
        /// conversation. A child whose parent was never seen resolves to null and stays top-level —
        /// nesting must never be a way for a call to go missing.</para>
        /// </summary>
        public static void CacheSubagentParent(JsonElement update, IDictionary<string, string>? parents)
        {
            if (parents is null || !IsInvokeSubagentRow(update))
                return;
            if (GetKiroSubtaskId(update) is { } subtaskId && GetString(update, "toolCallId") is { Length: > 0 } id)
                parents[subtaskId] = id;
        }

        /// <summary>
        /// The <c>_meta.claudeCode</c> block, which the Claude Code adapter hangs its own facts off.
        /// Note the key is <c>claudeCode</c>; the sibling <c>_claude/*</c> keys (rate limit, prompt
        /// origin) are a different namespace on the same <c>_meta</c> and are read elsewhere.
        /// </summary>
        private static bool TryGetClaudeMeta(JsonElement update, out JsonElement claudeCode)
        {
            claudeCode = default;
            return update.ValueKind == JsonValueKind.Object &&
                   TryGetObject(update, "_meta", out var meta) &&
                   TryGetObject(meta, "claudeCode", out claudeCode);
        }

        /// <summary>
        /// Which sub-agent invocation this call was made BY, or null for the main agent's own work.
        /// Two backends, one answer: Claude names the parent call directly
        /// (<c>_meta.claudeCode.parentToolUseId</c>, present on both the opening frame and the
        /// completion — measured on 0.70.0), while Kiro v3 names a subtask that
        /// <see cref="CacheSubagentParent"/> has already resolved to a row.
        /// </summary>
        public static string? ResolveParentToolCallId(JsonElement update, IDictionary<string, string>? subagentParents)
        {
            if (TryGetClaudeMeta(update, out var claudeCode) &&
                GetString(claudeCode, "parentToolUseId") is { Length: > 0 } parent)
                return parent;

            // v3: the invoking row is tagged with its own subtask id — it is the parent, not a child.
            if (subagentParents is not null && !IsInvokeSubagentRow(update) &&
                GetKiroSubtaskId(update) is { } subtaskId &&
                subagentParents.TryGetValue(subtaskId, out var owner))
                return owner;

            return null;
        }

        /// <summary>
        /// True when this call is the one that STARTS a sub-agent — the row everything else nests
        /// under. Claude marks it <c>_meta.claudeCode.subagent: true</c>; Kiro v3 doesn't mark it at
        /// all, but gives it the <c>invoke_subagent_</c> toolCallId prefix, which serves.
        /// </summary>
        public static bool IsSubagentLaunchCall(JsonElement update)
        {
            if (TryGetClaudeMeta(update, out var claudeCode) &&
                claudeCode.TryGetProperty("subagent", out var flag) &&
                flag.ValueKind == JsonValueKind.True)
                return true;

            return IsInvokeSubagentRow(update);
        }

        /// <summary>
        /// True when a "completed" frame is really a sub-agent LAUNCH acknowledgement — the work has not
        /// happened yet, it has only been started. Claude's async <c>Task</c> settles the call
        /// immediately and then streams the sub-agent's child calls for minutes afterwards, so taking
        /// this at face value puts a green tick on a row whose work is still arriving underneath it.
        /// <para>Read structurally, from <c>_meta.claudeCode.toolResponse</c>
        /// (<c>{isAsync:true, status:"async_launched", agentId, …}</c>), never from the result prose —
        /// which the adapter itself labels internal metadata and asks not to be quoted. Note the
        /// <c>toolResponse</c> rides its OWN status-less frame immediately before the
        /// <c>status:"completed"</c> one, so the flag has to be remembered across the pair rather than
        /// read off the completion alone.</para>
        /// </summary>
        public static bool IsAsyncLaunchAcknowledgement(JsonElement update)
        {
            if (!TryGetClaudeMeta(update, out var claudeCode) ||
                !TryGetObject(claudeCode, "toolResponse", out var response))
                return false;

            return GetString(response, "status") == AsyncLaunchedStatus ||
                   (response.TryGetProperty("isAsync", out var isAsync) && isAsync.ValueKind == JsonValueKind.True);
        }

        // The adapter's own name for "this sub-agent was started, not finished".
        //
        // DO NOT remove this on the strength of grepping the adapter's dist for it. The string lives in
        // the VENDORED SDK (@anthropic-ai/claude-agent-sdk 0.3.232, sdk-tools.d.ts) as a typed union
        // member which the adapter forwards verbatim, so a search of the adapter's own bundle
        // structurally cannot find it and reports it as gone. It is not: a captured session
        // (agentInfo.version 0.70.0, 2026-08-22) carries it three times.
        private const string AsyncLaunchedStatus = "async_launched";

        /// <summary>
        /// Remembers that a sub-agent call runs in the BACKGROUND, so the "completed" frame that follows
        /// can be read as the launch it is. Kept per session by <see cref="AcpClientTarget"/> beside the
        /// command/rawInput caches, because the fact and the frame it has to be applied to arrive
        /// separately: the marker rides a status-less <c>toolResponse</c> frame, and the frame that
        /// claims completion carries nothing to distinguish it from a real one.
        /// <para>Two independent signals, either sufficient. <c>run_in_background</c> is an ordinary
        /// argument and lands on an enriching update while the call is still open; the
        /// <c>toolResponse.status</c> is the adapter's own account of what it did and lands at the end.
        /// Reading both is deliberate rather than belt-and-braces: the argument is what the agent asked
        /// for and the status is what happened, and either one alone is a single point of failure for a
        /// row that would otherwise go green over work that has not started.</para>
        /// </summary>
        public static void CacheBackgroundLaunch(JsonElement update, ISet<string>? launches)
        {
            if (launches is null || GetString(update, "toolCallId") is not { Length: > 0 } id)
                return;

            if (IsAsyncLaunchAcknowledgement(update) || HasBackgroundRequest(update))
                launches.Add(id);
        }

        /// <summary>
        /// Remembers that a tool call's generic row was RETIRED into the plan card, so its later frames
        /// cannot re-decide that (issue #190). Recorded on the opening <c>tool_call</c> frame only,
        /// because that is the frame the decision was made on: an update recording itself would agree
        /// with the reading that produced the bug rather than with the row that is on screen.
        /// <para>The failure it closes is a ledger one, not a rendering one. A plan tool call opened
        /// with empty <c>rawInput</c> yields an ordinary <c>ToolCallStarted</c>; once the arguments
        /// stream in, the update matches the plan predicate and its completion is suppressed. Nothing
        /// then closes the pair, and the host's "next step" release — which fires when the last open
        /// call closes — is stranded until the turn ends. Kept per session by
        /// <see cref="AcpClientTarget"/> beside the other caches, for the same reason: the two frames
        /// this spans are separate messages.</para>
        /// </summary>
        public static void CachePlanToolCall(JsonElement update, ISet<string>? planToolCalls)
        {
            if (planToolCalls is null ||
                GetString(update, "sessionUpdate") != "tool_call" ||
                GetString(update, "toolCallId") is not { Length: > 0 } id)
                return;

            if (IsPlanToolCall(update))
                planToolCalls.Add(id);
        }

        private static bool HasBackgroundRequest(JsonElement update) =>
            TryGetObject(update, "rawInput", out var rawInput) &&
            rawInput.TryGetProperty("run_in_background", out var background) &&
            background.ValueKind == JsonValueKind.True;

        /// <summary>
        /// Claude's own human-readable label for a call whose ACP title is the raw thing being run
        /// (<c>_meta.claudeCode.title</c> — "List tracked non-C# files" against a title of
        /// <c>git ls-files | grep …</c>). Null when the backend sent none, which is most calls.
        /// </summary>
        public static string? ExtractClaudeTitle(JsonElement update) =>
            TryGetClaudeMeta(update, out var claudeCode) && GetString(claudeCode, "title") is { Length: > 0 } title
                ? title
                : null;

        /// <summary>
        /// Yields an <see cref="AgentEvent.EditProposed"/> for each ACP <c>diff</c> content entry on a
        /// tool call (<c>{ "type":"diff", "path", "oldText", "newText" }</c>). <c>oldText</c> is null for
        /// a new file, surfaced here as empty. This is the spec-standard, agent-agnostic edit surface.
        /// The <paramref name="contentCache"/> repairs a degenerate diff and is then refreshed with the
        /// new content so a later edit to the same file has an accurate "before".
        /// </summary>
        private static IEnumerable<AgentEvent> ExtractDiffEdits(
            JsonElement update,
            IDictionary<string, string>? contentCache,
            IDictionary<string, FileWriteResult>? writeCapture,
            string? agentRoot,
            IDictionary<string, string>? subagentParents)
        {
            if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                yield break;

            var toolCallId = GetString(update, "toolCallId");
            // Reported edit locations (path + 1-based line), consumed positionally per path. The Claude
            // adapter's finalized edit update aligns one location per diff hunk (structuredPatch's
            // newStart); the provisional first send carries a location without a line. The line lets
            // the host sanity-check where a hunk-only diff is spliced back into the full file.
            var locations = ParseLocationLines(update, agentRoot);
            // The agent's stated intent + the backend's edit-operation name, normalized off the tool
            // call's rawInput so the host can show them as detail without decoding provider shapes. The
            // diff itself stays for the native viewer.
            var (intent, operation) = ExtractEditMeta(update);
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || GetString(item, "type") != "diff")
                    continue;

                var path = GetString(item, "path");
                if (string.IsNullOrEmpty(path))
                    continue;
                // Kiro v3 sends the diff path as a percent-encoded file:// URI while locations/rawInput
                // use plain — often workspace-relative — paths; normalize so EditProposed, the
                // reported-line lookup, the write capture and the content cache all key on the same
                // absolute local path, whichever surface the frame happened to use.
                path = NormalizeLocalPath(path!, agentRoot);

                var oldText = GetString(item, "oldText") ?? string.Empty;
                var newText = GetString(item, "newText") ?? string.Empty;

                var (before, after) = ResolveEditText(path!, oldText, newText, contentCache, writeCapture);

                yield return new AgentEvent.EditProposed(
                    path!, before, after, toolCallId,
                    TakeLineFor(locations, path!), intent, operation,
                    ResolveParentToolCallId(update, subagentParents));

                // Keep the cache current so a subsequent edit to this file sees this result as its before.
                if (contentCache != null)
                    contentCache[path!] = after;
            }
        }

        /// <summary>
        /// Runs the spec-standard <see cref="ExtractDiffEdits"/> first; only if it yields nothing does it
        /// fall back to Kiro v3's str_replace edit shape (see <see cref="ExtractKiroFragmentEdit"/>). So a
        /// conformant <c>type:"diff"</c> surface always wins, and the Kiro-specific fallback fires solely
        /// for the frames that surface no usable diff.
        /// </summary>
        private static IEnumerable<AgentEvent> ExtractEdits(
            JsonElement update,
            IDictionary<string, string>? contentCache,
            IDictionary<string, FileWriteResult>? writeCapture,
            string? agentRoot,
            IDictionary<string, string>? subagentParents)
        {
            var any = false;
            foreach (var edit in ExtractDiffEdits(update, contentCache, writeCapture, agentRoot, subagentParents))
            {
                any = true;
                yield return edit;
            }
            if (any)
                yield break;

            foreach (var edit in ExtractKiroFragmentEdit(update, contentCache, writeCapture, agentRoot, subagentParents))
                yield return edit;
        }

        /// <summary>
        /// Kiro v3's <c>str_replace</c> ("Replace in File") edit, which <see cref="ExtractDiffEdits"/>
        /// misses: its <c>content</c> diff entry arrives blank (empty path + oldText/newText), so the real
        /// before/after live in <c>_meta.kiro.preview</c> (<c>file</c>/<c>originalContent</c>/
        /// <c>modifiedContent</c>, populated on the <c>tool_call</c> start frame) or, when that's blank
        /// (the pending update), in <c>rawInput</c> (<c>path</c>/<c>oldStr</c>/<c>newStr</c>). Yields one
        /// <see cref="AgentEvent.EditProposed"/> keyed on <c>toolCallId</c> so start + update dedupe onto a
        /// single edit card with a native diff — matching v2 <c>strReplace</c>.
        /// <para>
        /// <b>v3's whole-file <c>Write File</c> reaches here too, and the comment that used to say
        /// otherwise was wrong.</b> Captured 2026-09-03, its opening frame carries NO <c>content</c>
        /// array at all — the path and text are in <c>rawInput</c>, and the preview holds <c>file</c>
        /// plus <c>modifiedContent</c> (and <c>originalContent</c> only when the file already existed).
        /// So an ordinary v3 write becomes an edit CARD, off this method, on the opening frame.
        /// </para>
        /// <para>
        /// The consequence worth knowing: writing EMPTY content leaves the preview with a <c>file</c>
        /// and nothing else, the guard below rejects it, and the call falls through to a generic tool
        /// row titled with v3's bare "Write File" — which is the shape issue #178 reported, and why the
        /// fix for it lives on the tool row rather than here.
        /// </para>
        /// </summary>
        private static IEnumerable<AgentEvent> ExtractKiroFragmentEdit(
            JsonElement update,
            IDictionary<string, string>? contentCache,
            IDictionary<string, FileWriteResult>? writeCapture,
            string? agentRoot,
            IDictionary<string, string>? subagentParents)
        {
            string? path = null, oldText = null, newText = null;

            // Preferred: the display preview Kiro attaches to the start frame.
            if (update.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("kiro", out var kiro) && kiro.ValueKind == JsonValueKind.Object &&
                TryGetObject(kiro, "preview", out var preview))
            {
                var file = GetString(preview, "file");
                var original = GetString(preview, "originalContent");
                var modified = GetString(preview, "modifiedContent");
                if (!string.IsNullOrEmpty(file) && (!string.IsNullOrEmpty(original) || !string.IsNullOrEmpty(modified)))
                    (path, oldText, newText) = (file, original ?? string.Empty, modified ?? string.Empty);
            }

            // Fallback: the str_replace rawInput (present on the pending update, where preview is blank).
            if (path is null && TryGetObject(update, "rawInput", out var rawInput) &&
                (rawInput.TryGetProperty("oldStr", out _) || rawInput.TryGetProperty("newStr", out _)))
            {
                var file = GetString(rawInput, "path");
                if (!string.IsNullOrEmpty(file))
                    (path, oldText, newText) = (
                        file, GetString(rawInput, "oldStr") ?? string.Empty, GetString(rawInput, "newStr") ?? string.Empty);
            }

            if (path is null)
                yield break;

            path = NormalizeLocalPath(path, agentRoot);
            var (intent, operation) = ExtractEditMeta(update);
            var locations = ParseLocationLines(update, agentRoot);
            var (before, after) = ResolveEditText(path, oldText!, newText!, contentCache, writeCapture);

            yield return new AgentEvent.EditProposed(
                path, before, after, GetString(update, "toolCallId"),
                TakeLineFor(locations, path), intent, operation,
                ResolveParentToolCallId(update, subagentParents));

            if (contentCache != null)
                contentCache[path] = after;
        }

        /// <summary>
        /// Reads the update's <c>locations</c> array into (path, line) pairs, keeping only entries with a
        /// numeric line. Order is preserved so hunks that repeat a path are matched positionally.
        /// </summary>
        private static List<(string Path, int Line)> ParseLocationLines(JsonElement update, string? agentRoot)
        {
            var result = new List<(string, int)>();
            if (!update.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var loc in locations.EnumerateArray())
            {
                var path = GetString(loc, "path");
                if (string.IsNullOrEmpty(path))
                    continue;
                if (loc.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number
                    && line.TryGetInt32(out var n))
                    result.Add((NormalizeLocalPath(path!, agentRoot), n));
            }

            return result;
        }

        /// <summary>Removes and returns the first reported line for <paramref name="path"/>, or null.</summary>
        private static int? TakeLineFor(List<(string Path, int Line)> locations, string path)
        {
            for (var i = 0; i < locations.Count; i++)
            {
                if (string.Equals(locations[i].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    var line = locations[i].Line;
                    locations.RemoveAt(i);
                    return line;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the best "before" text for a diff. A genuine diff (<c>oldText != newText</c>, e.g. a
        /// surgical strReplace or a real new file) is trusted as-is. A degenerate one (<c>oldText ==
        /// newText</c>, which Kiro's whole-file <c>create</c> emits) is repaired from previously-seen
        /// content for the same path; failing that it falls back to an empty before, so the file shows
        /// as newly written (all-added) rather than producing a blank, no-change diff.
        /// </summary>
        private static string ResolveOldText(string path, string oldText, string newText, IDictionary<string, string>? cache)
        {
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                return oldText;

            if (cache != null && cache.TryGetValue(path, out var cached) && !string.Equals(cached, newText, StringComparison.Ordinal))
                return cached;

            return string.Empty;
        }

        /// <summary>
        /// Resolves the before/after an edit should carry. When the host performed this write itself (ACP
        /// <c>fs/write_text_file</c> — Kiro's v3 engine routes every edit that way), its captured pair is
        /// the authoritative record of what changed and replaces BOTH sides of the agent's report;
        /// otherwise the reported text stands, repaired by <see cref="ResolveOldText"/> as before.
        /// </summary>
        /// <remarks>
        /// Both sides swap together, never one: the agent's <c>newText</c> may be a surgical hunk while the
        /// capture is whole-file, and pairing a hunk against a full-file before would render as a
        /// delete-the-entire-file diff. Swapping also removes the need for the host's hunk-widening
        /// reconstruction on this route (locate the hunk in the current file, splice the old text back) —
        /// which can legitimately fail on repeated text or an edit that later moved, falling back to a
        /// context-free hunk. That reconstruction is exactly what the client-fs route no longer needs.
        /// <para>
        /// The capture is CONSUMED. Each write is followed by one enrichable frame (the completed
        /// <c>tool_call_update</c>; a str_replace's start frame is emitted before the write and so can't
        /// consume it), and popping stops a stale capture attaching to an unrelated later edit of the same
        /// file. Any miss — different path form, no capture, an agent that writes files itself — falls
        /// through to the agent's reported text, i.e. exactly the previous behaviour.
        /// </para>
        /// </remarks>
        private static (string oldText, string newText) ResolveEditText(
            string path, string oldText, string newText,
            IDictionary<string, string>? contentCache,
            IDictionary<string, FileWriteResult>? writeCapture)
        {
            if (writeCapture != null && writeCapture.TryGetValue(path, out var captured))
            {
                writeCapture.Remove(path);
                LogCaptureOutcome(path, oldText, newText, captured);
                return (captured.OldText, captured.NewText);
            }

            return (ResolveOldText(path, oldText, newText, contentCache), newText);
        }

        /// <summary>
        /// Records, per client-fs edit, whether the host's captured before/after actually DIFFERED from what
        /// the agent reported — the evidence for whether the swap in <see cref="ResolveEditText"/> earns its
        /// keep at all. If these only ever say <c>same</c>, backends are always reporting correctly and both
        /// the swap and <c>IEditApplier</c>'s return value can be removed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately logs the AGREEING case too, not just divergence. Were divergence alone logged,
        /// silence would be ambiguous: "every backend always reports correctly" and "the swap never fired"
        /// (a path-key mismatch, say) look identical — and the second would read as evidence for deleting the
        /// feature when it is really evidence the feature is broken. Pair these <c>applied</c> lines with the
        /// <c>stored</c> line <see cref="AcpClientTarget"/> writes per write: a <c>stored</c> with no
        /// <c>applied</c> means the capture went unused, i.e. either the keys didn't meet or no post-write
        /// frame carried an edit at all (the case the synthesized-card follow-up exists for).
        /// </para>
        /// <para>
        /// The comparison normalizes line endings, and must: <c>VsEditApplier</c> runs
        /// <c>LineEndings.Match</c>, so our capture carries the file's real endings while agents report
        /// <c>"\n"</c> — a raw compare would call every single write divergent and the log would tell you
        /// nothing. Lengths are reported raw, so a same-verdict with differing lengths is exactly that case.
        /// </para>
        /// <para>
        /// One short line per client-fs edit, so it stays on permanently rather than behind an env flag
        /// nobody remembers to set before the run that mattered. Engine stderr is drained into engine.log.
        /// </para>
        /// </remarks>
        private static void LogCaptureOutcome(string path, string reportedOld, string reportedNew, FileWriteResult captured)
        {
            var oldSame = SameIgnoringLineEndings(reportedOld, captured.OldText);
            var newSame = SameIgnoringLineEndings(reportedNew, captured.NewText);
            var which = oldSame ? (newSame ? string.Empty : " (new)") : (newSame ? " (old)" : " (old+new)");

            Console.Error.WriteLine(
                $"[edit-capture] applied={(oldSame && newSame ? "same" : "DIVERGED")}{which} path={path} " +
                $"agentLen={reportedOld?.Length ?? 0}/{reportedNew?.Length ?? 0} " +
                $"oursLen={captured.OldText.Length}/{captured.NewText.Length}");
        }

        /// <summary>Ordinal comparison with CRLF folded to LF on both sides. See <see cref="LogCaptureOutcome"/>.</summary>
        internal static bool SameIgnoringLineEndings(string? a, string? b) =>
            string.Equals(
                (a ?? string.Empty).Replace("\r\n", "\n"),
                (b ?? string.Empty).Replace("\r\n", "\n"),
                StringComparison.Ordinal);

        /// <summary>
        /// Records file contents from a completed <c>read</c> tool call (its <c>rawOutput.items[].Text</c>,
        /// aligned 1:1 with <c>rawInput.operations[].path</c>) into <paramref name="cache"/>, so a later
        /// degenerate edit can recover a real "before". Best-effort and defensive: skips directory
        /// listings and bails on any shape mismatch rather than risk mis-keying content to a path.
        /// Keys are normalized exactly as the edit path is, or a read reported relative and an edit
        /// reported absolute name the same file under two keys and the lookup misses.
        /// </summary>
        private static void CacheReadContent(JsonElement update, IDictionary<string, string>? cache, string? agentRoot)
        {
            if (cache is null || GetString(update, "kind") != "read")
                return;
            if (!TryGetObject(update, "rawInput", out var rawInput) ||
                !TryGetArray(rawInput, "operations", out var ops) ||
                !TryGetObject(update, "rawOutput", out var rawOutput) ||
                !TryGetArray(rawOutput, "items", out var items))
                return;

            var opList = ops.EnumerateArray().ToList();
            var itemList = items.EnumerateArray().ToList();
            if (opList.Count == 0 || opList.Count != itemList.Count)
                return; // a shape we don't recognise — skip rather than mis-key content

            for (var i = 0; i < opList.Count; i++)
            {
                if (GetString(opList[i], "mode") == "Directory")
                    continue; // a directory listing, not file content

                var path = GetString(opList[i], "path");
                var text = GetString(itemList[i], "Text");
                if (!string.IsNullOrEmpty(path) && text != null)
                    cache[NormalizeLocalPath(path!, agentRoot)] = text;
            }
        }

        /// <summary>
        /// Returns the update's <c>rawInput</c> as JSON text when it's an object with at least one
        /// argument property; null for absent/empty input (the Claude Code adapter sends <c>{}</c> on
        /// the initial tool_call and the real arguments on a later update). Kiro v3's <c>_meta</c>
        /// validation blob doesn't count as an argument and is stripped from the returned JSON.
        /// </summary>
        private static string? GetRawInputObjectJson(JsonElement update) =>
            TryGetObject(update, "rawInput", out var rawInput) && HasAnyDisplayProperty(rawInput)
                ? StripMeta(rawInput)
                : null;

        private static bool HasAnyProperty(JsonElement obj)
        {
            foreach (var _ in obj.EnumerateObject())
                return true;
            return false;
        }

        // True when the object has at least one property besides "_meta" (Kiro v3 embeds a validation
        // blob there — protocol noise, not tool arguments).
        private static bool HasAnyDisplayProperty(JsonElement obj)
        {
            foreach (var property in obj.EnumerateObject())
                if (!property.NameEquals("_meta"))
                    return true;
            return false;
        }

        // Serializes a rawInput element for display, dropping a top-level "_meta" property (see
        // HasAnyDisplayProperty). Non-objects and objects without _meta pass through verbatim.
        private static string StripMeta(JsonElement raw)
        {
            if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("_meta", out _))
                return raw.GetRawText();

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in raw.EnumerateObject())
                    if (!property.NameEquals("_meta"))
                        property.WriteTo(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private const int MaxResultTextChars = 2000;

        /// <summary>
        /// The bound on a structured payload that is exempt from <see cref="MaxResultTextChars"/>. Our
        /// run_tests payload is bounded at source (50 failures, each with a capped message and stack
        /// trace), so ~105 KB is its worst case and anything past this cap did not come from the tool
        /// we think it did. Over it, the text is clamped like any other output: the payload is lost to
        /// the parse, which is the graceful degradation (the card still comes from the side-channel
        /// copy), where an unbounded exemption would let arbitrary agent text into the persisted log.
        /// </summary>
        private const int MaxStructuredResultChars = 128 * 1024;

        /// <summary>
        /// Pulls a human-readable result out of a completed/failed tool update, for the tool row's
        /// collapsible detail. Precedence: a string <c>rawOutput</c> (Claude / spec agents) → the
        /// <c>stdout</c> of Kiro's structured object <c>rawOutput</c> → the update's <c>content[]</c> text
        /// entries (generic ACP). Truncated to keep transcript rows (and the persisted session log)
        /// chat-sized. stderr is surfaced separately by <see cref="ExtractErrorText"/>.
        /// </summary>
        private static string? ExtractResultText(JsonElement update)
        {
            string? text = null;
            if (update.TryGetProperty("rawOutput", out var rawOutput))
            {
                if (rawOutput.ValueKind == JsonValueKind.String)
                    text = rawOutput.GetString();
                else
                    // "message" is what Kiro v3's FILE tools report instead of stdout — captured
                    // 2026-09-03, a completed write answering {"message":"Created the …\\temp.txt
                    // file."} and nothing else. Reading stdout alone left every v3 write with an empty
                    // expander, which is half of what issue #189 was looking at. (Its FAILED writes
                    // answer a bare STRING rawOutput — "ENOENT: no such file or directory, mkdir …" —
                    // which the branch above already picks up.)
                    text = FirstKiroField(update, "stdout") ?? FirstKiroField(update, "message");
            }

            // Fall back to the spec-standard content[] (a generic ACP agent's result surface, and the
            // recovery when a structured rawOutput carried no stdout).
            text ??= ExtractContentText(update);
            return ClampResult(text);
        }

        /// <summary>
        /// The result-text clamp, with our own structured payloads exempt (issue #83). A clamp is a
        /// DISPLAY concern; the run_tests payload the agent echoes back is DATA the host parses into a
        /// card, and cutting it at 2000 chars left the JSON string open and appended a raw newline, so
        /// every test run threw a <c>JsonReaderException</c> on a routine path. The cut is ours, not a
        /// backend transport cap — nothing downstream can undo it, so the fix belongs here, before the
        /// text is anything but text.
        /// <para>
        /// The test is <see cref="StructuredToolResult.IsStructured"/> — "is this ours" — and not a list
        /// of the kinds this method happens to know about. Naming kinds here makes every new payload a
        /// thing someone has to remember to add, and forgetting reproduces issue #83 exactly: cut
        /// mid-string, thrown where the card is parsed, and only on payloads large enough to notice in
        /// the field.
        /// </para>
        /// </summary>
        private static string? ClampResult(string? text)
        {
            var trimmed = text?.Trim();
            return StructuredToolResult.IsStructured(trimmed) && trimmed!.Length <= MaxStructuredResultChars
                ? trimmed
                : Clamp(trimmed);
        }

        /// <summary>
        /// A command's error stream (Kiro's structured <c>stderr</c>), surfaced apart from the result
        /// because stderr is a separate stream with no meaningful position relative to stdout. Null when
        /// there's no (non-empty) stderr, or the shape isn't Kiro's structured output at all.
        /// <para>
        /// Read through <see cref="KiroResultPayloads"/> so BOTH engine nestings answer. Bound to the
        /// default engine's <c>rawOutput.items[].Json</c> alone, this returned null on every v3 frame —
        /// so a v3 call that failed had no account of itself to show, and the row settled red with an
        /// empty expander and nothing to expand (issue #189). <see cref="ExitedNonZero"/> had the same
        /// blindness with a worse symptom: no <c>exit_status</c> ever parsed, so a v3 command exiting
        /// non-zero reported SUCCESS.
        /// </para>
        /// </summary>
        private static string? ExtractErrorText(JsonElement update) =>
            Clamp(FirstKiroField(update, "stderr"));

        /// <summary>
        /// True when the tool reported a non-zero process exit code in Kiro's structured rawOutput
        /// (<c>exit_status</c> like <c>"exit code: 1"</c>). False when there's no parseable exit code, so
        /// callers fall back to the ACP <c>status</c> for success.
        /// </summary>
        private static bool ExitedNonZero(JsonElement update)
        {
            if (FirstKiroField(update, "exit_status") is { } raw)
            {
                var tail = raw.Substring(raw.LastIndexOf(':') + 1).Trim();
                if (int.TryParse(tail, out var code))
                    return code != 0;
            }

            return false;
        }

        /// <summary>
        /// Kiro reports shell results as a structured rawOutput object:
        /// <c>{ items:[{ Json:{ exit_status, stdout, stderr } }] }</c>. Yields the first item's
        /// <c>Json</c> object; false for any other shape (a string rawOutput, or a different agent).
        /// </summary>
        private static IEnumerable<JsonElement> KiroResultPayloads(JsonElement update)
        {
            if (!TryGetObject(update, "rawOutput", out var rawOutput))
                yield break;

            if (TryGetArray(rawOutput, "items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && TryGetObject(item, "Json", out var json))
                        yield return json;
            }

            // v3: the payload IS rawOutput, exactly as it is for the task list (issue #119). Yielded
            // last so a wrapped one still wins where both exist.
            yield return rawOutput;
        }

        /// <summary>
        /// The first non-empty <paramref name="field"/> across <see cref="KiroResultPayloads"/>, or null.
        /// </summary>
        private static string? FirstKiroField(JsonElement update, string field)
        {
            foreach (var json in KiroResultPayloads(update))
                if (GetString(json, field) is { Length: > 0 } value)
                    return value;

            return null;
        }

        private const int MaxOutputChunkChars = 256 * 1024;

        /// <summary>
        /// The live-chunk clamp — deliberately NOT <see cref="Clamp"/>: chunks feed the tool row
        /// (tail-capped there) and the terminal mirror (bounded stream), both of which want fidelity,
        /// so the cap is pathology-only and the text is NOT trimmed — chunk boundaries fall mid-stream,
        /// so leading/trailing whitespace is real output (the 2000-char result clamp cut a directory
        /// listing off in the pane, and its Trim ate Kiro's chunk-boundary newlines; measured in Visual Studio, 2026-07-17).
        /// Whitespace-ONLY chunks are still suppressed (bare-newline keep-alives are not output).
        /// </summary>
        private static string? ClampChunk(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return text!.Length <= MaxOutputChunkChars
                ? text
                : text.Substring(0, MaxOutputChunkChars) + "\n[… output truncated …]\n";
        }

        // Trims and truncates result/error text so a row (and the persisted log) stays chat-sized.
        // Result text goes through ClampResult, which exempts a structured payload from the cut.
        private static string? Clamp(string? text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            return text!.Length <= MaxResultTextChars ? text : text.Substring(0, MaxResultTextChars) + "\n…";
        }

        /// <summary>Joins the text of <c>content[]</c> entries (<c>{ type:"content", content:{ type:"text", text } }</c>).</summary>
        private static string? ExtractContentText(JsonElement update)
        {
            if (!TryGetArray(update, "content", out var content))
                return null;

            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    GetString(item, "type") == "content" &&
                    TryGetObject(item, "content", out var inner) &&
                    GetString(inner, "type") == "text" &&
                    GetString(inner, "text") is { Length: > 0 } text)
                    parts.Add(text);
            }

            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }

        private static bool TryGetObject(JsonElement element, string property, out JsonElement value) =>
            TryGetOfKind(element, property, JsonValueKind.Object, out value);

        private static bool TryGetArray(JsonElement element, string property, out JsonElement value) =>
            TryGetOfKind(element, property, JsonValueKind.Array, out value);

        private static bool TryGetOfKind(JsonElement element, string property, JsonValueKind kind, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(property, out value) &&
                value.ValueKind == kind)
                return true;

            value = default;
            return false;
        }

        /// <summary>
        /// Recognises Kiro's task-list (plan) tool by its input shape (carries <c>tasks</c>,
        /// <c>completed_task_ids</c>, or <c>task_list_description</c>), so its create/complete calls are
        /// rendered as a single live plan card rather than a generic tool row each.
        /// </summary>
        private static bool IsPlanToolCall(JsonElement update)
        {
            if (!TryGetObject(update, "rawInput", out var rawInput))
                return false;

            // `tasks` is an array on the default engine and an index-keyed OBJECT on v3
            // ({"0":{…},"1":{…}}), so its presence is what identifies the tool, not its shape.
            return rawInput.TryGetProperty("tasks", out _)
                || rawInput.TryGetProperty("completed_task_ids", out _)
                || rawInput.TryGetProperty("task_list_description", out _);
        }

        /// <summary>
        /// The task-list payloads carried by one tool update, innermost object first — the objects that
        /// may hold <c>{ tasks:[{ id, task_description, completed }], description }</c>.
        /// <para>Two engine shapes, both live (issue #119): the default engine wraps the payload as
        /// <c>rawOutput.items[].Json</c>, while <b>v3</b> puts the same fields directly on
        /// <c>rawOutput</c>. Yielding candidates instead of branching on the engine keeps every reader
        /// (the plan itself, and the cleared-list "all done" signal) agreeing about where to look.</para>
        /// </summary>
        private static IEnumerable<JsonElement> PlanPayloads(JsonElement update)
        {
            if (!TryGetObject(update, "rawOutput", out var rawOutput))
                yield break;

            if (TryGetArray(rawOutput, "items", out var outItems))
            {
                foreach (var outItem in outItems.EnumerateArray())
                {
                    if (TryGetObject(outItem, "Json", out var json))
                        yield return json;
                }
            }

            // v3: the payload IS rawOutput. Yielded last so a wrapped one still wins where both exist.
            if (rawOutput.TryGetProperty("tasks", out _))
                yield return rawOutput;
        }

        /// <summary>
        /// Pulls the full plan out of a task-list tool update — see <see cref="PlanPayloads"/> for where
        /// each engine puts <c>{ tasks:[{ id, task_description, completed }], description }</c>. Returns
        /// false for tool updates that aren't plans.
        /// </summary>
        private static bool TryExtractPlan(JsonElement update, out string? title, out IReadOnlyList<PlanItem> items)
        {
            title = null;
            items = Array.Empty<PlanItem>();

            foreach (var json in PlanPayloads(update))
            {
                if (!TryGetArray(json, "tasks", out var tasks))
                    continue;

                var list = new List<PlanItem>();
                foreach (var task in tasks.EnumerateArray())
                {
                    if (task.ValueKind != JsonValueKind.Object)
                        continue;
                    var description = GetString(task, "task_description") ?? string.Empty;
                    if (description.Length == 0)
                        continue;
                    var completed = task.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True;
                    var status = completed ? PlanItemStatus.Completed : PlanItemStatus.Pending;
                    list.Add(new PlanItem(GetString(task, "id") ?? string.Empty, description, status));
                }

                if (list.Count > 0)
                {
                    title = GetString(json, "description");
                    items = list;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when a plan tool update reports the whole plan finished: a "complete" command
        /// (<c>rawInput.completed_task_ids</c>) whose <c>rawOutput</c> carries an <em>empty</em> tasks
        /// list — Kiro disposes the task list when the last item completes, so the cleared list is the
        /// "all done" signal (a mid-plan complete echoes the full remaining list instead).
        /// </summary>
        private static bool IsPlanCompletion(JsonElement update)
        {
            if (!TryGetObject(update, "rawInput", out var rawInput) ||
                !rawInput.TryGetProperty("completed_task_ids", out _))
                return false;

            foreach (var json in PlanPayloads(update))
            {
                if (TryGetArray(json, "tasks", out var tasks) && tasks.GetArrayLength() == 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Extracts the spec-standard ACP <c>plan</c> entries (<c>{ content, priority, status }</c>),
        /// mapping status to <see cref="PlanItemStatus"/>. Entries have no id, so the index is used.
        /// </summary>
        private static bool TryExtractAcpPlan(JsonElement update, out IReadOnlyList<PlanItem> items)
        {
            items = Array.Empty<PlanItem>();
            if (!TryGetArray(update, "entries", out var entries))
                return false;

            var list = new List<PlanItem>();
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                var content = GetString(entry, "content") ?? string.Empty;
                if (content.Length == 0)
                    continue;
                list.Add(new PlanItem(index++.ToString(), content, ParsePlanStatus(GetString(entry, "status"))));
            }

            items = list;
            return list.Count > 0;
        }

        private static PlanItemStatus ParsePlanStatus(string? status) => status switch
        {
            "completed" => PlanItemStatus.Completed,
            "in_progress" => PlanItemStatus.InProgress,
            _ => PlanItemStatus.Pending,
        };

        /// <summary>Maps an ACP permission request into the host-facing request type.</summary>
        /// <summary>
        /// Whether a permission request's session is a sub-agent's rather than the primary one. Kiro
        /// v2 multiplexes each sub-agent as its own ACP session on the same connection, so a request
        /// on a different session id IS a sub-agent's - the only attribution v2 gives. An unknown
        /// primary (frames can precede session/new returning) means nothing can be said, and the
        /// answer is false rather than a guess.
        /// </summary>
        public static bool IsSubagentSessionRequest(string? sessionId, string? primarySessionId) =>
            !string.IsNullOrEmpty(primarySessionId) && !string.IsNullOrEmpty(sessionId) &&
            !string.Equals(sessionId, primarySessionId, StringComparison.Ordinal);

        public static PermissionRequest ToPermissionRequest(
            RequestPermissionParams p,
            IReadOnlyDictionary<string, string>? commandCache = null,
            IReadOnlyDictionary<string, string>? rawInputCache = null,
            string? agentRoot = null,
            bool fromSubagentSession = false)
        {
            var toolCallId = GetString(p.ToolCall, "toolCallId") ?? string.Empty;
            var title = GetString(p.ToolCall, "title") ?? "Permission required";
            // Kiro's request_permission tool call carries no `kind`; fall back to deriving it from the
            // _meta.trustOptions scope hints so the policy's edit/command rules and remembered "always"
            // choices key on the action type rather than the (per-file, unique) title.
            var kind = GetString(p.ToolCall, "kind") ?? DeriveKind(p.Meta);

            // Our permission model presents ONE option per kind. A backend can send a redundant same-kind
            // extra — Kiro adds a second allow_always, "Allow all for this session" (issue #43) — which we
            // drop: our "Allow always" already covers a session-wide grant (widen the editable glob to
            // `*`), so a duplicate button is just confusing. Empty-kind options are never deduped.
            var seenKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new List<PermissionOption>();
            foreach (var o in p.Options)
            {
                if (!string.IsNullOrEmpty(o.Kind) && !seenKinds.Add(o.Kind!))
                    continue;
                options.Add(new PermissionOption(o.OptionId, o.Name, MapKind(o.Kind)));
            }

            // Resolve the *clean* command string the banner shows and the policy keys on. Priority:
            //   1. the permission frame's own rawInput.command — reliable + present regardless of `kind`
            //      (Claude labels a PowerShell run kind "other" yet still carries the command there);
            //   2. the FULL command cached from the preceding tool_call frame — Kiro v3's
            //      request_permission carries no rawInput at all (its command rides _meta.kiro.command,
            //      which we do not read) and only a backend-truncated command title, so this is the only
            //      way to surface the whole script in the banner. v2 DOES carry rawInput.command on the
            //      permission frame and answers at source 1; the difference is per-ENGINE, not per-backend
            //      (both measured on the wire 2026-09-09);
            //   3. the command parsed from a "Running: …" title (last resort; possibly truncated).
            // A file op (edit/read) is path-scoped, never command-scoped: Kiro's fs tools put an operation
            // discriminator in rawInput.command ("strReplace"/"create"/…) that is NOT a shell command, so
            // resolving one here routed edits onto the *command* allow-list — an "always" on a single edit
            // then silently blanket-approved every string-replace to any file. Leave Command null so Path
            // governs; a real command keeps its execute/other kind and resolves below.

            // The SHELL FACT: whether the backend itself says this frame is a shell command. Established
            // from the frame and its meta only - the kind (Kiro v2's trustOptions and v3's
            // consent.capability both derive "execute", Claude's Bash declares it) or a real command in
            // rawInput / the tool_call cache (Claude's PowerShell tool declares "other" and carries the
            // command there). NEVER from the title, which is the one string the model writes.
            var commandFact = IsFileOpKind(kind)
                ? null
                : GetCommand(p.ToolCall) ?? LookupCached(commandCache, toolCallId);
            var isShellCommand = IsCommandKind(kind) || commandFact is not null;

            // The namespaced MCP tool name (if any), so the shell can resolve our IDE tools to their
            // authored risk — for our own tools the name is the only reliable signal (Kiro sends no kind).
            // Resolved BEFORE the command because it vetoes source 3 (see below).
            //
            // A SHELL COMMAND NEVER TAKES ITS NAME FROM THE TITLE (pre-release security review, September 2026). Kiro titles a
            // shell run "Running: <whatever the model chose>", so a model steered into running
            // `@code-wicket/get_diagnostics` - a batch file a cloned repository shipped, or one the
            // agent wrote itself under AcceptEdits - produced a frame whose title lifted out as OUR
            // tool's name. The shell then resolved it to get_diagnostics's authored ReadOnly tier and
            // AcceptReads approved the command with no banner, the audit row calling it a read-only
            // IDE query. The name is refused only where the frame carries the shell fact above, so a
            // real MCP call on either backend (Kiro v3 says capability:"mcp"; Claude's rawInput is the
            // tool's own arguments, none of them "command") keeps resolving by name exactly as before -
            // distrusting the title everywhere would send every IDE tool top-tier on Claude. A structured toolName is still honoured
            // here, being the backend's claim and not the model's; the shell refuses it the same tier
            // lift on its own side (PolicyPermissionHandler.TrustedToolName), so a frame carrying both
            // a structured name and a command still resolves as the command it is.
            var toolName = ExtractToolName(p.ToolCall, title, fromTitle: !isShellCommand);

            // An MCP TOOL CALL IS NOT A SHELL COMMAND, whatever its title looks like (issue #129). Kiro's
            // default engine titles one "Running: @code-wicket/build_solution" — the same
            // convention it uses for real shell commands — so source 3 resolved the tool NAME as the
            // command, and the banner then offered an editable *command* glob whose "save permanently"
            // wrote `@code-wicket/build_solution` into the command allow-list. Kiro v3 titles the
            // same call bare, so it took no command at all: the persisted rule matched nothing and went
            // quietly inert (as it does on Claude, whose `mcp__…` title never had the prefix either).
            // Same defect as the fs-op one above, through the title instead of rawInput — hence the same
            // shape of fix. Sources 1 and 2 are untouched: a tool that genuinely WRAPS a command carries
            // it in rawInput.command (our own run_command, a third-party shell server), and that is a real
            // command subject worth a glob. Only the title-derived guess is vetoed.
            var command = IsFileOpKind(kind)
                ? null
                : commandFact
                    ?? (toolName is null && (IsCommandKind(kind) || TitleIsCommand(title))
                        ? ExtractCommand(title)
                        : null);

            // The file path a file-scoped request targets — the subject the shell's path allow rules
            // match against, the way Command is the subject for command rules. Takes the resolved tool
            // name for the same reason the command above does: an MCP call has neither subject.
            var path = GetPathSubject(p, kind, toolName, agentRoot);

            // Carry the tool's raw input as Detail: it's what the permission banner shows (and, for a
            // command, what the deny-list scans as a fail-safe). Kiro's permission frame carries no
            // rawInput of its own — but under v3 the preceding tool_call does (v2 sent none there
            // either), so the cached copy lets the banner show an MCP call's real arguments.
            var detail = GetRawInput(p.ToolCall) ?? LookupCached(rawInputCache, toolCallId);

            // Synthesize a session-scoped "Deny (this session)" when the backend offers a plain reject but
            // no "always deny" (Claude Code sends only allow_once/allow_always/reject_once — never
            // reject_always), so the affordance is uniform across backends. We own deny client-side anyway
            // (answer reject_once + remember our own rule), so no backend option is needed: picking it
            // resolves via PolicyPermissionHandler's RejectAlways branch, which stores the session rule and
            // answers the agent the REAL reject_once — the sentinel id never reaches the agent. Only for a
            // *scoped* subject (a command, a file path, or a named MCP tool) so there's something specific
            // to deny; a bare unkinded request is skipped (it would give only a coarse by-kind block).
            var hasRejectOnce = options.Any(o => o.Kind == PermissionOptionKind.RejectOnce);
            var hasRejectAlways = options.Any(o => o.Kind == PermissionOptionKind.RejectAlways);
            var denyableSubject = command is not null || path is not null || toolName is not null;
            if (hasRejectOnce && !hasRejectAlways && denyableSubject)
                options.Add(new PermissionOption(
                    SyntheticDenySessionOptionId, "Deny (this session)", PermissionOptionKind.RejectAlways));

            // detail twice, deliberately: as Detail it is banner text, as ArgumentsJson it is the
            // subject a policy may read. Same bytes today, different contracts - see PermissionRequest.
            return new PermissionRequest(
                toolCallId, title, kind, detail, command, options, toolName, path, ArgumentsJson: detail,
                FromSubagentSession: fromSubagentSession);
        }

        /// <summary>
        /// Sentinel id for the client-synthesized "Deny (this session)" option, added when the backend
        /// offers no <c>reject_always</c>. It carries kind <see cref="PermissionOptionKind.RejectAlways"/>
        /// so the shell policy owns it (remember a session deny, answer the agent the real
        /// <c>reject_once</c>); this id is consumed shell-side and never sent to the agent.
        /// </summary>
        public const string SyntheticDenySessionOptionId = "__cwkt_deny_session";

        // Recovers the backend's namespaced MCP tool name (Claude "mcp__server__tool", Kiro
        // "@server/tool") so the shell can map it to an authored tool risk. Prefers a structured
        // name/toolName field, then the title (Kiro titles MCP runs "Running: @server/tool"; Claude may
        // use the namespaced name as the title). Returns null when nothing looks like a namespaced name.
        // <paramref name="fromTitle"/> false keeps the structured field and refuses the title: the
        // permission path passes false for a frame the backend marks as a shell command (see
        // ToPermissionRequest), where the title IS the command the model chose.
        private static string? ExtractToolName(JsonElement toolCall, string title, bool fromTitle = true)
        {
            var named = GetString(toolCall, "toolName") ?? GetString(toolCall, "name");
            if (LooksNamespaced(named))
                return named!.Trim();
            if (!fromTitle)
                return null;

            var titled = ExtractCommand(title); // strips a leading "Running: "
            return LooksNamespaced(titled) ? titled : null;
        }

        // A tool name is namespaced when it carries a backend's server prefix — the only forms our
        // IdeMcpServer parser can unwrap. Avoids surfacing prose titles as tool names.
        private static bool LooksNamespaced(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;
            var v = s!.Trim();
            return v.StartsWith("mcp__", StringComparison.Ordinal)
                || (v.StartsWith("@", StringComparison.Ordinal) && v.IndexOf('/') > 0);
        }

        /// <summary>
        /// The tool kind the host should act on. Normally the backend's own <c>kind</c>, but "other" is
        /// ACP's CATCH-ALL — the absence of a classification, not a claim about what the call does — so
        /// an unclassified, non-MCP call carrying a real <c>rawInput.command</c> resolves to "execute".
        /// </summary>
        /// <remarks>
        /// <para>
        /// claude-agent-acp 0.70.0 ships a Windows shell tool that titles itself "PowerShell" and
        /// declares <c>kind:"other"</c>, where the <c>Bash</c> tool it replaces declared "execute".
        /// Measured across five captured sessions: 11 / 21 / 24 / 5 execute frames and not one "other"
        /// before it, then 7 "other" and not one execute after. Everything keying on the kind therefore
        /// stopped recognising the agent's own shell commands.
        /// </para>
        /// <para>
        /// The terminal mirror is where that showed, and it showed as NOTHING: its non-streaming path
        /// (which exists for this backend — Claude sends no mid-run chunks, only a completed frame
        /// carrying the whole output) is gated on the kind, so seven real commands were dropped before
        /// the mirror was ever asked to write. A pane is created lazily on the first write, so no write
        /// meant no pane, no service connect, and NO LOG LINE AT ALL — a feature that had simply stopped,
        /// with an untouched "setting=on" line as the only trace and nothing to suggest where to look.
        /// </para>
        /// <para>
        /// Narrow on purpose, three ways. Only the catch-all is reinterpreted, so a kind the backend
        /// actually chose is never overridden — including "edit"/"read", which is what keeps Kiro's
        /// fs-op discriminator (it puts "strReplace" in an EDIT's <c>rawInput.command</c>) from being
        /// read as a shell command. An MCP call is left alone, because its rawInput follows a THIRD
        /// PARTY's schema and a key named "command" means whatever that server says it means — a Jira
        /// tool takes a "command" that is not a command line (#129, #131). And it reads the same
        /// <c>rawInput.command</c> the permission path already resolves rather than matching on tool
        /// NAMES, which would need a new entry every time a backend adds a shell.
        /// </para>
        /// <para>
        /// Display-side only, and deliberately: the permission path derives its own kind from the
        /// request frame (<see cref="ToPermissionRequest"/>) and never reads this one, and "other" and
        /// "execute" both resolve to the top Command tier anyway — so no approval decision moves.
        /// </para>
        /// </remarks>
        private static string? ResolveToolKind(JsonElement update, string? kind, string? toolName) =>
            string.Equals(kind, "other", StringComparison.OrdinalIgnoreCase) &&
            toolName is null &&
            GetCommand(update) is not null
                ? "execute"
                : kind;

        // A permission request is a shell-command request when its (possibly-derived) kind names an
        // execute/command action. Mirrors the banner's IsCommand test.
        private static bool IsCommandKind(string? kind) =>
            kind is not null &&
            (kind.IndexOf("execute", StringComparison.OrdinalIgnoreCase) >= 0 ||
             kind.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0);

        // File-operation kinds — edits (write/create/delete/move) and reads. A positive signal that a
        // request is NOT a shell command (its subject is a file path, not a command line), so a real
        // command's execute/other kind still resolves a command while Kiro's fs-op discriminator
        // ("strReplace" in an edit's rawInput.command) never does.
        private static readonly string[] FileOpKinds = { "edit", "write", "create", "delete", "move", "read" };

        private static bool IsFileOpKind(string? kind) =>
            kind is not null &&
            Array.Exists(FileOpKinds, k => kind.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// If this session update is a tool call carrying a shell command in <c>rawInput.command</c>,
        /// records it in <paramref name="cache"/> keyed by <c>toolCallId</c>. A later
        /// <c>request_permission</c> (which — for Kiro — carries no rawInput and only a possibly-truncated
        /// command title) then recovers the full command via <see cref="ToPermissionRequest"/>.
        /// </summary>
        internal static void CacheToolCommand(JsonElement update, IDictionary<string, string> cache)
        {
            var id = GetString(update, "toolCallId");
            if (string.IsNullOrEmpty(id))
                return;
            // Never cache a file-op tool's rawInput.command — for Kiro's fs tools that's an operation
            // discriminator ("strReplace"), not a shell command (see IsFileOpKind). Caching it would let
            // the later permission request recover a bogus command even after the consumer-side guard.
            if (IsFileOpKind(GetString(update, "kind")))
                return;
            var command = GetCommand(update);
            if (!string.IsNullOrEmpty(command))
                cache[id!] = command!;
        }

        private static string? LookupCached(IReadOnlyDictionary<string, string>? cache, string toolCallId) =>
            cache is not null && !string.IsNullOrEmpty(toolCallId) && cache.TryGetValue(toolCallId, out var c)
                ? c
                : null;

        /// <summary>
        /// Records a tool call's display-ready raw input (see <see cref="GetRawInputObjectJson"/>) in
        /// <paramref name="cache"/> keyed by <c>toolCallId</c> — the args side of the command-cache
        /// pattern. A later <c>request_permission</c> (whose toolCall carries no rawInput on Kiro)
        /// recovers it via <see cref="ToPermissionRequest"/> so the banner can show what the call
        /// will actually do. No-op when the update carries no arguments (Claude's placeholder
        /// <c>{}</c> never overwrites a previously cached real input).
        /// </summary>
        internal static void CacheToolRawInput(JsonElement update, IDictionary<string, string> cache)
        {
            var id = GetString(update, "toolCallId");
            if (string.IsNullOrEmpty(id))
                return;
            if (GetRawInputObjectJson(update) is { } rawInput)
                cache[id!] = rawInput;
        }

        // A tool titled "Running: …" is a shell-command call (how agents title command tool calls), so
        // its title is a valid command source even when the permission frame carries no `kind` (Kiro).
        private static bool TitleIsCommand(string title) =>
            title.TrimStart().StartsWith("Running:", StringComparison.OrdinalIgnoreCase);

        // The intent field names an edit tool's rawInput may carry (Kiro's __tool_use_purpose, Claude's
        // description) — the same keys the host reads for a tool row's "why" subtitle.
        private static readonly string[] EditIntentKeys = { "__tool_use_purpose", "description" };

        // Pulls an edit's display metadata off the tool call's rawInput: the agent's stated intent and
        // the backend's edit-operation name (Kiro's rawInput.command, e.g. "strReplace"; NOT a shell
        // command — see IsFileOpKind). Both null when absent (Claude edits carry neither). Kept in the
        // mapper so the UI never decodes provider-specific edit shapes.
        private static (string? Intent, string? Operation) ExtractEditMeta(JsonElement update)
        {
            if (update.ValueKind != JsonValueKind.Object ||
                !update.TryGetProperty("rawInput", out var raw) ||
                raw.ValueKind != JsonValueKind.Object)
                return (null, null);

            string? intent = null;
            foreach (var key in EditIntentKeys)
                if (raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) { intent = s!.Trim(); break; }
                }

            string? operation = null;
            if (raw.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            {
                var s = cmd.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    operation = s!.Trim();
            }

            return (intent, operation);
        }

        // The structured command a command tool will run, from rawInput.command (a string). Null when
        // the raw input isn't an object with a string command (e.g. Kiro, which sends no rawInput).
        private static string? GetCommand(JsonElement toolCall)
        {
            if (toolCall.ValueKind == JsonValueKind.Object &&
                toolCall.TryGetProperty("rawInput", out var raw) &&
                raw.ValueKind == JsonValueKind.Object &&
                raw.TryGetProperty("command", out var cmd) &&
                cmd.ValueKind == JsonValueKind.String)
            {
                var s = cmd.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
            }

            return null;
        }

        // Strips a leading "Running: " (how the ACP layer titles command tool calls) so a command
        // parsed from the title is just the command text. Used as the Kiro fallback for GetCommand.
        private static string ExtractCommand(string title)
        {
            const string prefix = "Running:";
            var t = title?.Trim() ?? string.Empty;
            return t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? t.Substring(prefix.Length).Trim()
                : t;
        }

        /// <summary>
        /// Infers an action kind when the tool call omits <c>kind</c> (Kiro sends none, either engine).
        /// v3 (primary): <c>_meta.kiro.consent.capability</c> — the permissions.yaml taxonomy
        /// (<c>fs_write</c>/<c>filesystem</c> → edit, <c>fs_read</c> → read, <c>shell</c> → execute).
        /// v2 (fallback): <c>_meta.trustOptions[].setting_key</c> (<c>*write*</c> → edit,
        /// <c>*command*</c> → execute, <c>*read*</c> → read).
        /// Returns null when there's no usable hint (e.g. MCP tool calls), leaving the request unkinded.
        /// </summary>
        private static string? DeriveKind(JsonElement? meta)
        {
            if (GetKiroConsent(meta) is { } consent && GetString(consent, "capability") is { } capability)
            {
                // "filesystem" spans read+write; classify to the higher (edit) tier. Other capabilities
                // (mcp, subagent, web_*…) deliberately stay unkinded — MCP risk resolves by tool name.
                if (KindOfCapability(capability) is { } fromCapability)
                    return fromCapability;

                // The capability is the MATCHED RULE's, not the tool's (measured 2026-09-11, kiro-cli
                // 2.21.1): a rule written as `capability: all` reports "all" on a plain file read, and
                // the request would land unkinded — top tier, and matching no path rule. The tool id
                // beside it still says what the tool IS, so it decides when the rule does not.
                if (capability.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                    KindOfKiroToolId(GetKiroToolId(meta)) is { } fromTool)
                    return fromTool;
            }

            if (meta is not { ValueKind: JsonValueKind.Object } m ||
                !m.TryGetProperty("trustOptions", out var opts) || opts.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var opt in opts.EnumerateArray())
            {
                var key = GetString(opt, "setting_key");
                if (key is null)
                    continue;
                if (key.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0) return "edit";
                if (key.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0) return "execute";
                if (key.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0) return "read";
            }

            return null;
        }

        // Kiro v3's consent block (_meta.kiro.consent: {capability, resource, workspaceRoot, …}), or
        // null when absent (v2, spec-ACP agents).
        /// <summary>The permissions.yaml capability taxonomy → action kind; null for the ones that
        /// deliberately stay unkinded (mcp, subagent, web_*) and for a rule-level "all".</summary>
        private static string? KindOfCapability(string capability)
        {
            if (capability.Equals("fs_write", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("filesystem", StringComparison.OrdinalIgnoreCase))
                return "edit";
            if (capability.Equals("fs_read", StringComparison.OrdinalIgnoreCase))
                return "read";
            if (capability.Equals("shell", StringComparison.OrdinalIgnoreCase))
                return "execute";
            return null;
        }

        /// <summary>
        /// Kiro v3's <c>_meta.kiro.toolId</c> → action kind. Two spellings are on the wire for the file
        /// tools — <c>fs_write</c> on a write, <c>read_file</c> on a read (both captured on 2.21.1) —
        /// so this matches the ROOT of the name rather than a list: anything reading, anything writing
        /// or replacing, anything shell-like. Unknown ids stay null rather than guessing a tier.
        /// </summary>
        private static string? KindOfKiroToolId(string? toolId)
        {
            if (string.IsNullOrEmpty(toolId))
                return null;
            var id = toolId!;
            if (id.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("replace", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0)
                return "edit";
            if (id.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0)
                return "read";
            if (id.IndexOf("shell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("execute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0)
                return "execute";
            return null;
        }

        /// <summary>Whether a v3 consent frame is about a FILE: its capability says so, or — under an
        /// "all" rule that says nothing — its tool id does.</summary>
        private static bool IsKiroFileConsent(JsonElement? meta, JsonElement consent)
        {
            if (GetString(consent, "capability") is not { } capability)
                return false;
            if (capability.StartsWith("fs_", StringComparison.OrdinalIgnoreCase) ||
                capability.Equals("filesystem", StringComparison.OrdinalIgnoreCase))
                return true;
            return capability.Equals("all", StringComparison.OrdinalIgnoreCase)
                && KindOfKiroToolId(GetKiroToolId(meta)) is "read" or "edit";
        }

        private static string? GetKiroToolId(JsonElement? meta) =>
            meta is { ValueKind: JsonValueKind.Object } m &&
            m.TryGetProperty("kiro", out var kiro) && kiro.ValueKind == JsonValueKind.Object
                ? GetString(kiro, "toolId")
                : null;

        private static JsonElement? GetKiroConsent(JsonElement? meta)
        {
            if (meta is { ValueKind: JsonValueKind.Object } m &&
                m.TryGetProperty("kiro", out var kiro) && kiro.ValueKind == JsonValueKind.Object &&
                kiro.TryGetProperty("consent", out var consent) && consent.ValueKind == JsonValueKind.Object)
                return consent;
            return null;
        }

        /// <summary>
        /// Resolves the absolute file path a file-scoped (edit/read) permission request targets — the
        /// subject the shell's path allow rules match against. Sources, in order:
        /// Kiro v3 <c>_meta.kiro.consent.resource</c> (workspace-relative; rooted against
        /// <c>consent.workspaceRoot</c>) for fs capabilities only; Kiro v2
        /// <c>_meta.trustOptions[].patterns[0]</c> (the "Specific paths" row's absolute path);
        /// spec-ACP <c>rawInput.file_path</c>/<c>rawInput.path</c> (Claude); the tool call's
        /// <c>locations[0].path</c>. Null for command requests, for MCP tool calls, and when nothing
        /// resolves.
        /// </summary>
        private static string? GetPathSubject(
            RequestPermissionParams p, string? kind, string? toolName, string? agentRoot)
        {
            // A command's "resource"/path fields aren't file subjects (a shell consent's resource is
            // the command text) — path rules must never match command requests.
            if (IsCommandKind(kind))
                return null;

            // AN MCP TOOL'S SUBJECT IS THE TOOL, NEVER A PATH — the same reasoning that keeps a third
            // party's rawInput.command from being read as a shell command (#129, #131, and
            // ResolveToolKind's remarks), applied to the key next to it. An MCP call's rawInput follows
            // THAT SERVER's schema, so a "path"/"file_path" in it means whatever the server says it
            // means; it is not a file this host is being asked to let the agent edit. Kiro sends no
            // kind for one and DeriveKind returns null by design ("MCP risk resolves by tool name"), so
            // the command gate above does not catch it and every fallback below would.
            //
            // Three things went wrong at once without this, and they formed a loop — the same defect
            // minting the over-broad rule and then honouring it:
            //   * PolicyPermissionHandler auto-approved the call against a persisted AllowedPaths glob,
            //     while asserting in its own comment that "requests without a Path (commands, MCP
            //     tools) can never satisfy a path rule";
            //   * PermissionBanner offered a PATH-scoped "Allow always" (widenable to the folder) and,
            //     because HasToolScope is gated on !HasPathScope, suppressed the server/tool rule that
            //     is an MCP tool's durable home (#129) — so the correct rule was unreachable for
            //     exactly these calls;
            //   * the banner's DisplayDetail is null under a path scope, so a third-party tool call was
            //     approved with its arguments hidden, the banner having taken it for an edit whose
            //     change is in the diff viewer.
            //
            // ExtractToolName is namespace-anchored (mcp__* / @server/tool), so this costs the file
            // subject of nothing that has one: Claude's Edit and Kiro's fs writes are not namespaced.
            if (toolName is not null)
                return null;

            if (GetKiroConsent(p.Meta) is { } consent &&
                IsKiroFileConsent(p.Meta, consent) &&
                GetString(consent, "resource") is { Length: > 0 } resource)
            {
                // The frame states its own root, which beats the session cwd where they differ — but
                // fall back to the cwd, since a resource with no stated root is still relative to the
                // agent's. One normalize call does the rooting and the canonicalization together.
                return NormalizeLocalPath(
                    resource, GetString(consent, "workspaceRoot") is { Length: > 0 } root ? root : agentRoot);
            }

            // Kiro v2: the "Specific paths" trust option's pattern IS the file's absolute path. Prefer
            // it over "Complete directory" (both share setting_key); skip command rows entirely.
            if (p.Meta is { ValueKind: JsonValueKind.Object } m &&
                m.TryGetProperty("trustOptions", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                string? firstPattern = null;
                foreach (var opt in opts.EnumerateArray())
                {
                    var key = GetString(opt, "setting_key");
                    if (key is null || key.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    if (!opt.TryGetProperty("patterns", out var patterns) ||
                        patterns.ValueKind != JsonValueKind.Array || patterns.GetArrayLength() == 0)
                        continue;
                    var pattern = patterns[0].ValueKind == JsonValueKind.String ? patterns[0].GetString() : null;
                    if (string.IsNullOrWhiteSpace(pattern))
                        continue;

                    var label = GetString(opt, "label");
                    if (label is not null && label.IndexOf("Specific", StringComparison.OrdinalIgnoreCase) >= 0)
                        return NormalizeLocalPath(pattern!, agentRoot);
                    firstPattern ??= pattern;
                }
                if (firstPattern is not null)
                    return NormalizeLocalPath(firstPattern, agentRoot);
            }

            // Spec-ACP (Claude): the tool's structured input names the file.
            if (p.ToolCall.ValueKind == JsonValueKind.Object &&
                p.ToolCall.TryGetProperty("rawInput", out var raw) && raw.ValueKind == JsonValueKind.Object)
            {
                var fromInput = GetString(raw, "file_path") ?? GetString(raw, "path");
                if (!string.IsNullOrWhiteSpace(fromInput))
                    return NormalizeLocalPath(fromInput!, agentRoot);
            }

            if (p.ToolCall.ValueKind == JsonValueKind.Object &&
                p.ToolCall.TryGetProperty("locations", out var locations) &&
                locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0 &&
                GetString(locations[0], "path") is { Length: > 0 } location)
                return NormalizeLocalPath(location, agentRoot);

            return null;
        }

        /// <summary>
        /// The canonical local path for an agent-reported one — see <see cref="AgentPath.Canonical"/>,
        /// which owns the rule and the three spellings that have split one edit into two cards.
        /// <paramref name="agentRoot"/> is the ACP session's cwd (<c>AcpAgentSession.ResolveCwd</c>), the
        /// origin the AGENT measures relative paths from; it is not necessarily the solution root (#54).
        /// Kept as a named seam because <see cref="AcpClientTarget"/> keys its write capture through it,
        /// and both sides of that lookup must go the same way.
        /// </summary>
        internal static string NormalizeLocalPath(string path, string? agentRoot = null)
            => AgentPath.Canonical(path, agentRoot);

        private static string? GetRawInput(JsonElement toolCall)
        {
            if (toolCall.ValueKind != JsonValueKind.Object || !toolCall.TryGetProperty("rawInput", out var raw))
                return null;

            return raw.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => raw.GetString(),
                // An argument-less tool (e.g. build_solution) sends an empty rawInput; don't surface the
                // literal "{}"/"[]" as a permission-banner subtitle — it's noise, not information.
                // (_meta doesn't count as an argument and is stripped — see HasAnyDisplayProperty.)
                JsonValueKind.Object => HasAnyDisplayProperty(raw) ? StripMeta(raw) : null,
                JsonValueKind.Array => raw.GetArrayLength() > 0 ? raw.GetRawText() : null,
                _ => raw.GetRawText(),
            };
        }

        private static PermissionOptionKind MapKind(string kind) => kind switch
        {
            "allow_once" => PermissionOptionKind.AllowOnce,
            "allow_always" => PermissionOptionKind.AllowAlways,
            "reject_once" => PermissionOptionKind.RejectOnce,
            "reject_always" => PermissionOptionKind.RejectAlways,
            _ => PermissionOptionKind.RejectOnce,
        };

        private static bool TryGetContentText(JsonElement update, out string text)
        {
            text = string.Empty;
            if (update.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Object &&
                GetString(content, "type") == "text" &&
                content.TryGetProperty("text", out var textEl) &&
                textEl.ValueKind == JsonValueKind.String)
            {
                text = textEl.GetString() ?? string.Empty;
                return text.Length > 0;
            }

            return false;
        }

        // Numeric accessors are tolerant of the wire's type drift: a backend may send a count as a JSON
        // number or (rarely) a string, and percentages arrive as both integers and floats. TryGet* rather
        // than Get* so an out-of-range or malformed value degrades to null instead of throwing mid-turn.
        //
        // The string half was described here and not implemented, and the gap is silent in the worst way:
        // a `"used":"120000"` makes TryMapClaudeUsage/TryMapKiroUsage return false, so the context ring
        // stops updating for the whole session and looks exactly like a backend that doesn't report usage
        // at all — collapsing the "a null means NOT REPORTED, never zero" distinction UsageReport is built
        // to keep. Invariant culture on purpose: this is a wire format, not a display one, and a machine
        // reading "80,5" as eight hundred and five is the failure that only shows up on someone else's
        // locale.
        private static int? GetInt(JsonElement element, string property) =>
            GetNumber(element, property) is not { } v ? null
            : v.ValueKind == JsonValueKind.Number ? (v.TryGetInt32(out var i) ? i : null)
            : int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) ? pi
            : null;

        private static long? GetLong(JsonElement element, string property) =>
            GetNumber(element, property) is not { } v ? null
            : v.ValueKind == JsonValueKind.Number ? (v.TryGetInt64(out var l) ? l : null)
            : long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl) ? pl
            : null;

        private static double? GetDouble(JsonElement element, string property) =>
            GetNumber(element, property) is not { } v ? null
            : v.ValueKind == JsonValueKind.Number ? (v.TryGetDouble(out var d) ? d : null)
            : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pd) ? pd
            : null;

        /// <summary>The property as a Number or a String element, or null — the two shapes a count or a
        /// percentage arrives in. Every other kind (an object, a null, a bool) is "not reported".</summary>
        private static JsonElement? GetNumber(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String)
                ? value
                : null;

        private static string? GetString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
