"""Bug: a steer opens the out-of-turn working window without saying so in engine.log.

The window has two openers - a live frame arriving with no turn of ours running, and a steer, which
arms it before the request goes out. Only the first wrote a line, so a steered episode left a
closing line with no opening one: the exact signature of a window that would not close, read off an
episode that closed perfectly well. Removing the line at the steer's opener restores that.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (!IsAgentWorkingOutOfTurn)
                _diagnosticLog?.Invoke(
                    $"[out-of-turn] opened by 'steer'; quiet window {CurrentQuietWindow.TotalSeconds:0.#}s");

"""
new = ""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
