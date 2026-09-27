"""Bug: a tray release enters below the resume decision, so only Enter decides.

Every send runs ResumeDecider against the lifetime's current answer. A release went
straight to the turn runner instead, which on its own is a send taking no decision - and with a
resume strategy left standing by an earlier Cancel it was a held batch silently reloading a whole
conversation into the agent, counting toward usage, with no banner and no notice. Routing the
release back at the turn runner puts it back.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                    : _delivery.SendReleasedAsync(text, preamble, note, attachments, contexts);"""
new = """                    : SendCoreAsync(text, preamble, note, attachments, contexts);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
