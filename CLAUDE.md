# Code Wicket — steering

@AGENTS.md

The steering document for this repository is [`AGENTS.md`](AGENTS.md). This file exists so that
Claude Code, which loads `CLAUDE.md` by name, gets it too — the `@AGENTS.md` line above imports it.

It is named `AGENTS.md` because the product is backend-pluggable by its own pitch, and steering only
one vendor's tool reads oddly in a repository that ships two backends. Kiro reads `AGENTS.md` from
anywhere in the tree; Claude Code reads this file; both end up at the same document.

**Put new steering in `AGENTS.md`, never here.** This file is a pointer, and anything written into it
is steering that only one backend will ever see.
