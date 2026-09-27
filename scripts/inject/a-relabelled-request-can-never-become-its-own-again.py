"""Bug: the request's origin is one-way, so reopening its conversation leaves it labelled as another's.

"Whose is this" has three answers and one of them is NOBODY. Returning early when the resolver says
nobody leaves a re-attached conversation's own banner saying the reply is recorded elsewhere, and
outside Stop's scope - so Stop cancels that conversation's turn and leaves its banner standing,
which is a regression against the behaviour before the keep. Restoring the early return restores it.
"""
import io, sys
P = "src/CodeWicket.UI/ViewModels/ChatViewModel.cs"
s = io.open(P, encoding="utf-8").read()
old = """            var resolved = _lifetime.ResolveOrigin();

            foreach (var entry in _permissionQueue)"""
new = """            var resolved = _lifetime.ResolveOrigin();
            if (resolved.title is null)
                return;

            foreach (var entry in _permissionQueue)"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
