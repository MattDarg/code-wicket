"""Bug: a conversation change ends a pending send but leaves the pane busy until its call returns.

The send is over - the message is back in the composer and nothing will be prompted - but the bar
stays up and Stop stays live on its account for as long as the abandoned call takes. Measured at
about twenty seconds against a real summarizer, which is the summarizer's latency and nothing to do
with the rule: what is wrong is the ORDER, not the number. This is the shape that shipped, the
ending leaving IsBusy to whatever was still in flight.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (wasPending)
                _host.SendEnded();"""
new = """            if (false)
                _host.SendEnded();"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
