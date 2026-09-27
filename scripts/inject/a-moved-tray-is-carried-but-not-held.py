"""Bug: a workspace move carries the held tray but does not HOLD it.

The carry alone is not enough. ClearTranscript closes the out-of-turn window one statement before it
clears the tray, so at the moment of the close the tray is still full and the window's setter posts a
turn-end release on exactly that condition. That post runs after the move's synchronous work, by
which time the carry has put the tray back - so without the hold it delivers messages written for the
workspace the user LEFT into the agent of the one they arrived at, unasked. Worse than the loss the
carry fixes, which is why the two had to land together.

Removing only the hold leaves the carry intact, so this separates the two halves rather than
measuring them together.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (PendingMessages.Count > 0)
                HoldTray(TrayHold.WorkspaceMoved);"""
new = """            if (false)
                HoldTray(TrayHold.WorkspaceMoved);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
