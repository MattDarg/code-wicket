using System.Linq;
using System.Text.Json;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The parts of <see cref="AcpMapper.ToPermissionRequest"/> that feed the permission banner and
    /// policy beyond the clean command (covered by <see cref="AcpMapperCommandTests"/>): deriving an
    /// action <c>kind</c> for Kiro (whose request carries none) from its <c>_meta.trustOptions</c>
    /// scope hints, mapping ACP option kinds so the UI can style/apply "always", and not surfacing an
    /// argument-less tool's empty raw input as a banner subtitle.
    /// </summary>
    public sealed class AcpMapperPermissionTests
    {
        private static PermissionRequest Map(string toolCallJson, string? metaJson = null, PermissionOptionDto[]? options = null)
        {
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(toolCallJson).RootElement.Clone(),
                Options = options ?? new[]
                {
                    new PermissionOptionDto("allow", "Allow", "allow_once"),
                    new PermissionOptionDto("reject", "Reject", "reject_once"),
                },
                Meta = metaJson is null ? null : JsonDocument.Parse(metaJson).RootElement.Clone(),
            };
            return AcpMapper.ToPermissionRequest(p);
        }

        // Kiro sends no kind; a write scope hint in trustOptions makes this an edit so the AcceptEdits
        // mode and edit "always" policy key on it correctly.
        [Fact]
        public void Kiro_TrustOptionsWrite_DerivesEditKind()
        {
            var request = Map(
                """{ "toolCallId": "k1", "title": "Write file" }""",
                """{ "trustOptions": [ { "setting_key": "runtime_write_paths" } ] }""");

            Assert.Equal("edit", request.Kind);
        }

        [Fact]
        public void Kiro_TrustOptionsCommand_DerivesExecuteKind()
        {
            var request = Map(
                """{ "toolCallId": "k2", "title": "Running: git status" }""",
                """{ "trustOptions": [ { "setting_key": "allowedCommands" } ] }""");

            Assert.Equal("execute", request.Kind);
            // Execute-kind + no rawInput -> command parsed from the "Running: " title.
            Assert.Equal("git status", request.Command);
        }

        [Fact]
        public void Kiro_TrustOptionsRead_DerivesReadKind()
        {
            var request = Map(
                """{ "toolCallId": "k3", "title": "Read file" }""",
                """{ "trustOptions": [ { "setting_key": "fsRead" } ] }""");

            Assert.Equal("read", request.Kind);
            Assert.Null(request.Command); // not a command kind
        }

        // Issue #43: Kiro sends FOUR options, two of kind allow_always ("Always" and "Allow all for this
        // session"). Our model presents one option per kind, so the redundant second allow_always is
        // dropped — otherwise the banner shows two identical "Allow always" buttons.
        [Fact]
        public void DuplicateAllowAlwaysOption_IsDeduplicatedByKind()
        {
            // A realistic full Kiro frame (all four kinds + the duplicate allow_always) — the reject_always
            // also keeps synthesis from adding a session-deny here, so this stays purely about dedup.
            var request = Map(
                """{ "toolCallId": "k5", "title": "Running: build" }""",
                options: new[]
                {
                    new PermissionOptionDto("allow_once", "Yes", "allow_once"),
                    new PermissionOptionDto("allow_always", "Always", "allow_always"),
                    new PermissionOptionDto("reject_once", "No", "reject_once"),
                    new PermissionOptionDto("reject_always", "Never", "reject_always"),
                    new PermissionOptionDto("allow_all_session", "Allow all for this session", "allow_always"),
                });

            Assert.Equal(4, request.Options.Count);
            Assert.Single(request.Options, o => o.Kind == PermissionOptionKind.AllowAlways);
            // The FIRST allow_always is the one kept (its optionId); the "allow_all_session" extra is gone.
            Assert.Equal("allow_always", request.Options.Single(o => o.Kind == PermissionOptionKind.AllowAlways).OptionId);
            Assert.DoesNotContain(request.Options, o => o.OptionId == "allow_all_session");
        }

        // No usable hint (e.g. an MCP tool call) leaves the request unkinded rather than guessing.
        [Fact]
        public void NoKindAndNoMeta_LeavesKindNull()
        {
            var request = Map("""{ "toolCallId": "k4", "title": "mcp__ide__build_solution" }""");

            Assert.Null(request.Kind);
        }

        // An explicit kind on the tool call always wins over the derived one.
        [Fact]
        public void ExplicitKind_WinsOverDerivation()
        {
            var request = Map(
                """{ "toolCallId": "k5", "title": "x", "kind": "execute" }""",
                """{ "trustOptions": [ { "setting_key": "runtime_write_paths" } ] }""");

            Assert.Equal("execute", request.Kind);
        }

        [Theory]
        [InlineData("allow_once", PermissionOptionKind.AllowOnce)]
        [InlineData("allow_always", PermissionOptionKind.AllowAlways)]
        [InlineData("reject_once", PermissionOptionKind.RejectOnce)]
        [InlineData("reject_always", PermissionOptionKind.RejectAlways)]
        [InlineData("something_new", PermissionOptionKind.RejectOnce)] // unknown -> safe default
        public void OptionKind_MapsFromAcpString(string acpKind, PermissionOptionKind expected)
        {
            var request = Map(
                """{ "toolCallId": "o1", "title": "x", "kind": "execute" }""",
                options: new[] { new PermissionOptionDto("id", "Label", acpKind) });

            // Locate by id (not Single): a lone reject_once on a scoped request also gets a synthesized
            // session-deny option, so the set may hold more than the one under test.
            var option = request.Options.Single(o => o.OptionId == "id");
            Assert.Equal(expected, option.Kind);
            Assert.Equal("Label", option.Label);
        }

        // An argument-less tool sends empty raw input; it must not become the banner's "{}" subtitle.
        [Fact]
        public void EmptyRawInput_ProducesNoDetail()
        {
            var request = Map("""{ "toolCallId": "o2", "title": "build_solution", "kind": "other", "rawInput": {} }""");

            Assert.Null(request.Detail);
        }

        // A populated raw input is carried as Detail (the banner subtitle / deny-list scan surface).
        [Fact]
        public void PopulatedRawInput_BecomesDetail()
        {
            var request = Map(
                """{ "toolCallId": "o3", "title": "x", "kind": "edit", "rawInput": { "file_path": "a.cs" } }""");

            Assert.Contains("a.cs", request.Detail);
        }

        // A missing title degrades to a stable label rather than an empty banner heading.
        [Fact]
        public void MissingTitle_FallsBackToDefaultLabel()
        {
            var request = Map("""{ "toolCallId": "o4", "kind": "edit" }""");

            Assert.Equal("Permission required", request.Title);
        }

        // Kiro titles an MCP tool run "Running: @server/tool"; the namespaced name is recovered as ToolName.
        [Fact]
        public void Kiro_McpToolTitle_ExtractsToolName()
        {
            var request = Map("""{ "toolCallId": "m1", "title": "Running: @code-wicket/run_tests" }""");

            Assert.Equal("@code-wicket/run_tests", request.ToolName);
        }

        // Claude uses the mcp__server__tool namespaced name (here as the title) — carried through as ToolName.
        [Fact]
        public void Claude_McpNamespacedTitle_ExtractsToolName()
        {
            var request = Map("""{ "toolCallId": "m2", "title": "mcp__code-wicket__get_diagnostics", "kind": "other" }""");

            Assert.Equal("mcp__code-wicket__get_diagnostics", request.ToolName);
        }

        // A structured name/toolName field is preferred over the (possibly prose) title.
        [Fact]
        public void StructuredToolNameField_WinsOverProseTitle()
        {
            var request = Map("""{ "toolCallId": "m3", "title": "Run the tests", "toolName": "mcp__code-wicket__run_tests" }""");

            Assert.Equal("mcp__code-wicket__run_tests", request.ToolName);
        }

        // A plain (non-namespaced) title is not a tool name — ToolName stays null so nothing spuriously
        // resolves to an authored risk.
        [Fact]
        public void PlainTitle_LeavesToolNameNull()
        {
            var request = Map("""{ "toolCallId": "m4", "title": "Running: git status", "kind": "execute" }""");

            Assert.Null(request.ToolName);
        }

        // --- Synthesized "Deny (this session)" for backends without reject_always --------------------

        private static readonly PermissionOptionDto[] ClaudeOptions =
        {
            new PermissionOptionDto("allow_once", "Yes", "allow_once"),
            new PermissionOptionDto("allow_always", "Always", "allow_always"),
            new PermissionOptionDto("reject_once", "No", "reject_once"),
        };

        // Claude Code sends only allow_once/allow_always/reject_once. For a scoped command request we add a
        // synthetic RejectAlways option (our session-deny affordance) carrying the sentinel id the shell
        // policy consumes and never sends to the agent.
        [Fact]
        public void ClaudeCommand_NoRejectAlways_SynthesizesSessionDeny()
        {
            var request = Map(
                """{ "toolCallId": "s1", "title": "Running: git push", "kind": "execute" }""",
                options: ClaudeOptions);

            var synth = Assert.Single(request.Options, o => o.Kind == PermissionOptionKind.RejectAlways);
            Assert.Equal(AcpMapper.SyntheticDenySessionOptionId, synth.OptionId);
        }

        // A file-scoped (edit) request also gets one — Path is the deny subject.
        [Fact]
        public void ClaudeEdit_NoRejectAlways_SynthesizesSessionDeny()
        {
            var request = Map(
                """{ "toolCallId": "s2", "title": "Edit a.cs", "kind": "edit", "rawInput": { "file_path": "C:\\ws\\a.cs" } }""",
                options: ClaudeOptions);

            Assert.Contains(request.Options, o =>
                o.Kind == PermissionOptionKind.RejectAlways && o.OptionId == AcpMapper.SyntheticDenySessionOptionId);
        }

        // A named MCP tool request gets one too (deny-by-name; it carries no command or path).
        [Fact]
        public void McpTool_NoRejectAlways_SynthesizesSessionDeny()
        {
            var request = Map(
                """{ "toolCallId": "s3", "title": "mcp__code-wicket__run_tests", "kind": "other" }""",
                options: ClaudeOptions);

            Assert.NotNull(request.ToolName);
            Assert.Null(request.Command);
            Assert.Null(request.Path);
            Assert.Contains(request.Options, o => o.Kind == PermissionOptionKind.RejectAlways);
        }

        // Kiro already sends reject_always — we must NOT add a duplicate.
        [Fact]
        public void KiroCommand_WithRejectAlways_NotSynthesized()
        {
            var request = Map(
                """{ "toolCallId": "s4", "title": "Running: git push", "kind": "execute" }""",
                options: new[]
                {
                    new PermissionOptionDto("accept", "Allow", "allow_once"),
                    new PermissionOptionDto("reject", "Deny", "reject_once"),
                    new PermissionOptionDto("always-reject", "Always deny", "reject_always"),
                });

            Assert.Single(request.Options, o => o.Kind == PermissionOptionKind.RejectAlways);
            Assert.DoesNotContain(request.Options, o => o.OptionId == AcpMapper.SyntheticDenySessionOptionId);
        }

        // An unscoped request (no command, path, or tool name) is NOT synthesized — a by-kind block would
        // be too coarse to offer here.
        [Fact]
        public void UnscopedRequest_NotSynthesized()
        {
            var request = Map(
                """{ "toolCallId": "s5", "title": "Approve action", "kind": "edit" }""",
                options: ClaudeOptions);

            Assert.Null(request.Command);
            Assert.Null(request.Path);
            Assert.Null(request.ToolName);
            Assert.DoesNotContain(request.Options, o => o.Kind == PermissionOptionKind.RejectAlways);
        }

        // Without a plain reject to fall back to, nothing is synthesized (Decide would have no real id to
        // answer the agent with).
        [Fact]
        public void NoRejectOnce_NotSynthesized()
        {
            var request = Map(
                """{ "toolCallId": "s6", "title": "Running: git push", "kind": "execute" }""",
                options: new[] { new PermissionOptionDto("allow_once", "Yes", "allow_once") });

            Assert.DoesNotContain(request.Options, o => o.Kind == PermissionOptionKind.RejectAlways);
        }

        // --- Kiro v3: _meta.kiro.consent (frame shape captured live 2026-07-20) --------------------

        // The exact write frame v3 sends: no kind, no trustOptions — capability drives the kind, and
        // resource+workspaceRoot resolve to the absolute path subject the shell's path rules match.
        [Fact]
        public void KiroV3_FsWriteConsent_DerivesEditKindAndPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "v1", "status": "pending", "title": "Write File" }""",
                """
                { "kiro": { "toolId": "fs_write",
                            "consent": { "capability": "fs_write", "resource": "hello.txt",
                                         "askType": "implicit", "workspaceRoot": "C:\\ws" },
                            "consentRound": 1 } }
                """);

            Assert.Equal("edit", request.Kind);
            Assert.Equal(@"C:\ws\hello.txt", request.Path);
        }

        [Fact]
        public void KiroV3_FsReadConsent_DerivesReadKind()
        {
            var request = Map(
                """{ "toolCallId": "v2", "title": "Read File" }""",
                """{ "kiro": { "consent": { "capability": "fs_read", "resource": "a.cs", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Equal("read", request.Kind);
            Assert.Equal(@"C:\ws\a.cs", request.Path);
        }

        // The consent's capability is the MATCHED RULE's, not the tool's (kiro-cli 2.21.1, measured
        // 2026-09-11 under an agent profile whose one rule is `capability: all, effect: ask`): a plain
        // read arrived as capability "all", and before this the request had no kind and no path — top
        // tier, matching no path rule. The toolId beside it says what the tool is.
        [Fact]
        public void KiroV3_AllRuleConsent_ReadKindAndPathComeFromTheToolId()
        {
            var request = Map(
                """{ "toolCallId": "v5", "title": "Read File" }""",
                """
                { "kiro": { "toolId": "read_file",
                            "consent": { "capability": "all", "resource": "notes.txt", "askType": "explicit",
                                         "matchedRule": { "capability": "all", "effect": "ask" },
                                         "scope": "agent", "source": "agent-profile", "workspaceRoot": "C:\\ws" },
                            "consentRound": 1 } }
                """);

            Assert.Equal("read", request.Kind);
            Assert.Equal(@"C:\ws\notes.txt", request.Path);
        }

        [Fact]
        public void KiroV3_AllRuleConsent_WriteToolId_DerivesEditKindAndPath()
        {
            var request = Map(
                """{ "toolCallId": "v6", "title": "Write File" }""",
                """{ "kiro": { "toolId": "fs_write", "consent": { "capability": "all", "resource": "out.txt", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Equal("edit", request.Kind);
            Assert.Equal(@"C:\ws\out.txt", request.Path);
        }

        // Under an "all" rule a shell tool's resource is still the command text: kind from the tool id,
        // and no path subject.
        [Fact]
        public void KiroV3_AllRuleConsent_ShellToolId_DerivesExecuteKind_NoPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "v7", "title": "Running: git status" }""",
                """{ "kiro": { "toolId": "execute_bash", "consent": { "capability": "all", "resource": "git status", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Equal("execute", request.Kind);
            Assert.Null(request.Path);
        }

        // A tool id that names nothing file- or shell-like stays unkinded (top tier), and its resource
        // — a sub-agent's name here — must not be taken for a file.
        [Fact]
        public void KiroV3_AllRuleConsent_UnknownToolId_StaysUnkinded_NoPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "v8", "title": "Sub-agent: kiro_default" }""",
                """{ "kiro": { "toolId": "invoke_sub_agent", "consent": { "capability": "all", "resource": "kiro_default", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Null(request.Kind);
            Assert.Null(request.Path);
        }

        // A shell consent's resource is the command text, NOT a file — it must never become a Path
        // subject a path rule could match.
        [Fact]
        public void KiroV3_ShellConsent_DerivesExecuteKind_NoPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "v3", "title": "Running: git status" }""",
                """{ "kiro": { "consent": { "capability": "shell", "resource": "git status", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Equal("execute", request.Kind);
            Assert.Null(request.Path);
        }

        // An already-absolute resource is used as-is (not re-rooted against the workspace).
        [Fact]
        public void KiroV3_AbsoluteResource_UsedAsIs()
        {
            var request = Map(
                """{ "toolCallId": "v4", "title": "Write File" }""",
                """{ "kiro": { "consent": { "capability": "fs_write", "resource": "C:\\elsewhere\\x.txt", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Equal(@"C:\elsewhere\x.txt", request.Path);
        }

        // Non-fs capabilities (mcp, subagent, web…) stay unkinded — MCP risk resolves by tool name.
        [Fact]
        public void KiroV3_McpCapability_LeavesKindNull()
        {
            var request = Map(
                """{ "toolCallId": "v5", "title": "Running: @code-wicket/run_tests" }""",
                """{ "kiro": { "consent": { "capability": "mcp", "resource": "code-wicket/run_tests" } } }""");

            Assert.Null(request.Kind);
            Assert.Null(request.Path);
            Assert.Equal("@code-wicket/run_tests", request.ToolName);
        }

        // --- Path subject from Kiro v2 trustOptions and spec-ACP rawInput ---------------------------

        // The real v2 write frame (captured 2026-07-03): "Specific paths" carries the file's absolute
        // path in patterns; it wins over the "Complete directory" row.
        [Fact]
        public void KiroV2_TrustOptionsSpecificPaths_IsThePathSubject()
        {
            var request = Map(
                """{ "toolCallId": "w1", "title": "Creating Program.cs" }""",
                """
                { "trustOptions": [
                    { "label": "Specific paths", "display": "Program.cs",
                      "setting_key": "runtime_write_paths", "patterns": ["C:\\proj\\Program.cs"] },
                    { "label": "Complete directory", "display": "C:\\proj",
                      "setting_key": "runtime_write_paths", "patterns": ["C:\\proj"] } ] }
                """);

            Assert.Equal("edit", request.Kind);
            Assert.Equal(@"C:\proj\Program.cs", request.Path);
        }

        // Claude (spec-ACP) names the file in the tool's structured input.
        [Fact]
        public void Claude_RawInputFilePath_IsThePathSubject()
        {
            var request = Map(
                """{ "toolCallId": "w2", "title": "Edit a.cs", "kind": "edit", "rawInput": { "file_path": "C:\\ws\\a.cs", "old_string": "x", "new_string": "y" } }""");

            Assert.Equal(@"C:\ws\a.cs", request.Path);
        }

        // A command request never gets a Path subject, even when its input carries path-ish fields.
        [Fact]
        public void CommandRequest_HasNoPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "w3", "title": "Running: cat a.txt", "kind": "execute", "rawInput": { "command": "cat a.txt", "path": "C:\\ws\\a.txt" } }""");

            Assert.Null(request.Path);
        }

        // An MCP TOOL never gets a Path subject either — its subject is the tool, and a "path" in a
        // third party's rawInput means whatever that server says it means (#129, #131). The gate is the
        // namespaced tool NAME, because Kiro sends no kind for an MCP call and DeriveKind returns null
        // for one by design, so nothing above this catches it.
        //
        // Three failures ride on this one field, and they close a loop: a persisted AllowedPaths glob
        // silently auto-approves the call (PolicyPermissionHandler asserts the opposite in its own
        // comment); the banner offers a PATH-scoped "Allow always" and, gating HasToolScope on
        // !HasPathScope, hides the server/tool rule that is an MCP tool's durable home; and
        // DisplayDetail goes null under a path scope, so the arguments are hidden from the approval.
        [Theory]
        [InlineData("mcp__deploy-server__deploy")]  // Claude's namespacing
        [InlineData("@deploy-server/deploy")]       // Kiro's
        public void McpToolCall_HasNoPathSubject(string toolName)
        {
            var request = Map(
                $$"""
                { "toolCallId": "m1", "title": "Deploy", "toolName": "{{toolName}}",
                  "rawInput": { "path": "C:\\repo\\dist" } }
                """);

            Assert.Null(request.Path);
            Assert.Equal(toolName, request.ToolName);
        }

        // The same call the other way round: an MCP tool whose input names a file_path, and one whose
        // path arrives via locations[] rather than rawInput. Both fallbacks sat below the missing gate.
        [Fact]
        public void McpToolCall_HasNoPathSubjectFromFilePathOrLocations()
        {
            Assert.Null(Map(
                """
                { "toolCallId": "m2", "title": "Running: @jira/create_issue", "toolName": "@jira/create_issue",
                  "rawInput": { "file_path": "C:\\repo\\src\\Foo.cs" } }
                """).Path);

            Assert.Null(Map(
                """
                { "toolCallId": "m3", "title": "Index", "toolName": "mcp__search__index",
                  "locations": [ { "path": "C:\\repo\\src\\Foo.cs" } ] }
                """).Path);
        }

        // The other direction, and the one that would hurt to lose: the gate is anchored on the
        // namespace, so an ordinary edit keeps its file subject. A bare toolName is not an MCP call —
        // Claude's built-in Edit sends one, and it is exactly what a path rule SHOULD govern.
        [Fact]
        public void ABareToolNameIsNotAnMcpCallAndKeepsItsPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "e1", "title": "Edit a.cs", "kind": "edit", "toolName": "Edit", "rawInput": { "file_path": "C:\\ws\\a.cs" } }""");

            Assert.Equal(@"C:\ws\a.cs", request.Path);
            Assert.Null(request.ToolName); // not namespaced -> not a tool subject either
        }

        // Kiro v3's consent block is fs-gated already, but pin that an MCP capability alongside a
        // resource cannot produce a path subject: v3 names MCP consents "mcp", never "fs_*".
        [Fact]
        public void KiroV3_McpConsent_HasNoPathSubject()
        {
            var request = Map(
                """{ "toolCallId": "m4", "title": "Deploy", "toolName": "@deploy-server/deploy" }""",
                """{ "kiro": { "consent": { "capability": "mcp", "resource": "dist", "workspaceRoot": "C:\\repo" } } }""");

            Assert.Null(request.Path);
        }

        // v3 sends percent-encoded file:// URIs in diff paths; if one reaches a permission frame it
        // normalizes to a local path so path rules and the banner see a real filename.
        [Fact]
        public void FileUriPath_NormalizesToLocalPath()
        {
            var request = Map(
                """{ "toolCallId": "w4", "title": "Write File", "kind": "edit", "rawInput": { "path": "file:///c%3A/ws/hello.txt" } }""");

            Assert.Equal(@"C:\ws\hello.txt", request.Path); // drive letter canonicalized to uppercase
        }

        // --- MCP-call args via the rawInput cache (v3 frame shapes captured live 2026-07-20) --------

        // Kiro's permission frame carries no rawInput, but v3's preceding tool_call does — cached by
        // toolCallId, it becomes the banner's Detail, with the _meta validation blob stripped.
        [Fact]
        public void McpPermission_UsesCachedToolCallRawInputAsDetail()
        {
            var toolCallUpdate = JsonDocument.Parse(
                """
                { "sessionUpdate": "tool_call", "toolCallId": "tooluse_1", "title": "@code_wicket/echo",
                  "kind": "other", "status": "pending",
                  "rawInput": { "text": "BRIDGE-token", "_meta": { "_isValid": true, "_activePath": ["text"] } } }
                """).RootElement.Clone();

            var cache = new System.Collections.Generic.Dictionary<string, string>();
            AcpMapper.CacheToolRawInput(toolCallUpdate, cache);

            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(
                    """{ "toolCallId": "tooluse_1", "status": "pending", "title": "@code_wicket/echo" }""").RootElement.Clone(),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            };
            var request = AcpMapper.ToPermissionRequest(p, rawInputCache: cache);

            Assert.Contains("BRIDGE-token", request.Detail);
            Assert.DoesNotContain("_isValid", request.Detail);
        }

        // A permission frame with its own rawInput ignores the cache (spec-ACP agents like Claude).
        [Fact]
        public void PermissionWithOwnRawInput_IgnoresCache()
        {
            var cache = new System.Collections.Generic.Dictionary<string, string> { ["t1"] = """{"stale":"x"}""" };
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(
                    """{ "toolCallId": "t1", "title": "Edit", "kind": "edit", "rawInput": { "file_path": "a.cs" } }""").RootElement.Clone(),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            };

            var request = AcpMapper.ToPermissionRequest(p, rawInputCache: cache);

            Assert.Contains("a.cs", request.Detail);
            Assert.DoesNotContain("stale", request.Detail);
        }

        // The tool row's raw input drops v3's _meta validation blob too (it's protocol noise, not args),
        // and a rawInput holding ONLY _meta counts as argument-less.
        [Fact]
        public void ToolCallStarted_StripsMetaFromRawInput()
        {
            var update = JsonDocument.Parse(
                """
                { "sessionUpdate": "tool_call", "toolCallId": "t1", "title": "@code_wicket/echo",
                  "kind": "other", "rawInput": { "text": "hi", "_meta": { "_isValid": true } } }
                """).RootElement.Clone();

            var started = Assert.Single(AcpMapper.Map(update).OfType<AgentEvent.ToolCallStarted>());

            Assert.Contains("hi", started.RawInputJson);
            Assert.DoesNotContain("_isValid", started.RawInputJson);
        }

        [Fact]
        public void RawInputWithOnlyMeta_TreatedAsNoArguments()
        {
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(
                    """{ "toolCallId": "t1", "title": "x", "kind": "other", "rawInput": { "_meta": { "_isValid": true } } }""").RootElement.Clone(),
                Options = new[] { new PermissionOptionDto("accept", "Allow", "allow_once") },
            };

            Assert.Null(AcpMapper.ToPermissionRequest(p).Detail);
        }
    }
}
