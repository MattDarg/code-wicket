using System;
using System.Threading;

namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// The process-wide invariant sink, for code that is not handed one explicitly (the view-model).
    /// It holds NO state of its own: what it records into is an <see cref="AsyncLocal{T}"/> scope that
    /// only a test opens, and that the product never does - so in the product a breach reaches nothing
    /// here, and this cannot produce the order-dependency a process-wide sink once did (the render-log
    /// sink collected whatever else wrote through it).
    /// </summary>
    /// <remarks>
    /// <para><b>AsyncLocal, not ThreadStatic, and that is the whole design.</b> An execution-context
    /// scope flows into every dispatcher operation posted and every continuation scheduled inside it,
    /// whichever thread it resumes on. A thread-static list saw only breaches raised on the thread that
    /// opened it, so a breach after a pool-thread hop was recorded nowhere and its test passed.
    /// Proved by <c>StaInvariantScopeTests</c>, over an injected thread-static scope.</para>
    /// </remarks>
    public static class Invariants
    {
        private static readonly AsyncLocal<IInvariantSink?> Scope = new AsyncLocal<IInvariantSink?>();

        /// <summary>Records into the scope open on the current flow, if any; otherwise does nothing.</summary>
        public static IInvariantSink Ambient { get; } = new AmbientSink();

        /// <summary>
        /// The view-model's sink: the breach, VERBATIM, as one unconditional line to <paramref name="log"/>
        /// (<c>engine.log</c>), then on to <see cref="Ambient"/>, which records only where a test opened a
        /// scope. The line is the product's record of the breach; the scope is the test's.
        /// </summary>
        /// <remarks>
        /// <para><b>The one writer of a breach's line.</b>
        /// A breach site passes its whole sentence, tagged with its own area (<c>[lifetime] …</c>,
        /// <c>[delivery] …</c>), and writes nothing itself: with every site writing its own line and this
        /// writing it again, engine.log held each breach twice, and with sites writing and sinks only
        /// recording, a future site that forgot its line would be silent in the product. No second tag is
        /// added here, which would be the doubling again in another form.</para>
        /// <para>The forward is the half that matters to the gates: without it every breach the view-model
        /// reports is logged and fails no test, because <c>StaTest.Run</c> sees breaches only through
        /// the ambient scope. Proved by <c>InvariantsLoggedTests</c>, over a logged sink injected so that it
        /// never reaches the scope.</para>
        /// </remarks>
        public static IInvariantSink Logged(Action<string>? log) => new LoggedSink(log, Ambient);

        /// <summary>
        /// <see cref="Logged(Action{string})"/>, forwarding to <paramref name="next"/> instead of the ambient scope:
        /// for a plain test that hands the class under test its own recorder and still asserts the line.
        /// </summary>
        public static IInvariantSink Logged(Action<string>? log, IInvariantSink next) =>
            new LoggedSink(log, next ?? throw new ArgumentNullException(nameof(next)));

        /// <summary>
        /// Opens a recording scope on the current execution context. Everything that flows from here -
        /// posts, continuations, <c>Task.Run</c> - reports into <paramref name="recorder"/>. Disposing
        /// restores the previous scope; dispose on the flow that opened it.
        /// </summary>
        public static IDisposable OpenScope(IInvariantSink recorder)
        {
            if (recorder is null) throw new ArgumentNullException(nameof(recorder));
            var previous = Scope.Value;
            Scope.Value = recorder;
            return new ScopeHandle(previous);
        }

        private sealed class AmbientSink : IInvariantSink
        {
            public void Breach(string message) => Scope.Value?.Breach(message);
        }

        private sealed class LoggedSink : IInvariantSink
        {
            private readonly Action<string>? _log;
            private readonly IInvariantSink _next;

            public LoggedSink(Action<string>? log, IInvariantSink next)
            {
                _log = log;
                _next = next;
            }

            public void Breach(string message)
            {
                _log?.Invoke(message);
                _next.Breach(message);
            }
        }

        private sealed class ScopeHandle : IDisposable
        {
            private readonly IInvariantSink? _previous;
            private bool _disposed;

            public ScopeHandle(IInvariantSink? previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Scope.Value = _previous;
            }
        }
    }
}
