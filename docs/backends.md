# Choosing a backend

A **backend** is the agent CLI that Code Wicket drives. Two are built in — Kiro and Claude Code —
and you can add any other agent that speaks ACP over stdio.

Both built-ins can be installed at once. The **backend picker** in the chat header switches between
them; the **model picker** beside it lists whatever that backend offers.

> Switching the backend picker starts a fresh session, and so does a model change the backend can't
> apply mid-session, so finish what you are doing first.
> Browsing your conversation history does *not* cost you the current session.

What a backend actually offers is not guesswork: the **info button** in the chat's status strip
reports what the live session negotiated — images and resuming included — for the backend you are on
right now.

## Claude Code

```
npm install -g @agentclientprotocol/claude-agent-acp
```

Needs **Node.js 22 or later**. Code Wicket uses your existing Claude Code sign-in, or
`ANTHROPIC_API_KEY` if you have one set. To sign in, [install Claude
Code](https://code.claude.com/docs/en/setup), run `claude` in a terminal and follow its sign-in
prompts. The adapter runs **its own copy** of Claude Code rather than the `claude` you installed, but
it reads the same sign-in.

If the adapter isn't on your `PATH`, set the path explicitly in **Settings… → Backends → Claude Code
→ Claude Code adapter path**.

Claude Code supports **resuming** a conversation and **attached images** (pasted or picked).

Claude Code writes files itself rather than through Visual Studio. Its edits still show as native
diffs, but they are not Visual Studio edits.

## Kiro

Install [kiro-cli](https://kiro.dev), make sure `kiro-cli` is on your `PATH`, and sign in with
`kiro-cli login` in a terminal (Kiro's [CLI authentication
guide](https://kiro.dev/docs/cli/authentication/) covers the routes). Code Wicket will not launch a
signed-out Kiro — the chat tells you to run `kiro-cli login` instead. If kiro-cli lives somewhere
unusual, set **Settings… → Backends → Kiro → Kiro CLI path**.

Code Wicket runs Kiro as its built-in default agent. Your own MCP servers still come along: Kiro's
default agent loads them from `~/.kiro/settings/mcp.json` and from `.kiro/settings/mcp.json` in the
workspace, not through Code Wicket. What it does not do is run a custom Kiro agent (a `.kiro/agents`
profile with its own prompt, tools or agent-scoped MCP servers): Code Wicket does not select a custom
Kiro agent. A
repository can still replace that default agent, trust rules included — see
[Permissions](permissions.md#reading-back-what-happened).

One Kiro-specific setting is worth knowing about:

- **Kiro agent engine** — `v1`, `v2` or `v3`, or blank for Kiro's default (`kiro-cli acp
  --agent-engine`). **This quietly changes which conversations you can pick up**: switching engines
  can change what the history picker finds, and a conversation started under v3 can't be resumed
  under v2. Where each engine keeps its conversations is covered by Kiro's own documentation.

Kiro supports **resuming** a conversation and **attached images**. Under the v3 engine Kiro hands its
file writes to Visual Studio, so those are Visual Studio edits and Ctrl+Z undoes them in the editor;
the earlier engines write files themselves, as Claude Code does.
Kiro also reads its steering files, agent configs and workspace MCP servers from its **working
directory** — see [Where the agent runs](#where-the-agent-runs) below.

## Adding your own agent

**Settings… → Backends → Custom and shared → Custom ACP agents** takes a JSON array:

```json
[
  {
    "providerId": "gemini",
    "displayName": "Gemini CLI",
    "cliPath": "gemini.cmd",
    "args": ["--experimental-acp"]
  }
]
```

`env` and `models` are optional per entry. When you save in Settings, each new or changed entry is
checked with a real ACP handshake: one that fails is refused with the reason, and nothing is saved.
The handshake also records whether the agent can resume conversations; declare `capabilities`
yourself to override that.

Entries edited straight into `config.json` are not checked — a bad one is skipped with a warning when
the chat window opens, rather than breaking the picker. Built-in backends win if you reuse their id.

Applies when the chat window next opens — use **Extensions → Code Wicket → Restart**.

## Turning a backend off

**Settings… → Backends → Enable Kiro / Enable Claude Code.** Turn one off to keep it out of the
picker on machines where it will never be installed. Custom agents are enabled by being present.

## Models

The model list comes from the backend at connection time, and is cached so the next launch's picker
isn't empty. If a model you expect is missing, it is almost always the backend's answer rather than
ours.

- **On Claude Code, the list belongs to the adapter's version**, not to your account or the `claude`
  you installed: the adapter runs its own copy of Claude Code, so a model your terminal `claude`
  offers can be missing here until the adapter is updated. Update it, then use **Extensions → Code
  Wicket → Restart**:

  ```
  npm install -g @agentclientprotocol/claude-agent-acp@latest
  ```

- **On Kiro**, check what `kiro-cli` itself offers.

**Settings… → General → Default model** is the model Code Wicket starts with. It follows the model
picker — choosing a model in the header updates it — so it is the last model you picked rather than a
fixed default. Clear it to let the backend choose.

## Where the agent runs

The agent's working directory is not always your solution folder, and for Kiro it matters: steering
files, agent configs and workspace MCP servers are read from there.

**Settings… → General → Agent workspace:**

- **Up to the repository root** (default) — look upwards from the solution for the backend's
  workspace folder (for example `.kiro`). The search stops at the repository and never uses your
  user profile folder.
- **Solution folder only** — never look past the solution.

A repository can also state this for itself. Commit a `.code-wicket/settings.json` with a **relative**
path:

```json
{ "agentWorkspaceRoot": "../.." }
```

Everyone who clones the repository then gets the same working directory without configuring anything.
Only that one key is honoured, only relative values are accepted, and the **Solution folder only**
setting overrides it — a repository can describe itself, it cannot grant itself anything.

Whatever is chosen, the chat says which directory the agent is running in when it isn't the obvious
one.

## Corporate networks

**Settings… → Backends → Custom and shared → Engine environment variables** takes `KEY=value`, one
per line, and applies them to the engine process and every agent CLI it spawns:

```
HTTPS_PROXY=http://proxy.corp:8080
NO_PROXY=localhost
AWS_CA_BUNDLE=C:\certs\corp-root.pem
NODE_EXTRA_CA_CERTS=C:\certs\corp-root.pem
```

`HTTPS_PROXY` and `NO_PROXY` route traffic through the proxy, and keep local addresses off it. The
other two matter behind a proxy that intercepts TLS: **Kiro** reads its trusted certificates from
`AWS_CA_BUNDLE`, and **Claude Code**, running on Node.js, from `NODE_EXTRA_CA_CERTS`. Each points at a
PEM file holding your organisation's root certificate; set the one for the backend you use, or both.

Applies when the chat window next opens.
