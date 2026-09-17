# Your data

## What leaves your machine

**Code Wicket does not talk to any AI service.** It has no model, no API key and no account of its
own. It launches the backend CLI *you* installed and signed in to, and everything goes through that.

When you send a message, the backend receives:

- what you typed;
- a small **workspace context** block — the solution, the full paths of its folder and the agent's
  working directory, the active file and **any text you have selected in it**, the other files you
  have open, a short selection of your current errors and warnings with their messages, and, while
  debugging, whether you are stopped and where;
- anything you attached (an image you pasted or picked, a debug-context or Output-window chip, paths
  you dragged in — a dropped file travels as its path, so its contents reach the backend only if the
  agent reads it);
- whatever the agent then **asks** for — file contents it reads, build output, test results, search
  results;
- when a conversation is continued from a **summary**, its earlier transcript — sent to be summarised
  by the backend you are on *now*, which is not necessarily the one the conversation was held with.

The agent decides what to read, and reading source is the normal case: assume that anything in your
solution can end up in the conversation.

**That traffic is between you and your backend's vendor, under their terms and your account with them.** We
are not a party to it, and we cannot see it. If your organisation has rules about where source may
go, they apply to the backend you choose — not to this extension.

## What is kept on this machine

| Where | What |
|---|---|
| `%APPDATA%\code-wicket\config.json` | Your settings, including permission rules, cached model lists, and any environment variables you set for the engine or a custom agent — so it can hold credentials. |
| `%APPDATA%\code-wicket\sessions` | Saved conversations, one file each, in a folder per workspace. |
| `%LOCALAPPDATA%\code-wicket\logs` | Diagnostic logs. |
| `%LOCALAPPDATA%\code-wicket\attachments` | Images you attached, so a reopened conversation can still show them. Written when the message is sent, never before. |
| `%LOCALAPPDATA%\code-wicket\diff` | Before-and-after copies of changed files, for the diff viewer. |
| `%LOCALAPPDATA%\code-wicket\testresults` | Result files from `run_tests`. |
| `%LOCALAPPDATA%\code-wicket\probe` | The working directory a custom agent is started in when Settings checks it. |
| `%LOCALAPPDATA%\code-wicket\workspace` | The scratch workspace used when no solution is open. |

Your backend CLI keeps its **own** conversation store, separately, wherever it puts it — that is what
the history picker's second tab is reading, and it is not managed by Code Wicket.

## Limits and clean-up

- **Logs** roll at 5 MB and are swept by age (7 days) and total size (50 MB).
- **Diff copies** are swept by age (7 days) and total size (20 MB).
- **Test results** are replaced by the next run, so at most one run's files are kept.
- **Attachments** are capped by size — **Settings… → Advanced → Storage** sets the budget, and the
  oldest go first when it is exceeded. A conversation reopened after that shows the message without
  the picture.
- **Conversations are not swept.** They are yours until you delete them.

To remove everything, delete `%APPDATA%\code-wicket` and `%LOCALAPPDATA%\code-wicket`. Uninstalling
the extension does not do this for you.

## Diagnostic logging

**Logs are local only.** Code Wicket does not upload them or send them anywhere; one leaves this
machine only if you attach it to something yourself.

`engine.log` is always written and is the one to read first; a few small error logs are written
beside it. Three extra logs are **off by default**:

- **`acp.log`** — the raw protocol frames between Code Wicket and your agent CLI. **This contains
  your prompts and the agent's replies**, so treat it as conversation content when sharing it.
- **`engine-channel.log`**, with `engine-channel.log.send` beside it — raw bytes between Visual Studio
  and the engine process. **This contains your prompts, the agent's replies, file contents and
  attached images, unredacted**, so treat it as more sensitive than `acp.log`.
- **`render.log`** — how long the chat pane spends drawing itself, with a description of this machine.

All three are under **Settings… → Advanced → Diagnostics**. Turn them on to capture a problem, then
off.
