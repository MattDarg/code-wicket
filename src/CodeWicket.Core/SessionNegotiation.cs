using System;

namespace CodeWicket.Core
{
    /// <summary>
    /// What a session's handshake actually settled — the facts a host can show but cannot work out
    /// for itself (issue #160). Distinct from the capability FLAGS the provider carries: those are a
    /// decision ("may I steer?"), this is the evidence ("did the agent say so?").
    ///
    /// <para><b>Every capability is a <see cref="bool"/>? and a null means NOT REPORTED, never
    /// "no".</b> This is the <c>UsageReport</c> rule, and it is reachable rather than theoretical: an
    /// agent that sends no <c>agentCapabilities</c> object at all leaves them unset. Rendering an
    /// unknown as a negative would manufacture a confident wrong answer about the session, which is
    /// worse than the silence it replaces.</para>
    ///
    /// <para><b>Build this on READ, never cache it.</b> <c>AgentVersionProbe</c> is fire-and-forget so
    /// that session start never waits on it, which means the version can land after the handshake
    /// completes. A record captured once at initialize reports null on a fast session and a version on
    /// a slow one, and the two are indistinguishable from "this CLI doesn't say".</para>
    /// </summary>
    /// <param name="AgentProgram">
    /// What the agent said it is, <b>verbatim</b> — <c>kiro-cli-chat 2.16.2</c>,
    /// <c>@agentclientprotocol/claude-agent-acp 0.70.0</c>. Null when neither the handshake's
    /// <c>agentInfo</c> nor the version probe has answered.
    /// <para><b>Never re-label this as the product.</b> Claude's <c>agentInfo</c> names the ADAPTER,
    /// and its version is not a Claude Code version — the adapter spawns its own vendored
    /// <c>claude</c> binary and vendors its own agent SDK, neither of which appears on the wire. So
    /// <c>0.70.0</c> rendered as "Claude Code 0.70.0" is a false claim, and it is the tempting one.
    /// Reformatting also costs the reader the ability to match it against the backend's own release
    /// notes.</para>
    /// </param>
    /// <param name="AgentLogDirectory">
    /// The BACKEND's own log directory, when it reports one (Kiro's
    /// <c>_meta.&lt;vendor&gt;.logging.logDir</c>). Worth relaying because it is the one diagnostic
    /// surface we neither own nor duplicate: a user who has exhausted our logs still has somewhere to
    /// look. Null when the backend names none.
    /// </param>
    /// <param name="SupportsResume">
    /// <c>agentCapabilities.loadSession</c>. Read from the handshake and NOT from the provider's
    /// capability flags: the flag refresh for this one is gated behind
    /// <c>AcpAgentConfig.DiscoverCapabilities</c>, which is false for both built-ins because they
    /// hand-declare it — so the flag is a config declaration that happens to agree, not a negotiation.
    /// </param>
    /// <param name="SupportsSteering"><c>_meta.steering.supported</c> — whether a mid-turn message can
    /// be injected rather than queued.</param>
    /// <param name="SupportsImages"><c>agentCapabilities.promptCapabilities.image</c> — whether a
    /// pasted image is sent as an image block or degrades to a path the agent must read.</param>
    /// <param name="SupportsSessionList"><c>agentCapabilities.sessionCapabilities.list</c> — whether
    /// the backend can enumerate its own CLI's stored conversations.</param>
    /// <param name="OpenedAt">
    /// When the connection to the agent was opened. Deliberately NOT the conversation's age: a
    /// conversation restored from history is older than the session now serving it.
    /// </param>
    /// <param name="ModeLabel">
    /// What this backend's ACP session "mode" IS, as the panel should label it — per-agent data,
    /// because ACP standardises the shape of a mode and not its meaning: Claude's modes are
    /// permission modes ("Permission mode"), Kiro's are agent configs ("Agent"). Null when the
    /// backend published no modes at all, which is not a mode of "none".
    /// </param>
    /// <param name="ModeId">The mode the backend reports being in NOW — the wire id. Null until
    /// reported. Read on every pull, so a switch made after the open (a plan-mode exit, a
    /// <c>set_mode</c>) shows the next time the panel opens.</param>
    /// <param name="ModeName">The backend's display name for <paramref name="ModeId"/>, where it
    /// gave one — Claude's <c>default</c> is named "Manual", and the name is what to print
    /// (issue #270); the id stays beside it because the id is what the logs say.</param>
    /// <param name="OpenedInModeId">The mode the backend reported when the session opened, BEFORE
    /// the host asserted anything — what the agent's own settings chose (issue #269). Shown only
    /// where it differs from <paramref name="ModeId"/>.</param>
    /// <param name="OpenedInModeName">Display name for <paramref name="OpenedInModeId"/>.</param>
    /// <param name="ModeOrigin">Where the running mode's definition came from, for backends that
    /// say (Kiro v3 tags each agent <c>bundled</c>/<c>workspace</c>/<c>user</c>/<c>client</c>).
    /// A repository-defined agent brings the repository's tool-trust rules, which is the fact worth
    /// a row. Null when the backend does not say.</param>
    /// <param name="ModeOriginRoot">The workspace root a <c>workspace</c>-origin mode was read
    /// from, when reported.</param>
    /// <param name="ModePinFailure">Why the host's permission-mode assertion did not land at open
    /// (issue #269), or null when it did or none was declared. Beside <paramref name="ModeId"/>
    /// this is the panel's one warning: the picker is decorative for this session.</param>
    /// <param name="RequestedModeId">The mode the provider asked the session to run as (Kiro v3's
    /// configured agent), or null.</param>
    /// <param name="RequestedModeFailure">Why <paramref name="RequestedModeId"/> was not applied —
    /// not offered, or refused — or null when it was, or none was requested.</param>
    public sealed record SessionNegotiation(
        string? AgentProgram,
        string? AgentLogDirectory,
        bool? SupportsResume,
        bool? SupportsSteering,
        bool? SupportsImages,
        bool? SupportsSessionList,
        DateTimeOffset OpenedAt,
        string? ModeLabel = null,
        string? ModeId = null,
        string? ModeName = null,
        string? OpenedInModeId = null,
        string? OpenedInModeName = null,
        string? ModeOrigin = null,
        string? ModeOriginRoot = null,
        string? ModePinFailure = null,
        string? RequestedModeId = null,
        string? RequestedModeFailure = null);
}
