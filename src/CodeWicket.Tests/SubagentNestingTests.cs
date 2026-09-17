using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A sub-agent's calls belong to the sub-agent, and until issue #125 nothing said so: a fan-out of
    /// three Claude sub-agents put 26 reads, greps and shell commands into the transcript as a flat wall
    /// of top-level rows, indistinguishable from the work of the agent the user was talking to.
    /// <para>Every frame below is verbatim from a live capture (claude-agent-acp <b>0.70.0</b>, one turn,
    /// 217 frames, three background sub-agents) or from the v3 capture already pinned by
    /// <see cref="SubagentTests"/>. Three separate facts are pinned, because they fail independently:</para>
    /// <list type="number">
    /// <item><b>Parentage.</b> <c>_meta.claudeCode.parentToolUseId</c> rides the child's opening frame
    /// AND its completion, so a row can be nested and still settled.</item>
    /// <item><b>The launch that is not a completion.</b> An async sub-agent reports "completed" the
    /// instant it STARTS, with a block of internal metadata as its result — and its children then arrive
    /// underneath a row that has already gone green. Both halves are wrong and both are fixed here.</item>
    /// <item><b>Kiro v3 reaches the same shape by a different route</b> — a flat <c>agentSubtaskId</c>
    /// that the <c>invoke_subagent_*</c> row shares, which is what makes it resolvable at all.</item>
    /// </list>
    /// <para><b>Why no test here settles a launched row:</b> nothing on the wire names one. The returning report is
    /// announced by <c>_meta._claude/origin:{kind:"task-notification"}</c> on a usage frame, which names
    /// no agent, and the launch-side <c>agentId</c> never reappears. In the captured run the counts
    /// matched (3 launches, 3 notifications) but the ORDER did not — launched 1,2,3 and returned 1,3,2 —
    /// so settling in launch order would have mislabelled two rows of three.</para>
    /// </summary>
    public sealed class SubagentNestingTests : IDisposable
    {
        // The three real toolCallIds from the capture, kept verbatim so a reader can find these frames
        // in the log rather than trusting a paraphrase.
        private const string TaskCallId = "toolu_018kd7BfYtdyqE9aKMjJhkKT";
        private const string ChildCallId = "toolu_01X4rDmGxuvz2SjC2Sxq593D";

        private readonly string _root;

        public SubagentNestingTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cwkt-nest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort scratch cleanup */ }
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static (AcpClientTarget Target, List<AgentEvent> Events) MakeTarget()
        {
            var events = new List<AgentEvent>();
            // IIdeServices is only touched by the permission/fs handlers, never by session/update.
            var target = new AcpClientTarget(null!, events.Add, () => "main-session");
            return (target, events);
        }

        private static void Feed(AcpClientTarget target, params string[] updates)
        {
            foreach (var update in updates)
                target.OnSessionUpdate(new SessionUpdateParams { SessionId = "main-session", Update = Parse(update) });
        }

        // ---------------------------------------------------------------------------------------
        // Claude: the captured frames
        // ---------------------------------------------------------------------------------------

        // The Task call as it OPENS: no arguments yet, a placeholder title, and the one fact that
        // matters — subagent:true. Everything nests under this row, so it has to be recognised here
        // rather than once its arguments have streamed in.
        private const string TaskOpenJson =
            """
            {
              "_meta": { "claudeCode": { "toolName": "Agent", "subagent": true } },
              "toolCallId": "toolu_018kd7BfYtdyqE9aKMjJhkKT",
              "sessionUpdate": "tool_call", "rawInput": {}, "status": "pending",
              "title": "Task", "kind": "think", "content": []
            }
            """;

        // The enrichment that names it, and declares it a BACKGROUND launch while the call is still
        // open. This is the earlier of the two async signals; the toolResponse below is the later one.
        private const string TaskEnrichedJson =
            """
            {
              "_meta": { "claudeCode": { "toolName": "Agent", "subagent": true } },
              "toolCallId": "toolu_018kd7BfYtdyqE9aKMjJhkKT",
              "sessionUpdate": "tool_call_update",
              "rawInput": {
                "description": "Map solution projects",
                "prompt": "Find the .sln file(s) and every .csproj.",
                "subagent_type": "Explore",
                "run_in_background": true
              },
              "title": "Map solution projects", "kind": "think"
            }
            """;

        // The adapter's own account of what it did. Note it rides its OWN frame, with no status at all —
        // which is why the flag has to be remembered rather than read off the completion.
        private const string TaskLaunchReceiptJson =
            """
            {
              "_meta": { "claudeCode": { "toolResponse": {
                "isAsync": true, "status": "async_launched", "agentId": "aa5f5cd6089e863a7",
                "description": "Map solution projects", "resolvedModel": "claude-opus-5[1m]"
              }, "toolName": "Agent" } },
              "toolCallId": "toolu_018kd7BfYtdyqE9aKMjJhkKT",
              "sessionUpdate": "tool_call_update"
            }
            """;

        // "completed" — carrying, as its result, the block the adapter itself calls internal metadata
        // and asks not to be quoted. The work has not started.
        private const string TaskCompletedJson =
            """
            {
              "_meta": { "claudeCode": { "toolName": "Agent" } },
              "toolCallId": "toolu_018kd7BfYtdyqE9aKMjJhkKT",
              "sessionUpdate": "tool_call_update", "status": "completed",
              "content": [ { "type": "content", "content": { "type": "text",
                "text": "Async agent launched successfully. (This tool result is internal metadata - never quote or paste any part of it, including the agentId below, into a user-facing reply.)\nagentId: aa5f5cd6089e863a7" } } ]
            }
            """;

        private const string ChildOpenJson =
            """
            {
              "_meta": { "claudeCode": { "toolName": "Glob", "parentToolUseId": "toolu_018kd7BfYtdyqE9aKMjJhkKT" } },
              "toolCallId": "toolu_01X4rDmGxuvz2SjC2Sxq593D",
              "sessionUpdate": "tool_call", "rawInput": { "pattern": "**/*.sln" },
              "status": "pending", "title": "Find `**/*.sln`", "kind": "search", "content": [], "locations": []
            }
            """;

        private const string ChildCompletedJson =
            """
            {
              "_meta": { "claudeCode": { "toolName": "Glob", "parentToolUseId": "toolu_018kd7BfYtdyqE9aKMjJhkKT" } },
              "toolCallId": "toolu_01X4rDmGxuvz2SjC2Sxq593D",
              "sessionUpdate": "tool_call_update", "status": "completed",
              "rawOutput": "Bookshelf.sln"
            }
            """;

        [Fact]
        public void AChildCallNamesTheSubagentThatMadeIt()
        {
            var (target, events) = MakeTarget();

            Feed(target, TaskOpenJson, ChildOpenJson);

            var child = events.OfType<AgentEvent.ToolCallStarted>().Single(e => e.ToolCallId == ChildCallId);
            Assert.Equal(TaskCallId, child.ParentToolCallId);
            Assert.False(child.IsSubagentLaunch);
        }

        /// <summary>
        /// The launch row is recognised on its OPENING frame, before any argument has arrived. It has to
        /// be: the children start arriving while the Task's own arguments are still streaming in, so a
        /// parent identified only once it is fully described would be identified too late to nest them.
        /// </summary>
        [Fact]
        public void TheTaskCallIsMarkedAsTheSubagentLaunchFromItsFirstFrame()
        {
            var (target, events) = MakeTarget();

            Feed(target, TaskOpenJson);

            var launch = Assert.Single(events.OfType<AgentEvent.ToolCallStarted>());
            Assert.True(launch.IsSubagentLaunch);
            Assert.Null(launch.ParentToolCallId); // the launch itself is the main agent's own work
        }

        /// <summary>
        /// The heart of it: "completed" arriving at LAUNCH is reported as a launch, and the metadata
        /// blob does not become the row's result. Both halves matter — the status is what puts a green
        /// tick over work that has not happened, and the blob is what fills the row's detail with a
        /// paragraph asking not to be quoted.
        /// </summary>
        [Fact]
        public void AnAsyncLaunchIsNotReportedAsACompletion()
        {
            var (target, events) = MakeTarget();

            Feed(target, TaskOpenJson, TaskEnrichedJson, TaskLaunchReceiptJson, TaskCompletedJson);

            var done = Assert.Single(events.OfType<AgentEvent.ToolCallCompleted>());
            Assert.True(done.LaunchedInBackground);
            Assert.Null(done.ResultText);
        }

        /// <summary>
        /// The launch receipt is not the only signal, and the earlier one has to work alone: it is an
        /// ordinary argument on a frame the row was going to get anyway, so it survives the adapter
        /// changing how it narrates itself.
        /// </summary>
        [Fact]
        public void TheBackgroundArgumentAloneIsEnoughToRecogniseALaunch()
        {
            var (target, events) = MakeTarget();

            Feed(target, TaskOpenJson, TaskEnrichedJson, TaskCompletedJson);

            Assert.True(Assert.Single(events.OfType<AgentEvent.ToolCallCompleted>()).LaunchedInBackground);
        }

        /// <summary>
        /// The other flavour, and the reason the async fix cannot just be "sub-agent rows never
        /// complete": a SYNCHRONOUS sub-agent really does finish on its completion frame, and that frame
        /// carries its whole report. Suppressing this would throw away the deliverable.
        /// </summary>
        [Fact]
        public void ASynchronousSubagentCompletesNormallyAndKeepsItsReport()
        {
            var (target, events) = MakeTarget();

            Feed(target,
                TaskOpenJson,
                """
                {
                  "_meta": { "claudeCode": { "toolName": "Agent", "subagent": true } },
                  "toolCallId": "toolu_018kd7BfYtdyqE9aKMjJhkKT",
                  "sessionUpdate": "tool_call_update",
                  "rawInput": { "description": "Summarize test methods", "subagent_type": "Explore" },
                  "title": "Summarize test methods", "kind": "think"
                }
                """,
                """
                {
                  "_meta": { "claudeCode": { "toolName": "Agent" } },
                  "toolCallId": "toolu_018kd7BfYtdyqE9aKMjJhkKT",
                  "sessionUpdate": "tool_call_update", "status": "completed",
                  "rawOutput": "## Test methods\n\nOne class, covering the mapping."
                }
                """);

            var done = Assert.Single(events.OfType<AgentEvent.ToolCallCompleted>());
            Assert.False(done.LaunchedInBackground);
            Assert.True(done.Success);
            Assert.Contains("One class", done.ResultText);
        }

        /// <summary>
        /// The main agent's own calls are untouched — the case that must not regress, since it is every
        /// call in every conversation that has no sub-agents in it at all.
        /// </summary>
        [Fact]
        public void AnOrdinaryCallIsNeitherNestedNorALaunch()
        {
            var (target, events) = MakeTarget();

            Feed(target,
                """
                {
                  "_meta": { "claudeCode": { "toolName": "Grep" } },
                  "toolCallId": "toolu_plain", "sessionUpdate": "tool_call",
                  "rawInput": { "pattern": "^Project" }, "status": "pending", "title": "grep", "kind": "search"
                }
                """);

            var started = Assert.Single(events.OfType<AgentEvent.ToolCallStarted>());
            Assert.Null(started.ParentToolCallId);
            Assert.False(started.IsSubagentLaunch);
        }

        /// <summary>
        /// Claude sends a human-readable label for calls whose ACP title is the raw thing being run.
        /// Captured: a row titled <c>git ls-files | grep -v -E '\.cs$' | head -300</c> alongside
        /// <c>cc.title = "List tracked non-C# files"</c>. The command is not lost — it is the row's
        /// input detail, in full.
        /// </summary>
        [Fact]
        public void ClaudesOwnLabelBecomesTheRowTitle()
        {
            var (target, events) = MakeTarget();

            Feed(target,
                """
                {
                  "_meta": { "claudeCode": { "toolName": "Bash", "title": "List tracked non-C# files",
                                             "parentToolUseId": "toolu_018kd7BfYtdyqE9aKMjJhkKT" } },
                  "toolCallId": "toolu_bash1", "sessionUpdate": "tool_call", "status": "pending",
                  "rawInput": { "command": "git ls-files | head -300", "description": "List tracked non-C# files" },
                  "title": "git ls-files | head -300", "kind": "execute"
                }
                """);

            var started = Assert.Single(events.OfType<AgentEvent.ToolCallStarted>());
            Assert.Equal("List tracked non-C# files", started.Title);
            Assert.Contains("git ls-files", started.RawInputJson);
        }

        // ---------------------------------------------------------------------------------------
        // Kiro v3: the same nesting, reached through a flat subtask tag
        // ---------------------------------------------------------------------------------------

        private const string V3InvokeRowJson =
            """
            {
              "sessionUpdate": "tool_call", "toolCallId": "invoke_subagent_tooluse_ylMY", "kind": "other",
              "title": "Sub-agent: general-task-execution",
              "rawInput": { "name": "general-task-execution", "prompt": "Summarize X.cs" },
              "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } }
            }
            """;

        private const string V3ChildCallJson =
            """
            {
              "sessionUpdate": "tool_call", "toolCallId": "tooluse_child", "title": "Read File", "kind": "read",
              "rawInput": { "path": "X.cs" }, "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } }
            }
            """;

        /// <summary>
        /// v3's tag says only "some sub-agent"; the invoking row wears the same tag AND the
        /// <c>invoke_subagent_</c> prefix, which is what turns a flat label into a parent. Before this,
        /// v3 could only DROP these calls — the work happened and the transcript showed none of it.
        /// </summary>
        [Fact]
        public void AV3SubagentsToolCallNestsUnderTheRowThatInvokedIt()
        {
            var (target, events) = MakeTarget();

            Feed(target, V3InvokeRowJson, V3ChildCallJson);

            var invoke = events.OfType<AgentEvent.ToolCallStarted>().Single(e => e.ToolCallId == "invoke_subagent_tooluse_ylMY");
            Assert.True(invoke.IsSubagentLaunch);
            Assert.Null(invoke.ParentToolCallId);

            var child = events.OfType<AgentEvent.ToolCallStarted>().Single(e => e.ToolCallId == "tooluse_child");
            Assert.Equal("invoke_subagent_tooluse_ylMY", child.ParentToolCallId);
        }

        /// <summary>
        /// The edit itself must carry the parent, not just the tool call — and this is the half the
        /// transcript tests cannot reach, since they set the field directly and never run the mapper.
        /// It matters for exactly the shape below: a v3 <c>str_replace</c> carries its diff on the
        /// opening frame, so no tool row is ever built for it and the card has nothing to fold into.
        /// </summary>
        [Fact]
        public void AV3SubagentsEditCarriesTheRowThatInvokedIt()
        {
            var (target, events) = MakeTarget();

            Feed(target, V3InvokeRowJson,
                """
                {
                  "sessionUpdate": "tool_call", "toolCallId": "tooluse_edit", "kind": "edit",
                  "title": "Replace in File",
                  "rawInput": { "path": "Parser.cs", "oldStr": "var x = 1;", "newStr": "var x = 2;" },
                  "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } }
                }
                """);

            var edit = Assert.Single(events.OfType<AgentEvent.EditProposed>());
            Assert.Equal("invoke_subagent_tooluse_ylMY", edit.ParentToolCallId);
        }

        /// <summary>The main agent's own edit carries no parent — the case that must not regress.</summary>
        [Fact]
        public void AnOrdinaryEditCarriesNoParent()
        {
            var (target, events) = MakeTarget();

            Feed(target,
                """
                {
                  "sessionUpdate": "tool_call", "toolCallId": "tooluse_edit", "kind": "edit",
                  "title": "Replace in File",
                  "rawInput": { "path": "Parser.cs", "oldStr": "var x = 1;", "newStr": "var x = 2;" }
                }
                """);

            Assert.Null(Assert.Single(events.OfType<AgentEvent.EditProposed>()).ParentToolCallId);
        }

        /// <summary>
        /// The other construction site: the spec-standard <c>content[] type:"diff"</c> edit, which is
        /// what a conformant agent (and Claude) sends. Two sites, two chances to forget the parent.
        /// </summary>
        [Fact]
        public void ADiffContentEditCarriesItsParentToo()
        {
            var (target, events) = MakeTarget();

            Feed(target, TaskOpenJson,
                """
                {
                  "_meta": { "claudeCode": { "toolName": "Edit", "parentToolUseId": "toolu_018kd7BfYtdyqE9aKMjJhkKT" } },
                  "sessionUpdate": "tool_call", "toolCallId": "toolu_edit1", "kind": "edit",
                  "title": "Edit Parser.cs",
                  "content": [ { "type": "diff", "path": "C:\\ws\\Parser.cs",
                                 "oldText": "var x = 1;", "newText": "var x = 2;" } ]
                }
                """);

            var edit = Assert.Single(events.OfType<AgentEvent.EditProposed>());
            Assert.Equal("toolu_018kd7BfYtdyqE9aKMjJhkKT", edit.ParentToolCallId);
        }

        /// <summary>
        /// Only the TOOL CALLS come back. A v3 sub-agent's prose stays dropped, and that half of the old
        /// rule stands on its own evidence: its streamed summary duplicates the row's deliverable and
        /// concatenates into the main agent's message — the interleaving that made parallel crews
        /// unreadable in the first place.
        /// </summary>
        [Fact]
        public void AV3SubagentsProseIsStillDropped()
        {
            var (target, events) = MakeTarget();

            Feed(target, V3InvokeRowJson,
                """
                { "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "sub streamed" },
                  "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } } }
                """);

            Assert.Empty(events.OfType<AgentEvent.AssistantTextDelta>());
        }

        /// <summary>
        /// A child whose parent was never seen — the frame arrived first, or the row was dropped
        /// upstream — stays top-level. Nesting must never become a way for a call to disappear.
        /// </summary>
        [Fact]
        public void AV3ChildWithNoKnownInvokerStaysTopLevel()
        {
            var (target, events) = MakeTarget();

            Feed(target, V3ChildCallJson); // no invoke row first

            var child = Assert.Single(events.OfType<AgentEvent.ToolCallStarted>());
            Assert.Null(child.ParentToolCallId);
        }

        // ---------------------------------------------------------------------------------------
        // The transcript, driven through the replay path Apply() shares with live events
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AChildRowIsNestedUnderItsParentRatherThanAddedToTheTranscript()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                Start("c1", "Find `**/*.sln`", parent: "p1"),
                Start("c2", "Read Bookshelf.sln", parent: "p1"));

            var top = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("p1", top.ToolCallId);
            Assert.Equal(2, top.Children.Count);
            Assert.Equal("2 calls", top.ChildSummary);
            Assert.True(top.CanExpand);
            Assert.False(top.IsExpanded); // collapsed by default: the fan-out is one line until asked
        }

        /// <summary>
        /// A nested call inserts nothing into the transcript, so it must not end the message the agent
        /// is streaming. Reported from a real session: "Three ag" and "ents are running in parallel"
        /// rendered as two separate assistant messages, because a sub-agent's call landed between the
        /// two deltas and closed the bubble — leaving a sentence cut in half with nothing between the
        /// halves to explain it.
        /// <para>The close is right for a TOP-LEVEL row: it is what keeps the order honest, since the
        /// next delta then starts a fresh bubble below the row. A nested row has no place in that
        /// order, so it has no business changing it. Measured on the wire (0.70.0): the agent narrates
        /// its fan-out while the sub-agents' calls are streaming in, so this fires mid-sentence and
        /// repeatedly — 8 nested frames landed between two halves of one sentence in the captured turn.</para>
        /// </summary>
        [Fact]
        public void ANestedCallDoesNotSplitTheMessageTheAgentIsStreaming()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                new AgentEventDto { Type = "text", Text = "Three ag" },
                Start("c1", "Find `**/*.sln`", parent: "p1"),
                new AgentEventDto { Type = "text", Text = "ents are running in parallel." });

            var message = Assert.Single(vm.Items.OfType<MessageItemViewModel>(), m => m.Role == MessageRole.Assistant);
            Assert.Equal("Three agents are running in parallel.", message.Text);
        }

        /// <summary>
        /// The other half of the same rule: a TOP-LEVEL row still closes the bubble, so the row keeps
        /// its place in the order and the text that follows it reads as coming after it. Without this
        /// the fix above would silently swallow the ordering the close exists to preserve.
        /// </summary>
        [Fact]
        public void ATopLevelCallStillEndsTheStreamingMessage()
        {
            var vm = Replay(
                new AgentEventDto { Type = "text", Text = "Let me look." },
                Start("t1", "Read notes.txt"),
                new AgentEventDto { Type = "text", Text = "Found it." });

            var messages = vm.Items.OfType<MessageItemViewModel>()
                .Where(m => m.Role == MessageRole.Assistant).ToList();
            Assert.Equal(2, messages.Count);
            Assert.Equal("Let me look.", messages[0].Text);
            Assert.Equal("Found it.", messages[1].Text);
        }

        /// <summary>
        /// The unknown-parent rule again, this time at the transcript. An id we cannot resolve is a
        /// top-level row — the outcome before nesting existed — never a dropped one.
        /// </summary>
        [Fact]
        public void AChildWhoseParentIsUnknownIsStillShown()
        {
            var vm = Replay(Start("orphan", "Read notes.txt", parent: "never-seen"));

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("orphan", row.ToolCallId);
            Assert.Null(row.Parent);
        }

        [Fact]
        public void ALaunchedSubagentRowDoesNotShowAsSucceeded()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                new AgentEventDto { Type = "toolDone", ToolCallId = "p1", Success = true, LaunchedInBackground = true });

            Assert.Equal(ToolStatus.Launched, Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Status);
        }

        /// <summary>
        /// A launched row survives the end of the turn as launched. The end-of-turn sweep exists to
        /// settle rows the backend abandoned mid-call, and it must not read this one as abandoned: the
        /// sub-agent is still working, and marking it failed would be as false as the green tick was.
        /// </summary>
        [Fact]
        public void ALaunchedRowIsNotSweptWhenTheTurnEnds()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                new AgentEventDto { Type = "toolDone", ToolCallId = "p1", Success = true, LaunchedInBackground = true },
                new AgentEventDto { Type = "turnDone" });

            Assert.Equal(ToolStatus.Launched, Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Status);
        }

        /// <summary>
        /// A child that never reported IS swept — nesting must not put a row out of reach of the sweep,
        /// or a cancelled fan-out leaves rows spinning forever inside a collapsed parent.
        /// </summary>
        [Fact]
        public void AChildLeftRunningAtTheEndOfATurnIsStillSettled()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                Start("c1", "Read Bookshelf.sln", parent: "p1"),
                new AgentEventDto { Type = "turnDone" });

            var child = Assert.Single(Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Children.OfType<ToolItemViewModel>());
            Assert.Equal(ToolStatus.Failed, child.Status);
        }

        /// <summary>
        /// Nesting is reconstructed from the saved log, because it rides the event rather than being
        /// view-model state — so there is no second code path for a restored conversation to diverge on.
        /// (Every test here restores from disk; this one says so out loud.)
        /// </summary>
        [Fact]
        public void NestingSurvivesARestore()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                Start("c1", "Find `**/*.sln`", parent: "p1"),
                new AgentEventDto { Type = "toolDone", ToolCallId = "c1", Success = true },
                new AgentEventDto { Type = "turnDone" });

            var parent = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            var child = Assert.Single(parent.Children.OfType<ToolItemViewModel>());
            Assert.Same(parent, child.Parent);
            Assert.Equal(ToolStatus.Success, child.Status);
        }

        /// <summary>
        /// Claude sends the same sentence as its own label and as <c>rawInput.description</c>, and the
        /// row draws those in two different places. Two identical lines read as a rendering fault, and
        /// the subtitle would be spending a line to repeat the title.
        /// </summary>
        [Fact]
        public void ASubtitleThatOnlyRepeatsTheTitleIsNotShown()
        {
            var vm = Replay(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "b1",
                Title = "List tracked non-C# files",
                Kind = "execute",
                RawInputJson = """{"command":"git ls-files","description":"List tracked non-C# files"}""",
            });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.False(row.HasDescription);
            Assert.Contains("git ls-files", row.InputDetail);
        }

        [Fact]
        public void ADifferentSubtitleIsStillShown()
        {
            var vm = Replay(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "b2",
                Title = "git ls-files",
                Kind = "execute",
                RawInputJson = """{"command":"git ls-files","description":"List tracked non-C# files"}""",
            });

            Assert.Equal("List tracked non-C# files", Assert.Single(vm.Items.OfType<ToolItemViewModel>()).Description);
        }

        /// <summary>
        /// A sub-agent that EDITS. Claude's `Edit` is an ordinary tool call, so it opens a row — which
        /// nests — and the diff then folds into that row by toolCallId, exactly as it does for the main
        /// agent. So this flavour needs nothing extra, and the test exists to say so: it is the shape
        /// that already worked, and a change to the fold path would silently un-nest every sub-agent
        /// edit without any other test noticing.
        /// </summary>
        [Fact]
        public void ASubagentsEditFoldsIntoItsNestedRow()
        {
            var vm = Replay(
                Start("p1", "Refactor the parser", launch: true),
                Start("c1", "Edit Parser.cs", parent: "p1"),
                new AgentEventDto
                {
                    Type = "edit", ToolCallId = "c1", Path = "Parser.cs",
                    OldText = "var x = 1;", NewText = "var x = 2;",
                });

            Assert.Empty(vm.Items.OfType<EditItemViewModel>());
            var parent = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            var child = Assert.Single(parent.Children.OfType<ToolItemViewModel>());
            Assert.True(child.HasDiff);
        }

        /// <summary>
        /// The flavour that does NOT open a row: Kiro v3's edit call carries its diff on the OPENING
        /// frame, so <c>AcpMapper</c> suppresses <c>ToolCallStarted</c> and the edit renders as a
        /// first-class card instead. A sub-agent's edit then had nothing to fold into and landed at the
        /// top level — the file changed by a sub-agent shown as though the main agent had changed it,
        /// which is exactly the attribution this issue exists to fix, in the one place a tool row could
        /// not carry it.
        /// </summary>
        [Fact]
        public void ASubagentsCardOnlyEditNestsUnderItsLaunchRow()
        {
            var vm = Replay(
                Start("p1", "Refactor the parser", launch: true),
                new AgentEventDto
                {
                    Type = "edit", ToolCallId = "v3-edit", Path = "Parser.cs",
                    OldText = "var x = 1;", NewText = "var x = 2;", ParentToolCallId = "p1",
                });

            Assert.Empty(vm.Items.OfType<EditItemViewModel>());
            var parent = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Single(parent.Children.OfType<EditItemViewModel>());
        }

        /// <summary>The export carries the nesting too — a copied transcript that flattens it is the bug again, one step further from anyone who can see the screen.</summary>
        [Fact]
        public void TheMarkdownExportNestsAChildUnderItsParent()
        {
            var vm = Replay(
                Start("p1", "Map solution projects", launch: true),
                Start("c1", "Find `**/*.sln`", parent: "p1"),
                new AgentEventDto { Type = "toolDone", ToolCallId = "c1", Success = true });

            var markdown = vm.BuildTranscriptMarkdown();

            Assert.Contains("**Tool:** Map solution projects", markdown);
            Assert.Contains("  **Tool:** Find", markdown); // indented under its parent
        }

        /// <summary>
        /// A permission request for a call nested under a launch is attributed to that launch on the
        /// banner (a follow-on from the same review): the row's parent is climbed and its title shown, so a
        /// sub-agent's write does not read as the main agent's. A top-level call carries no such line.
        /// </summary>
        [Fact]
        public async Task ABannerForANestedCallNamesTheLaunchingRow()
        {
            var vm = Replay(
                Start("task-1", "Explore the test failures", launch: true),
                Start("child-1", "Write File", parent: "task-1"),
                Start("plain-1", "Read File"));

            var nested = vm.RequestPermissionAsync(new PermissionRequestDto(
                "child-1", "Write File", "edit", null, null,
                new[] { new Ipc.PermissionOptionDto("allow", "Allow", "allow_once") },
                Path: Path.Combine(_root, "a.cs")));
            var banner = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
            Assert.True(banner.HasSubagent);
            Assert.Equal(PermissionBannerViewModel.SubagentSentence("Explore the test failures"), banner.Subagent);
            Assert.Equal("a.cs", banner.TargetPath);
            banner.Options[0].Command.Execute(null);
            Assert.Equal("allow", (await nested).OptionId);

            // Deliberately not awaited: this one is left PENDING so the banner below is the one it
            // raised. Discarded explicitly because the method is async, and an un-awaited call in an
            // async method is a warning the gate build treats as an error.
            _ = vm.RequestPermissionAsync(new PermissionRequestDto(
                "plain-1", "Read File", "read", null, null,
                new[] { new Ipc.PermissionOptionDto("allow", "Allow", "allow_once") }));
            var plain = Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
            Assert.False(plain.HasSubagent);
        }

        // --- helpers ---

        private static AgentEventDto Start(string id, string title, string? parent = null, bool launch = false) =>
            new()
            {
                Type = "toolStart",
                ToolCallId = id,
                Title = title,
                Kind = launch ? "think" : "read",
                ParentToolCallId = parent,
                IsSubagentLaunch = launch ? true : null,
            };

        private ChatViewModel Replay(params AgentEventDto[] events)
        {
            var store = new FileSessionStore(Path.Combine(_root, "sessions"));
            var session = new PersistedSession
            {
                WorkspaceRootPath = _root,
                AgentWorkingDirectory = _root,
                Title = "Replay",
            };
            foreach (var ev in events)
                session.Log.Add(new TranscriptEntry { Role = "agent", Event = ev });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("claude-code", null, _root, "Prompt", null),
                openDiff: (_, _, _, _) => Task.CompletedTask,
                sessionStore: store);
            vm.RestoreMostRecentSession();
            return vm;
        }

        // Mirrors FoldedEditRowTests' stub: a replay must never reach a backend, so every call that
        // would is an explicit failure rather than a silent no-op.
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
                => throw new InvalidOperationException("A replay must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A replay must not summarize.");
        }
    }
}
