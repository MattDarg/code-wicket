# Chat UI: rendering, transcript and input (`CodeWicket.UI`)

> The reasoning and measurements behind AGENTS.md's rules for the chat UI.

> **`#NN` refers to a private pre-release tracker.** The numbers are stable *names* for a failure
> mode, not links — the rule each one tags is stated here in full, so nothing is missing if you
> cannot open them.

> Terms used here without definition (the Exp hive, rungs, warm start, the prove-check verdicts, …) are
> in [AGENTS.md](../../AGENTS.md#terms-the-docs-use-without-defining-them).


## Contents

- **Rendering and markdown** — [Markdown rendering, and a document built while detached](#markdown-rendering-and-a-document-built-while-detached) · [Code highlighting, colour emoji and inline images](#code-highlighting-colour-emoji-and-inline-images) · [Clickable file references](#clickable-file-references) · [A tokenize that never returns (issue #177)](#a-tokenize-that-never-returns-issue-177)
- **Transcript scroll, focus and the composer** — [The transcript follows the newest content](#the-transcript-follows-the-newest-content) · [The resizable message box](#the-resizable-message-box)
- **Tool rows and edits** — [The tool row's intent subtitle (issue #131)](#the-tool-rows-intent-subtitle-issue-131) · [An MCP call is SHOWN by one name on every backend](#an-mcp-call-is-shown-by-one-name-on-every-backend) · [A read row names the file it read (issue #102)](#a-read-row-names-the-file-it-read-issue-102) · [An edit's two shapes must SAY the same things, not merely offer the same actions (issues #178, #189)](#an-edits-two-shapes-must-say-the-same-things-not-merely-offer-the-same-actions-issues-178-189) · [Folded tool row vs first-class edit card, and why both offer the same actions](#folded-tool-row-vs-first-class-edit-card-and-why-both-offer-the-same-actions)
- **Sub-agents and drill-down** — [Sub-agent nesting, and the row that completed before it started (issue #125)](#sub-agent-nesting-and-the-row-that-completed-before-it-started-issue-125) · [Opening a sub-agent's calls as their own transcript (issue #148)](#opening-a-sub-agents-calls-as-their-own-transcript-issue-148)
- **Mid-turn messages and steering** — [Held mid-turn messages (issue #70)](#held-mid-turn-messages-issue-70) · [The delivery guard covered the whole turn, so the tray worked once (issue #253)](#the-delivery-guard-covered-the-whole-turn-so-the-tray-worked-once-issue-253) · [The open-call ledger: one open/close pair per id (issue #190)](#the-open-call-ledger-one-openclose-pair-per-id-issue-190) · [A next step before the first step is not a safe point (issue #273)](#a-next-step-before-the-first-step-is-not-a-safe-point-issue-273) · [Steer vs cancel + send: the mechanism differs, the behaviour must not](#steer-vs-cancel--send-the-mechanism-differs-the-behaviour-must-not) · [Mid-turn steering: the mechanism under "send now"](#mid-turn-steering-the-mechanism-under-send-now) · [A held message could reach the agent stripped of its framing](#a-held-message-could-reach-the-agent-stripped-of-its-framing)
- **Images, drop and context capture** — [Pasted images (issue #118)](#pasted-images-issue-118) · [Dropping a file on the chat (#62)](#dropping-a-file-on-the-chat-62) · [Handing the debugger over: attached IDE context (issue #73, rung 2)](#handing-the-debugger-over-attached-ide-context-issue-73-rung-2) · [Handing over an Output pane](#handing-over-an-output-pane)
- **Render-cost instruments** — [Render-cost instruments (issue #86)](#render-cost-instruments-issue-86)
- **History picker** — [The CLI section in the history picker (issue #108)](#the-cli-section-in-the-history-picker-issue-108)
- **Session information** — [The session information panel (issue #160)](#the-session-information-panel-issue-160)
- **MCP roster** — [The MCP roster: a door, not a light (issue #122)](#the-mcp-roster-a-door-not-a-light-issue-122)

**`CodeWicket.UI`** — shared WPF chat (net472;net10.0-windows). `ChatViewModel` maps streamed `AgentEventDto`s → transcript items (marshals to the Dispatcher), `ChatView` + item templates, `Themes/DefaultTheme.xaml` brush keys via `DynamicResource` (VSIX overrides with VS colors).

## Markdown rendering, and a document built while detached

**No `IsExternalInit` polyfill here — don't add `record` types to this project; use plain classes/delegates.** **Assistant markdown** is rendered by `Markdown/MarkdownFlowRenderer` (walks the **Markdig** parser's AST → a themed WPF `FlowDocument`; brushes/fonts via `SetResourceReference` to `Chat.*` keys) hosted in a `FlowDocumentScrollViewer` via the `md:MarkdownText.Text` attached property (rebuilds the doc per streamed delta; **a doc built while the viewer is DETACHED is rebuilt when it re-attaches** (`MarkdownText.EnsureRerenderOnReattach`) — the virtualising panel recycles containers, so a render routinely fires while the viewer is out of the tree, where every `SetResourceReference`/inherited lookup answers with defaults and inline code spans get the **prose** font: narrower glyphs and no mono leading, so the message re-wraps and packs tighter than every other message in the pane (the reported "the last message goes dense", diagnosed live via the opt-in `CWKT_MARKDOWN_RENDER_LOG` trace in `Markdown/MarkdownRenderDiagnostics` — `connected=False` ⇒ `codeFont=<null>`; the same trace **ruled out** the two obvious suspects, since `width` and `docFont` read identically in healthy and broken frames)).

**The references recover on their own — the FORMATTING doesn't**, which is the whole reason a rebuild is required and why the fix can't be "make the resource lookups more robust": the doc was line-broken while they were unresolved, and one re-resolving to a value invalidates nothing that would re-break it, so the stale layout is what stays on screen. A check on the re-attached code span's *font* **passes with the fix removed**, so `MarkdownReattachTests` asserts that the document was *replaced*, which is the real contract. The detached render is NOT skipped (callers set the text once and read `Document` straight after), so the cost is one extra build+format per detach/attach cycle: measured over 3 `--perf` runs each way, the realistic **mixed** careful drag got *faster* (2128→1688ms whole drag, 0 pauses either way) while the **varied-sizes** stress drags ran **+16–19%** longer with the >50ms pause count barely moving (40→45, 51→56 of 200) — i.e. more total work spread over the same pauses, not new stutter.

**Don't measure this with the trace on**: it costs more than the fix does (+62% on the careful drag, 6252 synchronous file appends per harness run) and it hits precisely the gesture that resembles a real scroll. Cheap lever if the stress case ever matters: skip the corrective render for a doc with nothing tree-dependent in it (no code span, no fence, no link).

**User and "thinking" messages** are `Controls/SelectableEmojiText` (read-only RichTextBox + hidden measurer TextBlock so short messages shrink-wrap; literal text, no markdown).

## The transcript follows the newest content

**The transcript FOLLOWS the newest content** (`ChatView.TranscriptScroll_ScrollChanged`) by watching the scroller's extent, not item adds — streamed deltas append to an existing view-model and raise no `CollectionChanged`. Re-aiming goes through the two-pass `ScrollTranscriptToEnd` (second pass at `Loaded`, coalesced to one pending) rather than a bare `ScrollToEnd`, because a virtualising panel's extent is an ESTIMATE: the first pass lands on the estimate current when it was issued, and realising the items it passed then corrects it.

**Whether the user is scrolling is taken from the INPUT, never inferred from the offset** (issue #90). This is the rule the whole feature rests on. Reading intent off the scroll delta — an upward move is the user, *unless* the numbers say the scroller clamped itself, *unless* the extent grew at the same time — cannot work: each such rule is right about the case it was written for and wrong about the next, because the scroller reports the same delta whether the user dragged the thumb, the content shrank underneath them, the viewport changed size, or WPF brought a newly focused element into view. The wheel is instead read from the wheel event itself (`TranscriptScroll_PreviewMouseWheel`), and `IsUserScrolling` reads the thumb / track / selection-drag from "the button is down inside the transcript" (`_pointerDownInTranscript` + `Mouse.LeftButton`) and the keyboard from a one-shot armed by a nav key. Everything left over is **by construction** not the user.

**And what is not the user is corrected, not obeyed:** while following, `!IsAtBottom` ⇒ re-aim, unconditionally. That single line is what makes the plain statement of the bug hold — *"we haven't scrolled, so if we were at the end before we should be at the end afterwards"* — for a permission banner appearing, a tab switch, a card collapsing, a viewport resize, and any future source nobody has thought of. It replaced a guard that only re-aimed from an offset still within the slack of the old bottom, so **one** event moving the real bottom further than that left a gap nothing afterwards ever closed: the transcript stopped following for the rest of the turn while still *looking* parked at the end.

**Three consequences worth keeping straight.** (1) **A transcript with nothing to scroll is FOLLOWING, and that holds in both directions from one predicate** (`ChatView.CanScrollAway`, `ScrollableHeight > RepinThresholdPx`). Forwards: **a wheel with nothing to scroll is not a gesture** — over a transcript that fits its viewport it moves nothing, so no scroll change follows to put the state back, and the pill was offered forever on a chat already at its end (the first half of #90). Backwards: **the content can SHRINK back to fitting under a follow that was already cleared** (issue #195) — expand a row until the conversation overflows, scroll up to read it (the pill is correctly offered), then collapse the row again, and the scrollbar goes while the pill stays, offering a jump to a bottom already on screen. Nothing in a scroll change's *direction* can see that: a shrink arrives as an upward clamp or as no move at all, and the re-pin branch only ever fired on a downward one. So `ScrollChanged` asserts the invariant rather than reading the event — `!CanScrollAway` ⇒ following — **last** in the handler, so it wins over a branch above that is still describing a move of a few pixels. One predicate for both because they are the same rule read the two ways; spelled separately they could drift into disagreeing about the same pane, and the state that leaves behind is a pill that cannot be cleared by scrolling, there being nothing left to scroll. **Expanding a row is NOT itself what offers the pill**, measured (`pillAfterExpand=False`): an expansion is not a gesture — the pointer is up again by the time the toggle runs — so the follow survives it and the view re-aims to the new bottom. #195's first step necessarily included a scroll, which the report does not mention; do not go looking for a bug on the expand path. (2) **`ChatView.BringItemIntoView` stops the follow; WPF's own `BringIntoView` must not.** They are the same move with opposite meanings — ours is the plan strip's explicit "take me there", WPF's is raised for whatever element takes focus — so ours says so in code rather than leaving `ScrollChanged` to guess, and re-pins if it happened to land at the bottom (the newest item is a legitimate target). **And the flag saying so has to REACH the gesture, which for four months it did not** (issue #180): `RevealItem` is shared by the two callers the distinction exists to separate — `GoToItem` (the plan strip, a menu) and the park we perform on our own initiative — and it hard-coded `stopsFollowing: false` for both. So clicking the strip scrolled to the plan card, the `ScrollChanged` **that scroll itself raised** found the follow still set, and re-aimed straight back to the bottom; the strip looked inert unless the user had scrolled away first, which is the workaround that got reported. **The sibling gesture was immune for a reason that hid it**: `ShowPermissionTarget_Click` sets `_parkedOnPrompt` before it calls in and `ScrollTranscriptToEnd` early-returns on that, so it was shielded by the PARK rather than by the flag — two callers, one visibly broken and one correct by accident of a different mechanism. The fix passes the value THROUGH rather than deciding it at the shared method: the existing, already-correct distinction pushed one level down. **One deliberate consequence:** "Show in transcript" now leaves the view ON the row after the prompt is answered, where it used to ride back down as the park released — which is what its own doc already claimed (*they asked to see the row, so it should stay seen*), and the way back is the pill, the same way back every other cleared follow has. (3) **`RepinThresholdPx` (4px) is now the only threshold.** The old generous one (`max(24px, 15% of viewport)`) existed solely to absorb extent-estimate error in "did the follow survive?", a question the offset no longer answers. Do not re-merge them: a slack that generous swallows a 48px wheel notch, so the first notches of a *slow* scroll up read as still-at-the-bottom and any upward change arriving in that window re-pinned the follow, yanking the view back on the next delta — which is why that bug only ever reproduced when scrolling gently.

**The tight re-pin has a known cost, and the `JumpToLatestButton` is what pays it** — a THUMB drag stopping a few px short of the bottom does not re-pin (a wheel cannot land short; the notch clamps), so the standard jump-to-latest affordance (Slack/Discord/Teams/VS Code chat) is **load-bearing here, not decoration**: it is the one route back that doesn't depend on landing exactly on the bottom, which is what makes "I can't get following back" impossible by construction instead of by picking a forgiving threshold. Follow state therefore routes through the `FollowingTranscript` property (the field is only backing store) so it can drive the read-only `ShowJumpToLatest` DP the button binds to. It reuses `Chat.ChipButton` — no new theme keys, so the VSIX theme mapping is untouched — **overlays** the transcript's bottom edge rather than taking a row (so it never reflows the conversation as it comes and goes), and sits **outside `ZoomTransform`**: it acts on the view rather than being part of the conversation, so by the pane's own rule it is chrome and follows the environment font.

**A control that hides itself as a result of being clicked must LAND the keyboard focus it was holding** (`ChatView.ReclaimAbandonedFocus`). The pane has two — the jump-to-latest pill and the permission banner's options — and WPF re-homes the focus each was holding by dropping it on the **root visual of the presentation source**.

**That root is NOT always a `Window`, and assuming it was is why the first fix worked in the Desktop host and did nothing in VS.** A VS tool window's content lives in an `HwndSource` whose root visual is the content itself, so a test for `Keyboard.FocusedElement is null or Window` never fired and the fix was a silent no-op, against a build whose smoke was green. `IsFocusAbandoned` therefore asks the source what its root is (`PresentationSource.FromVisual(this)?.RootVisual`) instead of naming a type, which adapts to whatever a host roots its tree in. Reproduced offline by the smoke's **hosted-focus probe**, which builds that same shape — a `ChatView` as an `HwndSource`'s root visual, no `Window` in the tree — and measured the unfixed build landing focus on `ChatView` after the click. **The generalisable lesson: a UI check that only ever runs against one hosting shape cannot see a hosting-shape bug, and both of this repo's WPF hosts are Window-rooted while the shipping one is not.**

**The pill additionally hands its focus on BEFORE it hides**, in `JumpToLatest_Click`, and that half needs no theory about the host at all: the click focuses the button, the next line collapses it, so moving focus first means there is nothing to re-home. Pinned separately from the reclaim by sampling focus at the instant `ShowJumpToLatest` flips — `atWithdrawal=InputBox` with the hand-off, `atWithdrawal=JumpToLatestButton` without (corrected a dispatcher turn later by the reclaim, so the end state is identical and the hand-off would otherwise be invisible). The banner cannot use the same trick — it also leaves without a click, when the turn ends or a session switch clears it — so that path rests on recognising the abandoned focus, which is why the probe covers it too. Older measurement, still true in the Desktop host: both leave `Keyboard.FocusedElement` reading as the **window**. Focus on a window rather than a control makes the next arrow key *directional navigation* instead of a caret move, so it lands on whatever focusable thing is nearest — reported as Up moving the selection into the permission banner after clicking the pill, and the destination varies with what is on screen, which is what makes it read as random. Two properties, both pinned by injection: **conditional** (only `null`/`Window` is ours to place — focus on a real control is the user's, and a banner has more ways to leave than being answered, so an unconditional reclaim is a focus *steal*: it fails the check by dragging focus off the export button when a queued banner is cleared), and **deferred to `Input` priority** (the hide, WPF's fallback and the reclaim all hang off one property change, and the order between them is a subscription-order accident). Watched on `HasPendingPermission` rather than handled on the option button, so a banner cancelled by the turn ending or cleared by a session switch is covered by the same code. `Focus()` not `FocusInput()` — a half-typed message keeps its caret position.

**The Desktop `--smoke` follow checks drive gestures as GESTURES** — raising the tunnelling wheel event the view reads intent from and then the bubbling one the scroller acts on, or a `PreviewKeyDown` ahead of the `IScrollInfo` call. A bare `PageUp()`/`LineUp()` is no longer a scroll away from anything, so a harness that kept making them would be testing a path no user can take. Phases: streams / stops when scrolled up (keyboard + `BringItemIntoView`) / resumes / survives a shrink / stops on **one notch** / stops on a **slow** scroll with deltas interleaved / the button itself (offered, returns, **genuinely re-engages** — proven by the next delta being followed, not by one scroll landing at the bottom — and hides again) / survives a **permission banner** / survives a **tab switch** / a wheel with **nothing to scroll** / a row **expanded, read and collapsed again** (#195) / the **plan strip's click** (#180). The last four are #90's and #195's, and the last three run in a window of their own because they need transcripts the main probe cannot provide: one that fits, and one of 200 items where the panel is genuinely estimating (the probe's is a single very tall message, which realises whole). The collapse phase proves the follow with a **delta**, and that half had to be waited for and its growth asserted: a markdown message rebuilds on a throttle, so a settle or two after the append the transcript is still the size it was — and on a transcript that has not grown, “at the bottom” is `0 >= 0`, which the unfixed build satisfies as happily as the fixed one (it read `followedAfter=True @0/0` before the wait went in).

The plan-strip phase runs against the MAIN window because that is the only place a real plan exists (`ActivePlan` is set from a live event and there is no public way in), raises the routed `MouseLeftButtonUp` **on the strip's own named element** so the XAML handler attribute is part of what is pinned, and asserts three things — off the bottom, the pill offered, and **the plan card's own container inside the viewport**, since off-the-bottom alone is satisfied by a scroll to anywhere. Verified load-bearing 2026-09-03, by putting the strip's click back on the non-gesture value: `movedOffBottom=False @1146/1146 pillOffered=False showedThePlan=False (planTop=NaN)` — the reported snap-back exactly, with every other check green.

**Verified load-bearing by injecting each half of the fix** (2026-08-09), which is also the record of which scenario pins what: dropping the wheel's scrollability guard fails `emptyWheel` alone; unpinning on any upward move fails the banner, the viewport-collapse step and bring-into-view; re-aiming only on growth fails bring-into-view (`atBottom=False followedAfter=False @10977/11075` — 98px short, exactly the reported "not quite on the bottom"). **Of the three tab-switch mechanisms driven, only bring-into-view reproduced**: hiding and re-showing changes nothing because WPF skips layout for a collapsed subtree entirely, and the viewport collapse was survived by the old clamp arithmetic. So *which* mechanism VS's tab switch actually uses was not determined; the fix covers every mechanism in the class. Also measured while resolving whether "nothing to scroll" can be believed: on a jump to the top of a 2105px-scrollable transcript the estimate dipped to 1686, i.e. a fifth off, **nowhere near collapsing** (the earlier claim that it collapses to near zero was the reason the loose at-the-bottom test was rejected; it is not what this harness measures). Separately: "I scrolled up a bit and it didn't scroll" was **not** a follow bug at all — it was the detached-render density change above, and the view had scrolled all along.

## The resizable message box

**The message box is resizable** by a `Thumb` grip on its top edge (`Chat.InputResizeGrip` — invisible at rest, a short bar on hover; the template's outer `Background` must be a real brush or it won't hit-test), which Copilot Chat has no equivalent of. The dragged height is a **floor, not a fixed size** (`ChatView.ComputeInputBounds` → `MinHeight`, with the ceiling raised to match): auto-grow-with-content survives, so an undragged box still rests at one line and grows to ~6, exactly as the old hardcoded `MinHeight=28`/`MaxHeight=120` did. Ceiling-only (raise the max, let a short message shrink it back) was the first design and is **wrong** — dragging an *empty* box would do nothing visible, so the gesture reads as broken. Both bounds cap at **60% of the pane** (the input's row is `Auto`, so an uncapped height squeezes the transcript away); the cap never touches the stored preference, so re-docking taller gives the size back, and it never squeezes below one line. Persisted as `ExtensionConfig.ChatInputHeight` (0 = never dragged), saved on `DragCompleted` — a drag has a definite end, so unlike the zoom wheel it needs no debounce timer; double-click resets (the Ctrl+0 analogue).

**Do NOT compute the drag from `DragDeltaEventArgs.VerticalChange`**: the grip moves as the box grows and `Thumb` reports relative to itself, so growth feeds back into the next measurement — the handler measures `Mouse.GetPosition(this)` against the drag origin instead, `this` (ChatView) being outside the zoom transform and therefore still, and divides by the zoom scale since the box's bounds live under it. Pinned by `ChatInputSizingTests` (the arithmetic, no WPF tree) + a Desktop `--smoke` wiring check (grip present, hit-testable, undragged bounds intact); the pointer gesture itself can only be driven in a live Visual Studio instance.

## Code highlighting, colour emoji and inline images

**We use only the Markdig core parser (maintained) and own the WPF rendering — Markdig.Wpf was rejected (unmaintained since 2021, pins an ancient Markdig).** **Fenced-code syntax highlighting** goes through the `Markdown/CodeHighlighter` seam (same shape as the markdown and emoji seams: consume a tokenizer, own the WPF rendering). Engine = **ColorCode.Core 2.0.15** — pure-managed, netstandard, ZERO package deps, so the in-proc binding-identity invariant holds. Its tokens carry a **language-independent `ScopeName`** vocabulary, which is why one ~30-case switch maps everything onto the `Chat.Code.*` DynamicResource keys the VSIX repoints at VS's live editor colours. Unknown fence tag or any tokenizer throw → a single plain `Run` (it re-tokenizes over code it cannot assume is complete, so it must never fault). **That `catch` was the stated mitigation for incomplete code and it is not sufficient — see "A tokenize that never returns" below, which is what a hang does to it.**

**AvalonEdit was evaluated as a replacement and rejected (2026-07-25)** — the ask was broader language coverage, and it doesn't deliver it: its 21 bundled `.xshd` files have **no YAML and no bash** (the two we actually lacked), and swapping would *lose* F#/Haskell/TypeScript/Fortran/MATLAB while gaining only Patch/TeX/Boo/Coco. Worse, `.xshd` bakes literal colours in and its `HighlightingColor.Name`s are **per-language and ad hoc** (`MethodCall`, `NumberLiteral`, `TrueFalse`…), so live VS theming would need a per-language mapping table or a fork of all 21 files — the shared-`ScopeName` property above is the whole reason our theming is one switch. (Not blockers, for the record: AvalonEdit 6.3.1 is MIT with zero package deps and a `net462` asset, so the identity invariant would have held; the cost is 883KB of full editor control for a tokenizer, plus heavier per-delta allocation from `TextDocument`+`DocumentHighlighter`.) **Measured before deciding: across all 85 saved sessions the agent emitted `csharp` ×42 and one each of `xml`/`sql`/`powershell`/`json` — and ZERO yaml/bash/diff**, i.e. the shipped set already covers real traffic (sample is .NET-only, but so is this product). If coverage ever does become the goal, extend in place — `ColorCode.Languages.Load(ILanguage)` is public and an `ILanguage` is just regexes → `ScopeName`s, so YAML/ini/toml are ~40 lines each with theming untouched; a real engine swap only makes sense for TextMate-grammar breadth (`TextMateSharp`), never AvalonEdit. Do NOT host AvalonEdit's `TextEditor` control per code block either — an `InlineUIContainer` breaks `ChatClipboard` selection-copy, cross-block selection, and Ctrl+wheel zoom.

**Colour emoji** (WPF's text stack renders COLR fonts monochrome on every TFM): all transcript text funnels through the **`Markdown/EmojiText` seam** — splits literals into Runs + colour-emoji inlines, engine = pinned **Emoji.Wpf 0.3.4** (self-contained closure like Markdig, in the .vsix root; every `EmojiInline` must pin `Foreground=Black` or its tint shader washes the glyph) — swap-in-one-file if WPF ever ships native support. `EmojiText.PlainText` attached prop does TextBlocks (tool/notice/crew/plan titles).

**Inline images: the two bounds the containment fix did not have** (pre-release security review, September 2026). The containment made "local" mean that a file renders only when `WorkspacePath.IsUnderRoot` puts its full path inside the workspace root. Two things that test did not do. **It was lexical**: `Path.GetFullPath` never touches the filesystem, so a junction or symlink INSIDE the root naming a UNC share passed the spelling test, and `FileInfo.Exists` then followed it to the host — the NTLM handshake and SMB stall on the dispatcher the containment excludes. `Core.Ide.ReparsePoints.AnyBelow` walks every component below the root (the root's own ancestors are not its business — a developer's `C:\src` → `D:\src` junction carries the whole project) and asks each whether it is a link, by `FindFirstFileW`'s reparse TAG rather than the attribute — a OneDrive placeholder carries the attribute and is not a link, and refusing those would refuse every image in a synced repository on the net472 slice devenv loads. One P/Invoke, one answer on both TFMs; refused, never followed. `FileReferenceResolver` takes the same rule on its direct hit and skips linked directories in its index. **And every size cap was on ENCODED bytes**: PNG's compression of uniform pixels makes a sub-megabyte file that decodes to gigabytes, synchronously, on the dispatcher, and the 960 cap only ever bounded WIDTH, so a tall narrow bomb was not scaled at all. `ImageDecodeBudget.TryProbe` reads the frame's natural size from the header alone (`DelayCreation`, no cache — nothing is decoded) and refuses over 16 megapixels, a ceiling a 4K screenshot passes under; the clipboard/file attachment path takes the same probe, being bytes the user did not author either; and the edge cap scales whichever edge overshoots more. Pinned by `ImageDecodeBudgetTests`, whose bombs are HAND-ENCODED — rendering one would allocate the thing the guard refuses — and whose junction tests FAIL, naming the reason, where `mklink` is unavailable — an early return would report the same green as a run that actually measured the guard. Two injections LOAD-BEARING: a decode probe that always answers "within budget", and a containment walk that finds no link.

**Selection-copy goes through `Markdown/ChatClipboard`** (stock `TextRange.Text` silently drops `InlineUIContainer` content — emoji/checkboxes/images carry a `CopyText` stand-in). Markdown also renders **task lists** (themed `Chat.TaskCheckBox`) and **inline images with a no-fetch policy** (`Markdown/MarkdownImages`: data:-URIs + files UNDER THE WORKSPACE ROOT only; http(s) images stay alt-text hyperlinks — auto-fetch is an exfiltration channel). **"Local" is a containment, not a scheme** (pre-release security review, September 2026): the scheme guard let every `file:` URI and every rooted path through, and a UNC path is rooted, so `![](\\attacker.example\s\p.png)` in an assistant message was handed to `FileInfo.Exists` on the dispatcher with no click — Windows authenticates to the remote host, which leaks credentials and blocks the UI thread on the SMB timeout. Enumerating remote spellings (`\\?\UNC\`, device paths, percent-encoding, a `file://host/` that `LocalPath` turns back into `\\host\`) is the wrong shape, so the rule is positive: `TryResolveLocalPath` is pure and answers a full path only where `WorkspacePath.IsUnderRoot` places it inside the root the renderer's `FileLinkContext` already contains every file reference by — a refused source is never stat'd — and a renderer with no link context renders no file (data: images still render). Pasted attachments do not go through this path (`AttachmentViewModel` draws from bytes), so nothing shipped loses an image. **The decode stays synchronous and on the dispatcher, deliberately:** the off-thread rework the finding also asked for was twice measured by the review's own verifier to cost more than it bought (a decode-to-render loop that never settled, then permanent eviction of images already on screen), and what made the synchronous read hang was a remote volume, which containment excludes — a workspace that is itself on a share is a volume VS is already on. Pinned by `MarkdownImageSourceTests`: every spelling above against a pure resolver, and two REAL files on either side of a root so the outside null is containment and not absence.

## Clickable file references

**File references in the agent's prose are clickable** (`Markdown/FileReferences` + `FileReferenceResolver` + `FileLinkContext`): `Foo.cs:42` opens that file at that line via the host's `OpenFileAtLineAsync`. We **recognise the convention rather than inventing syntax** — measured across all 108 saved sessions, both backends emit `path:line` unprompted, so no `<ide-tools>` steering is spent on it, and a custom `[Foo.cs](cwkt://…)` scheme would have failed on all existing traffic.

**A line can carry a range or a column** (`IdeMcpServer.cs:9-11`, `ATestClass.cs:8:52` — both in that corpus, an en dash and GitHub's `#L9` in neither): neither changes where we open, but both must be swallowed into the match's **span**, or the link stops at the line and the tail renders as plain text beside it — one reference reading as a link with debris after it.

**85% of real references sit inside an inline code span**, so `CodeInline` is the *primary* render path (the link keeps the code styling); literal prose runs are the other 15% and chain refs-then-emoji through one owner (`AppendText`); **fenced blocks are deliberately not linkified** (ColorCode owns those runs).

**Resolution is the gate, not the pattern** — there is no extension whitelist (`System.Text.Json` matches the token shape and simply fails to resolve, same outcome for free), and a reference that doesn't resolve stays plain text because *a dead link is worse than no link*: rooted-as-written → combine-with-root → **unique filename from a lazily-built disk index** (63% of real refs are a bare filename), ambiguous or absent → nothing.

**Never links outside `AgentPathRoot`**, so a prompt-injected `C:\Users\you\.aws\credentials:1` can't become a one-click open; skipping `bin`/`obj`/`.git`/`node_modules` in the index is load-bearing rather than tidy, since a build-output copy is exactly what would make the unique-match rule fail. A ref carrying directories is matched by **path suffix** and does NOT fall back to the bare leaf (the agent said *which* `Foo.cs`).

**No IO ever happens on the render path** — the `FlowDocument` rebuilds on every streamed delta, so an inline `File.Exists` (plus, on a first miss, a filename search) would land on the UI thread ~200 times per message; the renderer only reads *already-cached* answers and an unknown ref renders as plain text, resolves off-thread, and upgrades (free mid-stream, one extra rebuild after the last delta). The matcher is **hand-written, not a regex**: one forward pass is linear by construction where a quantified path-class ahead of the extension backtracks quadratically, and `RegexOptions.NonBacktracking` is .NET 7+ (absent on the net472 slice). Negatives are cached too and invalidated per-leaf by `NoteFileWritten` on `EditProposed` — a file the agent is about to *create* doesn't exist when first mentioned. Context reaches the renderer as the `md:MarkdownText.Links` attached property (the root is per-session and `--smoke` builds two view-models, so a static hook was wrong); a host with no opener gets none and everything stays plain text.

**Restored transcripts linkify too** — resolution is disk state, not session state, and the root comes from `PersistedSession.AgentWorkingDirectory`, so this works retroactively on conversations recorded before it existed.

**The boundary to hold as this seam grows** (any further kind of link — a symbol, a diagnostic code, a test name — is held to it): **navigate is free, act must go through permissions** — a click that opens a file is a read the user asked for; a click that *runs* something suggested in model-authored prose can never be a bare prose link.

## A tokenize that never returns (issue #177)

A user's IDE froze outright while an agent was replying. The dump was unambiguous: `DispatcherTimer.FireTick → MarkdownText.Render → … → CodeHighlighter.SegmentCollector.Tokenize → ColorCode.Parsing.LanguageParser.Parse → Regex.Run`, stuck in `RegexInterpreter.Go`, on the UI thread. The input was a **570-character JSON block, truncated mid-token** — the agent's message had been cut off inside a deeply escaped string.

**The cause is in ColorCode's JSON grammar, and it is the textbook shape.** Its string pattern is `"[^"\\]*(?:\\[^\r\n]|[^"\\]*)*"` — `[^"\\]*` appears **both before and inside** the repeating group, so a run of ordinary characters can be split between the two in exponentially many ways. While the closing quote exists one split matches and the engine stops; with the quote missing **none** match and it must try them all. Truncated input is therefore not a slow case, it is an unbounded one. Reproduced offline: the reported payload ran past 20 s with no sign of stopping, in a killable child process, twice.

**The guard that should have covered this could not, and that is the lesson.** `CodeHighlighter.TryTokenize`'s `catch` was written for exactly this input class — its comment said so, naming incomplete mid-stream code — and `MarkdownRenderFirewall` sits above it for the same reason. **A hang raises nothing.** Both are `catch`es; neither can see a call that does not return. "It didn't throw" was true throughout the bug's life, which is why every check added here is a **timing** assertion.

### The fix: a deadline, supplied by substituting ColorCode's compiler

ColorCode builds each language's master pattern with `new Regex(pattern)` — no options argument, **no match timeout** — and caches it in a static dictionary. That constructor is not ours, but everything needed to go around it is public: `ILanguageCompiler`, `LanguageCompiler(Dictionary, ReaderWriterLockSlim)`, `CompiledLanguage` with a **public `Regex` setter**, `LanguageParser(ILanguageCompiler, ILanguageRepository)`, and `CodeColorizerBase(_, ILanguageParser)` — whose parser argument had been passed as `null!`.

So `CodeHighlighter.TimeoutLanguageCompiler` delegates to a real `LanguageCompiler` over **our own** dictionary and lock, then replaces the compiled `Regex` with `new Regex(r.ToString(), r.Options, TokenizeTimeout)`. Verified faithful: `ToString()` round-trips the pattern byte-identically and `Options` comes back `None` because ColorCode's flags are **inline in the pattern** (`(?x)`, `(?-xis)(?m)`) — which is also why its constructor never needed an options argument. Well-formed JSON tokenizes to the identical match sequence with and without the deadline.

**Because the dictionary is ours, ColorCode's static cache is never mutated.** This is an IDE: other extensions can share the assembly, and a process-wide change to how a shared tokenizer matches is not ours to make. For the same reason `AppDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", …)` is **not** the fix — it is read by `Regex`'s static constructor, so a package loading long after devenv started has no effect, and if it did take it would change matching for the whole of Visual Studio.

Supplying our own parser means supplying the `ILanguageRepository` it uses to resolve an **embedded** language (the JavaScript in an HTML `<script>`). `GlobalLanguageRepository` delegates straight back to `ColorCode.Languages`, which is what the default parser uses — get this wrong and embedded highlighting degrades silently, with everything else green.

### Off-thread is not an alternative, and this is worth not re-deriving

The tokenize *is* movable — `TryTokenize` returns plain `(string, string?)` structs with no WPF in them, and only the loop after it builds `Run`s. **It still does not fix this.** A backtracking `Regex` cannot be cancelled or interrupted, so on a worker thread it burns a core indefinitely and the frozen UI has merely become a leaked thread; and since the render re-tokenizes per tick, a permanently truncated block would leak **one spinning thread per tick**. `Thread.Abort` is not something to do inside devenv. That also disposes of `Task.Run(tokenize).Wait(50)`, which looks like a timeout without touching ColorCode: it is a bounded UI-thread block that abandons a thread which keeps running. **A match timeout is the only option that terminates the work rather than walking away from it.** With one in place, off-threading is a net loss — two-phase rendering pays a whole extra `attach`, which is 95–98% of a render (see `[md-cost]`), to move ~1 ms off the UI thread, and during streaming nearly every background result is stale on arrival.

A **size cap** would not have caught this either: the input was 570 characters. The blowup is exponential in the length of the unterminated run, not in block size, so a cap low enough to help would kill highlighting on ordinary blocks.

### The amplifier: it was one bad parse per tick, not one

`MarkdownText` rebuilds on a `DispatcherTimer` throttled to 100 ms and re-parses the **whole** message each time — measured at 202 rebuilds on a 30 KB reply — and `RenderCodeBlock` re-tokenized **every** fenced block in it, including ones final several seconds earlier. A timeout alone therefore converts an infinite hang into a *sustained* tax: 50 ms against a 100 ms interval is half the UI thread, indefinitely. Two things close that.

**A fence with no closing marker is not highlighted at all.** While a reply streams, the arriving block is unterminated on every single rebuild — precisely the pathological input, re-fed on every tick, and it can never be cached because the text differs each time. Waiting for the fence to close removes the whole streaming half; what remains is content that is *permanently* truncated, which then simply never highlights instead of spending the budget forever. Markdig answers this with `FencedCodeBlock.ClosingFencedCharCount` (pinned by a test — **`Block.IsOpen` is not it**, everything is closed by the time `Parse` returns).

**Tokenize results are cached** by language id + exact code text, **failures included** — so a block that can never tokenize pays the deadline once rather than per rebuild, and the settled blocks earlier in a streaming reply are free after their first pass. The cache only ever sees settled text, because unclosed fences never reach it, so it does not churn.

**Neither of those bounds a RENDER** (pre-release security review, September 2026). A message with N distinct blocks that cannot tokenize paid N deadlines on its first render — and, because the cache was ONE dictionary cleared at capacity, a message with more than 64 of them (or a pane with 64 ordinary blocks beside one bad one) emptied the cache under the failures and paid them all again on every rebuild: 65 × 50 ms per tick for as long as the message was on screen, the exact tax the cache exists to remove. Two changes. **Failures live apart from successes** (`CodeHighlighter.Failures`, a set of keys with its own, much larger cap) so ordinary blocks never evict them. **And one document render has a tokenize allowance** (`CodeHighlighter.DocumentTokenizeBudget`, 100 ms, a `HighlightBudget` the flow renderer creates per `Render` and threads to every `AppendTo`): each uncached tokenize is charged against it, and once it is spent the remaining blocks render plain and are *not* cached — they were never tried — so a later render with allowance tries them. A render is therefore at most the budget plus one deadline's overshoot, and a document converges to free in about N/2 renders. Cache hits cost nothing against the budget, so a long reply full of ordinary blocks is unaffected. Pinned by `CodeHighlighterTimeoutTests` — a cached failure surviving eighty successes, and sixteen pathological blocks rendering under the ceiling and converging — with `run-gates.ps1` as the verifier of record, timing being the subject.

**Those two are what closed issue #86's streaming symptom, and the fence rule is #86's OWN candidate fix 3** — "skip highlighting an unclosed fence — cheap, worth doing either way" — shipped here for a different reason and under a different number. What "cheap" got wrong is the shape of the cost. An open fence is not slightly wasteful to tokenize; it is the pathological input, and the run grows as the block streams, so the early ticks are cheap and the late ones are not. That is the reported "the last message is sluggish and then arrives all at once" seen from the other end: the tick falls behind its own 100 ms interval, the `Background`-priority throttle timer starves below `Render` so nothing repaints, and the whole message paints the moment the closing fence lands and the next tokenize is over well-formed input. **`[md-cost]`'s `parse` figure is where this shows up**, and it does so before any hang is visible — see the limitation note under `[md-cost]` below.

### Verification, and the one check that pinned nothing

`CodeHighlighterTimeoutTests` carries the reported payload **byte-identical** (627 chars, verified against the issue attachment programmatically rather than transcribed) and runs every body through `StaTest.RunWithin` — a bounded variant added for this, because with the fix removed these do not fail, they **never return**, and a suite that hangs tells `prove-check.ps1` nothing. The overrunning thread is abandoned, not stopped; there is nothing to stop it with, which is the defect restated.

The `--smoke` check exists **beside** the unit tests rather than instead of them: the unit suite is net10-only and devenv loads the **net472** slice, where the reported stack sat in .NET Framework's own `RegexInterpreter.Go`. Broader coverage of the shipping framework, not different coverage.

**The fence rule's first check reported `PINS NOTHING`, and why is the useful part.** It asserted that an unclosed truncated-JSON fence renders as one plain `Run` — which is true whether the fence was *skipped* or *tokenized and then timed out*, since both end at the same fallback. The assertion could not see the difference it was written for. It is now asserted on **well-formed** JSON, where the only way to get several `Run`s is for the tokenizer to have run, and in both directions so it cannot pass by never highlighting anything.

## The tool row's intent subtitle (issue #131)

The muted line under a tool row's title is the agent's stated "why", extracted in the UI by
`ChatViewModel.ExtractToolIntent` from the call's `rawInput`. Two keys can supply it, and the whole of
this issue is that they are **not equally trustworthy**:

- **`__tool_use_purpose` is injected by Kiro**, and named — double-underscored — precisely to avoid
  colliding with a tool's own arguments. It means the same thing on every call, ours or a third
  party's, so it stays trusted everywhere. That matters: MCP calls are most of what Kiro does, and a
  fix that silenced their subtitles would be a regression in the other direction.
- **`description` is injected by nobody.** It is an ordinary parameter that Claude Code's built-in
  tools happen to define with that meaning, and that any MCP server is equally free to define with
  another. Reported live on Kiro v3: a Jira tool takes `summary` and `description`, and the ticket's
  description — hundreds of words — became the row's subtitle.

So the host was reading meaning into a key name from **someone else's schema**, and it could not tell
whose schema it was: nothing on the tool-call events said whether the call was an MCP tool or a
backend built-in. The information already existed — `AcpMapper.ExtractToolName` derives it for
permission requests — it just wasn't published. `ToolCallStarted`/`ToolCallUpdated` (and the DTO) now
carry `ToolName`, and a non-null value is the marker: *rawInput follows a third party's schema*.

**Verified against real captures before relying on it**: an MCP tool call's `tool_call` frame carries
the namespaced name as its title in all three shapes seen live — `@server/tool`,
`Running: @server/tool` (Kiro's default engine, the same prefix a shell command wears, #129) and
`mcp__server__tool` — while backend built-ins are titled with prose (`Read File`). So the extraction
resolves exactly where it must and returns null for built-ins. The same guard reaches the permission
banner's edit subtitle for free, since `PermissionRequestDto.ToolName` was already there.

The subtitle is additionally **collapsed to its first line and capped** (`ClampIntent`, 200 chars).
That is a backstop, not the fix: it bounds a *genuine* intent that happens to be wordy, where the rule
above stops a foreign field being read as intent at all. Nothing is lost either way — the raw value
still renders in the expandable detail, itself capped by `FormatToolInput`.

The two key lists now agree on order (`__tool_use_purpose` first) with the mapper's `EditIntentKeys`,
which decodes the same concept for edit cards. That path is far lower risk — its `rawInput` is a
file-edit shape by construction rather than a foreign tool's arguments — but the two must not disagree
about a call carrying both.

## An MCP call is SHOWN by one name on every backend

The backends name one call three ways — Claude `mcp__code-wicket__build_solution`, Kiro v2
`Running: @code-wicket/build_solution` (the prefix a shell command wears, #129), Kiro v3
`@code_wicket/build_solution` — and the settings list a fourth, `code-wicket/build_solution`. The row and
the banner showed the backend's title verbatim, so a row could not be matched to the rule that approved it
by reading either. Now both show the canonical **`server/tool`** from `IdeMcpServer.TryDisplayName`,
which is ALSO what `TryNormalizeToolRule` stores — one definition, so the name on screen is the text a
user saves. The backend's own spelling is kept as the first line of the detail (`tool: …`, on the row's
`Detail` and the banner's `DisplayDetail`), since it is what the backend's log and the agent call the tool;
a call with no arguments now expands to it.

**The name comes from `ToolName`, never from the title, and never for a call with a SHELL FACT.** The
EVENT path lifts a name out of the title with no shell check (`AcpMapper.Map` calls `ExtractToolName`
with its default; only `ToPermissionRequest` passes `fromTitle: !isShellCommand`), so a shell command
titled `Running: @code-wicket/get_diagnostics` arrives carrying our tool's name. Renamed, its row would
read as our tool — the tool-name impersonation, moved from the tier to the screen. So the view-model's own test
(`IsShellFact`) is a command kind or a `command` argument, and it is **sticky**: a fact on any frame puts
back `BackendTitle` and keeps it, whatever a later frame names. The mapper was deliberately not changed —
on the event path `rawInput.command` is not a shell fact the way it is on a permission frame (an MCP tool
may define the argument), and the lifted name also decides `ResolveToolKind` and #131's schema rule, so
moving it moves the terminal mirror and the subtitle. The price here is that **`run_command`, whose
argument is literally `command`, keeps its raw title**. The banner needs no test of its own: a command
request is `IsCommand` and only a tool-scoped request is renamed.

An update may CONFER the name (Claude opens with a placeholder title and names the tool on the update),
and an update that names nothing leaves a named row alone. Replay stores the whole DTO, `ToolName`
included, so a restored conversation shows the same names. Pinned by `McpToolDisplayNameTests`.

**The row also SAYS it is an MCP call, on the same condition.** The icon was the fallback gear, shared
with every unclassified call, beside an `(other)` tag — ACP's catch-all, which says nothing. An MCP row
now draws the `@` (MDL2 `E910`, "Accounts": the agent addressing a server) and an `(mcp)` tag
(`KindLabel`, also used by the export). Both key on `IsMcpCall`, which is `RawToolName` being set, so a
command wearing one of our tools' names gets none of the name, the icon or the tag. The glyph was chosen
from a shortlist rendered at 13px, in every status colour, in both themes: `E910` was the clearest.
`E950` and `E964` were refused for resembling the Kiro IDE's own MCP icon, `E835` was distinct but faint
in the light theme, and **`ED5D` (two joined nodes) was the runner-up** — the one-line swap if `@` is
later wanted for addressing another agent or Code Wicket itself. `ToolGlyphTests` holds it real and
distinct from every other row glyph.

## A read row names the file it read (issue #102)

**The row's title comes from the backend, and backends disagree about whether it names anything.** Three captures, all live, all with the file path sitting in the call's own `rawInput` and its ACP `locations`:

| backend | `tool_call` title | on the completion frame |
|---|---|---|
| Kiro, default engine (v2) | `Reading readme-target.txt:1-5` | names it |
| Kiro `--agent-engine v3` | `Read File` | `Read File` — **never** names it |
| claude-agent-acp | `Read File` (placeholder, `rawInput:{}`) | enriched to `Read src\Foo.cs (1440 - 1479)` |

So the row shows the target itself — `ToolItemViewModel.TargetDisplayPath`, rendered beside the title exactly as the edit card renders `DisplayPath` (workspace-relative, code font, full path in the tooltip), which is what makes a read row read like an edit row. **The label is defined as what the title doesn't already say**, not as "a label for Kiro": v2 and Claude both name the file themselves, and saying it twice on every read row would be worse than the bug. That check is against the leaf name, so it holds whether the backend wrote an absolute or a relative path.

**Two consequences of it being derived from the title.** (1) A title change has to re-decide it — v3 re-sends its generic title on the completion frame, and an enrichment can arrive carrying a title but no arguments, which is the one frame that wouldn't otherwise re-attach the targets. Hence the `Title` setter republishes it; nothing else would notice. (2) It costs nothing when there's no path: the star column collapses to zero width, so a row without targets is laid out exactly as before.

**Nothing here decodes a new backend shape** — the targets are the ones `TryAttachReadTarget` already extracts for click-to-open, so this is composition over data the row was holding all along, which is why it stays in the UI rather than moving to `AcpMapper`. Copy and the markdown export carry the path too: "Read File" naming nothing is the same defect one step further from anyone who can see the screen.

**Proven at three levels**, each verified load-bearing by injecting the bug it guards: `ToolRowFileTargetTests` (the frames verbatim from the captures above, replayed through `ChatViewModel.Apply`), the Desktop `--smoke` check on the fake's v3-shaped read (`r3`), and the Console `kiro-read` proof against a real kiro-cli. The proof deliberately asserts **the backend fact the row rests on** — that a read call carries its file in its arguments — rather than the host behaviour, and reports the title's own count beside it (`0` on v3) because that number is what made this a bug. It parses the three argument shapes rather than searching the payload, since an intent field quoting the filename would otherwise pass a call whose row can name nothing.

## An edit's two shapes must SAY the same things, not merely offer the same actions (issues #178, #189)

Reported a fortnight apart and fixed together, because they are one mistake at two layers: **a reader that already knew the answer and a surface that never consulted it.** A folded edit row would copy and open a path it declined to print; an edit card would settle red with the reason for the failure sitting in the very event that settled it.

### Which shape a Kiro v3 write becomes, and what decides it

**Read the frame, not the code:** the code does not say which shape a backend produces. Settled by a live capture (2026-09-03).

**v3's `Write File` opening frame carries no `content` array at all.** The path and text are in `rawInput`, and the diff is in `_meta.kiro.preview` — `file` plus `modifiedContent`, with `originalContent` only where the file already existed. So the fork is decided entirely by whether that preview is usable:

| the opening frame's preview | shape | why |
|---|---|---|
| `file` + `modifiedContent` | an edit **card**, which names its own file | `ExtractKiroFragmentEdit` yields the edit up front |
| a `file` and nothing else (an EMPTY write), or no preview at all | a generic tool **row** wearing v3's bare `Write File`, the diff folding in on the later update | nothing to yield, so the call opens as an ordinary one |

The mapper's own doc comment used to claim v3's whole-file write "keeps a populated content diff and so never reaches here" — wrong in both directions, and corrected in place. **Read the frame before concluding which shape a backend produces.** The tell in a recorded transcript is that a v3 write logs **no `toolStart` at all**; the live discriminator, no logs needed, is that **the card's context menu has two items and the folded row's has three**.

The row is the shape #178 was reported from — confirmed from the report, which showed a tool row — which is why the label fix lands on the tool row rather than in the mapper. The fork is pinned as a **pair** of mapper tests taken from the live frames, since either one alone is satisfied by a mapper that always takes that branch.

### The label: two routes to a path, and the row read only one

`ToolItemViewModel` reaches a file two ways. Read targets arrive through `AttachFileTargets` (`_fileTargets`); a folded EDIT arrives through `TryAttachDiff`, which sets `_diffPath` alone. `TargetDisplayPath` consulted `_fileTargets` — which only a read ever fills — while `PathToCopy` and `CanOpenFile` both consulted `_diffPath`. Hence the symptom that made this read as cosmetic: **the right-click menu worked perfectly and the label was blank.**

Three things had to be true, and the first alone renders nothing:

- **The getter falls back to `_diffPath`.** The read route still WINS where both exist, being the richer one (a batched read names several files); the diff is the FALLBACK, and it covers Claude's folded edits at the same time. One diff per row, so the edit route never reaches the `+N more` count.
- **`TryAttachDiff` must RAISE `TargetDisplayPath`.** It is the very frame that supplies the row its path, so without a raise there the row goes on drawing the answer it computed before it had one.
- **`TryAttachDiff` must be GIVEN the workspace root.** `_fileTargetRoot` is what `WorkspacePath.Relative` measures against, so without it the edit route shows an absolute path where the read rows show a relative one — the same row, the same job, two spellings. Assigned with `??=`: a row only ever takes one of the two routes, and the host hands both the same `AgentPathRoot`, so which one set it never matters.

`FileTargetToolTip` takes the same fallback for the same reason one step along: the label is relative, so the FULL path has to be reachable on both routes or the edit route quietly loses the half the tooltip exists for.

**Deliberately narrower than teaching `TryAttachReadTarget` about write kinds**, which would flatten the two routes' deliberately different click semantics — a read row opens files, an edit row opens the diff — into one.

### The card had no expander, so a failed write said nothing (#189)

`toolDone`'s card branch set `Status` and `Permission` and dropped `ev.ErrorText`/`ev.Message`: the reason crossed the wire, reached `Apply`, and died one step from the screen. Beside it, the permission sentence was a **tooltip only** — which is to say undiscoverable — so the rule that every settled row carries a sentence saying why it ran was met by the tool row and quietly not by the card. **On Kiro v3 the card IS a write's ordinary shape**, so the calls that change the user's files were exactly the ones that could not say what happened.

`EditItemViewModel` now carries the tool row's own fields (`OutputDetail`, `ErrorDetail`, `Detail`, `IsExpanded` — collapsed by default, like the row's), and **`CanExpand` counts `HasPermissionSummary`**, so a settled call whose only content is the sentence still gets a chevron.

**The base needs a hook.** `Permission` and `PermissionSettled` are set by different frames and land in either order, and whichever lands second is the one that completes the sentence — so `OnPermissionDisplayChanged` is raised from **both** setters on the base and overridden by the card to re-raise `CanExpand`. Hanging it off one setter is correct until the day the other arrives last.

### Verification

`FoldedEditRowTests` covers the label, the tooltip and the root; `AcpMapperEventTests`/`AcpMapperOutputTests` cover the write fork and the v3 result nestings ([backend-behaviours.md](backend-behaviours.md)). Four injections proven load-bearing: the row's label reading the read-target list alone; the card branch setting the colour and discarding the error text; the detail chevron removed from the card template; and the Kiro result readers bound to the default engine's wrapped nesting only.

**The chevron is asserted by `--smoke`, because it is a XAML attribute whose removal leaves a CLEAN BUILD and every view-model check green** — the #62 shape: a behaviour that lives in XAML alone, where neither the compiler nor a view-model test can see it go. It is read off the DataTemplate's own `LoadContent()` and never off a realised card: the transcript virtualises, so whether this particular card has a container depends on where the pane happens to be scrolled, and **a check that passes by finding nothing is the exact failure being guarded against.** What it asserts is a `ToggleButton` bound to `IsExpanded` existing in the template's tree.

**One existing test was found pinning nothing, and the shape is the reusable part**: it searched the whole of `CopyText` for the path, which the row's `rawInput` detail carries anyway — so it passed with the label bug fully present. It now asserts the **head line**. Caught by `prove-check.ps1`, not by reading it.

## Sub-agent nesting, and the row that completed before it started (issue #125)

Measured on a live capture — **claude-agent-acp 0.70.0**, one turn, 217 frames, three background
sub-agents — plus a second run of two synchronous ones, and the v3 frames already pinned in
`SubagentTests`.

### What the wire actually says

| `_meta.claudeCode` shape | where | meaning |
|---|---|---|
| `{toolName:"Agent", subagent:true}` | the `Task` call and its enrichments | **this call starts a sub-agent** |
| `{toolName, parentToolUseId}` | a child's opening `tool_call` **and** its `status:"completed"` update | **this call was made BY that sub-agent** |
| `{toolName, toolResponse:{isAsync, status:"async_launched", agentId, outputFile, …}}` | its own **status-less** frame, immediately before the completion | **it was launched, not finished** |
| `{toolName, title}` | some `tool_call`s | Claude's human label for a call whose title is the raw command |
| `_meta._claude/origin:{kind:"task-notification"}` | on a `usage_update` | *a* sub-agent returned — which one is not said |

**A STOP is where that last row stops arriving, and the loss is total** (measured 2026-09-08,
`acp.log`). Six background `Task`s across two fan-outs with one `session/cancel` between them produced
exactly **three** `task-notification` frames — lines 122/132/138, all after the cancel at line 53, so
all three belonged to the fan-out launched *after* it. **The three launched before the cancel reported
nothing, ever.**

Three consequences, and the third is the one that is not about us at all:

- **Claude does not stop a background `Task` on cancel.** The user watched one run well past its own
  duration after pressing Stop. What the cancel ends is the *reporting*, not the work.
- **The completion is lost to the AGENT too, not just to the host.** Asked afterwards whether those
  tasks had finished, the model was still waiting for them. So a Stop with `Task`s in flight leaves
  the conversation itself holding a promise nothing will keep — this is not a display defect.
- **The count can therefore never reach zero**, and since the count is what settles rows, every later
  fan-out in that conversation is stranded with them. The reported screenshot showed six rows saying
  "running", which is 6 launches − 3 returns.

**This is the missing-notification failure the count's own design calls unguardable for want of
identity — and it is guardable in exactly this case**, because a cancel is a fact the host holds on its
own side and the stranded ids are its own turn's. So `SettleOpenToolRows` settles a launched row **on a
cancel only**, never on a natural turn end (a task legitimately returns after one of those, which is
why the count is not turn-scoped in the first place). The row is marked `unreported` rather than
`finished`: no result was received, and the work may still be running, so neither word may be used.

Three things fall out of that table and each one shaped the fix.

**Parentage is on both ends of a child's life**, so a row can be nested when it opens and settled when
it finishes without the mapper keeping any state of its own. (The intermediate `toolResponse`-only
frames carry no parent — but they carry no status either, so nothing needs one.)

**Async-ness is knowable twice, at different times.** `rawInput.run_in_background:true` is an ordinary
argument on an enriching update while the call is still open; `toolResponse.status` is the adapter's
account of what it did, and lands at the end. Both are read. That is not belt-and-braces: the first is
what the agent asked for and the second is what happened, and either alone is a single point of
failure for a row that otherwise goes green over work that has not started.

**Nothing can settle an async row, and this is measured rather than assumed.** The counts lined up
— three launches, three task notifications — so settling them in order is the obvious guess. The
capture says the guess is wrong: the narration returns **T1, T3, T2** against a launch order of
**T1, T2, T3**, so first-in-first-out would have mislabelled two rows of three. The `agentId` appears
only on the launch side (six occurrences, all of them). So the row states what is known — *launched* —
and waits. The end-of-turn sweep leaves it alone, because it only touches rows still `Running`.

**The blob was reaching the screen.** Verified, not inferred: the completed frame carries it in
`content[]`, so `ExtractContentText` picked it up as the row's detail — a paragraph whose own first
sentence asks that no part of it be quoted, including the `agentId` it then prints. The launch's real
arguments (prompt, `subagent_type`, `run_in_background`) are still on the row, because those came from
the call rather than from its plumbing.

### Kiro v3 reaches the same shape by a worse route

v3 tags every sub-agent frame with a flat `agentSubtaskId` and nothing says which call owns it. What
makes it resolvable is that **the invoking row is tagged too**, and is the only frame whose
`toolCallId` wears the `invoke_subagent_` prefix — so `CacheSubagentParent` turns "some sub-agent"
into "that row". Before this, v3 could only DROP the whole stream: the work happened and the
transcript showed none of it.

Only its **tool calls** come back. Its prose stays dropped, and that half keeps its own evidence: a
sub-agent's streamed summary duplicates its row's deliverable *and* concatenates into the main agent's
message — the interleaving that made parallel crews unreadable in the first place.

### Where the nesting lives, and the three things it disturbed

Children are an `ObservableCollection` on the parent `ToolItemViewModel` — the crew/plan-card shape —
drawn by an `ItemsControl` with **no `ItemTemplate`**, relying on the implicit `DataType` template
applying to them by type (so a sub-agent's own sub-agent nests for free). It is **`Collapsed` until
the row is expanded**, which is load-bearing rather than tidy: WPF measures nothing inside a collapsed
element, so a 26-call fan-out realises no container until the user asks. That is both the UX claim —
26 lines become one — and the cheap shape, since re-realising containers is what forces WPF to
regenerate render data, the dominant UI-thread cost of a scroll (#86).

- **The permission banner** highlights the row a prompt is about, and that row may now be inside a
  collapsed parent, where an outline outlines nothing and the user is asked to approve a call they
  cannot see. `ExpandAncestors` runs first — and is deliberately **not** reverted on dismiss, unlike
  the row's own expand state: the ancestors are the route back to a row they have just been shown.
  `BringItemIntoView` scrolls to the child's **root ancestor**, since `Items` holds only those.
- **The transcript PARKS on a pending permission prompt.** Reported live: a fan-out pushes the held
  row far above the fold, so the user is asked to approve a call whose start they cannot see — and
  expanding its ancestors (above) achieves nothing if the row itself is off-screen. The rule that
  settles it: **following is the pane in "auto mode"** — the viewport has been handed
  to us, so we may decide what is worth showing, and a call BLOCKED waiting for an answer is worth
  showing, everything else being still in motion and able to afford being missed. **Scrolled away is
  the opposite**: they are looking at something particular, and moving them off it is exactly the yank
  #90 exists to prevent. There they get the banner's **"Show in transcript"** and choose for
  themselves — which is why that button is always offered rather than only in the scrolled-away case
  (a park can be scrolled away from too).
  <br>**The park is not a scroll-away**, and that distinction is what removes the "resume or stay?"
  question entirely. `FollowingTranscript` records whether the *user* took control, and they did not —
  we moved the view. So the flag is untouched and only *re-aiming* is suspended: answering the prompt
  rides back down with no resume logic at all, while a user who scrolls **while** parked clears the
  follow through the ordinary input path and is left exactly where they put it, with the jump-to-latest
  pill to come back. Hence `BringItemIntoView(item, stopsFollowing:)` — the gesture callers (plan strip,
  menus) still mean "take me there", and this one call does not.
  <br>**A nested target needs the child's own element**, not its top-level container: on a Task row
  holding twenty calls, scrolling to the parent shows its header while the call in question sits far
  below. `RevealItem` realises the root, lays out, then finds the element whose `DataContext` is the
  target.
- **The click handler** now marks the event handled. A nested row sits inside its parent's
  header-bearing panel, so without it one click ran twice and clicking a sub-agent's read collapsed
  the sub-agent row out from under it.
- **The mid-turn tray** is the same root cause as the green tick: a launch emptying `_openToolCalls`
  fired the "next step" release into the middle of a fan-out — delivering a held message into exactly
  the burst of work the user was waiting out. A background launch is no longer a boundary.
- **The streaming message bubble.** A tool
  call closes the bubble (`SetStreaming(null)`), which is right for a **top-level** row: it is how the
  row keeps its place in the order, since the next delta then opens a fresh bubble below it. A
  **nested** row inserts nothing into the transcript, so the same close split one sentence into two
  messages with nothing between them — reported as *"Three"* / *"agents are exploring in parallel"*,
  and once mid-word, *"…WebApplication1 ex"* / *"ist on disk but are not…"*. It reads as a rendering
  fault, not as a call happening. It fires **often**, because on the wire the agent narrates its
  fan-out *while* the sub-agents' calls are streaming in: the captured turn put eight nested frames
  between two halves of one sentence. Both directions are pinned — over-closing splits the message,
  under-closing loses the ordering a top-level row depends on.

Parentage rides the **DTO**, so a restored conversation nests through the same `Apply` with no second
code path — and Claude's child calls were already being persisted, as the flat rows this fixes, so
for Claude the log costs nothing new.

### `claudeCode.title` is a swap, not an addition

Captured: `cc.title = "List tracked non-C# files"` against a row titled
`git ls-files | grep -v -E '\.cs$' | head -300`. The label is better — but it is **byte-identical to
`rawInput.description`**, which is already the row's intent subtitle (#131), so promoting it produced
two identical lines. The subtitle is suppressed when it would only repeat the title; the command stays
in the input detail, in full.

### Verification

Offline: `SubagentNestingTests` (19 tests) replays the captured frames verbatim through
`AcpClientTarget` and through `ChatViewModel`'s replay path, plus one in `HeldMessageTests` for the
tray gate. Ten injected bugs were each confirmed to fail the suite.

The Desktop `--smoke` check earns its place by asserting what no unit test can: that the children
**render**. It counts the child rows' own card chrome inside the parent's realised container —
deliberately not `ContentPresenter`s (WPF creates one per item whether or not a template was found,
falling back to `ToString()`) and deliberately not title text (the title is drawn through an attached
property that builds inlines, so `TextBlock.Text` is empty on a row that rendered perfectly). Verified
load-bearing by deleting the `ItemsControl` from the template: every unit test still passed and the
smoke reported `rendered=0`.

`Console claude-subagents` prints the raw `_meta.claudeCode` shapes **and the wire's own count of
parented calls** beside the mapped events. That count is the point of the proof, not decoration:
without it "we attributed nothing" and "there was nothing to attribute" are the same output and
opposite findings — one a mapper regression, one a model that chose to answer directly. The proof
returns INCONCLUSIVE rather than FAIL when no fan-out happened, for the same reason.

### "They always say running and never seem to finish"

A launched row had no way to stop saying "running", for two independent reasons:
`SettleOpenToolRows` only ever touched `ToolStatus.Running`, so a `Launched` row was never swept when
its turn ended; and `_meta._claude/origin:{kind:"task-notification"}` — the one signal that a background
task had come back — appeared in exactly one place in the repo, a comment in a test file. Nothing in the
running system could learn it.

**Settling launched rows at turn end is wrong, and the captures show why:**

```
2026-08-22 capture:       LAUNCH x3 -> RETURN x2 -> TURN-END -> RETURN      <- returned AFTER the turn
another capture:          RETURN x3 (the previous turn's) -> LAUNCH x6 -> RETURN x5 -> TURN-END
```

A background task outlives its turn, and its report can arrive in a later one. Settling at turn end
trades "never finishes" for "claims to have finished while still running" — the worse of the two errors,
because the first only withholds information and the second asserts something untrue.

**What is sound is a count.** Attribution is impossible (no agent named, launch id never reappears,
order does not match), but once the returns have caught up with the launches, *every* outstanding task
has come back — true of each row individually rather than guessed for it. Until then all of them keep
saying "running", which is honest about the weaker thing known: at least one is still going.

Four rules, each injected and watched to fail:

- **Settle on the COUNT, never per notification.** Per notification mislabels two rows of three.
- **Not turn-scoped.** A per-turn counter stalls non-zero in the first capture above and strands those
  rows for the rest of the session.
- **Floored at zero.** A return with nothing outstanding is credit from before the transcript was
  loaded; unfloored, the counter sits at zero with a later fan-out genuinely outstanding and the first
  return settles all of them. **That rule's first test pinned nothing** and the injection step caught
  it: with a *single* launch a negative counter clears the `> 0` gate just as well as zero, so the test
  only bites when its shape is a fan-out.
- **Only the "running" claim goes away.** The row keeps `ToolStatus.Launched` rather than flipping to
  Success: no result was ever sent for it, so a tick would claim an outcome we do not have.

A **restored** transcript settles unconditionally at the end of replay. The return signal is live
backend state and is not recorded, so replay reaches the end with whatever was outstanding when the log
was written — belonging to a process that has since exited. Without it the bug survives exactly where
it is least defensible: a conversation reopened days later, still claiming three sub-agents are working.

`AgentEvent.BackgroundTaskReturned` carries **no payload**, because the wire carries none. The mapper
yields it *beside* `UsageUpdated` rather than instead of it — the frame carries real consumption
figures too, and reading it as a notification must not cost the usage ring its update.

#### Re-measured on 0.70.0, and what it costs to be wrong

Re-measured against a 2026-08-22 capture (`agentInfo.version` **0.70.0**, one turn, three
background sub-agents) rather than inherited from the 0.63-era notes. Independently confirmed here:
`async_launched` ×3, `task-notification` ×3, three distinct `agentId`s each appearing **exactly once**.

**The launch side is fully attributable; the return side carries no identity at all.** Three
`tool_call_update`s pair `toolCallId` ↔ `agentId` (`toolu_01PXqH…`↔`a885df69f17e5da90`,
`toolu_01G699…`↔`a0e45df7cd0aff826`, `toolu_016UwL…`↔`a0db997a1852b47aa`), and each of those `agentId`s
then never appears again. The three returns are bare `usage_update` frames whose only marker is the
origin tag — no `agentId`, no `toolCallId`. There is nothing to correlate with, which is why the
mechanism is a count.

**The signal is per-return, not one-at-the-end.** Three launches produced three separate notifications.
So *"how many are still running"* is answerable and is a fact about the set; *"which one just finished"*
is not answerable at all. Only the settle decision waits for the count to reach zero.

**And the 1:1 correspondence is an INFERENCE, not a fact.** `task-notification` is an origin tag on a
usage frame, not a completion event — nothing in the protocol says one arrives per finished task. It
held 3↔3 in both captures, which is suggestive and not proof. **This is the load-bearing assumption of
the whole fix, so know what breaking it looks like**: an *extra* notification arriving while rows are
outstanding settles them early, and a row then says "finished" while its sub-agent is still working —
the one error worse than the bug this replaced. A *missing* one strands the rows, which is that bug
returning. Neither is guardable without identity, and there is none.

**Two reading traps in that log**, both of which make the docs look wrong on a first pass:

1. **The wire DOES settle each Task row** — `status:"completed"` one frame after its `async_launched`.
   So "nothing settles it" reads as plainly false until you notice the frame means the opposite of what
   it says. That frame is exactly what `ToolStatus.Launched` exists to disbelieve, and it is why the
   end-of-turn sweep must keep touching only rows still `Running`.
2. **`async_launched` is NOT gone in 0.70.0.** It lives in the vendored SDK
   (`@anthropic-ai/claude-agent-sdk` 0.3.232, `sdk-tools.d.ts`) as a typed union member the adapter
   forwards verbatim — so grepping the *adapter's* dist structurally cannot find it and reports it
   removed, which is a search that would conclude the recognition is dead code. Our capture
   carries it three times under that exact build.

**A lead that does not solve it.** 0.70.0 ships machinery the older captures did not show: a
`tool_progress` beat forwarded as `tool_call_update status:"in_progress"` carrying
`_meta.claudeCode.toolResponse.{elapsedTimeSeconds, subagentType, subagentRetry}`, gated on
`session.emittedToolCalls.has(toolCallId)`. It is **per-`toolCallId`, so attributable** — the only thing
on the return side that is. Two caveats kill it as a settle signal: it **did not fire at all** in our
capture (zero occurrences of all three fields, in a run long enough that a periodic beat should have
appeared — why is not established), and even when it fires it reports *still working*, never *finished*.
Its value would be giving a running row a real elapsed time and a stall reason (`subagentRetry` carries
the SDK's rate-limit retry counters), not settling it.

### The child cap, and the render cost of an expanded fan-out

Nesting rendered a fan-out correctly and made it expensive. An **expanded** row draws its children into
a plain `ItemsControl` — a `StackPanel`, so non-virtualizing — inside ONE container of the transcript's
virtualizing panel. The outer panel sees one item; that item is now twenty-six rows tall, every child is
realised at once, and all of it is re-rendered on every pass over the row.

`render.log`, one session, **no agent traffic in the window at all** (`engine.log` shows the session
restored and the next prompt two and a half minutes later), so purely expanding and scrolling:

| | earlier sessions | after nesting |
|---|---|---|
| dispatcher duty | 1.4—14.3 % | **50.8 %** |
| wall in `MediaContext.RenderMessageHandler` | ~1 % | **33 %** (43.6 s of 133 s) |
| mean per render pass | 12—43 ms | **80 ms** |
| worst single pass | 772—3809 ms | **4216 ms** |
| `VirtualizingStackPanel.InitializeViewport` | 1.26 ms/s | **23.3 ms/s** |

It flips at one moment, and the line names its own cause:

```
14:34:17  render-pass ms=101.1   realised=0  maxMeasure=0.0   carriedMax=4.8
14:34:39  render-pass ms=1779.0  realised=1  kinds=Tool:1  maxMeasure=51.7
```

**A single tool row measuring 50—64 ms**, against 0.5—2.5 ms for every ordinary row in every earlier
session and 2.5× the "one enormous message" (~20 ms) #86 blamed. Markdown was ruled out (`dutyBuild`
0.4—0.5 %) and so was layout — `layoutTotal` is 60—130 ms against 1800 ms passes, so the rest is
render-data regeneration, #86's known dominant cost handed a much larger tree.

So a row draws at most `ToolItemViewModel.ChildCap` (**5**) children, with one overflow step above
them. That step said "Show all N calls" and **dropped the cap in place**, which put the whole fan-out
back inside the one non-virtualizing container — the very cost measured above, reinstated on demand. It
now says "Open all N calls →" and NAVIGATES (issue #148, below), so the unbounded inline case has
stopped existing rather than being deferred behind a click. Each rule below was injected and watched to
fail.

**The cap bounds DISPLAY, never DATA.** `Children` stays whole — the export walks it, the collapsed row's
summary counts it, a reveal indexes into it — the rule `AcpMapper.ClampResult` exists for. Capping the
collection instead passes every visual check and drops a sub-agent's twenty-first call out of the
transcript the user copies.

**It keeps the LAST five, and the permission path decides that.** A blocked call is by definition the
most recent one, so under a first-N window the banner's reveal would raise the cap to the full length of
every large fan-out: the cap would hold everywhere except the one moment the pane is blocked and the
user is waiting on it. Two lesser reasons agree — a first-N window looks frozen for the whole turn while
a fan-out streams, and the transcript containing the row already follows its own newest content (#90),
so oldest-first would have contradicted its own container. **First-N and last-N agree on every COUNT**,
so the tests pin the identity of the drawn rows wherever direction is what is under test.

**A closed row holds nothing and does nothing.** The window is built on open and dropped on close rather
than maintained. A fan-out streams into a row nobody has opened — that is the entire point of nesting —
and every arriving call used to slide a window the control was drawing nothing from: fifty-two
collection notifications per twenty-six calls, for nothing. Nothing is lost, because what open rebuilds
is exactly what close threw away. Closing also puts the cap back, and the reason is the reveal rather
than the button: a banner raises the cap **without the user asking**, so left sticky, answering one
prompt silently leaves a permanently costly row behind for the session. It also makes the claim
unconditional — holding five rows while collapsed is only free *if* WPF never measures inside a
`Collapsed` element, which is an assumption about WPF propping up a performance claim.

**A cap change is ONE `Reset`; a call arriving is a Remove plus an Add.** (Since #148 the only thing
that still raises a row's cap is the REVEAL, which is what the cap always had to be restorable for; the
tests widen through `ExpandAncestors` rather than through the retired button.) Anchoring the window to the
newest call moved its moving edge to the FRONT, so widening five rows to twenty-six became twenty-one
`Insert(0,…)` calls, each re-indexing every container already generated. That cost did not exist while
the window was anchored to the oldest call (the same jumps were appends at the end), and **no content
assertion can see it** — the contents are identical either way — hence
`Mvvm.BatchedObservableCollection` and tests on the notification ACTION, not just its count. "Exactly
one notification" alone is also satisfied by a single bogus `Add`. It mutates `Collection<T>.Items`
directly, which bypasses `ClearItems`/`InsertItem` and so needs `CheckReentrancy()` re-stated by hand.
The streaming path deliberately does NOT batch: rebuilding there would throw away every container on
every call of a fan-out, the exact opposite of the fix, and equally invisible.

**What the cap did not fix was the reflow.** Collapsing a row that "Show all" had made twenty-six rows
tall means twenty-six rows of everything-below moving up and re-rendering; it scales with the count, and
it is confirmed by the shape of the `[realise]` line immediately before the spike — ten tool rows
realised, because a collapse is the only gesture that CREATES viewport space (show-all pushes rows out).
Deferring the container teardown cannot help: the reflow is triggered by the `Visibility` flip itself,
which has to be synchronous for the collapse to appear at all. Only not letting the row grow that tall
does — which is what the drill-down below now enforces by removing the gesture that grew it.

**The whole cap rests on ONE binding**, so it is asserted where a unit test cannot look. `--smoke`
expands a 26-child fan-out and counts the row chrome WPF actually realised inside the parent's
container; rebinding `ItemsSource` to `Children` reports `capped=26/5` and **`newestDrawn=True`** — the
view-model entirely correct, only the binding wrong, which is the #118 shape: green everywhere while
doing nothing.

**The launched row's note is one word** ("running", not "running in background") for the same reason the
title trims: everything else on that row docks right and keeps its width, so every character in the note
comes out of the field saying what the call actually is. What the shortened note stopped saying moved to
its tooltip rather than being dropped — that the row will never settle. The row itself also tips its
full title now: the title `TextBlock` already did, but only across the text, and a pointer over the call
count or the blank middle got nothing.

## Opening a sub-agent's calls as their own transcript (issue #148)

The cap above bounds an expanded row; "Show all" un-bounded it again. Drilling in removes the
unbounded case instead of deferring it: the transcript's `ItemsSource` swaps from `Items` to the row's
`Children`, so those calls are drawn by `TranscriptItemsControl` — the **virtualising** panel — and cost
what any other rows cost. Measured in `--smoke`: a 200-call fan-out realises **13** child containers.

**Not flattening children into `Items`.** Flattening also virtualises, and it breaks the invariant that
`Items` holds only top-level rows — which is not a tidiness rule but a load-bearing one:
`TranscriptMarkdown.RenderTool` recurses into `Children` while `Build` iterates `Items`, so flattening
double-renders every nested row into the export **and** the clipboard, silently. Drill-down leaves
`Items`, `RootAncestor` and the reveal untouched.

**The nav stack must never OWN transcript data.** A scope's items are the parent row's own `Children`,
reached through `Items`. The moment a scope's items lived outside their parent row, `Items` would stop
being the whole conversation and the export rule would break from the other direction — which is also
the real reason a promptable fork cannot be a nav frame (below).

### The split: the view-model owns which items, the view owns where you are looking

`NavPath` (a stack of rows, not a nullable field — a toggle is wrong at depth 2 and wrong silently),
`CurrentItems`, `CurrentScope`, `IsDrilledIn`, `RootHasNewActivity`, `Breadcrumb`. The transcript binds
`CurrentItems`; that is the one XAML edit that does the work.

The view keeps a parallel `_viewStack` of `{Following, ParkedOnPrompt, Anchor, AnchorDelta, Offset}`,
because the follow flag, the permission park and the scroll offset are facts about a **viewport** and
the view-model has no viewport. The follow flag therefore belongs to the frame, so drilling in cannot
leave the main transcript's follow state lying about.

**Back restores by ANCHOR, not by `VerticalOffset`** — and this needed proving rather than asserting. A
virtualising panel's extent is an estimate (the reason `ScrollTranscriptToEnd` is two-pass; the smoke has
measured 1686 against a true 2105). But the offset fallback is captured at the same instant as the
anchor, so **the two agree whenever the estimate is stable** — and on the smoke's 94-item transcript they
did: removing the anchor logic entirely left the check green. Padding the root to 214 items makes the
divergence real, and then the numbers separate cleanly:

| | anchor restored | anchor removed (injected) |
|---|---|---|
| offset out / back | 10157 → 9049 (1108 px) | 10157 → 10157 |
| row landed on | the same row, `top=-41 → -41` | a different row, `top=-41 → -34` |

The second column is the failure the design predicted: *somewhere unrelated but plausible*, which reads
as a scroll bug rather than a navigation one. **A check that cannot separate a fix from its fallback pins
neither** — only the injection said so.

**`AncestorWithin(scope)`**, with `RootAncestor` reimplemented as `AncestorWithin(null)`, so the
scope-relative and root-relative answers cannot diverge: one traversal, one stopping rule.
`BringItemIntoView` is now scope-relative and **never navigates** — an item in another scope gets `null`,
not the nearest thing that happens to be showing. `GoToItem` is *"take me there, wherever it is"* and is
called **only** from an explicit user gesture (the banner's "Show in transcript"). That split is
the issue's "never auto-navigate" rule expressed as an API shape rather than as a habit: no number of new
callers can start yanking the user between views.

### Two entry points, and the second is not a shortcut

The overflow control is **replaced**, not supplemented. But it needs `IsExpanded` **AND**
`HasHiddenChildren`, and the cap does not engage while `Children.Count <= ChildCap + 1` — so a
**collapsed** row offers nothing, and a fan-out of **six or fewer** offers nothing however it is opened.
Without a second way in, whether a sub-agent's work could be opened would depend on how big the fan-out
happened to be, for no reason the user can see. Hence **"Open transcript"** on the row's context menu,
gated on `CanOpenTranscript` (`Children.Count > 0`):

- **Visibility, not enabled-state**, matching "Open file"/"Copy path" in that same menu — a greyed item on
  a row that has no calls reads as a bug on a build.
- **Placed first.** On a sub-agent launch row the two file items are hidden anyway (a `Task` row acted on
  no file), so without it the menu is the single item "Copy"; this gives it a real action.
- **A `Click` handler, not a `Command`**, unlike its three siblings. Navigation has to capture the
  outgoing scroll position *before* the `ItemsSource` swaps, so every entry point routes through the
  view's funnel. A command bypasses the capture and fails **silently** — the symptom is landing at the top
  of the restored view on the way back, intermittently.

### What stays put

Everything session-scoped stays visible and live in a sub-view: the working bar, **Stop**, the mid-turn
tray, the usage ring. The tray's release gate (`_openToolCalls`) is a wire fact about tool boundaries —
**drilling in is not a tool boundary and must not touch it**.

The permission banner **reveals in place** via `EnsureChildVisible`, unchanged; if the target is outside
the current view it does nothing, and "Show in transcript" is the (user-initiated) way across. The pane
never navigates on the user's behalf: the root crumb carries a **dot** instead, so the way back says the
conversation moved on. The dot is fed by adds to `Items` only — a fan-out streaming into the scope on
screen appends to that row's `Children`, so the distinction is structural rather than a guess about what
the user is watching.

**Export and copy stay on `Items`, always.** One identifier away from `CurrentItems` and it produces a
plausible-looking file, found weeks later — so both callers now share one funnel that names the
collection once, and it is pinned by a test.

### A session change invalidates every frame

`LoadSession`, `StartNewSession` and `DeleteSession` all replace the conversation, so every row a frame
points at ceases to exist: a user drilled two levels in who picks another conversation from the history
popup would keep a breadcrumb naming the previous conversation's rows over an empty sub-view, with Back
popping toward a scope that is gone. Note that **`LoadSession` clears inline rather than through
`ClearTranscript`**, so the reset is wired in both places — and it is the likeliest way to reach a stale
frame, being the one conversation swap a user makes mid-read.

The view half is the subtle one: `ClearTranscript` is a view-model method with no hook into the view, so
clearing only `NavPath` would leave the view's saved anchors to be popped against a different
conversation's scroll state. Hence `ChatViewModel.NavigationReset`, an **explicit signal across the seam**
rather than two collections that have to be remembered in step — fired unconditionally, including from
the root, because "there is nothing to reset" is a claim about the view's state that the view-model
cannot check.

### Verification

Sixteen view-model checks (`TranscriptNavigationTests`), four of them injected and watched to fail. But
the feature's whole claim is invisible from there: point the binding at something non-virtualizing and
every one of them stays green while a 200-call fan-out realises 200 containers again. So the `--smoke`
phase seeds 200 children, **scrolls away from the bottom first** (or "back restored the position" is
trivially true at offset 0) through a real wheel gesture, drills in through the view's own funnel, counts
the row chrome WPF realised, and comes back. `--screenshot-drilldown` captures depth 2 **scrolled away**,
which is the only state the jump-to-latest pill appears in — the pill shares the transcript's grid row,
so a pill drawn on top of the breadcrumb is caught by the picture or not at all.

### Does multi-tab supersede this? No — they are orthogonal

A tab is a **session** container; this is a **view over one conversation's item tree**. The discriminator
is whether the thing you opened can be *prompted*, and a sub-agent's calls cannot: no composer, nothing
to Stop independently, and no usage of their own (#125 measured that). Opening 26 reads in a tab gives
them a composer that cannot send, a Stop that stops the parent, and a usage ring showing someone else's
numbers — and a tab has no Back, losing your place being the point of a tab and the failure mode of a
drill-down.

What this owes multi-tab is one constraint: **no navigation state may be process-wide.** `NavPath` is on
the view-model, `_viewStack` on the view, and there are no statics in either — so a second pane inherits
its own navigation for free, and one `static` would silently give two tabs one breadcrumb.

**Forking is the real overlap.** The criterion: **if a forked branch can be prompted it is a conversation and belongs in a tab;
if it is a read-only branch of this conversation's tree it belongs in the nav stack.** Every public member
is `OpenTranscript`/`CanOpenTranscript`, named for what it opens rather than for the sub-agent that
happens to be the only thing that can be opened.

## Folded tool row vs first-class edit card, and why both offer the same actions

**Which shape an agent edit takes is decided in `AcpMapper`, on the call's OPENING frame** — not in the view-model, and not by backend name:

```csharp
foreach (var edit in ExtractEdits(update, …)) { startedEdits = true; yield return edit; }
if (!startedEdits) yield return new AgentEvent.ToolCallStarted(…);
```

If the opening `tool_call` already yields an edit, `ToolCallStarted` is **suppressed and no tool row is ever built**. Otherwise a row exists, and a diff arriving later folds into it (`ChatViewModel`, keyed on a `toolCallId` opened *this turn*). Both captured live, 2026-08-08:

| backend | opening frame | result |
|---|---|---|
| Kiro `--agent-engine v3` | `tool_call kind=edit "Replace in File"`, blank diff → the v3 fallback yields an edit | **edit card**; no "Replace in File" row appears at all |
| claude-agent-acp | `tool_call kind=edit "Edit <path>"`, `rawInput:{file_path}`, no diff | **tool row**; the diff folds in two frames later, with `locations[].line` |

**The split is not arbitrary and the shapes stay.** Kiro v3's edit call carries no usable diff of its own and the write goes out through client-fs (`fs/write_text_file`), so our card *is* the representation; Claude's `Edit` is a genuine tool call with arguments, a result and a **failure mode**. Converting the row into a card was considered and rejected on that last point: the edit card is pencil + `(operation)` + path + intent with **no chevron and no detail surface**, so a Claude `Edit` failing on `old_string` not found would render as a red diff card with the sentence explaining it discarded. Status would survive (issue #46 already routes `toolDone` to edit cards) — the explanation would not.

**What must match is the ACTIONS, not the shape.** Until this, a folded row could be diffed and never opened: it knew the file, used it for the diff, and offered only "Copy", while the card produced by the *other* backend had "Open file" and "Copy path" all along. So the row now carries both, and "Open file" takes the **diff route** — `openFileAtDiff`, which re-locates the change in the file as it stands now — rather than the read-target route, whose destination is the reported line. Merging the two would silently flatten that back.

**It degrades rather than disappearing.** Only the VS host can re-locate a change (it has to read the file), so a host with no `openFileAtDiff` falls back to the reported line — exactly as the edit card has always done. **That fallback was found by the Desktop `--smoke` check, not by the unit tests**, which wired both callbacks and so only ever exercised the best case; the Desktop host wires neither onto a folded row and showed no "Open file" at all. Both menu items are bound to **visibility**, so a row that touched no file shows neither — getting that wrong wouldn't grey an item out, it would put "Open file" on a build.

## Held mid-turn messages (issue #70)

**One ladder, one modifier per rung.** Enter queues for the turn's end, Ctrl+Enter promotes to the
agent's next safe point, Ctrl+Shift+Enter cuts in now — and the row's red ↑ is that last rung for the
mouse. The reason the destructive one is furthest away is one measurement: a steer sent while a
`run_tests` call was running returned `AbortError: interrupt` to the agent and the run was lost, with
nothing surfacing that to the user — the tool row simply stopped. So the destructive gesture is the one
the user has to ask for, twice.

`Ctrl+Shift+Enter` is ours to take: VS binds it to `Edit.LineOpenBelow` **scoped to the Text Editor**, and
the composer is a tool window, so the scopes never meet (checked in Options → Keyboard, 2026-08-10).

**The rung selection is a pure function, and it had to become one to be correct.** `EnterGestures.For`
takes the modifiers and returns the rung; the handler does nothing but apply it. It was inline first, and
the top rung was **dead from the day it was added** — the handler tested Shift for "newline" before it
had looked at Ctrl, so `Ctrl+Shift+Enter` returned early and inserted a line break. Every rung had a
passing check, because the checks drive the view-model's commands and the commands were all fine; the
break was between the keyboard and them. It cannot be reached the other way either: the handler reads the
live `Keyboard.Modifiers`, which a synthesized `PreviewKeyDown` cannot fake, so no Desktop gesture check
could have covered it. Hence the extraction — the decision is testable exactly where the bug was, and
"Shift **alone** is newline" is stated as its own check, because that is the confusion rather than one row
of a table.

**Queue is the default, and that was a correction.** Steer was the default first, on the reasoning that
the earliest honourable release is the most useful one. It is also the one that can fire seconds after
you press Enter, at a boundary you did not know was coming — delivering sooner than intended, which is
precisely what the tray exists to prevent. Sooner is now a keystroke you choose. A consequence worth
knowing: with nothing running, plain Enter no longer goes straight out; it waits for the turn, because
under a Queue default there IS something left to protect (the rest of the turn's plan, which a steer or
a cancel both drop). Ctrl+Enter with nothing running still goes immediately, since the next safe point
is now.

**The mode pill drops a menu, because its chevron said it would.** It was a flip first: one chip cycling
two states behind a glyph that promises a list, so the only way to find out what the other value was
called was to change the setting — and changing it is not free, since switching to Steer with no tool
call open releases the tray immediately. The menu shows both values, **marks the one in force** (or the
two items read as two actions rather than one setting), and gives each the sentence it needs. "Steer" and
"Queue" are Kiro's words rather than self-evident ones, and a tooltip explaining them only arrives after
the user has already had to guess. Two values makes a thin menu; the affordance being honest and the
labels being readable side by side are worth more than the thinness costs.

Built as a `ContextMenu` opened from `Click` — VS's own menu theming, checkmarks and keyboard handling
for free, and Space/Enter reach the same menu because a `Button` raises `Click` for both, so there is one
behaviour to learn rather than a click that lists and a key that cycles. `PlacementTarget` **must** be set
in the handler: a menu opened in code has no idea what it belongs to, so without it WPF places it at the
pointer *and* it inherits the wrong DataContext, which would make the bindings silently resolve to
nothing. `TogglePendingReleaseCommand` stays on the view-model — it is what the screenshot host uses to
render the non-default label, and a two-state cycle is what another host would reach for first.

**Menus needed theming, and none of them had it.** The pill's drop-down came up with WPF's default light
chrome inside a dark pane — and so did every right-click menu in the transcript. A menu you have to summon is a menu nobody
screenshots, so nothing shows the gap. The fix is one **implicit** `ContextMenu` + `MenuItem` style pair in
`DefaultTheme.xaml`, which reaches all of them at once. Implicit is safe here precisely because that
dictionary is merged into `ChatView`'s own resources and **nowhere else**, so the styles cannot escape
into the host's menus. The default `MenuItem` template has to be replaced rather than recoloured: it
paints a light gutter column and takes its highlight from `SystemColors`, and no Setter reaches either.
The tick lives in that template for the same reason it exists at all — a menu of values reads as a list of
actions without it.

Two checks, because they fail differently. `--smoke` compares the menu's brushes against the *resolved*
resources rather than colour literals — the VSIX remaps those keys onto live VS brushes, so a hard-coded
expectation would pass in the Desktop host and be wrong in the IDE. And `--screenshot-held` now renders
the open menu to `screenshot-held-menu.png`, captured from the `ContextMenu` itself the way the usage
popup is, since a menu lives in its own hwnd and the window capture cannot see it. A reference comparison
is not evidence about a *rendered* defect; the shot is.

**The `<mid-turn-message>` framing was arrived at by four live runs, not by drafting.** It is text the
user did not write, so `AGENTS.md` bounds it — *it may only restate what the user's own gesture already
said* — but within that bound the exact wording turned out to matter more than the bound did, and every
correction came from a wire log rather than from reading the code:

1. **It works at all.** A hand-written block on a Kiro cancel had the agent answer an interjection *and*
   resume the second half of its task. Without one, a cancel erases every trace that the agent was
   mid-task, so a bare message should reasonably end the work — and did.
2. **The gesture must survive its own delivery.** Kiro received the *aside* wording for an *interrupt*:
   delivering an interrupt means cancelling, the cancel aborts the running call, and that call's
   completion drove a "next step" release which relabelled the gesture. Two guards, because the second
   fires after `IsBusy` is already false — an ordering nothing in the code suggests.
3. **An optional nudge is declined.** *"Pick the earlier work up again only if it still makes sense"*
   produced "4." and a full stop, with a never-started step silently abandoned. The aside block's plain
   *"carry on"* is exactly why the aside case resumed. Emphasis is not decoration here.
4. **The two halves looked unalike.** Claude, unprompted, reported that the interrupted call *"may or may
   not have run to completion"* and asked before redoing it — better than the instruction required — yet
   dropped the untouched step, as Kiro did. So the block was made to prescribe both: continue what was
   never started without asking, ask about the step that was cut short.
5. **…and that was over-reach, caught by the rule itself.** Re-run, both backends folded the untouched
   step into the same question — correctly, because in that task the second step only made sense after
   the first, whose state was unknown. The clause was not merely unverified: **the user's gesture never
   said it.** "Send this now, interrupting" says nothing about how to sequence the remainder, so
   prescribing a decision procedure was exactly the ventriloquism `AGENTS.md` forbids. What the gesture
   *does* say is that they pressed **interrupt and not Stop** — the work is not abandoned — and the
   destroyed facts (cut short, state unknowable) are ours to restate because our own transport erased
   them. The block now says that and stops; whether and how to resume is the model's judgement, which
   both make well.

**And a limit worth stating, because it bounds the parity claim this whole area rests on:** at step 3 both
backends had identical framing and behaved differently — Claude surfaced the ambiguity, Kiro stopped.
Framing can tell two models the same thing; it cannot make them equally good at acting on it. After step 4
they answer alike, which is the result, not a guarantee that wording closes model-quality gaps in general.

**Ctrl+Enter upgrades what is already queued**, because the release point belongs to the tray rather
than to a message — "I want these sooner" is rarely about only the one being typed.

**How the tool is DELIVERED decides whether it survives — measured, not inferred** (`Console
claude-steer-boundary`, 2026-08-09, adapter 0.63.0). The adapter's source documents `priority:"now"` as
interrupting a single-shot response *"or slotting in between a multi-step turn's tool calls"* — the very
boundary the tray implements client-side — which the `run_tests` loss appeared to contradict. It does
not: **both are true, of different tools.** Two phases, identical but for what was in flight when the
steer landed:

| in flight when the steer lands | outcome |
|---|---|
| built-in (`Bash`, `ping -n 30`) | `ToolCallCompleted success=True` **26s after the steer** — the steered reply waited for it |
| MCP (our `slow_probe`) | `ToolCallCompleted success=False` **10ms after the steer** — `MCP error -32001: AbortError: interrupt` |

**Both are SDK-driven — the axis is MCP vs built-in, not "theirs vs ours"**. The adapter depends on `@anthropic-ai/claude-agent-sdk`
(0.3.220 alongside adapter 0.63.0), so the same agent loop issued both calls. What the error names is
narrower and more useful: **`-32001` is `ErrorCode.RequestTimeout` from `@modelcontextprotocol/sdk`**, and
`AbortError: interrupt` is not a string in `sdk.mjs` at all — it is the abort *reason*, carried as the MCP
error's message. So what the interrupt reached is the **in-flight MCP JSON-RPC request**, which is
cancellable at the transport; the built-in tool's execution was simply not torn down. **Why the built-in
survived is not established** — "the SDK slots between the calls it drives" is a story that fits, and so
is "the abort never reaches a spawned subprocess"; nothing here distinguishes them, and only the MCP half
has a traced mechanism.

What does follow, and is what matters: **every IDE tool we expose reaches the agent as MCP**, so every one
of them is on the side that dies — including `build_solution` and `run_tests`, the expensive ones. The
tray is load-bearing precisely there. Generalising past that (all MCP tools, all built-ins) is one MCP
tool and one built-in wide, and should be re-measured when the adapter updates.

**A surviving call is not a safe haven, and the difference between the two sides is sharper than
"survives / dies"** (`claude-steer-boundary retention`, same session as above). Two follow-on questions,
both measured on a built-in call: does the REST of the turn happen, and does the model still have the
result? A turn was given two commands — `cat token.txt && ping -n 20`, then `echo SECOND-STEP-RAN` — and
steered during the first.

- **The rest of the turn is lost.** Zero tool calls started after the steer; the second command never
  ran, and the turn closed the same instant the first tool result arrived. So the steer does **not**
  wait out a multi-step sequence — it bites at the next moment there is any generation to pre-empt,
  which for a model blocked on a tool is the instant the result lands. The reading that it "waits for a
  safe boundary" is dead.
- **But the result is KEPT.** Asked on a plain follow-up turn, the model returned the exact random token
  the command had printed — and its steered reply had already volunteered *"the first command completed
  and printed `TK…` ahead of the ping output. The second command was never run."* The tool output is in
  the conversation; the pre-empted turn merely never got to speak about it.

So the two sides differ by more than survival. A steered built-in call is **retained and resumable** —
the work is done, the answer is in context, and the user can simply ask for it; only the commentary and
the remaining steps are lost. A steered MCP call is **destroyed and unrecoverable** — `AbortError:
interrupt`, no result on the agent's side at all, while our process runs the full duration anyway and
throws the answer away. Ours is the worse half of an already-lossy gesture.

## The delivery guard covered the whole turn, so the tray worked once (issue #253)

> Reported as *"it says the message won't be sent as the turn has ended when in
> Queue mode, but isn't the whole point of queue mode to send the message when the turn has ended?"*

`_deliveringPending` stops a released batch re-entering its own delivery: releasing runs a send, and a
send ending a turn is itself a release trigger. It was set for the whole of `DeliverPendingAsync` —
**including the await**. A send does not return until its turn ENDS, so the guard was up for the entire
duration of any turn a release had started, and `TryReleasePending` bails on it first thing.

So the tray worked exactly once per user-initiated turn:

1. turn 1 running, queue **A**;
2. turn 1 ends, the tray releases, **A** is sent — and that delivery starts turn 2 with the guard up;
3. queue **B** during turn 2;
4. turn 2 ends, `ScheduleTurnEndRelease` posts the release, the guard is still up, **B** stays in the
   tray. Nothing says so.

**Deterministic, not a race, and the ordering is why.** The release is posted from `SendCoreAsync`'s
`finally`, which runs BEFORE the task completes; `DeliverPendingAsync`'s own continuation — the thing
that would clear the guard — is queued behind it on the same dispatcher at the same priority. The
release always ran first and always lost.

**Not queue-specific.** The next-step boundary is a different trigger through the same gate, so a
Ctrl+Enter message on a tray-started turn was swallowed identically. That is what says the fault was
the guard's SCOPE rather than anything about queueing, and both directions are pinned.

**The fix is to scope it to the DISPATCH**: start the send inside the guard, await it outside. Invoking
an async method runs its synchronous prologue — the only part that can re-enter — and hands back a task
for the rest. Nothing the guard was for is lost: both callers empty the tray *before* calling
`DeliverPendingAsync`, so a batch re-entering its own delivery is already answered by the count.

**And the tray then LIED about it.** `PendingStatus`
had one sentence for "idle with something held" — *"Not sent — the turn was stopped."* — written on the
assumption that Stop (`_stopSuppressesRelease`) is the only way to be there. It is a guess worn as a
fact, and the screenshot on the issue is that guess being wrong: a user hunting a Stop they never
pressed. **A status may only name a cause it has actually checked.** The stop branch now reads the flag;
everything else says the agent isn't working. The second branch is not a state any known path
produces — it is there so the next strand, whatever it is, reports what the property can see.

### Verification

`HeldMessageTests.AMessageQueuedOnATurnATrayReleaseStartedStillGoesAtItsEnd` and
`ANextStepBoundaryStillReleasesOnATurnATrayReleaseStarted`. Proved load-bearing by
putting the await back inside the guard:
**2 failed of 24, and they are the two** — the count says the check ran, the identity says it measured
this bug.

## The open-call ledger: one open/close pair per id (issue #190)

> Reported as *"Steer at next step gets stuck and doesn't send when it should —
> either it says Steer but is actually in Queued mode, or we think some tool is still running when it
> isn't really."* The second sentence is the literal mechanism rather than a guess.

`_openToolCalls` is what "next step" means: add on `toolStart`, remove on `toolDone`, clear on
`turnDone`, and fire the release when the set empties. **It is a ledger written by two predicates,
evaluated on two different frames, by two pieces of code, with nothing forcing them to agree** — the
same class as the `PlanPayloads` lesson (*yields candidates rather than branching on the engine, so the
plan reader and the cleared-list reader can never disagree about where to look*). Here the open-writer
and the close-writer could disagree, and did, in three ways.

**A phantom that never leaves — background launches.** A background sub-agent's launch receipt is
deliberately not a boundary: it is the *start* of minutes of work, and releasing there delivers a held
message into the middle of a fan-out (issue #125, pinned). But nothing removed the id when the task
actually returned — `NoteBackgroundTaskReturned` decremented its counter and settled the rows and never
touched the ledger. So one background `Task` held the set non-empty for the rest of the turn: the rows
on screen went settled, every later tool pair came and went unremarked, and the tray held to `turnDone`
— which is Queue's behaviour, under a pill still reading "Steer". Meanwhile the status line named a row
the user could see had finished. **The return is the boundary the launch receipt was standing in for**,
so the drain belongs at the moment the counter reaches zero, beside the settle it already does.

**A phantom from a start whose completion was suppressed — plan calls.** The mapper decided "is this a
plan tool?" independently on each frame, from that frame's own `rawInput`. Fine while both frames agree,
and they need not: the mapper's own comment records that some adapters *"open a tool call with a
placeholder title and empty rawInput, then send the real title/arguments on an update once the input has
streamed in."* Such a call emits a start (no `rawInput` ⇒ not a plan tool) and is then retired into the
plan card by the update, whose completion is suppressed — a start with no close, stranded until the turn
ends. **Structural rather than backend-specific**: whichever adapter streams `rawInput`, nothing
made the two predicates agree. So plan-ness is now decided **once, on the opening frame** and remembered
per session (`AcpMapper.CachePlanToolCall`), and where the opening frame did *not* retire the call its
completion is emitted — closed, not enriched, because the card is what says what the call did.

**A boundary that was missed — v3 edits, and its inverted twin.** The mirror image: where the opening
frame already carries a diff the mapper emits the edit *instead of* a start, while `tool_call_update`
completes unconditionally. So a Kiro v3 write produced a `toolDone` for an id that was never opened —
and the gate read

```csharp
if (!_openToolCalls.Remove(id) || _openToolCalls.Count > 0) { …; break; }
```

which short-circuits on the first half and skips the release **even when the set is empty**. It asked
"did I remove something *and* is the set now empty" when the only thing it wants to know is *is anything
running now*. The gate now asks that.

**The inverted symptom is the sharper half and reads as a different bug entirely.** Because a v3 edit
was never *in* the ledger, a message typed during a pure run of edits saw `Count == 0` in `HoldMessage`
and was released immediately, into the middle of a write — **the tray never appeared at all**. Same root
cause, opposite failure. So an `edit` event opens the ledger too (idempotently, so an edit folding into a
row opened this turn changes nothing), and `PendingStatus` can name the file, an edit having no row in
`_toolsById` to take a title from.

**Why every offline check was green.** Every test in `HeldMessageTests` raised a **matched**
`toolStart`/`toolDone` pair. There was no test where a completion arrived for an id that never started,
none where a started call was retired into a plan card, and the one background test stopped at the launch
receipt and never raised the return — **the harness supplied the pairing the product cannot rely on being
given**, which is #118's shape. The three new checks raise the unmatched shapes, and each was proven
load-bearing by injecting its own defect; the plan one is asserted **in both directions**, since a check
that only proved the missing completion now arrives would pass just as well on a mapper that had stopped
retiring plan calls at all.

**One change ships without a check of its own, deliberately.** With the open side complete, the gate
rewrite has no independently reachable failure: a held message needs a non-empty set to be held at all,
so "an unmatched close with the set already empty" is not a state the tray can reach. It is kept
because it makes the ledger's question match its meaning — the next unmatched close, from an engine
shape nobody has seen yet, strands nothing — and it is recorded here rather than pinned by a test that
would pin nothing.

**And the default is now a setting** (`ExtensionConfig.DefaultMessageRelease`, General → *Messages typed
while the agent is working*). Queue remains the shipped default for the reason it was chosen — the next
step may be seconds away — but that trade is a working style rather than a fact. It is a **preset, not a
policy**: the tray's pill still moves it for the session, and the view-model still never reads
`ExtensionConfig` (the host sets `PendingReleaseMode` after construction). The hazard a setting whose
values are *words* carries is silence — `PendingReleaseModes.Parse` answers Queue for anything it does
not recognise, which is right for a config written before the setting existed and wrong for a manifest
offering a spelling the parser has never heard of, so the manifest's enum is checked against the parser
and the two values are checked against **each other** for round-tripping to different rungs.

## A next step before the first step is not a safe point (issue #273)

> Measured 2026-09-11 from two turns killed by a Ctrl+Enter, both reproduced from `acp.log`.
> Backend `@agentclientprotocol/claude-agent-acp` 0.63.0.

**What the wire showed, both times.** A `session/prompt` goes out; the only inbound frame for 11 s
(then 16 s) is `usage_update` — no `agent_message_chunk`, no `agent_thought_chunk`; a
`_session/steering` carrying the *promote* wording ("chose to wait for a safe point rather than
interrupt you") is sent and acknowledged; and **2–4 ms later the prompt fails**:

```
JSON-RPC -32603
Internal error: [ede_diagnostic] result_type=user last_content_type=n/a stop_reason=null
```

Not image-related — the first prompt carried an image and that was the obvious reading, but the
second was `text,text` and failed identically. Not steering in general — two earlier steers in the
same session landed on turns that had produced content and were fine. And not a narrow race: the
window is the model's pre-content working time, at the head of most substantive turns.

**Why the rung fired there.** `_openToolCalls` is what "next step" meant, and an empty ledger is
true in two different states — *between* steps and *before the first* — of which only one is safe.
The backend says which in its own words: `last_content_type=n/a`. So the definition gained its
second half: no tool call running AND the turn has begun its reply. Gated on **state**
(`_turnHasContent`) rather than on a list of frames, which is the out-of-turn window's precedent
(`!_sessionStarted`); set by the first text, thought, tool call or edit of a **live** turn and reset
when a prompt goes out. Usage is telemetry and does not count. Two consequences worth stating:

- **A reply or a thought is itself a boundary** — the one a message promoted during the window was
  waiting for — so the first content frame with nothing running fires the release the way a
  completion does. A call opening is content but not a boundary; the release waits for its close.
- **Only a LIVE turn's content.** Text arriving with no turn open is steered work running
  out-of-turn, and releasing on it would send a prompt into the middle of exactly the work the
  window exists to wait out. Hence the `IsBusy` half of `IsAtNextStepBoundary`.

**The cut-in rung is not gated — the user asked for it twice — but its mechanism is.** Before the
first content frame a steer does not interrupt the turn, it kills it, and what becomes of the
steered message on the agent's side is **not established** (the capture cannot say; the host side
is fine — the message is shown and recorded before the steer goes out, and a failed steer request
puts the text back in the box). A cancel there loses nothing, because nothing has been produced.
So `SendPendingNow` routes on `CanSteerNow` (`CanSteer && _turnHasContent`) and takes the cancel
route a non-steering backend always takes; the message goes as an ordinary prompt at the turn's
end, `<workspace-context>` included. `TryReleasePending` reads the same predicate for one rule's
sake — at a boundary the turn has content, so nothing changes there.

**On Kiro the promote rung now waits too.** Before, Ctrl+Enter with nothing running cancelled at
once; now it waits for the first content frame and cancels then. One definition of a boundary on
both backends is the rule (*same release points everywhere, delivery chosen from the handshake*),
and what the wait costs is one streamed chunk that stays on screen either way.

**The tray names the wait.** The second thing the issue reported is that nothing marks the window
— the pane shows its generic working indicator, so *sent, nothing back yet* looks exactly like
*streaming*, and "I sent one while you were generating" was an accurate description of a turn that
had emitted nothing for sixteen seconds. With the guard the steering-specific harm is gone; what
remains is that the tray, where the user is making exactly that judgement, now says *"Waiting for
the agent to begin its reply…"* rather than "Sending at the agent's next step…". The working bar
is unchanged.

**The open questions, answered.**

- *Is the steered message lost when the turn dies?* Not on our side (above). On the agent's side,
  unknown; the cut-in re-route makes it moot for the one rung that can still steer there.
- *Is the queue rung exposed?* No. A failed turn still returns from `PromptAsync`, and
  `SendCoreAsync`'s `finally` releases the tray on the turn's return, not on `turnDone`. **But the
  ledger was**: an `error` is a turn's terminal failure by construction (one emission site, inside
  the turn's own channel) and no `turnDone` follows it, so the ids a failed turn stranded outlived
  it and would have held the next turn's tray on a row the user could see was dead. An error now
  clears the ledger exactly as `turnDone` does.
- *Adapter-specific?* Unknown, and the guard is the fix available to us regardless — the mechanism
  is vendor-undocumented and capability-gated, so nothing about it can be relied on to change.

### Verification

`HeldMessageTests`: `CtrlEnterBeforeTheTurnsFirstContentWaitsForIt`, `AThoughtIsTheTurnsFirstContentToo`,
`TheContentSignalResetsWithEachTurn`, `CuttingInBeforeTheFirstContentCancelsRatherThanSteers`,
`AnErroredTurnLeavesNothingOnTheLedger`. One existing check,
`SteeringSoonerDeliversTheMessageItWasTypedWith`, now raises a content frame first — its subject is
the promotion's ordering, which needs a reachable boundary. Four injections, each LOAD-BEARING with
the expected count AND identity: "next step" meaning only "no tool call running" (3 fail: the first
three above); the has-begun-its-reply flag never reset per turn (1); a turn's terminal error leaving
the open-call ledger uncleared (1); and the cut-in rung steering before the first content frame (1). The Desktop steer proof (`--smoke`) waits for the
fake's first delta before promoting, reading `IsAgentTyping` as "busy with no bubble streaming" —
and the fake's `[slow-reply]` marker holds that turn open after its first delta until a steer
arrives, because the ordinary fake turn streams its whole reply inside one dispatcher tick and the
poll woke to a finished turn (measured: `busy=False` on both TFMs).

## Steer vs cancel + send: the mechanism differs, the behaviour must not

`claude-steer-boundary compare`, 2026-08-09. The question behind the whole boundary series: if a steer is
close enough to a well-timed cancel + send, then Claude's steering is a **mechanism** rather than a
capability, Kiro's cancel + send is not a lesser path, and the two backends have no business behaving
differently in the UI. Both runs interrupt the same in-flight MCP call at the same moment, and both
deliver a message answerable only from earlier conversation.

| | steer | cancel + send |
|---|---|---|
| in-flight MCP call | destroyed — `MCP error -32001: AbortError: interrupt` | **no terminal frame at all** |
| the agent's account of it | accurate: *"interrupted… nothing to report"* | **wrong**: *"it never ran and returned nothing"* |
| answered from context | yes | yes |
| reply arrives | out-of-turn | in a real turn |
| turns completed | 1 (`end_turn`) | 2 (`cancelled` + new) |

**The user-facing outcome of the message is the same** — both stop the work, both answer correctly from
context. That is the finding the design rests on. But two real differences remain, and **they cut in
opposite directions**, which is why neither path is simply better:

1. **Cancel is worse on the interrupted call.** The wire carries no completion for it, so a transcript
   row opened by that call **never settles** — `IsRunning` is `_status == "working"` and nothing on
   `turnDone` clears it (the `_openToolCalls.Clear()` there is the tray's release gate, not the row).
   The steer at least produces an honest failed row. Worse, the agent then *reports* that nothing ran
   when our tool had already started and will run to completion — the misleading-result failure the
   repo's own tool-result rule is about, arriving from the backend instead of from us.
2. **Cancel is better on turn accounting.** A real turn, a real `stopReason`, the reply in-turn — none
   of the out-of-turn window's estimation needed.

**So parity is reachable, but the host has to supply what the wire does not.** Keeping steering is the
decision (it is the gentler of the two on the row that matters); the corollary is that the cancel path
owes the user the same settled transcript — anything still open when a turn ends without a terminal frame
must be marked interrupted by the host.

**Whether the backend settles it is per-ENGINE — measured across three** (`claude-steer-boundary kiro` and
`… kiro-v3`, same host code, same catalog, same timing, only the provider swapped):

| backend | the in-flight call, on cancel |
|---|---|
| Claude adapter 0.63.0 | **no completion at all** |
| kiro-cli-chat 2.13.0, default engine | **no completion at all** |
| kiro-cli-chat 2.13.0, `--agent-engine v3` | `ToolCallCompleted success=False` — *"Tool execution was cancelled by the user."* |

All three kept conversation context on the follow-up prompt. **A backend CAN close those
rows** — v3 does, and does it better than our fallback wording. Note what that rules out as an explanation: not the vendor, not the CLI version (v3 and the
default engine are the *same* 2.13.0 build), and not cancelling as such. It is the engine, which is the
same axis as v3's differences in permissions, edits, models and sub-agents.

So the host sweep is a **fallback, not a correction**: it touches only rows still `Running`, so a backend
that settles keeps its own reason and the sweep is a no-op there. Two things to be careful of, both of
which the sweep relies on: a steered turn also ends with work still to come, but its destroyed call is
settled explicitly *before* `TurnCompleted` (measured 20ms apart, in that order) so the sweep must not
race it; and the sweep is a transcript concern, distinct from the `_openToolCalls.Clear()` in
`NoteToolBoundary`, which is the tray's release gate and must keep clearing regardless.

Incidental confirmation of a fix made one round earlier: Kiro titles the same call `Running:
@code_wicket/slow_probe` where Claude titles it `mcp__code_wicket__slow_probe`. Arming the
delivery on the title would have silently missed on Kiro; arming it on **our own catalog's invocation** is
the same fact on every backend, which is why it moved there.

**Two more instrument errors, both caught by reading the timeline rather than by the assertions.** The
first comparison armed its delivery on "the first tool call starts" — but the agent opens with its own
`ToolSearch` call, so the cancel fired before `slow_probe` was ever invoked and the run compared an
interrupted call against a call that never happened. The delivery now arms on our tool by name and
records, from our own catalog, that it was executing at that instant. The second: `callDestroyed` was a
bool, so a **missing** terminal frame read as "survived" — the exact inverse of the truth. It is
three-valued now. Both are the same failure as the ones below: an artifact that agrees with the
hypothesis.

**A token in the prompt makes this measurement invalid while it looks fine.** Put there (`run exactly:
echo TK…`), the model can answer from the instruction, and a correct answer proves nothing about the tool
result. So the token lives in a file the command reads and appears nowhere in the prompt. The tell is the
one this area keeps meeting: the check passed for a
reason other than the one being tested.

**And the call is orphaned, not cancelled — which is worse than "the run was lost".** Our `slow_probe`
ran to completion (+30.5s, well after the abort at +7.3s) and **its cancellation token never fired**: the
abort reached the agent's view of the call and nothing else. A steered-through `run_tests` therefore
still runs, still occupies the machine for its full duration, and has its result discarded — the user
pays the whole cost and gets nothing. The original note said the run was lost; the measurement says the
run *happens* and the answer is thrown away.

**Two notes on the method, both load-bearing.** **A proof that does not compare timestamps reads a pre-steer failure as a post-steer abort**: the agent's
own shell sandbox **blocks** a bare `sleep 25` outright (`Blocked: sleep 25 followed by…`), settling that
call ~3s *before* the steer goes out, and such a proof reports "BOTH aborted". It records each completion's
arrival time and only counts one that lands **after** the steer; a call that settles first is reported as
having measured nothing. `ping -n 30` replaces the sleep. This is the same shape as the errors this file
already documents — a plausible reading of an artifact the instrument produced itself — and it is why
the phase asserts the call was live at steer time rather than assuming it.

**Can we even tell what a "safe point" is?** Partly, and the two halves differ in kind. Measured across
the 20 saved `acp.log`s (81 turns, telemetry frames excluded):

| | |
|---|---|
| update frames with ≥1 tool call open | 3800 / 8191 (46%) |
| open-set drops to empty | 1610 (~19.9 per turn) |
| max concurrent tool calls | 2 |

So "nothing is running" is a real, recurring state, and it is **exact** on the wire — `tool_call` opens,
`tool_call_update{completed|failed}` closes, no timer involved. Two honest caveats. The 46% is by frame
count because `acp.log` has no timestamps; by *wall clock* it is far higher, since a `run_tests` frame
lasts minutes and a text chunk lasts milliseconds — which cuts in favour of holding, as the moments
worth protecting are exactly the ones frame-counting under-weights. And the promise is **best-effort,
one round-trip stale**: the logs show `toolDone` immediately followed by the next `tool_call`, so a
release can land inside a call opened in the gap. Strictly better than aborting the call the user was
watching (milliseconds of work against a four-minute build), but a race we cannot close — hence the tray
names what it waits for and always offers send-now, because a best-effort behaviour must report what it
did and offer a manual override.

**Message boundaries are not a release point, deliberately.** There is no end-of-message frame: text
arrives as `agent_message_chunk` deltas and simply stops, so "after the reply" could only ever be a
quiet timer — the same guess as the out-of-turn working window. It would also almost never fire: in
these logs a turn is one short text burst near the start and then a long chain of tool calls. And
pre-empting prose destroys nothing; the cost of cutting a sentence short is an unfinished sentence.

**Routing stays on `IsBusy` at the moment of delivery** — a live turn is a fact and the only thing that
makes a steer the right shape; with no turn open the batch is an ordinary prompt and gets the
`<workspace-context>` block one carries. The single exception is *timing*: a steer pre-empts the turn
that carried it, so `IsBusy` goes false while the agent is still working, and a turn-end batch would be
sent into an agent mid-edit. It therefore waits for the out-of-turn window to close — the only
end-of-work signal that exists for steered work. The estimate decides **when**, never **how**.

**UI.** The tray sits between the working bar and the composer — between the thing saying the agent is
busy and the thing that caused the wait, which is also the order they happen in. Inside `ZoomTransform`
(it is the user's own words awaiting delivery). Each message is a **row with an action area**, not a
line of text: send-now and remove live there, and it is where any further per-message
action belongs. The release is **always a button, on every
backend** — it used to fall back to plain text where the backend could not steer, because "next step" was
unhonourable there and a control that can't honour the option it offers teaches the wrong thing. That
premise is gone: at the boundary a non-steering backend ends the turn and delivers, which the comparison
above shows is the same outcome for the user, so the choice is real everywhere. The one asymmetry left is
invisible: **with nothing running the message goes out immediately on both** (a steer where one exists,
an ended turn where it does not), so the tray never appears — which is why a test that wants a *held*
message has to open a tool call first, and why three that used "no steering" as a shortcut to a held
message had to be corrected when this landed.

**Per-message dispositions and batched delivery are not in conflict**: the batch is whatever is
*eligible* when the release fires, so a message pointed at the turn's end simply isn't in the next-step
batch.

**Verification.** `HeldMessageTests` (13, all injection-verified) plus two Desktop checks. The fake
provider grew a `[slow-tool]` prompt keyword — a tool call that stays open ~2s and **fails with
`AbortError: interrupt` if a steer arrives while it runs**, modelling the measured behaviour. Without
that the smoke's headline assertion would be vacuous: a host that steers immediately would still see the
call finish. With it, injecting the old steer-on-Enter behaviour fails with `toolSurvived=False
(Failed)` — the run destroyed — which is the actual bug, not a proxy for it. `--screenshot-held` renders
the tray with two messages waiting on a running call.

## Mid-turn steering: the mechanism under "send now"

**Advertised top-level, and capability-gated — never version-gated.** `_meta.steering.supported` sits
beside `agentCapabilities` in `initialize`, and needs an explicit `[JsonPropertyName("_meta")]` (the
camelCase formatter drops the underscore and the property silently never binds). Adapter 0.55.0
advertises `promptQueueing` but not steering; 0.63.0 advertises both. The capability refresh therefore
runs for **every** agent, deliberately not behind `DiscoverCapabilities`: steering is a property of the
adapter build on the user's machine, so a hand-declared capability would be a claim about their machine
we have no business making. An unrecognised `outcome` is read as `Injected` rather than as a failure —
the protocol's contract is that the message is never dropped, so on any successful response the one
thing we know is that the agent has it.

**That gate turns out to be load-bearing for a reason we did not have when we chose it** (2026-08-09).
The mechanism it gates is **vendor-undocumented**: the adapter reaches it through an SDK field its own
JSDoc calls *"an internal Claude implementation detail, not part of the wire contract"*, and the public
Agent SDK documentation bears that out — the type reference does not publish `SDKUserMessage`'s shape,
the streaming-input page never mentions the field, and **no** mid-turn injection is documented anywhere;
the only documented mid-turn control is `interrupt()`. So the capability can stop being true without any
version we could predict. Gating on what the handshake offers degrades to the tray by itself when that
happens; a hand-declared capability or a version check would go on asserting a guarantee the vendor
never made. The argument for capability-gating was originally about *the user's machine* — it is also
about the mechanism being undocumented, and the second reason is the stronger one.

**It is a real injection — into a turn that is not ours.** Measured live against 0.63.0 (2026-08-01),
frames in order: an `agent_message_chunk` cut mid-sentence (`"6 — perfect: "`), the steer response
`{"outcome":"injected"}`, then the pre-empted turn closing with an ordinary `stopReason:"end_turn"` and
all-zero usage, and only *then* the steered reply's own deltas — followed by 29 seconds of silence. So
the steer **pre-empts the generation**, the interrupted turn **closes ~10ms later like any finished
one**, and the real work (tool calls, diffs, permission requests, the reply) runs with **no turn open
and nothing on the wire marking its end**.

**Read from the adapter's own source (2026-08-09), which settles what the wire could only suggest.**
`@agentclientprotocol/claude-agent-acp` ships `dist/acp-agent.js` unminified with full JSDoc — 0.63.0 on
this machine, the same build the capture above was taken against, so it is evidence about the *measured*
behaviour rather than about some other version. It corrects the gloss this section used to carry ("not
the injection its name suggests"): the steer is **not a cancel under another name**. `steer()`
(`:908-966`) never calls `cancel()` or `query.interrupt()` — compare `cancel()` (`:2901`), which sets
`session.cancelled`, interrupts the query and settles turns `stopReason:"cancelled"`. What it does
instead is push an `SDKUserMessage` onto the **same streaming input the SDK is already draining**, at
`priority:"now"`, which the SDK routes into the in-flight turn. The `end_turn` above is therefore the
pre-empted generation's ordinary result taking the ordinary settle path, not a cancellation in disguise
— which is also why `stopReason` cannot discriminate.

**The absent turn is a design decision, stated outright**, and that is the load-bearing part: *"unlike
`prompt()`, it does NOT create a Turn or enqueue on `turnQueue`… The steered message's own output
streams via `session/update`, not this response."* Nothing marks the work's **start** either, for a
stated reason — *"the injected message's echo carries a uuid that matches no queued turn, so the
consumer drops it as an unrelated replay without promoting/settling anything."* So the out-of-turn
working window is not covering for a quirk that might be fixed upstream and then deleted: it is the only
response a client can make to how the extension is built, and it stands until the wire protocol itself
grows a turn around steered work.

**`startedNewTurn` is the adapter's own cancel-free fallback.** With no unsettled turn — the turn we
meant to steer raced ahead and finished — `steer()` calls `this.prompt()` detached and returns
`startedNewTurn`, never dropping the message and never surfacing the race as an error. That is an
adapter guarantee rather than something we arrange, so our own handling of the same race is
belt-and-braces over it, not load-bearing.

**Why this is not "a hard cancel followed by a prompt", asked directly (2026-08-09).** The two are close
in *effect* — a killed generation, a lost in-flight tool call, an `end_turn` naming nothing unusual — and
the question is a fair one to reach from the wire alone. Three things separate them, and only the first
favours cancel + prompt. (1) Cancel + prompt hands back a **real turn**: a completion frame, a
`stopReason`, usage, and somewhere for an error to land — all the things the out-of-turn window exists to
estimate. (2) A steer carries **no `<workspace-context>`**: `SendAsync` prepends the workspace snapshot
and the one-shot `<ide-tools>` hint, `SteerAsync` sends the bare text as a single `ContentBlock`, so a
steered message reaches the agent stripped of the ambient IDE context every ordinary prompt carries.
(3) Against those, the steer **does not interrupt the session** — it adapts the running turn from inside,
and the message cannot be dropped in the race. Verdict: keep steering. What looked like a free
simplification (delete the steer path, cancel + prompt everywhere, delete the working window with it) is
a real trade, and (2) is the one worth fixing on its own terms.

**How to tell a turn-ending `session/prompt` response from one that isn't.** Three candidates, one
usable. (1) *We know we steered* — client-side, exact; this is the mechanism. (2) *Zero usage on the
response* is a real tell but a trap: across every saved `acp.log`, 69 `end_turn` responses, only 2 with
all-zero usage, and both are the adapter's local-only-command path — so it genuinely flags our case, but
**Kiro reports no per-turn tokens at all**, which would classify every Kiro turn as "not a real turn
end". Good corroboration, unusable as a discriminator. (3) *`stopReason`* is useless — the pre-empted
turn reports the perfectly ordinary `end_turn`.

**Why turn-less events must reach the host.** Dropping them is not graceful degradation: permission
requests are JSON-RPC *requests* on a separate path and arrive regardless, so silence means asking the
user to approve a file write whose tool row, diff and edit card were all discarded. Proven both ways by
`Console claude-steer-tools`, whose entire post-turn timeline collapses to a single naked permission
prompt when the routing is narrowed. It also covers a backend answering `session/prompt` early
(kirodotdev/Kiro#7724), whose reporter asks the maintainers exactly this question and whose workaround
is a client-side idle timer — the same shape as our quiet window, reached from the other direction.

**The one exception, and why it must not be widened.** `session/load` *replays the whole conversation*
as `session/update` notifications before it responds — spec'd behaviour, so a client can rebuild its UI
— and it lands with no turn open. Once the routing became unconditional, every resume appended the
entire conversation to the transcript **behind** the user's new message and re-recorded it: measured
live, one session's log went 610 entries → 2262 across four restarts, and the user's own message read as
lost under ~200 events of replayed history. We rebuild the transcript from our own log before the call
is made, so the backend's copy is pure duplication. Suppressed for the duration of the load only
(`_loadingHistory`), and the flag **cannot** be cleared on the response alone — the response overtakes
the notifications that preceded it (#33 again), so the `finally` drains the inbound dispatch first. The
test catches exactly that: a fix that clears on the response alone leaks the tail.

**The out-of-turn working window.** `IsAgentWorkingOutOfTurn` keeps Stop, the pickers, New Session and
the working bar honest while steered work runs. Two properties are load-bearing. It is
**re-openable**: a window that cannot re-open lets one quiet stretch longer than the timeout end the busy
state for the rest of the steered work, and every later tool call, edit and reply arrives beneath a pane
insisting the agent is idle. And its quiet
timeout is **three windows keyed on what the silence followed** — minutes while a steer has produced
nothing yet (the protocol guarantees work is coming, so that is a backstop rather than a guess), tens of
seconds after a tool call or reasoning, seconds after reply text, which usually comes last. The short
one is safe *only because* the window can re-open: guessing "that was the last word" then costs a blink
instead of the remainder of the turn. `usage`/`turnDone` are telemetry and count as neither — usage
frames trail a finished reply, so counting them would hold the bar up after the agent had stopped.
`error` is excluded on the same ground (#267): a `SessionError` has one emission site, inside a turn's
own channel, as its terminal failure, so it can never be evidence of work outliving that turn. It
reached the window because the shell ↔ engine hop then had no ordered dispatch — the prompt's response
cleared `IsBusy` before the error was applied — and put up the full 45-second window over a throttling
failure already on screen. That hop was fixed as #277 (see [gotchas.md](gotchas.md): ordered dispatch and a drain
barrier in `EngineClient`, plus the view-model applying its queued events inline before it acts on a
response). The exclusion is for what the frame *is*, so it stays fixed.

**And the session ANNOUNCING ITSELF is not the agent working.** The window opens on any turn-less live
event on purpose — naming the events that *do* mean work would silently miss whatever a backend does
next — but the converse hole is real, and it was measured on a Kiro v2 wire log (2026-08-10): opening the
chat window produced four `_kiro.dev/mcp/server_initialized` frames (two servers, twice over, because the
warm start re-announces them) plus an empty `_kiro.dev/subagent/list_update` roster, every one of them
before the user had sent anything and therefore turn-less. The pane put up the working bar and offered
Stop for a full 45-second quiet window with nothing running and nothing to stop. Kiro-shaped only in that
Kiro is the backend that announces most: the defect is general.

The discriminator is **not who sent the frame but whether it is evidence the agent is generating or
executing right now**. A server finishing its handshake is the CLI's own plumbing; an empty sub-agent
roster is the *absence* of sub-agents. A **populated** roster is work and still counts — under v2 each
sub-agent is its own session, so that roster is the only thing reporting them while the orchestrating
turn is not ours, and excluding `subagents` wholesale would blind the window to exactly the case it was
built for. Both halves are pinned, and both were checked by injection: dropping the guard fails the
announcement test, widening it to all rosters fails the other.

Note this is separate from **routing**, which stays on `IsBusy` — the window was never able to misdeliver
a message, only to misreport one. What it cost was the working bar, the Stop button and the pickers, for
45 seconds after every window open.

**Prior art.** Zed queues by default and offers steering as a per-message opt-in that never crosses the
wire (a client queue plus an in-process `end_turn_at_next_boundary` flag), and **refuses steering for
external agents entirely** because it can't detect their turn boundaries. The VS Code ACP adapter
(`formulahendry/vscode-acp`) forecloses the question — the composer is hard-disabled for the whole turn
— though its *rendering* is more robust than ours was: a flat listener fan-out with no turn scoping, so
late chunks still reach the transcript. Three different things are called "steer": Zed's lands after the
current step as a new turn; Kiro's TUI picks up at the next safe point and is not exposed over ACP at
all; Claude's `_session/steering` injects mid-sentence. Ours is the only true injection of the three,
which is exactly why it needed holding put in front of it.

**The Agent SDK's own documented model is queue-then-interrupt — which is the tray** (2026-08-09).
Streaming input mode advertises *"Queued Messages: send multiple messages that process sequentially,
with ability to interrupt"*, and the only mid-turn control in the reference is `interrupt()`. So the
shape we arrived at from a lost `run_tests` run is the shape the vendor documents; the injection we ride
is the undocumented extra. `interrupt()` has since grown a **receipt** — with the `interrupt_receipt_v1`
capability (Claude Code v2.1.205+) it *"resolves with an `SDKControlInterruptResponse` listing the queued
messages that survive the interrupt"* — which is our "Stop delays, never destroys" invariant reached from
the other end, and worth watching: if that reaches ACP it is a better-founded release point than a quiet
timer. Also on the cancel + prompt side of the trade, `interrupt()` during the thinking phase is a known
defect ([claude-agent-sdk-typescript#366](https://github.com/anthropics/claude-agent-sdk-typescript/issues/366)
— resolves the aborted turn as an `is_error` `error_during_execution` instead of a clean cancellation).

## Pasted images (issue #118)

**The backends were never the obstacle.** Every one we ship advertises `promptCapabilities.image: true`
at `initialize` — captured from our own `acp.log`, not assumed: kiro-cli **2.13.0** and **2.16.2**
(`{"image":true,"audio":false,"embeddedContext":false}`), Kiro **v3**
(`{"image":true,"embeddedContext":true}`) and **claude-agent-acp 0.63.0**
(`{"image":true,"embeddedContext":true}`). What was missing was entirely ours: `ContentBlock` carried
`(Type, Text)` and a comment saying "we only emit/consume the text variant for now".

**Data only, no `uri`** — read out of the adapter's own `dist/acp-agent.js` rather than inferred from
the spec:

```js
case "image":
  if (chunk.data) content.push({ type:"image",
    source:{ type:"base64", data: chunk.data, media_type: chunk.mimeType }});
  else if (chunk.uri?.startsWith("http")) …
```

So `{type:"image", data:<base64>, mimeType}` becomes a genuine Anthropic image block, and a `file://`
uri would be ignored there while its handling elsewhere is unproven. **And all three backends were then
confirmed to FORWARD the block rather than merely advertise it** — measured in Visual Studio, 2026-08-10,
on Kiro v2, Kiro v3 and Claude Code. That distinction is worth keeping: a handshake capability is a claim about the adapter, not
evidence that the bytes reach the model, and nothing offline could have closed it. One `ContentBlock` record serves
both shapes only because the formatter is `WhenWritingNull`; lose that and every text block starts
shipping `"data":null,"mimeType":null`. Pinned by `AcpImageAttachmentTests`.

**Ctrl+V, not Alt+V.** The terminal CLIs bind Alt+V because Ctrl+V structurally cannot work for them:
the terminal emulator consumes the key (or readline claims it as `quoted-insert`), and a pty carries
bytes rather than clipboard formats, so a TUI can never *receive* an image by being pasted into — Alt+V
is really "go read the OS clipboard yourself". None of that binds WPF, where `DataObjectPastingEventArgs`
hands us the whole `IDataObject`. Alt+V would also lose in devenv, where Alt is the menu-bar access key
and Alt+V is View.

**The paste is owned by the Paste COMMAND, and `DataObject.Pasting` is not a substitute.** A screen capture puts **no text form
on the clipboard at all** — Snipping Tool publishes exactly `Bitmap, System.Drawing.Bitmap, PNG`,
measured on the real clipboard — and WPF's `TextBoxBase` looks for a format it can apply, finds none, and
therefore (a) answers `CanExecute=false`, which greys the context menu's Paste out, and (b) **returns
without ever raising `DataObject.Pasting`**. A handler on that event is never invoked for precisely the
case the feature exists for: Ctrl+V does nothing, and the menu item is disabled. Confirmed directly —
a bare `TextBox` with a pasting handler, `Paste()` called against that clipboard, prints
`Pasting DID NOT FIRE`.

**A check that supplies a text form alongside the PNG passes against a build that does nothing.** A
synthesized `DataObject` carrying a PNG *and* a `UnicodeText` path matches an Explorer file copy or a
browser image copy, not the capture tool this feature exists for, so **the harness supplies the very
thing that makes the code path reachable**. It is the lesson of the zoom checks in
[verification.md](verification.md#the-automated-runs-must-not-touch-the-users-real-config), in a new
place: when something works under test and not in the product, suspect what the harness provides. A check that cannot drive WPF's own
decision about *whether to raise a paste at all* has to assert the mechanism instead — the smoke now
fails if the `ApplicationCommands.Paste` binding is missing, which is the only sub-assertion that moves
when the bug is reintroduced (every other one still passes).

A `CommandBinding` on `ApplicationCommands.Paste` covers Ctrl+V, Shift+Insert and the context menu in one
place. Its `CanExecute` uses the **cheap format-presence** `ClipboardImages.HasImage` and never a decode:
`CommandManager` re-queries it on essentially every focus and input change, so decoding a multi-megabyte
screenshot there would be paid for over and over to grey a menu item in or out. Saying yes there and no
at `TryRead` is the harmless direction — the paste does nothing — while the reverse would grey out a
paste that would have worked. `Executed` takes the image first, then falls through to `InputBox.Paste()`,
which is the implementation rather than the command, so there is no re-entrancy and
`DataObject.Pasting` still fires for the terminal-text cleaning.

**The image is still taken BEFORE the text paste, and that ordering is the feature.** A *mixed* clipboard
does carry both — Explorer publishes the file's path, a browser the page's markup — so pasting text first
drops a path into the box and discards the picture. `Ctrl+Shift+V` remains the escape hatch from the
terminal-text *cleaning*, and bypasses the command deliberately.

**A fully transparent DIB is an UNSET alpha channel, not transparency.** The DIB fallback is not just
lossy, it can be *silently* lossy. Measured on a real **ZoomIt** capture: it
publishes `Bitmap, System.Drawing.Bitmap, DeviceIndependentBitmap, Format17` and **no PNG**, so it takes
the fallback; WPF hands the DIB back as `Bgra32`, and of its 602,847 pixels **0 had alpha set and every
single one had colour**. The picture was entirely present in the RGB channels and entirely invisible —
`TryRead` succeeded, produced a valid 101 KB PNG, and that PNG was blank. **Worse than refusing the
paste**, because a blank picture is what reaches the agent, and neither the user nor the model has any
way to tell it was ever a screenshot.

`RecoverUnsetAlpha` drops the channel when **alpha is nowhere set AND colour is somewhere set**. Both
halves matter: the first alone also matches a genuinely empty image, where there is nothing behind the
transparency and dropping the channel turns blank into black — no better, and a claim about content that
is not there. One opaque pixel anywhere proves the channel was written, and from there the transparency
belongs to whoever authored it. It runs **before** the downscale, because a resampler is free to
premultiply and would fold the colour away against the zero alpha. Row by row with an early exit, so an
opaque image returns on its first pixel and only the defect being corrected reads the whole image, one
row's buffer at a time.

Two general points worth keeping. **"It decoded" is not "it worked"** — every check here passed on the
blank output, because they asserted a decode succeeded and bytes came back, and none of them looked at a
pixel. And the *reason* the alpha comment above was too weak is that it described the DIB as losing
transparency (true, and harmless for an opaque screenshot) rather than as producing an image that is
uniformly invisible (fatal). `TheWholeDibPathProducesAVisibleImage` now asserts opacity end to end.

**Format priority, never `Clipboard.GetImage()`.** That convenience goes through `CF_DIB`, which drops
alpha — a Snipping Tool capture with rounded corners or a shadow comes back with those pixels filled.
So: the registered `"PNG"` format first (Snipping Tool, Chrome, Firefox, Paint.NET, Greenshot, ShareX
all publish it), then `FileDrop` (read from disk, so a JPEG stays a JPEG rather than being re-encoded
larger), and the DIB last. **Bytes pass through untouched** unless the image exceeds 1568px on its long
edge (Claude's own effective resolution ceiling, above which tokens buy detail the model cannot resolve)
— a screenshot is already a PNG and re-encoding it would cost fidelity for nothing. **The media type
comes from the magic bytes**, not an extension or the clipboard's claim: it is what the backend is told
the bytes *are*, and a wrong one is rejected by the model API far from where it started.

**Written at SEND, not at paste**, and **the transcript stores a PATH, never the bytes.** A paste the
user thinks better of leaves no file; and the session log is re-read in full whenever the history picker
enumerates, so inlining megabytes of base64 per prompt would be paid for on every open, forever, to draw
a thumbnail. The directory is flat under `%LOCALAPPDATA%\code-wicket\attachments` with the
conversation id leading the filename — **not** subdirectories, because `RetentionSweep` is non-recursive
by design and a nested layout would be swept at its top level only, i.e. never. Content-addressed, so
re-sending writes nothing new. `AttachmentStore.RedirectTo` exists for the same reason
`ExtensionConfig.RedirectTo` does: a self-check that pastes an image would otherwise scatter test
pictures through the developer's own LocalAppData.

**The capability gate is published by the session and enforced by the HOST**, because the host is the
only layer that can do anything useful when the answer is no — it has the fallback (name the saved
path). Re-checking in `AcpAgentSession` would only turn a message the user believes they sent into
silence. And the answer is not known until a session has opened, so a paste is never *refused* on it;
the check happens at send. The degraded route is the manual workaround the issue describes, automated:
an `<attached-images>` block naming the files. **Both degradations are visible** — the
no-image-support notice once per session (it is a property of the backend), the couldn't-save error
every time (it is a property of the message). The one unacceptable outcome is a message reaching the
agent looking as though it carried the image when it did not.

**An image on its own is a whole message.** Paste, Enter. Requiring a word to justify the picture would
be a gate with nothing behind it — so the send gate became `HasSomethingToSend`, and all three Enter
gestures refresh together (`RaiseSendGateChanged`): refreshing only Send is how Ctrl+Enter ends up
disabled on a message Enter would have taken.

**Attachments belong to the MESSAGE, not to the tray.** A held mid-turn message carries its own images
and shows them in the tray, so removing one held message cannot take another's picture with it — the
release *point* is tray-wide, the content is not. A released batch joins them the way it joins the text,
because a batch is delivered as one message. Attachments ride a steer too: the comment on `SteerParams`
explains why the workspace-context block does not (the turn already paid for that snapshot), but an
attachment is the user's own content and belongs to the message carrying it.

**A sent attachment is detached from the composer.** `TakePendingAttachments` returns copies without the
remove command, because a × still shown in the tray or the transcript but silently doing nothing is
worse than no ×.

**`acp.log` needed a guard before any of this could ship.** The tee logs both directions raw, and the
log directory was measured *at* its 50 MB budget with 705 files — so one screenshot per prompt (twice
over, since `session/load` replays every image the user ever pasted) would evict the diagnostics the
file exists for. `Core.FrameLogRedaction`, which `FrameLogSink` writes through, elides a `data` value
that is a long unbroken base64 run, **keyed on the value's shape rather than the key name** ("data" is
generic enough to appear elsewhere, and a short one is worth keeping), and **says how much it dropped**
— a bare marker would leave a reader unable to tell a thumbnail from a 4K capture, which is the question
the log gets read for when a prompt is unexpectedly large. The read direction gained the write
direction's frame buffering to make this possible, with the same trailing-partial flush on dispose so it
stays lossless at teardown.

**The exported transcript carries the images, and the two consumers want different forms.** The export
failed the feature's own rule twice: a message with text kept the text and silently lost the picture,
and an **image-only message was dropped entirely** — `RenderMessage` returned null on empty text, so the
whole turn vanished rather than just its image. Emptiness is not enough to drop a message once an image
alone can be one.

`ImageExport.Embed` (the **exported file**) inlines a `data:` URI, because the user picked a location for
that file and means to keep or send it: a link into their own LocalAppData is portable to nobody and is
reclaimed by retention within days, so an export built from links is an artifact that silently rots —
the same class of failure as an image that pastes blank. `ImageExport.Link` (the **clipboard**, "Copy
all") links instead, because that text is pasted into an issue or a chat where megabytes of base64 are
hostile, and a shorter life is the right trade for a payload that lives until the next paste. A budget
caps embedding across the document and **says what it dropped**.

Either way it is a markdown image with the file's name as **alt text**, so that where it cannot render —
GitHub does not resolve `data:` URIs, and a `file://` link resolves nowhere but this machine — the reader
is still told a picture was there and what it was called. **A link is only emitted if it resolves** (a
stat, not a read): a broken link is worse than a sentence, because it looks like the picture ought to be
there. And the two absences are reported as different facts — a recorded path that no longer resolves was
swept by retention, while no path at all means the save failed when the message was sent.

**Resume and hand-off carry images by two different routes, and only one of them was working.** A
**full** resume is fine untouched: `session/load` replays the BACKEND's own history, which received the
image as a content block, so the agent's memory of it survives regardless of what our attachment
directory still holds. A **summary** resume is the opposite — the recap is built from our saved log by
`TranscriptText`, which read `entry.Text` alone, so a screenshot plus "fix this" summarized as
`User: fix this` and an image-only message as a blank `User:` line.

That is the export's defect on a path where nobody can see it, and it is worse for one reason: **the
reader is not a person.** A human reading an export notices a chip is missing; an agent reading a recap
just reasons confidently from an account with the subject removed. It also bites exactly where the
summary matters most — the summary route is what carries a conversation onto a DIFFERENT backend, so it
IS the hand-off, and this text is the whole of the new agent's knowledge of what came before.

The marker names the file and gives its **path while it still resolves**, so a capable agent can go and
read the image rather than merely being told one existed — the same move as the no-image-support
fallback, and for the same reason. Where retention has reclaimed it the loss is stated rather than
implied, because "there was an image and it is gone" is a better starting point than silence, which
reads as "there was none".

**A path only exists once something resolves one, and this marker stopped doing that silently.** The
transcript records the attachment's NAME rather than a path — `AttachmentStore.StoredForm`, so the
directory can move without stranding every image ever pasted, as it did at the rename — while the
marker went on statting the stored value directly. `File.Exists("handoff-1a2b….png")` resolves against
the process's working directory, devenv's install folder in the VSIX, so every image still sitting in
the store was handed over as *no longer available*: the exact claim this block exists to prevent, made
about a file that is right there, on the one route whose reader cannot notice. It resolves through
`AttachmentStore.ResolveStored` now, the same call the view-model's restore makes.

**Two things about how it survived are the reusable part.** The comment on `RestoreAttachments` called
itself *the* boundary where a stored entry becomes a resolved path and listed this route among its
downstream consumers — and it never was one: `TranscriptText` walks the PERSISTED log, not the
view-models, so a second reader of a stored entry exists and has to resolve for itself. A comment
asserting coverage it does not have is worse than none, because it answers the question that would
have found this. And **every fixture in `SummaryHandoffImageTests` recorded an absolute path**, which
was the truth right up until the store began recording names — so the suite went on asserting a shape
nothing writes, passed, and covered nothing. A test supplying the input production no longer produces
cannot fail for the right reason: #118's own clipboard lesson — the harness supplying the very thing
that made the path reachable — one layer over.

**What is deliberately NOT attached: files.** The agent can read those itself, and reading gets it the
*current* bytes rather than a snapshot that goes stale the moment it edits them — issue #95's rule
(ambient context carries what the agent cannot get by asking) applied to the composer. Embedding would
not even be portable: Kiro v2 advertises `embeddedContext:false`, while a path in the prompt text works
on every backend. The narrow reason images are the exception is that **a pasted snip has no path
to reference** — there is nothing to name.

That reason does not extend to *every* image. It is tempting to say an image already on disk needs no
attaching either — naming the file is plainly right for a drop — but that asserts something not
measured: whether a backend's own file read hands the MODEL pixels, as opposed to bytes it cannot see,
is per-backend and not established here. A path is the right answer for a file the agent reads *as text*; an image block is the only route
that is known to reach the model as a picture on all three backends, because that was captured. So the
gesture decides, and the user's gesture is the evidence: Ctrl+V and the picker say *look at this*, a
drop says *here is a file*.

### Picking the file instead (Add ▾ → Image…)

The same payload by a second gesture, asked for because a paste is only reachable once the picture is
already on the clipboard. It ends in `ChatViewModel.AttachImage`, exactly where a paste ends, so a picked
image and a pasted one are one chip, written to disk at one moment and put on the wire by one code path.

**It groups with the paste, not with the drop**, and that is the line a reader has to have: dropping a
file transcribes its PATH (#62), while Ctrl+V and the picker attach the BYTES. Same `.png`, three
gestures, two outcomes — set out under "An image FILE is a path like any other file" below, and in the
user docs as a table ([Using the chat](../using-the-chat.md#pointing-at-a-file-and-attaching-a-picture-are-different-things)). The user-facing wording of the split is *a path stays
current and a picture cannot*.

**The read is shared, not parallel.** `ClipboardImages.TryReadFile` is lifted out of the `FileDrop`
branch the clipboard path already had — a copied file in Explorer has always come in this way — so the
picker sniffs, decodes, downscales and refuses by the same rules as everything else here. *A different
gesture is not a different payload*: the alternative, a picker with its own read, is a second answer to
"what is an attachable image" and the two would drift on the first change to either.

**It is NOT a `ChatContextSource`, and that is a boundary rather than an exception.** A source produces a
`ChatContextCapture` — a text block riding the prompt inside a `HostPromptBlocks` tag — and this produces
an attachment: different payload, different chip, different wire shape. Putting it through the registry
would mean widening every source's contract for one item. The registry's own rule (issue #73's rung 2, where the user hands IDE context to the agent) is
that *two paths producing the same chip is two places to keep in step*, and that rule is honoured here by
the picker sharing the paste's path to the chip, not by pretending an image is a capture.

**The item heads the menu and is never disabled.** Attaching a file needs no IDE behind it — it is
Ctrl+V's peer, not the debugger read's — so it is offered by every host, and with it the `Add` button
and the composer strip above the message box are drawn unconditionally. Both used to be bound to
`HasContextSources`, which was right while the menu could legitimately be empty; `HasComposerStrip` is
retired rather than left as a property that is always true.

**A refusal is reported.** The dialog's filter is a hint — the user can pick "All files", and an
extension lies — so the magic bytes decide, and a pick can legitimately yield nothing attachable. Saying
nothing would leave a message that LOOKS as though it carries the picture, which is the one outcome this
issue rules out; the files that did attach are kept, because dropping those too would punish the good
half of a multiple selection. `Multiselect` is on, each file taking the read individually.

**What is checked, and where.** The read is pure and unit-tested (`AttachmentTests`: a picked PNG passes
through byte for byte, a PDF named `.png` is refused, and a missing, empty or null pick is refused rather
than thrown). The rest cannot be: **the menu is built in code-behind, so dropping the item leaves a clean
build and every unit test green**, and a check may no more summon a modal file dialog than it may write
to the real clipboard. So `--smoke` counts what the handler actually built and finds the item by its
`Tag` (`ChatView.AttachImageMenuItemTag`, not its header text, which is display copy), and drives
`TryAttachImageFile` — the seam beneath the dialog, `TryPasteImageFrom`'s twin — asserting **the chip it
produced** and, separately, that a non-image is refused. Three injections, all proven load-bearing:
a picked file skipping the sniff a pasted image goes through, the Add menu built without its
"Image..." item, and a seam that always answers true — the "attached nothing and said nothing"
failure, and the one only the refusal half can see.

## Render-cost instruments (issue #86)

Three instruments built to find where streaming and scrolling spend their time — `[md-cost]` (the
markdown render), `[realise]` (container realisation) and `[dispatch]` (the dispatcher's own queue) —
and two experiments run on what they found, `CacheLength` and `BitmapCache`.

### Streaming render cost: `[md-cost]` (issue #86)

The report is that streaming a long reply is fluid on the dev PC and sluggish on a work VDI, same build,
same content. Every candidate fix targets a different cost — an adaptive throttle, a stable-prefix
incremental render, not highlighting an unclosed fence, a lower rebuild rate under a remote session —
and picking between them without a number from the machine that feels slow would be restructuring on
a theory. So the first thing built is the measurement.

**Two numbers, because the obvious one is only half the cost.**

- **`build`** — the Markdig parse and the `FlowDocument` construction. Exact: it is bounded by the render
  call, which is why it can be reported without a caveat.
- **`frame`** — assigning `viewer.Document` returns immediately; it only marks the viewer dirty. WPF
  formats and line-breaks in the **layout pass afterwards**, and on a long message that is the larger
  half. Timed to a callback posted at `DispatcherPriority.Loaded`, the first priority below `Render`, so
  it runs once that pass is done. It is a **proxy and is labelled one** — anything else the dispatcher
  ran in between lands in it too. Read `build` as exact and `frame` as an upper bound.

**`duty` is the figure to compare across machines**, and the only one that can be. Raw milliseconds are
not comparable between a dev PC and a VDI; the share of wall-clock the render path held the UI thread is,
and it is what an adaptive throttle would be scaled against. `dutyBuild` is its exact lower half, and the
two together are what separate the candidate causes: a duty gap between two machines that **vanishes** in
the build half is a statement about the dispatcher, not about our render cost. If duty on the VDI is
comparable to the PC's and it still stutters, the cost is not on our thread at all — it is the remoting
encoder shipping a screen region that goes fully dirty ten times a second, which no amount of cheaper
content fixes and only a smaller dirty area does.

**The `[render-env]` header, once per process**, carries what no per-message line can: render tier
(`RenderCapability.Tier >> 16` — 0 means software rasterization, which would change everything),
`SystemParameters.IsRemoteSession`, `ProcessorCount`, the throttle intervals in force, and the runtime
(net472 vs net10 — the harness measured the wrong one for a long time, #106). A duty cycle read
without the throttle it was measured against says nothing.

**Opt-in, in `logs/render.log`, behind `logRendering`, and the append is QUEUED.** Writing inline is
wrong: `DiagnosticLog.AppendLine` opens, writes and closes per call under a
process-wide lock, so the append's cost belongs to the machine (AV/EDR, LocalAppData on a share, VDI) —
the exact environment #86 reports from. An inline write puts machine-priced IO on the slow machine's
UI thread. Writes chain serially on `TaskScheduler.Default`, which keeps the
order a cost trace has to be read in, with `Flush` at host teardown so the last message's line is not
lost to process exit. The default stayed off regardless — the queue makes the enabled case honest, not
the always-on case free. The file and the setting are named for RENDERING rather than for markdown so
the next render investigation joins them instead of adding a second checkbox for the same question; the
per-feature prefix (`[md-cost]`) is what distinguishes traces. The setting deliberately does **not**
enable the per-render `[md-render]` trace — with both on, most of what the aggregate reports is the
other one's file IO, so the instrument that falsifies the measurement stays a developer-only variable.

**Episodes exist because a container is RECYCLED.** One viewer serves many messages, so "this viewer's
renders" is not "this message's renders". An episode closes on a quiet gap (2s, comfortably above the
150ms synchronous ceiling so a mid-stream stall on a slow machine — exactly what is being reported —
cannot split one message in two and halve its apparent cost) or when the new text is **not an extension
of the last one rendered**. Below three renders it is dropped: a restored transcript renders every
message once, and a line each would bury the streaming ones the trace exists to show.

**A "shorter than before" boundary fails under a scroll.** A growing message only appends, so shorter
does separate one message from the next *while a reply is streaming*.
Under a scroll it does not: dragging the thumb recycles one container across many messages with no gap
between them, and any two whose lengths happened to increase merged into a single episode — reporting a
`len` belonging to the last of them and a `wall` belonging to none. Nothing about the line looked wrong,
which is the worst way for a diagnostic to be wrong. An ordinal prefix test against the previous text
answers the actual question (is this the same text, still growing?), costs one comparison beside a build
measured in hundreds of milliseconds, and accepts an unchanged text — which it must, or the corrective
re-attach render and the rebuild after a file reference resolves would each split a message in two.

**The frame window no longer counts BUILDS, anyone's.** A frame is timed to a callback posted below
`Render`, so a render starting before that callback runs was counted twice — once as `build`, again
inside `frame`. It reached the field as **`duty=101.3%`**, which is the useful kind of wrong: impossible
rather than plausible. The probe now captures a **process-wide** build clock when it posts and subtracts
whatever accrued by the time it fires. Process-wide rather than per-episode because per-episode is the
wrong scope: a drag realises many containers back to
back, so the work landing in one viewer's frame window is mostly OTHER viewers being realised — a
119-char message logged `frameTotal=3786.5` against its own `buildTotal=3.3` and a `duty` of **296.7%**,
with a 17,730-char message reporting 833.6ms of builds on the line written 1ms later. The clock is only
read as a difference between two points, so it needs no reset and cannot leak between windows or tests. Still an upper bound — unrelated dispatcher work lands in it and
always will — but it can no longer be inflated by the number printed next to it. It also carries the
episode's **generation**, because under a scroll the next episode opens immediately, so "is anything
open" cannot tell a stale callback from a live one.

**An episode that only ever rendered EMPTY text is dropped** — a container being cleared on recycle is
not a message. Six of the 21 lines in the first field log were these, 29% of the output at 4ms of wall
each, crowding out the ones worth reading. The test is exact rather than a cost threshold, and the
prefix boundary is what makes it so: a text going from content to `""` is not an extension, so it closes
the real episode and opens a separate empty one — the content keeps its numbers, only the all-empty
successor is dropped.

#### What it found: `attach`, by 95–98% (2026-08-12)

The trace was built for streaming and the first thing it caught was a **scrollbar drag**. Splitting
`build` into `parse` (Markdig plus every element constructed) and `attach` (`viewer.Document = document`
alone) settled it on the first drag — the same 17,730-char message, two separate realisations:

| | build max | parse max | **attach max** |
|---|---|---|---|
| | 1132.3 | 38.2 | **1106.2** |
| | 878.8 | 42.1 | **835.8** |

Constructing every element of that message costs ~40ms. **Assigning the finished document to its viewer
costs a full second.** It carries roughly **621 resource references** — 258 inline code spans at two each
(`Chat.CodeFontFamily`, `Chat.CodeBackground`), 105 hyperlinks at one (`Chat.LinkForeground`), plus the
document's own — all registered while it is parentless, all resolved when it meets a tree. That is
**~1.8ms per reference in devenv against ~0.012ms in the standalone host**: the ChatView sits inside a
tool-window pane, under 28 `Chat.*` overrides `VsTheme` injects, above devenv's own `Application.Resources`.

**Two conclusions, one of which killed a fix that had already been argued for.** Caching built documents
is **worthless** — a realisation re-attaches whatever it reuses, so a cache removes the 40ms and keeps
the 1000. And the lever is the references: `MarkdownText.Render` holds a viewer that IS in the tree, so
the values can be resolved once per render and assigned, turning ~621 registrations into a handful of
lookups. The cost of that is that values do not track a theme change the way references do, so
`VsTheme`'s existing `ThemeChanged` hook has to force a re-render of visible messages.

**And the offline harness could not have found this.** Its `--perf` split measures the same assignment at
**7.7ms** — the right RATIO (in-tree costs ~2.8x parentless, which is the resource-resolution signature)
and the wrong magnitude by 130x, because a standalone host has neither the tree depth nor the overrides.
Read that block for its ratios, never its absolutes. It is the same class of blind spot as #103's Exp
hive: a harness that cannot host the thing that makes the difference.

**Fixed, and measured on the same message in the same session (2026-08-12).** `MarkdownText.Render`
holds a viewer that IS in the tree, so `MarkdownPalette` resolves the seven themed keys once and the
renderer assigns values; twelve `SetResourceReference` sites became one `Themed` helper that assigns a
resolved value or falls back to a reference.

| | before | after | |
|---|---|---|---|
| `attachMax` | 835–1106 ms | **11.4–13.4 ms** | **~80×** |
| `buildMax` | 879–1132 ms | **26.4–28.1 ms** | **~37×** |
| `parseMax` | 38–42 ms | **20.2–20.8 ms** | ~2× |

Realising that message went from **~1 second to ~27 ms**. **Parse halving was not predicted** and is the
detail worth keeping: registering a `ResourceReferenceExpression` costs more than assigning a value, so
removing 621 of them made CONSTRUCTION cheaper too, not just the attach.

**Two things this cost.** A resolved value does not follow its key, so a theme change no longer repaints
an already-built document — `MarkdownText.RefreshThemedValues` re-renders the realised viewers under a
root and `VsTheme.Refresh` calls it, paid once per theme switch instead of ~621 times per message
realised. And `MarkdownPalette.Resolve` returning **null is a real answer**: a viewer rendering out of the
tree cannot answer, the renderer falls back to references, and `EnsureRerenderOnReattach` still corrects
the document. A palette of nulls instead of no palette would bake a detached viewer's non-answers in as
permanent values — the "last message goes dense" bug made incurable.

**The check that guards it could only be written one way.** Unit tests pin that the renderer USES a
palette it is handed; nothing offline pins that anything HANDS it one, and nulling that single line in
`MarkdownText.Render` leaves every offline test green while the fix does nothing. A value and a resolved
reference are also indistinguishable by the value itself — with the fix unwired the smoke still reports
`family=Cascadia Mono, Consolas`, entirely correct. `DependencyPropertyHelper.GetValueSource(...).IsExpression`
is the only thing that separates them, so `--smoke` asserts it is false on a realised message's inline
code run: **the expensive path being GONE, not the colours being right.**

**A caveat the same log carries**: `duty` still reads over 100% (220.7% on a 119-char message whose own
`attachMax` was 2.3ms), because the build clock can subtract other viewers' BUILDS and not their LAYOUT.
`frame` and `duty` are upper bounds by construction; `parse`, `attach` and `build` are the trustworthy
numbers, and they are the ones that answered this.

**Know what the floor costs on the scroll path**: a realised container renders *once*, so a drag's
episodes are almost all dropped, and a whole drag session can produce a single line. Fixing the boundary
makes what is reported honest; it does not make a drag visible. That needs an aggregate keyed on
something other than a message.

**A limitation to know before you trust a quiet log.** `NoteRenderCost` runs *after* the render
returns, and the episode line is flushed by a `DispatcherTimer` on the same UI thread — so **for a
render that never comes back, this trace's output is silence.** It cannot see its own worst case.
That is the blindness #177 records for `TryTokenize`'s `catch` and for `MarkdownRenderFirewall`
(*a hang raises nothing*), one layer up: **in the instrument rather than in the guard**. It
generalises — **a trace that records on COMPLETION is blind to the same failure class a `catch`
is** — so naming a phase that never *ends* needs the other shape, a watchdog on a pool thread
(`Core.StartupTimeline`). Point `[dispatch]` at it instead: self-time under the throttle tick is as
direct a fingerprint as exists.

**And a trace answers only for the content it was pointed at.** The `attach`-by-95–98% figures, the
palette fix and the handoff to `[realise]` all came from realising an already-settled message under a
drag — and **in a settled message every fence is closed**, so the pathological tokenizer input of
#177 cannot occur in the episodes that produced them. Check what state the content was in before
reading a number as general.


### The trace that answered, and then went quiet: `[realise]` (issue #86)

**With the palette fix in, `[md-cost]` stopped being the interesting log.** Two field drags on the same
session (2026-08-13), 35 renders over 2027 ms and 30 over 1046 ms:

```
renders=35 len=119 wall=2027.6 duty=1005.7% dutyBuild=1.1% buildTotal=21.5 frameMean=582.0 frameMax=2014.1
renders=30 len=119 wall=1045.6 duty=1276.1% dutyBuild=1.9% buildTotal=19.5 frameMean=444.1 frameMax=1040.8
```

Markdown building is **1.1–1.9%** of the gesture and holds up under load. But `frameMax=2014.1` against a
`wall` of 2027.6 says a callback posted at `DispatcherPriority.Loaded` when the drag began did not run
until it ended: the dispatcher stayed above `Loaded` for the whole gesture. **Mouse input is `Input` (5),
below `Loaded` (6)** — so the thumb was starved behind precisely that backlog, which is the judder,
mechanically. And during those three seconds the *only* markdown rendering anywhere in the pane was that
119-char item; the 17,730-char message had last rendered 90 seconds earlier.

**`[md-cost]` cannot name what held the thread, and that is structural rather than a gap to fill in.** It
is keyed on a markdown viewer, so the tool rows a drag passes *through* contribute nothing to it by
construction, and the <3-render floor above drops most of a drag regardless. A `FlowDocumentScrollViewer`
also does not virtualise its document: re-realising a container re-formats the whole thing with no
markdown build at all, so that cost lands in `frame` — the number explicitly labelled an upper bound —
and nowhere else.

Hence a second trace, keyed on the **gesture** rather than on a message. `TranscriptItemsControl`
overrides `PrepareContainerForItemOverride` and hands out a `TranscriptItemContainer` that times its own
measure and arrange. A subclass because there is no other seam: `ItemContainerGenerator.StatusChanged` is
per batch and names no item, and a container's layout time is not observable without being the container.
With the trace off the whole of it is two static bool tests per layout pass.

```
[realise] items=54 distinct=38 repeat=1.4x wall=215.5 duty=26.3% measures=68 layoutTotal=56.6
          measureTotal=46.9 measureMax=11.2 arrangeTotal=9.8 kinds=Message:49/58/52.4/11.2,Notice:5/10/4.3/0.9
```

**`repeat` and `kinds` are the two figures it exists for.** 125 realisations over 41 distinct items is one
stretch of transcript thrashing, not a transcript being traversed — a different problem with different
fixes, and previously answerable only by inference. `kinds` then says whether the cost is one enormous
message re-formatting or the eighty small rows around it, which decides whether the lever is item height,
item count or the row template. Per-kind figures are `realisations/measures/totalMs/maxMs`, sorted by
total, with the tail **folded into `other` rather than truncated** so the named kinds always add up.

Unlike `frame`, **every duration here is exact** — timed around the container's own measure, so nothing
else the dispatcher ran can land in it. The trade is that it sees only what the transcript's containers
do, which makes a large gap between `layoutTotal` and the wall-clock a finding in its own right rather
than a gap in the instrument.

**A line existing is not the check.** Injecting a plain `ContentPresenter` in place of the timing
container still produced `items=54 distinct=38 repeat=1.4x` — realisations counted, plausible, and
`measures=0`. So `--smoke` asserts measured *time*, not the presence of a line, and `RealisationCostTests`
pins only what the numbers mean once something calls the accumulator.

#### Two wastes found by reading the templates, not the log

**An assistant message was building the plain-text control it never shows.**
`SelectableEmojiText.Text` was bound unconditionally in the message template while the control is
`Collapsed` on exactly the messages it was doing the work for. `Visibility` stops layout; it does not stop
a property-changed callback — so `Rebuild()` ran on every realisation of every reply, splitting the whole
message into inlines **twice** (the measurer and the paragraph) and assigning a fresh `FlowDocument` to a
`RichTextBox`. The binding now sits in an `IsAssistant=False` trigger, mirroring the assistant half of the
same Grid, which has always been conditional. `--smoke` asserts the text is never set on the assistant
side **with the user side as its control**, without which it would also pass on a build where the trigger
simply never fires.

### Asking the dispatcher directly: `[dispatch]` (issue #86)

**Two corrections to what is written above.** A 21-second drag logged `items=214 distinct=50
repeat=4.3x` — the panel *does* re-realise the same stretch, so the "not thrash" reading from the earlier
short sweeps was premature. And `repeat` cannot say which kind of repeat it is: a user dragging up and
down over one stretch produces the same ratio as a panel correcting its own offset, and the trace carries
no direction or offset to separate them.

**Neither correction matters, and the same line says why.** Those 214 realisations cost **397.5 ms of
layout in 21,439 ms — 1.9%**. A message realisation is 8.6 ms, a tool row 0.49 ms, and the worst single
measure in the session is 20.2 ms. Even if the panel is thrashing, thrashing is cheap.

So three candidates are excluded — markdown build (~1%), realisation churn, container layout — and the
gap is stable and reproducible:

| ~900 ms window, 17,730-char message | |
|---|---|
| `[md-cost] frameTotal` | 820.0 ms |
| our markdown builds | 78.1 ms |
| container measure + arrange | ~100 ms |
| **unaccounted** | **~640 ms** |

What remains is the panel's own measure, flow-document formatting scheduled as separate dispatcher
operations, and the render pass. `DispatcherTrace` (with `DispatcherActivity` per operation) hooks `Dispatcher.Hooks` and buckets every operation
by priority, plus by **callback** for those over 2 ms — resolved from `DispatcherOperation`'s private
`_method`, cached per method, degrading to priorities alone if a runtime renames it.

```
[dispatch] ops=815 wall=4987.2 busy=62.5 duty=1.3% unaccounted=4924.7 maxOp=9.6
           byPriority=Render:305/60.2/9.6,Background:170/2.0/0.2,Input:337/0.3/0.0
           slow=Render/App.BurnDispatcherTime:5/40.3/8.3,Render/MediaContext.AnimatedRenderMessageHandler:1/9.6/9.6
```

**`unaccounted` is the point of the line, not a leftover.** Window messages, input handled inside the
pump, and the whole render thread are off the dispatcher, so a wall-clock it cannot account for is the
reading that sends this to the compositor and the remoting encoder — #86's original suspicion, and a
different fix (a smaller dirty area, not less work). A trace reporting only what it can name would
quietly foreclose that.

**Self-time, not elapsed time**, because an operation that pumps a nested frame contains others in full;
charging it their time would push `busy` past the wall, which is the `duty=101.3%` bug this project has
already fixed once.

**Two things nearly made it write an empty log, and only `--smoke` caught them.** The flush timer's own
tick is itself a dispatcher operation, so the instrument kept resetting the idle clock it uses to decide
the dispatcher has stopped — its frame is now marked and excluded from the record while still counting
against its parent, since the thread really was busy with it. And **a dispatcher never goes quiet**: in
devenv it is Visual Studio's, so an idle-only close waits for a lull that never comes. Hence
`MaxEpisodeMs`, which also makes a long drag a sequence of windows showing what dominated when. The
symptom of both was 808 operations noted and not one line written, with every unit test green.

**The naming path is runtime-dependent and this repo ships two**, which nothing offline can check because
the arithmetic tests hand the name in as a string. `--smoke` asserts a real posted callback comes back
named, on net10 and net472 both.

#### The known limitation that turned out to be a real defect: `duty=1301.5%`

Charging an operation to the episode open when it *completed* was accepted as a limitation when the trace
was written. A second machine's field log (a laptop, 2,608 windows over four days) shows what it costs:

```
[dispatch] ops=98 wall=4981.5 busy=64835.1 duty=1301.5% unaccounted=0.0 maxOp=64773.6
           byPriority=Send:6/64778.7/64773.6,…
```

**This is the impossible number self-time was supposed to have made unreachable**, and self-time was never
the leak. An operation is noted when it COMPLETES. A long one that pumps a nested frame has its children
complete first, and those close the episode it began in and open others — so when the outer finally
completes, its whole self-time lands in whichever episode is open by then, one whose wall may span 5 s
against a 64.7 s charge. Five windows in that log report `busy > wall`, **72.2 s of excess** in total, and
the windows the block actually spanned read as idle (`duty=0.3%`, `0.4%`) because nothing completed in them.

The fix is to give `Note` the operation's **start**, and move the episode's start back to cover it:
`busy <= wall` then holds by construction — every operation's span lies inside the window, and self-times
within one window are disjoint by definition. Note that `atMs - selfMs` is **not** the start: a pumping
operation's span includes its children, which is the entire case being fixed.

**Two things fell out of it, and the second nearly deleted the finding.**

The episodes now **overlap**: one holding a long pumping operation covers the same wall-clock as the
episodes its children produced. That is honest rather than residual — the outer operation really was
running throughout — but `[dispatch]` lines no longer partition time, so totalling their walls
double-counts.

And **backdating alone would have made the trace stop reporting the very block it exists to find.** An
episode reaching back over a 64.7 s operation trips `MaxEpisodeMs` on the next operation, so it closes
holding one or two — under `MinOperations`, and dropped. Going from impossible arithmetic to silence is
strictly worse: a wrong number invites a second look, an absent one does not. Hence `ReportOperationMs`
(100 ms): `MinOperations` exists to suppress an idle dispatcher's timer ticks, and **those are cheap by
definition**, so the test that separates "not a gesture" from "a gesture too short to count" is cost, not
population. Both halves are pinned by tests verified against injection — restoring the old episode start
fails the span test, removing the cost test drops the lone-operation line.

**A span test can pass against the unfixed code**: a nested burst landing more than `EpisodeGapMs` before
the outer operation completes forms its own episode, so the overlap it was written for never occurs. The harness has to put the child operations *within the gap* or
it is testing nothing, which is the same shape as the `realised>0` check that was reading the previous
phase's residue.

#### What it answered on the first drag

```
busy=776.5   duty=15.6%  slow=Render/MediaContext.RenderMessageHandler:2/689.8/643.7
busy=2919.4  duty=59.4%  slow=Render/MediaContext.RenderMessageHandler:15/2072.6/666.8
busy=3297.5  duty=72.1%  slow=Render/MediaContext.RenderMessageHandler:7/2433.8/1339.7
busy=1776.3  duty=35.5%  slow=Render/MediaContext.RenderMessageHandler:1/1169.3/1169.3
```

**25 render operations, ~6.4 s, inside an 11.3-second drag — single passes of 643, 667, 1170 and
1340 ms**, plus ~1.44 s of `AnimatedRenderMessageHandler`. `MediaContext.RenderMessageHandler` is layout
*and* render-data generation, and the `[realise]` line puts our containers' measure and arrange inside it
at **348.7 ms — about 5%**. The rest is WPF regenerating render data for the visual tree, which no
measure timer could ever have seen. Second place is `VirtualizingStackPanel.<InitializeViewport>` at
~718 ms, an order of magnitude behind.

**A caveat this trace cannot resolve**: `RenderMessageHandler` covers both generating render data and any
wait on the compositor. `tier=2 remote=False` argues against a starved compositor on this box, but on the
VDI the issue was reported from, the same line could mean the other thing.

**It also rehabilitates the realisation finding, with a caveat.** "Layout is only 3%, so churn doesn't
matter" was incomplete reasoning: re-realising a container replaces its content, which forces WPF to
regenerate that container's render data — and render-data generation is the ~6.4 s. So `repeat=2.8x`
plausibly *is* driving the dominant cost through a path the layout numbers don't show. Not confirmed: the
drag logged one `[realise]` covering 11.3 s against four 5-second `[dispatch]` windows, so nothing could
be correlated. Both traces now use the same 5 s window, which makes it a comparison next time. Note this
also undercuts the earlier "`CacheLength` measured flat" rejection — that was measured as *layout* time.

#### An animation nobody stopped

The idle windows are the giveaway: `Render:613 ops` per 5 s at `duty=0.6%` — **~120 render operations a
second on a pane doing nothing.**

The typing-indicator dots began on `FrameworkElement.Loaded` with `RepeatBehavior="Forever"` and were
never stopped. **Hiding an element does not stop its animation clock**, so from the moment the chat view
loaded WPF ran its animated render loop for the life of the window. Invisible at idle, where a pass with
nothing dirty costs 0.04 ms; during a drag those same forced passes each carry the whole dirty transcript.

Now started and stopped from the code-behind on the panel's own `IsVisible` — which is a property of the
whole ancestor chain, so the dots also stay still behind the collapsed working bar. Keyed on `IsVisible`
rather than on the view-model so it cannot drift from the `MultiDataTrigger` that does the hiding.
**`Remove`, not `Stop`**: Stop halts the clock but leaves the animation attached to the property, still
overriding the style's own opacity and still reporting `IsAnimated`.

**The check needed three tries, and each failure is the same lesson.** Reading the resting state and
calling it "hidden" failed because the fake backend is mid-turn at that point, so the dots were
legitimately up. Forcing the panel visible measured nothing, because `IsVisible` is false while any
ancestor is collapsed — the wiring behaving correctly, so the harness has to satisfy it rather than route
around it. And asserting opacity would prove nothing either way; `ValueSource.IsAnimated` is the only
thing that separates a running animation from a number that happens to match.

**One more instance of the observer keeping its subject alive.** The `--smoke` wait loop polls with
`await Task.Delay`, and every continuation is itself a dispatcher operation — so it refreshes the idle
clock it is waiting on, exactly as the flush timer's own tick did one level down. The idle close is
unreachable from that poll by construction and `MaxEpisodeMs` is what delivers, so the deadline has to
cover the window. It had been passing on luck at the boundary, and failed four runs out of four the
moment the typing-dots fix removed the ambient traffic that was nudging it over.

#### The wheel run, and why per-window aggregates had to stop

The mouse wheel reproduces it too, and it produced the clearest line in the whole investigation:

```
ops=771 wall=4985.1 busy=1416.8 duty=28.4% maxOp=1342.8
        slow=Render/MediaContext.RenderMessageHandler:1/1342.8/1342.8
[realise] items=8 distinct=6 repeat=1.3x layoutTotal=50.5 measureMax=19.6
```

**One render pass of 1,342.8 ms against eight realisations** — and another window with 1,483.5 ms. So
"~26 ms per realisation" was an artefact of averaging: a single pass can cost a second and a half on its
own. Meanwhile other windows show 66 passes averaging 18 ms. The distribution is bimodal, and only the
monsters are felt.

That also explains why a wiggle saturates at `duty=86–92%` with `unaccounted` collapsing to 8–14%: on
this machine the UI thread genuinely is the bottleneck, and it is inside WPF's render pass. Not the
compositor, not the remoting encoder — at least here.

**Five-second windows cannot go further.** The remaining question is about ordering inside a window: does
a monster pass follow a particular realisation? The symptom as described — "it kicks in when it loads more
stuff" — is exactly that claim, and no aggregate could confirm or refute it. Hence `[render-pass]`: every
WPF render pass drains a counter of what the transcript realised since the previous pass, and any pass
over `ReportPassMs` writes a line carrying it, plus the largest container measure among them — which is
how the one enormous message identifies itself (~20 ms against ~0.5 ms for a tool row).

```
[render-pass] ms=1342.8 realised=8 kinds=Message:4,Tool:4 maxMeasure=19.6
```

Every pass drains, not only the reported ones: draining on slow passes alone would silently redefine the
count as "since the last slow pass". `ReportPassMs` is a **field, not a const**, because `--smoke` has to
lower it — a standalone host cannot produce a 100 ms render pass on demand, and the honest options were a
test seam or a threshold chosen to suit the harness. The check asserts a pass whose `realised=` is
non-zero, since a presence check alone passes on a build where the counter is never fed (verified by
injection: `realised=0 kinds=none`).

**A pass too cheap to report CARRIES its counts forward — and for a while it dropped them.** Draining on
every pass is right, but the drained value was then discarded whenever the pass came in under
`ReportPassMs`. So realisations, then a trivial pass, then an expensive one produced `realised=0` on the
expensive one — which reads as "this 1342 ms pass followed no realisations" and is a **false negative
pointing away from the very hypothesis the trace was built to test**. A reported line now carries the
folded-forward figures beside its own, so the attribution stays exact (they did not happen immediately
before this pass, and the line says so) while nothing is silently lost:

```
[render-pass] ms=1342.8 realised=0 kinds=none maxMeasure=0.0 carried=8 carriedPasses=3 carriedMax=19.6 carriedKinds=Message:4,Tool:4
```

Emitted **only when something was folded**, not merely when cheap passes happened: in the field
`ReportPassMs` is 100, so most passes are cheap and a `carriedPasses`-based gate would put four extra
fields on nearly every line and bury the case worth finding.

**Found through the check, which was itself wrong.** `--smoke` asserted `realised>0` against realisations
left over from the earlier `[realise]` phase — the dispatch check realised nothing of its own — so
whether it passed depended on nothing but whether a sub-threshold pass slipped in between and ate them:
**4 consecutive failures, then 12 consecutive passes, on the same commit**. The check now brings its own
items and realises them *inside* the traced window, and accepts `realised` **or** `carried` (they are the
same fact for it — the realisations reached a line rather than being dropped). The arithmetic is WPF-free
and unit-tested (`RealisationsSinceRenderTests`, verified by injecting the discard: 4 of 6 fail); the
wiring stays `--smoke`'s, verified by removing the `NoteRealised` call and watching it fail. The lesson
generalises past this trace: **a check that reads state produced by an earlier phase is testing that
phase's residue, not its own subject** — and it fails on a schedule nobody can reproduce.

#### The answer: render cost is independent of realisation

103 measured passes, split on whether anything was realised since the previous one:

| | passes | mean ms | max ms |
|---|---|---|---|
| `realised=0` | 56 | **376.0** | **2114.6** |
| `realised>0` | 47 | **385.2** | 1672.2 |

**Identical**, and the most expensive pass in the whole log realised nothing at all. So the cost belongs
to what is already on screen being re-rendered as it moves, not to new content arriving — which kills the
realisation hypothesis outright, and with it `CacheLength`, which only changes when things realise.

**Read with the discard above in mind — the conclusion survives, one sentence of it does not.** That
split was measured before cheap passes carried forward, so a `realised=0` row means "nothing realised
immediately before this pass **or** a sub-threshold pass ate them", and the `realised=0` bucket is
therefore contaminated with passes that realisations did precede. The **table's** conclusion is
unaffected: reclassifying rows between two buckets whose means are already 376.0 and 385.2 cannot
manufacture a difference, and it is the *absence* of a gap that carries the argument. What is not safe is
the single strongest sentence — *the most expensive pass realised nothing at all* — because that is a
claim about **one** row, and one row is exactly what a discard can flip. Treat it as unproven rather than
false; `carried=` makes the same split cleanly measurable on the next field log, and re-running it is the
cheapest way to close this. The broader pattern holds regardless: **an instrument that can lose data
silently will lose it in whichever direction you are least likely to check.**

**The pattern to watch for:** an aggregate shows a correlation, and the correlation does not survive
being measured at the resolution of the thing it claims to explain. An instrument built to test the
hypothesis settles it; another round of field data only adds another aggregate.

The corroborating half is the symptom itself — *smooth scrolling all the way up, jerky on the way
back down* — because the second traversal has the same items, the same sizes and the same content as the
first. Nothing about the transcript differs; only accumulated state does. Hence `gc=g0/g1/g2` and
`heapMb` on each reported pass: a 2,114ms pass that rendered nothing new has to be explained by something
that is not work, and a collection inside it would say so outright.

#### Links before Text, and the order is load-bearing

A user reported filenames flashing plain white during a scroll and turning blue after a pause. That is
the resolver working as designed — the renderer never touches the disk, so a reference renders as plain
text and the document is rebuilt once resolution lands. But it also exposed something else.

`AppendReference` resolves through `_links`, and **with no link context it resolves nothing**: every
reference renders as plain text whether or not its answer is already cached, and nothing is even recorded
as pending. The transcript's style set `md:MarkdownText.Text` before `md:MarkdownText.Links`, so the
first render built the entire document without links and the second rebuilt it with them ~100 ms later —
two separate dispatcher beats, so **two full render passes**, and on the large message each one is its
own second-long pass.

Reversed, the first render has no *text* yet, so it costs an empty document instead of a wasted one.

**This is why `input:2` was mispriced.** It was dismissed earlier at "~26 ms, and only on the largest
message" — measured as build time, ignoring the render pass each of those builds drags behind it. The
right figure was potentially two monsters where one would do. The lesson generalises past this fix: after
the palette work, *build* time stopped being a useful proxy for anything.

#### A resolution that changes nothing must not rebuild

The resolver calls back whether or not a reference resolved, deliberately — "so a caller waiting to
re-render is never stranded". But a reference known not to resolve renders as plain text on the rebuild
exactly as it already does, so that rebuild reproduces the document it replaced. Whether the answer
*matters* is the caller's question, not the resolver's, so the test lives in `MarkdownText`:

```csharp
if (links.Resolver.TryGetResolved(key, out var full) && full is not null)
    QueueRender(viewer, StateOf(viewer), RenderCause.Resolve);
```

Not a heuristic — the documents are identical by construction. Pinned with its control, because a skip
that never rebuilds would pass the first test while file references silently stopped becoming links.

### `CacheLength` and `BitmapCache`, as experiments rather than fixes

`ExtensionConfig.TranscriptCacheLengthPages` is config-only, null by default, applied at chat-window
construction. It exists so a value can be A/B'd in the field without a rebuild per value.

The reasoning it is being tested against: if the cost is one enormous item's render data being
regenerated when it re-enters view, a cache margin is the cheapest way to stop it leaving. The earlier
rejection — "0/1/2 pages measured flat" — was measured as **layout** time, which is ~20 ms of a ~1,300 ms
pass, so it never tested this.

**Two correlations did not survive being measured at finer resolution.**
Realisation count correlated with render cost at r ≈ 0.97 across windows where drag intensity varied, and
then failed completely inside a single gesture where intensity was constant (48–65 realisations flat
against a 1.8× swing in render cost). And "≈26 ms per realisation regardless of row kind" held for two
drags and broke on the third at 15.7. The per-pass trace exists because both of those were aggregates
answering a question about ordering.

**The double theme refresh, found by `causes=` and fixed.** Every viewer realised during startup logged
`causes=input:2,theme:2`. `VsTheme` hooks the classification format map as well as `ThemeChanged` —
correctly, since Fonts & Colors edits and the editor's own late reaction to a theme switch raise only the
former — and that late reaction fires during startup, so the whole transcript was re-rendered against
values identical to the ones it had just been built with. This is the palette fix's bill being paid for
nothing.

The gate is `MarkdownPalette.Matches`, in `RefreshThemedValues` rather than in the host: the trigger is
"the themed values changed", which is a fact about the palette, so a host that grows a second hook should
not have to rediscover this. **Compared by VALUE, and that is the whole difficulty** — `VsTheme.SetBrushes`
blends and allocates a fresh `SolidColorBrush` per key on every refresh, so reference equality reports
"changed" every time and the gate would never close. Conservative where it cannot tell: an unrecognised
`Brush` subclass and a null palette both count as different, because a wrong "same" leaves the transcript
in the old theme (a visible bug) while a wrong "different" costs one re-render nobody sees.

**`renders=3` is the modal episode in the entire field log, and nobody could say which three.** A
realisation of the 17,730-char message costs ~80 ms of building where one build is ~27 ms. The candidates
— `Text` and `Links` each scheduling, a file reference resolving, the corrective render after a detached
realisation — are indistinguishable from outside and suggest different fixes, and two of them would be
legitimate. So the line now carries `causes=input:N,resolve:N,reattach:N,theme:N` and the next drag named
them: **`input:2` on every single episode**, with the third render being `resolve`, `reattach` or `theme`.

**And the answer argued against the fix.** The two input renders are not one value set twice — they are
`Text` and `Links` arriving as two separate property changes, each a genuine change. Suppressing the
second means coalescing two property sets into one render, which collides with the documented rule that
the first paint is synchronous because several callers read `Document` immediately after setting text.
The saving is ~26 ms and only on the largest message in the transcript (a typical row saves well under
1 ms), against a ~570 ms per-drag-second gap that neither instrument can see. **So it stays** — measured,
understood, and not worth the scheduling contract. The earlier framing of it here as a "provable
duplicate" was too strong: the output is redundant, the cause is not.

**Nothing on the render path can close an episode** — the last render of a message is indistinguishable
from the next one not having arrived — so the close is a timer and only a timer. It ticks at a fixed
interval and stops itself when nothing is open, rather than being restarted per render: ten timer
restarts a second is exactly the sort of cost this trace exists to avoid adding.

**The arithmetic is WPF-free and unit-tested; the mechanism is checked by the smoke, and it has to be
both.** `MarkdownRenderCost` needs no dispatcher and no visual tree, so the percentile, the duty over a
zero wall, the episode boundaries and the exact log line are pinned by `MarkdownRenderCostTests` (each
verified load-bearing by injecting its bug). But three of the four moving parts live in WPF and would
each fail *silently*: the sink never consulted from the render path, the post-layout probe never running
(a wrong priority would simply measure nothing), the idle timer never closing the episode. A build with
any of those broken writes an empty log and passes every offline test — the shape of issue #118, where a
self-check asserted the arithmetic of a paste WPF never raised. So `--smoke` streams a message into the
live transcript and asserts a line comes out with plausible numbers: renders above the floor, frame
samples present, non-zero wall and build totals.

A lone stall reaching `max` but **not** `p95` is pinned deliberately, because it looks like an off-by-one
and is not: one sample in twenty *is* the top 5%, so a percentile cannot separate it. That is why `max`
is reported beside `p95` rather than being left to it — "fixing" it would make `p95` a second `max`.

Dev-PC baseline, from the smoke's own line (small message, 9 renders, ~1s): **duty 3.7–6.3%**, build mean
~1.1ms, frame mean 3.0–5.9ms. Frame already dominates build 2.7–5×, on the machine that has no problem.

## Dropping a file on the chat (#62)

**Left to WPF's defaults**, dropping a file on the **input box does nothing**, and dropping it **anywhere
else in the chat opens the file in Visual Studio**. Neither is a bug — both are WPF working exactly as
designed, and the asymmetry between them is what the fix is built on.

A `TextBox`'s editor accepts only *text* formats, so a `FileDrop` is rejected outright and the drop
appears to do nothing at all. And nothing in `ChatView` set `AllowDrop`, so everywhere else the drag
routed past our content entirely and was handled by VS on the bubble above us.

**That asymmetry is also the evidence for the fix.** It is only consistent with one topology: WPF's OLE
target live in devenv's element tree (so the drag reaches routed events at all), the TextBox marking the
drag handled while refusing the format, and VS's own handler sitting on the **bubble**. Had VS handled it
on a `Preview` above us, it would have opened the file over the input box too — and it did not. So
`e.Handled` on a tunnelling handler at our root is enough to stop it. That is an inference from a
symptom rather than a reading of VS's source, and it held when measured in Visual Studio 18.9.1 ("Measured
in Visual Studio", below).

### One handler pair, on the root

`AllowDrop="True"` plus `PreviewDragEnter`/`PreviewDragOver`/`PreviewDrop` on the **ChatView root**. The
root is in the route for a drop over *any* descendant, so one pair covers the transcript, the header, the
banners and the composer; `DragEnter` shares the `DragOver` handler so the first frame shows no stale
cursor. `AllowDrop` is **not** an inherited dependency property, so this rests on WPF raising the
tunnelled events for a hit on a descendant that lacks it — the canonical "set it on the window" recipe.
Drops over each region have not been measured one by one (below); if one turns out dead, the contingency is three more attributes (`TranscriptItems`, the header
`Border`, the composer `Border`), with no handler duplication because `Preview` still tunnels through the
root.

Nothing here calls an OLE API. WPF's drop target is per-`HwndSource` and the VSIX creates none — our
content is handed to a `WindowPane` inside devenv's own tree — so there is no second `RegisterDragDrop`
and `DRAGDROP_E_ALREADYREGISTERED` is not reachable. We are a target only, so the `DoDragDrop`
modal-loop hazards do not apply either.

### What gets claimed

`ChatView.ClaimDrop` is the whole feature as a three-input truth table:

| carries | over the input box | claim |
|---|---|---|
| files | anywhere | **Files** |
| text | no | **TextIntoComposer** |
| text | yes | **None** |

**Files are claimed everywhere, including over the box** — that is the reported bug, and a rule that
politely left the box alone would reinstate half of it.

**Text over the box is deliberately left to the TextBox**, and it costs two things to get wrong: dragging
a selection *within* the box stops moving it, and WPF's routing of a text drop through the paste pipeline
goes away. That second one matters more than it looks — **it is the only reason dragged terminal output
is cleaned at all**. `InputBox_Pasting` reaches dragged text solely because WPF raises
`DataObject.Pasting` for a text drop on the box. The moment we insert via `SelectedText` ourselves that
event is never raised, so the text branch has to re-apply `TerminalText.Clean` **by hand** — and getting
it wrong fails silently, over the drag route only.

Extracted and static for the reason `EnterGestures.For` is: written inline this shape was wrong and
untestable, and a whole branch could sit dead under a full set of passing checks. **The seam re-makes the
claim rather than trusting the handler**, because "over the input box" *is* "we were given a point" — the
same fact the handler had. Without that, a self-check driving the seam sails past the decision entirely
and reports drag-to-move working while it has been silently taken away. That is not hypothetical: the
first version of the smoke phase found exactly this, on its first run.

### The drag may not touch the disk

`DragOver` fires on **every mouse-move**. A `File.Exists` per token per move — over a UNC share, with
folder redirection, or behind EDR interception — is machine-priced IO on the UI thread during the one
gesture that has to stay smooth: the #100 / #88 bulk-IO rule, in the environment #86 is reported from. So
the claim is made on **format presence alone** (`FileDropPaths.CouldCarryFiles`) and the existence check
runs once, on the drop.

**That makes the claim optimistic, and forces a corollary that reads backwards at first:** `Drop` sets
`e.Handled` **before** the decode and keeps it set **even when the decode yields nothing**. The Copy
cursor has already promised the drop will be taken; handing it back to VS at that point opens an editor
tab immediately after saying otherwise. A quiet no-op is the honest outcome, and the `[drop]` log line is
what keeps it diagnosable rather than mysterious.

`e.Effects` is intersected with `e.AllowedEffects`: `Copy` when offered (what every chat client shows),
`Link` only as a fallback (it reads as "create a shortcut", which is not what happens), and when neither
is allowed the drag is not claimed at all rather than advertising an effect the source will not honour.

### Decoding, without parsing anything

Explorer publishes `CF_HDROP` (`DataFormats.FileDrop`), already a `string[]`. Visual Studio's Solution
Explorer may publish only `CF_VSSTGPROJECTITEMS` / `CF_VSREFPROJECTITEMS`, whose `|`-delimited layout is
undocumented enough that reading field N risks dropping a project guid and a display name into the user's
prompt as though they were files.

So nothing is parsed by position. Every format is reduced to a bag of candidate tokens, and a token
survives only if it **names something that is actually on disk**. There is no shape to keep in step with
a future VS build, and a wrong guess costs nothing — it fails to exist and disappears. The same reasoning
decodes a stream as **UTF-16 and as UTF-8 and offers both**: the wrong decoding is mojibake, mojibake is
not a file. **Folders count**, or a Solution Explorer folder drop silently does nothing.

**A relative token is refused outright.** Both "does it exist" and every later resolution would be
measured against the PROCESS working directory — devenv's install folder in the VS host, a spelling
nothing else in the product uses. This is the other end of `WorkspacePath.ForPrompt`'s contract, and this
is the layer where the fact is knowable.

### The payload is the path

The agent has tools to read a file and every one takes a path, so sending bytes would spend tokens on
something it can fetch itself, lose *which* file it was, and freeze a snapshot where reading gets the
current bytes — #95's rule, which is #118's own stated reason for the paste exception.

`Core.Ide.WorkspacePath` is now the one home for how a path is *spelled*: `Relative`/`Absolute` moved out
of the WPF library, and `IsUnderRoot` extracted, **converging three copies of the same prefix test**.
They had drifted — `TestStackFrames` handed a *relative* frame path straight to `GetFullPath` — which is
why the convergence carries its own test rather than being a tidy-up.

`ForPrompt` is the payload half: canonicalize, relativise, quote on whitespace (plain double quotes, no
escaping — a Windows path cannot contain a `"`). **Its contract binds the caller.** Given neither a rooted
path nor a root it returns the input unchanged, because the only mechanism available is `GetFullPath`,
which would silently adopt devenv's install folder and manufacture a confident, wrong absolute path —
#54, where the failure is not an error but a **different real file**, created by the write, so the edit
reports clean while the file the user meant is untouched. 

The root is `ChatViewModel.AgentPathRoot` — the agent's cwd where a session has reported one, the
solution root otherwise — the same origin the blue file links and the edit applier use, so a path the
user drops and a path the transcript shows can never disagree about where they were measured from.

**An image FILE is a path like any other file.** #118's attachment exception exists for one narrow
reason: a pasted snip has no path to reference. A dropped `.png` has one. That leaves a deliberate
asymmetry worth writing down rather than leaving to be found as a bug — `Ctrl+V` of an Explorer-copied
PNG becomes a chip, dropping that same PNG becomes a path, and **picking it through `Add ▾ → Image…`
becomes a chip too**, that gesture being the paste's twin rather than the drop's. The asymmetry is not
that one route is right: a path is what an agent should get for a file it can read for itself (#95),
while an image is bytes the model has to be handed. What decides is which of those the user asked for,
and the gesture is how they say so.

**`Add ▾ → File path…` is this same insertion with no pointer**, and the two questions it settles are
worth keeping. **Why not a chip**: a chip carrying the file's CONTENT re-introduces the staleness the
path rule exists to avoid — worse, it puts two versions of one file in a conversation with nothing
saying which is authoritative — and it would not be portable either (Kiro v2 advertises
`embeddedContext:false`), so its fallback would be *naming the path*, which is the simpler thing
entire. A chip carrying only the path was the other candidate and was rejected on cost: the block it
would ride (`<referenced_files>`) is a fifth `HostPromptBlocks` tag, and every one of those carries
documented obligations in `Strip`, `SplitLeading`, `TranscriptText` and the export — a lot of
machinery to say what a line of text already says.

**Its one genuine advantage was insertion-point independence, and that is five lines, not a block.**
A drop aims with the pointer — `InsertionIndex` exists to recover exactly that — while a menu click
has no aim, so the caret is wherever it was last left, possibly scrolled out of view and possibly
mid-word. So the picker appends at `Text.Length`, which is what a drop landing outside the box already
does, and both gestures share `InsertIntoComposer` so the padding, the single undo unit and the caret
cannot drift apart. The spelling goes through `ChatViewModel.ComposerPathFor` for the reason that is an
invariant rather than tidiness: edits dedupe on `toolCallId|path`, so a second route spelling one file
two ways splits it in half downstream. All three are pinned by injection
(the path inserted at the caret, the raw path inserted instead of the composer's spelling, and the
Add menu built without its "File path..." item) — the first two failing in the file-drop phase, where the root
fixtures that make a spelling mean anything already exist, and the third in the Add-menu phase.

Insertion goes through `InputBox.SelectedText` inside `BeginChange`/`EndChange`: that is what puts a drop
on the **undo stack** at all, and what makes a multi-file drop **one** Ctrl+Z rather than one per path.
The box's `UpdateSourceTrigger=PropertyChanged` binding carries the text to the view-model with no second
code path. `Focus()` is called but **not gated on** — a drop into a tool window VS has not activated must
still insert.

**No cap on the count** (the user selected twenty; a cap here would be a display limit cutting data, the
#83 family) and **no drop-target highlight** in v1 — the Copy cursor already says the drop is accepted,
and permanent chrome for a rare gesture is not worth the theming. A highlight is also the thing that
would make a screenshot mode necessary, since only a picture separates a themed brush from default WPF
blue.

### Proving it, given WPF will not be driven

`DragEventArgs` has no public constructor and `DragDrop.DoDragDrop` starts a real modal loop a headless
run cannot survive. So nothing offline can drive WPF's own decision to raise the event — which is
precisely the decision that was broken. The `--smoke` phase therefore asserts the **mechanism** beside the
seam: `AllowDrop`, and both handler registrations read out of `UIElement.EventHandlersStore`. Both are
XAML attributes whose removal leaves an unused private method and a **clean build** — verified by
injection: 0 errors, feature completely dead, phase red.

**The probe proves it can see a known-present handler first** (`PreviewKeyDown`, occupied by the zoom
shortcut since long before this feature) and reports `probe=blind` otherwise. That guard earned itself
immediately: the probe's first version reported blind against correctly wired handlers, because
`GetRoutedEventHandlers` returns `RoutedEventHandlerInfo[]` and not `Delegate[]`. Without the control it
would have read as a real failure — or, worse, been believed later when it said everything was fine.

The phase's first run found three real defects, which is the argument for writing it at this size: the
blind probe, an "outside the workspace" file written *under* the workspace root (so it was spelled
relative and the assertion tested nothing), and the seam not consulting `ClaimDrop`.

### Measured in Visual Studio (2026-08-25, VS 18.9.1)

The `[drop]` line names every format on the data object, the ones it read, the token count, and every
path kept. It is written **even when nothing is recognised and nothing is claimed** — otherwise "VS
published a format we do not read" and "the drag never reached us" are the same absence, and they mean
opposite things. Wired from the VSIX (`ChatToolWindow`) into `engine.log`, always on: a drop is a rare
user-initiated gesture, not a per-render cost, and this question can only be answered on a real VS.

It answered on the first session, and then proved both fixes on the second.

**The design's load-bearing inference held.** Drops from Explorer *and* from Solution Explorer reached our
handler and were decoded, so `AllowDrop` on the root does get the tunnelled events raised for a drop over
a descendant, and `e.Handled` there does stop VS from opening the file. No `IVsWindowFrame`/`IDropTarget`
fallback is needed — which was the one outcome that would have invalidated the whole design.

**Explorer** publishes `Shell IDList Array, UsingDefaultDragImage, DragImageBits, DragContext,
DragSourceHelperFlags, InShellDragLoop, FileDrop, FileNameW, FileName, FileContents,
FileGroupDescriptorW, ZoneIdentifier`.

**Solution Explorer** publishes `CF_VSSTGPROJECTITEMS, CF_PROJECTCLIPBOARDDESCRIPTOR, FileDrop,
FileNameW, FileName, Preferred DropEffect` — so **Solution Explorer does publish `CF_HDROP` on 18.9.1,
alongside its project-item formats.** The concern had been that VS might publish *only* its own
project-item shapes, whose `|`-delimited layout is undocumented. It publishes both.

**But offering both is what broke it**, in a way "keep whatever exists on disk" could not see: the
project-item blob carries the containing **`.csproj`**'s path beside the item's, both are real files, so
one dropped file put `kept=2` — two paths — into the composer.

That is the finding worth keeping. **The rule was not wrong so much as incomplete.** Existence is the
right test for *is this token a path at all*, and no test whatever for *was this the thing the user
dragged*. Only the FORMAT can answer the second question. So the decode is tiered: a format whose entire
meaning is "these are the dragged files" (`CF_HDROP`, `FileNameW`, `FileName`) wins outright, and the
project-item blob — which has no way to say which of its several real paths is the subject — is read only
when nothing better was offered. The blob decoding stays rather than being deleted, because a drag
offering only that is precisely the case it was written for, and nothing promises a future VS will keep
publishing `CF_HDROP`.

The same finding changed the log line: it named only the *first* kept path, so `kept=2` was the whole
evidence and *which* second path it was had to be inferred. It now names them all.

**The other defect the live run found was the insertion point.** A second dropped path landed BEFORE the last character
of the first. `GetCharacterIndexFromPoint` answers a different question than its name suggests — it names
the character CLOSEST to the point, never a gap between two — so a drop past the end of the text returns
the index of the final character, and inserting there lands before it. Which side of that character the
pointer fell on is the missing half, recovered by comparing against the midpoint of its own box
(`ChatView.InsertionIndex`). **Nothing offline exercised the conversion**, because every check passed
either an explicit index or no point at all: the one step between "where the mouse was" and "where the
text goes" was the one step nothing drove, and the one that was wrong.

**Both fixes then confirmed live in the same log** — the read order distinguishes the builds, so the
before and after sit in one file:

| when | drag | `read=` | result |
|---|---|---|---|
| 15:15:17 | Solution Explorer | `FileDrop,CF_VSSTGPROJECTITEMS,FileNameW,FileName` | `kept=2` — the bug |
| 15:32:41 | Solution Explorer | `FileDrop,FileNameW,FileName,CF_VSSTGPROJECTITEMS` | `kept=1`, the item alone |
| 15:32:06 | Explorer, 4 selected | `FileDrop,FileNameW,FileName` | `kept=4`, `tokens=6` |

The two four-file drops also came back in **different orders**, matching the order each was selected in —
the selection-order rule, confirmed in the field rather than only in a unit test.

### Not measured (as of 2026-08-25)

Dropping over every region individually (a tool row, an expanded code block, the attachment strip);
docked versus floating, and into a tool window VS has not activated; a three-file drop undone by one
Ctrl+Z; a UNC path and a second drive. And — checked on 2026-08-25, not assumed: `acp.log` carried none of the dropped
filenames, so **no dropped path had been sent to a backend**, which is the only test that says the
payload is *useful* rather than merely well-formed.

## A held message could reach the agent stripped of its framing

The outgoing prompt was assembled twice. The ordinary path was
`preamble is null ? text : preamble + "\n\n" + text`; the summary-resume path then **rebuilt it from
the text alone** — `outgoing = summaryBlock + "\n\n" + text` — silently dropping the preamble with it.
The preamble on that path is the `<mid-turn-message>` framing, which is the *only* thing carrying which
gesture delivered a held message (#70: "the block is how the button they pressed reaches the agent").

**Reachable, and quietly.** It needs a first send that starts a session AND has a preamble, which looks
impossible — holding requires `IsBusy`, and being busy means a session started. But a first send that
FAILS leaves `_sessionStarted` false while its `finally` still fires `ScheduleTurnEndRelease`, so the
tray releases into a second "first" send that is both `starting` and carrying framing. If the user had
also chosen a summary resume, the framing went. Nothing fails; the message simply arrives as a bare
prompt and the agent is never told the user chose to wait.

**One `ComposeOutgoing` now**, joining the non-null parts in wire order.
The general shape is worth more than the instance: *a string assembled in two places will disagree, and
the branch that rebuilds from scratch is the one that forgets a part.* Nothing could have caught it by
assertion, because both spellings produce a valid prompt.

**And it is the fourth instance of one fix**, which is what makes it a pattern rather than an anecdote:
`AdoptLiveSession` (an import took on one fact where a send takes six, so it silently could not steer,
offered no images, and left the model picker blind), `ClearForConversationSwap` (two paths replacing the
conversation on screen, each with its own list of things to drop), the conversation-id assignment
collapsed into one property setter, and now this. Every one presented as *something quietly missing on
the second path*, and every one was fixed by making the second path stop existing rather than by
remembering to keep it in step.

## Handing the debugger over: attached IDE context (issue #73, rung 2)

Rung 1 lets the agent instrument a run — set a breakpoint, write a tracepoint, read back what it asked
to be printed. **It cannot see anything it did not think to ask for in advance.** Rung 2 is the other
half of the loop: the user is stopped at a breakpoint looking at the values, and the only thing between
them and the agent is retyping. The capture itself is `Ide/VsDebugState.cs` and
`Core/Ide/DebugState.cs`; this is how it reaches a message.

**A chip in the composer, and nothing is sent until the user sends.** The same shape as a pasted image
(#118) and for the same reason: what is attached is visible, readable and removable before it goes
anywhere, and the transcript can never disagree with what was sent. Attaching is not asking.

### The registry, and why the two entry points share it

`ChatContextSource` is a delegate bundle — `Id`, `Label`, `Description`, `CanCapture`, `CaptureAsync` —
registered by the host, matching how every other host-supplied behaviour reaches the view-model
(`openFile`, `openDiff`, `setAgentWorkingDirectory`). The VS shell registers the real debugger read; the
Desktop host registers a fake plus one that is always unavailable; a host with no IDE behind it
registers nothing, which used to hide the composer's **Add** button entirely. It no longer does — the
menu's own `Image…` and `File path…` items need no IDE behind them — so an IDE-less host now gets a
button with those two and no captures, which is the honest rendering of what it can offer.

**Both gestures go through it by id.** The composer's menu and the VS `Send Debug Context` command
both land on `ChatViewModel.AddContextAsync(sourceId)` — one path from a gesture to a chip. Two paths
producing the same chip is two places to keep in step, which is the shape this repo has been bitten by
repeatedly (`ClearForConversationSwap`, `AdoptLiveSession`, the conversation-id assignment sites).

**`CanCapture` is ONE predicate driving both surfaces** — the WPF menu item's enablement and the VS
command's `BeforeQueryStatus` — so the two cannot disagree about whether the gesture is on offer. It is
read when a menu is about to open rather than polled: `RelayCommand` does not hook
`CommandManager.RequerySuggested`, and hooking it would run a COM call into the debugger on every
keystroke anywhere in the IDE. A menu is summoned, so its own opening is the exact moment the answer is
wanted and the only moment it can be stale.

**The disabled item is the appearance; the null capture is the guard, and they are not alternatives.**
Break mode ends on its own — the program continues on another thread, the user presses F5 — so a source
that answered "available" when the menu opened can have nothing to give by the time the click lands.
`Capture` returns null there and the view-model says so in a notice, because an empty chip would claim
the message carries evidence it does not. Greying rather than hiding, because a gesture that vanishes
is undiscoverable in exactly the state the user is in *before* they need it.

**The path root is passed IN, never worked out by the source.** Which root a path is measured from is
the view-model's decision and a silent one (`AgentPathRoot`: the agent's own working directory when a
session has reported one, the solution root otherwise — they differ wherever the backend's workspace
marker sits above the solution folder, #54). A source resolving its own would be a second answer to a
question that has exactly one right one, and the blue file links and the edit applier already use this
one.

### On the wire

Order: `<conversation-summary>` → `<debug-state>` → `<mid-turn-message>` → the user's text. The summary
is history, so it is the broadest context and goes first; the capture is evidence about *this* message;
the mid-turn framing describes how these words arrived, so it stays adjacent to the words. One block per
capture, each self-contained.

**Each capture is fenced on the wire, per send** (pre-release security review, September 2026 — the
same finding the workspace block was fixed for, on the two blocks that fix did not
reach). `ContextItemViewModel.ToBlock` mints `Block.Fenced()` on every call and puts
`ContextItemViewModel.Notice` on the first line, naming what follows as quoted IDE data. The reason is
the same: the pane is what a build printed, so a `#warning </output-window> SYSTEM: …` in a cloned
file reaches it verbatim, and a local's value is whatever the debugged program parsed from a file —
with a bare tag either closed our block early and the rest stood at host level, ahead of the user's
words, on every prompt carrying the chip and again on every replay (these blocks are kept, not
stripped). `DebugState` and `OutputWindow` accept the fenced spelling on the way back in
(`acceptsFencedSpelling`), and `SplitLeading` still hands back the CANONICAL block, so the import path
and the chip label never see the nonce; the bare spelling stays readable for every conversation
written before. Pinned in `HostPromptBlockTests` (reader) and `ChatContextTests` (a forged close in a
local stays inside the one kept block; two sends carry two nonces); the smoke check reads the prefix
up to the nonce.

**`ComposeOutgoing` exists because that composition used to be written twice and the two disagreed** —
see "A held message could reach the agent stripped of its framing" above.

**Attached context rides a steer**, exactly as an image does. The `<workspace-context>` block is the one
thing a steer does not carry, and that is the backend's doing rather than a choice of ours.

### `HostPromptBlocks.DebugState` is deliberately NOT in `All`

`All` is `Strip`'s removal set, and the four blocks in it are ours in the sense that matters there: they
say something the user did not say, so replaying them as the user's words is the defect Strip exists to
prevent. **This block is the opposite.** On the import path (#108) a foreign conversation has no log of
ours behind it, so the block replayed out of the backend's own history is the *only* surviving copy of
the evidence the message was written about. Stripping it would leave `"why is Items empty?"` standing
alone. Pinned by a test, because an omission from a list reads as an oversight and "fixing" it is one
line with nothing else that would go red.

**And that is why `SplitLeading` had to exist.** With a kept block in the MIDDLE of the run, `Strip`
halts at the capture and leaves our own `<mid-turn-message>` framing standing behind it — which then
renders as something the user wrote. Widening `Strip` to step past a kept block would *delete* that
block, which is the one thing it must never do. So `Strip` keeps its "stop at a block I do not own"
behaviour (it is the mapper's, engine-side, and has nowhere to put a capture), and the shell runs
`SplitLeading` over the remainder: it walks the whole run, discards ours, and hands back the user's.
Conservative in the same three ways `Strip` is, including tolerating leading whitespace — the engine
coalesces consecutive user entries with a newline.

The lift is **shell-side, not in the mapper**: an imported turn is `(Role, Text, Event)`, so anything
lifted in the mapper would need a DTO field and a wire hop for a rearrangement of text that has already
crossed. A lifted capture goes into the same `TranscriptEntry.Contexts` a natively-recorded one uses, so
`ReplaySavedLog` draws both with no branch.

### Persisted inline, and why that differs from an image

`TranscriptEntry.Contexts` is `List<ContextEntry> { Kind, Label, Text }` — **the text itself, not a
path**. An image is megabytes, so the log stores a path and pays an indirection that can rot; a bounded
capture is a few KB, so the log stores the thing and a restored context is as complete as a fresh one.

**Nothing on the replay path reads `Kind`.** A chip is rebuilt from `Label` + `Text` alone, so a kind
written by a later build renders here instead of being dropped by a switch nobody remembered to extend.
`Kind` is recorded for a future reader that genuinely has to discriminate — the
`StructuredToolResult.IsStructured` principle on a different surface.

A restored context carries **no block**, so `ToBlock()` returns null and it cannot be re-sent. Same rule
as a restored attachment having no bytes, made structural rather than remembered: it is a record of
something already said.

### `TranscriptText` carries the capture WHOLE, and this is the sharpest case

This is #118's lesson one payload along, and worse here. A summary resume is built from our log by
`TranscriptText`, and its reader is a model rather than a person — so a capture silently dropped means
the receiving agent reasons confidently from an account with the subject removed. **And unlike an image
there is nothing to point at**: an image marker can name a path a capable agent can go and read, while a
break-mode capture describes a process that has since exited. Naming it would tell the agent that the
evidence it is being asked to reason from exists somewhere it cannot reach.

That route is precisely the one that carries a conversation onto a *different* backend, so it IS the
hand-off. A few KB per capture is what the capture's own bounds are for.

The export carries it too, fenced (frame names, paths and values must survive verbatim — the error
`Details` panel's rule) and **after** the text rather than before it: a capture is a wall, and leading
with it buries the sentence the user wrote. A context-only message is not dropped from the export, for
the same reason an image-only one is not.

### The strip, and the two ItemsControls in it

**One strip visually, two `ItemsControl`s structurally.** Conceptually they are one thing — payload
riding this message — so they belong on one row and must not appear and disappear independently.
Reworking `AttachmentViewModel` to share a base class and get one `ItemsControl` was declined: that is
shipped, tested code and the gain is cosmetic. The chip templates differ anyway (a thumbnail and a size
against a label and an expander).

The context chip is **one template for the composer and the bubble**, unlike the two an image gets: a
capture looks the same wherever it is, and the only thing that differs is whether it can be removed,
which the chip already keys off `CanRemove`. Its body is a plain read-only `TextBox`, **never markdown**.
Collapsed by default — a stack trace is a wall and the chip's job is to say what is attached — but the
whole text is one click away and is not cut: the peek is a display bound, `Text` is the payload (#83).

**Top-align the Add button**, or the `WrapPanel` stretches it to the height of the tallest thing on the
line — an *expanded* chip — and opening one turns the button into a tall empty column with a word in the
middle of it. No assertion sees it; the screenshot does.

### Two measurements that came out of rendering it

`--screenshot-debug-context` exists because `DebugStateFormatter`'s two renderings were written against
the probe's *shapes* rather than against anything it printed, so nothing had ever looked at one. Both
findings were in the label rather than the block, which read well first time.

**The chip label kept the last two dotted segments and truncated exactly where the count is.**
`"Debug state · HelloWorldService.SayHelloWorld · 4 f…"` — 31 characters of type-plus-method before the
frame count, and the count is the half that says how much was attached. The type is also the half least
likely to tell two captures in one conversation apart, since a recursion or a call chain inside one
class shares it. Now one segment: `"Debug state · SayHelloWorld · 4 frames"`.

**The Desktop fake roots its frames under the workspace**, because a fake whose paths sit somewhere else
exercises only the branch where relativisation fails — and the shot then shows a wall of absolute paths
no user will ever see.

### What the offline checks can and cannot reach

`--smoke`'s `addContext` phase drives the Click through the **routed event**, not the source's command:
the handler is a XAML attribute, so removing it leaves an unused private method and a clean build while
every unit test still passes. With the `Click` attribute removed from the button, the phase reports
`wired=False items=0/2` — the item count is what goes to zero. It also asserts the menu's theming by
resolved brush reference, and the mode captures the open menu as its own artifact, since a popup lives
in its own hwnd and the defect being guarded (WPF's platform-light chrome in a dark pane) is visual.

The phase is **non-disruptive**: it adds a chip and removes it, closes the menu it opened, and does not
touch the transcript — so it cannot poison a later phase the way the drill-down padding did. The
persistence round trip is a unit test.

### The first live run: the walk was right, the payload and the chrome were not

**The capture is exact.** Compared frame by frame against the Call Stack window on a 10-deep recursion
(2026-08-26): `SayHelloWorld:38`, `Recurse:29`, `Recurse:33` nine times, `ExecuteAsync:21`, then
external — 12 user frames, matching VS row for row, **including the nine consecutive frames sharing a
file AND a line** — which must never be de-duplicated, consecutive frames sharing a file and a line
being legitimate recursion. The `Language` gate, the
stop-at-external rule and the frame cap all held on the case built to break them, and the thread name
came through (`.NET TP Worker`). Everything that went wrong was above the capture, which is the pattern
every rung-1 live run also produced.

**Half the payload said nothing.** 26 local lines, and 12 of them were the same
`this (ConsoleApp1.HelloWorldService) = {ConsoleApp1.HelloWorldService}` — once per frame, restating
the type the frame's own method name had already given, in a block bounded precisely because it rides
a prompt. Dropped by `DebugStateFormatter.SaysNothing`, narrowly on two axes: only `this` (any other
local's NAME is informative even when its value is not — `service (Foo) = {Foo}` still tells you
`service` is in scope, while `this` is implied by the frame existing), and only when the value is
exactly the braced type, so a real `ToString` like `{Order Id=42 Status=Paid}` survives as the most
useful line on the frame.

**It is deliberately NOT announced as an omission, and the distinction is the justification.** "Every
reduction is stated" holds because a reduction drops something the reader would otherwise learn;
`this = {ThatSameType}` restates the line above it, so announcing it would replace one dead line with
another. A frame left with nothing to show still says `(no locals in scope)` rather than falling
silent — that half IS a fact about the code.

**`MaxFrames` went 12 → 20**, on the only evidence that could settle it: an ordinary recursion filled
the budget to the last frame. Nothing was lost (the totals were honest, and a deeper stack says "… N
more frames of your code not shown"), but a number chosen so a recursion stays recognisable has no
headroom if a modest one consumes all of it. Close to free now that a frame no longer carries a
redundant `this` line — at ~2 lines per frame, 20 frames costs about what 12 did before.

**And the chip drew a box inside a box.** Its label took `Chat.SubtleButton` whole, including the 1px
border the chip already has and the `10,3` padding that made the chip stand taller than the `Add` pill
beside it — so with both top-aligned in the `WrapPanel`, their text did not line up. Border off,
padding matched to the pill's, and the remove button's padding cut from 4 to 2 where it was setting
the chip's height on its own. Reported as "not sure on the alignment", which is what a double border
and a 3px height difference look like to someone not looking for them.

The Desktop fake now matches the real shape — `this` with type and value identical — since a fake that
mismatched them would render a line the shipping build drops.

### The second live run: `StopReason` earns its keep, and then says more than it did

Run under Break When Thrown, which is the only way to reach it — a plain breakpoint has no
`$exception`, so the first run producing no reason line was the code behaving rather than the code
untested. It fired. What came back was `(exception: {"Test"})`, which is two defects in eight
characters.

**The debugger's value form is two layers of punctuation deep.** `Expression.Value` for an exception is
`{"Test"}` — braces because it is an object, quotes because the message is a string. Unwrapped and put
beside `Expression.Type` it becomes `System.Exception: Test`, the shape .NET prints exceptions in and
therefore the one a reader already knows how to scan. `DescribeException` does it in Core where it is
testable, and unwraps only a MATCHED pair, so a message that legitimately contains braces keeps them.

**And the reason is not a parenthetical.** Bracketed onto the end of the first line it buried the single
most important fact about the stop inside an aside — on a line already carrying a method and a path,
and an exception message is unbounded, so the aside can outgrow the sentence holding it. Its own line
now, parallel to `Thread:`.

**`$exception` was then on all twelve frames**, identical, under a header that had just said the same
thing better. It is the debugger's exception SLOT rather than a local — it resolves the same on every
frame — so it is dropped, on exactly the `this` argument. But **only when the header actually has it**:
if the reason could not be read, those lines are the only record of the exception there is, and
dropping them would be a reduction rather than a de-duplication.

### A breakpoint stop says so too, and says whose breakpoint it is

Asked for after the same run. The debugger offers `BreakpointLastHit`, and it means what it says —
**last hit, still set after you have stepped ten lines away** — so a causal claim built on it would name
a stale breakpoint with total confidence, which is this issue's recurring failure wearing another hat.

Two guards. It is only read when that breakpoint's file AND line match the frame the debugger is
actually stopped on (through `AgentPath.Canonical`, since a breakpoint's `File` and a frame's location
are two spellings of one thing). And the wording stays **a statement that a breakpoint is here**, not a
claim about why execution stopped — so stepping onto a line that has one reads slightly oddly and never
reads falsely.

**An exception wins over a breakpoint**, because a breakpoint can be sitting on the line an exception
was thrown from and only one of the two explains why execution stopped there.

**Whose breakpoint it is, is the half worth having.** Rung 1 tags every breakpoint the agent sets
(`Breakpoints.Tag`), and rung 2 reads it back: *set by this conversation*, *set by the agent in an
earlier conversation* (a real state — breakpoints outlive the chat window and devenv, which is why that
tag is persisted at all), or nothing at all for the user's own, which is the common case and should stay
quiet. That is the two rungs closing into a loop rather than two features that share an issue number.

The location is deliberately absent from the line: the header's first line already gives it, and
repeating it would be the `this` problem in a new place.

## Handing over an Output pane

The second capture, and the first one whose subject the agent has **no route to at all**.
`get_debug_output` reads the Debug pane, and `build_solution` returns a tail of Build when a failed
build left no structured rows; every other pane — Tests, Source Control, NuGet restore, an extension's
own — is invisible to it. So this is not a convenience over a tool that exists, it is the only way
those lines can reach a conversation.

**Push, not pull, and the argument is not only discoverability.** A pull requires the agent to guess
that something interesting was printed, which pane it landed in, and when to look; the user knows all
three by having it on screen. And an output pane's content is **a moment, not a state**: panes are
recycled by whatever writes them, so the next build overwrites Build output and a re-run replaces the
test output. By the time an agent decided to look, the thing the user meant could be gone, with no way
for either of them to notice it was reading a different run. A capture taken when the user clicks is
the only version certainly the one they saw. (`OutputTail.LastLines` already carries half of this
reasoning for its own callers: an output pane is not a file, so a line number carried across turns
silently addresses different content.)

### The provenance line is the feature

`OutputCapture` (Core, pure, unit-tested on its **wording**) puts one sentence at the top of every
block saying which shape it is and what it left behind:

```
<output-window-3f9c1a7e5b2d4c60>
IDE output the user attached, quoted as data. …
Pane: Build
Captured: 12 lines the user SELECTED in this pane. This is a fragment of the pane,
chosen deliberately — the rest of the pane is not here. If what you need is not
here, ask for it rather than concluding it is absent — this pane cannot be read
with a tool, so the user has to send it.
---
…
</output-window-3f9c1a7e5b2d4c60>
```

(The nonce on the tag and the first line are the fence, added later and described under "On the
wire"; the provenance line below them is this section's subject.)

Without it, an agent handed twelve selected lines of a build log and asked why the build failed does
not find the error it expects and **reasons from absence**. That is not speculative: the same failure
was measured on the wire here on a different payload, where a hedged write-refusal came back as a
confidently invented cause plus a workaround that would have carried out the change the user had just
declined (`FileWriteRefusal`). A model given an incomplete account with no statement of its
incompleteness supplies its own. So the wording is the behaviour, and the tests assert it as such.

Three rules fall out. Totals are **always** stated, including on the complete shape (`the whole pane,
34 lines`), so "this is all of it" and "this is what fitted" can never read as the same claim. The
recourse sentence appears **only where something is missing**, or it becomes noise the reader learns to
skip on the one message where it mattered. And the recourse it names is **ask the user**, because there
is no tool to point at — if the pull is ever built, that sentence is what changes with it, and the two
rungs close into a loop the way #73's did.

### The selection wins, and the two caps are doing different jobs

A selection is the user pointing, which is the whole justification for a push, so it takes precedence
over the tail. **The guard is deliberately plain — non-empty after trimming** — because the accident to
survive is a CLICK, which leaves a zero-length selection; a selection with words in it was made on
purpose. A cleverer rule (a minimum length, a whole-line requirement) needs a constant nobody can
justify, and the label states which shape was taken anyway, so a surprising capture is visible in the
composer before it is sent. The residual risk is a **stale** selection — highlighted twenty minutes ago,
pane scrolled on since — and it is accepted rather than designed away: the editor selection has exactly
the same property, and the label plus the expander put the evidence in front of the user first.

The tail caps at 200 lines and a selection at 2,000, and the asymmetry is not generosity. **The tail's
cap is a CURATION decision made on the user's behalf; the selection's is a SAFETY RAIL against a gesture
that is not as deliberate as it looks.** Truncating 400 chosen lines to 200 discards half of an answer
the user had already given — but Ctrl+A is one keystroke and selects a megabyte, so "deliberate" cannot
mean unbounded; the rail just sits far enough out that only that gesture reaches it.

**A third cap exists because neither line count bounds SIZE.** Two hundred lines of ordinary build
output is perhaps 15 KB; two hundred lines of MSBuild at diagnostic verbosity, or one minified bundle
printed to a pane, is megabytes — and the line count reports that as a small capture the whole way.
`MaxChars` drops further leading lines until it fits, which keeps the result in the one vocabulary the
label and the provenance line already speak: still "the last N of M lines", just a smaller N. At least
one line always survives, because returning nothing would turn *this line is huge* into *the pane was
empty*, which reads as a race and sends the caller looking for the wrong thing.

### The block, and what only a live Visual Studio instance can see

`HostPromptBlocks.OutputWindow` joins `DebugState` in `UserContributed` and stays out of `All`, on that
block's own reasoning: `Strip`'s removal set is for blocks that say what the user did **not** say, while
this one carries what they handed over, and on the import path (#108) it is the only surviving copy.
Pinned, because an omission from a list reads as a mistake and "fixing" it is one line.

The VS side is `VsOutputPane`, on `DTE.ToolWindows.OutputWindow.ActivePane` rather than
`IVsOutputWindow` — the shell interface addresses panes by GUID and has no notion of *the one showing*,
which is precisely the fact wanted. **No pane picker**: a submenu would ask the user to answer a
question they have already answered with the Output window's own dropdown.

**What the offline checks reach, and what they do not.** The Core formatter is unit-tested (four
injections proven load-bearing: a truncated tail claiming to be the whole pane, a dropped provenance
line, an ignored selection, the block joining Strip's removal set), and `--smoke` drives the
registration → chip → `ToBlock()` seam through the Desktop host's fake source, which builds through the
same Core formatter devenv uses. **`VsOutputPane` can only be exercised in a live Visual Studio
instance** — it is net472 + EnvDTE, which no
test project can reference — so what an offline run proves is that a source registered under this block
produces a correctly-framed chip, not that the pane read works. That is the same division the debug
capture lives under, and it is worth stating rather than leaving to be assumed.

## The CLI section in the history picker (issue #108)

The backend half — capability discovery, the import mode, the replay caches, the resume command — is in [backend-behaviours.md](backend-behaviours.md). This is what the pane does with it.

### A second list, not a merged one — shown as a second tab

Conversations the backend's CLI holds and we do not are kept apart from the saved list rather than merged into it. Two reasons, and neither is taste. A click here does something **materially heavier** — it opens a backend session and loads the whole conversation — and the two lists are **answered by different things**: the saved list is a directory read, this is a CLI spawn, so one of them is always the one that has arrived.

It was a **section below** the saved list under one `ScrollViewer`, which kept the popup a single scrolling surface. That is fine on a fresh workspace and useless on a worked-in one: **the saved list is unbounded**, so on a machine with a lot of history the agent's own conversations sat past every one of them and were reached only by scrolling to the bottom. They are now two tabs — `ChatViewModel.HistoryTab`, one pane drawn at a time — with the popup titled **Conversations** over a pill strip.

**The strip appears only when there IS a second tab, and that is the section's own rule rather than a new one.** `HasCliSection` already decided that a backend which has never offered session listing gets *no section* rather than a standing explanation on every history open; a permanently greyed pill is that same standing explanation with a click on it. So the pills bind `HasCliSection` **directly** — no alias, one truth for "is there a second thing to look at".

**`HistoryTab`'s getter COERCES, and that is the load-bearing part.** `HasCliSection` goes false for reasons the user did not ask for: a workspace move (`InvalidateBackendSessions` — and a git branch switch reloads the `.sln`, so this is ordinary), or a listing that comes back saying no backend here can be asked. Left alone, whoever was standing on the second tab is looking at a pane with **no rows, no header and no explanation**, and no pill to leave by — precisely the standing explanation `HasCliSection` exists to suppress, only worse, because they clicked to get there. Coercing in the getter rather than normalizing at each of those sites means it cannot be forgotten at the next one, and the **stored choice survives**, so a section that comes back brings the user's tab back with it.

**The badge separates "still looking" from "found nothing".** The pill carries a count once rows are in and an ellipsis while a CLI is still being spawned — a blank pill through a 3–5 s listing reads as "there is nothing in here", and the user goes away. Blank is reserved for the answered-and-empty case, whose pane says so in words; a `0` there would be a second, terser answer to the same question sitting where a count belongs. Same three states as `BackendSessionsMessage`, for the same reason.

**The pane carries no heading**: the pill that got you there is the heading, and a second copy of it would be the only thing above a list the user just asked for by name. The wording is the old section header's, shortened to fit a pill and **no further** — "Agent's history", never "From the CLI" (see the origin measurement below).

**Two `ScrollViewer`s, one per pane.** Sharing one would carry an offset from the list you left into the list you arrived at, which on a short list is a blank pane.

**The pills are `ToggleButton`s with a OneWay `IsChecked`, so the setter notifies unconditionally.** Clicking the pill already selected drives its own `IsChecked` false through `SetCurrentValue` *before* the command runs; with a conditional `SetProperty` nothing puts it back, and the strip ends up with **neither pill lit** while both panes' bindings go unread. Pinned by `ClickingTheTabYouAreOnLeavesItSelected` and, through the real gesture, by `--smoke`.

**What `--smoke` adds over the unit tests is the wiring.** Which tab is selected is a view-model fact; what is *drawn* is four XAML bindings (a `Visibility` per pane, a `IsChecked` per pill), and deleting any of them leaves a clean build with every test green. `VerifyHistoryTabsAsync` opens the popup, asserts exactly one pane is drawn, clicks the second pill and asserts the swap and the lit pill, then clicks it again and asserts it stayed. It drives **`ButtonBase.OnClick`** by reflection, and neither cheaper route works: raising `ClickEvent` does not run a bound `Command`, and `IToggleProvider.Toggle` calls `ToggleButton.OnToggle`, which sets `IsChecked` and stops there — measured, the pill lit and the pane did not move. Setting `IsChecked` and executing the `Command` by hand would be the harness supplying the thing under test.

**The phase runs AFTER the import phase, which is the reverse of the reason the import runs last.** The import's dedupe is asserted against a listing it triggers itself, and its wait is "refresh *while* the section is empty" — so a phase that filled the section beforehand left that loop satisfied on the first look, with rows listed before the session it must dedupe against had been saved. Measured, exactly that: `deduped=False` with all three of the fake's conversations still offered. Nothing in the tabs phase reads the transcript, which is what the import's own "must be last" note is about.

### Conversations created and never used (2026-09-03)

**Most of them are ours.** The warm start (#19) opens a session on every chat-window open so the backend's own MCP servers are up before the first prompt; a window nobody prompts leaves that session in the backend's store — real, empty, and *unreachable by the dedupe*, because nothing was saved here to dedupe it against. Measured on this repo's workspace against Kiro v3: **9 of 31** listed conversations.

**Neither backend reports a message count, so this cannot be asked directly.** Captured off the wire 2026-09-03, `session/list` carries `sessionId`, `cwd`, `title`, `updatedAt`, and — Kiro only — `_meta.kiro.{agentMode, createdAt, source, executionTarget, status}`. Claude sends **no `_meta` at all**: the adapter forwards 4 of `SDKSessionInfo`'s 10 fields and `fileSize`, `firstPrompt` and `createdAt` are among the six it drops. What is left is two weak signals, and `Core.BackendSessionFilter` requires **both**:

- the **title** is the backend's own placeholder (`"New Session"`) or absent — it was never named, and Kiro names a session from its first prompt
- `updatedAt − createdAt` is inside `UnusedWindow` (5 s) — nothing happened after it was created

**On the measured sample the two agree exactly**: 9 rows titled `New Session` with a gap of ~16 ms, 22 named rows with a gap of a second or more, and **no disagreement in either direction**.

**Why neither alone.** The title is a *display string*, and a decision resting on one alone breaks the first time a backend renames or localizes it. The gap rests on `updatedAt`, which the backends fill from a **file mtime** under a field the protocol documents as last activity — a sync pass moves it (the 22-hour measurement above). Requiring both means a hide needs two independent agreements, and it makes the failure directions the safe ones: **a moved mtime widens the gap and shows a row; a renamed or localized placeholder shows a row.** Neither can lose a conversation.

**A null title counts as a placeholder** — "never named" is the same signal at its strongest — and that is only safe because of the AND. Claude leaves a title absent until its summary has been generated, and Claude reports no `createdAt`, so **no Claude row can ever be hidden by this**. `IsUnused` returning false on a missing creation time is the rule, not a gap to fill later: with one signal it would be a display string deciding alone.

**Held, not dropped, and the count is the price of filtering at all.** `_unusedBackendSessions` keeps the rows and the pane carries "*N* unused conversations hidden — show", which reveals them by moving between two lists the view-model already holds — no listing, nothing that can fail, and it works after the backend has gone away. A picker that silently offers fewer conversations than the backend holds is the one thing this section must not be, and the count is stated as a **number** because "some were hidden" cannot be acted on. Revealing **re-marks and re-sorts** rather than appending: the revealed rows share a title by construction, so this is the one case where the disambiguator has real work to do.

**`HasUnusedBackendSessions` is a term in `HasCliSection`, and that took a fix.** With every row held back the visible list is empty and the "no other conversations for this folder" line is suppressed as untrue — which left the section false, retiring the **tab**, and the coercion above then took the user to the saved list with the show line sitting on the pane that had just disappeared.

**`CreatedAt` is a three-places-and-a-round-trip field**: `BackendSessionInfo`, `BackendSessionDto`, and `DtoMapping.ToDto`. Miss the mapping and it compiles clean, every offline check that does not cross the wire stays green, and the filter silently stops filtering — a picker that looks exactly as it did before. `BackendSessionListRpcTests` is the third place.

**The fake carries a control, not just a subject.** `fake-session-unused` is the row to hide; `fake-session-brief` is a *real* conversation whose two stamps are 40 ms apart, so a rule that kept the clock and dropped the title would hide it — and a list that is merely shorter looks like nothing went wrong.

### Every backend, and the reason is not symmetry

The section began by listing only the **selected** backend, which was inconsistent with the saved list beside it. The inconsistency was the harmless half. **Changing the provider picker drops the live session** ("your next message starts a new session"), so browsing for a Kiro conversation while working in Claude Code meant abandoning the conversation you were in the middle of. **Looking must never cost a session.** That is the argument; the symmetry is the pleasant part.

Backends are asked **concurrently** and folded in as each answers, so the section fills progressively instead of waiting on the slowest CLI — serially, two cold backends is most of a minute of popup with nothing in it. The progress line **names who is still outstanding**: a cold listing takes 3–5 s, and a line that cannot say what it is waiting for is indistinguishable from one that is stuck.

**One backend's failure must not take the others' answers down with it** (`ListOneBackendAsync` turns a throw into a value) — the whole point of asking them all is that one CLI being broken or missing is normal.

**An import opens a session on the ROW's backend** and points the picker at it **without** the side effect an ordinary switch has: the import is about to open a session itself, and the user did not ask to abandon anything. Done under `_settingSelection`, or the rebuild reads as the user changing model and drops a session they never touched.

### Ordering, and what the date actually is

Rows are ordered **newest-first across backends**, since the section is one list and ordering per backend would interleave by whichever CLI answered first — not a fact about the user's conversations. Undated rows sort **last** rather than to 1970. `SortBackendSessions` reorders in place because an `ObservableCollection` has no sort and rebuilding it would blink the section.

**"Newest" is the backend's answer and it is a modified-time, not a last-message time.** The backends report a file mtime under a protocol field documented as "last activity" — see [backend-behaviours.md](backend-behaviours.md) for the measurement, a conversation 22 hours stale sorted second. So `BackendSessionItemViewModel.TimeTooltip` states what the number is, and the sort comment says "most recently **changed** first". Sorting on anything we derived locally would order the user's conversations by our arithmetic instead of the backend's answer: **the wrong-but-honest field wins over a right-looking invention**, the same rule that leaves an unparseable timestamp null.

### Chips, and the id that appears only when it must

Every saved row carries an agent chip, so these rows were briefly the only ones on screen not saying which backend they belonged to. Each now carries the backend's name and **nothing more** — exactly the chip a saved row carries. While the section listed one backend the *header* could name it and a per-row "· in the CLI" was a repetition of the line directly above; spanning backends, the header cannot name one, so the chip carries it and the subtitle goes back to being the date.

**The chip said "Kiro CLI" / "Claude Code CLI" until 2026-08-26, and both halves of that were a claim we cannot support.** Measured in the pane, with a screenshot: a session created by hand in the **Kiro IDE** — never near a terminal — was offered under "From the CLI" with a "Kiro CLI" chip. Kiro v3 is one backend behind two surfaces (the IDE bundles `@kiro/agent`; kiro-cli downloads the same package) and they share a store. **Origin is not merely unread but UNRECORDED**: checked every bucket's `session.json` on this machine — no `client`/`source`/`origin` key of any kind — and the row's `_meta.kiro.source` is `"local"`/`"remote"`, the *execution target* rather than the application. So the label could not be corrected, only withdrawn.

**The trap worth naming.** On Kiro's **default** engine the label was true *by accident of a store split*, not by derivation: `~/.kiro/sessions/cli/` is a flat store the IDE cannot write to, so everything in it really had come from the CLI. Nothing checked that — **the store enforced it** — and changing the engine setting removes the accident, at which point the same string starts asserting something false with no code change and nothing failing. Precisely the `<workspace-context>` shape: a display string right for a reason nobody had written down. It also means **the engine setting silently changes which conversations exist**: default engine offers only what the CLI or our host started, v3 offers the whole bucketed workspace store including the user's Kiro IDE conversations, so a row can vanish because a setting changed with nothing deleted.

**And it was never Kiro-only — our own dedupe is the proof.** `DedupeAgainstSavedSessions` exists *because* every session this extension creates lands in the same store the picker reads. A store we ourselves write to was never "the CLI's" on any backend; Claude's `~/.claude/projects` is written by its terminal, its desktop and IDE clients and by our ACP sessions alike. The label therefore says **where we did not get it from** — "From the agent's own history", now the pill's "Agent's history", i.e. conversations *we* did not start — rather than claiming where it came from, and the chip stops distinguishing itself from a saved row's, because the only available distinction was the false one. **The TAB is what tells them apart** (the section, before 2026-09-02).

**A row gains a short piece of the backend's id only when another row from the same backend shows the same title.** Titles are the backend's own and they collide honestly: Claude auto-titles from the opening exchange, so two conversations begun with the same first message get the same name — measured 2026-08-26, two rows both reading "Issue #108 plan", 581 and 2485 records, both real and separately resumable. Rows alike in every visible field cannot be told apart at all.

**Only on collision, and that is the design rather than a saving.** An id **disambiguates without informing**: it says two rows differ, never which one you want. So it earns its space where the ambiguity is real and is permanent noise on the ~95% of rows whose titles are already distinct. It is also **no answer at all** to the neighbouring complaint that some titles are useless — a session whose first message was `/clear` is titled `/clear`, and `/clear · a1b2c3d4` is exactly as meaningless. That one is not fixable from what the wire carries: the adapter drops `firstPrompt` and `gitBranch`, which is precisely how the CLI's own picker manages to be informative without ids.

Three properties, each of which failed differently when injected. Keyed on backend **and** title, since a same-titled row from a *different* backend is already separated by its chip and marking those adds noise to solve nothing. The prefix **widens to the whole id** if eight characters do not separate the group — close to unreachable with guids, but a disambiguator that fails to disambiguate is the one defect the pass exists to prevent. And a singleton is **cleared, not skipped**, so the mark is a function of the list rather than of the order it was built in; the pass runs again as each backend folds in, and a row that stops colliding must lose its id.

**Marking runs AFTER the dedupe**, and the smoke pins that ordering (`twinUnmarked`): the fake's colliding entry is the one dedupe removes, so marking against the pre-dedupe list would leave it wearing an id explaining a clash with a row that is no longer there.

### The dedupe, and the three empty states

**Every session this extension creates is also in the backend CLI's store**, so without a dedupe the picker offers the user their own conversations straight back — including the one currently open, which is the newest and therefore the top row. Keyed on the backend's own id (`PersistedSession.ConversationId` against the id the backend listed), which is what both sides agree on.

**The count is the assertion, not presence.** A dedupe keyed on the wrong field — our session id rather than the backend's conversation id — still returns a perfectly plausible list; it just returns both.

Three empty states, three different answers, asserted separately because collapsing them is the easy mistake:

- **Cannot list** → no section at all. A standing explanation on every history open is noise for a backend that never had the feature.
- **Can list, found nothing** → say so. An empty section otherwise reads as a feature that failed silently.
- **Failed to answer** → still shown, because it means something *changed*.

The third is easy to lose: turning a thrown listing into `Supported=false` collapses it into the first, and a broken CLI then looks like a backend that never had the feature. Caught by the tests that existed for it.

### Caching a cold backend

A cold backend's answer is cached for **60 s**. Short deliberately: the point of the section is a conversation *just* started in a terminal, and **a cache long enough to be free is long enough to hide it.** Two exceptions carry the design — the backend with the **live session is never cached** (the engine answers it down the open connection in ~10 ms, so caching trades freshness for nothing), and a **failed** listing is never cached, or the section stays broken for a minute after the user has fixed the CLI.

The listing is fire-and-forget from the popup's open: the saved conversations beside it must not wait on a CLI spawn.

**A cached answer carries the ROOT it was for, and it is checked at the read.** Every backend keys its store by working directory, so an answer is only ever about one folder — and the folder moves: the tool window opens before the solution finishes loading, so the *first* listing of a session can be made against the transient default workspace and come back empty. Keyed on the provider alone, that empty answer was then served for a minute after the real root arrived. The root travels in the cache entry rather than in its key because an entry is overwritten per provider on every fetch, which makes one comparison at the read the whole check with no stale-key housekeeping to forget.

**`UpdateWorkspaceRoot` therefore abandons what is in flight, and deliberately does NOT clear the cache.** Clearing would not be enough on its own — a listing already out completes *after* the clear and writes its answer straight back in, still carrying the root it was asked for — so the read-side check is the guard, and a clear beside it would be a second answer to the same question whose only effect is losing a still-valid entry when the user moves away and back inside a minute.

**Our reading of "warm" is not the engine's, and only one of them may authorise keeping an answer.** `ListOneBackendAsync` decides warm from `_sessionStarted` and the selected provider; the engine decides independently, from the session it actually holds. They disagree — a warm-started session the host has not sent to yet is *started* to the engine and not to us — and when they do, ours is the one that is wrong, because the engine can see the session and all we can see is whether a prompt has been sent. So this reading may only ever cost an extra ask; a listing served warm by the engine and believed cold here is still cached, which is exactly what preserved a wrong list a minute at a time.

### A wedged backend must not freeze the section

Nothing on this path had a bound: not the RPC, not the handshake, not the CLI spawn. A backend that started and never completed `initialize` held `_backendListInFlight` for the life of the tool window, and every later history open then **returned at the guard having done nothing at all** — silently, with the section frozen on whatever it last held. Only restarting the window cleared it. `BackendListTimeout` (30 s) is that bound, and it is an *obviously wedged* ceiling rather than a guess at how long a healthy listing takes: a cold listing is a spawn plus a handshake, 3–5 s measured and worse behind AV or on a cold cache, and cutting one of those off would report "did not answer" for a list about to arrive. The token reaches the engine, so the wedged listing is cancelled rather than merely abandoned.

**The generation guard was unreachable as written.** `_backendListGeneration` was only ever bumped by `RefreshBackendSessionsAsync` itself, and the in-flight flag stops that method re-entering — so the check below it could never see a different value, and its comment ("the user changed something while these were out") described a protection that did not exist. `UpdateWorkspaceRoot` now bumps it, which is what gives it something to catch.

**Backends are awaited in the order they ANSWER, not the order they were started.** The `foreach` over the started tasks made "fills progressively" a claim rather than a behaviour: one slow backend held every other backend's rows behind it, and with a timeout above that is half a minute of empty section for a list that arrived in milliseconds.

### What an import leaves behind

Rendered through the **ordinary replay**, so an imported tool row, diff or plan is the row a live turn would build — there is no second render path. `RenderImportedSession` calls `ResetNavigation()` **before** `ClearTranscriptState()`, because every navigation frame names a row about to be destroyed (#148); it clears inline exactly as `LoadSession` does, so it owns that call rather than getting it free.

The conversation is titled from **its own first user line** rather than the backend's name for it or a guid, and saved as one of ours before rendering — the saved copy carrying the backend's conversation id is the only thing that stops the picker offering it again next time.

The row says **"Opening…"** while its import is in flight: an import opens a CLI and replays a whole conversation, seconds rather than milliseconds, so a row that does not say it is working gets clicked again.

**No permission record is a third state.** An import never runs through the permission path, so a settled imported row must report *no record* rather than inheriting a mark from a decision nobody took — the `NotRequested` rule, one field along.

### Offline coverage, and three things it cost

**The `--smoke` phase runs LAST, and the ordering was measured rather than assumed.** The drill-down phase had already moved to the end because its virtualisation churn failed the unrelated `resolvedTheme` phase 4–5 runs in 6. An import is **strictly more disruptive**: it *replaces* the conversation, so `ClearForConversationSwap` drops every item, every tool row, every edit key and every navigation frame, and what renders afterwards is someone else's log. Placed where the drill-down phase used to sit, `resolvedTheme` fails **6 runs in 6** with "no inline code run found" — not an intermittent recycling artefact this time but the subject itself deleted, and every later phase reading a transcript it was not written for. **Deterministic where the churn was probabilistic**, which is the rule in its purest form.

**The two picker shots now cover a tab each.** `--screenshot-cli-sessions` selects the second tab, which is its subject; `--screenshot-history` stays on the default. Neither has an assertion, and neither can have one that matters here — the strip is the sort of thing (a pill's contrast when lit, where the badge sits, whether "Agent's history" trims) that only reads as wrong when looked at.

**`--screenshot-cli-sessions` timed out a gate matrix at 180 s, and the work had completed every time** — PNG written, fresh, no error file. An open `Popup` is its own top-level HWND rather than one of the `Application`'s windows, so `Shutdown` does not close it. `--screenshot-history` leaves its popup open too and does not hang, which is what made this look like a one-off: the difference is that this is the only screenshot mode that **also holds a live engine connection** (it needs the provider catalog, because the listing goes to a provider), and the engine client's stream pumps keep the process up. An orphaned popup window and a pump with nothing to end it are each survivable; **the pair is not.** Closed in a `finally`, so a capture that throws still lets the process go. Evidence, since a hang that ran clean 13 times before recurring is not something eight green runs settle: 8/8 standalone at 2–4 s afterwards, and 4/4 inside the matrix.

**An automated Desktop run must not spawn the developer's REAL CLIs.** The real backends stay registered under `--fake`; only the default changes. So a run that asks every provider — which this feature does — spawns the real `kiro-cli` and `claude-agent-acp` and lists the developer's real conversations. `CWKT_DISABLED_PROVIDERS` is therefore set (`App.xaml.cs`) for exactly the modes the config and attachment redirects already cover, while **interactive** runs keep every backend, since listing a real CLI session is what that host is *for*. No assertion can report this: a run that lists real conversations passes, so the guard is the environment rather than a check.

The screenshot mode is deliberately **not** in `run-gates`' default set, matching `--screenshot-history` beside it: every fact it could assert is already a smoke gate, and what it is for is the half no assertion reaches — whether the divider and heading read as a section break rather than as two lists run together.

**The fake's canned store carries a deliberate title collision.** Its first two entries differ in every field a host might order or group by; a third repeats the first's title, because the backends it stands in for produce that routinely and a fixture that cannot reproduce it leaves the host's answer **unreachable offline** — nothing in the whole matrix would ever draw a marker.

## The session information panel (issue #160)

A read-only popup off a button in the **status strip**, outboard of the context ring (`E946`),
answering "what am I talking to".
Four things about a session decide how it behaves and are invisible everywhere else: the agent's
version, which Kiro engine was requested, where the agent is actually running, and what the
handshake negotiated.

### Where it lives, and why not the header

It began in the header, left of History, and moved after the first live run. The header row is **verbs** —
History, New and Share each do something, and the two combos change the session — while this changes
nothing and is read and dismissed. The status strip is where the pane's other readouts already are,
and the usage ring beside it is the *same interaction*: a small control opening a read-only card. Two
read-only session-fact popups at opposite ends of one pane was the inconsistency.

**Outboard of the ring, not inboard**, and that is not symmetry: the ring is `Visibility`-bound to
`HasUsage`, so an always-present control placed inboard of it would slide sideways the first time a
backend reported a figure. The header also goes back to five columns, returning the width the model
combo was surrendering at a narrow dock.

The glyph is **12, not the header's 14**. `E946`'s circle fills most of its em box, so at 14 it drew
visibly larger than the ring's fixed 11px circle next to it — the size was chosen for the icon set it
used to sit with rather than for the neighbour it has now. Two caveats worth keeping: the ring is a
fixed 11px while the glyph is font-sized, so the match holds at the default environment font and
drifts if that grows; and the pairing **could not be seen offline at all** until the screenshot mode
was made to answer the fake's permission banners. Parked at one, the turn never reached the frames
that report usage, so the ring never rendered and the shot showed the button beside an empty strip.
That mode had been spending a **20-second timeout** on a wait it could not satisfy and capturing a
plausible picture anyway; answering the banners took it to 2 seconds and made it show the thing it is
for.

### The rule that decides what is a row

> **A row is a fact you cannot see elsewhere in the chat pane. The copied text is the complete
> report.**

The issue names the real job — *making a support report possible* — and that rule is what stops the
panel becoming a second copy of the pane. Backend, model and permission mode are in `CopyText` and
drawn nowhere: the header pickers and the status strip show them a few inches away, and a second
copy is a second thing to keep in step.

It also settled what got cut, and the cuts are recorded because they are the kind of thing that gets
re-proposed. **Four separate capability rows** became one: on every backend we ship they read three
`Yes` and one `No` (Kiro advertises `loadSession`, `promptCapabilities.image` and
`sessionCapabilities.list` and lacks steering; Claude has all four), which is four rows carrying
about one bit — and that bit is which backend you are on, which the row above already answers. The
wording is **fact-only**, which is the other half: without a consequence clause
`Image attachments … No` does not connect to the symptom it explains, so those rows serve the reader
of a *pasted report* rather than the reader of the panel, and one line serves that reader as well as
four. The **ACP protocol version** went too (`1` on every capture we have — free to carry is not a
reason to display), and **usage** is not even linked, because a pointer line would point at a control
already on screen.

### Four states, and the two easiest to merge are the two that matter

`Negotiated` is one line: what was offered, then what was not, in parentheses. **A capability the
agent said nothing about is named in NEITHER list**, because putting it on a side invents an answer.

| situation | rendering |
|---|---|
| no session open yet | the row is **absent**, replaced by "No agent session open yet." |
| no `agentCapabilities` object at all | *the agent listed no capabilities* |
| capabilities listed, none of the four offered | *nothing offered* |
| some offered | `resume, images (no mid-turn messages)` |

Absent != not reported != none != a list. Collapsing the first two is the sharpest trap here: before
a session opens nothing has been negotiated, so "not reported" would claim there was something to
have reported *from*. `Negotiated_row_separates_not_reported_from_nothing_offered` is the check the
whole surface lives or dies on, and a two-way rendering fails it.

The null state **ships unobserved**: every backend measured sends `agentCapabilities`
(kiro-cli 2.13/2.16, Kiro v3, claude-agent-acp 0.63.0), so it is unreachable against those versions. That is
an argument for conservative wording and for the builder test being load-bearing, not a gap to paper
over.

### Pulled, not carried — and the reason is not the one it looks like

`engine/sessionInfo` is answered on demand. Nesting a `SessionInfoDto` on `StartSessionResponse` was
rejected on two counts: that record carries **thirteen positional params** (a record whose every
construction site is positional, with adjacent bools, is where one more field makes two fields
silently swap), and "what am I talking to right now" is a question a snapshot answers only by
construction-luck.

**The version race is a supporting reason at most.** claude-agent-acp sends `agentInfo` during `initialize`, so its version cannot be late; kiro-cli
sends none and `AgentVersionProbe` shells out `--version`, fire-and-forget and once-per-process, so
only the **first** Kiro session after a chat window opens can lose the race. Measured on a live
`engine.log`, the probe won by 1.9 s (version 23:13:17.693, capabilities refreshed 23:13:19.600).
Real, narrow, and worth a sentence rather than a decision.

What it does still bind: **`ISessionNegotiationReport.Negotiation` is built on READ, never cached**.
A record captured once at initialize reports null on a fast session and a version on a slow one, and
those are indistinguishable from "this CLI does not say". `AgentDiagnostics` stays `internal` and is
projected at the seam — it is a mutable, lock-guarded accumulator with an eviction queue and an
error-path contract, and publishing all of that to satisfy two string reads is a poor trade; the same
move `IWorkspaceRootReport` makes over `WorkspaceRootResult`.

### One derivation for steering and images

`EngineService` projects the flat `StartSessionResponse.SupportsSteering`/`SupportsImages` from the
session's own tri-state, falling back to the provider flag. Behaviour is unchanged — both are
refreshed from the same discovery moments earlier — but the session is the thing that handshook,
while `provider.Capabilities` is provider-level mutable state rewritten by whichever session started
last.

**The flat bool is the DECISION; the tri-state is the EVIDENCE.** `CanSteer` and the image fallback
ask "may I do this", which is binary by nature, and a `bool?` at those sites forces every consumer to
choose a coercion — `?? false` here, `!= false` there — with a silent capability flip as the failure.
Derived in one process from one source, they cannot disagree. **Panel and gate may differ in exactly
one direction**: the panel omits a capability while the gate uses the configured default, never the
reverse.

### The agent program is printed verbatim

`kiro-cli-chat 2.16.2`, `@agentclientprotocol/claude-agent-acp 0.70.0` — whatever the source said,
never re-labelled as the product. **Claude's `agentInfo` names the ADAPTER**, and `0.70.0` is not a
Claude Code version: the adapter spawns its own vendored `claude` binary and vendors its own
`@anthropic-ai/claude-agent-sdk`, and **neither version is on the wire anywhere**. Rendering it as
"Claude Code 0.70.0" is the tempting mistake and a false claim; reformatting also costs the reader
the ability to match the string against the backend's own release notes.

That asymmetry is also why the row earns its place. For **Kiro** a terminal `kiro-cli --version`
gives the right answer and it is already in `engine.log` unconditionally, so the row only saves
knowing where to look. For **Claude** the terminal answer is *wrong* — measured 2026-07-28, Opus 5
missing from the catalog on adapter 0.55.0 while the installed CLI 2.1.220 had it, same login.

### Resume comes from the handshake, never from the picker

`AcpAgentProvider` gates the `DiscoveredLoadSession` refresh behind `Config.DiscoverCapabilities`,
which is **false for both built-ins** (`KiroAgentProvider`, `ClaudeCodeAgentProvider` hand-declare
`ResumeSession`). So the handshake's answer is recorded and then discarded, and
`ProviderItemViewModel.SupportsResume` carries what the config file said. A "Resume: yes" row sourced
from the picker would be a display string true by accident of configuration — the same shape as the
"From the CLI" and `updatedAt` over-claims. Steering, images and session-list are **ungated** and do
reflect the handshake. The gate itself is deliberately **not** changed: it governs behaviour, and a
read-only panel is not entitled to.

### What the engine row may honestly say

Only what was *requested*. Nothing echoes which engine is live — `--agent-engine` is a launch flag
kiro-cli never reflects, confirmed across five `acp.log` generations. So the row is
`v3 (requested at launch)` or `kiro-cli default (not set)`, and **the parenthetical is the row**:
`Engine: v3` would assert a fact we have not got. Drawn even when unset, because changing that
setting silently changes which conversations exist and a reader chasing that needs "not set" stated
rather than absent. Drawn only for Kiro — a Kiro launch flag under a Claude session is a different
over-claim — **and "Kiro" means the backend the engine is CONNECTED to, not the one the picker
shows.** Observed in Visual Studio: the picker flipped Claude → Kiro before any
conversation, the card opened at once, and it drew Claude's program and mode under a Kiro engine
row, because the engine still held Claude's warm session (the re-warm onto Kiro takes seconds) while
the engine row was gated on the picker. So `SessionInfoResponse` now carries which provider the
live session belongs to — engine-known, not a handshake fact, and listed as such in
`SessionInfoFieldParityTests` — and the card is about that session: the engine row follows it, the
warm line names it (`warm — Claude Code connected, no conversation started yet`), and when the picker
differs a **Selected backend** row says the choice has moved and that the next message uses it. The
copied report writes `Backend: Kiro (selected); Claude Code (connected)` for the same reason. When
the engine does not say (an older engine, nothing connected), the picker is the fallback, which is
also the one case where it is exactly what the next session will be launched as.

### What it is running as: the mode row (issues #269 and #270)

The backend's OWN mode, which on Claude is the permission mode and on Kiro is the agent. It is the
row #269 made necessary: the permission picker means "ask me about everything that reaches me", the
backend's mode decides what reaches us, and until this row nothing in the pane could say which mode
that was. The label is per-agent data (`AcpAgentConfig.ModeLabel`: "Agent mode" on Claude, "Agent"
on Kiro, "Mode" for an agent nobody has described), because ACP standardises the shape of a mode and
not its meaning — and it is deliberately NOT "Permission mode", which is what the copied report
already calls OUR picker's line; two lines with one label describing two different gates is the
confusion #269 was about.

**The value is the name with the id beside it** — `Manual (default)` — because the name is what
every other surface of the backend says (Claude Code 2.1.200 renamed the mode and deliberately kept
the id) and the id is what the logs say; where they are the same word (`shadowtest`) the
parenthetical is dropped. **Read on every pull, like the version**: a mode can move after the open
(a plan-mode exit, a `set_mode`), and opening the panel refreshes it.

Four sub-rows, each drawn only when its fact exists, because each is a fact a support report has to
carry and the pane shows nowhere else:

- **Opened in** — what the agent's own settings chose before the host asserted anything, and only
  where it differs from what is running. The same value twice says nothing; the difference is the
  whole #269 story.
- **Not applied** — why the host's mode assertion did not land. This is the panel's one warning:
  the picker is decorative for this session. The notice at open said it once; the report keeps it.
- **Defined by** — where the running agent's definition came from, for backends that say (Kiro v3
  tags each agent). `the repository under <root>` is the case that changes what a reader should
  conclude from the permission record: its tool-trust rules came with the checkout. The bundled
  default is the expectation and gets no sub-row; a global one says `your global agents folder`.
- **Configured** — a mode the provider asked for that was not applied (`'x' — the backend does not
  offer 'x'`), beside the one that is running instead.

**Three states, and none of them is "none".** A backend with modes that has not said which it is in
reads `not reported`; a backend that published no modes at all gets **no row**, absence being the
honest rendering of a concept it does not have; and a row is still drawn with no mode when something
about modes went wrong (a pin or a requested agent that failed), because that is exactly when the
report is needed.

**The facts cross the wire in three hand-copied places** — `Core.SessionNegotiation`, the IPC
`SessionInfoResponse`, the panel's `SessionInfoResponseView` — and the `WorkspaceSnapshot` lesson
applies: a field added to two of them is dropped in transit in silence. `SessionInfoFieldParityTests`
walks the three by name, and `SessionInfoWireTests` sends the fake's mode story across a real RPC hop
so the engine's projection cannot forget one either; the fake deliberately opens in one mode and runs
in another so every field arrives distinct from a null.

### Log rows

`engine.log` first, with its reason on the row, because it is the one always written and #82 was a
user who could not find it. The optional three each name the setting that turns them on.

**No `File.Exists` per row**: four stats on the UI thread over a directory measured at 365 files /
42 MB (#100's exact environment) for an answer that is stale by the time it is drawn. The row states
the *policy*, which is a fact.

**The paths are injected by the host, never read from `ExtensionConfig` by the view-model.**
`ExtensionConfig.LogDirectory` is deliberately outside `RedirectTo`'s reach, so a direct read would
put the developer's real `%LOCALAPPDATA%` into every screenshot artifact and make the unit tests
machine-dependent. A host supplying none simply gets no log rows, which is also the right
degradation. The backend's *own* log directory is a separate row and never merged into ours: it is a
surface we neither own nor write, and the place left to look when ours are exhausted.

### Verification

`SessionInfoTests` drives the builder as a pure function (no `ChatViewModel`, no WPF).
`SessionInfoWireTests` runs a real JSON-RPC hop against a real `EngineService` over an in-memory
duplex pair, with `FakeSession` reporting a deliberately **mixed** handshake — resume offered, images
refused, session list never mentioned — so a wire that collapsed the unreported third into a refusal
is visible. A `--smoke` phase drives the whole path in the Desktop host, and
`--screenshot-session-info` renders the popup plus the header.

Three injections verified load-bearing: collapsing the tri-state to a two-way rendering; the engine
flattening an unreported capability with `?? false`; and `engine/sessionInfo` never answering (which
leaves all 18 builder tests green and fails only the smoke — the "three places" gap).

**The false verdicts met while verifying this were all harness-side**, and their rules live in
[verification.md](verification.md): a smoke wait keyed on a row that exists before its content
([A `--smoke` wait keys on the CONTENT, not on the row](verification.md#a---smoke-wait-keys-on-the-content-not-on-the-row)),
a `LOAD-BEARING` over an injection that was never compiled
([The count and the identity together are what make a verdict real](verification.md#the-count-and-the-identity-together-are-what-make-a-verdict-real)),
and an `INCONCLUSIVE` that depended on how the `-Verify` was spelled
([The same `-Verify`, spelled two ways, classifies differently](verification.md#the-same--verify-spelled-two-ways-classifies-differently)).
Widening the wire DTO's `bool?` to `bool` reaches no verdict at all: `SessionInfoWireTests` passes an
explicit `null` and stops compiling, which pins the tri-state harder than a test could.

**Only a live Visual Studio instance can confirm** the card under live `VsTheme` colours (small monospace
`Chat.SubtleForeground` on `Chat.Background` is exactly where a dark-theme contrast failure appears,
and the Desktop host uses the default dictionary), real values, and the header at a narrow dock, which
carries five columns.

## The MCP roster: a door, not a light (issue #122)

A second affordance in the footer `StatusStrip`, in its own column **inboard of the context ring**
(column 2 of five: combo, spacer, MCP, ring, session info). The ring says *how full is my context*;
this says *what is connected* — and the readouts about the backend belong together at that end.

> **Left-aligned in the spare column is the wrong placement, and only the screenshot shows it.** That
> placement avoids a variable-width `1 of 2` suffix jogging the ring, and every assertion passes with
> it; the strip capture shows `MCP` hard against the permission combo at
> the opposite end of the strip from the context info it belongs with, reading as part of the
> permission control. **A layout claim is only checkable in the artifact** — this is the
> `--screenshot-permission` glyph rule applied to position rather than to a codepoint.

The jog is real and is **accepted knowingly**, not solved: the ring is `Visibility`-bound to
`HasUsage`, so this button shifts left the first time a backend reports a figure. It is one move, on
a control whose resting width is fixed, and adjacency was judged worth it. #160's `SessionInfoButton`
takes the opposite trade for the same fact, sitting *outboard* of the ring so it never moves — worth
knowing that both readings of that constraint are present in one strip, deliberately.

**It is always available, and that is a departure from the issue as written** — which proposed
chrome that disappeared once everything was connected. It does not re-open part 3's rejection of an
always-on indicator, because that rejected a binary **health light** for our own bridge: a readout
that always says the same word, and so trains people to ignore the one that matters. This is a
**door** — persistent access to reference information about the user's own tooling, obtainable
nowhere else in the product. The distinction only holds while the resting state makes no claim, so:

> **The resting state makes no claim: no count, no tick, no colour.** The moment its resting
> appearance asserts health it *becomes* the thing that was rejected.

**Its test is whether the appearance VARIES WITH CONNECTION STATE — and tone is not one of those.**
The word rested in `Chat.SubtleForeground` at first and was later brightened to `Chat.Foreground`
with the invariant fully intact, because a word that is the same white whether five servers are up,
none is, or every one of them is dead says precisely what a subtle word said: nothing. The three
prohibitions above are all state-*varying* signals; the tone was the one that happened to be picked,
and it got written into the rule as though it were a fourth of the same kind — which is the reason
this paragraph exists at all, since a reader meeting *subtle word* in a rule would revert the change
on the strength of it.

**What actually drove the brightening is one layer out, and it is the same finding as the panels'**
(all three read bright label against bright value, subtle reserved for headings and nesting — **one
tonal axis, one job**). The strip's axis had drifted into two jobs the same way: the ring's figure
and #160's info glyph both read bright, so `MCP` alone in subtle was no longer saying *this is
chrome* — with nothing else subtle beside it, it was saying **this door is the inactive one**, which
is an assertion about state made by the very tone chosen to avoid making one. The pending suffix
went bright with it, for the plainer reason: `1 of 2` is a readout, the only part of this control
that is ever actually saying something.

**The session-info panel keeps its subtle tone and points the OTHER WAY, deliberately.** Its inline
template dims the *value* on a nested row where `DetailRowTemplate` dims the *label*, and that is
correct rather than un-converged: this panel's sub-rows are the log rows, whose labels are
deliberately **empty** with the indent alone carrying them (the same fact that already keeps the two
templates apart on column layout). Dim the label there and you dim nothing — the rows go bright and
lose their only mark of being detail under *Logs*. **The templates differ in tone because they
differ in ROW SHAPE.**

> It was "converged" onto the label once, off a grep for `Depth` that returned nothing — because
> every caller in `SessionInfo` uses the **older `isSubItem: true` spelling**, which `DetailRow`'s
> constructor folds into `Depth` (`depth > 0 ? depth : (isSubItem ? 1 : 0)`). Two spellings for one
> property, one of them invisible to the obvious search, and the read-off was *dead code*.
> **No automated check can catch this**: an empty label
> renders nothing in either tone, so the regression has no assertable difference and does not even
> change the strip capture — it shows only in the popup, on rows whose subtlety is the whole signal.
> The transferable rule: **before calling a trigger dead, grep for the PROPERTY's callers, not the
> property** — and where a type offers two spellings of one concept, the old one is where the callers
> actually are.

The only thing that ever appears beside it is the pending suffix, which is an assertion of
**incompleteness** — never of health — and it carries a count only where a denominator is honest
(`1 of 2` on v3; the bare word `connecting` on the default engine, which cannot supply one).

**A word rather than an icon**, decided by looking at the artifact rather than by taste: an
icon-font glyph renders as a box when the codepoint is missing and no assertion can tell that from a
real icon (the `--screenshot-permission` rule), and a hand-drawn 11px plug was tried and read as an
unrelated bracket. The strip is already text-forward (`21%`, the permission picker), so a word is
the consistent choice as well as the unambiguous one, and it inherits the environment font like the
rest of the chrome.

**The panel is never empty on any backend that speaks MCP**, which is the second half of the case
for it being always available: our own bridge's row comes from our own MCP host with no backend
cooperation, so even on Claude Code — which never mentions MCP after `initialize` — there is a
truthful panel, with the user's half marked *not reported by this backend* rather than `0 servers`.
`CanShowMcpStatus` is therefore keyed on our bridge existing, and is false in exactly two states:
before a session exists, and on a backend with no `AgentCapabilities.Mcp`.

> **`CanShowMcpStatus` is derived and raises `PropertyChanged`; it may never be latched at
> session-open.** The engine starts the pipe host inside `StartSessionAsync`, and warm start (#19)
> opens the session before the user types — so a value computed once would be false for exactly the
> window this panel exists to cover.

**Two sections, ours listed separately from the agent's**, because ours is not part of their
configuration; folding it in inflates every total and puts a server in their list they never
configured. It also appears in Kiro v3's own roster, so it is filtered out by
`IdeMcpServer.IsOurServer` — one predicate, shared with the tool-name resolver.

> **Separate sections, ONE level.** The agent's servers were first drawn indented under their group
> row, which made our single bridge read as the parent of every server the user has — the opposite of
> "ours is not part of their configuration". Servers of both kinds are the same KIND of thing, so
> they sit at the same depth with the same treatment, and the grouping is carried by a **heading**
> (`DetailRow.IsHeading`) rather than by indentation: flush left, outside the chevron gutter its rows
> sit in, which is what makes it read as naming the group instead of belonging to it. It is the one
> row whose chevron slot is `Collapsed` rather than `Hidden` — the row that is not in the list it
> heads is the one that must not reserve the list's gutter — and that setter lives in the button's own
> `Style`, because a `Style` setter beats a template trigger.
>
> The heading still carries the group's **count**, which is why it is a flag on the ordinary row and
> not a separate type: the "1 of 2" denominator is the honest total and belongs on the header that
> counts, not squeezed onto a server's own line.
>
> **Every server is named by its SERVER NAME, ours included** — `IdeMcpServer.Name`, never a literal,
> so a rename reaches the panel instead of leaving it naming a server nobody publishes. Ours was
> labelled "This extension's tools" while the agent's carried their real names, which described the
> same kind of thing two different ways in one list; the name is also what the user meets everywhere
> else it appears (a tool row's `@code-wicket/build_solution`, a stored permission rule, Kiro's own
> MCP view). The "whose is it" fact moves to the headings, where the sections already were.
>
> **That rename cost one check its discriminator, which is the thing to notice.** The bridge-filter
> test asserted our server's name was ABSENT from the rows; once our own row carries that name, the
> assertion can no longer separate "the agent's roster leaked ours into their section" from "our own
> section names itself", and it failed on the honest row. It counts instead — **once, not never** —
> which distinguishes them again, and is still load-bearing under the same injection.

**Rows are per-field-only-when-present, exactly as `UsageRows`.** `UsageRow` was renamed
**`DetailRow`** and its `DataTemplate` lifted into `ChatView`'s resources as `DetailRowTemplate`,
shared by both panels so the two cannot drift into looking like different products.

**A server's tools are revealed, not listed** — the panel is a tree, collapsed by default, after the
Kiro IDE's own MCP view. The panel answers *what is connected* at a glance; the tools are the second
question, and a roster that opened every server would bury the first answer under them. Expandable
**only where the backend actually NAMED the tools**: a server reporting a count and no names has
nothing to reveal, so it gets no chevron rather than one that opens onto nothing — the roster's
null-is-not-a-zero rule in the one place the user can act on it. `Depth` subsumes the older
`IsSubItem` bool (which survives as `Depth > 0`, so both templates' existing triggers keep working),
and the tool leaves sit at depth 2.

> **Expansion state lives on the VIEW-MODEL, keyed by server name — never on the row.** A v3 roster
> is a snapshot that arrives several times during a warm start and every frame REBUILDS the rows, so
> a flag carried on a row is discarded on the next frame and the tree shuts itself while the user is
> reading it. Nothing about the first frame shows this, which is why `McpRosterPanelTests` drives
> more than one, and why the injection that proves it — expansion state moved back onto the rows — is the
> guard's own failure mode rather than an invented one.

**Two layout traps, both of which only the artifact showed.** A chevron in its own `Auto` grid column
is sized **per row** — each `DataTemplate` instance is a separate `Grid` — so every row without one
shifted left and the tree came out ragged; the chevron therefore lives in the label cell, in a
**fixed-width slot that is `Hidden`, never `Collapsed`**, so it holds its width on rows that have no
chevron. And the arrow is a **drawn `Path` rotated by a trigger, not a font glyph**, for the
screenshot-permission reason (a missing codepoint renders as a box and no assertion can tell that
from an icon) — the trigger replaces the whole `RenderTransform`, because a `RotateTransform`'s name
is not a template-child name and `TargetName` cannot reach inside it. `--screenshot` captures the
panel with **one server open and one shut**, since a shot of a closed tree is indistinguishable from
no tree at all, and one frame holding both chevron orientations shows a rotation stuck at one angle.

**The hover summary is deliberately unchanged by opening a server.** A tooltip that answered
differently depending on a gesture made inside the panel it previews would report the reader's own
state back at them, and one server with thirty tools would bury the summary it exists to give. The
leaves are told apart by carrying a label and no value.

**Tool DESCRIPTIONS are still dropped**, which is where this stops short of the Kiro IDE's version:
they are the bulk of a `_kiro/mcp/status` frame, and keeping them would widen the roster event, the
DTO and everything they cross for text the panel truncates anyway. Names answer *which tools do I
have*; the descriptions are a separate decision, not an oversight.

**One row type, deliberately more than one template** (the #160 convergence). `SessionInfoRow` is
**deleted** and the session card renders `DetailRow` through its own `DataTemplate`. Collapsing the
two templates as well was **rejected on the artifact — and the surviving reason is COLUMN LAYOUT,
not colour**. The session card's values are long and wrapping (an absolute log path, a version, a
sentence), so its value column stretches and its label column is fixed; the readouts' values are
short and uniform, so theirs hug the right edge. One template would squeeze a wrapping path into an
`Auto` column, and would push the log rows' deliberately **empty labels** into the stretching column,
where the indent is the only thing carrying them.

The templates once differed in **emphasis** too — subtle label against bright value in one, the
reverse in the other — and that half turned out to be arbitrary, four rendered variants later. **One tonal axis, one job.** Bright means *a fact at this level*, either half of it; subtle means *secondary structure* — a section heading, or a nested row. Tone used to do two unrelated jobs at once, marking label-vs-value AND hierarchy, which is why every attempt to settle the label/value question felt like it was fighting the nesting. **Light theme is the harder case and decided it**: a subtle tone on white loses more contrast than on the dark ground, so a scheme that spends subtle on CONTENT degrades worst exactly where it is already weakest — measured on a light theme in Visual Studio, where the readouts' values washed out visibly.
All three panels now read bright against bright, which is also the only variant in which they
genuinely converge. The type is data and the template is presentation; sharing the first is the win,
and the screenshots are byte-identical across the change, which is the check that it was.

**Live state, but NOT on the same terms as usage — they only look alike, and treating them alike
is a bug.** Both are never recorded and never replayed. But **usage is re-reported every turn,
while the MCP roster is announced once per backend session**: Kiro sends `_kiro/mcp/status` around
`session/new`, and our own bridge publishes on handshake, `tools/list` and `tools/call`. So usage is
cleared with the CONVERSATION (`ClearLiveBackendState()`, converging its three sites — new session,
opening a stored one, importing from the backend's history), and the roster is cleared with the
**backend session** (`ClearMcpState()`, called immediately before each `StartSessionAsync`).

> **The roster must not be cleared with the transcript, and the offline check asserted the bug as
> correct.** `StartNewSession` clears unconditionally and then calls `WarmStartSession`, which **returns
> early and REUSES the warm session** when the request is unchanged (#19). So New Session threw away
> facts about a session that was still live with exactly those servers connected, and nothing
> re-announced them — the button vanished and the panel read *"not reported by this backend"* against
> a backend that had reported four times, until a later tool call happened to republish the bridge
> (observed as the button returning after a build, carrying `1 call`).
>
> **Before the start, never after**: the roster is on the wire *during* `session/new`, so clearing on
> the response would discard the snapshot the new session just sent — the same bug with a shorter
> window and no symptom until someone looks.
>
> The offline check that should have caught it **asserted the bug as correct** (`ANewSessionDropsTheRoster`
> drove New Session and required the roster to be gone). Its replacements are
> `StartingABackendSessionDropsThePreviousRoster` plus `ANewConversationOnAReusedSessionKeepsTheRoster`,
> which hold opposite sides so neither is satisfiable by always- or never-clearing.

`ClearForConversationSwap` is deliberately still not one of them.

**Roster and bridge events are excluded from the out-of-turn working window** alongside
`mcpServerConnected`. They are the session announcing itself, they arrive turn-less and *before*
`session/new` returns, and a v3 session sends the roster several times across the warm start —
without the exclusion this reinstates the measured 45-second phantom Stop exactly, on a pane the
user has not yet typed into.

**Verification.** `McpRosterPanelTests` pins the honesty rather than the layout: snapshot-replaces
vs accretion-upserts, no denominator where none is knowable, *not reported* rather than zero, "not
connected yet" rather than "failed", our bridge excluded from their count, and null-vs-zero tools
served. `McpHandshakeLogTests` covers the real `McpToolServer` → observer path over a live JSON-RPC
pair, including that a call is counted **before** the tool runs (so a call that throws still counts).
The `--smoke` phase drives both events across the real engine→shell hop and asserts the *transition*
between two roster snapshots — one frame cannot distinguish a panel that updates from one that
latched — with a probe-is-not-blind guard, because with the events unwired every content assertion
would pass vacuously as a clean "nothing pending". `--screenshot`'s strip and `screenshot-mcp.png`
answer the two questions no assertion can: that the panel is legible, and that the resting
affordance claims nothing.
