"""Bug: the refused-reload report is written where the banner is answered, not where the message goes.

It says a condensed recap "was sent to a new session" - or, on the other variant, that this message
"starts a new session" - and both are claims about a delivery that has not happened yet: the send
still has the wait for our IDE tools and the commit ahead of it. Written there, a Stop in that window
leaves the claim over a transcript with no message under it.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                            _lifetime.ForgetRefusedReload();
                            send.ReloadFallbackReason = refusal;
                            send.ReloadFallbackUsedRecap = true;"""
new = """                            _lifetime.ForgetRefusedReload();
                            _host.ReportResumeFallback(refusal, Items.IndexOf(userMessage), resumedFromSummary: true);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
