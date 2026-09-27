"""Bug: a send made BEFORE the engine exited gets the exit's card twice.

The second notice answers a gesture, so it belongs only to a send the user made knowing the engine
had gone. Read at the catch instead of at the send's start, the exit's own report has already set
the fact by then - so a send that predates the exit is told about it twice, for one event it did not
cause. This is the read that shipped, restored.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                        if (engineGoneWhenSendBegan)
                            Items.Add(EngineExitNotice(exited.Exit));"""
new = """                        if (_lifetime.EngineGone is not null)
                            Items.Add(EngineExitNotice(exited.Exit));"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
