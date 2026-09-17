# Getting started

## 1. What you need

| | |
|---|---|
| **Visual Studio 2022 or 2026** | Code Wicket supports Visual Studio 2022 (17.14 or later) and Visual Studio 2026 (18.x), x64, on Windows. |
| **A backend CLI** | The agent runs in its own command-line tool, which you install and sign in to yourself. Pick one below. |
| **.NET 10 runtime (x64)** | Code Wicket's engine runs on it. If you have the .NET 10 SDK you already have it; otherwise install the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). |
| **Node.js 22 or later** | Only for the Claude Code backend, whose adapter is an npm package. |

**Code Wicket does nothing without a backend.** It is the gateway, not the agent — it does not talk
to any AI service itself, ship a model, or hold an API key of its own. Everything goes through the
CLI you choose, under that vendor's account and terms. See [Your data](your-data.md).

## 2. Install a backend

Pick whichever you already have an account for. You can install both and switch per conversation.

### Claude Code

```
npm install -g @agentclientprotocol/claude-agent-acp
```

The adapter uses your Claude Code sign-in. If you have not signed in to Claude Code on this machine,
[install Claude Code](https://code.claude.com/docs/en/setup), run `claude` in a terminal and follow
its sign-in prompts in the browser. The adapter runs its own copy of Claude Code, and it reads the
same sign-in. Alternatively, set `ANTHROPIC_API_KEY` to use an API key instead.

### Kiro

Install [kiro-cli](https://kiro.dev) and make sure `kiro-cli` is on your `PATH`, then sign in from a
terminal:

```
kiro-cli login
```

Kiro's [CLI authentication guide](https://kiro.dev/docs/cli/authentication/) covers the sign-in
routes. Code Wicket never starts a sign-in itself: if Kiro isn't signed in, the chat says so and asks
you to run `kiro-cli login`.

### Something else

Any agent that speaks ACP over stdio can be added yourself — see
[Choosing a backend](backends.md#adding-your-own-agent).

## 3. Install Code Wicket

In Visual Studio, open **Extensions → Manage Extensions**, search for **Code Wicket**, and install it.
Visual Studio installs extensions when it closes, so close it, let the VSIX Installer finish, and
start Visual Studio again. The extension installs for your Windows user only, so it needs no
administrator rights.

To install without the Marketplace, download `CodeWicket.VSExtension.vsix` from the
[GitHub Releases](https://github.com/MattDarg/code-wicket/releases) page, close Visual Studio, and
double-click the file.

## 4. Open the chat

**Extensions → Code Wicket → Code Wicket Chat.** Dock the window wherever suits you — the right-hand
dock alongside Solution Explorer is a common choice.

Open a solution first if you can. Code Wicket runs the agent in your solution's folder — or a folder
above it, where that is where the backend keeps its workspace settings; see
[Where the agent runs](backends.md#where-the-agent-runs) — so with no solution open it falls back to a
scratch workspace and says so in the chat.

The header has two pickers: the **backend** (Kiro, Claude Code, or one you added) and the **model**.
**The backend picker starts on Kiro.** If you installed Claude Code, choose it there before your first
message; Code Wicket remembers the choice, and **Settings… → General** sets the default.
The model list is filled in from the backend once it connects, so it may be brief for a second on the
very first launch.

## 5. Say something

Type a question and press **Enter**.

A first reply can take a few seconds while the backend starts up. After that:

- **Text** streams into the transcript as the agent writes it.
- **Tool rows** appear for each thing the agent does — reading a file, running a build, searching for
  a symbol. Click one to open the file or diff it touched (a row that touched none expands instead);
  its chevron shows exactly what ran and what came back.
- **Edit cards** appear when the agent changes a file. Click one to open the change in a native
  Visual Studio diff. Where Visual Studio applied the edit, **Ctrl+Z undoes it** like your own — see
  [Reading the transcript](using-the-chat.md#reading-the-transcript).
- **A permission banner** appears at the bottom when the agent asks to do something your current
  permission mode doesn't cover. That action waits until you answer. See [Permissions](permissions.md).

Good first prompts, in rough order of how much they prove:

```
What does this solution do?
Build the solution and tell me what's failing.
Run the tests in <YourProject>.Tests and fix the first failure.
```

The second and third are worth trying early: they exercise the Visual Studio integration rather than
just the model, so if something is wired up wrongly you find out immediately.

## 6. Set your comfort level

New installs start in the most cautious permission mode, **Ask each time**. Once you have a feel for
it, raise it with the **permission picker** at the bottom-left of the chat, in the status strip — a
common choice is **Allow edits**, which auto-approves reads and edits while still asking about
commands. The picker and **Extensions → Code Wicket → Settings…**, then **Code Wicket → Permissions**,
are the same setting: a change in either takes effect straight away and is kept for next time.

Read [Permissions](permissions.md) before you raise it. It is the setting that decides how much can
happen without you.

## Next

- [Choosing a backend](backends.md) — sign-in, models, and running more than one.
- [Using the chat](using-the-chat.md) — attachments, interrupting a turn, resuming old conversations.
- [What the agent can do in Visual Studio](ide-tools.md).
