# Using the chat

## Sending a message

**Enter** sends. **Shift+Enter** starts a new line.

### While the agent is working

Enter doesn't interrupt. A message typed mid-turn goes into a **tray** above the message box, where
you can still remove it, and one modifier decides how soon it goes:

| | |
|---|---|
| **Enter** | Queue it — send when the turn ends. |
| **Ctrl+Enter** | Send at the agent's next step — the next moment no tool call is running. The agent picks your message up there and carries on. |
| **Ctrl+Shift+Enter** | Send now, cutting into the turn. |

**Enter** and **Ctrl+Enter** never deliver into a running tool call. The pill on the tray shows which
release point the tray is using, and it applies to every message in the tray: **Ctrl+Enter** switches
it to the next step, taking anything already queued along with it, and it stays there until you
switch it back from the pill or the chat window reopens. The release point a chat window starts with
is **Settings… → General → Messages typed while the agent is working**.

The **Stop** button, at the right of the bar that shows the agent is working, cancels the turn and keeps your queued messages — they are
delayed, not destroyed.

Cutting in mid-turn ("send now") is a real interruption: a tool call already in flight is abandoned,
so prefer the lower rungs unless you genuinely need to redirect the agent immediately.

## Attaching things

- **Send a picture** two ways, whichever suits: **Ctrl+V** pastes one straight off the clipboard —
  a screenshot, a snip of a designer, a chart — and **Add ▾ → Image…** picks one off disk when it is
  already a file. Either way it becomes a chip above the message box; remove it before sending if you
  change your mind. A picture on its own is a whole message: you do not have to type anything beside
  it.
- **Point at a file** two ways as well: **drag it onto the chat**, or **Add ▾ → File path…** to pick
  it. Both insert the file's **path**, not its contents — the agent reads the file itself. A drop
  lands where you dropped it; the menu appends to the end of the message, since a menu click has
  nowhere to aim.
- **Paste terminal output** and it is cleaned up as it lands in the box, where you can still edit it.
  **Ctrl+Shift+V** pastes verbatim.
