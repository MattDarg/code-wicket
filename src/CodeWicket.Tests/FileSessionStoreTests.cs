using System;
using System.IO;
using System.Linq;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The disk-backed session store, the source of truth for conversation persistence — a regression
    /// here silently loses the user's transcripts. Uses a scratch root (the store's constructor takes
    /// one) so nothing touches %APPDATA%. Pins the save/load round trip, the per-workspace key isolation
    /// (different paths that share a leaf name must NOT collide), the "never throws / skip the bad file"
    /// robustness, and the picker metadata (newest-first, user-turn count).
    /// </summary>
    public sealed class FileSessionStoreTests : IDisposable
    {
        private readonly string _root;

        public FileSessionStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cwkt-sessiontests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort scratch cleanup */ }
        }

        private FileSessionStore NewStore() => new(_root);

        private static PersistedSession Session(string workspace, string? id = null, string title = "T")
        {
            var s = new PersistedSession { WorkspaceRootPath = workspace, Title = title };
            if (id is not null)
                s.Id = id;
            return s;
        }

        private static TranscriptEntry UserEntry(string text) => new() { Role = "user", Text = text };
        private static TranscriptEntry AgentEntry(string type = "text") => new() { Role = "agent", Event = new AgentEventDto { Type = type } };

        [Fact]
        public void SaveThenLoad_RoundTripsSessionAndLog()
        {
            var store = NewStore();
            var session = Session("C:\\src\\app", id: "sess1");
            session.ProviderId = "kiro";
            session.ConversationId = "conv-42";
            session.Log.Add(UserEntry("hello"));
            session.Log.Add(AgentEntry());

            store.Save(session);
            var loaded = store.Load("C:\\src\\app", "sess1");

            Assert.NotNull(loaded);
            Assert.Equal("sess1", loaded!.Id);
            Assert.Equal("kiro", loaded.ProviderId);
            Assert.Equal("conv-42", loaded.ConversationId);
            Assert.Equal(2, loaded.Log.Count);
            Assert.Equal("hello", loaded.Log[0].Text);
            Assert.Equal("text", loaded.Log[1].Event!.Type);
        }

        [Fact]
        public void Save_StampsUpdatedUtc()
        {
            var store = NewStore();
            var session = Session("C:\\src\\app", id: "s");
            session.UpdatedUtc = DateTime.UtcNow.AddDays(-10);
            var before = DateTime.UtcNow;

            store.Save(session);

            Assert.True(store.Load("C:\\src\\app", "s")!.UpdatedUtc >= before);
        }

        [Fact]
        public void Load_MissingSession_ReturnsNull()
        {
            Assert.Null(NewStore().Load("C:\\src\\app", "nope"));
        }

        // Two different workspaces that happen to share a leaf folder name must map to distinct storage
        // dirs, so their sessions never bleed into each other's history.
        [Fact]
        public void SameLeafName_DifferentPath_DoesNotCollide()
        {
            var store = NewStore();
            store.Save(Session("C:\\projects\\app", id: "a"));
            store.Save(Session("D:\\other\\app", id: "b"));

            var first = store.List("C:\\projects\\app");
            var second = store.List("D:\\other\\app");

            Assert.Equal("a", Assert.Single(first).Id);
            Assert.Equal("b", Assert.Single(second).Id);
        }

        // The workspace key normalizes the path (trailing slash + case), so the "same" workspace written
        // one way is found when queried another.
        [Fact]
        public void WorkspaceKey_IsCaseAndTrailingSlashInsensitive()
        {
            var store = NewStore();
            store.Save(Session("C:\\Src\\App\\", id: "s"));

            Assert.NotNull(store.Load("c:\\src\\app", "s"));
            Assert.Single(store.List("c:\\src\\app"));
        }

        [Fact]
        public void List_ReturnsNewestFirst()
        {
            var store = NewStore();
            store.Save(Session("C:\\ws", id: "older"));
            store.Save(Session("C:\\ws", id: "newer"));

            // Save() always stamps UpdatedUtc = UtcNow, so set distinct times on disk (what List sorts
            // by) directly — and in save order that does NOT match the expected sort, to prove the sort.
            StampUpdatedUtc("C:\\ws", "older", DateTime.UtcNow.AddMinutes(-5));
            StampUpdatedUtc("C:\\ws", "newer", DateTime.UtcNow);

            var list = store.List("C:\\ws");

            Assert.Equal(new[] { "newer", "older" }, list.Select(s => s.Id).ToArray());
        }

        // Rewrites a saved session's UpdatedUtc directly on disk, bypassing Save()'s UtcNow stamp, so a
        // sort-order test can pin deterministic timestamps.
        private void StampUpdatedUtc(string workspace, string id, DateTime updatedUtc)
        {
            var dir = Directory.EnumerateDirectories(_root).Single();
            var file = Path.Combine(dir, id + ".json");
            var session = System.Text.Json.JsonSerializer.Deserialize<PersistedSession>(
                File.ReadAllText(file), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            session.UpdatedUtc = updatedUtc;
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
                session, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }

        // MessageCount is the user-turn count (a cheap "how big" hint), not the total log length.
        [Fact]
        public void List_MessageCount_CountsUserTurnsOnly()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s");
            session.Log.Add(UserEntry("one"));
            session.Log.Add(AgentEntry());
            session.Log.Add(AgentEntry("toolStart"));
            session.Log.Add(UserEntry("two"));
            store.Save(session);

            Assert.Equal(2, Assert.Single(store.List("C:\\ws")).MessageCount);
        }

        // The picker surfaces every backend a conversation ran on, in order, de-duplicated — so a
        // summary-resume onto a second agent shows both.
        [Fact]
        public void List_ProviderIds_ReflectsTrackedAgentsInOrder()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s");
            session.ProviderId = "claude-code";
            session.ProviderIds.Add("kiro");
            session.ProviderIds.Add("claude-code");
            session.ProviderIds.Add("kiro"); // duplicate later use collapses
            store.Save(session);

            var summary = Assert.Single(store.List("C:\\ws"));
            Assert.Equal(new[] { "kiro", "claude-code" }, summary.ProviderIds.ToArray());
        }

        // A session saved before ProviderIds existed still shows its single agent (falls back to ProviderId).
        [Fact]
        public void List_ProviderIds_FallsBackToSingleProviderId_ForLegacySessions()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s");
            session.ProviderId = "kiro"; // no ProviderIds populated
            store.Save(session);

            var summary = Assert.Single(store.List("C:\\ws"));
            Assert.Equal(new[] { "kiro" }, summary.ProviderIds.ToArray());
        }

        [Fact]
        public void List_UnknownWorkspace_ReturnsEmpty()
        {
            Assert.Empty(NewStore().List("C:\\never\\saved"));
        }

        // A corrupt/half-written file must be skipped, not sink the whole listing.
        [Fact]
        public void List_SkipsCorruptFileButKeepsGoodOnes()
        {
            var store = NewStore();
            store.Save(Session("C:\\ws", id: "good"));

            // Drop a garbage .json into the same workspace dir the store used.
            var dir = Directory.EnumerateDirectories(_root).Single();
            File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{ this is not valid json");

            var list = store.List("C:\\ws");

            Assert.Equal("good", Assert.Single(list).Id);
        }

        [Fact]
        public void Delete_RemovesSession()
        {
            var store = NewStore();
            store.Save(Session("C:\\ws", id: "s"));

            store.Delete("C:\\ws", "s");

            Assert.Null(store.Load("C:\\ws", "s"));
            Assert.Empty(store.List("C:\\ws"));
        }

        [Fact]
        public void Delete_MissingSession_IsNoOp()
        {
            var ex = Record.Exception(() => NewStore().Delete("C:\\ws", "ghost"));
            Assert.Null(ex);
        }

        [Fact]
        public void Save_OverwritesExistingSession()
        {
            var store = NewStore();
            store.Save(Session("C:\\ws", id: "s", title: "first"));
            store.Save(Session("C:\\ws", id: "s", title: "second"));

            Assert.Equal("second", store.Load("C:\\ws", "s")!.Title);
            Assert.Single(store.List("C:\\ws")); // overwrite, not a second file
        }

        // An id containing path-invalid characters must still produce a usable, round-trippable file.
        [Fact]
        public void SessionId_WithInvalidPathChars_IsSanitizedAndRoundTrips()
        {
            var store = NewStore();
            var weirdId = "a/b:c*d";
            store.Save(Session("C:\\ws", id: weirdId));

            Assert.NotNull(store.Load("C:\\ws", weirdId));
        }

        // ---- Header-only listing -----------------------------------------------------------------
        //
        // List() reads each file's leading fields and stops at "log". These pin the three things that
        // makes true, each of which fails differently: that the log really is never parsed, that the
        // header can be found however far in it runs, and that a file written before the count existed
        // still lists (with the count absent rather than zero).

        // The direct proof that the transcript is not read: the log here is not valid JSON at all, and
        // the row still comes back. A full parse cannot produce this - it throws and the file is
        // skipped - so this assertion cannot pass by accident of the old behaviour.
        [Fact]
        public void List_ReadsHeaderOnly_AndNeverParsesTheLog()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s", title: "Header only");
            session.Log.Add(UserEntry("one"));
            store.Save(session);

            var file = SessionFile("s");
            var json = File.ReadAllText(file);
            var logAt = json.IndexOf("\"log\"", StringComparison.Ordinal);
            Assert.True(logAt > 0, "the serialized session must carry a log property");
            File.WriteAllText(file, json.Substring(0, logAt) + "\"log\": [ this is not json at all");

            var summary = Assert.Single(store.List("C:\\ws"));
            Assert.Equal("Header only", summary.Title);
            Assert.Equal(1, summary.MessageCount);
        }

        // The whole design rests on the transcript being the LAST property written, so a property added
        // after it would sit behind the point the reader stops and vanish from the picker. Pinned here
        // rather than left to member order, which is what the JsonPropertyOrder attribute defends.
        [Fact]
        public void SerializedSession_PutsTheLogLast()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s");
            session.ConversationId = "conv";
            session.ProviderId = "kiro";
            session.Log.Add(UserEntry("one"));
            store.Save(session);

            var json = File.ReadAllText(SessionFile("s"));
            var logAt = json.IndexOf("\"log\"", StringComparison.Ordinal);
            Assert.True(logAt > 0);
            foreach (var property in new[] { "id", "title", "conversationId", "providerId", "updatedUtc", "userMessageCount" })
            {
                var at = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);
                Assert.True(at > 0, property + " should be serialized");
                Assert.True(at < logAt, property + " must be written before the log");
            }
        }

        // A session saved before the count was recorded: absent, NOT zero. The distinction is the whole
        // reason the field is nullable - a zero would render as "0 msgs" against a conversation whose
        // title is its own first message.
        [Fact]
        public void List_MessageCount_IsNullWhenTheFileNeverRecordedIt()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s", title: "Older save");
            session.Log.Add(UserEntry("one"));
            session.Log.Add(UserEntry("two"));
            store.Save(session);

            var file = SessionFile("s");
            var json = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(file), "\"userMessageCount\"\\s*:\\s*\\d+,?", string.Empty);
            Assert.DoesNotContain("userMessageCount", json);
            File.WriteAllText(file, json);

            var summary = Assert.Single(store.List("C:\\ws"));
            Assert.Null(summary.MessageCount);
            Assert.Equal("Older save", summary.Title); // the rest of the header still reads
        }

        // The buffer starts at 1 KB and grows. A header past that has to be found by the refill loop,
        // not truncated into a skipped file - so this drives several grows and asserts the fields that
        // sit AFTER the oversized one still arrive.
        [Fact]
        public void List_FindsTheHeader_WhenItRunsPastTheInitialBuffer()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s", title: new string('t', 200_000));
            session.ConversationId = "conv-after-the-big-field";
            session.Log.Add(UserEntry("one"));
            store.Save(session);

            // Corrupting the log is what makes this pin anything: without it a reader that gave up on
            // the oversized header would fall through to the full parse and return the very same row,
            // so the assertion could not tell the refill loop working from it never running at all.
            var file = SessionFile("s");
            var json = File.ReadAllText(file);
            var logAt = json.IndexOf("\"log\"", StringComparison.Ordinal);
            File.WriteAllText(file, json.Substring(0, logAt) + "\"log\": [ not json either");

            var summary = Assert.Single(store.List("C:\\ws"));
            Assert.Equal(200_000, summary.Title.Length);
            Assert.Equal("conv-after-the-big-field", summary.ConversationId);
            Assert.Equal(1, summary.MessageCount);
        }

        // Both routes to a row - the header read and the full-parse fallback - build ProviderIds, so
        // they must agree. Asserted against the fallback's own input rather than trusting one of them.
        [Fact]
        public void List_ProviderHistory_MatchesBetweenHeaderAndFallback()
        {
            var store = NewStore();
            var session = Session("C:\\ws", id: "s");
            session.ProviderId = "claude-code";
            session.ProviderIds.Add("kiro");
            session.ProviderIds.Add("claude-code");
            store.Save(session);

            var viaHeader = Assert.Single(store.List("C:\\ws"));
            Assert.Equal(new[] { "kiro", "claude-code" }, viaHeader.ProviderIds.ToArray());

            // The same file with no providerIds array at all: the fallback to the single providerId is
            // the pre-tracking shape, and the header reader has to reproduce it.
            var json = File.ReadAllText(SessionFile("s"));
            File.WriteAllText(SessionFile("s"), System.Text.RegularExpressions.Regex.Replace(
                json, "\"providerIds\"\\s*:\\s*\\[[^\\]]*\\],?", string.Empty));

            var legacy = Assert.Single(store.List("C:\\ws"));
            Assert.Equal(new[] { "claude-code" }, legacy.ProviderIds.ToArray());
        }

        // A genuinely corrupt file is still skipped rather than failing the listing - the header reader
        // must not turn "unreadable" into "throws out of List".
        [Fact]
        public void List_SkipsAFileWhoseHeaderIsMalformed()
        {
            var store = NewStore();
            var good = Session("C:\\ws", id: "good", title: "Good");
            good.Log.Add(UserEntry("one"));
            store.Save(good);
            var bad = Session("C:\\ws", id: "bad");
            store.Save(bad);
            File.WriteAllText(SessionFile("bad"), "{ \"id\": \"bad\", \"title\": ");

            var summary = Assert.Single(store.List("C:\\ws"));
            Assert.Equal("Good", summary.Title);
        }

        private string SessionFile(string id) =>
            Path.Combine(Directory.EnumerateDirectories(_root).Single(), id + ".json");

    }
}
