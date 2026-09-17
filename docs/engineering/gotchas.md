# Gotchas (full detail)

> The reasoning and measurements behind AGENTS.md's gotchas: the diagnosis under each one, and the
> failure mode that produced it.

> **`#NN` refers to a private pre-release tracker.** The numbers are stable *names* for a failure
> mode, not links — the rule each one tags is stated here in full, so nothing is missing if you
> cannot open them.

> Terms used here without definition (the Exp hive, rungs, warm start, the prove-check verdicts, …) are
> in [AGENTS.md](../../AGENTS.md#terms-the-docs-use-without-defining-them).


## A StreamJsonRpc response can overtake the notifications that preceded it on the wire

**A StreamJsonRpc response can overtake the notifications that preceded it on the wire** (issue #33 — a turn's last streamed text chunks never reached the transcript). Inbound notifications are dispatched off the read loop (FIFO among themselves — text never scrambles), but the completion of a request *we* sent takes a different path and can jump that queue. `AcpAgentSession` closed the turn channel on the `session/prompt` result, so any `session/update` still queued behind it wrote to a completed channel and was **silently dropped** by `EmitToCurrentTurn` — tail-only loss, and worse, a straggler landing after the next `SendAsync` leaked the previous turn's tail into the *next* turn's transcript. Fix = `JsonRpc.SynchronizationContext = OrderedDispatchSynchronizationContext` (our own FIFO pump; set before `StartListening`) + `DrainInboundDispatchAsync()` posting a sentinel through that same queue as an ordering **barrier** before the turn writes `TurnCompleted`/`SessionError` and completes the channel (bounded 5s — a wedged handler must not hang the turn). The pump is non-blocking by construction: an `async` handler parked on the user (a permission request) releases it at its first `await`. Pinned by `AcpTurnTailTests` (blasts N chunks then answers the prompt with no pause; pre-fix it lost all but the first chunk of turn 0). Any future "the agent sent it but we never showed it" report starts here: diff acp.log against the transcript, and check whether the loss is at a turn boundary.

**Both hops need it, and the shell ↔ engine hop's fix (#277) has three parts.** Without them `EngineClient`'s `JsonRpc` has no `SynchronizationContext` and `PromptAsync` no barrier, so although `EngineService` sends its notifications in order (it awaits each `shell/onAgentEvent` write before answering `engine/prompt`), the shell's inbound dispatch can still apply a turn's last event after the `PromptResponse` continuation has cleared `IsBusy`. Seen as #267 — kiro-cli-chat 2.21.1, 2026-09-10, two back-to-back throttled prompts byte-identical on `acp.log`: prompt `id=4` won the race and prompt `id=5` lost it, `[out-of-turn] opened by 'error'` 5 ms after the error and closed 45 s later with nothing open. The frame exclusion that fixed #267 stays (an `error` is excluded for what it IS); #277 is the general fix, and it has three parts rather than the two the ACP hop has:

1. **`OrderedDispatchSynchronizationContext` moved to `Core`** (net472;net10 — the shell runs in-proc in devenv) and is set on `EngineClient`'s `JsonRpc` before `StartListening`. `SynchronizationContext` is mscorlib, so the identity-split policy has nothing to bite on.
2. **Every `EngineClient` call drains before its response is handed on** (`AfterInboundDrainAsync`, bounded 5 s, logged on timeout as `[rpc] EngineClient #n inbound drain did not complete`). Every call, not only `PromptAsync`: the issue's own list of "and which others? `SteerAsync`? `StartSessionAsync`'s opening frames?" is a question that has to be re-asked per call site otherwise, and the wrong answer is an absence. On every exit path too — the host acts on a faulted prompt (error card, `IsBusy` false) exactly as on a result.
3. **The host applies its queued events INLINE before acting on a response.** The barrier orders the handler's RAISE, and `ChatViewModel.OnAgentEvent` then hops to the UI thread; the response's `ConfigureAwait(true)` continuation is another hop. Dispatcher FIFO carries the order only within one priority, and a `DispatcherOperation` flows ITS priority into the `DispatcherSynchronizationContext` the awaits inside it capture (`BaseCompatibilityPreferences.FlowDispatcherSynchronizationContextPriority`, default on) — so a send begun inside a Send-priority `Invoke` posts its turn-end continuation at Send and it outranks every Normal-priority event post. `TurnTailAppliedBeforeTurnEndTests` begins its send exactly that way and fails deterministically with the inline apply removed. The events now go into a `ConcurrentQueue` with one posted drain at a time; `SendCoreAsync`'s catch and finally call `ApplyQueuedLiveEvents()` before the error card and before `IsBusy = false`.

**The pump runs one handler at a time, and on this hop the handlers are the REAL ones.** On the ACP hop every client-side handler is an RPC proxy into the shell — async at its first line. On the shell hop `ShellRpcTarget.PermissionAsync` reaches `VsPermissionHandler`, whose message-box fallback blocks its caller for as long as the dialog is open, and a tool may do real work before it first yields; run on the pump, either holds every event behind it. So the five request handlers leave the pump at once via `OffPump` (`Task.Run` — the pool, which is where StreamJsonRpc ran them before the pump existed, with no ambient `SynchronizationContext`), while the two notification handlers stay on it. `ShellHopOrderingTests.ABlockingRequestHandlerDoesNotStallTheEventsBehindIt` parks a handler on a `ManualResetEventSlim` and asserts the events behind it still arrive, for both the permission and the tool request.

Pinned by `ShellHopOrderingTests` (a real `EngineClient` over `FullDuplexStream` against a fake engine that blasts 200 events and answers with no pause — the `AcpTurnTailTests` shape one hop up; `EngineClient.Attach(Stream, Stream, …)` exists for it) and `TurnTailAppliedBeforeTurnEndTests`. The first is a race check: per verification.md the verifier is `run-gates.ps1`, and a solo run is evidence only when it fails.

## An inbound JSON-RPC handler bound to a TYPED params record silently loses frames (the NUL-padded-span gotcha, inbound edition).

StreamJsonRpc's `SystemTextJsonFormatter` deserializes a typed parameter from a rented buffer that is larger than the message, so STJ reads past the JSON's end into the padding and throws `'0x00' is invalid after a value ... BytePositionInLine: <message length>`. Asking for a raw **`JsonElement`** instead hands back the already-parsed document and sidesteps it entirely — the known cure, previously recorded only for the `set_config_option` *response*.

**On a notification there is no error anywhere**: StreamJsonRpc has nobody to answer, so the frame simply never reaches the handler and every symptom is an absence. That is exactly how Kiro v3's `config_option_update` (the frame carrying its model list) vanished while `CWKT_ACP_LOG` proved it arrived — the tee taps the raw stream, so **a frame in acp.log is NOT evidence the handler ran**. Every other `session/update` on the same connection was delivered; only the big one was lost, so it reads as content-specific when it is really size-specific. Diagnosis that works: `_rpc.TraceSource.Switch.Level = SourceLevels.Warning` + a `TextWriterTraceListener` before `StartListening` prints the swallowed exception (it also prints one `No target methods are registered that match "_kiro/…"` line per unhandled Kiro extension notification — those are expected). `AcpClientTarget.OnSessionUpdate` therefore takes a `JsonElement` and projects it via `SessionUpdateParams.From`; the record survives as the internal shape the tests and the rest of the method use.

**The remaining typed inbound params (`RequestPermissionParams`, the fs ones) are requests, so the same failure would at least surface as an RPC error rather than silence — but they carry whole-file text and are the next candidates if a large permission/fs frame ever "doesn't arrive".** Pinned by `AcpConfigOptionTests.LargeSessionUpdateFrameIsNotDropped` (a 20K chunk through a real session).

## kiro-cli reports a failed get-kas-token on STDOUT and exits 1 with an EMPTY stderr

**kiro-cli reports a failed `get-kas-token` on STDOUT and exits 1 with an EMPTY stderr** (2.13.0, measured 2026-07-29). The v3 auth callback (`KiroKasTokenProvider`) checked the exit code first and threw `get-kas-token exited {code}: {stderr}` — so the transcript got `Auth refresh callback failed: get-kas-token exited 1: `, with the trailing colon and nothing after it, while the one actionable line (`{"kind":"error","data":{"message":"You are not logged in. Please log in with \`kiro-cli login\`."}}`) sat unread on stdout.

**Parse the payload before trusting the exit code**; only an explicit `kind:"error"` frame's `data.message` is relayed (human guidance, never a credential — which is why this quotes a VALUE where `ParsePayload` deliberately reports only property names). Pinned by `KiroKasTokenTests`. Generalise: for this CLI the exit code is the *least* informative channel.

**`kiro-cli whoami` is NOT a usable liveness pre-flight** — it reported `Logged in with Builder ID` (exit 0) at the same moment `get-kas-token` reported not-logged-in, and flipped to `Not logged in` only *after* a refresh attempt failed. It reads cached credentials; the refresh is what discovers they're dead, and a rejected refresh clears the store. So the *first* symptom of an expired Kiro login is a v3 turn failing, with the model picker still fully populated from `chat --list-models`' cached-credential read — a combination that reads as "auth is fine, the feature is broken".

## A credential-less kiro-cli starts an OAuth *loopback* login inside whatever headless process we spawn it in, and killing that process breaks the login — so merely opening the chat window could strand one.

Measured 2026-07-29: the browser appeared at startup with no Kiro session ever started, which places it in `KiroModelCatalog.TryList` (runs on `engine/listProviders`), not in `acp`; the user's browser showed a failing **localhost** URL, and `kiro-cli login --use-device-flow`'s help ("for instances where browser **redirects** cannot be handled") confirms the default flow is a 127.0.0.1 redirect. So the CLI opens the browser, listens on a loopback port for the redirect back, and `RunKiroJson` tree-kills it 10s later — the IdP then redirects to a **refused port**. The user authorises and nothing happens: that is the original "auth link that doesn't resolve", and it is caused by *us* launching the CLI headless, not by the CLI alone.

**Two different levers, because the two subcommands differ:** `chat` takes **`--no-interactive`** (exit 1 in ~400ms with "Not logged in. Set the KIRO_API_KEY environment variable or run `kiro-cli login` first", nothing opened) — that's the catalog's fix and it is strictly better than a pre-flight: one process, no extra latency, and it also covers credentials that are *present but dead*, which `whoami` reports as logged in. `acp` **rejects `--no-interactive`** (clap exit 2), so the session launch instead goes through `KiroLoginState.IsSignedOut`, wired via the data-driven `AcpAgentConfig.PreflightCheck` hook (same shape as `AuthTokenProvider`).

**That probe is `chat --no-interactive --list-models`, deliberately NOT `whoami`** — per the note above, `whoami` calls a dead token a good login, so gating on it would wave through exactly the expired-credential case, which is the one most likely to strand a browser. `KiroModelCatalog` calls `NoteSignedIn()` when the same command succeeds for the picker, so on the normal path (window opens → picker populates → user sends) the gate spawns nothing at all.

**`IsSignedOut` is true only when the CLI's own "not logged in" wording appears**; a missing CLI, a timeout, a proxy failure or any unrecognised error all return false — turning "can't tell" into "you aren't signed in" would block a session AND send the user to fix a login that may be fine, so the match fails OPEN. A positive is cached for the engine's lifetime; a negative never is, so signing in from a terminal recovers without restarting VS. If `acp` ever gains `--no-interactive`, prefer it and delete the pre-flight.

**Residual, and it is not closable by any probe:** only a real model call proves a token is live, so the gate reduces the window rather than shutting it — which is why the downstream failure paths (Kiro's `authMethods` guidance, `KiroKasTokenProvider` relaying the CLI's message) have to stay good regardless. Pinned by `AcpPreflightTests` (incl. the flag itself, via `KiroModelCatalog.ListModelsArgs` — it reads like boilerplate and is one token from being tidied away). Note `KIRO_API_KEY` is a headless auth route the CLI mentions, reachable via `EngineEnvironment`.

## A stored ConversationId is not portable across Kiro's agent engines, and a refused session/load must not be fatal.

A v3-created `sess_…` replayed against v2 answers `-32603 "Internal error"` with `data:"Failed to start session: Session not found: sess_…"`; the same happens when the backend pruned the conversation, or when it never persisted because its first turn failed (exactly what an auth failure causes — which is how one logged-out session poisons the *next* start with an unrelated-looking error). `AcpAgentSession` falls back to `session/new`, reports why via `IResumeFallbackReport` → `StartSessionResponse.ResumeFailureReason` → a transcript notice, and start failures now unwrap the JSON-RPC error `data` the way a failed turn always did — the user was getting the bare word "Internal error" because only turns went through `DescribeError`.

**The notice is not optional politeness:** the transcript still shows the whole earlier conversation, so an agent that silently has none of it reads as having *forgotten*. Pinned by `AcpResumeFallbackTests`.

## An ACP agent states how to sign in exactly once, in initialize's authMethods

**An ACP agent states how to sign in exactly once, in `initialize`'s `authMethods`** (Kiro: `[{id:"kiro-login", description:"Run 'kiro-cli login' in terminal to authenticate. See https://kiro.dev/docs/cli/authentication/"}]`). We parsed the field and never read it. `AcpAgentSession.AuthGuidance` now holds it and `Describe` appends it to auth-shaped failures only — the match is deliberately narrow, because sending someone to fix their login when the failure was a rate limit is worse than saying nothing. An **empty** array is the norm for agents carrying their own auth (Claude Code sends `authMethods:[]`), so its absence signals nothing and is never reported.

## RemoteMethodNotFoundException does NOT derive from RemoteInvocationException

**`RemoteMethodNotFoundException` does NOT derive from `RemoteInvocationException`** — both sit under `RemoteRpcException`, so `catch (RemoteInvocationException) when (ex.ErrorCode == -32601)` misses the canonical -32601 (StreamJsonRpc raises the dedicated type for that code). Catch `RemoteRpcException` and test both. Confusingly, the *same* failure relayed across our engine→shell IPC arrives as a plain `RemoteInvocationException`, which is what the user-visible stack trace shows — so the type you see reported is not the type the engine caught.

## StreamJsonRpc AddLocalRpcTarget only supports EventHandler/EventHandler<T> events

**StreamJsonRpc `AddLocalRpcTarget` only supports `EventHandler`/`EventHandler<T>` events** — never put an `Action<T>` event on an RPC target (use a plain callback delegate; that's why `ShellRpcTarget` takes a delegate).

## In-proc StreamJsonRpc in devenv

**In-proc StreamJsonRpc in devenv:** our Shell pipes must use a **private `MemoryPool`**, not `ArrayPool.Shared`. Other in-proc consumers recycle shared-pool buffers as designed, and a channel that assumed a rented buffer was its own is corrupted by it — the guarantee has to hold against any co-tenant, so the pool must be ours.

## In-proc assembly binding (net472) — the identity-split gotcha.

Which copy of any assembly our in-proc code binds to is decided by **that VS build's own `devenv.exe.config` binding redirects**, not by what we bundle (we have no ProvideBindingRedirection/codeBase pkgdef entries, and a UseCodebase/pkgdef entry would not isolate it). A ref *inside* a redirect range sweeps to VS's copy; a ref *above* the range top loads our bundled copy — so one graph can get **two copies of the same assembly, whose types don't unify**. Proven on VS 18.3.1 (measured against 18.9.1, 2026-08-19): Shell compiled against System.Memory 4.0.5.0 (above 18.3's top) while VS's System.IO.Pipelines used VS's System.Memory → `MissingMethodException: PipeOptions..ctor(MemoryPool<byte>, …)` — the ctor existed everywhere since 2018; the *parameter type identity* didn't match.

**The same file also decides which WPF we render on, and the answer is not the one the TFM implies (measured 2026-08-19, VS 18.9.12112.369).** `devenv.exe` is still `.NETFramework,Version=v4.7.2`, but `devenv.exe.config` carries, for every WPF assembly, a `bindingRedirect oldVersion="0.0.0.0-99.0.0.0" newVersion="10.0.0.0"` **plus** a `codeBase href="AppLocalWPF\<name>.dll"`, and `Common7\IDE\AppLocalWPF\` holds the **modern dotnet/wpf stack** — `PresentationCore` 10.0.108.26403, alongside `wpfgfx_cor3`, `PenImc_cor3` and `DirectWriteForwarder`, the `_cor3` suffix being the .NET-Core WPF naming. The redirect spans **every** version, so a net472 assembly's reference to `PresentationCore, Version=4.0.0.0` is swept to 10.0.0.0 and loaded app-local: **our VSIX draws on .NET 10 WPF inside devenv, not on .NET Framework's WPF.** Three consequences. (1) Reason about the chat pane's text stack, layout and render behaviour — #86's whole subject — against **dotnet/wpf 10.x**; a .NET Framework WPF answer may simply not apply. (2) An **upstream** dotnet/wpf fix can reach the shipping VSIX through a VS update, with no retarget on our side — which is why the colour-emoji escape hatch (WPF PR #11684, COLR v0 glyph rendering) is worth re-probing rather than closing as unreachable — when `AppLocalWPF`'s version bumps, rather than on a VS *feature* version. (3) It is a second reason the standalone Desktop host cannot stand in for devenv: net10 Desktop gets its WPF from the shared runtime, net472 Desktop gets .NET Framework's, and **neither is the app-local build devenv loads** — the same blind-spot shape as the Exp hive missing #103's captor and the offline `--perf` split being right on ratios and 130× wrong on absolutes. Check the version with `(Get-Item '<VS>\Common7\IDE\AppLocalWPF\PresentationCore.dll').VersionInfo.FileVersion`.

**No code-level dodge exists** (even reflection fails — an object's base type is fixed to one identity at load); the earlier v1-API pump rewrite did NOT fix it (the pump stays as defense-in-depth).

**Policy: the net472 slice pins StreamJsonRpc to the `Microsoft.VisualStudio.SDK` baseline (2.22.11), per-TFM-conditional in Shell.csproj** — the SDK metapackage's pins ARE the oldest-supported-VS-compatible set, so every ref sweeps onto VS's coherent assemblies on all 18.x; net10 (engine/Desktop, out-of-proc, self-contained) keeps 2.25.25.

**The invariant is NOT "one copy in the process"** — Markdig ships compiled against a newer System.Memory, so a second private copy loads on older VS builds and that's fine (its spans never leave it). The invariant: **any of our assemblies whose System.Memory/Pipelines/StreamJsonRpc-typed objects cross into VS-shipped assemblies (= Shell) must compile against baseline versions**. Diagnosis kit: `startup-error.log` (copyable exception, written by ChatToolWindow framework-only), engine.log `[probe]` lines (loaded version + **path** of StreamJsonRpc/Pipelines — extension dir vs VS dir instantly reveals a split), and check refs with `[Reflection.Assembly]::LoadFile(...).GetReferencedAssemblies()`.

## An UNSIGNED bundled assembly is a process-wide name another extension can CAPTURE via pkgdef

**A pkgdef `RuntimeConfiguration\dependentAssembly` entry registers a `codeBase` for a simple name across the whole devenv process, and from then on EVERY request for that name — from any extension — is routed to that one file, with no fallback to the asking extension's own folder.** What happens next depends on one thing: whether the file's identity matches the request.

- **Match** → `Where-ref bind Codebase matches what is found in default context`, the bind switches from the LoadFrom context to the default context and succeeds. This is the registrant's own case, which is why the captor never notices.
- **Mismatch** → fusion falls through to a *private assembly* bind, and a simply-named private assembly must live under the application base (devenv's is `Common7\IDE\`, which no extension folder is). Rejected, `hr = 0x80131041`, `All probing URLs attempted and failed`.

So the appbase restriction bites **only on the mismatch path** — the registration is perfectly serviceable for whoever registered it and fatal for everyone shipping a different version of the same name. The discriminator is the pkgdef registration plus the version match, not the strong name and not the redirect range (the redirect is never applied at all: `Policy not being applied to reference at this time`). Contrast the identity-split above: that is two copies whose types don't unify; this is not getting a copy at all.

**Proven by fusion log** (2026-08-08), reproduced on demand: `System.IO.FileNotFoundException at <OurExtension>.UI.Markdown.MarkdownFlowRenderer.Render(System.String)`, thrown while JITing `Render`, unhandled on the WPF dispatcher, killing devenv. Both assembly names in this capture are redacted to the placeholders used below. The capturing extension's pkgdef contains:

```
[$RootKey$\RuntimeConfiguration\dependentAssembly\bindingRedirection\{8F362FD0-…}]
"name"="Markdig"   "publicKeyToken"=""   "oldVersion"="0.0.0.0-1.1.0.0"
"newVersion"="1.1.0.0"   "codeBase"="$PackageFolder$\Markdig.dll"
```

Both binds were captured on the same machine against the same file, and the contrast **is** the mechanism:

```
--- ours, FAILS ---                        --- theirs, SUCCEEDS ---
Calling assembly : <OurExtension>.UI       Calling assembly : <OtherExtension>
DisplayName = Markdig, Version=1.3.0.0     DisplayName = Markdig, Version=1.1.0.0
Attempting download …/r2u1s5iq.fnn/…       Attempting download …/r2u1s5iq.fnn/…
Assembly Name is: Markdig, 1.1.0.0         Assembly Name is: Markdig, 1.1.0.0
ERR: private assembly outside appbase      Where-ref bind Codebase matches …
All probing URLs attempted and failed      Switch to default context → succeeds
```

**Four things here are counter-intuitive:**

- **It is NOT a load-order race, and NOT "only one copy per AppDomain".** On the ordinary probing path unsigned binding ignores the version, so two copies coexist perfectly well: loading their 1.1.0.0 first and then rendering through our reference works — measured, 24/24 markdown shapes (tables, task lists, footnotes, emoji). The crash needs the *registration*, not a race.
- **It is triggered by INSTALL, not by use.** pkgdef entries are merged into the configuration store when an extension is installed or updated and read at startup, so the other extension need never load — no `.md` file, no activation, no solution open. That is why it presents as "fine for weeks, then crashes every launch": what changed was an **update**, not a usage pattern.
- **It is NOT symmetric, and the captor is unharmed.** Verified directly: with our extension disabled, opening a `.md` file binds their Markdig successfully through their own registration. They have no symptom to notice, which is why this can persist indefinitely upstream.
- **Version-matching WOULD work, and is still the wrong fix.** Ship the captor's exact version and your request matches their file and binds — the redirect range makes it look impossible, and the successful bind above shows it is not. But it pins you to their version forever and breaks the day they update, with no signal that you are coupled to them at all. Take the name out of contention instead.

**Of the 30 third-party assemblies in our .vsix, exactly five were unsigned** — `Markdig`, `Emoji.Wpf`, `Typography.OpenFont`, `Typography.GlyphLayout`, `Stfu` — and they are all in the markdown/emoji rendering island. Everything else, including `ColorCode.Core`, is strong-named and therefore loads side by side and is safe. Check with `[Reflection.AssemblyName]::GetAssemblyName(path).GetPublicKeyToken()`; empty means exposed.

**Diagnosing the next one: go straight to the fusion log** — static inspection cannot see this, because every file we ship is present and version-consistent. `HKLM\SOFTWARE\Microsoft\Fusion` → `EnableLog`/`ForceLog`/`LogFailures`=1, `LogPath`="C:\FusionLog\" (**create the directory first — fusion logs nothing if it is missing**, which costs a repro cycle), restart devenv, reproduce, then read the failing bind: the `Calling assembly` line names the victim and the `Attempting download of new URL` line names the captor. Turn all three back off afterwards. To find the captor without a repro, grep every installed extension's `*.pkgdef` for `dependentAssembly` entries naming an assembly you also ship.

**Fix, in preference order: a STRONG-NAMED package first, merging only if there isn't one.** A strong name puts the version back into the identity, so copies load side by side *and* a `codeBase` outside the appbase becomes legal — the capture simply cannot bite. `Markdig` → **`Markdig.Signed`**, same authors, published in lockstep with identical version numbers, same `Markdig.*` namespaces and types, so it is a package-reference swap with **no code change**. It is immune twice over: the assembly is named `Markdig.Signed`, which the existing registration cannot even reach, and it is strong-named. Verified after the swap: `System.Memory` still ships at 4.0.5.0 (no resolution drift), and `CodeWicket.UI` references `Markdig.Signed` with a public key token.

### Why not ILRepack

**A merge works, and costs more than the signed package**: a merge target, a `System.Memory` pin to undo its own side effect, and a Windows PowerShell 5.1 verification script wired into two release gates — because the merged assembly is net472 and nothing else we run offline is. Two traps come with it:

1. **`ILRepack.Lib.MSBuild.Task` ships a default target that runs on Release builds unless an `ILRepack.targets` file exists.** `AfterTargets="Build"`, gated only on `$(Configuration.Contains('Release'))` and `!Exists('<project dir>\ILRepack.targets')`, it merges **every DLL in the output folder** into the primary assembly and then deletes the copy-local references. So an `ILRepack.targets` file existing is what suppresses it, and deleting that file does not fall back to "no merge", it falls back to "merge everything". On this solution's output folder it fails on a duplicate type between Nerdbank.Streams and MessagePack; without that collision it would "succeed" and produce an unusable VSIX. **Debug builds are exempt, so every check that runs in Debug misses it.** Corollary when removing it: delete the targets file and the `PackageReference` **in the same commit** — drop only the file and Release builds start swallowing the whole output folder.
2. **Merging bakes the merged assembly's references into yours.** Markdig depends on `System.Memory` 4.6.3 (assembly 4.0.5.0); `PrivateAssets="all"` correctly stops the Markdig package edge reaching the VSIX, and takes that `System.Memory` constraint with it — NuGet then quietly resolved down to 4.6.0 (assembly 4.0.2.0), MSB3277, and **a different System.Memory in the VSIX than the day before**. Silently changing which System.Memory ships in-proc is not an acceptable side effect of a rendering fix.

### What stays exposed

**What can take NEITHER treatment.** `Emoji.Wpf` has **no signed variant on nuget.org** (checked, along with `Typography.OpenFont` and `Stfu`), and cannot be merged either: it resolves its own resources through pack URIs naming itself *and its version* — `/Emoji.Wpf;V0.3.4.0;component/picker.xaml`, plus `textbox`, `internal/win10flags`, `internal/win11flags` — baked into compiled BAML, which ILRepack cannot rewrite. Its island is all-or-nothing (`Emoji.Wpf` references Typography and Stfu by name), so all four stay external and their exposure is **irreducible by packaging**; `MarkdownRenderFirewall` is their permanent backstop. **Check for pack URIs before merging anything with WPF resources, and check in UTF-16** — metadata strings are UTF-16, so a UTF-8 scan for `pack://` reports a clean bill of health on an assembly that is full of them.

**Backstop for what stays exposed: `MarkdownRenderFirewall`.** The render path must not be able to kill the IDE for any reason, so the renderer call is wrapped and a failure degrades to plain text with a visible notice naming the contested assembly, plus a one-time log line inventorying what is actually loaded under that name — the line that turns "VS crashed" into one grep. The bridge method is `[MethodImpl(MethodImplOptions.NoInlining)]` and that attribute is **load-bearing**: a missing assembly surfaces while JIT-compiling the method that needs it, i.e. at that method's *call site*, so inlining the bridge would move the fault into the guarded method's own JIT and past the `try` — the same trap `ChatToolWindow.InitializeAsync` exists for. No unit test can pin that; the comment is the only guard.

**The standing guard is a release gate over the PACKAGE, not over a build step.** `Gate - no unexpected unsigned assemblies ship` unzips the Release .vsix, reads every root DLL's public key token, and fails on any unsigned third-party assembly outside an explicit allowlist (the four-DLL Emoji.Wpf island), plus a positive assertion that `Markdig.Signed.dll` is present. That catches the two regressions that matter and one no human review would: a swap back to the unsigned package, the signed package silently disappearing, and **a new unsigned dependency arriving transitively**. It proves it can see the package first (`CodeWicket.UI.dll` must be among the entries) because an absence check passes trivially against an empty list. Verified load-bearing by injecting both failures — dropping a real unsigned `Markdig.dll` into the extracted package, and removing `Markdig.Signed.dll` — each of which fails it, with a clean baseline either side. **Deliberately package-level:** the previous gate checked a build step's output, which meant it could only ever assert the thing we already knew to look for.

## The experimental instance has its OWN extension set, so cross-extension conflicts are invisible to every run there

**This is why #103 shipped — not a review miss, a blind spot in the harness.** Extensions install to the *standard* hive; the Exp hive the extension deploys into starts with none of them. So the entire class of "our extension interacts badly with another one" is unreachable by our verification by construction, and no amount of care in review substitutes for it. Confirmed by measurement: with the captor absent, `main` (the unfixed build) does **not** crash in Exp; with the captor sideloaded it crashes on every launch, deterministically. The same build, the same branch, opposite results — the hive contents *are* the variable.

**To test for pkgdef name capture, install a conflicting extension into your Exp hive on purpose** (sideloading below): one that registers a `codeBase` for a simple name we also ship turns the #103 repro into a standing regression test. The release gate over the package is the guard every build gets. Such an install survives `devenv /updateConfiguration`; only a full hive reset removes it.

**Sideloading one is not obvious, because an installed extension folder is NOT a valid `.vsix`.** A `.vsix` is an OPC package requiring `[Content_Types].xml` at the root, and VS **strips that on install** — so re-zipping the installed folder and feeding it to `VSIXInstaller /rootSuffix:Exp` fails with `MissingPackagePartException: does not contain the file extension.vsixmanifest at the root`, which is doubly misleading because the manifest is plainly right there. What works instead: copy the folder into `…\18.0_<id>Exp\Extensions\` and run `devenv /rootsuffix Exp /updateConfiguration` — VS discovers it and performs a *real* install into its own randomly-named folder. **Then delete your copy**, or the same extension identity is registered twice.

**Verify the registration reached the Exp CONFIGURATION STORE, not just the folder** — the pkgdef sitting on disk proves nothing; `privateregistry.bin` is what the binder reads.

**Two instruments that do NOT work while devenv is running, and both fail as a plausible-looking absence:**

- **`privateregistry.bin` is a loaded registry hive**, not an ordinary file — it cannot be read even with `FileShare.ReadWrite`. A naive read throws, leaves the buffer empty, and every subsequent "how many times does X appear" returns a confident **0**.
- **`Process.Modules` is blind to VS extension managed assemblies.** Control measurement: the standard-hive devenv, with both our own extension and the capturing one demonstrably loaded, reported **0** modules named `*markdig*` and **1** under LocalAppData out of 488. So "our assembly isn't loaded" read off `Process.Modules` means nothing at all.

**What does work:** parent-process linkage — the engine's `ParentProcessId` proves *which* devenv opened the chat window (`engine pid 36960 parent 9336`) — plus `engine.log` itself, and reasoning by elimination against a deterministic repro. **Before trusting any absence, make the instrument prove it can see a known-present thing first.** That guard caught both of the above; without it, two false findings would have gone into the record.

## Installing and uninstalling the VSIX

**VSIXInstaller batches pending extension operations, and elevation is a property of the BATCH, not of the extension.** If anything else queued at the same time needs admin, the whole batch does — so an ordinary per-user uninstall of *this* extension can prompt for elevation it does not itself require, and be rolled back part-way when that elevation is declined, leaving a half-uninstalled install. Nothing in our manifest asks for it: `AllUsers` appears nowhere in the tree, the single `InstallationTarget` carries no scope attribute so the install is per-user under `%LOCALAPPDATA%`, the one pkgdef is `$RootKey$`-scoped, and there are no HKLM writes. **Without admin rights, cancel the other extensions' updates and apply ours on its own**; re-running the uninstall then needs no manual cleanup, so try that before deleting anything by hand. **The diagnostic trap is the interesting part:** ruling out our own manifest does NOT narrow the field to "something about our extension installed oddly". Our extension was not special — it was in the wrong queue, and the next hypothesis after the manifest should have been "whose elevation is this?", not "which of our files needs admin?".

**A build whose Identity Id CHANGED does not replace the previous install — both register.** VS keys installed extensions on Identity Id, so a new-Id build installs *alongside* an old-Id one. The current Id is `CodeWicket.354bbd8d-…`; because the package GUID and the settings-manifest pkgdef key deliberately did NOT move with it, a build carrying an earlier Id still shares the package GUID `{1b202172-…}` — which is what the search below keys on. So uninstall the old one BEFORE installing across an identity change. **Whether two copies co-exist silently or fail visibly has not been observed**, which is why no cleanup tooling was built for it. Install folders are randomly named, so the only way to see what is actually registered is to read each manifest, with **every devenv closed** (a reading taken while VS runs is worthless — see the loaded-hive note above):

```powershell
Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory |
  Where-Object { $_.Name -match '^\d+\.\d+_[0-9a-fA-F]+$' } |
  ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'Extensions') -Directory -ErrorAction SilentlyContinue } |
  ForEach-Object {
      $m = Join-Path $_.FullName 'extension.vsixmanifest'
      if (Test-Path $m) {
          $id = ([xml](Get-Content $m -Raw)).PackageManifest.Metadata.Identity
          if ($id.Id -like '*1b202172*' -or $id.Id -like 'CodeWicket.*') {
              [pscustomobject]@{ Id = $id.Id; Version = $id.Version; Path = $_.FullName }
          }
      }
  } | Format-List
