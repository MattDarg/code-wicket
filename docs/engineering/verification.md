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
| Desktop `--smoke-replace` | the same, for the routes that REPLACE the conversation — its own mode because they clear the transcript |
| `--screenshot*` | renders the window — or a popup's own hwnd — to PNG |
| Console proofs | **manual, not a gate**: backend behaviour, most needing a live signed-in backend (`kiro*`, `claude-*`, …) — inventory in [solution-layout.md](solution-layout.md) |
| `--perf` | a **measurement**, not a gate |

`scripts/run-smoke.ps1` drives the Desktop checks (`-Mode`, `-Build`, `-Framework`, `-Full`, `-ScratchDir`, `-TimeoutSeconds`). Scratch artifacts land in the host's `bin/…/scratch/<host>/`.

**The usual invocation, before running in Visual Studio or opening a pull request:**

```
pwsh -NoProfile -File scripts/run-gates.ps1
```

It needs PowerShell 7 (`pwsh`) and refuses to run under Windows PowerShell 5.1 (below). The default gate set is `tests`, `smoke-net10`, `smoke-net472`, `smoke-replace-net10`, `smoke-replace-net472` and `screenshot`; `-Gates` picks others (any `screenshot-*` mode by name, or `perf-net472` / `perf-net10`), `-NoBuild` runs against what is already in `bin/`, `-MaxParallel` caps concurrency (default 4), `-Configuration Release` and `-TimeoutSeconds` (default 180 per gate; the perf gates take 1800) do what they say. It prints a table, and its exit code is the number of gates that failed. Each gate's console output is in `src/CodeWicket.Desktop/bin/gates/<gate>/gate.log` (stderr in `gate.err.log`), beside that gate's own scratch directory.

**It runs no Console proof.** The proofs are run by hand, one mode at a time (`dotnet run --project src/CodeWicket.Console -- <mode>`), and most of them drive a real backend. Of the ones that need none, two — the default in-memory ACP proof and `engine` — also run in CI, on every pull request (`ci.yml`), every preview build (`preview.yml`) and every release (`release.yml`), all through `.github/workflows/build.yml`.

`scripts/run-gates.ps1` runs the gates as a **matrix** — all of them at once, each in its own process: build once, then fan out. The build is the only step that cannot overlap — every gate reads the same `bin/` — so it runs serially first and the matrix runs against finished binaries that are read-only for the rest of the run. Measured 2026-08-24, with two screenshot modes beyond the default set: **tests + smoke on both TFMs + 3 screenshot modes = 35 s wall against 97 s serially.**

**Never rebuild while a matrix is in flight.** Replacing assemblies under a running gate is the same hazard `--perf` already carries, and it surfaced there first: a 4m14s "exit 0" that wrote no report at all.

### Every run writes a per-test TRX, and nothing overwrites one

The test project sets the TRX logger, so **a failing test has an identity that outlives its console** — on the `tests` gate and on a solo `dotnet test` alike. It is set on the PROJECT and not on the matrix's command line, because the run that first cost us an identity was a solo run: a flag only the matrix passed would have left that case exactly as it was. A caller passing its own logger overrides it, which is what the `prove-check` sweeps do.

**They are timestamped, so a green re-run never overwrites the red it followed.** That is the whole point, and it is the reason they accumulate rather than a failure to tidy up: the case this exists for is a red two or three runs back, discovered after the console is gone.

**So `run-gates.ps1` bounds them: newest ten, pruned before the matrix starts**, in `src/CodeWicket.Tests/TestResults`, which the runner names in its header — a results file nobody can find is the same defect as no results file with more steps in it. Ten is a measurement, not a preference: a TRX for this suite is **about 4 MB** (measured 2026-09-20 over five runs of 2911 tests), so ten is roughly the total budget `Core.DiagnosticLog` holds every other diagnostic to. A first guess of fifty was written down as "the cost is kilobytes" and would have been about 200 MB on a developer box — the number was checked against the directory before it shipped, which is the only reason that is a footnote rather than a finding.

**Reading one:** `Core.Ide.TrxSummary` already parses TRX for the agent-facing `run_tests` tool and knows both of that format's traps — the logger writes `notExecuted="0"` beside results it has just recorded as `NotExecuted`, so skipped is the LARGER of header and results; and a test's `name` is what the runner wrote and is not a name, so `className` and `project` belong beside it. Nothing in the gates needs its own reader.

### A list parameter cannot be passed with `pwsh -File`

