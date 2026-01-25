using System;
using System.Collections.Generic;

namespace GenHub.Core.Models.Content;

/// <summary>
/// Request for content replacement operation.
/// </summary>
public record ContentReplacementRequest
{
    /// <summary>
    /// Gets mapping of old manifest IDs to new manifest IDs.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ManifestMapping { get; init; }

    /// <summary>
    /// Gets a value indicating whether to remove old manifests after replacement.
    /// </summary>
    public bool RemoveOldManifests { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to run garbage collection after all operations.
    /// </summary>
    public bool RunGarbageCollection { get; init; } = true;

    /// <summary>
    /// Gets optional identifier for the source of this operation (e.g., "GeneralsOnline", "LocalEdit").
    /// </summary>
    public string? Source { get; init; }
}
