"""Bug: the removal sits where a send stops being PENDING rather than where one ENDS.

`PrepareAsync`'s finally runs for both outcomes - the send is about to prompt, or it ended - so the
helper called from there takes the lines away from a send that SUCCEEDED too, leaving a committed
prompt with no account in the transcript of the PROGRESS it reported: the "Summarizing..." and
"Resuming..." lines go, and the transcript reads as though the resume never happened.

Re-pointed after the outcome lines moved to the commit. It used to be recorded against the line
naming the resume achieved, which is now written AFTER this finally runs - out of the injected
removal's reach - at which point this script came back PINS NOTHING without anything about it having
changed. A script goes quietly dead when its subject moves; the verdict is on the pair.

The place, not the width: an-ending-removes-every-notice-the-send-inserted.py guards the other
mistake, and neither check covers the other.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                if (ReferenceEquals(_pending, send))
                {
                    _pending = null;
                    Phase = null;
                }"""
new = """                if (ReferenceEquals(_pending, send))
                {
                    DropResumeNotices(send);
                    _pending = null;
                    Phase = null;
                }"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
