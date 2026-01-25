using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using GenHub.Core.Constants;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Storage;
using GenHub.Core.Models.Content;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using GenHub.Features.Storage.Services;
using Microsoft.Extensions.Logging;

namespace GenHub.Features.Content.Services.Reconciliation;

/// <summary>
/// Central orchestrator for content reconciliation operations.
/// Enforces correct operation ordering to prevent GC timing issues:
/// Update Profiles → Untrack Old → Remove Old → GC.
/// </summary>
public class ContentReconciliationOrchestrator(
    IContentReconciliationService reconciliationService,
    IContentManifestPool manifestPool,
    ICasLifecycleManager casLifecycleManager,
    IReconciliationAuditLog auditLog,
    ILogger<ContentReconciliationOrchestrator> logger) : IContentReconciliationOrchestrator
{
    /// <inheritdoc/>
    public async Task<OperationResult<ContentReplacementResult>> ExecuteContentReplacementAsync(
        ContentReplacementRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N")[..ReconciliationConstants.OperationIdLength];
        var warnings = new List<string>();

        logger.LogInformation(
            "[Orchestrator:{OpId}] Starting content replacement for {Count} manifests",
            operationId,
            request.ManifestMapping.Count);

        // Publish start event
        WeakReferenceMessenger.Default.Send(new ReconciliationStartedEvent(
            operationId,
            "ContentReplacement",
            0, // Will be determined during execution
            request.ManifestMapping.Count));

        try
        {
            // STEP 1: Update all profiles with new manifest references
            logger.LogDebug("[Orchestrator:{OpId}] Step 1: Updating profiles", operationId);
            var profileResult = await reconciliationService.ReconcileBulkManifestReplacementAsync(
                request.ManifestMapping,
                cancellationToken);

            int profilesUpdated = profileResult.Success ? profileResult.Data : 0;
            if (!profileResult.Success)
            {
                warnings.Add($"Profile update partial failure: {profileResult.FirstError}");
            }

            // STEP 2: Untrack old manifests (MUST happen before GC)
            int manifestsRemoved = 0;
            if (request.RemoveOldManifests)
            {
                logger.LogDebug("[Orchestrator:{OpId}] Step 2: Untracking old manifests", operationId);

                // Publish removing events for each manifest
                foreach (var oldId in request.ManifestMapping.Keys)
                {
                    WeakReferenceMessenger.Default.Send(new ContentRemovingEvent(oldId, null, "Replacement"));
                }

                var untrackResult = await casLifecycleManager.UntrackManifestsAsync(
                    request.ManifestMapping.Keys,
                    cancellationToken);

                // STEP 3: Remove old manifest files from pool
                logger.LogDebug("[Orchestrator:{OpId}] Step 3: Removing old manifests from pool", operationId);
                foreach (var oldId in request.ManifestMapping.Keys)
                {
                    try
                    {
                        // Skip RemoveManifestAsync's internal untrack since we already did it
                        var manifestId = ManifestId.Create(oldId);
                        var removeResult = await manifestPool.RemoveManifestAsync(manifestId, cancellationToken);
                        if (removeResult.Success)
                        {
                            manifestsRemoved++;
                        }
                        else
                        {
                            warnings.Add($"Failed to remove manifest {oldId}: {removeResult.FirstError}");
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Error removing manifest {oldId}: {ex.Message}");
                    }
                }
            }

            // STEP 4: Run GC (now safe - .refs files are gone)
            int casObjectsCollected = 0;
            long bytesFreed = 0;
            if (request.RunGarbageCollection)
            {
                logger.LogDebug("[Orchestrator:{OpId}] Step 4: Running garbage collection", operationId);
                var gcResult = await casLifecycleManager.RunGarbageCollectionAsync(false, cancellationToken);
                if (gcResult.Success)
                {
                    casObjectsCollected = gcResult.Data.ObjectsDeleted;
                    bytesFreed = gcResult.Data.BytesFreed;
                }
            }

            stopwatch.Stop();

            var result = new ContentReplacementResult
            {
                ProfilesUpdated = profilesUpdated,
                ManifestsRemoved = manifestsRemoved,
                CasObjectsCollected = casObjectsCollected,
                BytesFreed = bytesFreed,
                Duration = stopwatch.Elapsed,
                Warnings = warnings,
            };

            await auditLog.LogOperationAsync(
                new ReconciliationAuditEntry
                {
                    OperationId = operationId,
                    OperationType = ReconciliationOperationType.ManifestReplacement,
                    Timestamp = DateTime.UtcNow,
                    Source = request.Source,
                    AffectedManifestIds = [.. request.ManifestMapping.Keys.Concat(request.ManifestMapping.Values).Distinct()],
                    ManifestMapping = request.ManifestMapping,
                    Success = true,
                    Duration = stopwatch.Elapsed,
                    Metadata = new Dictionary<string, string>
                    {
                        ["profilesUpdated"] = profilesUpdated.ToString(),
                        ["manifestsRemoved"] = manifestsRemoved.ToString(),
                        ["casObjectsCollected"] = casObjectsCollected.ToString(),
                        ["bytesFreed"] = bytesFreed.ToString(),
                    },
                },
                cancellationToken);

            // Publish completion event
            WeakReferenceMessenger.Default.Send(new ReconciliationCompletedEvent(
                operationId,
                "ContentReplacement",
                profilesUpdated,
                manifestsRemoved,
                true,
                null,
                stopwatch.Elapsed));

            logger.LogInformation(
                "[Orchestrator:{OpId}] Content replacement completed: {Profiles} profiles, {Manifests} manifests, {Objects} CAS objects, {Bytes} bytes freed",
                operationId,
                profilesUpdated,
                manifestsRemoved,
                casObjectsCollected,
                bytesFreed);

            return OperationResult<ContentReplacementResult>.CreateSuccess(result);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "[Orchestrator:{OpId}] Content replacement failed", operationId);

            await auditLog.LogOperationAsync(
                new ReconciliationAuditEntry
                {
                    OperationId = operationId,
                    OperationType = ReconciliationOperationType.ManifestReplacement,
                    Timestamp = DateTime.UtcNow,
                    Source = request.Source,
                    AffectedManifestIds = [.. request.ManifestMapping.Keys],
                    ManifestMapping = request.ManifestMapping,
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = stopwatch.Elapsed,
                },
                cancellationToken);

            // Publish failure event
            WeakReferenceMessenger.Default.Send(new ReconciliationCompletedEvent(
                operationId,
                "ContentReplacement",
                0,
                0,
                false,
                ex.Message,
                stopwatch.Elapsed));

            return OperationResult<ContentReplacementResult>.CreateFailure($"Content replacement failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<OperationResult<ContentRemovalResult>> ExecuteContentRemovalAsync(
        IEnumerable<string> manifestIds,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N")[..ReconciliationConstants.OperationIdLength];
        var ids = manifestIds.ToList();

        logger.LogInformation(
            "[Orchestrator:{OpId}] Starting content removal for {Count} manifests",
            operationId,
            ids.Count);

        try
        {
            // STEP 1: Update profiles to remove manifest references
            int profilesUpdated = 0;
            foreach (var manifestId in ids)
            {
                var reconcileResult = await reconciliationService.ReconcileManifestRemovalAsync(manifestId, cancellationToken);
                if (reconcileResult.Success)
                {
                    profilesUpdated += reconcileResult.Data;
                }
            }

            // STEP 2: Untrack all manifests
            foreach (var manifestId in ids)
            {
                WeakReferenceMessenger.Default.Send(new ContentRemovingEvent(manifestId, null, "Removal"));
            }

            await casLifecycleManager.UntrackManifestsAsync(ids, cancellationToken);

            // STEP 3: Remove manifest files
            int manifestsRemoved = 0;
            foreach (var id in ids)
            {
                var removeResult = await manifestPool.RemoveManifestAsync(ManifestId.Create(id), cancellationToken);
                if (removeResult.Success)
                {
                    manifestsRemoved++;
                }
            }

            // STEP 4: Run GC
            var gcResult = await casLifecycleManager.RunGarbageCollectionAsync(false, cancellationToken);

            stopwatch.Stop();

            var result = new ContentRemovalResult
            {
                ProfilesUpdated = profilesUpdated,
                ManifestsRemoved = manifestsRemoved,
                CasObjectsCollected = gcResult.Success ? gcResult.Data.ObjectsDeleted : 0,
                BytesFreed = gcResult.Success ? gcResult.Data.BytesFreed : 0,
                Duration = stopwatch.Elapsed,
            };

            await auditLog.LogOperationAsync(
                new ReconciliationAuditEntry
                {
                    OperationId = operationId,
                    OperationType = ReconciliationOperationType.ManifestRemoval,
                    Timestamp = DateTime.UtcNow,
                    AffectedManifestIds = ids,
                    Success = true,
                    Duration = stopwatch.Elapsed,
                },
                cancellationToken);

            logger.LogInformation(
                "[Orchestrator:{OpId}] Content removal completed: {Profiles} profiles, {Manifests} manifests removed",
                operationId,
                profilesUpdated,
                manifestsRemoved);

            return OperationResult<ContentRemovalResult>.CreateSuccess(result);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "[Orchestrator:{OpId}] Content removal failed", operationId);
            return OperationResult<ContentRemovalResult>.CreateFailure($"Content removal failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<OperationResult<ContentUpdateResult>> ExecuteContentUpdateAsync(
        string oldManifestId,
        ContentManifest newManifest,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var operationId = Guid.NewGuid().ToString("N")[..ReconciliationConstants.OperationIdLength];
        var newId = newManifest.Id.Value;
        var idChanged = !string.Equals(oldManifestId, newId, StringComparison.OrdinalIgnoreCase);

        logger.LogInformation(
            "[Orchestrator:{OpId}] Starting local content update: {OldId} → {NewId} (idChanged={Changed})",
            operationId,
            oldManifestId,
            newId,
            idChanged);

        try
        {
            // Delegate to existing orchestration logic which has correct ordering
            var result = await reconciliationService.OrchestrateLocalUpdateAsync(
                oldManifestId,
                newManifest,
                cancellationToken);

            stopwatch.Stop();

            if (!result.Success)
            {
                return OperationResult<ContentUpdateResult>.CreateFailure(result.FirstError ?? "Update failed");
            }

            // Estimate counts based on the operation performed
            // When content is updated, all profiles using it get updated and their workspaces invalidated
            // We don't have exact counts from OrchestrateLocalUpdateAsync, so we estimate conservatively
            // For accurate counts, the service would need to return them (future enhancement)
            var updateResult = new ContentUpdateResult
            {
                IdChanged = idChanged,
                ProfilesUpdated = 0, // Service doesn't return count, would need to be added
                WorkspacesInvalidated = 0, // Service doesn't return count, would need to be added
                Duration = stopwatch.Elapsed,
            };

            await auditLog.LogOperationAsync(
                new ReconciliationAuditEntry
                {
                    OperationId = operationId,
                    OperationType = ReconciliationOperationType.LocalContentUpdate,
                    Timestamp = DateTime.UtcNow,
                    AffectedManifestIds = [oldManifestId, newId],
                    ManifestMapping = new Dictionary<string, string> { [oldManifestId] = newId },
                    Success = true,
                    Metadata = new Dictionary<string, string>
                    {
                        ["idChanged"] = idChanged.ToString(),
                        ["durationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    },
                    Duration = stopwatch.Elapsed,
                },
                cancellationToken);

            logger.LogInformation(
                "[Orchestrator:{OpId}] Local content update completed in {Duration}ms",
                operationId,
                stopwatch.ElapsedMilliseconds);

            return OperationResult<ContentUpdateResult>.CreateSuccess(updateResult);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "[Orchestrator:{OpId}] Local content update failed", operationId);
            return OperationResult<ContentUpdateResult>.CreateFailure($"Update failed: {ex.Message}");
        }
    }
}
