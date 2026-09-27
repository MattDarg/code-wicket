"""Bug: a history open answers, for the user, every permission request still open.

The session those requests came from is untouched by a history open - it starts none - so issue
#256's rule applies: asked and labelled, never cancelled. Cancelling denies work the user launched
and can still see the result of, in the conversation they just left. Dropping the condition
restores the shipped behaviour, where both clears cancelled everything.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            if (openRequests == OpenRequests.Cancel)
                CancelPermissionRequests();
            _editsByKey.Clear();"""
new = """            CancelPermissionRequests();
            _editsByKey.Clear();"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
