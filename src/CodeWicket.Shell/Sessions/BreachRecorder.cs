using System.Collections.Generic;

namespace CodeWicket.Shell.Sessions
{
    /// <summary>
    /// Collects invariant breaches for a test to fail on. An INSTANCE, never thread-bound state: a
    /// breach raised after an await that resumed on a pool thread, inside <c>Task.Run</c>, or under
    /// <c>ConfigureAwait(false)</c> lands in the same list as one raised on the test's own thread. The
    /// first design recorded into a <c>[ThreadStatic]</c> list and could not see exactly those, which
    /// are what a refactor changes - so the instrument's sight would have depended on the await
    /// hygiene of the code it was checking.
    /// </summary>
    public sealed class BreachRecorder : IInvariantSink
    {
        private readonly object _gate = new object();
        private readonly List<string> _breaches = new List<string>();

        public void Breach(string message)
        {
            lock (_gate)
                _breaches.Add(message);
        }

        public int Count
        {
            get { lock (_gate) return _breaches.Count; }
        }

        /// <summary>Takes every breach recorded so far and empties the recorder: how a test that
        /// EXPECTS a breach asserts it and leaves nothing for the end-of-test check.</summary>
        public IReadOnlyList<string> Drain()
        {
            lock (_gate)
            {
                var taken = _breaches.ToArray();
                _breaches.Clear();
                return taken;
            }
        }
    }
}
