# Code Wicket — documentation

**One gateway. Every agent.**

Connect Claude Code, Kiro, or any ACP-enabled agent to Visual Studio 2022 and 2026 — with deep IDE
integration: diffs, builds, tests, Roslyn code fixes, and the live debugger.

The agent itself runs in its own command-line tool. Code Wicket gives it your solution's context and
a set of Visual Studio tools, shows every change it makes as a native VS diff, and when the agent asks
permission, your permission mode and rules decide whether it goes ahead or waits for you.

> **You need a backend CLI.** The extension does nothing on its own — see
> [Getting started](getting-started.md) for the one-time setup.

## Start here

| | |
|---|---|
| [Getting started](getting-started.md) | Prerequisites, install, and your first conversation. |
| [Choosing a backend](backends.md) | Kiro, Claude Code, or your own ACP agent — setup, sign-in, models. |
| [Using the chat](using-the-chat.md) | Sending, interrupting, attaching, reading tool rows and diffs, history. |
| [What the agent can do in Visual Studio](ide-tools.md) | The tools Code Wicket exposes, and the debugger hand-over. |
| [Permissions](permissions.md) | Approving actions, the four modes, and making a rule stick. |
| [Your data](your-data.md) | What is sent where, and what is kept on this machine. |
| [Troubleshooting](troubleshooting.md) | When something doesn't work, and what to attach to a bug report. |

## Where things are in Visual Studio

Most commands are on one menu: **Extensions → Code Wicket**.

| Command | What it does |
|---|---|
| **Code Wicket Chat** | Opens the chat tool window. Dock it wherever you like. Also on the **View** menu, and on **Ctrl+\\, Ctrl+W**. |
| **Settings…** | Opens Visual Studio's settings window. Code Wicket's pages are under **Code Wicket** in it. |
| **Open Logs Folder** | Opens `%LOCALAPPDATA%\code-wicket\logs` — start with `engine.log`. |
| **Restart** | Relaunches the engine and the agent CLI so changed settings take effect. The workspace's most recent conversation is reopened afterwards. |
| **About** | Version information. |

There is also **Debug → Send Debug Context to Code Wicket…**, on the editor's right-click menu as
well, available while you are stopped at a breakpoint. See [the debugger hand-over](ide-tools.md#handing-the-debugger-over).