`-File` hands its arguments to the script as literal strings and never parses them as PowerShell, so `pwsh -NoProfile -File scripts/run-gates.ps1 -Gates tests,smoke-net10` binds the whole comma string as ONE element of `[string[]]$Gates`. **It fails in the wrong place**, which is what makes it cost time: that single "gate" is handed to `run-smoke.ps1` as a `-Mode`, refused by its `ValidateSet`, and the table reports a failed gate whose error names a mode nobody typed. The run ends in about a second, so **the shape to recognise is a gate matrix that fails faster than a build.** Either invoke the script from a pwsh prompt, or use `pwsh -NoProfile -Command`. Measured 2026-09-16; the parameter's own help says so too, which is where a reader will be looking.

### A gate count is not a baseline without its gate list

The default set is four gates, and every `screenshot-*` mode is available under its own name — so "6/6" and "4/4" can describe the same tree in the same state, and a run reporting fewer gates than an earlier one has not lost one, it was asked for less. **Record the gate LIST beside the count whenever a run is used as a before/after baseline.** A bare count invites the alarming reading at exactly the moment it is most available: during an integration or a restructure, where "a gate has gone missing" is a plausible story and checking it costs a re-run.

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

## A route that CLEARS the transcript gets its own mode, not the last slot

`--smoke-replace` exists because every check in it replaces the conversation — the moved-root banner's
fresh answer, and the resume banners' "Start new conversation". Each clears the transcript, so under the
rule below each would have to run **last**, and "last" is a single slot that several checks already want
and say so in their own comments. A second mode costs one more gate and removes the ordering constraint
outright.

**What it reaches that a view-model test cannot** is the BUTTON. `ResumeRootPrecheckTests` and
`RecapFailedForTheSessionTests` pin the same routes and invoke the command directly; the mode finds the
button *by the command it is bound to*, checks it is enabled, and clicks that. A binding onto a command
that does not exist resolves to null with only a trace message — clean build, visible button, nothing
happens on click — which is the failure the history rows had before they were given a `CanExecute`.

**Verified load-bearing twice, and the second one is the more interesting.** Removing the tray carry from
`PromptDelivery.ReplaceConversation` fails BOTH routes on exactly one sub-check, `heldSurvived=False` with
an empty tray and only the parked message in the transcript, on runs that otherwise complete normally — the
defect that shipped in 1.0.0. Withholding the Choice banner's fresh answer fails the choice route alone, on
`buttonOffered=False`, while the fork route stays green: two routes pinned independently.

**A button must be asserted VISIBLE, not merely enabled.** Executing a command works whatever the button's
visibility, so a check that only reads `IsEnabled` passes on a button the user could never click — and the
Choice banner renders its fresh answer collapsed whenever a full reload is still available, which is most of
the time. Measured: with visibility unasserted, the withheld-answer injection above passed. **The same trap
moved the phase off its own route**: inflating a transcript to make one banner appear replaced the other
phase's banner with it, so the fork check silently ran against the Choice banner instead. Both phases now
assert WHICH banner they are looking at.

### Two harness traps, both met the first time a mode was added

**A new mode must be added to BOTH tests that ask "is this a text-result mode".** `run-smoke.ps1` decided
that in two places, each spelled as `smoke` or `perf`, so the new mode was treated as a **screenshot**: the
run failed correctly with exit 1 and `prove-check.ps1` reported `INCONCLUSIVE`, because the verdict never
reached the stream it classifies. That is *"a smoke verdict reaches the classifier only if the stream
carrying it does"*, arriving on schedule.

**The permission router is wired to the FIRST view-model, so a second one never sees a request.** A mode
that builds its own view-model must re-point `UiPermissionRouter.Prompt` at it, and answer the banner. Left
alone, the fake's command tool call opened a banner on the *other* window while this run waited out its idle
ceiling — 30 seconds of a 32-second gate — and then **passed for the wrong reason**: the tray still held its
message because nothing had released it, which is indistinguishable in the assertion from the carry having
worked. Answering the permission drops the gate to 2 seconds AND strengthens the claim, because the held
message is then seen to go OUT rather than merely to survive. **A check whose subject is "did this reach the
agent" must assert the turn settled**, or a stalled run reads as a pass.

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

- **`--perf` ALWAYS EXITS 0.** It catches its own exceptions into a `FAILED:` report and ends on `Shutdown(0)`, so the exit code proves nothing and **the artifact is the result**: check `perf-result.txt` exists and is fresh, or a run that did no work reads as a pass. **And freshness binds the WAIT as much as the read** — waiting for an artifact to EXIST is satisfied the instant the loop starts if a previous attempt left its copy there, so the wait returns immediately and hands over the old numbers with nothing about them marked stale. A long run that failed early is exactly the case that leaves one behind. Wait on something the run WRITES AT ITS END, or delete the artifact before starting; "it appeared" is not the same fact as "this run produced it".
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

