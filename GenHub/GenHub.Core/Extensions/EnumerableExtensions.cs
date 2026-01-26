using System.Collections.ObjectModel;

namespace GenHub.Core.Extensions;

/// <summary>
/// Extension methods for IEnumerable to ObservableCollection conversions.
/// </summary>
public static class EnumerableExtensions
{
    /// <summary>
    /// Converts an IEnumerable to an ObservableCollection.
    /// </summary>
    /// <typeparam name="T">The type of elements in the collection.</typeparam>
    /// <param name="source">The source enumerable.</param>
    /// <returns>An ObservableCollection containing the elements from the source.</returns>
    public static ObservableCollection<T> ToObservableCollection<T>(this IEnumerable<T> source)
    {
        return new ObservableCollection<T>(source);
    }

    /// <summary>
    /// Adds the elements of the specified collection to the ICollection.
    /// </summary>
    /// <typeparam name="T">The type of elements in the collection.</typeparam>
    /// <param name="destination">The collection to add elements to.</param>
    /// <param name="source">The collection whose elements should be added.</param>
    public static void AddRange<T>(this ICollection<T> destination, IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        // Handle self-add to avoid enumeration invalidation
        var itemsToAdd = ReferenceEquals(destination, source) ? source.ToList() : source;

        foreach (var item in itemsToAdd)
        {
            destination.Add(item);
        }
    }
}