"""Bug: an engineering doc names an injection script, which is removed before its branch merges.

The name resolves while the branch is under review and to nothing once it lands, so the time to catch
it is while the script still exists. What the doc should carry instead is what the script proved: the
check it failed and the mistake it injected. The check does not read the scripts themselves, so the
name is spelled.
"""
import io, sys
P = "docs/engineering/verification.md"
s = io.open(P, encoding="utf-8-sig").read()
old = """
## Proving a check is load-bearing: `scripts/prove-check.ps1`
"""
new = """
## Proving a check is load-bearing: `scripts/prove-check.ps1`

Proved by `a-stopped-tray-is-stranded-by-the-next-send`.
"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8-sig", newline="").write(s.replace(old, new, 1))
