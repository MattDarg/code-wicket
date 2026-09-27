"""Bug: a resume banner's Cancel holds the tray as though the user had pressed Stop.

Both gestures end the send the same way, so one enum member covered both - and the tray then told
someone who had pressed Cancel that the turn was stopped, and moved the pill they had set. That is
issue #253's shape one gesture over: a user hunting a Stop they never pressed. Pointing the two
banners' Cancel back at the Stop member restores it.
"""
import io, sys
P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                cancel: () => ChooseResumeFallback(ResumeFallback.BackedOut));"""
new = """                cancel: () => ChooseResumeFallback(ResumeFallback.Stopped));"""
if s.count(old) != 2:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new))
