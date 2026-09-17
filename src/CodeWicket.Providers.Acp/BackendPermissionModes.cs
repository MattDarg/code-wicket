using CodeWicket.Core;

namespace CodeWicket.Providers.Acp
{
    /// <summary>
    /// How the host's permission picker maps onto an agent's own permission modes — the lever the
    /// host drives so the backend's mode is a function of the picker and never of a file the agent
    /// can write (issue #269). Declared per agent as DATA, because ACP standardises what a mode looks
    /// like on the wire (<c>currentModeId</c>, <c>availableModes</c>, the config option in category
    /// <c>mode</c>) and not what it means: Claude's <c>default</c> is "ask", while Kiro's modes are
    /// agent configs and a pin there would switch agents. A guessed id could land on a MORE permissive
    /// mode, so an agent with no declaration is never pinned.
    /// </summary>
    /// <param name="AskModeId">The mode in which the agent asks about everything the host's policy
    /// expects to see. Every host mode maps here today.</param>
    /// <param name="AutoModeId">The agent's own classifier mode, where it has one. Declared so that
    /// issue #261's "Agent Auto" picker entry has somewhere to map to; nothing maps to it yet, and
    /// when something does it must be gated on the session's <c>availableModes</c>, which clamp
    /// <c>auto</c> per model.</param>
    /// <param name="SettingsHint">Where this agent reads the mode it opens in, for the notice shown
    /// when the host cannot move it — the user's next step is to edit that, and the ACP layer does
    /// not otherwise know the file.</param>
    public sealed record BackendPermissionModes(string AskModeId, string? AutoModeId = null, string? SettingsHint = null)
    {
        /// <summary>The backend mode the host's <paramref name="hostMode"/> asserts. One answer
        /// today: an inherited escalation is what this exists to stop, and a chosen one (#261) is a
        /// later entry in this map, not a hole in it.</summary>
        public string For(PermissionMode hostMode) => AskModeId;
    }
}
