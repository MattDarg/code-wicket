# VSIX shell: commands, menus and packaging

> The reasoning and measurements behind AGENTS.md's rules for the VSIX shell — commands, menus,
> `.vsct` authoring and packaging.

> **`#NN` refers to a private pre-release tracker.** The numbers are stable *names* for a failure
> mode, not links — the rule each one tags is stated here in full, so nothing is missing if you
> cannot open them.

> Terms used here without definition (the Exp hive, rungs, warm start, the prove-check verdicts, …) are
> in [AGENTS.md](../../AGENTS.md#terms-the-docs-use-without-defining-them).


## Running it in the experimental instance

**What it needs.** Visual Studio 2026 with the **Visual Studio extension development** workload, which supplies the debug launch and the VSSDK tools used below. Visual Studio 2022 17.14 is a supported place to *run* an installed Code Wicket, but not to build this solution: its build stack cannot target `net10.0`, which every project except the shell and IDE layers uses. The offline gates need neither Visual Studio (see [verification.md](verification.md)).

**Running it.** Make `CodeWicket.VSExtension` the startup project and start debugging (F5 by default). The build deploys the extension into the **experimental instance** — a second Visual Studio profile, launched with `/rootsuffix Exp`, with its own settings, window layout and extension set — and starts that instance under the debugger. Deployment happens only for a build made inside Visual Studio (`DeployExtension` is gated on `BuildingInsideVisualStudio`), so `dotnet build` packages the `.vsix` and deploys nothing.

Two things about that instance are not what its name suggests:

- **It shares Code Wicket's own state with every other copy on the machine.** `%APPDATA%\code-wicket\config.json`, the saved conversations and `%LOCALAPPDATA%\code-wicket\logs` are per user, not per Visual Studio profile, so a setting changed in the experimental instance is changed for an installed copy too.
- **Environment variables in `Properties/launchSettings.json` never reach it**, because the debug launch runs through the VSSDK debug target. Configure behaviour through `config.json` ([settings-and-logging.md](settings-and-logging.md)).

**Resetting it.** Close the experimental instance, then run the VSSDK's `CreateExpInstance.exe`, which is under `VSSDK\VisualStudioIntegration\Tools\Bin\` in the Visual Studio install folder:

```
CreateExpInstance.exe /Reset /VSInstance=18.0_<id> /RootSuffix=Exp
```

`<id>` is the suffix of your Visual Studio 2026 profile folder, `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_<id>` (the experimental one sits beside it as `18.0_<id>Exp`). A reset discards the instance's deployed extensions, settings and layout; the next debugging session deploys a fresh copy. Reset after rebuilding when the pane fails with `":' is an invalid start"` — a stale bundled engine ([gotchas.md](gotchas.md)). For a settings page registered through the pkgdef that does not appear, `devenv /rootsuffix Exp /updateConfiguration` is the lighter fix and keeps the instance's state ([settings-and-logging.md](settings-and-logging.md)).

## The shell, its commands and its menus

**`CodeWicket.VSExtension`** — net472 VSIX, the thin VSSDK shell (raw VSSDK, no toolkit). `CodeWicketPackage` (AsyncPackage, bg load) + `.vsct`. Commands: `ShowChatWindowCommand` (**directly under `View`**, parented to `IDG_VS_VIEW_ORG_WINDOWS` at a high priority — *not* View > Other Windows, where two dozen ancillary panes live; this is the product's primary window).

**Group-family gotcha:** the whole `IDG_VS_WNDO_*` block (0x019E–0x01A7, incl. `OTRWNDWS1..6` **and `WINDOWS1`/`WINDOWS2`**) lives *inside* the Other Windows cascade — `WINDOWS2` reads like a View-menu group but only moves the command to a different section of the same submenu. The View menu's own window groups are the **0x072x** block; the tell is `IDG_VS_WNDO_FINDRESULTS` at 0x0724 sitting adjacent, and Find Results really is directly under View. Verified against the live menu 2026-07-26: `ORG_WINDOWS` = Solution Explorer/Git Changes/Team+Server Explorer/Test Explorer **and GitHub Copilot Chat**; `CODEBROWSENAV_WINDOWS` = Bookmarks/Call Hierarchy/Class View/Object Browser; `DEV_WINDOWS` = Error List/Output/Task List/Toolbox/Terminal. We share `ORG_WINDOWS` with Copilot — `DEV_WINDOWS` was tried first and landed us between Error List and Output, which reads as a diagnostic pane.

**The `Extensions > Code Wicket` menu** carries all five — the chat window (a `<CommandPlacement>`, so one button lives in both places), `OpenSettingsCommand`, `OpenLogsCommand`, `RestartCommand`, and `AboutCommand` (alone in its own group, so it sits last below a separator).

**`OpenSettingsCommand` opens the settings window, not our page** — unified-settings *external regions* have no deep-link (the ToolsOptions command argument is a Tools>Options *page GUID*, which a region doesn't have), so on both versions it's `IVsUIShell.PostExecCommand(VSStd97CmdID.ToolsOptions)` and the value is discoverability — upgrade in place if a navigation API appears.

**`OpenLogsCommand` reveals `%LOCALAPPDATA%\code-wicket\logs` in Explorer** (`ProcessStartInfo(dir){UseShellExecute=true}` — the directory as the target, so there's no command line to quote); it creates the directory when absent (logs are written lazily, so a clean install legitimately has none, and an empty folder is a truer answer than an error) and is always enabled — unlike Restart there's no live object to depend on, and `startup-error.log` is written by the very failure that would stop a chat window existing.

**`AboutCommand` reports the version and build details** (`AboutDialog` — a code-built themed `DialogWindow`, same composition style as the tool window's status content): extension version, this assembly's build timestamp, VS brand/edition/release version, the **bundled** engine's build timestamp (labelled bundled because a `CWKT_ENGINE_EXE` override would point elsewhere) and the install folder, plus a **Copy details** button — the point is to make a bug report a paste rather than a transcription. Every fact is resolved from the running process (assembly metadata, the bundled engine's version resource, `IVsShell` properties + `DTE.Edition`); nothing is asked of the engine or a backend CLI, so it still answers when the chat window never started, which is when someone opens About — and the engine's build stamp is there for the stale-bundled-engine gotcha specifically. Always enabled, for the same reason as Open Logs Folder.

**A build timestamp must be stamped at COMPILE time, because a file timestamp is the INSTALL time** (issue #136). Both rows read `File.GetLastWriteTime` at first, which is the one thing about a deployed file that cannot survive being deployed: measured on one machine, an Exp deploy stamped every file in the extension folder — shell DLL and bundled engine alike — with one identical time, so the two rows could never differ from each other, neither was a build time, and the stale-bundled-engine question they exist to answer was unanswerable by construction. `src\BuildStamp.targets` (imported by the VSIX and the engine, and only those two) appends the real UTC build time to `AssemblyInformationalVersion` as semver build metadata — `<version>+<sha>.<yyyyMMddTHHmmZ>[.ci]`. **`InformationalVersion` rather than an `[AssemblyMetadata]` attribute, because the stamp has to be readable off the ENGINE**, which is net10 while the dialog is net472 and can never load it: `InformationalVersion` lands in the Win32 version resource as `ProductVersion`, which `FileVersionInfo` reads from a file without loading it — one mechanism for both rows. The timestamp is compact (`:` is not legal after a `+`) and the sha half is the SDK's own doing, so `BuildStamp.Parse` reads the identifiers **by shape, never by position** — semver's build metadata is an unordered set, and a third identifier arriving later must not be able to shift the meaning of the two already there. **`.ci` marks a release-workflow build**, which is what separates an official build from a local one of the same commit, and it is what the release gate asserts: the parsing is unit-tested (the source file is *linked* into the test project, since the dialog holds no runtime dependency on our other assemblies on purpose), but nothing offline can see whether the build still stamps anything at all — and a build that quietly stopped degrades to "build time unknown" in a dialog nobody opens until something is already wrong. **The fallback is deliberately relabelled rather than removed**: with no stamp to read the row still shows the file's timestamp, as `installed <time> (build time unknown)`. Being told the install time is useful; being told the install time and calling it a build is the bug. Cost: the stamp changes every build, so those two projects recompile every build — measured at ~1.1 s over a no-op solution build (1.85 s → 2.99 s). The same stamp feeds the tool window's `Starting …(build …)` line, which had the identical defect.

**The version has ONE source: `source.extension.vsixmanifest`** (statically `0.1.0`; the release workflow rewrites it from the pushed tag *before* building) — the `StampVersionFromVsixManifest` target copies it onto `FileVersion`/`InformationalVersion`, so the dialog reads assembly metadata instead of parsing a deployed manifest, and the DLL's file properties become truthful too.

**`AssemblyVersion` deliberately stays 1.0.0.0** — a release must not perturb this assembly's binding identity for a cosmetic string. The SDK appends `+<commit sha>` to InformationalVersion on its own, so the dialog's `'+'` trim is required, not defensive.

**`RestartCommand` rebuilds the chat window's session IN PLACE** (`ChatToolWindow.RestartAsync` — teardown, then re-run the normal init), which is literally what every "applies when the chat window next opens" setting needs (provider registration, custom agents, `EngineEnvironment`, all the `CWKT_*` env are forwarded on the engine process at *launch*).

**Closing the frame and reopening it does NOTHING — do not "simplify" Restart back to that.** It looks sufficient, because `ChatToolWindow` builds its whole graph in one init path and tears it down in `Dispose`, so the frame lifecycle looks like the restart. It is not: **VS caches tool-window panes for the package's lifetime, and closing a tool window's frame only HIDES it** — the pane isn't disposed until VS shuts down, so `ShowToolWindowAsync` hands back the *same live pane*, and the engine (and the agent CLI under it) survives while the window blinks convincingly. Measured 2026-07-28: after a close and reopen, `engine.log` held exactly one window init (`[terminal-mirror] setting=`) and one engine launch (`[probe]` block) and `acp.log` did not roll — the engine and its agent CLI survived, so an updated `claude-agent-acp` on disk was not picked up.

**Diagnostic tell for any future "did it really restart?": a genuine restart rolls `acp.log`/`engine.log` (`DiagnosticLog.StartRun`) and ends the live agent session.** The in-place path shares ONE teardown implementation with `Dispose` (`TeardownSession`) rather than duplicating it — that was the original concern and it's addressed by extraction, not by leaning on a frame lifecycle that doesn't fire. The conversation survives via the usual restore-most-recent-session. The command also `Show()`s the pane first, since a "closed" tool window is really a hidden one and the rebuild reports progress/failures in the window. Disabled (`BeforeQueryStatus`) when no chat window is open.

**Default keybinding `Ctrl+\, Ctrl+W`** on the chat command (vsct `<KeyBindings>`, global scope `guidVSStd97`): `Ctrl+\` is VS's *open-a-tool-window* chord family — a two-key chord in a sparse family beats the free `Ctrl+Alt+<letter>` singles (E/F/H/K/R/Y), which are what third-party extensions fight over.

**The letter is not a preference: the family NAMES THE WINDOW in its second key**, read off the shell's own binding table (2026-09-08).

| chord | window |
|---|---|
| `Ctrl+\, E` | **E**rror List |
| `Ctrl+\, T` | **T**ask List |
| `Ctrl+\, D` | Code **D**efinition View |
| `Ctrl+\, C` | **C**opilot (theirs) |

Code **D**efinition View is the precedent that a generic leading "Code" is skipped, so Code **W**icket takes **W**. `C` is the other reading of the same rule and Copilot has it. **A future rename should move this letter again — that is the convention working, not a defect in it.**

**Only the shell baseline (the VSSDK's own `*.vsct`) is checkable offline, and it UNDER-REPORTS** — the per-machine `CurrentSettings.vssettings` stores only *diffs* from the scheme, so it cannot enumerate the rest, and Copilot's `C` is not in the baseline either. **A clean baseline is necessary and not sufficient**; confirm in Tools > Options > Environment > Keyboard ("Shortcut currently used by"). If it ever collides it is one element to delete, and the command stays bindable by hand.

**Both variants of the second key are shipped (`Ctrl+\, Ctrl+W` + `Ctrl+\, W`), and that follows the SHELL, not just Copilot** — `E`, `T` and `D` each appear twice in the baseline's own table, so a single-variant binding is the odd one out. Holding Ctrl through a chord is a habit, not a rule, and the release case would otherwise silently do nothing. (Confirmed against the live binding table 2026-07-26: GitHub Copilot ships `Ctrl+\, C` **and** `Ctrl+\, Ctrl+C`.)

**The command alias rule (settled in Visual Studio 2026-07-26, over four rounds — get this right and don't re-derive it):**

> **displayed alias = `<parent menu's category>` + `"."` + `<LocCanonicalName>`**

`<Strings>` has six children in a fixed order — `ButtonText`, `MenuText`, `ToolTipText`, `CommandName`, `CanonicalName`, **`LocCanonicalName`** (from the SDK's own `ConvertCTCToVSCT.pl`, lines 231–251). Of those: `CommandName` is only the human-readable label in the Customize/keyboard *list*; **`CanonicalName` is inert** (our four Extensions commands each author one and it is never used); **`LocCanonicalName` is the live element**, and it supplies ONLY the part *after* the auto-prefixed category.

**Multi-dot is legal there** — that's exactly how Copilot gets `View.Github.Copilot.Chat` (category `View` + `Github.Copilot.Chat`). Authoring the full `View.CodeWicket.Chat` yields **`View.View.CodeWicket.Chat`**. With no `LocCanonicalName`, the remainder is **synthesized from the sanitized `ButtonText`** ("Settings…" → `Settings`). Ours: chat = `CodeWicket.Chat` under category `View`; Settings, Open Logs Folder and Restart ride the synthesis (category `CodeWicket` + `Settings`/`OpenLogsFolder`/`Restart`) — **note that makes their alias depend on their ButtonText, so renaming a menu label silently renames the alias**; give them an explicit `LocCanonicalName` if that ever matters. **About is the one that does not ride it**: it authors `LocCanonicalName` `About` explicitly, for exactly that reason.

**Why this took four rounds:** all four authored names *coincided* with what synthesis produces, so only the chat command was ever a real test case, and its every failure looked like dot-mangling. Dots are NOT stripped; a `\.` escape is neither needed nor honored (both were tried, and a full Exp reset ruled out cache). Renaming an alias can't break a shipped `<KeyBinding>` (bound by guid/id) but would orphan a *user*-created binding that referenced the old one.

### Focus on show

The shortcut is a way *in*, not just a show: `ShowChatWindowCommand` calls `IVsWindowFrame.Show()` (an already-open pane docked behind another tab isn't brought forward by `ShowToolWindowAsync` alone) then `ChatToolWindow.FocusInput()` → `ChatView.FocusInput()`, which puts the caret in the message box (dispatched at `Input` priority — focus set mid-frame-activation is discarded).

### What the package hosts

`ChatToolWindow` re-hosts the UI's `ChatView`, launches the engine, serves real `VsIdeServices` over IPC. `VsTheme.Apply` overrides the UI's `Chat.*` brushes with live VS theme colors. Settings render twice from one `ExtensionConfig`: the unified-settings region via `CodeWicketSettingsProvider` (`IExternalSettingsProvider` over config.json) on VS 2026, and the classic Tools>Options `DialogPage`s in `SettingsPages.cs` on VS 2022, whose Options dialog shows nothing else. Build bundles the engine + its net10 dependency closure into the VSIX.

## The debug-context command, in two menus (issue #73, rung 2)

`Send Debug Context to Code Wicket...` is placed twice, because there are two places a user is
standing when they want it: the **Debug menu**, where every other break-mode action lives, and the
**code window's context menu** in `IDG_VS_CODEWIN_DEBUG_STEP` beside Run To Cursor - which is where the
pointer already is when they are looking at the line that stopped.

### The Debug menu's groups are `IDG_VS_DBG_*`, not `IDG_VS_DEBUG_*`

**Measured 2026-08-26:** the Debug-menu entry was simply absent while
the *same button* appeared correctly in the code-window context menu. One vsct, one build, two
placements, one silently dropped — so the variable was isolated immediately: the Debug entry hung off a
custom group parented to `IDM_VS_MENU_DEBUG` itself, and the context entry went through a
`CommandPlacement` into an existing shell group.

The custom group existed on a premise that was **wrong**: "the Debug menu publishes no usable standard
group". It publishes five. They are just not named what you would grep for — `IDG_VS_DEBUG_*` finds only
`IDG_VS_DEBUG_DISPLAYRADIX` (0x0185), which lives elsewhere entirely, while the Debug menu's own are:

| Group | Id | What sits in it |
|---|---|---|
| `IDG_VS_DBG_STEP` | `0x0151` | Step Into / Over / Out |
| `IDG_VS_DBG_WATCH` | `0x0152` | QuickWatch… |
| `IDG_VS_DBG_BRKPTS` | `0x0153` | Toggle / New / Delete All / Disable All Breakpoints |
| `IDG_VS_DBG_STATEMENT` | `0x0154` | statement commands |
| `IDG_VS_DBG_ATTACH` | `0x0155` | Attach to Process…, Debug Dump File, Other Debug Targets |

All five are in the pinned VSSDK.BuildTools 18.5.40034 `vsshlids.h`, in `guidSHLMainMenu`'s own numeric
block. **And none of them renders.** Three placements were tried and every one drew nothing:

| Attempt | Placement | Result |
|---|---|---|
| 1 | custom `Group` → `IDM_VS_MENU_DEBUG` (`0x0089`) | nothing |
| 2 | `Button` `Parent` → `IDG_VS_DBG_WATCH` (`0x0152`) | nothing |
| 3 | `CommandPlacement` → `IDG_VS_DBG_WATCH` **and** `IDG_VS_DBG_BRKPTS` (`0x0153`) | nothing, either |

**The third was a deliberate probe rather than a third guess**, two groups at once so a single run in Visual Studio
could separate "wrong group" from "no group works here". Neither appeared, which is the answer that
closes the question — and it cost one run instead of two.

**Throughout, the code-window context placement drew correctly on every run.** That control is what
made the whole sequence diagnosable: the command is registered, its `BeforeQueryStatus` is reachable,
its other placements work, so the fault is the Debug menu rather than the command.

**The heading above those five ids explains it: "Run Menu Groups".** They date from when the menu was
called Run; VS's modern Debug menu is authored by the **debugger package**, and these survive in
`vsshlids.h` as legacy placeholders that nothing draws. So being correctly named and in the right guid
was never sufficient — **a header can define an id that no live menu consumes, and nothing about
reading it tells you which.**

### The real Debug menu

| | |
|---|---|
| Menu | `guidVSDebugGroup` `{C9DD4A58-47FB-11D2-83E7-00C04F9902C1}` :: `IDM_DEBUG_MENU` (`0x0401`) |
| Groups in it | `IDG_EXECUTION` `0x0004`, `IDG_STEPPING` `0x0005`, `IDG_WATCH` `0x0006`, `IDG_BREAKPOINTS` `0x0007`, … |
| We sit in | `IDG_WATCH`, priority `0x1700` — just past `cmdidQuickWatch`'s own `0x1600` |

**None of that is inferred.** `VsDbgCmdPlace.vsct` is VS's *own* placement file: it parents every one
of those groups to `IDM_DEBUG_MENU`, and places `cmdidQuickWatch` into `IDG_WATCH` at `0x1600`. So the
semantic neighbour the command belongs beside — "show me what is in scope right now" — is
reachable, just not through the shell.

**All of it ships in the pinned SDK**, in the same `inc/` folder as `vsshlids.h`: `VsDbgCmd.h`,
`vsdebugguids.h`, `VsDbgCmdPlace.vsct`, plus `ShellCmdDef.vsct`, `SharedCmdPlace.vsct` and a dozen
more. **"Not in `vsshlids.h`" is not "not in the SDK".**

**Homes:** `guidVSDebugGroup:IDG_WATCH` in the Debug menu and `IDG_VS_CODEWIN_DEBUG_STEP` in the code
window — two surfaces, both confirmed in Visual Studio. It was placed in `Extensions → Code Wicket`
as well, and that third home was dropped: a break-mode action is greyed there nearly always, and the
label, which has to name its target in the Debug menu and the editor, rendered as *Code Wicket > Send
Debug Context to Code Wicket*. The guid is declared
in `<Symbols>` rather than pulled in with `<Extern>`, matching how `IDG_VS_EXTENSIONS` is handled: one
literal at the point of use, no dependency on vsct resolving `DEFINE_GUID` out of a C header.

**Three rules for finding a placement**, each a local form of AGENTS.md's *make any instrument prove it
can see a known-present thing before trusting what it does not find* — a header grep and a directory
listing are instruments too, and both fail as a believable absence:

1. **`ls` the SDK's `inc/` folder before concluding the SDK does not ship something.**
   `VsDbgCmdPlace.vsct` sits beside `vsshlids.h`.
2. **Before concluding a header does not define something, grep it for an id you know it defines.**
   `IDG_VS_DEBUG` returns one irrelevant hit; a group known to be on that menu — `QuickWatch`'s — comes
   back empty against the same prefix, which says the prefix is wrong rather than that nothing is
   there. `IDG_VS_CODEWIN_DEBUG_STEP` (`0x01C3`) for the context menu was read out of the header, not
   inferred from an absence, and was right first time.
3. **When a vsct placement can fail silently, place it twice**, so one run answers two questions: a
   second placement that draws isolates the fault to the placement rather than the command.

**`DynamicVisibility` is deliberately NOT set**, and the reason is the bug above. That flag makes an
item HIDE rather than grey, so a placement whose `BeforeQueryStatus` never runs is invisible — which is
indistinguishable from the command not being registered at all, which is exactly the silent absence this section is about. Without it a broken placement still draws, as a greyed item, and says so.
`DefaultDisabled` stays, so the first query rather than the manifest decides: the CTMENU cache is read
at startup, when nothing is stopped. The query reads `VsDebugState.IsInBreakMode` — the same predicate
the composer's own menu uses, so the two surfaces cannot disagree about whether the gesture is on offer.

**The context-menu entry stays beside Run To Cursor**, in the debug cluster, rather than moving to the
top where Copilot's Chat sits. Weighed and declined (2026-08-26): Copilot's entry is always applicable,
ours applies only in break mode, so at the top it would be a greyed item on every right-click in every
file. Being in the debug neighbourhood is where the eye goes while debugging, and the Debug menu entry
carries the discoverable-before-you-need-it job.

**`VsDebugState` is public rather than internal for exactly one reason**: the command has to answer
"is the debugger stopped" BEFORE the chat window exists, so it cannot go through `VsIdeServices` - and
the alternative, a second break-mode read of its own, is the drift the one-predicate rule exists to
prevent.

The command **shows** the pane rather than merely finding it: the chip lands in a composer the user is
about to type into, so leaving the window closed would put their evidence somewhere they cannot see it.
A window still building its view-model says so in a message box rather than doing nothing - a menu click
that silently no-ops is the silent-absence failure this section is about.
