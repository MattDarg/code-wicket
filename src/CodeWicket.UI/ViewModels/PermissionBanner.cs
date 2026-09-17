using System;
using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core.Ide;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.UI.Mvvm;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// An inline permission prompt shown as a banner above the chat input (Claude/Kiro-style),
    /// instead of a modal dialog. Each option is a button that resolves the agent's pending request.
    /// For a scoped request's "always" choice — a command (editable command glob) or a file-scoped
    /// edit (editable path glob with file/folder scope shortcuts) — resolving is deferred to a second
    /// step (<see cref="IsEditingAlways"/>) where the user can adjust the scope and opt to persist it.
    /// </summary>
    public sealed class PermissionBannerViewModel : ObservableObject
    {
        // (optionId, rememberCommandGlob, rememberPathGlob, persist, rememberTool) — at most one subject
        // is non-null.
        private readonly Action<string, string?, string?, bool, string?> _decide;
        private string? _alwaysOptionId;
        private string? _confirmOptionId;
        private bool _isEditingAlways;
        private bool _isConfirmingAllow;
        private bool _isDenyEdit;
        private string? _pattern;

        /// <param name="decide">
        /// Invoked with (optionId, rememberCommandGlob, rememberPathGlob, persist, rememberTool) when a
        /// choice is confirmed; the subjects feed the decision DTO's RememberCommand/RememberPath/RememberTool.
        /// </param>
        /// <param name="intent">
        /// The agent's stated reason for this call (extracted by the host from the tool input), shown as
        /// a subtitle. Offered for every kind of request, not just an edit: the transcript row beside the
        /// banner shows it for all of them, and the prompt is where it is most decision-relevant. It is
        /// the agent's CLAIM, not ground truth — a prompt-injected agent writes this field — so it is
        /// rendered subordinate to, and never in place of, the command being approved.
        /// </param>
        /// <param name="viewDiff">
        /// For an edit request with a diff and a host diff viewer, a command that opens the native diff
        /// (old-on-disk vs proposed) for review before approving; null when there's nothing to show
        /// (e.g. a command request, or a host with no diff viewer) so the button stays hidden.
        /// </param>
        /// <param name="origin">
        /// The title of the conversation this request belongs to when that is NOT the one on screen
        /// (issue #256): the live session goes on working while the user reads another conversation,
        /// and its row is being recorded there rather than drawn here. Null in the ordinary case, and
        /// the sentence is then not shown at all.
        /// </param>
        /// <param name="originDeleted">
        /// True when <paramref name="origin"/> names a conversation the user has DELETED while its
        /// session went on running (pre-release security review, September 2026): the sentence then says so, because "still
        /// running, recorded in that conversation" would be a lie about a conversation that no
        /// longer exists.
        /// </param>
        /// <param name="subagentTitle">
        /// The title of the row that launched the sub-agent this request belongs to, when the
        /// request's own row is nested under one (Claude and Kiro v3 both name a parent call). Null
        /// for the main agent's own work, and when no row has arrived yet to climb.
        /// </param>
        /// <param name="workspaceRoot">
        /// The agent's root, to show a file-scoped request's path relative to it. The absolute
        /// path stays on the tooltip. Null shows the path as it came.
        /// </param>
        public PermissionBannerViewModel(
            PermissionRequestDto request,
            Action<string, string?, string?, bool, string?> decide,
            string? intent = null,
            RelayCommand? viewDiff = null,
            string? origin = null,
            bool originDeleted = false,
            string? subagentTitle = null,
            string? workspaceRoot = null)
        {
            _decide = decide;
            Origin = string.IsNullOrWhiteSpace(origin) ? null
                : originDeleted ? DeletedOriginSentence(origin!)
                : OriginSentence(origin!);
            // Which agent in the conversation is asking (a follow-on from the same review): the launching row by
            // name where parentage is known (Claude, Kiro v3), or "a sub-agent" where the only fact
            // is that the request came on a sub-agent's own session (Kiro v2). Nothing for the main
            // agent's own work, which is the common case and needs no sentence.
            Subagent = !string.IsNullOrWhiteSpace(subagentTitle) ? SubagentSentence(subagentTitle!)
                : request.FromSubagentSession ? UnnamedSubagentSentence
                : null;
            // The tool call this prompt is for — lets the chat correlate the banner to its transcript
            // row (highlight + expand it while the prompt is up). Empty when the request carries none.
            ToolCallId = request.ToolCallId;
            // Collapse a multi-line command title to a one-line header (the full command shows in the
            // DisplayDetail box below), matching the transcript tool row.
            Title = ChatViewModel.CollapseTitle(request.Title);
            Detail = request.Detail;

            // Command requests let the user turn an "always allow" into an editable glob (e.g.
            // "git status" -> "git *") instead of "always allow ANY command". A non-null Command is
            // exactly what marks this as a command request. File-scoped requests (a non-null Path on
            // a non-command) get the same treatment with a path glob, pre-filled with the file and
            // widenable to its folder via the scope shortcuts.
            IsCommand = !string.IsNullOrEmpty(request.Command);
            HasPathScope = !IsCommand && !string.IsNullOrEmpty(request.Path);
            // THE FILE, always, for a file-scoped request (pre-release security review, September 2026). Before it the path
            // lived only inside the "Allow always" pattern box, and the body showed nothing for an
            // edit because its change goes to the diff viewer - which exists only when a transcript
            // row correlates. Off screen there is no row, and Kiro v3 titles a whole-file write
            // "Write File", so nothing on the decision surface named the file. A command request
            // always shows its command; this is the same rule for a path.
            TargetPath = HasPathScope ? WorkspacePath.Relative(request.Path!, workspaceRoot) : null;
            TargetPathTooltip = HasPathScope ? request.Path : null;
            // An MCP tool request has neither subject — its subject is the tool itself. Its "always"
            // still gets the second step, for the persist checkbox alone: without one the durable
            // option is unreachable and "Always allow build_solution" dies with the session (#129).
            // ANY MCP tool, not only ours: both backends namespace the name, so a foreign tool's rule
            // can keep its server and is then just as safe to store — the restriction to our own
            // catalog only ever existed to stop BARE names colliding across servers. What the step
            // shows is the rule that will be saved, server included for ours too
            // (code-wicket/build_solution), so the banner never hides which server is being trusted.
            var isKnownTool = IdeMcpServer.TryNormalizeToolRule(request.ToolName, out var ruleName);
            HasToolScope = !IsCommand && !HasPathScope && isKnownTool;
            ToolName = HasToolScope ? request.ToolName : null;
            // And the TITLE is that same name, as the transcript row's is: one call, one name, on every
            // backend. Only for a tool-scoped request — a command (whose title is the model's text)
            // or a file keeps the backend's title — and with the backend's own spelling kept, first in
            // the detail box below, since that is what its log and the agent call the tool.
            if (HasToolScope)
            {
                Title = ruleName;
                RawToolName = request.ToolName!.Trim();
            }
            // ESCAPED for a command (pre-release security review, September 2026). The command is the agent's
            // text, and a wildcard in it - `git add *`, `find . -name "*.cs"` - became a wildcard in
            // the rule the moment the user accepted the default, so approving one literal command
            // approved a family the agent had chosen the shape of. Seeded escaped, the default rule is
            // exactly the command on screen, and widening to a glob is the user's own edit. A path
            // cannot carry a wildcard on Windows and is seeded as it is.
            _pattern = IsCommand ? PermissionGlob.Escape(request.Command!) : HasPathScope ? request.Path : HasToolScope ? ruleName : null;

            // Set when the shell policy matched an AlwaysPromptCommands caution pattern: the exact text
            // that matched, so we can call it out in red, strip "Allow always", and require an extra
            // confirm before a one-time Allow. Null = an ordinary (unflagged) request.
            FlaggedFragment = request.FlaggedFragment;
            FlaggedLabel = "\u26A0 " + (request.FlaggedReason ?? DefaultFlaggedReason) + ": ";
            RuleNote = string.IsNullOrWhiteSpace(request.RuleNote) ? null : request.RuleNote;

            Intent = intent;
            ViewDiffCommand = viewDiff;

            // What the banner *shows* in its body: a command shows the resolved command string (real
            // newlines, monospace, scrollable) so a long/multi-line script is legible; an EDIT shows
            // nothing here — its change goes to the native diff viewer ("View diff"), and the raw
            // rawInput JSON is unreadable (the intent is surfaced above instead); any other kind shows
            // the raw tool input. Distinct from Pattern (the editable "always" glob).
            DisplayDetail = IsCommand ? request.Command
                : HasPathScope ? null
                : RawToolName is null ? request.Detail
                : string.IsNullOrEmpty(request.Detail) ? "tool: " + RawToolName
                : "tool: " + RawToolName + "\n\n" + request.Detail;

            if (HasPathScope)
                ScopeChoices = BuildScopeChoices(request.Path!);

            // A backend can send more options than the standard four (Kiro adds a second allow_always,
            // "Allow all for this session") — the ACP layer de-duplicates by kind before we get here
            // (issue #43), so there's one option per kind to render.
            Options = request.Options
                // A flagged (sensitive) request strips "Allow always" entirely — a caution pattern must
                // never be blanket-allowed; the most you can do is allow it once (through the confirm gate).
                .Where(o => !(IsFlagged && string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase)))
                .Select(o =>
                {
                    var isAllowOnce = string.Equals(o.Kind, "AllowOnce", StringComparison.OrdinalIgnoreCase);
                    var isAllowAlways = string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase);
                    // A scoped request's "always" — allow OR deny — opens the editable-scope step;
                    // everything else resolves now. Deny-always is Kiro-only (Claude never sends it),
                    // and its scope step is session-only (no "save permanently"; see IsDenyEdit).
                    var isDeny = string.Equals(o.Kind, "RejectAlways", StringComparison.OrdinalIgnoreCase);
                    var isAlways = isDeny || isAllowAlways;
                    // A tool's step is offered for ALLOW only: a deny "always" is session-only everywhere
                    // (there is no permanent-deny store), so a step whose sole purpose is the persist
                    // checkbox would be an empty ceremony — and a hidden-checkbox variant of it reads as
                    // a permanent rule that isn't one.
                    var isScopedAlways = ((IsCommand || HasPathScope) && isAlways)
                        || (HasToolScope && isAllowAlways);
                    RelayCommand command =
                        // On a flagged request, allowing (once) routes through a confirm step instead of
                        // resolving straight away — the anti-fat-finger gate.
                        IsFlagged && isAllowOnce ? new RelayCommand(() => EnterAllowConfirm(o.OptionId))
                        : isScopedAlways ? new RelayCommand(() => EnterAlwaysEdit(o.OptionId, isDeny))
                        : new RelayCommand(() => _decide(o.OptionId, null, null, false, null));
                    // Emphasise (accent-fill) the safe default: allow normally, but DENY on a flagged
                    // request so the blue never steers a reflexive Allow on a sensitive command.
                    var emphasized = IsFlagged
                        ? string.Equals(o.Kind, "RejectOnce", StringComparison.OrdinalIgnoreCase)
                        : (isAllowOnce || isAllowAlways);
                    return new PermissionOptionViewModel(o.Label, o.Kind, command, emphasized);
                })
                .ToList();

            ConfirmAlwaysCommand = new RelayCommand(() => _decide(
                _alwaysOptionId!,
                IsCommand ? Pattern : null,
                HasPathScope ? Pattern : null,
                // Deny rules are session-only — never persist one even if the (hidden) box lingered set.
                !IsDenyEdit && PersistRemembered,
                // The namespaced name, not the displayed bare one: the policy unwraps it itself, and
                // doing it here would leave the shell trusting a name it never verified was ours.
                HasToolScope ? ToolName : null));
            CancelAlwaysCommand = new RelayCommand(() => IsEditingAlways = false);

            // The flagged-allow confirm gate: a one-time allow with no remembered rule.
            ConfirmFlaggedAllowCommand = new RelayCommand(() => _decide(_confirmOptionId!, null, null, false, null));
            CancelFlaggedAllowCommand = new RelayCommand(() => IsConfirmingAllow = false);
        }

        /// <summary>The id of the tool call this prompt is for, to correlate it to its transcript row.</summary>
        public string ToolCallId { get; }

        public string Title { get; }
        public string? Detail { get; }

        /// <summary>The full command/tool-input text rendered in the banner body (see the ctor).</summary>
        public string? DisplayDetail { get; }

        /// <summary>
        /// The agent's stated reason for this call, shown as a subtitle (null hides it). Any request
        /// kind, not only an edit — see the constructor for why it is kept subordinate to the command.
        /// </summary>
        public string? Intent { get; }
        public bool HasIntent => !string.IsNullOrWhiteSpace(Intent);

        /// <summary>
        /// Says which conversation asked, when it is not the one on screen (issue #256); null hides it.
        /// The wording is behaviour: it has to say both that the work is still running and where its
        /// answer will be, because neither is visible from the transcript the banner is sitting over.
        /// </summary>
        public string? Origin { get; }
        public bool HasOrigin => Origin is not null;

        /// <summary>The sentence <see cref="Origin"/> carries, pure so its wording can be asserted.</summary>
        public static string OriginSentence(string conversationTitle) =>
            "Asked by \u201c" + conversationTitle + "\u201d, which is still running \u2014 its reply is recorded in that conversation, not this one.";

        /// <summary>
        /// The origin sentence for a conversation the user DELETED while its session ran on.
        /// Says what is true of it now: still running, recorded nowhere, shown nowhere.
        /// </summary>
        public static string DeletedOriginSentence(string conversationTitle) =>
            "Asked by \u201c" + conversationTitle + "\u201d, a conversation you deleted that is still running \u2014 nothing it does is recorded or shown any more.";

        /// <summary>Which sub-agent is asking, by the title of the row that launched it.</summary>
        public static string SubagentSentence(string launchTitle) =>
            "Asked by a sub-agent, launched as \u201c" + launchTitle + "\u201d.";

        /// <summary>The v2 form: the request came on a sub-agent's own session, and that is all that is known.</summary>
        public const string UnnamedSubagentSentence = "Asked by a sub-agent this conversation launched.";

        /// <summary>Which agent within the conversation is asking, or null for the main agent's own work.</summary>
        public string? Subagent { get; }

        public bool HasSubagent => Subagent is not null;

        /// <summary>
        /// The file a file-scoped request targets, relative to the agent's root where it is under it.
        /// Shown whether or not a transcript row correlates - that row is the thing an
        /// off-screen request does not have.
        /// </summary>
        public string? TargetPath { get; }

        /// <summary>The absolute path behind <see cref="TargetPath"/>, for the tooltip.</summary>
        public string? TargetPathTooltip { get; }

        public bool HasTargetPath => TargetPath is not null;

        /// <summary>Opens the native diff for an edit prompt (old-on-disk vs proposed); null when unavailable.</summary>
        public RelayCommand? ViewDiffCommand { get; }
        public bool CanViewDiff => ViewDiffCommand is not null;

        public IReadOnlyList<PermissionOptionViewModel> Options { get; }

        /// <summary>True when this is a shell-command request (so "always" offers the editable scope step).</summary>
        public bool IsCommand { get; }

        /// <summary>True when this is a file-scoped (edit) request with a resolved path — "always"
        /// offers a path glob with file/folder scope shortcuts.</summary>
        public bool HasPathScope { get; }

        /// <summary>True when this is an MCP tool request (no command, no path) — "always allow" offers
        /// the tool by name, with the option to keep it permanently. Any server's tool, shown and saved as
        /// <c>server/tool</c>, ours included (<c>code-wicket/build_solution</c>).</summary>
        public bool HasToolScope { get; }

        /// <summary>The namespaced tool name a tool "always" remembers; null for every other request.</summary>
        public string? ToolName { get; }

        /// <summary>
        /// The backend's own spelling of the tool (<c>mcp__code-wicket__run_tests</c>) when <see cref="Title"/>
        /// shows the normalized <c>server/tool</c>; first line of <see cref="DisplayDetail"/>. Null otherwise.
        /// </summary>
        public string? RawToolName { get; }

        /// <summary>
        /// Whether the "always" step's subject can be edited. A command or path is a GLOB the user may
        /// widen; a tool name is not — matching is exact, so an edited name would silently match nothing
        /// (or, with a wildcard, something never approved).
        /// </summary>
        public bool IsPatternReadOnly => HasToolScope;

        /// <summary>Quick scope choices for a path-scoped "always" (This file / This folder); empty
        /// for command requests, whose scope is typed directly.</summary>
        public IReadOnlyList<ScopeChoiceViewModel> ScopeChoices { get; } = Array.Empty<ScopeChoiceViewModel>();

        public bool HasScopeChoices => ScopeChoices.Count > 0;

        /// <summary>The caption above the editable glob, matched to what the rule will govern (allow vs
        /// deny, command vs edit).</summary>
        public string AlwaysLabel => IsDenyEdit
            ? (IsCommand ? "Always deny commands matching:" : "Always deny edits to files matching:")
            : HasToolScope ? "Always allow this MCP tool:"
            : (IsCommand ? "Always allow commands matching:" : "Always allow edits to files matching:");

        /// <summary>True while the scope step is editing a DENY rule (opened from "Deny always").
        /// Session-only, so the "save permanently" option is hidden (see <see cref="CanPersist"/>).</summary>
        public bool IsDenyEdit
        {
            get => _isDenyEdit;
            private set
            {
                if (SetProperty(ref _isDenyEdit, value))
                {
                    OnPropertyChanged(nameof(AlwaysLabel));
                    OnPropertyChanged(nameof(CanPersist));
                }
            }
        }

        /// <summary>Whether the "save permanently" checkbox is offered. Allow rules can persist to config;
        /// deny rules are session-only (there is no permanent-deny store), so it's hidden for a deny.</summary>
        public bool CanPersist => !IsDenyEdit;

        /// <summary>The exact text an <c>AlwaysPromptCommands</c> caution pattern matched, or null when the
        /// request wasn't flagged. Drives the red call-out, the stripped "Allow always", and the confirm gate.</summary>
        public string? FlaggedFragment { get; }

        /// <summary>True when this request matched a sensitive (always-prompt) pattern.</summary>
        public bool IsFlagged => !string.IsNullOrEmpty(FlaggedFragment);

        /// <summary>
        /// The sentence in front of <see cref="FlaggedFragment"/> in the red call-out. The policy may
        /// supply its own (<c>PermissionRequest.FlaggedReason</c>) when the flag is not the user's
        /// always-prompt rule — a write into the extension's own settings, say — because the default
        /// sentence names a rule the user would then go looking for and not find.
        /// </summary>
        public string FlaggedLabel { get; }

        /// <summary>What the call-out says when the policy gave no reason: the flag was the user's own rule.</summary>
        public const string DefaultFlaggedReason = "Matches your always-prompt rule";

        /// <summary>
        /// One sentence from the policy when an allow rule the user can see in their settings did not
        /// decide this request - a wildcard command rule withheld over a shell operator (pre-release
        /// security review). Plain text under the title: this is the ORDINARY banner, not the flagged one,
        /// so nothing here is red and "Allow always" stays offered.
        /// </summary>
        public string? RuleNote { get; }

        public bool HasRuleNote => RuleNote is not null;

        /// <summary>
        /// The line under an editable COMMAND glob saying what a wildcard grants and what it never
        /// does. Only for a command allow rule: a path cannot carry an operator, a deny rule has no
        /// refusal to explain, and a tool rule has no glob.
        /// </summary>
        public string? PatternHint => IsCommand && !IsDenyEdit ? ShellOperators.WideningHint : null;

        public bool HasPatternHint => PatternHint is not null;

        /// <summary>Whether the command/tool-input box shows at all (has content; not an edit).</summary>
        public bool HasCommandDisplay => DisplayDetail is not null;

        /// <summary>Within the shared command box, show the plain selectable TextBox (ordinary requests).</summary>
        public bool ShowCommandBox => HasCommandDisplay && !IsFlagged;

        /// <summary>Within the shared command box, show the highlight-capable TextBlock instead, with the
        /// matched fragment called out inline (a flagged command request).</summary>
        public bool ShowHighlightedCommand => HasCommandDisplay && IsFlagged;

        /// <summary>True once the user clicked "always" on a scoped request: the editable scope step is shown.</summary>
        public bool IsEditingAlways
        {
            get => _isEditingAlways;
            private set { if (SetProperty(ref _isEditingAlways, value)) OnPropertyChanged(nameof(ShowOptions)); }
        }

        /// <summary>True while a flagged request's "allow once" is awaiting the extra confirm.</summary>
        public bool IsConfirmingAllow
        {
            get => _isConfirmingAllow;
            private set { if (SetProperty(ref _isConfirmingAllow, value)) OnPropertyChanged(nameof(ShowOptions)); }
        }

        /// <summary>Whether the option buttons show — hidden while in the scope-edit or confirm sub-steps.</summary>
        public bool ShowOptions => !IsEditingAlways && !IsConfirmingAllow;

        /// <summary>Confirms a flagged request's one-time allow (no rule remembered).</summary>
        public RelayCommand ConfirmFlaggedAllowCommand { get; }

        /// <summary>Backs out of the flagged-allow confirm step to the option buttons.</summary>
        public RelayCommand CancelFlaggedAllowCommand { get; }

        /// <summary>
        /// The glob remembered when the user confirms "always" — a command glob for command requests,
        /// a path glob for file-scoped ones. Two-way bound to the editable field so the user can
        /// widen/narrow the scope; the scope shortcuts rewrite it.
        /// </summary>
        public string? Pattern
        {
            get => _pattern;
            set => SetProperty(ref _pattern, value);
        }

        /// <summary>When true, a confirmed "always" rule is persisted durably rather than session-only.</summary>
        public bool PersistRemembered { get; set; }

        /// <summary>Confirms the (possibly-edited) "always" rule.</summary>
        public RelayCommand ConfirmAlwaysCommand { get; }

        /// <summary>Backs out of the "always" step to the option buttons.</summary>
        public RelayCommand CancelAlwaysCommand { get; }

        private void EnterAlwaysEdit(string optionId, bool isDeny)
        {
            _alwaysOptionId = optionId;
            IsDenyEdit = isDeny;
            IsEditingAlways = true;
        }

        private void EnterAllowConfirm(string optionId)
        {
            _confirmOptionId = optionId;
            IsConfirmingAllow = true;
        }

        // The two natural path scopes, mirroring Kiro's own trust choices (Specific paths / Complete
        // directory): the exact file, or everything under its folder.
        private List<ScopeChoiceViewModel> BuildScopeChoices(string path)
        {
            var choices = new List<ScopeChoiceViewModel>
            {
                new ScopeChoiceViewModel("This file", new RelayCommand(() => Pattern = path)),
            };

            string? dir = null;
            try { dir = System.IO.Path.GetDirectoryName(path); } catch (ArgumentException) { }
            if (!string.IsNullOrEmpty(dir))
            {
                var folderGlob = dir + System.IO.Path.DirectorySeparatorChar + "*";
                choices.Add(new ScopeChoiceViewModel("This folder", new RelayCommand(() => Pattern = folderGlob)));
            }

            return choices;
        }
    }

    /// <summary>A quick scope shortcut in the "always" step that rewrites the editable glob.</summary>
    public sealed class ScopeChoiceViewModel
    {
        public ScopeChoiceViewModel(string label, RelayCommand command)
        {
            Label = label;
            Command = command;
        }

        public string Label { get; }
        public RelayCommand Command { get; }
    }

    /// <summary>A permission-mode choice in the header dropdown (Id is the Core mode name).</summary>
    public sealed class PermissionModeOption
    {
        public PermissionModeOption(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }
        public string DisplayName { get; }
    }

    /// <summary>One selectable response button in a <see cref="PermissionBannerViewModel"/>.</summary>
    public sealed class PermissionOptionViewModel
    {
        public PermissionOptionViewModel(string label, string kind, RelayCommand command, bool emphasized)
        {
            Label = label;
            Kind = kind;
            Command = command;
            IsEmphasized = emphasized;
        }

        public string Label { get; }
        public string Kind { get; }
        public RelayCommand Command { get; }

        /// <summary>Which button gets the accent (filled) treatment — the safe default to steer toward.
        /// Normally the allow-style option; on a FLAGGED (sensitive) request it's Deny instead, so the
        /// blue emphasis never invites a reflexive Allow. Set by the banner (which knows the flag).</summary>
        public bool IsEmphasized { get; }

        /// <summary>
        /// A concise, backend-uniform button label derived from the ACP option kind ("Allow",
        /// "Allow always", "Deny", "Deny (this session)"); falls back to the agent's own verbose
        /// <see cref="Label"/> for an unrecognised kind. The full agent label is kept as the tooltip,
        /// so any scope detail Claude bakes into it (addRules/addDirectories) isn't lost. "Deny always"
        /// reads "Deny (this session)" because we enforce every deny as session-scoped and client-owned
        /// (both the real Kiro reject_always and our synthesized one — see AcpMapper).
        /// </summary>
        public string DisplayLabel =>
            string.Equals(Kind, "AllowOnce", StringComparison.OrdinalIgnoreCase) ? "Allow" :
            string.Equals(Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase) ? "Allow always" :
            string.Equals(Kind, "RejectOnce", StringComparison.OrdinalIgnoreCase) ? "Deny" :
            string.Equals(Kind, "RejectAlways", StringComparison.OrdinalIgnoreCase) ? "Deny (this session)" :
            Label;

        /// <summary>True for allow-style options, so the banner can emphasise them.</summary>
        public bool IsAllow =>
            string.Equals(Kind, "AllowOnce", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase);
    }
}
