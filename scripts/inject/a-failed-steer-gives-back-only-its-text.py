"""Bug: a mid-turn message that never reached the agent comes back as text alone.

The steer had a second, partial copy of the give-back rule: the text joined to the composer with a
bare newline, the pasted image and the IDE capture dropped, and the bubble left standing on the
transcript - so the message was on screen and in the box at once, minus half of itself. This is the
shape that shipped; the un-record beside it has its own script.
"""
import io, sys

# The escape is ASSEMBLED rather than spelled: written literally it is one backslash away from
# putting a real newline inside a C# string constant, which builds nothing and reports INCONCLUSIVE.
NL = '"' + chr(92) + 'n"'

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                GiveBackUnsentMessage(userMessage, text, attachments, contexts);"""
new = ("                InputText = string.IsNullOrEmpty(InputText) ? text : text + "
       + NL + " + InputText;")
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
