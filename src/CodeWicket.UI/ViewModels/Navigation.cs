using CodeWicket.UI.Mvvm;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// One step of the transcript's breadcrumb — the conversation itself at depth 0, then one per
    /// sub-agent row drilled into (issue #148).
    /// </summary>
    /// <remarks>
    /// A breadcrumb rather than a single Back chip, because nesting is recursive: a sub-agent's own
    /// sub-agent nests for free, so at depth 2 a single chip pops one frame and names no path — the user
    /// cannot see where they are, only that there is somewhere to go.
    /// <para>
    /// A plain class, not a record: this project has no <c>IsExternalInit</c> polyfill and multi-targets
    /// net472, so <c>record</c> does not compile here.
    /// </para>
    /// </remarks>
    public sealed class NavCrumbViewModel : ObservableObject
    {
        internal NavCrumbViewModel(string label, int depth, bool isCurrent, bool hasNewActivity)
        {
            Label = label;
            Depth = depth;
            IsCurrent = isCurrent;
            HasNewActivity = hasNewActivity;
        }

        /// <summary>What the crumb says — "Conversation" at the root, otherwise the row's own title.</summary>
        public string Label { get; }

        /// <summary>
        /// How deep this crumb sits: 0 is the conversation, 1 the first sub-agent opened, and so on.
        /// Clicking a crumb navigates TO that depth, so the value is the whole payload of the gesture.
        /// </summary>
        public int Depth { get; }

        /// <summary>Whether this is the view currently on screen — the last crumb, drawn inert.</summary>
        public bool IsCurrent { get; }

        /// <summary>
        /// Whether the scope behind this crumb has moved on since it was left. Only the root crumb ever
        /// sets it, and it is the answer to "the main turn produced something while I was drilled in":
        /// the pane deliberately does NOT navigate on its own (being yanked into a sub-view to see
        /// something is worse than an expanded row), so the way back has to say the transcript grew.
        /// </summary>
        public bool HasNewActivity { get; }
    }
}
