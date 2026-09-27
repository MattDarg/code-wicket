"""Bug: the carry across a conversation replacement snapshots the tray and puts nothing back.

The one helper every route with this rule goes through takes the held messages before the clear and
restores them after. Gutting the restore is the whole rule gone at once, and it must fail EVERY
route's own check by name - a total is not a per-route verdict. Four routes now: the resume choice's
fresh answer, the moved-root fork, start-over, and a workspace switch.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            foreach (var message in held)
                _host.PendingMessages.Add(message);"""
new = """            foreach (var message in held)
                _ = message;"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
