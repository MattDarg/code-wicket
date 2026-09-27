"""Bug: the tray's hold is read only once nothing is working.

A cancelled turn ends when the prompt returns, not when the button is pressed, and a cancel can
orphan an MCP call that never answers at all. Reading the hold after IsAgentWorking leaves the tray
saying "Queued - sending when this turn ends" for that whole stretch, which is precisely what the
gate has made impossible: they go on the user's next message. Putting the hold back below the
working branch restores it.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """                if (_trayHold != TrayHold.None)
                    return (_trayHold switch"""
new = """                if (_trayHold != TrayHold.None && !IsAgentWorking)
                    return (_trayHold switch"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
