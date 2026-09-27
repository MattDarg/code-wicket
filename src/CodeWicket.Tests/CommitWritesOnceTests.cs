using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// A send's commit writes its conversation ONCE, carrying the message and the backend session it
    /// was sent under together.
    /// </summary>
    /// <remarks>
    /// <para>The commit used to write twice: the record saved the conversation, and the block that
    /// stamps the session's id and provider onto it saved again a few statements later. The second
    /// write is not what matters — <b>the first one is</b>, because for the few milliseconds between
    /// them the file on disk held the user's first message and NO conversation id, and that is a state
    /// a restore cannot resume from. Read back from there the conversation is a transcript with nothing
    /// behind it, and the full reload the user chose is simply gone.</para>
    /// <para>So this pins the CONTENT of every write rather than counting them. A count says the fix is
    /// in place; the content says what the fix was for, and stays true if the save ever moves again.</para>
    /// </remarks>
    public sealed class CommitWritesOnceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-commit-writes-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>
        /// A brand-new conversation, whose id only exists once the session has started: the case where
        /// the two writes differed. A resumed one already carries its id, so it could never show this.
        /// </summary>
        [Fact]
        public void TheFirstMessageIsNeverWrittenWithoutTheSessionItWentOutOn() => RunSta(() =>
        {
            var store = new RecordingStore(new FileSessionStore(_dir));
            var engine = new ScriptedEngine().WithProvider("fake", "Fake");
            var vm = new ChatViewModel(
                engine,
                new StartSessionRequest("fake", null, _dir, "Prompt", null),
                sessionStore: store);
            vm.InitializeAsync().GetAwaiter().GetResult();
            Drain();

            vm.InputText = "what changed?";
            vm.SendCommand.Execute(null);
            DrainAll();

            Assert.Contains(engine.Prompts, p => p.Contains("what changed?", StringComparison.Ordinal));
            var orphaned = store.Writes
                .Where(w => w.LogCount > 0 && string.IsNullOrEmpty(w.ConversationId))
                .ToList();
            Assert.True(
                orphaned.Count == 0,
                "the conversation was written with its message and no backend session id, "
                + orphaned.Count + " time(s); every write: "
                + string.Join(", ", store.Writes.Select(w => $"[log {w.LogCount}, id '{w.ConversationId}']")));
        });

        /// <summary>
        /// Forwards to a real store and records what each write CONTAINED at the moment it was made.
        /// Snapshotted rather than held by reference: the conversation is one mutable object, so a list
        /// of references would read every entry as the final state and could never see a partial one.
        /// </summary>
        private sealed class RecordingStore : ISessionStore
        {
            private readonly ISessionStore _inner;

            public RecordingStore(ISessionStore inner) => _inner = inner;

            public List<(int LogCount, string? ConversationId)> Writes { get; } = new();

            public IReadOnlyList<SessionSummary> List(string workspaceRootPath) => _inner.List(workspaceRootPath);

            public PersistedSession? Load(string workspaceRootPath, string sessionId) =>
                _inner.Load(workspaceRootPath, sessionId);

            public void Save(PersistedSession session)
            {
                Writes.Add((session.Log.Count, session.ConversationId));
                _inner.Save(session);
            }

            public void Delete(string workspaceRootPath, string sessionId) => _inner.Delete(workspaceRootPath, sessionId);
        }

        private static void Drain() =>
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        private static void DrainAll()
        {
            for (var i = 0; i < 4; i++)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void RunSta(Action action) => StaTest.Run(action, withDispatcherContext: true);
    }
}