```

Two rows means the identity split; both folders need removing, then `ComponentModelCache` cleared and an empty `Extensions\extensions.configurationchanged` written so VS rescans on next start. **Never delete `privateregistry.bin`** — it is the whole per-user VS configuration store, not an extension cache, and removing it resets every VS setting there is.

## VSIX build (SDK-style, VS 2026/18.x)

**VSIX build (SDK-style, VS 2026/18.x):** (1) set `VSSDKBuildToolsAutoSetup=true` or the container targets never import (no .vsix); (2) build with **`dotnet build`** or the VS IDE, NOT standalone net472 `MSBuild.exe` (VSSDK1048 / `System.Collections.Immutable` load failure); (3) gate `DeployExtension` to `'$(BuildingInsideVisualStudio)'=='true'` — `dotnet build` packages but can't deploy; VS deploys to the Exp instance when you start debugging; (4) cross-TFM `ProjectReference` (net472 VSIX → net10 engine) needs `ReferenceOutputAssembly=false` + `SkipGetTargetFrameworkProperties=true`; (5) **VSCT: parent the `<Button>` directly into a standard shell group** (e.g. `IDG_VS_WNDO_OTRWNDWS1`) — nesting a custom `<Group>` under another group makes VS silently drop the command (a `<Group>` under a `<Menu>` is fine — that's how the Extensions menu is built; see [vsix-shell.md](vsix-shell.md)); (6) pinned to **`Microsoft.VSSDK.BuildTools` 18.5.40034** — do NOT bump to 18.9.820 (VSSDK1048 under standalone MSBuild, no functional gain). The one thing 18.9 does add that we wanted is `IDM_VS_MENU_EXTENSIONS` (0x0091) + `IDG_VS_EXTENSIONS` (0x6000) in `vsshlids.h`; **declare the id in our own `<Symbols>` against `guidSHLMainMenu` instead of bumping** (re-opening a GuidSymbol the `<Extern>` already defined, to add an `IDSymbol`, compiles fine).

**A numeric literal (`<Parent id="0x6000">`) does NOT work — vsct rejects numeric parent ids with VSCT1103.** Bumping the packaging toolchain for two `#define`s is a bad trade: VSSDK.BuildTools owns `IncludeShellDependenciesInVsix` and CTMENU embedding, both of which fail *silently* (the fresh-machine hang; the dropped command).

