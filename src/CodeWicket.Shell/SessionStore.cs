using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeWicket.Ipc;
using CodeWicket.Core;

namespace CodeWicket.Shell
{
    /// <summary>
    /// One entry in a persisted transcript: either a user prompt or a single streamed agent event.
    /// The log is an ordered append of these; replaying it through the UI's event pipeline rebuilds
    /// the exact transcript (messages, tool rows, edits, plans).
    /// </summary>
    public sealed class TranscriptEntry
    {
        /// <summary>"user" (a typed prompt in <see cref="Text"/>) or "agent" (a streamed <see cref="Event"/>).</summary>
        public string Role { get; set; } = "agent";

        /// <summary>The user's prompt text, when <see cref="Role"/> is "user".</summary>
        public string? Text { get; set; }

        /// <summary>The streamed agent event, when <see cref="Role"/> is "agent".</summary>
        public AgentEventDto? Event { get; set; }

        /// <summary>
        /// Images sent with a user prompt (issue #118), as PATHS into the attachment directory rather
        /// than as data. The log is re-read in full whenever the history picker enumerates, so inlining
        /// a couple of megabytes of base64 per prompt would be paid for on every open, forever, to draw
        /// a thumbnail. Null on every entry saved before this existed, and on one that carried none.
        /// <para>A path can go stale — retention sweeps the directory on its own schedule — so a
        /// restored attachment renders when the file is still there and degrades to a plain chip when
        /// it is not.</para>
        /// </summary>
        public List<AttachmentEntry>? Attachments { get; set; }

        /// <summary>
        /// IDE context the user attached to a user prompt (issue #73, rung 2) - a break-mode capture
        /// today, an editor selection or a diagnostics slice later.
        /// <para>Stored INLINE, unlike <see cref="Attachments"/>, and the difference is size rather
        /// than taste: an image is megabytes, so the log stores a path and pays an indirection that
        /// can rot; a bounded capture is a few KB, so the log stores the thing itself and a restored
        /// context is as complete as a fresh one. Null on every entry saved before this existed, and
        /// on one that carried none.</para>
        /// </summary>
        public List<ContextEntry>? Contexts { get; set; }
    }

    /// <summary>
    /// One attached piece of IDE context. Enough to render it again AND to hand it to a different
    /// agent on a summary resume, which is why the text is here rather than a reference to it.
    /// </summary>
    public sealed class ContextEntry
    {
        /// <summary>
        /// Which source produced it, as the prompt block's tag name (e.g. <c>debug-state</c>).
        /// <para><b>Nothing on the replay path reads this.</b> A chip is rebuilt from
        /// <see cref="Label"/> and <see cref="Text"/> alone, so a kind written by a later build
        /// renders correctly here instead of being silently dropped. It is recorded for a future
        /// reader that genuinely has to discriminate.</para>
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The chip's caption, as it was shown when the message was sent.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>The capture itself - the body of the block that went on the wire.</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>One saved attachment: enough to render it again, and nothing more.</summary>
    public sealed class AttachmentEntry
    {
        /// <summary>The name shown to the user (never sent to the agent — ACP has no field for it).</summary>
        public string Name { get; set; } = string.Empty;

        public string MimeType { get; set; } = string.Empty;

        /// <summary>
        /// The file NAME under the attachment directory, not a path — <see cref="AttachmentStore.StoredForm"/>
        /// writes it and <see cref="AttachmentStore.ResolveStored"/> reads it back, so the directory can
        /// move (it did, at the rename) without stranding every image ever pasted. A log written before
        /// that change holds an absolute path instead, which is why the resolve reads both shapes.
        /// <para><b>Never pass this to <c>File.Exists</c>.</b> A bare name resolves against the process's
        /// working directory — in the VSIX, devenv's install folder — so the answer is a confident
        /// false for a file that is sitting in the store.</para>
        /// </summary>
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>
    /// A saved conversation for a workspace: metadata plus an ordered transcript log that can be
    /// replayed to restore the chat. <see cref="ConversationId"/> is kept for a future "resume the
    /// agent's own context" follow-up; the display-only restore doesn't use it.
    /// </summary>
    public sealed class PersistedSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string WorkspaceRootPath { get; set; } = string.Empty;

        /// <summary>
        /// The directory the agent actually ran in — normally <see cref="WorkspaceRootPath"/>, but it
        /// can sit above it when the backend's workspace marker does (issue #54). Persisted because a
        /// restored transcript is replayed BEFORE any session opens, and its relative paths must root
        /// against the directory the agent used. Null on sessions saved before this was tracked, which
        /// falls back to <see cref="WorkspaceRootPath"/> — the pre-fix behaviour.
        /// Deliberately NOT part of the store's per-workspace key: history stays bucketed by the
        /// solution, so an existing conversation never disappears from the picker.
        /// </summary>
        public string? AgentWorkingDirectory { get; set; }

