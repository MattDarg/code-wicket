"""Bug: a workspace switch keeps the hold it found instead of writing its own reason.

This injects the option the user DECLINED (2026-09-26): a tray already stopped or backed out keeps that
sentence across the switch, and only a tray with no hold gets the workspace one. It takes two edits
because the live code cannot express it in one - the carry resets the hold on its way past, so the
earlier reason has to be captured BEFORE the carry to be put back after. Which is the point: the
decision is one line, and reversing it is a shape a later hold-preserving carry would arrive at by
accident, with nothing in the product failing to say so.

Nothing about the hold's behaviour changes - both sentences hold the tray identically, the gate reading
any non-None value - so the only check that can see this is one asserting the words.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()

capture_old = """            _delivery.CarryHeldMessagesAcross(() =>
            {
                ClearTranscript();"""
capture_new = """            var preCarryHold = _trayHold;
            _delivery.CarryHeldMessagesAcross(() =>
            {
                ClearTranscript();"""

hold_old = """            if (PendingMessages.Count > 0)
                HoldTray(TrayHold.WorkspaceMoved);"""
hold_new = """            if (PendingMessages.Count > 0)
                HoldTray(preCarryHold != TrayHold.None ? preCarryHold : TrayHold.WorkspaceMoved);"""

if s.count(capture_old) != 1 or s.count(hold_old) != 1:
    sys.exit("inject target not found")
s = s.replace(capture_old, capture_new, 1).replace(hold_old, hold_new, 1)
io.open(P, "w", encoding="utf-8", newline="").write(s)
