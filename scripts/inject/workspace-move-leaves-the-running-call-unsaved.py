"""Bug: a conversation left with a tool call in flight comes back with no row for it.

The checkpoint does not save on `toolStart`, the change drops the conversation with no save of its
own, and the call's terminal update arrives retired and is discarded - so nothing records that the
turn ended, and nothing saves the call. Removing the recorded end is the state that shipped.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            RecordEndOfOutstandingTurn();
            _delivery.EndPending(why);"""
new = """            _delivery.EndPending(why);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