- **Add ▾ → Output window** attaches what the Output window is showing — a failed build, a restore, a
  test run. If you have lines selected in the pane, those are what go; otherwise it takes the last
  200. The chip says which, and so does the copy the agent gets, so it knows to ask you for more
  rather than assuming what it needs was never printed. See
  [handing over the Output window](ide-tools.md#handing-over-the-output-window).
- **Add ▾ → Debug context** while stopped at a breakpoint attaches the current call stack and locals.
  See [the debugger hand-over](ide-tools.md#handing-the-debugger-over).

### Pointing at a file, and attaching a picture, are different things

They look similar and behave differently, so it is worth knowing which you want. **Point at a `.png`
and you get its path; paste or attach that same `.png` and you get the picture.** What you asked for
decides, not the file — which is why `Image…` and `File path…` sit next to each other in the menu:

| | **Drop, or Add ▾ → File path…** | **Ctrl+V, or Add ▾ → Image…** |
|---|---|---|
| What the message carries | the file's path, as text you can see in the box | the image itself, as a chip |
| Who reads it | the agent, when it decides to | the model, always — it is part of the message |
| Which version | whatever the file says **when the agent reads it** | the bytes **as they were when you attached them** |
| Good for | source, config, logs, and anything you are about to edit | screenshots, mock-ups, diagrams, a photo of a whiteboard |
| Multiple files | one path each, all in one Ctrl+Z | one chip each |
| Where it lands | in the message text, editable before you send | in the strip above the box |

The reason for the split is that a path stays current and a picture cannot. Source you are working on
changes under the agent — often because the agent itself changed it — so a path is better than a copy
that went stale the moment it was attached. A picture is the other way round: reading a file is how an
agent gets at text, and a path to a screenshot is no use to a model that needs to *see* it, so the
image travels with the message.

**Only images can be attached**, and only PNG, JPEG, GIF and WebP. The file's own content decides
that, not its name, so a `.png` that is really something else is refused and Code Wicket says which
files it could not take, and why. Large images are scaled down before they are sent; one still too
large after that, or over 16 megapixels, is refused.

If the current backend can't accept images at all, Code Wicket sends the saved file's path instead,
and says so the first time it happens in a session. [Which backends take images](backends.md)
depends on the CLI you are using.

## Reading the transcript

**Tool rows** are the things the agent did. Click the chevron to see the arguments, the full result
and, once the row has settled, a sentence saying why it was allowed to run. Click the row itself to
open the diff or file it touched; a row that touched no file expands instead.

**Edit cards** are file changes. Every edit shows as one, whichever backend made it — click it to
open a native Visual Studio diff. Code Wicket supports Visual Studio edits: **where Visual Studio
applied the edit, Ctrl+Z undoes it** exactly like your own. That covers Code Wicket's own rename and
code-fix tools, and a backend that hands its file writes to the editor rather than writing files
itself — [Choosing a backend](backends.md) says which do. A card that failed says why.

**The plan strip** appears when the agent is working through a task list, and ticks items off as it
goes.

**Sub-agent work** nests under the row that launched it. Expand the row to see the most recent few
calls; where there are more, **"Open all N calls →"** drills into that sub-agent's own transcript,
and so does **Open transcript** on the row's right-click menu. The bar across the top of it takes you
back exactly where you were.

**The transcript follows new content** while you are at the bottom, and stops following the moment
you scroll away. A pill appears to take you back to the newest content.

## The status strip

Along the bottom of the pane:

- **The permission picker**, at the left — the permission mode: **Ask each time**, **Allow reads**,
  **Allow edits** or **Allow all**. It is the same setting as **Settings… → Permissions → Permission
  mode**, and a change applies straight away. Changing it also clears the "always" and "deny for this
  session" rules you made during the session. See [Permissions](permissions.md#the-four-modes).
- **The context ring** — how full the model's context window is. Hover for the detail your backend
  reports (tokens, cost, a breakdown).
- **MCP** — a read-only roster of the MCP servers the agent is connected to, and the tools each one
  defines. Includes Code Wicket's own tools.
- **The info button** — what this session actually negotiated: the backend and adapter version, when
  the connection opened, the conversation id, the capabilities offered and withheld, and where the
  logs are. **Copy** puts the lot on the clipboard as plain text — this is the thing to paste into a
  bug report.

## Conversations

Conversations save themselves per workspace. Reopening the chat brings back the most recent one for
the solution you have open.

The **history button** in the header (the clock) lists what you can return to, in two tabs:

- **Saved** — conversations saved here, for this folder.
- **Agent's history** — what the agent's own store holds for this folder, including conversations you
  started outside Visual Studio entirely, in its CLI or its own IDE. Nothing is copied: these are the
  same conversations, keyed by the same id. The pill carries a count once the rows are in, and an
  ellipsis while a backend is still being asked — so "still looking" never reads as "nothing here".

Opening one replays it locally straight away. The backend is only reconnected when you send your next
message — so browsing history costs nothing.

When you do send, Code Wicket picks up where the conversation left off. A short conversation on a
backend that can reload it simply carries on. A long one asks whether to reload it in full or to
continue from a **summary**: a fresh session with a recap of what came before. Where the backend
can't reload it at all, the summary is the only way on and you are asked before it is used — which
is also how a conversation moves from one backend to another.

Conversations the backend created but never used are held back behind an **"N unused conversations
hidden — show"** line. Most of them are ours: opening the chat window warms up a session so your
first prompt isn't slow, and a window nobody types in leaves that session behind.

**New** starts fresh: a clean transcript and a new backend session. Nothing is lost — the old
conversation is already saved and is in the history list.

**Continue in a terminal…** hands the conversation on screen back the other way: it gives you the
command to resume that same conversation in the backend's own CLI. Where the backend supports it,
it is on the **Copy, export or continue** button in the header and on the transcript's right-click
menu.

## Keyboard reference

| | |
|---|---|
| **Enter** | Send, or queue while the agent is working. |
| **Shift+Enter** | New line. |
| **Ctrl+Enter** | Send at the agent's next step. |
| **Ctrl+Shift+Enter** | Send now, interrupting the turn. |
| **Ctrl+V** | Paste, including an image from the clipboard. |
| **Ctrl+Shift+V** | Paste without cleaning up terminal output. |
| **Ctrl+Z** | Undo an agent edit that Visual Studio applied, in the editor, like your own. |
| **Ctrl+MouseWheel**, **Ctrl+Plus**, **Ctrl+Minus** | Zoom the chat, while it has focus. |
| **Ctrl+0** | Reset the zoom. |
| **Ctrl+\\, Ctrl+W** | Open the chat window from anywhere in Visual Studio. |

The chat window can also be bound to a key of your own through **Tools → Options → Environment →
Keyboard**: search for `View.CodeWicket.Chat`.
