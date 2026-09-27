"""Bug: a reply arriving out of turn on screen is drawn and never saved.

`Checkpoint` writes on turn boundaries, and a background task's "Done" is text after a toolDone with
no turnDone coming - so the saved log ends at that toolDone and leaving the conversation drops the
reply. The same reply for an owner OFF screen is saved, `RouteOffScreen` marking it dirty and arming
the flush; removing the on-screen mark restores the asymmetry that was the finding.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (!IsBusy)
                _lifetime.MarkOwnerDirty();

            Apply(ev, beforeFirstPrompt);"""
new = """            Apply(ev, beforeFirstPrompt);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
