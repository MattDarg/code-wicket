using System;
using System.Linq;
using System.Text.Json;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// How <see cref="AcpMapper.ToPermissionRequest"/> resolves the clean command a permission request
    /// runs. The command feeds the allow-list / remembered-glob matcher, so getting it from the reliable
    /// structured field (rawInput.command) rather than the free-form title matters. Frames mirror real
    /// captures: Claude Code sends kind + rawInput; Kiro sends neither and titles commands "Running: ...".
    /// </summary>
    public sealed class AcpMapperCommandTests
    {
        private static Core.PermissionRequest Map(string toolCallJson)
        {
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(toolCallJson).RootElement.Clone(),
                Options = new[]
                {
                    new PermissionOptionDto("allow", "Allow", "allow_once"),
                    new PermissionOptionDto("reject", "Reject", "reject_once"),
                },
            };
            return AcpMapper.ToPermissionRequest(p);
        }

        // A real Claude Bash frame: the structured command is the source of truth.
        [Fact]
        public void Claude_ExecuteFrame_TakesCommandFromRawInput()
        {
            var request = Map(
                """
                {
                  "toolCallId": "toolu_1",
                  "title": "dotnet build",
                  "kind": "execute",
                  "rawInput": { "command": "dotnet build", "description": "Build the .NET solution via terminal" }
                }
                """);

            Assert.Equal("dotnet build", request.Command);
        }

        // When the agent titles the call with prose, we must still key on rawInput.command, not the title.
        [Fact]
        public void Claude_ProseTitle_StillTakesCommandFromRawInput()
        {
            var request = Map(
                """
                {
                  "toolCallId": "toolu_2",
                  "title": "Build the solution",
                  "kind": "execute",
                  "rawInput": { "command": "dotnet build" }
                }
                """);

            Assert.Equal("dotnet build", request.Command);
        }

        // Claude labels a PowerShell run kind "other" (not "execute") but still puts the command in
        // rawInput.command — we must key on that, not the kind, or the banner's editable glob never shows.
        [Fact]
        public void Claude_PowerShell_OtherKind_StillTakesCommandFromRawInput()
        {
            var request = Map(
                """
                {
                  "toolCallId": "toolu_ps",
                  "title": "PowerShell",
                  "kind": "other",
                  "rawInput": { "command": "dotnet build BlazorApp1.sln", "description": "Build the solution via dotnet CLI" }
                }
                """);

            Assert.Equal("dotnet build BlazorApp1.sln", request.Command);
        }

        // Kiro sends no rawInput and prefixes command titles with "Running: "; strip it for the command.
        [Fact]
        public void Kiro_NoRawInput_FallsBackToTitle()
        {
            var request = Map(
                """
                {
                  "toolCallId": "c1",
                  "title": "Running: git status",
                  "kind": "execute"
                }
                """);

            Assert.Equal("git status", request.Command);
        }

        // Command kind but no usable rawInput.command (empty object) also falls back to the title.
        [Fact]
        public void ExecuteKind_EmptyRawInput_FallsBackToTitle()
        {
            var request = Map(
                """
                {
                  "toolCallId": "c2",
                  "title": "Running: ls -la",
                  "kind": "execute",
                  "rawInput": {}
                }
                """);

            Assert.Equal("ls -la", request.Command);
        }

        // An argument-less tool (build_solution) sends empty rawInput; it must not surface as a "{}"/"[]"
        // permission-banner subtitle, and carries no command.
        [Theory]
        [InlineData("{}")]
        [InlineData("[]")]
        public void EmptyRawInput_YieldsNoDetailOrCommand(string rawInput)
        {
            var request = Map(
                $$"""
                { "toolCallId": "b1", "title": "mcp__code-wicket__build_solution", "kind": "other", "rawInput": {{rawInput}} }
                """);

            Assert.Null(request.Detail);
            Assert.Null(request.Command);
        }

        // Real Kiro: the request_permission frame has NO kind at all (and no rawInput), only a "Running:"
        // title. The title prefix alone must mark it a command, or the banner never shows the command.
        [Fact]
        public void Kiro_NoKind_RunningTitle_StillResolvesCommand()
        {
            var request = Map(
                """
                {
                  "toolCallId": "c3",
                  "title": "Running: npm test"
                }
                """);

            Assert.Equal("npm test", request.Command);
        }

        // Kiro truncates a long command in the title ("…"), but the full command arrived earlier on the
        // tool_call frame and was cached by toolCallId. The permission request must recover the FULL
        // command from the cache, not the truncated title, so the banner shows the whole script.
        [Fact]
        public void CachedFullCommand_PreferredOverTruncatedTitle()
        {
            const string full = "$path = 'C:\\x.cs'\n$c = [IO.File]::ReadAllText($path)\n[IO.File]::WriteAllText($path, $c)";
            var cache = new System.Collections.Generic.Dictionary<string, string> { ["c4"] = full };

            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(
                    """
                    { "toolCallId": "c4", "title": "Running: $path = 'C:\\x.cs' …" }
                    """).RootElement.Clone(),
                Options = new[] { new PermissionOptionDto("allow", "Allow", "allow_once") },
            };

            var request = AcpMapper.ToPermissionRequest(p, cache);

            Assert.Equal(full, request.Command);
        }

        // The cache is populated from a tool_call frame's rawInput.command, keyed by toolCallId.
        [Fact]
        public void CacheToolCommand_RecordsCommandByToolCallId()
        {
            var cache = new System.Collections.Generic.Dictionary<string, string>();
            var update = JsonDocument.Parse(
                """
                { "toolCallId": "c5", "title": "Running: git status", "kind": "execute", "rawInput": { "command": "git status" } }
                """).RootElement.Clone();

            AcpMapper.CacheToolCommand(update, cache);

            Assert.Equal("git status", cache["c5"]);
        }

        // Non-command requests carry no command, so the command allow-list can never match them.
        [Fact]
        public void EditFrame_HasNoCommand()
        {
            var request = Map(
                """
                {
                  "toolCallId": "toolu_3",
                  "title": "Edit Data\\BTestClass.cs",
                  "kind": "edit",
                  "rawInput": { "file_path": "C:\\src\\BTestClass.cs", "old_string": "a", "new_string": "b" }
                }
                """);

            Assert.Null(request.Command);
        }

        // Kiro's `write` tool puts an operation discriminator in rawInput.command ("strReplace"/"create"/
        // "append"/…). It is NOT a shell command: surfacing it as Command routed the edit onto the command
        // allow-list, so an "always" on one edit remembered the glob "strReplace" and silently
        // blanket-approved every string-replace to any file. An edit-kind request must carry no command.
        [Fact]
        public void KiroEdit_StrReplaceDiscriminator_IsNotTreatedAsCommand()
        {
            var request = Map(
                """
                {
                  "toolCallId": "tooluse_x",
                  "title": "Editing ATestClass.cs",
                  "kind": "edit",
                  "rawInput": { "command": "strReplace", "path": "C:\\src\\ATestClass.cs", "oldStr": "a", "newStr": "b" }
                }
                """);

            Assert.Null(request.Command);
        }

        // The same via the command cache: real Kiro (v2) sends no rawInput on the permission frame, so the
        // discriminator would arrive from the preceding tool_call. CacheToolCommand must not record it, and
        // even if a stale entry existed the edit-kind request must ignore it.
        [Fact]
        public void KiroEdit_StrReplace_NotCachedAsCommand()
        {
            var cache = new System.Collections.Generic.Dictionary<string, string>();
            var toolCall = JsonDocument.Parse(
                """
                { "toolCallId": "e9", "title": "Editing ATestClass.cs", "kind": "edit", "rawInput": { "command": "strReplace", "path": "C:\\src\\ATestClass.cs" } }
                """).RootElement.Clone();

            AcpMapper.CacheToolCommand(toolCall, cache);

            Assert.False(cache.ContainsKey("e9"));
        }

        // Even a poisoned cache entry (from any path) can't leak onto an edit request — the edit-kind guard
        // wins, so Path (not a command glob) governs the edit's permission.
        [Fact]
        public void EditRequest_IgnoresCachedCommand()
        {
            var cache = new System.Collections.Generic.Dictionary<string, string> { ["e10"] = "strReplace" };
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(
                    """
                    { "toolCallId": "e10", "title": "Editing ATestClass.cs", "kind": "edit" }
                    """).RootElement.Clone(),
                Options = new[] { new PermissionOptionDto("allow", "Allow", "allow_once") },
            };

            var request = AcpMapper.ToPermissionRequest(p, cache);

            Assert.Null(request.Command);
        }
    }
}
