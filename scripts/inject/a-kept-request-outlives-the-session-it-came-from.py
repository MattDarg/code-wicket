"""Bug: a request kept across a history open is left on screen after its session is disposed.

Keeping it is only honest while the session that asked it is the one the engine holds. The moment a
start replaces that session the banner asks the user to decide something no backend will ever hear
the answer to. Removing the cancel at the one place a session is disposed restores that.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (sessionDisposed)
                CancelPermissionRequests();"""
new = """            """
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
