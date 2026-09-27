"""Bug: a conversation change loses a message parked on the resume-choice or moved-root banner.

Those two phases hold the message in a field rather than in a send object - `Begin` has not run, so
there is no bubble - and the field was simply cleared. A released batch's pictures and IDE captures
go with it, the parked message being the only thing holding those. The other give-back branch, for
a send that has begun, has its own script.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            else
                GiveBackParked(parked);"""
new = ""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
