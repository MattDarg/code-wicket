using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CodeWicket.UI.Mvvm;
using CodeWicket.Core.Ide;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>Base for everything shown in the chat transcript.</summary>
    public abstract class ChatItemViewModel : ObservableObject
    {
        private bool _isHighlighted;
        private CodeWicket.Core.PermissionOutcome? _permission;
        private bool _permissionSettled;

        protected ChatItemViewModel() => CopyCommand = new RelayCommand(Copy);

        /// <summary>
        /// True while a pending permission prompt refers to this row: the tool/edit templates draw an
        /// accent outline so the user can see which transcript entry they're being asked to approve.
        /// Transient UI state driven by the banner (see <c>ChatViewModel</c>'s permission highlight);
        /// never persisted or replayed.
        /// </summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => SetProperty(ref _isHighlighted, value);
        }

        /// <summary>
        /// How this call came to be permitted, or null for "no record" once <see cref="PermissionSettled"/>
        /// is true. Lives on the base rather than the tool row because an EDIT card is a permission target
        /// too - the banner already highlights either - so the data is in one place even though only the
        /// tool row draws chrome for it today.
        /// </summary>
        public CodeWicket.Core.PermissionOutcome? Permission
        {
            get => _permission;
            set
            {
                if (!SetProperty(ref _permission, value))
                    return;
                OnPropertyChanged(nameof(PermissionGlyph));
                OnPropertyChanged(nameof(HasPermissionGlyph));
                OnPropertyChanged(nameof(PermissionGlyphTone));
                OnPropertyChanged(nameof(PermissionSummary));
                OnPropertyChanged(nameof(HasPermissionSummary));
                OnPermissionDisplayChanged();
            }
        }

        /// <summary>
        /// True once we know whether a record exists - i.e. the call has finished. Until then the row
        /// shows NOTHING, because "no outcome yet" and "no outcome recorded" are different states and a
        /// running row must not claim the second. Set when the completion is applied, live or replayed.
        /// </summary>
        public bool PermissionSettled
        {
            get => _permissionSettled;
            set
            {
                if (!SetProperty(ref _permissionSettled, value))
                    return;
                OnPropertyChanged(nameof(PermissionGlyph));
                OnPropertyChanged(nameof(HasPermissionGlyph));
                OnPropertyChanged(nameof(PermissionGlyphTone));
                OnPropertyChanged(nameof(PermissionSummary));
                OnPropertyChanged(nameof(HasPermissionSummary));
                OnPermissionDisplayChanged();
            }
        }

        /// <summary>
        /// Hook for a subclass whose own chrome depends on the permission sentence existing — the edit
        /// card's chevron, which must appear for a settled call that has nothing else to show, since the
        /// sentence is the only thing under it. On the base so both setters raise it: the two arrive in
        /// either order and whichever lands second is the one that completes the sentence.
        /// </summary>
        protected virtual void OnPermissionDisplayChanged()
        {
        }

        /// <summary>
        /// The at-a-glance mark, or null for no mark. Deliberately absent for the COMMONEST state (the
        /// backend never asked): a glyph on every read in a 26-call fan-out would carry no information
        /// and cost a realisation each. Silence therefore means one thing and one thing only, which is
        /// the whole point - so the rare "we have no record" case gets an explicit mark instead.
        /// </summary>
        public string? PermissionGlyph => _permission?.Kind switch
        {
        // FOUR marks, because the sentence carries everything finer. The glyph answers only "do I need to
        // look at this row?", and the four answers worth having at a glance are: you decided, it was
        // refused, it was allowed without asking you, and we cannot say. WHICH automatic thing allowed it
        // - a saved rule, a rule you made this session, or the mode's ceiling - is a distinction the mark
        // cannot carry legibly and does not need to: expanding the row says it in words. So all three
        // share one mark rather than being spelled out in glyphs a reader has to learn.
        //
        // U+00BB, never a lightning bolt: U+26A1 has an emoji presentation, so which font serves it - and
        // whether it lands monochrome or as a colour glyph beside monochrome chrome - is a per-machine
        // accident. The guillemet is plain Segoe UI everywhere, has no emoji variant, and reads as "waved
        // through", which fits a rule and a mode ceiling equally (U+00A7 read as "by rule" and so quietly
        // mis-described the mode, which is not a rule and is not in anyone's allowedCommands).
            CodeWicket.Core.PermissionOutcomeKind.UserAllowed => "\u2713",
            CodeWicket.Core.PermissionOutcomeKind.UserDenied => "\u2717",
            CodeWicket.Core.PermissionOutcomeKind.RuleAllowed => "\u00bb",
            CodeWicket.Core.PermissionOutcomeKind.RuleDenied => "\u2717",
            CodeWicket.Core.PermissionOutcomeKind.ModeAllowed => "\u00bb",
            CodeWicket.Core.PermissionOutcomeKind.NotRequested => null,
        // U+2013 for "no record", never a question mark: this state is EXPECTED - an older log, or
        // history replayed from a resumed CLI session - and a "?" in warning colour reads as a fault,
        // which sends the reader looking for a break that isn't there. A dash says "nothing to report
        // here" without asserting a problem. En dash rather than a hyphen so it reads as a deliberate
        // mark at the other glyphs' size, and like them it has no emoji presentation.
            _ => _permissionSettled ? "\u2013" : null,
        };

        public bool HasPermissionGlyph => PermissionGlyph is not null;

        /// <summary>
        /// Which colour the glyph takes, as a token the template matches on. Decided HERE rather than in
        /// XAML triggers so the mapping is unit-testable.
        /// <para>
        /// The two channels carry one axis each: the GLYPH says who decided (you, or policy), the COLOUR
        /// says what happened (it ran, it did not, we cannot say). So both allows are green and both
        /// refusals red - "allowed" is the same outcome however it was reached, and the mark beside it
        /// already says how. <c>Auto</c> stays a separate tone from <c>Allowed</c> even though they
        /// currently resolve to the same brush, because they are different facts and collapsing them here
        /// would mean re-splitting the enum to colour them apart again.
        /// </para>
        /// </summary>
        public string? PermissionGlyphTone => _permission?.Kind switch
        {
            CodeWicket.Core.PermissionOutcomeKind.UserAllowed => "Allowed",
            CodeWicket.Core.PermissionOutcomeKind.UserDenied => "Denied",
            CodeWicket.Core.PermissionOutcomeKind.RuleAllowed => "Auto",
            CodeWicket.Core.PermissionOutcomeKind.RuleDenied => "Denied",
            CodeWicket.Core.PermissionOutcomeKind.ModeAllowed => "Auto",
            CodeWicket.Core.PermissionOutcomeKind.NotRequested => null,
            _ => _permissionSettled ? "Unknown" : null,
        };

        public bool HasPermissionSummary => PermissionSummary is not null;

        /// <summary>
        /// The sentence shown in the expanded row - always present once settled, including for the two
        /// states that draw no glyph, so expanding a row ALWAYS answers "why did this run?".
        /// </summary>
        public string? PermissionSummary
        {
            get
            {
                if (!_permissionSettled && _permission is null)
                    return null;
                if (_permission is null)
                    return "Permission: no record \u2014 an older conversation, or replayed history.";

                var scope = _permission.RulePersisted ? "saved rule" : "rule for this session";
                var rule = string.IsNullOrWhiteSpace(_permission.Rule) ? null : _permission.Rule;
                var text = _permission.Kind switch
                {
                    // Names the PRODUCT, not the IDE (#252): the thing that was not consulted is our
                    // permission policy, and the IDE never had a say in it either way - so "Visual
                    // Studio" here pointed the reader at the wrong component to go and check.
                    CodeWicket.Core.PermissionOutcomeKind.NotRequested =>
                        $"Permission: the agent did not ask \u2014 it decided by its own rules and never consulted {CodeWicket.Core.Branding.ProductName}.",
                    CodeWicket.Core.PermissionOutcomeKind.UserAllowed =>
                        rule is null ? "Permission: you allowed this." : $"Permission: you allowed this, and saved \u201c{rule}\u201d.",
                    CodeWicket.Core.PermissionOutcomeKind.UserDenied =>
                        rule is null ? "Permission: you refused this." : $"Permission: you refused this, and saved \u201c{rule}\u201d.",
                    CodeWicket.Core.PermissionOutcomeKind.RuleAllowed =>
                        $"Permission: allowed automatically by a {scope}" + (rule is null ? "." : $" ({rule})."),
                    CodeWicket.Core.PermissionOutcomeKind.RuleDenied =>
                        $"Permission: refused automatically by a {scope}" + (rule is null ? "." : $" ({rule})."),
                    CodeWicket.Core.PermissionOutcomeKind.ModeAllowed =>
                        $"Permission: allowed by the current permission mode" + (rule is null ? "." : $" ({rule})."),
                    _ => "Permission: unrecognised record.",
                };

                // Live, the banner already makes an always-prompt hit unmissable, so this earns no chrome
                // - but a transcript reopened later has no banner, and "why was I asked when I have an
                // allow rule?" is exactly the question this answers.
                // The reason is the policy's when it gave one (a write into the extension's own
                // settings) and the always-prompt sentence otherwise - a log written before reasons
                // existed carries none, and every caution hit then WAS the user's rule.
                return _permission.CautionPrompted
                    ? text + " You were asked because " + (_permission.CautionReason
                        ?? "it matched an always-prompt rule, which overrides allow rules and the mode.")
                    : text;
            }
        }

        /// <summary>Copies <see cref="CopyText"/> to the clipboard (wired to a context menu / Ctrl+C).</summary>
        public RelayCommand CopyCommand { get; }

        /// <summary>
        /// The row this item is nested inside, or null when it sits directly in the transcript. On the
        /// BASE rather than on the tool row, because an edit CARD nests too — a sub-agent's edit on a
        /// backend that builds no tool row for it has nowhere else to go — and because both kinds are
        /// permission targets, so whatever reveals one must reach the other (issue #125).
        /// </summary>
        public ToolItemViewModel? Parent { get; internal set; }

        /// <summary>
        /// Opens every row between this one and the top, so a nested item can actually be seen. Used
        /// where something outside the transcript points at an item — the permission banner's
        /// highlight — since a highlight on an item inside a collapsed parent highlights nothing.
        /// </summary>
        public void ExpandAncestors()
        {
            // Walks the item AND its ancestor together, because opening a row is no longer enough to
            // reveal a child: a fan-out past the cap draws only its first few, so a banner pointing at
            // call 17 of 26 would open the row onto rows 1-5 and highlight nothing (issue #125). The
            // reveal raises that row's own cap far enough to include the child and no further.
            for (ChatItemViewModel item = this; item.Parent is { } ancestor; item = ancestor)
            {
                ancestor.IsExpanded = true;
                ancestor.EnsureChildVisible(item);
            }
        }

        /// <summary>
        /// The item that stands for this one inside <paramref name="scope"/> — itself when it sits
        /// directly in that scope, its ancestor when it is nested deeper, and <b>null when it is not
        /// inside that scope at all</b>.
        /// </summary>
        /// <remarks>
        /// The transcript draws one list at a time — the conversation at the root, a sub-agent's
        /// <c>Children</c> once drilled into (issue #148) — and everything that scrolls to an item has to
        /// name a member of the list currently on screen. So the question "what do I scroll to" is
        /// scope-relative, and the honest answer for an item in a different scope is <b>none</b>, not the
        /// nearest thing that happens to be showing.
        /// <para>
        /// <see cref="RootAncestor"/> is this with <c>scope: null</c> rather than its own walk, so the
        /// scope-relative and root-relative answers cannot diverge: they are one traversal with one
        /// stopping rule, and a bug fixed in one is fixed in both.
        /// </para>
        /// </remarks>
        public ChatItemViewModel? AncestorWithin(ToolItemViewModel? scope)
        {
            ChatItemViewModel item = this;
            while (true)
            {
                if (ReferenceEquals(item.Parent, scope))
                    return item;
                if (item.Parent is not { } parent)
                    return null;   // walked off the top without meeting scope: not inside it
                item = parent;
            }
        }

        /// <summary>
        /// The top-level item this one hangs under (itself, when it is top-level). The transcript's
        /// <c>Items</c> holds only these, so it is what a scroll-into-view has to be given while the
        /// pane is at the root of its navigation.
        /// </summary>
        public ChatItemViewModel RootAncestor => AncestorWithin(null)!;

        /// <summary>The text this item contributes to the clipboard; null/empty items copy nothing.</summary>
        public virtual string? CopyText => null;

        private void Copy()
        {
            try
            {
                if (!string.IsNullOrEmpty(CopyText))
                    System.Windows.Clipboard.SetText(CopyText);
            }
            catch
            {
                // Clipboard can be transiently locked by another app; copying is best-effort.
            }
        }
    }

    public enum MessageRole
    {
        User,
        Assistant,
        Thinking,
    }

    /// <summary>A user/assistant/thinking message whose text grows as deltas stream in.</summary>
    public sealed class MessageItemViewModel : ChatItemViewModel
    {
        private string _text = string.Empty;

        public MessageItemViewModel(
            MessageRole role,
            string text = "",
            string? deliveryNote = null,
            IReadOnlyList<AttachmentViewModel>? attachments = null,
            IReadOnlyList<ContextItemViewModel>? contexts = null)
        {
            Role = role;
            _text = text;
            DeliveryNote = deliveryNote;
            Attachments = attachments ?? Array.Empty<AttachmentViewModel>();
            Contexts = contexts ?? Array.Empty<ContextItemViewModel>();
        }

        public MessageRole Role { get; }

        /// <summary>
        /// Images sent with this message (issue #118) — always empty on an assistant bubble, since
        /// what the agent sends back is text. Fixed at construction: a message's attachments are part
        /// of what was said, so unlike <see cref="Text"/> they never stream in afterwards.
        /// </summary>
        public IReadOnlyList<AttachmentViewModel> Attachments { get; }

        public bool HasAttachments => Attachments.Count > 0;

        /// <summary>
        /// IDE context attached to this message (issue #73, rung 2) - the call stack and locals the
        /// user handed over. Fixed at construction for the same reason the attachments are: it is part
        /// of what was said.
        /// </summary>
        public IReadOnlyList<ContextItemViewModel> Contexts { get; }

        public bool HasContexts => Contexts.Count > 0;

        /// <summary>
        /// How this message reached the agent, when that was not the ordinary way — and, crucially, what
        /// the agent was told ABOUT it. A mid-turn delivery carries a short framing block the user did
        /// not write (see <c>ChatViewModel.PreambleFor</c>), so this is how that stops being invisible:
        /// the words we put in are ours, and the user is entitled to see that we put them there. Null
        /// for an ordinary send, which carries no framing.
        /// </summary>
        public string? DeliveryNote { get; }

        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public bool IsUser => Role == MessageRole.User;
        public bool IsAssistant => Role == MessageRole.Assistant;
        public bool IsThinking => Role == MessageRole.Thinking;

        public override string? CopyText => Text;

        public void Append(string delta) => Text += delta;
    }

    public enum ToolStatus
    {
        Running,
        Success,
        Failed,

        /// <summary>
        /// Started, not finished — an asynchronous sub-agent whose work runs on after the call that
        /// launched it reported "completed". A third state rather than a reuse of Running, because it
        /// is a different claim: Running says the agent is waiting on this call, Launched says it is
        /// not, and that nobody will tell us when the work ends. No backend reports a sub-agent's
        /// return in a way that names WHICH sub-agent returned, so a row in this state is never settled
        /// by us — settling it would mean guessing, and measured return order is not launch order
        /// (issue #125).
        /// </summary>
        Launched,
    }

    /// <summary>
    /// A tool call shown as a step row; status flips when it completes. Title/kind/detail update in
    /// place: backends may open the call with a placeholder and send the real title and arguments
    /// once the input has streamed in (the "toolUpdate" event).
    /// </summary>
    public sealed class ToolItemViewModel : ChatItemViewModel
    {
        private ToolStatus _status = ToolStatus.Running;
        private string _title;
        private string? _kind;
        private string? _description;
        private string? _inputDetail;
        private string? _outputDetail;
        private string? _errorDetail;
        private bool _isExpanded;
        private bool _isSubagentLaunch;
        private bool _isSessionSetup;
        private string? _diffPath;
        private string _diffOldText = string.Empty;
        private string _diffNewText = string.Empty;
        private int? _diffLine;
        private System.Func<string, string, string, int?, System.Threading.Tasks.Task>? _openDiff;
        // Opens the EDITED file at the change, resolved against the file as it stands now. Only set
        // when a diff folds into this row; the read-target route uses _openFile below.
        private System.Func<string, string, string, int?, System.Threading.Tasks.Task>? _openFileAtDiff;
        private readonly List<(string Path, int Line)> _fileTargets = new();
        // The root BOTH path routes render relative to. Supplied by whichever of AttachFileTargets /
        // TryAttachDiff arrives; a row only ever takes one of the two, and both are given the same
        // AgentPathRoot by the host, so which one set it never matters.
        private string? _fileTargetRoot;
        private System.Func<string, int, System.Threading.Tasks.Task>? _openFile;

        public ToolItemViewModel(string toolCallId, string title, string? kind)
        {
            ToolCallId = toolCallId;
            _title = title;
            _kind = kind;
            OpenDiffCommand = new RelayCommand(OpenDiff, () => CanOpenDiff);
            OpenFileCommand = new RelayCommand(OpenFileTarget, () => CanOpenFile);
            CopyPathCommand = new RelayCommand(CopyPath, () => HasPathToCopy);
        }

        public string ToolCallId { get; }

        public string Title
        {
            get => _title;
            set
            {
                // The target label is "what the title doesn't already say", so a new title re-decides it —
                // Kiro re-sends its generic "Read File" on the completion frame, Claude's enriching update
                // replaces the placeholder with a title that names the file itself.
                if (SetProperty(ref _title, value))
                    RaiseTargetPathChanged();
            }
        }

        public string? Kind
        {
            get => _kind;
            set { if (SetProperty(ref _kind, value)) { OnPropertyChanged(nameof(KindGlyph)); OnPropertyChanged(nameof(KindLabel)); } }
        }

        public ToolStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusGlyph));
                    OnPropertyChanged(nameof(StatusNote));
                    OnPropertyChanged(nameof(HasStatusNote));
                }
            }
        }

        /// <summary>
        /// A word for a state the row's colour cannot carry on its own. Only <see cref="ToolStatus.Launched"/>
        /// has one: running/succeeded/failed are the three every row has always had, and a reader knows
        /// them, but "started and still going, and nobody will tell us when it stops" is a claim no
        /// colour makes — leaving it to the glyph alone gives the user a blue icon and no explanation
        /// (issue #125).
        /// <para>
        /// <b>One word, because it is competing with the title for the same line.</b> Everything else on
        /// this row docks RIGHT — the kind, the call count, this note, the chevron — and the title takes
        /// what is left and trims, so every character here is taken from the one field that says what
        /// the call actually is. "running in background" cost about half the title on a sub-agent row.
        /// The part that had to go is the part a tooltip can hold, so it moved to
        /// <see cref="StatusNoteToolTip"/> rather than being dropped.
        /// </para>
        /// </summary>
        public string? StatusNote => _status != ToolStatus.Launched
            ? null
            : _launchAbandoned ? "unreported"
            : _launchReturned ? "finished"
            : "running";

        private bool _launchReturned;
        private bool _launchAbandoned;

        /// <summary>
        /// Records that this background call has come back — without a result, because the backend never
        /// sends one for it (issue #125).
        /// </summary>
        /// <remarks>
        /// <b>Never called on the strength of one notification.</b> A returning task is announced by a
        /// frame naming no agent, and measured, launches and returns match in NUMBER but not in ORDER,
        /// so settling a row per notification mislabels. The host calls this only once the returns have
        /// caught up with the launches, at which point every outstanding task has finished and this is
        /// true of each row rather than guessed for it.
        /// <para>
        /// The row keeps <see cref="ToolStatus.Launched"/> rather than flipping to
        /// <see cref="ToolStatus.Success"/>: it did not report a result and we are not claiming one. What
        /// changes is only that it stops saying it is still running.
        /// </para>
        /// </remarks>
        public void NoteLaunchReturned()
        {
            if (_status != ToolStatus.Launched || _launchReturned)
                return;

            _launchReturned = true;
            OnPropertyChanged(nameof(StatusNote));
            OnPropertyChanged(nameof(StatusNoteToolTip));
        }

        /// <summary>Whether this launched row has been told its task came back.</summary>
        public bool LaunchReturned => _launchReturned;

        /// <summary>
        /// Records that this background call will NEVER report, because the turn it was launched in was
        /// cancelled.
        /// </summary>
        /// <remarks>
        /// A third state, and it earns its place by being the only honest one. "finished" would claim a
        /// completion nobody received, and leaving it on "running" is what stranded these rows in the
        /// first place. Measured on a live capture (acp.log, 2026-09-08): six background Tasks launched
        /// across two fan-outs with a <c>session/cancel</c> between them produced exactly THREE
        /// <c>task-notification</c> frames, all belonging to the fan-out launched AFTER the cancel. The
        /// three launched before it reported nothing, ever.
        /// <para>
        /// The task itself is not necessarily dead — Claude does not stop a background Task on cancel,
        /// and the user watched one run well past its own duration. What is lost is the REPORT, and it
        /// is lost for everyone: asked whether those tasks had finished, the model was still waiting
        /// too. So this says the result was never reported, and declines to say the work stopped.
        /// </para>
        /// <para>
        /// Only a CANCEL may call this. A turn ending normally must not, because a background task can
        /// legitimately return after it — that is measured, and it is the whole reason the outstanding
        /// count is not turn-scoped.
        /// </para>
        /// </remarks>
        public void NoteLaunchAbandoned()
        {
            if (_status != ToolStatus.Launched || _launchReturned || _launchAbandoned)
                return;

            _launchAbandoned = true;
            OnPropertyChanged(nameof(StatusNote));
            OnPropertyChanged(nameof(StatusNoteToolTip));
        }

        /// <summary>Whether this launched row was stranded by a cancelled turn.</summary>
        public bool LaunchAbandoned => _launchAbandoned;

        /// <summary>
        /// What the shortened note no longer says: this row will never settle. Not a restatement of the
        /// word — the backend announces the returning report on a frame that names no agent, so there
        /// is nothing to match it against and the row stays as it is (issue #125).
        /// </summary>
        public string StatusNoteToolTip => _launchAbandoned
            ? "The turn was stopped before this background task reported. The backend sends no "
              + "completion for a stopped turn's tasks, so no result will arrive — for this row or for "
              + "the agent. The task itself may still be running."
            : _launchReturned
            ? "This ran in the background and has come back. The backend doesn't report which task each "
              + "result belongs to, so this row can't show what it returned."
            : "Running in the background. The backend doesn't report which task finished, so this row "
              + "settles only once every background call of this conversation has come back.";

        public bool HasStatusNote => StatusNote is not null;

        /// <summary>
        /// The agent's stated intent for this call (Claude's <c>rawInput.description</c> / Kiro's
        /// <c>__tool_use_purpose</c>), shown de-emphasized under the title. Null for agents that send no
        /// recognised intent field (a generic ACP backend); the row then just shows its title.
        /// </summary>
        public string? Description
        {
            get => _description;
            set { if (SetProperty(ref _description, value)) OnPropertyChanged(nameof(HasDescription)); }
        }

        /// <summary>Whether an intent subtitle should render (only when non-empty).</summary>
        public bool HasDescription => !string.IsNullOrEmpty(_description);

        /// <summary>The tool's arguments, rendered readable (shown first in the expandable detail).</summary>
        public string? InputDetail
        {
            get => _inputDetail;
            set { if (SetProperty(ref _inputDetail, value)) RaiseDetailChanged(); }
        }

        /// <summary>The tool's progress/result text (a command's stdout; shown after the input in the detail).</summary>
        public string? OutputDetail
        {
            get => _outputDetail;
            set { if (SetProperty(ref _outputDetail, value)) RaiseDetailChanged(); }
        }

        /// <summary>
        /// Whether <see cref="OutputDetail"/> currently holds live streamed output (appended chunks
        /// from a still-running call). Guards the progress-status path: a bare status string
        /// ("in_progress") must not clobber real output the user is watching.
        /// </summary>
        public bool HasLiveOutput { get; private set; }

        // Keep the live tail bounded: a chatty command (a build, a verbose test run) can stream far
        // more than a chat row should hold. The completion's authoritative result replaces this anyway.
        private const int MaxLiveOutputChars = 4000;
        private const string LiveTrimNotice = "… (earlier output trimmed)\n";

        private string _liveOutputBuffer = string.Empty; // accumulated tail, without the trim notice
        private bool _liveOutputTrimmed;

        /// <summary>
        /// Appends a chunk of live output from the still-running call (a shell command's
        /// stdout/stderr streamed as it happens), so the expanded row shows the command working
        /// instead of nothing until completion. Tail-capped; ignored after completion (the
        /// completion's result text is authoritative and must not be appended to).
        /// </summary>
        public void AppendLiveOutput(string chunk)
        {
            if (_status != ToolStatus.Running || string.IsNullOrEmpty(chunk))
                return;

            _liveOutputBuffer = _liveOutputBuffer.Length == 0 ? chunk : _liveOutputBuffer + "\n" + chunk;
            if (_liveOutputBuffer.Length > MaxLiveOutputChars)
            {
                _liveOutputBuffer = _liveOutputBuffer.Substring(_liveOutputBuffer.Length - MaxLiveOutputChars);
                _liveOutputTrimmed = true;
            }

            HasLiveOutput = true;
            OutputDetail = _liveOutputTrimmed ? LiveTrimNotice + _liveOutputBuffer : _liveOutputBuffer;
        }

        /// <summary>
        /// The tool's error stream (a command's stderr), shown as its own section below the result
        /// (a separate stream, styled as an error). Null when there's none.
        /// </summary>
        public string? ErrorDetail
        {
            get => _errorDetail;
            set { if (SetProperty(ref _errorDetail, value)) { OnPropertyChanged(nameof(HasErrorDetail)); RaiseDetailChanged(); } }
        }

        /// <summary>Whether a stderr section should render.</summary>
        public bool HasErrorDetail => !string.IsNullOrEmpty(_errorDetail);

        private string? _rawToolName;

        /// <summary>
        /// The backend's own name for an MCP call — <c>mcp__code-wicket__build_solution</c>,
        /// <c>@code_wicket/build_solution</c> — when <see cref="Title"/> shows the normalized
        /// <c>server/tool</c> instead; null when the title is the backend's own. Kept as the first line of
        /// the detail, because it is what the backend's log, and the agent itself, call the tool.
        /// </summary>
        public string? RawToolName
        {
            get => _rawToolName;
            set
            {
                if (!SetProperty(ref _rawToolName, value))
                    return;
                RaiseDetailChanged();
                OnPropertyChanged(nameof(IsMcpCall));
                OnPropertyChanged(nameof(KindGlyph));
                OnPropertyChanged(nameof(KindLabel));
            }
        }

        /// <summary>
        /// The title the backend last sent, collapsed to one line. What <see cref="Title"/> goes back to
        /// when a later frame shows the call to be a shell command after all.
        /// </summary>
        public string BackendTitle { get; set; } = string.Empty;

        /// <summary>
        /// Set once any frame of this call carried a SHELL FACT — a command kind, or a <c>command</c>
        /// argument. Sticky: such a row never takes a normalized MCP name, whatever a later frame says,
        /// because a shell command can be titled with one of our tools' names (pre-release security review, September 2026).
        /// </summary>
        public bool HasShellFact { get; set; }

        /// <summary>What the expander shows: the backend's tool name when the title is normalized, the input
        /// arguments, then the output/result.</summary>
        public string? Detail
        {
            get
            {
                var input = _rawToolName is null ? _inputDetail
                    : string.IsNullOrEmpty(_inputDetail) ? "tool: " + _rawToolName
                    : "tool: " + _rawToolName + "\n" + _inputDetail;
                return string.IsNullOrEmpty(input) ? _outputDetail :
                    string.IsNullOrEmpty(_outputDetail) ? input :
                    input + "\n\n" + _outputDetail;
            }
        }

        private void RaiseDetailChanged()
        {
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(HasDetail));
            OnPropertyChanged(nameof(CanExpand));
        }

        /// <summary>Whether there's detail to show (so the expander chevron only appears when useful).</summary>
        public bool HasDetail => !string.IsNullOrEmpty(Detail) || HasErrorDetail;

        /// <summary>Detail is collapsed by default (vertical economy); the chevron toggles this.</summary>
        /// <remarks>
        /// <b>A closed row holds NOTHING, and this property owns that</b> (issue #125). The window is
        /// built when the row opens and dropped when it closes, rather than being maintained whatever
        /// the row is doing. Two things follow, and the second is the one that was costing.
        /// <para>
        /// <b>The cap goes back.</b> A raised cap makes the row expensive for as long as it stays open,
        /// and it can be raised WITHOUT the user asking — a permission banner naming a call older than
        /// the window raises it as a side effect. Left sticky, that silently leaves a permanently costly
        /// row behind a prompt the user merely answered. The cost of being wrong about a deliberate
        /// "Show all" is one click, on the rare path.
        /// </para>
        /// <para>
        /// <b>And a closed row stops maintaining a window it is not drawing.</b> A fan-out streams into a
        /// row that is closed — that is the whole point of nesting — and every call arriving used to
        /// slide the window, so twenty-six calls cost fifty-two collection notifications against a
        /// control generating nothing from them. Building on open costs one rebuild in exchange, at the
        /// moment the user asked to see something, and nothing is lost: what open rebuilds is exactly
        /// what close threw away.
        /// </para>
        /// </remarks>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (!SetProperty(ref _isExpanded, value))
                    return;

                if (value)
                {
                    SyncVisibleChildren(batched: true);
                    return;
                }

                _shownChildren = ChildCap;
                _drawnFrom = Children.Count;
                VisibleChildren.ReplaceAll(Array.Empty<ChatItemViewModel>());
                OnPropertyChanged(nameof(HasHiddenChildren));
                OnPropertyChanged(nameof(OpenAllChildrenLabel));
            }
        }

        /// <summary>
        /// The calls a sub-agent made while working for this row (issue #125). Empty for every ordinary
        /// call, which is the overwhelming majority — a row pays nothing for the collection existing.
        /// <para>Children are drawn by an <c>ItemsControl</c> that is COLLAPSED until the row is
        /// expanded, which is what makes a 26-call fan-out cost one line: WPF measures nothing inside a
        /// collapsed element, so no child container is realised until the user asks for them. That
        /// matters more than it sounds — re-realising containers is what forces WPF to regenerate render
        /// data, measured as the dominant UI-thread cost of a scroll (issue #86).</para>
        /// </summary>
        public ObservableCollection<ChatItemViewModel> Children { get; } = new ObservableCollection<ChatItemViewModel>();

        public bool HasChildren => Children.Count > 0;

        /// <summary>
        /// How many children a row draws before it stops (issue #125). Five, because the question a
        /// nested row answers is "what sort of thing was that sub-agent doing" — which five calls
        /// settle — and not "line by line, what did it do", which is rare and now costs a click.
        /// </summary>
        public const int ChildCap = 5;

        // How many children this row is currently drawing. Raised to everything by the overflow
        // control, or just far enough by a reveal (see ExpandAncestors).
        private int _shownChildren = ChildCap;

        // Index in Children of VisibleChildren[0]. Tracked rather than derived: immediately after an
        // append, Children has grown and VisibleChildren has not, so Count-Count names the wrong row.
        private int _drawnFrom;

        /// <summary>
        /// How many children SHOULD be drawn right now.
        /// <para>The cap does not engage until it would hide more than one row: putting a single call
        /// behind "Show all 6 calls" costs a click and saves nothing.</para>
        /// </summary>
        private int EffectiveShown =>
            Children.Count <= ChildCap + 1 ? Children.Count : Math.Min(_shownChildren, Children.Count);

        /// <summary>
        /// The children this row actually draws — the <b>most recent</b> ones that fit under the
        /// cap.
        /// </summary>
        /// <remarks>
        /// <b>A projection, and <see cref="Children"/> stays whole.</b> The cap is a DISPLAY bound and
        /// must never cut DATA (the rule <c>AcpMapper.ClampResult</c> exists for): the export walks
        /// <see cref="Children"/>, the row's own summary counts it, and a reveal indexes into it. A row
        /// that only kept five would silently drop a sub-agent's twenty-first call from the transcript
        /// the user copies.
        /// <para>
        /// Kept as its own <see cref="ObservableCollection{T}"/> rather than a filtered view, because
        /// the <c>ItemsControl</c> that draws it is non-virtualizing: whatever is in here is realised,
        /// measured and re-rendered on every pass over the row, which is the whole cost this bounds.
        /// </para>
        /// <para>
        /// <b>The LAST few, not the first — and the permission path is what decides it.</b> A blocked
        /// call is by definition the most recent one, so under a first-N cap the banner's reveal would
        /// have to raise the cap to the full length of every large fan-out: the cap would hold
        /// everywhere except the one moment the pane is blocked and the user is waiting on it. Drawing
        /// the newest instead means the call being asked about is already on screen and no reveal is
        /// needed at all. Two lesser reasons agree: while a fan-out streams, the newest call is what
        /// says the row is still working (a first-N window looks frozen), and the transcript that
        /// contains this row already follows its own newest content (issue #90), so an oldest-first
        /// window would have contradicted its own container.
        /// </para>
        /// </remarks>
        public BatchedObservableCollection<ChatItemViewModel> VisibleChildren { get; } =
            new BatchedObservableCollection<ChatItemViewModel>();

        /// <summary>
        /// True when the cap is holding back at least one child, so the overflow control shows. False on
        /// a closed row: it draws nothing, which is not the same as hiding something.
        /// </summary>
        public bool HasHiddenChildren => _isExpanded && Children.Count > VisibleChildren.Count;

        /// <summary>
        /// What the overflow control says, e.g. "Open all 26 calls →". Null when nothing is hidden.
        /// </summary>
        /// <remarks>
        /// <b>It navigates; it does not widen the row (issue #148).</b> The control it replaced dropped the
        /// cap in place, which put the whole fan-out back inside one container of a non-virtualizing
        /// <c>ItemsControl</c> — the exact cost the cap exists to bound, reinstated on demand and measured
        /// at ~100x an ordinary row (a single expanded fan-out row at 52-64 ms measure against ~0.5 ms,
        /// with render passes over that one row at 1691-3352 ms). Opening the calls as their own transcript
        /// hands them to the virtualising panel instead, so the unbounded inline case stops existing rather
        /// than being deferred behind a click.
        /// <para>
        /// There is deliberately no command here. Navigation has to capture the outgoing scroll position
        /// BEFORE the transcript's <c>ItemsSource</c> swaps, which only the view can do, so every entry
        /// point routes through the view's funnel — see ChatView.OpenChildTranscript. A command would
        /// bypass the capture and fail silently, landing at the top of the restored view on the way back.
        /// </para>
        /// </remarks>
        public string? OpenAllChildrenLabel =>
            HasHiddenChildren
                ? "Open all " + Children.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) + " calls →"
                : null;

        /// <summary>
        /// Whether this row's calls can be opened as their own transcript (issue #148) — i.e. it has any.
        /// </summary>
        /// <remarks>
        /// <b>Drives VISIBILITY, not enabled-state</b>, matching "Open file"/"Copy path" in the same menu:
        /// a greyed-out item on a row that never launched a sub-agent reads as a bug on a build rather
        /// than as an affordance that does not apply here.
        /// <para>
        /// Deliberately wider than the overflow control's trigger. That control needs <c>IsExpanded</c> AND
        /// <see cref="HasHiddenChildren"/>, so a COLLAPSED row offers nothing until it is opened, and a
        /// fan-out of six or fewer offers nothing at all — the cap never engages, so nothing is hidden.
        /// Without the menu item, whether a sub-agent's work could be opened would depend on how big the
        /// fan-out happened to be, for no reason the user can see.
        /// </para>
        /// </remarks>
        public bool CanOpenTranscript => Children.Count > 0;

        /// <summary>
        /// Raises the cap just far enough that <paramref name="child"/> is drawn. Used by a reveal, so
        /// pointing at a call past the cap opens the row onto that call rather than onto the first five.
        /// </summary>
        internal void EnsureChildVisible(ChatItemViewModel child)
        {
            var index = Children.IndexOf(child);
            if (index < 0)
                return;

            // Measured from the END, because that is the end the window is anchored to.
            var needed = Children.Count - index;
            if (needed <= EffectiveShown)
                return;

            _shownChildren = needed;
            if (_isExpanded)
                SyncVisibleChildren(batched: true);
        }

        /// <summary>
        /// Brings <see cref="VisibleChildren"/> back in step with the cap by moving the window's edges,
        /// never by rebuilding it.
        /// </summary>
        /// <remarks>
        /// The window is always a contiguous suffix of <see cref="Children"/>, and what happens to it
        /// comes in two shapes that want opposite treatment.
        /// <para>
        /// <b>A step</b> — a call arrives and pushes the oldest drawn one out — is one remove and one add
        /// however big the cap is, and moving those two edges is much cheaper than a rebuild. This is
        /// the streaming case, so it is the common one.
        /// </para>
        /// <para>
        /// <b>A jump</b> — the cap changes, so the window widens to everything or narrows back — moves
        /// the FRONT edge by as many rows as the cap moved. Element by element against a
        /// non-virtualizing <c>ItemsControl</c> that is twenty-one separate insertions at index 0, each
        /// re-indexing every container already there; one Reset is far cheaper. <b>This is a cost the
        /// last-N window introduced</b>: anchored to the oldest call the same jumps were appends at the
        /// END, which cost nothing, so it appeared only when the anchor moved.
        /// </para>
        /// <para>
        /// Neither shape changes the row's dominant cost, which is the outer transcript reflowing when a
        /// row twenty-six rows tall becomes one: the viewport gains that much space at a stroke and
        /// re-renders. Only not letting the row grow that tall addresses that.
        /// </para>
        /// </remarks>
        private void SyncVisibleChildren(bool batched = false)
        {
            var target = EffectiveShown;
            var start = Children.Count - target;

            if (batched)
            {
                _drawnFrom = start;
                var window = new List<ChatItemViewModel>(target);
                for (var i = start; i < Children.Count; i++)
                    window.Add(Children[i]);
                VisibleChildren.ReplaceAll(window);

                OnPropertyChanged(nameof(HasHiddenChildren));
                OnPropertyChanged(nameof(OpenAllChildrenLabel));
                return;
            }

            if (VisibleChildren.Count == 0)
                _drawnFrom = start;

            // Slid forward: a call arrived and pushed the oldest drawn one out of the window.
            while (_drawnFrom < start && VisibleChildren.Count > 0)
            {
                VisibleChildren.RemoveAt(0);
                _drawnFrom++;
            }

            // Grew backwards: Show all, or a reveal naming a call older than the window.
            while (_drawnFrom > start)
            {
                _drawnFrom--;
                VisibleChildren.Insert(0, Children[_drawnFrom]);
            }

            // Calls that arrived at the end.
            for (var i = _drawnFrom + VisibleChildren.Count; i < Children.Count; i++)
                VisibleChildren.Add(Children[i]);

            OnPropertyChanged(nameof(HasHiddenChildren));
            OnPropertyChanged(nameof(OpenAllChildrenLabel));
        }

        /// <summary>What the collapsed row says it is hiding, e.g. "26 calls". Null when it hides nothing.</summary>
        public string? ChildSummary =>
            Children.Count == 0 ? null : Children.Count == 1 ? "1 call" : Children.Count + " calls";

        /// <summary>
        /// Whether the chevron appears. A sub-agent row usually has both detail and children, but a row
        /// with only children must still open — the children ARE its content.
        /// </summary>
        public bool CanExpand => HasDetail || HasChildren;

        /// <summary>
        /// This call started a sub-agent, so it owns <see cref="Children"/>. Kept as a flag rather than
        /// inferred from the collection being non-empty: a sub-agent that has not made its first call
        /// yet, or one whose calls we never saw, is still a sub-agent row.
        /// </summary>
        public bool IsSubagentLaunch
        {
            get => _isSubagentLaunch;
            set { if (SetProperty(ref _isSubagentLaunch, value)) OnPropertyChanged(nameof(KindGlyph)); }
        }

        /// <summary>
        /// This call was made while the session was OPENING, before anything had been asked of the
        /// conversation — kiro-cli 2.21.1 opens every one with a <c>fetch_cloud_config</c>. The row is
        /// still drawn, and stays expandable: it is a real tool call and the CLI shows it as one. What
        /// changes is <see cref="KindGlyph"/>, so it is not mistaken for work the user's own prompt
        /// caused; the status colour is left alone, that channel being spoken for.
        /// <para>Display only, and it stays that way because these frames are not recorded: nothing that
        /// preceded the conversation is in its log, so a restored transcript has none of them and never
        /// needs to carry the flag back.</para>
        /// </summary>
        public bool IsSessionSetup
        {
            get => _isSessionSetup;
            set { if (SetProperty(ref _isSessionSetup, value)) OnPropertyChanged(nameof(KindGlyph)); }
        }

        /// <summary>Nests <paramref name="child"/> under this row.</summary>
        public void AddChild(ChatItemViewModel child)
        {
            child.Parent = this;
            Children.Add(child);
            // Only a row that is DRAWING needs its window moved; a closed one rebuilds on open.
            if (_isExpanded)
                SyncVisibleChildren();
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(ChildSummary));
            OnPropertyChanged(nameof(CanExpand));
            OnPropertyChanged(nameof(CanOpenTranscript));
        }

        public override string? CopyText
        {
            get
            {
                var body = Detail;
                if (HasErrorDetail)
                    body = string.IsNullOrEmpty(body) ? "stderr:\n" + _errorDetail : body + "\n\nstderr:\n" + _errorDetail;
                // The permission sentence is part of what the row SHOWS once the call has settled, so it
                // is part of what the row copies - same order as on screen, above the detail. It is also
                // the half a pasted row most needs: "this command ran" is a different report from "this
                // command ran and nobody was asked", and the paste is usually going into a bug report.
                if (HasPermissionSummary)
                    body = string.IsNullOrEmpty(body) ? PermissionSummary : PermissionSummary + "\n" + body;
                // Copy what the row shows: a title that names no file is as unhelpful pasted as on screen.
                var head = HasTargetPath ? Title + "  " + TargetDisplayPath : Title;
                return string.IsNullOrEmpty(body) ? head : head + "\n" + body;
            }
        }

        /// <summary>
        /// Folds a file edit's diff into this row, for agents that open the call as a generic tool row
        /// (placeholder input) and deliver the diff on a later update (the Claude Code adapter's Edit
        /// flow) — one card instead of a tool row plus a near-identical edit row. One diff per row: the
        /// first path claims it and a re-send of the same path (provisional "before" finalized later)
        /// updates it in place; a different path returns false so the host renders it as its own
        /// <see cref="EditItemViewModel"/>. With a diff attached, clicking the row opens the host's
        /// diff viewer; the input/output detail stays reachable via the chevron.
        /// </summary>
        public bool TryAttachDiff(
            string path, string oldText, string newText,
            System.Func<string, string, string, int?, System.Threading.Tasks.Task>? openDiff, int? line = null,
            System.Func<string, string, string, int?, System.Threading.Tasks.Task>? openFileAtDiff = null,
            System.Func<string, int, System.Threading.Tasks.Task>? openFile = null,
            string? workspaceRoot = null)
        {
            if (_diffPath is not null && !string.Equals(_diffPath, path, System.StringComparison.OrdinalIgnoreCase))
                return false;

            var isNew = _diffPath is null;
            _diffPath = path;
            _diffOldText = oldText;
            _diffNewText = newText;
            // Unconditional assignment was the same bug pointing the other way: a finalized re-send
            // carrying no line blanked a line already resolved, and 70% of edits report none.
            _diffLine = line ?? _diffLine;
            // What TargetDisplayPath renders the folded path relative to (issue #178). Without it the
            // edit route would show an absolute path where the read rows show a workspace-relative one —
            // the same row, the same job, two spellings. Never blanks a root already set.
            _fileTargetRoot ??= workspaceRoot;
            _openDiff ??= openDiff;
            _openFileAtDiff ??= openFileAtDiff;
            // The plain callback as well, as the FALLBACK for a host that can't locate a change in the
            // file (only the VS host can read the file to do it — the Desktop host wires none). Without
            // it a folded row in such a host offers no "Open file" at all, which is worse than opening
            // at the reported line. The edit CARD has always degraded this way; the row now matches.
            _openFile ??= openFile;
            if (isNew)
            {
                OnPropertyChanged(nameof(HasDiff));
                OnPropertyChanged(nameof(CanOpenDiff));
                // A folded edit is openable as a FILE too, not only as a diff. Without this the row
                // ends up knowing exactly which file the agent edited, using it for the diff, and
                // offering no way to go there — which is what an edit CARD has always offered and a
                // folded tool row never did (the split is Claude vs Kiro, decided in AcpMapper by
                // whether the opening frame already carried an edit).
                OnPropertyChanged(nameof(CanOpenFile));
                OnPropertyChanged(nameof(OpenFileToolTip));
                OnPropertyChanged(nameof(PathToCopy));
                OnPropertyChanged(nameof(HasPathToCopy));
                OnPropertyChanged(nameof(FileTargetToolTip));
                RaiseRowCommandsChanged();
                // The label, which is what issue #178 was actually missing. The getter fix alone renders
                // nothing: this is the very frame that gives the row its path, so without a raise here
                // the row is still drawing the answer it computed before it had one.
                RaiseTargetPathChanged();
            }
            return true;
        }

        /// <summary>
        /// Attaches the file(s) a read-kind tool call targets, so the row can open them in the host's
        /// editor on click — the read-side analogue of <see cref="TryAttachDiff"/> (same card, same
        /// click-to-act feel; the input/output detail stays reachable via the chevron). Kiro's read
        /// batches several files into one call (its <c>operations</c> array), so a row can carry many
        /// targets — clicking opens them all. Updates in place: a later enrichment (the Claude adapter
        /// fills the real args on a tool_call_update) replaces the target list.
        /// <paramref name="workspaceRoot"/> is what <see cref="TargetDisplayPath"/> shows the target
        /// relative to; omit it and the row falls back to the absolute path, as the edit card does.
        /// </summary>
        public void AttachFileTargets(
            IReadOnlyList<(string Path, int Line)> targets,
            System.Func<string, int, System.Threading.Tasks.Task>? openFile,
            string? workspaceRoot = null)
        {
            if (targets.Count == 0)
                return;

            var wasEmpty = _fileTargets.Count == 0;
            _fileTargets.Clear();
            _fileTargets.AddRange(targets);
            _fileTargetRoot = workspaceRoot;
            _openFile ??= openFile;
            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(FileTargetToolTip));
            OnPropertyChanged(nameof(OpenFileToolTip));
            RaiseTargetPathChanged();
            if (wasEmpty)
            {
                OnPropertyChanged(nameof(HasFileTarget));
                OnPropertyChanged(nameof(CanOpenFile));
                // PathToCopy is `_diffPath ?? FilePath`, so it genuinely changes here - and
                // HasPathToCopy flips false -> true the moment the first target lands. The diff route
                // raises both; this one did not, and the context menu binds "Copy path" straight to
                // HasPathToCopy. Open the menu on a placeholder Read row (Claude sends `rawInput:{}`),
                // close it, let the enriching update arrive: PlacementTarget never changes, so with no
                // notification the item stays Collapsed for the life of the row.
                OnPropertyChanged(nameof(PathToCopy));
                OnPropertyChanged(nameof(HasPathToCopy));
                RaiseRowCommandsChanged();
            }
        }

        /// <summary>
        /// The file this row acts on, shown beside the title exactly as the edit card shows its
        /// <see cref="EditItemViewModel.DisplayPath"/> — workspace-relative, full path in the tooltip.
        /// Null when the backend's own title already names the file, so the row never says it twice:
        /// Claude titles a read "Read src/Foo.cs" and keeps that title, while Kiro's v3 engine titles
        /// every read "Read File" and never enriches it, which left the row naming no file at all
        /// (issue #102) even though the path was in the call's arguments and its ACP locations.
        /// </summary>
        /// <remarks>
        /// <b>Two routes reach a path, and reading only one of them was issue #178.</b> Read targets
        /// arrive through <see cref="AttachFileTargets"/>; a folded EDIT arrives through
        /// <see cref="TryAttachDiff"/> and sets <c>_diffPath</c> alone. Under Kiro v3 a write goes out
        /// over the client fs, so its diff can only be known AFTER the write — the opening frame carries
        /// none, the call therefore opens as a generic tool row titled with v3's bare "Write File", and
        /// the diff folds in on the later update. That row knew its file perfectly well and said
        /// nothing: <c>PathToCopy</c> and <c>CanOpenFile</c> both consult <c>_diffPath</c>, which is
        /// exactly why the right-click menu worked while the label stayed blank.
        /// <para>
        /// The read route still WINS where both exist, because it is the richer one (a batch names
        /// several files); the diff is the fallback, and covers Claude's folded edits at the same time.
        /// Deliberately narrower than teaching <see cref="TryAttachReadTarget"/> about write kinds,
        /// which would flatten the two routes' different click semantics into one — see
        /// <see cref="CanOpenFile"/> for why they are kept apart.
        /// </para>
        /// </remarks>
        public string? TargetDisplayPath
        {
            get
            {
                // The folded-edit fallback: no read targets, but this row edited a file we can name.
                var path = _fileTargets.Count > 0 ? _fileTargets[0].Path : _diffPath;
                if (path is null)
                    return null;
                if (LeafName(path) is { Length: > 0 } leaf &&
                    _title.IndexOf(leaf, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;

                var shown = WorkspacePath.Relative(path, _fileTargetRoot);
                // A batched read (Kiro's operations array) targets several files; name the first and
                // count the rest — the tooltip already lists them all, and clicking opens them all.
                // One diff per row, so the edit route never counts.
                return _fileTargets.Count > 1 ? shown + "  +" + (_fileTargets.Count - 1) + " more" : shown;
            }
        }

        /// <summary>Whether the target path should render (it's redundant when the title already names it).</summary>
        public bool HasTargetPath => TargetDisplayPath is not null;

        private void RaiseTargetPathChanged()
        {
            OnPropertyChanged(nameof(TargetDisplayPath));
            OnPropertyChanged(nameof(HasTargetPath));
        }

        /// <summary>
        /// Re-queries the three row commands whose <c>CanExecute</c> has just changed.
        /// </summary>
        /// <remarks>
        /// A property change is not enough, because a command's enabled state does not come from one.
        /// <c>RelayCommand</c> deliberately does not hook <c>CommandManager.RequerySuggested</c>, so
        /// nothing re-asks on its own, and <c>MenuItem</c> is an <c>ICommandSource</c> that LATCHES
        /// <c>IsEnabled</c> from the last <c>CanExecuteChanged</c> it saw. Open the context menu on a
        /// row that is still a placeholder, close it, let the enriching update arrive: the bound
        /// <c>Visibility</c> flips (those properties ARE raised) and the item appears - greyed out, on a
        /// row that can now do exactly what it is offering. Every other view-model here with a mutable
        /// predicate already does this (<c>ContextSources</c>, <c>SessionItems</c>); the tool row was
        /// the one that did not.
        /// </remarks>
        private void RaiseRowCommandsChanged()
        {
            OpenDiffCommand.RaiseCanExecuteChanged();
            OpenFileCommand.RaiseCanExecuteChanged();
            CopyPathCommand.RaiseCanExecuteChanged();
        }

        /// <summary>The file name alone; empty when the path can't be parsed (net472 throws on invalid chars).</summary>
        private static string LeafName(string path)
        {
            try { return System.IO.Path.GetFileName(path) ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>The first file this row's call targets (a read); null when none was attached.</summary>
        public string? FilePath => _fileTargets.Count > 0 ? _fileTargets[0].Path : null;

        /// <summary>Every file target attached to this row, in call order.</summary>
        public IReadOnlyList<(string Path, int Line)> FileTargets => _fileTargets;

        public bool HasFileTarget => _fileTargets.Count > 0;

        /// <summary>
        /// All target paths, one per line — the title tooltip (mirrors the single-file full path tip).
        /// Falls back to a folded edit's path for the same reason <see cref="TargetDisplayPath"/> does:
        /// the label shows a relative path, so the FULL one has to be reachable on both routes or the
        /// edit route quietly loses the half the tooltip exists for.
        /// </summary>
        public string? FileTargetToolTip =>
            _fileTargets.Count == 0 ? _diffPath : string.Join("\n", _fileTargets.Select(t => t.Path));

        /// <summary>What a row click does, for the hover tip ("Open file" / "Open N files").</summary>
        public string OpenFileToolTip =>
            _fileTargets.Count > 1 ? $"Open {_fileTargets.Count} files" : "Open file";

        /// <summary>
        /// Whether "Open file" is available — from read targets, or from a folded edit's diff.
        /// <para>Two routes because they resolve the destination differently: a read target carries the
        /// line the agent reported, while an edit is located in the file <em>as it stands now</em> so
        /// later work that pushed it down is still landed on (see <c>DiffSideBuilder.LocateLine</c>).
        /// Merging the diff into <c>_fileTargets</c> would flatten that back to the reported line.</para>
        /// </summary>
        public bool CanOpenFile =>
            (_openFile is not null && _fileTargets.Count > 0) || CanOpenDiffTargetFile;

        /// <summary>The folded-edit route: a diff is attached and SOME open callback is wired —
        /// the diff-locating one, or the plain one as the fallback for a host without it.</summary>
        private bool CanOpenDiffTargetFile =>
            _diffPath is not null && (_openFileAtDiff is not null || _openFile is not null);

        /// <summary>The path "Copy path" yields: the edited file, else the first read target. Null when
        /// the row acted on no file at all (a command, a search), which hides the menu item rather than
        /// offering one that copies nothing.</summary>
        public string? PathToCopy => _diffPath ?? FilePath;

        public bool HasPathToCopy => PathToCopy is not null;

        public RelayCommand OpenFileCommand { get; }

        public RelayCommand CopyPathCommand { get; }

        private void CopyPath()
        {
            if (PathToCopy is not { Length: > 0 } path)
                return;
            try { System.Windows.Clipboard.SetText(path); }
            catch { /* clipboard can be transiently locked; copying is best-effort */ }
        }

        private async void OpenFileTarget()
        {
            // The edit route first: when a diff is folded in, the file this row is ABOUT is the one it
            // edited, and it lands on the change rather than at the top.
            if (CanOpenDiffTargetFile)
            {
                try
                {
                    if (_openFileAtDiff is not null)
                        await _openFileAtDiff(_diffPath!, _diffOldText, _diffNewText, _diffLine);
                    else
                        await _openFile!(_diffPath!, _diffLine ?? 0);
                }
                catch { /* navigation is best-effort; never crash the UI */ }
                return;
            }

            if (_openFile is null || _fileTargets.Count == 0)
                return;
            // Snapshot: an enriching update can replace the list while an open is awaited.
            foreach (var (path, line) in _fileTargets.ToArray())
            {
                try { await _openFile(path, line); }
                catch { /* navigation is best-effort; never crash the UI */ }
            }
        }

        /// <summary>The file this row's attached diff is for; null when no edit was folded in.</summary>
        public string? DiffPath => _diffPath;

        public string DiffOldText => _diffOldText;
        public string DiffNewText => _diffNewText;

        public bool HasDiff => _diffPath is not null;

        /// <summary>Row click opens the diff only when one is attached AND a diff viewer is wired.</summary>
        public bool CanOpenDiff => _openDiff is not null && _diffPath is not null;

        public RelayCommand OpenDiffCommand { get; }

        private async void OpenDiff()
        {
            if (_openDiff is null || _diffPath is null)
                return;
            try { await _openDiff(_diffPath, _diffOldText, _diffNewText, _diffLine); }
            catch { /* preview is best-effort; never crash the UI */ }
        }

        public string StatusGlyph => Status switch
        {
            ToolStatus.Running => "…",
            ToolStatus.Success => "✓",
            ToolStatus.Failed => "✗",
            ToolStatus.Launched => "⇢",
            _ => "•",
        };

        /// <summary>
        /// A Segoe MDL2 Assets glyph (built from its code point to keep this source ASCII) for the
        /// tool's ACP kind, shown at the start of the row. Status is conveyed by the icon's colour.
        /// </summary>
        // Session setup outranks the kind, and it is the GLYPH that carries it rather than a tone. Tone
        // was tried and is wrong here for a reason this type states two lines up: on a tool row colour is
        // the STATUS channel, where subtle already means running-or-pending — so a muted session-setup
        // row read as "inactive, or not successful yet" rather than as "not your work". Same rule the
        // permission mark follows: colour is never the only carrier, and what colour cannot say goes in
        // the glyph.
        //
        // CHOSEN AT ROW SIZE rather than off a chart, which changed the answer twice. Cogs (E9F5) is the
        // obvious pick for "internal machinery" and is the wrong one: at the row's 13px it renders
        // smaller than every glyph beside it and reads as a speck. The command prompt (E756) is the
        // literal "system" icon and is wrong twice over — its C:\ collapses into a hatched box at this
        // size, and it would say SHELL COMMAND, which the `execute` kind below already owns.
        //
        // E93E is a broadcast, and it is ANNOUNCING rather than connecting — which is the rule's own
        // word for this category: the exclusions in NoteLiveEventForWorkingWindow are all "the session
        // announcing itself", and this row is the same thing arriving as a tool call instead of as a
        // notification. Read as a network icon it would be too narrow to keep (the next session-open
        // call a backend adds need not touch anything remote); read as an announcement it is exactly
        // the width of the rule. Sliders (E7C4) were the alternative and say CONFIGURATION, which
        // describes what this particular call fetches rather than why the row is here at all.
        /// <summary>
        /// Whether this row is an MCP tool call shown by its normalized name, which is exactly when
        /// <see cref="RawToolName"/> is set. So the icon and the tag inherit that name's shell-fact guard:
        /// a command titled with one of our tools' names draws none of the three.
        /// </summary>
        public bool IsMcpCall => _rawToolName is not null;

        /// <summary>
        /// The kind tag beside the title. <c>mcp</c> for an MCP call, whose ACP kind is the catch-all
        /// <c>other</c> and says nothing; the backend's own kind for everything else.
        /// </summary>
        public string? KindLabel => IsMcpCall ? "mcp" : _kind;

        // An MCP call draws the @ (E910, "Accounts"): the agent addressing a server. Chosen from a
        // shortlist rendered at 13px in every status colour and both themes, where it was the clearest
        // and furthest from the fallback gear an MCP call drew before. E950 and E964 were refused for
        // resembling the Kiro IDE's own MCP icon; ED5D (two joined nodes) was the runner-up, and is the
        // one-line swap if @ is later wanted for addressing another agent or Code Wicket itself.
        public string KindGlyph => ((char)(IsSessionSetup ? 0xE93E : IsSubagentLaunch ? 0xE716 : IsMcpCall ? 0xE910 : (Kind ?? string.Empty).ToLowerInvariant() switch
        {
            "read" => 0xE8A5,     // Document
            "edit" => 0xE70F,     // Edit (pencil)
            "delete" => 0xE74D,   // Delete
            "search" => 0xE721,   // Search
            "execute" => 0xE768,  // Play (run)
            "fetch" => 0xE896,    // Download
            _ => 0xE713,          // Settings gear (generic command/action)
        })).ToString();
    }

    /// <summary>
    /// A file edit the agent made. Carries the before/after text so the row can open the host's
    /// native diff viewer on click (the agent may have written the file itself, as Kiro does). The
    /// diff text itself isn't shown inline — the native viewer is richer — but the agent's intent and
    /// the edit operation (e.g. Kiro's <c>strReplace</c>) are surfaced as detail, mirroring the tool rows.
    /// </summary>
    public sealed class EditItemViewModel : ChatItemViewModel
    {
        private readonly System.Func<string, string, string, int?, System.Threading.Tasks.Task>? _openDiff;
        private readonly System.Func<string, int, System.Threading.Tasks.Task>? _openFile;
        private readonly System.Func<string, string, string, int?, System.Threading.Tasks.Task>? _openFileAtDiff;
        private ToolStatus _status = ToolStatus.Running;
        private string _oldText;
        private string _newText;
        private int? _line;
        private string? _intent;
        private string? _operation;
        private string? _outputDetail;
        private string? _errorDetail;
        private bool _isExpanded;

        public EditItemViewModel(
            string path, string oldText, string newText,
            System.Func<string, string, string, int?, System.Threading.Tasks.Task>? openDiff,
            string? workspaceRoot = null,
            System.Func<string, int, System.Threading.Tasks.Task>? openFile = null,
            int? line = null,
            string? intent = null,
            string? operation = null,
            System.Func<string, string, string, int?, System.Threading.Tasks.Task>? openFileAtDiff = null)
        {
            Path = path;
            DisplayPath = WorkspacePath.Relative(path, workspaceRoot);
            _oldText = oldText;
            _newText = newText;
            _line = line;
            _intent = intent;
            _operation = operation;
            _openDiff = openDiff;
            _openFile = openFile;
            _openFileAtDiff = openFileAtDiff;
            OpenDiffCommand = new RelayCommand(OpenDiff, () => CanOpenDiff);
            OpenFileCommand = new RelayCommand(OpenFileInIde, () => CanOpenFile);
        }

        /// <summary>
        /// The edit call's outcome, colouring the pencil icon like the tool rows' kind glyph (issue
        /// #46). Kiro-style edits carry their diff at tool_call start so no tool row ever opens; the
        /// call's completion lands on this card instead. Stays Running (subtle) for edits whose call
        /// never reports completion (no tool-call id, or a pre-fix persisted session).
        /// </summary>
        public ToolStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        /// <summary>The agent's stated reason for the edit (its "why"), shown as a subtitle like the tool rows.</summary>
        public string? Intent => _intent;
        public bool HasIntent => !string.IsNullOrWhiteSpace(_intent);

        /// <summary>The backend's edit-operation name (Kiro's strReplace/create/…), shown as a small label; null hides it.</summary>
        public string? Operation => _operation;
        public bool HasOperation => !string.IsNullOrWhiteSpace(_operation);

        /// <summary>The full path, used for operations (opening the diff, copy).</summary>
        public string Path { get; }

        /// <summary>
        /// The path as shown in the row: relative to the workspace root for files under it (forward
        /// slashes), else the full absolute path (so out-of-workspace edits stay obvious). Computed once
        /// at construction from the root current then; the full <see cref="Path"/> is still the tooltip.
        /// </summary>
        public string DisplayPath { get; }

        public string OldText => _oldText;
        public string NewText => _newText;

        /// <summary>
        /// Replaces the diff in place. Backends can re-send the same edit as its tool call
        /// finalizes (e.g. a provisional empty "before" repaired to the real file content, or the
        /// reported line arriving only on the finalized update), and the host updates the existing
        /// row rather than adding a duplicate. A null <paramref name="line"/> keeps the current one.
        /// </summary>
        public void Update(string oldText, string newText, int? line = null, string? intent = null, string? operation = null)
        {
            _oldText = oldText;
            _newText = newText;
            // `line ?? _line`, not `_line ??= line`. The two say opposite things and the doc above
            // describes this one: the FIRST value wins under ??=, so a provisional line was frozen and
            // the finalized report - the whole reason a re-send exists - was discarded. Null still
            // keeps what is there, which is what "a null line keeps the current one" means.
            _line = line ?? _line;
            OnPropertyChanged(nameof(OldText));
            OnPropertyChanged(nameof(NewText));

            // A finalized re-send can carry the intent/operation the provisional one lacked; fill them
            // in but never blank out a value already shown.
            if (!string.IsNullOrWhiteSpace(intent))
            {
                _intent = intent;
                OnPropertyChanged(nameof(Intent));
                OnPropertyChanged(nameof(HasIntent));
            }
            if (!string.IsNullOrWhiteSpace(operation))
            {
                _operation = operation;
                OnPropertyChanged(nameof(Operation));
                OnPropertyChanged(nameof(HasOperation));
            }
        }

        /// <summary>
        /// The call's result text, shown in the expander. Same field the tool row carries; an edit that
        /// renders as a CARD has no tool row to carry it, and under Kiro v3 the card is a write's
        /// ORDINARY shape — so without this the calls that change the user's files were exactly the ones
        /// that could not say what happened (issue #189).
        /// </summary>
        public string? OutputDetail
        {
            get => _outputDetail;
            set { if (SetProperty(ref _outputDetail, value)) RaiseDetailChanged(); }
        }

        /// <summary>
        /// Why the call failed, styled as an error below the result. This is the field the reported bug
        /// was actually about: a write failed, the reason crossed the wire, reached the view-model, and
        /// was dropped one step from the screen because only the tool-row branch read it.
        /// </summary>
        public string? ErrorDetail
        {
            get => _errorDetail;
            set { if (SetProperty(ref _errorDetail, value)) { OnPropertyChanged(nameof(HasErrorDetail)); RaiseDetailChanged(); } }
        }

        public bool HasErrorDetail => !string.IsNullOrEmpty(_errorDetail);

        /// <summary>What the expander shows above any error section.</summary>
        public string? Detail => _outputDetail;

        /// <summary>
        /// Whether the chevron appears. The permission sentence counts, exactly as it does for a tool
        /// row: "why did this run?" is answerable for every settled call, and it was hover-only here —
        /// a tooltip is undiscoverable, so the rule that the sentence is available on every settled row
        /// was met by the tool row and quietly not by the card.
        /// </summary>
        public bool CanExpand => !string.IsNullOrEmpty(Detail) || HasErrorDetail || HasPermissionSummary;

        public bool HasDetail => CanExpand;

        private void RaiseDetailChanged()
        {
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(HasDetail));
            OnPropertyChanged(nameof(CanExpand));
        }

        /// <summary>The chevron has to appear for a call whose only content IS the permission sentence.</summary>
        protected override void OnPermissionDisplayChanged() => RaiseDetailChanged();

        /// <summary>Collapsed by default, like the tool row's — vertical economy on a card that is usually fine.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public RelayCommand OpenDiffCommand { get; }

        public bool CanOpenDiff => _openDiff is not null;

        /// <summary>Opens the edited file itself in the editor (right-click menu); the row click still shows the diff.</summary>
        public RelayCommand OpenFileCommand { get; }

        // Either opener will do — the diff-aware one is preferred when the host wired it.
        public bool CanOpenFile => _openFileAtDiff is not null || _openFile is not null;

        // Deliberately the bare path, and deliberately NOT the permission sentence as well: this card's
        // context menu binds "Copy path" straight to CopyCommand, so whatever is here is what a menu item
        // promising a path hands over. The tool row can carry its whole contents because it has a
        // SEPARATE CopyPathCommand for the narrow job; here the two would be the same command. The
        // sentence reaches a reader through the header glyph's tooltip and through the export instead.
        public override string? CopyText => Path;

        private async void OpenDiff()
        {
            if (_openDiff is null)
                return;
            try { await _openDiff(Path, OldText, NewText, _line); }
            catch { /* preview is best-effort; never crash the UI */ }
        }

        /// <summary>
        /// "Open file" from the card's context menu — lands on the change, not the top of the file.
        /// The host resolves the line, because only it can read the file: the edit is found in the
        /// content as it stands NOW, so a change since pushed down by later work is still landed on,
        /// and the reported line (absent on ~70% of real edits) is only the fallback. A host that
        /// can't do that lookup still gets the reported line rather than 0.
        /// </summary>
        private async void OpenFileInIde()
        {
            try
            {
                if (_openFileAtDiff is not null)
                    await _openFileAtDiff(Path, _oldText, _newText, _line);
                else if (_openFile is not null)
                    await _openFile(Path, _line ?? 0);
            }
            catch { /* navigation is best-effort; never crash the UI */ }
        }
    }

    /// <summary>A task's progress (mirrors Core PlanItemStatus).</summary>
    public enum PlanTaskStatus
    {
        Pending,
        InProgress,
        Completed,
    }

    /// <summary>One task in a <see cref="PlanItemViewModel"/>; <see cref="Status"/> updates live.</summary>
    public sealed class PlanTaskViewModel : ObservableObject
    {
        private PlanTaskStatus _status;

        public PlanTaskViewModel(string id, string description, PlanTaskStatus status)
        {
            Id = id;
            Description = description;
            _status = status;
        }

        public string Id { get; }
        public string Description { get; }

        public PlanTaskStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(Completed));
                    OnPropertyChanged(nameof(InProgress));
                    OnPropertyChanged(nameof(Glyph));
                }
            }
        }

        public bool Completed => _status == PlanTaskStatus.Completed;
        public bool InProgress => _status == PlanTaskStatus.InProgress;

        /// <summary>Segoe MDL2 glyph: checked box (done), partial box (in progress), empty box (pending).</summary>
        public string Glyph => ((char)(_status switch
        {
            PlanTaskStatus.Completed => 0xE73A,   // CheckboxComposite (filled w/ check)
            PlanTaskStatus.InProgress => 0xE73C,  // CheckboxIndeterminate (partial)
            _ => 0xE739,                          // Checkbox (empty)
        })).ToString();
    }

    /// <summary>
    /// The agent's plan/task list, shown as a single card that updates in place as items tick off
    /// (backends send the full list on every change), fed by the "plan" agent event.
    /// </summary>
    public sealed class PlanItemViewModel : ChatItemViewModel
    {
        private string? _title;

        public PlanItemViewModel(string? title, IEnumerable<PlanTaskViewModel> tasks)
        {
            _title = title;
            foreach (var task in tasks)
                Tasks.Add(task);
        }

        public string? Title
        {
            get => _title;
            private set { if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(HasTitle)); }
        }

        public bool HasTitle => !string.IsNullOrEmpty(_title);

        public ObservableCollection<PlanTaskViewModel> Tasks { get; } = new();

        /// <summary>
        /// Applies a new full list. When the ids line up with the current ones, completion flags are
        /// updated in place (no flicker, smooth tick-off); otherwise the list is rebuilt.
        /// </summary>
        public void Update(string? title, IReadOnlyList<PlanTaskViewModel> tasks)
        {
            if (!string.IsNullOrEmpty(title))
                Title = title;

            if (tasks.Count == Tasks.Count && tasks.Select(t => t.Id).SequenceEqual(Tasks.Select(t => t.Id)))
            {
                for (var i = 0; i < tasks.Count; i++)
                    Tasks[i].Status = tasks[i].Status;
            }
            else
            {
                Tasks.Clear();
                foreach (var task in tasks)
                    Tasks.Add(task);
            }

            RaiseProgressChanged();
        }

        /// <summary>
        /// Marks every task completed — the "plan finished" update (an empty PlanUpdated: Kiro disposes
        /// the task list when its last item completes, so no full list arrives to tick the last box).
        /// </summary>
        public void CompleteAll()
        {
            foreach (var task in Tasks)
                task.Status = PlanTaskStatus.Completed;
            RaiseProgressChanged();
        }

        /// <summary>All tasks are done (an empty card doesn't count as complete).</summary>
        public bool IsComplete => Tasks.Count > 0 && Tasks.All(t => t.Completed);

        /// <summary>Completed-over-total, for the pinned progress strip ("2/5").</summary>
        public string ProgressText => $"{Tasks.Count(t => t.Completed)}/{Tasks.Count}";

        /// <summary>
        /// The task being worked (first in-progress, else first pending) — what the pinned strip shows
        /// as "where the agent is". Null once everything is done.
        /// </summary>
        public string? CurrentTaskDescription =>
            (Tasks.FirstOrDefault(t => t.InProgress) ?? Tasks.FirstOrDefault(t => !t.Completed))?.Description;

        private void RaiseProgressChanged()
        {
            OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(CurrentTaskDescription));
        }

        public override string? CopyText
        {
            get
            {
                var sb = new StringBuilder();
                if (HasTitle)
                    sb.AppendLine(_title);
                foreach (var task in Tasks)
                    sb.AppendLine($"[{(task.Completed ? "x" : " ")}] {task.Description}");
                return sb.ToString().TrimEnd();
            }
        }
    }

    /// <summary>
    /// One sub-agent in a <see cref="CrewItemViewModel"/>: live status while it runs, and — once its
    /// wrap-up delivers one — the final result it returned to the orchestrator, expandable as rendered
    /// markdown. The full activity stream is deliberately not carried.
    /// </summary>
    public sealed class CrewAgentViewModel : ObservableObject
    {
        private string _name;
        private string? _description;
        private string _status;
        private string? _statusMessage;
        private string? _result;
        private bool _isExpanded;

        public CrewAgentViewModel(string sessionId, string name, string? description, string status, string? statusMessage)
        {
            SessionId = sessionId;
            _name = name;
            _description = description;
            _status = status;
            _statusMessage = statusMessage;
        }

        public string SessionId { get; }

        /// <summary>The sub-agent's name (Kiro's session slug); refreshed from later roster snapshots.</summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>The task the orchestrator gave this sub-agent (its initial query); shown as a tooltip.</summary>
        public string? Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        /// <summary>The backend's raw state ("working"/"terminated" for Kiro); display-only passthrough.</summary>
        public string Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(StatusGlyph));
                    OnPropertyChanged(nameof(StatusLabel));
                }
            }
        }

        public string? StatusMessage
        {
            get => _statusMessage;
            set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(StatusLabel)); }
        }

        public bool IsRunning => _status == "working";

        /// <summary>Same status marks as the tool rows (…/✓), so crew rows read like tool steps.</summary>
        public string StatusGlyph => IsRunning ? "…" : "✓";

        /// <summary>e.g. "Running" while working; "done" once the backend reports the session ended.</summary>
        public string StatusLabel => IsRunning ? (_statusMessage ?? "Running") : "done";

        /// <summary>The final result the sub-agent returned; updates in place if re-sent.</summary>
        public string? Result
        {
            get => _result;
            set
            {
                if (SetProperty(ref _result, value))
                {
                    OnPropertyChanged(nameof(HasResult));
                    OnPropertyChanged(nameof(VisibleResult));
                }
            }
        }

        public bool HasResult => !string.IsNullOrEmpty(_result);

        /// <summary>Collapsed by default; results can be whole files, so vertical economy matters.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(VisibleResult)); }
        }

        /// <summary>
        /// What the markdown viewer binds: empty until expanded, so the FlowDocument (rebuilt per text
        /// change) is only ever built for rows the user actually opens — cheap while a crew streams.
        /// </summary>
        public string? VisibleResult => _isExpanded ? _result : null;
    }

    /// <summary>
    /// The backend's sub-agent roster ("agent crew"), one live card upserted from roster snapshots:
    /// rows update by session id and are never removed (the disposal snapshot is empty — final states
    /// stand). Each sub-agent's final result attaches to its row; the sub-agents' raw streams are
    /// filtered out upstream and never enter the transcript.
    /// </summary>
    public sealed class CrewItemViewModel : ChatItemViewModel
    {
        private readonly Dictionary<string, CrewAgentViewModel> _bySessionId = new();
        private string? _title;

        public string? Title
        {
            get => _title;
            private set { if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(HasTitle)); }
        }

        public bool HasTitle => !string.IsNullOrEmpty(_title);

        public ObservableCollection<CrewAgentViewModel> Agents { get; } = new();

        /// <summary>Adds or updates one roster entry. The group label ("crew-…") titles the card.</summary>
        public void Upsert(string sessionId, string name, string? description, string status, string? statusMessage, string? group)
        {
            // Take the latest non-empty group, not just the first: Kiro's early list_update frames can
            // carry a provisional group while it's still generating the crew name ("crew-Add missing BDD
            // scen"), then a later frame carries the complete one ("crew-Add missing BDD scenarios").
            // Locking Title to the first frame dropped that later update, leaving a mid-word-truncated
            // card title (issue #41 — same dropped-final-update class as #33). An absent/empty group in a
            // later frame is ignored (never clears a good title); each frame is a full roster snapshot.
            if (!string.IsNullOrEmpty(group))
            {
                var label = group!.Trim();
                if (label.StartsWith("crew-", System.StringComparison.OrdinalIgnoreCase))
                    label = label.Substring("crew-".Length).Trim();
                if (label.Length > 0)
                    Title = label;
            }

            if (_bySessionId.TryGetValue(sessionId, out var existing))
            {
                // Refresh the per-agent fields from the latest snapshot too (same dropped-update fix as
                // the title), but never regress: a terminated agent's snapshot omits sessionName/
                // initialQuery, so the mapper passes the sessionId as `name` and a null description —
                // guard so that fallback can't overwrite the real name/task captured earlier.
                if (!string.IsNullOrEmpty(name) && name != sessionId)
                    existing.Name = name;
                if (!string.IsNullOrWhiteSpace(description))
                    existing.Description = description;
                existing.Status = status;
                existing.StatusMessage = statusMessage;
                return;
            }

            var agent = new CrewAgentViewModel(sessionId, name, description, status, statusMessage);
            _bySessionId[sessionId] = agent;
            Agents.Add(agent);
        }

        /// <summary>Attaches (or refreshes) a sub-agent's final result; ignored for unknown session ids
        /// (a result observed before any roster frame has no row to land on — harmless).</summary>
        public void SetResult(string sessionId, string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (_bySessionId.TryGetValue(sessionId, out var agent))
                agent.Result = text;
        }

        public override string? CopyText
        {
            get
            {
                var sb = new StringBuilder();
                if (HasTitle)
                    sb.AppendLine(_title);
                foreach (var agent in Agents)
                {
                    sb.AppendLine($"[{(agent.IsRunning ? "…" : "x")}] {agent.Name}");
                    if (agent.HasResult)
                        sb.AppendLine(agent.Result);
                }
                return sb.ToString().TrimEnd();
            }
        }
    }

    /// <summary>
    /// The result of a <c>run_tests</c> call, rendered as a card: a pass/fail summary plus the failing
    /// tests, each clickable to jump to its source location (when a navigation callback is wired and the
    /// failure carries a stack-trace location). Derived from the tool's structured result, so it rebuilds
    /// on replay like everything else. The failure list is collapsible behind the summary — a broken
    /// suite otherwise buries the rest of the transcript under dozens of rows.
    /// </summary>
    public sealed class TestRunResultItemViewModel : ChatItemViewModel
    {
        /// <summary>
        /// Failure count at or under which the list starts expanded. A handful of failures is the detail
        /// you actually wanted to see, and hiding it behind a click would be a regression; past that the
        /// card is a wall, so the summary leads and the chevron opens it.
        /// </summary>
        internal const int InlineFailureLimit = 3;

        private bool _isExpanded;

        public TestRunResultItemViewModel(
            bool succeeded, int total, int passed, int failed, int skipped, bool truncated,
            IEnumerable<TestFailureViewModel> failures)
        {
            Succeeded = succeeded;
            Total = total;
            Passed = passed;
            Failed = failed;
            Skipped = skipped;
            Truncated = truncated;
            foreach (var f in failures)
                Failures.Add(f);
            _isExpanded = Failures.Count > 0 && Failures.Count <= InlineFailureLimit;
        }

        public bool Succeeded { get; }
        public int Total { get; }
        public int Passed { get; }
        public int Failed { get; }
        public int Skipped { get; }

        /// <summary>True when the failing-test list was capped (the tool reports more than it detailed).</summary>
        public bool Truncated { get; }

        /// <summary>The "…more failures not shown" footnote only means anything while the list is
        /// showing — collapsed, nothing is shown at all.</summary>
        public bool ShowTruncatedNote => Truncated && IsExpanded;

        public ObservableCollection<TestFailureViewModel> Failures { get; } = new();
        public bool HasFailures => Failures.Count > 0;
        public bool IsError => !Succeeded;

        /// <summary>Whether the failing-test list is showing; the header chevron toggles it. Transient
        /// (never persisted) — a replayed card re-derives its default from the failure count.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(ExpandToolTip));
                    OnPropertyChanged(nameof(ShowTruncatedNote));
                }
            }
        }

        public string ExpandToolTip => IsExpanded
            ? "Hide failing tests"
            : Failures.Count == 1 ? "Show 1 failing test" : $"Show {Failures.Count} failing tests";

        /// <summary>The mark inside the header status badge (Segoe MDL2, tinted by status): a check when all
        /// passed, an X otherwise. The template draws a thin themed circle around it (the font's own
        /// outline circle-X glyphs don't render a visible ring at this size, so we compose it).</summary>
        public string StatusGlyph => ((char)(Succeeded ? 0xE73E : 0xE711)).ToString(); // CheckMark / Cancel

        /// <summary>e.g. "12 passed  ·  2 failed  ·  1 skipped" (zero counts for failed/skipped omitted).</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string> { $"{Passed} passed" };
                if (Failed > 0) parts.Add($"{Failed} failed");
                if (Skipped > 0) parts.Add($"{Skipped} skipped");
                return string.Join("  ·  ", parts);
            }
        }

        public override string? CopyText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Tests: {Summary}");
                foreach (var f in Failures)
                {
                    // Copy carries every failure whatever the card is showing, and both locations when
                    // they differ — the throw site alone can be the least useful half of the pair.
                    sb.Append("  x ").Append(f.Name);
                    if (f.HasLocation)
                        sb.Append(" (").Append(f.LocationLabel).Append(')');
                    if (f.HasTestLocation)
                        sb.Append(" test: ").Append(f.TestLocationLabel);
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
        }
    }

    /// <summary>
    /// One failing test in a <see cref="TestRunResultItemViewModel"/>. Clicking opens where it threw;
    /// the context menu also offers where the TEST is, when the tool resolved that to somewhere else
    /// (see <see cref="TestFile"/>).
    /// </summary>
    public sealed class TestFailureViewModel : ObservableObject
    {
        private readonly System.Func<string, int, System.Threading.Tasks.Task>? _openFile;

        public TestFailureViewModel(
            string name, string? message, string? file, int line, string? workspaceRoot,
            System.Func<string, int, System.Threading.Tasks.Task>? openFile,
            string? testFile = null, int testLine = 0)
        {
            Name = name;
            Message = message;
            File = file;
            Line = line;
            DisplayPath = string.IsNullOrEmpty(file) ? null : WorkspacePath.Relative(file!, workspaceRoot);
            TestFile = testFile;
            TestLine = testLine;
            TestDisplayPath = string.IsNullOrEmpty(testFile) ? null : WorkspacePath.Relative(testFile!, workspaceRoot);
            _openFile = openFile;
            OpenCommand = new RelayCommand(OpenPrimary, () => CanOpen);
            OpenTestCommand = new RelayCommand(OpenTest, () => CanOpenTest);
            OpenFailureCommand = new RelayCommand(OpenFailure, () => CanOpenFailure);
        }

        public string Name { get; }
        public string? Message { get; }
        public bool HasMessage => !string.IsNullOrEmpty(Message);

        /// <summary>Absolute source path (used to open the editor); null when the trace had no location.</summary>
        public string? File { get; }
        public int Line { get; }
        public string? DisplayPath { get; }

        public bool HasLocation => !string.IsNullOrEmpty(File) && Line > 0;

        /// <summary>"src/Foo/BarTests.cs:42"-style label (relative under root); null when unknown.</summary>
        public string? LocationLabel => HasLocation ? $"{DisplayPath}:{Line}" : null;

        /// <summary>
        /// Where the test itself lives, when that differs from where it threw — a shared assertion helper,
        /// or a Reqnroll/SpecFlow step definition, where the failing Then tells you nothing without the
        /// scenario's Givens. The tool sends it only when it goes somewhere new, so a non-null value always
        /// means a second jump worth offering. For a feature test this is the <c>.feature</c> file itself.
        /// </summary>
        public string? TestFile { get; }
        public int TestLine { get; }
        public string? TestDisplayPath { get; }

        public bool HasTestLocation => !string.IsNullOrEmpty(TestFile) && TestLine > 0;
        public string? TestLocationLabel => HasTestLocation ? $"{TestDisplayPath}:{TestLine}" : null;

        /// <summary>
        /// Menu wording follows what you'd actually land in: a Gherkin feature file reads as the scenario,
        /// not "the test" (the generated code-behind that C# would call the test is not what opens).
        /// </summary>
        public string OpenTestHeader => IsFeatureFile(TestFile) ? "Go to scenario" : "Go to test";

        public bool CanOpenTest => _openFile is not null && HasTestLocation;
        public bool CanOpenFailure => _openFile is not null && HasLocation;

        /// <summary>Whether the row has anything to offer on right-click (an empty menu must not pop up).</summary>
        public bool HasContextActions => CanOpenFailure || CanOpenTest;

        /// <summary>Clickable only when a navigation callback is wired and there's somewhere to go.</summary>
        public bool CanOpen => _openFile is not null && (HasTestLocation || HasLocation);

        /// <summary>
        /// Where a plain click goes: the TEST when we resolved one, else the throw site. This matches
        /// VS's Test Explorer, where double-clicking a result opens the test and the stack-trace links
        /// are how you reach the assertion — and it's the better landing either way, since the throw
        /// site is often a generic assertion wrapper while the failure MESSAGE is already on the card.
        /// The two are the same place whenever the assert is written inline in the test, so this only
        /// differs for the shared-helper and BDD-step cases, which is exactly where the test wins.
        /// </summary>
        public bool PrimaryIsTest => HasTestLocation;

        /// <summary>The label on the row — describes where the click lands, so it follows the primary.
        /// Independent of whether an editor is wired: it's information either way.</summary>
        public string? PrimaryLocationLabel => PrimaryIsTest ? TestLocationLabel : LocationLabel;

        public bool HasPrimaryLocation => PrimaryLocationLabel is not null;

        /// <summary>Row tooltip: both ends of the trace, since the row itself can only show one.</summary>
        public string? LocationToolTip =>
            HasTestLocation && HasLocation ? $"Test: {TestLocationLabel}\nFailed at: {LocationLabel}"
            : HasTestLocation ? $"Test: {TestLocationLabel}"
            : HasLocation ? $"Failed at: {LocationLabel}"
            : null;

        public RelayCommand OpenCommand { get; }
        public RelayCommand OpenTestCommand { get; }
        public RelayCommand OpenFailureCommand { get; }

        private static bool IsFeatureFile(string? path) =>
            path is not null && path.EndsWith(".feature", System.StringComparison.OrdinalIgnoreCase);

        private void OpenPrimary()
        {
            if (PrimaryIsTest)
                OpenTest();
            else
                OpenFailure();
        }

        private void OpenFailure() => OpenAt(File, Line, HasLocation);

        private void OpenTest() => OpenAt(TestFile, TestLine, HasTestLocation);

        private async void OpenAt(string? path, int line, bool valid)
        {
            if (_openFile is null || !valid)
                return;
            try { await _openFile(path!, line); }
            catch { /* navigation is best-effort; never crash the UI */ }
        }
    }

    public enum NoticeKind
    {
        Info,
        Error,
    }

    /// <summary>A status line: errors, turn completion, etc.</summary>
    /// <summary>
    /// What the agent did to the user's breakpoints, in the transcript (issue #73).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The card is the point of the feature as much as the tool is. Red dots the user did not place,
    /// appearing only in the gutter, are the auto-opened-diff-window mistake again: something changed in
    /// their IDE and the conversation does not say so. So a set is shown here, with the CONDITION and the
    /// agent's REASON — the two things the gutter cannot tell them, and the reason the agent is better at
    /// this than they are.
    /// </para>
    /// <para>
    /// One card serves set / list / cleared because they are one subject and differ only in verb. Three
    /// near-identical templates would be three places to fix the next thing about breakpoint rows.
    /// </para>
    /// </remarks>
    public sealed class BreakpointCardItemViewModel : ChatItemViewModel
    {
        /// <summary>
        /// At or under this many rows the list starts open. A handful IS the detail you wanted; past that
        /// the card is a wall and the summary should lead — the same reasoning, and the same number, as
        /// the test-results card.
        /// </summary>
        internal const int InlineRowLimit = 3;

        private bool _isExpanded;

        public BreakpointCardItemViewModel(
            string action, string summary, bool isError, IEnumerable<BreakpointRowViewModel> rows,
            IEnumerable<string>? notes = null)
        {
            Action = action;
            Summary = summary;
            IsError = isError;
            foreach (var row in rows)
                Rows.Add(row);
            if (notes is not null)
            {
                foreach (var note in notes)
                    Notes.Add(note);
            }
            _isExpanded = Rows.Count > 0 && Rows.Count <= InlineRowLimit;
        }

        /// <summary>"set", "list" or "cleared" — the verb, straight from the payload.</summary>
        public string Action { get; }

        /// <summary>The tool's own one-line account of what happened; never re-derived here.</summary>
        public string Summary { get; }

        public bool IsError { get; }

        public ObservableCollection<BreakpointRowViewModel> Rows { get; } = new();

        public bool HasRows => Rows.Count > 0;

        /// <summary>
        /// The tool's advice, shown rather than hidden — "you set none in this conversation, but N remain
        /// from earlier ones" is the difference between the user's gutter being clean and them thinking
        /// it is.
        /// </summary>
        public ObservableCollection<string> Notes { get; } = new();

        public bool HasNotes => Notes.Count > 0;

        /// <summary>Whether the row list is showing; the header chevron toggles it. Never persisted —
        /// a replayed card re-derives its default from the row count.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                    OnPropertyChanged(nameof(ExpandToolTip));
            }
        }

        public string ExpandToolTip => IsExpanded
            ? "Hide breakpoints"
            : Rows.Count == 1 ? "Show 1 breakpoint" : $"Show {Rows.Count} breakpoints";

        /// <summary>Segoe MDL2, tinted by status. A cleared card gets its own mark: nothing was placed,
        /// so a tick would be claiming the opposite of what happened.</summary>
        public string StatusGlyph => ((char)(IsError ? 0xE711 : Action == "cleared" ? 0xE74D : 0xE73E)).ToString();

        public override string? CopyText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine(Summary);
                // Every row, whatever the card is showing — a collapsed card is a display state, not a
                // shorter set of facts.
                foreach (var row in Rows)
                    sb.Append("  ").AppendLine(row.CopyLine);
                foreach (var note in Notes)
                    sb.Append("  note: ").AppendLine(note);
                return sb.ToString().TrimEnd();
            }
        }
    }

    /// <summary>
    /// One breakpoint on a <see cref="BreakpointCardItemViewModel"/>. Clicking opens the line.
    /// </summary>
    public sealed class BreakpointRowViewModel : ObservableObject
    {
        private readonly System.Func<string, int, System.Threading.Tasks.Task>? _openFile;

        public BreakpointRowViewModel(
            string? file, int line, string? workspaceRoot,
            System.Func<string, int, System.Threading.Tasks.Task>? openFile,
            string? condition = null, string? printMessage = null, string? reason = null,
            string? status = null, string? error = null,
            bool setByAgent = true, bool? thisConversation = null)
        {
            File = file;
            Line = line;
            DisplayPath = string.IsNullOrEmpty(file) ? null : WorkspacePath.Relative(file!, workspaceRoot);
            Condition = condition;
            PrintMessage = printMessage;
            Reason = reason;
            Status = status;
            Error = error;
            SetByAgent = setByAgent;
            ThisConversation = thisConversation;
            _openFile = openFile;
            OpenCommand = new RelayCommand(Open, () => CanOpen);
        }

        public string? File { get; }
        public int Line { get; }
        public string? DisplayPath { get; }

        /// <summary>The condition that has to hold — the agent's real contribution over a plain stop.</summary>
        public string? Condition { get; }
        public bool HasCondition => !string.IsNullOrEmpty(Condition);

        /// <summary>Non-null when this prints and continues instead of stopping.</summary>
        public string? PrintMessage { get; }
        public bool IsTracepoint => !string.IsNullOrEmpty(PrintMessage);

        /// <summary>Why the agent chose this line, in its own words. The gutter cannot say this.</summary>
        public string? Reason { get; }
        public bool HasReason => !string.IsNullOrEmpty(Reason);

        /// <summary>"set" or "failed" on a set card; null elsewhere.</summary>
        public string? Status { get; }

        public string? Error { get; }
        public bool HasError => !string.IsNullOrEmpty(Error);

        /// <summary>False for a breakpoint the USER set, which only a list card ever shows.</summary>
        public bool SetByAgent { get; }

        /// <summary>
        /// Null when the row is not ours. Three states rather than two, because "the user set it",
        /// "you set it here" and "you set it in an earlier conversation" are three different answers and
        /// only the last explains a breakpoint the user does not remember agreeing to.
        /// </summary>
        public bool? ThisConversation { get; }

        /// <summary>The right-hand label: where it is.</summary>
        public string? LocationLabel => HasLocation ? $"{DisplayPath}:{Line}" : DisplayPath;

        public bool HasLocation => !string.IsNullOrEmpty(File) && Line > 0;

        public bool CanOpen => _openFile is not null && HasLocation;

        public RelayCommand OpenCommand { get; }

        /// <summary>One line of the card's Copy output — location, then whatever qualifies it.</summary>
        public string CopyLine
        {
            get
            {
                var sb = new StringBuilder(LocationLabel ?? "(no location)");
                if (HasCondition)
                    sb.Append("  when ").Append(Condition);
                if (IsTracepoint)
                    sb.Append("  print \"").Append(PrintMessage).Append('"');
                if (HasReason)
                    sb.Append("  — ").Append(Reason);
                if (HasError)
                    sb.Append("  FAILED: ").Append(Error);
                return sb.ToString();
            }
        }

        private async void Open()
        {
            if (_openFile is null || !HasLocation)
                return;
            try { await _openFile(File!, Line); }
            catch (Exception) { /* navigation is best-effort, exactly as the test card treats it */ }
        }
    }

    public sealed class NoticeItemViewModel : ChatItemViewModel
    {
        private bool _isExpanded;

        public NoticeItemViewModel(string text, NoticeKind kind = NoticeKind.Info, string? details = null)
        {
            Text = text;
            Kind = kind;
            Details = string.IsNullOrWhiteSpace(details) ? null : details!.Trim();
        }

        public string Text { get; }
        public NoticeKind Kind { get; }
        public bool IsError => Kind == NoticeKind.Error;

        /// <summary>
        /// Drawn while the session was OPENING, before anything was asked of it — an MCP server
        /// announcing itself on a warm session. The same flag a setup tool row carries, for the same
        /// reason: these are never recorded (live backend state belongs to a connection, not a
        /// transcript), so when a picker change tears that connection down they are the one thing on
        /// screen that describes nothing, and <c>DropSessionOpeningRows</c> takes them with the row.
        /// A notice that lands after a prompt is not flagged: its position relative to that prompt is
        /// the information (issue #19), and it stays.
        /// </summary>
        public bool IsSessionSetup { get; set; } // a setter, not init: the net472 slice has no IsExternalInit polyfill

        /// <summary>
        /// The backend's own account of a failure — its version, the JSON-RPC error code, its log
        /// directory, and what it wrote to stderr this session (see <c>AgentDiagnostics</c>). Null when
        /// there is nothing beyond <see cref="Text"/>, which is what keeps ordinary notices from
        /// growing an empty chevron.
        ///
        /// <para><b>Shown as plain text, deliberately not markdown.</b> This is raw diagnostic output —
        /// stderr, JSON and stack traces, full of underscores, backticks and angle brackets that a
        /// markdown pass would eat or restyle. Diagnostic text is the one place where the characters
        /// on screen must be the characters that were sent.</para>
        ///
        /// <para>It rides the event, so it is persisted with it: a restored transcript's errors keep
        /// their detail instead of collapsing back to the one-line message.</para>
        /// </summary>
        public string? Details { get; }

        public bool HasDetails => Details is not null;

        /// <summary>Whether the detail panel is showing. Starts collapsed — the message is the answer
        /// for most readers, and an error that dumped a session's stderr into the transcript unprompted
        /// would bury everything around it. Transient, like every other expansion state here.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                    OnPropertyChanged(nameof(ExpandToolTip));
            }
        }

        public string ExpandToolTip => IsExpanded ? "Hide details" : "Show details";

        /// <summary>
        /// Copying an error takes the details with it. The whole point of the panel is that this text
        /// ends up in a bug report, and a copy that silently stopped at the first line would recreate
        /// issue #82 one step further along.
        /// </summary>
        public override string? CopyText => HasDetails
            ? Text + System.Environment.NewLine + System.Environment.NewLine + Details
            : Text;
    }
}
