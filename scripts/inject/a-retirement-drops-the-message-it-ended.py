"""Bug: a conversation change ends a send that has BEGUN and keeps nothing of its message.

The message had left the composer, so dropping it lost the text, the pasted image and the IDE
capture at once - and with no notice, because there was nothing to announce. Only the branch for a
send that reached `Begin` is removed here; a message still parked on the resume-choice or moved-root
banner has its own branch and its own script.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (send is not null)
                GiveBackOnce(send);
            else
                GiveBackParked(parked);"""
new = """            GiveBackParked(parked);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
