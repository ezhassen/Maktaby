using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Maktaby.Helpers;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its whole contents with a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> instead of one notification per element.
/// </summary>
/// <remarks>
/// Reordering row by row with <see cref="ObservableCollection{T}.Move"/> costs a notification *and*
/// an array shift per row, so reordering a 20 000-row folder that way takes ~0.4 s on the UI thread
/// (measured). A reset is O(n) and lets the view regenerate what it needs.
/// </remarks>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces every element with <paramref name="items"/> and raises one reset. The view drops
    /// state it owns about the old containers (selection, scroll), so callers that keep state on
    /// the items themselves must re-apply it afterwards.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
