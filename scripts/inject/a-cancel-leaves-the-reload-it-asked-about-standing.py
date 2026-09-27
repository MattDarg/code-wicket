"""Bug: cancelling the resume-choice or moved-root banner leaves the resume strategy standing.

The Cancel resets the phase, the banner, the tray and the composer text. Leaving the STRATEGY behind
means the next send is no longer a first continuation, so nothing asks again and the full reload the
user had just declined runs silently on whatever is sent next. Removing the reset puts it back; the
sibling rule for the two banners past Begin (BackOutOfResume) is untouched, so this says which of
the two the check reads.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            _resumeStrategy = ResumeStrategy.Fresh;
            _host.HoldTray(TrayHold.BackedOut);"""
new = """            _host.HoldTray(TrayHold.BackedOut);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
