using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Heart.Collections;

/// <summary>
/// ObservableCollection that supports atomic range operations to avoid issuing
/// one CollectionChanged notification per item during batch UI updates.
/// ReplaceAll replaces the entire contents with a single Reset notification,
/// which prevents the CollectionView from rebinding/remeasuring per item.
/// </summary>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Clears all items and replaces them with <paramref name="items"/> in a single
    /// <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    /// Must be called on the UI thread.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
