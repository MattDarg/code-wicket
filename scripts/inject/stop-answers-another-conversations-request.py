"""Bug: Stop cancels permission requests belonging to another conversation (issue #256).

Stop aborts THIS conversation's work. A request kept from the one the user left is not this one's
to answer, and denying it is damage where the person pressing Stop is not looking.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            CancelPermissionRequests(ownOnly: true);"""
new = """            CancelPermissionRequests();"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
