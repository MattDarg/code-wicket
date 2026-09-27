"""Bug: the text a resume hands the summarizer breaks one reply into a line per streamed frame.

A reply is recorded one entry per frame, so the transcript text has to put the frames back together.
Flushing at each text entry instead tells the summarizer the agent spoke several times, and that
recap is the whole of what the next agent knows about the exchange. This is the shape the resume
fixtures could not catch: they seeded a whole reply as one entry, so there was nothing to merge.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                else if (entry.Event is { Type: "text", Text: { } text })
                {
                    assistant = (assistant ?? string.Empty) + text;
                }"""
new = """                else if (entry.Event is { Type: "text", Text: { } text })
                {
                    assistant = (assistant ?? string.Empty) + text;
                    FlushAssistant();
                }"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
