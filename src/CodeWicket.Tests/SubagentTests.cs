using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Sub-agent ("agent crew") handling. Kiro multiplexes each sub-agent as its own ACP session over
    /// the same connection; unfiltered, eight parallel sub-agents' message chunks concatenated into the
    /// main transcript as interleaved garbage (observed live 2026-07-16). Pins: (1) the session filter —
    /// foreign-session updates never map to transcript events, except (2) the sub-agent's final
    /// <c>rawInput.taskResult</c>, surfaced as a <see cref="AgentEvent.SubagentResult"/>; and (3) the
    /// <c>_kiro.dev/subagent/list_update</c> roster parse. Frames mirror the real capture.
    /// Kiro v3 changed the model (sub-agents run in the PRIMARY session, tagged
    /// <c>_meta.kiro.agentSubtaskId</c>, no roster notification); the v3 block pins
    /// <see cref="AcpMapper.IsSubagentInternalUpdate"/> recognising that internal stream while keeping
    /// the <c>invoke_subagent_*</c> tool row. Frames mirror the real v3 capture (2026-07-21).
    /// <para>Since issue #125 only the sub-agent's PROSE is dropped: its tool calls nest under the
    /// invoking row (see <c>SubagentNestingTests</c>). The predicate below is unchanged — it still
    /// answers "is this a sub-agent's internal frame" — but <c>AcpClientTarget</c> now acts on that
    /// answer differently depending on whether the frame is a tool call.</para>
    /// </summary>
    public sealed class SubagentTests
    {
        // A real roster frame's params (trimmed): the full current crew on every change.
        private const string RosterParamsJson =
            """
            {
              "subagents": [
                {
                  "sessionId": "ef3379e3-11f6-4cab-8326-5fa812985949",
                  "sessionName": "read_tep_message_serialization_tests",
                  "agentName": "kiro_default",
                  "initialQuery": "Read and return the full content of this file: C:\\repo\\TepMessageSerializationTests.cs",
                  "status": { "type": "working", "message": "Running" },
                  "group": "crew-Read all test files ",
                  "role": "kiro_default",
                  "dependsOn": [],
                  "createdAtMs": 1784158096644
                },
                {
                  "sessionId": "2e5fb81f-ce1a-4f2e-b189-a8e794c487bf",
                  "sessionName": "read_xml_request_reader_tests",
                  "status": { "type": "terminated" }
                }
              ],
              "pendingStages": []
            }
            """;

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void MapSubagentList_ParsesRosterFrame()
        {
            var roster = AcpMapper.MapSubagentList(Parse(RosterParamsJson));

            Assert.NotNull(roster);
            Assert.Equal(2, roster!.Agents.Count);

            var first = roster.Agents[0];
            Assert.Equal("ef3379e3-11f6-4cab-8326-5fa812985949", first.SessionId);
            Assert.Equal("read_tep_message_serialization_tests", first.Name);
            Assert.StartsWith("Read and return", first.Description);
            Assert.Equal("working", first.Status);
            Assert.Equal("Running", first.StatusMessage);
            Assert.Equal("crew-Read all test files ", first.Group);

            var second = roster.Agents[1];
            Assert.Equal("terminated", second.Status);
            Assert.Null(second.StatusMessage);
        }

        // Kiro sends an empty roster when the crew is disposed; mapping it to null keeps the host's
        // card at its final states instead of wiping it.
        [Fact]
        public void MapSubagentList_EmptyOrBadShape_ReturnsNull()
        {
            Assert.Null(AcpMapper.MapSubagentList(Parse("""{ "subagents": [], "pendingStages": [] }""")));
            Assert.Null(AcpMapper.MapSubagentList(Parse("""{ "somethingElse": true }""")));
            Assert.Null(AcpMapper.MapSubagentList(Parse("""{ "subagents": [ { "noSessionId": true } ] }""")));
        }

        [Fact]
        public void ExtractSubagentResult_FromWrapUpToolCall()
        {
            var update = Parse(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "tooluse_w74wcK5RkBzRVkTFAplgDl",
                  "title": "Summarizing",
                  "rawInput": {
                    "__tool_use_purpose": "Returning the full file content to the main agent",
                    "taskDescription": "Read and return the full content of X.cs",
                    "taskResult": "The full content of X.cs is:\n\n```csharp\nclass X { }\n```"
                  }
                }
                """);

            var result = AcpMapper.ExtractSubagentResult("sub-session-1", update);

            Assert.NotNull(result);
            Assert.Equal("sub-session-1", result!.SubagentSessionId);
            Assert.Contains("class X { }", result.Text);
        }

        [Fact]
        public void ExtractSubagentResult_NonResultUpdates_ReturnNull()
        {
            Assert.Null(AcpMapper.ExtractSubagentResult("sub", Parse(
                """{ "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "streamed noise" } }""")));
            Assert.Null(AcpMapper.ExtractSubagentResult("sub", Parse(
                """{ "sessionUpdate": "tool_call", "toolCallId": "t1", "title": "Reading", "rawInput": { "path": "X.cs" } }""")));
            Assert.Null(AcpMapper.ExtractSubagentResult("", Parse(
                """{ "sessionUpdate": "tool_call", "rawInput": { "taskResult": "orphaned" } }""")));
        }

        // The result lands in the transcript and the persisted session log, so it stays bounded.
        [Fact]
        public void ExtractSubagentResult_TruncatesOversizedResults()
        {
            var huge = new string('x', 50_000);
            var update = Parse($$"""{ "sessionUpdate": "tool_call", "rawInput": { "taskResult": "{{huge}}" } }""");

            var result = AcpMapper.ExtractSubagentResult("sub", update);

            Assert.NotNull(result);
            Assert.True(result!.Text.Length < 21_000);
            Assert.EndsWith("…(truncated)", result.Text);
        }

        // --- AcpClientTarget: the session filter itself ---

        private static (AcpClientTarget Target, List<AgentEvent> Events) MakeTarget(string? primarySessionId)
        {
            var events = new List<AgentEvent>();
            // IIdeServices is only touched by the permission/fs handlers, not session/update.
            var target = new AcpClientTarget(null!, events.Add, () => primarySessionId);
            return (target, events);
        }

        private static SessionUpdateParams UpdateFrame(string sessionId, string updateJson) =>
            new() { SessionId = sessionId, Update = Parse(updateJson) };

        private const string ChunkJson =
            """{ "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "hello" } }""";

        [Fact]
        public void ForeignSessionChunks_AreDropped_MainSessionChunks_Flow()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSessionUpdate(UpdateFrame("sub-session", ChunkJson));
            Assert.Empty(events);

            target.OnSessionUpdate(UpdateFrame("main-session", ChunkJson));
            var delta = Assert.IsType<AgentEvent.AssistantTextDelta>(Assert.Single(events));
            Assert.Equal("hello", delta.Text);
        }

        [Fact]
        public void ForeignSessionTaskResult_SurfacesAsSubagentResult()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSessionUpdate(UpdateFrame("sub-session",
                """{ "sessionUpdate": "tool_call_update", "toolCallId": "t1", "status": "completed", "title": "Summarizing", "rawInput": { "taskResult": "the answer" } }"""));

            var result = Assert.IsType<AgentEvent.SubagentResult>(Assert.Single(events));
            Assert.Equal("sub-session", result.SubagentSessionId);
            Assert.Equal("the answer", result.Text);
        }

        // Before session/new returns there is no primary id; nothing must be filtered then (the id is
        // only unknown during the handshake, when no sub-agents can exist).
        [Fact]
        public void NoPrimarySessionYet_NothingIsFiltered()
        {
            var (target, events) = MakeTarget(string.Empty);

            target.OnSessionUpdate(UpdateFrame("whatever", ChunkJson));

            Assert.Single(events);
        }

        [Fact]
        public void RosterNotification_EmitsSubagentsUpdated()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSubagentListUpdate(Parse(RosterParamsJson));
            target.OnSubagentListUpdate(Parse("""{ "subagents": [], "pendingStages": [] }"""));

            var roster = Assert.IsType<AgentEvent.SubagentsUpdated>(Assert.Single(events));
            Assert.Equal(2, roster.Agents.Count);
        }

        // --- Kiro v3: sub-agents run in the PRIMARY session, tagged _meta.kiro.agentSubtaskId ---
        // (v3 doesn't multiplex them into separate sessions like v2, so the session filter above never
        // trips; IsSubagentInternalUpdate drops the internal stream while keeping the invoke_subagent row.)

        // A sub-agent's streamed message chunk (tagged, no toolCallId) must not reach the transcript —
        // it duplicates the invoke_subagent row's result and jams into the main agent's message.
        [Fact]
        public void V3SubagentInternalChunk_InPrimarySession_IsDropped()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSessionUpdate(UpdateFrame("main-session",
                """
                { "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "sub streamed" },
                  "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } } }
                """));

            Assert.Empty(events);
        }

        // A sub-agent's own tool churn used to be dropped along with its prose. It is now NESTED under
        // the invoke_subagent row instead (issue #125) — the work stays visible and attributable, which
        // is strictly better than hiding it, and is only possible because the invoking row shares the
        // subtask id. What is pinned here is the half that did NOT change: with no invoking row seen
        // there is nothing to nest under, and an unattributable call still reaches the transcript rather
        // than vanishing. The nesting itself is pinned by SubagentNestingTests.
        [Fact]
        public void V3SubagentInternalToolCall_WithNoInvokerSeen_StillReachesTheTranscript()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSessionUpdate(UpdateFrame("main-session",
                """
                { "sessionUpdate": "tool_call", "toolCallId": "tooluse_child", "title": "Read File", "kind": "read",
                  "rawInput": { "path": "X.cs" }, "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } } }
                """));

            var started = Assert.IsType<AgentEvent.ToolCallStarted>(Assert.Single(events));
            Assert.Null(started.ParentToolCallId);
        }

        // The top-level "Sub-agent: …" row is tagged with the subtask id too, but its invoke_subagent_*
        // toolCallId keeps it in the transcript — that's the row the user sees, and its completed
        // rawOutput is the deliverable.
        [Fact]
        public void V3InvokeSubagentRow_InPrimarySession_Flows()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSessionUpdate(UpdateFrame("main-session",
                """
                { "sessionUpdate": "tool_call", "toolCallId": "invoke_subagent_tooluse_ylMY", "kind": "other",
                  "title": "Sub-agent: general-task-execution",
                  "rawInput": { "name": "general-task-execution", "prompt": "Summarize X.cs" },
                  "_meta": { "kiro": { "agentSubtaskId": "205d403c-sub" } } }
                """));

            var started = Assert.IsType<AgentEvent.ToolCallStarted>(Assert.Single(events));
            Assert.Equal("invoke_subagent_tooluse_ylMY", started.ToolCallId);
            Assert.Equal("Sub-agent: general-task-execution", started.Title);
        }

        // An untagged frame is the main agent's own — never filtered.
        [Fact]
        public void V3MainAgentChunk_Untagged_Flows()
        {
            var (target, events) = MakeTarget("main-session");

            target.OnSessionUpdate(UpdateFrame("main-session", ChunkJson));

            Assert.IsType<AgentEvent.AssistantTextDelta>(Assert.Single(events));
        }

        [Fact]
        public void IsSubagentInternalUpdate_Discriminates()
        {
            // Tagged + no/other toolCallId → internal (dropped).
            Assert.True(AcpMapper.IsSubagentInternalUpdate(Parse(
                """{ "sessionUpdate": "agent_message_chunk", "_meta": { "kiro": { "agentSubtaskId": "s1" } } }""")));
            Assert.True(AcpMapper.IsSubagentInternalUpdate(Parse(
                """{ "sessionUpdate": "tool_call", "toolCallId": "tooluse_child", "_meta": { "kiro": { "agentSubtaskId": "s1" } } }""")));
            // Tagged + invoke_subagent_* → the kept top-level row.
            Assert.False(AcpMapper.IsSubagentInternalUpdate(Parse(
                """{ "sessionUpdate": "tool_call", "toolCallId": "invoke_subagent_tooluse_x", "_meta": { "kiro": { "agentSubtaskId": "s1" } } }""")));
            // Untagged → main agent.
            Assert.False(AcpMapper.IsSubagentInternalUpdate(Parse(ChunkJson)));
        }
    }
}