**And "read it" assumes a reader, which a SWEEP does not have.** A loop that runs this over every script and records exit codes turns a script whose anchor has gone into a confident `PINS NOTHING` — the one verdict a sweep exists to produce, stated about a check that was never exercised. Measured: a sweep reported it that way, and the dead script was found only by opening the run's own log. **So a runner requires the verify's own summary line — a test total, or a smoke PASS/FAIL — before it believes any verdict**, and reports its absence as a verdict on the instrument.

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

### A fixture that seeds what the product never writes

The resume fixtures seeded a prior conversation as a user turn plus **one** agent entry holding a
whole reply, tagged `Role = "assistant"`. The product writes neither: a streamed reply is one entry
per FRAME, closed by a `turnDone`, and the role it stamps is `"agent"`.

**The two halves fail differently, and only one of them fails until something reads the role.** It is a
string no code reads — `TranscriptText` branches on the entry (a user's `Text`, an agent's `Event`) and
`ResumeDecider` on the event's type — so the fixtures could tell that lie indefinitely, and the first
code to read the role would behave one way in the pane and another in every resume test with nothing
red. The single entry is the half that cost something immediately: `TranscriptText` merges consecutive
text frames into one `Assistant:` line and flushes at the turn's end, and a one-frame fixture exercises
neither. **Measured:** injecting a flush per frame — so one reply becomes several `Assistant:` lines in
the recap that is the whole of what the next agent knows — failed only the checks written with the
merge in mind, and no other test in the suite.

So the seeding is one helper, `SeededConversation.AddExchange`, and every resume fixture goes through
it. The rule it stands for: **a fixture's shape is part of what the test measures**, and a shape the
product cannot produce is a test passing about a state that never occurs.

### A CORRECTION is itself a claim, and it gets less scrutiny than the original

Measured over one review round: three of the corrections written to fix precision defects were themselves
wrong or incomplete — a walk that named a continuation and asserted it always exists (it does not: every
`await` in the method sits inside one conditional block); a fixture doc rewritten to say "at most N pieces"
directly above its own worked example of MORE than N; and an identifier reported as now named in the text
that `git grep` could not find anywhere in the tree.

Each was written in the act of fixing exactly that kind of defect, and each got less examination than the
sentence it replaced. The reason is not carelessness, it is the state of mind: writing a correction feels
like *this is now right*, which is the same feeling that produced the original. **So re-check a correction
with the same instrument that found the fault** — if a grep found it, grep the replacement; if a walk found
it, walk the replacement. The cost is seconds and the alternative is a record that reads more carefully than
it is.

### A sweep over a RED baseline reports LOAD-BEARING for everything

The verdict a sweep runner reads is *"did any test fail"*, and a failure that was already there answers
yes whatever the injection did. So **every** script comes back `LOAD-BEARING`, the counts look ordinary,
and the identities do not save you either: the pre-existing failure's name sits in the list beside
whatever the injection really broke, and nothing on the line says which is which.

Measured: a statement dropped from a method while its comments were being moved into its doc left one
check failing, the build stayed at 0/0, and fourteen scripts returned `LOAD-BEARING` in a row before the
regression was found — by reading a diff for an unrelated reason, not by anything in the sweep.

**So a sweep runs the suite FIRST and refuses to start unless it is green.** Stronger than comparing each
run's failures against a recorded baseline set, because with a red baseline the counts are not comparable
at all; and worth the extra run, because a sweep is an hour of runtime spent producing one sentence and
nobody re-runs it to check. It is the count-and-identity rule one level up: **the denominator of a sweep
is a green tree.**

> **Which side of the repository boundary each guard lives on**, because two of the three in this section
> read alike and only one is in the tree. `scripts/` holds `prove-check.ps1`, `run-gates.ps1`,
> `run-smoke.ps1` and `measure-tool-usage.ps1` — **there is no sweep runner here**, and there is not meant to
> be one, no injection script but the two examples staying on `main` past its pull request
> ([`scripts/inject/README.md`](../../scripts/inject/README.md)): a runner drives the library of injection
> scripts, which lives outside this repository.
> So **the green-baseline gate above, the per-script target re-hash below and the population echo are
> properties of that external runner**, described here because the RULE is the repository's even where the
> code is not. The
> **breadcrumb** below is the opposite: it is implemented in `prove-check.ps1`, in this tree, and a reader
> can go and look at it. Do not read the three as one tool — and note that the paragraph after this one
> names the runner and the script in consecutive sentences, which is exactly the conflation this is here
> to stop.

