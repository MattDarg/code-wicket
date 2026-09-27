"""Bug: a send gesture that lands in the TRAY does not lift the hold.

The hold was lifted in the turn runner, which only a send that PROMPTS reaches. So Stop, then Enter
before the stopped turn returned, put the new message in the tray behind the held ones and left the
whole tray waiting for a second Enter - with nothing on screen saying so. Removing the lift from
HoldMessage restores that; the lift on the idle route is left intact, so only the tray case fails.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            LiftTrayHold();

            var item = new PendingMessageViewModel(text, RemovePending, attachments, contexts);"""
new = """            var item = new PendingMessageViewModel(text, RemovePending, attachments, contexts);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
