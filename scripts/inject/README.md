# Injection scripts

An injection script puts one bug back into the source, so that
[`scripts/prove-check.ps1`](../prove-check.ps1) can show the check guarding it fails without the fix.
How to run one, what the three verdicts mean and the traps in reading them are in
[verification.md](../../docs/engineering/verification.md#proving-a-check-is-load-bearing-scriptsprove-checkps1).

**The two scripts here are examples, not a library.** They are the ones `prove-check.ps1`'s own examples
run: `protected-path-guard-removed.py` verified by `dotnet test`, and `hyperlinks-dont-enter-a-list.py` by a
`--smoke` run.

**Write one, prove the check, and keep it until the pull request is approved.** Review rounds are when a
check can go quietly dead, so scripts stay on the branch through them, and a reviewer sees the evidence rather
than the claim. **Once the PR is approved, remove them in one final commit, then merge.** If a change is
needed after that, `git revert` the removal to get them back. None stays on `main` except the two examples
named above, `protected-path-guard-removed.py` and `hyperlinks-dont-enter-a-list.py`, because on `main`
nothing would keep another script's anchor current. The pull request keeps them instead: GitHub retains a PR's commits after its branch is
deleted, so the commit before the removal has each script beside the code it was green against.

**What makes the removal safe:** record what was proven where the claim is made, in words — the check, what
was injected, and the verdict with the tests it fails and how many — the count and the identity together are what make
a verdict real: *"verified load-bearing by injecting a setter that clears the
suppression flag instead of restoring it"*. Never cite a script by name; once removed, the name resolves to
nothing.

**Measured on the branch that set this rule**: re-running every row that targeted one file, after each change
to it, caught three cases where an injection had quietly stopped proving what its record claimed — a new test
legitimately depended on the injected behaviour, so the set of tests that failed was no longer the set
recorded — and confirmed that collapsing three copies of a rule into one helper left all 33 of that file's
checks load-bearing. Neither is reachable by a script that was never committed. **Only the two scripts here
are kept past a merge**, because `prove-check.ps1`'s own examples run them.

## The shape

[`protected-path-guard-removed.py`](protected-path-guard-removed.py) is the shape to copy:

- **The docstring names the bug the check guards**, not the edit the script makes. The edit is readable
  from `old` and `new`; what it means is not.
- **One exact anchor, replaced once.** A pattern that can match somewhere else injects a different bug.
- **Prefer CODE to prose, and a comment least of all** — a preference with a reason, not a rule, because
  sometimes prose is the only unique anchor. The reason: **an anchor should break for the same reasons the
  guard needs re-examining.** A code anchor breaks when the code it guards moves, which is exactly when
  someone should look at whether the guard still measures what it claims. A prose anchor breaks when
  somebody rewords a sentence — the thing most likely to change and least likely to change behaviour — so
  every break is noise and the guard needs no thought at all. A COMMENT is the worst case twice over: it is
  prose, and it is what a documentation pass rewrites, by someone who is not thinking about injections and
  will not run a sweep, which is how an anchor dies with the longest possible latency. Measured on one
  branch: two anchors died in one step, both because a sentence was reworded, neither because the behaviour
  under guard had changed. **Where prose is genuinely the only unique anchor, say so at the line**, so the
  next reader knows it was a choice rather than an oversight.
- **A missed anchor exits non-zero with `inject target not found`.** `prove-check.ps1` treats a failed
  injection command as a hard error; a script that silently wrote nothing would reach the same verdict
  only through the hash check, and says less about why.
- **The anchor must match what `read()` returns.** A text-mode read turns a CRLF checkout into `\n`, so
  write anchors with `\n`. The written file's line endings do not matter: `prove-check.ps1` restores from a
  byte copy.
- **Match the target's BOM.** `utf-8-sig` suits the C# sources that carry one; for a file without one
  (markdown, for one) use `utf-8`, or the write adds a BOM as a second change.
- **A script run BY HAND leaves its target LF, whatever it was.** Every script here reads with Python's
  default universal-newline translation, which has already collapsed CRLF to `\n` in the string, and writes
  with `newline=""`, which faithfully writes the collapsed version back. Measured on a CRLF source:
  1683 CRs before, **0** after, for a script that changed one line. Under `prove-check.ps1` this is
  invisible and harmless — it restores from a byte-exact snapshot — so it is a trap only for a script run
  directly. Restore from git afterwards, and do not read a whole-file diff as evidence of what the script
  changed. It is the mirror of the trap above: there the BOM is added silently, here the endings are.

## Four traps

**While it exists, the script is in the tree every published-file guard scans.** An injection proving such
a guard must ASSEMBLE the string the guard bans rather than spell it out, or the guard fails on the script
itself as soon as it is staged — green in the session that wrote it, red on the next run.

**Inject one guard at a time.** An injection that removes two guards cannot say which one the check pins.
Where a behaviour is deliberately guarded twice, removing either half alone leaves the check green, and
that is the finding: a check that cannot separate a fix from its fallback pins neither. Say so where the
proof is recorded rather than quietly removing both.

**Reproduce the BARE mistake that shipped, never route it through the correct path.** An injection that
reaches the defect using the fix's own machinery measures the machinery instead. Measured: a notice that
had been written too early was re-injected through the helper that registers it for removal — so the
ending took it away again, the consequence check stayed green, and only the timing check failed. Injected
as the plain insert that actually shipped, both failed. **An injection built out of the fixed code's parts
will understate what it pins**, and it understates it in the reassuring direction.

**Re-prove every script anchored in a file you changed — not the ones you think your change reached.** That
is the instruction; the rest is why it is not optional.

**A script does not break when its subject moves. It goes quietly DEAD, and that reads as a pass.** It keeps
injecting something that still compiles and still lands, while the check it proves has stopped being able to
see it — or its anchor stops matching, at which point it is not run at all and so is not in the denominator
of whatever "N of N" you report. Both were measured on one branch: a line moved across a `finally` and its
injection came back `PINS NOTHING` with nothing about the script wrong; and a script whose anchor a fix had
rewritten sat inside three reported "N of N LOAD-BEARING" figures, each from a sweep scoped to what that fix
had obviously touched. The dead one was the script that same work had written, which is where anchors move.

**So read `PINS NOTHING` as a verdict on the PAIR** — the script and where its subject now lives — and scope
a sweep by the FILES you touched rather than by the guards you meant to touch. This is what the
committed-on-the-branch rule above buys; a script nobody kept cannot be re-proved at all.

**Two more things a sweep's scope needs, both measured on one step.**

**A guard whose input is the TREE is reached by what you ADD, not only by what you edit.** The script proving
"no source file cites an injection script" targets a file a step never touched, while the test it proves
scans everything under `src/` — which that step had added files to. A population derived from changed targets
alone misses it, and nothing about the miss looks wrong. So the population is two sets: the scripts whose
target you edited, and the scripts whose SUBJECT is a scan you changed the input to.

**WHILE A SWEEP IS RUNNING THE WORKING TREE IS NOT YOURS.** An injection is live in it for most of the
run, so anything that reads the tree through git takes that injection as your work. Two ways this has
actually gone wrong, opposite in effect and identical in cause:

- **`git add -A` CAPTURES the live injection into history.** Measured: a documentation-only commit made
  during a sweep swept up a `ChatViewModel.cs` with a guard removed — someone else's bug, in a commit whose
  message is about a markdown file, and the only warning was a line-endings notice naming the file. Caught
  by reading the commit's own `--stat` afterwards. The repair never touches the working tree, because
  prove-check is still using it: `git reset --soft HEAD~1`, `git restore --staged --source=HEAD -- <file>`,
  recommit.
- **`git checkout -- <path>` DESTROYS your uncommitted work**, below.

**So commit by explicit pathspec, never `-A`, until the sweep has finished** — and read the `--stat` of
anything committed during one. A docs-only change is exactly the case that feels safe enough to skip both.

**An anchor loop must COMMIT first, or restore from a byte copy — never from git.** Checking that every
anchor still lands is a cheap loop: run each script, see the file change, put it back. Putting it back with
`git checkout -- <file>` is the trap this whole script exists to avoid, and it bites hardest here because the
loop runs it dozens of times: on an uncommitted tree it silently reverts every unrelated change in those
files along with the injection. Measured — a set of review fixes, build-verified and suite-verified minutes
earlier, gone, and the only sign was the loop reporting the newest script as MISSED, which it could only be
because the property it anchors on had been reverted too. **The reported MISS was a symptom of the loop's own
damage, not of a dead anchor**, which is the reading that costs the time.

**And a guard written around it can INVERT** — measured, and it is the shape to check before running any
line that ends in `git checkout`. The guard was `git diff --quiet -- src/ && echo "DEAD" || { …; git checkout
-- src/; }`, meant to skip the restore where the injection had not landed. `git diff --quiet` exits 0 on a
CLEAN tree, so the `||` branch — the one holding the destructive command — is the branch that runs on a DIRTY
one: the restore fired on precisely the state it was written to protect, and the safe case was the only case
it skipped. Two rules out of one line. **A predicate whose success means "nothing to lose" puts the
destructive command in its `&&` branch, never its `||`** — read that direction out loud before running it,
because both spellings look like a precaution. And the deeper error is that the predicate answered a
different question from the one asked: *did my injection land* and *is the tree dirty* are the same question
only on a committed tree, and on any other one **"something changed here" cannot tell your injection from
somebody's unfinished work** — which is the whole reason the restore is a byte copy and not a git operation.

**The anchor half has a cheap instrument, so run it over EVERY script anchored in a file you changed.**
A check-derived population is small enough to sweep with the full suite behind it. The anchor-derived one is
not, once a round edits a file that most of the library anchors into — and the anchor question does not need a
test run to answer: inject, assert the file changed, restore from the byte copy, and read the exit status. It
costs seconds per script against minutes, so the honest split is **every anchor checked for landing, and the
checks-derived set swept for verdicts**. Measured 2026-09-26, over the library as it stood then: of the 48
scripts anchored in the two source files that round edited, one had died — its anchor pointed at a ternary the
round had turned into a `switch`. **A dead script exits the way `PINS NOTHING` does**, so a
landing check is also the only instrument that separates those two without reading a log — and an anchor
spanning two lines can half-survive, which is what makes its death read as implausible when a sweep reports
it: the line quoted first in the script was still there.

**A population derived from ANCHORS answers a different question from one derived from CHECKS.** An anchor
sweep asks *did the code move under the script*; a check-derived population asks *could the behaviour under
guard have changed*. A round whose only source change is a statement MOVED between two methods can leave every
anchor landing — measured, 42 run and 0 missed — while changing whether a guard still fails, because the moved
statement is read by the guard's CHECK rather than sitting at its anchor. Measured: `InputText = string.Empty`
moved from a shared decision method into the composer's own entry, and a set of checks assert on what the box
holds afterwards. So where a round MOVES a statement rather than rewriting a region, the re-proof set is the
scripts whose checks read what moved — derived from the last sweep's own check identities, not from the diff.

**Derive it over the WHOLE TREE, and name the scope you used.** A population is worked out by searching, so
the search's scope silently becomes the population's boundary: one run over `src/CodeWicket.UI/` yields a set
that omits every caller in the hosts and the tests, every verdict in the resulting sweep is correct, and the
scripts that were never selected are not in the denominator to be missed. **Scope is part of the claim** —
*"the only caller"* of anything `public` is a tree-wide question that no directory-scoped search can answer,
and an absence is evidence about the set searched and nothing else. Excluding `bin/` and `obj/` is the one
narrowing that is safe, and it is about build output rather than about source.

**Derive that set per FILE, not per method, and a MENTION is not a READ.** A per-method scan of each check's
body misses every assertion reached through a shared helper, which is an ordinary thing for a test class to
have: measured on `ConversationChangeOrderingTests`, whose theory rows all assert the composer inside one
private `Check(phase, route)`. Per method the derivation returned 15 scripts and silently dropped one of the
three the round was known to owe; per file, 24. Over-inclusion is the safe direction — a file where some other
test reads the same state pulls its checks in, and the cost is one re-proof. In the other direction, a
predicate counting any MENTION returned 26: `vm.InputText = "…"` driving a send is setup and cannot be
affected by *when* the box is cleared, and only a read inside an assertion can.

**Sweep after every ROUND, not at the end of a step.** Measured: one step, two sweeps, **four** dead scripts,
and every one of the four was killed by the work of the round that had just run — two by the step's own
edits, two more by the fixes it made to its own review findings. That is the mechanism above restated as a
rate: anchors die where the author is editing, so the density of deaths is highest in the round that just
edited, and a sweep deferred to the end of a step is asked to find deaths from several rounds at once while
each later round has been running against scripts already dead.

**Re-anchor to the same subject, and the proof is the check identities.** A re-anchored script that comes back
`LOAD-BEARING` at the SAME checks as before has kept its subject; one that comes back load-bearing at a
different set has been quietly pointed at something else, and the count alone cannot tell you which happened.

**A sweep RUNNER has to be built for the verdict it is reading.** `prove-check.ps1`'s own guidance — a hard
error is not a fourth exit code, so read the printed line — assumes a reader, and a loop is not one. See
[verification.md](../../docs/engineering/verification.md#three-verdicts-because-of-the-exit-code-family-again)
for what a runner must require before it believes a verdict. **And pass any ARRAY parameter through
`pwsh -Command`, never `-File`** — `-File` parses its arguments as a command line, so `-Path a b` hands `a` to
`-Path` and leaves `b` to bind positionally to whatever parameter is still free, and a comma-joined
`-Path a,b` binds one string that is neither file. **How that surfaces differs by script, and only one of the
two is quiet.** On `prove-check.ps1` the stray value lands on `-Name`, the only non-mandatory string, so the
injection runs with one target snapshotted and the SECOND file is left dirty — which the stray-write guard
catches, with a hard error naming the value that was swallowed. On a runner with a free positional parameter
there is no such guard, and a whole population swept as one name that matches no file ends in an ordinary
summary line.
