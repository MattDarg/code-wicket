using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.Ide
{
    /// <summary>
    /// The real, VS-backed <see cref="IIdeServices"/>: editor/solution context, edits through the
    /// editor, VS-native tools, and VS permission prompts. Built by <see cref="CreateAsync"/> on the
    /// UI thread; the shell hands this to its ShellRpcTarget so the engine's IIdeServices calls
    /// (which arrive over IPC on a background thread) are served against the live IDE.
    /// </summary>
    public sealed class VsIdeServices : IIdeServices
    {
        private readonly DTE2 _dte;
        private readonly IVsFolderWorkspaceService _folderWorkspace;
        private readonly VsWorkspaceContext _workspace;
        private readonly VsEditApplier _edits;
        private readonly AgentWriteLedger _writes; // may be null: a host that records no writes

        private VsIdeServices(
            DTE2 dte, IVsFolderWorkspaceService folderWorkspace, VsWorkspaceContext workspace,
            VsEditApplier edits, IToolCatalog tools, IPermissionHandler permissions, bool isDefaultWorkspace,
            AgentWriteLedger writes)
        {
            _dte = dte;
            _folderWorkspace = folderWorkspace;
            _workspace = workspace;
            _edits = edits;
            Tools = tools;
            Permissions = permissions;
            IsDefaultWorkspace = isDefaultWorkspace;
            _writes = writes;
        }

        /// <summary>
        /// A project (re)loaded: any record of the agent having written ITS project file is closed, since
        /// the loaded copy now reflects the disk (issue #257). This is the ONLY thing that closes such a
        /// record short of the solution closing — measured, the stale state has no decay of its own, and
        /// the user's click that forces the reload arrives here as an unload/load pair.
        /// </summary>
        private void NoteProjectLoaded(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_writes is null || hierarchy is null)
                return;
            var projectFile = TryGetProjectFile(hierarchy);
            if (string.IsNullOrEmpty(projectFile))
                return;
            _writes.Forget(projectFile);
            // And the reload itself is recorded: an import above this project was taken by THIS reload
            // and by no other project's, so the import note must drop this project and keep the rest.
            _writes.NoteReloaded(projectFile);
        }

        private static string TryGetProjectFile(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (hierarchy is IVsProject project
                    && ErrorHandler.Succeeded(project.GetMkDocument(VSConstants.VSITEMID_ROOT, out var document))
                    && !string.IsNullOrEmpty(document))
                    return document;
                if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(VSConstants.VSITEMID_ROOT, out var name)))
                    return name;
            }
            catch (Exception)
            {
                // A hierarchy that will not name its file has no record to close.
            }
            return null;
        }

        public IWorkspaceContext Workspace => _workspace;
        public IEditApplier Edits => _edits;
        public IToolCatalog Tools { get; }
        public IPermissionHandler Permissions { get; }

        /// <summary>True when no solution/folder was open, so the agent is scoped to the default
        /// workspace (<see cref="DefaultWorkspaceRoot"/>) rather than a real project. The chat surfaces
        /// this so the scope is never a surprise.</summary>
        public bool IsDefaultWorkspace { get; private set; }

        /// <summary>The current workspace root (the open solution's directory, or the default workspace).</summary>
        public string WorkspaceRoot => _workspace.RootPath;

        /// <summary>
        /// Re-resolves the workspace root from the current solution or open folder (falling back to the
        /// default workspace when neither is open) and updates the workspace context + edit applier in
        /// place. Returns the new root, whether it's the default, and whether it changed. Call on the UI
        /// thread when the solution/folder opens or closes so the agent's working directory tracks it —
        /// the session is captured at start, so a change means the next session must restart in the new root.
        /// </summary>
        public (string Root, bool IsDefault, bool Changed) UpdateWorkspaceRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var (root, isDefault) = ResolveWorkspaceRoot(_dte, _folderWorkspace);
            var changed = !string.Equals(root, _workspace.RootPath, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                _workspace.SetRootPath(root);
                _edits.SetRootPath(root);
                IsDefaultWorkspace = isDefault;
            }

            return (root, isDefault, changed);
        }

        /// <summary>
        /// Tells the edit applier which directory the agent is actually running in, so its relative paths
        /// resolve the way the agent meant them. Shell-only, like the diff preview: the session reports
        /// its working directory when it starts and the host pushes it here — no IPC, since the applier
        /// lives in this process. Null/empty (no session yet, or a backend reporting none) restores the
        /// solution-root fallback rather than pinning a stale root from a previous session.
        /// </summary>
        /// <remarks>
        /// The two differ whenever <c>WorkspaceRootLocator</c> widens past the solution folder for a
        /// backend's workspace marker (issue #54) — and the failure is silent in both directions, because
        /// resolving against the wrong root finds a DIFFERENT file rather than none. See
        /// <c>VsEditApplier.SetAgentRootPath</c>.
        /// </remarks>
        public void SetAgentWorkingDirectory(string workingDirectory)
        {
            _edits.SetAgentRootPath(workingDirectory);
            // The breakpoint tools root relative paths the same way, and for the same reason: resolving
            // against the wrong directory names a DIFFERENT REAL FILE rather than failing. The two must
            // move together or an edit and a breakpoint in one turn can land in different files.
            (Tools as VsToolCatalog)?.SetAgentWorkingDirectory(workingDirectory);
        }

        /// <summary>
        /// Tells the tool catalog which conversation is running, so breakpoints it sets are tagged with it
        /// and <c>clear_breakpoints</c> can tell this conversation's from an earlier one's (issue #73).
        /// </summary>
        /// <remarks>
        /// Shell-only and in-proc, exactly like <see cref="SetAgentWorkingDirectory"/> — no IPC, because
        /// the catalog lives in this process. Push it on session start, on restoring a conversation and on
        /// New: a stale value does not fail, it files the breakpoints under the previous conversation, and
        /// clearing then declines to remove ones it did in fact set.
        /// </remarks>
        public void SetConversationId(string conversationId)
        {
            // Kept here as well as pushed down, because the debug capture needs it too: it is what
            // lets a stop at an agent-set breakpoint say whether THIS conversation set it (issue #73,
            // rung 1's tag read by rung 2).
            _conversationId = conversationId;
            (Tools as VsToolCatalog)?.SetConversationId(conversationId);
        }

        private string _conversationId;

        /// <summary>
        /// Whether the debugger is stopped, so the "attach debug context" gesture is worth offering
        /// (issue #73, rung 2). Call on the UI thread.
        /// <para>Shell-only, like <see cref="OpenFileAtLineAsync"/>: it runs in-proc in the VS process
        /// and terminates there, so it needs no IPC and no <c>IIdeServices</c> surface. The agent has
        /// no business asking this - the whole point of rung 2 is that the USER decides what to hand
        /// over.</para>
        /// </summary>
        public bool IsDebuggerStopped()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VsDebugState.IsInBreakMode(_dte);
        }

        /// <summary>
        /// The call stack and locals the debugger is showing, or null when it is not stopped. Call on
        /// the UI thread.
        /// <para>Null is the RACE answer as well as the "never started" one, and the caller must treat
        /// it as such: <see cref="IsDebuggerStopped"/> can be true when a menu opens and false by the
        /// time the item is clicked.</para>
        /// <para>Shell-only for the same reason as <see cref="IsDebuggerStopped"/>. The capture walks
        /// the stack by SELECTING each frame, which moves the user's own frame selection and restores
        /// it - so it must not be re-entered concurrently, and being reachable only from a UI gesture
        /// on the UI thread is what guarantees that.</para>
        /// </summary>
        public DebugStateCapture CaptureDebugState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VsDebugState.Capture(_dte, _conversationId);
        }

        /// <summary>
        /// Whether the Output window has an active pane with something in it, so the "attach the
        /// output" gesture is worth offering. Call on the UI thread.
        /// <para>Shell-only, and for the sharper version of <see cref="IsDebuggerStopped"/>'s reason:
        /// there is no tool that reads an arbitrary output pane, so this is not a fact the agent could
        /// ask for by another route even if it should. WHICH pane matters is the user's to say, and
        /// they say it by having it on screen.</para>
        /// </summary>
        public bool HasOutputPaneContent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VsOutputPane.HasContent(_dte);
        }

        /// <summary>
        /// The active Output-window pane as a capture — the user's selection in it where there is one,
        /// the tail of it otherwise — or null when there is nothing to take. Call on the UI thread.
        /// <para>Null is the RACE answer as well as the "nothing there" one, exactly as
        /// <see cref="CaptureDebugState"/>'s is: a build starting clears the pane, and it can do that
        /// between the menu opening and the click.</para>
        /// </summary>
        public OutputPaneCapture CaptureOutputPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VsOutputPane.Capture(_dte);
        }

        /// <summary>
        /// Opens <paramref name="path"/> in the editor and navigates to <paramref name="line"/> (1-based).
        /// Backs the test-results card's "go to source". UI-thread marshaled and best-effort — a missing
        /// file or a navigation hiccup just no-ops. Shell-only (not on <see cref="IIdeServices"/>): it runs
        /// in-proc in the VS process, like the diff preview, so it needs no IPC or Core surface.
        /// </summary>
        public async Task OpenFileAtLineAsync(string path, int line)
        {
            if (string.IsNullOrEmpty(path))
                return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (!File.Exists(path))
                    return;
                var window = _dte.ItemOperations.OpenFile(path);
                window?.Activate();
                if (line > 0 && _dte.ActiveDocument?.Selection is EnvDTE.TextSelection selection)
                    selection.GotoLine(line, false);
            }
            catch
            {
                // Navigation is best-effort; never throw into the UI.
            }
        }

        /// <summary>
        /// Opens <paramref name="path"/> at the edit described by <paramref name="oldText"/>/
        /// <paramref name="newText"/> rather than at the top — the edit card's "Open file". The line is
        /// resolved by finding that change in the file as it stands now (see
        /// <see cref="VsEditApplier.ResolveDiffLineAsync"/>), so an edit that has since been pushed down
        /// by later work is still landed on; <paramref name="reportedLine"/> is only the fallback.
        /// Shell-only, like <see cref="OpenFileAtLineAsync"/> — a UI gesture that terminates in this
        /// process, so it needs no IPC or Core surface.
        /// </summary>
        public async Task OpenFileAtDiffAsync(string path, string oldText, string newText, int? reportedLine = null)
        {
            if (string.IsNullOrEmpty(path))
                return;
            var line = await _edits.ResolveDiffLineAsync(path, oldText, newText, reportedLine).ConfigureAwait(false);
            await OpenFileAtLineAsync(path, line);
        }

        /// <summary>
        /// Subscribes to workspace changes — a <c>.sln</c> solution opening/closing AND an "Open Folder"
        /// workspace opening/closing — and invokes <paramref name="onChanged"/>(newRoot, isDefault) on the
        /// UI thread whenever the resolved root actually moves (the IDE services are repointed in place
        /// first). Reconciles once immediately, to catch a workspace that finished loading during startup.
        /// Dispose the returned token to unsubscribe. Call on the UI thread.
        /// <para><paramref name="onSolutionOpening"/> fires when the shell BEGINS opening a solution,
        /// which the host needs because there is no reload event: a reload reaches us as a close and
        /// then an open, and this is the only prompt signal that the close was half of one. It arrives
        /// before the projects load; the matching <c>onChanged</c> arrives only after they have.</para>
        /// </summary>
        public IDisposable SubscribeWorkspaceChanged(Action<string, bool> onChanged, Action onSolutionOpening)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (onChanged is null)
                throw new ArgumentNullException(nameof(onChanged));
            if (onSolutionOpening is null)
                throw new ArgumentNullException(nameof(onSolutionOpening));

            return new WorkspaceChangeSubscription(this, _folderWorkspace, onChanged, onSolutionOpening);
        }

        /// <summary>
        /// The workspace used when no solution/folder is open — <see cref="StoragePaths.DefaultAgentWorkspace"/>,
        /// named there so the permission guard that exempts it (<see cref="ProtectedPaths"/>) and the
        /// host that hands it out cannot spell it differently.
        /// </summary>
        public static string DefaultWorkspaceRoot => StoragePaths.DefaultAgentWorkspace;

        /// <summary>
        /// Resolves DTE via the VS global service providers, derives the workspace root from the open
        /// solution (falling back to <see cref="DefaultWorkspaceRoot"/>), and builds the VS-backed services.
        /// </summary>
        /// <param name="enableRunCommand">Registers the <c>run_command</c> tool (config
        /// <c>RunCommandEnabled</c>, default on); the catalog is snapshotted per session, so this
        /// applies when the chat window next opens. The parameter default here is deliberately NOT the
        /// product default: the one real caller passes the config value explicitly, so an OMITTED
        /// argument is test/plumbing convenience and stays the conservative answer for a tool that runs
        /// command lines.</param>
        /// <param name="writes">
        /// The ledger the shell records the agent's file writes into, which the build and test tools
        /// check against the loaded projects (issue #257). Optional; null disables that check.
        /// </param>
        public static async Task<VsIdeServices> CreateAsync(bool enableRunCommand = false, AgentWriteLedger writes = null, CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dte = (DTE2)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SDTE));
            if (dte is null)
                throw new InvalidOperationException("Could not obtain the DTE service.");

            // The Error List, as IErrorList: the source-view scope both diagnostics readers need, and the
            // fallback route to the error table's manager when MEF cannot supply it (the primary route is
            // the accessor below). Optional - null just yields no Error List data. Its IVsTaskList view is
            // deliberately not used: it follows the source dropdown and carries no error codes.
            var errorListService = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsErrorList));
            var errorListTable = errorListService as IErrorList;

            // Editor adapters (for buffer-level minimal edits); optional — null falls back to DTE writes.
            var componentModel = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();

            // Diff viewer (for edit previews); optional — null just skips the preview.
            var diffService = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsDifferenceService)) as IVsDifferenceService;

            // Folder ("Open Folder") workspace service; optional — null just means folder roots aren't
            // detected (a missing export shouldn't break the rest of the IDE services).
            IVsFolderWorkspaceService folderWorkspace = null;
            try { folderWorkspace = componentModel?.GetService<IVsFolderWorkspaceService>(); }
            catch { /* service unavailable */ }

            // Roslyn workspace (semantic tools) and VS's code-fix providers (apply_code_fix), both handed
            // over as ACCESSORS rather than resolved here — this method runs on the UI thread, and both
            // of these force MEF composition: GetService<VisualStudioWorkspace> brings up Roslyn's
            // workspace, and GetExtensions<CodeFixProvider> is the ImportMany equivalent, so it
            // instantiates every code-fix export in the catalog and loads each contributing assembly.
            //
            // Resolving them here froze the whole IDE on a machine whose MEF composition cache was cold
            // — which is precisely the state a VS update leaves it in, and why this reproduced only
            // after an update and only on some machines. The chat window's init starts with
            // JoinableTaskFactory.RunAsync and therefore runs inline on the UI thread up to the first
            // real yield, so the composition (and the extension assembly loads behind it) was holding
            // the thread that VS's own solution load needs to make progress.
            //
            // The catalog resolves each on first use and caches the answer. Nothing else here composes
            // MEF: the two GetService calls below are for lightweight editor/workspace services that are
            // already composed in any running IDE.
            Func<VisualStudioWorkspace> roslynWorkspaceAccessor =
                componentModel is null
                    ? null
                    : () => { try { return componentModel?.GetService<VisualStudioWorkspace>(); } catch { return null; } };

            Func<IReadOnlyList<CodeFixProvider>> codeFixProvidersAccessor =
                componentModel is null
                    ? null
                    : () => { try { return componentModel?.GetExtensions<CodeFixProvider>()?.ToArray(); } catch { return null; } };

            // The errors TABLE, independent of the Error List window and of its filter (issue #93). Lazy
            // like the two above — ITableManagerProvider is a MEF export, and this method runs on the UI
            // thread. Resolving the provider is cheap next to Roslyn's workspace, but "cheap MEF" is what
            // the freeze taught us not to assume; the tools that need it already run off the UI thread.
            Func<Microsoft.VisualStudio.Shell.TableManager.ITableManager> errorTableManagerAccessor =
                componentModel is null
                    ? null
                    : () =>
                    {
                        try
                        {
                            return componentModel?
                                .GetService<Microsoft.VisualStudio.Shell.TableManager.ITableManagerProvider>()
                                ?.GetTableManager(Microsoft.VisualStudio.Shell.TableManager.StandardTables.ErrorsTable);
                        }
                        catch { return null; }
                    };

            var (rootPath, isDefaultWorkspace) = ResolveWorkspaceRoot(dte, folderWorkspace);

            return new VsIdeServices(
                dte,
                folderWorkspace,
                new VsWorkspaceContext(dte, rootPath, errorListTable, errorTableManagerAccessor),
                new VsEditApplier(dte, rootPath, ServiceProvider.GlobalProvider, adapters, diffService),
                new VsToolCatalog(dte, errorListTable, roslynWorkspaceAccessor, codeFixProvidersAccessor, enableRunCommand, errorTableManagerAccessor, writes),
                new VsPermissionHandler(ServiceProvider.GlobalProvider),
                isDefaultWorkspace,
                writes);
        }

        private static (string Root, bool IsDefault) ResolveWorkspaceRoot(DTE2 dte, IVsFolderWorkspaceService folderWorkspace)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A real .sln/solution wins.
            var solution = dte.Solution;
            if (solution != null && !string.IsNullOrEmpty(solution.FullName))
                return (Path.GetDirectoryName(solution.FullName), false);

            // "Open Folder" mode: no .sln, but the folder is a genuine working directory.
            var folder = folderWorkspace?.CurrentWorkspace?.Location;
            if (!string.IsNullOrEmpty(folder))
                return (folder, false);

            Directory.CreateDirectory(DefaultWorkspaceRoot);
            return (DefaultWorkspaceRoot, true);
        }

        /// <summary>
        /// Watches both the <c>.sln</c> solution (<see cref="IVsSolutionEvents"/>) and the folder
        /// workspace (<see cref="IVsFolderWorkspaceService.OnActiveWorkspaceChanged"/>), funneling either
        /// into a single re-resolve. Notifies only when the root actually moves. Disposing unadvises both.
        /// </summary>
        private sealed class WorkspaceChangeSubscription : IVsSolutionEvents, IVsSolutionLoadEvents, IDisposable
        {
            private readonly VsIdeServices _owner;
            private readonly IVsFolderWorkspaceService _folderWorkspace;
            private readonly Action<string, bool> _onChanged;
            private readonly Action _onSolutionOpening;
            private readonly IVsSolution _solution;
            private uint _solutionCookie;
            private bool _disposed;

            public WorkspaceChangeSubscription(
                VsIdeServices owner, IVsFolderWorkspaceService folderWorkspace,
                Action<string, bool> onChanged, Action onSolutionOpening)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                _owner = owner;
                _folderWorkspace = folderWorkspace;
                _onChanged = onChanged;
                _onSolutionOpening = onSolutionOpening;

                _solution = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution)) as IVsSolution;
                _solution?.AdviseSolutionEvents(this, out _solutionCookie);

                if (_folderWorkspace is not null)
                    _folderWorkspace.OnActiveWorkspaceChanged += OnActiveWorkspaceChangedAsync;

                // Catch a workspace that finished loading during async startup, before we advised.
                Raise();
            }

            // Folder open/close fires off the UI thread (it's an async event) — marshal back first.
            private async Task OnActiveWorkspaceChangedAsync(object sender, EventArgs e)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Raise();
            }

            private void Raise()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_disposed)
                    return;

                var (root, isDefault, changed) = _owner.UpdateWorkspaceRoot();
                if (changed)
                    _onChanged(root, isDefault);
            }

            // .sln open/close — these fire on the UI thread.
            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Raise();
                return VSConstants.S_OK;
            }

            public int OnAfterCloseSolution(object pUnkReserved)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                // No loaded project can be stale against a write once the solution is gone; the next
                // open loads every project from disk.
                _owner._writes?.Clear();
                Raise();
                return VSConstants.S_OK;
            }

            // A project arriving loaded — at solution open, on a reload from the "project has been
            // modified" prompt (an unload/load pair), or on Reload Project — closes the record of its
            // project file having been written (issue #257).
            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!_disposed)
                    _owner.NoteProjectLoaded(pHierarchy);
                return VSConstants.S_OK;
            }

            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;

            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!_disposed)
                    _owner.NoteProjectLoaded(pRealHierarchy);
                return VSConstants.S_OK;
            }
            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
            public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

            // IVsSolutionLoadEvents — the shell QIs this sink for it off the same AdviseSolutionEvents
            // registration, so there is no second advise to keep in step.
            //
            // OnBeforeOpenSolution is the whole reason we implement the interface: a solution RELOAD (VS
            // does one whenever the .sln changes on disk, which a git branch switch does) arrives as a
            // close followed by an open, with no event saying the two are one gesture. The close alone
            // reads as "the user has no solution open", and acting on it retired the chat session for a
            // solution that never moved. This says an open has BEGUN — promptly, unlike
            // OnAfterOpenSolution, which waits for every project to load — so the host can hold the
            // close rather than act on it. It fires for an ordinary open too, where the host has nothing
            // held and nothing happens.
            public int OnBeforeOpenSolution(string pszSolutionFilename)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!_disposed)
                    _onSolutionOpening();
                return VSConstants.S_OK;
            }

            public int OnBeforeBackgroundSolutionLoadBegins() => VSConstants.S_OK;

            public int OnQueryBackgroundLoadProjectBatch(out bool pfShouldDelayLoadToNextIdle)
            {
                pfShouldDelayLoadToNextIdle = false;
                return VSConstants.S_OK;
            }

            public int OnBeforeLoadProjectBatch(bool fIsBackgroundIdleBatch) => VSConstants.S_OK;
            public int OnAfterLoadProjectBatch(bool fIsBackgroundIdleBatch) => VSConstants.S_OK;
            public int OnAfterBackgroundSolutionLoadComplete() => VSConstants.S_OK;

            public void Dispose()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_disposed)
                    return;
                _disposed = true;

                if (_solution is not null && _solutionCookie != 0)
                {
                    try { _solution.UnadviseSolutionEvents(_solutionCookie); }
                    catch { /* best effort on teardown */ }
                    _solutionCookie = 0;
                }

                if (_folderWorkspace is not null)
                {
                    try { _folderWorkspace.OnActiveWorkspaceChanged -= OnActiveWorkspaceChangedAsync; }
                    catch { /* best effort on teardown */ }
                }
            }
        }
    }
}
