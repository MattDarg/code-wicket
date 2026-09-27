"""Bug: a send that PROMPTS does not lift the tray's hold, so the hold never ends.

The hold is a delay, not a block: once the user sends something of their own the pause they asked
for is over and whatever is still held rides on that turn's end. Removing the lift from the idle
send route restores a tray stranded for the rest of the session. The tray-landing route keeps its
own lift, so this fails the prompting case alone.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
# Anchored on CODE at both ends: the lift, and the idle route's one call into the delivery. Only
# comment lines may stand between them, so rewording that comment cannot kill the anchor, and moving
# the lift away from the send does.
lift = "            LiftTrayHold();\n"
send = "            await _delivery.SendAsync(text).ConfigureAwait(true);\n"
if s.count(send) != 1:
    sys.exit("inject target not found")
end = s.index(send)
start = s.rfind(lift, 0, end)
between = s[start + len(lift):end] if start >= 0 else None
if between is None or any(l.strip() and not l.strip().startswith("//") for l in between.split("\n")):
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s[:start] + s[start + len(lift):])
