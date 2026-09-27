"""Bug: the carry across a conversation replacement keeps the held messages' TEXT and not the messages.

A route that replaces the conversation on the user's behalf snapshots the tray and puts it back. The
tray's own contents are the messages, and a message holds its pictures and its IDE captures - the
tray took them off the composer when it held it, so they exist nowhere else. Rebuilding from the text
is the plausible version of "carry the tray", and it loses them in silence: the row is still there,
still says what the user typed, and the paperclip is simply gone.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            var held = _host.PendingMessages.ToList();"""
new = """            var held = _host.PendingMessages
                .Select(m => new PendingMessageViewModel(m.Text, r => _host.PendingMessages.Remove(r)))
                .ToList();"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
