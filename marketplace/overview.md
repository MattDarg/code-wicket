# Code Wicket

**One gateway. Every agent.**

Connect Claude Code, Kiro, or any ACP-enabled agent to Visual Studio 2022 and 2026 — with deep IDE
integration: diffs, builds, tests, Roslyn code fixes, and the live debugger.

![Code Wicket docked in Visual Studio: the agent has fixed two failing tests, and one of its edits is open in Visual Studio's own diff viewer](https://raw.githubusercontent.com/MattDarg/code-wicket/main/docs/images/native-diff.png)

---

## Before you install

Code Wicket is the gateway, not the agent. It ships no model, talks to no AI service of its own, and
holds no API key. You bring a backend CLI and sign in to it yourself, under your own account and that
vendor's terms.

| You need | |
|---|---|
| **Visual Studio 2022 or 2026** (17.14 or later / 18.x), Windows x64 | Supported on both. |
| **A backend CLI** | [`kiro-cli`](https://kiro.dev), or `npm install -g @agentclientprotocol/claude-agent-acp` for Claude Code — installed and signed in to yourself. |
| **.NET 10 runtime** (x64) | Code Wicket's engine runs on it. The .NET 10 SDK includes it; otherwise install the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). |
| **Node.js 22 or later** | For the Claude Code backend only — its adapter is an npm package. |

**Without a backend the extension is inert.** Install one first.

---

## Visual Studio's own answers, not a shell's

The agent runs in its own CLI. Code Wicket gives it your solution's context and a set of real Visual
Studio tools, exposed to it as an MCP server — so when it asks whether the build is clean, it gets
*your* build.

![The agent runs the tests through Visual Studio and reads back two failures with their messages and source locations](https://raw.githubusercontent.com/MattDarg/code-wicket/main/docs/images/hero-mid-turn.png)

### Building and checking

- **`build_solution`** — builds the solution, or one project, exactly as the IDE builds it.
  Incremental by default, and it reports the errors you would see in the Error List.
- **`get_diagnostics`** — the current errors and warnings with file, line, column and code.
  Compiler diagnostics by default; analyzers, or the whole live Error List, on request.
- **`run_tests`** — builds, then runs the tests — SDK-style projects through `dotnet test` or the
  Microsoft Testing Platform, old-style .NET Framework projects through Visual Studio's own
  `vstest.console` — and reports failures with messages, stack traces and source locations.
  Filterable by method, class or namespace.

### Navigating and changing code

- **`find_symbol`**, **`find_references`**, **`find_implementations`** — from the Roslyn workspace
  Visual Studio is already maintaining for your solution.
- **`rename_symbol`** — a real Roslyn rename across the solution, not a find-and-replace.
- **`apply_code_fix`** — the same fixes as the editor's light bulb.
- **`open_file`** — brings a file and line to the front, so you are looking at what it is talking
  about.

Code Wicket ships no copy of Roslyn. These are the answers the IDE already has.

### The developer environment

**`run_command`** runs in the Visual Studio Developer environment, where `msbuild`,
`vstest.console`, `sn`, `signtool` and the rest are on `PATH` — which a plain shell does not have.

---

## Visual Studio edits, and a native diff for every change

When the agent changes a file, an edit card appears in the transcript. Click it and the change opens
in a **native Visual Studio diff** — the one you already know, with your settings and your theme —
whichever backend made it.

Code Wicket supports Visual Studio edits. Where Visual Studio applies the change — a backend that
hands its file writes to the editor, and Code Wicket's own rename and code-fix tools — **Ctrl+Z
undoes it like your own**, and if a file is read-only or needs checking out, the write is refused and
says so plainly; nothing is routed around your refusal.

---

## You decide what's approved

A permission banner appears in the chat when the agent asks to do something your current mode
doesn't already cover, and **the agent waits** — that action doesn't happen until you answer.

![A permission banner asking before the agent edits Loan.cs, with the proposed change already open as a diff](https://raw.githubusercontent.com/MattDarg/code-wicket/main/docs/images/permission-banner.png)

Four modes, a cumulative ladder — **Ask each time** (the default), **Allow reads**, **Allow edits**,
**Allow all**. On top of the mode you keep rules of your own: allowed commands, allowed edit paths,
allowed MCP tools, and an **always-prompt list that overrides all of it**, so `git push --force` can
be made to ask however permissive the mode is. Every "always" decision is held by Code Wicket rather
than by the backend, so it means the same thing whichever agent you are talking to.

Backends resolve their own settings before asking, so low-risk work — reading a file in your
solution, a text search — often won't raise a banner at all. Every tool row records which way it
went, including when the backend never asked.

---

## It can drive the debugger — and running your code is its own permission

The agent doesn't start debugging; you do (F5 by default). It places the breakpoints and reads what stops there.

![The agent reading values from a process stopped in the debugger, with the call stack and locals in its answer](https://raw.githubusercontent.com/MattDarg/code-wicket/main/docs/images/debugger.png)

- **`set_breakpoint`** — a plain stop on a line. Each one carries the agent's reason, shown beside it
  so you know why it is there.
- **`set_expression_breakpoint`** — with a condition, or as a **tracepoint** that prints and keeps
  running. Visual Studio evaluates those inside your program, so this one sits in the top
  permission tier: when the backend asks about the call, every mode below **Allow all** puts it to
  you.
- **`read_expression`** — evaluates against the stopped process **reading memory only**.
- **`execute_expression`** — for an expression that *may* run code, such as a property getter. The
  same top tier.

Each of those is a pair of separate tools rather than one with a flag, so the safe one can be allowed
without allowing the one that runs code.

You hand the debugger over deliberately, as a chip on your message — the stopped frame, its locals
and their values go to the agent when you say so, not on every prompt.

---

## Bring your own agent

Kiro and Claude Code ship configured. Anything else that speaks **ACP over stdio** is a few lines of
config away — command, arguments, environment — with no code and no new build. Switch backend or
model per conversation from the chat header.

![The model picker in the chat header, listing the backend's models](https://raw.githubusercontent.com/MattDarg/code-wicket/main/docs/images/picker-model.png)

Conversations save themselves per workspace and resume where you left off, and Code Wicket can pick
up conversations you started in the backend's own CLI, outside Visual Studio entirely.

---

## What happens to your code

**Code Wicket does not talk to any AI service.** It has no model, no API key and no account of its
own — it launches the CLI *you* installed and signed in to, and everything goes through that, under
that vendor's terms and your account with them. We are not a party to it and cannot see it.

When you send a message, the backend receives what you typed, a small block describing your
workspace, anything you attached, and whatever the agent then asks for — including the contents of
files it reads. **Assume anything in your solution can end up in the conversation.**

Settings, saved conversations, logs and attachments live under `%APPDATA%\code-wicket` and
`%LOCALAPPDATA%\code-wicket`, and Code Wicket does not send its logs anywhere.
[Your data](https://github.com/MattDarg/code-wicket/blob/main/docs/your-data.md) has the detail.

---

## Getting started

1. Install a backend CLI and sign in to it.
2. Install Code Wicket and restart Visual Studio.
3. **Extensions → Code Wicket → Code Wicket Chat**, and dock it wherever suits you.
4. Open a solution, then ask it something that proves the wiring:
   *"Build the solution and tell me what's failing."*

Full setup, backend-by-backend, in the [getting started guide](https://github.com/MattDarg/code-wicket/blob/main/docs/getting-started.md).

---

## Links

- [Documentation](https://github.com/MattDarg/code-wicket/blob/main/docs/README.md)
- [Choosing a backend](https://github.com/MattDarg/code-wicket/blob/main/docs/backends.md)
- [What the agent can do in Visual Studio](https://github.com/MattDarg/code-wicket/blob/main/docs/ide-tools.md)
- [Permissions](https://github.com/MattDarg/code-wicket/blob/main/docs/permissions.md)
- [Your data](https://github.com/MattDarg/code-wicket/blob/main/docs/your-data.md)
- [Troubleshooting](https://github.com/MattDarg/code-wicket/blob/main/docs/troubleshooting.md)
- [Source on GitHub](https://github.com/MattDarg/code-wicket) · Apache-2.0
