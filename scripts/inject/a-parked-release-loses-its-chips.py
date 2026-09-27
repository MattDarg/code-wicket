"""Bug: backing out of the banner a released batch parked on drops its pictures and captures.

A typed message's chips stay in the composer while the banner is up, so its give-back is the text
alone. A released batch took its chips OUT of the tray when it was held, so the parked message is
the only thing holding them and the give-back has to put them back. Cutting the parked form down to
the text alone is what a release inherits from the typed one.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (!message.StillInTheComposer)
                GiveBackChips(message.Attachments, message.Contexts);"""
new = """            if (false)
                GiveBackChips(message.Attachments, message.Contexts);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
