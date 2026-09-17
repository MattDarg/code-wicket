using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core
{
    /// <summary>
    /// Host-provided handler the agent calls before performing a sensitive action.
    /// Mirrors ACP <c>session/request_permission</c>.
    /// </summary>
    public interface IPermissionHandler
    {
        /// <summary>Asks the user (or applies <see cref="PermissionMode"/> policy) and returns the chosen option.</summary>
        Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>A request to perform a sensitive action, with the options the agent will honour.</summary>
    /// <remarks>
    /// <c>Command</c> is the clean shell command this request runs, for command (execute-kind) requests
    /// only; null otherwise. It is sourced from the tool's structured <c>rawInput.command</c> where
    /// available, else from the command cached off the preceding <c>tool_call</c> frame, and only as a
    /// last resort from the title. Which of those answers is per-ENGINE, not per-backend: Kiro v2 carries
    /// <c>rawInput.command</c> on the permission frame itself, while v3 sends no <c>rawInput</c> there at
    /// all — its command rides <c>_meta.kiro.command</c>, which we do not read — and so falls to the
    /// cache. Both measured on the wire 2026-09-09; do not restate this as "Kiro sends no raw input",
    /// which is the v3 shape written up as though it were the backend's.
    /// It is the authoritative string the command allow-list / remembered "always" globs match
    /// against — deliberately NOT the free-form <c>Title</c> (an agent may title a command with prose)
    /// nor the raw-input JSON blob in <c>Detail</c>.
    ///
    /// <c>ToolName</c> is the backend-reported tool identifier for MCP tool calls, in the backend's
    /// namespaced form (Claude <c>mcp__server__tool</c>, Kiro <c>@server/tool</c>); null when the request
    /// carries no recognisable tool name. The shell's policy resolves it (via
    /// <see cref="Ide.IdeMcpServer.TryResolveLocalTool"/>) to an authored tool risk — the only reliable
    /// signal for our IDE tools, whose requests carry no useful <c>Kind</c> (Kiro sends none at all).
    ///
    /// <c>Path</c> is the absolute file path a file-scoped (edit/read) request targets — the subject the
    /// path allow-list and remembered path globs match against, the way <c>Command</c> is the subject
    /// for command rules. Sourced per backend (Kiro v3 <c>_meta.kiro.consent.resource</c>+
    /// <c>workspaceRoot</c>, Kiro v2 <c>_meta.trustOptions[].patterns</c>, spec-ACP <c>rawInput</c>
    /// path fields); null when the request isn't file-scoped or no path could be resolved.
    /// </remarks>
    /// <remarks>
    /// <c>ArgumentsJson</c> is the tool call's raw input as it stood WHEN THE REQUEST ARRIVED, and it is
    /// diagnostic rather than decisive. No policy may key on it, and one briefly did: measured
    /// 2026-08-28, a permission request for an MCP tool arrives BEFORE the <c>tool_call</c> frame
    /// carrying the arguments, so this is null exactly when a decision would want it — while the banner
    /// fills the arguments in moments later and looks, to anyone watching, as though they were always
    /// there. It survives because the permission log reports it (<c>args=none</c>), and that line is what
    /// diagnosed the tier bug the resolver caused. Null whenever the frame carried no arguments.
    /// </remarks>
    /// <remarks>
    /// <c>FlaggedFragment</c> is set (by the shell policy) when the request matched an
    /// <c>AlwaysPromptCommands</c> caution pattern: the exact text that matched, so the banner can call it
    /// out in red and gate the approval (strip "Allow always", require an extra confirm). Null when the
    /// request wasn't flagged.
    /// </remarks>
    /// <remarks>
    /// <c>FromSubagentSession</c> is true when the request arrived on a session OTHER than the primary
    /// one - Kiro v2 multiplexes each sub-agent as its own ACP session on the one connection, and its
    /// permission requests stay unfiltered because the sub-agent needs the answer to progress. It is
    /// the only attribution v2 offers (no parent call id, so no row to climb), and the banner says
    /// "a sub-agent" without a name. False on v3 and Claude, whose sub-agents share the primary
    /// session and are attributed through the row's parent instead (a follow-on from the same review).
    /// </remarks>
    /// <remarks>
    /// <c>RuleNote</c> is one sentence the ordinary banner shows under the title when an allow rule
    /// the user can see in their settings did NOT decide this request - a wildcard command rule
    /// withheld because the line carries a shell operator (<see cref="ShellOperators"/>). Not a flag:
    /// the request is ordinary, the banner is the ordinary one, and "Allow always" is still offered.
    /// Null on every request no rule was withheld from.
    /// </remarks>
    /// <remarks>
    /// <c>FlaggedReason</c> says WHY a flagged request was flagged, when it was not the user's own
    /// always-prompt rule: the banner's call-out reads it in place of "Matches your always-prompt rule".
    /// Null on an ordinary caution hit, where that default sentence is the truth. Set beside
    /// <c>FlaggedFragment</c> for a write into the extension's own state
    /// (<see cref="ProtectedPaths"/>), where the fragment is the path and the sentence names a rule the
    /// user never wrote and cannot find in their settings.
    /// </remarks>
    public sealed record PermissionRequest(
        string ToolCallId,
        string Title,
        string? Kind,
        string? Detail,
        string? Command,
        IReadOnlyList<PermissionOption> Options,
        string? ToolName = null,
        string? Path = null,
        string? FlaggedFragment = null,
        string? ArgumentsJson = null,
        string? FlaggedReason = null,
        string? RuleNote = null,
        bool FromSubagentSession = false);

    /// <summary>One selectable response to a <see cref="PermissionRequest"/>.</summary>
    public sealed record PermissionOption(string OptionId, string Label, PermissionOptionKind Kind);

    /// <summary>The nature of a permission option, so the UI can style it and apply "always" policies.</summary>
    public enum PermissionOptionKind
    {
        AllowOnce,
        AllowAlways,
        RejectOnce,
        RejectAlways,
    }

    /// <summary>The user's (or policy's) decision. <see cref="Cancelled"/> indicates the turn was aborted.</summary>
    /// <param name="OptionId">The chosen option's id (the value the agent expects back).</param>
    /// <param name="Cancelled">True when the turn was aborted rather than answered.</param>
    /// <param name="RememberCommand">
    /// When the user chose an "always" option for a command, the glob pattern to remember (which they
    /// may have edited to broaden/narrow the scope, e.g. <c>git *</c>). Null for non-command decisions.
    /// </param>
    /// <param name="PersistRemembered">
    /// True if a remembered <see cref="RememberCommand"/>/<see cref="RememberPath"/> should be persisted
    /// durably (to config) rather than kept only for the session.
    /// </param>
    /// <param name="RememberPath">
    /// When the user chose an "always" option for a file-scoped (edit) request, the path glob to
    /// remember (pre-filled with the request's <see cref="PermissionRequest.Path"/>; the user may have
    /// widened it to a directory, e.g. <c>C:\repo\src\*</c>). Null for non-edit decisions.
    /// </param>
    /// <param name="RememberTool">
    /// When the user chose "always" for an MCP TOOL request — one with no command and no path subject,
    /// so neither glob applies — the namespaced tool name to remember (e.g.
    /// <c>@code-wicket/build_solution</c>). Unlike the two globs this is not editable: a tool
    /// rule matches one named tool exactly, so there is no scope to widen. Null for every other decision.
    /// </param>
    public sealed record PermissionDecision(
        string OptionId,
        bool Cancelled = false,
        string? RememberCommand = null,
        bool PersistRemembered = false,
        string? RememberPath = null,
        string? RememberTool = null);
}
