using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Model selection against a backend shaped like Kiro's v3 agent engine: its <c>session/new</c>
    /// response carries no models, it answers <c>session/set_model</c> with "Method not found", and it
    /// publishes the model selector in a <c>config_option_update</c> notification AFTER the session
    /// opens. Two independent failures had to be fixed for that to work, and both are pinned here —
    /// the notification being silently dropped (large frames re-deserialized from a NUL-padded span),
    /// and nothing consuming the selector when it did arrive.
    /// </summary>
    public class AcpConfigOptionTests
    {
        private static readonly BackendPermissionModes Modes =
            new("default", AutoModeId: "auto", SettingsHint: "Edit the test settings file.");

        [Fact]
        public async Task LargeSessionUpdateFrameIsNotDropped()
        {
            // The dropped Kiro frame was ~4KB; a typed params record made the formatter re-read the
            // message from a rented buffer and throw on its trailing NULs, and on a notification that
            // exception goes nowhere — the text simply never arrives. Well past 4KB so the padding
            // can't coincidentally line up.
            const int size = 20_000;

            using var harness = await ConfigOptionHarness.StartAsync(new FakeAgentBehaviour
            {
                LargeChunkSize = size,
            });

            var received = new StringBuilder();
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("go")))
                if (ev is AgentEvent.AssistantTextDelta delta)
                    received.Append(delta.Text);

            Assert.Equal(size, received.Length);
        }

        [Fact]
        public async Task ModelSelectorPublishedAfterSessionNewIsDiscoveredAndApplied()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour
                {
                    PublishModelsAfterSessionNew = true,
                    SetModelIsMethodNotFound = true,
                },
                requestedModel: "m2");

            var discovery = Assert.IsAssignableFrom<IModelDiscovery>(harness.Session);
            await WaitUntilAsync(() => discovery.DiscoveredModels.Count > 0);

            Assert.Equal(new[] { "m1", "m2", "m3" }, discovery.DiscoveredModels.Select(m => m.Id));

            // The launch model: v3 refuses the --model flag, so the only route is applying it once the
            // selector shows up. Before the fix this silently left the session on the backend's default.
            await WaitUntilAsync(() => discovery.CurrentModelId == "m2");
            Assert.Contains(("model", "m2"), harness.Agent.ConfigOptionSets);

            // The mid-session switch — the picker gesture that failed with "Method not found".
            await harness.Session.SetModelAsync("m3");
            Assert.Equal("m3", discovery.CurrentModelId);
            Assert.Contains(("model", "m3"), harness.Agent.ConfigOptionSets);
        }

        /// <summary>
        /// The selector and its catalogue can arrive in separate frames, and the launch model must
        /// survive the gap.
        /// <para>
        /// The first frame names the config id with an empty <c>options</c> array, which is enough to
        /// set <c>_modelConfigId</c> and NOT enough to apply anything — <c>ParseModelConfigOption</c>
        /// only takes a catalogue it actually collected. The pending request was consumed before that
        /// was tested, so it was discarded UN-ATTEMPTED and no later frame, however complete, could
        /// apply it: the session ran on the backend's default while the picker showed the user's
        /// choice. Precisely the failure the pending mechanism exists to prevent, one frame earlier.
        /// </para>
        /// </summary>
        [Fact]
        public async Task AModelRequestSurvivesASelectorAnnouncedBeforeItsCatalogue()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour
                {
                    PublishModelsAfterSessionNew = true,
                    AnnounceSelectorBeforeCatalogue = true,
                    SetModelIsMethodNotFound = true,
                },
                requestedModel: "m2");

            var discovery = Assert.IsAssignableFrom<IModelDiscovery>(harness.Session);
            await WaitUntilAsync(() => discovery.CurrentModelId == "m2");

            Assert.Contains(("model", "m2"), harness.Agent.ConfigOptionSets);
        }

        [Fact]
        public async Task SetModelFallsBackToConfigOptionWhenTheBackendHasNoSetModel()
        {
            // The selector notification races the user: switch before it lands and we don't yet know the
            // backend is a config-option one. A hard "Method not found" is a good enough signal to try
            // the other route rather than fail the switch on timing.
            using var harness = await ConfigOptionHarness.StartAsync(new FakeAgentBehaviour
            {
                PublishModelsAfterSessionNew = false,
                SetModelIsMethodNotFound = true,
            });

            await harness.Session.SetModelAsync("m3");

            Assert.Contains(("model", "m3"), harness.Agent.ConfigOptionSets);
            Assert.Equal("m3", ((IModelDiscovery)harness.Session).CurrentModelId);
        }

        // ---- issue #269: the backend's permission mode is pinned to the agent's declared ask mode ----
        //
        // The failure is an absence (a backend that self-approves never asks), so the offline check
        // asserts the PRESENCE of the pin on every path that opens a session, and that a session whose
        // pin does not land is refused rather than started.

        [Fact]
        public async Task ABackendOpeningInBypassIsPinnedToTheAskModeBeforeStartReturns()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "bypassPermissions" },
                backendModes: Modes);

            // Asserted with nothing awaited after StartAsync: the pin has to be in place before the
            // session is handed to anyone who could send a prompt through it.
            Assert.Contains(("mode", "default"), harness.Agent.ConfigOptionSets);
            Assert.Equal("bypassPermissions", harness.Session.OpenedInModeId);
            Assert.Equal("default", harness.Session.BackendModeId);
            Assert.Equal("Manual", harness.Session.BackendModeName);
        }

        [Fact]
        public async Task ThePinIsAssertedEvenWhenTheBackendAlreadyReportsTheAskMode()
        {
            // The report is the adapter's record; the CLI behind it keeps its own. The pin is what
            // syncs the two, so "already default" is not a reason to skip it.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default" },
                backendModes: Modes);

            Assert.Contains(("mode", "default"), harness.Agent.ConfigOptionSets);
        }

        [Fact]
        public async Task AResumedSessionIsPinnedToo()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "bypassPermissions" },
                backendModes: Modes,
                resumeId: "old-1");

            Assert.Contains("session/load", harness.Agent.MethodsSeen);
            Assert.Contains(("mode", "default"), harness.Agent.ConfigOptionSets);
        }

        [Fact]
        public async Task TheLoadRefusedFallbackSessionIsPinnedToo()
        {
            // The #268 path: session/load answers "not found", the session falls back to session/new.
            // Easy to miss because it is a second open inside the same call.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "bypassPermissions", LoadIsNotFound = true },
                backendModes: Modes,
                resumeId: "old-1");

            Assert.Contains("session/load", harness.Agent.MethodsSeen);
            Assert.Contains("session/new", harness.Agent.MethodsSeen);
            Assert.Contains(("mode", "default"), harness.Agent.ConfigOptionSets);
        }

        [Fact]
        public async Task ARejectedPinOverAnEscalatedBackendContinuesAndSaysThePickerHasNoEffect()
        {
            // Fails OPEN: a pin can only fail on an adapter change, and refusing every session for
            // that would take the tool down over an update. What must not happen is silence — this
            // is the #269 hole itself, so the notice says the picker is decorative and names the
            // setting to remove.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "bypassPermissions", RejectModePin = true },
                backendModes: Modes);

            Assert.Contains("refused", harness.Session.BackendModePinFailure, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("bypassPermissions", harness.Session.BackendModeId);

            var notice = Assert.Single(harness.Notices.OfType<AgentEvent.BackendNotice>());
            Assert.Equal("error", notice.Level);
            Assert.Contains("Bypass Permissions", notice.Message, StringComparison.Ordinal);
            Assert.Contains("has no effect on this session", notice.Message, StringComparison.Ordinal);
            Assert.Contains("Edit the test settings file.", notice.Message, StringComparison.Ordinal);
            Assert.Contains("start a new session", notice.Message, StringComparison.Ordinal);

            // And the session is a session: a prompt still runs through it.
            var stop = "(none)";
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("go")))
                if (ev is AgentEvent.TurnCompleted c) stop = c.StopReason ?? stop;
            Assert.Equal("end_turn", stop);
        }

        [Fact]
        public async Task AFailedAssertionOverAnAskingBackendIsOnlyAWarning()
        {
            // The gate is on — the backend reports the mapped mode — and only the (redundant)
            // assertion failed. Worth a line, not an alarm.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default", RejectModePin = true },
                backendModes: Modes);

            Assert.NotNull(harness.Session.BackendModePinFailure);
            var notice = Assert.Single(harness.Notices.OfType<AgentEvent.BackendNotice>());
            Assert.Equal("warning", notice.Level);
            Assert.Contains("nothing changes", notice.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ABackendWithNoModeOptionIsSurfacedAsTheGateBeingUnknown()
        {
            // A declared mapping the backend never published a selector for: nothing could be asked,
            // and an unreported mode is not "ask" — it is unknown, which is the second level.
            using var harness = await ConfigOptionHarness.StartAsync(new FakeAgentBehaviour(), backendModes: Modes);

            Assert.Contains("no permission-mode option", harness.Session.BackendModePinFailure, StringComparison.Ordinal);
            var notice = Assert.Single(harness.Notices.OfType<AgentEvent.BackendNotice>());
            Assert.Equal("error", notice.Level);
        }

        [Fact]
        public async Task NoDeclaredAskModeMeansNoPinAndNoRefusal()
        {
            // Kiro's shape: modes are agent configs, nothing is declared, nothing is sent.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "bypassPermissions" });

            Assert.DoesNotContain(harness.Agent.ConfigOptionSets, s => s.ConfigId == "mode");
            Assert.Equal("bypassPermissions", harness.Session.BackendModeId);
        }

        [Fact]
        public async Task AModeChangeTheBackendAnnouncesIsRecorded()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default", AnnounceModeOnPrompt = "bypassPermissions" },
                backendModes: Modes);

            await foreach (var _ in harness.Session.SendAsync(new PromptInput("go"))) { }

            await WaitUntilAsync(() => harness.Session.BackendModeId == "bypassPermissions");
            Assert.Equal(new[] { "default", "bypassPermissions" }, harness.Session.BackendModeUpdates);
        }

        // ---- the provider's requested session mode (Kiro v3's agent) is applied over the wire ----

        [Fact]
        public async Task ARequestedModeIsSelectedBySetModeAfterOpen()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default" },
                requestedModeId: "acceptEdits");

            Assert.Equal(new[] { "acceptEdits" }, harness.Agent.ModeSets);
            Assert.Equal("acceptEdits", harness.Session.BackendModeId);
            Assert.Null(harness.Session.RequestedModeFailure);
            Assert.Empty(harness.Notices.OfType<AgentEvent.BackendNotice>());
        }

        [Fact]
        public async Task ARequestedModeTheBackendDoesNotOfferIsANoticeNotADeadSession()
        {
            // The launch-flag shape this replaces killed the session before the handshake.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default" },
                requestedModeId: "no-such-agent");

            Assert.Empty(harness.Agent.ModeSets);
            Assert.Contains("does not offer 'no-such-agent'", harness.Session.RequestedModeFailure, StringComparison.Ordinal);
            var notice = Assert.Single(harness.Notices.OfType<AgentEvent.BackendNotice>());
            Assert.Equal("error", notice.Level);
            Assert.Contains("'no-such-agent'", notice.Message, StringComparison.Ordinal);
            Assert.Contains("running as 'Manual' (default)", notice.Message, StringComparison.Ordinal);

            var stop = "(none)";
            await foreach (var ev in harness.Session.SendAsync(new PromptInput("go")))
                if (ev is AgentEvent.TurnCompleted c) stop = c.StopReason ?? stop;
            Assert.Equal("end_turn", stop);
        }

        [Fact]
        public async Task ARefusedModeIsANoticeNotADeadSession()
        {
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default", RejectSetMode = true },
                requestedModeId: "acceptEdits");

            Assert.Contains("refused 'acceptEdits'", harness.Session.RequestedModeFailure, StringComparison.Ordinal);
            var notice = Assert.Single(harness.Notices.OfType<AgentEvent.BackendNotice>());
            Assert.Equal("error", notice.Level);
            Assert.Equal("default", harness.Session.BackendModeId);
        }

        [Fact]
        public async Task ARepositoryDefinedModeIsNamedAsSuch()
        {
            // Kiro v3 tags each agent with its origin; a workspace one brings the repository's own
            // tool-trust rules, which is the #269 shape reached by the user's own configuration —
            // allowed, and said.
            using var harness = await ConfigOptionHarness.StartAsync(
                new FakeAgentBehaviour { ReportMode = "default", WorkspaceModeId = "team-agent" },
                requestedModeId: "team-agent");

            Assert.Equal(new[] { "team-agent" }, harness.Agent.ModeSets);
            Assert.Null(harness.Session.RequestedModeFailure);
            var notice = Assert.Single(harness.Notices.OfType<AgentEvent.BackendNotice>());
            Assert.Equal("warning", notice.Level);
            Assert.Contains("defined by the repository under C:\\repo", notice.Message, StringComparison.Ordinal);
            Assert.Contains("never reaches the permission prompt here", notice.Message, StringComparison.Ordinal);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.True(condition(), "condition was not met within the timeout");
        }

        private sealed class FakeAgentBehaviour
        {
            /// <summary>Publish the model selector as a post-session/new notification (Kiro v3).</summary>
            public bool PublishModelsAfterSessionNew { get; init; }

            /// <summary>Answer session/set_model with -32601, as Kiro's v3 engine does.</summary>
            public bool SetModelIsMethodNotFound { get; init; }

            /// <summary>When set, a prompt streams one chunk of this many characters.</summary>
            public int LargeChunkSize { get; init; }

            /// <summary>
            /// Announce the model selector in a frame carrying NO options, before the frame that carries
            /// the catalogue. A real v3 engine publishes the selector and its contents separately, so
            /// this is the ordinary two-frame shape rather than a malformed one.
            /// </summary>
            public bool AnnounceSelectorBeforeCatalogue { get; init; }

            /// <summary>When set, session/new and session/load report this permission mode (in both
            /// the <c>modes</c> object and a config option of category "mode"), as claude-agent-acp does.</summary>
            public string? ReportMode { get; init; }

            /// <summary>Answer a set_config_option on the mode with an error, as the adapter does for a
            /// mode the session does not offer.</summary>
            public bool RejectModePin { get; init; }

            /// <summary>Answer session/load with "Session not found", forcing the session/new fallback.</summary>
            public bool LoadIsNotFound { get; init; }

            /// <summary>When set, a prompt first sends a current_mode_update naming this mode — the
            /// backend switching modes on its own.</summary>
            public string? AnnounceModeOnPrompt { get; init; }

            /// <summary>Answer session/set_mode with an error.</summary>
            public bool RejectSetMode { get; init; }

            /// <summary>When set, availableModes also lists this id tagged as workspace-defined, the
            /// way Kiro v3 tags an agent from <c>.kiro/agents</c> (<c>_meta.kiro.source</c> + root).</summary>
            public string? WorkspaceModeId { get; init; }
        }

        private sealed class ConfigOptionHarness : IDisposable
        {
            private readonly string _root;

            private ConfigOptionHarness(AcpAgentSession session, FakeAgent agent, string root, List<AgentEvent> notices)
            {
                Session = session;
                Agent = agent;
                _root = root;
                Notices = notices;
            }

            public AcpAgentSession Session { get; }

            public FakeAgent Agent { get; }

            /// <summary>Every event the session raised with no turn open — where a notice about the
            /// session's own opening lands.</summary>
            public List<AgentEvent> Notices { get; }

            public static async Task<ConfigOptionHarness> StartAsync(
                FakeAgentBehaviour behaviour, string? requestedModel = null,
                BackendPermissionModes? backendModes = null, string? resumeId = null,
                string? requestedModeId = null)
            {
                var (clientStream, serverStream) = FullDuplexStream.CreatePair();
                var agent = new FakeAgent(serverStream, behaviour);
                agent.Start();

                var root = Path.Combine(Path.GetTempPath(), "cwkt-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                var notices = new List<AgentEvent>();
                var session = new AcpAgentSession(
                    new SessionOptions
                    {
                        WorkspaceRootPath = root, ModelId = requestedModel, ResumeConversationId = resumeId,
                        OutOfTurnEvents = ev => { lock (notices) notices.Add(ev); },
                    },
                    new StubIdeServices(root),
                    new StreamAcpConnection(clientStream),
                    backendModes: backendModes,
                    requestedModeId: requestedModeId);
                try
                {
                    await session.InitializeAsync(CancellationToken.None);
                }
                catch
                {
                    agent.Dispose();
                    try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
                    throw;
                }

                return new ConfigOptionHarness(session, agent, root, notices);
            }

            public void Dispose()
            {
                try { Session.DisposeAsync().AsTask().Wait(2000); } catch { /* teardown */ }
                Agent.Dispose();
                try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
            }
        }

        private sealed class StreamAcpConnection : IAcpConnection
        {
            private readonly Stream _stream;

            public StreamAcpConnection(Stream duplex) => _stream = duplex;

            public Stream Sending => _stream;

            public Stream Receiving => _stream;

            public void Dispose() => _stream.Dispose();
        }

        /// <summary>
        /// A newline-delimited JSON-RPC agent that models the v3 shapes: no models on session/new, an
        /// optional config_option_update notification carrying the selector, set_model answered as
        /// method-not-found, and set_config_option echoing the updated options back.
        /// </summary>
        private sealed class FakeAgent : IDisposable
        {
            private static readonly string[] ModelIds = { "m1", "m2", "m3" };

            private readonly Stream _stream;
            private readonly FakeAgentBehaviour _behaviour;
            private readonly StreamWriter _writer;
            private readonly StreamReader _reader;
            private readonly List<(string ConfigId, string Value)> _configOptionSets = new();
            private readonly List<string> _methods = new();
            private readonly List<string> _modeSets = new();
            private string _currentModel = "m1";
            private string? _currentMode;
            private Task? _pump;

            public FakeAgent(Stream stream, FakeAgentBehaviour behaviour)
            {
                _stream = stream;
                _behaviour = behaviour;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }

            /// <summary>Every session/set_config_option this agent was asked for, in order.</summary>
            public IReadOnlyList<(string ConfigId, string Value)> ConfigOptionSets
            {
                get { lock (_configOptionSets) return _configOptionSets.ToList(); }
            }

            /// <summary>Every session/set_mode this agent was asked for, in order.</summary>
            public IReadOnlyList<string> ModeSets
            {
                get { lock (_modeSets) return _modeSets.ToList(); }
            }

            /// <summary>Every request method this agent answered, in order.</summary>
            public IReadOnlyList<string> MethodsSeen
            {
                get { lock (_methods) return _methods.ToList(); }
            }

            public void Start() => _pump = Task.Run(PumpAsync);

            private async Task PumpAsync()
            {
                try
                {
                    string? line;
                    while ((line = await _reader.ReadLineAsync()) is not null)
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("method", out var methodEl))
                            continue;
                        var method = methodEl.GetString();
                        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
                        if (id is null)
                            continue;

                        await HandleAsync(method, id, root).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // The stream closes when the session is disposed; nothing to report.
                }
            }

            private async Task HandleAsync(string? method, string id, JsonElement request)
            {
                lock (_methods) _methods.Add(method ?? string.Empty);
                switch (method)
                {
                    case "initialize":
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":1}}}}");
                        break;

                    case "session/load" when _behaviour.LoadIsNotFound:
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32603," +
                            $"\"message\":\"Session not found: old-1\"}}}}");
                        break;

                    case "session/load":
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{{OpenResultBodyJson()}}}}}");
                        break;

                    case "session/new":
                        // Deliberately no models here: that's the whole point of the v3 shape.
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{{OpenResultBodyJson()}}}}}");
                        if (_behaviour.PublishModelsAfterSessionNew)
                        {
                            if (_behaviour.AnnounceSelectorBeforeCatalogue)
                                await SendAsync(
                                    "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
                                    "\"update\":{\"sessionUpdate\":\"config_option_update\",\"configOptions\":" +
                                    EmptySelectorJson() + "}}}");
                            await SendAsync(
                                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
                                "\"update\":{\"sessionUpdate\":\"config_option_update\",\"configOptions\":" +
                                ConfigOptionsJson() + "}}}");
                        }
                        break;

                    case "session/set_model":
                        if (_behaviour.SetModelIsMethodNotFound)
                            await SendAsync(
                                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32601," +
                                $"\"message\":\"Method not found\"}}}}");
                        else
                            await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                        break;

                    case "session/set_config_option":
                        var configId = request.GetProperty("params").GetProperty("configId").GetString() ?? string.Empty;
                        var value = request.GetProperty("params").GetProperty("value").GetString() ?? string.Empty;
                        lock (_configOptionSets)
                            _configOptionSets.Add((configId, value));
                        if (configId == "model")
                            _currentModel = value;
                        if (configId == "mode")
                        {
                            if (_behaviour.RejectModePin)
                            {
                                await SendAsync(
                                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32603," +
                                    $"\"message\":\"Mode {value} is not available in this session\"}}}}");
                                break;
                            }

                            // As the adapter does: the notification precedes the response.
                            _currentMode = value;
                            await SendAsync(ModeUpdateJson(value));
                        }
                        await SendAsync(
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"configOptions\":{ConfigOptionsJson()}}}}}");
                        break;

                    case "session/set_mode":
                        var modeId = request.GetProperty("params").GetProperty("modeId").GetString() ?? string.Empty;
                        lock (_modeSets) _modeSets.Add(modeId);
                        if (_behaviour.RejectSetMode)
                        {
                            await SendAsync(
                                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32603," +
                                $"\"message\":\"Mode {modeId} cannot be selected\"}}}}");
                            break;
                        }
                        _currentMode = modeId;
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                        await SendAsync(ModeUpdateJson(modeId));
                        break;

                    case "session/prompt":
                        if (_behaviour.AnnounceModeOnPrompt is { } announced)
                        {
                            _currentMode = announced;
                            await SendAsync(ModeUpdateJson(announced));
                        }
                        if (_behaviour.LargeChunkSize > 0)
                            await SendAsync(
                                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
                                "\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\"," +
                                "\"text\":\"" + new string('x', _behaviour.LargeChunkSize) + "\"}}}}");
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"stopReason\":\"end_turn\"}}}}");
                        break;

                    default:
                        await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                        break;
                }
            }

            /// <summary>The selector, padded past 4KB — the real frame is large because every option
            /// carries a description, and its size is what used to make it vanish.</summary>
            private string ConfigOptionsJson()
            {
                var options = string.Join(",", ModelIds.Select(m =>
                    $"{{\"value\":\"{m}\",\"name\":\"Model {m}\",\"description\":\"{new string('d', 2000)}\"}}"));
                var model = "{\"type\":\"select\",\"id\":\"model\",\"name\":\"Model\",\"category\":\"model\"," +
                            $"\"currentValue\":\"{_currentModel}\",\"options\":[{options}]}}";
                return _currentMode is null ? $"[{model}]" : $"[{ModeOptionJson()},{model}]";
            }

            /// <summary>The body of a session/new or session/load result: the id, and — when the
            /// behaviour reports a mode — the <c>modes</c> object and config options claude-agent-acp
            /// sends, with the mode option FIRST as it is on the wire.</summary>
            private string OpenResultBodyJson()
            {
                _currentMode = _behaviour.ReportMode;
                if (_currentMode is null)
                    return "\"sessionId\":\"s1\"";
                var workspaceMode = _behaviour.WorkspaceModeId is { } w
                    ? ",{\"id\":\"" + w + "\",\"name\":\"" + w + "\",\"_meta\":{\"kiro\":{\"source\":\"workspace\"," +
                      "\"resource\":{\"resourceType\":\"agent\",\"source\":{\"origin\":\"workspace\",\"root\":\"C:\\\\repo\"}}}}}"
                    : string.Empty;
                return "\"sessionId\":\"s1\",\"modes\":{\"currentModeId\":\"" + _currentMode + "\",\"availableModes\":[" +
                       "{\"id\":\"default\",\"name\":\"Manual\"},{\"id\":\"acceptEdits\",\"name\":\"Accept Edits\"}," +
                       "{\"id\":\"bypassPermissions\",\"name\":\"Bypass Permissions\"}" + workspaceMode + "]}," +
                       $"\"configOptions\":[{ModeOptionJson()}]";
            }

            private string ModeOptionJson() =>
                "{\"id\":\"mode\",\"name\":\"Mode\",\"category\":\"mode\",\"type\":\"select\"," +
                $"\"currentValue\":\"{_currentMode}\",\"options\":[" +
                "{\"value\":\"default\",\"name\":\"Manual\"},{\"value\":\"acceptEdits\",\"name\":\"Accept Edits\"}," +
                "{\"value\":\"bypassPermissions\",\"name\":\"Bypass Permissions\"}]}";

            private static string ModeUpdateJson(string modeId) =>
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\"," +
                "\"update\":{\"sessionUpdate\":\"current_mode_update\",\"currentModeId\":\"" + modeId + "\"}}}";

            /// <summary>The same selector with an empty options array — it names the config id, and
            /// nothing else.</summary>
            private string EmptySelectorJson() =>
                "[{\"type\":\"select\",\"id\":\"model\",\"name\":\"Model\",\"category\":\"model\"," +
                $"\"currentValue\":\"{_currentModel}\",\"options\":[]}}]";

            private async Task SendAsync(string json)
            {
                await _writer.WriteAsync(json);
                await _writer.WriteAsync('\n');
                await _writer.FlushAsync();
            }

            public void Dispose()
            {
                try { _stream.Dispose(); } catch { /* ignore */ }
                try { _pump?.Wait(1000); } catch { /* ignore */ }
            }
        }
    }
}
