using System;
using CodeWicket.Ipc;

namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// What <see cref="SessionLifetime"/> asks of the pane it serves: the parts of a session's life that
    /// are the pane's to carry out - its MCP roster, its transcript notices, its dispatcher timer - and
    /// that the lifetime must not reach for itself, having no dispatcher and no transcript.
    /// </summary>
    /// <remarks>
    /// Called on the pane's own synchronization context, which is the lifetime's contract too. None of
    /// these may throw: a warm start's failure report runs inside the catch that keeps the warm task
    /// from faulting.
    /// </remarks>
    public interface ILifetimeHost
    {
        /// <summary>Drops the MCP roster before a session start: the new roster arrives during it.</summary>
        void ClearMcpState();

        /// <summary>A warm start opened its session, so a later failure of the same selection is news again.</summary>
        void WarmStartSucceeded();

        /// <summary>
        /// A warm start failed. Reported only while <paramref name="request"/> is still
        /// <see cref="SessionLifetime.WarmRequest"/>: a warm superseded by a picker change answers a
        /// question nobody is asking any more.
        /// </summary>
        void WarmStartFailed(StartSessionRequest request, Exception exception);

        /// <summary>
        /// The live session's owner was released: the next start, or its conversation deleted.
        /// </summary>
        /// <param name="sessionDisposed">
        /// True where the SESSION goes with the owner - a start replacing it - and false where only the
        /// owner does, a deleted conversation's session running on tombstoned. The host's permission
        /// queue turns on this: a request can be answered while its session is the one the engine holds,
        /// and not afterwards.
        /// </param>
        void LiveOwnerReleased(bool sessionDisposed);

        /// <summary>Arms the off-screen owner's flush timer if it is not already running (armed, not restarted).</summary>
        void ArmOwnerFlush();

        /// <summary>Stops the off-screen owner's flush timer.</summary>
        void DisarmOwnerFlush();

        /// <summary>
        /// A start the lifetime made itself - a warm start - ran into an engine that has exited (issue #299). The
        /// host makes the transition (<see cref="SessionLifetime.EngineExited"/>) and says so once. It is not a
        /// warm start's failure, which would name a backend for an engine that is gone.
        /// </summary>
        void EngineExited(EngineExit exit);
    }
}
