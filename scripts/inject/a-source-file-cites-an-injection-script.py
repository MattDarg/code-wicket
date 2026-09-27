"""Bug: a source comment names an injection script, which the merge deletes.

A script is committed on the branch that writes it and dropped before merge, so the citation is dangling
by construction - and a comment fails no build, so nothing reports it. Five such citations were
already dangling when the guard was written, their scripts having been pruned when the injection
library moved out of the repository. Restoring one restores the class.
"""
import io, sys

P = "src/CodeWicket.Shell/Sessions/Invariants.cs"
s = io.open(P, encoding="utf-8").read()
old = "    /// Proved by <c>StaInvariantScopeTests</c>, over an injected thread-static scope.</para>"
new = ("    /// Proved by <c>StaInvariantScopeTests</c> and "
       "<c>" + "scripts/inject" + "/ambient-breach-scope-is-thread-static.py</c>.</para>")
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
