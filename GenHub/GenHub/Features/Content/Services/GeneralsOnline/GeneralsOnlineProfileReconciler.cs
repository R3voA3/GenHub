using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Constants;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Notifications;
using GenHub.Core.Models.Content;
using GenHub.Core.Models.Enums;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using Microsoft.Extensions.Logging;

namespace GenHub.Features.Content.Services.GeneralsOnline;

/// <summary>
/// Service for reconciling profiles when GeneralsOnline updates are detected.
/// When an update is found, this service updates all profiles using GeneralsOnline,
/// removes old manifests and CAS content, and prepares profiles for the new version.
/// </summary>
public class GeneralsOnlineProfileReconciler(
    ILogger<GeneralsOnlineProfileReconciler> logger,
    GeneralsOnlineUpdateService updateService,
    IContentManifestPool manifestPool,
    IContentOrchestrator contentOrchestrator,
    IContentReconciliationService reconciliationService,
    INotificationService notificationService)
    : IGeneralsOnlineProfileReconciler
{
    /// <inheritdoc/>
    public async Task<OperationResult<bool>> CheckAndReconcileIfNeededAsync(
        string triggeringProfileId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation(
                "[GO Reconciler] Checking for GeneralsOnline updates (triggered by profile: {ProfileId})",
                triggeringProfileId);

            // Step 1: Check for updates
            var updateResult = await updateService.CheckForUpdatesAsync(cancellationToken);

            if (!updateResult.Success)
            {
                logger.LogWarning(
                    "[GO Reconciler] Update check failed: {Error}",
                    updateResult.FirstError);
                return OperationResult<bool>.CreateFailure(
                    $"Failed to check for GeneralsOnline updates: {updateResult.FirstError}");
            }

            if (!updateResult.IsUpdateAvailable)
            {
                logger.LogInformation(
                    "[GO Reconciler] No update available. Current version: {Version}",
                    updateResult.CurrentVersion);
                return OperationResult<bool>.CreateSuccess(false);
            }

            logger.LogInformation(
                "[GO Reconciler] Update available! Current: {CurrentVersion}, Latest: {LatestVersion}",
                updateResult.CurrentVersion,
                updateResult.LatestVersion);

            // Step 2: Notify user that update is being installed
            notificationService.ShowInfo(
                "GeneralsOnline Update Found",
                $"Installing GeneralsOnline {updateResult.LatestVersion}. Please wait...",
                NotificationDurations.VeryLong);

            // Step 3: Find all GeneralsOnline manifests currently installed
            var oldManifests = await FindGeneralsOnlineManifestsAsync(cancellationToken);
            if (oldManifests.Count == 0)
            {
                logger.LogWarning("[GO Reconciler] No existing GeneralsOnline manifests found in pool");
            }

            logger.LogInformation(
                "[GO Reconciler] Found {Count} existing GeneralsOnline manifests to replace",
                oldManifests.Count);

            // Step 4: Download and acquire new content
            var acquireResult = await AcquireLatestVersionAsync(oldManifests, cancellationToken);
            if (!acquireResult.Success)
            {
                notificationService.ShowError(
                    "GeneralsOnline Update Failed",
                    $"Failed to download update: {acquireResult.FirstError}",
                    NotificationDurations.Critical);

                return OperationResult<bool>.CreateFailure(
                    $"Failed to acquire new GeneralsOnline version: {acquireResult.FirstError}");
            }

            var newManifests = acquireResult.Data!;
            logger.LogInformation(
                "[GO Reconciler] Successfully acquired {Count} new manifests",
                newManifests.Count);

            // Step 5: Update all affected profiles using unified service
            var manifestMapping = BuildManifestMapping(oldManifests, newManifests);
            var updateProfilesResult = await reconciliationService.ReconcileBulkManifestReplacementAsync(
                manifestMapping,
                cancellationToken);

            if (!updateProfilesResult.Success)
            {
                notificationService.ShowWarning(
                    "GeneralsOnline Update Partial",
                    $"Some profiles could not be updated: {updateProfilesResult.FirstError}",
                    NotificationDurations.VeryLong);
            }

            // Step 6: Remove old manifests from pool (excluding any that match the new ones)
            await RemoveOldManifestsAsync(oldManifests, newManifests, cancellationToken);

            // Step 7: Run garbage collection AFTER untracking is complete
            await reconciliationService.ScheduleGarbageCollectionAsync(false, cancellationToken);

            // Step 8: Show success notification
            notificationService.ShowSuccess(
                "GeneralsOnline Updated",
                $"Successfully updated to version {updateResult.LatestVersion}. {updateProfilesResult.Data} profiles updated.",
                NotificationDurations.Long);

            logger.LogInformation(
                "[GO Reconciler] Reconciliation complete. Updated {ProfileCount} profiles",
                updateProfilesResult.Data);

            return OperationResult<bool>.CreateSuccess(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GO Reconciler] Reconciliation failed unexpectedly");
            notificationService.ShowError(
                "GeneralsOnline Update Error",
                $"An error occurred during update: {ex.Message}",
                NotificationDurations.Critical);
            return OperationResult<bool>.CreateFailure($"Reconciliation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a mapping from old manifest IDs to new manifest IDs.
    /// </summary>
    private static Dictionary<string, string> BuildManifestMapping(
        List<ContentManifest> oldManifests,
        List<ContentManifest> newManifests)
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var oldManifest in oldManifests)
        {
            // Find corresponding new manifest by matching content type and variant
            var newManifest = newManifests.FirstOrDefault(n =>
                n.ContentType == oldManifest.ContentType &&
                MatchesByVariant(oldManifest.Id.Value, n.Id.Value));

            if (newManifest != null)
            {
                mapping[oldManifest.Id.Value] = newManifest.Id.Value;
            }
        }

        return mapping;
    }

    /// <summary>
    /// Checks if two manifest IDs refer to the same variant (30hz, 60hz, or quickmatch-maps).
    /// </summary>
    private static bool MatchesByVariant(string oldId, string newId)
    {
        var oldVariant = ExtractVariant(oldId);
        var newVariant = ExtractVariant(newId);
        return string.Equals(oldVariant, newVariant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the variant suffix from a manifest ID.
    /// </summary>
    private static string? ExtractVariant(string manifestId)
    {
        var parts = manifestId.Split('.');
        if (parts.Length == 0) return null;

        var lastPart = parts[^1];
        if (lastPart.Equals("30hz", StringComparison.OrdinalIgnoreCase) ||
            lastPart.Equals("60hz", StringComparison.OrdinalIgnoreCase) ||
            lastPart.Equals("quickmatchmaps", StringComparison.OrdinalIgnoreCase))
        {
            return lastPart;
        }

        return parts.Length > 1 ? parts[^1] : null;
    }

    /// <summary>
    /// Finds all GeneralsOnline manifests currently in the manifest pool.
    /// </summary>
    private async Task<List<ContentManifest>> FindGeneralsOnlineManifestsAsync(
        CancellationToken cancellationToken)
    {
        var manifestsResult = await manifestPool.GetAllManifestsAsync(cancellationToken);
        if (!manifestsResult.Success || manifestsResult.Data == null)
        {
            return [];
        }

        return [.. manifestsResult.Data
            .Where(m =>
                m.Publisher?.PublisherType?.Equals(PublisherTypeConstants.GeneralsOnline, StringComparison.OrdinalIgnoreCase) == true ||
                m.Id.Value.Contains(".generalsonline.", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("GeneralsOnline", StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Acquires the latest GeneralsOnline version by searching and downloading.
    /// </summary>
    private async Task<OperationResult<List<ContentManifest>>> AcquireLatestVersionAsync(
        List<ContentManifest> oldManifests,
        CancellationToken cancellationToken)
    {
        try
        {
            var searchQuery = new ContentSearchQuery
            {
                ProviderName = GeneralsOnlineConstants.PublisherType,
                ContentType = ContentType.GameClient,
                TargetGame = GameType.ZeroHour,
            };

            var searchResult = await contentOrchestrator.SearchAsync(searchQuery, cancellationToken);
            if (!searchResult.Success || searchResult.Data == null || !searchResult.Data.Any())
            {
                return OperationResult<List<ContentManifest>>.CreateFailure(
                    "No GeneralsOnline content found from provider");
            }

            foreach (var result in searchResult.Data)
            {
                await contentOrchestrator.AcquireContentAsync(result, progress: null, cancellationToken);
            }

            var allGoManifests = await FindGeneralsOnlineManifestsAsync(cancellationToken);
            var oldIds = oldManifests.Select(m => m.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newManifests = allGoManifests
                .Where(m => !oldIds.Contains(m.Id.Value))
                .ToList();

            if (newManifests.Count == 0)
            {
                return OperationResult<List<ContentManifest>>.CreateFailure(
                    "Acquisition completed but no new GeneralsOnline manifests were found in the pool");
            }

            return OperationResult<List<ContentManifest>>.CreateSuccess(newManifests);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GO Reconciler] Failed to acquire latest version");
            return OperationResult<List<ContentManifest>>.CreateFailure($"Failed to acquire latest version: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes old GeneralsOnline manifests from the manifest pool.
    /// </summary>
    private async Task RemoveOldManifestsAsync(
        List<ContentManifest> oldManifests,
        List<ContentManifest> newManifests,
        CancellationToken cancellationToken)
    {
        var newManifestIds = newManifests.Select(m => m.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in oldManifests)
        {
            if (newManifestIds.Contains(manifest.Id.Value)) continue;

            logger.LogInformation("[GO Reconciler] Removing old manifest: {ManifestId}", manifest.Id.Value);

            var removeResult = await manifestPool.RemoveManifestAsync(manifest.Id, cancellationToken);
            if (!removeResult.Success)
            {
                logger.LogWarning("[GO Reconciler] Failed to remove old manifest {ManifestId}: {Error}", manifest.Id.Value, removeResult.FirstError);
            }
        }
    }
}
