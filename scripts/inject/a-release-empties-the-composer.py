"""Bug: parking a released batch empties the composer, which is not that message's box.

"The composer still holds this message" is STATED, by whichever factory made the message: FromComposer
says yes, FromTray says no, and nothing else decides it. The mistake this puts back is the factory
working it out from the batch instead - a release that carried no pictures then reads as the
composer's, and parking it empties a box holding something else entirely.

It was once derived that way, from the chips being absent rather than empty, which read correctly and
left three expressions each re-deriving the same distinction. The flag replaced all three; this
injection is the bare mistake at the one place the fact is now decided.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                new ParkedMessage(stillInTheComposer: false, text, preamble, deliveryNote, attachments, contexts);"""
new = """                new ParkedMessage(stillInTheComposer: attachments.Count == 0, text, preamble, deliveryNote, attachments, contexts);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
