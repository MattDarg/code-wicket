"""Bug: Stop moves the pill to Queue even when the tray is empty.

The switch exists to describe where held messages will go. With nothing held it protects nothing
and changes a setting the user can see - and the user set that setting. Dropping the count check
restores the unconditional form.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (reason == TrayHold.Stopped && PendingMessages.Count > 0)"""
new = """            if (reason == TrayHold.Stopped)"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
