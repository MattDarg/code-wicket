"""Bug: a send that parked on a resume banner goes out with no framing.

A tray release is the only send that has any - a preamble telling the agent its work was cut short,
and the note on the bubble saying so. The park held the text alone, so a release answered at the
banner reached the wire as though it had just been typed: the agent is not told, and the bubble
carries no note. Passing nulls at the one call the park re-enters puts it back.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                message.Text, message.Preamble, message.DeliveryNote,"""
new = """                message.Text, preamble: null, deliveryNote: null,"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
