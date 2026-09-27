"""Bug: a workspace switch clears the tray instead of carrying it across.

Clearing the tray is right for a GESTURE - New, or loading another conversation, the user choosing to
leave the conversation their held messages belong to - and wrong for a REPLACEMENT the host performs
on their behalf while their words are still queued. A solution or folder switch is the second, and the
loss is silent: no notice, no composer text, no transcript row, the pane entirely healthy and the
words gone, so no symptom brings anyone to look.

SHAPED AS A DROP BEFORE THE SNAPSHOT rather than as a removal of the helper call, so that the two
guards stay separate: this one must pin the ROUTE's carry, and the re-aim's position inside that carry
has its own script. Dropping the tray first leaves the carry with nothing to take, which is what the
route did before it called the helper at all.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            _delivery.CarryHeldMessagesAcross(() =>"""
new = """            ClearHeldMessages();
            _delivery.CarryHeldMessagesAcross(() =>"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
