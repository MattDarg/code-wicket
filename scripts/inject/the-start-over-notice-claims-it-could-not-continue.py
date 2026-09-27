"""Bug: the "Started a new conversation" notice says the conversation COULDN'T be continued.

Four routes reach that notice, and on two of them something else was on offer - a full reload after
a failed recap, a recap after a refused reload - and the user picked a new conversation over it. So
the notice asserts an impossibility that is false, which is what made the old sentence contradict
its own parenthesis ("...couldn't be continued (a new conversation was chosen over reloading it in
full)"). Putting the verb back restores the claim.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = "” wasn't continued "
new = "” couldn't be continued "
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
