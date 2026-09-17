using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How a completed <c>tool_call_update</c> maps to a <see cref="AgentEvent.ToolCallCompleted"/>:
    /// result text (stdout), the separate error stream (stderr), and semantic success (a shell command
    /// that ran but exited non-zero is a failure). Frames mirror real captures — Claude sends a string
    /// rawOutput; Kiro sends a structured object with per-stream text + an exit code.
    /// </summary>
    public sealed class AcpMapperOutputTests
    {
        private static AgentEvent.ToolCallCompleted MapCompletion(string updateJson)
        {
            var update = JsonDocument.Parse(updateJson).RootElement.Clone();
            return AcpMapper.Map(update).OfType<AgentEvent.ToolCallCompleted>().Single();
        }

        private static AgentEvent[] MapAll(string updateJson)
        {
            var update = JsonDocument.Parse(updateJson).RootElement.Clone();
            return AcpMapper.Map(update).ToArray();
        }

        // --- Live output chunks (a running command's stdout, streamed before completion) ---

        // The real Kiro shape from issue #22's capture: a status-less tool_call_update whose content[]
        // carries a chunk of the running command's stdout. Previously dropped — the row showed nothing
        // until the command finished (or hung).
        [Fact]
        public void Kiro_StatuslessContentUpdate_EmitsLiveOutputChunk()
        {
            var events = MapAll(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "rm 'src/stuff/appsettings.dev.json'\n" } } ]
                }
                """);

            var chunk = Assert.Single(events.OfType<AgentEvent.ToolCallOutputChunk>());
            Assert.Equal("t1", chunk.ToolCallId);
            Assert.Contains("appsettings.dev.json", chunk.Text);
            Assert.Empty(events.OfType<AgentEvent.ToolCallCompleted>());
        }

        // A completion frame's content is the RESULT surface (ToolCallCompleted.ResultText) — it must
        // not double-emit as a live chunk on top.
        [Fact]
        public void CompletionWithContent_EmitsNoChunk()
        {
            var events = MapAll(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "status": "completed",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "done output" } } ]
                }
                """);

            Assert.Empty(events.OfType<AgentEvent.ToolCallOutputChunk>());
            Assert.Contains("done output", Assert.Single(events.OfType<AgentEvent.ToolCallCompleted>()).ResultText);
        }

        // An in-progress status alongside content: both the chunk and the progress event surface.
        [Fact]
        public void InProgressStatusWithContent_EmitsChunkAndProgress()
        {
            var events = MapAll(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "status": "in_progress",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "compiling...\n" } } ]
                }
                """);

            Assert.Contains("compiling", Assert.Single(events.OfType<AgentEvent.ToolCallOutputChunk>()).Text);
            Assert.Single(events.OfType<AgentEvent.ToolCallProgress>());
        }

        // A chunk larger than the 2000-char RESULT clamp must survive intact: chunks feed the tool
        // row (tail-capped there) and the terminal mirror, and a single Get-ChildItem content entry
        // was cut off at 2k in the pane (in Visual Studio, 2026-07-17). Only the pathology cap (256K) truncates.
        [Fact]
        public void LargeChunk_IsNotResultClamped()
        {
            var big = new string('x', 5000);
            var events = MapAll(
                $$"""
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "{{big}}" } } ]
                }
                """);

            Assert.Equal(5000, Assert.Single(events.OfType<AgentEvent.ToolCallOutputChunk>()).Text.Length);
        }

        // Chunk boundaries fall mid-stream, so leading/trailing whitespace is real output — the old
        // shared Clamp's Trim ate Kiro's chunk-boundary newlines (fused dir-listing rows).
        [Fact]
        public void ChunkWhitespace_IsPreserved()
        {
            var events = MapAll(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "\n    Directory: C:\\x\n" } } ]
                }
                """);

            Assert.Equal("\n    Directory: C:\\x\n", Assert.Single(events.OfType<AgentEvent.ToolCallOutputChunk>()).Text);
        }

        // Whitespace-only content (a bare newline keep-alive) must not emit an empty chunk.
        [Fact]
        public void WhitespaceOnlyContent_EmitsNoChunk()
        {
            var events = MapAll(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "\n" } } ]
                }
                """);

            Assert.Empty(events.OfType<AgentEvent.ToolCallOutputChunk>());
        }

        [Fact]
        public void Kiro_StructuredOutput_ExitZero_SucceedsWithStdoutAndNoError()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t1",
                  "status": "completed",
                  "rawOutput": { "items": [ { "Json": { "exit_status": "exit code: 0", "stdout": "Build succeeded.\n", "stderr": "" } } ] }
                }
                """);

            Assert.True(done.Success);
            Assert.Contains("Build succeeded", done.ResultText);
            Assert.Null(done.ErrorText);
        }

        // A command that "completed" but exited non-zero must be a failure, with stderr surfaced apart.
        [Fact]
        public void Kiro_StructuredOutput_NonZeroExit_FailsAndSurfacesStderr()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t2",
                  "status": "completed",
                  "rawOutput": { "items": [ { "Json": { "exit_status": "exit code: 1", "stdout": "partial output\n", "stderr": "error CS1002: ; expected\n" } } ] }
                }
                """);

            Assert.False(done.Success);
            Assert.Contains("partial output", done.ResultText);
            Assert.Contains("CS1002", done.ErrorText);
        }

        // An unparseable exit_status must not flip success — fall back to the ACP status.
        [Fact]
        public void Kiro_StructuredOutput_UnparseableExit_FallsBackToStatus()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t3",
                  "status": "completed",
                  "rawOutput": { "items": [ { "Json": { "exit_status": "unknown", "stdout": "x" } } ] }
                }
                """);

            Assert.True(done.Success);
        }

        [Fact]
        public void Claude_StringOutput_BecomesResultText()
        {
            var done = MapCompletion(
                """
                { "sessionUpdate": "tool_call_update", "toolCallId": "t4", "status": "completed", "rawOutput": "stdout text here" }
                """);

            Assert.True(done.Success);
            Assert.Equal("stdout text here", done.ResultText);
            Assert.Null(done.ErrorText);
        }

        [Fact]
        public void ContentArray_IsUsedWhenNoRawOutput()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t5",
                  "status": "completed",
                  "content": [ { "type": "content", "content": { "type": "text", "text": "content result" } } ]
                }
                """);

            Assert.Equal("content result", done.ResultText);
        }

        [Fact]
        public void StatusFailed_IsAFailure()
        {
            var done = MapCompletion(
                """
                { "sessionUpdate": "tool_call_update", "toolCallId": "t6", "status": "failed", "rawOutput": "boom" }
                """);

            Assert.False(done.Success);
        }

        // --- Kiro v3: the payload IS rawOutput, with no items[].Json wrapper (issue #189) ---
        //
        // Same split as the task list in #119: the default engine wraps its structured result as
        // rawOutput.items[].Json, v3 puts it directly on rawOutput. Every reader here was bound to the
        // wrapped shape alone, so on v3 they answered nothing at all — silently, since "no stderr" and
        // "a nesting we don't recognise" produce the same null. Read through KiroResultPayloads, which
        // yields BOTH candidates, so neither engine has a branch and the three readers cannot come to
        // disagree about where to look.

        /// <summary>
        /// A v3 command's stdout. Wrapped-shape coverage above stays as it is: this is the same field
        /// one nesting up.
        /// </summary>
        [Fact]
        public void KiroV3_UnwrappedRawOutput_YieldsStdout()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update", "toolCallId": "v1", "status": "completed",
                  "rawOutput": { "stdout": "3 files changed", "stderr": "", "exit_status": "exit code: 0" }
                }
                """);

            Assert.Equal("3 files changed", done.ResultText);
            Assert.True(done.Success);
        }

        /// <summary>
        /// The half issue #189 was looking at: a failed v3 call HAS an account of itself, and it was
        /// being dropped — so the row settled red with nothing to expand and the user had nowhere to
        /// ask why.
        /// </summary>
        [Fact]
        public void KiroV3_UnwrappedRawOutput_YieldsStderr()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update", "toolCallId": "v2", "status": "failed",
                  "rawOutput": { "stderr": "EACCES: permission denied, open 'temp.txt'" }
                }
                """);

            Assert.Equal("EACCES: permission denied, open 'temp.txt'", done.ErrorText);
        }

        /// <summary>
        /// The sharper one, because it is wrong in the SAFE-LOOKING direction: with no exit code ever
        /// parsed, a v3 command that ran and failed reported <c>completed</c> with a green tick.
        /// </summary>
        [Fact]
        public void KiroV3_UnwrappedNonZeroExit_IsAFailure()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update", "toolCallId": "v3", "status": "completed",
                  "rawOutput": { "stdout": "Build FAILED.", "exit_status": "exit code: 1" }
                }
                """);

            Assert.False(done.Success);
        }

        /// <summary>
        /// v3's FILE tools report neither stream — a completed write answers only <c>message</c>
        /// (captured 2026-09-03, verbatim). Reading stdout alone left every v3 write with an empty
        /// expander, which is the same complaint as the failed case above with a quieter symptom.
        /// </summary>
        [Fact]
        public void KiroV3_WriteFileMessage_BecomesTheResultText()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update", "toolCallId": "tooluse_5dMADk9VmxMADYIKK4lYJ9",
                  "status": "completed", "title": "Write File",
                  "rawOutput": { "message": "Created the c:\\Users\\someone\\source\\repos\\BlazorApp1\\temp.txt file." }
                }
                """);

            Assert.Contains("Created the", done.ResultText);
            Assert.True(done.Success);
        }

        /// <summary>
        /// And a FAILED v3 write answers a bare string rawOutput, not an object — so the two halves of
        /// one tool's reporting take different branches. Captured 2026-09-03.
        /// </summary>
        [Fact]
        public void KiroV3_FailedWrite_ReportsItsStringRawOutput()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update", "toolCallId": "tooluse_jd5Hxg", "status": "failed",
                  "title": "Write File",
                  "rawOutput": "ENOENT: no such file or directory, mkdir 'C:\\Users\\someone\\.kiro\\sessions'"
                }
                """);

            Assert.False(done.Success);
            Assert.Contains("ENOENT", done.ResultText);
        }

        /// <summary>
        /// The wrapped shape still WINS where both could match, so nothing about the default engine
        /// moves. Without this the fix could quietly have swapped which nesting is authoritative.
        /// </summary>
        [Fact]
        public void WrappedPayloadStillWinsOverTheUnwrappedOne()
        {
            var done = MapCompletion(
                """
                {
                  "sessionUpdate": "tool_call_update", "toolCallId": "v4", "status": "completed",
                  "rawOutput": { "stdout": "outer", "items": [ { "Json": { "stdout": "inner" } } ] }
                }
                """);

            Assert.Equal("inner", done.ResultText);
        }
    }
}
