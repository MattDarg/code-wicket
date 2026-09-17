# Kiro agents: what was measured, what was removed, and what a feature would need

> Code Wicket does not expose Kiro agent selection. This records what was measured and what a
> feature would need, so that a later design does not have to re-measure it. The permission side is in
> [permissions.md](permissions.md) (issue #269).

> **`#NN` refers to a private pre-release tracker.** The numbers are stable *names* for a failure
> mode, not links — the rule each one tags is stated here in full, so nothing is missing if you
> cannot open them.

> Terms used here without definition (the Exp hive, rungs, warm start, the prove-check verdicts, …) are
> in [AGENTS.md](../../AGENTS.md#terms-the-docs-use-without-defining-them).

## What a Kiro agent is

A Kiro **agent** (also "agent config", "custom agent", "profile") is a file — JSON, or on v3 also
Markdown with YAML front matter — carrying a `prompt`, a `tools` list, a `permissions` block
(v3; `allowedTools`/`toolsSettings` on v2, deprecated), optional `mcpServers`, `resources`,
`includeMcpJson`, `includePowers`, `model`, `welcomeMessage`. It lives in `~/.kiro/agents/`
(global) or `<workspace>/.kiro/agents/` (workspace). **Workspace agents take precedence by name**
(documented, and measured: a workspace `kiro_default.json` replaced the built-in default on v2).

Kiro's built-in default agent loads the global and workspace `mcp.json` on its own (measured
2026-07-10). What a *named* agent adds is its own prompt, tools, resources, and **agent-scoped**
MCP servers (`kiro-cli mcp add --agent <name>`), which are loaded only while that agent is active.

## How each engine selects one

Measured on kiro-cli 2.21.1, both engines.

| | v2 (kiro-cli 2.21.1 default) | v3 (`--agent-engine v3`) |
|---|---|---|
| Selection | `kiro-cli acp --agent <name>` launch flag | **`--agent` is refused at launch** (exit 2, like `--model`); every agent is an ACP session mode — `availableModes` lists them, `session/set_mode` switches |
| What `modes` means on the wire | agent configs: `kiro_default`, `kiro_planner`, `kiro_guide` (built-ins only; a workspace shadow of `kiro_default` does NOT appear as a separate entry) | agent configs: `vibe` (named **Default**), `spec`, `quick-spec`, `bug-fix`, …, plus every file-based agent, each tagged `_meta.kiro.source` (`bundled` / `workspace` / `user`) with `resource.source.root` |
| Default agent | `kiro_default` | `vibe`, display name "Default" — the docs use the name, the wire the id |
| A workspace agent named like the default | **silently replaces it** (its prompt ran; `kiro-cli agent list` still showed the built-in with the built-in's description) | is only *listed*; the default stays bundled `vibe` |
| Delegation to a workspace agent | not measured | `invoke_subagent` **asks** (`capability: subagent`), so it is not a bypass |

## Permissions, per engine

Measured on kiro-cli 2.21.1.

- **v2**: `allowedTools` names trusted tools. An "ask everything" profile (`allowedTools: []`,
  `permissions: all → ask`) changes **nothing measurable**: reads never ask on v2, writes always
  did. `--trust-tools=` (documented "trust no tools") **adds** trust and never removes the agent's.
  So v2 has **no lever** the host can pull, and a workspace file that grants allow is a grant the host
  cannot override, only detect (filesystem check for `<agentRoot>\.kiro\agents\<currentModeId>.json|.md`; not
  built).
- **v3**: `permissions.rules` = `{capability, match?, exclude?, effect: allow|deny|ask}`, resolved
  deny > ask > allow, most restrictive wins across scopes (`kiro`, `administration`, `user`,
  `workspace`, `agent`, `session`). **The `agent` scope may grant `allow`** (the server's scope
  table: `agent:["deny","ask","allow"]`), which is the v3 half of the #269 shape — reached only
  when the agent is *selected*, which the host now does only for a configured name and announces
  when the agent is repository-defined. An **ask-everything profile makes the engine ask us about
  everything, reads included** (measured: a read Default auto-allows arrived as
  `request_permission`).
- **A consent's `capability` is the MATCHED RULE's, not the tool's**: under an `all` rule a read
  reported `capability: "all"`. `AcpMapper` reads `_meta.kiro.toolId` when the capability says
  nothing (`fs_write` on a write, `read_file` on a read — two spellings, matched by root).

## Client-provided agents (v3 only) — the lever a repository cannot shadow

`session/new` and `session/load` parse `_meta.kiro`, whose schema includes
`customAgents: array (max 50)`. Each entry: `id` (required), `prompt` (required — an empty
string is accepted), `description`, `tools` (`"*"` or a list), `excludedTools`, `model`,
`includeMcpJson`, `includePowers`, `mcpServers`, `resources`, `permissions`, `welcomeMessage`.
Registered with source `client-provided`, origin `client`; **an entry whose id is a built-in mode
id is skipped** (so Default cannot be patched); a same-named file agent is **overridden** (logged
`agent.register.client.override`; measured — the client one ran over an allow-everything
workspace shadow, and the read asked). Selected afterwards by `session/set_mode` like any mode.

What running as a client agent means, measured with the identity question ("what are you, who made
you, list your first three rules"): with an **empty prompt** the agent still says it is Kiro, made
by AWS, and does its file work — Kiro's base prompt sits underneath a custom `prompt`. But Default
answered the rules question with three concrete rules and both client agents declined, so
**Default carries guidance of its own that a client agent does not inherit.** A feature built on
this runs as *our* agent, not Kiro's Default, by an amount that cannot be measured from outside.

## Why there is no agent setting

1. Its rationale over-claimed: "Kiro loads its MCP servers through the active agent config" is true
   only of **agent-scoped** servers; the global and workspace `mcp.json` load without it.
2. Pre-release is the only cheap time — a settings leaf is a cross-machine binding, and removing
   one after release orphans every install that set it.
3. It is the surface a "Code Wicket owns the Kiro agent" feature (the client-agent lever) would
   have to be exclusive with, since a client agent cannot be combined with a user-configured one
   without the host re-supplying theirs.
4. On v2 it was the second route by which a repository could redefine the running agent (a
   workspace file named for the configured agent), the first being the built-in default's name.

**What it costs**: custom Kiro agents (prompt, tools, resources, agent-scoped MCP servers) are not
reachable from Code Wicket until a designed feature brings them back. Kiro's own terminal is
unaffected.

## What remains, as an implementation tool

Kept deliberately, Kiro-specific, with no user-facing binding:

- `KiroProviderOptions.Agent` — the provider option. Nothing in the extension sets it; the Console
  proofs do (`--kiro-agent=`), and a future feature would.
- `KiroAgentProvider.BuildBaseLaunchArgs` passes it as `--agent` on v1/v2 and **not** on v3;
  `RequestedSessionModeId` returns it on v3, and the generic `AcpAgentSession.ApplyRequestedModeAsync`
  selects it by `session/set_mode` after the open — a name the engine does not offer, or refuses, is
  an **error notice** naming what the session runs as; a repository-defined one is applied and
  announced with a **warning notice**. Mode origins are read from `_meta.<vendor>.source` +
  `resource.source.root` and kept on the session.
- `AcpAgentConfig.SessionMeta` / `KiroProviderOptions.SessionMetaJson` — the raw `_meta` sent
  with every open, which is how a client-provided agent is supplied.
- The proof: `CodeWicket.Console backend-gate kiro <control|hole|hole-delegate|trust-none|ask-read-write|identity>`
  with `--engine=v3`, `--kiro-agent=NAME`, `--agent-name=NAME` (the workspace file's name), `--md`,
  `--ask`, `--client-agent`, `--empty-prompt`; `CWKT_KIRO_ARGS` appends raw launch args.
- Tests: `BackendSessionListLaunchTests` (flag on v2, wire on v3), `AcpConfigOptionTests` (the
  requested-mode outcomes), `AcpMapperPermissionTests` (the `all`-rule consents).

## What a feature would have to decide

1. **Custom agents back, as a picker or a setting?** A picker can show each agent's origin on v3
   (the handshake tags it) and must say so; on v2 origin is a filesystem check. Where the agent is
   repository-defined, the warning is the honest treatment, not a refusal — the user chose it.
2. **"Kiro asks about everything"** (the client-agent lever): opt-in, beside a planned **Agent Auto** entry in
   our permission picker (issue #261: the backend's own classifier mode, declared in
   `BackendPermissionModes` and mapped to nothing yet); exclusive with a custom agent (or re-supply theirs — which means parsing `.md` and
   `.json` agent files, resources and MCP, i.e. re-implementing Kiro's loader); what prompt to
   send; that Default's own rules are lost; that it is **v3 only** — v2 users get detection only.
3. **Agent-scoped MCP servers**: the one thing the removed setting genuinely provided. A client
   agent can carry `mcpServers` and `includeMcpJson`, so the lever and this are the same feature.
4. **Surface it** (#270) — **built.** The session info panel's mode row shows what the agent reports
   running as — on Kiro the agent, on Claude Code the permission mode — with sub-rows for **Opened in**
   (where the agent's own settings chose differently), **Not applied** (Code Wicket's mode pin did
   not land), **Defined by** (the mode's origin, a repository path included) and **Configured** (an
   agent this session was configured to run as that was not applied). The fields
   ride the DTO (`ModeLabel` and its siblings in `Dtos.cs`) into `SessionInfo.cs`, pinned by
   `SessionInfoWireTests`.
