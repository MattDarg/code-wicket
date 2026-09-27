using System;
using System.Collections.Generic;
using CodeWicket.Ipc;
using CodeWicket.Shell;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Seeds a prior conversation the way the PRODUCT records one, rather than the way a fixture finds
    /// convenient.
    /// </summary>
    /// <remarks>
    /// <para>The resume fixtures wrote one agent entry holding a whole reply, under
    /// <c>Role = "assistant"</c>. Both halves were wrong and only one of them was inert.</para>
    /// <para><b>The role is a string nothing reads</b> — <c>TranscriptText</c> branches on the
    /// ENTRY (a user's `Text`, an agent's `Event`), and <c>ResumeDecider</c> on the event's type — so
    /// "assistant" was a lie the fixtures could tell forever. That is what makes it worth fixing rather
    /// than leaving: the first code to read the role would behave one way in the product and another in
    /// every resume test, and nothing would be red.</para>
    /// <para><b>The single entry is the half that masked something now.</b> A streamed reply is recorded
    /// one entry per FRAME, and `TranscriptText` merges consecutive text frames into one
    /// <c>Assistant:</c> line, flushing at the turn's end. A one-frame fixture exercises neither the
    /// merge nor the flush — the recap handed to the summarizer looked right for a shape the product
    /// never writes.</para>
    /// </remarks>
    internal static class SeededConversation
    {
        /// <summary>The role the product writes for a streamed agent frame (<c>SessionStore</c>'s own default).</summary>
        internal const string AgentRole = "agent";

        /// <summary>
        /// One user turn and the agent's reply to it, the reply split across <paramref name="frames"/>
        /// text events and closed by a <c>turnDone</c>, exactly as a live turn is recorded.
        /// </summary>
        /// <remarks>
        /// The character total is the reply's own, whatever the split, so a fixture that sizes a
        /// transcript against <c>ResumeDecider</c>'s threshold keeps sizing it the same way.
        /// </remarks>
        internal static void AddExchange(PersistedSession session, string user, string reply, int frames = 3)
        {
            if (session is null)
                throw new ArgumentNullException(nameof(session));
            if (frames < 1)
                throw new ArgumentOutOfRangeException(nameof(frames), "a reply is at least one frame");

            session.Log.Add(new TranscriptEntry { Role = "user", Text = user });
            foreach (var frame in Split(reply, frames))
                session.Log.Add(new TranscriptEntry
                {
                    Role = AgentRole,
                    Event = new AgentEventDto { Type = "text", Text = frame },
                });
            session.Log.Add(new TranscriptEntry
            {
                Role = AgentRole,
                Event = new AgentEventDto { Type = "turnDone" },
            });
        }

        /// <summary>
        /// Cuts <paramref name="text"/> into ROUGHLY <paramref name="frames"/> pieces, the last taking
        /// the remainder. An empty reply still yields one frame, because a turn that said nothing is
        /// recorded as having said nothing rather than as not having happened.
        /// </summary>
        /// <remarks>
        /// <b>At least one, and not exactly <paramref name="frames"/> — in EITHER direction.</b> The
        /// doc says so because a later check could be written against it. The slice size rounds down, so
        /// a short reply overshoots: three characters asked for two frames yields THREE, and four asked
        /// for three yields four; a reply shorter than the count undershoots instead. What IS exact is
        /// the only thing a fixture depends on — the frames concatenate back to the reply, so the
        /// character total is the reply's own whatever the split, which <c>TranscriptTextShapeTests</c>
        /// asserts over several frame counts.
        /// </remarks>
        private static IEnumerable<string> Split(string text, int frames)
        {
            text ??= string.Empty;
            if (text.Length == 0 || frames == 1)
            {
                yield return text;
                yield break;
            }

            var size = Math.Max(1, text.Length / frames);
            for (var at = 0; at < text.Length; at += size)
            {
                var remaining = text.Length - at;
                // The last frame takes everything left, so an uneven split never yields a stray tail.
                var take = at + size + size > text.Length ? remaining : size;
                yield return text.Substring(at, take);
                if (take == remaining)
                    yield break;
            }
        }
    }
}
