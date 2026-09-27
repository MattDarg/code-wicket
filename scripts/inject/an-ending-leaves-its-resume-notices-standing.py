"""Bug: a send that ends leaves behind what it said about the resume it was attempting.

"Summarizing this conversation to resume from a recap...", "Resuming this conversation...", and the
line naming the recap it sent all describe THIS send. Stop, a banner's Cancel and a conversation
change each end it with nothing recorded and the message back in the composer - and the lines stay,
so the transcript says the pane is summarizing for a send that no longer exists. The engine-exit
route is where it is worst: it is the one conversation change that clears nothing, so the chat
stays on screen insisting it is working in an engine that has gone.

Emptying the helper is one guard, not three: each of its three call sites is pinned by its own
check, measured by removing each alone.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (send is null)
                return;
            foreach (var notice in send.TakeResumeNotices())
                Items.Remove(notice);"""
new = """            if (send is null)
                return;"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