**It invalidates every sweep figure taken before the guard existed, and the honest form is to say so
rather than to re-derive them.** Earlier figures on a branch were taken against suites that were green
*as far as anyone knew*, which is a different statement from *measured*, and quietly upgrading them is
the failure this rule is about. Say which a number is; do not re-run for the sake of the sentence.

**And the runner's own count is only worth something read against the population handed IN.** A population
that arrives as one joined string is one name matching no script: the runner reports `NO VERDICT (the verify
never reported)` for it and finishes with *"1 exercised, 0 LOAD-BEARING"* — every line of that true, and read
past in a second, because nothing on it is a number a reader recognises as wrong. The denominator is the only
thing that gives it away, and the caller is the only one who knows it. So the runner ECHOES the population it
was given before it starts and refuses a name with no file behind it, which is the count-and-identity rule
turned on the runner itself: **a sweep that swept nothing must not be able to end with a summary line.**

**SCOPE IS PART OF THE CLAIM, and a search scoped to one directory answers a narrower question than the one
asked.** The sharp case: *"the only caller"* of anything `public` is a tree-wide question, and no
directory-scoped search can answer it — a grep over one project finds that project's callers and reports them
as if they were all of them, because nothing in the output says what was not looked at. **An absence is
evidence about the SET SEARCHED and about nothing else**, which is the same shape as the population echo
above: an instrument answering confidently about a population smaller than the one you meant.

**What makes it a rule here rather than a general caution is that scoped searches DERIVE POPULATIONS.** The
re-proof set is worked out by searching — for anchors, and for the checks that read what moved — so a search
scoped to the wrong tree does not merely produce a wrong sentence. It **silently shrinks the set of guards
that get re-proved**, and every verdict in the smaller sweep is correct, so nothing flags the omission: the
summary line counts what ran and cannot count what was never selected. **So state the scope with the
derivation**, and where the subject is a member other assemblies can reach, the scope is the whole tree
including the hosts and the tests.

**And a sweep runner killed mid-run leaves its target INJECTED** — the runner is the external one, and
what it leaves behind is the in-tree script's doing: `prove-check` restores in a `finally`,
which a terminated process never reaches — so an aborted sweep leaves a tree that builds, mostly passes,
and contains a bug nobody wrote. **The two rules compose**: an abort that leaves an injection behind
produces exactly the red baseline that then makes everything read load-bearing, which is why the guard
above catches most aborts on the next run rather than by looking for them. What it cannot catch is an
injection the suite stays green over — one that pins nothing — so the external runner also **re-hashes every
target after each script and stops if one differs from what it snapshotted at the start**, which closes
the within-sweep case. The across-runs case is closed by a BREADCRUMB, not by a hash: `prove-check`
writes `scripts/.prove-check-in-flight`, naming the check and its targets, immediately before it injects,
and deletes it in the same `finally` that restores. **A marker present at startup means a previous run
died injected**, and the script refuses to start and says which files to look at. Its PRESENCE carries the
fact rather than its content, which is why it is immune to the bootstrap problem a hash has — it survives
for exactly the same reason the restore did not happen. It is gitignored: a marker committed into a clone
would refuse every run on a machine where nothing had happened.

**A killed sweep leaves RUNNING PROCESSES as well as an injected file, and those are what turn the next
verdict into a measurement of the previous one.** `dotnet test` runs the suite in `testhost.exe` children;
terminating the runner orphans them, and they hold the test project's output assemblies open. The next build
then fails on locked files — which is visible — and a sweep whose `-Verify` carries `--no-build` runs anyway,
against the binary the last SUCCESSFUL build produced, which is the one with the injection compiled into it.
Measured: a verify reported `Failed: 2` from a binary still carrying the PREVIOUS script's injection, over a
source tree that had been correctly restored — the two disagreeing being invisible, because only one of them
is what runs.

**And a sweep that exits CLEANLY leaves the same thing behind, which is the case a reader will meet more
often.** Every script in a sweep runs with `-NoRebuild` — the right choice per script, since the next one
rebuilds anyway — so the binary surviving the last script is the one built with ITS bug in it, beside a source
tree the `finally` has correctly restored. Measured: a suite green minutes earlier failed exactly one check
straight after a 12-script sweep, with `git diff` over `src/` empty, and the failing assertion was the one
that script was written to break. **Nothing is wrong and nothing will say so**, because the restore did happen
and the guards all passed. So a sweep rebuilds before it reports, and **`git diff` being empty is not evidence
that what you are about to run is the code in front of you.** **So a `--no-build` verify is worth exactly what the last build was**, and the state to restore
after killing a sweep is three things and not one: kill the orphaned hosts, rebuild, and only then believe a
number. The green baseline gate catches this when the leftovers turn the suite red and cannot when they do
not, which is the same blind spot the target re-hash exists for.

