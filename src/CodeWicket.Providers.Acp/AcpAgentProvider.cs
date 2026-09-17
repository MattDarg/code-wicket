using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// A generic, config-driven ACP agent backend: it spawns the CLI described by an
    /// <see cref="AcpAgentConfig"/> and drives it over ACP. The ACP protocol handling
    /// (<see cref="AcpAgentSession"/>) is entirely provider-agnostic, so most ACP agents need only a
    /// config value. Agents with extra launch flags subclass this and override
    /// <see cref="BuildLaunchArgs"/> (see <c>KiroAgentProvider</c>).
    /// </summary>
    public class AcpAgentProvider : IAgentProvider, IBackendSessionCatalog, IResumeCommandTemplate
    {
        // Starts as the configured declaration; refreshed from the agent's initialize response on
        // session start when Config.DiscoverCapabilities (config-defined agents, so an agent update
        // that adds/removes session/load is caught without editing config).
        private AgentCapabilities _capabilities;

        // Starts as the configured seed; replaced by the live list a session discovers (Claude Code
        // reports models only once a session is open), so a later ListProviders reflects the real list.
        private IReadOnlyList<ModelInfo> _models;

        public AcpAgentProvider(AcpAgentConfig config)
        {
            Config = config;
            _capabilities = config.Capabilities;
            _models = config.Models;
        }

        /// <summary>The launch + identity config this provider was built from.</summary>
        protected AcpAgentConfig Config { get; }

        public string ProviderId => Config.ProviderId;

        public string DisplayName => Config.DisplayName;

        public virtual IReadOnlyList<ModelInfo> Models => _models;

        public AgentCapabilities Capabilities => _capabilities;

        /// <inheritdoc/>
        public string? ResumeCommandTemplate => Config.ResumeCommandTemplate;

        /// <summary>
        /// Seeds the advertised model list from a persisted cache (a prior run's discovery), so the picker
        /// shows the real models before this run opens its first session. A later session's live discovery
        /// still overrides this. No-op on empty input. Virtual so a provider that discovers models by other
        /// means (Kiro's live shell-out, whose own <see cref="Models"/> override bypasses this field) can
        /// also fold the seed into its cache.
        /// </summary>
        public virtual void SeedModels(IReadOnlyList<ModelInfo> models)
        {
            if (models is { Count: > 0 })
                _models = models;
        }

        /// <summary>
        /// Invites a provider whose model list costs a live probe to start that probe NOW, in the
        /// background, and report the answer through <paramref name="onRefreshed"/> when it lands.
        /// No-op by default: for every provider whose <see cref="Models"/> is just data from
        /// <see cref="AcpAgentConfig"/> there is nothing to discover and nothing to wait for.
        ///
        /// Declared here rather than on the one provider that needs it so the engine can offer it to
        /// everything it registered without asking what backend anything is — same rule as the model
        /// selection and workspace-marker mechanics, which are data rather than branches.
        /// </summary>
        public virtual void BeginModelRefresh(Action<IReadOnlyList<ModelInfo>>? onRefreshed = null)
        {
        }

        /// <summary>
        /// Runs <see cref="AcpAgentConfig.PreflightCheck"/>. Virtual so a provider whose check shares
        /// work with a background probe can SEQUENCE the two rather than let them race — see
        /// <c>KiroAgentProvider</c>, where the gate and the probe are literally the same CLI command
        /// and running both spawns a duplicate process that contends with the session launch.
        /// </summary>
        protected virtual string? Preflight() => Config.PreflightCheck?.Invoke();

        public async Task<IAgentSession> StartSessionAsync(SessionOptions options, IIdeServices ide, CancellationToken cancellationToken = default)
        {
            var solutionRoot = string.IsNullOrEmpty(options.WorkspaceRootPath)
                ? Directory.GetCurrentDirectory()
                : options.WorkspaceRootPath;

            // A backend can resolve its whole workspace (steering, agent configs, workspace MCP) from
            // cwd with no upward walk of its own, so a solution nested below the folder holding that
            // marker silently loses all of it (issue #54). Resolved ONCE here and used for both the
            // process working directory and the ACP session/new cwd — those must never diverge.
            // Resolved ONCE here and used for both the process working directory and the ACP
            // session/new cwd - those must never diverge - and through the SAME method the session
            // listing uses, so a project file that redirects one cannot leave the other reading a
            // different store (see AgentRootResolution).
            // A pinned directory searches for nothing (issue #185): the user chose to keep this
            // conversation where its history lives, over whatever the marker walk or a project file
            // would have chosen. Validated by the host that set it, and checked again here because a
            // provider is driven by more than one host: a pin that fails the check is logged and the
            // ordinary resolution runs, after which the engine's own guard refuses the cross-root load.
            var pinned = options.PinnedWorkingDirectory is { Length: > 0 } pin ? pin : null;
            var pinRefusal = pinned is null ? null : ResumeRootGuard.RefusePin(pinned);
            if (pinRefusal is not null)
                System.Console.Error.WriteLine($"[acp] '{ProviderId}' pinned working directory refused: {pinRefusal}");
            var resolution = pinned is not null && pinRefusal is null
                ? PinnedResolution(pinned, solutionRoot)
                : ResolveAgentRoot(
                    solutionRoot, options.WorkspaceScope, line => System.Console.Error.WriteLine(line));
            var workspaceRoot = resolution.Root;
            if (workspaceRoot.Widened)
            {
                System.Console.Error.WriteLine(
                    $"[acp] '{ProviderId}' working directory widened to '{workspaceRoot.Root}' " +
                    $"(solution '{solutionRoot}'): {workspaceRoot.Reason}");
            }
            else
            {
                System.Console.Error.WriteLine(
                    $"[acp] '{ProviderId}' working directory '{workspaceRoot.Root}': {workspaceRoot.Reason}");
            }

            // Before the process exists: a backend that would answer a missing login by opening a browser
            // must not be launched headless (see AcpAgentConfig.PreflightCheck).
            if (Preflight() is { Length: > 0 } refusal)
            {
                System.Console.Error.WriteLine($"[acp] '{ProviderId}' not launched: {refusal}");
                throw new InvalidOperationException(refusal);
            }

            var connection = new ProcessAcpConnection(
                Config.CliPath, BuildLaunchArgs(options), Config.Environment, workspaceRoot.Root, ProviderId);

            // Fire-and-forget, so this adds nothing to the time the user waits for a session. Only
            // does anything for backends that don't state their version at initialize — see
            // AgentVersionProbe. The line it logs is worth having even on a session that never fails.
            AgentVersionProbe.Begin(Config.CliPath, Config.VersionArgs, connection.Diagnostics);

            var session = new AcpAgentSession(
                options, ide, connection, Config.AuthTokenProvider, workspaceRoot, connection.Diagnostics,
                resolution.Notices(), Config.BackendModes, RequestedSessionModeId, Config.SessionMeta,
                Config.ModeLabel);
            try
            {
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);

                if (Config.DiscoverCapabilities && session.DiscoveredLoadSession is bool loadSession)
                {
                    var refreshed = loadSession
                        ? _capabilities | AgentCapabilities.ResumeSession
                        : _capabilities & ~AgentCapabilities.ResumeSession;
                    if (refreshed != _capabilities)
                    {
                        System.Console.Error.WriteLine(
                            $"[acp] '{ProviderId}' capabilities refreshed from initialize: ResumeSession={loadSession}");
                        _capabilities = refreshed;
                    }
                }

                // Steering is refreshed for EVERY agent, not just config-defined ones (hence no
                // DiscoverCapabilities gate): it is a property of the installed adapter build rather
                // than of the backend we configured — the Claude adapter gained it between 0.55.0 and
                // 0.63.0 — so a hand-declared capability would be a claim about the user's machine that
                // we have no business making. The handshake answers it definitively every session.
                if (session.DiscoveredSteering is bool steering)
                {
                    var refreshed = steering
                        ? _capabilities | AgentCapabilities.Steering
                        : _capabilities & ~AgentCapabilities.Steering;
                    if (refreshed != _capabilities)
                    {
                        System.Console.Error.WriteLine(
                            $"[acp] '{ProviderId}' capabilities refreshed from initialize: Steering={steering}");
                        _capabilities = refreshed;
                    }
                }

                // Image prompts, same rule as steering and for the same reason: the answer belongs to the
                // build on the other end of the pipe. Every backend we ship says true today (captured on
                // the wire: kiro-cli 2.13/2.16, Kiro v3, claude-agent-acp 0.63.0), which is exactly why
                // it must be discovered rather than assumed — a capability that is currently universal
                // is the easiest kind to hard-code and the hardest to notice losing.
                if (session.DiscoveredImagePrompts is bool imagePrompts)
                {
                    var refreshed = imagePrompts
                        ? _capabilities | AgentCapabilities.ImagePrompts
                        : _capabilities & ~AgentCapabilities.ImagePrompts;
                    if (refreshed != _capabilities)
                    {
                        System.Console.Error.WriteLine(
                            $"[acp] '{ProviderId}' capabilities refreshed from initialize: ImagePrompts={imagePrompts}");
                        _capabilities = refreshed;
                    }
                }

                // Session listing, same rule again: whether the backend can enumerate its own CLI's
                // stored conversations (issue #108) is a fact about the installed adapter, and the two
                // backends we ship disagree today. Ungated for the same reason as steering.
                if (session.DiscoveredSessionList is bool sessionList)
                {
                    var refreshed = sessionList
                        ? _capabilities | AgentCapabilities.SessionList
                        : _capabilities & ~AgentCapabilities.SessionList;
                    if (refreshed != _capabilities)
                    {
                        System.Console.Error.WriteLine(
                            $"[acp] '{ProviderId}' capabilities refreshed from initialize: SessionList={sessionList}");
                        _capabilities = refreshed;
                    }
                }

                // Config-defined agents get ModelSelection inferred from whether the session exposed a
                // model config option (hand-declared built-ins are authoritative and untouched here).
                if (Config.DiscoverCapabilities && session.DiscoveredModelSelection is bool modelSelection)
                {
                    var refreshed = modelSelection
                        ? _capabilities | AgentCapabilities.ModelSelection
                        : _capabilities & ~AgentCapabilities.ModelSelection;
                    if (refreshed != _capabilities)
                        _capabilities = refreshed;
                }

                // Cache the live model list a session discovered so a later ListProviders (e.g. the
                // picker reopening) reflects the real models rather than the static seed.
                if (session.DiscoveredModels.Count > 0)
                    _models = session.DiscoveredModels;

                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc/>
        public WorkspaceRootResult ResolveListingRoot(string workspaceRootPath, AgentWorkspaceScope scope) =>
            // The AGENT's root, not the solution's. session/list is scoped by cwd and Claude keys its
            // store by a hash of exactly that path, so asking from the solution folder when the agent
            // runs above it (issue #54) returns an empty list - which is indistinguishable from the user
            // having no CLI sessions, and wrong in the direction nobody investigates.
            //
            // Logs nothing: this runs per listing, and the [session-list] lines already name the root
            // it resolved. The full diagnosis belongs to the session start, which happens once.
            ResolveAgentRoot(workspaceRootPath, scope, log: null).Root;

        /// <summary>
        /// Where the agent runs when the user kept a conversation in the directory its history lives
        /// in (issue #185): that directory, with nothing searched — no marker walk and no project
        /// settings file, since the pin exists precisely to override what either would have said.
        /// </summary>
        internal static AgentRootResolution PinnedResolution(string pinned, string solutionRoot) =>
            new AgentRootResolution(
                new WorkspaceRootResult(
                    pinned,
                    !WorkspaceRootLocator.SameRoot(pinned, solutionRoot),
                    null,
                    ResumeRootGuard.PinnedReason),
                ProjectSettingsLocation.None("not searched: the working directory was chosen for this conversation"),
                Array.Empty<ProjectSettingsIssue>());

        /// <summary>
        /// Where the agent should run for <paramref name="workspaceRootPath"/>: the project's own
        /// answer when it supplies one, else the bounded walk for this backend's workspace marker.
        /// </summary>
        /// <param name="log">Where the <c>[project-settings]</c> lines go, or null to stay quiet.</param>
        /// <remarks>
        /// <para>
        /// <b>Both the session start and the session listing come through here, and that is the point.</b>
        /// They ask the same question, and once a checked-in file can redirect the answer, an override
        /// reaching only one of them leaves the backend's listing reading a different store - an empty
        /// list, indistinguishable from having no conversations (issue #108).
        /// </para>
        /// <para>
        /// <b>The log lines are a PAIR, and the first is written before the search.</b> A line reporting
        /// the RESULT is silent for a bug in the search itself, and then both lines are missing with
        /// nothing to say the read was even attempted. Saying what is about to be asked is what gives the
        /// second line's absence a meaning - the shape of the <c>[session-list]</c> and <c>[mcp]</c> pairs.
        /// </para>
        /// </remarks>
        protected internal AgentRootResolution ResolveAgentRoot(
            string workspaceRootPath, AgentWorkspaceScope scope, Action<string>? log)
        {
            var solutionRoot = string.IsNullOrEmpty(workspaceRootPath)
                ? Directory.GetCurrentDirectory()
                : workspaceRootPath;

            log?.Invoke($"[project-settings] searching from '{solutionRoot}' (scope {scope})");

            var location = ProjectSettingsLocator.Find(solutionRoot);
            log?.Invoke(location.Found
                ? $"[project-settings] found '{location.FolderPath}': {location.Reason}"
                : $"[project-settings] none found: {location.Reason}");

            var read = ProjectSettingsReader.Read(location.SettingsFilePath);
            foreach (var issue in read.Issues)
                log?.Invoke($"[project-settings] {issue.Message}");

            // Measured from the directory CONTAINING the settings folder, never the solution root: it
            // is the only anchor stable under a clone or a parent rename, and the only one a human
            // editing the file assumes.
            var anchor = location.FolderPath is { } folder ? Path.GetDirectoryName(folder) : null;
            var over = ProjectWorkspaceRootOverride.Resolve(
                read.Settings.AgentWorkspaceRoot, anchor, scope, location.SettingsFilePath);

            var issues = new List<ProjectSettingsIssue>(read.Issues);
            if (over.Issue is { } refusal)
                issues.Add(refusal);

            if (over.Applied)
            {
                log?.Invoke($"[project-settings] applied: {over.Reason}");
                var widened = !WorkspaceRootLocator.SameRoot(over.Root, solutionRoot);
                return new AgentRootResolution(
                    new WorkspaceRootResult(over.Root!, widened, null, over.Reason), location, issues);
            }

            // Said whether or not anything was set, so "we applied nothing" and "there was nothing to
            // apply" are different lines rather than the same silence.
            log?.Invoke($"[project-settings] applied: nothing ({over.Reason})");

            return new AgentRootResolution(
                WorkspaceRootLocator.Resolve(solutionRoot, Config.WorkspaceMarkers, scope), location, issues);
        }

        /// <inheritdoc/>
        public async Task<BackendSessionListResult> ListBackendSessionsAsync(
            string workspaceRootPath, AgentWorkspaceScope scope, IIdeServices ide,
            CancellationToken cancellationToken = default)
        {
            var solutionRoot = string.IsNullOrEmpty(workspaceRootPath)
                ? Directory.GetCurrentDirectory()
                : workspaceRootPath;

            // Through the same resolution the engine's warm-path check uses, so a listing can never run
            // in a directory the check would have called wrong.
            var workspaceRoot = ResolveListingRoot(solutionRoot, scope);

            // Same gate as a session start: a backend that would answer a missing login by opening a
            // browser must not be launched headless. Unlike a session start this is not an exception -
            // the caller is a picker, and "you are signed out" is a line to show under an empty list.
            if (Preflight() is { Length: > 0 } refusal)
                return BackendSessionListResult.Unavailable(refusal);

            AcpAgentSession? session = null;
            try
            {
                var connection = new ProcessAcpConnection(
                    Config.CliPath, BuildBaseLaunchArgs(), Config.Environment, workspaceRoot.Root, ProviderId);

                session = new AcpAgentSession(
                    new SessionOptions { WorkspaceRootPath = solutionRoot, WorkspaceScope = scope },
                    ide, connection, Config.AuthTokenProvider, workspaceRoot, connection.Diagnostics);

                // Handshake only: no session/new, so nothing is created, nothing is metered, and there
                // is no conversation to tear down afterwards.
                await session.HandshakeOnlyAsync(cancellationToken).ConfigureAwait(false);

                if (session.DiscoveredSessionList != true)
                {
                    return BackendSessionListResult.Unavailable(
                        $"{DisplayName} does not offer a session list.");
                }

                var sessions = await session.ListSessionsAsync(workspaceRoot.Root, cancellationToken)
                    .ConfigureAwait(false);
                return BackendSessionListResult.Found(sessions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A CLI that is missing, wedged or speaking something other than ACP all land here. The
                // backend's own words are the most useful thing we have, and the picker shows them.
                System.Console.Error.WriteLine($"[acp] '{ProviderId}' session list failed: {ex.Message}");
                return BackendSessionListResult.Unavailable(ex.Message);
            }
            finally
            {
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Launch arguments that do NOT depend on a session - everything needed to reach the right
        /// backend, minus anything derived from <see cref="SessionOptions"/>. This is what a listing
        /// process launches with, since there is no session to derive from.
        /// <para><b>An override must put every flag that selects WHICH STORE is read here rather than in
        /// <see cref="BuildLaunchArgs"/>.</b> Kiro's <c>--agent-engine</c> is the case in point: its v3
        /// engine keeps its own sessions, so listing without the flag reads a different engine's store
        /// and returns an empty list - which looks exactly like the user having no CLI sessions. Same
        /// failure as asking from the wrong cwd, and just as quiet.</para>
        /// </summary>
        protected internal virtual IReadOnlyList<string> BuildBaseLaunchArgs() => Config.LaunchArgs;

        /// <summary>
        /// Builds the process arguments for a session. The base returns the configured
        /// <see cref="AcpAgentConfig.LaunchArgs"/> verbatim; override to add agent-specific flags
        /// (e.g. model/effort selection) derived from <paramref name="options"/>.
        /// </summary>
        protected internal virtual IReadOnlyList<string> BuildLaunchArgs(SessionOptions options) => Config.LaunchArgs;

        /// <summary>
        /// An ACP session mode to select over the wire (<c>session/set_mode</c>) once the session is
        /// open, for a backend that takes its "agent" choice that way rather than as a launch flag —
        /// Kiro's v3 engine rejects <c>--agent</c> at launch (exit 2, measured 2026-09-11) exactly as it
        /// rejects <c>--model</c>, and offers the same agents as the session's <c>availableModes</c>.
        /// Null (the default) selects nothing. <b>Best-effort by contract</b>: a mode the backend does
        /// not offer, or refuses, is reported to the user as a notice and the session carries on in the
        /// backend's default — a wrong agent name must not cost the session, the way the launch flag
        /// did.
        /// </summary>
        protected internal virtual string? RequestedSessionModeId => null;
    }
}
