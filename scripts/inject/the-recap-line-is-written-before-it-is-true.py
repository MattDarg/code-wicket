"""Bug: "a condensed recap was sent instead of the full history" is written before any send happens.

Written where the recap is BUILT, the line precedes the session start, the wait for our IDE tools and
the commit - so a send stopped or retired in any of them leaves the claim standing over a transcript
with no message under it. Written at the commit it cannot: there is no gap left between the claim and
the delivery it describes.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                            summaryBlock = ChatViewModel.BuildSummaryBlock(summary, priorRoot);
                            send.ResumedFromSummary = true;"""
new = """                            summaryBlock = ChatViewModel.BuildSummaryBlock(summary, priorRoot);
                            Items.Insert(Items.IndexOf(userMessage), new NoticeItemViewModel(
                                "Resumed from a summary — a condensed recap was sent instead of the full history."));"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