**A guard that REFUSES is reporting a fact about the tree; one that ERRORS is reporting its own failure —
and the two are read in opposite directions.** The rule above, that `INCONCLUSIVE` and any error from the
injection step are verdicts on the INSTRUMENT, applies where the instrument could not do its job. A refusal
is the opposite case: it did its job, and what it found is the thing to go and look at. The breadcrumb
above refused two consecutive runs and was diagnosed as unreliable; it was correct both times, and the marker
it was refusing over had been left by a teardown of the form `cp <backup> <target> && rm -f <marker>`. The
`cp` failed — the backup had already been consumed — so `&&` never reached the `rm`, and a run that really had
finished injected left its marker standing. **A guard's clear must not be CONDITIONAL on unrelated work**:
chained behind a command that can fail, the clear is the step that silently does not happen, and the
consequence lands on the next run where nothing explains it.

**A file the injection wrote that `-Path` did not name is the other way a tree is left injected, and the
breadcrumb cannot see it.** That failure exits NORMALLY: the `finally` restores exactly what it
snapshotted, deletes the marker because nothing was killed, and leaves the unnamed file carrying the bug.
Measured — a script patching two files, run with one of them as `-Path`: the hash check reported "the
injection changed no file", which was true of the snapshotted one, and the other file stayed injected
through the report, the restore and the clean exit. The symptom names the wrong thing, which is what costs
the time.

So `prove-check` diffs `git status --porcelain` across the injection and **refuses when a file it did not
snapshot became dirty**, naming it — a tree that was already dirty is fine, because what it looks for is a
file THIS run made dirty and is not going to put back. **That tolerance is also the blind spot:** a stray
write into a file that was ALREADY dirty is invisible to it and is not restored either, which mid-refactor
is the normal state of the tree — the other reason to commit before a sweep, beyond keeping an anchor loop
from eating the work. It is advisory where git cannot answer, the rest of
the script deliberately not depending on git, and it runs BEFORE the missed-anchor throw so the message
names the cause rather than the symptom. It cannot restore the file — it has no snapshot of it — so it says
which one to restore.

**The refusal says how to clear it, and that is not politeness.** A guard that blocks every later run
without stating a way forward is a worse failure than the hazard it prevents, and whoever hits this one is
by definition in the state where they have least idea what is going on. So it names the marker's path, says
to read `git diff` on the targets and restore them first, and says not to build, test or sweep until then —
because every result before that measures the injection. `git checkout -- <file>` is the right tool there
and only there, while the file holds no uncommitted work of its own.

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

### A verdict licenses the CHECK, never the explanation written beside it

**`LOAD-BEARING` is a fact about a test and an injection, and it certifies nothing about the sentence saying
WHY the test can fail.** The two are independent: an injection removes a guard, the check goes red, and the
verdict is earned whatever the paragraph above that check claims about the path reaching it. So a wrong
mechanism beside a load-bearing check is invisible to every instrument in this document — and it is the half
the next reader actually uses, because nobody re-derives a path that already has an explanation.

**Measured, one round on one branch: three published mechanisms wrong, three green checks** (2026-09-26). The
release a tray's hold refuses was documented as coming from the statement order inside a clear method; it comes
from a cancel one call earlier, which sets the same flag false synchronously, leaving the documented close a
no-op. A missing hold was documented as failing on a picture assertion; the run's own TRX showed the picture
PASSING and a sentence assertion failing. And a signal was documented as making a check "measure the first move
twice over" when it cancels that move altogether. Each check was load-bearing before the correction and after
it, and nothing went red in between.

**Two consequences worth acting on.** Prose is checked by READING THE CODE, and a verdict is not evidence for
it — so a review that confirms verdicts has confirmed nothing about the explanations, and the cheapest honest
form is to **name the statement that does the work**, because a named statement can be looked at and a
described path cannot. **And where a mechanism claim would be falsifiable, write it that way**: *"reordering
these two lines would exercise the hold"* is a prediction, and it was wrong, where *"this setter posts the
release"* points at a line a reader can go and read.

### Two failure modes recur when writing the injection

**An expectation-shaped traversal.** The #142 proof walked the *leaves* and asked whether each applied, but a broken build offers a **group**, which is not a leaf — so the sweep examined nothing and agreed with itself. **Iterate what the code under test actually produced, not what you expect it to have produced.** A near neighbour, same issue: comparing objects across **two separate gathers** — a second `RegisterCodeFixesAsync` pass builds fresh `CodeAction` instances, so a reference check found no match for any leaf and "this one was not offered" was trivially true of everything.

