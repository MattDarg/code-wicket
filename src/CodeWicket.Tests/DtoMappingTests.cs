using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The Core &lt;-&gt; wire DTO mapping at the engine/shell boundary. Every AgentEvent variant must map
    /// to a distinct, correctly-populated DTO (a dropped field here silently loses data across the IPC
    /// seam), and the two-way types (workspace snapshot, permission request/decision) must survive a
    /// full round trip. Enum parses fall back to a safe value rather than throwing on an unknown wire string.
    /// </summary>
    public sealed class DtoMappingTests
    {
        /// <summary>
        /// An outcome kind this build does not know about must not cross the wire as an AFFIRMATIVE
        /// claim.
        /// </summary>
        /// <remarks>
        /// The write side defaulted an unmapped enum to <c>"notRequested"</c> — the most reassuring
        /// answer available, and a statement that a security-relevant decision was never put to the
        /// user. Its own inverse (<c>ToOutcome</c>) degrades an unrecognised kind to null = "no record",
        /// and says why: claiming the wrong permission state is worse than admitting we cannot read it.
        /// The two halves have to degrade the same way.
        /// <para>
        /// Reachable by ordinary maintenance: add a member to <c>PermissionOutcomeKind</c>, miss this
        /// switch, and every affected tool row is persisted and replayed asserting nobody was asked —
        /// with no throw and no log. The cast below stands in for that member.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnUnmappedOutcomeKindDoesNotCrossAsNotRequested()
        {
            var unmapped = (PermissionOutcomeKind)9999;

            var dto = DtoMapping.ToDto(new PermissionOutcome(unmapped, "rule", true, false));

            Assert.NotEqual("notRequested", dto.Kind);
            // ...and it round-trips to "no record", which is the state the row already knows how to draw.
            Assert.Null(DtoMapping.ToOutcome(dto));
        }

        /// <summary>The known kinds still round-trip, so the default is not swallowing real answers.</summary>
        [Theory]
        [InlineData(PermissionOutcomeKind.NotRequested)]
        [InlineData(PermissionOutcomeKind.UserAllowed)]
        [InlineData(PermissionOutcomeKind.UserDenied)]
        [InlineData(PermissionOutcomeKind.RuleAllowed)]
        [InlineData(PermissionOutcomeKind.RuleDenied)]
        [InlineData(PermissionOutcomeKind.ModeAllowed)]
        public void EveryKnownOutcomeKindRoundTrips(PermissionOutcomeKind kind)
        {
            var dto = DtoMapping.ToDto(new PermissionOutcome(kind, "rule", true, false));

            Assert.Equal(kind, DtoMapping.ToOutcome(dto)!.Kind);
        }

        [Fact]
        public void TextDelta_MapsToTextDto()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.AssistantTextDelta("hello"));

            Assert.Equal("text", dto.Type);
            Assert.Equal("hello", dto.Text);
        }

        [Fact]
        public void ThinkingDelta_MapsToThinkingDto()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ThinkingDelta("hmm"));

            Assert.Equal("thinking", dto.Type);
            Assert.Equal("hmm", dto.Text);
        }

        [Fact]
        public void ToolCallStarted_CarriesTitleKindAndRawInput()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ToolCallStarted("t1", "Run build", "execute", """{ "command": "dotnet build" }"""));

            Assert.Equal("toolStart", dto.Type);
            Assert.Equal("t1", dto.ToolCallId);
            Assert.Equal("Run build", dto.Title);
            Assert.Equal("execute", dto.Kind);
            Assert.Contains("dotnet build", dto.RawInputJson);
        }

        [Fact]
        public void ToolCallUpdated_MapsToToolUpdateDto()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ToolCallUpdated("t2", "Read x", "read", """{ "file": "x" }"""));

            Assert.Equal("toolUpdate", dto.Type);
            Assert.Equal("t2", dto.ToolCallId);
            Assert.Equal("Read x", dto.Title);
            Assert.Equal("read", dto.Kind);
        }

        [Fact]
        public void ToolCallProgress_MapsToToolProgressDto()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ToolCallProgress("t3", "working"));

            Assert.Equal("toolProgress", dto.Type);
            Assert.Equal("t3", dto.ToolCallId);
            Assert.Equal("working", dto.Message);
        }

        // Live output chunks ride Text (like the other streamed deltas), keyed to their tool call.
        [Fact]
        public void ToolCallOutputChunk_MapsToToolOutputDto()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ToolCallOutputChunk("t9", "On branch main\n"));

            Assert.Equal("toolOutput", dto.Type);
            Assert.Equal("t9", dto.ToolCallId);
            Assert.Equal("On branch main\n", dto.Text);
        }

        // The completion carries semantic success, stdout (Message), and a separate stderr stream.
        [Fact]
        public void ToolCallCompleted_CarriesSuccessResultAndError()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ToolCallCompleted("t4", Success: false, ResultText: "out", ErrorText: "err"));

            Assert.Equal("toolDone", dto.Type);
            Assert.False(dto.Success);
            Assert.Equal("out", dto.Message);
            Assert.Equal("err", dto.ErrorText);
        }

        /// <summary>
        /// The host-authored flag has to survive the WIRE, and this is the round trip that says so —
        /// the rule this file exists for, applied to the field whose loss is the quietest: a card is
        /// built from it and from nothing else, so a dropped mapping shows up as cards that stop
        /// appearing on the one delivery entitled to draw them, with every offline check still green.
        /// <para>
        /// Both values are asserted because both carry meaning. True is the grant. FALSE is written out
        /// rather than folded to null (unlike <c>LaunchedInBackground</c> beside it) because the
        /// persisted log reads absence as "this entry predates the flag" and cards it on replay — so a
        /// false that vanished on the way to disk would forge that date for every echo the agent sends.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ToolCallCompleted_CarriesHostAuthoredAcrossTheWireAndIntoTheLog(bool hostAuthored)
        {
            var dto = DtoMapping.ToDto(new AgentEvent.ToolCallCompleted(
                "t4", Success: true, ResultText: "{\"resultKind\":\"testRun\"}", HostAuthored: hostAuthored));

            Assert.Equal(hostAuthored, dto.HostAuthored);

            // The engine's own formatter options (EngineHost / EngineClient) — the hop the flag must
            // survive to reach the view-model at all.
            var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            var roundTripped = JsonSerializer.Deserialize<AgentEventDto>(JsonSerializer.Serialize(dto, wire), wire);
            Assert.Equal(hostAuthored, roundTripped!.HostAuthored);

            // ...and the persisted log's options (FileSessionStore), where the field must be PRESENT
            // either way for absence to keep meaning "older than this flag".
            var stored = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            Assert.Contains("\"hostAuthored\"", stored, StringComparison.Ordinal);
        }

        [Fact]
        public void EditProposed_CarriesPathBeforeAndAfter()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.EditProposed("a.cs", "old", "new", "t5"));

            Assert.Equal("edit", dto.Type);
            Assert.Equal("a.cs", dto.Path);
            Assert.Equal("old", dto.OldText);
            Assert.Equal("new", dto.NewText);
            Assert.Equal("t5", dto.ToolCallId);
        }

        [Fact]
        public void PlanUpdated_MapsItemsWithStringifiedStatus()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.PlanUpdated("My plan", new[]
            {
                new PlanItem("1", "step one", PlanItemStatus.Completed),
                new PlanItem("2", "step two", PlanItemStatus.InProgress),
            }));

            Assert.Equal("plan", dto.Type);
            Assert.Equal("My plan", dto.Title);
            Assert.Equal(2, dto.PlanItems!.Count);
            Assert.Equal("step one", dto.PlanItems[0].Description);
            Assert.Equal("Completed", dto.PlanItems[0].Status);
            Assert.Equal("InProgress", dto.PlanItems[1].Status);
        }

        [Fact]
        public void SessionError_MapsToErrorDto()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.SessionError("boom", "stacktrace"));

            Assert.Equal("error", dto.Type);
            Assert.Equal("boom", dto.Message);
            Assert.Equal("stacktrace", dto.Details);
        }

        [Fact]
        public void TurnCompleted_CarriesStopReasonAndTokenUsage()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.TurnCompleted(
                new UsageReport { InputTokens = 100, OutputTokens = 50, CachedReadTokens = 900, TotalTokens = 1050 },
                "end_turn"));

            Assert.Equal("turnDone", dto.Type);
            Assert.Equal("end_turn", dto.StopReason);
            Assert.Equal(100, dto.Usage!.InputTokens);
            Assert.Equal(50, dto.Usage!.OutputTokens);
            Assert.Equal(900, dto.Usage!.CachedReadTokens);
            Assert.Equal(1050, dto.Usage!.TotalTokens);
        }

        [Fact]
        public void TurnCompleted_NullUsage_LeavesTokenCountsNull()
        {
            var dto = DtoMapping.ToDto(new AgentEvent.TurnCompleted(null, "end_turn"));

            Assert.Null(dto.Usage);
        }

        [Fact]
        public void UsageUpdated_CarriesContextFillAndBreakdown()
        {
            // The mid-turn snapshot. Fields the backend didn't report must stay null across the wire —
            // a host has to be able to tell "not reported" from "zero".
            var dto = DtoMapping.ToDto(new AgentEvent.UsageUpdated(new UsageReport
            {
                ContextPercent = 2.848,
                ContextUsedTokens = 28480,
                ContextWindowTokens = 1000000,
                Cost = 0.55,
                CostCurrency = "USD",
                Breakdown = new[] { new UsageBreakdownEntry("Your prompts", 4356, 0.4) },
            }));

            Assert.Equal("usage", dto.Type);
            Assert.Equal(2.848, dto.Usage!.ContextPercent);
            Assert.Equal(28480, dto.Usage!.ContextUsedTokens);
            Assert.Equal("USD", dto.Usage!.CostCurrency);
            Assert.Null(dto.Usage!.InputTokens);
            var entry = Assert.Single(dto.Usage!.Breakdown!);
            Assert.Equal("Your prompts", entry.Label);
            Assert.Equal(4356, entry.Tokens);
        }

        [Theory]
        [InlineData(PermissionMode.Prompt)]
        [InlineData(PermissionMode.AcceptReads)]
        [InlineData(PermissionMode.AcceptEdits)]
        [InlineData(PermissionMode.AcceptAll)]
        public void PermissionMode_RoundTrips(PermissionMode mode)
        {
            Assert.Equal(mode, DtoMapping.ToPermissionMode(DtoMapping.ToWire(mode)));
        }

        // An unknown wire mode string must not throw — it degrades to the safe default (Prompt).
        [Fact]
        public void PermissionMode_UnknownString_DefaultsToPrompt()
        {
            Assert.Equal(PermissionMode.Prompt, DtoMapping.ToPermissionMode("SomethingElse"));
        }

        // The retired "ReadOnly" mode migrates to its ladder equivalent rather than falling to Prompt.
        [Fact]
        public void PermissionMode_LegacyReadOnly_MigratesToAcceptReads()
        {
            Assert.Equal(PermissionMode.AcceptReads, DtoMapping.ToPermissionMode("ReadOnly"));
        }

        // ToolName survives the round trip so the shell can resolve an IDE tool's authored risk.
        [Fact]
        public void PermissionRequest_ToolName_RoundTrips()
        {
            var original = new PermissionRequest("t1", "Run tests", null, null, null,
                new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) },
                ToolName: "mcp__code-wicket__run_tests");

            var round = DtoMapping.ToRequest(DtoMapping.ToDto(original));

            Assert.Equal("mcp__code-wicket__run_tests", round.ToolName);
        }

        // Path survives the round trip so the shell's path allow rules see the edit's subject.
        [Fact]
        public void PermissionRequest_Path_RoundTrips()
        {
            var original = new PermissionRequest("t1", "Write hello.txt", "edit", null, null,
                new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) },
                Path: @"C:\ws\hello.txt");

            var round = DtoMapping.ToRequest(DtoMapping.ToDto(original));

            Assert.Equal(@"C:\ws\hello.txt", round.Path);
        }

        // The protected-path reason survives the round trip, or the banner would fall back to naming
        // an always-prompt rule the user never wrote (pre-release security review, September 2026).
        [Fact]
        public void PermissionRequest_FlaggedReason_RoundTrips()
        {
            var original = new PermissionRequest("t1", "Write config.json", "edit", null, null,
                new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) },
                Path: @"C:\p\config.json", FlaggedFragment: @"C:\p\config.json",
                FlaggedReason: ProtectedPaths.BannerReason);

            var round = DtoMapping.ToRequest(DtoMapping.ToDto(original));

            Assert.Equal(@"C:\p\config.json", round.FlaggedFragment);
            Assert.Equal(ProtectedPaths.BannerReason, round.FlaggedReason);
        }

        // The withheld-rule note reaches the banner through the same DTO.
        [Fact]
        public void PermissionRequest_RuleNote_RoundTrips()
        {
            var note = ShellOperators.WithheldSentence("git *", "&&");
            var original = new PermissionRequest("t1", "Running: git status && calc", "execute", null,
                "git status && calc",
                new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) },
                RuleNote: note);

            var round = DtoMapping.ToRequest(DtoMapping.ToDto(original));

            Assert.Equal(note, round.RuleNote);
        }

        // The v2 sub-agent-session fact reaches the banner through the same DTO.
        [Fact]
        public void PermissionRequest_FromSubagentSession_RoundTrips()
        {
            var original = new PermissionRequest("t1", "Write File", "edit", null, null,
                new[] { new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce) },
                FromSubagentSession: true);

            Assert.True(DtoMapping.ToRequest(DtoMapping.ToDto(original)).FromSubagentSession);
        }

        // Same for the row's account of it, which is what a reopened transcript has left.
        [Fact]
        public void PermissionOutcome_CautionReason_RoundTrips()
        {
            var original = new PermissionOutcome(
                PermissionOutcomeKind.UserAllowed, null, false, true, ProtectedPaths.RowReason);

            var round = DtoMapping.ToOutcome(DtoMapping.ToDto(original));

            Assert.True(round!.CautionPrompted);
            Assert.Equal(ProtectedPaths.RowReason, round.CautionReason);
        }

        // RememberPath survives the round trip so an edit's "always" glob reaches the shell policy.
        [Fact]
        public void PermissionDecision_RememberPath_RoundTrips()
        {
            var original = new PermissionDecision("always", RememberPath: @"C:\ws\src\*", PersistRemembered: true);

            var round = DtoMapping.ToDecision(DtoMapping.ToDto(original));

            Assert.Equal(@"C:\ws\src\*", round.RememberPath);
            Assert.True(round.PersistRemembered);
        }

        [Fact]
        public void WorkspaceSnapshot_RoundTrips()
        {
            var original = new WorkspaceSnapshot
            {
                SolutionName = "S.sln",
                ActiveFilePath = "a.cs",
                Selection = new TextSelection("a.cs", 1, 2, 3, 4, "sel"),
                OpenFilePaths = new[] { "a.cs", "b.cs" },
                Diagnostics = new[] { new DiagnosticInfo("a.cs", 5, 6, DiagnosticSeverity.Warning, "warn", "CS0168") },
            };

            var round = DtoMapping.ToSnapshot(DtoMapping.ToDto(original));

            Assert.Equal("S.sln", round.SolutionName);
            Assert.Equal("a.cs", round.ActiveFilePath);
            Assert.Equal(new TextSelection("a.cs", 1, 2, 3, 4, "sel"), round.Selection);
            Assert.Equal(new[] { "a.cs", "b.cs" }, round.OpenFilePaths);
            var diag = Assert.Single(round.Diagnostics);
            Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
            Assert.Equal("CS0168", diag.Code);
        }

        [Fact]
        public void WorkspaceSnapshot_NullSelection_RoundTripsAsNull()
        {
            var round = DtoMapping.ToSnapshot(DtoMapping.ToDto(new WorkspaceSnapshot { SolutionName = "S.sln" }));

            Assert.Null(round.Selection);
        }

        [Fact]
        public void PermissionRequest_RoundTrips()
        {
            var original = new PermissionRequest("t1", "Run build", "execute", "detail", "dotnet build", new[]
            {
                new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
                new PermissionOption("always", "Always", PermissionOptionKind.AllowAlways),
            });

            var round = DtoMapping.ToRequest(DtoMapping.ToDto(original));

            Assert.Equal("t1", round.ToolCallId);
            Assert.Equal("execute", round.Kind);
            Assert.Equal("dotnet build", round.Command);
            Assert.Equal(2, round.Options.Count);
            Assert.Equal(PermissionOptionKind.AllowAlways, round.Options[1].Kind);
        }

        [Fact]
        public void PermissionDecision_RoundTrips()
        {
            var original = new PermissionDecision("allow", Cancelled: false, RememberCommand: "git *", PersistRemembered: true);

            var round = DtoMapping.ToDecision(DtoMapping.ToDto(original));

            Assert.Equal("allow", round.OptionId);
            Assert.Equal("git *", round.RememberCommand);
            Assert.True(round.PersistRemembered);
        }

        // Provider metadata expands the capability flags to their string names for the picker.
        [Fact]
        public void ProviderInfo_EnumeratesCapabilityFlags()
        {
            var provider = new FakeProvider(AgentCapabilities.ToolCalls | AgentCapabilities.ResumeSession);

            var dto = DtoMapping.ToDto(provider);

            Assert.Equal("fake", dto.Id);
            Assert.Contains("ToolCalls", dto.Capabilities);
            Assert.Contains("ResumeSession", dto.Capabilities);
            Assert.DoesNotContain("None", dto.Capabilities);
            Assert.Equal("m1", dto.Models.Single().Id);
        }

        private sealed class FakeProvider : IAgentProvider
        {
            private readonly AgentCapabilities _caps;
            public FakeProvider(AgentCapabilities caps) => _caps = caps;

            public string ProviderId => "fake";
            public string DisplayName => "Fake";
            public IReadOnlyList<ModelInfo> Models => new[] { new ModelInfo("m1", "Model 1") };
            public AgentCapabilities Capabilities => _caps;

            public System.Threading.Tasks.Task<IAgentSession> StartSessionAsync(
                SessionOptions options, CodeWicket.Core.Ide.IIdeServices ide,
                System.Threading.CancellationToken cancellationToken = default) =>
                throw new System.NotImplementedException();
        }

        /// <summary>
        /// The ambient debugger state must survive the wire, because the two halves live on opposite
        /// sides of it: the snapshot is built IDE-side and rendered engine-side (issue #73).
        /// </summary>
        /// <remarks>
        /// A field added to <c>WorkspaceSnapshot</c> and to the formatter but NOT to the DTO is dropped
        /// in transit in silence - and a timing line proving the IDE side built the value is not proof
        /// the block reached the prompt.
        /// Nothing about the code looks wrong at either end; only the round trip shows it.
        /// </remarks>
        [Fact]
        public void WorkspaceSnapshot_CarriesTheDebugSession()
        {
            var original = new WorkspaceSnapshot
            {
                SolutionName = "S",
                DebugSession = new DebugSessionInfo(IsStopped: true)
                {
                    File = @"C:\src\HelloWorldService.cs",
                    Line = 34,
                    Method = "ConsoleApp1.HelloWorldService.Recurse",
                    Reason = "Breakpoint - set by this conversation",
                    Thread = "Thread 21184",
                    ProcessId = 4812,
                    StopNumber = 3,
                },
            };

            var round = DtoMapping.ToSnapshot(DtoMapping.ToDto(original));

            Assert.NotNull(round.DebugSession);
            Assert.Equal(original.DebugSession, round.DebugSession);
        }

        // A snapshot with no debug session must stay null rather than arrive as an empty one: "not
        // debugging" and "debugging, details unknown" render differently and mean different things.
        [Fact]
        public void WorkspaceSnapshot_WithoutADebugSession_StaysNull()
        {
            var round = DtoMapping.ToSnapshot(DtoMapping.ToDto(new WorkspaceSnapshot { SolutionName = "S" }));

            Assert.Null(round.DebugSession);
        }
    }
}
