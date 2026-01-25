using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;

namespace GenHub.Core.Interfaces.Content;

/// <summary>
/// Unified service for reconciling game profiles and manifest metadata.
/// Coordinates between profile metadata updates and content addressable storage tracking.
/// </summary>
public interface IContentReconciliationService
{
    /// <summary>
    /// Reconciles all profiles by replacing references to an old manifest ID with a new one.
    /// Also handles CAS reference tracking cleanup.
    /// </summary>
    /// <param name="oldId">The manifest ID to replace.</param>
    /// <param name="newId">The new manifest ID to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the number of updated profiles.</returns>
    Task<OperationResult<int>> ReconcileManifestReplacementAsync(
        string oldId,
        string newId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles all profiles by applying multiple manifest ID replacements.
    /// </summary>
    /// <param name="replacements">Dictionary of old ID to new ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the number of updated profiles.</returns>
    Task<OperationResult<int>> ReconcileBulkManifestReplacementAsync(
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles all profiles by removing references to a deleted manifest ID.
    /// Also handles CAS reference tracking cleanup.
    /// </summary>
    /// <param name="manifestId">The manifest ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the number of updated profiles.</returns>
    Task<OperationResult<int>> ReconcileManifestRemovalAsync(
        string manifestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// High-level orchestration for updating local content.
    /// Handles new manifest creation, profile reconciliation, and old content cleanup.
    /// </summary>
    /// <param name="oldId">The existing manifest ID.</param>
    /// <param name="newManifest">The newly generated manifest (may have different ID or same ID).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    Task<OperationResult> OrchestrateLocalUpdateAsync(
        string oldId,
        ContentManifest newManifest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules garbage collection to run. Should be called AFTER all untrack operations are complete.
    /// </summary>
    /// <param name="force">Whether to force garbage collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    Task<OperationResult> ScheduleGarbageCollectionAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}
