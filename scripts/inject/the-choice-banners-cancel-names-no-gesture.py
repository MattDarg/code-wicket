"""Bug: cancelling the Choice or moved-root banner leaves the tray naming no gesture.

No turn-end release is scheduled on that route, so the follow-ups stay put either way - what the
hold buys is that the tray says what the user just did. Without it the sentence read "Not sent -
the agent isn't working", which is true, is not what happened, and is issue #253's shape: a status
naming a cause it has not checked. Removing the hold restores it.
"""
import io, sys
P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            _host.HoldTray(TrayHold.BackedOut);
            var parked = _parked;"""
new = """            var parked = _parked;"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
