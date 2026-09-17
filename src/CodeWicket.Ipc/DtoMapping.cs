using System;
using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Ipc
{
    /// <summary>Converts between Core domain types and the wire DTOs.</summary>
    public static class DtoMapping
    {
        // Core <-> DTO for a permission outcome. The kind crosses as a STRING: a transcript saved by a
        // newer build must replay on an older one, and an unrecognised kind has to degrade to "no
        // usable record" rather than throw halfway through rebuilding a conversation.
        public static PermissionOutcomeDto ToDto(PermissionOutcome outcome) => new(
            outcome.Kind switch
            {
                PermissionOutcomeKind.NotRequested => "notRequested",
                PermissionOutcomeKind.UserAllowed => "userAllowed",
                PermissionOutcomeKind.UserDenied => "userDenied",
                PermissionOutcomeKind.RuleAllowed => "ruleAllowed",
                PermissionOutcomeKind.RuleDenied => "ruleDenied",
                PermissionOutcomeKind.ModeAllowed => "modeAllowed",

                // "unknown", NOT "notRequested" — the write side has to degrade the same way the read
                // side does, and the two answers are opposites. ToOutcome below deliberately maps an
                // unrecognised kind to null = "no record"; this defaulted an unrecognised kind to a
                // CLAIM, and the most reassuring one available: nobody was ever asked. Add a member to
                // PermissionOutcomeKind and forget this switch, and every affected tool row is persisted
                // and replayed asserting that a security-relevant decision was never put to the user —
                // silently, with no throw and no log, and no way for a reader to tell.
                //
                // A string that is in neither list round-trips through ToOutcome to null, which is the
                // "we cannot read this" the row already knows how to draw. Same reasoning as that
                // method's own comment: claiming the wrong permission state is worse than admitting we
                // cannot read it.
                _ => "unknown",
            },
            outcome.Rule, outcome.RulePersisted, outcome.CautionPrompted, outcome.CautionReason);

        /// <summary>
        /// The outcome a saved event describes, or null when it describes none we understand - which the
        /// row renders as "no record", the same as a log written before outcomes existed. Unrecognised is
        /// deliberately folded into that rather than guessed at: claiming the wrong permission state is
        /// worse than admitting we cannot read it.
        /// </summary>
        public static PermissionOutcome? ToOutcome(PermissionOutcomeDto? dto)
        {
            if (dto is null)
                return null;
            PermissionOutcomeKind kind;
            switch (dto.Kind)
            {
                case "notRequested": kind = PermissionOutcomeKind.NotRequested; break;
                case "userAllowed": kind = PermissionOutcomeKind.UserAllowed; break;
                case "userDenied": kind = PermissionOutcomeKind.UserDenied; break;
                case "ruleAllowed": kind = PermissionOutcomeKind.RuleAllowed; break;
                case "ruleDenied": kind = PermissionOutcomeKind.RuleDenied; break;
                case "modeAllowed": kind = PermissionOutcomeKind.ModeAllowed; break;
                default: return null;
            }
            return new PermissionOutcome(kind, dto.Rule, dto.RulePersisted, dto.CautionPrompted, dto.CautionReason);
        }

        // --- AgentEvent (engine -> shell, one way) ---
        public static AgentEventDto ToDto(AgentEvent ev) => ev switch
        {
            AgentEvent.AssistantTextDelta e => new AgentEventDto { Type = "text", Text = e.Text },
            AgentEvent.ThinkingDelta e => new AgentEventDto { Type = "thinking", Text = e.Text },
            // A false flag maps to null, not false: these fields are written into every persisted
            // session log, and "not a sub-agent" is the overwhelmingly common case (WhenWritingNull).
            AgentEvent.ToolCallStarted e => new AgentEventDto { Type = "toolStart", ToolCallId = e.ToolCallId, Title = e.Title, Kind = e.Kind, RawInputJson = e.RawInputJson, ToolName = e.ToolName, ParentToolCallId = e.ParentToolCallId, IsSubagentLaunch = e.IsSubagentLaunch ? true : null },
            AgentEvent.ToolCallUpdated e => new AgentEventDto { Type = "toolUpdate", ToolCallId = e.ToolCallId, Title = e.Title, Kind = e.Kind, RawInputJson = e.RawInputJson, ToolName = e.ToolName, ParentToolCallId = e.ParentToolCallId, IsSubagentLaunch = e.IsSubagentLaunch ? true : null },
            AgentEvent.ToolCallProgress e => new AgentEventDto { Type = "toolProgress", ToolCallId = e.ToolCallId, Message = e.Message },
            AgentEvent.ToolCallOutputChunk e => new AgentEventDto { Type = "toolOutput", ToolCallId = e.ToolCallId, Text = e.Text },
            // HostAuthored deliberately does NOT take the "false maps to null" treatment above. It is
            // read as a three-state on replay: absent means the entry predates the field, and a false
            // folded to null would forge that date on every ordinary completion. It is written out both
            // ways so that absence can only come from a log older than this line.
            AgentEvent.ToolCallCompleted e => new AgentEventDto { Type = "toolDone", ToolCallId = e.ToolCallId, Success = e.Success, Message = e.ResultText, ErrorText = e.ErrorText, LaunchedInBackground = e.LaunchedInBackground ? true : null, HostAuthored = e.HostAuthored },
            AgentEvent.EditProposed e => new AgentEventDto { Type = "edit", ToolCallId = e.ToolCallId, Path = e.Path, OldText = e.OldText, NewText = e.NewText, EditLine = e.Line, EditIntent = e.Intent, EditOperation = e.Operation, ParentToolCallId = e.ParentToolCallId },
            AgentEvent.PlanUpdated e => new AgentEventDto { Type = "plan", Title = e.Title, PlanItems = e.Items.Select(i => new PlanItemDto(i.Id, i.Description, i.Status.ToString())).ToList() },
            AgentEvent.SubagentsUpdated e => new AgentEventDto { Type = "subagents", Subagents = e.Agents.Select(a => new SubagentDto(a.SessionId, a.Name, a.Description, a.Status, a.StatusMessage, a.Group)).ToList() },
            AgentEvent.SubagentResult e => new AgentEventDto { Type = "subagentResult", SubagentSessionId = e.SubagentSessionId, Text = e.Text },
            AgentEvent.McpServerConnected e => new AgentEventDto { Type = "mcpServerConnected", Title = e.ServerName },
            AgentEvent.McpRosterUpdated e => new AgentEventDto
            {
                Type = "mcpRoster",
                McpRoster = new McpRosterDto(
                    e.Roster.Servers
                        .Select(s => new McpServerDto(s.Name, s.IsConnected, s.RawStatus, s.AuthType, s.ToolCount, s.ToolNames))
                        .ToList(),
                    e.Roster.NamesWholeConfiguredSet),
            },
            AgentEvent.McpBridgeStatusUpdated e => new AgentEventDto
            {
                Type = "mcpBridge",
                McpBridge = new McpBridgeDto(e.Bridge.Connected, e.Bridge.ToolsServed, e.Bridge.ToolNames, e.Bridge.ToolCalls),
            },
            AgentEvent.SessionError e => new AgentEventDto { Type = "error", Message = e.Message, Details = e.Details },
            AgentEvent.BackendNotice e => new AgentEventDto
            {
                Type = "notice", Message = e.Message, NoticeLevel = e.Level, Details = e.Details,
            },
            AgentEvent.TurnCompleted e => new AgentEventDto { Type = "turnDone", StopReason = e.StopReason, Usage = ToDto(e.Usage) },
            AgentEvent.UsageUpdated e => new AgentEventDto { Type = "usage", Usage = ToDto(e.Usage) },
            AgentEvent.BackgroundTaskReturned => new AgentEventDto { Type = "backgroundTaskReturned" },
            _ => new AgentEventDto { Type = "unknown" },
        };

        /// <summary>
        /// Prompt attachments, wire -> Core. The DTO's <c>Name</c> is dropped deliberately: it exists
        /// for the composer chip and the transcript tooltip, and there is no ACP field for it, so
        /// carrying it further would only invite someone to invent a place to put it.
        /// </summary>
        public static IReadOnlyList<PromptAttachment> ToCore(IReadOnlyList<PromptAttachmentDto>? attachments) =>
            attachments is null || attachments.Count == 0
                ? Array.Empty<PromptAttachment>()
                : attachments.Select(a => new PromptAttachment(a.MimeType, a.Data)).ToList();

        /// <summary>Flattens a usage report for the wire. Null in, null out — "not reported" must survive.</summary>
        public static UsageDto? ToDto(UsageReport? usage) => usage is null ? null : new UsageDto
        {
            ContextPercent = usage.ContextPercent,
            ContextUsedTokens = usage.ContextUsedTokens,
            ContextWindowTokens = usage.ContextWindowTokens,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CachedReadTokens = usage.CachedReadTokens,
            CachedWriteTokens = usage.CachedWriteTokens,
            TotalTokens = usage.TotalTokens,
            Cost = usage.Cost,
            CostCurrency = usage.CostCurrency,
            RateLimitStatus = usage.RateLimitStatus,
            RateLimitType = usage.RateLimitType,
            RateLimitResetsAt = usage.RateLimitResetsAt,
            SummarizeThresholdPercent = usage.SummarizeThresholdPercent,
            TruncateThresholdPercent = usage.TruncateThresholdPercent,
            Breakdown = usage.Breakdown?.Select(b => new UsageBreakdownDto(b.Label, b.Tokens, b.Percent)).ToList(),
        };

        // --- Provider metadata (engine -> shell, for the picker) ---
        public static ProviderInfoDto ToDto(IAgentProvider p) => new(
            p.ProviderId,
            p.DisplayName,
            p.Models.Select(m => new ModelInfoDto(m.Id, m.DisplayName, m.RateMultiplier)).ToList(),
            EnumerateCapabilities(p.Capabilities).ToList(),
            (p as IResumeCommandTemplate)?.ResumeCommandTemplate);

        // --- Backend session listing (engine -> shell, issue #108) ---

        // UtcDateTime, not DateTime — Core carries the offset and the DTO cannot, so the conversion has
        // to be made HERE where the offset is still known. Reading a DateTimeOffset's .DateTime instead
        // hands over the local wall-clock reading with its offset thrown away, which is off by hours and
        // wrong in a way nothing downstream can detect. Null stays null: a backend that does not track a
        // time has not reported one, and the host must not order a picker on a value it invented.
        public static BackendSessionDto ToDto(BackendSessionInfo info) => new(
            info.Id, info.Title, info.UpdatedAt?.UtcDateTime, info.Cwd, info.CreatedAt?.UtcDateTime);

        // One entry of an imported conversation (issue #108). One way, engine -> shell, like the rest
        // of this file: the shell replays these through the same Apply the live stream goes through
        // and never sends one back.
        public static ImportedEntryDto ToDto(ImportedTurnEntry entry) => new(
            entry.Role, entry.Text, entry.Event is null ? null : ToDto(entry.Event));

        private static IEnumerable<string> EnumerateCapabilities(AgentCapabilities caps) =>
            Enum.GetValues(typeof(AgentCapabilities))
                .Cast<AgentCapabilities>()
                .Where(c => c != AgentCapabilities.None && caps.HasFlag(c))
                .Select(c => c.ToString());

        // --- PermissionMode <-> string ---
        public static string ToWire(PermissionMode mode) => mode.ToString();

        public static PermissionMode ToPermissionMode(string value) =>
            PermissionModeParser.Parse(value);

        // --- WorkspaceSnapshot <-> DTO ---
        public static WorkspaceSnapshotDto ToDto(WorkspaceSnapshot s) => new()
        {
            SolutionName = s.SolutionName,
            ActiveFilePath = s.ActiveFilePath,
            Selection = s.Selection is { } sel
                ? new TextSelectionDto(sel.FilePath, sel.StartLine, sel.StartColumn, sel.EndLine, sel.EndColumn, sel.Text)
                : null,
            OpenFilePaths = s.OpenFilePaths,
            Diagnostics = s.Diagnostics
                .Select(d => new DiagnosticDto(d.FilePath, d.Line, d.Column, d.Severity.ToString(), d.Message, d.Code))
                .ToList(),
            TotalErrorCount = s.TotalErrorCount,
            TotalWarningCount = s.TotalWarningCount,
            OmittedDiagnosticCount = s.OmittedDiagnosticCount,
            DebugSession = s.DebugSession is { } debug
                ? new DebugSessionDto
                {
                    IsStopped = debug.IsStopped,
                    File = debug.File,
                    Line = debug.Line,
                    Method = debug.Method,
                    Reason = debug.Reason,
                    Thread = debug.Thread,
                    ProcessId = debug.ProcessId,
                    StopNumber = debug.StopNumber,
                }
                : null,
        };

        public static WorkspaceSnapshot ToSnapshot(WorkspaceSnapshotDto d) => new()
        {
            SolutionName = d.SolutionName,
            ActiveFilePath = d.ActiveFilePath,
            Selection = d.Selection is { } sel
                ? new TextSelection(sel.FilePath, sel.StartLine, sel.StartColumn, sel.EndLine, sel.EndColumn, sel.Text)
                : null,
            OpenFilePaths = d.OpenFilePaths,
            Diagnostics = d.Diagnostics
                .Select(x => new DiagnosticInfo(x.FilePath, x.Line, x.Column, ParseSeverity(x.Severity), x.Message, x.Code))
                .ToList(),
            TotalErrorCount = d.TotalErrorCount,
            TotalWarningCount = d.TotalWarningCount,
            OmittedDiagnosticCount = d.OmittedDiagnosticCount,
            DebugSession = d.DebugSession is { } debug
                ? new DebugSessionInfo(debug.IsStopped)
                {
                    File = debug.File,
                    Line = debug.Line,
                    Method = debug.Method,
                    Reason = debug.Reason,
                    Thread = debug.Thread,
                    ProcessId = debug.ProcessId,
                    StopNumber = debug.StopNumber,
                }
                : null,
        };

        private static DiagnosticSeverity ParseSeverity(string value) =>
            Enum.TryParse<DiagnosticSeverity>(value, ignoreCase: true, out var s) ? s : DiagnosticSeverity.Info;

        // --- Permission request/decision <-> DTO ---
        public static PermissionRequestDto ToDto(PermissionRequest r) => new(
            r.ToolCallId, r.Title, r.Kind, r.Detail, r.Command,
            r.Options.Select(o => new PermissionOptionDto(o.OptionId, o.Label, o.Kind.ToString())).ToList(),
            r.ToolName, r.Path, r.FlaggedFragment, FlaggedReason: r.FlaggedReason, RuleNote: r.RuleNote,
            FromSubagentSession: r.FromSubagentSession);

        public static PermissionRequest ToRequest(PermissionRequestDto d) => new(
            d.ToolCallId, d.Title, d.Kind, d.Detail, d.Command,
            d.Options.Select(o => new PermissionOption(o.OptionId, o.Label, ParseOptionKind(o.Kind))).ToList(),
            d.ToolName, d.Path, d.FlaggedFragment, FlaggedReason: d.FlaggedReason, RuleNote: d.RuleNote,
            FromSubagentSession: d.FromSubagentSession);

        private static PermissionOptionKind ParseOptionKind(string value) =>
            Enum.TryParse<PermissionOptionKind>(value, ignoreCase: true, out var k) ? k : PermissionOptionKind.RejectOnce;

        public static PermissionDecisionDto ToDto(PermissionDecision d) =>
            new(d.OptionId, d.Cancelled, d.RememberCommand, d.PersistRemembered, d.RememberPath, d.RememberTool);

        public static PermissionDecision ToDecision(PermissionDecisionDto d) =>
            new(d.OptionId, d.Cancelled, d.RememberCommand, d.PersistRemembered, d.RememberPath, d.RememberTool);

        // --- Tools <-> DTO ---
        public static ToolDescriptorDto ToDto(ToolDescriptor t) => new(t.Name, t.Description, t.InputSchemaJson);

        public static ToolDescriptor ToDescriptor(ToolDescriptorDto d) => new(d.Name, d.Description, d.InputSchemaJson);

        public static ToolResultDto ToDto(ToolResult r) => new(r.IsError, r.ContentJson);

        public static ToolResult ToResult(ToolResultDto d) => new(d.IsError, d.ContentJson);
    }
}
