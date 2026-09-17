using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeWicket.Ide
{
    /// <summary>How a solution update (build, rebuild, clean) ended, as the build manager reported it.</summary>
    internal readonly struct SolutionUpdateOutcome
    {
        public SolutionUpdateOutcome(bool succeeded, bool cancelled)
        {
            Succeeded = succeeded;
            Cancelled = cancelled;
        }

        /// <summary><c>fSucceeded</c> from <c>UpdateSolution_Done</c>: every project's update succeeded.</summary>
        public bool Succeeded { get; }

        /// <summary>The update was cancelled inside Visual Studio (Build → Cancel), by anyone.</summary>
        public bool Cancelled { get; }
    }

    /// <summary>
    /// Completion of ONE solution update, taken from the build manager's own
    /// <see cref="IVsUpdateSolutionEvents.UpdateSolution_Done"/> rather than from polling
    /// <c>SolutionBuild.BuildState</c> (issue #250).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an event and not the state poll the build phase uses.</b> <c>build_solution</c> with
    /// <c>rebuild:true</c> used to run <c>Clean(WaitForCleanToFinish: true)</c> on the UI thread: a
    /// synchronous, unbounded, recursive delete of every <c>bin</c>/<c>obj</c> in the solution, which is
    /// the operation AGENTS.md forbids on that thread. The obvious replacement, <c>Clean(false)</c> plus
    /// the existing <c>BuildState == Done</c> poll, has a race the old comment named without solving: if
    /// the state has not yet moved to <c>InProgress</c> when the poll first reads it, the poll returns
    /// at once and the build starts DURING the clean, the compiler racing the deletion of its own
    /// inputs. That failure is silent and intermittent, which is worse than the freeze it replaces.
    /// Nothing offline can say whether <c>Clean(false)</c> flips the state before returning, and the
    /// build manager may legitimately DEFER an update (a solution still loading), so a poll cannot be
    /// made safe by ordering alone.
    /// </para>
    /// <para>
    /// <b>The event is race-free by construction, in both directions.</b> The sink is advised BEFORE the
    /// update is started, so a completion cannot be missed, however fast the clean; and
    /// <c>UpdateSolution_Done</c> cannot fire before the update has ended, however late it began. The
    /// other shape the issue floated, "wait for a transition INTO <c>InProgress</c> and then out of it",
    /// fails the first direction: a clean that finishes inside one 400 ms poll interval never shows
    /// <c>InProgress</c> to a poller at all, and the wait then runs to the timeout.
    /// </para>
    /// <para>
    /// <b>What the sink must not do.</b> The callbacks run on the UI thread inside the build manager's
    /// own dispatch. Completing the task there is fine; RUNNING the caller's continuation there is not,
    /// since that continuation starts the next update while the manager is still unwinding the last.
    /// Hence <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>: the awaiting code resumes
    /// off this stack, switches back to the UI thread through the dispatcher, and only then goes on.
    /// The caller still confirms <c>BuildState</c> is no longer <c>InProgress</c> before starting the
    /// build, so both mechanisms have to agree; neither alone is trusted.
    /// </para>
    /// <para>
    /// <b>One waiter per update.</b> A sink advised across two updates would hand the first one's
    /// <c>Done</c> to whoever awaited second. Advise, start, await, dispose; the dispose must land on
    /// the UI thread, which is why callers unadvise in a <c>finally</c> after an unconditional switch.
    /// </para>
    /// </remarks>
    internal sealed class SolutionUpdateWaiter : IVsUpdateSolutionEvents, IDisposable
    {
        private readonly IVsSolutionBuildManager _buildManager;
        private readonly TaskCompletionSource<SolutionUpdateOutcome> _done =
            new TaskCompletionSource<SolutionUpdateOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        private uint _cookie;
        private bool _disposed;

        private SolutionUpdateWaiter(IVsSolutionBuildManager buildManager, uint cookie)
        {
            _buildManager = buildManager;
            _cookie = cookie;
        }

        /// <summary>
        /// Advises on the build manager. UI thread only. Returns <c>null</c> when the service is
        /// unavailable or refuses the advise, so the caller can choose the correct-but-blocking path
        /// rather than an unwaited one.
        /// </summary>
        public static SolutionUpdateWaiter TryAdvise()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = ServiceProvider.GlobalProvider.GetService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager;
            if (manager is null)
                return null;

            var sink = new SolutionUpdateWaiter(manager, 0);
            if (!ErrorHandler.Succeeded(manager.AdviseUpdateSolutionEvents(sink, out var cookie)) || cookie == 0)
                return null;
            sink._cookie = cookie;
            return sink;
        }

        /// <summary>True once <c>UpdateSolution_Begin</c> has been seen — separates "never started" from "not finished".
        /// Written on the UI thread, read from the pool after a timeout, hence the volatile pair.</summary>
        public bool Started => Volatile.Read(ref _started);
        private bool _started;

        /// <summary>Completes when the update ends (done or cancelled). Continuations never run on the build manager's stack.</summary>
        public Task<SolutionUpdateOutcome> Completed => _done.Task;

        int IVsUpdateSolutionEvents.UpdateSolution_StartUpdate(ref int pfCancelUpdate) => VSConstants.S_OK;

        int IVsUpdateSolutionEvents.UpdateSolution_Begin(ref int pfCancelUpdate)
        {
            Volatile.Write(ref _started, true);
            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.UpdateSolution_Done(int fSucceeded, int fModified, int fCancelCommand)
        {
            // TrySet: a Cancel may already have completed the task; the first report stands.
            _done.TrySetResult(new SolutionUpdateOutcome(succeeded: fSucceeded != 0, cancelled: fCancelCommand != 0));
            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.UpdateSolution_Cancel()
        {
            _done.TrySetResult(new SolutionUpdateOutcome(succeeded: false, cancelled: true));
            return VSConstants.S_OK;
        }

        int IVsUpdateSolutionEvents.OnActiveProjectCfgChange(IVsHierarchy pIVsHierarchy) => VSConstants.S_OK;

        /// <summary>Unadvises. UI thread only; best effort, never throws.</summary>
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
                return;
            _disposed = true;
            if (_cookie != 0)
            {
                try { _buildManager.UnadviseUpdateSolutionEvents(_cookie); }
                catch { /* best effort on teardown */ }
                _cookie = 0;
            }
        }
    }
}
