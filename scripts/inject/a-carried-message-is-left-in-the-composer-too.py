"""Bug: a "Start new conversation" answer sends the parked message AND leaves it in the composer.

Both fresh answers - the resume-choice banner's and the moved-root banner's - carry the parked
message across the clear by hand. The clear ends the parked send, which hands its message back to
the composer, so the field has to be emptied before the clear rather than merely read: left set, the
message goes into the new conversation and stays in the box to be sent a second time.

Both call sites are the same rule, so they are injected together; the two tests that fail name the
two routes.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                    var parked = _parked;
                    _parked = null;"""
new = """                    var parked = _parked;"""
if s.count(old) != 2:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new))
