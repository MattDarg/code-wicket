using System.Collections.Generic;

namespace CodeWicket.Core
{
    /// <summary>
    /// One entry of a conversation IMPORTED from a backend's own history — the replay a
    /// <c>session/load</c> sends before it responds, captured rather than dropped so a conversation
    /// started in the backend's terminal CLI can be opened here (issue #108).
    /// <para>Shaped to match the shell's persisted <c>TranscriptEntry</c> deliberately: a user prompt
    /// carries <see cref="Text"/>, an agent event carries <see cref="Event"/>, and
    /// <see cref="Role"/> takes the same two words. An import is a transcript we did not record, so
    /// what it produces has to be indistinguishable from one we did — otherwise the host needs a
    /// second render path, which is the thing every other replay rule here exists to avoid.</para>
    /// </summary>
    public sealed record ImportedTurnEntry(string Role, string? Text, AgentEvent? Event)
    {
        /// <summary>The <see cref="Role"/> of a prompt the user sent.</summary>
        public const string UserRole = "user";

        /// <summary>The <see cref="Role"/> of a streamed agent event.</summary>
        public const string AgentRole = "agent";

        /// <summary>A message the user sent, as the backend replayed it.</summary>
        public static ImportedTurnEntry User(string text) => new(UserRole, text, null);

        /// <summary>One mapped agent event, the same shape a live turn would have produced.</summary>
        public static ImportedTurnEntry Agent(AgentEvent ev) => new(AgentRole, null, ev);

        /// <summary>
        /// Joins consecutive <see cref="UserRole"/> entries into one. ONE message the user sent can
        /// replay as SEVERAL frames — measured against claude-agent-acp, a message with an image and
        /// text arrives as two <c>user_message_chunk</c>s — so the capture is per-chunk while a
        /// transcript entry is per-message, and without this an ordinary send comes back as two
        /// bubbles that the user never typed separately.
        /// <para>Run BEFORE the entries cross the wire, so the list a host receives is 1:1 with the
        /// <c>TranscriptEntry</c> shape it already renders and no second join rule has to exist on the
        /// far side. An agent entry between two user entries ends the run: that is a real turn
        /// boundary, and joining across it would put the user's reply above the answer it followed.</para>
        /// </summary>
        public static IReadOnlyList<ImportedTurnEntry> Coalesce(IReadOnlyList<ImportedTurnEntry> entries)
        {
            if (entries is null || entries.Count == 0)
                return System.Array.Empty<ImportedTurnEntry>();

            var joined = new List<ImportedTurnEntry>(entries.Count);
            foreach (var entry in entries)
            {
                var last = joined.Count > 0 ? joined[joined.Count - 1] : null;
                if (entry.Role == UserRole && last is not null && last.Role == UserRole)
                {
                    // Newline, not a space: the chunks are separate content blocks, and a text block
                    // following an image marker reads as a caption rather than as the same sentence.
                    joined[joined.Count - 1] = last with { Text = last.Text + "\n" + entry.Text };
                    continue;
                }

                joined.Add(entry);
            }

            return joined;
        }
    }
}
