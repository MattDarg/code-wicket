using System;
using System.Linq;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The session information panel's builder (issue #160). <c>SessionInfoViewModel.Build</c> is a
    /// pure function over <see cref="SessionInfoInputs"/>, so every one of these constructs inputs
    /// directly and never touches <c>ChatViewModel</c> or a WPF tree.
    ///
    /// <para>The panel's whole reason to exist is that it says what a session actually negotiated, so
    /// the tests that matter most here are the ones pinning what it may NOT say: a capability nobody
    /// reported must not read as a refusal, a requested engine must not read as a running one, and an
    /// adapter's version must not be re-labelled as the product's.</para>
    /// </summary>
    public sealed class SessionInfoTests
    {
        private static SessionEnvironment Env(
            bool acp = false, bool channel = false, bool render = false, string? kiroEngine = null) =>
            new SessionEnvironment(
                logDirectory: @"X:\scratch\logs",
                engineLogFile: @"X:\scratch\logs\engine.log",
                acpLogFile: @"X:\scratch\logs\acp.log",
                engineChannelLogFile: @"X:\scratch\logs\engine-channel.log",
                renderLogFile: @"X:\scratch\logs\render.log",
                acpLogEnabled: acp,
                engineChannelLogEnabled: channel,
                renderLogEnabled: render,
                kiroAgentEngine: kiroEngine);

        private static SessionInfoInputs Live(SessionInfoResponseView? negotiated = null) =>
            Session(negotiated ?? All(true));

        /// <summary>A live session whose info the engine has not answered for yet.</summary>
        private static SessionInfoInputs LiveUnread() => Session(null);

        private static SessionInfoInputs Session(SessionInfoResponseView? negotiated) =>
            new SessionInfoInputs
            {
                SessionStarted = true,
                ProviderId = "kiro",
                ProviderDisplayName = "Kiro",
                ModelDisplayName = "Claude Sonnet 4",
                PermissionMode = "Accept edits",
                ConversationId = "sess_abc123",
                Negotiated = negotiated,
                Now = DateTimeOffset.Parse("2026-09-02T14:55:00Z"),
            };

        private static SessionInfoResponseView All(bool? value) => new SessionInfoResponseView
        {
            SupportsResume = value,
            SupportsSteering = value,
            SupportsImages = value,
            SupportsSessionList = value,
        };

        private static string ValueOf(SessionInfoViewModel vm, string label) =>
            vm.Rows.Single(r => r.Label == label).Value;

        private static bool Has(SessionInfoViewModel vm, string label) =>
            vm.Rows.Any(r => r.Label == label);

        /// <summary>
        /// The check this whole surface lives or dies on. "The agent listed no capabilities" and
        /// "the agent offered none of them" are different claims about the session, and collapsing the
        /// first into the second manufactures a confident wrong answer — the <c>UsageReport</c> rule.
        /// A two-way rendering (<c>x == true ? "Yes" : "No"</c>) fails here.
        /// </summary>
        [Fact]
        public void Negotiated_row_separates_not_reported_from_nothing_offered()
        {
            var notReported = ValueOf(SessionInfoViewModel.Build(Live(All(null))), "Negotiated");
            var noneOffered = ValueOf(SessionInfoViewModel.Build(Live(All(false))), "Negotiated");
            var allOffered = ValueOf(SessionInfoViewModel.Build(Live(All(true))), "Negotiated");

            Assert.Equal(SessionInfoViewModel.CapabilitiesNotReported, notReported);
            Assert.Equal(SessionInfoViewModel.NothingOffered, noneOffered);

            // Three distinct answers, not two dressed up as three.
            Assert.NotEqual(notReported, noneOffered);
            Assert.NotEqual(noneOffered, allOffered);
            Assert.NotEqual(notReported, allOffered);
        }

        /// <summary>
        /// Offered first, withheld in parentheses — and a capability the agent said nothing about is
        /// named in NEITHER list, because putting it on a side is inventing an answer.
        /// </summary>
        [Fact]
        public void Negotiated_row_names_what_was_offered_and_what_was_not()
        {
            var vm = SessionInfoViewModel.Build(Live(new SessionInfoResponseView
            {
                SupportsResume = true,
                SupportsImages = true,
                SupportsSteering = false,
                SupportsSessionList = null,
            }));

            var value = ValueOf(vm, "Negotiated");
            Assert.Equal("resume, images (no mid-turn messages)", value);
            Assert.DoesNotContain("conversation list", value);
        }

        /// <summary>
        /// The fourth state, and the one most easily collapsed into "not reported". Before a session
        /// opens nothing has been negotiated, so there is nothing to have reported FROM — the rows are
        /// omitted rather than rendered as unknowns.
        /// </summary>
        [Fact]
        public void No_live_session_omits_the_negotiated_and_conversation_rows()
        {
            var vm = SessionInfoViewModel.Build(new SessionInfoInputs
            {
                SessionStarted = false,
                ProviderId = "kiro",
                ConversationId = "sess_abc123",
            });

            Assert.False(Has(vm, "Negotiated"));
            Assert.False(Has(vm, "Conversation"));
            Assert.False(Has(vm, "Agent program"));
            Assert.Equal(SessionInfoViewModel.NoSessionYet, ValueOf(vm, "Session"));
        }

        /// <summary>
        /// Verbatim, whatever the source said. Claude's <c>agentInfo</c> names the ADAPTER, and its
        /// version is not a Claude Code version — the adapter runs its own vendored binary — so
        /// substituting the provider's display name would state something false.
        /// </summary>
        [Fact]
        public void Agent_program_row_is_verbatim()
        {
            const string reported = "@agentclientprotocol/claude-agent-acp 0.70.0";
            var inputs = Live(new SessionInfoResponseView { AgentProgram = reported });
            inputs.ProviderId = "claude-code";
            inputs.ProviderDisplayName = "Claude Code";

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.Equal(reported, ValueOf(vm, "Agent program"));
            Assert.DoesNotContain("Claude Code 0.70", ValueOf(vm, "Agent program"));
        }

        [Fact]
        public void Agent_program_row_says_not_reported_rather_than_vanishing()
        {
            var vm = SessionInfoViewModel.Build(Live(new SessionInfoResponseView()));
            Assert.Equal("not reported", ValueOf(vm, "Agent program"));
        }

        /// <summary>
        /// Nothing reads back which engine is live, so the row may only ever describe the request. Both
        /// halves are pinned: a bare "v3" would assert a fact we have not got, and drawing the row on a
        /// non-Kiro session would attach a Kiro launch flag to a backend that never saw it.
        /// </summary>
        [Fact]
        public void Kiro_engine_row_says_requested_at_launch()
        {
            var inputs = Live();
            inputs.Environment = Env(kiroEngine: "v3");

            var value = ValueOf(SessionInfoViewModel.Build(inputs), "Agent engine");

            Assert.Contains("v3", value);
            Assert.Contains("requested at launch", value);
            Assert.NotEqual("v3", value);
        }

        [Fact]
        public void Kiro_engine_row_states_the_default_rather_than_omitting_it()
        {
            var inputs = Live();
            inputs.Environment = Env(kiroEngine: null);

            // Drawn even when unset: changing this setting silently changes which conversations exist,
            // so a reader chasing that needs "not set" stated rather than absent.
            Assert.Contains("not set", ValueOf(SessionInfoViewModel.Build(inputs), "Agent engine"));
        }

        [Fact]
        public void Kiro_engine_row_is_absent_for_another_backend()
        {
            var inputs = Live();
            inputs.ProviderId = "claude-code";
            inputs.Environment = Env(kiroEngine: "v3");

            Assert.False(Has(SessionInfoViewModel.Build(inputs), "Agent engine"));
        }

        /// <summary>
        /// Both directions, because a check that always flags pins nothing.
        /// </summary>
        [Fact]
        public void Working_directory_flags_a_divergence_from_the_solution()
        {
            var inputs = Live();
            inputs.AgentWorkingDirectory = @"C:\src\repo";
            inputs.SolutionRoot = @"C:\src\repo\App";

            Assert.True(Has(SessionInfoViewModel.Build(inputs), "Solution"));
        }

        [Fact]
        public void Working_directory_is_unflagged_when_it_matches_the_solution()
        {
            var inputs = Live();
            inputs.AgentWorkingDirectory = @"C:\src\repo";
            inputs.SolutionRoot = @"C:\src\repo\";

            Assert.False(Has(SessionInfoViewModel.Build(inputs), "Solution"));
        }

        /// <summary>
        /// engine.log is the one that is always written, and issue #82 was a user who could not find
        /// it. Its position is the row's whole point.
        /// </summary>
        [Fact]
        public void Engine_log_is_named_first()
        {
            var inputs = Live();
            inputs.Environment = Env();

            var logs = SessionInfoViewModel.Build(inputs).Rows.Single(r => r.Label == "Logs");

            Assert.StartsWith("engine.log", logs.Value);
            Assert.Contains("read this first", logs.Value);
        }

        /// <summary>
        /// Pins the harness rule rather than the feature: <c>ExtensionConfig.LogDirectory</c> is
        /// deliberately outside <c>RedirectTo</c>'s reach, so a builder that read it directly would put
        /// the developer's real path into every screenshot artifact. Reading it here would make this
        /// test find a row.
        /// </summary>
        [Fact]
        public void Log_rows_are_absent_when_the_host_supplies_no_environment()
        {
            var inputs = Live();
            inputs.Environment = null;

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.False(Has(vm, "Logs"));
            Assert.False(Has(vm, "Log folder"));
            Assert.DoesNotContain(@"X:\scratch", vm.CopyText);
        }

        [Fact]
        public void An_off_log_says_which_setting_turns_it_on()
        {
            var inputs = Live();
            inputs.Environment = Env(acp: false);

            var rows = SessionInfoViewModel.Build(inputs).Rows;
            var acp = rows.Single(r => r.Value.StartsWith("acp.log", StringComparison.Ordinal));

            Assert.Contains("off", acp.Value);
            Assert.Contains("Log agent protocol frames", acp.Value);
        }

        /// <summary>
        /// The panel's rule: a row is a fact you cannot see elsewhere in the pane, and the copied text
        /// is the complete report. Backend, model and permission mode are already on screen in the
        /// header and status strip, so they are copied but not drawn.
        /// </summary>
        [Fact]
        public void Copy_text_carries_the_identity_facts_the_panel_does_not_draw()
        {
            var vm = SessionInfoViewModel.Build(Live());

            Assert.False(Has(vm, "Backend"));
            Assert.False(Has(vm, "Model"));
            Assert.False(Has(vm, "Permission mode"));

            Assert.Contains("Backend: Kiro", vm.CopyText);
            Assert.Contains("Model: Claude Sonnet 4", vm.CopyText);
            Assert.Contains("Permission mode: Accept edits", vm.CopyText);
        }

        /// <summary>
        /// A copy that dropped the unknowns would be a different document from the one on screen — and
        /// the unknowns are the interesting part of a support report.
        /// </summary>
        [Fact]
        public void Copy_text_carries_the_unknowns_verbatim()
        {
            var vm = SessionInfoViewModel.Build(Live(All(null)));

            Assert.Contains(SessionInfoViewModel.CapabilitiesNotReported, vm.CopyText);
            Assert.Contains("not reported", vm.CopyText);
        }

        /// <summary>Every drawn row reaches the clipboard, so the paste and the screenshot agree.</summary>
        [Fact]
        public void Copy_text_carries_every_drawn_row()
        {
            var inputs = Live();
            inputs.AgentWorkingDirectory = @"C:\src\repo";
            inputs.SolutionRoot = @"C:\src\repo\App";
            inputs.Environment = Env();

            var vm = SessionInfoViewModel.Build(inputs);

            foreach (var row in vm.Rows)
                Assert.Contains(row.Value, vm.CopyText);
        }

        [Fact]
        public void Opened_row_reports_elapsed_time_against_the_supplied_clock()
        {
            var inputs = Live(new SessionInfoResponseView
            {
                OpenedAt = DateTimeOffset.Parse("2026-09-02T14:32:00Z"),
            });

            Assert.Contains("23 min ago", ValueOf(SessionInfoViewModel.Build(inputs), "Backend session opened"));
        }

        /// <summary>
        /// A session whose info has not been read yet is not a session that reported nothing. The panel
        /// says so rather than rendering the capabilities as absent.
        /// </summary>
        [Fact]
        public void An_unread_session_is_distinct_from_one_that_reported_nothing()
        {
            var unread = SessionInfoViewModel.Build(LiveUnread());
            Assert.Equal("not read yet", ValueOf(unread, "Negotiated"));
            Assert.NotEqual(SessionInfoViewModel.CapabilitiesNotReported, ValueOf(unread, "Negotiated"));
        }

        /// <summary>
        /// The engine answered, so a session exists; the host has not adopted it. Found in Visual Studio for
        /// #160: the panel drew "No agent session open yet." directly above that same session's
        /// backend log directory, because every row built from the pull was gated on SessionStarted
        /// except that one. A card that visibly disagrees with itself undermines every other row on
        /// it, which is the whole thing this panel exists to be trusted for.
        /// </summary>
        [Fact]
        public void Warm_session_is_named_rather_than_reported_as_no_session()
        {
            var inputs = Session(new SessionInfoResponseView
            {
                AgentLogDirectory = @"C:\Users\someone\.kiro\logs\20260903T195141017",
                SupportsResume = true,
            });
            inputs.SessionStarted = false;

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.Equal(SessionInfoViewModel.WarmNoConversation, ValueOf(vm, "Session"));
            Assert.NotEqual(SessionInfoViewModel.NoSessionYet, ValueOf(vm, "Session"));
        }

        /// <summary>
        /// The other half, and it has to be asserted separately: a check that always says "warm" pins
        /// nothing. Nothing running means the pull returned null, and that is a different sentence.
        /// </summary>
        [Fact]
        public void No_session_at_all_is_still_reported_as_no_session()
        {
            var inputs = Session(null);
            inputs.SessionStarted = false;

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.Equal(SessionInfoViewModel.NoSessionYet, ValueOf(vm, "Session"));
        }

        /// <summary>
        /// The contradiction itself, stated as one assertion rather than left implied by the two
        /// above: wherever the backend's own log directory is drawn, the session line may not be
        /// claiming there is no session to have one.
        /// </summary>
        [Fact]
        public void Agent_log_row_never_appears_beside_a_no_session_claim()
        {
            var inputs = Session(new SessionInfoResponseView
            {
                AgentLogDirectory = @"C:\Users\someone\.kiro\logs\20260903T195141017",
            });
            inputs.SessionStarted = false;

            var vm = SessionInfoViewModel.Build(inputs);

            var hasAgentLogs = vm.Rows.Any(r => r.Label == "Agent's own logs");
            var claimsNoSession = vm.Rows.Any(r => r.Value == SessionInfoViewModel.NoSessionYet);
            Assert.True(hasAgentLogs, "the backend log row is what makes the contradiction visible");
            Assert.False(claimsNoSession, "drew \"no agent session open yet\" beside a live backend log directory");
        }

        /// <summary>
        /// Naming the warm state is only half of it. Having admitted a session exists, the card must
        /// say WHAT it is - the agent program is the row that closed #82, and it is a fact about the
        /// connection rather than about a conversation, so it is known the moment the handshake lands.
        /// The gate used to be SessionStarted for both, which left the panel reporting "warm" and
        /// declining to identify what it was warm with.
        /// </summary>
        [Fact]
        public void Warm_session_still_names_the_agent_and_what_it_negotiated()
        {
            var inputs = Session(new SessionInfoResponseView
            {
                AgentProgram = "kiro-cli-chat 2.16.2",
                SupportsResume = true,
                SupportsImages = false,
            });
            inputs.SessionStarted = false;

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.Equal("kiro-cli-chat 2.16.2", ValueOf(vm, "Agent program"));
            Assert.Equal("resume (no images)", ValueOf(vm, "Negotiated"));

            // ...and the one row that genuinely does not exist yet stays absent.
            Assert.DoesNotContain(vm.Rows, r => r.Label == "Conversation");
        }

        // ---- the mode row (issues #269/#270): what the agent itself is running as -----------------

        private static SessionInfoResponseView ClaudeModes(
            string id = "default", string? name = "Manual", string? openedIn = null, string? openedInName = null,
            string? pinFailure = null) =>
            new SessionInfoResponseView
            {
                AgentProgram = "@agentclientprotocol/claude-agent-acp 0.63.0",
                SupportsResume = true, SupportsSteering = true, SupportsImages = true, SupportsSessionList = true,
                ModeLabel = "Agent mode", ModeId = id, ModeName = name,
                OpenedInModeId = openedIn, OpenedInModeName = openedInName, ModePinFailure = pinFailure,
            };

        /// <summary>The name is what every other Claude surface says (2.1.200 renamed the mode and kept
        /// the id); the id is what the logs say. Both, name first.</summary>
        [Fact]
        public void Mode_row_prints_the_name_with_the_id_beside_it()
        {
            var vm = SessionInfoViewModel.Build(Live(ClaudeModes()));

            Assert.Equal("Manual (default)", ValueOf(vm, "Agent mode"));
            Assert.False(Has(vm, SessionInfoViewModel.OpenedInLabel));
            Assert.False(Has(vm, SessionInfoViewModel.NotAppliedLabel));
        }

        /// <summary>What the agent's own settings chose is shown only where it differs from what is
        /// running: the same value twice says nothing, and the difference is the whole #269 story.</summary>
        [Fact]
        public void Mode_row_says_what_the_session_opened_in_only_when_it_differs()
        {
            var moved = SessionInfoViewModel.Build(Live(ClaudeModes(
                openedIn: "bypassPermissions", openedInName: "Bypass Permissions")));
            Assert.Equal("Bypass Permissions (bypassPermissions)", ValueOf(moved, SessionInfoViewModel.OpenedInLabel));
            Assert.True(moved.Rows.Single(r => r.Label == SessionInfoViewModel.OpenedInLabel).IsSubItem);

            var same = SessionInfoViewModel.Build(Live(ClaudeModes(openedIn: "default", openedInName: "Manual")));
            Assert.False(Has(same, SessionInfoViewModel.OpenedInLabel));
        }

        /// <summary>A pin that did not land is the panel's one warning: the picker is decorative for
        /// this session, and the row says why in the agent's own words.</summary>
        [Fact]
        public void Mode_row_carries_a_failed_pin_as_a_sub_row()
        {
            var vm = SessionInfoViewModel.Build(Live(ClaudeModes(
                id: "bypassPermissions", name: "Bypass Permissions",
                openedIn: "bypassPermissions", openedInName: "Bypass Permissions",
                pinFailure: "the agent refused 'default': Mode default is not available in this session")));

            Assert.Equal("Bypass Permissions (bypassPermissions)", ValueOf(vm, "Agent mode"));
            Assert.Contains("refused 'default'", ValueOf(vm, SessionInfoViewModel.NotAppliedLabel), StringComparison.Ordinal);
            Assert.False(Has(vm, SessionInfoViewModel.OpenedInLabel)); // opened where it still is
        }

        /// <summary>A backend with modes that has not said which it is in gets "not reported", never a
        /// guess; a backend with NO modes gets no row at all — "none" is not a mode.</summary>
        [Fact]
        public void Mode_row_distinguishes_unreported_from_absent()
        {
            var unreported = SessionInfoViewModel.Build(Live(new SessionInfoResponseView
            {
                SupportsResume = true, ModeLabel = "Agent mode", ModeId = null,
            }));
            Assert.Equal(SessionInfoViewModel.ModeNotReported, ValueOf(unreported, "Agent mode"));

            var noModes = SessionInfoViewModel.Build(Live(new SessionInfoResponseView { SupportsResume = true }));
            Assert.DoesNotContain(noModes.Rows, r => r.Label is "Agent mode" or "Agent" or "Mode");
        }

        /// <summary>Kiro's modes are agents, and v3 says where each came from. A repository-defined one
        /// brings the repository's tool-trust rules, which is the fact worth a sub-row; the bundled
        /// default is the expectation and gets none.</summary>
        [Fact]
        public void Agent_row_names_a_repository_defined_agent()
        {
            var repoDefined = SessionInfoViewModel.Build(Live(new SessionInfoResponseView
            {
                AgentProgram = "Kiro CLI Agent 2.21.1", SupportsResume = true,
                ModeLabel = "Agent", ModeId = "shadowtest", ModeName = "shadowtest",
                ModeOrigin = "workspace", ModeOriginRoot = @"C:\repo",
            }));
            Assert.Equal("shadowtest", ValueOf(repoDefined, "Agent")); // name == id: no parenthetical
            Assert.Equal(@"the repository under C:\repo", ValueOf(repoDefined, SessionInfoViewModel.DefinedByLabel));

            var bundled = SessionInfoViewModel.Build(Live(new SessionInfoResponseView
            {
                SupportsResume = true, ModeLabel = "Agent", ModeId = "vibe", ModeName = "Default", ModeOrigin = "bundled",
            }));
            Assert.Equal("Default (vibe)", ValueOf(bundled, "Agent"));
            Assert.False(Has(bundled, SessionInfoViewModel.DefinedByLabel));
        }

        /// <summary>A configured agent that was not applied is named beside the one that is running,
        /// with the backend's reason — the notice at open said it once; the report has to carry it.</summary>
        [Fact]
        public void Agent_row_names_a_configured_agent_that_was_not_applied()
        {
            var vm = SessionInfoViewModel.Build(Live(new SessionInfoResponseView
            {
                SupportsResume = true, ModeLabel = "Agent", ModeId = "vibe", ModeName = "Default",
                RequestedModeId = "no-such-agent", RequestedModeFailure = "the backend does not offer 'no-such-agent'",
            }));

            Assert.Equal("Default (vibe)", ValueOf(vm, "Agent"));
            Assert.Equal("'no-such-agent' \u2014 the backend does not offer 'no-such-agent'", ValueOf(vm, SessionInfoViewModel.ConfiguredLabel));
        }

        /// <summary>The copied report is the complete panel, sub-rows indented under their row — and it
        /// keeps OUR picker's line apart from the agent's own mode by label.</summary>
        [Fact]
        public void Copy_text_carries_the_mode_row_and_its_sub_rows()
        {
            var vm = SessionInfoViewModel.Build(Live(ClaudeModes(
                openedIn: "bypassPermissions", openedInName: "Bypass Permissions")));

            Assert.Contains("Permission mode: Accept edits", vm.CopyText, StringComparison.Ordinal);
            Assert.Contains("Agent mode: Manual (default)", vm.CopyText, StringComparison.Ordinal);
            Assert.Contains("  Opened in: Bypass Permissions (bypassPermissions)", vm.CopyText, StringComparison.Ordinal);
        }

        // ---- the card is about the CONNECTED backend, not the picker ---------------------------------

        /// <summary>
        /// Measured: flipping the picker Claude → Kiro and opening the card at once drew Claude's
        /// program and mode under a Kiro "Agent engine" row, because the engine still held Claude's
        /// warm session and the engine row was gated on the picker. The rows that describe a session
        /// follow the session; the picker gets a row of its own that says it has moved.
        /// </summary>
        [Fact]
        public void A_moved_picker_does_not_dress_the_connected_backend_in_the_new_ones_rows()
        {
            var inputs = Session(new SessionInfoResponseView
            {
                AgentProgram = "@agentclientprotocol/claude-agent-acp 0.63.0", SupportsResume = true,
                ProviderId = "claude-code", ProviderDisplayName = "Claude Code",
                ModeLabel = "Agent mode", ModeId = "default", ModeName = "Manual",
            });
            inputs.SessionStarted = false;               // flipped before any conversation
            inputs.ProviderId = "kiro";                  // the picker
            inputs.ProviderDisplayName = "Kiro";
            inputs.Environment = Env(kiroEngine: "v3");

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.False(Has(vm, "Agent engine"));       // a Kiro launch flag under a Claude session
            Assert.Equal(SessionInfoViewModel.WarmNoConversationOn("Claude Code"), ValueOf(vm, "Session"));
            var moved = ValueOf(vm, SessionInfoViewModel.SelectedBackendLabel);
            Assert.StartsWith("Kiro", moved, StringComparison.Ordinal);
            Assert.Contains("describe Claude Code", moved, StringComparison.Ordinal);
            Assert.Contains("next message uses Kiro", moved, StringComparison.Ordinal);
            Assert.Contains("Backend: Kiro (selected); Claude Code (connected)", vm.CopyText, StringComparison.Ordinal);
        }

        [Fact]
        public void The_engine_row_follows_the_connected_backend()
        {
            var inputs = Session(new SessionInfoResponseView
            {
                SupportsResume = true, ProviderId = "kiro", ProviderDisplayName = "Kiro",
            });
            inputs.Environment = Env(kiroEngine: "v3");

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.Equal("v3 (requested at launch)", ValueOf(vm, "Agent engine"));
            Assert.False(Has(vm, SessionInfoViewModel.SelectedBackendLabel)); // picker and session agree
            Assert.Contains("Backend: Kiro", vm.CopyText, StringComparison.Ordinal);
            Assert.DoesNotContain("(selected)", vm.CopyText, StringComparison.Ordinal);
        }

        /// <summary>With nothing connected there is no session to follow, so the engine row falls back
        /// to the picker — the one case where the picker IS what the next session will be launched as.</summary>
        [Fact]
        public void With_no_session_the_engine_row_follows_the_picker()
        {
            var inputs = Session(null);
            inputs.SessionStarted = false;
            inputs.Environment = Env(kiroEngine: "v3");

            var vm = SessionInfoViewModel.Build(inputs);

            Assert.Equal("v3 (requested at launch)", ValueOf(vm, "Agent engine"));
            Assert.Equal(SessionInfoViewModel.NoSessionYet, ValueOf(vm, "Session"));
        }
    }
}
