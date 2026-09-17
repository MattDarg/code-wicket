using System;
using System.Collections.Generic;
using System.Linq;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.Mvvm;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// Which half of the history picker is on show. Two lists answered by two different things — a
    /// directory read and a CLI spawn — and until 2026-09-02 they were stacked in one scrolling
    /// surface, so on a workspace with a lot of saved conversations the agent's own history sat below
    /// every one of them and was reached only by scrolling past the lot.
    /// <para>The choice is a view-model fact rather than view state, so the fallback below it can be
    /// tested: <c>ChatViewModel.HistoryTab</c> coerces back to <see cref="Saved"/> whenever there is
    /// no second tab to be on, and a pane with no rows, no header and no explanation is exactly what
    /// that prevents.</para>
    /// </summary>
    public enum HistoryTab
    {
        /// <summary>Conversations this extension saved for the workspace.</summary>
        Saved,

        /// <summary>Conversations the backend's own store holds that we do not (issue #108).</summary>
        Backend,
    }

    /// <summary>
    /// One saved conversation in the history picker. <see cref="Title"/> is editable inline (renames
    /// persist on commit via the parent's callback); Load/Delete are wired to the parent view-model.
    /// </summary>
    public sealed class SessionSummaryViewModel : ObservableObject
    {
        private readonly Action<string, string> _rename;
        private string _title;
        private bool _isEditing;

        public SessionSummaryViewModel(
            SessionSummary summary, bool isCurrent,
            Action<string> load, Action<string> delete, Action<string, string> rename,
            Func<string, string>? resolveProviderName = null)
        {
            Id = summary.Id;
            _title = summary.Title;
            UpdatedUtc = summary.UpdatedUtc;
            MessageCount = summary.MessageCount;
            SizeBytes = summary.SizeBytes;
            LastContextPercent = summary.LastContextPercent;
            IsCurrent = isCurrent;
            AgentLabel = BuildAgentLabel(summary.ProviderIds, resolveProviderName);
            _rename = rename;

            LoadCommand = new RelayCommand(() => load(Id));
            DeleteCommand = new RelayCommand(() => delete(Id));
            RenameCommand = new RelayCommand(() => IsEditing = true);
        }

        /// <summary>True while the title is being edited inline (swaps the label for an edit box).</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set => SetProperty(ref _isEditing, value);
        }

        public string Id { get; }
        public DateTime UpdatedUtc { get; }

        /// <summary>User turns in the conversation, or null when the saved session never recorded one
        /// (see <see cref="SessionSummary.MessageCount"/>) - the subtitle omits the segment rather than
        /// showing a zero.</summary>
        public int? MessageCount { get; }
        public long SizeBytes { get; }

        /// <summary>Context fill when the conversation was last active; null when never reported.</summary>
        public double? LastContextPercent { get; }

        /// <summary>True for the conversation currently shown, so the picker can highlight it.</summary>
        public bool IsCurrent { get; }

        /// <summary>The backend(s) this conversation ran on, shown as a chip in the picker — a single name
        /// (e.g. "Claude Code"), or "Kiro → Claude Code" when a Summary-resume switched agents. Empty for
        /// sessions saved before the agent was tracked (the chip is hidden).</summary>
        public string AgentLabel { get; }

        /// <summary>Drives the agent chip's visibility (hidden when the agent is unknown).</summary>
        public bool HasAgentLabel => AgentLabel.Length > 0;

        // Maps the stored provider ids to display names (falling back to the raw id) and joins a
        // multi-agent conversation with an arrow to show the hand-off order.
        private static string BuildAgentLabel(IReadOnlyList<string> providerIds, Func<string, string>? resolve)
        {
            if (providerIds is null || providerIds.Count == 0)
                return string.Empty;
            var names = providerIds.Select(id => resolve?.Invoke(id) ?? id);
            return string.Join(" → ", names);
        }

        /// <summary>Editable session name. Bound with a LostFocus trigger, so a rename persists once.</summary>
        public string Title
        {
            get => _title;
            set
            {
                var trimmed = string.IsNullOrWhiteSpace(value) ? _title : value.Trim();
                if (SetProperty(ref _title, trimmed))
                    _rename(Id, _title);
            }
        }

        /// <summary>
        /// Secondary line: when it was last updated, how many turns, and its on-disk size. The turn
        /// count is dropped when it was never recorded - a segment reading "0 msgs" beside a title
        /// taken from the conversation's own first message would contradict itself.
        /// </summary>
        public string Subtitle =>
            UpdatedUtc.ToLocalTime().ToString("g")
            + (MessageCount is { } messages
                ? "  ·  " + messages + (messages == 1 ? " msg" : " msgs")
                : string.Empty)
            + (LastContextPercent is { } fill
                ? "  ·  " + FormatPercent(fill) + " context"
                : "  ·  " + FormatSize(SizeBytes));

        // Whole percent: this is where the conversation was left, not a live readout, and a decimal
        // would imply a precision the number does not have by the time anyone reads it.
        private static string FormatPercent(double percent) =>
            Math.Round(percent).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%";

        // Compact human-readable byte size (B / KB / MB).
        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            double kb = bytes / 1024.0;
            return kb < 1024 ? kb.ToString("0.#") + " KB" : (kb / 1024.0).ToString("0.#") + " MB";
        }

        public RelayCommand LoadCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand RenameCommand { get; }
    }

    /// <summary>
    /// One conversation the backend's own store holds that we do NOT hold a transcript for - a
    /// session begun in the terminal, offered so it can be picked up here (issue #108).
    /// <para>Deliberately not a <see cref="SessionSummaryViewModel"/>. Rename and delete do not apply
    /// to a conversation in another program's store, there is no local size or message count to show,
    /// and its click does something materially heavier than opening a saved transcript: it starts a
    /// backend session and loads the whole conversation. A shared type would have to answer for all of
    /// that with null checks at every property.</para>
    /// </summary>
    public sealed class BackendSessionItemViewModel : ObservableObject
    {
        private readonly Action<BackendSessionItemViewModel> _import;
        private bool _isImporting;
        private string? _disambiguator;

        public BackendSessionItemViewModel(
            BackendSessionDto session, string providerId, string providerName,
            Action<BackendSessionItemViewModel> import)
        {
            Id = session.Id;
            ProviderId = providerId;

            // The backend's name, and NOTHING about where the conversation came from. It said
            // "<name> CLI" until 2026-08-26, which asserted a provenance nothing on the wire carries -
            // see the section header's comment in ChatView.xaml for the measurement. The heading says
            // which STORE these came from; the chip answers which BACKEND, exactly as it does on a
            // saved row, and the section they sit in is what tells the two apart.
            AgentLabel = providerName;
            UpdatedUtc = session.UpdatedUtc;
            _import = import;

            // The backend names its own conversations and the two we ship name them differently: Claude
            // from a generated summary, Kiro from the first user message verbatim. Either can be absent,
            // and an id is not a name - so say what it is rather than showing a guid as a title.
            //
            // Kiro's "verbatim" is the trap: it names a session from its first prompt, and ours begins
            // with the workspace-context block - so every conversation this extension started is called
            // "<workspace-context>" in its store. Measured on 37 kiro-cli sessions: both of the ones
            // ever prompted are ours, and both are named that. Showing it back would be repeating our
            // own plumbing to the user as if it were their words.
            Title = string.IsNullOrWhiteSpace(session.Title) || HostPromptBlocks.IsOurFraming(session.Title)
                ? "Untitled conversation"
                : session.Title!;

            ImportCommand = new RelayCommand(() => _import(this), () => !IsImporting);
        }

        public string Id { get; }

        /// <summary>
        /// Which backend holds this conversation. Carried per ROW rather than read from the picker,
        /// because the section spans every backend: importing must open a session on the backend that
        /// actually has the conversation, not on whichever one the composer happens to be set to.
        /// </summary>
        public string ProviderId { get; }

        public string Title { get; }

        public string AgentLabel { get; }

        public DateTime? UpdatedUtc { get; }

        /// <summary>
        /// Set while this row's import is in flight. An import opens a session and replays the whole
        /// conversation - seconds, not milliseconds - so the row has to say it is working or the user
        /// clicks it again.
        /// </summary>
        public bool IsImporting
        {
            get => _isImporting;
            set
            {
                if (SetProperty(ref _isImporting, value))
                {
                    OnPropertyChanged(nameof(Subtitle));
                    ImportCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// A short piece of the backend's own id, set by the list that holds this row and ONLY when
        /// another row from the same backend carries the same title.
        /// <para>Deliberately not part of <see cref="Title"/> and deliberately not always shown: an id
        /// DISAMBIGUATES WITHOUT INFORMING - it says two rows differ, never which one you want - so it
        /// earns its space only where the ambiguity is real, and is noise on every other row. It is no
        /// answer at all to the neighbouring complaint that some titles are useless: a session whose
        /// first message was <c>/clear</c> is titled "/clear", and "/clear  ·  a1b2c3d4" is exactly as
        /// meaningless. That one is not fixable from what the wire carries.</para>
        /// </summary>
        public string? Disambiguator
        {
            get => _disambiguator;
            set
            {
                if (SetProperty(ref _disambiguator, value))
                    OnPropertyChanged(nameof(Subtitle));
            }
        }

        /// <summary>
        /// The backend's own timestamp, or a plain statement that it did not report one - never a
        /// fabricated date, since the list is ordered by this. Says nothing about WHICH backend: the
        /// chip beside it does, the same way a saved row's chip does.
        /// </summary>
        public string Subtitle
        {
            get
            {
                if (IsImporting)
                    return "Opening…";

                var when = UpdatedUtc is { } stamp
                    ? stamp.ToLocalTime().ToString("g")
                    : "no date reported";

                return Disambiguator is { Length: > 0 } id ? when + "  ·  " + id : when;
            }
        }

        /// <summary>
        /// What the date on this row actually means - and it is not what the field is called.
        /// <para>ACP's own schema documents <c>updatedAt</c> as "ISO 8601 timestamp of last activity",
        /// but claude-agent-acp fills it from the session FILE's mtime (the Agent SDK's
        /// <c>lastModified: e.mtime</c>, mapped straight through as
        /// <c>new Date(session.lastModified).toISOString()</c>). So ANYTHING that rewrites the file
        /// moves it, and the conversation need not have been touched at all.</para>
        /// <para>Measured 2026-08-26: a cloud-sync pass restamped three session files across two
        /// project folders within 270ms of each other, none spoken to since the previous day - putting
        /// a conversation whose last message was 22 hours old SECOND in a list sorted newest-first.
        /// That is an upstream contract violation rather than a limit of the protocol, so the fix is a
        /// bug report; what belongs here is not implying a precision the number does not have.</para>
        /// <para>Null when no date was reported, so the row falls back to the button's own tooltip
        /// rather than explaining a number that is not on screen.</para>
        /// </summary>
        public string? TimeTooltip =>
            UpdatedUtc is null
                ? null
                : "When this conversation's file was last changed. That is not always when it was "
                  + "last used - background syncing moves it too.";

        public RelayCommand ImportCommand { get; }
    }

    /// <summary>
    /// The banner above the composer that asks how a restored conversation should be continued. It
    /// serves three moments, and every button it can draw is optional because they do not overlap:
    ///
    /// <list type="bullet">
    /// <item><b>Before the send</b> (<see cref="Choice"/>): reload the backend's full context, or start
    /// fresh with a summary of the prior transcript — cheaper for a big conversation. Cancel puts the
    /// message back in the composer, nothing having been started.</item>
    /// <item><b>After the summary failed</b> (<see cref="SummaryFailed"/>, issue #84): the summary the
    /// user asked for could not be produced, so the two remaining real choices are offered instead.
    /// There is no Cancel here — by this point the message is in the transcript and the log, and the
    /// question is only which context it goes out with.</item>
    /// <item><b>After the backend refused the reload</b> (<see cref="ResumeRefused"/>, issue #268): the
    /// mirror of the one above, on the other resume route. Same question, opposite pair — the recap is
    /// what is left here, and the full reload is the thing that just failed.</item>
    /// </list>
    /// </summary>
    public sealed class ResumeChoiceViewModel : ObservableObject
    {
        // Everything arrives through this one constructor rather than through object-initializer
        // properties: `init` accessors need IsExternalInit, which this project deliberately does not
        // carry (it multi-targets net472, where the polyfill would have to be added).
        private ResumeChoiceViewModel(
            string heading, string detail, string? reason,
            bool allowFull, bool allowSummary, bool allowFresh, bool allowCancel,
            bool summaryRecommended, string freshLabel, string freshTooltip,
            System.Action? resumeFull, System.Action? resumeSummary,
            System.Action? resumeFresh, System.Action? cancel,
            string fullLabel = ResumeFullLabel, string? fullTooltip = null,
            string? summaryTooltip = null)
        {
            SummaryTooltip = summaryTooltip;
            Heading = heading;
            Detail = detail;
            Reason = reason;
            AllowFull = allowFull;
            AllowSummary = allowSummary;
            AllowFresh = allowFresh;
            AllowCancel = allowCancel;
            SummaryRecommended = summaryRecommended;
            FreshLabel = freshLabel;
            FreshTooltip = freshTooltip;
            FullLabel = fullLabel;
            FullTooltip = fullTooltip;
            ResumeFullCommand = new RelayCommand(resumeFull ?? (() => { }));
            ResumeSummaryCommand = new RelayCommand(resumeSummary ?? (() => { }));
            ResumeFreshCommand = new RelayCommand(resumeFresh ?? (() => { }));
            CancelCommand = new RelayCommand(cancel ?? (() => { }));
        }

        /// <summary>The send-time choice: full context vs a summary, with a way out.</summary>
        public static ResumeChoiceViewModel Choice(
            string detail, bool allowFull, bool summaryRecommended,
            System.Action resumeFull, System.Action resumeSummary, System.Action cancel) =>
            new("Continue this conversation?", detail, reason: null,
                allowFull: allowFull, allowSummary: true, allowFresh: false, allowCancel: true,
                summaryRecommended: summaryRecommended,
                freshLabel: StartFreshLabel, freshTooltip: NoHistoryTooltip,
                resumeFull: resumeFull, resumeSummary: resumeSummary, resumeFresh: null, cancel: cancel,
                summaryTooltip: RecapTooltip);

        /// <summary>
        /// The summary resume the user chose could not be produced (issue #84). Offers what is actually
        /// left — reload the full context where that is possible, or go on without the history — rather
        /// than proceeding as if nothing had been asked for, which is what used to happen: the recap
        /// silently did not go and the conversation continued into a session with no memory of it.
        /// <para>Summary is deliberately not re-offered: it is the thing that just failed, and a button
        /// that retries it without anything having changed is an invitation to a second wait ending the
        /// same way.</para>
        /// </summary>
        public static ResumeChoiceViewModel SummaryFailed(
            string detail, bool allowFull, System.Action resumeFull, System.Action startFresh) =>
            new("Couldn't summarize this conversation", detail, reason: null,
                allowFull: allowFull, allowSummary: false, allowFresh: true, allowCancel: false,
                summaryRecommended: false,
                freshLabel: StartFreshLabel, freshTooltip: NoHistoryTooltip,
                resumeFull: resumeFull, resumeSummary: null, resumeFresh: startFresh, cancel: null);

        /// <summary>
        /// The backend refused to reload the conversation, so the send is already sitting on a new,
        /// empty session (issue #268). The mirror of <see cref="SummaryFailed"/>: the same question
        /// asked from the other resume route, so the same two answers are offered with their roles
        /// swapped — a recap built from OUR copy of the transcript is what is left, and the full
        /// reload is the thing that just failed and so is not re-offered.
        /// <para><b>The fresh button says "Send anyway", not "Start fresh".</b> By the time this
        /// banner is up the fresh session exists — it is what the refusal fell through to — so a label
        /// promising to start one describes a step that has already happened, and invites the reading
        /// that nothing is under way yet and there is still something to back out of.</para>
        /// </summary>
        public static ResumeChoiceViewModel ResumeRefused(
            string detail, string reason, bool allowSummary,
            System.Action resumeSummary, System.Action sendAnyway) =>
            new("Couldn't reload this conversation", detail, reason,
                allowFull: false, allowSummary: allowSummary, allowFresh: true, allowCancel: false,
                summaryRecommended: allowSummary,
                freshLabel: "Send anyway", freshTooltip: NoHistoryTooltip,
                resumeFull: null, resumeSummary: resumeSummary, resumeFresh: sendAnyway, cancel: null,
                summaryTooltip: RecapTooltip);

        /// <summary>
        /// The conversation's working directory has moved out from under it (issue #185): the marker
        /// walk or a project settings file would now run the agent somewhere other than where this
        /// conversation's history lives. Asked BEFORE anything is recorded, which is why it has a
        /// Cancel, and it offers the two outcomes the unilateral fork could not: run it where it
        /// lives (full fidelity, this conversation only), or carry a recap here.
        /// <para>The full-reload slot is relabelled rather than a fourth button added: it IS the full
        /// reload, in the one directory a full reload works from.</para>
        /// </summary>
        public static ResumeChoiceViewModel RootMoved(
            string detail, System.Action openWhereItLives, System.Action resumeSummary,
            System.Action startFresh, System.Action cancel) =>
            new("This conversation ran in a different directory", detail, reason: null,
                allowFull: true, allowSummary: true, allowFresh: true, allowCancel: true,
                summaryRecommended: false,
                freshLabel: StartFreshLabel,
                freshTooltip: "Start a new conversation here; this one stays in the history picker",
                resumeFull: openWhereItLives, resumeSummary: resumeSummary,
                resumeFresh: startFresh, cancel: cancel,
                fullLabel: "Open it where its history lives",
                fullTooltip: "Run this conversation in the directory it was made in, with its full "
                             + "context — for this open only; you will be asked again next time",
                summaryTooltip: "Start here, sending a short recap of the conversation above with your "
                                + "message instead of its full context (counts toward usage)");

        private const string StartFreshLabel = "Start fresh";

        private const string ResumeFullLabel = "Resume full context";

        private const string RecapTooltip =
            "Send a short recap of the conversation above with your message, instead of its full "
            + "context (counts toward usage)";

        private const string NoHistoryTooltip =
            "Send this message with no history of the conversation above it";

        /// <summary>The banner's title. Two shapes ask two different questions, so it is not a constant.</summary>
        public string Heading { get; }

        public string Detail { get; }

        /// <summary>
        /// The backend's own account of what went wrong, drawn apart from <see cref="Detail"/> and in
        /// the error colour; null on the banners that have none.
        /// <para>Its own line because it is the only sentence here that is not ours. Run into the
        /// muted paragraph it read as a footnote — the one failure in the text, set in the quietest
        /// thing on the banner — while it is the fact the user is actually deciding on, and the one
        /// they will paste into a bug report. Colour is not the only carrier: it is a separate line
        /// and it quotes the backend, so it still reads as the odd one out in a mono theme.</para>
        /// </summary>
        public string? Reason { get; }

        /// <summary>True when full-context resume is possible (native session/load on a matching backend);
        /// false hides the "Resume full context" button.</summary>
        public bool AllowFull { get; }

        /// <summary>True while a summary resume is still something to offer — i.e. before it was tried.</summary>
        public bool AllowSummary { get; }

        /// <summary>True when going on without the prior conversation is one of the offered choices.</summary>
        public bool AllowFresh { get; }

        /// <summary>True while backing out is still possible — before the message has been committed.</summary>
        public bool AllowCancel { get; }

        /// <summary>True for a large conversation, where the summary option is the suggested default.</summary>
        public bool SummaryRecommended { get; }

        /// <summary>
        /// What the go-on-without-the-history button says. Bound rather than fixed in the XAML because
        /// the two banners that draw it stand at opposite sides of the session start: before it the
        /// button starts a fresh session, after it the fresh session is already there and the button
        /// only decides what the message carries into it.
        /// </summary>
        public string FreshLabel { get; }

        public string FreshTooltip { get; }

        /// <summary>What the full-reload button says: "Resume full context" everywhere but the
        /// moved-root banner, where the same action runs the conversation where its history lives.</summary>
        public string FullLabel { get; }

        public string? FullTooltip { get; }

        /// <summary>What the recap button does, per banner: it means "instead of the full context"
        /// on one and "instead of nothing" on another, and a button the other two beside it explain
        /// themselves on read as the one nobody had thought about.</summary>
        public string? SummaryTooltip { get; }

        public RelayCommand ResumeFullCommand { get; }
        public RelayCommand ResumeSummaryCommand { get; }

        /// <summary>Continues with no history of the conversation above it.</summary>
        public RelayCommand ResumeFreshCommand { get; }

        /// <summary>Backs out of the resume: dismisses the banner and returns the pending message to the input.</summary>
        public RelayCommand CancelCommand { get; }
    }
}
