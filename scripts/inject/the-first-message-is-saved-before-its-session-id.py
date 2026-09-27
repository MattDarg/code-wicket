"""Bug: a new conversation's first message is written to disk before the session it went out on.

The commit stamped the backend session's id and provider onto the conversation AFTER the record had
already saved it, and saved again. Between the two writes the file held the user's first message
with no conversation id - a state a restore cannot resume from, so a copy read from there is a
transcript with nothing behind it and the reload the user chose is gone. Restoring the second save
puts it back; only a brand-new conversation can show it, its id not existing until the session
starts.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            RecordOnce(send, send.Starting ? send.Live : null);

            if (send.Starting)
            {
                PendingResume = null;
                _resumeStrategy = ResumeStrategy.Fresh;
            }"""
new = """            RecordOnce(send);

            if (send.Starting)
            {
                PendingResume = null;
                _resumeStrategy = ResumeStrategy.Fresh;

                if (_host.Persisted is { } persisted && send.Live is { } live)
                {
                    persisted.ConversationId = live.ConversationId;
                    persisted.ProviderId = _host.SelectedProvider?.Id ?? persisted.ProviderId;
                    ChatViewModel.TrackProvider(persisted, persisted.ProviderId);
                    _host.Store?.Save(persisted);
                }
            }"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
