using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// A disk-backed fake <see cref="IIdeServices"/> for the headless host. Edits are written
    /// under a working directory and recorded, so we can assert the agent's file changes were
    /// routed through the host (not performed by the agent itself).
    /// </summary>
    internal sealed class ConsoleIdeServices : IIdeServices
    {
        // Appended from the RPC callback threads the edit applier runs on, and read from the proof's
        // own thread. Both sides take the gate and the reader gets a SNAPSHOT: handing out the live
        // list let a write landing out of turn either throw mid-enumeration or go unseen (#247).
        private readonly List<string> _written = new();
        private readonly object _writtenGate = new object();

        /// <param name="permissions">
        /// Overrides the auto-allow handler. Proofs that need to observe the permission traffic itself
        /// (rather than merely get past it) supply their own recorder — permission requests reach the
        /// host as JSON-RPC <em>requests</em>, on a path entirely separate from the turn's event stream,
        /// so they are the one thing an event-stream assertion cannot see.
        /// </param>
        public ConsoleIdeServices(string rootPath, IPermissionHandler? permissions = null)
        {
            Workspace = new ConsoleWorkspaceContext(rootPath);
            Edits = new ConsoleEditApplier(rootPath, path =>
            {
                lock (_writtenGate) _written.Add(path);
                Console.WriteLine($"  [edit-applier] host wrote {path}");
            });
            Tools = new EmptyToolCatalog();
            Permissions = permissions ?? new AutoAllowPermissionHandler();
        }

        public IWorkspaceContext Workspace { get; }
        public IEditApplier Edits { get; }
        public IToolCatalog Tools { get; }
        public IPermissionHandler Permissions { get; }

        public IReadOnlyList<string> WrittenFiles
        {
            get { lock (_writtenGate) return _written.ToArray(); }
        }

        private sealed class ConsoleWorkspaceContext : IWorkspaceContext
        {
            public ConsoleWorkspaceContext(string? rootPath) => RootPath = rootPath;

            public string? RootPath { get; }

            public Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new WorkspaceSnapshot { SolutionName = "ConsoleHost" });
        }

        private sealed class ConsoleEditApplier : IEditApplier
        {
            private readonly string _root;
            private readonly Action<string> _onWrite;

            public ConsoleEditApplier(string root, Action<string> onWrite)
            {
                _root = root;
                _onWrite = onWrite;
            }

            public Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default)
            {
                var full = Resolve(path);
                // Missing is an error, not an empty file — see IEditApplier.ReadTextFileAsync.
                if (!File.Exists(full))
                    throw new FileNotFoundException($"File not found: {full}", full);
                return Task.FromResult(TextRange.Slice(File.ReadAllText(full), line, limit));
            }

            public Task<FileWriteResult?> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default)
            {
                var full = Resolve(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var oldText = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
                File.WriteAllText(full, content);
                _onWrite(full);
                return Task.FromResult<FileWriteResult?>(new FileWriteResult(full, oldText, content));
            }

            // No diff viewer in the console host.
            public Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            private string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_root, path);
        }

        private sealed class EmptyToolCatalog : IToolCatalog
        {
            public IReadOnlyList<ToolDescriptor> Tools { get; } = Array.Empty<ToolDescriptor>();

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ToolResult(IsError: true, "{\"error\":\"no IDE tools in the console host\"}"));
        }

        private sealed class AutoAllowPermissionHandler : IPermissionHandler
        {
            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                var choice = request.Options.FirstOrDefault(o =>
                                 o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways)
                             ?? request.Options.FirstOrDefault();

                Console.WriteLine($"  [permission] auto-allow '{request.Title}' -> {choice?.OptionId ?? "(none)"}");
                return Task.FromResult(new PermissionDecision(choice?.OptionId ?? "allow", Cancelled: choice is null));
            }
        }
    }
}
