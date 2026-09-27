"""Bug: deleting the conversation on screen answers its open permission requests.

Its session runs on, tombstoned, so the request is still asked and still answerable - it just says
the conversation is one the user deleted. Dropping the condition restores the cancel that took it
away.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (openRequests == OpenRequests.Cancel)
                CancelPermissionRequests();
            ResetLaunchTracking();"""
new = """            CancelPermissionRequests();
            ResetLaunchTracking();"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
