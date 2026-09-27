"""Bug: two routes to one send both hand its message back, so the composer gets it twice.

The give-back used to be guarded by the epoch moving, which every conversation change does - except
the engine EXIT, which leaves the transcript up, and except the window between a back-out marking a
message for give-back and the turn runner performing it. Removing the once-only fact on the send
restores both: the text is prepended to itself and every chip is inserted a second time.

One guard, two reachable routes, so two checks fail - which is what a defect with two live routes
looks like rather than an injection that removed two things.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (send.GivenBack)
                return;
            send.GivenBack = true;
            GiveBackUnsentMessage"""
new = """            GiveBackUnsentMessage"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
