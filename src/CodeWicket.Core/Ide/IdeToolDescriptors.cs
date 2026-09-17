namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// What the agent is TOLD about each IDE tool: its name, description, input schema and permission tier.
    /// The Visual Studio catalog (<c>VsToolCatalog</c>) registers these and owns the handlers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In Core because the text is behaviour and needs a test.</b> The catalog is net472 + VS SDK and no
    /// test project can reference it, so while the descriptors lived there nothing could hold them to the
    /// one limit that matters: Claude Code cuts an MCP tool description at 2KB (changelog 2.1.84), SILENTLY.
    /// Three were over it when that was checked (2026-09-13) — <c>get_diagnostics</c> lost its last 786
    /// characters, the whole of the passage explaining why its build and IntelliSense counts differ — and
    /// the lost text read correctly in every
    /// review, because nothing a reviewer looks at is truncated. <c>IdeToolDescriptorTests</c> is the guard.
    /// </para>
    /// <para>
    /// <b>A cap is named by the field that reports it, not by a number</b>, unless the number is a Core
    /// constant concatenated in. The same review found descriptions stating limits and behaviours the
    /// handlers had since changed; a field name cannot drift from the payload the way a copied figure does.
    /// </para>
    /// <para>
    /// <b>The first sentence is also the <c>&lt;ide-tools&gt;</c> hint</b> (<c>AcpAgentSession.Summarize</c>),
    /// so it must stand alone and stay within 200 characters.
    /// </para>
    /// </remarks>
    public static class IdeToolDescriptors
    {
        /// <summary>Claude Code's cap on an MCP tool description, in UTF-8 bytes. Text past it never reaches the model.</summary>
        public const int MaxDescriptionBytes = 2048;

        // --- Shared breakpoint text. Declared FIRST: static fields initialize in textual order, and the
        // two breakpoint descriptors below read these.

        /// <summary>
        /// The tail both breakpoint-setting descriptions carry: what a batch costs, that breakpoints
        /// outlive devenv, the user's-breakpoint conflict, and what NOT to say once they are placed.
        /// </summary>
        /// <remarks>
        /// Shared rather than repeated because the two tools differ in exactly one thing — whether they
        /// may carry an expression — and a paragraph that drifted between them would advertise a second
        /// difference that does not exist.
        /// </remarks>
        private static readonly string BreakpointBatchAdvice =
            "Set related breakpoints in one call — one approval, at most " + Breakpoints.MaxPerCall + ". They persist " +
            "across a Visual Studio restart and are tagged as yours for list_breakpoints and clear_breakpoints. A " +
            "line already holding a breakpoint the USER set returns status 'conflict' and theirs is untouched: choose " +
            "another line or ask them. AFTERWARDS, DO NOT LIST THEM BACK: the chat already shows the user a card " +
            "with every breakpoint, its condition and your reason. Say instead what the card cannot — what to run, " +
            "what to watch for, why the counting works out, which of two stops will hit first.";

        /// <summary>The row properties both breakpoint schemas share; only the expression pair differs.</summary>
        private const string BreakpointItemProperties =
            "\"file\":{\"type\":\"string\",\"description\":\"Source file: absolute, or relative to your working directory.\"}," +
            "\"line\":{\"type\":\"integer\",\"description\":\"1-based line. Must be an executable statement — a blank line, a comment or a declaration will not bind.\"}," +
            "\"hitCount\":{\"type\":\"integer\",\"description\":\"Optional. Stop only on the Nth hit (1 or more). Omit to stop every time.\"}," +
            "\"reason\":{\"type\":\"string\",\"description\":\"Why this line is worth stopping at, in your own words. Shown to the user.\"}";

        // --- Build, diagnostics, tests ---------------------------------------------------------------

        public static readonly ToolDescriptor BuildSolution = new ToolDescriptor(
            "build_solution",
            "Build the loaded Visual Studio solution exactly as the IDE builds it (active configuration/platform, " +
            "project references) and report whether it succeeded, with the build's own errors. Prefer this over " +
            "running 'dotnet build' yourself: it matches what the user sees, and it reports errors the live editor " +
            "cannot see (build-generated code, NuGet/MSBuild/SDK), so it is the authoritative check that the code " +
            "compiles. Incremental by default; rebuild:true forces Clean+Rebuild. project:<Solution Explorer name> " +
            "builds one project and scopes the errors and warnings to it. Returns succeeded, errorCount, " +
            "warningCount, errorsByCode, warningsByCode and the first errors (file, line, column, code, message); " +
            "includeWarnings:true itemizes warnings too. A failed build that left no error rows carries " +
            "'buildOutput', the tail of the Build pane, instead — read it, since errorCount can then be 0. " +
            "cancelled:true means the user cancelled the build: nothing about the code was decided, so do not " +
            "retry unless they ask. If the result carries a 'note', read it FIRST: it names files you wrote that " +
            "this build did not compile (a file in no loaded project, or a project file or import the loaded " +
            "project has not taken yet) and says what does and does not fix that — a succeeded build with a note " +
            "says nothing about those files. To fix warnings, including analyzer suggestions the build does not " +
            "emit, use get_diagnostics with apply_code_fix; its live counts need not match this build's.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"rebuild\":{\"type\":\"boolean\",\"description\":\"Clean+Rebuild instead of an incremental build (slower). Defaults to false.\"}," +
            "\"project\":{\"type\":\"string\",\"description\":\"Build only this project, by its Solution Explorer display name. With rebuild:true the clean still covers the whole solution; only this project rebuilds.\"}," +
            "\"includeWarnings\":{\"type\":\"boolean\",\"description\":\"Also itemize warnings (file, line, column, code, message), capped like errors; 'truncatedWarnings' says when more exist. Defaults to false.\"}" +
            "},\"additionalProperties\":false}");

        public static readonly ToolDescriptor GetDiagnostics = new ToolDescriptor(
            "get_diagnostics",
            "Return the current errors and warnings for the solution — the LIVE view of the code as it stands now, " +
            "each with file, line, column, code, message and source. Use it after editing or building to see what " +
            "needs fixing. Expect its counts to differ from build_solution's, and neither is stale: this analyses " +
            "the current code, while build_solution reports what the last build produced (and an incremental build " +
            "only reports the projects it recompiled). 'scope': 'compiler' (default) is CS/BC diagnostics computed " +
            "live — fast, reproducible, never lagging an edit; 'analyzers' adds the projects' referenced analyzers " +
            "(CA, StyleCop); 'full' adds the Error List — editor-hosted analyzers such as SonarLint, IDE " +
            "suggestions, and build-only NU/NETSDK/MSB codes from the last build, which reflect editor state and " +
            "that build rather than the code now. 'compiler' and 'analyzers' can never show build-only codes and " +
            "are incomplete while a solution is still loading. Prefer the default. 'severity' is the minimum: " +
            "'error', 'warning' (default), 'info' or 'hidden'. 'file' filters by path: an absolute path is one " +
            "file, a relative one matches by suffix and may span projects, with a 'note' naming the files — " +
            "lengthen it to narrow. A 'note' also says when the filter matched no file at all: that result does " +
            "NOT mean the file is clean. IDE style rules (IDE####, such as IDE0005) are hidden by default and only " +
            "run for one file: pass 'file', severity 'hidden' and scope 'analyzers', then fix with apply_code_fix. " +
            "Compiler and analyzer rows carry 'endColumn' ('endLine' too when the span crosses lines): two rows " +
            "sharing file, line, code AND start column but ending differently are DISTINCT problems — fix both. " +
            "Rows are capped; 'total' and 'truncated' give the true count, 'countsByCode' the shape.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"scope\":{\"type\":\"string\",\"enum\":[\"compiler\",\"analyzers\",\"full\"],\"description\":\"'compiler' (default, fast and reproducible), 'analyzers' (+ referenced analyzers) or 'full' (+ the Error List: editor-hosted analyzers and build-only codes from the last build).\"}," +
            "\"severity\":{\"type\":\"string\",\"enum\":[\"error\",\"warning\",\"info\",\"hidden\"],\"description\":\"Minimum severity to include (default 'warning').\"}," +
            "\"file\":{\"type\":\"string\",\"description\":\"Restrict to one source file: an absolute path, or a relative one matched by path suffix. Required for IDE style rules to run.\"}" +
            "},\"additionalProperties\":false}");

        public static readonly ToolDescriptor RunTests = new ToolDescriptor(
            "run_tests",
            "Run the solution's tests and report the results: succeeded, the total/passed/failed/skipped counts, " +
            "and each failing test with its message, stack trace and source location. Prefer this over running " +
            "'dotnet test' yourself. The solution is built first (incrementally, in Visual Studio), so do not build " +
            "beforehand; if that build fails no tests run, and the result carries the build's own 'errors' or, when " +
            "it left none, 'buildOutput'. cancelled:true means the user cancelled that build — do not retry unless " +
            "they ask. Runs ALL tests by default. For a subset prefer filterMethod / filterClass / filterNamespace, " +
            "which are translated to each project's framework and so work in a solution that mixes frameworks; " +
            "'filter' is an escape hatch for an advanced expression. A failing test with testFile/testLine threw " +
            "somewhere other than the test itself (a shared assertion helper, a Reqnroll/SpecFlow step): read the " +
            "test too before concluding what broke. Skipped tests are named in 'skippedTests' with the reason they " +
            "did not run. Failing and skipped entries carry 'className' and 'project' beside 'name', because NUnit " +
            "and MSTest report a bare method name. 'resultsFiles' lists each project's TRX. Classic VSTest projects " +
            "run with 'dotnet test', Microsoft Testing Platform projects (xUnit v3 and others) with 'dotnet run', " +
            "and old-style .NET Framework projects with vstest.console.exe; a single *.runsettings at the solution " +
            "root is applied to the VSTest ones. Read 'notes' (or 'note', on a failed build) when present: a " +
            "project whose results could not be read is NOT in the counts, and a test file you wrote that no " +
            "loaded project contains is neither compiled nor run — the note names it and says what fixes that.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"filterMethod\":{\"type\":\"string\",\"description\":\"Optional. Run one test method by its fully-qualified name, e.g. 'Namespace.Class.Method' — build it from a result entry's 'className' + '.' + 'name' (xUnit's 'name' is already qualified). Exact match.\"}," +
            "\"filterClass\":{\"type\":\"string\",\"description\":\"Optional. Run all tests in a class, e.g. 'Namespace.Class'. VSTest projects run every test whose name contains 'Namespace.Class.'. xUnit v3 (MTP) matches the class exactly, nested classes excluded; append '*' to widen it there — only when every test project is xUnit v3, because on a VSTest project a '*' matches nothing.\"}," +
            "\"filterNamespace\":{\"type\":\"string\",\"description\":\"Optional. Run all tests under a namespace, e.g. 'Namespace'. VSTest projects include sub-namespaces. xUnit v3 (MTP) matches the namespace exactly; append '*' to include sub-namespaces there ('*' only at the start or end) — only when every test project is xUnit v3, because on a VSTest project a '*' matches nothing.\"}," +
            "\"filter\":{\"type\":\"string\",\"description\":\"Optional escape hatch: the bare filter EXPRESSION in each project's own dialect, never a command-line option. VSTest (classic, MSTest, NUnit, old-style): e.g. 'FullyQualifiedName~Namespace.Class&TestCategory=Fast' (escape a literal '(' or '&' with a backslash). xUnit v3 (MTP): a filter query, e.g. '/Namespace/Class/Method'. Applied per project, so avoid it in a mixed-framework solution. Refused: a value starting with '-' (or '/' on VSTest), or containing a double quote or a control character. Mutually exclusive with the structured filters.\"}" +
            "},\"additionalProperties\":false}");

        // --- Editor --------------------------------------------------------------------------------------

        public static readonly ToolDescriptor OpenFile = new ToolDescriptor(
            "open_file",
            "Open a file in the Visual Studio editor and bring it to the front for the user, optionally at a line. " +
            "Use it to show the user a file you are discussing or a place you changed; it does NOT return the " +
            "file's contents (read the file yourself for that). 'file' is an absolute path or one relative to your " +
            "working directory; 'line' (1-based) moves the caret there. Returns the path that was opened.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"file\":{\"type\":\"string\",\"description\":\"The file to open: absolute, or relative to your working directory.\"}," +
            "\"line\":{\"type\":\"integer\",\"description\":\"Optional 1-based line to move the caret to.\"}" +
            "},\"required\":[\"file\"],\"additionalProperties\":false}");

        // --- Breakpoints (issue #73) -----------------------------------------------------------------------

        public static readonly ToolDescriptor SetBreakpoint = new ToolDescriptor(
            Breakpoints.PlainToolName,
            "Place plain breakpoints in Visual Studio for the USER to debug with: a stop on a line, optionally only " +
            "on the Nth hit. You do not run the debugger; you decide where to stop, and they start debugging. Use this " +
            "instead of telling the user to add Console.WriteLine/print statements and rebuild — that changes the " +
            "code, costs a rebuild per question, and only answers what you thought to instrument. Say in 'reason' " +
            "what you expect to learn: it is shown beside the breakpoint, and a red dot they did not place says " +
            "nothing on its own. THIS TOOL TAKES NO EXPRESSIONS: a 'condition' or 'printMessage' is evaluated " +
            "inside the user's own running program, so it belongs to set_expression_breakpoint, which asks them " +
            "first. Sending either here is REFUSED and nothing is set — never quietly dropped, so a breakpoint this " +
            "tool reports is exactly the one you asked for. " +
            BreakpointBatchAdvice,
            "{\"type\":\"object\",\"properties\":{" +
            "\"breakpoints\":{\"type\":\"array\",\"description\":\"The breakpoints to place. Send related stops together rather than one call each.\",\"items\":{\"type\":\"object\",\"properties\":{" +
            BreakpointItemProperties +
            "},\"required\":[\"file\",\"line\"],\"additionalProperties\":false}}" +
            "},\"required\":[\"breakpoints\"],\"additionalProperties\":false}")
        {
            // A plain stop changes what happens the next time the user starts debugging and nothing else, so it stays where it has
            // always been: auto-approved at AcceptEdits. Everything that would RUN in their process lives
            // under the other name.
            Risk = ToolRisk.Edit,
        };

        /// <summary>
        /// The escalation, as a SEPARATE NAME rather than an argument on <see cref="SetBreakpoint"/> — for
        /// the reason, and by the measurement, recorded on <see cref="ExecuteExpression"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <c>condition</c> and a <c>printMessage</c> are not stored data. Visual Studio EVALUATES both in
        /// the debuggee when the line is reached — function evaluation, property getters, method calls — so
        /// an expression placed here runs inside the user's own process on their next debug run, and the tracepoint
        /// that carries it survives a devenv restart in the <c>.suo</c>. That is the capability
        /// <see cref="ExecuteExpression"/> charges a permission prompt for, reached at a different hour
        /// rather than a different tier.
        /// </para>
        /// <para>
        /// Resolving the tier from the arguments instead is not available, and the remark on
        /// <see cref="ExecuteExpression"/> is where that was measured: the permission request for an MCP tool
        /// arrives BEFORE the <c>tool_call</c> frame carrying them, so the policy would decide with nothing
        /// to read. Read it before reaching for argument-derived risk again.
        /// </para>
        /// <para>
        /// Both names run the same handler; only the tier and the description differ. Plain rows may ride
        /// along in a batch — one call, one prompt — but a batch with no expression in it belongs to
        /// <see cref="SetBreakpoint"/>, which costs the user nothing.
        /// </para>
        /// </remarks>
        public static readonly ToolDescriptor SetExpressionBreakpoint = new ToolDescriptor(
            Breakpoints.ExpressionToolName,
            "Place breakpoints that carry an EXPRESSION: a 'condition' deciding whether to stop, or a " +
            "'printMessage' that makes it a tracepoint. Otherwise identical to set_breakpoint — use that one when " +
            "there is no expression, because this one ASKS THE USER EVERY TIME: the debugger evaluates these " +
            "expressions inside their running program when the line is reached, so a getter or method call in one " +
            "executes in the process they are debugging, on their next debug run. A condition (e.g. 'i == 47') is " +
            "usually better than a plain stop the user must step through. PASS 'printMessage' TO MAKE A " +
            "TRACEPOINT: the message goes to the Output window and execution CONTINUES, so the user starts debugging " +
            "once and nobody steps — usually what you want for a loop. '{expression}' in the message is evaluated " +
            "at that line (e.g. 'i={i} count={order.Items.Count}'). Nothing can check those names before the " +
            "program runs: one not in scope prints 'error CS0103' in the Output window instead of failing here, so " +
            "use names you have read in that method. Prefer reading names over calling anything, and say in your " +
            "message what you are trying to learn — the user decides on your expressions. Give tracepoint " +
            "messages a distinctive prefix, then read them back yourself with get_debug_output once the user has " +
            "run. " +
            BreakpointBatchAdvice,
            "{\"type\":\"object\",\"properties\":{" +
            "\"breakpoints\":{\"type\":\"array\",\"description\":\"The breakpoints to place. Send related stops together rather than one call each; plain stops may ride along.\",\"items\":{\"type\":\"object\",\"properties\":{" +
            BreakpointItemProperties + "," +
            "\"condition\":{\"type\":\"string\",\"description\":\"Optional expression that must be true to stop, evaluated in the debuggee's scope at that line (e.g. 'order.Items.Count == 0').\"}," +
            "\"printMessage\":{\"type\":\"string\",\"description\":\"Optional. Makes this a TRACEPOINT: print this to the Output window and continue instead of stopping. '{expression}' is interpolated in scope at that line.\"}" +
            "},\"required\":[\"file\",\"line\"],\"additionalProperties\":false}}" +
            "},\"required\":[\"breakpoints\"],\"additionalProperties\":false}")
        {
            // The top tier: every mode below AcceptAll prompts, every time. That IS the gate — the same one
            // execute_expression carries, for the same capability.
            Risk = ToolRisk.Command,
        };

        public static readonly ToolDescriptor ListBreakpoints = new ToolDescriptor(
            "list_breakpoints",
            "List the breakpoints currently set in Visual Studio, and which of them YOU set. Each comes with its " +
            "location, condition, hit count, tracepoint message, whether it is enabled, and whether you set it in " +
            "this conversation or an earlier one. Use it to see what is in place before adding more, to check one of yours is still there, or to " +
            "find out what the user has set themselves. Pass 'file' to narrow it to one source file.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"file\":{\"type\":\"string\",\"description\":\"Optional. Only list breakpoints in this file (absolute, or relative to your working directory).\"}" +
            "},\"additionalProperties\":false}");

        public static readonly ToolDescriptor ClearBreakpoints = new ToolDescriptor(
            "clear_breakpoints",
            "Remove breakpoints that YOU set. The user's own are never touched: to remove one of those, ask them, " +
            "or point them at Debug > Windows > Breakpoints. \"The breakpoints you set\" means THIS conversation's, " +
            "which is the default — keep it unless the user has actually asked about earlier sessions. Only then " +
            "pass scope:\"all\", which also removes ones you set in EARLIER conversations (they survive a restart, " +
            "so a long investigation can leave a dozen behind). Call list_breakpoints first if you are unsure which " +
            "the user means. Pass 'file' to limit it to one source file. Returns how many were removed and how many " +
            "of yours remain, so you can tell the user what is left in their gutter.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"scope\":{\"type\":\"string\",\"enum\":[\"conversation\",\"all\"],\"description\":\"'conversation' (default) removes only what you set in this conversation; 'all' removes everything you have set in this solution.\"}," +
            "\"file\":{\"type\":\"string\",\"description\":\"Optional. Only remove breakpoints in this file (absolute, or relative to your working directory).\"}" +
            "},\"additionalProperties\":false}")
        { Risk = ToolRisk.Edit };

        // --- Debugger output and values ------------------------------------------------------------------

        public static readonly ToolDescriptor GetDebugOutput = new ToolDescriptor(
            "get_debug_output",
            "Read what the running program has written to Visual Studio's Debug output pane, including the " +
            "messages from any TRACEPOINT you set with set_expression_breakpoint. This is how you get your own " +
            "instrumentation back: set tracepoints, have the user start debugging, then read the values here yourself " +
            "instead of asking them to paste the Output window. The pane also holds the debugger's own chatter " +
            "(assembly loads, first-chance exceptions, the exit code), usually far bulkier than what you want, so " +
            "pass 'contains' to keep only matching lines — a distinctive prefix on your tracepoint messages makes " +
            "that exact. Reading is safe at any time: it does not touch the debuggee, and works while it is " +
            "running, stopped or finished. Only the most recent output is returned; 'truncated' says when there " +
            "was more. It reads THIS Visual Studio instance: no output with a 'summary' means no debug session has " +
            "written here (a program debugged from another instance is not visible), while a read that failed is " +
            "reported as an error, never as empty output.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"contains\":{\"type\":\"string\",\"description\":\"Optional. Keep only lines containing this text (case-insensitive), e.g. the distinctive prefix of your tracepoint messages.\"}," +
            "\"tailLines\":{\"type\":\"integer\",\"description\":\"Optional. Return only the last N lines (1 or more), after any 'contains' filter. Earlier output cannot be paged back to, so narrow with 'contains' rather than walking backwards.\"}" +
            "},\"additionalProperties\":false}");

        public static readonly ToolDescriptor ReadExpression = new ToolDescriptor(
            "read_expression",
            "Read values out of the program the user is stopped in, without re-running it. Use it when a " +
            "<debug-state> the user sent lacks something, or for a value you could not have known to ask for in " +
            "advance — instead of adding a tracepoint and costing the user another run per question. The debugger " +
            "must be STOPPED (at a breakpoint or an exception); if it is not, the result says so, and you should " +
            "set a breakpoint with set_breakpoint and ask the user to run. 'expressions' are evaluated in the " +
            "debuggee's own language at the chosen frame, as typed into the Watch window: locals, fields, 'this', " +
            "arithmetic, comparisons, casts and indexing all work (e.g. 'order.Items.Count', 'items[3]'). Send " +
            "related ones together, at most " + DebugEvalLimits.MaxExpressions + ". NOTHING IN YOUR EXPRESSION IS " +
            "ALLOWED TO RUN CODE: the debugger reads memory only, so a property getter or a method call (including " +
            "ToString()) is refused rather than executed, and the refusal says which — a getter that fills a cache " +
            "or bumps a counter would change the very state the user is looking at. An object comes back with its " +
            "fields expanded, usually what you wanted: read 'order' rather than calling 'order.ToString()'. If a " +
            "value is genuinely only reachable by running code, use execute_expression for that expression.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"expressions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"The expressions to evaluate, in the debuggee's language. Send related ones together.\"}," +
            "\"frame\":{\"type\":\"integer\",\"description\":\"Optional 1-based stack frame to evaluate in, numbered as this tool returns them (1 = innermost). Defaults to 1.\"}" +
            "},\"required\":[\"expressions\"],\"additionalProperties\":false}")
        {
            // Reading memory is genuinely read-only, and this name can only ever do that: the guard is not an
            // argument here, it is the tool.
            Risk = ToolRisk.ReadOnly,
        };

        /// <summary>
        /// The escalation, as a SEPARATE NAME rather than an argument — because the permission layer cannot
        /// see arguments in time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One tool with an <c>allowSideEffects</c> argument was the better surface and was built first. It
        /// cannot work: measured 2026-08-28, the permission request for an MCP tool arrives BEFORE the
        /// <c>tool_call</c> frame that carries the arguments, so the policy resolves risk with nothing to
        /// read — <c>[permission] prompt: … risk=Command, args=none</c> — while the banner fills the
        /// arguments in a moment later and looks, to anyone watching, as though they were there all along.
        /// </para>
        /// <para>
        /// The resolver's safe default then fired on the SAFE path: every guarded read prompted, even at
        /// AcceptEdits, which is the one thing that tool must not do. Falling back to
        /// <see cref="ToolRisk.ReadOnly"/> instead would have made the tier a gate in name only.
        /// </para>
        /// <para>
        /// A name is in the request from the start. So the tier rides the name, which the permission model
        /// already keys everything on — <c>_toolRisks</c>, <c>AllowedTools</c> rules, remember-by-tool — and
        /// needs no new plumbing at all.
        /// </para>
        /// <para>
        /// Both names run the same handler; only the tier and the description differ. The agent is told to
        /// reach for this one ONLY after the guarded tool has actually refused, so the prompt lands where the
        /// user can judge it: a named expression, in a process they are looking at.
        /// </para>
        /// </remarks>
        public static readonly ToolDescriptor ExecuteExpression = new ToolDescriptor(
            "execute_expression",
            "Evaluate an expression in the stopped program, ALLOWING IT TO RUN CODE — property getters, " +
            "ToString(), method calls. Otherwise identical to read_expression. USE IT ONLY AFTER read_expression " +
            "HAS REFUSED: it asks the user for permission every time, because running code inside the process they " +
            "are stopped in can change the very state they are investigating — a getter that fills a cache or " +
            "bumps a counter moves the bug out from under them, and no undo brings it back. Prefer expanding an " +
            "object with read_expression over calling ToString() on it; that usually answers the question. When " +
            "you do call this, say in your message what you are trying to learn, because the user sees your " +
            "expression and decides on it.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"expressions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"The expressions to evaluate. Only the ones that genuinely need code to run.\"}," +
            "\"frame\":{\"type\":\"integer\",\"description\":\"Optional 1-based stack frame, numbered as read_expression returns them. Defaults to 1.\"}" +
            "},\"required\":[\"expressions\"],\"additionalProperties\":false}")
        {
            // The top tier: every mode below AcceptAll prompts, every time. That IS the gate.
            Risk = ToolRisk.Command,
        };

        // --- Command line ----------------------------------------------------------------------------------

        public static readonly ToolDescriptor RunCommand = new ToolDescriptor(
            "run_command",
            "Run a command line in the Visual Studio Developer environment, where msbuild, vstest.console, sn, " +
            "signtool and the other VS developer tools are on PATH (a plain shell does not have them). Use it ONLY " +
            "for commands that need those tools — use your own shell for everything else. It runs via cmd.exe, so " +
            "use cmd syntax (chain with && or &, quote with double quotes — NOT PowerShell or bash syntax), in the " +
            "solution directory by default. Prefer commands that finish on their own: you cannot answer an " +
            "interactive prompt yourself, but the user can type answers into the IDE's terminal pane — the result's " +
            "notes say when they did, so never assume a prompt was auto-answered. Returns succeeded (exit code 0), " +
            "exitCode, stdout and stderr (merged into stdout when it ran on the terminal). Long output keeps its " +
            "BEGINNING and is truncated, with the full length noted: when what you need is at the end (msbuild's " +
            "errors), filter or redirect the output. Interpret a nonzero exit code by the command's own conventions " +
            "(e.g. 'choice' returns the selected option's index).",
            "{\"type\":\"object\",\"properties\":{" +
            "\"command\":{\"type\":\"string\",\"description\":\"The command line to run, e.g. 'msbuild MyProj.csproj -t:Build -v:m'.\"}," +
            "\"workingDirectory\":{\"type\":\"string\",\"description\":\"Optional. Absolute path; defaults to the solution directory, or the extension's default workspace when no solution is open.\"}," +
            "\"timeoutSeconds\":{\"type\":\"integer\",\"description\":\"Optional. Default 300, max 900; the process tree is killed on timeout.\"}" +
            "},\"required\":[\"command\"],\"additionalProperties\":false}")
        { Risk = ToolRisk.Command };

        // --- Semantic code tools (C#/VB, Visual Studio's live Roslyn workspace) ---------------------------

        public static readonly ToolDescriptor FindReferences = new ToolDescriptor(
            "find_references",
            "Find every usage of a C#/VB symbol declared in this solution's source — use this instead of grep for " +
            "the real usages of a method, type, property or field. Semantic, via Visual Studio's live model: precise " +
            "across projects and inheritance, where a text search matches comments, strings and unrelated " +
            "same-named symbols. 'symbol' is a simple name (e.g. 'ExecuteAsync') or a dotted namespace/type " +
            "qualifier to narrow it (e.g. 'MyType.ExecuteAsync'); a qualifier cannot pick out one overload, so " +
            "every overload of the name is reported. Symbols from NuGet packages or the .NET libraries are not " +
            "searched. Returns each matching symbol with its definition and each reference's file, line, column " +
            "and source line.",
            "{\"type\":\"object\",\"properties\":{\"symbol\":{\"type\":\"string\",\"description\":\"The symbol name to find references for; simple or dotted-qualified.\"}},\"required\":[\"symbol\"],\"additionalProperties\":false}");

        public static readonly ToolDescriptor FindSymbol = new ToolDescriptor(
            "find_symbol",
            "Find where a C#/VB symbol is declared, with its full signature — use this instead of grep/glob when " +
            "locating a type or member by name. It resolves symbols semantically via Visual Studio's live " +
            "workspace, so unlike a text search it also finds what you cannot grep: types and members from " +
            "referenced NuGet packages, project references and the .NET base class library (for example the exact " +
            "signature and overloads of a framework API). 'symbol' is a simple name (e.g. 'OrderProcessor') or a " +
            "dotted qualifier to narrow it (e.g. 'MyType.Execute' or 'System.Collections.Generic.List'). Returns " +
            "each match's signature, kind, namespace, containing type, XML-doc summary, base type and interfaces, " +
            "and either a source file/line/column or the defining assembly.",
            "{\"type\":\"object\",\"properties\":{\"symbol\":{\"type\":\"string\",\"description\":\"The symbol name to look up; simple or dotted-qualified.\"}},\"required\":[\"symbol\"],\"additionalProperties\":false}");

        public static readonly ToolDescriptor FindImplementations = new ToolDescriptor(
            "find_implementations",
            "Find what implements or extends a C#/VB symbol — the down-the-hierarchy view, via Visual Studio's " +
            "semantic model. An interface or interface member returns its implementations, a class its derived " +
            "classes (transitive), a virtual, abstract or override member its overrides; anything else (a struct, " +
            "an enum, a non-virtual member) returns relation 'none' with an empty list. More reliable than a text " +
            "search, which misses inherited, indirect and generic implementations. Use it to see the concrete " +
            "behavior behind an abstraction, or before changing an interface or base member to find every " +
            "implementation or override you must update. Pairs with find_references (call sites) and find_symbol " +
            "(declaration). 'symbol' is a simple name or dotted qualifier (e.g. 'IPaymentProcessor' or " +
            "'IPaymentProcessor.Charge'). Returns each match with its relation and the implementers' signatures and " +
            "locations.",
            "{\"type\":\"object\",\"properties\":{\"symbol\":{\"type\":\"string\",\"description\":\"The interface, base class or overridable member to find implementations/overrides/derived types for; simple or dotted-qualified.\"}},\"required\":[\"symbol\"],\"additionalProperties\":false}");

        public static readonly ToolDescriptor RenameSymbol = new ToolDescriptor(
            "rename_symbol",
            "Rename a C#/VB symbol and every reference to it across the solution using Visual Studio's semantic " +
            "rename. Safe where a find/replace is not: only real references change, never matching text in " +
            "comments, strings or unrelated same-named symbols. The change is applied through Visual Studio, so it " +
            "shows in the editor and Ctrl+Z undoes it; prefer this over editing files yourself to rename something. " +
            "Only symbols declared in your source can be renamed. 'symbol' must resolve to exactly one symbol (a " +
            "simple or dotted-qualified name): if several match, the candidates are listed and NOTHING changes, so " +
            "re-issue with a longer qualifier. A name cannot tell overloads apart, so an overloaded method cannot " +
            "be renamed with this tool. 'newName' is the new identifier. Returns the files changed and the number " +
            "of edits. Razor and other markup references are not updated — build afterwards to catch them.",
            "{\"type\":\"object\",\"properties\":{\"symbol\":{\"type\":\"string\",\"description\":\"The symbol to rename; must resolve to exactly one source symbol (simple or dotted-qualified).\"},\"newName\":{\"type\":\"string\",\"description\":\"The new identifier.\"}},\"required\":[\"symbol\",\"newName\"],\"additionalProperties\":false}")
        { Risk = ToolRisk.Edit };

        public static readonly ToolDescriptor ApplyCodeFix = new ToolDescriptor(
            "apply_code_fix",
            "Apply Visual Studio's own light-bulb (Quick Actions) fix for a diagnostic — add a missing using, " +
            "implement an interface, generate a member, apply an analyzer fix. Prefer this over hand-editing to " +
            "resolve a compiler error or analyzer warning: it applies the fix the IDE would, and Ctrl+Z undoes it. " +
            "'file' and 'line' (1-based) locate the diagnostic — get them from get_diagnostics or build_solution. " +
            "'diagnosticId' (e.g. 'CS0246') picks one of several on the line, and 'column' (1-based) one of several " +
            "with the same id. IDE style rules (IDE####) work too; hidden ones are absent from get_diagnostics' " +
            "default view, so name the id (e.g. 'IDE0005' on a using line). If several DIFFERENT fixes are offered " +
            "and no 'fixTitle' is given, they are listed and NOTHING changes. TO FIX A WHOLE RULE IN A FILE, pass " +
            "scope:\"document\" with 'diagnosticId' and NO 'line': every occurrence of that id in the file is fixed " +
            "in ONE call (Visual Studio's 'Fix all occurrences in Document'). Do NOT loop the line form for that; " +
            "for several files, call once per file. When occurrences still offering the same fix remain, the result " +
            "carries 'sameFixRemaining' (with 'sameFixRemainingLines' or 'sameFixRemainingColumns') — re-run while " +
            "it is present, stop when it is absent, which is the normal outcome. Returns the diagnostic fixed, the " +
            "fix applied, 'scopeApplied' (a provider without fix-all degrades a document request to one occurrence, " +
            "and says so) and the files changed. A fix that changes nothing is reported as an error, not a " +
            "successful no-op.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"file\":{\"type\":\"string\",\"description\":\"The source file containing the diagnostic: absolute, or relative to the solution folder. A relative path may be a suffix (e.g. 'Engine/Program.cs') but must identify exactly ONE file; otherwise the candidates are listed so you can re-issue.\"}," +
            "\"scope\":{\"type\":\"string\",\"enum\":[\"line\",\"document\"],\"description\":\"'line' (default) fixes ONE occurrence at 'line'. 'document' fixes EVERY occurrence of 'diagnosticId' in the file in one call; 'line' is then optional and only picks which occurrence's fix is the template. There is deliberately no project- or solution-wide scope.\"}," +
            "\"line\":{\"type\":\"integer\",\"description\":\"1-based line of the diagnostic. Required for scope 'line'; optional for scope 'document'.\"}," +
            "\"column\":{\"type\":\"integer\",\"description\":\"Optional 1-based start column, to target one of several same-id diagnostics on the line.\"}," +
            "\"diagnosticId\":{\"type\":\"string\",\"description\":\"Diagnostic id, e.g. 'CS0246'. Optional for scope 'line' (disambiguates); REQUIRED for scope 'document', where it is the rule being fixed.\"}," +
            "\"fixTitle\":{\"type\":\"string\",\"description\":\"Optional title of the fix to apply when several are offered.\"}" +
            "},\"required\":[\"file\"],\"additionalProperties\":false}")
        { Risk = ToolRisk.Edit };
    }
}
