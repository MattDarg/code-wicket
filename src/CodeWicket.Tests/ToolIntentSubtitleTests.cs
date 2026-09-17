using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core;
using CodeWicket.Ipc;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The tool row's muted subtitle is the agent's stated INTENT, and the two fields it can come from
    /// are not equally trustworthy (issue #131).
    /// <para>
    /// <c>__tool_use_purpose</c> is injected by Kiro and named to avoid colliding with a tool's own
    /// arguments. <c>description</c> is injected by nobody: it is an ordinary parameter that Claude's
    /// built-in tools define with that meaning, and that any third-party MCP server is free to define
    /// with another. Reported live on Kiro v3: a Jira MCP tool takes <c>summary</c> and
    /// <c>description</c>, so a whole ticket body was rendering as the row's subtitle.
    /// </para>
    /// <para>
    /// So the rule is about WHOSE SCHEMA the arguments follow, which the host cannot work out for
    /// itself — the mapper now says, by putting the namespaced tool name on the event.
    /// </para>
    /// </summary>
    public class ToolIntentSubtitleTests
    {
        private const string JiraDescription =
            "As a user I want the export to include the audit columns so that the finance team can " +
            "reconcile without opening each record. Acceptance criteria: 1. The CSV gains CreatedBy, " +
            "CreatedUtc and ApprovedBy. 2. The columns are populated for historic rows via the " +
            "backfill job. 3. The report header documents them. See the attached spreadsheet for the " +
            "exact column order agreed with Finance on the 3rd.";

        // --- the mapper says whose schema it is ------------------------------------------------------

        private static AgentEvent[] Map(string updateJson) =>
            AcpMapper.Map(JsonDocument.Parse(updateJson).RootElement.Clone()).ToArray();

        /// <summary>An MCP call carries its namespaced name, whichever backend spelled it.</summary>
        [Theory]
        [InlineData("@atlassian/createJiraIssue")]
        [InlineData("mcp__atlassian__createJiraIssue")]
        public void AnMcpToolCallIsMarkedWithItsName(string title)
        {
            var started = Map(
                $$"""
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "j1",
                  "title": "{{title}}",
                  "rawInput": { "summary": "Add audit columns", "description": "…" }
                }
                """).OfType<AgentEvent.ToolCallStarted>().Single();

            Assert.Equal(title, started.ToolName);
        }

        /// <summary>
        /// A backend built-in carries none — which is what licenses reading its own convention. Kiro's
        /// v2 shape, where the title wears the "Running: " prefix a shell command would.
        /// </summary>
        [Fact]
        public void ABuiltInCallIsNotMarked()
        {
            var started = Map(
                """
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "c1",
                  "title": "Running: git status",
                  "kind": "execute"
                }
                """).OfType<AgentEvent.ToolCallStarted>().Single();

            Assert.Null(started.ToolName);
        }

        // --- the reported frame, verbatim ------------------------------------------------------------

        /// <summary>
        /// The captured shape from issue #133 (redacted by the reporter), which is the case this whole
        /// change exists for and the one thing my own logs could not supply: a THIRD-PARTY server, not
        /// ours. It settles that Kiro namespaces a foreign MCP tool exactly as it namespaces ours —
        /// `@atlassian-mcp/create_issue_smart`, `kind:"other"` — so the tool name resolves and the
        /// guard fires. The ticket body arrives as `description`, markdown and multi-line, alongside
        /// the tool's other ordinary arguments.
        /// </summary>
        [Fact]
        public void TheReportedJiraCallShowsNoSubtitleAndKeepsItsArguments() => RunSta(() =>
        {
            const string title = "@atlassian-mcp/create_issue_smart";
            var rawInput = JsonSerializer.Serialize(new
            {
                project_key = "CPLT",
                summary = "Redacted for privacy",
                issue_type = "Story",
                // Markdown and multi-line, as a ticket body is.
                description = @"## Summary

Redacted for privacy

## Acceptance criteria

" + JiraDescription,
            });

            // The mapper marks it from the title alone — there is no toolName field on the frame.
            var started = Map(
                $$"""
                {
                  "sessionUpdate": "tool_call",
                  "toolCallId": "tooluse_JyMLAsw6SHAEC3E23EshFf",
                  "title": "{{title}}",
                  "kind": "other"
                }
                """).OfType<AgentEvent.ToolCallStarted>().Single();
            Assert.Equal(title, started.ToolName);

            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "tooluse_JyMLAsw6SHAEC3E23EshFf", Title = title,
                Kind = "other", RawInputJson = rawInput, ToolName = title,
            });
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.False(row.HasDescription);
            // The row still names the call — by the server/tool every backend's spelling normalizes to
            // (McpToolDisplayNameTests) — and every argument is still one click away.
            Assert.Equal("atlassian-mcp/create_issue_smart", row.Title);
            Assert.Contains("project_key", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Redacted for privacy", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        // --- the row reads it ------------------------------------------------------------------------

        /// <summary>
        /// The reported bug: a Jira ticket body must not become the subtitle. The row keeps its title
        /// and shows no intent — the arguments are still there, in the expandable detail.
        /// </summary>
        [Fact]
        public void AnMcpToolsDescriptionIsNotTheAgentsIntent() => RunSta(() =>
        {
            var vm = Drive(
                toolName: "@atlassian/createJiraIssue",
                rawInput: new { summary = "Add audit columns to the export", description = JiraDescription });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.False(row.HasDescription);
            Assert.Null(row.Description);
            // Not lost, just not masquerading as intent.
            Assert.Contains("audit columns", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// Kiro's injected purpose is still trusted on an MCP call — it is the backend's own field, put
        /// there by the agent loop rather than by the tool's schema. This is the half that must NOT be
        /// lost to the fix, since MCP calls are most of what Kiro does.
        /// </summary>
        [Fact]
        public void AnInjectedPurposeIsStillTheIntentOnAnMcpCall() => RunSta(() =>
        {
            var vm = Drive(
                toolName: "@atlassian/createJiraIssue",
                rawInput: new
                {
                    summary = "Add audit columns",
                    description = JiraDescription,
                    __tool_use_purpose = "Raise the ticket the user asked for",
                });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("Raise the ticket the user asked for", row.Description);
        });

        /// <summary>
        /// A backend BUILT-IN keeps its convention: Claude's own tools define description as the human
        /// summary of the call, and that is the field this subtitle was built for.
        /// </summary>
        [Fact]
        public void ABuiltInsDescriptionIsStillTheIntent() => RunSta(() =>
        {
            var vm = Drive(toolName: null, rawInput: new { command = "dotnet build", description = "Build the solution" });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("Build the solution", row.Description);
        });

        /// <summary>
        /// The backstop, independent of whose field it is: a subtitle is one line and bounded. A wordy
        /// purpose — or a future backend's — must not take over the transcript.
        /// </summary>
        [Fact]
        public void AVeryLongIntentIsCollapsedToOneCappedLine() => RunSta(() =>
        {
            var vm = Drive(
                toolName: "@atlassian/createJiraIssue",
                rawInput: new { __tool_use_purpose = "Raise the ticket\nand link it to the epic\n" + JiraDescription });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.NotNull(row.Description);
            Assert.DoesNotContain("\n", row.Description!, StringComparison.Ordinal);
            Assert.True(row.Description!.Length <= 201, $"subtitle was {row.Description.Length} chars");
        });

        /// <summary>
        /// Claude opens a row with <c>rawInput:{}</c> and sends the real arguments on the update, so the
        /// guard has to hold on that path too — it is the one that carries the interesting payload.
        /// </summary>
        [Fact]
        public void TheGuardHoldsOnTheEnrichingUpdate() => RunSta(() =>
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);

            engine.Raise(new AgentEventDto
            {
                Type = "toolStart", ToolCallId = "j1", Title = "mcp__atlassian__createJiraIssue",
                Kind = "other", RawInputJson = "{}", ToolName = "mcp__atlassian__createJiraIssue",
            });
            engine.Raise(new AgentEventDto
            {
                Type = "toolUpdate", ToolCallId = "j1", Title = "mcp__atlassian__createJiraIssue",
                RawInputJson = JsonSerializer.Serialize(new { summary = "s", description = JiraDescription }),
                ToolName = "mcp__atlassian__createJiraIssue",
            });
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.False(row.HasDescription);
        });

        // --- and the detail shows it once, not twice -------------------------------------------------

        /// <summary>
        /// The key we lifted into the subtitle is dropped from the raw argument dump, so one value does
        /// not render twice on the same row. Kiro's injected purpose is the common case.
        /// </summary>
        [Fact]
        public void ADecodedPurposeIsNotAlsoDumpedAsAnArgument() => RunSta(() =>
        {
            var vm = Drive(
                toolName: null,
                rawInput: new Dictionary<string, object>
                {
                    ["path"] = "src/Foo.cs",
                    ["__tool_use_purpose"] = "Check the audit columns are populated",
                });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("Check the audit columns are populated", row.Description);
            // The subtitle has it; the dump must not repeat it — but every other argument survives.
            Assert.DoesNotContain("__tool_use_purpose", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("audit columns", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("path: src/Foo.cs", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// A built-in's <c>description</c> is decoded (Claude's convention), so it is suppressed too —
        /// the rule is about the key we READ, not about the double-underscore convention.
        /// </summary>
        [Fact]
        public void ADecodedDescriptionIsNotAlsoDumpedOnABuiltIn() => RunSta(() =>
        {
            var vm = Drive(
                toolName: null,
                rawInput: new { command = "git status", description = "See what changed before committing" });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("See what changed before committing", row.Description);
            Assert.DoesNotContain("before committing", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("command: git status", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// THE GUARD, and the reason suppression is keyed on decoding rather than on the key's shape: an
        /// MCP call's <c>description</c> is refused as intent (#131), so the dump is the ONLY place its
        /// value appears and it must survive there. Remove the #131 guard from
        /// <c>ResolveIntentKey</c> and this fails — the ticket body would become the subtitle AND vanish
        /// from the detail, which is strictly worse than the bug #131 fixed.
        /// </summary>
        [Fact]
        public void AnMcpToolsDescriptionSurvivesInTheDumpBecauseItWasNotDecoded() => RunSta(() =>
        {
            var vm = Drive(
                toolName: "@atlassian/createJiraIssue",
                rawInput: new { summary = "Add audit columns", description = JiraDescription });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.False(row.HasDescription);
            Assert.Contains("description:", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("audit columns", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        /// <summary>
        /// The case a blanket <c>__</c> filter would have broken: an ACP agent we have not tested yet
        /// carries its intent in a tag we do not know. We do not decode it, so it stays visible in the
        /// dump — which is exactly how someone would discover it exists.
        /// </summary>
        [Fact]
        public void AnUnrecognisedInternalTagIsNotSuppressed() => RunSta(() =>
        {
            var vm = Drive(
                toolName: null,
                rawInput: new Dictionary<string, object>
                {
                    ["path"] = "src/Foo.cs",
                    ["__agent_rationale"] = "Reading the file before editing it",
                });

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.False(row.HasDescription);
            Assert.Contains("__agent_rationale", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Reading the file before editing it", row.InputDetail ?? string.Empty, StringComparison.Ordinal);
        });

        // --- and the PROMPT reads it too, not just the row -------------------------------------------

        /// <summary>
        /// The asymmetry this closes: the intent was resolved only for an EDIT prompt, so a command or
        /// MCP prompt showed no reason while the row right beside it showed one. Restore the isEdit early
        /// return in <c>ResolveBannerContext</c> and this fails.
        /// </summary>
        [Fact]
        public void ACommandPromptShowsTheAgentsStatedReason() => RunSta(() =>
        {
            var banner = Prompt(
                command: "git push --force origin main",
                path: null,
                toolName: null,
                rawInput: new Dictionary<string, object>
                {
                    ["command"] = "git push --force origin main",
                    ["__tool_use_purpose"] = "Publish the rebased branch",
                });

            Assert.True(banner.HasIntent);
            Assert.Equal("Publish the rebased branch", banner.Intent);
        });

        /// <summary>
        /// An MCP prompt too — and this is the case that matters most in practice, since every IDE tool we
        /// expose reaches the agent as MCP, so build_solution and run_tests prompt through this path.
        /// </summary>
        [Fact]
        public void AnMcpToolPromptShowsAnInjectedPurpose() => RunSta(() =>
        {
            var banner = Prompt(
                command: null,
                path: null,
                toolName: "@code-wicket/run_tests",
                rawInput: new Dictionary<string, object>
                {
                    ["filterClass"] = "OrderTests",
                    ["__tool_use_purpose"] = "Confirm the discount fix did not break checkout",
                });

            Assert.True(banner.HasIntent);
            Assert.Equal("Confirm the discount fix did not break checkout", banner.Intent);
        });

        /// <summary>
        /// #131's guard binds the prompt as well, and more strictly: a third party's <c>description</c>
        /// rendering as the reason next to Allow would be actively misleading, not merely noisy. The
        /// arguments still reach the user through the banner's own detail box.
        /// </summary>
        [Fact]
        public void AnMcpToolPromptDoesNotReadItsDescriptionAsAReason() => RunSta(() =>
        {
            var banner = Prompt(
                command: null,
                path: null,
                toolName: "@atlassian/createJiraIssue",
                rawInput: new { summary = "Add audit columns", description = JiraDescription });

            Assert.False(banner.HasIntent);
            Assert.Null(banner.Intent);
        });

        /// <summary>The edit prompt it was originally built for keeps working.</summary>
        [Fact]
        public void AnEditPromptStillShowsItsReason() => RunSta(() =>
        {
            var banner = Prompt(
                command: null,
                path: @"C:\ws\src\Foo.cs",
                toolName: null,
                rawInput: new { path = @"C:\ws\src\Foo.cs", description = "Add the null guard" });

            Assert.True(banner.HasIntent);
            Assert.Equal("Add the null guard", banner.Intent);
        });

        // --- harness ---------------------------------------------------------------------------------

        // Raises a permission request through the view-model and returns the banner it put up. The task is
        // deliberately not awaited: it completes only when the user answers, which is the point of a prompt.
        private static PermissionBannerViewModel Prompt(
            string? command, string? path, string? toolName, object rawInput)
        {
            var vm = NewViewModel(new StubEngine());
            _ = vm.RequestPermissionAsync(new PermissionRequestDto(
                "t1",
                toolName ?? command ?? "Edit file",
                command is null ? "edit" : "execute",
                JsonSerializer.Serialize(rawInput),
                command,
                new[] { new Ipc.PermissionOptionDto("allow_once", "Allow", "AllowOnce") },
                ToolName: toolName,
                Path: path));
            DrainDispatcher();
            return Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
        }


        private static ChatViewModel Drive(string? toolName, object rawInput)
        {
            var engine = new StubEngine();
            var vm = NewViewModel(engine);
            engine.Raise(new AgentEventDto
            {
                Type = "toolStart",
                ToolCallId = "t1",
                Title = toolName ?? "Running: dotnet build",
                Kind = "other",
                RawInputJson = JsonSerializer.Serialize(rawInput),
                ToolName = toolName,
            });
            DrainDispatcher();
            return vm;
        }

        private static ChatViewModel NewViewModel(StubEngine engine) => new(
            engine, new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null));

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        private sealed class StubEngine : IEngineConnection
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

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never open a session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
