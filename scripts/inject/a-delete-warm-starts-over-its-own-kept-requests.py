"""Bug: the on-screen delete keeps its open requests and disposes them in the same gesture.

The delete clears the pane back to a fresh conversation, which warm-starts - and a warm start
disposes the session the engine holds, which is the tombstoned session whose survival is the whole
of #256's tombstone. So the keep lasted until the end of the same click, and the request the banner
had just been re-labelled with was cancelled. Removing the guard restores that.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (_permissionQueue.Count > 0)
                return;

            // Only the paths that would certainly start fresh at send-time"""
new = """            // Only the paths that would certainly start fresh at send-time"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
