using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Input;
using CodeWicket.UI.Mvvm;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// Where the host's diagnostic files live, and the settings that decide whether they are written.
    /// <b>Supplied by the host, never read from <c>ExtensionConfig</c> by the view-model.</b>
    /// <para><c>ExtensionConfig.LogDirectory</c> is deliberately outside <c>RedirectTo</c>'s reach, so a
    /// view-model reading it directly would put the developer's real <c>%LOCALAPPDATA%</c> path into
    /// every screenshot artifact and make the unit tests machine-dependent. A host that supplies none
    /// simply gets no log rows, which is also the right degradation.</para>
    /// </summary>
    public sealed class SessionEnvironment
    {
        public SessionEnvironment(
            string logDirectory,
            string engineLogFile,
            string acpLogFile,
            string engineChannelLogFile,
            string renderLogFile,
            bool acpLogEnabled,
            bool engineChannelLogEnabled,
            bool renderLogEnabled,
            string? kiroAgentEngine)
        {
            LogDirectory = logDirectory;
            EngineLogFile = engineLogFile;
            AcpLogFile = acpLogFile;
            EngineChannelLogFile = engineChannelLogFile;
            RenderLogFile = renderLogFile;
            AcpLogEnabled = acpLogEnabled;
            EngineChannelLogEnabled = engineChannelLogEnabled;
            RenderLogEnabled = renderLogEnabled;
            KiroAgentEngine = kiroAgentEngine;
        }

        public string LogDirectory { get; }

        public string EngineLogFile { get; }

        public string AcpLogFile { get; }

        public string EngineChannelLogFile { get; }

        public string RenderLogFile { get; }

        public bool AcpLogEnabled { get; }

        public bool EngineChannelLogEnabled { get; }

        public bool RenderLogEnabled { get; }

        /// <summary>The configured <c>kiroAgentEngine</c>, or null when unset. <b>What was REQUESTED at
        /// launch.</b> Nothing reads back which engine is live — <c>--agent-engine</c> is a flag the CLI
        /// never echoes — so this may never be rendered as a statement about what is running.</summary>
        public string? KiroAgentEngine { get; }
    }

    /// <summary>Everything <see cref="SessionInfoViewModel.Build"/> reads. Plain fields so a test can
    /// construct one directly without a <c>ChatViewModel</c> or a WPF tree.</summary>
    public sealed class SessionInfoInputs
    {
        public bool SessionStarted { get; set; }

        public string? ProviderId { get; set; }

        public string? ProviderDisplayName { get; set; }

        public string? ModelDisplayName { get; set; }

        public string? PermissionMode { get; set; }

        public string? ConversationId { get; set; }

        public string? AgentWorkingDirectory { get; set; }

        public string? SolutionRoot { get; set; }

        /// <summary>The engine's answer, or null when it has not answered (or there is no session).</summary>
        public SessionInfoResponseView? Negotiated { get; set; }

        public SessionEnvironment? Environment { get; set; }

        /// <summary>Injected so "23 minutes ago" is testable.</summary>
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    }

    /// <summary>
    /// The engine's session-info answer, in the UI's own vocabulary. A hand-copied view rather than the
    /// IPC record so the builder's tests need no wire types, and so the tri-states arrive here with
    /// their nullability intact.
    /// </summary>
    public sealed class SessionInfoResponseView
    {
        public string? AgentProgram { get; set; }

        public string? AgentLogDirectory { get; set; }

        public bool? SupportsResume { get; set; }

        public bool? SupportsSteering { get; set; }

        public bool? SupportsImages { get; set; }

        public bool? SupportsSessionList { get; set; }

        public DateTimeOffset? OpenedAt { get; set; }

        // The mode facts (issues #269/#270). Same names as the IPC record and Core.SessionNegotiation,
        // deliberately: SessionInfoWireTests walks the three by name so a field cannot be dropped in
        // transit in silence.
        public string? ModeLabel { get; set; }

        public string? ModeId { get; set; }

        public string? ModeName { get; set; }

        public string? OpenedInModeId { get; set; }

        public string? OpenedInModeName { get; set; }

        public string? ModeOrigin { get; set; }

        public string? ModeOriginRoot { get; set; }

        public string? ModePinFailure { get; set; }

        public string? RequestedModeId { get; set; }

        public string? RequestedModeFailure { get; set; }

        /// <summary>Which backend the live session belongs to (engine-known; see the DTO). Null when
        /// the engine did not say, which every answer before this field did.</summary>
        public string? ProviderId { get; set; }

        public string? ProviderDisplayName { get; set; }
    }

    /// <summary>
    /// The read-only "what am I talking to" panel (issue #160).
    ///
    /// <para><b>The rule that decides what is a row: a row is a fact you cannot see elsewhere in the
    /// chat pane; the copied text is the complete report.</b> That is why the backend, model and
    /// permission mode appear in <see cref="CopyText"/> but are drawn nowhere — the header pickers and
    /// the status strip already show them a few inches away, and a second copy is a second thing to
    /// keep in step. The panel's job, in the issue's own words, is making a support report possible.</para>
    ///
    /// <para><b><see cref="Build"/> is a pure function.</b> Everything it needs arrives on
    /// <see cref="SessionInfoInputs"/>, including the clock, so the tests construct inputs directly and
    /// never touch <c>ChatViewModel</c> or WPF.</para>
    /// </summary>
    public sealed class SessionInfoViewModel : ObservableObject
    {
        /// <summary>What a capability row says when the agent sent no capabilities object at all. It is
        /// NOT "none": "the agent did not say" and "the agent said no" are different claims, and
        /// rendering the first as the second manufactures a confident wrong answer about the session
        /// (the <c>UsageReport</c> rule).</summary>
        public const string CapabilitiesNotReported = "the agent listed no capabilities";

        /// <summary>What a capability row says when the agent listed capabilities and offered none of
        /// the four. Deliberately distinct from <see cref="CapabilitiesNotReported"/>.</summary>
        public const string NothingOffered = "nothing offered";

        /// <summary>Shown instead of the negotiated rows before any session has opened. A fourth state:
        /// nothing has been negotiated, so even "not reported" would claim there was something to
        /// report from.</summary>
        public const string NoSessionYet = "No agent session open yet.";

        /// <summary>
        /// The engine holds a session but the host has not adopted it — a warm start (issue #19), or a
        /// transcript restored display-first and not yet reconnected. Distinct from
        /// <see cref="NoSessionYet"/>, and the distinction is not cosmetic: the panel was reporting
        /// "no agent session open yet" directly above that session's own log directory, which is the
        /// one thing a support card must never do. Told apart by whether the pull ANSWERED, which is
        /// proof the engine has a session, rather than by a flag the host maintains.
        /// </summary>
        public const string WarmNoConversation = "warm — no conversation started yet";

        /// <summary>The mode row when the backend has modes but has not said which it is in.</summary>
        public const string ModeNotReported = "not reported";

        /// <summary>The row drawn when the picker names a different backend from the one the engine is
        /// connected to: the card describes the connection, and this says the choice has moved.</summary>
        public const string SelectedBackendLabel = "Selected backend";

        /// <summary><see cref="WarmNoConversation"/> with the connected backend named, once the engine
        /// says which it is — so a card opened just after a picker change cannot read as the new
        /// backend's while the previous one's session is still the live one.</summary>
        public static string WarmNoConversationOn(string backend) =>
            "warm — " + backend + " connected, no conversation started yet";

        /// <summary>Sub-row labels of the mode row. Constants because the tests assert them and the
        /// copy text carries them: a support report is read by someone matching words.</summary>
        public const string OpenedInLabel = "Opened in";
        public const string NotAppliedLabel = "Not applied";
        public const string DefinedByLabel = "Defined by";
        public const string ConfiguredLabel = "Configured";

        private readonly string _copyText;
        private string _copyLabel = "Copy";

        private SessionInfoViewModel(IReadOnlyList<DetailRow> rows, string copyText)
        {
            Rows = rows;
            _copyText = copyText;
            CopyCommand = new RelayCommand(Copy);
        }

        public IReadOnlyList<DetailRow> Rows { get; }

        /// <summary>The whole panel as plain text, one <c>Label: Value</c> per line — the form
        /// <c>UsageDetail</c> already established. Not markdown: this lands in a bug report, where a
        /// table is noise.
        /// <para>It carries the identity rows the panel does not draw, and it carries the unknown
        /// strings verbatim — a copy that omitted them would be a different document from the one the
        /// user is looking at, and the unknowns are the interesting part.</para></summary>
        public string CopyText => _copyText;

        public ICommand CopyCommand { get; }

        /// <summary>Flips to "Copied" briefly. A clipboard write is invisible, and the panel is not the
        /// transcript, so it cannot report itself there.</summary>
        public string CopyLabel
        {
            get => _copyLabel;
            private set => SetProperty(ref _copyLabel, value);
        }

        private void Copy()
        {
            try
            {
                System.Windows.Clipboard.SetText(_copyText);
            }
            catch
            {
                // Clipboard can be transiently locked by another app; copying is best-effort.
                return;
            }

            CopyLabel = "Copied";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyLabel = "Copy";
            };
            timer.Start();
        }

        public static SessionInfoViewModel Build(SessionInfoInputs inputs)
        {
            if (inputs is null)
                throw new ArgumentNullException(nameof(inputs));

            var rows = new List<DetailRow>();
            var n = inputs.Negotiated;

            // The backend the card is ABOUT is the one the engine is connected to, when it says. The
            // picker is what the user will get next, and after a flip the two differ for as long as
            // the previous backend's warm session outlives it (measured: a card opened right after
            // flipping Claude → Kiro showed Claude's program and mode under a Kiro engine row).
            var liveProvider = n?.ProviderId;
            var pickerMoved = liveProvider is not null && inputs.ProviderId is not null
                && !string.Equals(liveProvider, inputs.ProviderId, StringComparison.OrdinalIgnoreCase);

            // --- What the agent is -------------------------------------------------------------
            // Verbatim, whatever the source said, and never re-labelled as the product: Claude's
            // agentInfo names the ADAPTER, whose version is not a Claude Code version - the adapter
            // spawns its own vendored binary and vendors its own SDK, neither of which is on the wire.
            // Rendering "@agentclientprotocol/claude-agent-acp 0.70.0" as "Claude Code 0.70.0" is the
            // tempting mistake and a false claim. Reformatting also costs the reader the ability to
            // match the string against the backend's own release notes.
            // Drawn whenever the handshake ANSWERED, started or merely warm. The gate used to be
            // SessionStarted, which conflated two kinds of row: facts about the CONNECTION (what
            // program, what it negotiated, when it opened) are true the moment the handshake
            // completes, while the conversation id is genuinely absent until something is sent. Only
            // the second belongs behind that gate - and gating the first made the card admit a warm
            // session while refusing to say what it was, which is the #82 question exactly.
            if (inputs.SessionStarted || n is not null)
            {
                rows.Add(new DetailRow(
                    "Agent program",
                    string.IsNullOrEmpty(n?.AgentProgram) ? "not reported" : n!.AgentProgram!,
                    tooltip: "What the agent reported itself to be, exactly as it said it. For Claude "
                        + "this names the ACP adapter, not the Claude Code build it runs."));
            }

            // --- What it is running as (issues #269/#270) --------------------------------------
            // The backend's OWN mode, read from the session on every pull: on Claude the permission
            // mode Code Wicket asserted at open, on Kiro the agent. Drawn when the backend has modes
            // at all (a label), or when something about them went wrong even if it has not — a pin
            // that could not land, a configured agent that was not applied — because those are the
            // facts a support report exists to carry. A backend with no modes gets no row: "none" is
            // not a mode, and absence is the honest rendering of it.
            if (n is not null && (n.ModeLabel is not null || n.ModePinFailure is not null || n.RequestedModeFailure is not null))
            {
                rows.Add(new DetailRow(
                    n.ModeLabel ?? "Mode",
                    n.ModeId is null ? ModeNotReported : DescribeMode(n.ModeId, n.ModeName),
                    tooltip: "What the agent itself reports running as, read from the session. On Claude "
                        + "Code this is the permission mode Code Wicket set when the session opened; on "
                        + "Kiro it is the agent."));

                // What the agent's own settings chose before we set anything — only where it differs,
                // because the same value twice says nothing and the difference is the whole #269 story.
                if (n.OpenedInModeId is not null && !string.Equals(n.OpenedInModeId, n.ModeId, StringComparison.Ordinal))
                {
                    rows.Add(new DetailRow(
                        OpenedInLabel, DescribeMode(n.OpenedInModeId, n.OpenedInModeName), isSubItem: true,
                        tooltip: "The mode the agent's own settings chose when the session opened, before "
                            + "Code Wicket set the one above."));
                }

                if (n.ModePinFailure is not null)
                {
                    rows.Add(new DetailRow(
                        NotAppliedLabel, n.ModePinFailure, isSubItem: true,
                        tooltip: "Code Wicket asked the agent for its ask-about-everything mode and the "
                            + "request did not land. The notice in the chat at open says what to do."));
                }

                if (DescribeOrigin(n.ModeOrigin, n.ModeOriginRoot) is { } origin)
                {
                    rows.Add(new DetailRow(
                        DefinedByLabel, origin, isSubItem: true,
                        tooltip: "Where this agent's definition, and so its tool-trust rules, came from."));
                }

                if (n.RequestedModeFailure is not null)
                {
                    rows.Add(new DetailRow(
                        ConfiguredLabel,
                        (n.RequestedModeId is null ? string.Empty : "'" + n.RequestedModeId + "' — ") + n.RequestedModeFailure,
                        isSubItem: true,
                        tooltip: "The agent this session was configured to run as was not applied; it is "
                            + "running as the one above."));
                }
            }

            // The CONFIGURED engine, drawn only for the backend it belongs to — the LIVE one when the
            // engine says which, the picker's when it does not (an older engine, or nothing connected). A Kiro launch flag under
            // a Claude session is a different over-claim, and it is exactly what a card opened after a
            // picker flip drew. Nothing reads back which engine is live, so the parenthetical is the
            // row: "v3" alone would assert a fact we have not got. Drawn even when unset, because
            // changing this setting silently changes which conversations exist, and a reader chasing
            // that needs "not set" stated rather than absent.
            if (string.Equals(liveProvider ?? inputs.ProviderId, "kiro", StringComparison.OrdinalIgnoreCase))
            {
                var engine = inputs.Environment?.KiroAgentEngine;
                rows.Add(new DetailRow(
                    "Agent engine",
                    string.IsNullOrWhiteSpace(engine)
                        ? "kiro-cli default (not set)"
                        : engine!.Trim() + " (requested at launch)",
                    tooltip: "The engine Code Wicket asked for at launch. kiro-cli does not report "
                        + "which engine it is actually running."));
            }

            // --- This conversation -------------------------------------------------------------
            if (!inputs.SessionStarted)
            {
                // Two different silences, and merging them is what shipped: a null pull means nothing
                // is running, while an ANSWERED pull means the engine has a session this pane has not
                // adopted. The rows below stay hidden either way - they describe the user's
                // conversation, which has not begun - but the backend's own log directory further down
                // is drawn from that same answer, so claiming there is no session contradicts it.
                rows.Add(new DetailRow(
                    "Session",
                    n is null ? NoSessionYet
                        : (n.ProviderDisplayName ?? n.ProviderId) is { } connected ? WarmNoConversationOn(connected)
                        : WarmNoConversation,
                    tooltip: n is null
                        ? null
                        : "The backend is connected and ready; nothing has been sent to it yet."));
            }

            // The picker has moved since this session connected. Said on its own row rather than by
            // re-labelling the rows above, which describe a real, still-connected session: the next
            // message is what starts the new one (or the warm start already under way replaces it).
            if (pickerMoved)
            {
                rows.Add(new DetailRow(
                    SelectedBackendLabel,
                    (inputs.ProviderDisplayName ?? inputs.ProviderId!)
                        + " — the picker changed after this session connected; the rows above describe "
                        + (n!.ProviderDisplayName ?? n.ProviderId!)
                        + ", and your next message uses " + (inputs.ProviderDisplayName ?? inputs.ProviderId!) + ".",
                    tooltip: "A backend switch takes effect on the next message. Until then the engine "
                        + "still holds the previous backend's session, and that is what this card reports."));
            }
            // The ONLY row that belongs behind SessionStarted: an id the backend's store is keyed
            // by does not exist until something has been sent.
            if (inputs.SessionStarted && !string.IsNullOrEmpty(inputs.ConversationId))
            {
                rows.Add(new DetailRow(
                    "Conversation", inputs.ConversationId!, isMonospace: true,
                    tooltip: "The id the backend's own store is keyed by."));
            }

            // When the CONNECTION opened, which a warm session has as much as a busy one.
            if (n?.OpenedAt is DateTimeOffset opened)
            {
                rows.Add(new DetailRow(
                    "Backend session opened",
                    DescribeOpened(opened, inputs.Now),
                    tooltip: "When the connection to the agent was opened. A conversation restored "
                        + "from history can be older than this."));
            }

            // Drawn for a started session even when the pull has not landed, where it reads
            // "not read yet" - a real state, and distinct from every answer the handshake can give.
            if (inputs.SessionStarted || n is not null)
            {
                rows.Add(new DetailRow("Negotiated", DescribeNegotiated(n)));
            }

            // --- Where it is running -----------------------------------------------------------
            if (!string.IsNullOrEmpty(inputs.AgentWorkingDirectory))
            {
                rows.Add(new DetailRow(
                    "Working directory", inputs.AgentWorkingDirectory!, isMonospace: true));

                // Only when they differ - the case that costs someone an afternoon when an edit lands
                // somewhere unexpected (issue #54). Same comparison the transcript notice makes.
                if (!string.IsNullOrEmpty(inputs.SolutionRoot)
                    && !string.Equals(
                        inputs.AgentWorkingDirectory!.TrimEnd('\\', '/'),
                        inputs.SolutionRoot!.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new DetailRow(
                        "Solution", inputs.SolutionRoot!, isSubItem: true, isMonospace: true,
                        tooltip: "The agent is not running in the solution folder. Relative paths it "
                            + "reports are rooted at its working directory, not here."));
                }
            }

            // --- Where to look when something is wrong -----------------------------------------
            if (inputs.Environment is SessionEnvironment env)
            {
                // engine.log FIRST and with its reason on the row: it is the one that is always
                // written, and issue #82 was a user who could not find it. No File.Exists per row -
                // four stats on the UI thread over a directory that has been measured at 365 files /
                // 42 MB, for an answer that is stale by the time it is drawn. The row states the
                // policy, which is a fact.
                rows.Add(new DetailRow(
                    "Logs", "engine.log — always written, read this first", isMonospace: true));
                rows.Add(new DetailRow(
                    string.Empty, DescribeOptionalLog("acp.log", env.AcpLogEnabled, "Log agent protocol frames"),
                    isSubItem: true, isMonospace: true));
                rows.Add(new DetailRow(
                    string.Empty,
                    DescribeOptionalLog("engine-channel.log", env.EngineChannelLogEnabled, "Log engine channel bytes"),
                    isSubItem: true, isMonospace: true));
                rows.Add(new DetailRow(
                    string.Empty,
                    DescribeOptionalLog("render.log", env.RenderLogEnabled, "Log chat rendering cost"),
                    isSubItem: true, isMonospace: true));
                rows.Add(new DetailRow(
                    "Log folder", env.LogDirectory, isMonospace: true));
            }

            // The BACKEND's own logs, never merged into ours: a surface we neither own nor write, and
            // the place left to look when our own logs are exhausted.
            if (!string.IsNullOrEmpty(n?.AgentLogDirectory))
            {
                rows.Add(new DetailRow(
                    "Agent's own logs", n!.AgentLogDirectory!, isMonospace: true,
                    tooltip: "Written by the backend itself, not by Code Wicket."));
            }

            return new SessionInfoViewModel(rows, BuildCopyText(inputs, rows));
        }

        /// <summary>
        /// The four negotiated capabilities as one line: what was offered, then what was not, in
        /// parentheses. Four states, and the first two are the ones that must not merge — see
        /// <see cref="CapabilitiesNotReported"/>.
        /// </summary>
        private static string DescribeNegotiated(SessionInfoResponseView? n)
        {
            if (n is null)
                return "not read yet";

            // No capabilities object at all. Not "none offered": the agent never addressed the
            // question, and saying it declined is a claim it did not make.
            if (n.SupportsResume is null && n.SupportsSteering is null
                && n.SupportsImages is null && n.SupportsSessionList is null)
            {
                return CapabilitiesNotReported;
            }

            var offered = new List<string>();
            var withheld = new List<string>();
            Sort(n.SupportsResume, "resume", offered, withheld);
            Sort(n.SupportsImages, "images", offered, withheld);
            Sort(n.SupportsSteering, "mid-turn messages", offered, withheld);
            Sort(n.SupportsSessionList, "conversation list", offered, withheld);

            if (offered.Count == 0)
                return NothingOffered;

            var text = string.Join(", ", offered);
            return withheld.Count == 0 ? text : text + " (no " + string.Join(", ", withheld) + ")";

            static void Sort(bool? value, string name, List<string> yes, List<string> no)
            {
                if (value == true)
                    yes.Add(name);
                else if (value == false)
                    no.Add(name);

                // A null is named in neither list: the agent said nothing about it, and inventing a
                // side for it is the whole failure mode this row exists to avoid.
            }
        }

        /// <summary>The name, with the id beside it when they differ: "Manual (default)". The name is
        /// what every other surface of the backend says; the id is what its logs and ours say.</summary>
        private static string DescribeMode(string id, string? name) =>
            string.IsNullOrEmpty(name) || string.Equals(name, id, StringComparison.Ordinal)
                ? id
                : name + " (" + id + ")";

        /// <summary>
        /// The origin sentence, or null for the one origin that is the expectation (bundled — the
        /// backend's own) and for an unreported one. A repository-defined agent is the case that
        /// changes what a reader should conclude about the permission record: its trust rules came
        /// with the checkout.
        /// </summary>
        private static string? DescribeOrigin(string? origin, string? root)
        {
            if (string.IsNullOrEmpty(origin))
                return null;
            if (origin!.Equals("workspace", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(root) ? "the repository" : "the repository under " + root;
            if (origin.Equals("user", StringComparison.OrdinalIgnoreCase))
                return "your global agents folder";
            if (origin.Equals("client", StringComparison.OrdinalIgnoreCase))
                return "Code Wicket";
            return null;
        }

        private static string DescribeOptionalLog(string file, bool enabled, string settingName) =>
            enabled ? file : file + " — off (" + settingName + ")";

        private static string DescribeOpened(DateTimeOffset opened, DateTimeOffset now)
        {
            var local = opened.ToLocalTime();
            var clock = local.ToString("HH:mm", CultureInfo.CurrentCulture);
            var elapsed = now - opened;

            if (elapsed < TimeSpan.Zero)
                return clock;
            if (elapsed < TimeSpan.FromMinutes(1))
                return clock + " (just now)";
            if (elapsed < TimeSpan.FromHours(1))
                return clock + " (" + ((int)elapsed.TotalMinutes).ToString(CultureInfo.CurrentCulture) + " min ago)";
            if (elapsed < TimeSpan.FromDays(1))
                return clock + " (" + ((int)elapsed.TotalHours).ToString(CultureInfo.CurrentCulture) + " h ago)";

            return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        /// <summary>
        /// The complete report. Built from the same inputs as <see cref="Rows"/> so the two cannot
        /// drift, and deliberately NOT from <see cref="Rows"/> itself — it carries the identity facts
        /// the panel does not draw because they are already on screen.
        /// </summary>
        private static string BuildCopyText(SessionInfoInputs inputs, IReadOnlyList<DetailRow> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Code Wicket session");

            // Selected vs connected, when they differ: a report pasted from a card opened just after a
            // picker flip must not name one backend beside the other's facts.
            var selected = inputs.ProviderDisplayName ?? inputs.ProviderId;
            var connected = inputs.Negotiated?.ProviderDisplayName ?? inputs.Negotiated?.ProviderId;
            var backend = connected is not null && selected is not null
                && !string.Equals(connected, selected, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(inputs.Negotiated?.ProviderId, inputs.ProviderId, StringComparison.OrdinalIgnoreCase)
                    ? selected + " (selected); " + connected + " (connected)"
                    : selected;
            Append(sb, "Backend", backend);
            Append(sb, "Model", inputs.ModelDisplayName);
            Append(sb, "Permission mode", inputs.PermissionMode);

            foreach (var row in rows)
            {
                var label = string.IsNullOrEmpty(row.Label)
                    ? "  "
                    : (row.IsSubItem ? "  " + row.Label : row.Label);
                Append(sb, label, row.Value);
            }

            return sb.ToString().TrimEnd();

            static void Append(StringBuilder sb, string label, string? value)
            {
                if (value is null)
                    return;
                sb.Append(label).Append(": ").AppendLine(value);
            }
        }
    }
}