        /// <summary>
        /// The user chose, on THIS open, to keep this conversation in <see cref="AgentWorkingDirectory"/>
        /// when the agent would otherwise have run somewhere else (issue #185): its history lives there,
        /// and a full reload works only from there. <b>Never written to disk</b> — it lives on the loaded
        /// object, so it cannot leak to another conversation and a reopen from the store asks again.
        /// A persisted flag was built first and reverted (user, 2026-09-12): it pinned every later
        /// reopen with no way back but editing this file, which is the remedy issue #185 was filed
        /// against — and for a small conversation, where no banner precedes the full reload, it took
        /// the summary and fresh routes away entirely.
        /// </summary>
        [JsonIgnore]
        public bool PinWorkingDirectoryThisOpen { get; set; }

        public string Title { get; set; } = DefaultTitle;

        /// <summary>The backend this conversation currently runs on (the most recent one it was
        /// resumed onto). See <see cref="ProviderIds"/> for the full history.</summary>
        public string? ProviderId { get; set; }

        /// <summary>
        /// Every backend this conversation has run on, in first-used order and de-duplicated. Usually
        /// a single entry, but a Summary-resume can carry the conversation onto a different agent
        /// (fresh session + injected recap), so this can hold more than one. Empty for sessions saved
        /// before this was tracked — callers fall back to <see cref="ProviderId"/>.
        /// </summary>
        public List<string> ProviderIds { get; set; } = new();

        public string? ModelId { get; set; }
        public string? PermissionMode { get; set; }
        public string? ConversationId { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// How many user turns the <see cref="Log"/> holds, recorded so the history picker never has to
        /// read the log to find out. <b>Written by <see cref="FileSessionStore.Save"/> and by nothing
        /// else</b> — derived state with one writer cannot drift from the list it describes.
        /// <para>
        /// Null means <b>not recorded</b>, never zero: sessions saved before this field existed have no
        /// count, and the picker omits the segment rather than claiming an empty conversation. It is
        /// deliberately not backfilled — re-reading the log to supply it would reinstate the whole cost
        /// this field exists to remove, on precisely the oldest and largest files.
        /// </para>
        /// </summary>
        public int? UserMessageCount { get; set; }

        /// <summary>
        /// The last context-window fill the backend reported for this conversation, 0-100, or null when
        /// none was ever reported.
        /// <para>
        /// <b>History, not live state.</b> It replaces recording the usage stream itself: 1,571 usage
        /// events across 27 of 29 sessions, 14% of the store by bytes, whose only lasting value was
        /// this one number - and which, being replayed on restore, drew a dead conversation's figure on
        /// the live context ring. Written by the chat view-model on the live path only.
        /// </para>
        /// <para>
        /// <b>A sample of a sawtooth.</b> Backends compact their context in place (issue #85), which
        /// drops the fill sharply, so this is the reading at the moment the conversation was last
        /// active and NOT a measure of how large the conversation is. That is why nothing decides
        /// anything from it - <c>ResumeDecider</c> deliberately keeps weighing resumes on the
        /// transcript's own size, which compaction cannot move.
        /// </para>
        /// </summary>
        public double? LastContextPercent { get; set; }

        /// <summary>
        /// The transcript. <b>Must serialize last</b> — <see cref="FileSessionStore.List"/> reads the
        /// header with a streaming reader that stops here, so a property written after it would be
        /// invisible to the picker. Pinned by the attribute rather than left to member order, which a
        /// later edit could silently change (the header is ~600 bytes against a log measured in MB).
        /// </summary>
        [JsonPropertyOrder(int.MaxValue)]
        public List<TranscriptEntry> Log { get; set; } = new();

        /// <summary>Placeholder title before the first user message names the session.</summary>
        public const string DefaultTitle = "New session";
    }


