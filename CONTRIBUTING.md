# Contributing to Code Wicket

Thank you for considering it. Bug reports, reproductions and pull requests are all welcome.

## Before you start

- **Found a security problem?** Please don't open a public issue. Email
  **security@code-wicket.dev** instead — [SECURITY.md](SECURITY.md) says what to include and what is
  in scope.
- **Reporting a bug:** say which Visual Studio version, which backend (Kiro or Claude Code) and its
  version, and what you expected against what happened. `engine.log` is always written and is usually
  the fastest way to an answer — **Extensions → Code Wicket → Open Logs Folder** takes you to it.
  Read it before you attach it: it can contain paths and text from your solution.
- **Planning a larger change?** Open an issue first, so we can agree the shape before you spend time
  on it. A small fix can go straight to a pull request.

## Building and testing

What you need:

- **Windows.** The extension, the chat UI and the unit tests (`net10.0-windows`) are Windows-only.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** — enough on its own to
  build everything, including the VSIX, since the VSSDK build tools come from NuGet.
- **PowerShell 7 (`pwsh`)** for the scripts under `scripts/`.
- **Git, and a clone rather than a source download.** The unit tests that scan the published tree ask
  git which files are tracked, and fail without a repository.
- **Python 3**, only to run an injection script through `scripts/prove-check.ps1`.
- **Visual Studio 2026 with the Visual Studio extension development workload**, only to run and debug
  the extension. Visual Studio 2022 17.14 runs an *installed* Code Wicket, but its build stack cannot
  target `net10.0`, so it cannot build this solution.

```
dotnet build CodeWicket.slnx
dotnet test src/CodeWicket.Tests/CodeWicket.Tests.csproj
```

- **Build with `dotnet build`**, or inside Visual Studio — never a standalone `MSBuild.exe`. The
  build should finish with no warnings and no errors.
- **Run the offline gates before you open a pull request:** `scripts/run-gates.ps1`. It needs
  PowerShell 7 (`pwsh`); under Windows PowerShell 5.1 every gate reports a failure over its own pass.
  It builds once with warnings as errors, as the release pipeline does, then runs the unit tests,
  the standalone chat host's self-check on both target frameworks, and its screenshot check.
- **Running and debugging the extension needs Visual Studio 2026** — start debugging (F5 by default)
  on `CodeWicket.VSExtension` deploys it into the experimental instance.
  [Running it in the experimental instance](docs/engineering/vsix-shell.md#running-it-in-the-experimental-instance)
  covers what that instance shares with an installed copy, and how to reset it.

## How this codebase is written

[`AGENTS.md`](AGENTS.md) is the project's steering document: the architecture, and the rules that
must not be broken, each with a pointer to the document that holds the evidence behind it. **Read
the linked document before changing anything in its area.** A rule that looks arbitrary is usually
one that cost a live capture to learn. (It is named for the coding agent that reads it
automatically, but it is written for people too.)

A few habits the maintainer asks of changes:

- **A new test should be shown to fail without the fix.** `scripts/prove-check.ps1` injects the bug a
  check guards and reports whether the check notices; a check that stays green over the bug pins
  nothing.
- **Where wording is behaviour, test the wording.** Messages the agent or the user acts on — a refused
  write, a build note, a permission reason — are asserted word for word.
- **Commit messages say what is now true**, in a sentence: *"A reload is verified after the fact, not
  predicted"* rather than *"Fix reload bug"*. The body says why.

**Reviewing** rather than writing? [`REVIEW.md`](REVIEW.md) says what a review here has to do that an
ordinary one does not — chiefly that the steering document is deliberately incomplete, so finding no
rule in it is not evidence that none applies.

## The Contributor License Agreement

Before a first pull request can be merged, you'll be asked to sign the
[Contributor License Agreement](CLA.md). CLA Assistant does this in the pull request itself: one
click, once.

**Why the project asks for one.** Apache-2.0 already lets anyone use a contribution, commercially
included, as long as its conditions are kept. The Agreement adds two things: a licence broad enough
for the maintainer to release your contribution under other terms, free of those conditions — in a
commercial edition or add-on, for example — and your confirmation that the work is yours to
contribute. You keep the copyright in your work.

**What you get in return.** The Agreement commits the maintainer, and anyone who takes the project
over, to one thing in writing: **any version released under terms other than Apache-2.0 becomes
Apache-2.0 two years after it is published — or immediately, if the project is abandoned.** Your
work can't be locked away for good. Section 8 of the Agreement has the exact terms.

**What doesn't change.** The code you contribute to is Apache-2.0, and stays available under
Apache-2.0 for as long as anyone has a copy. You can still use your own contributions however you
like.

## Licence

By contributing, you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE), and under the [Contributor License Agreement](CLA.md) once you have
signed it.