**Pinning the harness rather than the rule.** Both instances found by injection, both #62. A token refused by an *earlier* gate can never reach the gate under test, so the case has to get past every prior refusal before it proves anything. And a case that injects its own dependency cannot see the real one, so a rule living only in the default path — a filesystem predicate, a clock, a resolver — needs one case that takes that path.

### The injection must COMPILE, so falsify the behaviour rather than the condition

Replacing a guard's condition with a constant (`if (false)`) makes its body unreachable, and a warning in a tree built to 0 warn / 0 err takes the run to `INCONCLUSIVE` — which reports that the INSTRUMENT failed, not that the product is sound. Removing a `!`, or otherwise making the guard genuinely not fire for the callers it protects, injects the same defect and still builds. Same family as the rule that a green over an injected bug is a verdict on the check rather than the code: **an injection that cannot run has measured nothing.**

### Write the script LITERALLY; do not generate one through a writer

Its anchors are `'\r\n'`-joined source lines in a tree checked out `eol=crlf`, and those two-character escapes have to survive every layer between the author and the file. Build the script as a string inside another script and that layer consumes them: measured, the escapes arrived as real CR+LF bytes, which split the anchor's own string literal across lines and left a `SyntaxError`. **So write it as its own text** — a literal heredoc, or a file-writing tool — and **never re-type a literal that already exists correctly in the target file; copy its bytes.** The same fault reached a test's verbatim `C:\repo` (which became `C:` + CR + `epo`, compiled, and went red for the right reason on garbage literals) and a sentence quoting the escape sequence it was about.

**Every generated write takes a post-condition, and one cheap check catches all of it:** assert the file's CR count equals its CRLF count, so no lone CR survives; then `ast.parse` the script and `grep -c` its anchor in the target. None of that needs a build. **A lone CR is invisible in every diff and in most editors, and `git status` reports nothing.**

**A broken script costs a verdict, not a wrong answer.** The runner reports the failed injection and names the exit code, and `prove-check.ps1` restores its snapshot first — so it fails loudly and changes nothing, which is the behaviour to preserve. The risk is reading that as a fact about the product: **`INCONCLUSIVE`, and any error from the injection step, are verdicts on the INSTRUMENT.** Read the script's own log before the code under test.

### An anchor's indentation can alias a deeper one

A twelve-space anchor occurs inside every twenty-space line with the same text, so a count of "how many places does this appear" answers a different question than the one asked — measured, a count reported three occurrences of a line that appears once at that depth. **Anchor each line to its own start** (a leading newline in the pattern), or match on lines rather than on substrings. An injection written without that edits a site nobody chose, and it does so silently.

### Do not edit AROUND a known defect in the file whose anchor you are changing

"Not what I'm here for" is the judgement to distrust when the thing being stepped over is in the file being edited. Measured 2026-09-16: a script's CRLF fallback ran twice, converting an already-converted string into `\r\r\n`; it was noticed, left, and then the very anchor it operates on was rewritten. The re-anchor failed twice and cost two cycles — once for that, once because a fix earlier in the same round had given a second call site the same shape and made the anchor ambiguous. Both were self-inflicted and both were visible at the moment of the edit.

### Before a sweep, grep for the identifiers you DELETED, not only the lines you rewrote

A rewritten line is something you are looking at, so you grep for it. **A member you removed leaves nothing on screen to remind you**, and every script that named it is now either an error (its anchor is gone) or `INCONCLUSIVE` (its injected text no longer compiles) — two different verdicts for one cause, which makes them read as unrelated. Measured 2026-09-16: a grep over the rewritten lines caught two scripts and missed two more that named deleted members. It is the cheaper half of the same check.

## An instrument that reports confidently about nothing

Every rule above is one instrument lying about one run. These are the ones that lie about whether a run
happened at all, and they are grouped because the tell is the same each time: **a number arrives, it is
the number you expected, and nothing in it says what it was measured over.**

### A test that passes when you expected RED is a question, not a result

**It is the cheapest measurement available and the most likely to be read as an answer.** Writing a check for
a defect you have not reproduced yet, and watching it pass, says one of three things — the defect is not
there, the check cannot see it, or the SETUP never staged it — and the third is the commonest and the only
one that looks like the first.

