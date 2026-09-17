using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CodeWicket.UI.Mvvm
{
    /// <summary>
    /// An <see cref="ObservableCollection{T}"/> that can be re-filled in one notification instead of one
    /// per element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For a window that moves at its FRONT.</b> A sub-agent row draws the most recent few of its
    /// children (issue #125), so widening it — "Show all", or a reveal naming an older call — inserts at
    /// index 0, and narrowing it again removes from index 0. Done one element at a time against a
    /// non-virtualizing <c>ItemsControl</c>, each of those is a separate <c>CollectionChanged</c> that
    /// re-indexes every container already generated, so widening a five-row window to twenty-six shuffles
    /// the whole list twenty-one times over. Appending — the shape this collection is NOT for — never had
    /// that problem, which is why it only appeared when the window was anchored to the newest call.
    /// </para>
    /// <para>
    /// <b>A Reset is right for a jump and wrong for a step.</b> It makes the control throw away every
    /// container and rebuild, which is cheaper than shuffling them repeatedly but far more expensive than
    /// the one-remove-one-add of an ordinary slide. So the caller chooses: a cap change batches, a call
    /// arriving does not.
    /// </para>
    /// </remarks>
    public sealed class BatchedObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>Replaces the contents, raising a single <see cref="NotifyCollectionChangedAction.Reset"/>.</summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            // Mutating Items directly bypasses ClearItems/InsertItem, and with them the reentrancy check
            // the base class does on every mutation. Re-stated here rather than skipped: a handler that
            // edits the collection while it is being rebuilt is a corrupt state either way, and silently
            // allowing it here only moves where it surfaces.
            CheckReentrancy();

            Items.Clear();
            foreach (var item in items)
                Items.Add(item);

            // Both property notifications, in this order, are what ObservableCollection itself raises
            // around a Reset; a binding that watches Count without them silently keeps a stale value.
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
