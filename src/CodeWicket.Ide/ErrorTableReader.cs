using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.TableManager;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Reads the VS error table's rows from its DATA SOURCES rather than from the Error List window's
    /// control, so what the diagnostics tools report no longer depends on the developer's Error List
    /// filter (issue #93).
    /// </summary>
    /// <remarks>
    /// <b>What was wrong with reading the control.</b> <c>IWpfTableControl.Entries</c> is the rows
    /// currently DISPLAYED — after that window's filter dropdown, its search box, and its groupings. So
    /// two developers with different filter states got different answers from <c>build_solution</c> on the
    /// same solution, with nothing surfacing why. Measured, same build, only the filter changing: "Build +
    /// IntelliSense" (the default) gave <c>errorCount:0</c>; "Build Only" gave <c>errorCount:4</c>. In the
    /// combined view VS collapses a build row and its IntelliSense twin into one entry that reports as
    /// IntelliSense, so the <c>ErrorSource.Build</c> filter dropped it — deterministically, and no amount
    /// of waiting helped. Worse than one lost line: the collapsed view held one CS1002, while the build
    /// data held the whole cascade (MC3074 and two CS0006 from an assembly that was never produced) that
    /// IntelliSense structurally cannot generate.
    /// <para>
    /// <b>The table manager, not the window.</b> The manager is resolved from MEF
    /// (<see cref="ITableManagerProvider"/>) rather than through <c>IErrorList.TableControl</c>, which
    /// also removes the dependency on the Error List window existing at all — a tool answering "are there
    /// errors" has no business caring whether a tool window is open.
    /// </para>
    /// <para>
    /// <b>Short-lived by design.</b> Each read subscribes, takes what the sources publish, and disposes.
    /// The standard sources deliver their current contents synchronously from <c>Subscribe</c> (that is
    /// how the Error List's own control fills), so a one-shot read sees them; and for the one caller that
    /// cannot tolerate a late publisher — the post-build convergence poll — every 150ms sample subscribes
    /// afresh, so a source that published after an earlier sample is picked up by a later one. Holding a
    /// subscription open across the poll would buy nothing that re-subscribing does not, and would put
    /// disposal on whichever thread the poll happened to resume on.
    /// </para>
    /// </remarks>
    internal sealed class ErrorTableReader : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly CollectingSink _sink = new CollectingSink();

        private ErrorTableReader() { }

        /// <summary>
        /// Subscribes to every source registered with <paramref name="manager"/>. A null manager (MEF
        /// unavailable) yields a reader over no rows rather than throwing — the same "no Error List data"
        /// answer a null <c>IErrorList</c> produced before.
        /// </summary>
        public static ErrorTableReader Open(ITableManager manager)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var reader = new ErrorTableReader();
            var sources = manager?.Sources;
            if (sources is null)
                return reader;

            foreach (var source in sources)
            {
                // One bad source must not cost us the others: a third-party analyzer's table source
                // throwing from Subscribe would otherwise take the whole diagnostics read with it.
                try
                {
                    var subscription = source?.Subscribe(reader._sink);
                    if (subscription is not null)
                        reader._subscriptions.Add(subscription);
                }
                catch
                {
                    // Source unavailable; its rows are simply absent.
                }
            }
            return reader;
        }

        /// <summary>
        /// The rows published by every subscribed source, in no particular order. Enumerated lazily, so a
        /// caller that stops early (the scan cap) does not materialize the rest.
        /// </summary>
        public IEnumerable<ErrorTableRow> Rows()
        {
            foreach (var entry in _sink.TakeEntries())
                yield return new ErrorTableRow(entry);

            foreach (var snapshot in _sink.TakeSnapshots())
                for (var i = 0; i < snapshot.Count; i++)
                    yield return new ErrorTableRow(snapshot, i);
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                try { subscription.Dispose(); }
                catch { /* unsubscribing is best-effort */ }
            }
            _subscriptions.Clear();
        }

        /// <summary>
        /// Collects what the sources publish. Locked because a source may publish from a background
        /// thread; the collections are handed out as copies so enumeration cannot race a late publish.
        /// </summary>
        /// <remarks>
        /// <b><see cref="IsStable"/> is implemented and ignored, and that is a measured decision rather than
        /// an omission.</b> It is how a source tells a consumer its data has stopped moving, which would be a
        /// far better convergence signal than the post-build poll's "two identical samples" heuristic — but
        /// measured across all four sources of the errors table, on a short-lived subscription <b>none of
        /// them ever sets it</b>. The value read back was only ever the default assigned here, which is
        /// exactly why it first looked like a unanimous "stable" from every source. Hence the empty-result
        /// floor in <c>CollectBuildDiagnosticsConvergedAsync</c> instead.
        /// </remarks>
        private sealed class CollectingSink : ITableDataSink
        {
            private readonly object _gate = new object();
            private readonly List<ITableEntry> _entries = new List<ITableEntry>();
            private readonly List<ITableEntriesSnapshot> _snapshots = new List<ITableEntriesSnapshot>();
            private readonly List<ITableEntriesSnapshotFactory> _factories = new List<ITableEntriesSnapshotFactory>();

            public bool IsStable { get; set; } = true;

            public List<ITableEntry> TakeEntries()
            {
                lock (_gate)
                    return new List<ITableEntry>(_entries);
            }

            /// <summary>
            /// Every snapshot published to us, including the CURRENT snapshot of each published factory —
            /// a factory is a live handle whose snapshot versions up as its source revises its rows, so it
            /// must be resolved at read time and never cached.
            /// </summary>
            public List<ITableEntriesSnapshot> TakeSnapshots()
            {
                List<ITableEntriesSnapshotFactory> factories;
                List<ITableEntriesSnapshot> snapshots;
                lock (_gate)
                {
                    factories = new List<ITableEntriesSnapshotFactory>(_factories);
                    snapshots = new List<ITableEntriesSnapshot>(_snapshots);
                }

                foreach (var factory in factories)
                {
                    ITableEntriesSnapshot current = null;
                    try { current = factory.GetCurrentSnapshot(); }
                    catch { /* a factory whose source has gone away */ }
                    if (current is not null)
                        snapshots.Add(current);
                }
                return snapshots;
            }

            public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries = false)
            {
                lock (_gate)
                {
                    if (removeAllEntries)
                        _entries.Clear();
                    if (newEntries is not null)
                        _entries.AddRange(newEntries);
                }
            }

            public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
            {
                if (oldEntries is null)
                    return;
                lock (_gate)
                    foreach (var entry in oldEntries)
                        _entries.Remove(entry);
            }

            public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
            {
                RemoveEntries(oldEntries);
                AddEntries(newEntries);
            }

            public void RemoveAllEntries()
            {
                lock (_gate)
                    _entries.Clear();
            }

            public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots = false)
            {
                lock (_gate)
                {
                    if (removeAllSnapshots)
                        _snapshots.Clear();
                    if (newSnapshot is not null)
                        _snapshots.Add(newSnapshot);
                }
            }

            public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
            {
                lock (_gate)
                    _snapshots.Remove(oldSnapshot);
            }

            public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
            {
                RemoveSnapshot(oldSnapshot);
                AddSnapshot(newSnapshot);
            }

            public void RemoveAllSnapshots()
            {
                lock (_gate)
                    _snapshots.Clear();
            }

            public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories = false)
            {
                lock (_gate)
                {
                    if (removeAllFactories)
                        _factories.Clear();
                    if (newFactory is not null)
                        _factories.Add(newFactory);
                }
            }

            public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
            {
                lock (_gate)
                    _factories.Remove(oldFactory);
            }

            public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
            {
                RemoveFactory(oldFactory);
                AddFactory(newFactory);
            }

            /// <summary>A factory revised its rows. Nothing to do: snapshots are resolved at read time.</summary>
            public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory) { }

            public void RemoveAllFactories()
            {
                lock (_gate)
                    _factories.Clear();
            }
        }
    }

    /// <summary>
    /// One row of the error table, reached either as a published <see cref="ITableEntry"/> or as an index
    /// into a published snapshot. Both answer by column name; only the snapshot form hands back an
    /// untyped value, which is why the callers go through <see cref="Core.Ide.TableValue"/>.
    /// </summary>
    internal readonly struct ErrorTableRow
    {
        private readonly ITableEntry _entry;
        private readonly ITableEntriesSnapshot _snapshot;
        private readonly int _index;

        public ErrorTableRow(ITableEntry entry)
        {
            _entry = entry;
            _snapshot = null;
            _index = 0;
        }

        public ErrorTableRow(ITableEntriesSnapshot snapshot, int index)
        {
            _entry = null;
            _snapshot = snapshot;
            _index = index;
        }

        /// <summary>The raw column value, or null when the row has no such column.</summary>
        public object GetValue(string keyName)
        {
            try
            {
                if (_entry is not null)
                    return _entry.TryGetValue(keyName, out var entryValue) ? entryValue : null;
                if (_snapshot is not null)
                    return _snapshot.TryGetValue(_index, keyName, out var snapshotValue) ? snapshotValue : null;
            }
            catch
            {
                // A source's own TryGetValue throwing must cost that column, not the whole read.
            }
            return null;
        }
    }
}
