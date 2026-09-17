using System;
using Microsoft.VisualStudio.Shell;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Puts the Error List's source view into the state a read needs, and puts back exactly what it
    /// found. Shared by the tool catalog's diagnostics reads and by the workspace-context capture — both are hostage to the same dropdown.
    /// </summary>
    /// <remarks>
    /// <b>Why a build read asks for the IntelliSense half to be OFF.</b> VS does not simply add the live
    /// diagnostics alongside the build's — it RE-ATTRIBUTES. Measured on one solution with no edits
    /// between the two reads: `CS8602` went from 6 rows marked <c>Build</c> to 3 <c>Build</c> + 3
    /// <c>Other</c>, and `CS8604` from 3 <c>Build</c> to 2 + 1, per-code totals preserved. Nothing was
    /// fixed; live analysis warmed up on the open files and four diagnostics changed source, at which
    /// point the <c>ErrorSource.Build</c> filter drops them and <c>build_solution</c> under-reports a
    /// 28-warning build as 24 — worsening the more files are open. Asking for Build Only is the same
    /// remedy Roslyn's own issues give (dotnet/roslyn#74380 et al: "flip to Build Only"), applied for
    /// the length of one read instead of left for the developer to do by hand.
    /// <para>
    /// So this is not monotonic widening: a build read may need to turn the live half OFF, which is a
    /// mutation in the opposite direction. It is safe for the same reason a widen is, and it restores the original either way.
    /// </para>
    /// <para>
    /// <b>INVARIANT: nothing inside this scope may yield the UI thread.</b> The change is unobservable to
    /// the developer only because the whole set → subscribe → read → restore runs in one synchronous
    /// block, so the dispatcher never gets a turn while the view differs from theirs and the Error List
    /// has no opportunity to repaint. Introduce an <c>await</c> between the set and the restore and it
    /// becomes both VISIBLE and re-entrant. It also rests on the re-publish being SYNCHRONOUS — rows
    /// reach a subscribe on the very next statement, which is what was measured. If a future VS
    /// dispatched that work, reads would go back to returning zero and there would be no way to wait for
    /// it without breaking the first invariant. That is what to re-measure if diagnostics ever come back
    /// empty again.
    /// </para>
    /// </remarks>
    internal sealed class ErrorListViewScope : IDisposable
    {
        private readonly IErrorList _errorList;
        private readonly bool? _restoreBuild;   // null => we did not change it
        private readonly bool? _restoreOther;

        private ErrorListViewScope(IErrorList errorList, bool? restoreBuild, bool? restoreOther)
        {
            _errorList = errorList;
            _restoreBuild = restoreBuild;
            _restoreOther = restoreOther;
        }

        public static IDisposable Apply(IErrorList errorList, bool wantBuild, bool wantOther)
        {
            if (errorList is null)
                return new ErrorListViewScope(null, null, null);

            bool? restoreBuild = null, restoreOther = null;
            try
            {
                if (errorList.AreBuildErrorSourceEntriesShown != wantBuild)
                {
                    restoreBuild = errorList.AreBuildErrorSourceEntriesShown;
                    errorList.AreBuildErrorSourceEntriesShown = wantBuild;
                }
                if (errorList.AreOtherErrorSourceEntriesShown != wantOther)
                {
                    restoreOther = errorList.AreOtherErrorSourceEntriesShown;
                    errorList.AreOtherErrorSourceEntriesShown = wantOther;
                }
            }
            catch
            {
                // Best-effort: a failure here must leave the read to proceed on whatever the current
                // view publishes, not sink the tool call. Anything already changed is recorded above,
                // so it is still restored.
            }
            return new ErrorListViewScope(errorList, restoreBuild, restoreOther);
        }

        public void Dispose()
        {
            if (_errorList is null)
                return;
            try
            {
                if (_restoreBuild.HasValue)
                    _errorList.AreBuildErrorSourceEntriesShown = _restoreBuild.Value;
                if (_restoreOther.HasValue)
                    _errorList.AreOtherErrorSourceEntriesShown = _restoreOther.Value;
            }
            catch { /* best-effort restore; never throw out of a using */ }
        }
    }
}
