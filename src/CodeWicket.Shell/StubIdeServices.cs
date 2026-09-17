using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Core.Ide;

namespace CodeWicket.Shell
{
    /// <summary>
    /// A disk-backed <see cref="IIdeServices"/> stub for hosts that don't yet have the real
    /// Visual Studio implementation (the Desktop app, and the VSIX before Phase 5). Edits are
    /// written under a working directory; permissions auto-allow; no IDE tools are exposed.
    /// Phase 5 replaces this with the real VS-backed services.
    /// </summary>
    public sealed class StubIdeServices : IIdeServices
    {
        // Same rule as ConsoleIdeServices: appended from RPC callback threads, read from the host's,
        // so both sides take the gate and the reader gets a snapshot rather than the live list (#247).
        private readonly List<string> _written = new();
        private readonly object _writtenGate = new object();

        public StubIdeServices(string rootPath, string? solutionName = null, Action<string>? onWrite = null)
        {
            Workspace = new StubWorkspaceContext(rootPath, solutionName ?? "DesktopHost");
            Edits = new StubEditApplier(rootPath, path =>
            {
                lock (_writtenGate) _written.Add(path);
                onWrite?.Invoke(path);
            });
            Tools = new EmptyToolCatalog();
            Permissions = new AutoAllowPermissionHandler();
        }

        public IWorkspaceContext Workspace { get; }
        public IEditApplier Edits { get; }
        public IToolCatalog Tools { get; }
        public IPermissionHandler Permissions { get; }

        /// <summary>Absolute paths the agent's edits have been written to, in order.</summary>
        public IReadOnlyList<string> WrittenFiles
        {
            get { lock (_writtenGate) return _written.ToArray(); }
        }

        private sealed class StubWorkspaceContext : IWorkspaceContext
        {
            private readonly string _solutionName;

            public StubWorkspaceContext(string? rootPath, string solutionName)
            {
                RootPath = rootPath;
                _solutionName = solutionName;
            }

            public string? RootPath { get; }

            public Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new WorkspaceSnapshot { SolutionName = _solutionName });
        }

        private sealed class StubEditApplier : IEditApplier
        {
            private readonly string _root;
            private readonly Action<string> _onWrite;

            public StubEditApplier(string root, Action<string> onWrite)
            {
                _root = root;
                _onWrite = onWrite;
            }

            public Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default)
            {
                var full = Resolve(path);
                // Missing is an error, not an empty file — see IEditApplier.ReadTextFileAsync. Held to
                // the same contract as the VS applier deliberately: the agent must never see read
                // semantics that depend on which host it happens to be running against.
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

            // No native diff viewer outside Visual Studio; the Desktop host has nothing to show.
            public Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            private string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_root, path);
        }

        private sealed class EmptyToolCatalog : IToolCatalog
        {
            public IReadOnlyList<ToolDescriptor> Tools { get; } = Array.Empty<ToolDescriptor>();

            public Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ToolResult(IsError: true, "{\"error\":\"no IDE tools in this host\"}"));
        }

        private sealed class AutoAllowPermissionHandler : IPermissionHandler
        {
            public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
            {
                var choice = request.Options.FirstOrDefault(o =>
                                 o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways)
                             ?? request.Options.FirstOrDefault();

                return Task.FromResult(new PermissionDecision(choice?.OptionId ?? "allow", Cancelled: choice is null));
            }
        }
    }
}
