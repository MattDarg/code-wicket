using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Snapshot source for editor/solution context injected into agent turns. The agent is otherwise
    /// blind to what the user is looking at; this is what makes the integration "deep".
    /// </summary>
    public interface IWorkspaceContext
    {
        /// <summary>Absolute path to the workspace/solution root, if open.</summary>
        string? RootPath { get; }

        /// <summary>Captures a point-in-time snapshot of editor and solution state.</summary>
        Task<WorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>A point-in-time view of the IDE that can be attached to a prompt.</summary>
    public sealed record WorkspaceSnapshot
    {
        public string? SolutionName { get; init; }
        public string? ActiveFilePath { get; init; }
        public TextSelection? Selection { get; init; }
        public IReadOnlyList<string> OpenFilePaths { get; init; } = Array.Empty<string>();
        /// <summary>The diagnostics worth listing - see <see cref="WorkspaceDiagnostics"/>, NOT the whole set.</summary>
        public IReadOnlyList<DiagnosticInfo> Diagnostics { get; init; } = Array.Empty<DiagnosticInfo>();

        /// <summary>
        /// Totals across EVERY diagnostic, not just the listed ones, so the block can always say how much
        /// it is not showing. An unlabelled truncated list reads as complete, which is the failure these
        /// exist to prevent (issue #95).
        /// </summary>
        public int TotalErrorCount { get; init; }

        /// <inheritdoc cref="TotalErrorCount"/>
        public int TotalWarningCount { get; init; }

        /// <summary>How many diagnostics exist that <see cref="Diagnostics"/> does not name.</summary>
        public int OmittedDiagnosticCount { get; init; }

        /// <summary>
        /// Whether the user is debugging right now, and where execution is stopped if it is. Null when
        /// there is no debug session.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It is here because the agent cannot get it by asking</b> — the #95 rule for what earns a
        /// place in ambient context. It can pull frames and values with <c>read_expression</c>; it
        /// has no way to learn that a session EXISTS, and that fact changes what it should do: editing a
        /// source file mid-session leaves the running binary no longer matching the source, silently
        /// invalidating every line number it has been given.
        /// </para>
        /// <para>
        /// <b>The FACT and the location only.</b> Frames, locals and values are pullable now, so putting
        /// them here would charge every prompt for something the agent can ask for when it needs it —
        /// the same reason this block carries diagnostic shape rather than the diagnostic set.
        /// </para>
        /// </remarks>
        public DebugSessionInfo? DebugSession { get; init; }
    }

    /// <summary>The ambient half of the debugger's state — see <see cref="WorkspaceSnapshot.DebugSession"/>.</summary>
    /// <param name="IsStopped">
    /// False while the program runs. Read from the debugger's current MODE rather than tracked from
    /// events: the event stream has stop events but no matching "resumed" event, so a state machine
    /// built on it drifts, and a stale "stopped" is exactly the confident wrong answer to avoid.
    /// </param>
    public sealed record DebugSessionInfo(bool IsStopped)
    {
        public string? File { get; init; }
        public int Line { get; init; }
        public string? Method { get; init; }

        /// <summary>Why it stopped, in the same words the pushed block uses.</summary>
        public string? Reason { get; init; }

        /// <summary>
        /// The stopped thread. Asked for by name by an agent using this block: a multi-threaded stop
        /// makes "where" ambiguous on its own, and the thread is what disambiguates it.
        /// </summary>
        public string? Thread { get; init; }

        /// <summary>The debuggee's process id — with <see cref="StopNumber"/>, a stop's identity.</summary>
        public int? ProcessId { get; init; }

        /// <summary>
        /// How many times this session has stopped. With <see cref="ProcessId"/> it identifies a stop, so
        /// a <c>&lt;debug-state&gt;</c> block the user pushed earlier can be told apart from the one the
        /// agent is looking at now — observed live, a pushed block described a stop that a restart had
        /// already ended, and only a coincidence revealed it.
        /// </summary>
        public int StopNumber { get; init; }

        public bool HasLocation => !string.IsNullOrEmpty(File) && Line > 0;
    }

    /// <summary>A selected range in a file (1-based line/column), with the selected text when small.</summary>
    public sealed record TextSelection(
        string FilePath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        string? Text);

    /// <summary>A compiler/analyzer diagnostic, e.g. from the Error List.</summary>
    public sealed record DiagnosticInfo(
        string FilePath,
        int Line,
        int Column,
        DiagnosticSeverity Severity,
        string Message,
        string? Code);

    public enum DiagnosticSeverity
    {
        Hidden,
        Info,
        Warning,
        Error,
    }
}
