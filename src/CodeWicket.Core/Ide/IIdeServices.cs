namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// The host-provided facade an <see cref="IAgentProvider"/> consumes to integrate with the
    /// editor: read workspace context, route file edits through the IDE, expose IDE-native tools,
    /// and prompt for permissions. The Console/Desktop hosts supply fakes; the VS extension supplies
    /// real implementations backed by VSSDK/Roslyn.
    /// </summary>
    public interface IIdeServices
    {
        /// <summary>Editor/solution context: active file, selection, open docs, diagnostics.</summary>
        IWorkspaceContext Workspace { get; }

        /// <summary>Client-owned filesystem: the agent's reads/writes go through here (diff/undo/SCC in VS).</summary>
        IEditApplier Edits { get; }

        /// <summary>IDE-native tools exposed to the agent (surfaced to the backend over MCP).</summary>
        IToolCatalog Tools { get; }

        /// <summary>Permission prompts surfaced to the user.</summary>
        IPermissionHandler Permissions { get; }
    }
}
