"""Bug: a conversation change writes an end over a turn that had already ended.

The turn lease is returned one dispatcher hop after the backend's own `turnDone` has been applied
and recorded, so a change landing in that hop sees a lease outstanding for a turn that finished
normally - and records a second, contradicting end onto it.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            var log = conversation.Log;
            if (log.Count > 0 && log[log.Count - 1].Event is { Type: "turnDone" })
                return;

"""
new = ""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
