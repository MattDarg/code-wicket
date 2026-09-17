using System;
using CodeWicket.Core;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Opt-in best-effort diagnostic log for <c>run_tests</c>. Records the tool-side lifecycle (the pre-run
    /// build, each <c>dotnet</c> command with its exit code and output, parsed counts).
    /// **Writes only when <c>CWKT_TESTRUN_LOG</c> names a file** — otherwise a no-op, so normal runs stay quiet.
    /// Set it as a real environment variable before launching devenv (launchSettings env never reaches the
    /// experimental instance). Never throws — a diagnostic must not break a run.
    /// </summary>
    internal static class TestRunDebugLog
    {
        /// <summary>True when the log is opted in. Guard call sites whose message is expensive to build
        /// (e.g. interpolating a whole test run's console output) — <see cref="Write"/> no-ops, but the
        /// caller's string is materialized before it gets here.</summary>
        public static bool Enabled
            => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CWKT_TESTRUN_LOG"));

        public static void Write(string message)
        {
            var path = Environment.GetEnvironmentVariable("CWKT_TESTRUN_LOG");
            if (string.IsNullOrWhiteSpace(path))
                return; // opt-in only

            // DiagnosticLog owns the directory creation, the lock, the size cap, the roll and the
            // per-line timestamp — this log lands wherever CWKT_TESTRUN_LOG points, which may be
            // outside the swept log dir, so the per-file cap is the only retention it gets.
            DiagnosticLog.AppendLine(path, message);
        }
    }
}
