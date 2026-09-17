namespace CodeWicket.Ipc
{
    /// <summary>JSON-RPC method names for the engine &lt;-&gt; shell boundary.</summary>
    public static class RpcMethods
    {
        // shell -> engine (session control)
        public const string ListProviders = "engine/listProviders";
        public const string StartSession = "engine/startSession";
        public const string Prompt = "engine/prompt";
        public const string Cancel = "engine/cancel";
        public const string Steer = "engine/steer";
        public const string Summarize = "engine/summarize";
        public const string SetModel = "engine/setModel";
        public const string ListBackendSessions = "engine/listBackendSessions";
        public const string TakeImportedHistory = "engine/takeImportedHistory";
        public const string ResolveAgentRoot = "engine/resolveAgentRoot";
        public const string SessionInfo = "engine/sessionInfo";

        // engine -> shell (event sink + IIdeServices callbacks)
        public const string OnAgentEvent = "shell/onAgentEvent";
        public const string OnProviderModels = "shell/providers/models";
        public const string WorkspaceCapture = "shell/workspace/capture";
        public const string EditsRead = "shell/edits/read";
        public const string EditsWrite = "shell/edits/write";
        public const string ToolsList = "shell/tools/list";
        public const string ToolsInvoke = "shell/tools/invoke";
        public const string PermissionsRequest = "shell/permissions/request";
    }
}