    /// <summary>
    /// The leading fields of a saved session - everything the history picker shows - read without
    /// touching the transcript that follows them.
    /// <para>
    /// This exists because <see cref="PersistedSession.Log"/> is the last property written and dwarfs
    /// everything before it. A <see cref="Utf8JsonReader"/> walking the top-level object can collect
    /// every header field and stop the moment it reaches <c>log</c>, which on a real store means
    /// reading a few hundred bytes of a file measured in megabytes.
    /// </para>
    /// </summary>
    internal sealed class SessionHeader
    {
        /// <summary>
        /// How far into a file the header is allowed to run before the streaming read gives up and the
        /// caller falls back to a full parse.
        /// <para>Unreachable in practice - the longest header field is the title, which the host caps
        /// at 60 characters - so this is a safety net, not a budget: a file whose header somehow ran
        /// past it must still produce a row, because a conversation silently missing from the picker is
        /// a worse outcome than a slow one.</para>
        /// </summary>
        private const int HeaderReadLimitBytes = 1024 * 1024;

        private const int InitialBufferBytes = 1024;

        public string Id { get; private set; } = string.Empty;
        public string Title { get; private set; } = PersistedSession.DefaultTitle;
        public DateTime UpdatedUtc { get; private set; }
        public int? UserMessageCount { get; private set; }
        public double? LastContextPercent { get; private set; }
        public string? ConversationId { get; private set; }
        public string? ProviderId { get; private set; }
        public List<string> ProviderIds { get; } = new();

        /// <summary>
        /// The ordered, de-duplicated set of backends this conversation ran on. Mirrors
        /// <c>FileSessionStore.ProviderHistory</c> exactly, including its fall back to the single
        /// <see cref="ProviderId"/> for sessions saved before the list existed - the two paths produce
        /// the same rows, so they must answer this the same way.
        /// </summary>
        public IReadOnlyList<string> ProviderHistory()
        {
            var ids = ProviderIds
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0 && !string.IsNullOrEmpty(ProviderId))
                ids.Add(ProviderId!);
            return ids;
        }

        /// <summary>
        /// Reads one file's header. False means "this needs the full parse" - the header ran past
        /// <see cref="HeaderReadLimitBytes"/>, or the document ended before <c>log</c> was reached.
        /// A malformed document throws, which <c>FileSessionStore.List</c> treats as it always has:
        /// skip the file rather than fail the listing.
        /// </summary>
        public static bool TryRead(string path, out SessionHeader header)
        {
            header = new SessionHeader();
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, InitialBufferBytes);

            var length = stream.Length;
            var capacity = InitialBufferBytes;
            while (true)
            {
                var buffer = new byte[capacity];
                stream.Position = 0;
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0)
                        break;
                    read += n;
                }

                // isFinalBlock only when the buffer holds the whole file: with more to come, a reader
                // that runs out of data reports it by returning false, which is the signal to grow.
                // Told (wrongly) that the file ends here, the same truncation would throw instead.
                var complete = read >= length;
                var parsed = new SessionHeader();
                if (parsed.TryParse(buffer, read, isFinalBlock: complete))
                {
                    header = parsed;
                    return true;
                }

                if (complete || capacity >= HeaderReadLimitBytes)
                    return false;

