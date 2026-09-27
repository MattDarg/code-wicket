"""Bug: a mid-turn message that never reached the agent stays in the saved log.

A steer is recorded BEFORE it is sent, its place among the running turn's frames being right only at
that moment - so a failure is the one case that has to take the entry back out. Left there, the
message came back on the next reload looking said, with no reply under it.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                    recordedInto.Log.Remove(recorded);"""
new = """                    _ = recorded;"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
