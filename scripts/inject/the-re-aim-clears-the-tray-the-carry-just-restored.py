"""Bug: the re-aim restore runs AFTER the carry, clearing the tray the carry just put back.

The workspace route carries the tray across its clear, and then - outside that carry - re-aims at the
new root by restoring its most recent conversation. That restore goes through LoadSession, the call a
history CLICK uses, which clears the tray like the gesture it usually is. So the carry put the
messages back and the next statement took them away again: the same silent loss one line further on.

Only reachable on the SECOND of the two moves a solution switch makes, and only when the new root has
a conversation to load - which is why a check that collapses the switch to one move cannot see it.
"""
import io, sys

P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                if (!_workspaceRestartPending)
                    RestoreMostRecentSession();
            });"""
new = """            });

            if (!_workspaceRestartPending)
                RestoreMostRecentSession();"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
