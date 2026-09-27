"""Bug: "sent before the IDE tools were confirmed" is written where the WAIT ends, not where the send goes.

It is a claim about a delivery, and the send still has its commit ahead of it - so a Stop during the
wait hands the message back, the wait then times out, and the sentence is appended to a transcript
with no message under it. The same on the engine-exit route.

The log line beside it stays where it is either way: that records the wait, which did happen.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                $"[mcp] waited {clock.Elapsed.TotalSeconds:0.#} s without the IDE tools being confirmed");
            return false;"""
new = """                $"[mcp] waited {clock.Elapsed.TotalSeconds:0.#} s without the IDE tools being confirmed");
            Items.Add(new NoticeItemViewModel(IdeToolsNotConfirmedNotice));
            return false;"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
