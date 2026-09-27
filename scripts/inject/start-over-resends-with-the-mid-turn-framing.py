"""Bug: the message a resume banner sends into a NEW conversation keeps its mid-turn framing.

The message may have reached the send that started this over as an interrupt or an aside, which
puts a <mid-turn-message> block on the wire - "whatever you were doing was cut short, deal with
this first" - and a note on the bubble saying so. That describes a turn in the conversation being
LEFT. Carried across it is the first thing a brand-new agent reads, about work it never did.
Restoring the pass-through of the send's own preamble puts the WIRE half back.

The BUBBLE half can no longer be injected, and that is a fact about the code rather than a gap
here: `OutgoingSend` no longer carries the delivery note at all, so there is nothing on the send to
pass through. The check still asserts it - a bubble with a note would fail
`Assert.Null(shown.DeliveryNote)` - but the defect as it shipped is now unreachable by
construction, which is stronger than an injection.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                    _delivery.StartOverInNewConversation(text, send.Attachments, send.Contexts);"""
new = """                    _delivery.StartOverInNewConversation(text, send.Attachments, send.Contexts, send.Preamble);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))

P2 = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s2 = io.open(P2, encoding="utf-8").read()
old2 = """            string text,
            IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts)
        {"""
new2 = """            string text,
            IReadOnlyList<AttachmentViewModel> attachments, IReadOnlyList<ContextItemViewModel> contexts,
            string? preamble = null)
        {"""
old3 = """            _ = _host.SendCoreAsync(text, preamble: null, deliveryNote: null, attachments, contexts);"""
new3 = """            _ = _host.SendCoreAsync(text, preamble, deliveryNote: null, attachments, contexts);"""
if s2.count(old2) != 1 or s2.count(old3) != 1:
    sys.exit("inject target not found")
io.open(P2, "w", encoding="utf-8", newline="").write(s2.replace(old2, new2, 1).replace(old3, new3, 1))
