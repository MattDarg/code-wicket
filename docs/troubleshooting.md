# Troubleshooting

## Start here

**Extensions → Code Wicket → Open Logs Folder**, then open **`engine.log`**. It is always written,
it carries your agent CLI's own error output, and it is the file that answers most questions.

The **info button** in the chat's status strip is the other half: it says what this session actually
negotiated — backend, adapter version, when the connection opened, the conversation id, the
capabilities offered and withheld, and where the logs are. **Copy** puts all of it on the clipboard
as plain text.

## Common situations

### The chat says a session failed to start

Expand the error. When the backend started and then failed, Code Wicket relays its **own** message
rather than paraphrasing it, and that text is usually the answer: not signed in, a proxy refusing
the connection. When the CLI couldn't be launched at all, the message is Code Wicket's, and names
what it looked for: *no program named '…' was found on PATH*, or *no program was found at '…'*.

- **Not signed in** — sign in with the backend's CLI in a terminal, then **Extensions → Code Wicket →
  Restart**.
- **CLI not found** — first check the backend picker in the chat header names the backend you
  installed: it starts on Kiro. Otherwise set the path explicitly in **Settings… → Backends** (Kiro CLI path / Claude
  Code adapter path). A CLI installed while Visual Studio was open is not found on `PATH` until
  Visual Studio restarts: Code Wicket's engine and the CLI inherit the `PATH` Visual Studio started
  with, so **Restart** from the Code Wicket menu is not enough.
- **Behind a proxy** — set `HTTPS_PROXY` (and `NO_PROXY=localhost`) under **Settings… → Backends →
  Custom and shared → Engine environment variables**. Behind a proxy that intercepts TLS, also point
  `AWS_CA_BUNDLE` (Kiro) or `NODE_EXTRA_CA_CERTS` (Claude Code) at your organisation's root certificate;
  see [Corporate networks](backends.md#corporate-networks).

### The chat says the engine needs the .NET 10 runtime

Code Wicket's engine runs on the **.NET 10 runtime (x64)**. When .NET reports it missing, the chat
says so — *"Code Wicket's engine needs the .NET 10 runtime, and could not start…"* — and the details
hold .NET's own message, with its download link. Install the
[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), then restart Visual Studio.

If the engine stopped for any other reason, the notice gives its exit code and where its log is.

### Claude Code won't start

It needs **Node.js 22 or later**. Check with `node --version`, and check the adapter is installed:

```
npm install -g @agentclientprotocol/claude-agent-acp
```

Note that the version shown in the info panel is the **adapter's**, which is not the same as the
Claude Code version — so a terminal `claude --version` is answering about a different program than
the one running here.

### A setting didn't take effect

Most settings are read when the chat window opens. **Extensions → Code Wicket → Restart** applies
them without restarting Visual Studio. The workspace's most recent conversation is reopened
afterwards.

### The model picker is empty, or missing a model

The list comes from the backend, and is cached after the first successful connection, so the very
first launch is the one that can look bare for a moment.

- **On Claude Code**, the list is the **adapter's**, and it is bound to the adapter's version rather
  than to your account or the `claude` in your terminal. A model your terminal offers can be missing
  here until you update the adapter — `npm install -g @agentclientprotocol/claude-agent-acp@latest` —
  and then use **Extensions → Code Wicket → Restart**.
- **On Kiro**, check what `kiro-cli` itself offers: if a model isn't there, it is Kiro's answer.

### The agent isn't using the Visual Studio tools

Open the **MCP** panel in the status strip. Under **This extension**, Code Wicket's own `code-wicket`
entry should read *connected · N tools*; *tools not requested yet* means the agent connected but never
asked for the catalog. `engine.log` has the same two facts as lines — one when the bridge handshakes
and one saying how many tools it served.

Some backends need steering toward integrated tools when they have strong built-ins of their own.
Asking directly ("use the build_solution tool") is a fair diagnostic.

### History is empty, or missing conversations

- Conversations are **per workspace**. A different solution has a different history.
- The **Agent's history** tab reads the CLI's store, which is keyed by **working directory**. If the agent
  is running somewhere unexpected, the list is genuinely empty rather than broken — the info panel
  says which directory it is using.
- On Kiro, the **agent engine** setting can change which conversations the history picker finds,
  and a conversation started under v3 can't be resumed under v2.
- If the chat window opened before your solution finished loading, restarting the window
  (**Extensions → Code Wicket → Restart**) re-resolves the workspace.

Conversations the backend created but never used are held back behind an **"N unused conversations
hidden — show"** line — check there before concluding something is lost.

### An image didn't attach, or went as a path

**"Couldn't attach …"** is followed by the reason. Most often the file's own bytes are not one of the
four formats the backends take (PNG, JPEG, GIF, WebP). The extension is not what decides, so a `.png`
that is really a bitmap or a renamed document is refused — re-save it as a PNG and try again. A file
is also refused when it is empty, locked or gone by the time it was read, truncated or corrupt, too
large to read, over 16 megapixels, or still too large after being scaled down; for those, resize or
re-export it.

**The message went out naming a file instead of showing it** when the backend you are on cannot accept
images. Code Wicket says so at the time rather than sending a message that looks as though it carried
the picture; see [backends](backends.md) for which take images.

**Dragging an image onto the chat, or picking it with `Add ▾ → File path…`, inserts its path** — that
is not a failure, it is the other gesture. Use **Ctrl+V** or **Add ▾ → Image…** when you want the
picture itself; see
[the two gestures compared](using-the-chat.md#pointing-at-a-file-and-attaching-a-picture-are-different-things).

### An edit didn't happen

If Visual Studio refused the change — a read-only file, or a source-control checkout you cancelled —
the agent is told the file is **unchanged** and why, and the card says so too. Clear the read-only
attribute (or allow the checkout prompt) and ask again. Code Wicket will not write the file another
way behind a refusal you made.

### "Starting Code Wicket…" never finishes

The chat window is waiting on start-up. `engine.log` times each phase and names any phase that hasn't
returned; a log with phase lines but no `window ready` is a start-up that didn't finish. That is
worth reporting — see below.

### My settings vanished

A `config.json` that can't be parsed is never silently reset: it is preserved as
`config.json.bad` beside it and the problem is logged. If you have been hand-editing, that file is
your copy back.

## Reporting a bug

Code Wicket never sends its logs anywhere, so a report only has what you attach. Please include:

1. **The info panel's Copy output** — backend, versions, capabilities, workspace.
2. **`engine.log`**, from `%LOCALAPPDATA%\code-wicket\logs`.
3. What you did, what you expected, and what happened.

If the problem is about what the agent *said or sent*, turn on **Settings… → Advanced → Diagnostics →
Log agent protocol frames** (`acp.log`), reproduce it, then turn it back off. **That file contains
your prompts and the agent's replies** — check it before attaching it to anything public.

Issues: <https://github.com/MattDarg/code-wicket/issues>