Measured twice in one step, and both times the answer was the setup rather than the product. A check for a
double give-back was written against the phase whose gate the harness already had, and passed: that phase
catches its own failures and returns a reason, so the fault never reached the code under test. A check for a
conversation being recreated by a stale write passed because it deleted the conversation BEFORE the message
was recorded, so the record went into a new one and the case was never assembled.

**So a green on a check that has never been seen red is not evidence about the code**, and the next move is
to make it fail on purpose — against the unfixed code, or by breaking the setup — before believing either
outcome. This is the same rule as *the count and the identity together are what make a verdict real*, one
step earlier: a passing check with no red behind it has a count and no identity.

### A caveat that names no next action is decoration

**If a caveat is real it names something to do; if it names nothing, either it is empty or the action is
being skipped.** Measured on this repo's own reporting: a run went red, four later runs were green, and the
report said the green runs were *"not evidence the red was unrelated"* — and then moved on, which is
functionally treating them as exactly that evidence. The caveat was true, correctly worded, and changed
nothing.

**This is harder to catch than a missing caveat, because the record looks careful.** A reader checking
whether the risk was acknowledged finds that it was. So the test is not "did I state the limit" but **"what
does the stated limit tell the next person to do, and did I do it"** — here, keep the run's log and chase it
under the conditions it appeared in, which found nothing but is the only thing that could have.

### A rate must report the DENOMINATOR it measured

**Before trusting any pass/fail rate, print the artifact the run located and the number of tests it actually
executed.** A rate over an unstated denominator is not a measurement.

The denominator can be zero while the rate still looks like evidence. Measured 2026-09-16, probing one test
class twice: a loop whose summary extraction yielded an array rather than a line never fired its match and
reported **0 of 10 failing**; its replacement ran `dotnet test -f net10.0` against a project that builds
`net10.0-windows`, so every run died on *"The test source file … was not found"* and it reported **10 of 10
failing**. Zero tests ran either time, and both numbers were quoted before anyone looked.

**The dangerous half is the one that AGREES with the hypothesis under test.** "10 of 10 failing" arrived
while that test was already suspect and read as confirmation; "0 of 10" would have been questioned on sight.
An instrument is least examined exactly where it is most believed, and a loop around a command you have
typed a hundred times does not feel like an instrument at all.

### "N of N" states a count where the POPULATION is the claim

**A re-prove's population is every script anchored in a file the change touched — not the scripts the
change obviously reached.** A sweep scoped to the second cannot by construction find the ones it silently
killed, because a script whose anchor a change destroyed is never RUN and so was never in the denominator.
It does not fail; it is absent, and the figure beside it is true of what ran and false as a claim about the
library.

Measured (2026-09-19): one script's anchor was rewritten by a fix three commits earlier, so it answered
`inject target not found` from that point on — and sat inside three separately reported "N of N
LOAD-BEARING" figures, each from a sweep of the scripts that fix had obviously touched. The wide sweep that
found it re-proved 23 scripts across the four files the work had edited: **22 were sound, and the dead one
was the script that work had itself written**, which is not a coincidence — it is the file being edited, so
it is where anchors move.

**Replacing a count with an INCOMPLETE list is worse than the count was.** This rule's own first
application named three checks where six existed, and read as exhaustive; the stale number it replaced at
least announced itself as something checkable, while a short list quietly excludes and nothing about it
looks wrong. So the rule has two halves: name the measurement's source instead of copying its result, **and
either name them ALL or say the list is not exhaustive and where the full set is.** A count ages; a
truncated list is wrong on the day it is written.

**The generalisation, which is the useful half: before trusting a count, ask which two answers it would
print the same.** The same session then reconciled a suite total by diffing added and removed `[Fact]`
lines per commit, and got "0 removed" for a commit that did replace a test — git aligns identical attribute
lines as unchanged context, so that instrument prints the same thing for "nothing was removed" and "a test
was replaced in place". Counting the declarations PRESENT at each commit separates them, and separates a
`Theory` that quietly lost a case from one that did not. Twice in one step, an instrument measured what it
TOUCHED rather than the population, and both times in the reassuring direction.

### A shell text tool is not a line-ending instrument, and `git status` cannot see a flip

An editor that writes LF into a tree checked out `eol=crlf` breaks every CRLF-only anchor in the file it
touched, and **neither obvious instrument can see it.** `git status` compares after normalisation, so a file
rewritten wholesale from CRLF to LF reports as unmodified — a residue check run straight afterwards will say
the tree is clean, twice, and mean it. And MSYS `grep`/`sed`/`cat -A` normalise on read: measured 2026-09-16,
`grep -c $'\r$'` reported 8145 of 8145 lines CRLF for a file a binary read showed had **zero** CRLF pairs and
8145 bare LFs, and `cat -A` printed a bare `$` on a line it had just counted as CR-terminated. Two tools,
same bytes, opposite answers.

