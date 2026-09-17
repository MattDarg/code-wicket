using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Replaying a saved conversation has to reproduce its SHAPE, not just its content — who said what,
    /// in which bubble. The replay runs the saved log back through the same <c>Apply</c> pipeline that
    /// builds the live transcript, and that pipeline keeps a streaming assistant message open across
    /// events so consecutive deltas grow one bubble. A user turn has to close it, exactly as sending
    /// does live; nothing else in the loop will.
    /// </summary>
    public class TranscriptReplayTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(AppContext.BaseDirectory, "scratch", "replay-" + Guid.NewGuid().ToString("N"));

        public TranscriptReplayTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* scratch */ }
        }

        /// <summary>
        /// The reply that follows a user message must be its own bubble. Without the streaming message
        /// being closed on a user turn, it is appended to the assistant bubble from BEFORE the question —
        /// so on screen the answer appears above the thing it answers, glued to the previous reply, and
        /// the user's message reads as though it was never answered at all.
        /// <para>
        /// Only reproducible with no tool call between the two replies: every tool/edit/plan event closes
        /// the streaming message on its way through <c>Apply</c>, which is why this survived — most turns
        /// contain one, and a plain question-and-answer pair is the case that does not.
        /// </para>
        /// </summary>
        [Fact]
        public void AReplyAfterAUserTurnIsItsOwnBubble() => RunSta(() =>
        {
            var store = new FileSessionStore(Path.Combine(_root, "sessions"));
            var session = new PersistedSession
            {
                WorkspaceRootPath = _root,
                AgentWorkingDirectory = _root,
                Title = "Replayed",
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "first question" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "text", Text = "FIRST ANSWER" },
            });
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "second question" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "text", Text = "SECOND ANSWER" },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("fake", null, _root, "Prompt", null),
                sessionStore: store);

            vm.RestoreMostRecentSession();

            var messages = vm.Items.OfType<MessageItemViewModel>().ToList();
            var assistants = messages.Where(m => m.IsAssistant).ToList();

            // Two questions, two answers — not one bubble carrying both answers.
            Assert.Equal(2, assistants.Count);
            Assert.DoesNotContain("SECOND ANSWER", assistants[0].Text, StringComparison.Ordinal);

            // And the order has to read as a conversation: each answer BELOW the question it answers.
            var order = messages.Select(m => m.IsUser ? "u" : "a").ToList();
            Assert.Equal(new[] { "u", "a", "u", "a" }, order);
        });

        // ---- helpers --------------------------------------------------------------------------

        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>A replay must not touch a backend, so every call here is a failure.</summary>
        private sealed class OfflineEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not summarize.");
        }
    }
}
