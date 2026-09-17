using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeWicket.Engine;
using CodeWicket.Ipc;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The <c>engine/listBackendSessions</c> hop (issue #108): a real JSON-RPC round trip over an
    /// in-memory pair, so the request DTO, the provider resolution, the mapping and the response DTO
    /// are exercised together rather than each asserted in isolation.
    /// <para><b>Why a round trip rather than calling the method directly:</b> the two things most
    /// likely to break here are invisible to a direct call. A DTO that does not round-trip through
    /// StreamJsonRpc's formatter comes back with default fields and no error anywhere, and the engine
    /// takes its parameters as a single object — a mismatch there produces a method-not-found rather
    /// than a wrong answer. Both are transport facts, so the transport has to be in the test.</para>
    /// <para>The fake provider is what makes this offline at all: it is the only backend that can
    /// answer a listing with no CLI installed, which is what separates a test of the ENGINE hop from a
    /// test of the ACP half beside it.</para>
    /// </summary>
    public class BackendSessionListRpcTests
    {
        [Fact]
        public async Task AListingCrossesTheWireWithEveryFieldIntact()
        {
            using var harness = Harness.Start(new FakeAgentProvider());

            var response = await harness.ListAsync(new ListBackendSessionsRequest("fake", @"C:\ws"));

            Assert.True(response.Supported);
            Assert.Null(response.Reason);
            Assert.Equal(5, response.Sessions.Count);

            // Distinct ids, distinct titles: a mapping that collapsed entries would still return two
            // rows, so identity is what the assertion has to be on.
            Assert.Equal("fake-session-recent", response.Sessions[0].Id);
            Assert.Equal("fake-session-older", response.Sessions[1].Id);
            Assert.NotEqual(response.Sessions[0].Title, response.Sessions[1].Title);

            // The third repeats the first's title on purpose - the real backends do - so the mapping
            // must carry it through rather than treating a repeated title as a duplicate row. Identity
            // is what separates them, and it is the only thing that can.
            Assert.Equal("fake-session-twin", response.Sessions[2].Id);
            Assert.Equal(response.Sessions[0].Title, response.Sessions[2].Title);
            Assert.NotEqual(response.Sessions[0].Id, response.Sessions[2].Id);
            Assert.All(response.Sessions, s => Assert.False(string.IsNullOrEmpty(s.Title)));

            // CreatedUtc crosses too, and it is the field most likely to be forgotten: it is on the
            // Core record and on the DTO, and a mapping that never assigned it would leave it null on
            // every row - at which point the unused-conversation filter silently stops filtering, with
            // nothing failing and a picker that merely looks like it did before. Three places, and this
            // is the third.
            var unused = response.Sessions.Single(s => s.Id == "fake-session-unused");
            Assert.NotNull(unused.CreatedUtc);
            Assert.Equal(DateTimeKind.Utc, unused.CreatedUtc!.Value.Kind);
            Assert.True(unused.UpdatedUtc - unused.CreatedUtc < TimeSpan.FromSeconds(1));

            // ...and stays null where the backend reported none, rather than being defaulted into a
            // value the filter would then act on.
            Assert.Null(response.Sessions.Single(s => s.Id == "fake-session-recent").CreatedUtc);
        }

        [Fact]
        public async Task AReportedTimeCrossesAsUtc()
        {
            // The load-bearing mapping rule. Core carries a DateTimeOffset and the DTO a DateTime, so
            // the offset has to be resolved HERE, where it is still known. Read `.DateTime` instead of
            // `.UtcDateTime` and this goes red on any machine not on UTC — while every other assertion
            // in the file still passes, because the value is present and merely hours wrong.
            var info = new BackendSessionInfo(
                "s1", "t", new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.FromHours(5)), null);

            var dto = DtoMapping.ToDto(info);

            Assert.Equal(new DateTime(2026, 8, 25, 4, 0, 0, DateTimeKind.Utc), dto.UpdatedUtc);
            Assert.Equal(DateTimeKind.Utc, dto.UpdatedUtc!.Value.Kind);
        }

        [Fact]
        public void AnUnreportedTimeStaysNull()
        {
            // A backend that does not track a time has not reported one, and the host must not order a
            // picker on a value we invented. DateTime.MinValue here would sort every such row last and
            // look deliberate.
            var dto = DtoMapping.ToDto(new BackendSessionInfo("s1", null, null, null));

            Assert.Null(dto.UpdatedUtc);
            Assert.Null(dto.Title);
            Assert.Null(dto.Cwd);
        }

        [Fact]
        public async Task ABackendWithNoStoreIsReportedRatherThanThrown()
        {
            // The caller is a picker, so an unsupported backend has to come back as a sentence to show
            // under an empty list. Throwing here would surface as a broken popup for the ordinary case
            // of a backend that simply keeps no CLI history.
            using var harness = Harness.Start(new StorelessProvider());

            var response = await harness.ListAsync(new ListBackendSessionsRequest("storeless", @"C:\ws"));

            Assert.False(response.Supported);
            Assert.Empty(response.Sessions);

            // The reason must name the backend: "cannot list" with no subject tells a user nothing when
            // several backends are configured.
            Assert.Contains("Storeless", response.Reason);
        }

        [Fact]
        public async Task AnIdleSessionAnswersTheListingForItsOwnRoot()
        {
            // The optimisation this whole path exists for: a cold listing spawns a CLI, handshakes and
            // disposes (3-5s), and an open session has already paid that. Asserted first, so the refusal
            // below is a refusal rather than a reuse that never worked in the first place.
            using var harness = Harness.Start(new WarmSessionProvider());
            await harness.StartSessionAsync("warm", @"C:\ws\solution");

            var response = await harness.ListAsync(new ListBackendSessionsRequest("warm", @"C:\ws\solution"));

            Assert.Equal("from-the-live-session", Assert.Single(response.Sessions).Id);
        }

        [Fact]
        public async Task ASessionRootedElsewhereIsNotReusedToAnswerTheListing()
        {
            // Measured 2026-08-28. A session's working directory is captured when its process launches
            // and cannot move afterwards, so a warm start that ran before the solution finished loading
            // stays rooted at the default workspace for its whole life. Every backend keys its
            // conversation store by that directory, so reusing the session then reads a DIFFERENT store
            // — and for a root nobody has worked in the answer is an empty list, which a picker cannot
            // tell from "you have no stored conversations". The section reported nothing for two hours
            // forty minutes and was cured only by restarting the tool window.
            using var harness = Harness.Start(new WarmSessionProvider());
            await harness.StartSessionAsync("warm", @"C:\ws\default-workspace");

            var response = await harness.ListAsync(new ListBackendSessionsRequest("warm", @"C:\ws\solution"));

            // The cold listing ran instead: this is the provider's store, not the session's answer.
            Assert.Equal("from-a-cold-listing", Assert.Single(response.Sessions).Id);
            Assert.True(response.Supported);
        }

        [Fact]
        public async Task AnUnsupportedBackendIsNotAnEmptyList()
        {
            // The distinction the whole response shape exists to carry. Both of these have zero
            // sessions, and collapsing them is the same mistake as reading a null usage figure as zero
            // — one says "this backend cannot tell you", the other "there is nothing here".
            using var storeless = Harness.Start(new StorelessProvider());
            using var empty = Harness.Start(new EmptyStoreProvider());

            var unsupported = await storeless.ListAsync(new ListBackendSessionsRequest("storeless", @"C:\ws"));
            var nothingStored = await empty.ListAsync(new ListBackendSessionsRequest("empty", @"C:\ws"));

            Assert.Empty(unsupported.Sessions);
            Assert.Empty(nothingStored.Sessions);
            Assert.NotEqual(unsupported.Supported, nothingStored.Supported);
            Assert.Null(nothingStored.Reason);
        }

        [Fact]
        public async Task AnUnknownProviderIsAHostBugAndThrows()
        {
            // The one case that is NOT a backend state: a provider id naming nothing at all means the
            // host asked about something it never had, and reporting that as "unsupported" would hide
            // it behind a sentence the user cannot act on.
            using var harness = Harness.Start(new FakeAgentProvider());

            await Assert.ThrowsAsync<RemoteInvocationException>(
                () => harness.ListAsync(new ListBackendSessionsRequest("nobody", @"C:\ws")));
        }

        // A backend that drives sessions but keeps no CLI history — the fake's opposite, and the
        // ordinary case for an in-process provider.
        private sealed class StorelessProvider : IAgentProvider
        {
            public string ProviderId => "storeless";
            public string DisplayName => "Storeless backend";
            public IReadOnlyList<ModelInfo> Models { get; } = Array.Empty<ModelInfo>();
            public AgentCapabilities Capabilities => AgentCapabilities.None;

            public Task<IAgentSession> StartSessionAsync(
                SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Not started in these tests.");
        }

        // Has a store, and it is empty. Distinct from the above in exactly the way the response has to
        // report — see AnUnsupportedBackendIsNotAnEmptyList.
        private sealed class EmptyStoreProvider : IAgentProvider, IBackendSessionCatalog
        {
            public string ProviderId => "empty";
            public string DisplayName => "Empty store";
            public IReadOnlyList<ModelInfo> Models { get; } = Array.Empty<ModelInfo>();
            public AgentCapabilities Capabilities => AgentCapabilities.None;

            public Task<IAgentSession> StartSessionAsync(
                SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Not started in these tests.");

            public Task<BackendSessionListResult> ListBackendSessionsAsync(
                string workspaceRootPath, AgentWorkspaceScope scope, IIdeServices ide,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(BackendSessionListResult.Found(Array.Empty<BackendSessionInfo>()));

            public WorkspaceRootResult ResolveListingRoot(string workspaceRootPath, AgentWorkspaceScope scope) =>
                new(workspaceRootPath, false, null, "test stub");
        }

        /// <summary>
        /// A backend whose open session can answer a listing — the only shape in which the engine's
        /// reuse decision is reachable at all.
        /// <para>Its two stores answer with DIFFERENT ids on purpose. A double whose warm and cold
        /// paths return the same rows makes "the session answered" and "a CLI was spawned" the same
        /// observation, and a check that cannot tell them apart pins neither — which is how a session
        /// rooted in the wrong directory came to answer for two and a half hours with nothing
        /// noticing.</para>
        /// <para>Kept here rather than folded into <c>FakeAgentProvider</c>: the fake is what the
        /// Desktop <c>--smoke</c> drives, and it opens a session before it browses. A fake that could
        /// answer warm would silently move every CLI-pickup check onto the warm path.</para>
        /// </summary>
        private sealed class WarmSessionProvider : IAgentProvider, IBackendSessionCatalog
        {
            public string ProviderId => "warm";
            public string DisplayName => "Warm backend";
            public IReadOnlyList<ModelInfo> Models { get; } = Array.Empty<ModelInfo>();
            public AgentCapabilities Capabilities => AgentCapabilities.None;

            public Task<IAgentSession> StartSessionAsync(
                SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default) =>
                Task.FromResult<IAgentSession>(new Session(options.WorkspaceRootPath));

            public Task<BackendSessionListResult> ListBackendSessionsAsync(
                string workspaceRootPath, AgentWorkspaceScope scope, IIdeServices ide,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(BackendSessionListResult.Found(new[]
                {
                    new BackendSessionInfo(
                        "from-a-cold-listing", "Cold", DateTimeOffset.UtcNow, workspaceRootPath),
                }));

            public WorkspaceRootResult ResolveListingRoot(string workspaceRootPath, AgentWorkspaceScope scope) =>
                new(workspaceRootPath, false, null, "asked where it was told to");

            private sealed class Session : IAgentSession, IBackendSessionList, IWorkspaceRootReport
            {
                public Session(string workspaceRootPath) =>
                    // Captured at construction and never updated, exactly as a real backend's is: the
                    // CLI process is launched with a working directory and cannot be moved afterwards.
                    WorkspaceRoot = new WorkspaceRootResult(
                        workspaceRootPath, false, null, "launched here");

                public WorkspaceRootResult WorkspaceRoot { get; }

                public string ConversationId => "warm-conv";

                public Task<IReadOnlyList<BackendSessionInfo>> ListSessionsAsync(
                    string? cwd, CancellationToken cancellationToken = default) =>
                    Task.FromResult<IReadOnlyList<BackendSessionInfo>>(new[]
                    {
                        new BackendSessionInfo(
                            "from-the-live-session", "Warm", DateTimeOffset.UtcNow, cwd),
                    });

                public IAsyncEnumerable<AgentEvent> SendAsync(
                    PromptInput prompt, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException("These tests never prompt.");

                public Task CancelAsync() => Task.CompletedTask;

                public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) =>
                    Task.CompletedTask;

                public Task<SteerOutcome> SteerAsync(
                    PromptInput prompt, CancellationToken cancellationToken = default) =>
                    Task.FromResult(SteerOutcome.NotSupported);

                public ValueTask DisposeAsync() => default;
            }
        }

        /// <summary>
        /// An engine and a shell either end of an in-memory duplex pair. The shell half answers
        /// <c>shell/tools/list</c> and nothing else — that is the one callback
        /// <c>IpcIdeServices.CreateAsync</c> makes, and without it the cold listing path deadlocks
        /// rather than failing, which is a confusing way for a test to go wrong.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            private readonly JsonRpc _shellRpc;
            private readonly JsonRpc _engineRpc;
            private readonly EngineService _engine;

            private Harness(JsonRpc shellRpc, JsonRpc engineRpc, EngineService engine)
            {
                _shellRpc = shellRpc;
                _engineRpc = engineRpc;
                _engine = engine;
            }

            public static Harness Start(IAgentProvider provider)
            {
                var registry = new AgentProviderRegistry();
                registry.Register(provider);

                var (shellEnd, engineEnd) = FullDuplexStream.CreatePair();

                var shellRpc = new JsonRpc(new NewLineDelimitedMessageHandler(shellEnd, shellEnd, NewFormatter()));
                shellRpc.AddLocalRpcTarget(new ToolsOnlyShell(), new JsonRpcTargetOptions());

                var engineService = new EngineService(registry);
                var engineRpc = new JsonRpc(new NewLineDelimitedMessageHandler(engineEnd, engineEnd, NewFormatter()));
                engineRpc.AddLocalRpcTarget(engineService, new JsonRpcTargetOptions());
                engineService.Rpc = engineRpc;

                engineRpc.StartListening();
                shellRpc.StartListening();

                return new Harness(shellRpc, engineRpc, engineService);
            }

            public Task<ListBackendSessionsResponse> ListAsync(ListBackendSessionsRequest request) =>
                _shellRpc.InvokeWithParameterObjectAsync<ListBackendSessionsResponse>(
                    RpcMethods.ListBackendSessions, request);

            /// <summary>Opens a live session at <paramref name="workspaceRootPath"/>, so the listing has
            /// an idle connection it might reuse - and a root it has to check before it does.</summary>
            public Task<StartSessionResponse> StartSessionAsync(string providerId, string workspaceRootPath) =>
                _shellRpc.InvokeWithParameterObjectAsync<StartSessionResponse>(
                    RpcMethods.StartSession,
                    new StartSessionRequest(providerId, null, workspaceRootPath, "Prompt", null));

            public void Dispose()
            {
                _shellRpc.Dispose();
                _engineRpc.Dispose();
                _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            // Mirrors EngineClient's formatter: web defaults plus WhenWritingNull, which is what lets
            // one record serve an optional field rather than emitting an explicit null for it.
            private static SystemTextJsonFormatter NewFormatter() => new()
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };

            private sealed class ToolsOnlyShell
            {
                [JsonRpcMethod(RpcMethods.ToolsList)]
                public ToolListResponse ToolsList() => new(Array.Empty<ToolDescriptorDto>());
            }
        }
    }
}
