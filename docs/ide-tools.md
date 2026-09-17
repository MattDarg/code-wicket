# What the agent can do in Visual Studio

Code Wicket exposes a set of Visual Studio tools to the agent as an MCP server named
**`code-wicket`**, alongside whatever tools the agent already has. A tool row in the transcript, the
permission banner and a permission rule all name one as `code-wicket/<tool>` on every backend — see
[Permissions](permissions.md). The raw name a backend uses on the wire differs
(`mcp__code-wicket__build_solution`, `@code-wicket/build_solution`) and is what appears in its logs.

They matter because they are **Visual Studio's own answers**, not a shell's: the build is the build
you see in the IDE, the symbol search is the one Roslyn is already maintaining for your solution, and
the tests run against what that build produced.

## Building and checking

| Tool | What it does |
|---|---|
| `build_solution` | Builds the solution — or one project — exactly as the IDE builds it, and reports the errors. Incremental by default. |
| `get_diagnostics` | The current errors and warnings, with file, line, column and code. Can include analyzers, or the whole live Error List. |
| `run_tests` | Builds, then runs the solution's tests and reports the failures with their messages, stack traces and source locations. SDK-style test projects run through `dotnet test`, or `dotnet run` for the Microsoft Testing Platform; old-style .NET Framework test projects run through Visual Studio's own `vstest.console`. Can be filtered by method, class or namespace. |

Both `build_solution` and `run_tests` are worth preferring over the agent running `dotnet build` or
`dotnet test` itself, and Code Wicket tells the agent so — the results match what you see, and the
diagnostics include things a plain command line would report differently.

## Navigating and changing code

| Tool | What it does |
|---|---|
| `find_symbol` | Find a symbol by name across the solution. |
| `find_references` | Every reference to a symbol. |
| `find_implementations` | Implementations of an interface or overrides of a member. |
| `rename_symbol` | A real Roslyn rename across the solution, not a find-and-replace. |
| `apply_code_fix` | Apply a Roslyn code fix — the same fixes as the editor's light bulb. |
| `open_file` | Bring a file (and optionally a line) to the front in your editor, so you are looking at what the agent is talking about. |

These come from the live Roslyn workspace Visual Studio is already running, so they work on C# and
Visual Basic code. Code Wicket does not ship its own copy of Roslyn.

## Running commands

`run_command` runs a command line in the **Visual Studio Developer environment**, where `msbuild`,
`vstest.console`, `sn`, `signtool` and the rest of the VS developer tools are on `PATH` — which a
plain shell does not have. It is meant for exactly that, not as a general-purpose shell.

It carries the highest risk tier, so **every permission mode below "Allow all" prompts for each
invocation**, unless a command rule of yours allows that command. You can turn it off entirely in
**Settings… → Advanced → Tools**, which also stops it being advertised to the agent at all; that
applies when the chat window next opens.

## Debugging

The agent doesn't run your debugger. It decides where to stop and what to watch; you start debugging (F5 by default).

| Tool | What it does |
|---|---|
| `set_breakpoint` | Place a plain breakpoint — a stop on a line, optionally only on the Nth hit. Carries a reason, shown on its row in the chat so you know why it is there. |
| `set_expression_breakpoint` | The same, but with a **condition** or a **tracepoint** message that prints and keeps running. Visual Studio evaluates those in your process when the line is reached, so it sits in the top permission tier with `run_command`: when the backend asks about the call, every mode below **Allow all** puts it to you, unless you have allowed the tool by name. |
| `list_breakpoints` | What is currently set. |
| `clear_breakpoints` | Remove the breakpoints it placed. |
| `get_debug_output` | Read back what its tracepoints printed. |
| `read_expression` | Evaluate an expression against the stopped debuggee **without running any code** — reads memory only. |
| `execute_expression` | Evaluate an expression that *may* run code (a property getter, a method). The same top permission tier as `set_expression_breakpoint`. |

Both pairs — `read_expression`/`execute_expression` and `set_breakpoint`/`set_expression_breakpoint` —
are deliberately separate names rather than one tool with a flag, so the safe one can be allowed
without also allowing the one that runs code in your process. A condition or a tracepoint message is
code the debugger evaluates in the program you are debugging, which is why it sits with
`execute_expression` rather than with a plain stop.

Breakpoints the agent places are marked as its own and are replaced in place rather than
accumulating. **Your own breakpoints are never touched.** If one of yours is at the same location,
the agent is told there is a conflict rather than silently taking it over.

### Handing the debugger over

While you are stopped at a breakpoint, **Debug → Send Debug Context to Code Wicket…** — or **Add →
Debug context** in the chat — attaches the call stack, the locals and why you stopped, as a chip on
your message. You can read it, and remove it, before anything is sent.

This is the counterpart to breakpoints: the agent can only read back what it thought to instrument
for, whereas the hand-over gives it what is actually on screen.

### Handing over the Output window

**Add ▾ → Output window** attaches what the Output window is showing — a failed build, a NuGet
restore, a test run, whatever an extension printed. Same shape as the debug hand-over: a chip you can
read and remove before anything is sent.

It is worth knowing why this is a hand-over rather than a tool. `get_debug_output` reads the **Debug**
pane, and a build's tail comes back with `build_solution`; every other pane — Tests, Source Control,
your extensions' own — the agent cannot see at all, and it could not know which one to look in even
if it could. You already answered that by having it on screen.

**If you have selected lines in the pane, those are what go**, up to a generous limit; otherwise it
takes the last 200 lines. Either way the chip says which it did, and so does the message the agent
receives — so when it has only a fragment it can ask you for the rest instead of concluding the thing
it was looking for was never printed.

## What the agent knows without asking

Every prompt carries a small block of context the agent could not get by asking: the workspace it is
running in, the active file and any text you have selected in it, which other files you have open,
the shape of the current errors and warnings, and — while a debug session is live — whether it is
running or stopped, and where and why it stopped.

It deliberately does *not* include the full diagnostic list, or the contents of your files beyond
that selection. The agent asks for those with the tools above, so it gets the current answer rather
than a stale one.

## Your own MCP servers

Code Wicket's tools sit alongside any MCP servers your backend is configured with; Kiro loads yours
through its agent config. The **MCP** button in the status strip shows the whole roster — every
server the agent is connected to and the tools each one defines.
