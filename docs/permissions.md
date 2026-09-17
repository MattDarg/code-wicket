# Permissions

When the agent asks to do something your settings don't already cover, a **banner appears at the
bottom of the chat** and the agent waits — it is genuinely blocked until you answer, not racing
ahead. The settings here decide which requests are approved for you and which you are shown.

What they don't decide is which requests get *made*. The backend resolves its own permission
settings first, and anything it approves itself never reaches Code Wicket — so no banner appears
and none of your rules apply to it. In practice backends do this for low-risk work and ask about
the rest, which is why "Ask each time" doesn't literally mean every action. See
[Reading back what happened](#reading-back-what-happened) for how to tell which is which.

## The four modes

The **permission picker** at the bottom-left of the chat, in the status strip, sets the mode, and so
does **Settings… → Permissions → Permission mode** — they are the same setting. A change takes effect
at once, for the session in progress as well as later ones. The modes are a cumulative ladder: each
level auto-approves everything below it, plus one more tier of risk.

| Mode | Auto-approves | Still asks about |
|---|---|---|
| **Ask each time** (default) | nothing | everything |
| **Allow reads** | reading files, and Code Wicket's read-only tools — builds, tests, diagnostics, symbol searches | edits, commands, and anything else the backend asks about |
| **Allow edits** | the above, plus file changes | commands, and anything else |
| **Allow all** | everything else | anything on your always-prompt list, and the [three rules you cannot switch off](#three-rules-you-cannot-switch-off) |

A backend's own search or fetch tool counts as *anything else* — though backends usually run searches
without asking at all (see [Reading back what happened](#reading-back-what-happened)).

**Builds and test runs execute your solution's own code** — MSBuild targets, and the tests themselves —
and they are approved at **Allow reads**, alongside the other read-only tools. They sit there because
they are the agent's main working loop, and a mode that stopped covering it would push you to a looser
one. What is *not* grouped with them is an expression the agent writes and has evaluated inside a
running process: that is a separate tool in the top tier, so only **Allow all**, or a rule you write,
lets it through.

**Allow edits** is a common working setting: the agent gets on with the code while you still see
every change as a diff, and every command the backend asks about waits for you.

## Answering a prompt

The banner offers up to four answers:

- **Allow** — just this one.
- **Allow always** — then a second step showing exactly what the rule will cover, as an editable
  pattern (a command glob, a file-path glob, or one MCP tool name). Tick **Save this rule
  permanently** to keep it after Visual Studio closes; leave it unticked and it lasts the session.
- **Deny** — refuse this one.
- **Deny (this session)** — refuse it and everything matching, for the rest of the session. Deny
  rules are always session-only, so there is no checkbox on that path.

Read the pattern before you accept it. It is pre-filled with something sensible, but it is the rule
you are actually making, and widening it here is how an "always" ends up covering more than you meant.

For a command, the pattern is pre-filled to match **exactly the command you were shown**: any `*` or
`?` in it is written as `[*]` or `[?]`, which match those characters literally. Typing a plain `*` or
`?` is how you widen it — `*` matches anything, `?` any one character. A rule with a wildcard in it
[never covers a command containing a shell operator](#rules-you-can-write-yourself).

An "always" answer is remembered by Code Wicket, not by the agent, so it means the same thing
whichever backend you are on.

Session rules, allow and deny alike, are cleared when you change workspace, start a **New**
conversation, or change the permission mode — an allow you made against one solution is not in force
for another.

## Rules you can write yourself

All under **Settings… → Permissions**, one pattern per line:

| Setting | What it does |
|---|---|
| **Allowed commands** | Glob patterns (`git status`, `dotnet format*`) auto-approved without prompting. |
| **Always-prompt (sensitive) commands** | Patterns that must **always** be reviewed — `rm -rf`, `git push --force` — even under "Allow all" and even if an allow rule matches. |
| **Allowed edit paths** | Absolute-path globs (`C:\repo\src\*`). A request on a matching file is auto-approved — reads as well as edits. |
| **Allowed MCP tools** | Tools auto-approved by name, one `server/tool` per line — `code-wicket/build_solution`, `github/create_issue`. |

Order matters, and the sensitive list is the one that wins:

```
deny  →  always-prompt (sensitive)  →  allow  →  permission mode  →  ask
```

So an always-prompt pattern overrides both your allow list and your mode. That is what makes "Allow
all" survivable: put the handful of things you never want done silently in that box.

**Allow patterns are anchored** — `git status` matches the command `git status`, not
`sudo git status`. **A command rule with a wildcard never covers a command line containing a shell
operator** — `&`, `|`, `;`, `>`, `<`, `(`, a backtick or a line break — because those can turn one
command into two, or into a write to a file: a `git *` rule does not approve
`git status && del *.cs`. Such a command falls through to your mode, and if the mode does not cover
it you are asked, with a note naming the rule that was not applied. A rule with no wildcard is not
affected, since it already names the whole command. Write `[*]` and `[?]` for those characters
literally. The sensitive list is deliberately the opposite: it matches anywhere in what
the request shows — the command line, for a command — so a dangerous fragment can't be hidden by
wrapping it.

**MCP tool rules are exact names, not globs** — a rule names one tool. This is where **Allow always →
Save this rule permanently** writes to; remove a line to withdraw the grant.

## Three rules you cannot switch off

Three kinds of write are always put to you with a red call-out, whatever mode is set and whatever
allow rules you have, and "Allow always" is not offered for them. As everywhere on this page, that
covers the writes **the backend asks about**: one it carries out without asking never reaches Code
Wicket at all (see [Reading back what happened](#reading-back-what-happened)).

- **A write into Code Wicket's own files**: `%APPDATA%\code-wicket`, which holds your settings and
  saved conversations, and `%LOCALAPPDATA%\code-wicket`, which holds logs and attachments — apart
  from the scratch workspace the agent works in when no solution is open. Those files hold the very
  rules you are reading about, so an edit there is not an edit like any other. A command line that
  names `code-wicket\config.json`, your settings file, is called out the same way; that is the only
  file this rule looks for in a command, so a command that writes your conversations or logs is
  governed by your mode and command rules like any other command.
- **A write to a file that sets what the agent may do without asking**: `.claude/settings.json`,
  `.claude/settings.local.json`, anything under `.kiro/agents`, and Kiro's `permissions.yaml`,
  wherever they are. A command line that names one of those files, or the `.kiro/agents` folder
  itself, is called out the same way.
- **A write that reaches its file through a link inside your workspace** — a symbolic link or a
  junction below the folder the agent is working in. The two rules above read the path as written,
  and a link means the path is not where the file is: `src\notes\config.json` can be a link that
  lands in Code Wicket's own settings folder. Code Wicket deliberately does not follow the link to
  find out where it goes (following one that points at another machine is itself a risk), so it
  tells you the path went through a link and lets you decide. A repository that genuinely contains
  links will ask you about writes into them.

For a command, the first two rules look only at what the command line spells out. Code Wicket cannot tell a
read from a write there, so a command that only reads one of these files is called out too. A
command that writes one without naming it — changing into its folder first, copying a folder over
it, running a script that does — is not caught, and neither is anything the command starts. Treat
the call-out on a command as a highlight, not a check: under **Ask each time** and **Allow edits**
every command the backend asks about is put to you anyway, and under **Allow all** it stops only a
command that names the file. And like everything on this page, it sees only the commands the
backend asks about: one the backend runs without asking never reaches it (see
[Reading back what happened](#reading-back-what-happened)).

All three are code, not settings, so there is nothing an agent can edit to lift them. The banner and the
tool row say which rule it was, so you are not sent looking for a pattern you never wrote.

## Reading back what happened

Every settled tool row says how it came to run: a rule allowed it, your mode allowed it, you allowed
it earlier, or **the backend never asked**. Expand the row for the sentence.

That last one is the case worth understanding, because it is the one your settings had no part in.
Code Wicket's permission model runs on the requests a backend makes of it, and the backend chooses
which those are using its own configuration. Work it approves itself arrives already done: no
banner, no rule of yours consulted, no `[permission]` line in `engine.log`. Backends typically handle reading
a file inside your solution or running a text search this way, and ask about edits and commands —
so a session where you were asked about every command and no reads is working normally, not
misconfigured.

The row is how you tell the two apart after the fact, and it is the only place that distinction is
visible. **If you want a backend to ask about more than it does, that is a setting on the backend,
not one here** — Code Wicket can be stricter than the backend about what reaches it, never more
permissive, and it cannot make an agent ask.

What Code Wicket can do is keep the backend's own permission mode from being chosen by a file in
your repository. **On Claude Code** the mode is read from settings files that include
`.claude/settings.local.json` under the workspace, which the agent's own file tools can write, and a
`bypassPermissions`, `acceptEdits` or `auto` there would start the next session asking less than you
chose. So
Code Wicket puts Claude Code into its ask mode ("Manual") every time a session opens, whatever those
files say, and the mode picker here is what decides. If that cannot be done, the chat says so rather
than going quiet: a warning when Claude Code still reports the ask mode, and a clear notice — naming
the setting to remove — when it does not, because in that state the picker here has no effect on the
session. **On Kiro** there is no equivalent: which tools Kiro trusts comes from its agent
configuration, and a `.kiro/agents` file in the repository named for the built-in default agent (or
for an agent you have configured) replaces it silently, including its trust rules. `kiro-cli agent
list` does not show that replacement. If you use Kiro on repositories you do not control, check
`.kiro/agents` yourself. On every backend, the write that would put such a file in place is one of
the [three rules you cannot switch off](#three-rules-you-cannot-switch-off): Code Wicket asks about it
with a red call-out whatever mode is set. That is the one step of such an escalation it can see,
and asking is what it does — it does not prevent the write, and it cannot see a backend that has
already stopped asking. For a custom ACP agent none of this applies: Code Wicket governs what
the agent asks about, and nothing else.

## Where this is stored

`%APPDATA%\code-wicket\config.json`, along with the rest of your settings. Your permanent rules are
in there as plain text, and the settings page is the supported way to edit them.
