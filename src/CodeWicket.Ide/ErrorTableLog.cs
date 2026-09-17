using System;
using CodeWicket.Core;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Opt-in diagnostic for the Error List read. **Writes only when <c>CWKT_ERRORTABLE_LOG</c> names a
    /// file** — otherwise a no-op, so normal runs stay quiet. Set it as a real environment variable before
    /// launching devenv (launchSettings env never reaches the experimental instance). Never throws.
    /// </summary>
    /// <remarks>
    /// <b>Kept because this read has a history of failing silently and plausibly.</b> Issue #93 took three
    /// wrong diagnoses — a permanent IntelliSense source, convergence timing, and the Error List control
    /// filtering rows — before instrumentation settled it, and each wrong answer came from measuring one
    /// state and generalising to a mechanism. The line below is deliberately the *comparison* rather than a
    /// value, because that is what broke the deadlock every time:
    /// <list type="bullet">
    /// <item><c>sourceRows</c> vs <c>controlRows</c> killed "the control filters our rows" — they agreed
    /// exactly across nine reads, then diverged 28-to-0 straight after a programmatic view change, which is
    /// what proved the read has to go to the sources.</item>
    /// <item><c>Build=</c> vs <c>Other=</c> caught VS RE-ATTRIBUTING build diagnostics to the live half as
    /// the workspace warms — per-code totals preserved, source changed — which is why a build read asks for
    /// Build Only.</item>
    /// <item><c>view(...)</c> shows what the developer's dropdown was and what we set it to, since that
    /// gates whether the rows are published at all.</item>
    /// </list>
    /// If build diagnostics ever come back wrong again, turn this on and read one line before theorising.
    /// The hypothesis-specific instrumentation (per-source subscribe reports, <c>ITableDataSink.IsStable</c>)
    /// was removed: those questions are answered and recorded where they belong, in
    /// <see cref="ErrorTableReader"/> and the convergence poll.
    /// </remarks>
    internal static class ErrorTableLog
    {
        /// <summary>True when the log is opted in. Guard call sites whose message costs anything to build.</summary>
        public static bool Enabled
            => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CWKT_ERRORTABLE_LOG"));

        public static void Write(string message)
        {
            var path = Environment.GetEnvironmentVariable("CWKT_ERRORTABLE_LOG");
            if (string.IsNullOrWhiteSpace(path))
                return; // opt-in only

            // DiagnosticLog owns the directory creation, the lock, the size cap, the roll — and the
            // per-line timestamp, so this adds only the channel tag.
            DiagnosticLog.AppendLine(path, $"[error-table] {message}");
        }
    }
}
