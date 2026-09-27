"""Bug: a failed steer's un-record writes a conversation the pane has left, recreating a deleted one.

A save is a creation rather than a correction - the store does CreateDirectory plus WriteAllText - so
writing the captured conversation back after removing the steer's entry puts a deleted file on disk
again and the conversation returns to the history picker. Deleting has no busy gate, and the delete
that removes the file can itself be what fails the steer in flight.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """                    if (ReferenceEquals(_host.Persisted, recordedInto))
                        _host.Store?.Save(recordedInto);"""
new = """                    _host.Store?.Save(recordedInto);"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
