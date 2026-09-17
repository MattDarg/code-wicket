using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Ipc;

namespace CodeWicket.Engine
{
    /// <summary>
    /// Wires an <see cref="EngineService"/> to a JSON-RPC connection over a pair of streams
    /// (the shell's process pipe in production, or an in-memory duplex stream in tests).
    /// </summary>
    public sealed class EngineHost : IAsyncDisposable
    {
        private readonly JsonRpc _rpc;
        private readonly EngineService _service;

        public EngineHost(Stream sending, Stream receiving, AgentProviderRegistry registry, string? defaultProviderId = null)
        {
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                },
            };

            var handler = new NewLineDelimitedMessageHandler(sending, receiving, formatter);
            _rpc = new JsonRpc(handler);
            _service = new EngineService(registry, defaultProviderId) { Rpc = _rpc };
            _rpc.AddLocalRpcTarget(_service, new JsonRpcTargetOptions());
        }

        /// <summary>Completes when the connection closes.</summary>
        public Task Completion => _rpc.Completion;

        public void Start() => _rpc.StartListening();

        /// <summary>
        /// Pushes a backend's freshly discovered model list to the shell. Notifications are legal on
        /// this connection at any time and this one belongs to no session, which is the point: the
        /// discovery it reports happens while the window is opening, long before any session exists.
        /// Fire-and-forget — a picker that keeps showing last run's list is the state we deliberately
        /// made acceptable, so a failed push must not surface as anything.
        /// </summary>
        public void NotifyProviderModels(string providerId, IReadOnlyList<ModelInfo> models)
        {
            try
            {
                _ = _rpc.NotifyWithParameterObjectAsync(
                    RpcMethods.OnProviderModels,
                    new ProviderModelsDto(
                        providerId,
                        models.Select(m => new ModelInfoDto(m.Id, m.DisplayName, m.RateMultiplier)).ToList()));
            }
            catch { /* connection gone or closing */ }
        }

        public async ValueTask DisposeAsync()
        {
            _rpc.Dispose();
            await _service.DisposeAsync().ConfigureAwait(false);
        }
    }
}
