# Injection scripts

An injection script puts one bug back into the source, so that
[`scripts/prove-check.ps1`](../prove-check.ps1) can show the check guarding it fails without the fix.
How to run one, what the three verdicts mean and the traps in reading them are in
[verification.md](../../docs/engineering/verification.md#proving-a-check-is-load-bearing-scriptsprove-checkps1).

**The two scripts here are examples, not a library.** They are the ones `prove-check.ps1`'s own examples
run: `protected-path-guard-removed.py` verified by `dotnet test`, and `hyperlinks-dont-enter-a-list.py` by a
`--smoke` run.

**Write one, prove the check, and do not commit it.** A script's value is at the moment it proves the check.
After that its exact source anchor goes stale as the code moves, and nothing reports it: a committed script
is only checked when someone runs it again. Record what was proven where the claim is made, in words — the
check, what was injected, and the verdict with its count — so the claim still reads when the code has moved
on: *"verified load-bearing by injecting a setter that clears the suppression flag instead of restoring
it"*.

## The shape

[`protected-path-guard-removed.py`](protected-path-guard-removed.py) is the shape to copy:

- **The docstring names the bug the check guards**, not the edit the script makes. The edit is readable
  from `old` and `new`; what it means is not.
- **One exact anchor, replaced once.** A pattern that can match somewhere else injects a different bug.
- **A missed anchor exits non-zero with `inject target not found`.** `prove-check.ps1` treats a failed
  injection command as a hard error; a script that silently wrote nothing would reach the same verdict
  only through the hash check, and says less about why.
- **The anchor must match what `read()` returns.** A text-mode read turns a CRLF checkout into `\n`, so
  write anchors with `\n`. The written file's line endings do not matter: `prove-check.ps1` restores from a
  byte copy.
- **Match the target's BOM.** `utf-8-sig` suits the C# sources that carry one; for a file without one
  (markdown, for one) use `utf-8`, or the write adds a BOM as a second change.

## Two traps

**While it exists, the script is in the tree every published-file guard scans.** An injection proving such
a guard must ASSEMBLE the string the guard bans rather than spell it out, or the guard fails on the script
itself as soon as it is staged — green in the session that wrote it, red on the next run.

**Inject one guard at a time.** An injection that removes two guards cannot say which one the check pins.
Where a behaviour is deliberately guarded twice, removing either half alone leaves the check green, and
that is the finding: a check that cannot separate a fix from its fallback pins neither. Say so where the
proof is recorded rather than quietly removing both.
