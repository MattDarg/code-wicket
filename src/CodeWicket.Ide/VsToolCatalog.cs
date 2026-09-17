using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using EnvDTE;
using EnvDTE80;
// Core's breakpoint rules, aliased because EnvDTE has a Breakpoints of its own and this file uses both.
// Aliasing rather than renaming Core: EnvDTE.Breakpoints is the collection on the debugger, Core's is the
// request/identity logic, and the two names are each right in their own project.
using AgentBreakpoints = CodeWicket.Core.Ide.Breakpoints;
using Microsoft.VisualStudio;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Threading;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using CodeDocument = Microsoft.CodeAnalysis.Document;
using CodeSolution = Microsoft.CodeAnalysis.Solution;
using DiagnosticSeverity = CodeWicket.Core.Ide.DiagnosticSeverity;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.Ide
{
    /// <summary>
    /// VS-native tools exposed to the agent (surfaced to the backend as MCP — see the engine's
    /// McpToolServer + named-pipe bridge). These deliberately cover only what the IDE can do that a
    /// CLI agent cannot do as well: build the loaded solution exactly as VS does, read the live
    /// Error List (including IntelliSense/analyzer diagnostics that never appear in a command-line
    /// build), and answer <em>semantic</em> find-references from VS's live Roslyn workspace (precise
    /// across projects/overloads/inheritance, unlike the text grep a CLI agent would run). We do NOT
    /// ship our own Roslyn — <c>find_references</c> queries the <see cref="VisualStudioWorkspace"/>
    /// devenv already maintains (compile-only package refs; VS provides the runtime assemblies), so
    /// there is no second Roslyn to conflict with VS's. Text-only tools (grep/read/replace) are still
    /// omitted: capable ACP agents do those well themselves.
    /// </summary>
    public sealed class VsToolCatalog : IToolCatalog
    {
        private const int MaxDiagnosticsScanned = 1000;
        private const int MaxDiagnosticsReturned = 50;  // detailed rows in a get_diagnostics result (the rest are summarized by code)
        private const int MaxErrorsReported = 50;        // detailed error rows in a build_solution result
        private const int MaxBuildOutputChars = 8000;    // tail of the Build output pane included only when a failed build left no structured rows
        private const int MaxCodeCounts = 30;            // distinct codes listed in the by-code summary
        private const int MaxMessageLength = 400;        // per-diagnostic message cap (defends against pathological analyzer messages)
        /// <summary>
        /// Where TRX output from <c>run_tests</c> lands, under LocalAppData — NOT temp, which is
        /// EDR-sensitive (project policy). One member because the two callers spelled it out
        /// independently about 1200 lines apart, so a change to one was a change to neither.
        /// </summary>
        private static string TestResultsRoot => Path.Combine(StoragePaths.Local, "testresults");

        private const int MaxSymbolsReported = 10;
        private const int MaxSymbolMatchesReported = 25;
        private const int MaxReferencesPerSymbol = 200;
        private const int MaxImplementationsPerSymbol = 200;
        private const int MaxRenameFilesListed = 200;
        private const int MaxFixChoicesListed = 25;
        private const int MaxSourceLineLength = 200;
        private const int MaxDocLength = 500;
        private const int MaxFailuresReported = 50;      // detailed failing-test rows in a run_tests result
        private const int MaxSkippedReported = 50;       // named skipped tests in a run_tests result
        private const int MaxStackTraceLength = 1500;    // per-failure stack trace cap
        // Cross-project fan-out for the analyzer pass: half the cores (min 1), so a solution-wide
        // analyzer run stays bounded and devenv's UI thread (and the threadpool work it JTF-joins)
        // keeps breathing room (issue #39). See AddAnalyzerDiagnosticsAsync for why the bound lives
        // at the project level rather than inside Roslyn's executor.
        private static readonly int AnalyzerProjectParallelism = Math.Max(1, Environment.ProcessorCount / 2);
        private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan TestRunTimeout = TimeSpan.FromMinutes(15); // per dotnet invocation

        // Signature for a matched symbol, e.g. "Namespace.Type.Method(int)" — enough for the agent to
        // tell overloads apart without the noise of full parameter/return qualification.
        private static readonly SymbolDisplayFormat SignatureFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

        // Rich signature for find_symbol: accessibility + modifiers + return/parameter types + names +
        // default values + the type keyword (class/interface/enum...), e.g.
        // "public async System.Threading.Tasks.Task<int> MyType.ExecuteAsync(string name, int count = 0)".
        private static readonly SymbolDisplayFormat DefinitionFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeModifiers
                | SymbolDisplayMemberOptions.IncludeAccessibility,
            kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        // Compact form for base types / implemented interfaces, e.g. "IReadOnlyList<string>".
        private static readonly SymbolDisplayFormat TypeRefFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

        // Drop null members (e.g. a metadata symbol has no source definition; a method has no baseType).
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly DTE2 _dte;
        private readonly IErrorList _errorList; // VS Error List window; the fallback route to the error table's manager when MEF is unavailable. May be null.
        private readonly Func<ITableManager> _errorTableManagerAccessor; // resolves the errors table from MEF lazily; see the ErrorTableManager property.
        private ITableManager _resolvedErrorTableManager; // cached once resolved NON-null; see the ErrorTableManager property.
        private bool _errorTableManagerResolved;
        private readonly Func<VisualStudioWorkspace> _workspaceAccessor; // non-null => the semantic tools are registered; see the Workspace property.
        private readonly Lazy<VisualStudioWorkspace> _workspace; // resolves VS's live Roslyn workspace on first use (MEF).
        private readonly Func<IReadOnlyList<CodeFixProvider>> _codeFixProvidersAccessor; // non-null => apply_code_fix is registered.
        private readonly Lazy<IReadOnlyList<CodeFixProvider>> _codeFixProviders; // resolves VS's code-fix providers on first use (MEF).
        private readonly AgentWriteLedger _writes; // the agent's file writes, checked against the loaded projects after a build (issue #257); may be null.

        /// <summary>
        /// VS's live Roslyn workspace, resolved from MEF on FIRST USE rather than at construction, and
        /// cached (including a null answer). Null => the semantic tools report themselves unavailable and
        /// <c>get_diagnostics</c> has no compiler-diagnostic source.
        /// </summary>
        /// <remarks>
        /// Laziness here is not a micro-optimisation, it is the fix for a whole-IDE freeze. This catalog is
        /// built by <see cref="VsIdeServices.CreateAsync"/> while the chat tool window is opening, which
        /// runs ON THE UI THREAD (the window's init is started with JoinableTaskFactory.RunAsync, so it
        /// executes inline until the first real yield). Asking MEF for this export forces Roslyn's
        /// workspace to compose, synchronously, on that thread — and a VS update invalidates the MEF
        /// composition cache, so the first launches afterwards rebuild it by scanning every extension
        /// assembly. Holding the UI thread through that starves the very solution load the composition is
        /// waiting on: reported as "switching to the chat tab freezes the whole of VS", and not
        /// reproducible on a machine whose cache was already warm.
        /// <para>
        /// Resolving on first use moves that cost onto a background tool invocation the user actually
        /// asked for, where it is attributable and cannot deadlock against solution load. It also FIXES a
        /// second bug: the eager snapshot ran before the language services were necessarily up, and a null
        /// answer then disabled the semantic tools for the whole session with no way back.
        /// </para>
        /// <para>
        /// NOTE this moves the MEF resolution off the UI thread (handlers arrive on threadpool threads and
        /// marshal on explicitly where they need to). That is deliberate and consistent with how this file
        /// already treats these exports: the semantic tools and apply_code_fix INVOKE the workspace and the
        /// providers off the UI thread already — "Resolution runs off the UI thread; only TryApplyChanges
        /// is marshalled onto it" — so if calling into them there is safe, constructing them there is too.
        /// Marshalling the resolution back onto the UI thread would merely relocate the stall rather than
        /// remove it. Resolution is unsynchronised, matching the operation-state accessor below: a race
        /// costs a duplicate GetService (idempotent and thread-safe), never a torn answer.
        /// </para>
        /// </remarks>
        private VisualStudioWorkspace Workspace => _workspace.Value;

        /// <summary>
        /// VS's live code-fix providers, resolved from MEF on first use and cached. Null/empty =>
        /// <c>apply_code_fix</c> reports itself unavailable.
        /// </summary>
        /// <remarks>
        /// The heaviest of the three lazy accessors and the reason this change exists: the underlying
        /// <c>GetExtensions&lt;CodeFixProvider&gt;()</c> is the ImportMany equivalent, so it INSTANTIATES
        /// every code-fix export in the entire VS catalog — Roslyn's own plus every installed extension's
        /// — loading each contributing assembly. That is a per-machine cost (it scales with what the
        /// user's IT ships, which is why one developer sees no problem and another cannot open the pane),
        /// and it was being paid on the UI thread at window open for a tool most sessions never call.
        /// </remarks>
        private IReadOnlyList<CodeFixProvider> CodeFixProviders => _codeFixProviders.Value;
        /// <summary>
        /// The manager for VS's errors table (<see cref="StandardTables.ErrorsTable"/>) — the object the
        /// diagnostics reads enumerate sources from — resolved from MEF on first use and cached, for the
        /// reason the accessors above are lazy.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT reached through the Error List window. The window's control is a CONSUMER of
        /// this same manager that applies the user's filter on top, which is issue #93; and asking the
        /// manager directly also means the tools work when that window has never been opened. The
        /// <see cref="IErrorList"/> route survives only as a fallback for a devenv that gave us no
        /// component model at all — it reaches the identical manager, just via the window.
        /// </remarks>
        private ITableManager ErrorTableManager
        {
            get
            {
                // A null answer is NOT cached, unlike the Roslyn/code-fix accessors above. Those resolve
                // exports that exist once MEF has composed, so "unavailable" is a stable answer worth
                // remembering. This one can legitimately be unavailable now and available later — the
                // shared errors table and its sources are not necessarily up when the first tool call
                // lands — and caching that null disabled diagnostics for the WHOLE session with no way
                // back, which is the exact bug the Workspace accessor's remarks describe getting caught by.
                if (!_errorTableManagerResolved || _resolvedErrorTableManager is null)
                {
                    _errorTableManagerResolved = true;
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

        private readonly Dictionary<string, Func<string, CancellationToken, Task<ToolResult>>> _handlers;

        public VsToolCatalog(DTE2 dte, IErrorList errorList, Func<VisualStudioWorkspace> workspaceAccessor = null, Func<IReadOnlyList<CodeFixProvider>> codeFixProvidersAccessor = null, bool enableRunCommand = false, Func<ITableManager> errorTableManagerAccessor = null, AgentWriteLedger writes = null)
        {
            _dte = dte;
            _errorList = errorList;
            _errorTableManagerAccessor = errorTableManagerAccessor;
            _writes = writes;
            _workspaceAccessor = workspaceAccessor;
            _codeFixProvidersAccessor = codeFixProvidersAccessor;

            // PublicationOnly is the mode the remarks on Workspace already describe: the factory may run
            // more than once under a race, the first result wins, and the value is PUBLISHED ATOMICALLY -
            // so a duplicate GetService is possible and a torn answer is not. The hand-rolled pair could
            // not honour that second half: it set its `resolved` flag BEFORE the invoke, so a second tool
            // call arriving during MEF composition (slow by construction, which is the whole reason this
            // is lazy) saw "resolved" with a null value and reported the semantic tools unavailable for a
            // session where they were fine.
            //
            // The catch stays INSIDE the factory deliberately. PublicationOnly does not cache exceptions
            // - measured: a throwing factory is re-invoked on every access - so letting one escape would
            // re-attempt the expensive composition on every tool call. Caught and turned into null, the
            // "asked, unavailable" answer is cached exactly as before (also measured: a returned null IS
            // cached, factory runs once).
            _workspace = new Lazy<VisualStudioWorkspace>(
                () => { try { return _workspaceAccessor?.Invoke(); } catch { return null; } },
                LazyThreadSafetyMode.PublicationOnly);
            _codeFixProviders = new Lazy<IReadOnlyList<CodeFixProvider>>(
                () => { try { return _codeFixProvidersAccessor?.Invoke(); } catch { return null; } },
                LazyThreadSafetyMode.PublicationOnly);
            _handlers = new Dictionary<string, Func<string, CancellationToken, Task<ToolResult>>>(StringComparer.Ordinal)
            {
                ["build_solution"] = BuildSolutionAsync,
                ["get_diagnostics"] = GetDiagnosticsAsync,
                ["run_tests"] = RunTestsAsync,
                ["open_file"] = OpenFileAsync,
                // Issue #73. Registered unconditionally: they need only the DTE this catalog already
                // holds — no MEF export, no optional service — so there is nothing to gate on.
                // One implementation, two names, for the reason read_expression/execute_expression are
                // two names: a condition and a printMessage are evaluated in the user's process, so the
                // name that may carry them is Command-tier and the plain one stays Edit-tier. The flag
                // is bound HERE, at the registration, not read back from the invoked name inside the
                // handler.
                [AgentBreakpoints.PlainToolName] = (a, ct) => SetBreakpointAsync(a, allowExpressions: false, ct),
                [AgentBreakpoints.ExpressionToolName] = (a, ct) => SetBreakpointAsync(a, allowExpressions: true, ct),
                ["list_breakpoints"] = ListBreakpointsAsync,
                ["clear_breakpoints"] = ClearBreakpointsAsync,
                ["get_debug_output"] = GetDebugOutputAsync,
                // One implementation, two names, and the flag is bound HERE rather than read back from
                // the invoked name inside the handler: a field holding "which tool am I" would be wrong
                // the moment two calls overlap, and this cannot be.
                ["read_expression"] = (a, ct) => EvaluateExpressionsAsync(a, allowSideEffects: false, ct),
                ["execute_expression"] = (a, ct) => EvaluateExpressionsAsync(a, allowSideEffects: true, ct),
            };

            var tools = new List<ToolDescriptor>
            {
                IdeToolDescriptors.BuildSolution, IdeToolDescriptors.GetDiagnostics, IdeToolDescriptors.RunTests, IdeToolDescriptors.OpenFile,
                IdeToolDescriptors.SetBreakpoint, IdeToolDescriptors.SetExpressionBreakpoint, IdeToolDescriptors.ListBreakpoints, IdeToolDescriptors.ClearBreakpoints,
                IdeToolDescriptors.GetDebugOutput, IdeToolDescriptors.ReadExpression, IdeToolDescriptors.ExecuteExpression,
            };

            // Config-gated (RunCommandEnabled, default ON): a disabled install never advertises the
            // tool at all — same gating philosophy as the terminal mirror, applied at catalog build
            // (the tool list is snapshotted into the agent session, so it applies on next window open).
            // The registration was never the safety boundary: run_command carries the top
            // ToolRisk.Command tier, so every permission mode below AcceptAll prompts per invocation.
            // Off therefore means "never offer it here", not "the only thing stopping it".
            if (enableRunCommand)
            {
                _handlers["run_command"] = RunCommandAsync;
                tools.Add(IdeToolDescriptors.RunCommand);
            }
            // Registration is gated on the ACCESSOR existing (i.e. VS gave us a component model at all),
            // NOT on the export resolving — resolving is what costs the UI thread, and it is exactly what
            // this catalog no longer does at construction. See the Workspace property.
            //
            // The trade is deliberate and it is a net improvement. Advertising a semantic tool that then
            // reports "the C#/VB code analysis workspace is not available" (every one of these handlers
            // already opens with that guard) costs one wasted call on an install with no language
            // services — a C++-only VS. The behaviour it REPLACES is worse and hit the common case: the
            // snapshot ran while the solution was still loading, Roslyn had not composed yet, and the
            // tools were then missing for the entire session with no way to recover.
            if (_workspaceAccessor is not null)
            {
                _handlers["find_references"] = FindReferencesAsync;
                _handlers["find_symbol"] = FindSymbolAsync;
                _handlers["find_implementations"] = FindImplementationsAsync;
                _handlers["rename_symbol"] = RenameSymbolAsync;
                tools.Add(IdeToolDescriptors.FindReferences);
                tools.Add(IdeToolDescriptors.FindSymbol);
                tools.Add(IdeToolDescriptors.FindImplementations);
                tools.Add(IdeToolDescriptors.RenameSymbol);

                if (_codeFixProvidersAccessor is not null)
                {
                    _handlers["apply_code_fix"] = ApplyCodeFixAsync;
                    tools.Add(IdeToolDescriptors.ApplyCodeFix);
                }
            }
            Tools = tools;
        }

        // --- Breakpoint tools: the two facts pushed in from the host (issue #73) ----------------------
        //
        // Both take the same route as the edit applier's agent root: the shell holds them, the shell
        // pushes them here, and nothing crosses IPC because both ends live in the devenv process. Both
        // are volatile because the push arrives on the UI thread while a tool call may read before its
        // own hop onto it.

        private volatile string _agentWorkingDirectory;
        private volatile string _conversationId;

        /// <summary>
        /// The directory the agent measures its relative paths from — its ACP session cwd, which is NOT
        /// necessarily the solution root (issue #54).
        /// </summary>
        /// <remarks>
        /// Rooting a breakpoint's path against the wrong directory does not error: it names a DIFFERENT
        /// REAL FILE, so the breakpoint binds somewhere the agent did not mean, or never binds at all,
        /// and either way it reads as the agent having guessed the wrong line.
        /// </remarks>
        public void SetAgentWorkingDirectory(string workingDirectory) =>
            _agentWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;

        /// <summary>
        /// The conversation whose breakpoints we are setting, so the tag can record which one placed them.
        /// </summary>
        /// <remarks>
        /// Pushed on session start, on restoring a conversation and on New — the same three moments as the
        /// working directory. A stale value does not fail: it files this conversation's breakpoints under
        /// the previous one, and <c>clear_breakpoints</c> then declines to remove breakpoints it did in
        /// fact set.
        /// </remarks>
        public void SetConversationId(string conversationId) =>
            _conversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId;

        /// <summary>
        /// Where a relative path in a tool request (a breakpoint, a file to open) is measured from: the agent's own directory
        /// first, the solution's only as a fallback. Never <c>Path.GetFullPath</c> on a relative path —
        /// that resolves against the PROCESS working directory, which here is devenv's install folder.
        /// </summary>
        private string RelativePathRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string solutionDirectory = null;
            try
            {
                var solutionFile = _dte.Solution?.FullName;
                if (!string.IsNullOrEmpty(solutionFile))
                    solutionDirectory = Path.GetDirectoryName(solutionFile);
            }
            catch (Exception)
            {
                // An unloaded or half-open solution throws; the agent's own root is a fine answer alone.
            }

            return AgentPath.PreferredRoot(_agentWorkingDirectory, solutionDirectory);
        }

        /// <summary>
        /// Optional live sink for our own spawned commands (the terminal mirror): called once with a
        /// human title per shellout. Shell-local wiring, in-proc only — never on the IPC wire (same
        /// principle as <see cref="ToolDescriptor"/> risk). Invocations are best-effort and guarded;
        /// a sink fault must never affect the tool run.
        /// </summary>
        public Action<string> LiveCommandHeader { get; set; }

        /// <summary>
        /// Optional live sink companion to <see cref="LiveCommandHeader"/>: one call per output line
        /// (stdout and stderr interleaved), on threadpool callbacks as the process emits them.
        /// </summary>
        public Action<string> LiveCommandLine { get; set; }

        private void MirrorHeader(string title)
        {
            try { LiveCommandHeader?.Invoke(title); }
            catch { /* mirror is cosmetic; never disturb the run */ }
        }

        /// <summary>
        /// Host-wired pty factory (ConPTY in the Shell): when present, run_command runs its
        /// process on a pseudo-terminal — real TTY semantics (echo, prompts) and pane
        /// interactivity — instead of redirected pipes. Null (stub hosts / factory failure) keeps
        /// the pipes path.
        /// </summary>
        public CommandPtyFactory CommandPtyFactory { get; set; }

        /// <summary>Raw VT output sink for pty runs (the mirror pane, verbatim — no line splitting).</summary>
        public Action<string> LiveCommandRawOutput { get; set; }

        // The one active interactive session: pane keystrokes route here; nothing running = dropped.
        private volatile ICommandPty _activeCommandPty;
        private int _activePtyInputBytes; // ground truth for the payload: did the USER type during the run?
        private volatile int _terminalColumns = 120;
        private volatile int _terminalRows = 30;

        /// <summary>Pane keystrokes → the active run_command pty (no-op when nothing is running).</summary>
        public void SendCommandPtyInput(byte[] data)
        {
            try
            {
                var session = _activeCommandPty;
                if (session is null || data is null)
                    return;
                Interlocked.Add(ref _activePtyInputBytes, data.Length);
                session.WriteInput(data);
            }
            catch { /* input routing is best-effort */ }
        }

        /// <summary>Tracks the pane size for pty creation and live-resizes the active session.</summary>
        public void SetTerminalSize(int columns, int rows)
        {
            if (columns <= 0 || rows <= 0)
                return;
            _terminalColumns = columns;
            _terminalRows = rows;
            try { _activeCommandPty?.Resize(columns, rows); }
            catch { /* cosmetic */ }
        }

        public IReadOnlyList<ToolDescriptor> Tools { get; }

        public async Task<ToolResult> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            if (!_handlers.TryGetValue(toolName ?? string.Empty, out var handler))
                return new ToolResult(IsError: true, Json.Object(("error", $"no such tool: {toolName}")));

            try
            {
                return await handler(argumentsJson ?? "{}", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "tool invocation was cancelled")));
            }
            catch (Exception ex)
            {
                // Keep the exception's identity. A bare ex.Message is often meaningless on its own — issue #21
                // arrived as "Not implemented (Exception from HRESULT: 0x80004001 (E_NOTIMPL))" with no tool,
                // no type and no stack, so there was nothing to diagnose from. The full exception goes to
                // logs\tool-error.log (always on); the agent gets the tool name and the type.
                ToolErrorLog.Write(toolName, ex);
                return new ToolResult(IsError: true, Json.Object(
                    ("error", $"{toolName} failed — {ex.GetType().Name}: {ex.Message}"),
                    ("detail", "this is an unexpected internal failure; the full exception is in "
                        + ToolErrorLog.Path)));
            }
        }

        /// <summary>How much of the diagnostic ladder to include (see <see cref="IdeToolDescriptors.GetDiagnostics"/>). Nested: each level ⊇ the last.</summary>
        private enum DiagnosticScope { Compiler, Analyzers, Full }

        /// <summary>One diagnostic in the tool output, with its provenance (see <see cref="DiagnosticScope"/>). Plain class — no records in this net472 assembly.</summary>
        private sealed class ToolDiagnostic
        {
            public string File;
            public int Line;    // 1-based
            public int Column;  // 1-based
            public int? EndLine;    // 1-based; Roslyn rows only (the Error List table exposes no end span)
            public int? EndColumn;  // 1-based; Roslyn rows only
            public DiagnosticSeverity Severity;
            public string Message;
            public string Code;
            public string Source; // "compiler" | "analyzer" | "errorList" | "build"
            public string ProjectName; // owning project (Error List rows only); used to scope a build:<name> result
        }

        /// <summary>
        /// Builds the solution (async, polling so the UI stays responsive) and summarizes the result.
        /// Incremental by default; <c>rebuild:true</c> forces a Clean+Rebuild. <c>project:&lt;name&gt;</c>
        /// scopes the build (and the reported diagnostics) to a single project via
        /// <see cref="SolutionBuild.BuildProject"/> instead of the whole solution — there is no per-project
        /// clean API, so a scoped rebuild still cleans the whole solution first. Success comes from the real
        /// build (<see cref="SolutionBuild.LastBuildInfo"/>); error details are the build's OWN diagnostics
        /// (the <see cref="ErrorSource.Build"/> Error List rows), NOT Roslyn's design-time view, which is
        /// unreliable here both ways — missing real build errors (issue #47) and inventing phantom ones on a
        /// cold workspace. See <see cref="CollectBuildDiagnosticsAsync"/>. If a failed build somehow leaves
        /// no structured rows, the tail of the Build output pane is attached as a backstop so the result is
        /// never "failed with nothing to act on".
        /// </summary>
        private async Task<ToolResult> BuildSolutionAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            bool rebuild;
            bool includeWarnings;
            string projectName;
            try
            {
                rebuild = ReadBoolArg(argumentsJson, "rebuild");
                includeWarnings = ReadBoolArg(argumentsJson, "includeWarnings");
                // ReadStringArg swallows malformed JSON, but ReadBoolArg above already threw for it,
                // so the fail-loud "not valid JSON" behavior of this block is preserved.
                projectName = ReadStringArg(argumentsJson, "project");
            }
            catch (JsonException) { return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON"))); }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var solution = _dte.Solution;
            if (solution is null || string.IsNullOrEmpty(solution.FullName))
                return new ToolResult(IsError: true, Json.Object(("error", "no solution is open")));

            var build = solution.SolutionBuild;

            EnvDTE.Project targetProject = null;
            if (projectName is not null)
            {
                var candidates = new List<EnvDTE.Project>();
                var available = new List<string>();
                // Projects DTE can't describe (unloaded / load-failed) are skipped, not fatal — but they're
                // reported, so "no project named X" can't hide the fact that X is sitting there unloaded.
                var skipped = new List<string>();
                foreach (var p in EnumerateProjects(solution, skipped))
                {
                    var name = TryGetProjectName(p, skipped);
                    if (name is null)
                        continue;
                    available.Add(name);
                    if (string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(p);
                }
                if (candidates.Count != 1)
                {
                    available.Sort(StringComparer.OrdinalIgnoreCase);
                    var reason = candidates.Count == 0 ? $"no project named '{projectName}'" : $"'{projectName}' matches multiple projects";
                    var notFoundPayload = new
                    {
                        error = reason,
                        availableProjects = available,
                        skippedProjects = skipped.Count > 0 ? skipped : null,
                    };
                    return new ToolResult(IsError: true, JsonSerializer.Serialize(notFoundPayload, JsonOptions));
                }
                targetProject = candidates[0];
            }

            // One deadline for the whole operation: a rebuild is two updates (clean, then build) and
            // BuildTimeout bounds their SUM, not each — the issue #250 fix made the clean waitable, and a
            // wait that restarted the clock would let a rebuild take twice the advertised bound.
            var deadline = DateTime.UtcNow + BuildTimeout;

            // The build manager runs one update at a time. Refuse rather than collide when the user (or a
            // previous, cancelled tool call — a cancelled wait does not cancel the update) already has one
            // running. Checked on this same synchronous stretch of the UI thread as the start call, with
            // no YIELD between (CleanSolutionAsync's SwitchToMainThreadAsync completes synchronously here,
            // being already on the main thread, and TryAdvise is synchronous), so nothing can begin an
            // update in the gap — the same invariant the Error List scope rests on. Without it the event
            // wait in CleanSolutionAsync would take the FOREIGN update's Done as the clean's and build a
            // solution that was never cleaned. Keep it that way: an await that yields between this line
            // and Clean reopens the gap. The BUILD makes its own check inside RunBuildWitnessedAsync, on
            // the same terms, because the clean's awaits sit between this line and that start.
            if (build.BuildState == vsBuildState.vsBuildStateInProgress)
                return new ToolResult(IsError: true, Json.Object(("error",
                    "a build is already in progress in Visual Studio; wait for it to finish (or cancel it) and retry")));

            // Clean first for a rebuild. Asynchronous and waited on the build manager's own Done event —
            // NOT Clean(WaitForCleanToFinish: true), which blocked the UI thread for the whole recursive
            // bin/obj delete (issue #250), and NOT Clean(false) + the BuildState poll, which can return
            // before the state has moved and start the build DURING the clean. See SolutionUpdateWaiter.
            // There is no per-project clean via DTE automation, so a scoped rebuild still cleans everything.
            bool? cleanSucceeded = null;
            if (rebuild)
            {
                var clean = await CleanSolutionAsync(build, deadline, cancellationToken).ConfigureAwait(false);
                if (clean.Cancelled)
                    return new ToolResult(IsError: true, Json.Object(("error",
                        "the clean was cancelled in Visual Studio before the rebuild started; nothing was built")));
                cleanSucceeded = clean.Succeeded;
                // CleanSolutionAsync resumes on the pool; the build below needs the UI thread again.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            }
            string activeConfiguration = null;
            if (targetProject is not null)
            {
                // ActiveConfiguration can be null while the solution is still loading — fail structured
                // like every other path here rather than letting the deref throw out of the tool.
                activeConfiguration = build.ActiveConfiguration?.Name;
                if (activeConfiguration is null)
                    return new ToolResult(IsError: true, Json.Object(("error",
                        "no active solution configuration (is the solution still loading?); retry, or build without 'project'")));
            }
            var (started, cancelled) = await RunBuildWitnessedAsync(build, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread(); // the helper's contract; also what satisfies VSTHRD010 inside a lambda
                if (targetProject is not null)
                    build.BuildProject(activeConfiguration, targetProject.UniqueName, WaitForBuildToFinish: false);
                else
                    build.Build(WaitForBuildToFinish: false);
            }, cancellationToken, deadline).ConfigureAwait(false);
            if (!started)
                return new ToolResult(IsError: true, Json.Object(("error",
                    "a build is already in progress in Visual Studio; wait for it to finish (or cancel it) and retry")));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var projectsFailed = build.LastBuildInfo; // number of projects that failed to build
            var succeeded = projectsFailed == 0;

            // A build the USER cancelled (Build → Cancel) is not a failed build, and reporting it as one
            // sent the agent straight back into it. Measured in Visual Studio, 2026-09-09: the cancelled build left projectsFailed:5
            // and ONE Error List row — MSB4242, "SDK could not be resolved ... because the worker node was
            // shut down", i.e. the abort itself — and with no cancellation fact in the payload the agent
            // read that as "a transient MSBuild node failure" and retried the build the user had just
            // stopped. Same shape as the write refusal (AGENTS.md): a result that declines to name its cause
            // gets one supplied. So the cause is named, the debris is NOT reported as the build's verdict,
            // and the workaround is closed in words. The Error List is not read at all here: its rows
            // describe the abort, not the code, and converging on them would only launder them.
            if (cancelled == true)
            {
                var cancelledPayload = new
                {
                    error = "the build was cancelled in Visual Studio (Build → Cancel) before it finished; "
                        + $"{projectsFailed} project(s) did not complete. Nothing about the code was decided: a cancelled "
                        + "build's Error List rows describe the abort (an MSBuild node shut down mid-build), not the "
                        + "code, so they are not reported. Do not retry unless the user asks.",
                    succeeded,
                    cancelled = true,
                    rebuild,
                    cleanSucceeded,
                    project = targetProject?.Name,
                    projectsFailed,
                };
                return new ToolResult(IsError: true, JsonSerializer.Serialize(cancelledPayload, JsonOptions));
            }

            // The build's own report — its Error List rows, converged, and the output-pane backstop — read by
            // the one helper run_tests' pre-run build also reads, so the two cannot disagree about why the
            // same build failed (issue #296). See ReadBuildReportAsync.
            var report = await ReadBuildReportAsync(targetProject?.Name, succeeded, cancellationToken).ConfigureAwait(false);
            var errors = report.Errors;
            var warnings = report.Warnings;
            var buildOutput = report.BuildOutput;

            // What this build did NOT compile among the files the agent wrote (issue #257). Assessed after
            // the build, against the projects as loaded now, and reported FIRST: a green result over a file
            // the loaded project does not contain is the silent-wrong-answer shape, and `succeeded` alone
            // would be read as "the code compiles".
            var staleness = await AssessProjectStalenessAsync(solution, report.CompiledFiles, cancellationToken).ConfigureAwait(false);

            var payload = new
            {
                note = ProjectStaleness.Summary(staleness),
                succeeded,
                // Tri-state on purpose: false = the build manager reported a normal completion; true never
                // reaches here (handled above); null = no witness, and the field is omitted rather than
                // asserting "not cancelled" from something that could not see a cancel.
                cancelled,
                rebuild,
                // Only on a rebuild (null otherwise, and omitted). A clean that failed — a locked output,
                // typically — is a fact the agent cannot get anywhere else, and the build that followed it
                // ran over whatever the clean left behind; the build outcome still decides `succeeded`.
                cleanSucceeded,
                project = targetProject?.Name,
                projectsFailed,
                errorCount = errors.Count,
                warningCount = warnings.Count,
                errorsByCode = CountByCode(errors),
                // warningsByCode is always included (cheap per-code summary, symmetric with errorsByCode);
                // the itemized warnings list is opt-in via includeWarnings, since a clean build can carry
                // far more warnings than errors and the agent usually only needs the count + breakdown.
                warningsByCode = CountByCode(warnings),
                errors = errors.Take(MaxErrorsReported).Select(DiagnosticJson),
                truncatedErrors = errors.Count > MaxErrorsReported,
                warnings = includeWarnings ? warnings.Take(MaxErrorsReported).Select(DiagnosticJson) : null,
                truncatedWarnings = includeWarnings && warnings.Count > MaxErrorsReported,
                buildOutput,
                projectStaleness = StalenessJson(staleness),
            };
            // A failed build reports isError:true (with the errors in the payload) so the agent renders the
            // tool call as failed (red) rather than a green tick — MCP's isError is exactly for a tool that
            // ran but whose operation failed.
            return new ToolResult(IsError: !succeeded, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>What a just-finished build reported about itself. Plain class — no records in this net472 assembly.</summary>
        private sealed class BuildReport
        {
            public List<ToolDiagnostic> Errors;
            public List<ToolDiagnostic> Warnings;
            /// <summary>The Build output pane's tail, only for a failed build that left no error rows; else null.</summary>
            public string BuildOutput;
            /// <summary>Canonical paths of the files the build reported any row in — files it compiled (issue #257).</summary>
            public HashSet<string> CompiledFiles;
        }

        /// <summary>
        /// The report of a build that has just finished, for EVERY tool that runs one: <c>build_solution</c> and
        /// <c>run_tests</c>' pre-run build. One reader, so the two tools cannot disagree about why the same build
        /// failed (issue #296: <c>run_tests</c> still read Roslyn's live compiler, which cannot see a build-only
        /// diagnostic, and reported a failed build with <c>errors: []</c> under "fix the build errors").
        /// </summary>
        /// <remarks>
        /// <para>
        /// The details are the BUILD's own diagnostics (<see cref="ErrorSource.Build"/> Error List rows), NOT
        /// Roslyn's: a real build just ran, so the build is ground truth. Roslyn's design-time view is
        /// unreliable here both ways — it misses real build errors (generated-code CS####, NU/MSB/NETSDK, a
        /// CS2001 from the CSC task, an MSBuild <c>&lt;Error&gt;</c> — issues #47, #296) AND invents ones the
        /// build doesn't have (a cold workspace reporting CS0122 for an honored InternalsVisibleTo, on a build
        /// that succeeded — measured in Visual Studio, 2026-07-24). The ErrorSource.Build filter excludes the lagging IntelliSense
        /// half, so dropping Roslyn loses nothing. get_diagnostics keeps its Roslyn live-buffer view.
        /// </para>
        /// <para>
        /// The Error List posts build rows ASYNCHRONOUSLY after vsBuildStateDone, so a read at build-done can be
        /// stale in EITHER direction (in Visual Studio, 2026-07-24: a failed incremental build's CS1002 not yet posted →
        /// errorCount:0; a just-fixed build's old CS1002 not yet cleared → phantom errorCount:1). LastBuildInfo
        /// (<paramref name="succeeded"/>) is the synchronous ground truth, so the rows are waited on to converge
        /// to it (<see cref="CollectBuildDiagnosticsConvergedAsync"/>). A succeeded build has no compile errors
        /// by definition, so any error row on one is a stale entry the poll couldn't outlast and is dropped.
        /// </para>
        /// <para>
        /// Last-resort backstop (issue #47): a failed build whose errors never reached the Error List (a target
        /// crash, a failed pre/post-build event, or convergence timing out) must still tell the agent
        /// *something*, so the tail of the Build output pane — the synchronous authoritative record — is
        /// attached for exactly that case. Awaited, with the UI-thread switch inside the callee: the
        /// ConfigureAwait(false) before it has put us on the pool (issue #89).
        /// </para>
        /// <para>
        /// The files this build reported diagnostics in are files it compiled — a rebuild evaluates a stale
        /// legacy project from disk for that one build — and the #257 note must not say otherwise beside them.
        /// Warnings count: any row proves the compiler read the file.
        /// </para>
        /// </remarks>
        private async Task<BuildReport> ReadBuildReportAsync(string projectName, bool succeeded, CancellationToken cancellationToken)
        {
            var diagnostics = await CollectBuildDiagnosticsConvergedAsync(projectName, succeeded, cancellationToken).ConfigureAwait(false);
            var errors = succeeded ? new List<ToolDiagnostic>() : diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
            var buildOutput = (!succeeded && errors.Count == 0)
                ? await ReadBuildOutputTailAsync(cancellationToken).ConfigureAwait(false)
                : null;
            return new BuildReport
            {
                Errors = errors,
                Warnings = warnings,
                BuildOutput = buildOutput,
                CompiledFiles = new HashSet<string>(
                    diagnostics.Select(d => d.File).Where(f => !string.IsNullOrEmpty(f)).Select(f => AgentPath.Canonical(f)),
                    StringComparer.OrdinalIgnoreCase),
            };
        }

        /// <summary>The structured form of the staleness notes for a payload, or null (omitted) when there are none.</summary>
        private static object StalenessJson(IReadOnlyList<ProjectStalenessNote> notes) =>
            notes is { Count: > 0 }
                ? notes.Select(n => new
                {
                    kind = n.Kind switch
                    {
                        ProjectStalenessKind.NotInProject => "notInProject",
                        ProjectStalenessKind.ProjectNotReloaded => "projectNotReloaded",
                        ProjectStalenessKind.ImportNotReloaded => "importNotReloaded",
                        _ => n.Kind.ToString(),
                    },
                    file = n.File,
                    project = n.Project,
                    message = n.Message,
                }).ToList()
                : null;

        /// <summary>
        /// Which of the files the agent has written this build did not compile, against the projects as
        /// LOADED now (issue #257). Empty when there is nothing to say, and empty — logged — when the
        /// assessment itself fails: an optional enrichment may never fail the operation it decorates
        /// (issue #189).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three candidate classes, from the ledger's paths: a compiled-source file is asked of the loaded
        /// hierarchies (<see cref="ProjectMembershipProbe"/>); a project file is stale where it matches a
        /// loaded NON-SDK project — an SDK-style project takes an external edit in place (8/8 measured), so
        /// its entry is never reported and never needs closing; an import is stale where a non-SDK project
        /// lies under its directory. The dialect and the on-disk file-name check are file reads and run off
        /// the UI thread; the hierarchy questions run on it. Restricting the source candidates to the
        /// compiled extensions is what keeps a written README from producing a note.
        /// </para>
        /// <para>
        /// The source-file check is scoped to files under the solution directory or under a loaded
        /// project's directory. A file the agent wrote elsewhere is legitimately in no project, and saying
        /// so on every build would be noise about a file the build was never expected to include.
        /// </para>
        /// </remarks>
        /// <param name="solution">The open solution.</param>
        /// <param name="compiledFiles">
        /// Canonical paths of the files the build just reported diagnostics in, or null when the caller
        /// has no build rows (a test run's own build that succeeded, whose rows are never read).
        /// </param>
        /// <param name="cancellationToken">Cancellation.</param>
        private async Task<IReadOnlyList<ProjectStalenessNote>> AssessProjectStalenessAsync(
            EnvDTE.Solution solution, ISet<string> compiledFiles, CancellationToken cancellationToken)
        {
            var written = _writes?.Snapshot();
            if (written is null || written.Count == 0)
                return Array.Empty<ProjectStalenessNote>();

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var solutionDir = SafeDirectory(solution?.FullName);
                var projects = new List<(string File, string Dir)>();
                foreach (var p in EnumerateProjects(solution))
                {
                    var file = TryGetProjectFullName(p, null);
                    var dir = SafeDirectory(file);
                    if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(dir))
                        projects.Add((AgentPath.Canonical(file), dir));
                }

                var staleProjectCandidates = new List<string>();
                var importCandidates = new List<string>();
                var orphanCandidates = new List<(string Path, string NearestProject)>();

                foreach (var raw in written)
                {
                    var path = AgentPath.Canonical(raw);
                    if (!AgentPath.IsRooted(path))
                        continue;

                    if (ProjectStaleness.IsProjectFile(path))
                    {
                        if (projects.Any(p => string.Equals(p.File, path, StringComparison.OrdinalIgnoreCase)))
                            staleProjectCandidates.Add(path);
                        continue;
                    }
                    if (ProjectStaleness.IsImportFile(path))
                    {
                        importCandidates.Add(path);
                        continue;
                    }
                    if (!ProjectStaleness.IsCompiledSource(path))
                        continue;

                    // The deepest loaded project whose directory contains the file, if any.
                    var nearest = projects
                        .Where(p => WorkspacePath.IsUnderRoot(path, p.Dir))
                        .OrderByDescending(p => p.Dir.Length)
                        .Select(p => p.File)
                        .FirstOrDefault();
                    if (nearest is null && !WorkspacePath.IsUnderRoot(path, solutionDir))
                        continue;

                    // A file the disk no longer has cannot be "not compiled" — the agent (or the user)
                    // removed it since the write, and the ledger keeps its entry only because nothing else
                    // is a fact about it. Not forgotten: a later write recreates it and the check applies.
                    if (!File.Exists(path))
                        continue;

                    var inProject = ProjectMembershipProbe.IsInLoadedProject(ServiceProvider.GlobalProvider, path);
                    if (inProject == false)
                        orphanCandidates.Add((path, nearest));
                }

                if (staleProjectCandidates.Count == 0 && importCandidates.Count == 0 && orphanCandidates.Count == 0)
                    return Array.Empty<ProjectStalenessNote>();

                // The file reads — dialect and the on-disk name check — off the UI thread.
                await TaskScheduler.Default;
                cancellationToken.ThrowIfCancellationRequested();

                var styles = new Dictionary<string, ProjectStyle?>(StringComparer.OrdinalIgnoreCase);
                ProjectStyle? StyleOf(string projectFile)
                {
                    if (!styles.TryGetValue(projectFile, out var style))
                        styles[projectFile] = style = ProjectFileText.Style(projectFile);
                    return style;
                }

                var staleProjects = staleProjectCandidates
                    .Where(p => StyleOf(p) == ProjectStyle.Legacy)
                    .Select(p => new StaleProjectFile(p))
                    .ToList();
                var staleProjectSet = new HashSet<string>(staleProjects.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);

                var orphans = orphanCandidates.Select(o => new OrphanedSourceFile(
                        o.Path,
                        o.NearestProject,
                        o.NearestProject is null ? null : StyleOf(o.NearestProject),
                        o.NearestProject is not null && ProjectFileText.NamesFileOnDisk(o.NearestProject, Path.GetFileName(o.Path)),
                        o.NearestProject is not null && staleProjectSet.Contains(o.NearestProject),
                        compiledThisBuild: compiledFiles is not null && compiledFiles.Contains(o.Path)))
                    .ToList();

                var staleImports = new List<StaleImportFile>();
                foreach (var import in importCandidates)
                {
                    var importDir = SafeDirectory(import);
                    if (string.IsNullOrEmpty(importDir))
                        continue;
                    // A project that reloaded AFTER the import was written has taken it; the note names
                    // only the ones that have not, and is dropped once none remain.
                    var legacyBelow = projects
                        .Where(p => WorkspacePath.IsUnderRoot(p.File, importDir) && StyleOf(p.File) == ProjectStyle.Legacy)
                        .Where(p => !_writes.WasReloadedSince(p.File, import))
                        .Select(p => p.File)
                        .ToList();
                    if (legacyBelow.Count > 0)
                        staleImports.Add(new StaleImportFile(import, legacyBelow));
                }

                return ProjectStaleness.Describe(orphans, staleProjects, staleImports);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ToolErrorLog.Write("build_solution", new InvalidOperationException("project staleness assessment failed (issue #257); reported nothing", ex));
                return Array.Empty<ProjectStalenessNote>();
            }

            static string SafeDirectory(string path)
            {
                if (string.IsNullOrEmpty(path))
                    return null;
                try { return Path.GetDirectoryName(path); }
                catch (ArgumentException) { return null; }
            }
        }

        /// <summary>
        /// The diagnostic set for a just-finished build: the build's OWN diagnostics, taken from the
        /// <see cref="ErrorSource.Build"/> Error List rows and NOT from Roslyn. build_solution ran a real
        /// MSBuild build, so the build is ground truth — and Roslyn's design-time compilation is unreliable
        /// here in BOTH directions: it misses real build errors it structurally can't see (generated-code
        /// CS####, build-only NU/MSB/NETSDK — issue #47), and it invents errors the build doesn't have (a
        /// cold/half-warmed workspace reported CS0122 "inaccessible due to protection level" for an
        /// InternalsVisibleTo the real build honored, on a build that SUCCEEDED — measured in Visual Studio, 2026-07-24). The
        /// ErrorSource.Build filter already excludes the lagging IntelliSense half, so the only reason Roslyn
        /// was ever used for build_solution (dodging that lag) no longer applies. Scoped to
        /// <paramref name="projectName"/> when a single project was built. get_diagnostics keeps its own
        /// Roslyn-based live-buffer view via the shared <see cref="CollectDiagnosticsAsync"/> — unaffected.
        /// </summary>
        private async Task<List<ToolDiagnostic>> CollectBuildDiagnosticsAsync(string projectName, CancellationToken cancellationToken)
        {
            var result = new List<ToolDiagnostic>();

            await AddBuildSourceDiagnosticsAsync(result, DiagnosticSeverity.Warning, projectName, cancellationToken).ConfigureAwait(false);

            return result.OrderByDescending(d => (int)d.Severity).ToList();
        }

        private const int ErrorListSettleTimeoutMs = 4000;  // max wait for the Error List to catch up to a just-finished build
        private const int ErrorListPollIntervalMs = 150;    // gap between convergence samples

        /// <summary>
        /// How long an EMPTY result must persist before it counts as settled, on a build that succeeded.
        /// </summary>
        /// <remarks>
        /// Convergence is otherwise decided by two consecutive identical samples, and that cannot tell
        /// "nothing has arrived" from "nothing more is coming": at the start of a succeeded build, two
        /// EMPTY samples are identical trivially, so the poll returned after ~160ms having waited for
        /// nothing and reported a build's warnings as zero. Only the empty case needs the floor — a
        /// non-empty set that stops changing really has settled, and a FAILED build with no rows never
        /// satisfies <c>errorPresenceMatches</c> so it already polls to the deadline.
        /// <para>
        /// A floor rather than the publisher's own signal because there isn't one: <c>ITableDataSink</c>
        /// exposes <c>IsStable</c>, but measured across every source of the errors table, on a short-lived
        /// subscription NONE of them ever sets it — the value read back is only ever our own default.
        /// </para>
        /// </remarks>
        private const int ErrorListEmptyFloorMs = 1200;

        /// <summary>
        /// Reads the build-source diagnostics, but first waits for the Error List to CONVERGE to the build's
        /// own outcome. VS posts build rows asynchronously after vsBuildStateDone, so an immediate read can be
        /// stale either way (in Visual Studio, 2026-07-24: a failed incremental build's CS1002 not yet posted → errorCount:0
        /// with the backstop covering it; a just-fixed build's old CS1002 not yet cleared → phantom
        /// errorCount:1 on a build that succeeded). <paramref name="succeeded"/> (LastBuildInfo) is the
        /// synchronous ground truth, so we poll until the read's error PRESENCE matches it AND the row set has
        /// stopped changing across two samples, bounded by <see cref="ErrorListSettleTimeoutMs"/>. On timeout
        /// we return the last read; the caller trusts the build outcome (drops errors on a success) and falls
        /// back to the build output pane for a failed build that still shows nothing.
        /// </summary>
        private async Task<List<ToolDiagnostic>> CollectBuildDiagnosticsConvergedAsync(string projectName, bool succeeded, CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            var deadline = started + TimeSpan.FromMilliseconds(ErrorListSettleTimeoutMs);
            var rows = await CollectBuildDiagnosticsAsync(projectName, cancellationToken).ConfigureAwait(false);
            var prevSig = diagnosticsSignature(rows);
            var sample = 0;
            // `sample == 0 ||` guarantees at least one comparison: the deadline is measured from before the
            // FIRST read, so a slow one (or a breakpoint) could put us past it before ever looking twice,
            // and the poll would return having sampled nothing at all. Observed, with samples=0.
            while (sample == 0 || DateTime.UtcNow < deadline)
            {
                await Task.Delay(ErrorListPollIntervalMs, cancellationToken).ConfigureAwait(false);
                rows = await CollectBuildDiagnosticsAsync(projectName, cancellationToken).ConfigureAwait(false);
                var sig = diagnosticsSignature(rows);
                var errorPresenceMatches = rows.Any(d => d.Severity == DiagnosticSeverity.Error) == !succeeded;
                sample++;

                // An empty result may not be called settled until the floor has passed — see
                // ErrorListEmptyFloorMs. Without it "this build produced no warnings" and "no warnings have
                // been posted yet" are the same two identical samples.
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                var emptyTooSoon = rows.Count == 0 && elapsedMs < ErrorListEmptyFloorMs;
                // Matches the build outcome, unchanged since the last sample, and — if empty — has been so
                // for long enough that "none" is distinguishable from "not yet".
                if (errorPresenceMatches && sig == prevSig && !emptyTooSoon)
                    break;
                prevSig = sig;
            }
            return rows;

            // Order-independent fingerprint of the row set, so "unchanged since last sample" means settled.
            static string diagnosticsSignature(List<ToolDiagnostic> ds) => ds.Count + "|" + string.Join(";",
                ds.Select(d => d.Code + "@" + (d.File ?? string.Empty) + ":" + d.Line + "," + d.Column).OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// Cleans the whole solution without blocking the UI thread, and returns only when the build
        /// manager has reported the clean finished (issue #250). Resumes on the thread pool.
        /// </summary>
        /// <remarks>
        /// Two signals, and both must agree before the caller starts the build. (1) The build manager's
        /// <c>UpdateSolution_Done</c>, advised BEFORE <c>Clean</c> is called so it cannot be missed and
        /// cannot be mistaken for the pre-clean state — that is the one that closes the race the old
        /// synchronous form existed to avoid (its rationale: "a synchronous clean avoids racing the shared
        /// BuildState with the build that follows"). (2) <c>BuildState</c> no longer <c>InProgress</c>, read
        /// after the event's continuation has been posted back through the dispatcher — the event fires
        /// from inside the manager's own completion, and a build started on that stack would find it busy.
        /// The second check waits WHILE <c>InProgress</c> rather than FOR <c>Done</c>: after the event the
        /// only thing being guarded is a manager still unwinding, and a state that read anything else would
        /// otherwise turn a finished clean into a ten-minute wait.
        /// <para>
        /// The wait is cancellable; the clean is not. A cancelled tool call returns while VS finishes the
        /// clean on its own, which is exactly what the build phase has always done — and why the caller's
        /// busy check exists for the NEXT call. If the build manager cannot be advised, the clean runs the
        /// old synchronous way: frozen but correct, logged, never an unwaited clean followed by a build.
        /// </para>
        /// </remarks>
        private static async Task<SolutionUpdateOutcome> CleanSolutionAsync(SolutionBuild build, DateTime deadline, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var waiter = SolutionUpdateWaiter.TryAdvise();
            if (waiter is null)
            {
                ToolErrorLog.Write("build_solution", new InvalidOperationException(
                    "SVsSolutionBuildManager unavailable; the clean ran synchronously on the UI thread (issue #250 fallback)"));
                build.Clean(WaitForCleanToFinish: true);
                await TaskScheduler.Default;
                return new SolutionUpdateOutcome(succeeded: build.LastBuildInfo == 0, cancelled: false);
            }

            try
            {
                var started = DateTime.UtcNow;
                build.Clean(WaitForCleanToFinish: false);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var completed = await Task.WhenAny(waiter.Completed, Task.Delay(remaining, timeoutCts.Token)).ConfigureAwait(false);
                timeoutCts.Cancel(); // stop the pending delay either way
                if (completed != waiter.Completed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Started separates "still deleting" from "the update never began" — the latter is the
                    // shape a deferred or refused start takes, and it is the one worth naming in a report.
                    throw new TimeoutException(waiter.Started
                        ? $"clean timed out after {(DateTime.UtcNow - started).TotalSeconds:0}s"
                        : $"clean never started within {(DateTime.UtcNow - started).TotalSeconds:0}s (the build manager reported no UpdateSolution_Begin)");
                }
                var outcome = await waiter.Completed.ConfigureAwait(false);

                // Signal (2): the manager may still be inside the completion that raised the event.
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    if (build.BuildState != vsBuildState.vsBuildStateInProgress)
                        break;
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("clean reported done but the build manager stayed busy until the deadline");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                await TaskScheduler.Default;
                return outcome;
            }
            finally
            {
                // Unadvise on the UI thread (symmetric with the advise), regardless of where we resumed
                // and of whether we are leaving by result, timeout or cancellation.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                waiter.Dispose();
            }
        }

        /// <summary>
        /// Starts a build and polls it to completion exactly as before, with the build manager's Done event
        /// advised alongside as a WITNESS — never as the completion signal — so the caller learns whether the
        /// update was cancelled in VS. Returns <c>started:false</c> when the manager is already busy (the
        /// check and the start share one synchronous stretch of the UI thread, no yield between — the same
        /// invariant the clean's busy check rests on), and <c>cancelled:null</c> when nothing could witness.
        /// </summary>
        /// <remarks>
        /// The poll stays the arbiter of "over" because it has held in Visual Studio across every session since #47; the
        /// event only ADDS the cancel flag the poll cannot see. The two are not ordered for us — the event
        /// fires from inside the manager's completion, the state flips around the same time — so after the
        /// poll returns the witness is given a short grace to land, bounded by <see cref="WitnessGrace"/>
        /// and never by the build deadline: a witness that has not reported by then answers "unknown", and
        /// the build's result is still reported in full. A missing witness can therefore never hang a build
        /// that finished, only lose the cancel fact for it.
        /// </remarks>
        private static async Task<(bool started, bool? cancelled)> RunBuildWitnessedAsync(SolutionBuild build, Action start, CancellationToken cancellationToken, DateTime? deadline = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (build.BuildState == vsBuildState.vsBuildStateInProgress)
                return (started: false, cancelled: null);
            var witness = SolutionUpdateWaiter.TryAdvise();
            try
            {
                start();
                await WaitForBuildStateDoneAsync(build, cancellationToken, deadline).ConfigureAwait(false);
                if (witness is null)
                    return (started: true, cancelled: null);

                using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var completed = await Task.WhenAny(witness.Completed, Task.Delay(WitnessGrace, graceCts.Token)).ConfigureAwait(false);
                graceCts.Cancel(); // stop the pending delay either way
                if (completed != witness.Completed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (started: true, cancelled: null);
                }
                var outcome = await witness.Completed.ConfigureAwait(false);
                return (started: true, cancelled: outcome.Cancelled);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
                witness?.Dispose();
            }
        }

        /// <summary>How long a finished build's witness may lag the state poll before its answer is "unknown".</summary>
        private static readonly TimeSpan WitnessGrace = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Polls the solution build to completion on the UI thread without blocking it. <paramref name="deadline"/>
        /// lets a caller that has already spent part of <see cref="BuildTimeout"/> on an earlier phase (the
        /// rebuild's clean) keep one bound over the whole operation; absent, the build gets the full budget.
        /// </summary>
        private static async Task WaitForBuildStateDoneAsync(SolutionBuild build, CancellationToken cancellationToken, DateTime? deadline = null)
        {
            var due = deadline ?? DateTime.UtcNow + BuildTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (build.BuildState == vsBuildState.vsBuildStateDone)
                    return;
                if (DateTime.UtcNow > due)
                    throw new TimeoutException("build timed out");
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns the solution's errors and warnings at the requested <c>scope</c> (see
        /// <see cref="IdeToolDescriptors.GetDiagnostics"/>). Compiler diagnostics always come from Roslyn (synchronous,
        /// buffer-accurate, never lagging the way the async Error List does); analyzers and the live Error
        /// List are layered in per scope, de-duplicated with source precedence.
        /// </summary>
        private async Task<ToolResult> GetDiagnosticsAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            DiagnosticScope scope;
            DiagnosticSeverity minSeverity;
            string fileFilter, argError;
            try { (scope, minSeverity, fileFilter, argError) = ReadDiagnosticsArgs(argumentsJson); }
            catch (JsonException) { return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON"))); }
            if (argError is not null)
                return new ToolResult(IsError: true, Json.Object(("error", argError)));

            var diagnostics = await CollectDiagnosticsAsync(scope, minSeverity, cancellationToken, fileFilter: fileFilter).ConfigureAwait(false);
            var capped = diagnostics.Take(MaxDiagnosticsReturned).ToList();

            // A broken solution can have hundreds of diagnostics; dumping them all is neither loadable nor
            // useful. Report the true totals + a by-code breakdown (so the shape is visible — e.g. "127×
            // CS0246"), plus a most-severe-first sample. The agent fixes by category and re-runs.
            var payload = new
            {
                scope = scope.ToString().ToLowerInvariant(),
                severity = minSeverity.ToString().ToLowerInvariant(),
                file = fileFilter,
                total = diagnostics.Count,
                returned = capped.Count,
                truncated = diagnostics.Count > capped.Count,
                errorCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
                warningCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
                infoCount = minSeverity <= DiagnosticSeverity.Info ? diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info) : (int?)null,
                hiddenCount = minSeverity == DiagnosticSeverity.Hidden ? diagnostics.Count(d => d.Severity == DiagnosticSeverity.Hidden) : (int?)null,
                countsByCode = CountByCode(diagnostics),
                note = AmbiguousFilterNote(fileFilter, diagnostics) ?? UnmatchedFilterNote(fileFilter, diagnostics),
                diagnostics = capped.Select(DiagnosticJson),
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Runs the solution's tests via the command line and returns structured results: MTP projects (opt into
        /// the Microsoft Testing Platform runner) via <c>dotnet run</c>, classic vstest (reference
        /// Microsoft.NET.Test.Sdk) via <c>dotnet test</c>, both with the trx logger; the TRX(es) aggregate into one
        /// card (<see cref="RunTestProjectsAsync"/>). An incremental VS build runs first (<see cref="BuildBeforeTestRunAsync"/>)
        /// so the <c>--no-build</c> runs test fresh output. A <c>filter</c> runs a subset (framework-native, verbatim).
        /// </summary>
        private async Task<ToolResult> RunTestsAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var solution = _dte.Solution;
            if (solution is null || string.IsNullOrEmpty(solution.FullName))
                return new ToolResult(IsError: true, Json.Object(("error", "no solution is open")));

            // The command-line runner: MTP projects (opt into the Microsoft Testing Platform runner) via
            // `dotnet run`, classic vstest (references Microsoft.NET.Test.Sdk) via `dotnet test`. The UI
            // thread is used ONLY to harvest DTE COM state (project paths, configuration, solution dir);
            // classification is csproj/props XML parsing — file I/O that must not run on the UI thread
            // (a large solution or slow disk would freeze devenv for the whole pass).
            var (filterSpec, filterError) = ReadFilterSpec(argumentsJson);
            if (filterError is not null)
                return new ToolResult(IsError: true, Json.Object(("error", filterError)));

            // A project DTE can't describe (unloaded / load-failed — common right after a merge or a branch
            // switch) is skipped rather than aborting the run; unreadable ones ride out in the payload notes
            // so a suite that silently lost a test project can't read as a clean pass. See issue #21.
            var projectPaths = new List<string>();
            var unreadableProjects = new List<string>();
            foreach (var p in EnumerateProjects(solution, unreadableProjects))
            {
                var path = TryGetProjectFullName(p, unreadableProjects);
                if (!string.IsNullOrEmpty(path))
                    projectPaths.Add(path);
            }
            var configuration = solution.SolutionBuild?.ActiveConfiguration?.Name ?? "Debug";
            var workspaceRoot = Path.GetDirectoryName(solution.FullName) ?? string.Empty;
            var resultsRoot = TestResultsRoot;

            var (testProjects, runSettings) = await Task.Run(() =>
            {
                PruneOldTestResults(resultsRoot);
                return (TestProjects.Classify(projectPaths), FindSolutionRunSettings(workspaceRoot));
            }, cancellationToken).ConfigureAwait(false);
            if (testProjects.Count == 0)
            {
                var reason = "no test projects found in the solution (a project referencing Microsoft.NET.Test.Sdk, "
                    + "one opting into the Microsoft Testing Platform runner, or an old-style .NET Framework "
                    + "test project)";
                // Don't let an unloaded project read as "this solution has no tests".
                if (unreadableProjects.Count > 0)
                    reason += ". Note: " + string.Join("; ", unreadableProjects);
                return new ToolResult(IsError: true, Json.Object(("error", reason)));
            }

            // ANY filter is translated per project to that framework's own filter option, the structured
            // ones to its flags and a raw expression to its query option (the raw value is a
            // filter expression, never a piece of command line). If any target project's framework isn't
            // one we know the option for (e.g. TUnit), fail loud — never run it unfiltered, which would
            // look like a filtered run but execute everything. Classic vstest is always translatable.
            if (!filterSpec.IsEmpty)
            {
                var untranslatable = testProjects
                    .Where(t => t.Dialect == FilterDialect.Unknown)
                    .Select(t => Path.GetFileNameWithoutExtension(t.ProjectFile)).ToList();
                if (untranslatable.Count > 0)
                    return new ToolResult(IsError: true, Json.Object(("error",
                        "cannot apply a filter to these project(s): " + string.Join(", ", untranslatable)
                        + ". Their test framework's filter option isn't recognised, so run_tests can only run "
                        + "them unfiltered. To run a subset, invoke that framework's own runner with run_command.")));

                // A value the quoting cannot make safe - one that is itself an option - is refused before
                // anything is spawned, with the contract spelled out (see RefuseFilterValue).
                foreach (var project in testProjects)
                {
                    if (TestRunCommands.RefuseFilterValue(filterSpec, project.Dialect) is { } refusal)
                        return new ToolResult(IsError: true, Json.Object(("error", refusal)));
                }
            }

            // An old-style project is run against its BUILT OUTPUT ASSEMBLY, and only DTE can say where
            // that is. Resolved here, after classification, so the UI thread is re-entered only when the
            // solution actually holds one - the classification pass itself is file I/O and stays off it.
            var legacyAssemblies = await ResolveLegacyOutputAssembliesAsync(testProjects, cancellationToken)
                .ConfigureAwait(false);

            TestRunDebugLog.Write($"run_tests: mtp={testProjects.Count(t => t.Kind == TestProjectKind.Mtp)} classic={testProjects.Count(t => t.Kind == TestProjectKind.Classic)} legacy={testProjects.Count(t => t.Kind == TestProjectKind.LegacyVsTest)}, config={configuration}, filter={filterSpec.Describe()}, runsettings={runSettings.Path ?? "(none)"}");

            // Incrementally build in VS before the command-line run. The shellout uses `--no-build`, so
            // without this it would test stale/missing output. We build via VS, not `dotnet build`: VS owns
            // these bin/obj outputs, so an incremental VS build coordinates against the assemblies VS already
            // has loaded — no file-lock race — and the subsequent `--no-build` run only reads them.
            var buildFailure = await BuildBeforeTestRunAsync(solution, cancellationToken);
            if (buildFailure is not null)
                return buildFailure;

            // The same check build_solution makes (issue #257), and here it is the worst case: a legacy
            // project runs against its BUILT OUTPUT ASSEMBLY, so a new test file the loaded project does
            // not contain is neither compiled nor run, and the count simply does not move.
            var solutionNotes = new List<string>(unreadableProjects);
            var staleness = await AssessProjectStalenessAsync(solution, compiledFiles: null, cancellationToken).ConfigureAwait(false);
            solutionNotes.AddRange(staleness.Select(n => n.Message));

            return await RunTestProjectsAsync(
                testProjects, filterSpec, configuration, resultsRoot, workspaceRoot, runSettings,
                solutionNotes, legacyAssemblies, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Opens a file in the VS editor and optionally navigates to a 1-based line, mirroring
        /// <see cref="VsIdeServices.OpenFileAtLineAsync"/> but as an agent-invokable tool. A relative
        /// <c>file</c> resolves against the agent's directory first, the solution's as a fallback (issue #54). Unlike that best-effort helper this
        /// reports failure (no path / not found) to the agent so it can correct the path. UI-thread marshaled.
        /// </summary>
        private async Task<ToolResult> OpenFileAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            string file;
            int line;
            try
            {
                if (string.IsNullOrWhiteSpace(argumentsJson))
                    return new ToolResult(IsError: true, Json.Object(("error", "missing required 'file' argument")));
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                file = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                line = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var li) ? li : 0;
            }
            catch (JsonException) { return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON"))); }

            if (string.IsNullOrWhiteSpace(file))
                return new ToolResult(IsError: true, Json.Object(("error", "missing required 'file' argument")));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // A relative path is the AGENT's, so it is measured from the agent's directory first (issue #54),
            // exactly as the breakpoint tools measure theirs. The solution directory alone was wrong wherever
            // the workspace root widened past it, and with no solution open the path stayed relative and was
            // checked against devenv's install folder. Refused when there is nothing to root it against.
            var path = AgentPath.Canonical(file.Trim(), RelativePathRoot());
            if (!AgentPath.IsRooted(path))
                return new ToolResult(IsError: true, Json.Object(("error",
                    $"'{file.Trim()}' is a relative path and there is no working directory or solution to measure it from; pass an absolute path.")));

            if (!File.Exists(path))
                return new ToolResult(IsError: true, Json.Object(("error", $"file not found: {path}")));

            try
            {
                var window = _dte.ItemOperations.OpenFile(path);
                window?.Activate();
                if (line > 0 && _dte.ActiveDocument?.Selection is EnvDTE.TextSelection selection)
                    selection.GotoLine(line, false);
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", $"could not open the file: {ex.Message}")));
            }

            var payload = new { opened = path, line = line > 0 ? line : (int?)null };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        // --- Breakpoint tools (issue #73) -------------------------------------------------------------

        /// <summary>
        /// Places the requested breakpoints and tags each one as ours. All VS work is on the UI thread;
        /// the decisions (validation, path canonicalisation, duplicate collapse) belong to
        /// <see cref="Breakpoints"/> in Core, where they are testable without devenv.
        /// </summary>
        /// <param name="allowExpressions">
        /// Which of the two names this call came in on — <see cref="IdeToolDescriptors.SetExpressionBreakpoint"/> (true)
        /// or <see cref="IdeToolDescriptors.SetBreakpoint"/> (false). Bound at registration, never read back from the
        /// invoked name here: a field holding "which tool am I" would be wrong the moment two calls
        /// overlap. Core refuses the batch, naming the other tool, when a plain call carries an
        /// expression.
        /// </param>
        private async Task<ToolResult> SetBreakpointAsync(string argumentsJson, bool allowExpressions, CancellationToken cancellationToken)
        {
            List<BreakpointSpec> requested;
            try
            {
                requested = ReadBreakpointSpecs(argumentsJson);
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var batch = AgentBreakpoints.Prepare(requested, RelativePathRoot(), allowExpressions);
            if (batch.Error is not null)
                return new ToolResult(IsError: true, Json.Object(("error", batch.Error)));

            var tag = AgentBreakpoints.Tag(_conversationId);
            var rows = new List<object>(batch.Specs.Count);
            var placed = 0;
            var conflicted = 0;

            foreach (var spec in batch.Specs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string failure = null;
                string conflict = null;
                try
                {
                    // ONE BREAKPOINT PER LOCATION. The rule and the evidence for it live on
                    // AgentBreakpoints.Decide in Core; this is the half that has to obey it, and the
                    // shape is the point. The refusal used to be a string assigned in one branch and read
                    // forty lines later, and the branch fell through into the Add below: the tool
                    // answered "conflict - choose another line" while Visual Studio had already taken the
                    // breakpoint, stamped our tag on it (so clear_breakpoints would later delete at a
                    // location the USER was watching) and, for a tracepoint, cleared BreakWhenHit there.
                    // Switching on a returned outcome, with the Add reachable from one arm only, makes
                    // that mistake unwriteable rather than merely fixed.
                    var existing = FindBreakpointAt(spec.File, spec.Line);
                    switch (AgentBreakpoints.Decide(existing != null, existing?.Mine == true))
                    {
                        case ExistingBreakpointAction.RefuseAsConflict:
                            // Never replace the user's - that would silently discard a condition or hit
                            // count they set deliberately - and never add beside it. Reported WITH its
                            // details, so the agent can choose another line or ask, and so it learns the
                            // user is already watching this one, which is worth knowing in itself.
                            conflict = existing.Describe();
                            break;

                        case ExistingBreakpointAction.Replace:
                            // Ours: a revision, which is the case Breakpoints.Prepare already handles
                            // WITHIN a batch. Removing and re-adding makes set_breakpoint idempotent per
                            // location, so re-setting a line with a new condition just works - today that
                            // needs clear_breakpoints first, and clearing is all-or-nothing across the
                            // agent's breakpoints, which loses every other stop to change one.
                            existing.Remove();
                            failure = PlaceBreakpoint(spec, tag);
                            break;

                        default:
                            failure = PlaceBreakpoint(spec, tag);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }

                if (failure is null && conflict is null)
                    placed++;
                else if (conflict is not null)
                    conflicted++;

                rows.Add(new
                {
                    file = spec.File,
                    line = spec.Line,
                    condition = spec.Condition,
                    hitCount = spec.HitCount,
                    printMessage = spec.PrintMessage,
                    reason = spec.Reason,
                    tracepoint = spec.PrintMessage is null ? (bool?)null : true,
                    // Three states, not two: "the location is already taken" is neither a success nor a
                    // malfunction, and collapsing it into either tells the agent the wrong thing to do
                    // next - retry blindly, or give up on a line that is merely occupied.
                    status = conflict != null ? "conflict" : failure is null ? "set" : "failed",
                    error = failure ?? conflict,
                });
            }

            // Subtraction counted conflicts as failures, which contradicted the three-state `status`
            // in the very same payload: a location that is merely occupied is neither a success nor a
            // malfunction, and telling the agent it FAILED invites the retry the status field exists to
            // prevent.
            var failed = batch.Specs.Count - placed - conflicted;
            var notes = new List<string>(batch.Notes);
            if (placed > 0 && _conversationId is null)
            {
                notes.Add("This conversation could not be identified, so these breakpoints are tagged as " +
                          "yours but not as belonging to any conversation: clear_breakpoints will only " +
                          "reach them with scope 'all'.");
            }

            // A batch where SOME were placed is a legitimate partial result, not a failed call: the rows
            // say which, and failing it would throw away the ones that worked. A batch where NONE were is
            // an error, because the caller asked for an effect and there is nothing to report but a false
            // impression (see docs/engineering/ide-services.md — "queries report, mutations must confirm").
            var payload = new
            {
                resultKind = "breakpoints",
                action = "set",
                requestId = NewRequestId(),
                summary = placed == batch.Specs.Count
                    ? $"Set {Plural(placed, "breakpoint")}."
                    : $"Set {placed} of {batch.Specs.Count} breakpoints; " +
                      string.Join(", ", new[]
                      {
                          failed > 0 ? $"{failed} failed" : null,
                          conflicted > 0 ? $"{conflicted} already taken by the user" : null,
                      }.Where(part => part is not null)) + ".",
                set = placed,
                failed,
                conflicts = conflicted,
                breakpoints = rows,
                notes = notes.Count > 0 ? notes : null,
            };
            return new ToolResult(IsError: placed == 0, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>A breakpoint already sitting where this call wants to put one.</summary>
        private sealed class ExistingBreakpoint
        {
            public Breakpoint Breakpoint { get; set; }
            public bool Mine { get; set; }
            public string Condition { get; set; }
            public int HitCount { get; set; }

            public string Describe()
            {
                var text = "There is already a breakpoint here that you did not set";
                if (!string.IsNullOrEmpty(Condition))
                    text += " (condition: " + Condition + ")";
                else if (HitCount > 0)
                    text += " (stops on hit " + HitCount + ")";

                return text +
                    ". Visual Studio keeps one breakpoint per location - clearing it from the gutter " +
                    "removes every breakpoint there - so a second one would be invisible to the user, " +
                    "ambiguous when it is hit, and removed by their next click. Choose another line, or " +
                    "ask the user to adjust theirs.";
            }

            public void Remove()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { Breakpoint.Delete(); }
                catch (Exception) { /* already gone, and the Add that follows is still what we want */ }
            }
        }

        /// <summary>
        /// The breakpoint at this location, with whether it is ours, or null.
        /// </summary>
        /// <remarks>
        /// Disabled breakpoints COUNT here, unlike in stop attribution where a disabled one cannot have
        /// fired: a disabled breakpoint still occupies the location, still shows in the gutter, and is
        /// still what the user's click removes.
        /// </remarks>
        private ExistingBreakpoint FindBreakpointAt(string file, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var wanted = AgentPath.Canonical(file);

            try
            {
                foreach (Breakpoint breakpoint in _dte.Debugger.Breakpoints)
                {
                    try
                    {
                        if (breakpoint.FileLine != line)
                            continue;
                        if (!string.Equals(AgentPath.Canonical(breakpoint.File), wanted, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string tag = null;
                        try { tag = (breakpoint as Breakpoint2)?.Tag; } catch (Exception) { }

                        return new ExistingBreakpoint
                        {
                            Breakpoint = breakpoint,
                            Mine = AgentBreakpoints.IsAgentTag(tag),
                            Condition = Blank(breakpoint.Condition),
                            HitCount = breakpoint.HitCountTarget,
                        };
                    }
                    catch (Exception)
                    {
                        // A breakpoint whose own members throw cannot be compared. Skipping it means we
                        // may add beside it - which is exactly the behaviour that existed before this
                        // guard, so it fails toward the old behaviour rather than toward a refusal.
                    }
                }
            }
            catch (Exception)
            {
                // No breakpoint collection at all: proceed as before rather than refuse to set anything.
            }

            return null;
        }

        private async Task<ToolResult> ListBreakpointsAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            string fileFilter;
            try
            {
                fileFilter = ReadOptionalString(argumentsJson, "file");
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var wanted = ResolveFileFilter(fileFilter);
            var rows = new List<object>();
            var mine = 0;
            var thisConversation = 0;
            try
            {
                foreach (Breakpoint bp in _dte.Debugger.Breakpoints)
                {
                    if (!TryDescribe(bp, out var described))
                    {
                        // A breakpoint whose own members throw cannot be described — but it is still THERE,
                        // and a list that silently omits it would tell the caller the gutter is emptier
                        // than it is.
                        rows.Add(new { error = "this breakpoint could not be read" });
                        continue;
                    }

                    if (wanted is not null && !AgentBreakpoints.KeyComparer.Equals(described.File ?? string.Empty, wanted))
                        continue;

                    var setByAgent = AgentBreakpoints.IsAgentTag(described.Tag);
                    var here = setByAgent && AgentBreakpoints.IsFromConversation(described.Tag, _conversationId);
                    if (setByAgent)
                        mine++;
                    if (here)
                        thisConversation++;

                    rows.Add(new
                    {
                        file = described.File,
                        line = described.Line,
                        enabled = described.Enabled,
                        condition = Blank(described.Condition),
                        hitCount = described.HitCount > 0 ? (int?)described.HitCount : null,
                        printMessage = Blank(described.Message),
                        tracepoint = string.IsNullOrEmpty(described.Message) ? (bool?)null : true,
                        setByAgent,
                        // Only meaningful for ours, and stated rather than left to be inferred from a
                        // missing field: "not mine" and "mine, from an earlier conversation" are different
                        // answers to the question the caller is actually asking.
                        thisConversation = setByAgent ? (bool?)here : null,
                    });
                }
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "could not read the breakpoints: " + ex.Message)));
            }

            var payload = new
            {
                resultKind = "breakpoints",
                action = "list",
                requestId = NewRequestId(),
                // Third person, and specific about WHICH conversation. Every string in this payload is
                // rendered on the transcript card, where the reader is the USER — so "set by you" said the
                // opposite of what it meant (reported from Visual Studio: the card told the user they had set four
                // breakpoints the agent had). And "set by the agent" is not enough on its own, because
                // that count spans conversations: the moment an earlier one is in the list it stops
                // describing what the user is looking at. The agent reads setByAgent/thisConversation for
                // its own decisions; those are unambiguous whoever is looking.
                summary = SummariseListing(rows.Count, mine, thisConversation, wanted is not null),
                total = rows.Count,
                setByAgent = mine,
                breakpoints = rows,
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        private async Task<ToolResult> ClearBreakpointsAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            string scope;
            string fileFilter;
            try
            {
                scope = ReadOptionalString(argumentsJson, "scope") ?? "conversation";
                fileFilter = ReadOptionalString(argumentsJson, "file");
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }

            var everywhere = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
            if (!everywhere && !string.Equals(scope, "conversation", StringComparison.OrdinalIgnoreCase))
            {
                return new ToolResult(IsError: true, Json.Object(
                    ("error", $"unknown scope '{scope}' — use 'conversation' (default, what you set in this " +
                              "conversation) or 'all' (everything you have set in this solution).")));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var wanted = ResolveFileFilter(fileFilter);
            var doomed = new List<Breakpoint>();
            var rows = new List<object>();
            var othersOfMine = 0;

            try
            {
                // Collected first, deleted after: deleting while enumerating a live COM collection is how
                // you skip half of it.
                foreach (Breakpoint bp in _dte.Debugger.Breakpoints)
                {
                    // A breakpoint we cannot read is one we cannot prove is ours, so it is left alone.
                    // That is the safe direction for a delete: the cost of skipping is a breakpoint the
                    // user removes by hand, the cost of guessing is removing one of theirs.
                    if (!TryDescribe(bp, out var described) || !AgentBreakpoints.IsAgentTag(described.Tag))
                        continue;

                    var inScope = everywhere || AgentBreakpoints.IsFromConversation(described.Tag, _conversationId);
                    var inFile = wanted is null || AgentBreakpoints.KeyComparer.Equals(described.File ?? string.Empty, wanted);

                    if (inScope && inFile)
                    {
                        doomed.Add(bp);
                        rows.Add(new { file = described.File, line = described.Line });
                    }
                    else
                    {
                        othersOfMine++;
                    }
                }
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "could not read the breakpoints: " + ex.Message)));
            }

            var removed = 0;
            string failure = null;
            foreach (var bp in doomed)
            {
                try
                {
                    bp.Delete();
                    removed++;
                }
                catch (Exception ex)
                {
                    failure ??= ex.Message;
                }
            }

            // Third person for the same reason the listing is (the card's reader is the user), and the
            // WIDE scope says so. Removing across every conversation is broader than the request usually
            // means, and it announced itself nowhere: the scope sat in the payload while the card said
            // only "Removed 4 breakpoints." An action wider than the one asked for has to be visible in
            // the sentence the user actually reads.
            var summary = doomed.Count == 0
                ? (everywhere
                    ? "No agent-set breakpoints to remove."
                    : "No breakpoints were set in this conversation.")
                : (everywhere
                    ? $"Removed {Plural(removed, "breakpoint")} set by the agent, across all conversations."
                    : $"Removed {Plural(removed, "breakpoint")} set in this conversation.");
            var notes = new List<string>();
            if (doomed.Count == 0 && !everywhere && othersOfMine > 0)
            {
                // Advice in a payload is as load-bearing as the result: without this the caller reads
                // "nothing to remove" and stops, while breakpoints it set are still in the user's gutter.
                // Phrased as a statement rather than an instruction, because it is shown to the user too.
                notes.Add($"{Plural(othersOfMine, "breakpoint")} set in earlier conversations remain; " +
                          "clearing with scope 'all' would remove those as well.");
            }
            if (failure is not null)
                notes.Add("At least one could not be removed: " + failure);

            var payload = new
            {
                resultKind = "breakpoints",
                action = "cleared",
                requestId = NewRequestId(),
                summary,
                scope = everywhere ? "all" : "conversation",
                removed,
                remaining = othersOfMine + (doomed.Count - removed),
                breakpoints = rows,
                notes = notes.Count > 0 ? notes : null,
            };
            // Finding nothing of ours is a legitimate negative — the true answer to "remove what you set"
            // when there is nothing — so it succeeds and says so. Failing to delete one we DID find is an
            // error: the caller asked for an effect that did not happen.
            var couldNotDelete = doomed.Count > 0 && removed == 0;
            return new ToolResult(IsError: couldNotDelete, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Reads the Debug output pane, so the agent can collect its own tracepoint messages instead of
        /// asking the user to paste them (issue #73).
        /// </summary>
        /// <remarks>
        /// This completes the loop rung 1 otherwise leaves half-open: the agent instruments a run, the
        /// user starts debugging once, and the values come back without anybody transcribing anything. It is
        /// deliberately NOT rung 3 — nothing is launched, the debuggee is never driven, there is no break
        /// mode to sit in and no process to orphan. Every hazard that gates rung 3 comes from RUNNING the
        /// program; this reads a text buffer.
        /// <para>
        /// Mechanically the same read <c>build_solution</c> already does against the Build pane, one GUID
        /// along, and it inherits that method's rule: switch to the UI thread here rather than requiring
        /// the caller to be on it (issue #89 — a <c>ConfigureAwait(false)</c> anywhere in front of it
        /// would otherwise make this throw <c>RPC_E_WRONG_THREAD</c> deterministically).
        /// </para>
        /// </remarks>
        private async Task<ToolResult> GetDebugOutputAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            string contains;
            int? tailLines;
            try
            {
                contains = ReadOptionalString(argumentsJson, "contains");
                tailLines = ReadOptionalInt(argumentsJson, "tailLines");
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }

            if (tailLines is { } requested && requested < 1)
            {
                return new ToolResult(IsError: true, Json.Object(
                    ("error", $"tailLines was {requested}; omit it for everything that fits, or pass 1 or more.")));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // "Nothing there" and "couldn't look" are different answers, and only one of them is a
            // problem — so the read reports WHICH it reached rather than throwing its way past the
            // distinction (issue #272). No catch here: every step of the walk is guarded inside, where
            // what failed is still known.
            var read = ReadDebugPane();

            if (read.Outcome == DebugPaneOutcome.Unreadable)
            {
                return new ToolResult(IsError: true, Json.Object(("error", read.Summary)));
            }

            if (read.Outcome == DebugPaneOutcome.Absent)
            {
                // A true, useful answer — no pane means no debug session has run in this instance — so it
                // succeeds and says which case it is rather than returning a bare empty. Where the
                // absence was inferred from a throw, that message rides along as a note: this issue's own
                // diagnosis was never reproduced, and the note is what identifies the getter next time.
                var absent = new
                {
                    pane = "Debug",
                    summary = read.Summary,
                    note = Blank(read.Detail),
                    lines = 0,
                    output = (string)null,
                };
                return new ToolResult(IsError: false, JsonSerializer.Serialize(absent, JsonOptions));
            }

            var text = read.Text;

            var totalLines = text.Length == 0 ? 0 : text.Split('\n').Length;
            var matched = OutputTail.LinesContaining(text, contains);
            // Narrow, then trim. Both reduce, and both are counted as truncation below, so a caller can
            // never mistake a deliberately short read for the whole of a short run.
            var kept = OutputTail.LastLines(matched, tailLines);

            // Tail, not head: the pane accumulates across runs within one Visual Studio session, so the
            // interesting output is the most recent. Keeping the wrong end returns plausible, correctly
            // formatted output describing a run nobody asked about — see OutputTail.
            var joined = OutputTail.Keep(string.Join("\n", kept).TrimEnd(), MaxBuildOutputChars, out var charTrimmed);
            var returnedLines = joined.Length == 0 ? 0 : joined.Split('\n').Length;
            // Either reduction counts: a caller that asked for the last 20 lines still needs to know that
            // 200 matched, or "20 lines" reads as the whole run.
            var trimmed = charTrimmed || returnedLines < matched.Count;

            var payload = new
            {
                pane = "Debug",
                summary = kept.Count == 0
                    ? (string.IsNullOrEmpty(contains)
                        ? "The Debug output pane is empty."
                        : $"No lines in the Debug output contain '{contains}'.")
                    : $"{Plural(returnedLines, "line")} of debug output"
                      + (string.IsNullOrEmpty(contains) ? string.Empty : $" containing '{contains}'")
                      + (trimmed ? $", trimmed from {matched.Count}." : "."),
                filter = Blank(contains),
                // Counted from the text being RETURNED, not from what matched. The two differ whenever
                // the tail trimmed, and reporting the matched count here put "63 lines" beside an output
                // holding about fifty of them (observed live). A count that does not describe the thing
                // it sits next to is the defect, however small the gap.
                lines = returnedLines,
                // What matched before trimming, and what the pane holds. Three numbers because trimming
                // makes three of them true at once, and only the first describes 'output'.
                matchedLines = trimmed ? (int?)matched.Count : null,
                totalLines = totalLines,
                truncated = trimmed,
                output = kept.Count == 0 ? null : joined,
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Serves BOTH evaluation tools. Which one was called decides whether code may run — the name is
        /// the gate, because the permission layer sees a name and does not yet see arguments.
        /// </summary>
        private async Task<ToolResult> EvaluateExpressionsAsync(
            string argumentsJson, bool allowSideEffects, CancellationToken cancellationToken)
        {
            List<string> expressions;
            int? frameIndex;
            try
            {
                expressions = ReadStringArray(argumentsJson, "expressions");
                frameIndex = ReadOptionalInt(argumentsJson, "frame");
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }

            if (expressions is null || expressions.Count == 0)
                return new ToolResult(IsError: true, Json.Object(("error", "pass at least one expression to evaluate.")));

            if (expressions.Count > DebugEvalLimits.MaxExpressions)
            {
                return new ToolResult(IsError: true, Json.Object(
                    ("error", $"{expressions.Count} expressions is more than the {DebugEvalLimits.MaxExpressions} this tool takes at once; send the most useful ones.")));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // "Not stopped" is a fact about the world rather than a broken tool - but there is no value to
            // return, so it fails with the sentence that says what to do instead. A success carrying no
            // value invites reasoning from nothing.
            if (!VsDebugState.IsInBreakMode(_dte))
            {
                return new ToolResult(IsError: true, Json.Object(
                    ("error", "The debugger is not stopped, so there is nothing to evaluate. Set a breakpoint with set_breakpoint and ask the user to run.")));
            }

            IReadOnlyList<Ad7Evaluator.Frame> frames;
            try
            {
                frames = Ad7Evaluator.Frames();
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "could not read the call stack: " + ex.Message)));
            }

            if (frames.Count == 0)
            {
                return new ToolResult(IsError: true, Json.Object(
                    ("error", "The debugger is stopped but no stack frame could be read. If the session was already stopped when this window opened, continue and stop again.")));
            }

            var wanted = frameIndex ?? 1;
            if (wanted < 1 || wanted > frames.Count)
            {
                return new ToolResult(IsError: true, Json.Object(
                    ("error", $"frame {wanted} does not exist; the stack has {frames.Count} frame(s), numbered from 1 (innermost).")));
            }

            var frame = frames[wanted - 1];
            IReadOnlyList<DebugEvaluation> results;
            int skipped;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                results = Ad7Evaluator.Evaluate(frame, expressions, allowSideEffects, out skipped);
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "the evaluation failed: " + ex.Message)));
            }

            clock.Stop();
            // Timed and logged because "the typing dots kept moving" is the only evidence anyone has had
            // that this does not freeze the IDE - and that is evidence for QUICK, not for unblocked: the
            // dots cannot resolve a stall shorter than perception, and AGENTS.md already records them
            // animating while hidden. Every AD7 call here is synchronous on the UI thread, so this number
            // is the freeze, per call, on the user's own machine.
            // ToolErrorLog is for errors and this is not one; DiagnosticLog owns the stamping, the roll
            // and the retention policy, so it goes there - beside tool-error.log rather than inside it.
            try
            {
                Core.DiagnosticLog.AppendLine(
                    System.IO.Path.Combine(ToolErrorLog.Directory, "debug-eval.log"),
                    $"[debug-eval] {clock.ElapsedMilliseconds} ms, {expressions.Count} expression(s), " +
                    $"frame {frame.Index}/{frames.Count}, sideEffects={allowSideEffects}, " +
                    $"evaluated={results.Count}, skipped={skipped}");
            }
            catch (Exception)
            {
                // A trace must never cost the caller their answer.
            }

            var refused = 0;
            var rows = new List<object>();
            foreach (var result in results)
            {
                if (result.Kind != DebugValueKind.Value)
                    refused++;

                // NAMES what the expansion dropped, rather than only counting it. A bare count declares
                // the truncation - which is the rule - and still leaves the reader unable to act on it:
                // on a large object it is the difference between "nothing I needed" and "the field I am
                // looking for, silently dropped" to an agent reading this payload.
                var omitted = result.Omitted is null ? null : (object)new
                {
                    count = result.Omitted.Count,
                    reason = DebugOmission.WireName(result.Omitted.Reason),
                    // Present only when it is TRUE, so the ordinary case reads as an exact count and this
                    // one has to be noticed: the enumeration itself stopped, so both the count and the
                    // names below are a floor. Reachable on any large collection.
                    countIsFloor = result.Omitted.CountIsFloor ? (bool?)true : null,
                    names = result.Omitted.Names,
                };

                rows.Add(new
                {
                    expression = result.Expression,
                    // The three states are named rather than left to be inferred from a missing value:
                    // "no value" and "a value that happens to be absent" are different answers.
                    kind = result.Kind switch
                    {
                        DebugValueKind.Value => "value",
                        DebugValueKind.NeedsCode => "refused",
                        _ => "unavailable",
                    },
                    type = result.Type,
                    value = result.Value,
                    // The debugger's own words, never paraphrased - it says which of the two an
                    // "unavailable" is, and no attribute can.
                    message = result.Message,
                    expandable = result.IsExpandable ? (bool?)true : null,
                    // NAMES the tool rather than asserting a boolean. A flag such as
                    // retryWithSideEffects:true points at an option by name, and goes on pointing at it
                    // once the escape hatch is a separate tool rather than a parameter on this same call.
                    // A string that says where to go needs no context
                    // to interpret; a bool needed the reader to remember a parameter name.
                    //
                    // And only ever on a GUARDED call: telling the agent to retry with the tool it just
                    // used would be advice to loop.
                    //
                    // A MEMBER that needed code counts too: the top-level read succeeded, so without this
                    // an object whose every property was refused pointed nowhere, and the reader had to
                    // know that the tool named beside a member's "needsCode" is the same one.
                    retryWith = !allowSideEffects &&
                                (result.Kind == DebugValueKind.NeedsCode || result.MayNeedCode ||
                                 result.Children.Any(c => c.ValueUnavailable == "needsCode"))
                        ? "execute_expression"
                        : null,
                    members = result.Children.Count == 0 ? null : result.Children.Select(c => new
                    {
                        name = c.Name,
                        type = c.Type,
                        value = c.Value,
                        expandable = c.IsExpandable ? (bool?)true : null,
                        // Only on getters, and only when something guarded them: it answers "why has
                        // this no value, and would execute_expression get one" without the agent
                        // discovering it by refusal. Silent on fields, which are the majority.
                        isProperty = c.IsProperty ? (bool?)true : null,
                        // WHY there is no value, in place of the refusal that used to be returned AS one.
                        // An expandable member reads "notExpanded" - the structural fact - rather than a
                        // cause we cannot establish; the debugger's own sentence follows it.
                        valueUnavailable = c.ValueUnavailable,
                        message = c.Message,
                    }).ToArray(),
                    omittedMembers = omitted,
                });
            }

            var payload = new
            {
                resultKind = "debugEvaluation",
                requestId = NewRequestId(),
                summary = SummariseEvaluation(results.Count, refused, frame, allowSideEffects),
                // Stated so the agent can see WHICH frame answered, and name another next time. Its own
                // numbering, so nothing has to be translated from the <debug-state> block the user sent.
                frame = new { index = frame.Index, method = frame.Method, file = frame.File, line = frame.Line > 0 ? (int?)frame.Line : null },
                // WHY it is stopped, which the agent could otherwise learn only from the <debug-state>
                // block the USER pushes. Composed by VsDebugState so the push
                // and this pull cannot describe the same stop in different words.
                stop = new
                {
                    reason = VsDebugState.DescribeStop(_dte, frame.File, frame.Line, _conversationId),
                    thread = VsDebugState.ThreadName(_dte),
                    // A pushed block carries no expiry: read a few turns later it may describe a stop that
                    // no longer exists, and nothing in it says so. process+number is what turns that from
                    // a coincidence the agent has to notice into a fact it can read.
                    processId = TryProcessId(),
                    number = Ad7DebugEvents.StopCount,
                },
                frames = frames.Select(f => new { index = f.Index, method = f.Method, file = f.File, line = f.Line > 0 ? (int?)f.Line : null }).ToArray(),
                sideEffectsAllowed = allowSideEffects,
                // Stated, never silent: a call that stopped early and said nothing reads as a complete
                // answer, which is the reduction-must-be-announced rule the rest of this file follows.
                skippedForBudget = skipped > 0 ? (int?)skipped : null,
                results = rows,
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>The debuggee's process id, or null. Half of a stop's identity; the other half is
        /// <see cref="Ad7DebugEvents.StopCount"/>.</summary>
        private int? TryProcessId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return _dte?.Debugger?.CurrentProcess?.ProcessID;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SummariseEvaluation(int total, int refused, Ad7Evaluator.Frame frame, bool allowSideEffects)
        {
            var where = frame.HasSource
                ? $" in {System.IO.Path.GetFileName(frame.File)}:{frame.Line}"
                : $" in frame {frame.Index}";

            if (refused == 0)
                return $"Evaluated {Plural(total, "expression")}{where}.";

            var mode = allowSideEffects ? string.Empty : " (nothing was allowed to run)";
            return refused == total
                ? $"None of the {Plural(total, "expression")} could be read{where}{mode}."
                : $"Evaluated {total - refused} of {Plural(total, "expression")}{where}{mode}.";
        }

        /// <summary>
        /// The Debug pane's whole text, or which of the other three things happened instead (issue #272).
        /// </summary>
        /// <remarks>
        /// <b>The walk is guarded at each of its four steps rather than wrapped in one <c>catch</c>,
        /// because each step failing means something different.</b> This used to return a string-or-null
        /// under a single <c>catch (Exception)</c> at the call site, which turned every one of them into
        /// the same opaque HRESULT and made the caller's considered "there is no pane" branch unreachable
        /// in the very case it exists for — see <see cref="DebugPaneRead"/> for the measurement.
        /// <para>
        /// <b>The per-pane guard is the non-obvious half.</b> The Output window holds every pane in the
        /// instance, ours and other extensions' alike, and the loop touches <c>Guid</c> on all of them to
        /// find the one it wants. One uncooperative stranger's getter throwing used to end the search, so
        /// a Debug pane that was present and readable reported as a failure decided by which extensions
        /// happened to be installed. A pane that will not say what it is cannot be the one we are looking
        /// for, so it is skipped.
        /// </para>
        /// </remarks>
        private DebugPaneRead ReadDebugPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Asking for the Output window is its own step: a tool window that has never been created is
            // the leading suspect for this issue's E_FAIL, and the answer to it is "there is nothing to
            // read", not "the read failed". A null says the same thing with no failure to report.
            EnvDTE.OutputWindow window;
            try
            {
                window = _dte?.ToolWindows?.OutputWindow;
            }
            catch (Exception ex)
            {
                return DebugPaneRead.NoOutputWindow(ex.Message);
            }

            if (window is null)
                return DebugPaneRead.NoOutputWindow(detail: null);

            // Searching is the step that can genuinely leave us unable to answer: a search that did not
            // finish has established nothing about whether the pane is there.
            EnvDTE.OutputWindowPane found;
            try
            {
                var collection = window.OutputWindowPanes;
                if (collection is null)
                    return DebugPaneRead.NoOutputWindow(detail: null);
                found = FindPane(collection, VSConstants.OutputWindowPaneGuid.DebugPane_guid);
            }
            catch (Exception ex)
            {
                return DebugPaneRead.SearchFailed(ex.Message);
            }

            if (found is null)
                return DebugPaneRead.NoDebugPane();

            try
            {
                var doc = found.TextDocument;
                var text = doc?.StartPoint?.CreateEditPoint()?.GetText(doc.EndPoint);
                return DebugPaneRead.Read(text is null ? string.Empty : text.Replace("\r\n", "\n"));
            }
            catch (Exception ex)
            {
                // Found ours and could not read it — the one outcome here that is a real failure.
                return DebugPaneRead.Unreadable(ex.Message);
            }
        }

        /// <summary>
        /// The pane carrying this GUID, or null if the window holds none. Shared by the Debug read and
        /// <c>build_solution</c>'s Build-pane tail rather than written twice, for the reason those two
        /// already share <see cref="OutputTail"/>: the guard below is the whole substance of the search,
        /// and a second copy is a second chance to omit it.
        /// </summary>
        /// <remarks>
        /// <b>Each pane's identity is read under its own guard.</b> The Output window holds every pane in
        /// the instance — ours, Visual Studio's, and any extension's — and finding one means touching
        /// <c>Guid</c> on all of them. A stranger's getter throwing used to end the search, so whether a
        /// present, readable Debug or Build pane could be found at all turned on which extensions happened
        /// to be installed. A pane that will not say what it is cannot be the one we are looking for.
        /// <para>
        /// It deliberately does NOT catch around the enumeration itself: the collection refusing to be
        /// walked means the search did not happen, and what that is worth differs between the two callers
        /// — an error the agent must not read as an empty pane, against a backstop that is simply omitted.
        /// That judgement stays with them.
        /// </para>
        /// </remarks>
        private static EnvDTE.OutputWindowPane FindPane(EnvDTE.OutputWindowPanes panes, Guid paneGuid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (EnvDTE.OutputWindowPane pane in panes)
            {
                Guid g;
                try
                {
                    if (!Guid.TryParse(pane.Guid, out g))
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                if (g == paneGuid)
                    return pane;
            }

            return null;
        }

        /// <summary>
        /// Adds one breakpoint and stamps it as ours. Returns null on success, or the reason it did not
        /// take. Never called for a location the user already owns - that arm of the switch above does
        /// not reach here, which is the whole point of lifting this out.
        /// </summary>
        private string PlaceBreakpoint(BreakpointSpec spec, string tag)
        {
            // Asserted rather than assumed: the caller switched to the main thread long before the
            // loop, and lifting this method out of it moved the DTE calls away from that statement.
            ThreadHelper.ThrowIfNotOnUIThread();

            // Add takes twelve positional parameters; named arguments keep the two that matter (at the
            // end) legible. A location can bind to more than one breakpoint, which is why this returns a
            // collection rather than a breakpoint.
            var added = _dte.Debugger.Breakpoints.Add(
                Function: string.Empty,
                File: spec.File,
                Line: spec.Line,
                Column: 1,
                Condition: spec.Condition ?? string.Empty,
                ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                Language: string.Empty,
                Data: string.Empty,
                DataCount: 1,
                Address: string.Empty,
                HitCount: spec.HitCount ?? 0,
                HitCountType: spec.HitCount is null
                    ? dbgHitCountType.dbgHitCountTypeNone
                    : dbgHitCountType.dbgHitCountTypeEqual);

            var bound = new List<Breakpoint>();
            if (added is not null)
            {
                foreach (Breakpoint bp in added)
                    bound.Add(bp);
            }

            if (bound.Count == 0)
                return "Visual Studio accepted the request but created no breakpoint — check the " +
                       "line is an executable statement in a file that is part of the build.";

            foreach (var bp in bound)
            {
                var refused = ApplyOurMarkers(bp, tag, spec.PrintMessage);
                if (refused is null)
                    continue;

                // A tracepoint that did not take is not a lesser tracepoint, it is a STOP: the user's next
                // The run halts on a line where they were promised it would print and carry on. Reporting that
                // row as set (it used to be, with tracepoint:true beside it) is the apply_code_fix shape —
                // a summary claiming an effect the IDE did not produce. So nothing is left behind, and the
                // row fails with the reason.
                foreach (var placed in bound)
                {
                    try { placed.Delete(); }
                    catch (Exception) { /* already gone is the outcome we want */ }
                }
                return refused;
            }

            return null;
        }

        /// <summary>
        /// Stamps our tag on a breakpoint, and turns it into a tracepoint when a message was asked for.
        /// </summary>
        /// <remarks>
        /// A tracepoint is exactly <c>Message</c> set AND <c>BreakWhenHit</c> false — "show a message in
        /// the Output window" plus "continue code execution". Verified in Visual Studio, 2026-08-26, along with the fact
        /// that both survive a devenv restart; VS's own breakpoint export shows the result as
        /// <c>TracepointText</c> + <c>IsTracepointActive</c> + <c>TracepointTargetType=VsOutputWindow</c>
        /// + <c>IsBreakWhenHit=0</c>, so this writes the first-class thing rather than an approximation
        /// of it.
        /// <para>
        /// Two neighbouring members on this interface are deliberately never touched. <c>Macro</c> throws
        /// on read and write — VS dropped macro support. <c>FilterBy</c> accepts any string and becomes a
        /// real thread/process filter, which silently stops the breakpoint ever matching.
        /// </para>
        /// <para>
        /// The TAG is best-effort: a breakpoint that will not take it is still a breakpoint the user can
        /// use, so losing the marker costs us the ability to clear it automatically, not the feature. The
        /// MESSAGE is not: without it the breakpoint is a stop where a tracepoint was asked for, which is a
        /// different thing, so a refusal is returned for the caller to undo rather than swallowed.
        /// </para>
        /// <para>
        /// <paramref name="printMessage"/> is an expression the debugger EVALUATES in the debuggee, so it
        /// only ever reaches here from <see cref="IdeToolDescriptors.SetExpressionBreakpoint"/>, the Command-tier name:
        /// <c>Breakpoints.Prepare</c> refuses the whole batch when the plain name carries one. This
        /// method must not become a second way in.
        /// </para>
        /// </remarks>
        /// <returns>Null when the breakpoint is what was asked for; otherwise why it is not.</returns>
        private static string ApplyOurMarkers(Breakpoint bp, string tag, string printMessage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var wantsTracepoint = !string.IsNullOrEmpty(printMessage);
            if (bp is not Breakpoint2 bp2)
            {
                return wantsTracepoint
                    ? "Visual Studio created the breakpoint but did not offer the interface that makes it a " +
                      "tracepoint, so it was removed rather than left as a stop. Nothing is set on this line."
                    : null;
            }

            try { bp2.Tag = tag; } catch (Exception) { /* marker only */ }

            if (!wantsTracepoint)
                return null;

            try
            {
                bp2.Message = printMessage;
                bp2.BreakWhenHit = false;
                return null;
            }
            catch (Exception ex)
            {
                return "Visual Studio created the breakpoint but refused to make it a tracepoint (" + ex.Message +
                       "), so it was removed rather than left as a stop. Nothing is set on this line.";
            }
        }

        /// <summary>Reads the <c>breakpoints</c> array into specs; shape errors surface from Prepare.</summary>
        /// <remarks>
        /// It reads <c>condition</c> and <c>printMessage</c> whichever name the call arrived on, even
        /// though <c>set_breakpoint</c> does not advertise them. That is deliberate: dropping them here
        /// would put the strip back, silently, one layer below the refusal that exists to prevent it.
        /// </remarks>
        private static List<BreakpointSpec> ReadBreakpointSpecs(string argumentsJson)
        {
            var specs = new List<BreakpointSpec>();
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return specs;

            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("breakpoints", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return specs;
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                string Str(string name) =>
                    entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                int? Int(string name) =>
                    entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                        ? (int?)n : null;

                specs.Add(new BreakpointSpec(Str("file") ?? string.Empty, Int("line") ?? 0)
                {
                    Condition = Str("condition"),
                    HitCount = Int("hitCount"),
                    PrintMessage = Str("printMessage"),
                    Reason = Str("reason"),
                });
            }

            return specs;
        }

        /// <summary>An optional integer argument; null when absent or not a number.</summary>
        /// <remarks>
        /// Absent and zero must stay distinguishable, which is why this is nullable rather than
        /// defaulting to 0 — the same rule <c>TableValue</c> exists for, where "absent" reading as a
        /// number silently became a real value.
        /// </remarks>
        private static bool? ReadOptionalBool(string argumentsJson, string name)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(name, out var v))
            {
                return null;
            }
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            };
        }

        /// <summary>
        /// A required array of strings. Non-string entries are skipped rather than failing the call: one
        /// malformed element should cost that element, not the other nine the agent sent with it.
        /// </summary>
        private static List<string> ReadStringArray(string argumentsJson, string name)
        {
            var values = new List<string>();
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return values;

            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(name, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    values.Add(text);
            }
            return values;
        }

        private static int? ReadOptionalInt(string argumentsJson, string name)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                   && v.TryGetInt32(out var n)
                ? (int?)n
                : null;
        }

        private static string ReadOptionalString(string argumentsJson, string name)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }

        /// <summary>The canonical form of a <c>file</c> filter, or null when none was given.</summary>
        private string ResolveFileFilter(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return string.IsNullOrWhiteSpace(file) ? null : AgentPath.Canonical(file.Trim(), RelativePathRoot());
        }

        /// <summary>
        /// The listing's one-line account, written for the person reading the card.
        /// </summary>
        /// <remarks>
        /// Three shapes because there are three situations, and collapsing them loses the one that
        /// matters. All of the agent's are from this conversation — the ordinary case — so say that.
        /// Some are older, which is precisely when <c>clear_breakpoints</c>' default scope will not reach
        /// them, so the count is worth surfacing before the user asks why something survived. Or none are
        /// the agent's at all, which is a real answer rather than a zero to be inferred.
        /// </remarks>
        private static string SummariseListing(int total, int agentSet, int thisConversation, bool scopedToFile)
        {
            if (total == 0)
                return scopedToFile ? "No breakpoints are set in that file." : "No breakpoints are set.";
            if (agentSet == 0)
                return $"{Plural(total, "breakpoint")}, none set by the agent.";
            if (agentSet == thisConversation)
                return $"{Plural(total, "breakpoint")}, {agentSet} set in this conversation.";
            return $"{Plural(total, "breakpoint")}, {agentSet} set by the agent " +
                   $"({thisConversation} in this conversation).";
        }

        private static string NewRequestId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        private static string Plural(int count, string noun) =>
            count == 1 ? "1 " + noun : count + " " + noun + "s";

        private static string Blank(string value) => string.IsNullOrEmpty(value) ? null : value;

        /// <summary>Everything we read off one breakpoint, gathered in a single guarded pass.</summary>
        private struct BreakpointFacts
        {
            public string File;
            public int Line;
            public bool Enabled;
            public string Condition;
            public int HitCount;
            public string Message;
            public string Tag;
        }

        /// <summary>
        /// Reads a breakpoint's members, or reports that it could not be read. False means the CALLER
        /// decides what to do about it, which differs by tool: a list says so rather than dropping the
        /// row, a clear leaves it alone rather than guessing whether it is ours.
        /// </summary>
        /// <remarks>
        /// One try around the whole read, deliberately. Reading each member through its own guarded
        /// lambda was the first shape and it cost every access a VSTHRD010 warning — the analyzer cannot
        /// see that the closure runs synchronously inside a method that has already asserted the UI
        /// thread. Direct reads are also the honest shape: a COM object throwing on <c>File</c> is not
        /// going to answer <c>FileLine</c> meaningfully either.
        /// </remarks>
        private static bool TryDescribe(Breakpoint bp, out BreakpointFacts facts)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            facts = default;
            try
            {
                var bp2 = bp as Breakpoint2;
                facts.File = bp.File;
                facts.Line = bp.FileLine;
                facts.Enabled = bp.Enabled;
                facts.Condition = bp.Condition;
                facts.HitCount = bp.HitCountTarget;
                facts.Message = bp2?.Message;
                facts.Tag = bp2?.Tag;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Best-effort prune of previous run_tests output so the per-run TRX folders don't accumulate. Each
        /// run writes to its own GUID subfolder; we clear the earlier ones at the START of the next run (rather
        /// than immediately after parsing) so the just-finished run's <c>.trx</c> lingers — openable in VS via
        /// the payload's <c>resultsFile</c> — until a new run supersedes it. Net effect: at most one run's
        /// output on disk at rest. A folder locked by a still-open TRX is skipped and reclaimed next time.
        /// </summary>
        private static void PruneOldTestResults(string testResultsRoot)
        {
            try
            {
                if (!Directory.Exists(testResultsRoot))
                    return;
                foreach (var dir in Directory.EnumerateDirectories(testResultsRoot))
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* in use / locked — leave it; the next run retries */ }
                }
            }
            catch { /* enumeration failed — non-fatal, results still get written */ }
        }

        /// <summary>Aggregatable parsed contents of one or more TRX files (so multiple MTP projects merge into one result).</summary>
        private sealed class TrxData
        {
            public int Total;
            public int Passed;
            public int Failed;
            public int Skipped;
            public readonly List<object> Failures = new();
            public readonly List<object> Skips = new();
        }

        /// <summary>
        /// Accumulates one VSTest TRX into <paramref name="into"/>: counts + failing tests (with source
        /// locations). Namespace-agnostic (matches by local name). Detailed failures are capped globally at
        /// <see cref="MaxFailuresReported"/> across all accumulated files. Throws on unreadable XML.
        /// The counts come from <see cref="TrxSummary"/> (in Core, so they can be pinned by tests) and are
        /// read PER FILE then added: its reconciliation of the header against the results is only
        /// meaningful within the one run that wrote both.
        /// <paramref name="project"/> rides on every failing and skipped test, being the half of "which
        /// test is this" the file itself cannot supply — one TRX knows nothing of the run's other projects.
        /// </summary>
        private static void AccumulateTrx(TrxData into, string trxPath, string workspaceRoot, string project = null)
        {
            var doc = XDocument.Load(trxPath);
            var root = doc.Root;

            var counts = TrxSummary.Read(root);
            into.Total += counts.Total;
            into.Passed += counts.Passed;
            into.Failed += counts.Failed;
            into.Skipped += counts.Skipped;

            var results = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Results");
            if (results is null)
                return;

            // testId -> the declaring class. Lets the test-location rule VERIFY which frame is the test
            // method rather than trusting its position in the trace (see TestStackFrames.Test), and names
            // the test unambiguously in the payload. Read in Core, so the skipped list and this walk
            // cannot disagree about what a test's class IS.
            var classNameByTestId = TrxSummary.ClassNamesByTestId(root);

            // Which tests did not run, and why: named here rather than left to the count alone, since the
            // reason a runner records is the answer to the only question a bare "2 skipped" raises.
            foreach (var skip in TrxSummary.ReadSkipped(root))
            {
                if (into.Skips.Count >= MaxSkippedReported)
                    break;
                into.Skips.Add(new
                {
                    name = skip.Name,
                    className = skip.ClassName,
                    project,
                    outcome = skip.Outcome,
                    reason = Truncate(skip.Reason, MaxMessageLength),
                });
            }

            foreach (var r in results.Elements().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                var outcome = (string)r.Attribute("outcome");
                if (!TrxSummary.IsFailure(outcome))
                    continue;
                if (into.Failures.Count >= MaxFailuresReported)
                    break;
                var errorInfo = r.Elements().FirstOrDefault(e => e.Name.LocalName == "Output")
                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
                var message = errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
                var stack = errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value;
                // Both locations come off the FULL stack, before the payload's trace is truncated.
                var testId = (string)r.Attribute("testId");
                classNameByTestId.TryGetValue(testId ?? string.Empty, out var testClassName);
                var location = TestStackFrames.Failure(stack, workspaceRoot);
                var testLocation = TestStackFrames.Test(stack, workspaceRoot, location, testClassName);
                into.Failures.Add(new
                {
                    name = (string)r.Attribute("testName"),
                    // NUnit and MSTest write a BARE method name here, xUnit a qualified one, so the name
                    // alone cannot say which project's TestMethod1 this is. Reported beside it rather than
                    // joined into it: see TrxSummary's remarks for the case where joining is wrong.
                    className = testClassName,
                    project,
                    outcome,
                    message = Truncate(message, MaxMessageLength),
                    stackTrace = Truncate(stack, MaxStackTraceLength),
                    file = location?.File,
                    line = location?.Line,
                    // Where the TEST is, when that isn't where it threw (a shared assertion helper, a
                    // Reqnroll step definition). Absent when it would just repeat file/line.
                    testFile = testLocation?.File,
                    testLine = testLocation?.Line,
                });
            }
        }

        /// <summary>
        /// Builds the run_tests structured result (the host's test-results card) from aggregated TRX data.
        /// <paramref name="notes"/> carries per-project problems (a project that produced no readable TRX);
        /// when <paramref name="incomplete"/> is set those projects' tests are missing from the counts, so
        /// the run never reports success — the notes say which projects and why.
        /// <para>
        /// <paramref name="resultsFile"/> and <paramref name="resultsFiles"/> are not the same thing and
        /// both are needed. A run writes ONE TRX PER PROJECT, so the single path can only ever be one of
        /// them — it stays because it is the card's dedupe key (unique per run: each project gets its own
        /// GUID directory), and it is the field every recorded conversation already carries. The complete
        /// per-project set is <paramref name="resultsFiles"/>, without which a three-project solution
        /// reports one file and the results that are not in it are unreachable from the payload.
        /// </para>
        /// </summary>
        private static ToolResult BuildTestRunResult(TrxData data, string resultsFile, IReadOnlyList<string> notes = null,
            bool incomplete = false, IReadOnlyList<(string Project, string File)> resultsFiles = null)
        {
            var succeeded = data.Failed == 0 && data.Total > 0 && !incomplete;
            TestRunDebugLog.Write($"test result: total={data.Total} passed={data.Passed} failed={data.Failed} skipped={data.Skipped} incomplete={incomplete}");
            var payload = new
            {
                // Marker: lets the host render a rich test-results card. Kept stable AND serialized as the
                // FIRST property — every reader keys off the exact prefix
                // (Core.Ide.StructuredToolResult.TestRunPrefix), which costs no parse and can't be
                // false-positived by output that merely quotes the marker text. Reordering these
                // properties silently un-cards the run. Full fidelity reaches the UI via the shell
                // side-channel (ShellRpcTarget.ToolsInvokeAsync); the agent's echoed copy takes the ACP
                // mapper's path, where the display clamp exempts this shape (issue #83).
                resultKind = "testRun",
                succeeded,
                total = data.Total,
                passed = data.Passed,
                failed = data.Failed,
                skipped = data.Skipped,
                failureCount = data.Failed,
                truncatedFailures = data.Failed > data.Failures.Count,
                failures = data.Failures,
                // Which tests did not run, and why. Omitted entirely when none did, so a clean run reads as
                // it always has. `truncatedSkipped` is the one rule that the list is shorter than the count
                // — the cap, and equally a file that gave counts with no results to name them from.
                skippedTests = data.Skips.Count > 0 ? data.Skips : null,
                truncatedSkipped = data.Skipped > data.Skips.Count ? (bool?)true : null,
                resultsFile,
                resultsFiles = resultsFiles is { Count: > 0 }
                    ? resultsFiles.Select(f => new { project = f.Project, file = f.File }).ToList()
                    : null,
                incompleteResults = incomplete ? (bool?)true : null,
                notes = notes is { Count: > 0 } ? notes : null,
            };
            // Like build_solution: a run with failures reports isError:true (with details in the payload).
            return new ToolResult(IsError: !succeeded, JsonSerializer.Serialize(payload, JsonOptions));
        }

        // ---- command-line test runner ---------------------------------------------------------------
        // Both frameworks run via the command line and yield a TRX we aggregate into one card. MTP (xUnit v3
        // etc.) runs the test host directly via `dotnet run --no-build -- --report-trx`, because a Test
        // Explorer run drives the MTP host in server mode and never passes `--report-trx`; classic vstest
        // runs via `dotnet test --no-build --logger trx`. Test Explorer automation is also whole-suite only,
        // where the command line takes a subset filter.

        // ---- test filter arguments ----
        // Parsing the agent's JSON into a FilterSpec stays here (Core takes no JSON dependency);
        // translating that spec to each framework's native flags is Core.Ide.TestRunCommands.

        /// <summary>Reads a string argument by name; null when absent/blank/malformed. Tolerant of malformed JSON.</summary>
        private static string ReadStringArg(string argumentsJson, string name)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.String)
                {
                    var value = f.GetString();
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch (JsonException) { /* malformed args — treat as absent */ }
            return null;
        }

        /// <summary>
        /// Parses the filter arguments (<c>filter</c> / <c>filterMethod</c> / <c>filterClass</c> / <c>filterNamespace</c>)
        /// into a <see cref="FilterSpec"/>. They're mutually exclusive; more than one set returns an error string
        /// (reported to the agent) so an ambiguous filter never silently picks one. Malformed JSON is an error
        /// too — treating it as "no filter" would run the ENTIRE suite while looking like a filtered run.
        /// One parse of the arguments, not one per property.
        /// </summary>
        private static (FilterSpec Spec, string Error) ReadFilterSpec(string argumentsJson)
        {
            string raw = null, method = null, klass = null, ns = null;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        string Read(string name) =>
                            doc.RootElement.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.String
                            && f.GetString() is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
                        raw = Read("filter");
                        method = Read("filterMethod");
                        klass = Read("filterClass");
                        ns = Read("filterNamespace");
                    }
                }
                catch (JsonException) { return (null, "arguments were not valid JSON"); }
            }

            var set = new List<string>();
            if (raw != null) set.Add("filter");
            if (method != null) set.Add("filterMethod");
            if (klass != null) set.Add("filterClass");
            if (ns != null) set.Add("filterNamespace");
            if (set.Count > 1)
                return (null, "specify only one of filter / filterMethod / filterClass / filterNamespace "
                    + "(they're mutually exclusive); got: " + string.Join(", ", set));

            var spec = new FilterSpec();
            if (raw != null) spec.Raw = raw;
            else if (method != null) { spec.Kind = StructuredFilterKind.Method; spec.Value = method; }
            else if (klass != null) { spec.Kind = StructuredFilterKind.Class; spec.Value = klass; }
            else if (ns != null) { spec.Kind = StructuredFilterKind.Namespace; spec.Value = ns; }
            return (spec, null);
        }

        /// <summary>
        /// Best-effort solution <c>.runsettings</c> discovery for the classic (<c>dotnet test</c>) runner:
        /// a single <c>*.runsettings</c> at the solution root is passed via <c>--settings</c>, mirroring
        /// the common VS convention. VS's *manually selected* runsettings (Test &gt; Configure Run Settings)
        /// is per-user window state we can't read headlessly, and a project-level
        /// <c>RunSettingsFilePath</c> property is honored by <c>dotnet test</c> natively — this covers the
        /// convention-file gap in between. MTP hosts don't take runsettings at all (core MTP doesn't
        /// support them). Returns the path plus a human note (applied / ambiguous) for the payload's
        /// notes, both null when there's nothing to do.
        /// </summary>
        private static (string Path, string Note) FindSolutionRunSettings(string workspaceRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
                    return (null, null);
                var files = Directory.GetFiles(workspaceRoot, "*.runsettings");
                if (files.Length == 1)
                    return (files[0], $"applied solution runsettings {Path.GetFileName(files[0])} to classic (VSTest) projects "
                        + "(MTP projects don't take runsettings; if VS has a different runsettings selected manually, results may differ)");
                if (files.Length > 1)
                    return (null, "multiple *.runsettings files at the solution root — none applied; "
                        + "set RunSettingsFilePath in the test project(s) to pick one");
                return (null, null);
            }
            catch { return (null, null); }
        }

        /// <summary>Newest <c>.trx</c> in a directory, or null. Best-effort.</summary>
        private static string NewestTrx(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return null;
                return new DirectoryInfo(directory).GetFiles("*.trx")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            }
            catch { return null; }
        }

        /// <summary>
        /// Runs an incremental VS build (whole solution, no clean) before the command-line test run so the
        /// tests don't execute against stale output. Returns <c>null</c> on success; on a build failure
        /// returns an <see cref="ToolResult"/> carrying the build's own report — the reader build_solution uses,
        /// <see cref="ReadBuildReportAsync"/> (issue #296) — so the agent gets the errors instead of a
        /// stale/misleading test result. Uses VS's own <see cref="SolutionBuild"/> — see the
        /// call site for why that avoids the DLL-locking concern the CLI build would raise.
        /// </summary>
        private async Task<ToolResult> BuildBeforeTestRunAsync(EnvDTE.Solution solution, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var build = solution.SolutionBuild;
            TestRunDebugLog.Write("run_tests: incremental VS build before test run");
            var (started, cancelled) = await RunBuildWitnessedAsync(build, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                build.Build(WaitForBuildToFinish: false);
            }, cancellationToken).ConfigureAwait(false);
            if (!started)
                return new ToolResult(IsError: true, Json.Object(("error",
                    "a build is already in progress in Visual Studio; wait for it to finish (or cancel it) and retry")));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var projectsFailed = build.LastBuildInfo;
            if (projectsFailed == 0)
                return null;

            // Same rule as build_solution: a build the user cancelled is not a failed build, and its Error
            // List rows describe the abort, not the code — reporting them as compiler errors would send the
            // agent to fix a phantom. Name the cause, run no tests, close the retry.
            if (cancelled == true)
            {
                TestRunDebugLog.Write($"run_tests: pre-run build was cancelled in VS ({projectsFailed} project(s) incomplete); skipping tests");
                var cancelledPayload = new
                {
                    error = "the build before the test run was cancelled in Visual Studio (Build → Cancel); "
                        + $"{projectsFailed} project(s) did not complete and no tests were run. Nothing about the code "
                        + "was decided. Do not retry unless the user asks.",
                    cancelled = true, // a real bool — Json.Object would have quoted it
                    projectsFailed,
                };
                return new ToolResult(IsError: true, JsonSerializer.Serialize(cancelledPayload, JsonOptions));
            }

            // Build failed — surface the BUILD's own report rather than running the tests against the last
            // good output. The same reader build_solution uses (issue #296): this path used to read Roslyn's
            // live compiler, which cannot see a build-only diagnostic (CS2001, an MSBuild <Error>, NU/MSB/
            // NETSDK), so every such failure arrived as errors:[] under "fix the build errors". It also
            // brings the output-pane backstop for a failure that leaves no rows at all.
            var report = await ReadBuildReportAsync(projectName: null, succeeded: false, cancellationToken).ConfigureAwait(false);
            var errors = report.Errors;
            TestRunDebugLog.Write($"run_tests: pre-run build failed ({projectsFailed} project(s), {errors.Count} error(s), buildOutput={(report.BuildOutput is null ? "none" : "attached")}); skipping tests");
            // A stale loaded project is one way a build fails — a deleted file the loaded copy still lists
            // gives CS2001 — so the assessment rides the failure too (issue #257), told which files this
            // build compiled exactly as build_solution's is.
            var staleness = await AssessProjectStalenessAsync(solution, report.CompiledFiles, cancellationToken).ConfigureAwait(false);
            var payload = new
            {
                // Chosen by what the payload beside it carries: it may point at 'errors' only when there are
                // some, and names no cause where nothing could be read (TestRunBuildFailure).
                error = TestRunBuildFailure.Message(projectsFailed, errors.Count, report.BuildOutput is not null),
                note = ProjectStaleness.Summary(staleness),
                projectsFailed,
                errorCount = errors.Count,
                errorsByCode = CountByCode(errors),
                errors = errors.Take(MaxErrorsReported).Select(DiagnosticJson),
                truncatedErrors = errors.Count > MaxErrorsReported,
                buildOutput = report.BuildOutput,
                projectStaleness = StalenessJson(staleness),
            };
            return new ToolResult(IsError: true, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Runs every test project via the command line and aggregates the TRX(es) into one result/card:
        /// MTP projects via <c>dotnet run --no-build -- --report-trx</c> (the test host directly), classic
        /// vstest via <c>dotnet test --no-build --logger trx</c>. <c>--no-build</c> reuses VS's build output
        /// (no output-DLL contention; the caller runs an incremental VS build first via
        /// <see cref="BuildBeforeTestRunAsync"/>). Best-effort: an MTP project that writes no TRX (e.g. the
        /// TrxReport extension isn't referenced, so <c>--report-trx</c> errors) is retried once WITHOUT the
        /// flag so its console output is at least surfaced; any other no-TRX project has its output captured
        /// in the notes. Off the UI thread (pure process/IO).
        /// </summary>
        private async Task<ToolResult> RunTestProjectsAsync(
            IReadOnlyList<TestProject> testProjects, FilterSpec filter,
            string configuration, string testResultsRoot, string workspaceRoot,
            (string Path, string Note) runSettings, IReadOnlyList<string> solutionNotes,
            IReadOnlyDictionary<string, string> legacyAssemblies, CancellationToken cancellationToken)
        {
            var data = new TrxData();
            var notes = new List<string>();
            // Informational (doesn't mark the run incomplete): projects the solution couldn't describe (they
            // may or may not have held tests — see EnumerateProjects), then which solution runsettings was
            // applied, or why none was despite candidates existing.
            if (solutionNotes is { Count: > 0 })
                notes.AddRange(solutionNotes);
            if (runSettings.Note is not null)
                notes.Add(runSettings.Note);
            string lastTrx = null;
            var anyTrx = false;
            // Every TRX that parsed, with the project it belongs to: one file per project, and the payload
            // must reach all of them (the last one alone is an arbitrary choice among N).
            var trxFiles = new List<(string Project, string File)>();
            // Set when any project's results couldn't be read into the card (no TRX / unreadable TRX):
            // its tests are missing from the counts, so the run must not report success.
            var incomplete = false;

            foreach (var project in testProjects)
            {
                var (csproj, kind, dialect) = (project.ProjectFile, project.Kind, project.Dialect);
                cancellationToken.ThrowIfCancellationRequested();
                var resultsDir = Path.Combine(testResultsRoot, Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(resultsDir); } catch { }

                var name = Path.GetFileNameWithoutExtension(csproj);
                // Translate the filter per project's framework (raw filters pass through verbatim); the
                // structured-filter preflight in RunTestsAsync has already ruled out untranslatable frameworks.
                var fragment = TestRunCommands.BuildFilterFragment(filter, dialect);

                int exit;
                string stdout, stderr, args;
                if (kind == TestProjectKind.LegacyVsTest)
                {
                    // Two things must be true before this project can run, and neither is a failure of the
                    // run itself: we must know where its output assembly is (DTE), and that assembly must
                    // exist (the VS build above should have produced it). A project failing either is
                    // REPORTED and marks the run incomplete - never dropped, which would leave its absence
                    // looking like a solution that simply has fewer tests than it does.
                    var (assembly, why) = ResolveLegacyAssemblyToRun(csproj, legacyAssemblies);
                    if (assembly is null)
                    {
                        notes.Add($"{name}: its tests are NOT included in the counts - {why}");
                        incomplete = true;
                        continue;
                    }

                    var vstest = FindVsTestConsole();
                    if (vstest is null)
                    {
                        notes.Add($"{name}: its tests are NOT included in the counts - could not find "
                            + "vstest.console.exe in this Visual Studio installation "
                            + $"(expected under {VsTestConsoleRelativePath}), which is the only runner that "
                            + "can execute an old-style .NET Framework test project.");
                        incomplete = true;
                        continue;
                    }

                    args = TestRunCommands.BuildLegacyVsTestArguments(
                        assembly, resultsDir, TestRunCommands.BuildLegacyFilterExpression(filter), runSettings.Path);
                    MirrorHeader($"run_tests: {name}");
                    (exit, stdout, stderr) = await RunProcessAsync(
                        vstest, args, Path.GetDirectoryName(csproj), environment: null,
                        cancellationToken, LiveCommandLine).ConfigureAwait(false);
                }
                else
                {
                    args = TestRunCommands.BuildTestCommand(kind, csproj, configuration, resultsDir, fragment, runSettings.Path);
                    MirrorHeader($"run_tests: {name}");
                    (exit, stdout, stderr) = await RunDotnetAsync(args, Path.GetDirectoryName(csproj), cancellationToken, LiveCommandLine).ConfigureAwait(false);
                }
                // Enabled-guarded: this message interpolates the run's ENTIRE console output — without the
                // guard every run would build (and throw away) a potentially multi-megabyte string.
                if (TestRunDebugLog.Enabled)
                    TestRunDebugLog.Write($"{kind} run [{name}] exit={exit}\n  args: dotnet {args}\n  --- stdout ---\n{stdout}\n  --- stderr ---\n{stderr}\n  --- end ---");

                var trx = NewestTrx(resultsDir);
                if (trx is not null)
                {
                    try { AccumulateTrx(data, trx, workspaceRoot, name); anyTrx = true; lastTrx = trx; trxFiles.Add((name, trx)); continue; }
                    catch (Exception ex)
                    {
                        notes.Add($"{name}: its tests are NOT included in the counts — could not read its results file ({ex.Message})");
                        incomplete = true;
                        continue;
                    }
                }

                incomplete = true;
                if (kind == TestProjectKind.Mtp && exit != TimedOutExitCode && exit != StartFailedExitCode)
                {
                    // No TRX — likely the TrxReport extension isn't referenced (so --report-trx errored). Re-run
                    // without the flag so the tests actually run, and surface their console output + pass/fail.
                    notes.Add($"{name}: its tests are NOT included in the counts (no TRX produced) — reference "
                        + "Microsoft.Testing.Extensions.TrxReport for full results.");
                    var plainArgs = $"run --project \"{csproj}\" --no-build -c \"{configuration}\""
                        + (string.IsNullOrEmpty(fragment) ? "" : " -- " + fragment);
                    MirrorHeader($"run_tests: {name} (no-TRX rerun)");
                    var (exit2, out2, err2) = await RunDotnetAsync(plainArgs, Path.GetDirectoryName(csproj), cancellationToken, LiveCommandLine).ConfigureAwait(false);
                    TestRunDebugLog.Write($"mtp run (no-trx fallback) [{name}] exit={exit2}");
                    if (exit2 != 0)
                        notes.Add($"{name}: the run FAILED (exit code {exit2}).");
                    notes.Add(Truncate($"{name} output:\n{out2}{(string.IsNullOrWhiteSpace(err2) ? "" : "\n" + err2)}", 4000));
                }
                else
                {
                    // Classic: dotnet test writes a TRX whenever tests are discovered; none usually means a run
                    // error (no tests discovered, or the args/build failed). Also the MTP timed-out/never-started
                    // case — re-running those without --report-trx would just hang or fail again. Surface the
                    // captured output so it's not lost.
                    notes.Add(Truncate($"{name}: no results file produced. Output:\n{stdout}{(string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr)}", 4000));
                }
            }

            // A filter that ran but matched zero tests is the classic exact-vs-prefix / wildcard confusion
            // — especially on xUnit v3, where filterNamespace/filterClass match EXACTLY (no sub-namespaces /
            // nested types) unlike VSTest's contains-match. Tell the agent how to widen it in the result,
            // rather than leaving a bare "0 tests" card it has to guess about (report-back + escape-hatch).
            if (anyTrx && data.Total == 0 && !filter.IsEmpty)
                notes.Add(TestRunCommands.ZeroMatchFilterHint(filter, testProjects));

            // Per-project problems ride in the payload's notes (and force succeeded:false via incomplete),
            // so a failing/unreadable project is never silently dropped just because another project's TRX
            // parsed fine.
            if (anyTrx)
                return BuildTestRunResult(data, lastTrx, notes, incomplete, trxFiles);

            // Nothing produced a TRX — return the collected output + guidance (non-card), so results aren't lost.
            return new ToolResult(IsError: true, Json.Object(
                ("error", "the test run produced no results file"),
                ("details", Truncate(string.Join("\n\n", notes), 8000))));
        }


        /// <summary>Where vstest.console.exe sits relative to <c>Common7\IDE</c> in a VS install.</summary>
        private const string VsTestConsoleRelativePath = @"CommonExtensions\Microsoft\TestWindow\vstest.console.exe";

        /// <summary>
        /// <c>vstest.console.exe</c> from the Visual Studio we are running inside, or null.
        /// </summary>
        /// <remarks>
        /// Located the way <see cref="VsDevEnvironment"/> locates VsDevCmd.bat: devenv.exe lives in
        /// <c>Common7\IDE</c>, with <c>VSAPPIDDIR</c> (set inside devenv) as the fallback for hosts whose
        /// MainModule is not devenv. <b>Deliberately no vswhere fallback</b> - we are running inside the
        /// install we want, and vswhere answers about every install on the machine, so it could hand back
        /// a different one. A wrong-but-plausible vstest.console is worse than none: none is reported, and
        /// the other silently runs the tests under another version of the platform.
        /// </remarks>
        private static string FindVsTestConsole()
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    var candidate = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(exe), VsTestConsoleRelativePath));
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                // MainModule throws in some host contexts; the env-var fallback below still applies.
            }

            var appIdDir = Environment.GetEnvironmentVariable("VSAPPIDDIR");
            if (!string.IsNullOrEmpty(appIdDir))
            {
                var candidate = Path.GetFullPath(Path.Combine(appIdDir, VsTestConsoleRelativePath));
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// The output assembly to hand vstest.console for one legacy project, or null plus the sentence
        /// explaining why there is none.
        /// </summary>
        /// <remarks>
        /// Both failures are reported rather than guessed at. There is deliberately <b>no csproj-XML
        /// fallback</b> for the path: <c>OutputPath</c> in an old-style project is almost always inside a
        /// <c>Condition</c>, and <see cref="TestProjects.ReadUnconditionalProperty"/> refuses conditioned
        /// definitions precisely because it cannot evaluate them - so a fallback would not be a worse
        /// answer, it would be a confidently wrong path, and running the wrong assembly reports normally.
        /// DTE or a note; nothing in between.
        /// </remarks>
        private static (string Assembly, string Why) ResolveLegacyAssemblyToRun(
            string projectFile, IReadOnlyDictionary<string, string> legacyAssemblies)
        {
            if (legacyAssemblies is null
                || !legacyAssemblies.TryGetValue(projectFile, out var assembly)
                || string.IsNullOrEmpty(assembly))
            {
                return (null, "Visual Studio could not report its build output path, so there is no "
                    + "assembly to run. Reload the project in Solution Explorer and try again.");
            }
            if (!File.Exists(assembly))
            {
                return (null, $"its build output was not found at {assembly}. The solution built, so this "
                    + "usually means the project is not part of the active solution configuration.");
            }
            return (assembly, null);
        }

        /// <summary>
        /// Full paths to the built output assembly of every legacy test project, keyed by project file.
        /// Returns empty without touching the UI thread when the solution holds no legacy project.
        /// </summary>
        /// <remarks>
        /// <c>OutputPath</c> comes from the ACTIVE configuration (it differs per configuration and is what
        /// the build above just produced) while <c>OutputFileName</c> is a project-level property. Both are
        /// read defensively: DTE throws readily on a project it cannot fully describe, and a project we
        /// cannot resolve is reported by the caller rather than skipped.
        /// </remarks>
        private async Task<IReadOnlyDictionary<string, string>> ResolveLegacyOutputAssembliesAsync(
            IReadOnlyList<TestProject> testProjects, CancellationToken cancellationToken)
        {
            var wanted = new HashSet<string>(
                testProjects.Where(t => t.Kind == TestProjectKind.LegacyVsTest).Select(t => t.ProjectFile),
                StringComparer.OrdinalIgnoreCase);
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
                return resolved;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var solution = _dte.Solution;
            if (solution is null)
                return resolved;

            foreach (var project in EnumerateProjects(solution))
            {
                var path = TryGetProjectFullName(project, null);
                if (string.IsNullOrEmpty(path) || !wanted.Contains(path))
                    continue;
                var assembly = TryGetOutputAssemblyPath(project, path);
                if (!string.IsNullOrEmpty(assembly))
                    resolved[path] = assembly;
            }
            return resolved;
        }

        /// <summary>The project's built output assembly per DTE, or null when DTE won't give up either half.</summary>
        private static string TryGetOutputAssemblyPath(EnvDTE.Project project, string projectFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var outputPath = project.ConfigurationManager?.ActiveConfiguration?
                    .Properties?.Item("OutputPath")?.Value as string;
                var outputFileName = project.Properties?.Item("OutputFileName")?.Value as string;
                if (string.IsNullOrEmpty(outputPath) || string.IsNullOrEmpty(outputFileName))
                    return null;

                // OutputPath is relative to the project directory ("bin\Debug\"), which is why the project
                // file's own directory is the base rather than the solution's.
                var projectDir = Path.GetDirectoryName(projectFile);
                if (string.IsNullOrEmpty(projectDir))
                    return null;
                return Path.GetFullPath(Path.Combine(projectDir, outputPath, outputFileName));
            }
            catch
            {
                // Any of those hops can throw on a project DTE cannot fully describe. The caller reports
                // the project rather than dropping it.
                return null;
            }
        }

        /// <summary>Synthetic exit code: the run exceeded <see cref="TestRunTimeout"/> and was tree-killed.</summary>
        private const int TimedOutExitCode = -2;
        /// <summary>Synthetic exit code: the dotnet process could not be started at all.</summary>
        private const int StartFailedExitCode = -1;

        /// <summary>
        /// Runs <c>dotnet &lt;arguments&gt;</c>, capturing stdout/stderr and the exit code. net472-safe wait
        /// (TaskCompletionSource on Exited, not WaitForExitAsync). Bounded by <see cref="TestRunTimeout"/>
        /// per invocation — a hung test would otherwise wedge the tool call indefinitely (the old Test
        /// Explorer path had the same guard); on timeout or cancellation the WHOLE process tree is killed
        /// (<see cref="KillProcessTree"/>), not just the direct <c>dotnet</c> process, because the real test
        /// host is a child that would otherwise linger holding the build output DLLs — failing the next
        /// run's pre-test VS build with file locks.
        /// </summary>
        /// <summary>
        /// The <c>run_command</c> tool: runs a command line via cmd.exe inside the captured VS
        /// developer environment (<see cref="VsDevEnvironment"/>). Opt-in (see the constructor);
        /// risk-tiered Command, so every mode below AcceptAll prompts. A nonzero exit is a
        /// <em>result</em> (the agent needs the output either way) — <c>IsError</c> is reserved for
        /// start-failure, timeout, and environment/argument problems.
        /// </summary>
        private async Task<ToolResult> RunCommandAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            string command = null, workingDirectory = null;
            var timeoutSeconds = 300;
            try
            {
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    using var doc = JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
                            command = c.GetString();
                        if (doc.RootElement.TryGetProperty("workingDirectory", out var w) && w.ValueKind == JsonValueKind.String)
                            workingDirectory = w.GetString();
                        if (doc.RootElement.TryGetProperty("timeoutSeconds", out var t) && t.ValueKind == JsonValueKind.Number)
                            timeoutSeconds = t.GetInt32();
                    }
                }
            }
            catch (JsonException) { return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON"))); }

            if (string.IsNullOrWhiteSpace(command))
                return new ToolResult(IsError: true, Json.Object(("error", "'command' is required")));
            timeoutSeconds = Math.Max(1, Math.Min(timeoutSeconds, 900));

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                // Default to the solution directory (the agent's workspace); the stable default
                // workspace when nothing is open.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                var solutionPath = _dte.Solution?.FullName;
                workingDirectory = string.IsNullOrEmpty(solutionPath)
                    ? VsIdeServices.DefaultWorkspaceRoot
                    : Path.GetDirectoryName(solutionPath);
            }
            else if (!Directory.Exists(workingDirectory))
            {
                return new ToolResult(IsError: true, Json.Object(("error", $"workingDirectory does not exist: {workingDirectory}")));
            }

            // Off the UI thread for the env capture + the run itself.
            return await Task.Run(() => RunCommandCoreAsync(command, workingDirectory, timeoutSeconds, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<ToolResult> RunCommandCoreAsync(
            string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var (devEnv, envError) = await VsDevEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
            if (devEnv is null)
                return new ToolResult(IsError: true, Json.Object(
                    ("error", $"could not establish the VS developer environment: {envError}")));

            // The captured block replaces the inherited env wholesale; layer the non-interactive
            // guards on top (mirrors the engine's IAcpConnection.NonInteractiveEnvDefaults — that
            // type lives engine-side, so the values are duplicated here) unless already set.
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in devEnv)
                env[kv.Key] = kv.Value;
            foreach (var kv in new Dictionary<string, string>
            {
                ["GIT_EDITOR"] = "true",
                ["GIT_SEQUENCE_EDITOR"] = "true",
                ["GIT_PAGER"] = "cat",
                ["PAGER"] = "cat",
                ["GIT_TERMINAL_PROMPT"] = "0",
            })
            {
                if (!env.ContainsKey(kv.Key))
                    env[kv.Key] = kv.Value;
            }

            MirrorHeader("run_command: " + command);
            var notes = new List<string>();
            var commandLine = $"cmd.exe /d /s /c \"{command}\"";

            int exit;
            string stdout, stderr;
            // Interactive-terminal path first (Phase 4): real TTY semantics + the user can answer a
            // prompting command from the pane. Null result (no factory / pty creation failed) falls
            // back to redirected pipes — same tool contract either way.
            var pty = await TryRunViaPtyAsync(commandLine, workingDirectory, env, timeoutSeconds, notes, cancellationToken)
                .ConfigureAwait(false);
            if (pty is { } ptyRun)
            {
                exit = ptyRun.ExitCode;
                stdout = ptyRun.Output;
                stderr = string.Empty;
                notes.Add("ran on an interactive terminal: output is the merged console stream (stderr included), ANSI sequences removed");
            }
            else
            {
                (exit, stdout, stderr) = await RunProcessAsync(
                    "cmd.exe", $"/d /s /c \"{command}\"", workingDirectory, env,
                    cancellationToken, LiveCommandLine, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
            }

            if (exit == TimedOutExitCode)
                notes.Add($"the command did not finish within {timeoutSeconds}s and its process tree was killed");
            else if (exit == StartFailedExitCode)
                notes.Add("the command's process could not be started");

            const int OutputCap = 16 * 1024;
            string CapText(string text, string name)
            {
                if (string.IsNullOrEmpty(text) || text.Length <= OutputCap)
                    return text ?? string.Empty;
                notes.Add($"{name} truncated ({text.Length} chars total)");
                return text.Substring(0, OutputCap) + "\n[… truncated …]";
            }

            var payload = JsonSerializer.Serialize(new
            {
                succeeded = exit == 0,
                exitCode = exit,
                command,
                workingDirectory,
                stdout = CapText(stdout, "stdout"),
                stderr = CapText(stderr, "stderr"),
                notes,
            });
            return new ToolResult(IsError: exit == TimedOutExitCode || exit == StartFailedExitCode, payload);
        }

        /// <summary>
        /// Runs the command on a host-provided pseudo-terminal. Null = not attempted/possible
        /// (caller falls back to pipes). Output accumulates bounded (256K head) and tees raw to
        /// the pane; the returned text is ANSI-stripped for the agent payload. The session is
        /// published as <see cref="_activeCommandPty"/> for the duration so pane keystrokes and
        /// resizes reach it.
        /// </summary>
        private async Task<(int ExitCode, string Output)?> TryRunViaPtyAsync(
            string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> env,
            int timeoutSeconds, List<string> notes, CancellationToken cancellationToken)
        {
            var factory = CommandPtyFactory;
            if (factory is null)
                return null;

            const int CaptureCap = 256 * 1024;
            var captured = new StringBuilder();
            var rawSink = LiveCommandRawOutput;
            void OnOutput(string chunk)
            {
                lock (captured)
                {
                    if (captured.Length < CaptureCap)
                        captured.Append(chunk, 0, Math.Min(chunk.Length, CaptureCap - captured.Length));
                }
                try { rawSink?.Invoke(chunk); } catch { }
            }

            ICommandPty session;
            try { session = factory(commandLine, workingDirectory, env, _terminalColumns, _terminalRows, OnOutput); }
            catch { session = null; }
            if (session is null)
                return null;

            Interlocked.Exchange(ref _activePtyInputBytes, 0);
            _activeCommandPty = session;
            try
            {
                using (cancellationToken.Register(session.Kill))
                {
                    var exitTask = session.WaitForExitAsync(cancellationToken);
                    var completed = await Task.WhenAny(
                        exitTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)).ConfigureAwait(false);
                    int exit;
                    if (completed != exitTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        session.Kill();
                        exit = TimedOutExitCode;
                    }
                    else
                    {
                        exit = await exitTask.ConfigureAwait(false);
                    }

                    // Closing the pty (Dispose) is what EOFs its reader; give the tail a moment to
                    // flush into the capture before snapshotting.
                    session.Dispose();
                    await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);

                    // Ground truth against confabulation: on the first live run the agent saw a
                    // prompt answered in the output and invented "ran non-interactively,
                    // auto-answered" — when the USER had typed the answer into the pane. State it.
                    var inputBytes = Interlocked.Exchange(ref _activePtyInputBytes, 0);
                    if (inputBytes > 0)
                        notes.Add("the user typed input into the IDE terminal during this command "
                            + "(interactive prompts in the output were answered by the user, not auto-answered)");

                    string output;
                    lock (captured)
                        output = captured.ToString();
                    return (exit, AnsiText.Strip(output));
                }
            }
            finally
            {
                _activeCommandPty = null;
                try { session.Dispose(); } catch { }
            }
        }

        private static Task<(int ExitCode, string StdOut, string StdErr)> RunDotnetAsync(
            string arguments, string workingDirectory, CancellationToken cancellationToken,
            Action<string> liveLine = null)
            => RunProcessAsync("dotnet", arguments, workingDirectory, environment: null,
                cancellationToken, liveLine);

        /// <summary>
        /// The general spawned-command core (run_tests' dotnet invocations, run_command's dev-env
        /// commands): captures stdout/stderr + exit code, streams lines to the optional live sink,
        /// bounded by <paramref name="timeout"/> (default <see cref="TestRunTimeout"/>) with the
        /// whole process tree killed on timeout/cancel. <paramref name="environment"/> non-null
        /// REPLACES the inherited environment (the VsDevCmd block is authoritative and already
        /// contains the parent's variables).
        /// </summary>
        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken,
            Action<string> liveLine = null, TimeSpan? timeout = null)
        {
            var bound = timeout ?? TestRunTimeout;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (environment is not null)
            {
                psi.EnvironmentVariables.Clear();
                foreach (var kv in environment)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            // The captured output only surfaces truncated (notes cap at 4000 chars) unless the debug log
            // is on, so keep a bounded head rather than pinning a verbose suite's entire console output
            // (potentially megabytes) inside devenv for the duration of the run.
            var captureAll = TestRunDebugLog.Enabled;
            const int MaxCapturedOutputChars = 256 * 1024;
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The live sink (terminal mirror) is guarded per line: these are threadpool callbacks in
            // devenv, where an unhandled exception kills the process (same reasoning as Exited below).
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (captureAll || stdout.Length < MaxCapturedOutputChars) stdout.AppendLine(e.Data);
                try { liveLine?.Invoke(e.Data); } catch { }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (captureAll || stderr.Length < MaxCapturedOutputChars) stderr.AppendLine(e.Data);
                try { liveLine?.Invoke(e.Data); } catch { }
            };
            // Guarded: on the cancellation path the awaiter has already resumed and disposed the Process
            // when the killed child's Exited finally fires — reading ExitCode then throws, and an unhandled
            // exception in this threadpool callback would take down the host process (devenv on net472).
            process.Exited += (_, _) => { try { exited.TrySetResult(process.ExitCode); } catch { exited.TrySetCanceled(); } };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                var hint = fileName == "dotnet" ? " (is the .NET SDK on PATH?)" : string.Empty;
                return (StartFailedExitCode, string.Empty, $"could not start {fileName}: {ex.Message}{hint}");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (cancellationToken.Register(() =>
            {
                KillProcessTree(process);
                exited.TrySetCanceled();
            }))
            {
                var completed = await Task.WhenAny(exited.Task, Task.Delay(bound, cancellationToken)).ConfigureAwait(false);
                if (completed != exited.Task)
                {
                    // The delay won — either cancelled (throw as cancellation) or a genuine timeout.
                    cancellationToken.ThrowIfCancellationRequested();
                    KillProcessTree(process);
                    TestRunDebugLog.Write($"{fileName} {arguments}: did not finish within {bound.TotalMinutes:0.#} min — killed the process tree");
                    return (TimedOutExitCode, stdout.ToString(), stderr.ToString()
                        + $"\n[{fileName}] the process did not finish within {bound.TotalMinutes:0.#} minutes and was killed");
                }
                var exitCode = await exited.Task.ConfigureAwait(false);
                try { process.WaitForExit(); } catch { } // flush the async stdout/stderr readers
                return (exitCode, stdout.ToString(), stderr.ToString());
            }
        }

        /// <summary>
        /// Best-effort kill of a process AND its children via <c>taskkill /T /F</c> (net472 has no
        /// <c>Kill(entireProcessTree)</c>). <c>dotnet run</c>/<c>dotnet test</c> spawn the real test host
        /// as a child; killing only the parent leaves an orphaned host running with the build output
        /// loaded. Falls back to a plain <see cref="System.Diagnostics.Process.Kill()"/>.
        /// </summary>
        private static void KillProcessTree(System.Diagnostics.Process process)
        {
            try
            {
                if (process.HasExited)
                    return;
                using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {process.Id} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                killer?.WaitForExit(5000);
            }
            catch { /* best-effort — fall through to the plain kill */ }
            try { if (!process.HasExited) process.Kill(); } catch { }
        }

        // The failure/test source locations for a run_tests result come out of the stack trace. The
        // parsing lives in Core (TestStackFrames) so it can be pinned by unit tests against real captured
        // traces — this project is net472 + VS-bound and can't be referenced from the test project.

        /// <summary>
        /// The diagnostic collection engine shared by both tools. Layers the sources per scope and de-dupes
        /// by (file, line, code) with precedence compiler &gt; analyzer &gt; errorList (achieved by add order —
        /// first writer of a key wins), so a rule found both via Roslyn and the Error List is labeled by its
        /// deterministic source. Compiler+analyzer diagnostics come from Roslyn off the UI thread; the Error
        /// List (Full scope) is read on the UI thread. <paramref name="minSeverity"/> is the severity floor
        /// (default Warning = the classic Error+Warning behavior; Info/Hidden only on request);
        /// <paramref name="fileFilter"/> restricts every source to one file. Ordered most-severe first.
        /// </summary>
        private async Task<List<ToolDiagnostic>> CollectDiagnosticsAsync(DiagnosticScope scope, DiagnosticSeverity minSeverity, CancellationToken cancellationToken, string projectName = null, string fileFilter = null)
        {
            // Get OFF the UI thread before any Roslyn work (issue #39). build_solution/run_tests call
            // this right after their main-thread build wait, and an already-cached GetCompilationAsync
            // completes synchronously — ConfigureAwait(false) does not unstick a continuation that never
            // yields, so whole-solution GetDiagnostics (full method-body binding) ran ON the UI thread
            // and froze VS. An unconditional hop makes the entry thread irrelevant.
            await TaskScheduler.Default;

            var result = new List<ToolDiagnostic>();
            var deduper = new DiagnosticDeduper();

            if (Workspace is not null)
            {
                var solution = Workspace.CurrentSolution;
                await AddCompilerDiagnosticsAsync(solution, minSeverity, fileFilter, result, deduper, cancellationToken, projectName).ConfigureAwait(false);
                if (scope != DiagnosticScope.Compiler)
                    await AddAnalyzerDiagnosticsAsync(solution, minSeverity, fileFilter, result, deduper, cancellationToken).ConfigureAwait(false);
                if (scope == DiagnosticScope.Full)
                    await AddErrorListDiagnosticsAsync(result, deduper, excludeCompilerCodes: true, minSeverity, fileFilter, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // No Roslyn workspace (e.g. no C#/VB loaded) — fall back to the Error List for everything.
                await AddErrorListDiagnosticsAsync(result, deduper, excludeCompilerCodes: false, minSeverity, fileFilter, cancellationToken).ConfigureAwait(false);
            }

            return result
                .OrderByDescending(d => (int)d.Severity)
                .ToList();
        }

        /// <summary>Roslyn compiler diagnostics (CS####/BC####) for every project (or just <paramref name="projectName"/> when given) — synchronous, reflects the current buffers.</summary>
        private static async Task AddCompilerDiagnosticsAsync(CodeSolution solution, DiagnosticSeverity minSeverity, string fileFilter, List<ToolDiagnostic> result, DiagnosticDeduper deduper, CancellationToken cancellationToken, string projectName = null)
        {
            foreach (var project in solution.Projects)
            {
                if (projectName is not null && !MatchesRoslynProjectName(project.Name, projectName))
                    continue;
                if (fileFilter is not null && !ProjectContainsFile(project, fileFilter))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                Microsoft.CodeAnalysis.Compilation compilation;
                try { compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { continue; }
                if (compilation is null)
                    continue;

                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                    AddRoslynDiagnostic(diagnostic, "compiler", minSeverity, fileFilter, result, deduper);
            }
        }

        /// <summary>
        /// Roslyn analyzer diagnostics from each project's referenced analyzers (CA/StyleCop/package Sonar…).
        /// With a <paramref name="fileFilter"/> the analysis is document-scoped (semantic + syntax passes on
        /// just the matching documents) — and only then, when the severity floor also drops below Warning,
        /// do the VS-hosted IDE style analyzers join in (their output is hidden/info-tier, so a Warning floor
        /// would filter it all anyway, and solution-wide they are too slow and too noisy — visible IDE####
        /// entries for open files already surface via the Error List in Full scope). Best-effort per project.
        /// </summary>
        private static async Task AddAnalyzerDiagnosticsAsync(CodeSolution solution, DiagnosticSeverity minSeverity, string fileFilter, List<ToolDiagnostic> result, DiagnosticDeduper deduper, CancellationToken cancellationToken)
        {
            var includeIdeAnalyzers = fileFilter is not null && minSeverity < DiagnosticSeverity.Warning;

            // Bounded concurrency (issue #39): Roslyn's own switch is all-or-nothing —
            // CompilationWithAnalyzersOptions.ConcurrentAnalysis has no degree-of-parallelism, it fans
            // the analyzer executor across every core, and inside devenv that saturates the threadpool
            // VS's UI-thread work constantly joins on (JTF), so a solution-wide pass read as a VS-wide
            // hang. So the controllable knob lives one level up: each project's executor runs
            // sequentially, and up to AnalyzerProjectParallelism projects analyze concurrently, capping
            // total CPU at roughly that many cores with the rest left for the IDE. Results merge in
            // solution order so the DiagnosticDeduper precedence stays deterministic.
            var throttle = new SemaphoreSlim(AnalyzerProjectParallelism);
            var perProject = solution.Projects
                .Select(async project =>
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return await CollectProjectAnalyzerDiagnosticsAsync(project, fileFilter, includeIdeAnalyzers, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                })
                .ToList();

            foreach (var projectTask in perProject)
                foreach (var diagnostic in await projectTask.ConfigureAwait(false))
                    AddRoslynDiagnostic(diagnostic, "analyzer", minSeverity, fileFilter, result, deduper);
        }

        /// <summary>
        /// One project's raw analyzer diagnostics (the caller filters and dedupes on merge). Best-effort:
        /// analyzers that can't run headlessly yield an empty result rather than sinking the whole call.
        /// </summary>
        private static async Task<List<Diagnostic>> CollectProjectAnalyzerDiagnosticsAsync(Microsoft.CodeAnalysis.Project project, string fileFilter, bool includeIdeAnalyzers, CancellationToken cancellationToken)
        {
            var collected = new List<Diagnostic>();

            var documents = fileFilter is null
                ? null
                : project.Documents.Where(d => MatchesFileFilter(d.FilePath, fileFilter)).ToList();
            if (documents is { Count: 0 })
                return collected; // the requested file is not in this project

            var analyzers = includeIdeAnalyzers
                ? CollectFixableAnalyzers(project)
                : project.AnalyzerReferences.SelectMany(r => SafeGetAnalyzers(r, project.Language)).ToImmutableArray();
            if (analyzers.IsEmpty)
                return collected;

            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                    return collected;
                var withAnalyzers = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
                    project.AnalyzerOptions, onAnalyzerException: null,
                    concurrentAnalysis: false, logAnalyzerExecutionTime: false));
                if (documents is null)
                {
                    collected.AddRange(await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    foreach (var document in documents)
                    {
                        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                        if (model is null)
                            continue;
                        var semantic = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken).ConfigureAwait(false);
                        var syntax = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancellationToken).ConfigureAwait(false);
                        collected.AddRange(semantic.Concat(syntax));
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Some analyzers can't run headlessly; their absence shouldn't sink the whole call.
            }

            return collected;
        }

        /// <summary>Whether the project holds a document matching the file filter.</summary>
        private static bool ProjectContainsFile(Microsoft.CodeAnalysis.Project project, string fileFilter)
            => project.Documents.Any(d => MatchesFileFilter(d.FilePath, fileFilter));

        /// <summary>
        /// Matches a diagnostic/document path against the caller's 'file' argument: full-path equality, or —
        /// for a non-rooted filter — a path-suffix match on a separator boundary (so "Services/Foo.cs"
        /// matches, but "Foo.cs" never matches "MyFoo.cs"). Case-insensitive, slash-agnostic.
        /// </summary>
        /// <summary>
        /// Warns that a file filter matched no document in the solution, so an empty result says nothing
        /// about the file. Null unless the result really is empty and the filter really matched nothing.
        /// </summary>
        /// <remarks>
        /// "Clean" and "never analysed" are the same empty result, and the second is the one that causes a
        /// wrong action: the commonest use of a filtered <c>get_diagnostics</c> is confirming a fix landed,
        /// so a silent nothing reads as success. A typo is the obvious cause and an agent will often catch
        /// that one from context — measured, it corrected <c>Progarm.cs</c> unprompted. The cause it cannot
        /// catch is a path that is correct in every respect except membership: a file excluded from its
        /// project, one in a project that failed to load, or one not part of the build at all. It exists,
        /// it spells right, it resolves on disk, and it was never looked at.
        /// <para>
        /// Only computed when the result is EMPTY, which is the only case that can mislead and also makes
        /// the document walk free in the common path. That ordering also disposes of the <c>full</c>-scope
        /// wrinkle for nothing: Error List rows for a non-member file are still results, so a match there
        /// leaves the result non-empty and this never runs to contradict it.
        /// </para>
        /// Silent when there is no workspace to ask — that is "cannot tell", and asserting from it would be
        /// the same mistake as the messages that named causes they could not observe.
        /// </remarks>
        private string UnmatchedFilterNote(string fileFilter, List<ToolDiagnostic> diagnostics)
        {
            if (string.IsNullOrEmpty(fileFilter) || diagnostics.Count > 0)
                return null;

            var solution = Workspace?.CurrentSolution;
            if (solution is null)
                return null;

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (MatchesFileFilter(document.FilePath, fileFilter))
                        return null; // the file is in the solution and genuinely has nothing to report
                }
            }

            return $"The filter '{fileFilter}' matched no file in the solution, so nothing was analysed — this " +
                   "result does NOT mean the file is clean. Check the path, or whether the file belongs to a " +
                   "project that is loaded and part of the build.";
        }

        /// <summary>
        /// Names the files a RELATIVE file filter matched when it matched more than one, or null when the
        /// result is about a single file (or the filter was absolute, which can only name one).
        /// </summary>
        /// <remarks>
        /// <see cref="MatchesFileFilter"/> matches a relative filter by path suffix, so a bare leaf name
        /// silently answers about every file that ends with it and the caller cannot tell from the shape
        /// of the result. Measured live 2026-08-04: <c>file: "Program.cs"</c> returned 303 diagnostics
        /// spanning CodeWicket.Engine and CodeWicket.Console, of which 46 belonged to the one the
        /// caller meant. The agent recovered only by reading the per-entry <c>file</c> fields and
        /// inferring the ambiguity for itself.
        /// <para>
        /// A note rather than an error, for the same reason as the semantic-scope note: the merged result
        /// is TRUE, it just answers a broader question than the one that looks like it was asked. It also
        /// settles an inconsistency — apply_code_fix REFUSES an ambiguous relative path (a wrong edit is
        /// unrecoverable) while this reports and continues (a wider read is not). Same argument, opposite
        /// policies, deliberately, and now both say so.
        /// </para>
        /// Computed from the results rather than by instrumenting the filter, so it covers every source
        /// that feeds them (Roslyn documents and Error List rows alike) with no extra passes.
        /// </remarks>
        private static string AmbiguousFilterNote(string fileFilter, List<ToolDiagnostic> diagnostics)
        {
            if (string.IsNullOrEmpty(fileFilter) || Path.IsPathRooted(fileFilter.Replace('/', Path.DirectorySeparatorChar)))
                return null;

            var files = diagnostics
                .Select(d => d.File)
                .Where(f => !string.IsNullOrEmpty(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count < 2)
                return null;

            const int MaxNamed = 8;
            var named = string.Join(", ", files.Take(MaxNamed));
            if (files.Count > MaxNamed)
                named += $", … (+{files.Count - MaxNamed} more)";

            return $"The filter '{fileFilter}' is a relative path and matched {files.Count} files, whose diagnostics are " +
                   $"COMBINED above rather than scoped to one: {named}. The counts and totals cover all of them — " +
                   "re-issue with a longer path fragment, or an absolute path, to scope this to a single file.";
        }

        /// <summary>
        /// Whether a path passes the caller's file filter. A null filter admits everything (no filter);
        /// otherwise the shared rule decides, so this and apply_code_fix's document lookup cannot drift
        /// apart — them disagreeing is the bug that produced <see cref="Core.Ide.FilePathMatch"/>.
        /// </summary>
        private static bool MatchesFileFilter(string path, string fileFilter)
            => fileFilter is null || Core.Ide.FilePathMatch.Matches(path, fileFilter);

        /// <summary>The live VS Error List (Full scope): non-compiler entries (SonarLint/IDE/CA…). CS/BC are dropped — Roslyn owns those. An Info floor also admits the "Messages" tier (IDE suggestions on open files); the Error List never holds Hidden.</summary>
        private async Task AddErrorListDiagnosticsAsync(List<ToolDiagnostic> result, DiagnosticDeduper deduper, bool excludeCompilerCodes, DiagnosticSeverity minSeverity, string fileFilter, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            foreach (var entry in ReadErrorListTable())
            {
                if (entry.Severity < minSeverity)
                    continue;
                if (!MatchesFileFilter(entry.File, fileFilter))
                    continue;
                if (excludeCompilerCodes && IsCompilerCode(entry.Code))
                    continue; // Roslyn is the authority for compiler diagnostics (the Error List copy can be stale).
                entry.Source = "errorList";
                if (deduper.TryAddErrorList(entry.File, entry.Line, entry.Column, entry.Code))
                    result.Add(entry);
            }

            // Leave on the threadpool: this method completes on the UI thread, and the caller's
            // ConfigureAwait(false) continuations (sort, JSON payload, the RPC response write) would
            // otherwise inline right here on it.
            await TaskScheduler.Default;
        }

        /// <summary>
        /// Build-source diagnostics for <c>build_solution</c>: the errors/warnings the *real* MSBuild build
        /// just produced (<see cref="ErrorSource.Build"/> rows), which the design-time Roslyn compilation
        /// can't see — generated-code CS#### (WCF/T4 ambiguities like the CS0104 that motivated this),
        /// build-only NU/MSB/NETSDK codes, and analyzers that run at build. Scoped to
        /// <paramref name="projectName"/> when a single project was built so a scoped result doesn't drag in
        /// another project's stale rows. UI thread only. Safe here precisely because build_solution just ran a
        /// build, so the rows are fresh; get_diagnostics (which does not build) deliberately does NOT use it.
        ///
        /// NO field-based de-dup: the Error List row set IS the ground truth we're matching, and two distinct
        /// build diagnostics can be identical in every field the table exposes to us. A live instance hit exactly that —
        /// two CS8602 "possibly null" at XmlRequestReader.cs(68,28,68,40) and (68,28,68,59): same start
        /// line+column, same code, same message, differing only in the END span the table doesn't surface.
        /// The Error List shows them as two rows because they are two entries; any key on (file,line,column,
        /// code,message) merged them and undercounted. So we count entries 1:1, matching what the user sees.
        /// </summary>
        private async Task AddBuildSourceDiagnosticsAsync(List<ToolDiagnostic> result, DiagnosticSeverity minSeverity, string projectName, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            foreach (var entry in ReadErrorListTable(buildSourceOnly: true))
            {
                if (entry.Severity < minSeverity)
                    continue;
                if (projectName is not null && !MatchesErrorListProject(entry.ProjectName, projectName))
                    continue;
                entry.Source = "build";
                result.Add(entry);
            }
        }

        /// <summary>Matches an Error List row's ProjectName against a requested build target — exact, or the
        /// "Name (net8.0)" per-TFM display a multi-targeted project shows (mirrors <see cref="MatchesRoslynProjectName"/>).</summary>
        private static bool MatchesErrorListProject(string errorListProject, string projectName)
        {
            if (string.IsNullOrEmpty(errorListProject))
                return false;
            return string.Equals(errorListProject, projectName, StringComparison.OrdinalIgnoreCase)
                || errorListProject.StartsWith(projectName + " (", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The tail of VS's Build output pane, as a last-resort backstop for a build that failed but left
        /// zero structured rows (a target crash, a failed pre/post-build event, an MSBuild error that never
        /// reached the Error List) — so build_solution never again reports "failed" with nothing to act on
        /// (issue #47). Best-effort: any failure returns null and the payload just omits it.
        /// </summary>
        /// <remarks>
        /// <b>Switches to the UI thread itself rather than requiring the caller to be on it</b> (issue #89).
        /// It used to assert with <c>ThrowIfNotOnUIThread</c>, and the one call site reached it off the UI
        /// thread: <c>BuildSolutionAsync</c> establishes affinity, then awaits
        /// <c>CollectBuildDiagnosticsConvergedAsync(...).ConfigureAwait(false)</c>, which discards the
        /// captured context so everything after it resumes on the pool. That is deterministic, not a race —
        /// a <c>ConfigureAwait(false)</c> continuation may not inline onto a thread carrying a non-default
        /// SynchronizationContext, so it is always queued away — and it threw
        /// <c>COMException RPC_E_WRONG_THREAD</c> every time it was reached.
        /// <para>
        /// Reached rarely, which is why it stayed latent: the caller only computes this for a build that
        /// FAILED while leaving zero Error List rows. So the backstop that exists to stop "failed with
        /// nothing to act on" was instead turning that exact case into a hard tool error, costing the
        /// caller the structured payload as well as the output tail — strictly worse than having no
        /// backstop at all.
        /// </para>
        /// The guarantee now travels with the method instead of with its call site, matching
        /// <c>AddBuildSourceDiagnosticsAsync</c>, so a future caller cannot reintroduce this by awaiting
        /// something in front of it.
        /// </remarks>
        private async Task<string> ReadBuildOutputTailAsync(CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            try
            {
                var panes = _dte?.ToolWindows?.OutputWindow?.OutputWindowPanes;
                if (panes is null)
                    return null;
                // Shared with get_debug_output's read rather than walked again here (issue #272): one
                // stranger's Guid getter throwing used to end this search too, and here the loss is
                // silent — the backstop for "the build failed with nothing to act on" simply not being
                // in the payload, which is indistinguishable from a build that left no output.
                var pane = FindPane(panes, VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid);
                if (pane is not null)
                {
                    var doc = pane.TextDocument;
                    var text = doc?.StartPoint?.CreateEditPoint()?.GetText(doc.EndPoint);
                    if (string.IsNullOrWhiteSpace(text))
                        return null;
                    // Shared with get_debug_output's read (issue #73) rather than kept as a second copy:
                    // two panes trimmed by two implementations is two chances to keep the wrong end, and
                    // the wrong end is silent — plausible output describing a different run.
                    return OutputTail.Keep(text.Replace("\r\n", "\n").TrimEnd(), MaxBuildOutputChars);
                }
            }
            catch
            {
                // Output-window automation is best-effort; a failure here must not sink the build result.
            }
            return null;
        }

        /// <summary>
        /// Reads the VS error table so we get the ErrorCode column. UI thread only. With
        /// <paramref name="buildSourceOnly"/> it keeps only rows the last build produced
        /// (<see cref="ErrorSource.Build"/>), dropping the IntelliSense/live half (<c>ErrorSource.Other</c>) —
        /// that half lags a just-applied edit, which is exactly why the shared diagnostics path trusts Roslyn
        /// for CS/BC. A row with no ErrorSource key is treated as non-build (excluded) so only confirmed build
        /// rows pass.
        /// </summary>
        /// <remarks>
        /// <b>Two halves, and BOTH are needed (issue #93).</b> The Error List's source dropdown is not a
        /// display filter — it gates what the data source PUBLISHES, so with it set to "IntelliSense Only"
        /// a solution's entire build output is absent from the table and no reading layer can recover it.
        /// <see cref="WidenErrorListView"/> therefore turns both halves on for the duration of the read and
        /// restores the user's own view afterwards. And the read must go to the table's DATA rather than the
        /// window's control, because after that flip the two DISAGREE: measured, the sources held 28 rows
        /// while the control still held 0, since the control is a long-lived subscriber updated by dispatched
        /// events whereas a fresh subscribe sees current state. Reading the control here would hand back the
        /// zero we just fixed.
        /// <para>
        /// The <c>ErrorSource.Build</c> filter is unchanged and deliberately kept: it is what stops stale
        /// editor rows producing phantom errors on a build that succeeded. Measured against the real table,
        /// it drops nothing it shouldn't — all 28 rows of a 28-warning build came through marked
        /// <c>Build</c> and all 28 were kept.
        /// </para>
        /// </remarks>
        private List<ToolDiagnostic> ReadErrorListTable(bool buildSourceOnly = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var entries = new List<ToolDiagnostic>();

            // MUST wrap the subscribe below, not just the enumeration: the flip is picked up because
            // ErrorTableReader.Open subscribes AFTER it, so the sources' current state already includes
            // the build rows. Setting it afterwards would change nothing we then read.
            // A build read asks for the live half OFF, because VS RE-ATTRIBUTES build diagnostics to
            // ErrorSource.Other as live analysis warms up — see ErrorListViewScope for the measurement.
            using (WidenErrorListView(wantOther: !buildSourceOnly))
            {
                var manager = ErrorTableManager;
                int buildRows = 0, otherRows = 0, sourceRows = 0; // see ErrorTableLog (opt-in)

                using (var reader = ErrorTableReader.Open(manager))
                {
                    foreach (var row in reader.Rows())
                    {
                        if (entries.Count >= MaxDiagnosticsScanned)
                            break;

                        // A snapshot column arrives boxed and untyped, so the enum comparisons that the
                        // control's generic TryGetValue<T> used to do are ours now (see Core.Ide.TableValue).
                        var hasSource = TableValue.TryAsInt(row.GetValue(StandardTableKeyNames.ErrorSource), out var src);
                        var isBuildRow = hasSource && (ErrorSource)src == ErrorSource.Build;
                        sourceRows++;
                        if (isBuildRow) buildRows++; else otherRows++;

                        var severity = TableValue.TryAsInt(row.GetValue(StandardTableKeyNames.ErrorSeverity), out var category)
                            ? MapCategory((__VSERRORCATEGORY)category)
                            : DiagnosticSeverity.Info;
                        var code = TableValue.AsString(row.GetValue(StandardTableKeyNames.ErrorCode));

                        if (buildSourceOnly && !isBuildRow)
                            continue;

                        entries.Add(new ToolDiagnostic
                        {
                            File = TableValue.AsString(row.GetValue(StandardTableKeyNames.DocumentName)) ?? string.Empty,
                            Line = TableValue.AsOneBasedPosition(row.GetValue(StandardTableKeyNames.Line)),
                            Column = TableValue.AsOneBasedPosition(row.GetValue(StandardTableKeyNames.Column)),
                            Severity = severity,
                            Message = TableValue.AsString(row.GetValue(StandardTableKeyNames.Text)) ?? string.Empty,
                            Code = code,
                            ProjectName = TableValue.AsString(row.GetValue(StandardTableKeyNames.ProjectName)),
                        });
                    }

                    if (ErrorTableLog.Enabled)
                        LogRead(buildSourceOnly, manager, sourceRows, buildRows, otherRows, entries.Count);
                }
            }
            return entries;
        }

        /// <summary>
        /// Makes the Error List publish BOTH error sources for the duration of a diagnostics read, and
        /// restores the developer's own view when the scope closes (issue #93).
        /// </summary>
        /// <remarks>
        /// <b>This is a mutation of IDE state to service a query, which normally we would not do.</b> It
        /// earns the exception because the alternative is a confidently wrong answer: the source dropdown
        /// decides what the data source PUBLISHES, not merely what the window shows, so with it on
        /// "IntelliSense Only" a solution that builds with 28 warnings reports <c>warningCount: 0</c> — and
        /// with it on "Build Only" the analyzer/IntelliSense half vanishes the same way. Both were measured
        /// on the same solution with nothing changing but the dropdown. No read-side change reaches this;
        /// the rows are never published, and the loss is upstream in VS (dotnet/roslyn#74380 and its
        /// predecessors, closed as not planned).
        /// <para>
        /// Kept as narrow as it can be: nothing happens unless a half is actually switched off, the flip
        /// lasts only as long as the read, and it is undone in a <c>finally</c> so a throw or a cancelled
        /// turn cannot strand the developer in a view they did not choose. The severity toggles
        /// (<c>AreErrorsShown</c> and friends) are deliberately NOT touched — those are display-only, which
        /// is why changing them was observed to make no difference to what we read, and mutating them would
        /// be a visible change that buys nothing.
        /// </para>
        /// <para>
        /// Restores the ORIGINAL value rather than assuming <c>false</c>, and only for the halves it
        /// actually changed, so a concurrent change by the user is overwritten as narrowly as possible.
        /// </para>
        /// <para>
        /// <b>INVARIANT: nothing inside this scope may yield the UI thread.</b> The flip is invisible to the
        /// developer only because the whole widen → subscribe → read → restore runs in one synchronous
        /// block, so the dispatcher never gets a turn while a half is switched on and the Error List has no
        /// opportunity to repaint. That is not a happy accident — it is the same fact as the sources holding
        /// 28 rows while the control still held 0. Introduce an <c>await</c> between the widen and the
        /// restore and the flip becomes both VISIBLE and re-entrant. It is also why the scope sits inside
        /// <see cref="ReadErrorListTable"/> per read rather than once around
        /// <see cref="CollectBuildDiagnosticsConvergedAsync"/>'s poll, which would be fewer flips but yields
        /// on every <c>Task.Delay</c> between samples.
        /// </para>
        /// <para>
        /// <b>It also rests on the re-publish being synchronous</b> — setting the property makes the rows
        /// available to a subscribe on the very next statement, which is what was measured. If a future VS
        /// dispatched that work instead, this read would go back to returning zero, and there would be no
        /// way to wait for it without breaking the invariant above. That is the thing to re-measure if
        /// build diagnostics ever start coming back empty again.
        /// </para>
        /// </remarks>
        /// <param name="wantOther">
        /// Whether the IntelliSense/live half should be ON for this read. <b>False for a build-source read,
        /// and that is not an optimisation — it is the fix for the re-attribution below.</b>
        /// </param>
        private IDisposable WidenErrorListView(bool wantOther)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ErrorListViewScope.Apply(_errorList, wantBuild: true, wantOther: wantOther);
        }

        /// <summary>
        /// One line per Error List read, opt-in via <see cref="ErrorTableLog"/> — the comparison that has
        /// settled every wrong turn in this area. UI thread only.
        /// </summary>
        private void LogRead(bool buildSourceOnly, ITableManager manager, int sourceRows, int buildRows, int otherRows, int kept)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // What the WINDOW holds, against what the SOURCES hold. Distinguish the "no number"
                // outcomes: an absent control and an empty one mean very different things.
                string controlRows;
                try
                {
                    if (_errorList is null)
                        controlRows = "(no IErrorList)";
                    else
                    {
                        var control = _errorList.TableControl;
                        controlRows = control is null ? "(no TableControl)" : control.Entries.Count().ToString();
                    }
                }
                catch (Exception ex) { controlRows = "(threw " + ex.GetType().Name + ")"; }

                string view = "(no IErrorList)";
                try
                {
                    if (_errorList is not null)
                        view = $"build={_errorList.AreBuildErrorSourceEntriesShown},other={_errorList.AreOtherErrorSourceEntriesShown}";
                }
                catch (Exception ex) { view = "threw " + ex.GetType().Name; }

                ErrorTableLog.Write(
                    $"read buildOnly={buildSourceOnly} manager={(manager is null ? "NULL" : manager.Identifier)} " +
                    $"sourceRows={sourceRows} (Build={buildRows},Other={otherRows}) controlRows={controlRows} " +
                    $"kept={kept} view({view})");
            }
            catch { /* a diagnostic must never break a tool call */ }
        }

        /// <summary>Maps and de-dupes one Roslyn diagnostic into the result set (first writer of a key wins → source precedence).</summary>
        private static void AddRoslynDiagnostic(Microsoft.CodeAnalysis.Diagnostic diagnostic, string source, DiagnosticSeverity minSeverity, string fileFilter, List<ToolDiagnostic> result, DiagnosticDeduper deduper)
        {
            var severity = MapSeverity(diagnostic.Severity);
            if (severity < minSeverity)
                return;
            if (!diagnostic.Location.IsInSource)
                return;

            var span = diagnostic.Location.GetLineSpan();
            if (!MatchesFileFilter(span.Path, fileFilter))
                return;
            var item = new ToolDiagnostic
            {
                File = span.Path,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
                Severity = severity,
                Message = diagnostic.GetMessage(),
                Code = diagnostic.Id,
                Source = source,
            };
            // Keyed on the FULL span: two distinct diagnostics can share a start column and differ only in
            // where they end (the issue-#47 CS8602 pair at (68,28,68,40) and (68,28,68,59)).
            if (deduper.TryAddRoslyn(item.File, item.Line, item.Column, item.EndLine.Value, item.EndColumn.Value, item.Code))
                result.Add(item);
        }

        /// <summary>
        /// Matches a Roslyn workspace project name against a DTE display name. A multi-targeting project
        /// appears in the Roslyn workspace once per TFM, named "Name (net472)" / "Name (net10.0)", so the
        /// DTE name matches exactly OR as the "Name (…)" prefix — otherwise a scoped build of a
        /// multi-targeted project would collect no diagnostics at all. Diagnostics duplicated across TFM
        /// slices collapse via the (file, line, code) seen-key.
        /// </summary>
        private static bool MatchesRoslynProjectName(string roslynName, string projectName)
        {
            if (string.Equals(roslynName, projectName, StringComparison.OrdinalIgnoreCase))
                return true;
            return roslynName is not null
                && roslynName.StartsWith(projectName + " (", StringComparison.OrdinalIgnoreCase)
                && roslynName.EndsWith(")", StringComparison.Ordinal);
        }

        /// <summary>True for a C#/VB *compiler* diagnostic id (CS#### / BC####) — the class Roslyn owns.</summary>
        private static bool IsCompilerCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 3)
                return false;
            var prefix = (code[0] == 'C' || code[0] == 'c') && (code[1] == 'S' || code[1] == 's');
            var vb = (code[0] == 'B' || code[0] == 'b') && (code[1] == 'C' || code[1] == 'c');
            if (!prefix && !vb)
                return false;
            for (var i = 2; i < code.Length; i++)
                if (!char.IsDigit(code[i]))
                    return false;
            return true;
        }

        private static DiagnosticSeverity MapSeverity(Microsoft.CodeAnalysis.DiagnosticSeverity severity) => severity switch
        {
            Microsoft.CodeAnalysis.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Info => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Hidden,
        };

        private static DiagnosticSeverity MapCategory(__VSERRORCATEGORY category) => category switch
        {
            __VSERRORCATEGORY.EC_ERROR => DiagnosticSeverity.Error,
            __VSERRORCATEGORY.EC_WARNING => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };

        private static object DiagnosticJson(ToolDiagnostic d) => new
        {
            severity = d.Severity.ToString().ToLowerInvariant(),
            file = d.File,
            line = d.Line,
            column = d.Column,
            // Without the end span, two distinct same-start diagnostics (CS8602 at (68,28,68,40) and
            // (68,28,68,59)) serialize identically — CS####'s message text is generic — and read as a
            // duplicated row. endLine only when the span isn't single-line; both null for Error List rows.
            endLine = d.EndLine == d.Line ? null : d.EndLine,
            endColumn = d.EndColumn,
            code = string.IsNullOrEmpty(d.Code) ? null : d.Code,
            message = Truncate(d.Message, MaxMessageLength),
            source = d.Source,
        };

        /// <summary>Counts diagnostics by code (most frequent first, capped) so a large result stays legible — e.g. {"CS0246":127,"CS0103":40}.</summary>
        private static Dictionary<string, int> CountByCode(IEnumerable<ToolDiagnostic> diagnostics)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in diagnostics)
            {
                var code = string.IsNullOrEmpty(d.Code) ? "(none)" : d.Code;
                counts[code] = counts.TryGetValue(code, out var n) ? n + 1 : 1;
            }
            return counts
                .OrderByDescending(kv => kv.Value)
                .Take(MaxCodeCounts)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "…";

        /// <summary>Reads the optional 'rebuild' boolean argument (default false).</summary>
        /// <summary>Reads an optional boolean argument (default false). Throws <see cref="JsonException"/> on
        /// malformed JSON so the caller can fail loud rather than silently treating a typo'd payload as absent.</summary>
        private static bool ReadBoolArg(string argumentsJson, string name)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return false;
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out var r)
                && (r.ValueKind == JsonValueKind.True || r.ValueKind == JsonValueKind.False)
                && r.GetBoolean();
        }

        /// <summary>Flattens the solution's buildable projects, descending into solution folders (which aren't buildable themselves). UI-thread only (DTE COM objects).</summary>
        /// <summary>
        /// Flattens the solution's projects (recursing through solution folders), skipping any whose DTE
        /// properties throw and recording why in <paramref name="skipped"/>.
        /// <para>
        /// A project that failed to load, is unloaded, or is an exotic project type is represented in
        /// <c>Solution.Projects</c> by a stub that does NOT implement the full <see cref="EnvDTE.Project"/>
        /// surface: reading <c>Kind</c>/<c>Name</c>/<c>FullName</c> on it throws
        /// <see cref="NotImplementedException"/> (E_NOTIMPL). That used to abort the whole tool — issue #21,
        /// where <c>run_tests</c> returned a bare "Not implemented (Exception from HRESULT: 0x80004001)" on a
        /// solution that had just been through a merge-conflict resolution. One broken project must never take
        /// out the tool, so every property read here is guarded and reported rather than fatal.
        /// </para>
        /// </summary>
        private static IEnumerable<EnvDTE.Project> EnumerateProjects(EnvDTE.Solution solution, List<string> skipped = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Materialize first: the COM enumeration itself can throw, and a try/catch cannot wrap a yield.
            var roots = new List<EnvDTE.Project>();
            try
            {
                foreach (EnvDTE.Project project in solution.Projects)
                    roots.Add(project);
            }
            catch (Exception ex)
            {
                skipped?.Add($"the solution's project list could not be read ({ex.GetType().Name}: {ex.Message})");
            }

            foreach (var root in roots)
                foreach (var p in EnumerateProject(root, skipped))
                    yield return p;
        }

        private static IEnumerable<EnvDTE.Project> EnumerateProject(EnvDTE.Project project, List<string> skipped)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project is null)
                yield break;

            // Kind is the first read that throws on a load-failed / unloaded project stub.
            string kind;
            try { kind = project.Kind; }
            catch (Exception ex) { skipped?.Add(DescribeSkippedProject(project, ex)); yield break; }

            if (!string.Equals(kind, EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
            {
                yield return project;
                yield break;
            }

            var children = new List<EnvDTE.Project>();
            try
            {
                if (project.ProjectItems is not null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        EnvDTE.Project sub = null;
                        try { sub = item.SubProject; }
                        catch { /* an item that can't expose its sub-project is not one */ }
                        if (sub is not null)
                            children.Add(sub);
                    }
                }
            }
            catch (Exception ex)
            {
                skipped?.Add(DescribeSkippedProject(project, ex));
            }

            foreach (var child in children)
                foreach (var p in EnumerateProject(child, skipped))
                    yield return p;
        }

        /// <summary>The project's path, or null (recorded in <paramref name="skipped"/>) when DTE won't give it up.</summary>
        private static string TryGetProjectFullName(EnvDTE.Project project, List<string> skipped)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return project.FullName; }
            catch (Exception ex) { skipped?.Add(DescribeSkippedProject(project, ex)); return null; }
        }

        /// <summary>The project's display name, or null (recorded in <paramref name="skipped"/>) when DTE won't give it up.</summary>
        private static string TryGetProjectName(EnvDTE.Project project, List<string> skipped)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return project.Name; }
            catch (Exception ex) { skipped?.Add(DescribeSkippedProject(project, ex)); return null; }
        }

        /// <summary>Names the unusable project as far as DTE will allow (Name itself may throw on the same stub).</summary>
        private static string DescribeSkippedProject(EnvDTE.Project project, Exception ex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string name = null;
            try { name = project.Name; }
            catch { /* the stub won't even name itself */ }
            var subject = string.IsNullOrEmpty(name) ? "an unloaded or unreadable project" : $"project '{name}'";
            return $"{subject} was skipped — Visual Studio could not read it ({ex.GetType().Name}: {ex.Message}); "
                + "reload it in Solution Explorer (or reopen the solution) if it should be included";
        }

        /// <summary>
        /// Reads the optional get_diagnostics arguments: 'scope' (compiler | analyzers | full; default
        /// compiler), 'severity' (the minimum severity included; default warning) and 'file'. An unknown
        /// scope/severity value FAILS rather than silently returning the default — a typo like
        /// scope:"analyzer" would otherwise yield compiler-only results the caller reads as "no analyzer
        /// findings", a silent wrong answer.
        /// </summary>
        private static (DiagnosticScope Scope, DiagnosticSeverity MinSeverity, string File, string Error) ReadDiagnosticsArgs(string argumentsJson)
        {
            var scope = DiagnosticScope.Compiler;
            var minSeverity = DiagnosticSeverity.Warning;
            string file = null;

            if (string.IsNullOrWhiteSpace(argumentsJson))
                return (scope, minSeverity, file, null);
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (scope, minSeverity, file, null);

            if (doc.RootElement.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String)
            {
                switch (s.GetString()?.Trim().ToLowerInvariant())
                {
                    case "compiler": scope = DiagnosticScope.Compiler; break;
                    case "analyzers": scope = DiagnosticScope.Analyzers; break;
                    case "full": scope = DiagnosticScope.Full; break;
                    default: return (scope, minSeverity, file, $"unknown scope '{s.GetString()}' — use compiler, analyzers or full");
                }
            }

            if (doc.RootElement.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String)
            {
                switch (sev.GetString()?.Trim().ToLowerInvariant())
                {
                    case "error": minSeverity = DiagnosticSeverity.Error; break;
                    case "warning": minSeverity = DiagnosticSeverity.Warning; break;
                    case "info": minSeverity = DiagnosticSeverity.Info; break;
                    case "hidden": minSeverity = DiagnosticSeverity.Hidden; break;
                    default: return (scope, minSeverity, file, $"unknown severity '{sev.GetString()}' — use error, warning, info or hidden");
                }
            }

            if (doc.RootElement.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                file = f.GetString().Trim();

            return (scope, minSeverity, file, null);
        }

        /// <summary>
        /// Finds every semantic reference to the named symbol via VS's live Roslyn workspace. Runs off
        /// the UI thread (the Roslyn workspace/SymbolFinder APIs are thread-safe and genuinely async, so
        /// a large solution search doesn't freeze the IDE). Name resolution: the query's last dotted
        /// segment is the symbol name; any leading qualifier must match the symbol's namespace/type path
        /// as a suffix, which is how overloads/same-named symbols get disambiguated. Only source-declared
        /// symbols are considered (references to framework types like System.String are out of scope).
        /// </summary>
        private async Task<ToolResult> FindReferencesAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            if (Workspace is null)
                return new ToolResult(IsError: true, Json.Object(("error", "the C#/VB code analysis workspace is not available")));

            string query;
            try
            {
                query = ReadSymbolArgument(argumentsJson);
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResult(IsError: true, Json.Object(("error", "the 'symbol' argument is required")));

            var solution = Workspace.CurrentSolution;
            var symbols = await ResolveSymbolsAsync(solution, query, sourceOnly: true, cancellationToken).ConfigureAwait(false);

            var matches = new List<object>();
            foreach (var symbol in symbols.Take(MaxSymbolsReported))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var references = await CollectReferencesAsync(solution, symbol, cancellationToken).ConfigureAwait(false);
                var definition = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                matches.Add(new
                {
                    signature = symbol.ToDisplayString(SignatureFormat),
                    kind = symbol.Kind.ToString(),
                    definition = definition is null ? null : LocationJson(definition.GetLineSpan()),
                    referenceCount = references.Count,
                    truncatedReferences = references.Count >= MaxReferencesPerSymbol,
                    references,
                });
            }

            var payload = new
            {
                symbol = query,
                matchCount = symbols.Count,
                truncatedMatches = symbols.Count > MaxSymbolsReported,
                matches,
                note = WorkspaceScopeNote(),
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Looks up symbols by (optionally qualified) name and returns each declaration + rich signature —
        /// including metadata symbols from referenced assemblies/the BCL that a text search can't reach.
        /// Read-only; runs off the UI thread. No reference search, so it's cheap enough to report more
        /// matches than find_references.
        /// </summary>
        private async Task<ToolResult> FindSymbolAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            if (Workspace is null)
                return new ToolResult(IsError: true, Json.Object(("error", "the C#/VB code analysis workspace is not available")));

            string query;
            try
            {
                query = ReadSymbolArgument(argumentsJson);
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResult(IsError: true, Json.Object(("error", "the 'symbol' argument is required")));

            var solution = Workspace.CurrentSolution;
            var symbols = await ResolveSymbolsAsync(solution, query, sourceOnly: false, cancellationToken).ConfigureAwait(false);

            var matches = new List<object>();
            foreach (var symbol in symbols.Take(MaxSymbolMatchesReported))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches.Add(BuildSymbolMatch(symbol, cancellationToken));
            }

            var payload = new
            {
                symbol = query,
                matchCount = symbols.Count,
                truncatedMatches = symbols.Count > MaxSymbolMatchesReported,
                matches,
                note = WorkspaceScopeNote(),
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>Projects one resolved symbol to the find_symbol result shape (signature + location/metadata + docs).</summary>
        private static object BuildSymbolMatch(ISymbol symbol, CancellationToken cancellationToken)
        {
            var source = symbol.Locations.FirstOrDefault(l => l.IsInSource);

            string baseType = null;
            string[] interfaces = null;
            if (symbol is INamedTypeSymbol named)
            {
                if (named.BaseType is INamedTypeSymbol bt
                    && bt.SpecialType != SpecialType.System_Object
                    && bt.SpecialType != SpecialType.System_ValueType
                    && bt.SpecialType != SpecialType.System_Enum)
                    baseType = bt.ToDisplayString(TypeRefFormat);
                if (named.Interfaces.Length > 0)
                    interfaces = named.Interfaces.Select(i => i.ToDisplayString(TypeRefFormat)).ToArray();
            }

            return new
            {
                signature = symbol.ToDisplayString(DefinitionFormat),
                kind = symbol.Kind.ToString(),
                @namespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
                containingType = symbol.ContainingType?.ToDisplayString(TypeRefFormat),
                source = source is not null,
                definition = source is null ? null : LocationJson(source.GetLineSpan()),
                assembly = symbol.ContainingAssembly?.Identity.Name,
                documentation = DocSummary(symbol, cancellationToken),
                baseType,
                interfaces,
            };
        }

        /// <summary>The <c>&lt;summary&gt;</c> text from a symbol's XML doc comment, flattened and length-capped (null if none).</summary>
        private static string DocSummary(ISymbol symbol, CancellationToken cancellationToken)
        {
            try
            {
                var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
                if (string.IsNullOrWhiteSpace(xml))
                    return null;

                var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
                if (summary is null)
                    return null;

                var text = string.Concat(summary.Nodes().Select(DocNodeText));
                text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
                if (text.Length == 0)
                    return null;
                return text.Length > MaxDocLength ? text.Substring(0, MaxDocLength) : text;
            }
            catch
            {
                return null; // malformed / unavailable doc XML is non-fatal
            }
        }

        /// <summary>Flattens an XML-doc node to plain text, turning &lt;see cref&gt;/&lt;paramref&gt; into their bare names.</summary>
        private static string DocNodeText(XNode node)
        {
            switch (node)
            {
                case XText t:
                    return t.Value;
                case XElement e when e.Attribute("cref") is { } cref:
                    return StripCrefPrefix(cref.Value);
                case XElement e when (e.Name.LocalName == "paramref" || e.Name.LocalName == "typeparamref")
                    && e.Attribute("name") is { } n:
                    return n.Value;
                case XElement e:
                    return string.Concat(e.Nodes().Select(DocNodeText));
                default:
                    return string.Empty;
            }
        }

        /// <summary>"T:System.String" -> "String"; "M:MyType.Do(System.Int32)" -> "Do".</summary>
        private static string StripCrefPrefix(string cref)
        {
            var colon = cref.IndexOf(':');
            var body = colon >= 0 ? cref.Substring(colon + 1) : cref;
            var paren = body.IndexOf('(');
            if (paren >= 0)
                body = body.Substring(0, paren);
            var lastDot = body.LastIndexOf('.');
            return lastDot >= 0 && lastDot < body.Length - 1 ? body.Substring(lastDot + 1) : body;
        }

        /// <summary>
        /// The down-the-hierarchy view: resolves the named symbol (source or metadata) and, dispatching on
        /// its kind, returns implementing types/members (interface), overriding members (virtual/abstract),
        /// or derived classes (class). Read-only; runs off the UI thread. Reuses the find_symbol resolver
        /// and per-result shape.
        /// </summary>
        private async Task<ToolResult> FindImplementationsAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            if (Workspace is null)
                return new ToolResult(IsError: true, Json.Object(("error", "the C#/VB code analysis workspace is not available")));

            string query;
            try
            {
                query = ReadSymbolArgument(argumentsJson);
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResult(IsError: true, Json.Object(("error", "the 'symbol' argument is required")));

            var solution = Workspace.CurrentSolution;
            var symbols = await ResolveSymbolsAsync(solution, query, sourceOnly: false, cancellationToken).ConfigureAwait(false);

            var matches = new List<object>();
            foreach (var symbol in symbols.Take(MaxSymbolsReported))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (relation, implementations) = await CollectImplementationsAsync(solution, symbol, cancellationToken).ConfigureAwait(false);
                var definition = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                matches.Add(new
                {
                    signature = symbol.ToDisplayString(SignatureFormat),
                    kind = symbol.Kind.ToString(),
                    relation,
                    source = definition is not null,
                    definition = definition is null ? null : LocationJson(definition.GetLineSpan()),
                    assembly = symbol.ContainingAssembly?.Identity.Name,
                    implementationCount = implementations.Count,
                    truncatedImplementations = implementations.Count >= MaxImplementationsPerSymbol,
                    implementations,
                });
            }

            var payload = new
            {
                symbol = query,
                matchCount = symbols.Count,
                truncatedMatches = symbols.Count > MaxSymbolsReported,
                matches,
                note = WorkspaceScopeNote(),
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Dispatches one symbol to the right hierarchy query and returns (relation, distinct results).
        /// relation is "implementation" (interface / interface member), "override" (virtual/abstract/override
        /// member), "derived" (class), or "none" when the symbol has no down-hierarchy (e.g. a sealed method,
        /// a struct/enum, a non-overridable member).
        /// </summary>
        private static async Task<(string Relation, List<object> Items)> CollectImplementationsAsync(CodeSolution solution, ISymbol symbol, CancellationToken cancellationToken)
        {
            var target = symbol.OriginalDefinition ?? symbol;
            var relation = RelationFor(target);
            if (relation == "none")
                return (relation, new List<object>());

            IEnumerable<ISymbol> found;
            try
            {
                found = await RunHierarchyFinderAsync(solution, target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return (relation, new List<object>());
            }

            var items = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var impl in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (impl is null || !seen.Add(SymbolKey(impl)))
                    continue;
                items.Add(BuildSymbolMatch(impl, cancellationToken));
                if (items.Count >= MaxImplementationsPerSymbol)
                    break;
            }
            return (relation, items);
        }

        /// <summary>The down-hierarchy relation a symbol has, or "none" — see <see cref="CollectImplementationsAsync"/>.</summary>
        private static string RelationFor(ISymbol symbol)
        {
            if (symbol is INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Interface) return "implementation";
                if (type.TypeKind == TypeKind.Class) return "derived";
                return "none";
            }
            if (symbol.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Event)
            {
                if (symbol.ContainingType?.TypeKind == TypeKind.Interface) return "implementation";
                if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride) return "override";
            }
            return "none";
        }

        /// <summary>Roslyn's index-based finder for the symbol's down-hierarchy relation.</summary>
        private static async Task<IEnumerable<ISymbol>> RunHierarchyFinderAsync(CodeSolution solution, ISymbol target, CancellationToken cancellationToken)
        {
            if (target is INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Interface)
                    return await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (type.TypeKind == TypeKind.Class)
                    return await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, projects: null, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Array.Empty<ISymbol>();
            }
            if (target.ContainingType?.TypeKind == TypeKind.Interface)
                return await SymbolFinder.FindImplementationsAsync(target, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await SymbolFinder.FindOverridesAsync(target, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Semantic rename: renames exactly one source symbol and all its references across the solution,
        /// then applies the change through VS so it lands in the editor and joins the global undo stack.
        /// Refuses to act on an ambiguous name (returns the candidates), a metadata symbol, or an invalid
        /// identifier — a wrong rename is expensive. Resolution runs off the UI thread; only the final
        /// <see cref="VisualStudioWorkspace.TryApplyChanges"/> is marshalled onto it.
        /// </summary>
        private async Task<ToolResult> RenameSymbolAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            if (Workspace is null)
                return new ToolResult(IsError: true, Json.Object(("error", "the C#/VB code analysis workspace is not available")));

            string query, newName;
            try
            {
                (query, newName) = ReadRenameArguments(argumentsJson);
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }
            if (string.IsNullOrWhiteSpace(query))
                return new ToolResult(IsError: true, Json.Object(("error", "the 'symbol' argument is required")));
            if (string.IsNullOrWhiteSpace(newName))
                return new ToolResult(IsError: true, Json.Object(("error", "the 'newName' argument is required")));
            if (!IsValidIdentifier(newName))
                return new ToolResult(IsError: true, Json.Object(("error", $"'{newName}' is not a valid identifier")));

            var solution = Workspace.CurrentSolution;
            var symbols = await ResolveSymbolsAsync(solution, query, sourceOnly: true, cancellationToken).ConfigureAwait(false);

            if (symbols.Count == 0)
                // The scope note takes over when there is nothing loaded: the stock message offers a
                // reason ("from a referenced assembly") that would be a wrong lead in that case.
                return new ToolResult(IsError: true, Json.Object(("error",
                    WorkspaceScopeNote() is { } scope
                        ? $"cannot rename '{query}': {scope}"
                        : $"no source symbol named '{query}' was found (a symbol from a referenced assembly cannot be renamed)")));
            if (symbols.Count > 1)
            {
                // Refuse to guess — hand back the candidates so the agent can re-issue with a qualifier.
                var payload = new
                {
                    error = $"'{query}' is ambiguous ({symbols.Count} matches); re-issue with a dotted-qualified name so exactly one matches.",
                    candidates = symbols.Take(MaxSymbolMatchesReported).Select(s => new
                    {
                        signature = s.ToDisplayString(SignatureFormat),
                        kind = s.Kind.ToString(),
                        definition = s.Locations.FirstOrDefault(l => l.IsInSource) is { } d ? LocationJson(d.GetLineSpan()) : null,
                    }),
                };
                return new ToolResult(IsError: true, JsonSerializer.Serialize(payload, JsonOptions));
            }

            var symbol = symbols[0];
            if (string.Equals(symbol.Name, newName, StringComparison.Ordinal))
                return new ToolResult(IsError: true, Json.Object(("error", $"the symbol is already named '{newName}'")));

            CodeSolution renamed;
            try
            {
                renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return new ToolResult(IsError: true, Json.Object(("error", $"rename failed: {ex.Message}")));
            }

            // Summarize from the immutable solution snapshots (independent of when we apply).
            var (fileCount, editCount, files) = await SummarizeSolutionChangesAsync(solution, renamed, cancellationToken).ConfigureAwait(false);

            // Apply through VS on the UI thread so it flows into open buffers + the editor undo stack.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            bool applied;
            try
            {
                applied = Workspace.TryApplyChanges(renamed);
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", $"could not apply the rename: {ex.Message}")));
            }
            if (!applied)
                return new ToolResult(IsError: true, Json.Object(("error", "could not apply the rename — the solution changed underneath it; re-read and try again")));

            // Save the touched buffers so disk matches the editor (still undoable — the undo stack survives).
            SaveChangedDocuments(solution, renamed);

            var oldName = symbol.Name;
            var result = new
            {
                // Human-readable headline first, so the agent's tool-call row reads cleanly.
                summary = $"Renamed '{oldName}' to '{newName}' in {fileCount} file{(fileCount == 1 ? "" : "s")} " +
                          $"({editCount} edit{(editCount == 1 ? "" : "s")}). Applied and saved in Visual Studio — undo with Ctrl+Z.",
                renamed = true,
                oldName,
                newName,
                symbol = symbol.ToDisplayString(SignatureFormat),
                filesChanged = fileCount,
                editCount,
                truncatedFiles = files.Count >= MaxRenameFilesListed,
                files,
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(result, JsonOptions));
        }

        /// <summary>
        /// Whether the new snapshot differs from the old in a way the per-file edit counts do NOT capture:
        /// documents or whole projects added or removed.
        /// </summary>
        /// <remarks>
        /// <see cref="SummarizeSolutionChangesAsync"/> walks <c>GetChangedDocuments()</c> only, so a fix
        /// that adds a file (move type to a new file) reports zero files and zero edits while having done
        /// real work. Anything treating "zero edits" as "did nothing" has to consult this first.
        /// </remarks>
        private static bool HasStructuralChanges(CodeSolution oldSolution, CodeSolution newSolution)
        {
            var changes = newSolution.GetChanges(oldSolution);
            if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
                return true;

            foreach (var projectChange in changes.GetProjectChanges())
            {
                if (projectChange.GetAddedDocuments().Any() || projectChange.GetRemovedDocuments().Any())
                    return true;
            }

            return false;
        }

        /// <summary>Per-file edit counts for the documents that differ between two solution snapshots (capped).</summary>
        private static async Task<(int FileCount, int EditCount, List<object> Files)> SummarizeSolutionChangesAsync(
            CodeSolution oldSolution, CodeSolution newSolution, CancellationToken cancellationToken)
        {
            var files = new List<object>();
            var totalEdits = 0;
            var changedFiles = 0; // the TOTAL; `files` above is a capped sample of it.

            foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
            {
                foreach (var docId in projectChange.GetChangedDocuments())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var oldDoc = oldSolution.GetDocument(docId);
                    var newDoc = newSolution.GetDocument(docId);
                    if (oldDoc is null || newDoc is null)
                        continue;

                    var edits = (await newDoc.GetTextChangesAsync(oldDoc, cancellationToken).ConfigureAwait(false)).Count();
                    totalEdits += edits;
                    changedFiles++;
                    if (files.Count < MaxRenameFilesListed)
                        files.Add(new { file = newDoc.FilePath, edits });
                }
            }

            // The COUNT, not the length of the sample. `files` stops growing at MaxRenameFilesListed
            // while totalEdits keeps accumulating, so returning files.Count reported a rename touching 340
            // files as "in 200 files (1,900 edits)" - a total silently clamped to the display cap, which
            // the agent then relays to the user as fact. A capped sample must never double as a total.
            return (changedFiles, totalEdits, files);
        }

        /// <summary>
        /// Persists to disk the documents changed by an applied solution. <see cref="VisualStudioWorkspace.TryApplyChanges"/>
        /// lands edits for OPEN documents in their (dirty) editor buffers without saving, while writing CLOSED documents
        /// straight to disk — a mixed state where the agent's on-disk view lags what VS shows. This saves the still-dirty
        /// open buffers so disk matches the editor, scoped to only the changed files (NOT a global Save-All that would
        /// sweep up the user's unrelated edits). The editor undo stack survives the save, so the change stays undoable
        /// with Ctrl+Z. Best-effort per document; must be called on the UI thread.
        /// </summary>
        private void SaveChangedDocuments(CodeSolution oldSolution, CodeSolution newSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
                foreach (var docId in projectChange.GetChangedDocuments())
                {
                    var path = newSolution.GetDocument(docId)?.FilePath;
                    if (!string.IsNullOrEmpty(path))
                        changedPaths.Add(path);
                }
            if (changedPaths.Count == 0)
                return;

            foreach (EnvDTE.Document doc in _dte.Documents)
            {
                try
                {
                    if (!doc.Saved && !string.IsNullOrEmpty(doc.FullName) && changedPaths.Contains(doc.FullName))
                        doc.Save();
                }
                catch
                {
                    // A buffer that refuses to save (read-only, transient VS state) shouldn't fail the whole tool —
                    // the edit already landed in the buffer, so the user can still persist it with Ctrl+S.
                }
            }
        }

        /// <summary>Reads the required 'symbol' and 'newName' string arguments.</summary>
        private static (string Symbol, string NewName) ReadRenameArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return (null, null);
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);
            var symbol = root.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var newName = root.TryGetProperty("newName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return (symbol, newName);
        }

        /// <summary>Conservative identifier check (letter/underscore start, then letters/digits/underscore) — the C#/VB common subset.</summary>
        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!(char.IsLetter(name[0]) || name[0] == '_'))
                return false;
            for (var i = 1; i < name.Length; i++)
                if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_'))
                    return false;
            return true;
        }

        /// <summary>
        /// Applies a VS code fix for a diagnostic at a location, using the IDE's own <see cref="CodeFixProvider"/>s
        /// (the light-bulb fixes). Collects the diagnostics on the line (compiler diagnostics reliably; analyzer
        /// diagnostics — project references plus the VS-hosted IDE style analyzers — best-effort), matches
        /// providers by fixable id, and —
        /// if exactly one fix results (or one is named via fixTitle) — applies it through VS (undoable). Multiple
        /// fixes with no selection return the choices and make no change. Resolution runs off the UI thread; only
        /// <see cref="VisualStudioWorkspace.TryApplyChanges"/> is marshalled onto it.
        /// <para>
        /// <c>scope:"document"</c> (issue #91) applies the same fix to EVERY occurrence of one diagnostic id in
        /// the file in a single call, through Roslyn's own <see cref="FixAllProvider"/> — the mechanism behind
        /// the light bulb's "Fix all occurrences in Document". The chosen action is only the template: what
        /// identifies it across the other occurrences is its <see cref="CodeAction.EquivalenceKey"/>, which is
        /// what Roslyn's own light bulb passes here. Everything from <c>GetOperationsAsync</c> down is shared
        /// with the line scope — only how the action is PRODUCED differs.
        /// </para>
        /// <para>
        /// <b>That key belongs to fix-all and MUST NOT be borrowed by the line scope</b>, which keeps comparing
        /// <see cref="CodeAction.Title"/> on purpose. It reads like the sanctioned answer to "is this the same
        /// fix" — it is not location-independent. Measured against the real C# fixers: two CS0103 for the SAME
        /// name on one line register identically-titled actions whose keys differ, because a grouped action's
        /// key embeds per-occurrence hashes
        /// (<c>"Generate field 'alpha';…;38944327;33772669;"</c> against <c>"…;49489088;25897426;"</c>). Keying
        /// the sibling count on that would call one fix two, fire the ambiguity guard, and hand the caller
        /// <c>fixTitle</c> as the way out — which cannot separate two identical titles. That is the exact dead
        /// end the guard's own comment records escaping. Where it is safe it is also pointless: IDE0055's three
        /// fixes on one line share <c>"AbstractFormattingCodeFixProvider"</c> AND the title "Fix formatting",
        /// and a flat add-using has <c>Key == Title</c> verbatim. Identical, worse, identical — so the line
        /// scope stays on Title. Fix-all is different because Roslyn matches the key against actions it
        /// recomputes inside one operation, never across independently registered ones.
        /// </para>
        /// <para>
        /// Deliberately capped at one document, though <see cref="FixAllScope"/> also offers Project and
        /// Solution. Risk resolves by tool NAME (<see cref="ToolRisk.Edit"/> here), so a solution-wide fix-all
        /// would auto-approve under AcceptEdits identically to a one-line fix; a per-file cap keeps the blast
        /// radius of one approval to one file without needing a second tool or argument-derived risk. Widening
        /// it means settling that first — and implementing the project-wide members of
        /// <see cref="DocumentDiagnosticProvider"/>, which are unreachable at this scope and answer empty.
        /// </para>
        /// </summary>
        private async Task<ToolResult> ApplyCodeFixAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            if (Workspace is null || CodeFixProviders is null || CodeFixProviders.Count == 0)
                return new ToolResult(IsError: true, Json.Object(("error", "code fixes are not available in this session")));

            string file, diagnosticId, fixTitle, scopeArgument;
            int line, column;
            try
            {
                (file, line, column, diagnosticId, fixTitle, scopeArgument) = ReadFixArguments(argumentsJson);
            }
            catch (JsonException)
            {
                return new ToolResult(IsError: true, Json.Object(("error", "arguments were not valid JSON")));
            }
            if (string.IsNullOrWhiteSpace(file))
                return new ToolResult(IsError: true, Json.Object(("error", "the 'file' argument is required")));
            if (!CodeFixScopes.TryParse(scopeArgument, out var scope, out var scopeError))
                return new ToolResult(IsError: true, Json.Object(("error", scopeError)));
            var argumentError = CodeFixScopes.Validate(scope, line, diagnosticId);
            if (argumentError is not null)
                return new ToolResult(IsError: true, Json.Object(("error", argumentError)));

            var solution = Workspace.CurrentSolution;
            var document = ResolveDocument(solution, file, out var ambiguousMatches);
            if (document is null)
            {
                // Refusing an ambiguous name is deliberate — applying a fix to the wrong file is not
                // recoverable — but "no document open" describes the opposite problem and reads as "that
                // file is not in the solution", which sends the caller looking for the wrong thing. Naming
                // the candidates makes the retry one step. Mirrors get_diagnostics' ambiguity note, which
                // reports and continues for the same input because a wider READ is recoverable.
                if (ambiguousMatches.Count > 1)
                    return new ToolResult(IsError: true, JsonSerializer.Serialize(new
                    {
                        error = $"'{file}' matches {ambiguousMatches.Count} files in the solution, so it is ambiguous " +
                                "and nothing was changed. Re-issue with one of the paths below (a longer fragment is enough).",
                        matches = ambiguousMatches.Take(MaxFixChoicesListed),
                        truncatedMatches = ambiguousMatches.Count > MaxFixChoicesListed,
                    }, JsonOptions));

                return new ToolResult(IsError: true, Json.Object(("error", $"no document open in the solution for '{file}'")));
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (line >= 1 && line > text.Lines.Count)
                return new ToolResult(IsError: true, Json.Object(("error", $"line {line} is past the end of '{file}' ({text.Lines.Count} lines)")));

            // Where the diagnostics are gathered from. A document scope MAY still carry 'line'/'column' — they
            // then choose WHICH occurrence's offered fix becomes the template, not how far the fix reaches — so
            // the whole-file span is used only when no position was given.
            var wholeDocument = scope == CodeFixScope.Document && line < 1;
            var seedSpan = wholeDocument ? new TextSpan(0, text.Length) : text.Lines[line - 1].Span;

            var diagnostics = await GetDiagnosticsAtAsync(document, seedSpan, diagnosticId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(diagnosticId))
                diagnostics = diagnostics.Where(d => string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase)).ToList();
            // 'column' narrows to one diagnostic when several of the same id sit on the line — the same job
            // 'diagnosticId' does for the id axis. Optional by design: for the common case (identically-titled
            // fixes that the caller wants applied anyway) the auto-apply-first path below is less work than
            // making it pick a position it has no basis to choose between.
            var columnFiltered = column > 0
                ? diagnostics.Where(d => ColumnOf(d) == column).ToList()
                : diagnostics;

            if (columnFiltered.Count == 0)
            {
                // Built up rather than nested inline: the two axes (was a column named? was a line named?)
                // multiply, and a message naming "line -1" — which a document scope has — is the kind of
                // detail that sends a caller looking for the wrong thing.
                var where = line >= 1 ? $"on line {line} of '{file}'" : $"in '{file}'";
                var missing = string.IsNullOrEmpty(diagnosticId) ? "diagnostic" : $"diagnostic with id '{diagnosticId}'";
                var payload = new
                {
                    error = column > 0 && diagnostics.Count > 0
                        ? $"no {missing} at column {column} {where}"
                        : wholeDocument
                            ? $"no {missing} anywhere in '{file}', so there is nothing to fix"
                            : $"no {missing} found {where}",
                    // On a column miss, name the columns that ARE on the line so the retry is one step.
                    diagnosticsOnLine = column > 0
                        ? diagnostics.Take(MaxFixChoicesListed)
                            .Select(d => new { id = d.Id, column = ColumnOf(d), message = d.GetMessage() })
                        : null,
                };
                return new ToolResult(IsError: true, JsonSerializer.Serialize(payload, JsonOptions));
            }
            diagnostics = columnFiltered;

            // Which diagnostics get asked for their fixes. A line scope asks all of them, because the count of
            // same-titled siblings is what it reports back. A document scope asks exactly ONE — the fix is
            // identified to Roslyn by its equivalence key and fix-all finds the rest itself, so interrogating
            // every provider about every occurrence would be precisely the N calls this scope exists to remove.
            // First by position, so which occurrence seeds it is deterministic rather than analyzer-order.
            var seedDiagnostics = wholeDocument
                ? diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Take(1).ToList()
                : diagnostics;

            // Gather the offered fixes across the seed diagnostics. The PROVIDER is carried alongside because
            // fix-all is asked of the provider that offered the action, not of the action itself.
            var fixes = new List<(CodeAction Action, Diagnostic Diagnostic, CodeFixProvider Provider)>();
            foreach (var diagnostic in seedDiagnostics)
            {
                foreach (var provider in ProvidersFor(diagnostic.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellationToken);
                    try
                    {
                        await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue; // a provider that can't run headlessly shouldn't sink the whole call
                    }
                    // Flattened, because an action carrying NestedActions is a MENU and not a fix — see
                    // CodeFixActions.Applicable. Registered groups were being collected and offered as
                    // though they could be applied, and they cannot.
                    foreach (var action in actions)
                        foreach (var applicable in CodeFixActions.Applicable(action))
                            fixes.Add((applicable, diagnostic, provider));
                }
            }

            if (fixes.Count == 0)
            {
                var payload = new
                {
                    error = wholeDocument
                        ? $"no code fix is available for '{diagnosticId}' in '{file}' — it is reported {diagnostics.Count} " +
                          $"time{(diagnostics.Count == 1 ? "" : "s")} but no code-fix provider offers a fix for it"
                        : "no code fix is available for the diagnostic(s) on this line",
                    diagnostics = seedDiagnostics.Take(MaxFixChoicesListed).Select(d => new { id = d.Id, line = LineOf(d), column = ColumnOf(d), message = d.GetMessage() }),
                };
                return new ToolResult(IsError: true, JsonSerializer.Serialize(payload, JsonOptions));
            }

            // How many of the candidates are the SAME action as the one we end up applying. Two diagnostics
            // of one id on a line (e.g. IDE0055 at two columns) each register their own fix, identically
            // titled; applying one leaves the rest, so the caller is told to re-run rather than left to
            // infer it from an unchanged diagnostic count.
            var sameTitleSkipped = 0;
            List<int> sameTitleColumns = null;

            (CodeAction Action, Diagnostic Diagnostic, CodeFixProvider Provider) chosen;
            if (!string.IsNullOrEmpty(fixTitle))
            {
                var match = fixes.Where(f => string.Equals(f.Action.Title, fixTitle, StringComparison.Ordinal)).ToList();
                if (match.Count == 0)
                    return new ToolResult(IsError: true, JsonSerializer.Serialize(new
                    {
                        error = $"no offered fix titled '{fixTitle}'",
                        availableFixes = fixes.Take(MaxFixChoicesListed)
                            .Select(f => new { title = f.Action.Title, diagnosticId = f.Diagnostic.Id, column = ColumnOf(f.Diagnostic) }),
                    }, JsonOptions));
                chosen = match[0];
                sameTitleSkipped = match.Count - 1;
                sameTitleColumns = match.Skip(1).Select(f => ColumnOf(f.Diagnostic)).Distinct().OrderBy(c => c).ToList();
            }
            else if (fixes.Count == 1)
            {
                chosen = fixes[0];
            }
            else if (DistinctTitles(fixes).Count == 1)
            {
                // Every candidate is the SAME action, so there is nothing to choose between: listing them
                // would offer the caller N identical titles and 'fixTitle' — the only selector it has — can't
                // tell them apart. That's a dead end, not a safety rail (observed live: two IDE0055 "Fix
                // formatting" on one line). The ambiguity guard exists to stop us silently picking between
                // DIFFERENT actions ("add using" vs "generate method"); it has no work to do here. Apply the
                // first and report the rest as remaining.
                chosen = fixes[0];
                sameTitleSkipped = fixes.Count - 1;
                sameTitleColumns = fixes.Skip(1).Select(f => ColumnOf(f.Diagnostic)).Distinct().OrderBy(c => c).ToList();
            }
            else
            {
                // Genuinely different actions — refuse to guess and hand back the choices.
                return new ToolResult(IsError: true, JsonSerializer.Serialize(new
                {
                    error = $"{DistinctTitles(fixes).Count} different fixes apply here; re-issue with 'fixTitle' to choose one.",
                    availableFixes = fixes
                        .GroupBy(f => f.Action.Title, StringComparer.Ordinal)
                        .Take(MaxFixChoicesListed)
                        .Select(g => new
                        {
                            title = g.Key,
                            diagnosticId = g.First().Diagnostic.Id,
                            occurrences = g.Count(),
                            // Where each same-titled fix applies, so a group of identical titles is still
                            // readable — and targetable via 'column'.
                            columns = g.Select(x => ColumnOf(x.Diagnostic)).Distinct().OrderBy(c => c).ToList(),
                        }),
                }, JsonOptions));
            }

            // The seed's line/column, used by every message below. Read off the chosen diagnostic rather than
            // the request, which for a document scope may not carry a position at all.
            var seedLine = LineOf(chosen.Diagnostic);
            var seedColumn = ColumnOf(chosen.Diagnostic);

            // Widen the chosen action to every occurrence in the document, when asked and when the provider
            // supports it. This is the whole of issue #91: from GetOperationsAsync down, the two scopes are
            // the same code — only the CodeAction differs.
            //
            // Degradation is stated rather than silent. A provider returning null from GetFixAllProvider() is
            // ordinary (opting out is a supported choice), and so is fix-all failing to compute; either way
            // the single fix is still worth applying, but a caller that asked to fix the file and is told
            // "applied" would reasonably believe the file is done. So the reason is carried to the summary and
            // 'scopeApplied' reports what actually ran — never what was requested.
            var actionToApply = chosen.Action;
            var appliedScope = scope;
            string scopeDegradedReason = null;
            if (scope == CodeFixScope.Document)
            {
                var fixAllProvider = TryGetFixAllProvider(chosen.Provider);
                if (fixAllProvider is null)
                {
                    appliedScope = CodeFixScope.Line;
                    scopeDegradedReason = $"the code-fix provider for {chosen.Diagnostic.Id} does not support fix-all";
                }
                else
                {
                    CodeAction fixAllAction = null;
                    try
                    {
                        // EquivalenceKey, not Title, and ONLY here — see the remarks on this method for why
                        // the line scope must not follow suit (the key is not location-independent). It is
                        // the right key for this call because Roslyn matches it against actions it
                        // recomputes inside this one operation. It may legitimately be null — the batch
                        // fixer then matches the other null-keyed actions, which is exactly what VS does on
                        // the same action, so this reproduces the light bulb rather than second-guessing it.
                        // The ambiguity guard above has already refused the case where distinct fixes were
                        // on offer and none was chosen.
                        var fixAllContext = new FixAllContext(
                            document,
                            chosen.Provider,
                            FixAllScope.Document,
                            chosen.Action.EquivalenceKey,
                            new[] { chosen.Diagnostic.Id },
                            new DocumentDiagnosticProvider(chosen.Diagnostic.Id),
                            cancellationToken);
                        fixAllAction = await fixAllProvider.GetFixAsync(fixAllContext).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        scopeDegradedReason = $"computing the fix-all failed ({ex.GetType().Name}: {ex.Message})";
                    }

                    if (fixAllAction is null)
                    {
                        appliedScope = CodeFixScope.Line;
                        scopeDegradedReason ??= $"the fix-all provider for {chosen.Diagnostic.Id} produced no action";
                    }
                    else
                    {
                        actionToApply = fixAllAction;
                    }
                }
            }

            // Turn the chosen action into a changed solution.
            ImmutableArray<CodeActionOperation> operations;
            try
            {
                operations = await actionToApply.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Names the fix and the exception TYPE, not just Message. This path's real-world case was a
                // grouped action, whose NotSupportedException carries the offending .NET type name as its
                // whole message — so the agent was handed
                // "the fix could not be computed: Microsoft.CodeAnalysis.CodeActions.CodeAction+CodeActionWithNestedActions"
                // and had nothing to act on. That cause is gone (groups are flattened before we get here),
                // which is exactly why this needs to read well for whatever the NEXT one turns out to be.
                return new ToolResult(IsError: true, Json.Object(("error",
                    $"'{actionToApply.Title}' for {chosen.Diagnostic.Id} could not be computed " +
                    $"({ex.GetType().Name}: {ex.Message}), so nothing was applied.")));
            }

            var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (applyOp is null)
                return new ToolResult(IsError: true, Json.Object(("error", "the fix produced no applicable code changes")));

            var changedSolution = applyOp.ChangedSolution;
            var (fileCount, editCount, files) = await SummarizeSolutionChangesAsync(solution, changedSolution, cancellationToken).ConfigureAwait(false);

            // A fix can hand back a ChangedSolution IDENTICAL to the current one, and Roslyn applies that
            // perfectly happily — TryApplyChanges returns true for a no-op. Reported as success it becomes
            // an outright lie ("Applied 'Fix formatting' ... in 0 files (0 edits). Applied and saved in
            // Visual Studio") and, worse, a LOOP: the summary below also says "N more on this line offer
            // the same fix — re-run to apply the next one", so the agent re-runs, gets the same empty
            // success and the same advice, and never converges. Measured live 2026-08-04 on IDE0055: one
            // real fix, then two no-op "successes" reporting the identical remaining column, after which
            // the agent gave up on the tool and edited the file by hand.
            //
            // There are TWO causes and we cannot tell them apart from here, so the message must not claim
            // to. A stale diagnostic is one (the list was captured before an earlier fix that resolved
            // it). The other is a diagnostic whose own fix simply makes no edit: measured live 2026-08-04
            // on a real, current IDE0055 — a `};` sitting at column 1 on line 93 of EngineService.cs,
            // still reported by a get_diagnostics run AFTER the previous fix — whose 'Fix formatting'
            // action returned a solution identical to the input.
            //
            // That second one is NOT ours and must not be described as ours. Checked by hand in the IDE:
            // Visual Studio's own light bulb offers the same fix on that line and it does nothing there
            // either. So this path faithfully reproduces the light bulb, which is the contract this tool
            // is meant to have — the diagnostic is simply one Roslyn reports without being able to
            // resolve it. Two earlier versions of this message got that wrong, first asserting staleness
            // and then blaming programmatic invocation; both sent the caller somewhere useless. It now
            // states the observation and offers the escape hatch, which is the only thing that works.
            //
            // Structural changes are checked separately because the counts above walk GetChangedDocuments()
            // only: a fix that ADDS a file (move type to a new file) legitimately reports zero edits and
            // must not be mistaken for one that did nothing.
            //
            // A document scope reaches here the same way and means the same thing — a fix-all whose every
            // occurrence is a no-op is still a no-op — so it takes the same refusal rather than reporting a
            // successful sweep of nothing.
            if (fileCount == 0 && editCount == 0 && !HasStructuralChanges(solution, changedSolution))
                return new ToolResult(IsError: true, Json.Object(("error",
                    $"'{chosen.Action.Title}' for {chosen.Diagnostic.Id} " +
                    (appliedScope == CodeFixScope.Document
                        ? $"across '{document.Name}' "
                        : $"at line {seedLine}, column {seedColumn} ") +
                    "produced no change, so nothing was applied. Either an earlier fix already resolved it, or this " +
                    "is a diagnostic Roslyn reports but its own fix does not resolve. Re-run get_diagnostics: " + "" +
                    "if it is gone, carry on; if it is still reported, edit the file directly — retrying this tool cannot help.")));

            // Apply through VS on the UI thread (editor + undo stack), like rename_symbol.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            bool applied;
            try
            {
                applied = Workspace.TryApplyChanges(changedSolution);
            }
            catch (Exception ex)
            {
                return new ToolResult(IsError: true, Json.Object(("error", $"could not apply the fix: {ex.Message}")));
            }
            if (!applied)
                return new ToolResult(IsError: true, Json.Object(("error", "could not apply the fix — the solution changed underneath it; re-read and try again")));

            // Save the touched buffers so disk matches the editor (still undoable — the undo stack survives).
            SaveChangedDocuments(solution, changedSolution);

            // The sameTitle counts gathered above describe the line as it was BEFORE the fix ran, and for
            // some fixes that is immediately wrong: one 'Fix formatting' reformats the whole line and
            // resolves every sibling IDE0055 on it. Reporting the stale count is what previously talked the
            // caller into re-running against diagnostics that no longer existed — it re-ran, changed
            // nothing, and was told it had succeeded. So re-ask the analyzers against the APPLIED solution
            // and report what is actually left; the number then means what it says and a re-run is safe to
            // act on.
            //
            // Only when there were siblings to begin with, so the common single-fix call never pays for a
            // second analysis pass. Off the UI thread — we are on it in order to apply, and this re-runs
            // the analyzers, which is exactly the work the first pass is kept off it for.
            //
            // For a document scope the re-check is NOT conditional. Fix-all is not guaranteed exhaustive —
            // a provider may decline occurrences it cannot merge — and "the file is done" is precisely the
            // claim a caller acts on without verifying. It is also the only useful report when the request
            // degraded to a single fix, where the leftovers are the whole story.
            List<int> sameTitleLines = null;
            if (sameTitleSkipped > 0 || scope == CodeFixScope.Document)
            {
                var appliedSolution = Workspace.CurrentSolution;
                await TaskScheduler.Default;
                var remaining = await CountSameTitleFixesAsync(
                        appliedSolution, document.Id, scope == CodeFixScope.Document ? (int?)null : line,
                        chosen.Action.Title, diagnosticId, cancellationToken)
                    .ConfigureAwait(false);
                sameTitleSkipped = remaining.Count;
                // Reported on the axis the scope works in: columns on one line, lines across a file.
                sameTitleColumns = sameTitleSkipped > 0 && scope == CodeFixScope.Line
                    ? remaining.Select(r => r.Column).Distinct().OrderBy(c => c).ToList()
                    : null;
                sameTitleLines = sameTitleSkipped > 0 && scope == CodeFixScope.Document
                    ? remaining.Select(r => r.Line).Distinct().OrderBy(l => l).ToList()
                    : null;
            }

            var appliedWhere = appliedScope == CodeFixScope.Document
                ? $"to every occurrence in '{document.Name}'"
                : $"at line {seedLine}, column {seedColumn}";
            // A degradation is stated in the SUMMARY, not left to a structured field: 'scopeApplied' is the
            // machine-readable half, but the prose is what gets weighted (see the isError/summary rule), and
            // a caller told only "Applied" after asking for the file would stop there.
            var degradedNote = scopeDegradedReason is null
                ? string.Empty
                : $" NOTE: scope 'document' was requested but {scopeDegradedReason}, so only this one occurrence was fixed.";
            var remainingNote = sameTitleSkipped == 0
                ? string.Empty
                // Re-measured against the applied solution above, so this can be stated plainly: these
                // diagnostics were re-checked AFTER the fix landed and still offer it. Siblings the fix
                // resolved on its way past have already dropped out, so a re-run is safe to act on rather
                // than a guess.
                : scope == CodeFixScope.Document
                    ? $" {sameTitleSkipped} occurrence{(sameTitleSkipped == 1 ? "" : "s")} in the file still offer{(sameTitleSkipped == 1 ? "s" : "")} " +
                      $"the same fix (line{(sameTitleLines.Count == 1 ? "" : "s")} {string.Join(", ", sameTitleLines)}) — re-run to continue."
                    : $" {sameTitleSkipped} more diagnostic{(sameTitleSkipped == 1 ? "" : "s")} on this line still offer{(sameTitleSkipped == 1 ? "s" : "")} " +
                      $"the same fix (column{(sameTitleColumns.Count == 1 ? "" : "s")} {string.Join(", ", sameTitleColumns)}) — " +
                      $"re-run to apply the next one.";

            var result = new
            {
                summary = $"Applied '{chosen.Action.Title}' for {chosen.Diagnostic.Id} {appliedWhere} — " +
                          $"{editCount} edit{(editCount == 1 ? "" : "s")} in {fileCount} file{(fileCount == 1 ? "" : "s")}. " +
                          "Applied and saved in Visual Studio — undo with Ctrl+Z." + degradedNote + remainingNote,
                applied = true,
                // What actually ran, never what was asked for — the two differ whenever a provider opts out
                // of fix-all, and only this field says which happened.
                scopeApplied = appliedScope == CodeFixScope.Document ? "document" : "line",
                scopeDegradedReason,
                diagnostic = new { id = chosen.Diagnostic.Id, line = seedLine, column = seedColumn, message = chosen.Diagnostic.GetMessage() },
                fix = chosen.Action.Title,
                // Omitted (JsonIgnoreCondition.WhenWritingNull) when nothing is left — their presence is the
                // signal that a re-run is worthwhile, on whichever axis the scope works in.
                sameFixRemaining = sameTitleSkipped > 0 ? sameTitleSkipped : (int?)null,
                sameFixRemainingColumns = sameTitleColumns,
                sameFixRemainingLines = sameTitleLines,
                filesChanged = fileCount,
                editCount,
                truncatedFiles = files.Count >= MaxRenameFilesListed,
                files,
            };
            return new ToolResult(IsError: false, JsonSerializer.Serialize(result, JsonOptions));
        }

        /// <summary>
        /// The diagnostic's 1-based start column, matching what get_diagnostics/build_solution report — the
        /// axis that separates two same-id diagnostics on one line. Not a complete discriminator on its own:
        /// a pair can share a start and differ only in END column (the issue-#47 CS8602s did), which is why
        /// it narrows rather than being required.
        /// </summary>
        private static int ColumnOf(Diagnostic diagnostic) =>
            diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1;

        /// <summary>The diagnostic's 1-based line — the axis a document-wide fix reports leftovers on.</summary>
        private static int LineOf(Diagnostic diagnostic) =>
            diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;

        /// <summary>
        /// The provider's fix-all engine, or null when it opts out (which is ordinary and supported).
        /// Guarded like <see cref="ProvidersFor"/> — a provider that throws on the way is skipped, not fatal.
        /// </summary>
        private static FixAllProvider TryGetFixAllProvider(CodeFixProvider provider)
        {
            try { return provider.GetFixAllProvider(); }
            catch { return null; }
        }

        /// <summary>
        /// Feeds Roslyn's fix-all engine the same diagnostics this tool reads itself — compiler diagnostics
        /// from the semantic model plus the best-effort analyzer pass, including the VS-hosted IDE style
        /// analyzers that live in no <c>AnalyzerReference</c>. A <see cref="FixAllContext"/> cannot be
        /// constructed without one, and supplying VS's own would mean going through its (internal) diagnostic
        /// service — so this keeps fix-all reading exactly what the line scope reads, and a diagnostic the
        /// tool can fix one at a time is one fix-all can see.
        /// </summary>
        /// <remarks>
        /// Only <see cref="GetDocumentDiagnosticsAsync"/> is reachable: the context is constructed at
        /// <see cref="FixAllScope.Document"/> and nowhere else, so the project-wide members are never called.
        /// They answer empty rather than throwing, so a stray call cannot sink a fix that would otherwise
        /// work — but WIDENING the scope means implementing them first, because an empty answer there fixes
        /// nothing while still reporting success, which is the one outcome the tool's result contract forbids.
        /// </remarks>
        private sealed class DocumentDiagnosticProvider : FixAllContext.DiagnosticProvider
        {
            private readonly string _diagnosticId;

            public DocumentDiagnosticProvider(string diagnosticId) => _diagnosticId = diagnosticId;

            public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(CodeDocument document, CancellationToken cancellationToken)
            {
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var all = await GetDiagnosticsAtAsync(document, new TextSpan(0, text.Length), _diagnosticId, cancellationToken)
                    .ConfigureAwait(false);
                return all.Where(d => string.Equals(d.Id, _diagnosticId, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Microsoft.CodeAnalysis.Project project, CancellationToken cancellationToken) =>
                Task.FromResult(Enumerable.Empty<Diagnostic>());

            public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Microsoft.CodeAnalysis.Project project, CancellationToken cancellationToken) =>
                Task.FromResult(Enumerable.Empty<Diagnostic>());
        }

        /// <summary>The distinct fix titles among the candidates — what the caller can actually choose between.</summary>
        private static List<string> DistinctTitles(List<(CodeAction Action, Diagnostic Diagnostic, CodeFixProvider Provider)> fixes) =>
            fixes.Select(f => f.Action.Title).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>
        /// Re-asks the analyzers which diagnostics still offer a fix titled <paramref name="fixTitle"/>,
        /// against the solution snapshot given (i.e. after an apply). Scoped to <paramref name="line"/>, or
        /// to the whole document when it is null. Returns their 1-based positions, empty when none are left.
        /// </summary>
        /// <remarks>
        /// This exists so <c>sameFixRemaining</c> is a measurement rather than an assumption. The gather
        /// before the apply cannot know which siblings the applied fix took with it, and for formatting
        /// fixes it takes all of them — so the pre-apply count is not merely imprecise, it is reliably
        /// wrong in the commonest multi-fix case. Counted per DIAGNOSTIC (one position each) rather than per
        /// registered action, matching what the reported list means.
        /// <para>
        /// A line scope carries the line number over from the request. A fix that changes the line COUNT
        /// above itself would move the remaining diagnostics off it and they would go unreported — accepted:
        /// silence costs one extra get_diagnostics, whereas the reverse error (naming work that is already
        /// done) is the loop this replaced. The document scope has no such blind spot, which is one reason
        /// its re-check runs unconditionally.
        /// </para>
        /// </remarks>
        private async Task<List<(int Line, int Column)>> CountSameTitleFixesAsync(
            CodeSolution solution, DocumentId documentId, int? line, string fixTitle, string diagnosticId,
            CancellationToken cancellationToken)
        {
            var found = new List<(int Line, int Column)>();

            var document = solution.GetDocument(documentId);
            if (document is null)
                return found;

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (line is int only && (only < 1 || only > text.Lines.Count))
                return found;
            var span = line is int at ? text.Lines[at - 1].Span : new TextSpan(0, text.Length);

            var diagnostics = await GetDiagnosticsAtAsync(document, span, diagnosticId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(diagnosticId))
                diagnostics = diagnostics.Where(d => string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var diagnostic in diagnostics)
            {
                foreach (var provider in ProvidersFor(diagnostic.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellationToken);
                    try
                    {
                        await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue; // same tolerance as the first pass: a provider that can't run headlessly is skipped
                    }

                    if (actions.Any(a => string.Equals(a.Title, fixTitle, StringComparison.Ordinal)))
                    {
                        found.Add((LineOf(diagnostic), ColumnOf(diagnostic)));
                        break; // this diagnostic counts once, however many providers offer the title
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Explains an EMPTY semantic scope, or null when projects are loaded and a nil result therefore
        /// means what it says. Serialized onto the semantic results, where <c>JsonOptions</c> omits it
        /// whenever it is null.
        /// </summary>
        /// <remarks>
        /// "No matches" and "nothing was searched" are the same empty list, and only one of them is an
        /// answer. With no solution open the empty result is TRUE — the semantic scope really does hold
        /// no symbols — so this is a note rather than an error; turning it into a failure would assert a
        /// problem that does not exist. The case that actually misleads is the other one: a solution IS
        /// open but its projects have not finished loading, so the same empty list means "ask again in a
        /// moment", and an agent reading it as "this type does not exist" is transiently, unreproducibly
        /// wrong — the worst kind, since the identical call succeeds a minute later with nothing to
        /// explain the difference.
        /// <para>
        /// The two are told apart by the Roslyn solution's own FilePath rather than by asking DTE, which
        /// would need the UI thread — these tools deliberately run off it.
        /// </para>
        /// </remarks>
        private string WorkspaceScopeNote()
        {
            var solution = Workspace?.CurrentSolution;
            if (solution is null || solution.ProjectIds.Count > 0)
                return null;

            return string.IsNullOrEmpty(solution.FilePath)
                ? "No solution or folder is open, so there was nothing to search: this result describes an empty " +
                  "semantic scope, not the absence of the symbol from the code."
                // Deliberately does NOT assert which. A solution still opening and one that genuinely holds
                // no projects are indistinguishable from here — both are a FilePath with zero ProjectIds —
                // and telling an empty solution to "retry shortly" promises something that will never
                // happen. Separating them would mean asking DTE for the load state, on the UI thread these
                // tools exist to stay off.
                : "A solution is open but no projects are loaded, so there was nothing to search — either it is " +
                  "still opening, or it contains no projects. If it is still opening, the same call will " +
                  "succeed shortly.";
        }

        /// <summary>Providers whose fixable ids include the given diagnostic id (guarded — a throwing provider is skipped).</summary>
        private IEnumerable<CodeFixProvider> ProvidersFor(string diagnosticId)
        {
            foreach (var provider in CodeFixProviders)
            {
                bool matches;
                try { matches = provider.FixableDiagnosticIds.Contains(diagnosticId); }
                catch { matches = false; }
                if (matches)
                    yield return provider;
            }
        }

        /// <summary>
        /// Diagnostics overlapping the line: compiler diagnostics from the semantic model (reliable) plus,
        /// best-effort, analyzer diagnostics — the project's analyzer references AND the IDE-hosted style
        /// analyzers (IDE####), which VS keeps outside <c>AnalyzerReferences</c> (span-scoped, semantic +
        /// syntax passes). Hidden diagnostics are dropped unless <paramref name="requestedId"/> names them
        /// explicitly (IDE style rules like IDE0005 default to hidden severity yet still carry a fix).
        /// Never throws — the analyzer pass is wrapped since some analyzers can't run headlessly here.
        /// </summary>
        private static async Task<List<Diagnostic>> GetDiagnosticsAtAsync(CodeDocument document, TextSpan lineSpan, string requestedId, CancellationToken cancellationToken)
        {
            var result = new List<Diagnostic>();

            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
                return result;

            bool Keep(Diagnostic d) =>
                (d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden
                    || (requestedId is not null && string.Equals(d.Id, requestedId, StringComparison.OrdinalIgnoreCase)))
                && d.Location.IsInSource
                && d.Location.SourceSpan.IntersectsWith(lineSpan);

            result.AddRange(model.GetDiagnostics(lineSpan, cancellationToken).Where(Keep));

            try
            {
                var analyzers = CollectFixableAnalyzers(document.Project);
                if (!analyzers.IsEmpty)
                {
                    var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                    if (compilation is not null)
                    {
                        var withAnalyzers = compilation.WithAnalyzers(analyzers, document.Project.AnalyzerOptions);
                        var semantic = await withAnalyzers
                            .GetAnalyzerSemanticDiagnosticsAsync(model, lineSpan, cancellationToken).ConfigureAwait(false);
                        var syntax = await withAnalyzers
                            .GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancellationToken).ConfigureAwait(false);
                        result.AddRange(semantic.Concat(syntax).Where(Keep));
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Analyzer diagnostics are best-effort; compiler diagnostics still stand.
            }

            // The same rule can be registered twice (e.g. SDK NetAnalyzers + VS's hosted copy) — a duplicate
            // diagnostic would fabricate a false "N fixes apply" ambiguity, so dedupe by (id, span).
            return result
                .GroupBy(d => (d.Id, d.Location.SourceSpan))
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Every analyzer that can produce a fixable diagnostic for the project: its own analyzer references,
        /// any solution-level (host-registered) references, and the IDE style analyzers reflected out of the
        /// Roslyn Features assemblies devenv has already loaded — VS hosts those separately, so they appear in
        /// no <c>AnalyzerReferences</c> at all. Deduped by analyzer type.
        /// </summary>
        private static ImmutableArray<DiagnosticAnalyzer> CollectFixableAnalyzers(Microsoft.CodeAnalysis.Project project)
        {
            var seen = new HashSet<Type>();
            var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

            foreach (var analyzer in project.AnalyzerReferences.SelectMany(r => SafeGetAnalyzers(r, project.Language)))
                if (seen.Add(analyzer.GetType()))
                    builder.Add(analyzer);
            foreach (var analyzer in project.Solution.AnalyzerReferences.SelectMany(r => SafeGetAnalyzers(r, project.Language)))
                if (seen.Add(analyzer.GetType()))
                    builder.Add(analyzer);
            foreach (var analyzer in GetIdeAnalyzers(project.Language))
                if (seen.Add(analyzer.GetType()))
                    builder.Add(analyzer);

            return builder.ToImmutable();
        }

        private static IEnumerable<DiagnosticAnalyzer> SafeGetAnalyzers(AnalyzerReference reference, string language)
        {
            try { return reference.GetAnalyzers(language); }
            catch { return Enumerable.Empty<DiagnosticAnalyzer>(); }
        }

        private static readonly object IdeAnalyzerLock = new object();
        private static Dictionary<string, ImmutableArray<DiagnosticAnalyzer>> _ideAnalyzersByLanguage;

        /// <summary>
        /// The IDE's own style analyzers (IDE####), instantiated from the Roslyn Features assemblies already
        /// loaded in devenv — we never load Roslyn assemblies ourselves (same no-BYO-Roslyn rule as the
        /// semantic tools; if the assemblies aren't loaded yet the scan just yields nothing). Cached per
        /// language for the devenv session. Skips <c>DocumentDiagnosticAnalyzer</c>s (an IDE-host-only
        /// execution model that yields nothing under <c>CompilationWithAnalyzers</c>); every instantiation is
        /// guarded so one bad analyzer never sinks discovery.
        /// </summary>
        private static ImmutableArray<DiagnosticAnalyzer> GetIdeAnalyzers(string language)
        {
            lock (IdeAnalyzerLock)
            {
                _ideAnalyzersByLanguage = _ideAnalyzersByLanguage ?? new Dictionary<string, ImmutableArray<DiagnosticAnalyzer>>(StringComparer.Ordinal);
                if (_ideAnalyzersByLanguage.TryGetValue(language, out var cached))
                    return cached;

                var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = assembly.GetName().Name; }
                    catch { continue; }
                    if (name != "Microsoft.CodeAnalysis.Features"
                        && name != "Microsoft.CodeAnalysis.CSharp.Features"
                        && name != "Microsoft.CodeAnalysis.VisualBasic.Features")
                        continue;

                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        try
                        {
                            if (type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
                                continue;
                            var attribute = type.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), inherit: false)
                                .OfType<DiagnosticAnalyzerAttribute>().FirstOrDefault();
                            if (attribute is null || !attribute.Languages.Contains(language))
                                continue;
                            if (DerivesFromByName(type, "DocumentDiagnosticAnalyzer"))
                                continue;
                            var ctor = type.GetConstructor(
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                                binder: null, Type.EmptyTypes, modifiers: null);
                            if (ctor is null)
                                continue;
                            builder.Add((DiagnosticAnalyzer)ctor.Invoke(null));
                        }
                        catch { /* one bad analyzer never sinks discovery */ }
                    }
                }

                var analyzers = builder.ToImmutable();
                _ideAnalyzersByLanguage[language] = analyzers;
                return analyzers;
            }
        }

        /// <summary>Whether a base type of the given name sits anywhere in the type's hierarchy (the type itself excluded).</summary>
        private static bool DerivesFromByName(Type type, string baseTypeName)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
                if (current.Name == baseTypeName)
                    return true;
            return false;
        }

        /// <summary>
        /// Finds the workspace document for an agent-supplied path, accepting three spellings in
        /// descending order of precision: exactly as given, rooted against the solution directory, and
        /// — for a relative path — a unique path-suffix match.
        /// </summary>
        /// <remarks>
        /// Exact match alone was not enough, and the gap was reachable rather than theoretical. Roslyn's
        /// <c>Document.FilePath</c> is always ABSOLUTE, while agents routinely write a workspace-relative
        /// path; the two tools then DISAGREED, which is the part that actually hurt. get_diagnostics
        /// accepts a relative file filter (<see cref="MatchesFileFilter"/>, which normalises separators
        /// and suffix-matches) and open_file roots a relative path against the solution directory — so an
        /// agent could FIND a diagnostic at a relative path and then be refused when it tried to FIX one
        /// at that same path. Measured live 2026-08-04: two apply_code_fix calls refused with "no
        /// document open in the solution for 'src\CodeWicket.Engine\EngineService.cs'" before the
        /// agent worked out to pass an absolute path. The rule here is deliberately the same one
        /// MatchesFileFilter uses, so the pair now accept the same spellings.
        /// <para>
        /// The suffix match must be UNIQUE. A typical solution has the same leaf name in several projects
        /// and applying a fix to the wrong file is far worse than refusing one. Note the uniqueness test
        /// compares FILE PATHS, not documents: a multi-targeted project (this solution has several)
        /// surfaces one file as a separate document per target framework, and those must not read as
        /// ambiguous.
        /// </para>
        /// </remarks>
        /// <param name="ambiguousMatches">
        /// The files a relative path matched when it matched more than one — empty otherwise. Refusing is
        /// right, but refusing SILENTLY was not: the caller was told "no document open in the solution for
        /// 'Program.cs'" when the truth was that two were found, so the only actionable fact (which files,
        /// hence what to say instead) was computed and then discarded. Measured live 2026-08-04.
        /// </param>
        private static CodeDocument ResolveDocument(CodeSolution solution, string filePath, out IReadOnlyList<string> ambiguousMatches)
        {
            ambiguousMatches = Array.Empty<string>();

            // Roslyn's own path index first — an O(1) answer for the common case (an absolute path, which
            // is what the agent sends most of the time) before the walk below.
            if (FindByExactPath(solution, filePath) is { } exact)
                return exact;

            // The rule itself lives in Core so it is unit-testable without devenv and so this and
            // get_diagnostics' filter provably share it. Roslyn's contribution is the candidate set;
            // everything after that is string work.
            var byPath = new List<(string Path, CodeDocument Document)>();
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (!string.IsNullOrEmpty(document.FilePath))
                        byPath.Add((document.FilePath, document));
                }
            }

            var resolution = Core.Ide.FilePathMatch.Resolve(
                byPath.Select(p => p.Path), filePath, Path.GetDirectoryName(solution.FilePath ?? string.Empty));

            if (resolution.IsAmbiguous)
            {
                ambiguousMatches = resolution.AmbiguousMatches;
                return null;
            }

            if (resolution.Path is null)
                return null;

            foreach (var (path, document) in byPath)
            {
                if (string.Equals(path, resolution.Path, StringComparison.OrdinalIgnoreCase))
                    return document;
            }

            return null;
        }

        /// <summary>The original lookup: by path index, then a case-insensitive scan.</summary>
        private static CodeDocument FindByExactPath(CodeSolution solution, string filePath)
        {
            var ids = solution.GetDocumentIdsWithFilePath(filePath);
            if (ids.Length > 0)
                return solution.GetDocument(ids[0]);

            foreach (var project in solution.Projects)
                foreach (var document in project.Documents)
                    if (string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        return document;

            return null;
        }

        /// <summary>Reads the 'file'/'line' (required) and 'column'/'diagnosticId'/'fixTitle' (optional) arguments.</summary>
        private static (string File, int Line, int Column, string DiagnosticId, string FixTitle, string Scope) ReadFixArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return (null, -1, 0, null, null, null);
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, -1, 0, null, null, null);

            var file = root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var line = root.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var li) ? li : -1;
            var column = root.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : 0;
            var id = root.TryGetProperty("diagnosticId", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var title = root.TryGetProperty("fixTitle", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var scope = root.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (file, line, column, id, title, scope);
        }

        /// <summary>
        /// Resolves declared symbols across the solution whose (optionally qualified) name matches the query.
        /// <paramref name="sourceOnly"/> true keeps only source-declared symbols (find_references — there is
        /// nothing to reference-search in metadata); false also returns metadata symbols from referenced
        /// assemblies/the BCL (find_symbol — that's its whole point).
        /// </summary>
        private static async Task<List<ISymbol>> ResolveSymbolsAsync(CodeSolution solution, string query, bool sourceOnly, CancellationToken cancellationToken)
        {
            var dot = query.LastIndexOf('.');
            var name = dot >= 0 && dot < query.Length - 1 ? query.Substring(dot + 1) : query;

            var results = new List<ISymbol>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IEnumerable<ISymbol> found;
                try
                {
                    found = await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!(cancellationToken.IsCancellationRequested))
                {
                    continue; // a project that can't be analyzed shouldn't abort the whole search
                }

                foreach (var symbol in found)
                {
                    if (sourceOnly && !symbol.Locations.Any(l => l.IsInSource))
                        continue; // find_references: skip metadata like System.String
                    if (!QualifierMatches(symbol, query))
                        continue;
                    if (seen.Add(SymbolKey(symbol)))
                        results.Add(symbol);
                }
            }

            return results;
        }

        /// <summary>Collects the distinct source reference locations for one symbol (capped, with the source line text).</summary>
        private static async Task<List<object>> CollectReferencesAsync(CodeSolution solution, ISymbol symbol, CancellationToken cancellationToken)
        {
            var hits = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            IEnumerable<ReferencedSymbol> found;
            try
            {
                found = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!(cancellationToken.IsCancellationRequested))
            {
                return hits;
            }

            foreach (var referenced in found)
            {
                foreach (var reference in referenced.Locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var location = reference.Location;
                    if (location is null || !location.IsInSource)
                        continue;

                    var span = location.GetLineSpan();
                    var key = span.Path + "|" + span.StartLinePosition.Line + "|" + span.StartLinePosition.Character;
                    if (!seen.Add(key))
                        continue;

                    hits.Add(new
                    {
                        file = span.Path,
                        line = span.StartLinePosition.Line + 1,   // 1-based for readability
                        column = span.StartLinePosition.Character + 1,
                        text = GetSourceLine(location),
                    });
                    if (hits.Count >= MaxReferencesPerSymbol)
                        return hits;
                }
            }

            return hits;
        }

        /// <summary>A dotted qualifier in the query must match the symbol's namespace/type path as a whole-segment suffix.</summary>
        private static bool QualifierMatches(ISymbol symbol, string query)
        {
            if (query.IndexOf('.') < 0)
                return true;
            var qualified = QualifiedName(symbol);
            return qualified.Equals(query, StringComparison.Ordinal)
                || qualified.EndsWith("." + query, StringComparison.Ordinal);
        }

        /// <summary>Namespace.Type.Member path (no parameter lists), for suffix-matching a dotted query.</summary>
        private static string QualifiedName(ISymbol symbol)
        {
            var names = new Stack<string>();
            names.Push(symbol.Name);
            for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
                names.Push(type.Name);
            for (var ns = symbol.ContainingNamespace; ns is not null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
                names.Push(ns.Name);
            return string.Join(".", names);
        }

        /// <summary>
        /// Stable dedup key for a symbol that surfaces from multiple projects' compilations. The fully
        /// qualified display (parameters included) keeps overloads distinct while collapsing the same
        /// symbol seen through different projects; the assembly identity separates a source symbol from a
        /// same-named metadata one.
        /// </summary>
        private static string SymbolKey(ISymbol symbol)
        {
            // NOTE: SymbolDisplayFormat.FullyQualifiedFormat qualifies TYPES but NOT members (its
            // memberOptions is empty), so two overrides of the same method in different types both render
            // as bare "ATestMethod()" and collide — which silently collapsed multiple overrides/
            // implementations down to one (in Visual Studio, 2026-07-06). The documentation-comment id is the canonical,
            // fully member-qualified, per-symbol identity (e.g. "M:Test.BTestClass.ATestMethod" — includes
            // namespace, containing type and parameter types), so it keeps distinct symbols distinct and
            // overloads separate. Fall back to the display string for the rare symbol without a doc id.
            var definition = symbol.OriginalDefinition ?? symbol;
            var id = definition.GetDocumentationCommentId();
            var body = string.IsNullOrEmpty(id)
                ? definition.Kind + "|" + definition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : id;
            return body + "|" + (definition.ContainingAssembly?.Identity.Name ?? string.Empty);
        }

        private static object LocationJson(FileLinePositionSpan span) => new
        {
            file = span.Path,
            line = span.StartLinePosition.Line + 1,
            column = span.StartLinePosition.Character + 1,
        };

        /// <summary>The trimmed, length-capped source line that contains the given location.</summary>
        private static string GetSourceLine(Location location)
        {
            try
            {
                var text = location.SourceTree?.GetText();
                if (text is null)
                    return string.Empty;
                var lineIndex = location.GetLineSpan().StartLinePosition.Line;
                if (lineIndex < 0 || lineIndex >= text.Lines.Count)
                    return string.Empty;
                var line = text.Lines[lineIndex].ToString().Trim();
                return line.Length > MaxSourceLineLength ? line.Substring(0, MaxSourceLineLength) : line;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Reads the required 'symbol' string from the tool arguments (STJ, as proven on net472 in AcpAgentProbe).</summary>
        private static string ReadSymbolArgument(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("symbol", out var s)
                && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
        }

        /// <summary>
        /// Minimal JSON encoding. Avoids a System.Text.Json / Newtonsoft dependency in this in-proc
        /// VSIX assembly (where assembly-binding conflicts with VS's own copies are a real hazard).
        /// </summary>
        private static class Json
        {
            public static string Object(params (string Key, string Value)[] members)
            {
                var sb = new StringBuilder();
                sb.Append('{');
                for (var i = 0; i < members.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(String(members[i].Key)).Append(':').Append(String(members[i].Value));
                }
                sb.Append('}');
                return sb.ToString();
            }

            public static string String(string value)
            {
                if (value is null)
                    return "\"\"";

                var sb = new StringBuilder(value.Length + 2);
                sb.Append('"');
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
                return sb.ToString();
            }
        }
    }
}
