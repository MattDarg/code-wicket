using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Permanent per-tool trust, and the accident it replaces (issue #129).
    /// <para>
    /// Kiro's default engine titles an MCP tool call <c>"Running: @code-wicket/build_solution"</c>
    /// — the same convention it uses for real shell commands — so the mapper resolved the tool NAME as
    /// the request's command, the banner offered an editable *command* glob, and "save permanently"
    /// wrote a tool name into the command allow-list. Kiro v3 and Claude title the same call without
    /// that prefix, so the rule matched nothing there and went quietly inert.
    /// </para>
    /// <para>
    /// Three layers have to agree for that to be fixed rather than merely moved: the mapper must not
    /// invent a command, the policy must have somewhere real to put a tool rule, and the banner must
    /// offer it. Each is pinned here, in that order.
    /// </para>
    /// </summary>
    [Collection(StoragePathCollection.Name)]
    public sealed class ToolTrustTests
    {
        private const string OurTool = "@code-wicket/build_solution";
        private const string OurToolClaude = "mcp__code-wicket__build_solution";
        private const string ForeignTool = "@some-other-server/build_solution";

        // ---- the mapper: an MCP tool call is not a shell command -------------------------------------

        private static PermissionRequest MapPermission(string toolCallJson)
        {
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(toolCallJson).RootElement.Clone(),
                Options = new[] { new Providers.Acp.PermissionOptionDto("allow", "Allow", "allow_once") },
            };
            return AcpMapper.ToPermissionRequest(p);
        }

        /// <summary>
        /// The defect itself, from a captured frame (2026-08-10): Kiro's default engine,
        /// no kind, no rawInput, the tool name wearing the shell-command title convention.
        /// </summary>
        [Fact]
        public void AToolCallTitledLikeACommandDoesNotBecomeOne()
        {
            var request = MapPermission(
                $$"""{ "toolCallId": "k1", "title": "Running: {{OurTool}}" }""");

            Assert.Null(request.Command);
            Assert.Equal(OurTool, request.ToolName);
        }

        /// <summary>Kiro v3's shape for the same call — bare title. Never had a command; still doesn't.</summary>
        [Fact]
        public void TheSameToolCallOnV3AlsoHasNoCommand()
        {
            var request = MapPermission($$"""{ "toolCallId": "k2", "title": "{{OurTool}}" }""");

            Assert.Null(request.Command);
            Assert.Equal(OurTool, request.ToolName);
        }

        /// <summary>
        /// The veto is narrow: a REAL shell command still resolves from its title, which is the whole
        /// reason that path exists (Kiro's request_permission carries no rawInput).
        /// </summary>
        [Fact]
        public void ARealShellCommandStillResolvesFromItsTitle()
        {
            var request = MapPermission("""{ "toolCallId": "c1", "title": "Running: git status" }""");

            Assert.Equal("git status", request.Command);
            Assert.Null(request.ToolName);
        }

        /// <summary>
        /// A tool that genuinely WRAPS a command keeps it — our own run_command, whose argument is
        /// literally `command`. Losing this would take away the command glob for the one tool that
        /// should have one, and would blind the caution list to what it is about to run.
        /// <para>What it does NOT keep, since the pre-release security review, is a name lifted from the title: a frame
        /// carrying a real command is a shell command, and the mapper cannot tell this title from a
        /// shell run whose command the model spelled as one of our tool names (see
        /// <see cref="ShellCommandImpersonationTests"/>). The name bought a command frame nothing:
        /// its tier is Command by kind exactly as run_command is authored, and the banner offers a
        /// command glob, never a tool rule, for anything with a command.</para>
        /// </summary>
        [Fact]
        public void AToolThatWrapsACommandKeepsTheRealCommand()
        {
            var request = MapPermission(
                """
                {
                  "toolCallId": "rc1",
                  "title": "Running: @code-wicket/run_command",
                  "rawInput": { "command": "msbuild MyProj.csproj -t:Build" }
                }
                """);

            Assert.Equal("msbuild MyProj.csproj -t:Build", request.Command);
            Assert.Null(request.ToolName);
        }

        // ---- the policy: a tool rule is a real, matchable subject ------------------------------------

        private sealed class StubPrompt : IPermissionHandler
        {
            public int Calls;
            public Func<PermissionRequest, PermissionDecision> Responder = _ => new PermissionDecision("reject");

            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(Responder(request));
            }
        }

        private static PermissionRequest ToolRequest(string toolName) =>
            new("t1", toolName, Kind: null, Detail: null, Command: null,
                new[]
                {
                    new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
                    new PermissionOption("allow-always", "Allow always", PermissionOptionKind.AllowAlways),
                    new PermissionOption("reject", "Reject", PermissionOptionKind.RejectOnce),
                    new PermissionOption("reject-always", "Reject always", PermissionOptionKind.RejectAlways),
                },
                ToolName: toolName);

        private static (PolicyPermissionHandler handler, StubPrompt prompt) NewHandler(
            IEnumerable<string>? allowTools = null, PermissionMode mode = PermissionMode.Prompt)
        {
            var prompt = new StubPrompt();
            var handler = new PolicyPermissionHandler(prompt, mode);
            handler.SetToolPolicy(allowTools);
            return (handler, prompt);
        }

        /// <summary>
        /// The headline: a config rule auto-approves the tool in Prompt mode, where every IDE tool call
        /// otherwise prompts.
        /// </summary>
        [Fact]
        public async Task AConfiguredToolIsAutoApproved()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "code-wicket/build_solution" });

            var decision = await handler.RequestAsync(ToolRequest(OurTool));

            Assert.Equal("allow", decision.OptionId);
            Assert.Equal(0, prompt.Calls);
        }

        /// <summary>
        /// One rule, every spelling: the two backends namespace the same tool differently and v3
        /// underscores the server, so a rule saved under one has to hold under the others. This is what
        /// the old command-list entries could not do — and why they stopped working when the engine
        /// changed. A hand-typed bare name is accepted and means ours.
        /// </summary>
        [Theory]
        [InlineData(OurTool)]
        [InlineData(OurToolClaude)]
        [InlineData("@code_wicket/build_solution")]
        public async Task AToolRuleHoldsOnEveryBackendSpelling(string incoming)
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "code-wicket/build_solution" });

            Assert.Equal("allow", (await handler.RequestAsync(ToolRequest(incoming))).OptionId);
            Assert.Equal(0, prompt.Calls);
        }

        /// <summary>A bare name typed into settings still works — it is taken as ours.</summary>
        [Fact]
        public async Task ABareRuleIsTakenAsOurs()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "build_solution" });

            Assert.Equal("allow", (await handler.RequestAsync(ToolRequest(OurTool))).OptionId);
            Assert.Equal(0, prompt.Calls);
        }

        /// <summary>
        /// The collision guard, and the reason every rule keeps its server: another MCP server's tool of
        /// the same name must still be asked about. A grant leaking across servers would be the worst
        /// kind of silent widening, since nothing in the banner would have told the user it could happen.
        /// </summary>
        [Fact]
        public async Task AnotherServersToolOfTheSameNameIsStillAsked()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "code-wicket/build_solution" });

            await handler.RequestAsync(ToolRequest(ForeignTool));

            Assert.Equal(1, prompt.Calls);
        }

        /// <summary>...and the converse: a rule for another server's tool does not grant OURS of that name.</summary>
        [Fact]
        public async Task OurToolIsStillAskedWhenOnlyTheForeignOneIsTrusted()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "some-other-server/build_solution" });

            await handler.RequestAsync(ToolRequest(OurTool));

            Assert.Equal(1, prompt.Calls);
        }

        /// <summary>
        /// A foreign tool CAN be trusted permanently — it keeps its server, so there is no collision to
        /// protect against. Refusing it would rest on tool rules being scoped to
        /// our own catalog; that scoping only ever existed to stop BARE names colliding.
        /// </summary>
        [Fact]
        public async Task AForeignToolCanBeTrustedByItsFullName()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "some-other-server/build_solution" });

            var decision = await handler.RequestAsync(ToolRequest(ForeignTool));

            Assert.Equal("allow", decision.OptionId);
            Assert.Equal(0, prompt.Calls);
        }

        /// <summary>
        /// A foreign rule holds across backends too, and without parsing the incoming name: the rule
        /// builds both spellings from its own halves, so an unknown server's ambiguous split cannot
        /// widen it. Kiro v3's underscored server name falls out of the same construction.
        /// </summary>
        [Theory]
        [InlineData("@some-other-server/build_solution")]
        [InlineData("mcp__some-other-server__build_solution")]
        [InlineData("@some_other_server/build_solution")]
        public async Task AForeignRuleHoldsAcrossBackendSpellings(string incoming)
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "some-other-server/build_solution" });

            Assert.Equal("allow", (await handler.RequestAsync(ToolRequest(incoming))).OptionId);
            Assert.Equal(0, prompt.Calls);
        }

        /// <summary>
        /// The TOOL half is not folded the way the server half is: run_tests and run-tests are different
        /// tools, and a trust rule may not conflate them just because one backend normalizes servers.
        /// </summary>
        [Fact]
        public async Task AForeignRuleDoesNotConflateUnderscoresInTheToolName()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "some-other-server/build_solution" });

            await handler.RequestAsync(ToolRequest("@some-other-server/build-solution"));

            Assert.Equal(1, prompt.Calls);
        }

        /// <summary>A tool "always" with no persist box holds for the session and touches no config.</summary>
        [Fact]
        public async Task AlwaysAllowRemembersTheToolForTheSession()
        {
            var (handler, prompt) = NewHandler();
            string? persisted = null;
            handler.PersistAllowedTool = t => persisted = t;
            prompt.Responder = _ => new PermissionDecision("allow-always", RememberTool: OurTool);

            await handler.RequestAsync(ToolRequest(OurTool));
            var second = await handler.RequestAsync(ToolRequest(OurTool));

            Assert.Equal("allow", second.OptionId);
            Assert.Equal(1, prompt.Calls); // the second never reached the user
            Assert.Null(persisted);
        }

        /// <summary>
        /// With the box ticked it persists — canonically, whichever spelling the backend sent, so the
        /// config list is portable AND says which server it is trusting.
        /// </summary>
        [Fact]
        public async Task PersistedToolIsStoredCanonically()
        {
            var (handler, prompt) = NewHandler();
            string? persisted = null;
            handler.PersistAllowedTool = t => persisted = t;
            prompt.Responder = _ => new PermissionDecision(
                "allow-always", RememberTool: OurToolClaude, PersistRemembered: true);

            await handler.RequestAsync(ToolRequest(OurToolClaude));

            Assert.Equal("code-wicket/build_solution", persisted);
        }

        /// <summary>
        /// Precedence: a session deny for the same tool beats a session grant for it. The deny tier runs
        /// first by design, and a tool rule must not be the thing that finally out-ranks it.
        /// </summary>
        [Fact]
        public async Task ADeniedToolStaysDeniedDespiteAGrant()
        {
            var (handler, prompt) = NewHandler();
            prompt.Responder = _ => new PermissionDecision("allow-always", RememberTool: OurTool);
            await handler.RequestAsync(ToolRequest(OurTool));

            // The user changes their mind: deny always (unscoped → remembered by tool key).
            prompt.Responder = _ => new PermissionDecision("reject-always");
            handler.ResetRemembered();
            await handler.RequestAsync(ToolRequest(OurTool));

            var decision = await handler.RequestAsync(ToolRequest(OurTool));
            Assert.Equal("reject", decision.OptionId);
        }

        /// <summary>
        /// A mode change clears session grants (it always has — the ladder moved under them) but must
        /// NOT clear the config list, which is the difference the persist box buys.
        /// </summary>
        [Fact]
        public async Task AModeChangeClearsTheSessionGrantButNotTheConfiguredOne()
        {
            var (handler, prompt) = NewHandler(allowTools: new[] { "code-wicket/run_tests" });
            prompt.Responder = _ => new PermissionDecision("allow-always", RememberTool: OurTool);
            await handler.RequestAsync(ToolRequest(OurTool));       // session grant for build_solution
            var callsAfterGrant = prompt.Calls;

            handler.Mode = PermissionMode.Prompt;                   // setter resets remembered choices

            await handler.RequestAsync(ToolRequest(OurTool));       // asks again
            Assert.Equal(callsAfterGrant + 1, prompt.Calls);

            var configured = await handler.RequestAsync(ToolRequest("@code-wicket/run_tests"));
            Assert.Equal("allow", configured.OptionId);
            Assert.Equal(callsAfterGrant + 1, prompt.Calls);        // config rule still silent
        }

        // ---- the config: the list, and the migration off the command list ----------------------------

        /// <summary>
        /// Every rule normalizes to canonical server/tool — ours included, so the stored list shows a
        /// hand-editor the format, and so no reader has to know that a bare name would have meant ours.
        /// A bare name typed by a person still resolves, gaining our server.
        /// </summary>
        [Fact]
        public void ToolRulesNormalizeToServerSlashTool()
        {
            Assert.True(IdeMcpServer.TryNormalizeToolRule(OurTool, out var kiro));
            Assert.Equal("code-wicket/build_solution", kiro);
            Assert.True(IdeMcpServer.TryNormalizeToolRule(OurToolClaude, out var claude));
            Assert.Equal("code-wicket/build_solution", claude);
            Assert.True(IdeMcpServer.TryNormalizeToolRule("build_solution", out var bare));
            Assert.Equal("code-wicket/build_solution", bare);
            // v3 underscores the server; the stored rule keeps the canonical hyphenated spelling.
            Assert.True(IdeMcpServer.TryNormalizeToolRule("@code_wicket/build_solution", out var v3));
            Assert.Equal("code-wicket/build_solution", v3);

            // Another server's keeps ITS server — never stripped to a bare name, which would then match
            // OUR tool of that name.
            Assert.True(IdeMcpServer.TryNormalizeToolRule(ForeignTool, out var foreign));
            Assert.Equal("some-other-server/build_solution", foreign);
            Assert.True(IdeMcpServer.TryNormalizeToolRule("mcp__gitlab__create_issue", out var claudeForeign));
            Assert.Equal("gitlab/create_issue", claudeForeign);
        }

        /// <summary>
        /// Normalization is IDEMPOTENT, and that is load-bearing rather than tidy: the list is
        /// re-normalized on every config load and every SetToolPolicy, so a rule that could not survive
        /// its own output shape would be dropped the moment it was read back — every stored rule
        /// silently forgotten one restart after the user made it. (Caught by these tests, having been
        /// written that way.)
        /// </summary>
        [Theory]
        [InlineData("code-wicket/build_solution")]
        [InlineData("some-other-server/build_solution")]
        [InlineData("gitlab/create_issue")]
        public void NormalizingACanonicalRuleReturnsItUnchanged(string rule)
        {
            Assert.True(IdeMcpServer.TryNormalizeToolRule(rule, out var once));
            Assert.Equal(rule, once);
            Assert.True(IdeMcpServer.TryNormalizeToolRule(once, out var twice));
            Assert.Equal(rule, twice);
        }

        /// <summary>
        /// The migration that stops an existing grant going inert. EVERY namespaced name moves — ours
        /// unwrapped, another server's canonicalised — because both backends' spellings are a shape no
        /// shell command shares. A real command stays put. Leaving foreign entries behind was the first
        /// cut of this, and it left exactly the dead-rule-you-can-still-see the migration exists to stop.
        /// </summary>
        [Fact]
        public void ToolNamesInTheCommandListMigrateToTheToolList()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "scratch", "tooltrust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path,
                """
                {
                  "allowedCommands": [
                    "@code-wicket/build_solution",
                    "mcp__code-wicket__run_tests",
                    "dotnet build",
                    "@some-other-server/function"
                  ]
                }
                """);
            ExtensionConfig.RedirectTo(path);
            try
            {
                var config = ExtensionConfig.Load();

                Assert.Equal(
                    new[]
                    {
                        "code-wicket/build_solution",
                        "code-wicket/run_tests",
                        "some-other-server/function",
                    },
                    config.AllowedTools);
                Assert.Equal(new[] { "dotnet build" }, config.AllowedCommands);
            }
            finally
            {
                ExtensionConfig.RedirectTo(null!);
                try { Directory.Delete(dir, recursive: true); } catch { /* scratch */ }
            }
        }

        // ---- the banner: the durable option is reachable ---------------------------------------------

        private static PermissionBannerViewModel Banner(
            string? toolName, out List<(string option, string? command, string? path, bool persist, string? tool)> decisions)
        {
            var captured = new List<(string, string?, string?, bool, string?)>();
            decisions = captured;
            return new PermissionBannerViewModel(
                new PermissionRequestDto(
                    "t1", toolName ?? "Some action", null, null, null,
                    new[]
                    {
                        new Ipc.PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
                        new Ipc.PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
                        new Ipc.PermissionOptionDto("reject_once", "Deny", "RejectOnce"),
                    },
                    ToolName: toolName),
                (option, command, path, persist, tool) => captured.Add((option, command, path, persist, tool)));
        }

        /// <summary>
        /// "Always allow" on a tool opens the second step — which exists here for the persist checkbox
        /// alone. Without it the durable option is unreachable and the grant dies with the session,
        /// which is the state this issue found.
        /// </summary>
        [Fact]
        public void AToolAlwaysOpensTheStepAndCanBeSavedPermanently()
        {
            var banner = Banner(OurTool, out var decisions);
            Assert.True(banner.HasToolScope);

            banner.Options.Single(o => o.Kind == "AllowAlways").Command.Execute(null);

            Assert.True(banner.IsEditingAlways);
            Assert.Empty(decisions); // deferred to the step, not resolved on the click
            // Shown exactly as the rule will be stored, server and all.
            Assert.Equal("code-wicket/build_solution", banner.Pattern);
            Assert.True(banner.IsPatternReadOnly);            // exact match: nothing to widen
            Assert.True(banner.CanPersist);
            Assert.Contains("tool", banner.AlwaysLabel, StringComparison.OrdinalIgnoreCase);

            banner.PersistRemembered = true;
            banner.ConfirmAlwaysCommand.Execute(null);

            var (option, command, path, persist, tool) = Assert.Single(decisions);
            Assert.Equal("allow_always", option);
            Assert.Equal(OurTool, tool);   // namespaced: the policy does its own unwrapping
            Assert.True(persist);
            Assert.Null(command);
            Assert.Null(path);
        }

        /// <summary>
        /// Another server's tool gets the same step — but named with its SERVER, so the banner never
        /// hides whose tool is being trusted and the saved rule can't be mistaken for one of ours.
        /// </summary>
        [Fact]
        public void AForeignToolGetsTheStepNamedWithItsServer()
        {
            var banner = Banner(ForeignTool, out var decisions);
            Assert.True(banner.HasToolScope);

            banner.Options.Single(o => o.Kind == "AllowAlways").Command.Execute(null);

            Assert.True(banner.IsEditingAlways);
            Assert.Equal("some-other-server/build_solution", banner.Pattern);
            Assert.True(banner.IsPatternReadOnly);

            banner.PersistRemembered = true;
            banner.ConfirmAlwaysCommand.Execute(null);

            var (_, _, _, persist, tool) = Assert.Single(decisions);
            Assert.Equal(ForeignTool, tool);
            Assert.True(persist);
        }

        /// <summary>
        /// A request with no tool name at all still resolves on the click — there is no subject to
        /// write a rule about, so there is nothing for the step to show.
        /// </summary>
        [Fact]
        public void ARequestWithNoToolNameResolvesAtOnce()
        {
            var banner = Banner(null, out var decisions);
            Assert.False(banner.HasToolScope);

            banner.Options.Single(o => o.Kind == "AllowAlways").Command.Execute(null);

            Assert.False(banner.IsEditingAlways);
            var (_, _, _, persist, tool) = Assert.Single(decisions);
            Assert.Null(tool);
            Assert.False(persist);
        }
    }
}
