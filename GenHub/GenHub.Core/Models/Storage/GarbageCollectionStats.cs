using System;

namespace GenHub.Core.Interfaces.Storage;

/// <summary>
/// Statistics from a garbage collection run.
/// </summary>
public record GarbageCollectionStats
{
    /// <summary>
    /// Gets the number of CAS objects scanned.
    /// </summary>
    public int ObjectsScanned { get; init; }

    /// <summary>
    /// Gets the number of CAS objects that are referenced.
    /// </summary>
    public int ObjectsReferenced { get; init; }

    /// <summary>
    /// Gets the number of CAS objects deleted.
    /// </summary>
    public int ObjectsDeleted { get; init; }

    /// <summary>
    /// Gets the bytes freed by deletion.
    /// </summary>
    public long BytesFreed { get; init; }

    /// <summary>
    /// Gets the duration of the GC operation.
    /// </summary>
    public TimeSpan Duration { get; init; }
}
