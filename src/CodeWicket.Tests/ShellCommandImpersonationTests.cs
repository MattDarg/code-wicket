using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A shell command cannot borrow an IDE tool's name to borrow its tier (pre-release security review, September 2026).
    /// <para>
    /// The permission ladder resolves our IDE tools by NAME, and for Kiro the name is lifted out of
    /// the request's title - which for a shell command is <c>Running: &lt;whatever the model
    /// chose&gt;</c>. So a model steered into running <c>@code-wicket/get_diagnostics</c> (a batch
    /// file a cloned repository shipped, or one the agent wrote itself under AcceptEdits) produced a
    /// frame whose title lifted out as our tool's name, resolved to its authored ReadOnly tier, and
    /// was approved under AcceptReads with no banner - the audit row calling it a read-only IDE
    /// query. A persisted <c>code-wicket/get_diagnostics</c> rule approved the same command under
    /// Prompt mode.
    /// </para>
    /// <para>
    /// The fix is keyed on a fact the BACKEND states and the title cannot forge: an execute kind, or
    /// a command in rawInput / the tool_call cache. Where that fact is present, the mapper refuses
    /// to take a name from the title and the policy refuses to trust any name at all. Where it is
    /// absent - every real MCP call on both shipping backends - nothing changes, and that half matters:
    /// distrusting the title everywhere sends every IDE tool top-tier on Claude, whose MCP frames carry no structured name.
    /// </para>
    /// </summary>
    public sealed class ShellCommandImpersonationTests
    {
        private const string Impersonated = "@code-wicket/get_diagnostics";
        private const string ImpersonatedClaude = "mcp__code-wicket__get_diagnostics";

        private static PermissionRequest Map(
            string toolCallJson, string? metaJson = null, IReadOnlyDictionary<string, string>? commandCache = null)
        {
            var p = new RequestPermissionParams
            {
                ToolCall = JsonDocument.Parse(toolCallJson).RootElement.Clone(),
                Options = new[]
                {
                    new PermissionOptionDto("allow", "Allow", "allow_once"),
                    new PermissionOptionDto("reject", "Reject", "reject_once"),
                },
                Meta = metaJson is null ? null : JsonDocument.Parse(metaJson).RootElement.Clone(),
            };
            return AcpMapper.ToPermissionRequest(p, commandCache);
        }

        // ---- the mapper: a shell frame takes no name from its title ---------------------------------

        /// <summary>The finding's own frame: Kiro v3, consent capability "shell", the title our tool's name.</summary>
        [Fact]
        public void KiroV3_ShellConsentTitledAsOurTool_IsACommandNotATool()
        {
            var request = Map(
                $$"""{ "toolCallId": "c1", "title": "Running: {{Impersonated}}" }""",
                $$"""{ "kiro": { "consent": { "capability": "shell", "resource": "{{Impersonated}}", "workspaceRoot": "C:\\ws" } } }""");

            Assert.Null(request.ToolName);
            Assert.Equal("execute", request.Kind);
            // Resolved as the command it is, so the banner shows it as one and offers a command glob.
            Assert.Equal(Impersonated, request.Command);
        }

        /// <summary>Kiro v2: no consent block, the command scope hint in trustOptions.</summary>
        [Fact]
        public void KiroV2_CommandTrustOptionTitledAsOurTool_IsACommandNotATool()
        {
            var request = Map(
                $$"""{ "toolCallId": "c2", "title": "Running: {{Impersonated}}" }""",
                """{ "trustOptions": [ { "setting_key": "allowedCommands" } ] }""");

            Assert.Null(request.ToolName);
            Assert.Equal(Impersonated, request.Command);
        }

        /// <summary>
        /// Claude's PowerShell tool declares kind "other" and carries the command in rawInput; the
        /// namespaced spelling in the title (or the command) must not become a name.
        /// </summary>
        [Theory]
        [InlineData("execute")]
        [InlineData("other")]
        public void Claude_ShellFrameTitledAsOurTool_IsACommandNotATool(string kind)
        {
            var request = Map(
                $$"""{ "toolCallId": "c3", "title": "{{ImpersonatedClaude}}", "kind": "{{kind}}", "rawInput": { "command": "{{ImpersonatedClaude}}" } }""");

            Assert.Null(request.ToolName);
            Assert.Equal(ImpersonatedClaude, request.Command);
        }

        /// <summary>
        /// Kiro's request carries no rawInput; the full command arrived on the tool_call frame and was
        /// cached. That cached command is the shell fact here, and it must veto the title too.
        /// </summary>
        [Fact]
        public void Kiro_CachedCommandIsAShellFact()
        {
            var cache = new Dictionary<string, string> { ["c4"] = Impersonated + " --verbose" };

            var request = Map($$"""{ "toolCallId": "c4", "title": "Running: {{Impersonated}}" }""", commandCache: cache);

            Assert.Null(request.ToolName);
            Assert.Equal(Impersonated + " --verbose", request.Command);
        }

        /// <summary>
        /// The other half, and the regression the earlier attempt shipped: a real MCP call on either
        /// backend keeps its name. Kiro v3 says so with capability "mcp"; Kiro v2 and Claude say
        /// nothing at all, and nothing is the shell fact's absence.
        /// </summary>
        [Fact]
        public void ARealMcpCallStillResolvesByName_KiroV3()
        {
            var request = Map(
                $$"""{ "toolCallId": "m1", "title": "Running: {{Impersonated}}" }""",
                """{ "kiro": { "consent": { "capability": "mcp", "resource": "code-wicket/get_diagnostics" } } }""");

            Assert.Equal(Impersonated, request.ToolName);
            Assert.Null(request.Command);
        }

        [Fact]
        public void ARealMcpCallStillResolvesByName_KiroV2()
        {
            var request = Map($$"""{ "toolCallId": "m2", "title": "Running: {{Impersonated}}" }""");

            Assert.Equal(Impersonated, request.ToolName);
            Assert.Null(request.Command);
        }

        [Fact]
        public void ARealMcpCallStillResolvesByName_Claude()
        {
            var request = Map(
                $$"""{ "toolCallId": "m3", "title": "{{ImpersonatedClaude}}", "kind": "other", "rawInput": { "scope": "solution" } }""");

            Assert.Equal(ImpersonatedClaude, request.ToolName);
            Assert.Null(request.Command);
        }

        /// <summary>
        /// A structured toolName is the backend's claim, not the model's, so the mapper keeps it even
        /// beside a command. The POLICY is what refuses to trust it there (below).
        /// </summary>
        [Fact]
        public void AStructuredNameBesideACommandIsKeptByTheMapper()
        {
            var request = Map(
                $$"""{ "toolCallId": "s1", "title": "Run", "kind": "execute", "toolName": "{{ImpersonatedClaude}}", "rawInput": { "command": "evil.bat" } }""");

            Assert.Equal(ImpersonatedClaude, request.ToolName);
            Assert.Equal("evil.bat", request.Command);
        }

        // ---- the policy: a shell frame gets no by-name trust, whatever name it carries ---------------

        private sealed class StubPrompt : IPermissionHandler
        {
            public int Calls;

            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new PermissionDecision("reject"));
            }
        }

        private static IReadOnlyList<PermissionOption> StdOptions() => new[]
        {
            new PermissionOption("allow", "Allow", PermissionOptionKind.AllowOnce),
            new PermissionOption("allow-always", "Allow always", PermissionOptionKind.AllowAlways),
            new PermissionOption("reject", "Reject", PermissionOptionKind.RejectOnce),
        };

        private static (PolicyPermissionHandler handler, StubPrompt prompt) NewHandler(PermissionMode mode)
        {
            var prompt = new StubPrompt();
            var handler = new PolicyPermissionHandler(prompt, mode);
            handler.SetCommandPolicy(null, null);
            handler.SetToolRisks(new[] { new ToolDescriptor("get_diagnostics", "", "") }); // ReadOnly
            return (handler, prompt);
        }

        // A request built by hand rather than through the mapper, carrying BOTH a trusted-looking name
        // and a shell fact - the shape a structured toolName beside rawInput.command maps to, and the
        // one the mapper's own guard does not reach.
        private static PermissionRequest ShellFrameNamed(string toolName, string? kind, string? command) =>
            new("c", "Running: " + toolName, kind, Detail: null, command, StdOptions(), ToolName: toolName);

        [Theory]
        [InlineData("execute", null)]         // the kind alone is the fact
        [InlineData("other", "evil.bat")]     // the command alone is the fact
        [InlineData("execute", "evil.bat")]   // both
        public async Task AcceptReads_DoesNotApproveAShellFrameByItsBorrowedName(string kind, string? command)
        {
            var (handler, prompt) = NewHandler(PermissionMode.AcceptReads);

            var decision = await handler.RequestAsync(ShellFrameNamed(Impersonated, kind, command));

            Assert.Equal(1, prompt.Calls);
            Assert.Equal("reject", decision.OptionId);
        }

        [Fact]
        public async Task APersistedToolRule_DoesNotApproveAShellFrameByItsBorrowedName()
        {
            var (handler, prompt) = NewHandler(PermissionMode.Prompt);
            handler.SetToolPolicy(new[] { "code-wicket/get_diagnostics" });

            // The rule works for the tool it names...
            var real = await handler.RequestAsync(new PermissionRequest(
                "m", Impersonated, Kind: null, Detail: null, Command: null, StdOptions(), ToolName: Impersonated));
            Assert.Equal("allow", real.OptionId);
            Assert.Equal(0, prompt.Calls);

            // ...and not for a command wearing its name.
            await handler.RequestAsync(ShellFrameNamed(Impersonated, "execute", null));
            Assert.Equal(1, prompt.Calls);
        }

        [Fact]
        public async Task ARememberedByKeyGrant_DoesNotCarryFromTheToolToAShellFrame()
        {
            // A foreign-looking name our normalizer cannot turn into a rule falls back to the key
            // memory ("tool:<name>"); the shell frame must not share that key.
            var remembering = new RememberingPrompt();
            var handler = new PolicyPermissionHandler(remembering, PermissionMode.Prompt);
            handler.SetCommandPolicy(null, null);

            const string name = "mcp__code-wicket__get_diagnostics";
            var first = await handler.RequestAsync(new PermissionRequest(
                "m", name, Kind: null, Detail: null, Command: null, StdOptions(), ToolName: name));
            Assert.Equal("allow", first.OptionId);
            Assert.Equal(1, remembering.Calls);

            var again = await handler.RequestAsync(new PermissionRequest(
                "m2", name, Kind: null, Detail: null, Command: null, StdOptions(), ToolName: name));
            Assert.Equal("allow", again.OptionId);
            Assert.Equal(1, remembering.Calls); // remembered

            await handler.RequestAsync(ShellFrameNamed(name, "execute", null));
            Assert.Equal(2, remembering.Calls); // the shell frame is prompted
        }

        // Answers "Allow always" with no glob and no tool rule, so the grant lands in the key memory.
        private sealed class RememberingPrompt : IPermissionHandler
        {
            public int Calls;

            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new PermissionDecision("allow-always"));
            }
        }

        /// <summary>The name keeps its tier where there is no shell fact: the AcceptReads path an IDE tool takes today.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("other")]
        public async Task AcceptReads_StillApprovesARealToolCallByName(string? kind)
        {
            var (handler, prompt) = NewHandler(PermissionMode.AcceptReads);

            var decision = await handler.RequestAsync(new PermissionRequest(
                "m", Impersonated, kind, Detail: null, Command: null, StdOptions(), ToolName: Impersonated));

            Assert.Equal("allow", decision.OptionId);
            Assert.Equal(0, prompt.Calls);
        }
    }
}
