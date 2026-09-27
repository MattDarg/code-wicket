"""Bug: the tray's hold is never read where messages are released.

The shipped shape read the hold on the two TURN-END routes only, and Stop's own flip of the pill
to Queue then hid what that missed. This restores exactly that: the gate goes, the flip stays.
A stopped call reports after IsBusy has gone false, its completion drives the next-step route, and
with the pill set back to Steer while the messages are still held the tray goes out.

Only the gate is removed - the pending-send check above it and the flip are left intact - so what
fails is the one case the flip cannot cover.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (_trayHold != TrayHold.None)
                return;
"""
new = ""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
