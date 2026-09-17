using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using CodeWicket.Core.Ide;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Captures editor/solution context from the live IDE via DTE, plus diagnostics from the
    /// Error List (SVsErrorList). Runs on the UI thread.
    /// </summary>
    public sealed class VsWorkspaceContext : IWorkspaceContext
    {
        private const int MaxInlineSelectionChars = 4000;
        private const int MaxDiagnosticsScanned = 1000;
        private const int MaxDiagnosticsReturned = 200;

        private readonly DTE2 _dte;
        private readonly IErrorList _errorList;                          // for the source-view scope; may be null.
        private readonly Func<ITableManager> _errorTableManagerAccessor; // resolves the errors table lazily (MEF).
        private ITableManager _resolvedErrorTableManager;

        public VsWorkspaceContext(DTE2 dte, string rootPath, IErrorList errorList, Func<ITableManager> errorTableManagerAccessor = null)
        {
            _dte = dte;
            RootPath = rootPath;
            _errorList = errorList;
            _errorTableManagerAccessor = errorTableManagerAccessor;
        }

        /// <summary>
        /// The errors table, resolved from MEF on first use. A null answer is deliberately NOT cached: it
        /// can be unavailable now and available later, and caching it would leave the context block with no
        /// diagnostics for the whole session with no way back.
        /// </summary>
        private ITableManager ErrorTableManager
        {
            get
            {
                if (_resolvedErrorTableManager is null)
                {
                    try { _resolvedErrorTableManager = _errorTableManagerAccessor?.Invoke(); }
                    catch { _resolvedErrorTableManager = null; }
                    if (_resolvedErrorTableManager is null)
                    {
                        try { _resolvedErrorTableManager = _errorList?.TableControl?.Manager; }
                        catch { _resolvedErrorTableManager = null; }
                    }
                }
                return _resolvedErrorTableManager;
            }
        }

        public string RootPath { get; private set; }

        /// <summary>Updates the workspace root in place (e.g. when the open solution changes).</summary>
        internal void SetRootPath(string rootPath) => RootPath = rootPath;

        public async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            string solutionName = null;
            var solution = _dte.Solution;
            if (solution != null && !string.IsNullOrEmpty(solution.FullName))
                solutionName = Path.GetFileNameWithoutExtension(solution.FullName);

            string activeFilePath = null;
            TextSelection selection = null;
            var activeDocument = _dte.ActiveDocument;
            if (activeDocument != null)
            {
                activeFilePath = activeDocument.FullName;
                if (activeDocument.Selection is EnvDTE.TextSelection ts && ts.TopPoint != null)
                {
                    var top = ts.TopPoint;
                    var bottom = ts.BottomPoint;
                    var text = ts.Text;
                    var inlineText = string.IsNullOrEmpty(text) || text.Length > MaxInlineSelectionChars ? null : text;
                    selection = new TextSelection(
                        activeFilePath,
                        top.Line, top.LineCharOffset,
                        bottom.Line, bottom.LineCharOffset,
                        inlineText);
                }
            }

            var openFiles = new List<string>();
            foreach (EnvDTE.Document document in _dte.Documents)
            {
                if (!string.IsNullOrEmpty(document.FullName))
                    openFiles.Add(document.FullName);
            }

            var diagnostics = ReadDiagnostics(activeFilePath, openFiles);
            return new WorkspaceSnapshot
            {
                SolutionName = solutionName,
                ActiveFilePath = activeFilePath,
                Selection = selection,
                OpenFilePaths = openFiles,
                Diagnostics = diagnostics.Selected,
                TotalErrorCount = diagnostics.TotalErrors,
                TotalWarningCount = diagnostics.TotalWarnings,
                OmittedDiagnosticCount = diagnostics.Omitted,
                DebugSession = ReadDebugSession(),
            };
        }

        /// <summary>
        /// Whether the user is debugging, and where they are stopped. Null when they are not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two sources, each for what it is cheap at.</b> The MODE comes from
        /// <c>Debugger.CurrentMode</c> — one COM property, and always accurate. It is NOT tracked from
        /// the AD7 event stream, which has stop events but no matching "resumed" event: a state machine
        /// built on that drifts, and a stale "stopped" is precisely the confident wrong answer this
        /// issue keeps producing. The LOCATION comes from the cached stop
        /// (<see cref="Ad7DebugEvents"/>), which costs nothing because the debugger already told us.
        /// </para>
        /// <para>
        /// This runs on EVERY prompt, so nothing here may walk the stack or read a value. One frame,
        /// already in hand, and a mode flag.
        /// </para>
        /// </remarks>
        private DebugSessionInfo ReadDebugSession()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // NOT DEBUGGING COSTS ONE PROPERTY READ, and that is the constraint: this runs on every
                // prompt, and the overwhelming majority of prompts happen with no debugger at all. Both
                // early returns are before anything walks a stack or enumerates a breakpoint.
                var mode = _dte?.Debugger?.CurrentMode ?? EnvDTE.dbgDebugMode.dbgDesignMode;
                if (mode == EnvDTE.dbgDebugMode.dbgDesignMode)
                    return null;

                if (mode != EnvDTE.dbgDebugMode.dbgBreakMode)
                    return new DebugSessionInfo(IsStopped: false);

                // Stopped: a frame and a stop reason are worth paying for, but the price is measured
                // rather than assumed - "some extra work while debugging is fine, if the cost is not
                // significant" is only a decision once someone can see the number.
                var clock = System.Diagnostics.Stopwatch.StartNew();

                // ONE frame. Frames() would walk up to twenty, each with a document-context lookup, for
                // nineteen this block will never name.
                var top = Ad7Evaluator.TopFrame();

                var session = new DebugSessionInfo(IsStopped: true)
                {
                    File = top?.File,
                    Line = top?.Line ?? 0,
                    Method = top?.Method,
                    Reason = VsDebugState.DescribeStop(_dte, top?.File, top?.Line ?? 0, conversationId: null),
                    Thread = VsDebugState.ThreadName(_dte),
                    ProcessId = TryDebuggeeProcessId(),
                    StopNumber = Ad7DebugEvents.StopCount,
                };

                clock.Stop();
                try
                {
                    Core.DiagnosticLog.AppendLine(
                        System.IO.Path.Combine(ToolErrorLog.Directory, "debug-eval.log"),
                        $"[debug-context] {clock.ElapsedMilliseconds} ms (stopped, per prompt)");
                }
                catch (Exception)
                {
                    // A trace must never cost the caller their context.
                }

                return session;
            }
            catch (Exception)
            {
                // Ambient context is advisory. A debugger that will not answer costs the block a line,
                // never the prompt.
                return null;
            }
        }

        private int? TryDebuggeeProcessId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return _dte?.Debugger?.CurrentProcess?.ProcessID; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The diagnostics worth putting in front of the agent, plus honest totals for the rest.
        /// </summary>
        /// <remarks>
        /// <b>Reads the error TABLE, with the source view forced open (issue #95).</b> This used to go
        /// through <c>IVsTaskList.EnumTaskItems</c>, which inherits the Error List source dropdown just as
        /// the tool reads did (#93). Measured: with it on "IntelliSense Only" the whole
        /// <c>Diagnostics</c> section was ABSENT - and because an empty section is omitted rather than
        /// reported, the agent read a solution with no problems, on EVERY prompt. That is the worst
        /// instance of the defect, since unlike the tools it needs no call to mislead. The old path also
        /// hard-coded <c>Code: null</c>, so nothing carried a CS####/NU#### the agent could act on.
        /// <para>
        /// Both halves are requested, unlike a build read: this block wants the analyzer and live
        /// diagnostics as much as the build's. See <see cref="ErrorListViewScope"/> for the invariants, in
        /// particular that nothing inside the scope may yield the UI thread.
        /// </para>
        /// <para>
        /// <b>The full state, not the developer's current view.</b> The dropdown is a WORKAROUND for the
        /// VS re-attributing build diagnostics - the official advice is "flip to Build Only" - so reading it as
        /// scoping intent inverts what they meant. Contrast the active file and the selection, which are
        /// current by construction and so are legitimate focus signals. Which rows survive is
        /// <see cref="WorkspaceDiagnostics"/>' decision, in Core where it is testable, and it runs HERE so
        /// that only the selected rows cross the IPC boundary rather than the whole solution's.
        /// </para>
        /// </remarks>
        private WorkspaceDiagnosticSelection ReadDiagnostics(string activeFilePath, IReadOnlyList<string> openFilePaths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var all = new List<DiagnosticInfo>();
            using (ErrorListViewScope.Apply(_errorList, wantBuild: true, wantOther: true))
            using (var reader = ErrorTableReader.Open(ErrorTableManager))
            {
                foreach (var row in reader.Rows())
                {
                    if (all.Count >= MaxDiagnosticsScanned)
                        break;

                    var severity = TableValue.TryAsInt(row.GetValue(StandardTableKeyNames.ErrorSeverity), out var category)
                        ? MapCategory((__VSERRORCATEGORY)category)
                        : DiagnosticSeverity.Info;

                    all.Add(new DiagnosticInfo(
                        TableValue.AsString(row.GetValue(StandardTableKeyNames.DocumentName)) ?? string.Empty,
                        TableValue.AsOneBasedPosition(row.GetValue(StandardTableKeyNames.Line)),
                        TableValue.AsOneBasedPosition(row.GetValue(StandardTableKeyNames.Column)),
                        severity,
                        TableValue.AsString(row.GetValue(StandardTableKeyNames.Text)) ?? string.Empty,
                        TableValue.AsString(row.GetValue(StandardTableKeyNames.ErrorCode))));
                }
            }

            return WorkspaceDiagnostics.Select(all, activeFilePath, openFilePaths);
        }

        private static DiagnosticSeverity MapCategory(__VSERRORCATEGORY category) => category switch
        {
            __VSERRORCATEGORY.EC_ERROR => DiagnosticSeverity.Error,
            __VSERRORCATEGORY.EC_WARNING => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };
    }
}