## A stale bundled engine desyncs the shell↔engine pipe

A stale bundled engine desyncs the shell↔engine pipe (`":' is an invalid start"`) — rebuild and reset the Exp hive.

## If a new Unified Settings property doesn't appear in the experimental instance

**The build stamps `CacheTag` from a hash of registration.json, so a new leaf, title and description appear with no extra step** (measured 2026-08-11). The mechanism below is what that stamp defeats, and it is the fallback if a hive still will not re-read: the settings registration.json is merged into VS's per-hive *configuration cache* via the SettingsManifest pkgdef, and a content-only change to the JSON (same VSIX version, same pkgdef bytes) doesn't invalidate it. Fix without nuking the hive: `devenv /rootsuffix Exp /updateConfiguration` (full Exp reset also works but loses layout/hive state).

**`CacheTag` invalidates manifest CONTENT only.** A classic Tools > Options page is registered through `ToolsOptionsPages` in the *pkgdef*, which nothing stamps — so adding one does require `/updateConfiguration` on the Exp hive before the page appears.

## Fresh-machine "Starting Code Wicket…" hang = missing net472 dependency in the .vsix.

The VSSDK packaging (`IncludeCopyLocalReferencesInVSIXContainer`) **excludes** every assembly that resolves transitively through the `Microsoft.VisualStudio.SDK` metapackage (StreamJsonRpc, MessagePack, VS.Threading, Nerdbank.Streams, System.IO.Pipelines, System.Text.Json, the System.* facades — and, the actual offender, **`Microsoft.Bcl.AsyncInterfaces`**) as "provided by VS," even though they're copied into `bin\`. Dev boxes and experimental-instance deploys mask it (VS's own probing satisfies most); a **double-click install on a fresh machine** doesn't, so the shell's `ChatToolWindow.InitializeAsync` faults the moment it JITs a method touching the missing type → the fault lands on the `FileAndForget` task (activity-log only) → the window sits forever on the initial status. Fix = the **`IncludeShellDependenciesInVsix`** target force-adds `@(ReferenceCopyLocalPaths)` `.dll`s as `VSIXSourceItem` into the VSIX root (extension folder is on VS's binding path, which is why bundled Markdig loads).

**`SetTargetFramework` pins alone do NOT fix this** (they change nothing in the container). Verify after any packaging change: unzip the `.vsix` and diff its root DLLs against `bin\Debug\net472\*.dll` — the set difference must be empty.

## WPF Run.Text binds two-way by default

**WPF `Run.Text` binds two-way by default** — bind read-only VM props as `Mode=OneWay` (else `XamlParseException` at render).

## Building only the UI project leaves a stale CodeWicket.UI.dll

Building only the UI project leaves a stale `CodeWicket.UI.dll` in the host bin — rebuild the host/Desktop project to pick up UI XAML changes.

## net472

**net472** needs `Microsoft.Bcl.AsyncInterfaces` for IAsyncDisposable/IAsyncEnumerable; its `IsNullOrEmpty` lacks `[NotNullWhen]` (use explicit null checks for 0-warning).

## Pin MessagePack 2.5.301

Pin **`MessagePack` 2.5.301** to override the transitive `MessagePack` 2.5.198 (CVE-2026-48109) that StreamJsonRpc resolves.

## Unified Settings validation failures must be isTransient: true.

An `ExternalSettingOperationResult.Failure` with `isTransient: false` makes USX grey the setting out pending `ErrorConditionResolved` — since a stubbed no-op event never fires, the field locks and the user can't correct their input (observed with a malformed `engineEnvironment` line, 2026-07-10). `CodeWicketSettingsProvider.Reject` centralizes this (transient failure + tracked error state), and `ErrorConditionResolved` is raised on the next successful save as belt-and-braces.

## Never use temp for anything agent-facing

**Never use temp for anything agent-facing** — no process launched with a temp cwd, no agent-content files written to temp: corporate EDR can flag temp activity as suspicious. Use stable per-user dirs under `%LOCALAPPDATA%\code-wicket\` instead: `workspace` (default agent workspace), `workspace-stub` (VSIX stub-IDE mode), `probe` (AcpAgentProbe cwd), `diff` (VsEditApplier's before/after copies, swept by age+budget — see [settings-and-logging.md](settings-and-logging.md)). Temp remains fine for dev-only artifacts (tests, local scripts). **The write-side `%TEMP%` fallbacks must NOT be inherited by anything that DELETES**: a sweep pointed at `%TEMP%` reaches the whole machine's temp files, so the retention path resolves its directory separately and returns null rather than falling back.

## XML csproj comments can't contain --.

**XML csproj comments can't contain `--`.**

## A log file held open by a running process shows a stale size (even 0 bytes) in dir listings

**A log file held open by a running process shows a stale size (even 0 bytes) in dir listings** — NTFS updates the directory entry lazily. Open the file (Get-Content / a fresh FileStream) to see the real length before declaring a logging bug (2026-07-05). `Console engine-claude` is the live proof that both debug tees (CWKT_ACP_LOG + engine-channel) capture through the real out-of-proc engine.

## An interactive self-check assumes it owns the foreground, and using the machine while it runs fails it

A focus assertion cannot tell "the feature is broken" from "the window wasn't active": WPF drops keyboard focus when a window loses activation, so typing elsewhere while `--smoke` runs fails a good build. **Read the failure's `foreground=` stamp before anything else** — what each value means, and the neighbouring-assertion tell, are in [verification.md](verification.md#foreground--read-it-before-anything-else-on-a-focus-failure).
