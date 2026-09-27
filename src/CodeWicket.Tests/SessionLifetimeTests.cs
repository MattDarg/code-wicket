using System;
using System.Collections.Generic;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.Shell.Sessions;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The session lifetime's turn lease, proved at the lifetime itself: no warm start while a turn is
    /// outstanding. No view-model test could prove the gate while the view-model had an <c>IsBusy</c> gate of
    /// its own refusing the same warm start, so these are the proof that let that gate be removed.
    /// </summary>
    public sealed class SessionLifetimeTests : InvariantCheckedTest
    {
        private static readonly StartSessionRequest Fresh =
            new StartSessionRequest("claude-code", null, @"C:\repo", "default", null);

        private readonly ScriptedEngine _engine = new ScriptedEngine();
        private readonly List<string> _log = new List<string>();

        // One log, written by the sink the lifetime reports through, as the view-model wires it: the breach's line is asserted where the product writes it.
        private SessionLifetime NewLifetime() =>
            new SessionLifetime(_engine, store: null, _host, Invariants.Logged(_log.Add, Breaches), _log.Add);

        private readonly QuietHost _host = new QuietHost();

        private static readonly EngineExit Exit = new EngineExit(3, new[] { "[engine] unhandled" });

        private static PromptedTurn Commit(SessionLifetime lifetime) =>
            lifetime.Commit(lifetime.CurrentToken, () => null)!;

        /// <summary>
        /// A workspace move retires the turn on the wire, and the warm start for the new root must wait
        /// for that turn to return (issue #217): starting a session is what disposes the old one.
        /// </summary>
        [Fact]
        public void ARetiredTurnHoldsTheWarmStartUntilItReturns()
        {
            var lifetime = NewLifetime();
            var turn = Commit(lifetime);

            // What a workspace move does to a turn still on the wire: the conversation is replaced under its lease.
            lifetime.ConversationReplaced();
            Assert.True(lifetime.IsTurnRetired);

            lifetime.WarmStart(Fresh);
            Assert.Equal(0, _engine.StartCount);

            Assert.True(turn.Return().WasRetired);
            Assert.False(lifetime.IsTurnRetired);

            lifetime.WarmStart(Fresh);
            Assert.Equal(1, _engine.StartCount);
        }

        /// <summary>
        /// A second return of a turn already returned does nothing. Were it to realign the epochs again, it
        /// would un-retire the NEXT turn a later move retired, and let that turn's frames into the new chat.
        /// </summary>
        [Fact]
        public void ReturnIsIdempotent()
        {
            var lifetime = NewLifetime();
            var turn = Commit(lifetime);
            lifetime.ConversationReplaced();
            var first = turn.Return();
            Assert.True(first.WasRetired);

            var next = Commit(lifetime);
            lifetime.ConversationReplaced();

            Assert.Same(first, turn.Return());
            Assert.True(lifetime.IsTurnRetired);
            lifetime.WarmStart(Fresh);
            Assert.Equal(0, _engine.StartCount);

            Assert.True(next.Return().WasRetired);
        }

        /// <summary>
        /// A lease still outstanding at the next commit is a missed return. It never breaks the pane: one
        /// unconditional log line, the lease voided, one breach, and the new lease issued. The voided lease
        /// returning late then releases nothing that belongs to the new one.
        /// </summary>
        [Fact]
        public void CommitOverAnOutstandingTurnLogsVoidsAndProceeds()
        {
            var lifetime = NewLifetime();
            lifetime.Display(new PersistedSession { Title = "Fix the build" });
            var stale = Commit(lifetime);

            var next = Commit(lifetime);

            var line = Assert.Single(_log);
            Assert.StartsWith("[lifetime] stale turn lease voided: a turn committed ", line, StringComparison.Ordinal);
            Assert.EndsWith("s ago for 'Fix the build' never returned; proceeding", line, StringComparison.Ordinal);
            Assert.Equal(new[] { line }, Breaches.Drain());
            Assert.NotSame(stale, next);
            Assert.False(lifetime.IsTurnRetired);

            Assert.False(stale.Return().WasRetired);
            lifetime.WarmStart(Fresh);
            Assert.Equal(0, _engine.StartCount);

            next.Return();
            lifetime.WarmStart(Fresh);
            Assert.Equal(1, _engine.StartCount);
        }

        /// <summary>
        /// A send whose conversation was replaced before its prompt cannot commit. Nothing it would have
        /// done to the conversation runs, and the pane's session is not counted as prompted.
        /// </summary>
        [Fact]
        public void CommitRefusesAStaleToken()
        {
            var lifetime = NewLifetime();
            var token = lifetime.CurrentToken;
            lifetime.ConversationReplaced();

            var ran = false;
            var turn = lifetime.Commit(token, () =>
            {
                ran = true;
                return null;
            });

            Assert.Null(turn);
            Assert.False(ran);
            Assert.False(lifetime.IsPrompted);
            Assert.False(lifetime.HasOutstandingTurn);
        }

        /// <summary>
        /// A session adopted for a send is Open until that send commits, and only the commit makes it Prompted.
        /// </summary>
        [Fact]
        public void AnAdoptedSessionIsOpenUntilItsSendCommits()
        {
            var lifetime = NewLifetime();
            lifetime.Adopt(Fresh, new StartSessionResponse("conv"), prompted: false);

            Assert.True(lifetime.HasSession);
            Assert.False(lifetime.IsPrompted);

            Commit(lifetime).Return();
            Assert.True(lifetime.IsPrompted);
        }

        /// <summary>
        /// EngineGone: an engine whose process has exited is never asked for a session again.
        /// A warm start is a no-op, and a send's start fails at once with the exit, rather than each finding out
        /// by trying (issue #299). Terminal: only a new view-model, on a new engine, leaves it.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task AnExitedEngineIsNeverAskedForASession()
        {
            var lifetime = NewLifetime();
            Assert.True(lifetime.EngineExited(Exit));
            Assert.False(lifetime.EngineExited(Exit));
            Assert.Same(Exit, lifetime.EngineGone);

            lifetime.WarmStart(Fresh);
            var take = await Assert.ThrowsAsync<EngineExitedException>(
                () => lifetime.TakeWarmOrStartAsync(Fresh, lifetime.CurrentToken));
            Assert.Same(Exit, take.Exit);
            var start = await Assert.ThrowsAsync<EngineExitedException>(() => lifetime.StartEngineSessionAsync(Fresh));
            Assert.Same(Exit, start.Exit);

            Assert.Equal(0, _engine.StartCount);
        }

        /// <summary>
        /// A turn outstanding when the engine exits will never return in any useful sense: its lease is
        /// voided, so nothing reads the pane as holding a turn. Its late return, from the prompt's failure, is harmless.
        /// </summary>
        [Fact]
        public void AnEngineExitVoidsTheOutstandingLease()
        {
            var lifetime = NewLifetime();
            var turn = Commit(lifetime);

            lifetime.EngineExited(Exit);

            Assert.False(lifetime.HasOutstandingTurn);
            Assert.False(turn.Return().WasRetired);
        }

        /// <summary>
        /// A warm start that runs into the exit before the exit's own report reaches the pane is the exit, not a
        /// warm start that failed: the host is told so, and says it once. Reported as a failed start it named the
        /// backend ("Kiro couldn't start: …") for an engine that was gone, beside the exit's own notice.
        /// </summary>
        [Fact]
        public void AWarmStartThatRunsIntoTheExitReportsTheExit()
        {
            var lifetime = NewLifetime();
            _engine.StartFails = new EngineExitedException(Exit);

            lifetime.WarmStart(Fresh);

            Assert.Same(Exit, Assert.Single(_host.Exits));
        }

        private sealed class QuietHost : ILifetimeHost
        {
            public void ClearMcpState()
            {
            }

            public void WarmStartSucceeded()
            {
            }

            public void WarmStartFailed(StartSessionRequest request, Exception exception) =>
                throw new InvalidOperationException("The scripted start failed: " + exception.Message, exception);

            public void LiveOwnerReleased(bool sessionDisposed)
            {
            }

            public void ArmOwnerFlush()
            {
            }

            public void DisarmOwnerFlush()
            {
            }

            public List<EngineExit> Exits { get; } = new List<EngineExit>();

            public void EngineExited(EngineExit exit) => Exits.Add(exit);
        }
    }
}
