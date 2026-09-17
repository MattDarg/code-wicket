using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How <see cref="AcpMapper.Map"/> turns <c>session/update</c> frames into <see cref="AgentEvent"/>s
    /// beyond the command/output surface: streamed text/thinking chunks, the spec-standard diff edit
    /// surface (incl. new-file and degenerate-diff repair from the read cache), plan cards (Kiro's
    /// task-list tool and the spec <c>plan</c> channel), and in-flight tool-row enrichment. Frames
    /// mirror real captures so a wire-shape regression fails here rather than silently in the UI.
    /// </summary>
    public sealed class AcpMapperEventTests
    {
        private static IReadOnlyList<AgentEvent> Map(
            string updateJson,
            IDictionary<string, string>? cache = null,
            IDictionary<string, FileWriteResult>? writes = null,
            string? agentRoot = null)
        {
            var update = JsonDocument.Parse(updateJson).RootElement.Clone();
            return AcpMapper.Map(update, cache, writes, agentRoot).ToList();
        }

        /// <summary>
        /// Several frames of ONE session, mapped through the per-session caches <c>AcpClientTarget</c>
        /// keeps — the only way to exercise a decision that spans two frames (issue #190). Primed in
        /// the same order the live path primes it: cache first, then map.
        /// </summary>
        private static IReadOnlyList<AgentEvent> MapSession(params string[] updateJson)
        {
            var planToolCalls = new HashSet<string>(System.StringComparer.Ordinal);
            var events = new List<AgentEvent>();
            foreach (var json in updateJson)
            {
                var update = JsonDocument.Parse(json).RootElement.Clone();
                AcpMapper.CachePlanToolCall(update, planToolCalls);
                events.AddRange(AcpMapper.Map(update, planToolCalls: planToolCalls));
            }

            return events;
        }

        private static T Single<T>(
            string updateJson,
            IDictionary<string, string>? cache = null,
            IDictionary<string, FileWriteResult>? writes = null,
            string? agentRoot = null) where T : AgentEvent =>
            Map(updateJson, cache, writes, agentRoot).OfType<T>().Single();

        /// <summary>
        /// A write-capture map as <c>AcpClientTarget</c> builds it: keyed by the applier's resolved path
        /// put through the SAME normalize call the mapper's lookup uses, case-insensitive. Keying it by
        /// hand instead would test the test — the two sides meet here only because one function maps both
        /// spellings (the applier's and the agent's) onto one string.
        /// </summary>
        private static Dictionary<string, FileWriteResult> Captured(
            string resolvedPath, string oldText, string newText, string? agentRoot = null) =>
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                [AcpMapper.NormalizeLocalPath(resolvedPath, agentRoot)] =
                    new FileWriteResult(resolvedPath, oldText, newText),
            };

        [Fact]
        public void AgentMessageChunk_BecomesAssistantTextDelta()
        {
            var ev = Single<AgentEvent.AssistantTextDelta>(
                """
                { "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "Hello" } }
                """);

            Assert.Equal("Hello", ev.Text);
        }

        [Fact]
        public void AgentThoughtChunk_BecomesThinkingDelta()
        {
            var ev = Single<AgentEvent.ThinkingDelta>(
                """
                { "sessionUpdate": "agent_thought_chunk", "content": { "type": "text", "text": "let me think" } }
                """);

            Assert.Equal("let me think", ev.Text);
        }

        // An empty text chunk carries no signal; it must not emit a delta (would render a blank line).
        [Fact]
        public void EmptyTextChunk_EmitsNothing()
        {
            Assert.Empty(Map(
                """
                { "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "" } }
                """));
        }

        // A frame with no recognised sessionUpdate discriminator yields nothing rather than throwing.
        [Fact]
        public void UnknownUpdate_EmitsNothing()
        {
            Assert.Empty(Map("""{ "sessionUpdate": "_kiro.dev/whatever", "foo": 1 }"""));
            Assert.Empty(Map("""{ "noDiscriminator": true }"""));
            Assert.Empty(Map("""[1, 2, 3]"""));
        }

        // The spec-standard edit surface: a tool_call with a diff content entry. A brand-new file has a
        // null oldText, surfaced as an empty "before" (renders as all-added), and no generic tool row.
        [Fact]
        public void ToolCallWithDiff_NewFile_EmitsEditWithEmptyBeforeAndNoToolRow()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t1",
                  "title": "Write notes.txt",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "C:\\src\\notes.txt", "oldText": null, "newText": "hello world" } ]
                }
                """);

            var edit = Assert.Single(events.OfType<AgentEvent.EditProposed>());
            Assert.Equal("C:\\src\\notes.txt", edit.Path);
            Assert.Equal("", edit.OldText);
            Assert.Equal("hello world", edit.NewText);
            Assert.Equal("t1", edit.ToolCallId);
            // Diffs replace the generic "started" row entirely.
            Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());
        }

        // A genuine (surgical) diff keeps its reported before-text verbatim.
        [Fact]
        public void ToolCallWithDiff_RealEdit_KeepsReportedBefore()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t2",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "int x = 1;", "newText": "int x = 2;" } ]
                }
                """);

            Assert.Equal("int x = 1;", edit.OldText);
            Assert.Equal("int x = 2;", edit.NewText);
            // No locations on this update → no reported line.
            Assert.Null(edit.Line);
        }

        // A Kiro edit tool_call carries its intent (__tool_use_purpose) and operation (rawInput.command,
        // e.g. strReplace) alongside the diff; both ride on EditProposed so the host shows them as detail
        // without decoding the provider shape (the diff text itself stays for the native viewer).
        [Fact]
        public void ToolCallWithDiff_CapturesIntentAndOperation()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t3",
                  "kind": "edit",
                  "rawInput": { "__tool_use_purpose": "Fix the assertion", "command": "strReplace", "path": "a.cs" },
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "Assert.NotNull(x);", "newText": "Assert.Null(x);" } ]
                }
                """);

            Assert.Equal("Fix the assertion", edit.Intent);
            Assert.Equal("strReplace", edit.Operation);
        }

        // Regression (double edit card): the same edit reaches us with two path spellings — Kiro v3's
        // str_replace hunk carries the rawInput path with an UPPERCASE drive (C:\…), while its finalized
        // full-file diff carries a file:///c%3A/… URI that decodes to a LOWERCASE drive (c:\…). The edit
        // dedup key is toolCallId|path (case-sensitive), so both must normalize to the SAME path or the hunk
        // and the full-file diff split into two rows. The drive letter is canonicalized to uppercase for both.
        [Fact]
        public void EditPath_FileUriAndRawInput_ShareCanonicalUppercaseDrive()
        {
            var fromUri = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t9",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "file:///c%3A/ws/Foo.cs", "oldText": "a", "newText": "b" } ]
                }
                """);

            var fromRawInput = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t9",
                  "kind": "edit",
                  "rawInput": { "path": "C:\\ws\\Foo.cs", "oldStr": "a", "newStr": "b" }
                }
                """);

            Assert.Equal(@"C:\ws\Foo.cs", fromUri.Path);
            Assert.Equal(@"C:\ws\Foo.cs", fromRawInput.Path);
            // Same toolCallId + identical canonical path => same dedup key => one card, not two.
            Assert.Equal(fromUri.Path, fromRawInput.Path);
        }

        // Regression (double edit card, the field report): the same v3 file_write reaches us with a
        // RELATIVE POSIX path on one surface — whatever the model typed into rawInput — and the absolute
        // file:// URI on the other. Both must root against the ACP session's cwd and canonicalize onto one
        // string, or toolCallId|path yields two keys and the write shows as two cards: the first copying a
        // relative unix path, the second an absolute Windows one. The model's spelling is its own choice
        // and can change mid-conversation, which is why this presents as "fine, then every edit doubles".
        [Fact]
        public void EditPath_RelativeRawInputAndAbsoluteUri_ShareOneCanonicalPath()
        {
            const string root = @"C:\ws\dotnet";

            var fromRelative = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "w1",
                  "kind": "edit",
                  "rawInput": { "path": "proj/Foo.cs", "oldStr": "a", "newStr": "b" }
                }
                """,
                agentRoot: root);

            var fromUri = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "w1",
                  "status": "completed",
                  "content": [ { "type": "diff", "path": "file:///c%3A/ws/dotnet/proj/Foo.cs", "oldText": "a", "newText": "b" } ]
                }
                """,
                agentRoot: root);

            Assert.Equal(@"C:\ws\dotnet\proj\Foo.cs", fromRelative.Path);
            Assert.Equal(@"C:\ws\dotnet\proj\Foo.cs", fromUri.Path);
            // Same toolCallId + identical canonical path => one dedup key => one card, not two.
            Assert.Equal(fromRelative.Path, fromUri.Path);
        }

        // The agent's relative path is measured from the ACP session's cwd, which is NOT necessarily the
        // solution root (issue #54). Rooting it anywhere else would name a different file, so with no root
        // supplied it is left relative rather than resolved against the process working directory — that
        // is devenv's install dir, and GetFullPath would silently invent a path under it.
        [Fact]
        public void EditPath_RelativeWithNoRoot_IsLeftRelative()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "w2",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "proj/Foo.cs", "oldText": "a", "newText": "b" } ]
                }
                """);

            // Separators unify (so two relative spellings still meet) but nothing is invented in front.
            Assert.Equal(@"proj\Foo.cs", edit.Path);
        }

        // The write capture is keyed on the applier's resolved path, and the applier resolves a relative
        // agent path with Path.Combine — which keeps the agent's POSIX separators ("C:\ws\dotnet/proj/
        // Foo.cs"). Without canonicalizing both sides that key never meets the finalized diff's, and the
        // authoritative before/after is silently dropped in favour of the agent's claim.
        [Fact]
        public void ClientFsWrite_Capture_MatchesAcrossMixedSeparators()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "w3",
                  "status": "completed",
                  "content": [ { "type": "diff", "path": "file:///c%3A/ws/dotnet/proj/Foo.cs", "oldText": "stale", "newText": "agent-claim" } ]
                }
                """,
                writes: Captured(@"C:\ws\dotnet/proj/Foo.cs", "real-before", "real-after", @"C:\ws\dotnet"),
                agentRoot: @"C:\ws\dotnet");

            Assert.Equal("real-before", edit.OldText);
            Assert.Equal("real-after", edit.NewText);
        }

        // The read cache has the same two-spellings problem: a read reporting a relative path and an edit
        // reporting the absolute one must key alike, or a degenerate whole-file diff can't recover its
        // real "before" and renders as a create.
        [Fact]
        public void ReadCache_RelativeReadPath_MatchesAbsoluteEditPath()
        {
            const string root = @"C:\ws\dotnet";
            var cache = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

            Map(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "r1",
                  "kind": "read",
                  "status": "completed",
                  "rawInput": { "operations": [ { "path": "proj/Foo.cs" } ] },
                  "rawOutput": { "items": [ { "Text": "the real before" } ] }
                }
                """,
                cache: cache, agentRoot: root);

            // A degenerate "create" diff (oldText == newText) for the same file, named absolutely.
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "r2",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "C:\\ws\\dotnet\\proj\\Foo.cs", "oldText": "rewritten", "newText": "rewritten" } ]
                }
                """,
                cache: cache, agentRoot: root);

            Assert.Equal("the real before", edit.OldText);
            Assert.Equal("rewritten", edit.NewText);
        }

        // Kiro v3's str_replace ("Replace in File") tool_call START: the spec content diff is absent, and
        // the before/after live in _meta.kiro.preview. It must still become an edit card (with a native
        // diff) and NOT a bare tool row — the v2 strReplace behaviour. Regression for the "v3 edits don't
        // open a VS diff window" report.
        [Fact]
        public void KiroV3StrReplace_Start_FromPreview_EmitsEditAndNoToolRow()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "tu1",
                  "title": "Replace in File",
                  "kind": "edit",
                  "rawInput": { "path": "c:\\ws\\a.csproj", "oldStr": "<A/>", "newStr": "<A/><B/>" },
                  "locations": [ { "path": "c:\\ws\\a.csproj" } ],
                  "_meta": { "kiro": { "preview": { "file": "c:\\ws\\a.csproj", "originalContent": "<A/>", "modifiedContent": "<A/><B/>" } } }
                }
                """);

            var edit = Assert.Single(events.OfType<AgentEvent.EditProposed>());
            Assert.Equal("C:\\ws\\a.csproj", edit.Path); // drive letter canonicalized to uppercase
            Assert.Equal("<A/>", edit.OldText);
            Assert.Equal("<A/><B/>", edit.NewText);
            Assert.Equal("tu1", edit.ToolCallId);
            Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());
        }

        // Kiro v3 str_replace pending UPDATE: preview is blanked and the content diff entry is present but
        // empty (blank path + text), so the fallback reads rawInput.oldStr/newStr. Same toolCallId as the
        // start so the two dedupe onto one edit card.
        [Fact]
        public void KiroV3StrReplace_Update_FromRawInput_EmitsEdit()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tu1",
                  "status": "pending",
                  "rawInput": { "path": "c:\\ws\\a.csproj", "oldStr": "<A/>", "newStr": "<A/><B/>", "replace_all": false },
                  "content": [ { "type": "diff", "path": "", "oldText": "", "newText": "" } ],
                  "_meta": { "kiro": { "preview": { "originalContent": "", "modifiedContent": "" } } }
                }
                """);

            Assert.Equal("C:\\ws\\a.csproj", edit.Path); // drive letter canonicalized to uppercase
            Assert.Equal("<A/>", edit.OldText);
            Assert.Equal("<A/><B/>", edit.NewText);
            Assert.Equal("tu1", edit.ToolCallId);
        }

        // --- Write capture: an edit WE applied through the client fs carries our before/after, not the
        // agent's report. See AcpMapper.ResolveEditText / Core FileWriteResult.

        // The str_replace shape is the one that actually loses fidelity without this: the agent reports
        // only the changed hunk, so the host would have to locate it in the file and splice the old text
        // back to show any surrounding context. Our capture is the whole file on both sides, exactly as
        // written, so the reconstruction is never needed.
        [Fact]
        public void ClientFsWrite_Capture_ReplacesReportedHunk()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tu1",
                  "status": "completed",
                  "rawInput": { "path": "c:\\ws\\a.cs", "oldStr": "int x = 1;", "newStr": "int x = 2;" },
                  "content": [ { "type": "diff", "path": "", "oldText": "", "newText": "" } ]
                }
                """,
                writes: Captured("C:\\ws\\a.cs", "class A { int x = 1; }", "class A { int x = 2; }"));

            Assert.Equal("C:\\ws\\a.cs", edit.Path);
            Assert.Equal("class A { int x = 1; }", edit.OldText);
            Assert.Equal("class A { int x = 2; }", edit.NewText);
            Assert.Equal("tu1", edit.ToolCallId); // still the agent's row identity
        }

        // Kiro v3's whole-file write reports its diff path as a percent-encoded file:// URI while the
        // applier resolves a plain local path — they must normalize onto the same key or the capture
        // silently never applies.
        [Fact]
        public void ClientFsWrite_Capture_MatchesAcrossFileUriAndLocalPath()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tu2",
                  "status": "completed",
                  "content": [ { "type": "diff", "path": "file:///c%3A/ws/a.cs", "oldText": "stale", "newText": "agent-claim" } ]
                }
                """,
                writes: Captured("C:\\ws\\a.cs", "real-before", "real-after"));

            Assert.Equal("real-before", edit.OldText);
            Assert.Equal("real-after", edit.NewText);
        }

        // Consumed on use: a second edit of the same file in the same session must not re-apply the
        // first write's capture, or it would show a transformation that already happened.
        [Fact]
        public void ClientFsWrite_Capture_ConsumedOnce()
        {
            const string frame =
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tu3",
                  "status": "completed",
                  "content": [ { "type": "diff", "path": "c:\\ws\\a.cs", "oldText": "agent-before", "newText": "agent-after" } ]
                }
                """;
            var writes = Captured("C:\\ws\\a.cs", "real-before", "real-after");

            var first = Map(frame, writes: writes).OfType<AgentEvent.EditProposed>().Single();
            var second = Map(frame, writes: writes).OfType<AgentEvent.EditProposed>().Single();

            Assert.Equal("real-before", first.OldText);
            Assert.Equal("agent-before", second.OldText); // falls back to the agent's report
            Assert.Equal("agent-after", second.NewText);
        }

        // No capture (an agent that writes files itself — Kiro v2, Claude) is the untouched path.
        [Fact]
        public void ClientFsWrite_NoCapture_KeepsAgentReportedText()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tu4",
                  "status": "completed",
                  "content": [ { "type": "diff", "path": "c:\\ws\\a.cs", "oldText": "before", "newText": "after" } ]
                }
                """,
                writes: Captured("C:\\ws\\OTHER.cs", "real-before", "real-after"));

            Assert.Equal("before", edit.OldText);
            Assert.Equal("after", edit.NewText);
        }

        // The divergence log exists to answer "is the capture ever needed?", and this comparison is what
        // makes its silence meaningful. VsEditApplier runs LineEndings.Match, so our capture carries the
        // file's real endings while agents report "\n" — compared raw, EVERY write would read as divergent
        // and the log would say nothing at all.
        [Fact]
        public void CaptureComparison_FoldsLineEndings_SoCrlfVsLfIsNotDivergence()
        {
            Assert.True(AcpMapper.SameIgnoringLineEndings("a\r\nb\r\nc", "a\nb\nc"));
            Assert.True(AcpMapper.SameIgnoringLineEndings(null, ""));
            Assert.False(AcpMapper.SameIgnoringLineEndings("a\nb", "a\nB")); // real difference still caught
            Assert.False(AcpMapper.SameIgnoringLineEndings("a\nb", "a\nb\nc"));
        }

        // Claude's edit rawInput carries neither a purpose nor a command discriminator (just file_path/
        // old_string/new_string); Intent and Operation stay null rather than surfacing noise.
        [Fact]
        public void ToolCallWithDiff_NoMeta_LeavesIntentAndOperationNull()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t4",
                  "kind": "edit",
                  "rawInput": { "file_path": "a.cs", "old_string": "a", "new_string": "b" },
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "a", "newText": "b" } ]
                }
                """);

            Assert.Null(edit.Intent);
            Assert.Null(edit.Operation);
        }

        // The reported edit location's line (ACP locations[].line — Claude's finalized structuredPatch
        // newStart) rides along on the EditProposed so the host can sanity-check where it splices a
        // hunk-only diff back into the full file.
        [Fact]
        public void ToolCallWithDiff_CapturesReportedLocationLine()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t2",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "int x = 1;", "newText": "int x = 2;" } ],
                  "locations": [ { "path": "a.cs", "line": 42 } ]
                }
                """);

            Assert.Equal(42, edit.Line);
        }

        // Multiple hunks to the same file: locations are consumed positionally so each diff gets its own
        // reported line.
        [Fact]
        public void ToolCallWithDiff_MultipleHunks_MatchesLinesPositionally()
        {
            var edits = Map(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t2",
                  "kind": "edit",
                  "content": [
                    { "type": "diff", "path": "a.cs", "oldText": "a", "newText": "A" },
                    { "type": "diff", "path": "a.cs", "oldText": "b", "newText": "B" }
                  ],
                  "locations": [ { "path": "a.cs", "line": 10 }, { "path": "a.cs", "line": 20 } ]
                }
                """).OfType<AgentEvent.EditProposed>().ToList();

            Assert.Equal(2, edits.Count);
            Assert.Equal(10, edits[0].Line);
            Assert.Equal(20, edits[1].Line);
        }

        // A location without a numeric line (the adapter's provisional first send) leaves Line null.
        [Fact]
        public void ToolCallWithDiff_LocationWithoutLine_LeavesLineNull()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t2",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "x", "newText": "y" } ],
                  "locations": [ { "path": "a.cs" } ]
                }
                """);

            Assert.Null(edit.Line);
        }

        // Kiro's whole-file create emits a degenerate diff (oldText == newText). With no cache the before
        // falls back to empty (all-added) rather than a blank no-change diff.
        [Fact]
        public void DegenerateDiff_NoCache_FallsBackToEmptyBefore()
        {
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t3",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "same", "newText": "same" } ]
                }
                """);

            Assert.Equal("", edit.OldText);
            Assert.Equal("same", edit.NewText);
        }

        // With a session read-cache holding the file's prior content, a degenerate diff is repaired to a
        // real before, and the cache is refreshed to the new content for the next edit.
        [Fact]
        public void DegenerateDiff_WithCachedContent_RepairsBeforeAndRefreshesCache()
        {
            var cache = new Dictionary<string, string> { ["a.cs"] = "old body" };
            var edit = Single<AgentEvent.EditProposed>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "t4",
                  "kind": "edit",
                  "content": [ { "type": "diff", "path": "a.cs", "oldText": "new body", "newText": "new body" } ]
                }
                """, cache);

            Assert.Equal("old body", edit.OldText);
            Assert.Equal("new body", edit.NewText);
            Assert.Equal("new body", cache["a.cs"]); // cache advanced for the next edit's "before"
        }

        // Kiro's task-list tool_call is rendered as a live plan card, so its generic started row is
        // suppressed (recognised by the tasks/completed_task_ids/task_list_description input shape).
        [Fact]
        public void KiroPlanToolCall_IsSuppressed()
        {
            Assert.Empty(Map(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "p1",
                  "title": "Task list",
                  "rawInput": { "tasks": [ { "task_description": "do a thing" } ] }
                }
                """));
        }

        // Kiro's task-list update carries the full list in rawOutput.items[].Json.tasks -> a PlanUpdated
        // with per-item completed status and the plan description as the title.
        [Fact]
        public void KiroPlanUpdate_BecomesPlanUpdatedWithStatuses()
        {
            var plan = Single<AgentEvent.PlanUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "p2",
                  "status": "completed",
                  "rawOutput": { "items": [ { "Json": {
                    "description": "Ship it",
                    "tasks": [
                      { "id": "1", "task_description": "write code", "completed": true },
                      { "id": "2", "task_description": "test it", "completed": false }
                    ]
                  } } ] }
                }
                """);

            Assert.Equal("Ship it", plan.Title);
            Assert.Collection(plan.Items,
                i => { Assert.Equal("write code", i.Description); Assert.Equal(PlanItemStatus.Completed, i.Status); },
                i => { Assert.Equal("test it", i.Description); Assert.Equal(PlanItemStatus.Pending, i.Status); });
        }

        // A plan update stands in for the tool completion row, so no ToolCallCompleted is also emitted.
        [Fact]
        public void KiroPlanUpdate_SuppressesCompletionRow()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "p3",
                  "status": "completed",
                  "rawOutput": { "items": [ { "Json": { "tasks": [ { "task_description": "x" } ] } } ] }
                }
                """);

            Assert.Empty(events.OfType<AgentEvent.ToolCallCompleted>());
        }

        /// <summary>
        /// Whether a tool call has a generic row is decided ONCE, on its opening frame (issue #190).
        /// <para>
        /// It used to be re-decided per frame, from that frame's own <c>rawInput</c>, and the two
        /// readings need not agree: an adapter that opens a call with a placeholder title and EMPTY
        /// <c>rawInput</c> (the shape <c>AcpMapper</c> already documents, and the reason
        /// <c>ToolCallUpdated</c> exists) starts an ordinary row, and the update that finally carries
        /// <c>tasks</c> then retires it into the plan card and suppresses its completion. A start with
        /// no close is a call the host believes is still running for the rest of the turn — which is
        /// what strands the tray's "next step" release.
        /// </para>
        /// </summary>
        [Fact]
        public void PlanToolWhoseArgumentsArriveLate_StillClosesTheRowItOpened()
        {
            var events = MapSession(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "p6",
                  "title": "Tool",
                  "status": "pending"
                }
                """,
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "p6",
                  "status": "completed",
                  "rawInput": { "tasks": [ { "task_description": "x" } ] },
                  "rawOutput": { "items": [ { "Json": { "tasks": [ { "task_description": "x" } ] } } ] }
                }
                """);

            // The row was opened...
            Assert.Equal("p6", Assert.Single(events.OfType<AgentEvent.ToolCallStarted>()).ToolCallId);
            // ...the plan card still stands in for its content...
            Assert.Single(events.OfType<AgentEvent.PlanUpdated>());
            // ...and the pair closes. Closed, not enriched: the task list is the card's, not the row's.
            var done = Assert.Single(events.OfType<AgentEvent.ToolCallCompleted>());
            Assert.Equal("p6", done.ToolCallId);
            Assert.True(done.Success);
            Assert.Empty(events.OfType<AgentEvent.ToolCallUpdated>());
        }

        /// <summary>
        /// The other direction, and the one a fix could quietly break: a plan tool recognised on its
        /// OPENING frame has no row to close, so its completion stays suppressed exactly as before. A
        /// check that only proved the completion now arrives would pass on a mapper that had simply
        /// stopped retiring plan calls at all.
        /// </summary>
        [Fact]
        public void PlanToolRecognisedOnItsOpeningFrame_StillHasNoRowAtAll()
        {
            var events = MapSession(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "p7",
                  "title": "Task list",
                  "rawInput": { "tasks": [ { "task_description": "x" } ] }
                }
                """,
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "p7",
                  "status": "completed",
                  "rawInput": { "tasks": [ { "task_description": "x" } ] },
                  "rawOutput": { "items": [ { "Json": { "tasks": [ { "task_description": "x" } ] } } ] }
                }
                """);

            Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());
            Assert.Empty(events.OfType<AgentEvent.ToolCallCompleted>());
            Assert.Single(events.OfType<AgentEvent.PlanUpdated>());
        }

        // The final "complete" update: Kiro disposes the task list when its last item completes, so
        // the frame carries tasks:[] (real captured shape, issue #17). That must surface as an EMPTY
        // PlanUpdated ("plan finished — tick everything off"), not be dropped as a generic plan-tool
        // frame, or the card never shows the last item done.
        [Fact]
        public void KiroPlanFinalComplete_ClearedListBecomesEmptyPlanUpdated()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "p4",
                  "kind": "other",
                  "status": "completed",
                  "title": "Completing #3",
                  "rawInput": { "command": "complete", "completed_task_ids": ["3"], "context_update": "All 3 tasks completed" },
                  "rawOutput": { "items": [ { "Json": { "tasks": [], "description": "", "context": [], "modified_files": [] } } ] }
                }
                """);

            var plan = Assert.Single(events.OfType<AgentEvent.PlanUpdated>());
            Assert.Null(plan.Title);
            Assert.Empty(plan.Items);
            Assert.Empty(events.OfType<AgentEvent.ToolCallCompleted>()); // still no generic row
        }

        // A mid-plan "complete" whose rawOutput hasn't arrived yet (or a non-complete plan command)
        // stays suppressed — only the cleared-list completion synthesizes the empty PlanUpdated.
        [Fact]
        public void KiroPlanCompleteWithoutClearedList_StaysSuppressed()
        {
            Assert.Empty(Map(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "p5",
                  "status": "in_progress",
                  "rawInput": { "command": "complete", "completed_task_ids": ["1"] }
                }
                """));
        }

        // Kiro's v3 agent engine drives the SAME task-list tool but puts the payload straight on
        // rawOutput, with no items[].Json wrapper (captured 2026-08-10, kiro-cli 2.16.2
        // --agent-engine v3, issue #119). Unrecognised, every v3 turn ran with no plan card at all.
        [Fact]
        public void KiroV3PlanUpdate_PayloadOnRawOutput_BecomesPlanUpdated()
        {
            var plan = Single<AgentEvent.PlanUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tooluse_05NbUOuPjeQzSsB47QiqK3",
                  "status": "completed",
                  "title": "Task List",
                  "rawInput": { "command": "complete", "completed_task_ids": { "0": "1" } },
                  "rawOutput": {
                    "description": "Create notes.txt, append to it, and read it back",
                    "tasks": [
                      { "id": "1", "task_description": "Create notes.txt containing the single line: one", "completed": true },
                      { "id": "2", "task_description": "Append a second line to notes.txt: two", "completed": false }
                    ],
                    "context": [],
                    "modified_files": []
                  }
                }
                """);

            Assert.Equal("Create notes.txt, append to it, and read it back", plan.Title);
            Assert.Collection(plan.Items,
                i => Assert.Equal(PlanItemStatus.Completed, i.Status),
                i => Assert.Equal(PlanItemStatus.Pending, i.Status));
        }

        // v3's last "complete" clears the list exactly as the default engine does — one nesting level
        // shallower. Same meaning, so it must reach the card the same way: everything ticked off.
        [Fact]
        public void KiroV3PlanFinalComplete_ClearedListBecomesEmptyPlanUpdated()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "tooluse_RC44BqjqQpSMt0BPRbiU19",
                  "status": "completed",
                  "title": "Task List",
                  "rawInput": { "command": "complete", "completed_task_ids": { "0": "3" } },
                  "rawOutput": { "tasks": [], "description": "", "context": [], "modified_files": [] }
                }
                """);

            var plan = Assert.Single(events.OfType<AgentEvent.PlanUpdated>());
            Assert.Empty(plan.Items);
            Assert.Empty(events.OfType<AgentEvent.ToolCallCompleted>());
        }

        // v3 sends rawInput.tasks as an index-keyed OBJECT, not an array, on the "create" call. The
        // tool is still the task list, so its row must stay suppressed in favour of the plan card.
        [Fact]
        public void KiroV3PlanCreate_ObjectKeyedTasks_SuppressesToolRow()
        {
            Assert.Empty(Map(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "tooluse_FG0FwXzSNHb09kCF6rkKjA",
                  "title": "Task List",
                  "kind": "other",
                  "status": "in_progress",
                  "rawInput": {
                    "command": "create",
                    "tasks": { "0": { "task_description": "Create notes.txt" } }
                  }
                }
                """).OfType<AgentEvent.ToolCallStarted>());
        }

        // The spec-standard "plan" channel: { entries:[{ content, status }] }, mapped index-keyed.
        [Fact]
        public void AcpPlanChannel_BecomesPlanUpdated()
        {
            var plan = Single<AgentEvent.PlanUpdated>(
                """
                {
                  "sessionUpdate": "plan",
                  "entries": [
                    { "content": "step one", "status": "completed" },
                    { "content": "step two", "status": "in_progress" },
                    { "content": "step three", "status": "pending" }
                  ]
                }
                """);

            Assert.Null(plan.Title);
            Assert.Equal(3, plan.Items.Count);
            Assert.Equal(PlanItemStatus.Completed, plan.Items[0].Status);
            Assert.Equal(PlanItemStatus.InProgress, plan.Items[1].Status);
            Assert.Equal(PlanItemStatus.Pending, plan.Items[2].Status);
            Assert.Equal("0", plan.Items[0].Id); // entries carry no id -> index used
        }

        // The Claude adapter opens a call with a placeholder title/empty input, then enriches it on an
        // update; that enrichment surfaces as ToolCallUpdated so the host can fill the existing row.
        [Fact]
        public void ToolCallUpdate_EnrichmentSurfacesTitleKindAndInput()
        {
            var updated = Single<AgentEvent.ToolCallUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "t5",
                  "title": "Read config.json",
                  "kind": "read",
                  "rawInput": { "file_path": "config.json" }
                }
                """);

            Assert.Equal("t5", updated.ToolCallId);
            Assert.Equal("Read config.json", updated.Title);
            Assert.Equal("read", updated.Kind);
            Assert.Contains("config.json", updated.RawInputJson);
        }

        // An in-flight (non-terminal) status is progress, not completion.
        [Fact]
        public void ToolCallUpdate_InProgressStatus_BecomesProgress()
        {
            var events = Map(
                """
                { "sessionUpdate": "tool_call_update", "toolCallId": "t6", "status": "in_progress" }
                """);

            var progress = Assert.Single(events.OfType<AgentEvent.ToolCallProgress>());
            Assert.Equal("in_progress", progress.Message);
            Assert.Empty(events.OfType<AgentEvent.ToolCallCompleted>());
        }
        // ------------------------------------------------------------------ tool kind resolution
        // ACP's "other" is a catch-all, and claude-agent-acp 0.70.0 puts its Windows shell tool there:
        // titled "PowerShell", kind "other", the command in rawInput. Everything keying on the kind
        // stopped seeing the agent's own commands — the terminal mirror most visibly, by going silent.
        // Frames below are the captured ones (acp.log, 2026-08-23), trimmed.

        [Fact]
        public void PowerShellCall_OtherKindCarryingCommand_ResolvesToExecute()
        {
            var ev = Single<AgentEvent.ToolCallUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "_meta": { "claudeCode": { "toolName": "PowerShell" } },
                  "toolCallId": "toolu_01KJCzN218FPKYaxsxKtnEaT",
                  "title": "PowerShell",
                  "kind": "other",
                  "rawInput": {
                    "command": "Get-ChildItem -Force | Select-Object Mode, LastWriteTime, Length, Name",
                    "description": "List top-level directory contents"
                  }
                }
                """);

            Assert.Equal("execute", ev.Kind);
        }

        [Fact]
        public void PowerShellCall_OpeningFrame_ResolvesOnTheStartedEventToo()
        {
            var ev = Single<AgentEvent.ToolCallStarted>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "toolu_1",
                  "title": "PowerShell",
                  "kind": "other",
                  "status": "pending",
                  "rawInput": { "command": "dotnet --info" }
                }
                """);

            Assert.Equal("execute", ev.Kind);
        }

        // The real opening frame carries kind "other" and an EMPTY rawInput - the command only arrives on
        // the enriching update. With nothing to go on the catch-all must stand: guessing here would make
        // every unclassified call a command.
        [Fact]
        public void OtherKind_WithNoCommand_StaysOther()
        {
            var ev = Single<AgentEvent.ToolCallStarted>(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "toolu_1",
                  "title": "PowerShell",
                  "kind": "other",
                  "status": "pending",
                  "rawInput": {}
                }
                """);

            Assert.Equal("other", ev.Kind);
        }

        // An MCP call's rawInput is a THIRD PARTY's schema: a Jira tool takes a "command" that is not a
        // command line (#129/#131). The namespaced name is what says so, and it must veto the promotion.
        [Fact]
        public void McpCall_OtherKindWithACommandArgument_IsNeverPromoted()
        {
            var ev = Single<AgentEvent.ToolCallUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "call_1",
                  "title": "mcp__jira__create_issue",
                  "kind": "other",
                  "rawInput": { "command": "deploy the release", "summary": "Ship it" }
                }
                """);

            Assert.Equal("other", ev.Kind);
            Assert.Equal("mcp__jira__create_issue", ev.ToolName);
        }

        // Kiro puts its fs-op discriminator in an EDIT's rawInput.command. Leaving every backend-chosen
        // kind alone is what keeps "strReplace" from being read as a shell command - the same trap
        // IsFileOpKind guards on the permission path.
        [Fact]
        public void EditKind_CarryingKirosStrReplaceDiscriminator_IsNeverPromoted()
        {
            var ev = Single<AgentEvent.ToolCallUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "kiro_1",
                  "title": "Edit File",
                  "kind": "edit",
                  "rawInput": { "command": "strReplace", "path": "C:\\ws\\Program.cs" }
                }
                """);

            Assert.Equal("edit", ev.Kind);
        }

        // A kind the backend actually chose is never rewritten - including the one this exists to produce.
        [Fact]
        public void ExecuteKind_PassesThroughUnchanged()
        {
            var ev = Single<AgentEvent.ToolCallUpdated>(
                """
                {
                  "sessionUpdate": "tool_call_update",
                  "toolCallId": "toolu_2",
                  "title": "dotnet build",
                  "kind": "execute",
                  "rawInput": { "command": "dotnet build" }
                }
                """);

            Assert.Equal("execute", ev.Kind);
        }

        // --- Kiro v3's Write File: which SHAPE a write becomes, and what decides it (#178) ----------
        //
        // Both frames below are verbatim from a live capture (2026-09-03, kiro-cli --agent-engine v3),
        // and the pair is here because reading the code got this wrong twice in one session. v3 sends NO
        // `content` array on a write's opening frame at all — the diff is in `_meta.kiro.preview` — so
        // the fork is decided entirely by whether that preview is usable, and the two branches produce
        // visibly different transcripts. Pinned as a PAIR: either one alone is satisfied by a mapper
        // that always takes that branch.

        /// <summary>
        /// The ordinary case: a populated preview yields the edit on the OPENING frame, so no tool row
        /// is ever built and the write renders as a first-class edit card that names its own file.
        /// </summary>
        [Fact]
        public void KiroV3_WriteWithPreview_YieldsAnEditAndNoToolRow()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call", "toolCallId": "tooluse_5dMADk9VmxMADYIKK4lYJ9",
                  "title": "Write File", "kind": "edit", "status": "in_progress",
                  "rawInput": { "path": "c:\\ws\\temp.txt", "text": "hello" },
                  "locations": [ { "path": "c:\\ws\\temp.txt" } ],
                  "_meta": { "kiro": { "toolOrigin": "default",
                                       "preview": { "file": "c:\\ws\\temp.txt", "modifiedContent": "hello" } } }
                }
                """);

            var edit = Assert.Single(events.OfType<AgentEvent.EditProposed>());
            Assert.Equal("hello", edit.NewText);
            Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());
        }

        /// <summary>
        /// And the shape issue #178 was actually looking at. With nothing usable in the preview — an
        /// EMPTY write leaves a <c>file</c> and no <c>modifiedContent</c>, and a CLI that sends no
        /// preview at all lands here too — there is no edit to yield, so the call opens as a generic
        /// tool row wearing v3's bare "Write File". The diff folds into it later, which is why that row
        /// could copy and open a path it declined to display.
        /// </summary>
        [Fact]
        public void KiroV3_WriteWithoutUsablePreview_OpensAsAGenericToolRow()
        {
            var events = Map(
                """
                {
                  "sessionUpdate": "tool_call", "toolCallId": "tooluse_5dMADk9VmxMADYIKK4lYJ9",
                  "title": "Write File", "kind": "edit", "status": "in_progress",
                  "rawInput": { "path": "c:\\ws\\temp.txt", "text": "" },
                  "locations": [ { "path": "c:\\ws\\temp.txt" } ],
                  "_meta": { "kiro": { "toolOrigin": "default",
                                       "preview": { "file": "c:\\ws\\temp.txt" } } }
                }
                """);

            var started = Assert.Single(events.OfType<AgentEvent.ToolCallStarted>());
            Assert.Equal("Write File", started.Title);
            Assert.Equal("edit", started.Kind);
            Assert.Empty(events.OfType<AgentEvent.EditProposed>());
        }
    }
}
