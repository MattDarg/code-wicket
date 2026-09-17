using System;
using System.Collections.Generic;
using System.IO;
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
    /// "Open it where its history lives" (issue #185) across the shell↔engine boundary: the pinned
    /// directory rides the start request, the engine's cross-root guard compares against IT rather
    /// than against where the marker walk would run the agent, and it reaches the provider as
    /// <see cref="SessionOptions.PinnedWorkingDirectory"/>. Over a real hop for the usual reason — a
    /// DTO field that does not survive the formatter comes back null and reads as "no pin", which is
    /// exactly the old behaviour, so the regression is a believable absence.
    /// </summary>
    public sealed class PinnedWorkingDirectoryRpcTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "cwkt-pin-rpc-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort */ }
        }

        private string Dir(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact]
        public async Task APinnedReloadIsNotRefusedAndTheProviderIsToldToRunThere()
        {
            var home = Dir("home");
            var provider = new CatalogProvider(listingRoot: Dir("elsewhere"));
            using var harness = Harness.Start(provider);

            var start = await harness.StartAsync(new StartSessionRequest(
                "catalog", null, home, "Prompt", "conv-1",
                ResumeWorkingDirectory: home, PinnedWorkingDirectory: home));

            Assert.Null(start.ResumeFailureReason);
            Assert.Equal("conv-1", provider.LastOptions!.ResumeConversationId);
            Assert.Equal(home, provider.LastOptions.PinnedWorkingDirectory);
        }

        /// <summary>The control: the same request without the pin is refused exactly as #183 refused it.</summary>
        [Fact]
        public async Task WithoutThePinTheGuardRefusesAsBefore()
        {
            var home = Dir("home");
            var provider = new CatalogProvider(listingRoot: Dir("elsewhere"));
            using var harness = Harness.Start(provider);

            var start = await harness.StartAsync(new StartSessionRequest(
                "catalog", null, home, "Prompt", "conv-1", ResumeWorkingDirectory: home));

            Assert.Contains("different working directory", start.ResumeFailureReason!, StringComparison.Ordinal);
            Assert.Null(provider.LastOptions!.ResumeConversationId);
            Assert.Null(provider.LastOptions.PinnedWorkingDirectory);
        }

        /// <summary>
        /// A pin naming a directory that no longer exists is dropped rather than obeyed, and the
        /// guard then answers the cross-root load the way it always did — so a stale pin degrades to
        /// the #268 banner, never to a session in a directory that is not there.
        /// </summary>
        [Fact]
        public async Task AStalePinIsDroppedAndTheGuardAnswers()
        {
            var home = Dir("home");
            var provider = new CatalogProvider(listingRoot: Dir("elsewhere"));
            using var harness = Harness.Start(provider);

            var start = await harness.StartAsync(new StartSessionRequest(
                "catalog", null, home, "Prompt", "conv-1",
                ResumeWorkingDirectory: home, PinnedWorkingDirectory: Path.Combine(_root, "gone")));

            Assert.Contains("different working directory", start.ResumeFailureReason!, StringComparison.Ordinal);
            Assert.Null(provider.LastOptions!.PinnedWorkingDirectory);
        }

        /// <summary>A provider whose listing root is fixed elsewhere, recording what it was asked to start.</summary>
        private sealed class CatalogProvider : IAgentProvider, IBackendSessionCatalog
        {
            private readonly string _listingRoot;

            public CatalogProvider(string listingRoot) => _listingRoot = listingRoot;

            public SessionOptions? LastOptions { get; private set; }

            public string ProviderId => "catalog";
            public string DisplayName => "Catalog backend";
            public IReadOnlyList<ModelInfo> Models { get; } = Array.Empty<ModelInfo>();
            public AgentCapabilities Capabilities => AgentCapabilities.ResumeSession;

            public Task<IAgentSession> StartSessionAsync(
                SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default)
            {
                LastOptions = options;
                return Task.FromResult<IAgentSession>(new Session(options.ResumeConversationId ?? "conv-new"));
            }

            public Task<BackendSessionListResult> ListBackendSessionsAsync(
                string workspaceRootPath, AgentWorkspaceScope scope, IIdeServices ide,
                CancellationToken cancellationToken = default)
                => Task.FromResult(BackendSessionListResult.Unavailable("not in this test"));

            public WorkspaceRootResult ResolveListingRoot(string workspaceRootPath, AgentWorkspaceScope scope)
                => new(_listingRoot, true, null, "test marker");

            private sealed class Session : IAgentSession, IResumeFallbackReport
            {
                public Session(string id) => ConversationId = id;

                public string ConversationId { get; }
                public string? ResumeFailureReason => null;
                public int? ReplayedHistoryCount => 1;

                public IAsyncEnumerable<AgentEvent> SendAsync(
                    PromptInput prompt, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException("These tests never prompt.");

                public Task CancelAsync() => Task.CompletedTask;
                public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

                public Task<SteerOutcome> SteerAsync(
                    PromptInput prompt, CancellationToken cancellationToken = default) =>
                    Task.FromResult(SteerOutcome.NotSupported);

                public ValueTask DisposeAsync() => default;
            }
        }

        /// <summary>The same in-memory engine↔shell pair <c>ImportedHistoryRpcTests</c> uses.</summary>
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

            public Task<StartSessionResponse> StartAsync(StartSessionRequest request) =>
                _shellRpc.InvokeWithParameterObjectAsync<StartSessionResponse>(
                    RpcMethods.StartSession, request);

            public void Dispose()
            {
                _shellRpc.Dispose();
                _engineRpc.Dispose();
                _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

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
