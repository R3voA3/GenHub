using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Interfaces.Storage;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using GenHub.Core.Models.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenHub.Features.Storage.Services;

/// <summary>
/// Manages CAS reference lifecycle with proper ordering guarantees.
/// Wraps CasReferenceTracker and CasService to ensure GC only runs after untracking.
/// </summary>
public class CasLifecycleManager(
    CasReferenceTracker referenceTracker,
    ICasService casService,
    ICasStorage casStorage,
    IOptions<CasConfiguration> config,
    ILogger<CasLifecycleManager> logger) : ICasLifecycleManager
{
    private readonly SemaphoreSlim _gcLock = new(1, 1);

    /// <inheritdoc/>
    public async Task<OperationResult> ReplaceManifestReferencesAsync(
        string oldManifestId,
        ContentManifest newManifest,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation(
                "Replacing manifest references: {OldId} → {NewId}",
                oldManifestId,
                newManifest.Id.Value);

            // Step 1: Track new manifest first (ensures new content is protected)
            await referenceTracker.TrackManifestReferencesAsync(
                newManifest.Id.Value,
                newManifest,
                cancellationToken);

            // Step 2: Untrack old manifest (makes old content eligible for GC)
            if (!string.Equals(oldManifestId, newManifest.Id.Value, StringComparison.OrdinalIgnoreCase))
            {
                await referenceTracker.UntrackManifestAsync(oldManifestId, cancellationToken);
            }

            logger.LogInformation(
                "Successfully replaced manifest references: {OldId} → {NewId}",
                oldManifestId,
                newManifest.Id.Value);

            return OperationResult.CreateSuccess();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to replace manifest references: {OldId} → {NewId}",
                oldManifestId,
                newManifest.Id.Value);
            return OperationResult.CreateFailure($"Failed to replace references: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<OperationResult<int>> UntrackManifestsAsync(
        IEnumerable<string> manifestIds,
        CancellationToken cancellationToken = default)
    {
        var ids = manifestIds.ToList();
        int untracked = 0;

        foreach (var manifestId in ids)
        {
            try
            {
                await referenceTracker.UntrackManifestAsync(manifestId, cancellationToken);
                untracked++;
                logger.LogDebug("Untracked manifest: {ManifestId}", manifestId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to untrack manifest: {ManifestId}", manifestId);
            }
        }

        logger.LogInformation("Untracked {Count}/{Total} manifests", untracked, ids.Count);
        return OperationResult<int>.CreateSuccess(untracked);
    }

    /// <inheritdoc/>
    public async Task<OperationResult<GarbageCollectionStats>> RunGarbageCollectionAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        // Ensure only one GC runs at a time
        if (!await _gcLock.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            logger.LogWarning("GC already in progress, skipping");
            return OperationResult<GarbageCollectionStats>.CreateFailure("GC already in progress");
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Starting garbage collection (force={Force})", force);

            var gcResult = await casService.RunGarbageCollectionAsync(force, cancellationToken);

            stopwatch.Stop();

            var stats = new GarbageCollectionStats
            {
                ObjectsScanned = gcResult.ObjectsScanned,
                ObjectsReferenced = gcResult.ObjectsReferenced,
                ObjectsDeleted = gcResult.ObjectsDeleted,
                BytesFreed = gcResult.BytesFreed,
                Duration = stopwatch.Elapsed,
            };

            logger.LogInformation(
                "GC completed: scanned={Scanned}, referenced={Referenced}, deleted={Deleted}, freed={Bytes} bytes",
                stats.ObjectsScanned,
                stats.ObjectsReferenced,
                stats.ObjectsDeleted,
                stats.BytesFreed);

            return OperationResult<GarbageCollectionStats>.CreateSuccess(stats);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Garbage collection failed");
            return OperationResult<GarbageCollectionStats>.CreateFailure($"GC failed: {ex.Message}");
        }
        finally
        {
            _gcLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<OperationResult<CasReferenceAudit>> GetReferenceAuditAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get all referenced hashes
            var referencedHashes = await referenceTracker.GetAllReferencedHashesAsync(cancellationToken);

            // Get all CAS objects
            var allObjects = await casStorage.GetAllObjectHashesAsync(cancellationToken);

            // Count orphaned objects
            var orphanedCount = allObjects.Except(referencedHashes).Count();

            // Count manifests and workspaces from refs directory
            var refsDir = Path.Combine(config.Value.CasRootPath, "refs");
            var manifestsDir = Path.Combine(refsDir, "manifests");
            var workspacesDir = Path.Combine(refsDir, "workspaces");

            var manifestIds = Directory.Exists(manifestsDir)
                ? Directory.GetFiles(manifestsDir, "*.refs")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .ToList()
                : [];

            var workspaceIds = Directory.Exists(workspacesDir)
                ? Directory.GetFiles(workspacesDir, "*.refs")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .ToList()
                : [];

            var audit = new CasReferenceAudit
            {
                TotalManifests = manifestIds.Count,
                TotalWorkspaces = workspaceIds.Count,
                TotalReferencedHashes = referencedHashes.Count,
                TotalCasObjects = allObjects.Length,
                OrphanedObjects = orphanedCount,
                ManifestIds = manifestIds,
                WorkspaceIds = workspaceIds,
            };

            return OperationResult<CasReferenceAudit>.CreateSuccess(audit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get reference audit");
            return OperationResult<CasReferenceAudit>.CreateFailure($"Audit failed: {ex.Message}");
        }
    }
}
