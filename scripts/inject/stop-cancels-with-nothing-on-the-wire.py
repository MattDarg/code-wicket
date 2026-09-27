"""Bug: Stop sends the engine a cancel with none of this conversation's work outstanding.

With nothing on the wire the cancel lands on whatever the engine holds, which can be another
conversation's work going on off screen (issue #256) - damage where the person pressing Stop is not
looking. Removing the condition restores the shipped shape, where the rule was upheld only by the
branches above and by the working bar's definition.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (!hasWorkOnTheWire)
                return;

            try { await _engine.CancelAsync().ConfigureAwait(true); }"""
new = """            try { await _engine.CancelAsync().ConfigureAwait(true); }"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
