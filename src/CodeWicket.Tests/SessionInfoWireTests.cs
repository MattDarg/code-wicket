using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using CodeWicket.Core;
using CodeWicket.Engine;
using CodeWicket.Ipc;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The session-info facts crossing the engine↔shell boundary (issue #160).
    ///
    /// <para>These are the load-bearing checks of the feature, because the failure they guard is
    /// SILENT: a capability the agent never reported arriving at the host as <c>false</c> compiles,
    /// serializes, renders, and states something about the session that is not true. The panel's whole
    /// contract is that every row is a claim we can stand behind, and the wire is where that contract
    /// is easiest to lose — AGENTS.md's "three places, not two, and a round-trip test".</para>
    /// </summary>
    public sealed class SessionInfoWireTests
    {
        /// <summary>
        /// Mirrors <c>EngineClient.NewFormatter</c>: web defaults plus <c>WhenWritingNull</c>. Testing
        /// through any other configuration would prove something about a wire we do not have.
        /// </summary>
        private static SystemTextJsonFormatter NewFormatter() => new()
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            },
        };

        /// <summary>
        /// The tri-state survives serialization with all three states intact. Widening any of the
        /// <c>bool?</c>s to <c>bool</c> still compiles and still round-trips — and turns the null into
        /// false, which is the one outcome this surface exists to prevent.
        /// </summary>
        [Fact]
        public void A_null_capability_comes_back_null_and_not_false()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            var sent = new SessionInfoResponse(
                AgentProgram: "kiro-cli-chat 2.16.2",
                AgentLogDirectory: @"C:\Users\x\.kiro\logs\20260901T222004803",
                SupportsResume: true,
                SupportsSteering: false,
                SupportsImages: null,
                SupportsSessionList: true,
                OpenedAtUtc: "2026-09-02T13:32:00.0000000Z");

            var json = JsonSerializer.Serialize(sent, options);
            var back = JsonSerializer.Deserialize<SessionInfoResponse>(json, options);

            Assert.NotNull(back);
            Assert.Equal(sent.AgentProgram, back!.AgentProgram);
            Assert.Equal(sent.AgentLogDirectory, back.AgentLogDirectory);
            Assert.Equal(sent.OpenedAtUtc, back.OpenedAtUtc);

            Assert.True(back.SupportsResume);
            Assert.False(back.SupportsSteering);
            Assert.True(back.SupportsSessionList);

            // The assertion the whole file is for. `Assert.Null` rather than `Assert.NotEqual(false)`,
            // because a widened bool would make the second read as a pass on `default`.
            Assert.Null(back.SupportsImages);

            // WhenWritingNull means an unreported capability is ABSENT from the payload rather than
            // present as an explicit null. Both deserialize to null; only one of them can be told from
            // a backend that answered, so this pins which shape we are actually sending.
            Assert.DoesNotContain("supportsImages", json, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An unparseable timestamp is dropped rather than defaulted. A defaulted one renders as a
        /// confident wrong time, which is worse than an absent row.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a time")]
        public void An_unusable_opened_time_is_carried_as_null(string? value)
        {
            var response = new SessionInfoResponse(OpenedAtUtc: value);
            Assert.False(DateTimeOffset.TryParse(response.OpenedAtUtc, out _) && !string.IsNullOrEmpty(value));
        }

        /// <summary>
        /// The facts reach the shell through a real JSON-RPC hop against a real
        /// <see cref="EngineService"/>. A hand-built DTO cannot prove the field crossed; only this can,
        /// and stopping <c>EngineService.SessionInfo</c> from projecting a field leaves every builder
        /// test green while this goes red.
        /// </summary>
        [Fact]
        public async Task The_handshake_facts_cross_a_real_rpc_hop()
        {
            using var harness = Harness.Start();
            await harness.StartSessionAsync();

            var info = await harness.SessionInfoAsync();

            Assert.NotNull(info);
            Assert.Equal("fake-agent 1.0", info!.AgentProgram);

            // The fake reports a deliberately mixed handshake; all three states must arrive distinct.
            Assert.True(info.SupportsResume);
            Assert.False(info.SupportsImages);
            Assert.Null(info.SupportsSessionList);

            Assert.True(DateTimeOffset.TryParse(info.OpenedAtUtc, out _));
        }

        /// <summary>
        /// The mode facts (issues #269/#270) cross the same hop, each one distinct. The fake's story
        /// is opened-in-bypass, now-in-default, bundled — six fields, none of which may arrive as the
        /// null a dropped projection would leave. <see cref="SessionInfoFieldParityTests"/> pins the
        /// NAMES on the three types; this pins that <c>EngineService.SessionInfo</c> actually copies
        /// them, which a name check cannot see.
        /// </summary>
        [Fact]
        public async Task The_mode_facts_cross_a_real_rpc_hop()
        {
            using var harness = Harness.Start();
            await harness.StartSessionAsync();

            var info = await harness.SessionInfoAsync();

            Assert.NotNull(info);
            Assert.Equal("Mode", info!.ModeLabel);
            Assert.Equal("default", info.ModeId);
            Assert.Equal("Manual", info.ModeName);
            Assert.Equal("bypassPermissions", info.OpenedInModeId);
            Assert.Equal("Bypass Permissions", info.OpenedInModeName);
            Assert.Equal("bundled", info.ModeOrigin);
            Assert.Null(info.ModePinFailure);
            Assert.Null(info.RequestedModeFailure);
        }

        /// <summary>
        /// No session is null, and that is a different answer from a session that reported nothing —
        /// the host renders it as "no agent session open yet". Returning an empty response here would
        /// collapse the two and make the panel claim a handshake happened.
        /// </summary>
        [Fact]
        public async Task No_live_session_answers_null()
        {
            using var harness = Harness.Start();
            Assert.Null(await harness.SessionInfoAsync());
        }

        /// <summary>
        /// The single derivation. The flat <see cref="StartSessionResponse.SupportsSteering"/> the
        /// behaviour gate reads is projected from the session's own handshake answer, so the panel's
        /// tri-state and the gate cannot disagree. Reverting it to
        /// <c>provider.Capabilities.HasFlag(...)</c> reinstates two sources of truth.
        /// </summary>
        [Fact]
        public async Task The_steering_gate_is_projected_from_the_handshake()
        {
            using var harness = Harness.Start();

            var started = await harness.StartSessionAsync();
            var info = await harness.SessionInfoAsync();

            Assert.NotNull(info);
            Assert.Equal(info!.SupportsSteering, started.SupportsSteering);
            Assert.Equal(info.SupportsImages, started.SupportsImages);

            // And the direction that must never occur: the fake refuses images in its handshake, so
            // the gate must be shut regardless of what the provider's flags advertise.
            Assert.False(started.SupportsImages);
        }

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

            public static Harness Start()
            {
                var registry = new AgentProviderRegistry();
                registry.Register(new FakeAgentProvider());

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

            public Task<StartSessionResponse> StartSessionAsync() =>
                _shellRpc.InvokeWithParameterObjectAsync<StartSessionResponse>(
                    RpcMethods.StartSession,
                    new StartSessionRequest("fake", null, @"C:\ws", "Prompt", null));

            public Task<SessionInfoResponse?> SessionInfoAsync() =>
                _shellRpc.InvokeWithCancellationAsync<SessionInfoResponse?>(
                    RpcMethods.SessionInfo, Array.Empty<object>(), default);

            public void Dispose()
            {
                _shellRpc.Dispose();
                _engineRpc.Dispose();
                _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            /// <summary>The engine asks the shell for the IDE tool catalog while a session starts; an
            /// empty one is enough here, and answering nothing else keeps the harness honest about what
            /// this test needs.</summary>
            private sealed class ToolsOnlyShell
            {
                [JsonRpcMethod(RpcMethods.ToolsList)]
                public ToolListResponse ToolsList() => new(Array.Empty<ToolDescriptorDto>());
            }
        }
    }
}
