using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Picking up a conversation the backend's own CLI holds (issue #108) — the host half: which
    /// sessions are offered, what is said when none are, and what an import leaves behind.
    /// </summary>
    public sealed class CliSessionPickupTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));

        public CliSessionPickupTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void AConversationWeAlreadyHoldIsNotOfferedBack() => RunSta(() =>
        {
            // Every session this extension creates is in the CLI's store too, so without the dedupe the
            // picker offers the user their own conversations straight back as if they were foreign —
            // including the one currently open, which is the newest and so the top row.
            var store = new FileSessionStore(_root);
            store.Save(new PersistedSession
            {
                WorkspaceRootPath = _root,
                ConversationId = "conv-ours",
                Title = "One of ours",
            });

            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-ours", "One of ours", DateTime.UtcNow, _root),
                    new BackendSessionDto("conv-theirs", "Started in the terminal", DateTime.UtcNow, _root),
                },
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { "conv-theirs" }, vm.BackendSessions.Select(s => s.Id).ToArray());
            Assert.True(vm.HasCliSection);
        });

        [Fact]
        public void AConversationNamedAfterOurOwnFramingIsShownAsUnnamed() => RunSta(() =>
        {
            // Kiro names a session from its first prompt, and ours begins
            // with the workspace-context block - so every conversation this extension starts is called
            // "<workspace-context>" in Kiro's own store. Measured on 37 kiro-cli sessions on one machine:
            // the only two ever prompted are ours, and both carry that name.
            //
            // The title arrives from the backend, so this is the last place it can be caught. Not
            // stripped and shown from the tag onwards, either: the truncated tail reads like a real
            // title, which is a better-dressed lie than the tag itself.
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto(
                        "conv-ours",
                        "<workspace-context>\r\nWorking directory: C:\\src\\repo\r\nActive file: Program.cs",
                        DateTime.UtcNow,
                        _root),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal("Untitled conversation", vm.BackendSessions.Single().Title);
        });

        [Fact]
        public void ARealTitleIsLeftAlone() => RunSta(() =>
        {
            // The guard must not eat a genuine name - Claude generates readable ones, and a Kiro session
            // started in the terminal is named from the user's own first words.
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-cli", "Parallelize sub-agents to explore directories", DateTime.UtcNow, _root),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal("Parallelize sub-agents to explore directories", vm.BackendSessions.Single().Title);
        });

        [Fact]
        public void EveryBackendIsListed_NotJustTheSelectedOne() => RunSta(() =>
        {
            // Browsing must never cost a session. Changing the provider picker DROPS the live one
            // ("your next message starts a new session"), so a section that listed only the selected
            // backend would mean looking for a Kiro conversation abandoned the Claude one you were in
            // the middle of. That is the reason this spans backends - the symmetry with the saved list
            // is the pleasant part, not the argument.
            var engine = new StubEngine
            {
                ProviderList = new List<ProviderInfoDto>
                {
                    new ProviderInfoDto("kiro", "Kiro", new List<ModelInfoDto>(), new List<string>()),
                    new ProviderInfoDto("claude-code", "Claude Code", new List<ModelInfoDto>(), new List<string>()),
                },
            };
            engine.SessionsByProvider["kiro"] = new[]
            {
                new BackendSessionDto("k1", "Kiro one", new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc), _root),
            };
            engine.SessionsByProvider["claude-code"] = new[]
            {
                new BackendSessionDto("c1", "Claude one", new DateTime(2026, 8, 25, 11, 0, 0, DateTimeKind.Utc), _root),
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            // Newest first ACROSS backends: the section is one list, so ordering per backend would
            // interleave by whichever CLI answered first, which says nothing about the conversations.
            Assert.Equal(new[] { "c1", "k1" }, vm.BackendSessions.Select(s => s.Id).ToArray());

            // And each row says whose it is, since the header can no longer name one backend.
            //
            // The BACKEND's name and nothing more. A "<name> CLI" chip asserts a provenance nothing on
            // the wire carries - the backends share one store between their CLI, their IDE and us, so a
            // row here may never have been near a terminal (measured 2026-08-26 on a hand-made Kiro IDE
            // session). Our own dedupe is the proof it was never true: it exists because every session
            // this extension creates is in that same store.
            Assert.Equal("Claude Code", vm.BackendSessions[0].AgentLabel);
            Assert.Equal("Kiro", vm.BackendSessions[1].AgentLabel);
            Assert.All(vm.BackendSessions, s => Assert.DoesNotContain("CLI", s.AgentLabel));
        });

        [Fact]
        public void SameTitledRowsFromOneBackendAreToldApart() => RunSta(() =>
        {
            // Claude auto-titles from the opening exchange, so two conversations begun with the same
            // first message get the same name. Measured on the real store: two rows both reading
            // "Issue #108 plan", 581 and 2485 records, both real and separately resumable. Alike in
            // every visible field, they cannot be told apart at all.
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("aaaa1111-x", "Issue #108 plan", new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc), _root),
                    new BackendSessionDto("bbbb2222-x", "Issue #108 plan", new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc), _root),
                    new BackendSessionDto("cccc3333-x", "Something else", new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc), _root),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            var collided = vm.BackendSessions.Where(s => s.Title == "Issue #108 plan").ToList();
            Assert.Equal(2, collided.Count);

            // Marked, and distinctly - a disambiguator that fails to disambiguate is the whole defect.
            Assert.All(collided, s => Assert.False(string.IsNullOrEmpty(s.Disambiguator)));
            Assert.Equal(2, collided.Select(s => s.Disambiguator).Distinct().Count());
            Assert.All(collided, s => Assert.Contains(s.Disambiguator!, s.Subtitle));

            // And the row that was never ambiguous stays clean: an id says two rows DIFFER, never which
            // one you want, so it earns its space only where the ambiguity is real.
            var unique = vm.BackendSessions.Single(s => s.Title == "Something else");
            Assert.Null(unique.Disambiguator);
            Assert.DoesNotContain("cccc3333", unique.Subtitle);
        });

        [Fact]
        public void TheSameTitleOnTwoBackendsIsNotMarked() => RunSta(() =>
        {
            // The chip already separates these ("Kiro" against "Claude Code"), so an id here
            // would be noise added to solve nothing.
            var engine = new StubEngine
            {
                ProviderList = new List<ProviderInfoDto>
                {
                    new ProviderInfoDto("kiro", "Kiro", new List<ModelInfoDto>(), new List<string>()),
                    new ProviderInfoDto("claude-code", "Claude Code", new List<ModelInfoDto>(), new List<string>()),
                },
            };
            engine.SessionsByProvider["kiro"] = new[]
            {
                new BackendSessionDto("k1", "Fix the picker", new DateTime(2026, 8, 25, 9, 0, 0, DateTimeKind.Utc), _root),
            };
            engine.SessionsByProvider["claude-code"] = new[]
            {
                new BackendSessionDto("c1", "Fix the picker", new DateTime(2026, 8, 25, 11, 0, 0, DateTimeKind.Utc), _root),
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(2, vm.BackendSessions.Count);
            Assert.All(vm.BackendSessions, s => Assert.Null(s.Disambiguator));
        });

        [Fact]
        public void AShortIdThatDoesNotSeparateIsWidened() => RunSta(() =>
        {
            // Close to unreachable with guids, but the failure mode is silent - two rows both marked
            // "abcdefgh" are exactly as indistinguishable as two unmarked ones - so it is checked
            // rather than assumed.
            var rows = new[]
            {
                Row("abcdefgh-1111", "Same name"),
                Row("abcdefgh-2222", "Same name"),
            };

            ChatViewModel.MarkCollidingTitles(rows);

            Assert.Equal(new[] { "abcdefgh-1111", "abcdefgh-2222" }, rows.Select(r => r.Disambiguator).ToArray());
        });

        [Fact]
        public void TheMarkIsAFunctionOfTheListNotOfHowItWasBuilt() => RunSta(() =>
        {
            // The pass runs again as each backend folds in, so it must depend only on what it is given.
            // A row that stops colliding has to LOSE its id, or a stale marker becomes permanent noise
            // on a row that no longer needs one.
            var twin = Row("aaaa1111-x", "Same name");
            var other = Row("bbbb2222-x", "Same name");

            ChatViewModel.MarkCollidingTitles(new[] { twin, other });
            Assert.False(string.IsNullOrEmpty(twin.Disambiguator));

            ChatViewModel.MarkCollidingTitles(new[] { twin });
            Assert.Null(twin.Disambiguator);
        });

        [Fact]
        public void TheDateSaysWhatItActuallyMeasures() => RunSta(() =>
        {
            // ACP documents updatedAt as "ISO 8601 timestamp of last activity"; claude-agent-acp fills
            // it from the session file's mtime, so a background sync moves it. Measured 2026-08-26: a
            // conversation last spoken to 22 hours earlier was restamped and sorted second. We relay the
            // backend's answer rather than inventing one, so the row must not imply a precision it has
            // not got.
            var dated = Row("a1", "Dated", new DateTime(2026, 8, 26, 3, 17, 0, DateTimeKind.Utc));
            Assert.NotNull(dated.TimeTooltip);
            Assert.Contains("not always when it was last used", dated.TimeTooltip!);

            // Nothing to qualify when no date was reported - the button's own tooltip shows through
            // rather than this one explaining a number that is not on screen.
            Assert.Null(Row("a2", "Undated", updated: null).TimeTooltip);
        });

        private static BackendSessionItemViewModel Row(string id, string title, DateTime? updated = null) =>
            new BackendSessionItemViewModel(
                new BackendSessionDto(id, title, updated, null), "claude-code", "Claude Code", _ => { });

        [Fact]
        public void AColdBackendIsNotRespawnedOnEveryOpen() => RunSta(() =>
        {
            // A listing costs a CLI spawn (3-5s measured), and the picker can be opened repeatedly while
            // the user hunts. Reopening inside the cache's life must not pay again.
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("c1", "One", DateTime.UtcNow, _root) },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(1, engine.ListCalls["fake"]);

            // Still shown - a cached answer renders, it does not merely suppress the work.
            Assert.Single(vm.BackendSessions);
        });

        [Fact]
        public void AFailedListingIsNotCached() => RunSta(() =>
        {
            // A failure is usually transient - the CLI mid-update, a login lapsed - so caching it would
            // leave the section broken for a minute after the user fixed the thing it complained about.
            var engine = new StubEngine { ListThrows = new InvalidOperationException("spawn failed") };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(2, engine.ListCalls["fake"]);
        });

        [Fact]
        public void ABackendThatCannotListGetsNoSectionAtAll() => RunSta(() =>
        {
            // A backend that has never offered session listing must not explain itself on every history
            // open. Absence is the right answer; a standing sentence is noise.
            var engine = new StubEngine { Supported = false, Reason = "Kiro cannot list its sessions." };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.False(vm.HasCliSection);
            Assert.Equal("Kiro cannot list its sessions.", vm.BackendSessionsMessage);
        });

        [Fact]
        public void LookedAndFoundNoneIsSaidOutLoud() => RunSta(() =>
        {
            // Distinct from the case above: this backend CAN be asked and had nothing, which is worth
            // confirming — otherwise an empty section reads as a feature that failed silently.
            var engine = new StubEngine { Sessions = Array.Empty<BackendSessionDto>() };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.True(vm.HasCliSection);
            Assert.Contains("No", vm.BackendSessionsMessage, StringComparison.Ordinal);
        });

        [Fact]
        public void AFailureToReachTheBackendStillShows() => RunSta(() =>
        {
            // Unlike "cannot list", a failure means something CHANGED — the CLI moved, a login lapsed —
            // so it is worth the user's attention and the backend's own words are the useful part.
            var engine = new StubEngine { ListThrows = new InvalidOperationException("spawn failed") };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.True(vm.HasCliSection);
            Assert.Contains("spawn failed", vm.BackendSessionsMessage, StringComparison.Ordinal);
        });

        /// <summary>
        /// An import REPLACES the backend session, so it must not run under a live turn — the same
        /// guard <c>LoadSession</c> has always taken, on the path that had none.
        /// <para>
        /// Mid-turn the picker is still open and its rows still clickable (<c>ImportCommand</c> reads
        /// only <c>IsImporting</c>, and the history toggle has no gate at all). One click swapped the
        /// provider, cleared the MCP state and opened a NEW session while the previous prompt was still
        /// streaming — and the events it went on to produce were then recorded against the imported
        /// conversation, writing a turn the user ran in one conversation into another one's log.
        /// </para>
        /// </summary>
        [Fact]
        public void AnImportIsRefusedWhileATurnIsStillRunning() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
                ImportedEntries = new[]
                {
                    new ImportedEntryDto("user", "someone else's question", null),
                },
                PromptGate = new TaskCompletionSource<PromptResponse>(),
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            vm.InputText = "the turn the user is actually running";
            vm.SendCommand.Execute(null);
            Assert.True(vm.IsBusy);

            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            // Nothing was imported: no row from the other conversation, and no log written under its id.
            Assert.DoesNotContain(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("someone else's question", StringComparison.Ordinal));
            Assert.DoesNotContain(store.List(_root), s => s.ConversationId == "conv-cli");

            // ...and the live conversation is still the one on screen, still running.
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("the turn the user is actually running", StringComparison.Ordinal));
            Assert.True(vm.IsBusy);
        });

        /// <summary>
        /// A permission request open across a conversation swap is ANSWERED, not carried over.
        /// <para>
        /// It is reachable precisely because it is out of turn: steering raises a request with
        /// <c>IsBusy</c> false, so nothing about opening another conversation asks the user to deal with
        /// the banner first. <c>ClearForConversationSwap</c> cleared the outcomes and not the queue, so
        /// <c>Items.Clear()</c> destroyed the row the banner outlines and left the banner standing over
        /// the new transcript — where Allow authorises a write into the conversation the user just left,
        /// and dismissing leaves the old backend blocked on a request nothing can answer. Cancelled is
        /// the same answer a Stop and a workspace switch give, and for the same reason.
        /// </para>
        /// </summary>
        [Fact]
        public void APermissionRequestOpenAcrossAnImportIsCancelledRatherThanCarriedOver() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
                ImportedEntries = new[] { new ImportedEntryDto("user", "a question", null) },
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            var decision = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1", "Write Program.cs", "edit", null, null,
                new[] { new PermissionOptionDto("allow", "Allow", "allow_once") }));
            Assert.NotNull(vm.PendingPermission);

            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            Assert.True(decision.IsCompleted);
            Assert.True(decision.Result.Cancelled);
            Assert.Null(vm.PendingPermission);
        });

        [Fact]
        public void AnImportedConversationIsSavedAndRendered() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
                ImportedEntries = new[]
                {
                    new ImportedEntryDto("user", "what does this solution do?", null),
                    new ImportedEntryDto("agent", null, new AgentEventDto { Type = "text", Text = "It bridges VS to an agent." }),
                },
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            // Saved as one of ours, keyed by the BACKEND's id — which is also what stops it being
            // offered again on the next history open.
            var saved = Assert.Single(store.List(_root));
            Assert.Equal("conv-cli", saved.ConversationId);

            // Rendered through the ordinary replay, so the user's words are a message rather than a
            // blob of imported state.
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.Text.Contains("what does this solution do?", StringComparison.Ordinal));

            // The conversation is loaded backend-side, so the next prompt is an ordinary send: no
            // resume choice is pending.
            Assert.False(vm.HasPendingResume);
        });

        /// <summary>
        /// A capture the user attached in an EARLIER session comes back as a chip, not as tags in
        /// their message (issue #73, rung 2).
        ///
        /// <para>Two halves, and only one of them is about tidiness. Our own framing has to come off,
        /// or the import renders our words as theirs - that is <c>Strip</c>'s job and it already had
        /// it. But the capture has to come off <b>and be kept</b>: a foreign conversation has no log
        /// of ours behind it, so the replayed block is the ONLY surviving record of the evidence the
        /// message was written about, and dropping it leaves the question standing alone.</para>
        ///
        /// <para>The mid-turn block here is not decoration. The wire order puts the capture BETWEEN
        /// our two blocks, so a reader that only removed a leading run of our own tags would stop at
        /// the capture and hand our framing back as the user's words - which is exactly the shape
        /// this check would go red on.</para>
        /// </summary>
        [Fact]
        public void AnImportedMessageGivesItsAttachedContextBackAsAChip() => RunSta(() =>
        {
            var store = new FileSessionStore(_root);
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "Yesterday", DateTime.UtcNow, _root) },
                ImportedEntries = new[]
                {
                    new ImportedEntryDto(
                        "user",
                        "<debug-state>\nStopped in Recurse at Foo.cs:33.\n</debug-state>\n\n"
                        + "<mid-turn-message>\nThe user interrupted you.\n</mid-turn-message>\n\n"
                        + "why is Items empty?",
                        null),
                },
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            var message = Assert.Single(
                vm.Items.OfType<MessageItemViewModel>(), m => m.Role == MessageRole.User);

            // Their words, and only their words.
            Assert.Equal("why is Items empty?", message.Text);
            Assert.DoesNotContain("mid-turn-message", message.Text, StringComparison.Ordinal);

            // ...with the evidence beside them rather than gone.
            var context = Assert.Single(message.Contexts);
            Assert.Contains("Stopped in Recurse", context.Text);
            Assert.Equal("debug-state", context.Kind);

            // And it survives the round trip through the log the import wrote.
            var saved = store.Load(_root, store.List(_root).Single().Id)!;
            var savedEntry = Assert.Single(saved.Log, e => e.Role == "user");
            Assert.Contains("Stopped in Recurse", Assert.Single(savedEntry.Contexts!).Text);
        });

        /// <summary>
        /// A user QUOTING a block back does not have it lifted out from under them.
        ///
        /// <para>The rule is <c>SplitLeading</c>'s and is pinned there, but it is worth a case at the
        /// lift as well, because this is the layer where getting it wrong does harm: the block would
        /// be cut out of the middle of a sentence and re-rendered as a chip, and the message the user
        /// is shown would no longer be the message they sent. Asking "what is this &lt;debug-state&gt;
        /// thing?" is exactly the shape that would do it.</para>
        /// </summary>
        [Fact]
        public void AQuotedBlockMidMessageIsLeftWhereItIs() => RunSta(() =>
        {
            const string quoted = "what is this: <debug-state>\nStopped in Recurse.\n</debug-state> ?";

            var store = new FileSessionStore(_root);
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "Yesterday", DateTime.UtcNow, _root) },
                ImportedEntries = new[] { new ImportedEntryDto("user", quoted, null) },
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            var message = Assert.Single(
                vm.Items.OfType<MessageItemViewModel>(), m => m.Role == MessageRole.User);

            Assert.Equal(quoted, message.Text);
            Assert.False(message.HasContexts);
        });

        [Fact]
        public void AnImportedSessionIsAsCapableAsOneWeStartedOurselves() => RunSta(() =>
        {
            // The import opens a real backend session, so it has a real handshake answer - and it has to
            // take that answer on exactly as an ordinary send does. Setting the started flag alone
            // leaves an imported conversation silently unable to steer. Every miss here
            // degrades quietly rather than failing - a steer becomes a queued message, an image becomes
            // a path - which is why it wants a test rather than a reviewer.
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
                SupportsSteering = true,
                ImportedEntries = new[] { new ImportedEntryDto("user", "carry on", null) },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            // CanSteer is the observable end of it: started AND the handshake said yes.
            Assert.True(vm.CanSteer);
        });

        [Fact]
        public void ARefusedLoadImportsNothingAndSaysWhy() => RunSta(() =>
        {
            // The backend starts a FRESH session rather than failing, so without this the user would get
            // an empty transcript titled as the conversation they picked and a live agent that has never
            // heard of it. Measured against the real CLI: it refuses to load a conversation it currently
            // has open — which is the newest row, and so the likeliest one to be clicked.
            var store = new FileSessionStore(_root);
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-open", "Open in a terminal", DateTime.UtcNow, _root) },
                ResumeFailureReason = "session is already open",
                ImportedEntries = Array.Empty<ImportedEntryDto>(),
            };

            var vm = NewViewModel(engine, store);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.ImportBackendSessionAsync(vm.BackendSessions.Single()).GetAwaiter().GetResult();

            Assert.Empty(store.List(_root));
            Assert.Contains(vm.Items.OfType<NoticeItemViewModel>(),
                n => n.Text.Contains("session is already open", StringComparison.Ordinal));
        });

        // Initialized, because the CLI listing is asked of the selected provider and selection is what
        // InitializeAsync establishes - an uninitialized view-model asks nobody, correctly.
        // ---- The workspace moving under a listing (measured 2026-08-28) -------------------------
        //
        // The tool window opens before the solution finishes loading, so the FIRST listing of a session
        // can be made against the transient default workspace. Every backend keys its conversation store
        // by working directory, so that answer is about a folder nobody has ever worked in - an empty
        // list, which a picker cannot tell from "you have no stored conversations". Nothing retired it
        // when the real root arrived, and it was cached, so the section reported nothing until the tool
        // window was restarted.

        [Fact]
        public void AListingIsReAskedWhenTheWorkspaceMoves() => RunSta(() =>
        {
            var loading = Path.Combine(_root, "default-workspace");
            var solution = Path.Combine(_root, "solution");
            Directory.CreateDirectory(loading);
            Directory.CreateDirectory(solution);

            var engine = new StubEngine();
            engine.SessionsByRoot[loading] = Array.Empty<BackendSessionDto>();
            engine.SessionsByRoot[solution] = new[]
            {
                new BackendSessionDto("conv-real", "Started in the terminal", DateTime.UtcNow, solution),
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root), loading);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            Assert.Empty(vm.BackendSessions);

            vm.UpdateWorkspaceRoot(solution, null);
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            // Asked again, for the new root - not served the minute-old answer about the old one.
            Assert.Equal(new[] { loading, solution }, engine.RootsAsked.ToArray());
            Assert.Equal(new[] { "conv-real" }, vm.BackendSessions.Select(s => s.Id).ToArray());
        });

        [Fact]
        public void AnswerInFlightWhenTheWorkspaceMovesIsDiscarded_NotCached() => RunSta(() =>
        {
            // Clearing the cache on the move is not enough on its own: a listing already out completes
            // AFTER the clear and writes its answer in, still carrying the root it was asked for. So the
            // root travels WITH the cached answer and is checked at the read - which is also what stops
            // the abandoned listing's rows landing in a section that now belongs to another folder.
            var loading = Path.Combine(_root, "default-workspace");
            var solution = Path.Combine(_root, "solution");
            Directory.CreateDirectory(loading);
            Directory.CreateDirectory(solution);

            var gate = new TaskCompletionSource<bool>();
            var engine = new StubEngine { ListGate = gate };
            engine.SessionsByRoot[loading] = new[]
            {
                new BackendSessionDto("conv-stale", "From the wrong folder", DateTime.UtcNow, loading),
            };
            engine.SessionsByRoot[solution] = new[]
            {
                new BackendSessionDto("conv-real", "Started in the terminal", DateTime.UtcNow, solution),
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root), loading);

            var inFlight = vm.RefreshBackendSessionsAsync();
            vm.UpdateWorkspaceRoot(solution, null);

            gate.SetResult(true);
            Assert.True(inFlight.Wait(TimeSpan.FromSeconds(5)), "the abandoned listing never completed");

            // Its rows are dropped rather than added to a section that has moved on.
            Assert.Empty(vm.BackendSessions);

            engine.ListGate = null;
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { loading, solution }, engine.RootsAsked.ToArray());
            Assert.Equal(new[] { "conv-real" }, vm.BackendSessions.Select(s => s.Id).ToArray());
        });

        [Fact]
        public void AWedgedBackendReleasesTheSectionInsteadOfFreezingIt() => RunSta(() =>
        {
            // A CLI that spawns and never completes its handshake used to hold the in-flight guard for
            // the life of the window: every later history open returned at the guard having done
            // nothing, silently, with the section frozen on whatever it last held. Only restarting the
            // tool window cleared it.
            var gate = new TaskCompletionSource<bool>();
            var engine = new StubEngine { ListGate = gate };

            var vm = NewViewModel(engine, new FileSessionStore(_root), _root);
            vm.BackendListTimeout = TimeSpan.FromMilliseconds(200);

            var first = vm.RefreshBackendSessionsAsync();
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)), "a wedged backend never released the section");

            // Reported rather than passed off as an empty store: the user is told nothing came back, and
            // "no conversations" would be a claim about their history that we have no evidence for.
            Assert.Contains("did not answer", vm.BackendSessionsMessage);
            Assert.True(vm.HasCliSection);

            // The half that matters. Pre-fix this asked once and every later open was a no-op.
            var second = vm.RefreshBackendSessionsAsync();
            Assert.True(second.Wait(TimeSpan.FromSeconds(5)), "the second open was never released either");
            Assert.Equal(2, engine.ListCalls["fake"]);

            gate.TrySetResult(true);
        });

        // ---- The picker's two tabs -------------------------------------------------------------
        //
        // The saved list and the backend's own history used to be stacked in one scrolling surface,
        // which put the second one below every conversation the workspace had ever saved. They are now
        // two tabs. What the tabs need pinning for is not the switch - it is what happens when the tab
        // you are standing on stops existing.

        [Fact]
        public void TheSavedListIsTheTabYouLandOn() => RunSta(() =>
        {
            // The saved list is a directory read and is already on screen when the popup opens; the
            // other tab is a CLI spawn that has not answered yet. Opening onto the one still loading
            // would show an ellipsis where the conversations used to be.
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(HistoryTab.Saved, vm.HistoryTab);
            Assert.True(vm.ShowSavedSessions);
            Assert.False(vm.ShowBackendSessions);
        });

        [Fact]
        public void LosingTheSectionTakesYouBackToTheSavedList() => RunSta(() =>
        {
            // THE REASON THE TAB COERCES. HasCliSection goes false for reasons the user did not ask
            // for - here a workspace move, which is an ordinary git branch switch (VS reloads the .sln,
            // and the CLI section is scoped to a working directory). Left alone, whoever was standing on
            // the second tab would be looking at a pane with no rows, no header and no explanation:
            // exactly the standing explanation HasCliSection exists to suppress, only worse, because
            // they clicked to get there.
            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            vm.HistoryTab = HistoryTab.Backend;
            Assert.True(vm.ShowBackendSessions);

            vm.UpdateWorkspaceRoot(Path.Combine(_root, "elsewhere"), notice: null);

            Assert.False(vm.HasCliSection);
            Assert.Equal(HistoryTab.Saved, vm.HistoryTab);
            Assert.True(vm.ShowSavedSessions);
        });

        [Fact]
        public void TheTabYouCHOSEComesBackWithItsSection() => RunSta(() =>
        {
            // The other half of the coercion: it corrects what is DRAWN without destroying what was
            // asked for. A user who went looking in the agent's history is still looking for it after
            // the new workspace answers, and being silently put back on the saved list every time would
            // make the tab feel like it had not taken.
            var newRoot = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(newRoot);

            var engine = new StubEngine
            {
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.HistoryTab = HistoryTab.Backend;

            vm.UpdateWorkspaceRoot(newRoot, notice: null);
            Assert.True(vm.ShowSavedSessions);

            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.True(vm.HasCliSection);
            Assert.True(vm.ShowBackendSessions);
        });

        [Fact]
        public void ABackendThatCannotBeAskedHasNoTabToStandOn() => RunSta(() =>
        {
            // Same coercion, reached the other way: this backend has never offered session listing, so
            // there is no second pill in the strip at all - and setting the tab anyway must not draw a
            // pane that has no pill.
            var engine = new StubEngine { Supported = false, Reason = "Kiro cannot list its sessions." };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            vm.HistoryTab = HistoryTab.Backend;

            Assert.False(vm.HasCliSection);
            Assert.True(vm.ShowSavedSessions);
        });

        [Fact]
        public void ClickingTheTabYouAreOnLeavesItSelected() => RunSta(() =>
        {
            // The pills are ToggleButtons, so clicking the selected one drives its own IsChecked to
            // false through SetCurrentValue before the command runs. The binding is OneWay, so nothing
            // puts it back unless the setter notifies even when the value did not change - and the
            // symptom is a strip with NEITHER pill lit and both panes' bindings unread.
            var vm = NewViewModel(new StubEngine(), new FileSessionStore(_root));

            var raised = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.ShowSavedSessions))
                    raised++;
            };

            vm.HistoryTab = HistoryTab.Saved;

            Assert.True(raised > 0, "re-selecting the current tab must still notify, or the pill stays unchecked");
        });

        [Fact]
        public void TheBadgeSeparatesStillLookingFromFoundNothing() => RunSta(() =>
        {
            // The pill carries a count so the list can be judged without opening it - which is the
            // whole point of promoting it out of the scroll. That makes the empty pill ambiguous unless
            // "still spawning a CLI" says something of its own: a blank pill during a 3-5s listing
            // reads as "there is nothing in here", and the user goes away.
            var gate = new TaskCompletionSource<bool>();
            var engine = new StubEngine
            {
                ListGate = gate,
                Sessions = new[] { new BackendSessionDto("conv-cli", "From the terminal", DateTime.UtcNow, _root) },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            var listing = vm.RefreshBackendSessionsAsync();

            Assert.Equal("…", vm.BackendTabBadge);

            gate.TrySetResult(true);
            Assert.True(listing.Wait(TimeSpan.FromSeconds(5)), "the listing was never released");

            Assert.Equal("1", vm.BackendTabBadge);
        });

        [Fact]
        public void FoundNothingCarriesNoBadgeAtAll() => RunSta(() =>
        {
            // The third state, and it is the one that must stay blank: the pane below says "No other
            // conversations for this folder" in words, and a "0" on the pill would be a second, terser
            // answer to the same question sitting where a count belongs.
            var vm = NewViewModel(new StubEngine { Sessions = Array.Empty<BackendSessionDto>() }, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.False(vm.HasBackendTabBadge);
            Assert.Null(vm.BackendTabBadge);
        });

        // ---- Conversations created and never used ----------------------------------------------
        //
        // Almost all of them are ours: the warm start (#19) opens a session on every chat-window open,
        // and a window nobody prompts leaves one in the backend's store that the dedupe cannot reach,
        // because nothing was saved here to dedupe it against. Measured on this repo's workspace
        // 2026-09-03 against Kiro v3: 9 of 31 listed conversations.

        [Fact]
        public void AConversationCreatedAndNeverUsedIsHeldBack() => RunSta(() =>
        {
            var created = DateTime.UtcNow.AddMinutes(-5);
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-real", "Investigate the failing build", created.AddHours(1), _root, created),
                    new BackendSessionDto("conv-unused", "New Session", created.AddMilliseconds(16), _root, created),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { "conv-real" }, vm.BackendSessions.Select(s => s.Id).ToArray());
            Assert.Equal(1, vm.UnusedBackendSessionCount);
            Assert.True(vm.HasUnusedBackendSessions);
        });

        [Fact]
        public void ARealConversationWithCloseTimestampsIsNotHidden() => RunSta(() =>
        {
            // THE CONTROL, and the reason the rule needs two signals. A short conversation has a
            // created-to-updated gap of milliseconds exactly as an unused one does; only the title
            // separates them. A filter resting on the clock alone hides this - and a list that is
            // merely SHORTER looks like nothing at all went wrong.
            var created = DateTime.UtcNow.AddMinutes(-5);
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-brief", "Quick question about the build", created.AddMilliseconds(40), _root, created),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { "conv-brief" }, vm.BackendSessions.Select(s => s.Id).ToArray());
            Assert.False(vm.HasUnusedBackendSessions);
        });

        [Fact]
        public void ABackendThatReportsNoCreationTimeHidesNothing() => RunSta(() =>
        {
            // Claude's whole behaviour here. claude-agent-acp forwards 4 of SDKSessionInfo's 10 fields
            // and createdAt is among the six it drops, so there is no second opinion - and with one
            // signal this would be a display string deciding on its own. It refuses instead.
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-untitled", null, DateTime.UtcNow, _root, null),
                    new BackendSessionDto("conv-placeholder", "New Session", DateTime.UtcNow, _root, null),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Equal(2, vm.BackendSessions.Count);
            Assert.False(vm.HasUnusedBackendSessions);
        });

        [Fact]
        public void TheHiddenOnesCanBeShown() => RunSta(() =>
        {
            // The escape hatch, and it is the price of filtering on two weak signals at all: a picker
            // that silently offers fewer conversations than the backend holds is the one thing this
            // section must not be. Revealing is a move between two lists the view-model already holds,
            // so it costs no listing and cannot fail.
            var created = DateTime.UtcNow.AddMinutes(-5);
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-real", "Investigate the failing build", created.AddHours(1), _root, created),
                    new BackendSessionDto("conv-unused", "New Session", created.AddMilliseconds(16), _root, created),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Contains("1 unused conversation hidden", vm.UnusedBackendSessionsLabel, StringComparison.Ordinal);

            vm.ShowUnusedBackendSessionsCommand.Execute(null);

            Assert.Equal(2, vm.BackendSessions.Count);
            Assert.Contains("conv-unused", vm.BackendSessions.Select(s => s.Id));
            Assert.False(vm.HasUnusedBackendSessions);
        });

        [Fact]
        public void AListThatIsALLUNUSEDKeepsItsTab() => RunSta(() =>
        {
            // The sharpest case, and it took a fix. With every row held back the visible list is empty
            // and the "no other conversations" line would be a plain untruth, so it is suppressed -
            // which left HasCliSection false, retiring the tab and taking the user to the saved list by
            // the coercion, with the show line they needed on the pane that had just disappeared.
            var created = DateTime.UtcNow.AddMinutes(-5);
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-unused-1", "New Session", created.AddMilliseconds(16), _root, created),
                    new BackendSessionDto("conv-unused-2", "New Session", created.AddMilliseconds(8), _root, created),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();

            Assert.Empty(vm.BackendSessions);
            Assert.Equal(2, vm.UnusedBackendSessionCount);
            Assert.True(vm.HasCliSection);
            Assert.Null(vm.BackendSessionsMessage);

            // ...and the tab is still somewhere the user can stand.
            vm.HistoryTab = HistoryTab.Backend;
            Assert.True(vm.ShowBackendSessions);
        });

        [Fact]
        public void RevealedRowsAreMarkedAndOrderedWithTheRest() => RunSta(() =>
        {
            // Revealed rows share a title BY CONSTRUCTION - they are all the backend's "not named yet" -
            // so this is the one case where the disambiguator has real work to do. Appending them
            // unmarked and unsorted would put several identical rows at the bottom of a newest-first
            // list, which is two defects the moment the hatch is used.
            var created = DateTime.UtcNow.AddHours(-5);
            var engine = new StubEngine
            {
                Sessions = new[]
                {
                    new BackendSessionDto("conv-old", "Investigate the failing build", created, _root, created.AddHours(-1)),
                    new BackendSessionDto("conv-unused-new", "New Session", DateTime.UtcNow, _root, DateTime.UtcNow),
                    new BackendSessionDto("conv-unused-old", "New Session", created.AddHours(-2), _root, created.AddHours(-2)),
                },
            };

            var vm = NewViewModel(engine, new FileSessionStore(_root));
            vm.RefreshBackendSessionsAsync().GetAwaiter().GetResult();
            vm.ShowUnusedBackendSessionsCommand.Execute(null);

            Assert.Equal(
                new[] { "conv-unused-new", "conv-old", "conv-unused-old" },
                vm.BackendSessions.Select(s => s.Id).ToArray());

            var twins = vm.BackendSessions.Where(s => s.Id.StartsWith("conv-unused", StringComparison.Ordinal));
            Assert.All(twins, t => Assert.False(string.IsNullOrEmpty(t.Disambiguator)));
        });

        private ChatViewModel NewViewModel(
            StubEngine engine, FileSessionStore store, string workspaceRoot)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, workspaceRoot, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            return vm;
        }

        private ChatViewModel NewViewModel(StubEngine engine, FileSessionStore store)
        {
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            return vm;
        }

        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>An engine whose only jobs are to list, to start, and to hand back an import.</summary>
        private sealed class StubEngine : IEngineConnection
        {
            public IReadOnlyList<BackendSessionDto> Sessions { get; set; } = Array.Empty<BackendSessionDto>();

            public IReadOnlyList<ImportedEntryDto> ImportedEntries { get; set; } = Array.Empty<ImportedEntryDto>();

            public bool Supported { get; set; } = true;

            public string? Reason { get; set; }

            public string? ResumeFailureReason { get; set; }

            public bool SupportsSteering { get; set; }

            public Exception? ListThrows { get; set; }

            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            /// <summary>Every backend the picker will ask. Defaults to one; set for cross-backend cases.</summary>
            public IReadOnlyList<ProviderInfoDto> ProviderList { get; set; } = new List<ProviderInfoDto>
            {
                new ProviderInfoDto("fake", "Fake", new List<ModelInfoDto>(), new List<string>()),
            };

            /// <summary>Per-provider answers, for the cross-backend cases. Falls back to Sessions.</summary>
            public Dictionary<string, IReadOnlyList<BackendSessionDto>> SessionsByProvider { get; } = new();

            /// <summary>How many times each provider was actually asked - the cache's observable end.</summary>
            public Dictionary<string, int> ListCalls { get; } = new();

            /// <summary>Every workspace root a listing was asked for, in order. The cache is scoped to
            /// one, so which root reached the engine is the only thing that separates a re-ask from a
            /// stale answer served back.</summary>
            public List<string> RootsAsked { get; } = new();

            /// <summary>Per-root answers, for the workspace-move cases. Falls back to
            /// <see cref="SessionsByProvider"/> and then <see cref="Sessions"/>.</summary>
            public Dictionary<string, IReadOnlyList<BackendSessionDto>> SessionsByRoot { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>When set, a listing waits on it instead of answering - a CLI that spawned and
            /// never finished its handshake, which is the state that used to wedge the section for the
            /// life of the window. Left unset, every listing answers immediately.</summary>
            public TaskCompletionSource<bool>? ListGate { get; set; }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(ProviderList));

            public async Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
            {
                var id = request.ProviderId ?? string.Empty;
                ListCalls[id] = ListCalls.TryGetValue(id, out var n) ? n + 1 : 1;
                RootsAsked.Add(request.WorkspaceRootPath ?? string.Empty);

                if (ListGate is { } gate)
                {
                    // Whichever comes first: the test releasing it, or the caller giving up. Written as
                    // a race rather than gate.Task.WaitAsync(ct) so the stub says out loud that a
                    // cancelled listing is a real outcome here and not an incidental one.
                    await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, cancellationToken))
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (ListThrows is not null)
                    throw ListThrows;

                var sessions =
                    SessionsByRoot.TryGetValue(request.WorkspaceRootPath ?? string.Empty, out var forRoot) ? forRoot
                    : SessionsByProvider.TryGetValue(id, out var forProvider) ? forProvider
                    : Sessions;

                return new ListBackendSessionsResponse(Supported, Reason, sessions);
            }

            public Task<StartSessionResponse> StartSessionAsync(
                StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse(
                    request.ResumeConversationId ?? "fresh",
                    ResumeFailureReason: ResumeFailureReason,
                    SupportsSteering: SupportsSteering,
                    ImportedHistoryCount: ImportedEntries.Count));

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
            {
                var page = ImportedEntries.Skip(request.Offset).Take(request.Limit).ToList();
                return Task.FromResult(new TakeImportedHistoryResponse(page, ImportedEntries.Count));
            }

            /// <summary>
            /// A prompt that never completes, so a test can hold the view-model BUSY. Left null for the
            /// twenty-odd cases that never prompt at all — those still throw rather than quietly
            /// pretending a turn ran.
            /// </summary>
            public TaskCompletionSource<PromptResponse>? PromptGate { get; set; }

            public Task<PromptResponse> PromptAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => PromptGate?.Task ?? throw new InvalidOperationException("These tests never prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(
                string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SummarizeResponse> SummarizeAsync(
                SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
