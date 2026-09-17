# Verification: the offline gates, and what each one is load-bearing for


> **`#NN` refers to a private pre-release tracker.** The numbers are stable *names* for a failure
> mode, not links — the rule each one tags is stated here in full, so nothing is missing if you
> cannot open them.

> Terms used here without definition (the Exp hive, rungs, warm start, the prove-check verdicts, …) are
> in [AGENTS.md](../../AGENTS.md#terms-the-docs-use-without-defining-them).

The rules are in [`AGENTS.md`](../../AGENTS.md) under **Conventions**. This is the evidence behind them: what was measured, which harness defect produced each rule, and the failures worth recognising on sight.

Everything here is *offline* — it runs without devenv. What it therefore cannot reach is recorded at the bottom, because a gate's blind spots are as load-bearing as its assertions: #103 shipped through all of them.

## The gates, and how to run them

Build with `dotnet build CodeWicket.slnx` (green including the VSIX), 0 warn / 0 err.

| gate | what it is |
|---|---|
| `dotnet test` | the unit suite |
| Desktop `--smoke` | headless self-check, exits 0 on a pass, 1 on a failed check, 3 on an exception, on **both** TFMs |
| `--screenshot*` | renders the window — or a popup's own hwnd — to PNG |
| Console proofs | **manual, not a gate**: backend behaviour, most needing a live signed-in backend (`kiro*`, `claude-*`, …) — inventory in [solution-layout.md](solution-layout.md) |
| `--perf` | a **measurement**, not a gate |

`scripts/run-smoke.ps1` drives the Desktop checks (`-Mode`, `-Build`, `-Framework`, `-Full`, `-ScratchDir`, `-TimeoutSeconds`). Scratch artifacts land in the host's `bin/…/scratch/<host>/`.

**The usual invocation, before running in Visual Studio or opening a pull request:**

```
pwsh -NoProfile -File scripts/run-gates.ps1
```

It needs PowerShell 7 (`pwsh`) and refuses to run under Windows PowerShell 5.1 (below). The default gate set is `tests`, `smoke-net10`, `smoke-net472` and `screenshot`; `-Gates` picks others (any `screenshot-*` mode by name, or `perf-net472` / `perf-net10`), `-NoBuild` runs against what is already in `bin/`, `-MaxParallel` caps concurrency (default 4), `-Configuration Release` and `-TimeoutSeconds` (default 180 per gate; the perf gates take 1800) do what they say. It prints a table, and its exit code is the number of gates that failed. Each gate's console output is in `src/CodeWicket.Desktop/bin/gates/<gate>/gate.log` (stderr in `gate.err.log`), beside that gate's own scratch directory.

**It runs no Console proof.** The proofs are run by hand, one mode at a time (`dotnet run --project src/CodeWicket.Console -- <mode>`), and most of them drive a real backend. Of the ones that need none, two — the default in-memory ACP proof and `engine` — also run in the release workflow.

`scripts/run-gates.ps1` runs the gates as a **matrix** — all of them at once, each in its own process: build once, then fan out. The build is the only step that cannot overlap — every gate reads the same `bin/` — so it runs serially first and the matrix runs against finished binaries that are read-only for the rest of the run. Measured 2026-08-24, with two screenshot modes beyond the default set: **tests + smoke on both TFMs + 3 screenshot modes = 35 s wall against 97 s serially.**

**Never rebuild while a matrix is in flight.** Replacing assemblies under a running gate is the same hazard `--perf` already carries, and it surfaced there first: a 4m14s "exit 0" that wrote no report at all.

## The exit code is not the result

Three instances, three different suppliers of the wrong answer. This is the most expensive recurring lesson in the harness, which is why the rule is stated once and named three times.

**1. The harness supplies it.** Every `--screenshot*` mode and `--perf` catch their own exceptions, write an error artifact and *still* call `Shutdown(0)`; `--smoke` exits 1 or 3, but only its artifact says which checks failed. So `run-smoke.ps1` fails a run whose artifact says `FAIL`/`FAILED` despite exit 0, and warns when the artifact predates the run rather than reading the previous run's file as this one's. Verified by injecting a throw into `RunScreenshotAsync` and watching the run report `exited: True code: 0` — the exit code lying, on record.

The screenshot modes had that trap and had never been given the answer (2026-08-22): the old check was that the artifact merely **existed**, so a run that threw passed on the *previous* run's PNG. A stale artifact, or an error file written during the run, now fails the gate. Harmless while a human looked at the picture afterwards; not harmless in a matrix where a table is the only thing anyone reads.

**2. The reader's shell supplies it.** `run-gates.ps1` **requires pwsh and refuses to run without it.** Under Windows PowerShell 5.1 it does not error, it *lies*: `Start-Process -PassThru` with redirected streams yields an EMPTY `ExitCode` once the child exits — measured, 5.1 `HasExited=True ExitCode=[]` against pwsh 7 `ExitCode=[3]` for the same `cmd /c exit 3` — so **every gate reports `FAIL ()` while its own log says PASS**. And `Process.Kill(bool)` does not exist on .NET Framework, so a timed-out gate's process tree is left running. The refusal is a hard throw naming the symptom; the engine's own `#requires` names only a version, which is not the thing a reader needs to be told.

**3. The injected build supplies it.** See `prove-check.ps1` below — the worst-placed of the three, because that script is the instrument every "verified load-bearing" claim is measured with.

## The automated runs must not touch the user's real config

`ExtensionConfig.RedirectTo` points `--smoke`/`--screenshot*` at a scratch config. The chat view loads config in its constructor, so **the redirect must happen first thing in `OnStartup`**.

*Interactive* Desktop runs deliberately keep sharing the real file — that parity is the point of the host.

The self-checks drive real gestures that persist (the zoom checks write `chatZoom` four times per run), so an unredirected run overwrites the developer's own settings. **When shared state resets inexplicably, check what the test harness writes before theorising about the product.** If a new check's gesture persists anything, it must go somewhere disposable.

The same reasoning binds `AttachmentStore.RedirectTo`, and it is why `ExtensionConfig.LogDirectory` is deliberately **not** covered by the redirect: a writer switching itself on inside the UI library would fire from the unit tests too, which render real markdown, into the developer's own logs.

## Isolation is ONE lever: `CWKT_SCRATCH_DIR`

`run-smoke.ps1 -ScratchDir`, read by `HostScratch`. One lever is sufficient because every piece of per-run state an automated mode persists hangs off that one base: the redirected `config.json`, the attachments dir, the sessions store, `crash.txt`, and the result artifact the script reads back.

**Runs on different frameworks never collided** — the default base is under `binDir`, which is per-TFM — so the collision is two runs on the *same* framework, which is most of the matrix (every screenshot mode and the smoke share one net10 bin). The sharp edge: the headless modes **delete the sessions store recursively at startup**, so the second run to start wipes the directory the first is restoring from, and the failure surfaces in unrelated assertions.

Each gate is its **own process, not a runspace**. `ForEach-Object -Parallel` shares `$env:` across runspaces in one process, so the scratch override would be undone by the very thing providing it.

### Three shared resources are fine, and it is worth knowing why

- **The binaries** are read-only once built (hence the no-rebuild rule above).
- **The log directory** is untouched, because the Desktop host opens no tee in the automated modes. A host that *does* tee would braid runs into one file and needs its own answer before joining the matrix.
- **The screen** is not shared at all: the screenshot modes rasterise through `RenderTargetBitmap` off-screen, so concurrent windows cannot occlude or steal focus the way a screen-capture harness would.

### The residual hazard is ACTIVATION, not capture

The smoke's focus assertions read `Keyboard.FocusedElement`. Measured clean over 24 concurrent window-opening gate runs on an idle box — which is evidence, not proof — so **a smoke gate that fails in a matrix gets its `foreground=` stamp read FIRST**, and the runner prints the failing gate's log for exactly that reason.

### `perf` and `screenshot-held` are deliberately outside the default set

`perf` because it is a measurement rather than a gate. `screenshot-held` because it hangs on **shutdown** intermittently (3 of 4 runs, at 20/60/180 s ceilings) — the *work* completes every time, both PNGs written fresh with no error file, so the mode does its job and then fails to exit. It reproduces standalone and sequential with no scratch override, so it is not a parallelism artefact. Excluded rather than papered over with a longer timeout, since a gate red 3 runs in 4 trains you to ignore the table.

## `foreground=` — read it before anything else on a focus failure

Keyboard focus goes null when a window loses activation, while logical focus survives, so working at the keyboard while a run is in flight makes a focus assertion report a good build as broken (measured 2026-08-22). Every focus assertion therefore carries `foreground=ours|elsewhere|none|unknown` (`App.ForegroundStamp`: `GetForegroundWindow` compared against our process id, best-effort, so any failure reads `unknown` and never "not focused").

- `foreground=elsewhere` — the machine was in use and **the result says nothing**.
- `foreground=ours` — the failure is real.

**The neighbouring assertions are the second tell.** A code defect fails consistently across every focus check in the run; activation loss fails one while its neighbours pass — `hostedFocus=False (... bannerFocused=True, bannerLanded=<null>)` in the same run as `streamFollow`'s `tookFocus=True, focusLanded=InputBox`.

It is a **stamp and not a skip** on purpose: a check that opted out when unfocused would trade a diagnosable false failure for a silent gap on a release gate. It renders only on the FAIL line, since a passing run writes a prose summary. **Read the failure, then ask whether the machine was in use** — before re-running, which destroys the evidence (next section).

## A failing run is APPENDED to `smoke-failures.log`

Because `smoke-result.txt` is overwritten by the next run — so **re-running to see whether a failure was intermittent is what destroys the evidence for it**. Measured 2026-08-19: a net472 smoke failed once, four re-runs each wiped the diagnostic, and eight more could not reproduce it.

The console prints a self-announcing truncation on a pass and the whole thing on a failure; `-Full` prints it always.

## A `--smoke` phase must leave the transcript as it found it — and REMOVING what it added is not enough

The drill-down phase (#148) pads the root to 214 items so that restoring by anchor and restoring by raw offset stop agreeing, then removes the padding. But **the virtualisation churn is not undone by the removal**: it leaves an already-captured markdown viewer rendering *out of the tree*, where `MarkdownPalette.Resolve` correctly returns null and the renderer falls back to resource references.

The next phase to read that viewer is `resolvedTheme`, whose whole job is asserting those references are gone — so it failed **4-5 runs in 6 with nothing wrong with the product**. Bisected: pre-#148 main 0/6, phase on 4-5/6, phase on with only its padding removed 0/6.

**And the CHECK was half the defect.** A single reading cannot tell a PERMANENT fallback from a MOMENTARY one, and a detached render falling back to references is the *designed* behaviour — `MarkdownText` owes a corrective render on reattach (`EnsureRerenderOnReattach`). So it now reads **twice**, re-finding the run because the correction assigns a fresh `FlowDocument`, and reports which case it saw:

```
isExpression=True->False (transient...)          <- designed
isExpression=True->True  (STUCK on references...) <- regression
```

Measured: the exact pre-move layout that failed 4-5/6 passes **0/6** with only the second reading added, so the state was transient and **nothing was ever wrong with the product** — and nulling `MarkdownPalette.Resolve` still fails it 3/3, so the check did not lose its teeth.

Both halves are kept: the ordering rule stops a phase poisoning its neighbours, the second reading stops this check reporting a designed transient as a regression. **A disruptive phase therefore runs LAST.**

### Concurrency is not the cause

The failure looks exactly like activation contention, and a parallel-vs-serial comparison confounded by this phase *plus* a human at the keyboard (4/9 against 0/4) appears to confirm it. **Concurrency is not a cause**: on a quiet box `-MaxParallel 4` measured **0/5**, and serialising window-opening gates doubled wall time for no benefit.

## A `--smoke` wait keys on the CONTENT, not on the row

A phase that waits for a row to be **present** is satisfied by the unpopulated state wherever rows exist
before their data does. The session information panel is the instance: its rows exist from the moment a
session starts, carrying "not reported" and "not read yet" (`SessionInfo.cs`), so a wait keyed on the row
returned at once and the phase failed with the feature working. **Wait for the content, not for the row.**

## A screenshot mode that needs a SHORT transcript should seed and restore one

Not drive the fake. The fake provider's scripted turn is long enough to push the subject off the top of the frame, and scrolling back is correctly refused while the transcript is following (#90) — that is the design working, not something to defeat for an artifact.

`--screenshot-attachment` seeds a `PersistedSession` and restores it, which also happens to be the only way to see a thumbnail decoded from its saved **file** rather than from bytes still in memory.

## The Desktop host multi-targets, and `net472` is the slice devenv loads

Issue #106. `-Framework net472` runs the same self-check against the net472 build of `CodeWicket.UI`: its BAML and theming, the `Markdig.Signed`/ColorCode/Emoji.Wpf closure, and the VS-SDK-baseline StreamJsonRpc that `Shell` pins only there. Everything else offline is net10, so this is the **only** check that touches the shipping framework. Both slices are release gates.

It is broader coverage, **not different coverage** — see the blind spots below.

### Keep the host TFM-neutral

`System.Index`/`^1`, `Math.Clamp`, and the interpolated-handler `AppendLine(IFormatProvider, …)` / `string.Create(IFormatProvider, …)` overloads are all net6+ and break the net472 slice. `PerfHarness.Inv` is the invariant-formatting spelling both TFMs have.

Note an **addition** of interpolated strings converts to a *handler* but **not** to `FormattableString`, so each fragment wraps separately or the concatenation silently goes through the current culture.

## `--perf` is a measurement run, not a gate

It runs on net472 by choice: devenv is net472 and #86 is a report against that framework, so a net10 sweep measures a configuration no user runs.

Default sweep, same box, Debug: **net472 7m21s against net10 4m41s** — ~1.6x, both producing the same 85-line report.

Two rules for reading it:

- **`--perf` ALWAYS EXITS 0.** It catches its own exceptions into a `FAILED:` report and ends on `Shutdown(0)`, so the exit code proves nothing and **the artifact is the result**: check `perf-result.txt` exists and is fresh, or a run that did no work reads as a pass.
- **A timeout is not a measurement.** A run killed at a timeout gives no ratio — a pegged CPU at the kill says only that the sweep grinds rather than deadlocks. **A comparison needs a completed run on each side.**

The sweep is long on both TFMs because the drag and large-reply passes use **hardcoded** sizes (500 items; 120 x up to 256 repeats), so `--perf-items`/`--perf-iterations` shrink almost nothing. It overran `run-smoke.ps1`'s old 300 s perf timeout on both; the default is 1800 s (`-TimeoutSeconds` overrides). A kill mid-sweep loses everything, the report being written only at the end.

## Proving a check is load-bearing: `scripts/prove-check.ps1`

**Verify every new check by injecting the bug it guards and watching it fail.** A test that passes with the fix removed pins nothing — and a proof can print a confident `PASS` over the exact defect it was written for (#142).

The script snapshots the files you name, runs your injection, runs the check, and restores from the copy in a `finally`. How to write the injection, and why it is not committed, is in [`scripts/inject/README.md`](../../scripts/inject/README.md), beside two example scripts.

**Do NOT undo an injection with `git checkout -- <file>`.** It reverts the file to HEAD, discarding every *uncommitted* change in that file along with the injected line — silently and unrecoverably. The script exists so that avoiding it does not depend on remembering it. Restoring from a byte copy also retires the old "commit before you inject" precondition: the script cannot touch a file it was not given and does not care what git thinks, so a dirty tree is fine.

### It checks what a hand-run sequence does not

**The injection must actually land.** A `sed` whose pattern matches nothing exits 0 having changed nothing, and the check then passes for the most misleading possible reason. Every target is hashed before and after; an unchanged tree is a hard error, not a green run.

**The result is inverted**, because that is what the exercise means: the check *failing* is the success condition, so a check that stays green over an injected bug is reported as `PINS NOTHING` rather than left for a reader to notice.

**The output tree is put back, not only the source.** After restoring, it rebuilds, because `bin/` still holds a build made from the injected version and a `--no-build` run in between would measure the bug while reporting on the fix. `-NoRebuild` opts out, for a caller about to build anyway.

### Three verdicts, because of the exit-code family again

An injection that does not **compile** makes `dotnet test` exit non-zero having executed nothing, which off the exit code alone is indistinguishable from the check failing — so a verdict read off the exit code prints a confident `LOAD-BEARING` over a build error (measured 2026-08-26). That is the worst-placed instance of the family, because this script is the instrument every other "verified load-bearing" claim is measured with, and **a tool that can print a green about a check that never ran is worse than no tool** — the manual inject/run/undo at least forces you to read the numbers.

So the verdict is decided on **what the run REPORTED** — a test summary line with a non-zero total, or a smoke run's own PASS/FAIL — and the exit code only separates the two real outcomes once something is known to have run. (A smoke verdict reaches the classifier only if the stream carrying it does — see *The same `-Verify`, spelled two ways, classifies differently* below.)

| verdict | meaning |
|---|---|
| `LOAD-BEARING` | the check failed over the injected bug |
| `PINS NOTHING` | the check stayed green |
| `INCONCLUSIVE` (exit 2) | a broken injected build; a `--filter` matching nothing (zero tests, and exit 0 in some configurations, which would otherwise read as `PINS NOTHING` when nothing was measured at all); or any `-Verify` output it cannot recognise |

For a check that needs a slow or live run: **inject → build → restore → run the already-built binary**, so the tree is never dirty while the run is in flight. `-NoRebuild` is what keeps the injected binary in `bin/` past the restore.

**A hard error is not a fourth exit code.** A missed anchor or a failed injection command throws, and under `pwsh -File` that exits 1 — the same code as `PINS NOTHING`. The printed line says which; read it.

### The count and the identity together are what make a verdict real

This is the sharpest statement of what the three verdicts are protecting.

A `LOAD-BEARING` can be printed over an injection that **was never compiled** when two things coincide
(#160): the `-Verify` is `run-smoke.ps1`, which does **not build unless given `-Build`**, so it measures
the previous run's binaries; and the exit 1 it reads comes from a *different* phase failing for a
*different* reason (there, a forward-slash `-ScratchDir` breaking the file-link check), while the
smoke's own detail line says the phase under test passes throughout.

So the rule is not "re-run anything verified through `run-smoke.ps1`", which is only the local
symptom:

> **A non-zero total proves the injection compiled and the check actually RAN. The RIGHT check
> failing proves it measured THIS bug. Either alone is satisfiable by an accident.**

A build that failed to compile the injection runs zero tests — that is the `INCONCLUSIVE` case this
script already has. What it cannot detect on its own is the second half: a suite-wide `-Verify` that
goes red for an unrelated reason satisfies the exit-code test while measuring nothing. `dotnet test`
is the easier instrument here because its summary carries **both** (`Failed: 1, … Total: 8`, plus the
failing test's name) and because it builds by default; a whole-suite gate carries neither unless you
go and read the artifact.

Two things that correctly return `INCONCLUSIVE` and look alarming for ten seconds:

- **`dotnet test -v n`** — the normal-verbosity summary is a shape this script cannot recognise, so it
  declines to reach a verdict rather than guessing. That is the tool being right.
- **Widening a wire DTO's `bool?` to `bool`** — a test that passes an explicit `null` stops
  compiling, so no verdict is reachable. The type system is pinning that one harder than a test could,
  and "no verdict" is the correct outcome rather than a gap in the check.

### The same `-Verify`, spelled two ways, classifies differently

The same injection can come back `LOAD-BEARING` or `INCONCLUSIVE` depending only on how the `-Verify`
is written (#160):

```
-Verify 'pwsh -NoProfile -File scripts/run-smoke.ps1 -Mode smoke -Build ...'   # classifies
-Verify './scripts/run-smoke.ps1 -Mode smoke -Build ...'                       # INCONCLUSIVE
```

`run-smoke.ps1` prints its own `PASS:`/`FAIL:` line with **`Write-Host`**, which writes to the
**information stream**. `prove-check` collected the run with `Invoke-Expression "$Verify 2>&1" |
Tee-Object`, and `2>&1` takes stderr only. Run **in-process**, that verdict therefore reaches the
host (you can read it on screen) and reaches the classifier not at all. Run as a **child process**,
the same `Write-Host` is just the child's stdout, which the parent captures like any other text — so
the child-process spelling classifies, for a reason that has nothing to do with the check.

Measured, `exit` is *not* the discriminator (a control script without it behaves identically), and
neither is the script-vs-command shape. Only the process boundary is. Fixed by adding **`6>&1`** to
that invocation, so both spellings classify; the child-process form keeps working unchanged.

Two things make it worth more than a note:

- **It lands in exactly the wrong place.** A `dotnet test` verify was never affected — its summary
  comes back on stdout either way. The spelling-sensitive one is the `run-smoke.ps1` verify, which is
  the only instrument that spans processes, and therefore the only one that can catch the
  **"three places, not two"** gap. The check family that needs proving hardest was the one whose
  verdict depended on an incidental detail of the caller's phrasing.
- **It degrades toward a non-answer, which is why nobody chases it.** `INCONCLUSIVE` reads as "your
  injection was clumsy", so the reflex is to rewrite the injection. Failing toward *no verdict* is
  safe in the way that matters — it never printed a green over an unrun check — and expensive in the
  way that hides it: nothing is ever wrong enough to investigate.

The transferable rule, for any harness reading another program's output: **decide which stream the
thing you are classifying actually writes to, and whether a process boundary sits between you and
it.** `2>&1` looks like "everything" and is not; and a stream that is VISIBLE is not thereby
CAPTURED.

### Two failure modes recur when writing the injection

**An expectation-shaped traversal.** The #142 proof walked the *leaves* and asked whether each applied, but a broken build offers a **group**, which is not a leaf — so the sweep examined nothing and agreed with itself. **Iterate what the code under test actually produced, not what you expect it to have produced.** A near neighbour, same issue: comparing objects across **two separate gathers** — a second `RegisterCodeFixesAsync` pass builds fresh `CodeAction` instances, so a reference check found no match for any leaf and "this one was not offered" was trivially true of everything.

**Pinning the harness rather than the rule.** Both instances found by injection, both #62. A token refused by an *earlier* gate can never reach the gate under test, so the case has to get past every prior refusal before it proves anything. And a case that injects its own dependency cannot see the real one, so a rule living only in the default path — a filesystem predicate, a clock, a resolver — needs one case that takes that path.

## A solo run and the gate matrix are different instruments (#218)

`run-gates.ps1` runs the matrix **concurrently**, so every gate is measured on a machine three other gates are competing for. That is a scheduling detail right up until the subject of a check is a **race**, and then it is the only condition under which the check can see anything.

Measured on #218, one commit, one phase, the same assertion — a permission prompt parked on the last transcript row, viewport shrinking 373→259 as the banner takes its own row:

| how it was run | pre-fix | fixed |
|---|---|---|
| `run-smoke.ps1` alone | row at **221+39** in 259 — whole | **221+39** — byte-identical |
| `run-gates.ps1` | **201+86** in 259 — 28px past the fold, **both TFMs** | whole, 3 consecutive runs |

The bug was a scroll racing the realisation pass before it. A quiet machine wins that race every time, so run on its own the check reports the same numbers over broken and fixed code and **the two runs agreeing is what looks like proof**. It is not evidence of anything; both runs asked the same question in the same conditions and neither could have answered differently.

Three consequences, and the second is the expensive one:

- **A `PINS NOTHING` verdict is about the check AND its verifier together, never the check alone.** `prove-check.ps1` returned it three times over a real, reproducible defect, because the `-Verify` it was handed was the solo smoke. The script is behaving correctly and its output is worth exactly what the verifier underneath it is worth. Read it as *"this pairing proved nothing"* and the next move is to change the pairing.
- **It sends you to rewrite the product, not the harness.** A green over an injected bug is a statement about the check, so the reflex is to re-aim the check — and re-aiming it is *often right*, which is what makes this expensive: two of the three re-aims here were genuine improvements, and none of them was the reason for the verdict. The loop runs until the verifier itself is suspected.
- **Say so in the check's own doc.** The #218 phase carries *"verify this one under the gates, never by itself — a solo green here says nothing at all"*, because the next reader has no way to infer it: nothing about the phase looks timing-dependent, and its solo run is green.

**So: when what a check measures is a race, a layout pass, or anything else decided by who got the CPU, the verifier is `run-gates.ps1`.** For everything else the solo run stays the faster loop and nothing here argues against it — the point is that the two are not interchangeable, and only one of them has been contended.

## What the offline gates structurally cannot reach

- **Cross-extension conflicts.** The experimental instance has its own extension set, so #103 was invisible there *and* to every offline gate. A standalone host loads no competing extension, so the net472 slice cannot reproduce a pkgdef name capture either. → [gotchas.md](gotchas.md)
- **Resource resolution inside devenv.** The offline `--perf` split measures the document assignment at 7.7 ms against the ~1000 ms measured in devenv — right ratio, wrong magnitude by 130x. **Read it for ratios and never for absolutes.** → [chat-ui.md](chat-ui.md)
- **Anything that only exists in devenv**: the startup overlay, `ide services` holding the UI thread, the real `IIdeServices`. Only a live Visual Studio instance can exercise those.
- **A shape the harness always supplies correctly.** Not a gate limitation but an aim one, and the commonest way a whole file of green checks measures nothing: every test in `HeldMessageTests` raised a **matched** `toolStart`/`toolDone` pair, so none of them could see a ledger whose two writers disagree — which is all three of issue #190. **What a fake hands you is a claim about the wire, and a claim is what needs checking**: if the product's defence exists because a shape MIGHT be malformed, some check has to raise the malformed shape, or the defence is being tested against the one input that never needed it (#118's clipboard, one layer up).
- **WPF's own decision whether to raise an event.** `DataObject.Pasting` (#118) and `DragEventArgs` (#62) cannot be driven offline, so those checks assert the *mechanism* — that the binding and the handler registrations exist — rather than the behaviour. A check that cannot drive the decision must assert the mechanism, or it passes over a build that does nothing.

## Miscellany

- **WPF GUI exit code / IO**: `Start-Process -PassThru` + `WaitForExit(ms)`. A bare `& exe` returns before the GUI exits.
- **Measurement environment**: Visual Studio 2026 Professional 18.9.1 (18.9.12112.369), and Visual Studio 2022 Professional 17.14 as the second. The offline gates need neither. The Console proofs additionally need the backend they drive: `kiro-cli` on `PATH` and signed in, and/or the Claude Code adapter installed with a Claude Code sign-in (or `ANTHROPIC_API_KEY`) and Node.js 22 or later — see the inventory in [solution-layout.md](solution-layout.md) for which proof needs which.