                capacity = (int)Math.Min((long)capacity * 4, HeaderReadLimitBytes);
            }
        }

        // Walks the top-level object, filling fields until "log" is reached. Returns false when the
        // buffer ran out first; the caller re-reads with a bigger one. Restarting the parse from the
        // start of the buffer on each grow costs nothing at these sizes and avoids carrying a
        // JsonReaderState across refills for a document that is, in practice, read once.
        private bool TryParse(byte[] buffer, int count, bool isFinalBlock)
        {
            var reader = new Utf8JsonReader(
                new ReadOnlySpan<byte>(buffer, 0, count),
                isFinalBlock,
                default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            while (true)
            {
                if (!reader.Read())
                    return false;
                if (reader.TokenType == JsonTokenType.EndObject)
                    return true; // a session with no log at all - every header field is still valid
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return false;

                var name = reader.GetString();

                // The transcript, and the point of all this: everything the picker needs is behind us.
                if (string.Equals(name, "log", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!reader.Read())
                    return false;

                switch (name?.ToLowerInvariant())
                {
                    case "id":
                        Id = reader.GetString() ?? string.Empty;
                        break;
                    case "title":
                        Title = reader.GetString() ?? PersistedSession.DefaultTitle;
                        break;
                    case "updatedutc":
                        if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var updated))
                            UpdatedUtc = updated;
                        break;
                    case "usermessagecount":
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var messages))
                            UserMessageCount = messages;
                        break;
                    case "lastcontextpercent":
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var fill))
                            LastContextPercent = fill;
                        break;
                    case "conversationid":
                        ConversationId = reader.GetString();
                        break;
                    case "providerid":
                        ProviderId = reader.GetString();
                        break;
                    case "providerids":
                        if (!ReadProviderIds(ref reader))
                            return false;
                        break;
                    default:
                        if (!reader.TrySkip())
                            return false;
                        break;
                }
            }
        }

        private bool ReadProviderIds(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                return reader.TokenType == JsonTokenType.Null || reader.TrySkip();

            while (true)
            {
                if (!reader.Read())
                    return false;
                if (reader.TokenType == JsonTokenType.EndArray)
                    return true;
                if (reader.TokenType == JsonTokenType.String)
                    ProviderIds.Add(reader.GetString() ?? string.Empty);
                else if (!reader.TrySkip())
                    return false;
            }
        }
    }

    /// <summary>Lightweight header for the history picker (no transcript body loaded).</summary>
    public sealed class SessionSummary
    {
        public SessionSummary(
            string id, string title, DateTime updatedUtc, int? messageCount, long sizeBytes,
            IReadOnlyList<string> providerIds, string? conversationId = null,
            double? lastContextPercent = null)
        {
            Id = id;
            Title = title;
            UpdatedUtc = updatedUtc;
            MessageCount = messageCount;
            SizeBytes = sizeBytes;
            ProviderIds = providerIds;
            ConversationId = conversationId;
            LastContextPercent = lastContextPercent;
        }

        public string Id { get; }
        public string Title { get; }
        public DateTime UpdatedUtc { get; }

        /// <summary>The backend(s) this conversation ran on, in first-used order (usually one; more when a
        /// Summary-resume carried it onto another agent). Empty when the saved session predates tracking.</summary>
        public IReadOnlyList<string> ProviderIds { get; }

        /// <summary>
        /// The BACKEND's own id for this conversation, when one was ever established. Carried on the
        /// summary purely so the picker can tell that a conversation offered by the backend's CLI is
        /// one we already hold (issue #108) - every session we create is in the CLI's store too, so
        /// without this every one of them is offered back as if it were foreign.
        /// <para>Null for a conversation whose session never opened, and for anything saved before the
        /// id was persisted. Both re-appear as foreign, which is wrong in the direction that merely
        /// offers a duplicate rather than hiding a conversation.</para>
        /// </summary>
        public string? ConversationId { get; }

        /// <summary>
        /// Number of user turns — a cheap "how big is this" hint for the picker. <b>Null means not
        /// recorded</b> (a session saved before the count was persisted), never zero; the row omits the
        /// segment rather than showing "0 msgs" for a conversation that plainly has messages.
        /// </summary>
        public int? MessageCount { get; }

        /// <summary>On-disk size of the saved session (bytes) — a rough proxy for resume weight.</summary>
        public long SizeBytes { get; }

        /// <summary>
        /// Context fill when the conversation was last active, or null when the backend never reported
        /// one (and for every session saved before it was kept). See
        /// <see cref="PersistedSession.LastContextPercent"/> for why it is history rather than a size.
        /// </summary>
        public double? LastContextPercent { get; }
    }

    /// <summary>Persists conversations per workspace so the transcript survives tool-window/VS restarts.</summary>
    public interface ISessionStore
    {
        /// <summary>Session headers for a workspace, newest first.</summary>
        IReadOnlyList<SessionSummary> List(string workspaceRootPath);

        /// <summary>Loads a full session, or null if missing/unreadable.</summary>
        PersistedSession? Load(string workspaceRootPath, string sessionId);

        /// <summary>Creates or overwrites a session (stamps <see cref="PersistedSession.UpdatedUtc"/>).</summary>
        void Save(PersistedSession session);

        /// <summary>Removes a session; a no-op if it doesn't exist.</summary>
        void Delete(string workspaceRootPath, string sessionId);
    }

    /// <summary>
    /// Disk-backed <see cref="ISessionStore"/>: one JSON file per session under
    /// <c>%APPDATA%\code-wicket\sessions\&lt;workspace&gt;\&lt;id&gt;.json</c> (the root is
    /// overridable for tests/hosts). Best-effort throughout — a locked/unwritable/corrupt store must
    /// never disrupt the chat, mirroring <see cref="ExtensionConfig"/>.
    /// </summary>
    public sealed class FileSessionStore : ISessionStore
    {
        private static readonly JsonSerializerOptions Json =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private readonly string _root;

        /// <param name="rootDirectory">Storage root; null uses <c>%APPDATA%\code-wicket\sessions</c>.</param>
        public FileSessionStore(string? rootDirectory = null) => _root = rootDirectory ?? DefaultRoot;

        /// <summary>Default storage root, alongside <c>config.json</c>.</summary>
        public static string DefaultRoot => Path.Combine(StoragePaths.Roaming, "sessions");

        /// <summary>
        /// Summarizes every saved conversation for a workspace, newest first.
        /// <para>
        /// <b>Reads only each file’s header, not its transcript.</b> Every field a row needs sits in
        /// the first few hundred bytes (measured: <c>"log"</c> begins at byte 527–580 across a real
        /// store), so deserializing the whole document to build a summary read ~99.98% of the bytes for
        /// nothing — 12 MB across 29 files on this repo’s own workspace, on the UI thread, every time
        /// the picker opened. The one field that used to require the log — the user-turn count — is now
        /// recorded in the header by <see cref="Save"/>.
        /// </para>
        /// </summary>
        public IReadOnlyList<SessionSummary> List(string workspaceRootPath)
        {
            var result = new List<SessionSummary>();
            try
            {
                var dir = new DirectoryInfo(WorkspaceDir(workspaceRootPath));
                if (!dir.Exists)
                    return result;

                // EnumerateFiles yields FileInfo, so the size comes from the directory walk the
                // enumeration already performed rather than a separate stat per file.
                foreach (var file in dir.EnumerateFiles("*.json"))
                {
                    try
                    {
                        if (TryReadSummary(file, out var summary))
                            result.Add(summary!);
                    }
                    catch
                    {
                        // Skip a corrupt/half-written file rather than failing the whole list.
                    }
                }
            }
            catch
            {
                // Unreadable store → empty history.
            }

            result.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
            return result;
        }

        // One file → one row. Header-only where possible; a full parse is the fallback for a header
        // too large to be one (see HeaderReadLimitBytes), so a pathological file costs time rather
        // than vanishing from the picker.
        private static bool TryReadSummary(FileInfo file, out SessionSummary? summary)
        {
            summary = null;
            if (SessionHeader.TryRead(file.FullName, out var header))
            {
                summary = new SessionSummary(
                    header.Id, header.Title, header.UpdatedUtc, header.UserMessageCount,
                    file.Length, header.ProviderHistory(), header.ConversationId,
                    header.LastContextPercent);
                return true;
            }

            var session = JsonSerializer.Deserialize<PersistedSession>(File.ReadAllText(file.FullName), Json);
            if (session is null)
                return false;

            summary = new SessionSummary(
                session.Id, session.Title, session.UpdatedUtc,
                session.UserMessageCount ?? session.Log.Count(e => e.Role == "user"),
                file.Length, ProviderHistory(session), session.ConversationId,
                session.LastContextPercent);
            return true;
        }

        // The ordered, de-duplicated set of backends a session ran on. Prefers the tracked list; falls
        // back to the single ProviderId for sessions saved before ProviderIds existed.
        private static IReadOnlyList<string> ProviderHistory(PersistedSession session)
        {
            var ids = session.ProviderIds
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0 && !string.IsNullOrEmpty(session.ProviderId))
                ids.Add(session.ProviderId!);
            return ids;
        }

        public PersistedSession? Load(string workspaceRootPath, string sessionId)
        {
            try
            {
                var path = SessionPath(workspaceRootPath, sessionId);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<PersistedSession>(File.ReadAllText(path), Json)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public void Save(PersistedSession session)
        {
            try
            {
                session.UpdatedUtc = DateTime.UtcNow;

                // The header's copy of the count, refreshed here and nowhere else. Derived from the log
                // at the moment the log is written, so it cannot drift and no caller can forget it.
                session.UserMessageCount = session.Log.Count(e => e.Role == "user");
                var path = SessionPath(session.WorkspaceRootPath, session.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(session, Json));
            }
            catch
            {
                // Best effort: a failed write must not break the session.
            }
        }

        public void Delete(string workspaceRootPath, string sessionId)
        {
            try
            {
                var path = SessionPath(workspaceRootPath, sessionId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort.
            }
        }

        private string SessionPath(string workspaceRootPath, string sessionId) =>
            Path.Combine(WorkspaceDir(workspaceRootPath), Sanitize(sessionId) + ".json");

        private string WorkspaceDir(string workspaceRootPath) =>
            Path.Combine(_root, WorkspaceKey(workspaceRootPath));

        // Stable per-workspace folder: a readable leaf-name prefix + a short hash of the normalized
        // full path, so different workspaces never collide even when their leaf folder names match.
        private static string WorkspaceKey(string workspaceRootPath)
        {
            var normalized = (workspaceRootPath ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
            var name = Sanitize(Path.GetFileName(normalized));
            if (string.IsNullOrEmpty(name))
                name = "workspace";

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var shortHash = BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            return name + "-" + shortHash;
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