**Measure line endings in binary** — `b.count(b'\r\n')` against `b.count(b'\n')` — never with a text tool;
and when a CRLF-only anchor fails on code nobody edited, suspect the FILE's endings before re-anchoring. The
repository itself is never harmed, because git normalises to LF in the blob on commit, which is exactly what
lets the damage survive a clean `git status`.

*The tell that is there all along:* git prints `LF will be replaced by CRLF the next time Git touches it` on
every `git add` of such a file. It reads as routine noise; it is the flip being reported.

**And survey the WORKTREE, not the index.** It is a whole-file write of a NEW file that lands LF here; an
in-place edit of an existing CRLF file preserves its endings. So the files that flip are precisely the ones
git has not yet heard of, and the two failures compound: a survey driven by `git ls-files` reports "LF or
mixed: 0" and the zero reads as proof. Measured 2026-09-16 — two newly written files carried 29 and 106 bare
LFs with no CRLF pairs while a survey run moments earlier over the tracked list reported none. **The
confirming check has the same shape and so decides nothing**: run after an intervening `git add`, the index
lists those files as tracked, which is equally consistent with "untracked at survey time" and "tracked all
along". Only the binary read separates them.

### An in-place file rewrite reads into a VARIABLE first

`b = open(p, 'rb').read()` on one line, `open(p, 'wb').write(transform(b))` on the next. Never
`open(p, 'wb').write(open(p, 'rb').read()...)`: Python evaluates a call's TARGET before its arguments, so the
`'wb'` open truncates the file to zero bytes and the nested read then runs against the file it has just
emptied. The one-liner is the natural way to express an in-place transform and it destroys its input every
time it executes.

Measured 2026-09-16: a line-ending converter in that form emptied the source file it was converting, 182
lines to 0 bytes. It had been run several times before without harm purely because it had always found
nothing to convert — **the destructive branch executes only when a file actually needs rewriting, which is
exactly when the file matters.**

What made it expensive is that every downstream instrument then reported the truth about a destroyed file. An
empty `.cs` is valid C#, so the build SUCCEEDED with 0 errors; the runner found no such class and reported
*"No test matches the given testcase filter"*, exiting **0**; and three diagnostic passes went looking for an
incremental-build fault, a csproj `Compile` glob and a namespace typo before anything stat'ed the file.
**When a class you know you wrote is not in the assembly, check the file's SIZE before forming any other
hypothesis** — and treat a tool that rewrites files in place as needing the same proof of correctness as a
check, because it can silently destroy the evidence every other instrument is reading.

### A warning is not re-emitted by an incremental build

A project that did not recompile prints none of its warnings again, so "the build succeeded and no warning
line appeared" is not evidence in a tree held to 0 warn / 0 err — it is evidence about the projects that
happened to rebuild. Measured: a missing `<param>` tag (CS1573) shipped in a commit and surfaced only when an
unrelated change downstream forced that project to compile again. **Where the claim is "no warnings", the
build that supports it is `--no-incremental`.** Same family as the exit code above: the cheap reading of a
tool's output answers a narrower question than the one being asked.

## A comment that rules a state OUT is a claim, and needs the same proof as a check

A comment saying some state cannot occur — *"X cannot be carrying Y, because Z clears it first"*, *"this only
runs when W"* — is an assertion about reachability. Either derive it from something enforced (a type, a guard
the reader can see) or write down how it was checked. Where the code and such a comment disagree, **the
comment's claim is what to delete**, not only the branch it justified.

**It earns a rule because this class of comment is self-protecting.** It is written at the moment its author
believes it, it reads as the product of analysis, and it then tells every later reader not to look. Three of
them in one change, 2026-09-16:

- *"a prompted pane cannot be carrying a failed recap, because `Commit` clears it"* — `Supersede` KEEPS
  `_prompted`, `Adopt(prompted: true)` sets it, and on the route in question the failure happens *before* any
  session start, so no `Commit` intervenes. One of the ways back the user had been promised silently did not
  work.
- *"the swap calls this for itself, so clearing inside `Detach` is safe"* — `Detach` has a second caller,
  every session start, including the one the failing send makes for itself.
- *"`Detach` has one caller"* — from a truncated grep. It has three.

**The tell is the shape of the sentence, not its subject**: a *because* clause doing load-bearing work about
what cannot happen. The cheap discipline is to name the mechanism that enforces it — and where that mechanism
is "every caller does X", to have counted the callers with a search proved complete first, which is the
denominator rule above applied to a code search.

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
