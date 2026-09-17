# Security policy

## Reporting a vulnerability

**Please don't report security problems in a public issue.** Email **security@code-wicket.dev**
instead.

A useful report includes:

- what an attacker can do, and what they need to be able to do it — for example, a repository the
  user opens, text the agent reads, or a file the agent writes;
- the steps to reproduce it, or a proof of concept;
- your Code Wicket version (**Extensions → Code Wicket → About**), your Visual Studio version, and
  which backend you were using.

Code Wicket is maintained by one person. You'll be kept informed as a fix progresses, and credited in
the release notes unless you'd rather not be.

## Supported versions

Security fixes go into the latest release. Please check a problem still reproduces there before
reporting it.

## Scope

**In scope** — anything Code Wicket itself does, including:

- the Visual Studio extension and its out-of-process engine;
- the Visual Studio tools Code Wicket serves to the agent over MCP;
- how Code Wicket decides and applies permissions for the requests an agent makes;
- how it handles repository content it passes to the agent, such as the workspace context it sends
  with each prompt.

**Out of scope:**

- **Vulnerabilities in a backend CLI itself** — Claude Code, the `claude-agent-acp` adapter, Kiro, or
  any other ACP agent. Please report those to that vendor through its own security process.
- **Actions a backend takes under its own settings without asking.** Code Wicket's permission settings
  apply to the requests that reach it; anything a backend approves itself never does. That is
  documented behaviour ([Permissions](docs/permissions.md)), not a vulnerability — though a way to make
  Code Wicket approve something its settings say it should ask about is in scope.
- **What the backend's vendor does with your code.** See [Your data](docs/your-data.md).
