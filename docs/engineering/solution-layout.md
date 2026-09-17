# Solution layout (full detail)

> The reasoning and measurements behind AGENTS.md's one-line summary of each project. The three
> biggest have their own docs: [chat-ui.md](chat-ui.md), [ide-services.md](ide-services.md),
> [vsix-shell.md](vsix-shell.md).

> **`#NN` refers to a private pre-release tracker.** The numbers are stable *names* for a failure
> mode, not links — the rule each one tags is stated here in full, so nothing is missing if you
> cannot open them.

> Terms used here without definition (the Exp hive, rungs, warm start, the prove-check verdicts, …) are
> in [AGENTS.md](../../AGENTS.md#terms-the-docs-use-without-defining-them).


## CodeWicket.Core

**`CodeWicket.Core`** — host/backend-agnostic contracts: `IAgentProvider`, `IAgentSession`, closed `AgentEvent` hierarchy, `IIdeServices` facade (`IWorkspaceContext`/`IEditApplier`/`IToolCatalog`/`IPermissionHandler`), `AgentProviderRegistry`. Also `OrderedDispatchSynchronizationContext`, the FIFO inbound-dispatch pump both JSON-RPC hops set on their `JsonRpc` (#33 on the ACP hop, #277 on the shell hop) — here because the shell hop's slice is net472 and `Providers.Acp` is net10-only. No VS/ACP/JSON deps — raw payloads cross as JSON strings.

## CodeWicket.Providers.Acp

**`CodeWicket.Providers.Acp`** — the **generic, config-driven ACP layer** (protocol handling is provider-agnostic; only launch config is per-agent). `AcpAgentConfig` (CliPath/LaunchArgs/Env/Models/Capabilities as data), `AcpAgentProvider`, `AcpAgentSession` (the ACP driver). Drives the CLI over newline-delimited JSON-RPC (StreamJsonRpc `NewLineDelimitedMessageHandler` + `SystemTextJsonFormatter`). Transport seam = internal `IAcpConnection` (`ProcessAcpConnection` kills the **whole process tree** — npm `.cmd` shims wrap node; the agent's **stderr drains to ours** prefixed `[<cli>]` — that's where SDKs report MCP failures, and an undrained pipe can stall the child). `SendAsync` prepends a `<workspace-context>` block. `CWKT_ACP_LOG` and `CWKT_MCP_LOG` each tee **both** directions into **ONE** file, stamped per frame (`Core.FrameLogSink` + `Core.FrameTee*`, #211 and its MCP follow-up) — the `<path>.send` companions are gone, and `FrameLogSinkTests` pins the absence of both, because reading either half alone looks complete while being half the conversation. **The frame-aligned buffering is what makes one file safe**, not the path: a raw byte tee interleaves mid-frame, which is why the MCP merge had to replace its tees rather than repoint them. **The direction marks are written from OUR seat** — `->` is what this process sent — so on the ACP log that is client→agent and on the MCP log server→client, the opposite parties. **`Shell.TeeStream` (the engine channel) is deliberately still two raw files**: it exists to catch a channel that is *not* well-formed, which is the one thing a frame-aligned sink assumes. A new ACP agent = a new `AcpAgentConfig` in a thin per-agent project — **or pure user config**: `AcpAgentConfigJson.ParseList` parses the config.json `customAcpAgents` array (JSON shape = `Shell.CustomAcpAgent`, keep in sync) into `AcpAgentConfig`s the engine registers as plain `AcpAgentProvider`s (built-ins win on id collision; bad entries warn + skip, never throw).

## CodeWicket.Providers.Kiro

**`CodeWicket.Providers.Kiro`** — `KiroAgentProvider : AcpAgentProvider`, a thin subclass supplying Kiro's config (`kiro-cli`/`["acp"]`) + Kiro's optional flags (`--model`, and `--agent` when `KiroProviderOptions.Agent` is set) + the live model catalog (`KiroModelCatalog` shells out to `kiro-cli chat --list-models`).

**Kiro loads its *own* user-configured MCP servers through the active *agent config*, NOT our IDE-tool MCP bridge (which is injected separately via ACP `SessionOptions.McpServers`).** `kiro-cli acp` with no `--agent` runs the built-in `kiro_default` agent (`tools:["*"]`, `includeMcpJson:true`), which auto-inherits the global/workspace `mcp.json` (`~/.kiro/settings/mcp.json`, `<ws>/.kiro/settings/mcp.json`) servers — but NOT servers scoped to a *named* agent (`kiro-cli mcp add --agent <name>` writes into that agent's `mcpServers`; a plain `mcp add` writes to global mcp.json). So agent-scoped MCP servers are reachable only while that agent runs — and **the user-facing agent setting was removed before release (2026-09-11)**, so they are not reachable from Code Wicket; `KiroProviderOptions.Agent` remains an implementation tool and the whole record is [kiro-agents.md](kiro-agents.md). (A non-default agent also needs `includeMcpJson:true` or inline `mcpServers`, AND the MCP tools referenced in its `tools` array — `"*"` or `"@server"`.)

## CodeWicket.Providers.ClaudeCode

**`CodeWicket.Providers.ClaudeCode`** — `ClaudeCodeAgentProvider : AcpAgentProvider` driving the ACP adapter for the Claude Agent SDK (npm **`@agentclientprotocol/claude-agent-acp`**, bin `claude-agent-acp` — a `.cmd` shim on Windows, so the default CliPath is `claude-agent-acp.cmd` there; CreateProcess only appends `.exe` when searching PATH). Auth rides the user's Claude Code login (or `ANTHROPIC_API_KEY`); needs Node ≥ 22. Caps incl. `ResumeSession` (adapter advertises `loadSession`) **and `ModelSelection`** — the adapter surfaces its live model list via ACP **session config options** (category `model`) in `session/new`, not the old `session/set_model`; the generic ACP session handles both.

## CodeWicket.Ipc

**`CodeWicket.Ipc`** — shared engine↔shell JSON-RPC contracts (`RpcMethods`, flat DTOs, `DtoMapping` Core↔DTO). Multi-targets net472;net10.0 (has its own `IsExternalInit` polyfill).

## CodeWicket.Shell

**`CodeWicket.Shell`** — host-side plumbing shared by every WPF host (net472;net10.0, no WPF/VS deps). `EngineClient` (launches/supervises the engine process, owns the JSON-RPC connection), `ShellRpcTarget` (serves `shell/*` IIdeServices callbacks + `shell/onAgentEvent`), `StubIdeServices` (disk-backed, auto-allow), `PolicyPermissionHandler`/`UiPermissionRouter`, `ExtensionConfig` (the unified settings model — [settings-and-logging.md](settings-and-logging.md)).

## CodeWicket.UI

**`CodeWicket.UI`** — the shared WPF chat (`ChatViewModel`, `ChatView`, themed brush keys via `DynamicResource`), multi-targeting `net472;net10.0-windows` so the VS tool window and the Desktop host draw the same control. Everything about it is in [chat-ui.md](chat-ui.md).

## CodeWicket.Desktop

**`CodeWicket.Desktop`** — standalone WPF host (fast UI iteration, no VS), multi-targeting **`net472;net10.0-windows`**. Launches the engine via `EngineClient` (fake provider) + `StubIdeServices`. `--smoke` = headless self-check (exits 0/1; also proves transcript persistence via a 2nd view-model save→restore→replay); `--screenshot` = renders the window to PNG (captures the first pending permission banner); `--screenshot-history` = seeds sample sessions and renders the **history popup** to PNG (popups live in their own hwnd, so it captures `popup.Child` directly — reuse this pattern to screenshot any popup/flyout headlessly). Dev knobs are CLI args: `--permission-mode <m>`, repeatable `--allow`/`--deny <glob>`.

### Why it multi-targets (issue #106)

devenv is net472, so the **net472** slice of `CodeWicket.UI` is what ships: its BAML, its theming, the `Markdig.Signed`/ColorCode/Emoji.Wpf closure, and the VS-SDK-baseline StreamJsonRpc that `Shell` pins only on that TFM. Every other offline check we run — unit tests, Console proofs, the other Desktop modes — is net10, so before this the shipping slice was exercised by **nothing**: a net472-only breakage passed every gate. `scripts/run-smoke.ps1 -Framework net472` runs the same self-check there, and both slices are release gates.

Scope, precisely: this proves the UI **works** on net472, not that it survives #103's pkgdef name capture. A standalone host loads no competing extension. Broader coverage, not different coverage — only a live Visual Studio instance carrying another extension that registers a `codeBase` for a simple name we also ship tests the conflict itself.

Two mechanical consequences:

- **Keep the host TFM-neutral.** `System.Index` (`^1`), `Math.Clamp`, and the interpolated-string-handler overloads `StringBuilder.AppendLine(IFormatProvider, …)` / `string.Create(IFormatProvider, …)` are all net6+ and fail to compile on net472. `PerfHarness.Inv` is the invariant-formatting spelling both TFMs share. Watch one trap: an **addition** of interpolated strings converts to a handler as one unit, but **not** to `FormattableString` — so `Inv($"a" + $"b")` would concatenate under the current culture before `Inv` ever saw it. Wrap each fragment.
- **The engine `ProjectReference` needs `SkipGetTargetFrameworkProperties` *and* `UndefineProperties="TargetFramework"`.** The engine is net10-only and consumed as a process, never as an assembly. The VSIX needs only the skip because it is single-TFM; a multi-targeting project dispatches its inner builds with `TargetFramework` as a **global** property, which flows across the reference and fails NETSDK1005 regardless of the skip. `EnginePathResolver` needed no change — it walks up to the `.slnx` and both output dirs sit at the same depth (verified by running).

### `--perf` on net472

A **measurement run, not a gate**. Default sweep, same box, Debug: **net472 7m21s against net10 4m41s** (~1.6×), both producing the same 85-line report. Worth running on net472 whenever the question is performance — devenv is net472 and #86 is a report against that framework, so a net10 sweep measures one no user runs.

Three rules hold for any `--perf` run:

- **`--perf` always exits 0.** It catches its own exceptions into a `FAILED:` report and finishes on `Shutdown(0)`, so the exit code carries no information. **The artifact is the result** — check `perf-result.txt` exists and is fresh. A run that did nothing otherwise reads as a pass.
- **A timeout is not a measurement.** A killed run shows only that the sweep *grinds* rather than deadlocks — a pegged CPU at the kill says nothing about the ratio, and "did not finish inside the timeout" is not a speed. The 1.6× above comes from a *completed* run on each side. The sweep is long on both TFMs regardless — the drag and large-reply passes use **hardcoded** sizes (500 items; 120 × up to 256 repeats), so `--perf-items`/`--perf-iterations` shrink almost nothing. It overran `run-smoke.ps1`'s old 300 s perf default on both; now 1800 s, `-TimeoutSeconds` to override. The report is written only at the end, so a timeout kill loses the whole run.
- **Never rebuild while a perf run is in flight.** Replacing its assemblies mid-run gives an "exit 0" (4m14s, measured) that writes no report at all — which, combined with the always-zero exit above, looks exactly like a successful fast run.

## CodeWicket.Engine

**`CodeWicket.Engine`** — net10 out-of-proc host exe. `EngineHost`/`EngineService` (startSession/prompt/cancel/**summarize**; holds an `AgentProviderRegistry`, picks per-session via `StartSessionRequest.ProviderId`; `engine/summarize` runs an isolated throwaway ReadOnly session and returns a summary for "resume from summary" without touching the active session), `IpcIdeServices` (proxy forwarding IIdeServices over IPC), `Mcp/McpToolServer` (surfaces `IToolCatalog` as MCP), `FakeAgentProvider` (test double exercising every IIdeServices facet). `Program` = the stdio entrypoint the VSIX launches; also `--mcp-stdio` relay mode.

## CodeWicket.Console

**`CodeWicket.Console`** — headless host + the proofs. **A proof is run by hand, one mode per run, and is not a gate**: `dotnet run --project src/CodeWicket.Console -- <mode> [args]`. The mode is the first argument with any leading dashes trimmed, so `engine` and `--engine` are the same mode; **no argument, or an unrecognised one, runs the default** in-memory proof — a mistyped mode passes quietly. A proof exits 0 on PASS and 1 on FAIL unless its row says otherwise. Two need no backend and also run in the release workflow: the default and `engine`.

**Needs**, in the table: *none* is offline and in-memory; *kiro* launches a real `kiro-cli acp` and needs it signed in; *claude* launches the real Claude Code adapter and needs a Claude Code sign-in or `ANTHROPIC_API_KEY`; *relay* means it also starts the engine's `--mcp-stdio` relay through `dotnet`. `CWKT_KIRO_ARGS` appends raw launch args for the Kiro proofs: those that build their provider through `CreateKiroProvider()` (`kiro`, `kiro-edit`, `kiro-read`, `kiro-mcp`, `kiro-vstools`, `claude-steer-boundary kiro`, and `kiro-tasks`/`kiro-resume` when no engine argument is given), and `mcp-ready` and `backend-gate`, which read it themselves; the others set their own launch.

| mode | args | proves | needs |
|---|---|---|---|
| *(default)* | — | A real `AcpAgentSession` driven by an in-memory fake agent routes the agent's file write through the host `IEditApplier`. | none |
| `engine` | — | The shell↔engine round trip over in-memory streams with `FakeAgentProvider` (start, set model, a prompt that routes an edit back); `engine/takeImportedHistory` returning entries in wire order across pages; the in-engine `McpToolServer` answering `tools/list` and a call. | none |
| `clientfs-errors` | — | A failed `fs/write_text_file` or `fs/read_text_file` (access denied, file in use, bad path, not found) reaches the agent with its reason intact after both RPC hops — real `ShellRpcTarget`, `IpcIdeServices` and `AcpClientTarget`, only the applier faked. | none |
| `notice-replay` | — | Captured Kiro frames sent into `AcpClientTarget` produce a `BackendNotice` exactly where expected (rate limit, usage limit, compaction completed) and none for compaction started or a plain usage reading. | none |
| `codefix-actions` | — | Against the real Roslyn C# fix providers on an `AdhocWorkspace`, grouped actions still refuse to apply and every flattened, filtered fix applies. Its logic is linked from `CodeWicket.Ide`, not copied. | none (Roslyn runtime from NuGet) |
| `mcp-bridge` | — | An MCP client talking to a child `CodeWicket.Engine --mcp-stdio <pipe>` gets `tools/list` and a tool call answered by the pipe host in this process. | relay |
| `legacy-tests` | `[work root]` | A freshly built old-style MSTest project is classified as legacy VSTest, the shipping `vstest.console` arguments run its tests, and class and method filters narrow correctly. **Exits 0 with SKIP when no Visual Studio install is found.** | a Visual Studio install; no backend |
| `project-settings` | `[refused]` | Default leg: `agentWorkspaceRoot` in `.code-wicket/settings.json` moves the working directory to a folder the marker walk cannot reach, and Kiro loads that folder's steering. `refused` leg: a key the schema does not honour is named in one notice and nothing is applied. | default leg kiro; `refused` none |
| `kiro` | — | Nothing is asserted: one turn in the current directory, events printed. **Always exits 0.** | kiro |
| `kiro-edit` | — | Kiro writes a file itself, and the edit reaches the host as an `EditProposed` with the right before and after, the file on disk. | kiro |
| `kiro-read` | — | Every read-kind tool call names its target file in its raw input. | kiro |
| `kiro-steering` | — | For a solution two levels below a repository root, the working directory widens to that root and the agent answers from `.kiro/steering` there. | kiro |
| `kiro-tasks` | `[engine]` | A multi-step task prompt produces `PlanUpdated` events ending all completed, with no task-tool row leaking into the transcript. | kiro |
| `kiro-resume` | `[engine]` | After resuming a conversation by id the agent still knows what it was told, and the replay leaks no transcript content. | kiro |
| `kiro-v3-models` | — | On the v3 engine the model selector arrives by notification after `session/new`, the launch model is applied and a mid-session switch works. **Reports PASS with the turn skipped when the backend refuses it** — an exhausted plan streams its refusal as assistant text, so "saw text" alone must never stand in for a completed turn. | kiro (v3) |
| `kiro-mcp` | — | Kiro calls our IDE-tool MCP server through the pipe relay and announces it connected. | kiro, relay |
| `kiro-vstools` | — | Kiro finds and calls a no-argument IDE tool (`build_solution`) from our catalog's definitions. | kiro, relay |
| `kiro-image` / `claude-image` | — | The model reads back the colour bands of a generated image in an **unguessable order** — the only way to separate *the backend accepted the block* from *the pixels arrived*, which no offline test can reach. **The regression it guards is the silent one**: an image dropped in transit looks exactly like one delivered. | kiro / claude |
| `claude` | — | A real Claude Code turn streams text and ends `end_turn`. | claude |
| `claude-models` | — | The session reports a model list and a current model, a switch to another model lands, and a turn still completes. | claude |
| `claude-edit` | — | Claude writes a file and the write reaches the host, reporting whether it came through `IEditApplier` or as an `EditProposed`. | claude |
| `claude-mcp` | — | Through the engine exe (the VSIX topology), Claude calls our MCP tool via the relay and the turn completes. | claude, relay |
| `engine-claude` | — | A real Claude turn through the out-of-process engine exe (the VSIX topology) streams text, and both debug tees capture bytes. | claude |
| `claude-steer` | — | A mid-turn steer is injected, its content arrives, and exactly one turn completion fires. **0 = SKIP** when the adapter does not advertise steering, **2 = INCONCLUSIVE**. | claude |
| `claude-steer-tools` | — | A steer asking for a file write is injected; its tool events and permission request arrive after the interrupted turn closed, and the file is written. **0 = SKIP, 2 = INCONCLUSIVE.** | claude |
| `claude-steer-boundary` | `[mcp\|bash\|retention\|compare\|kiro\|kiro-v3]` | **A measurement**: whether a steer or a cancel landing during a running tool call destroys it — our MCP tool against a built-in shell call — and whether the rest of the turn survives. Exits 0 on any result, 2 on INCONCLUSIVE. | claude (the `kiro*` sub-modes: kiro), relay |
| `claude-subagents` | — | In a real sub-agent fan-out, every child call carrying a parent id points at a launch row that was seen, and a background launch's receipt text reaches no row. **2 = INCONCLUSIVE** when the model did not delegate. | claude |
| `claude-sessions` | `[workspace] [session id]` | `session/list` for a directory maps every session seen on the wire, and loading one with history import turns the replay into user and agent entries. Sends no prompt. **2 = nothing to list or import.** | claude, plus existing conversations for that directory |
| `custom-agent` | — | A `customAcpAgents` JSON entry for the Claude adapter passes the real `initialize` probe, and a plain `AcpAgentProvider` built from it takes its resume capability from the probe and streams text. | claude |
| `resume-cross-root` | `[claude\|kiro] [kiro engine]` | Characterises how a backend answers a resume of a real conversation id from a different working directory: refuses, carries the context, or succeeds empty. **Always exits 0.** | claude or kiro |
| `resume-unknown-id` | same | The same characterisation for a well-formed id the backend never issued. **Always exits 0.** | claude or kiro |
| `backend-gate` | `<claude\|kiro> [cases] [flags]` | Whether the backend's OWN permission gate still asks once a file in the working tree has told it not to, and whether the host's mode assertion puts it back (#269). Its instrument is a permission handler that rejects everything, so a probe file that exists was approved by someone else, and a control run gates every verdict. Kiro's cases and flags: [kiro-agents.md](kiro-agents.md). **Exits 1 on any FAIL or INCONCLUSIVE verdict.** | claude or kiro |
| `mcp-ready` | `<kiro\|claude> [immediate\|served\|announced] [--engine=v3]` | **A measurement**: WHEN after `session/new` a prompt can rely on our IDE tools, and which signal says so. Exits 0 whenever every session opened; the verdict is the timeline table. | claude or kiro, relay |

Three proof shapes worth knowing: a proof **need not need a live backend** (`clientfs-errors`); a proof **can ask what no offline test can reach** (`kiro-image`); and a proof **may need a runtime the shipping code deliberately lacks** (`codefix-actions`).

## CodeWicket.Ide

**`CodeWicket.Ide`** — net472, loaded in-process in devenv: the real VS-backed `IIdeServices` and every agent-facing IDE tool. Everything about it is in [ide-services.md](ide-services.md).

## CodeWicket.VSExtension

**`CodeWicket.VSExtension`** — net472, the thin VSSDK shell: the package, commands and menus, the tool window, the settings registration, and the build step that bundles the engine into the VSIX. Everything about it, including running it in the experimental instance, is in [vsix-shell.md](vsix-shell.md).

## CodeWicket.Tests

**`CodeWicket.Tests`** — the unit suite (xUnit), `net10.0-windows` and therefore Windows-only, run by `dotnet test` and by the `tests` gate in `scripts/run-gates.ps1`. It references `Core`, `Shell`, `Providers.Acp`, `Providers.Kiro`, `Ipc`, `UI` and `Engine` — the last so the engine end of the shell↔engine hop can be driven over in-memory streams. It ships in nothing, so it references freely.

Two net472-only sources are **linked** into it rather than referenced, because a net10 project cannot reference their assemblies: `BuildStamp.cs` from the VSIX, and `CodeFixActions.cs` from `Ide` together with the Console's `CodeFixProbe.cs`, run against a real Roslyn runtime the shipping `Ide` deliberately does not carry. The suite also holds the published-tree scans built on `SourceTree.PublishedFiles()`, which is why a change to any published file, prose included, can turn it red.
