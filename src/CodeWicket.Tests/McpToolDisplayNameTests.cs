using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodeWicket.Core.Ide;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// One MCP call is SHOWN by one name on every backend — the canonical <c>server/tool</c> its saved
    /// rule carries — with the backend's own spelling kept in the detail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backends name one call three ways: Claude <c>mcp__code-wicket__build_solution</c>, Kiro v2
    /// <c>Running: @code-wicket/build_solution</c> (the prefix a shell command wears), Kiro v3
    /// <c>@code_wicket/build_solution</c>. The settings list names it a fourth, so a row could not be
    /// matched to the rule that approved it by reading either.
    /// </para>
    /// <para>
    /// <b>The half that must not be lost</b>: a shell command can be titled with one of our tools' names
    /// (pre-release security review, September 2026), and the EVENT path lifts that name out of the title with no shell check, so
    /// a borrowed name is on the event. A call carrying a shell fact — a command kind, or a
    /// <c>command</c> argument — therefore keeps the backend's title, on every frame after it too.
    /// </para>
    /// </remarks>
    public sealed class McpToolDisplayNameTests
    {
        private const string Shown = "code-wicket/build_solution";

        // ---- the name ------------------------------------------------------------------------------------

        [Theory]
        [InlineData("mcp__code-wicket__build_solution")] // Claude
        [InlineData("@code-wicket/build_solution")]      // Kiro v2
        [InlineData("@code_wicket/build_solution")]      // Kiro v3
        [InlineData("mcp__code_wicket__build_solution")]
        public void EveryBackendsSpellingOfOurToolIsOneName(string raw)
        {
            Assert.True(IdeMcpServer.TryDisplayName(raw, out var shown));
            Assert.Equal(Shown, shown);
        }

        [Theory]
        [InlineData("@atlassian-mcp/create_issue_smart", "atlassian-mcp/create_issue_smart")]
        [InlineData("mcp__atlassian__createJiraIssue", "atlassian/createJiraIssue")]
        public void AnotherServersToolKeepsItsServer(string raw, string expected)
        {
            Assert.True(IdeMcpServer.TryDisplayName(raw, out var shown));
            Assert.Equal(expected, shown);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("build_solution")]
        [InlineData("Read File")]
        [InlineData("Running: @code-wicket/build_solution")]
        public void ANameThatIsNotABackendsNamespacedNameIsNotRenamed(string? raw) =>
            Assert.False(IdeMcpServer.TryDisplayName(raw, out _));

        /// <summary>The point of one definition: the name on the row is the text the user saves.</summary>
        [Theory]
        [InlineData("mcp__code-wicket__build_solution")]
        [InlineData("@code_wicket/build_solution")]
        [InlineData("@atlassian-mcp/create_issue_smart")]
        [InlineData("mcp__atlassian__createJiraIssue")]
        public void TheShownNameIsTheSavedRule(string raw)
        {
            Assert.True(IdeMcpServer.TryDisplayName(raw, out var shown));
            Assert.True(IdeMcpServer.TryNormalizeToolRule(raw, out var rule));
            Assert.Equal(rule, shown);
        }

        // ---- the row -------------------------------------------------------------------------------------

        [Theory]
        [InlineData("mcp__code-wicket__build_solution", "mcp__code-wicket__build_solution")]
        [InlineData("Running: @code-wicket/build_solution", "@code-wicket/build_solution")]
        [InlineData("@code_wicket/build_solution", "@code_wicket/build_solution")]
        public void TheRowShowsOneNameAndKeepsTheBackendsInItsDetail(string title, string toolName) => RunSta(() =>
        {
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("t1", title, toolName, kind: null, rawInput: "{\"scope\":\"solution\"}"));
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal(Shown, row.Title);
            Assert.Equal(toolName, row.RawToolName);
            Assert.StartsWith("tool: " + toolName + "\n", row.Detail, StringComparison.Ordinal);
            Assert.Contains("scope: solution", row.Detail, StringComparison.Ordinal);
            // The icon and the tag follow the same name: an MCP call reads as one at a glance.
            Assert.True(row.IsMcpCall);
            Assert.Equal("mcp", row.KindLabel);
        });

        /// <summary>A call with no arguments had no detail at all; it now expands to the backend's name.</summary>
        [Fact]
        public void ARowWithNoArgumentsStillExpandsToTheBackendsName() => RunSta(() =>
        {
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("t1", "@code-wicket/build_solution", "@code-wicket/build_solution", kind: null, rawInput: null));
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.True(row.HasDetail);
            Assert.Equal("tool: @code-wicket/build_solution", row.Detail);
        });

        [Fact]
        public void ABuiltInKeepsItsTitle() => RunSta(() =>
        {
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("r1", "Read File", toolName: null, kind: "read", rawInput: "{\"path\":\"a.cs\"}"));
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("Read File", row.Title);
            Assert.Null(row.RawToolName);
            Assert.False(row.IsMcpCall);
            Assert.DoesNotContain("tool:", row.Detail, StringComparison.Ordinal);
            Assert.Equal("read", row.KindLabel);
        });

        /// <summary>
        /// THE GUARD. A shell command titled with our tool's name, carrying the name the event path lifted
        /// from that title, keeps the backend's title — renaming it would dress the command as our tool.
        /// Both shell facts: the kind alone, and a command argument alone (Claude's PowerShell tool).
        /// </summary>
        [Theory]
        [InlineData("execute", null)]
        [InlineData("other", "{\"command\":\"@code-wicket/get_diagnostics\"}")]
        public void AShellCommandWearingOurToolsNameKeepsItsOwnTitle(string kind, string? rawInput) => RunSta(() =>
        {
            const string title = "Running: @code-wicket/get_diagnostics";
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("c1", title, "@code-wicket/get_diagnostics", kind, rawInput));
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal(title, row.Title);
            Assert.Null(row.RawToolName);
            Assert.False(row.IsMcpCall);
        });

        /// <summary>
        /// A shell fact that arrives LATER undoes a name an earlier frame earned, and keeps it undone:
        /// a following frame that names the tool again with no fact on it does not rename the row.
        /// </summary>
        [Fact]
        public void AShellFactOnALaterFrameIsPermanent() => RunSta(() =>
        {
            const string title = "Running: @code-wicket/get_diagnostics";
            const string name = "@code-wicket/get_diagnostics";
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("c1", title, name, kind: null, rawInput: null));
            engine.Raise(new AgentEventDto { Type = "toolUpdate", ToolCallId = "c1", Kind = "execute" });
            engine.Raise(new AgentEventDto { Type = "toolUpdate", ToolCallId = "c1", Title = title, ToolName = name, RawInputJson = "{}" });
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal(title, row.Title);
            Assert.Null(row.RawToolName);
            Assert.False(row.IsMcpCall);
        });

        /// <summary>Claude opens a row with a placeholder title and names the tool only on the update.</summary>
        [Fact]
        public void APlaceholderRowTakesTheNameWhenAnUpdateBringsIt() => RunSta(() =>
        {
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("t1", "Tool", toolName: null, kind: "other", rawInput: "{}"));
            engine.Raise(new AgentEventDto
            {
                Type = "toolUpdate", ToolCallId = "t1", Title = "mcp__code-wicket__run_tests",
                ToolName = "mcp__code-wicket__run_tests", RawInputJson = "{\"filterClass\":\"OrderTests\"}",
            });
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal("code-wicket/run_tests", row.Title);
            Assert.Equal("mcp__code-wicket__run_tests", row.RawToolName);
        });

        /// <summary>An update that names no tool must not put the backend's spelling back on a named row.</summary>
        [Fact]
        public void AnUpdateThatNamesNoToolLeavesTheNameAlone() => RunSta(() =>
        {
            var (vm, engine) = NewViewModel();
            engine.Raise(Start("t1", "@code-wicket/build_solution", "@code-wicket/build_solution", kind: null, rawInput: null));
            engine.Raise(new AgentEventDto { Type = "toolUpdate", ToolCallId = "t1", Title = "@code-wicket/build_solution", RawInputJson = "{\"rebuild\":true}" });
            DrainDispatcher();

            var row = Assert.Single(vm.Items.OfType<ToolItemViewModel>());
            Assert.Equal(Shown, row.Title);
            Assert.Equal("@code-wicket/build_solution", row.RawToolName);
        });

        // ---- the banner ----------------------------------------------------------------------------------

        [Fact]
        public void ABannerTitlesAToolRequestByTheRuleItWouldSave() => RunSta(() =>
        {
            const string raw = "mcp__code-wicket__run_tests";
            var banner = Prompt(new PermissionRequestDto(
                "t1", raw, null, "{\"filterClass\":\"OrderTests\"}", null, Options(), ToolName: raw));

            Assert.Equal("code-wicket/run_tests", banner.Title);
            Assert.Equal(raw, banner.RawToolName);
            Assert.StartsWith("tool: " + raw + "\n\n", banner.DisplayDetail, StringComparison.Ordinal);

            banner.Options.Single(o => o.Kind == "AllowAlways").Command.Execute(null);
            Assert.Equal(banner.Title, banner.Pattern);
        });

        /// <summary>A command request's title is the model's text and is never renamed.</summary>
        [Fact]
        public void ABannerForACommandKeepsItsTitle() => RunSta(() =>
        {
            const string title = "Running: @code-wicket/get_diagnostics";
            var banner = Prompt(new PermissionRequestDto(
                "c1", title, "execute", null, "@code-wicket/get_diagnostics", Options()));

            Assert.Equal(title, banner.Title);
            Assert.Null(banner.RawToolName);
            Assert.Equal("@code-wicket/get_diagnostics", banner.DisplayDetail);
        });

        // ---- harness -------------------------------------------------------------------------------------

        private static AgentEventDto Start(string id, string title, string? toolName, string? kind, string? rawInput) => new()
        {
            Type = "toolStart", ToolCallId = id, Title = title, ToolName = toolName, Kind = kind, RawInputJson = rawInput,
        };

        private static PermissionOptionDto[] Options() => new[]
        {
            new PermissionOptionDto("allow_once", "Allow", "AllowOnce"),
            new PermissionOptionDto("allow_always", "Allow always", "AllowAlways"),
        };

        // The task is deliberately not awaited: it completes only when the user answers.
        private static PermissionBannerViewModel Prompt(PermissionRequestDto request)
        {
            var (vm, _) = NewViewModel();
            _ = vm.RequestPermissionAsync(request);
            DrainDispatcher();
            return Assert.IsType<PermissionBannerViewModel>(vm.PendingPermission);
        }

        private static (ChatViewModel vm, StubEngine engine) NewViewModel()
        {
            var engine = new StubEngine();
            return (new ChatViewModel(engine, new StartSessionRequest("fake", null, AppContext.BaseDirectory, "Prompt", null)), engine);
        }

        private static void DrainDispatcher() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // One shared, GATED implementation - see StaTest.
        private static void RunSta(Action action) => StaTest.Run(action);

        private sealed class StubEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent;

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            public void Raise(AgentEventDto ev) => AgentEvent?.Invoke(ev);

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
                => throw new NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("These tests never summarize.");
        }
    }
}
