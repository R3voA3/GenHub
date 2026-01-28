using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Constants;
using GenHub.Core.Interfaces.Common;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Notifications;
using GenHub.Core.Models.Content;
using GenHub.Core.Models.Dialogs;
using GenHub.Core.Models.Enums;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using GenHub.Core.Models.Results.Content;
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
    INotificationService notificationService,
    IDialogService dialogService,
    IUserSettingsService userSettingsService,
    IGameProfileManager profileManager)
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

            // Check if this specific version is skipped
            var settings = userSettingsService.Get();
            if (settings.SkippedUpdateVersions.TryGetValue(GeneralsOnlineConstants.PublisherType, out var skippedVer) &&
                skippedVer == updateResult.LatestVersion)
            {
                logger.LogInformation("[GO Reconciler] User opted to skip version {Version}. Skipping.", updateResult.LatestVersion);
                return OperationResult<bool>.CreateSuccess(false);
            }

            // Determine strategy
            UpdateStrategy strategy = settings.PreferredUpdateStrategy ?? UpdateStrategy.ReplaceCurrent;
            bool autoUpdate = settings.AutoUpdateGeneralsOnline == true;

            // Prompt user if preference is not set (AutoUpdate is null/false or Strategy is null/implied)
            // But we only skip dialog if AutoUpdate is TRUE.
            if (!autoUpdate)
            {
                var dialogResult = await dialogService.ShowUpdateOptionDialogAsync(
                    "Generals Online Update Available",
                    $"A new version of **Generals Online** is available ({updateResult.LatestVersion}).\n\nHow do you want to apply this update?");

                if (dialogResult == null) return OperationResult<bool>.CreateSuccess(false);

                if (dialogResult.Action == "Skip")
                {
                    logger.LogInformation("[GO Reconciler] User skipped version {Version}.", updateResult.LatestVersion);

                    // Always save skipped version unless they explicitly unchecked "Do not ask again"?
                    // Actually the "Skip Update" button usually implies skipping THIS version.
                    // The "Don't ask again" checkbox might mean "Skip ALL updates"? No, that's AutoUpdate=False.
                    // I'll assume Skip Button + DoNotAskAgain means "Skip THIS version permanently" (which is what SkippedVersions dict does).
                    await userSettingsService.TryUpdateAndSaveAsync(s =>
                    {
                        s.SkippedUpdateVersions[GeneralsOnlineConstants.PublisherType] = updateResult.LatestVersion ?? string.Empty;
                        return true;
                    });
                    return OperationResult<bool>.CreateSuccess(false);
                }

                strategy = dialogResult.Strategy;

                if (dialogResult.IsDoNotAskAgain)
                {
                    logger.LogInformation("[GO Reconciler] Saving user preference for GeneralsOnline updates");
                    await userSettingsService.TryUpdateAndSaveAsync(s =>
                    {
                        s.AutoUpdateGeneralsOnline = true;
                        s.PreferredUpdateStrategy = strategy;
                        return true;
                    });
                }
            }

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

            // Step 5: Update affected profiles based on strategy
            int profilesUpdated = 0;

            if (strategy == UpdateStrategy.CreateNewProfile)
            {
                var createResult = await CreateNewProfilesForUpdateAsync(oldManifests, newManifests, updateResult.LatestVersion ?? "Unknown", cancellationToken);
                if (createResult.Success) profilesUpdated = createResult.Data;
                else notificationService.ShowWarning("GeneralsOnline Update Partial", $"Failed to create some new profiles: {createResult.FirstError}");
            }
            else
            {
                // ReplaceCurrent
                var manifestMapping = BuildManifestMapping(oldManifests, newManifests);
                var updateProfilesResult = await reconciliationService.ReconcileBulkManifestReplacementAsync(
                    manifestMapping,
                    cancellationToken);

                if (updateProfilesResult.Success) profilesUpdated = updateProfilesResult.Data;
                else notificationService.ShowWarning("GeneralsOnline Update Partial", $"Some profiles could not be updated: {updateProfilesResult.FirstError}", NotificationDurations.VeryLong);

                // For ReplaceCurrent, we generally want to remove old manifests if configured
                // Check if user wants to keep old versions even when replacing (Setting)
                if (settings.DeleteOldGeneralsOnlineVersions)
                {
                    // Remove old manifests
                     await RemoveOldManifestsAsync(oldManifests, newManifests, cancellationToken);
                }
            }

            // Step 5.5: Enforce MapPack dependency (add MapPack to profile if missing)
            // This applies to BOTH strategies (New profiles need it too, and existing ones need it)
            // But CreateNewProfilesForUpdateAsync handles it internally for new profiles?
            // Better to run it broadly just in case.
            await EnforceMapPackDependencyAsync(newManifests, cancellationToken);

            // Step 7: Run garbage collection
            await reconciliationService.ScheduleGarbageCollectionAsync(false, cancellationToken);

            // Step 8: Show success notification
            notificationService.ShowSuccess(
                "GeneralsOnline Updated",
                $"Successfully updated to version {updateResult.LatestVersion}. {profilesUpdated} profiles {(strategy == UpdateStrategy.CreateNewProfile ? "created" : "updated")}.",
                NotificationDurations.Long);

            logger.LogInformation(
                "[GO Reconciler] Reconciliation complete. Processed {ProfileCount} profiles with strategy {Strategy}",
                profilesUpdated,
                strategy);

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
            // Find corresponding new manifest by matching variant
            var newManifest = newManifests.FirstOrDefault(n =>
                (n.ContentType == oldManifest.ContentType ||
                 (oldManifest.ContentType == Core.Models.Enums.ContentType.Mod && n.ContentType == Core.Models.Enums.ContentType.GameClient)) &&
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

        // Check for legacy ID formats
        if (lastPart.Equals("generalsonlinezh-60", StringComparison.OrdinalIgnoreCase))
        {
            return "60hz";
        }

        if (lastPart.Equals("generalsonlinezh", StringComparison.OrdinalIgnoreCase) ||
            lastPart.Equals("generalsonlinezh-30", StringComparison.OrdinalIgnoreCase))
        {
            return "30hz";
        }

        // Check for map pack variations
        if (lastPart.Contains("quickmatchmaps", StringComparison.OrdinalIgnoreCase) ||
            lastPart.Contains("generalsonlinemaps", StringComparison.OrdinalIgnoreCase))
        {
            return "quickmatchmaps";
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
                !m.Id.Value.Contains(".local.", StringComparison.OrdinalIgnoreCase) && // Exclude local content
                (m.Publisher?.PublisherType?.Equals(PublisherTypeConstants.GeneralsOnline, StringComparison.OrdinalIgnoreCase) == true ||
                 m.Id.Value.Contains(".generalsonline.", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Contains("GeneralsOnline", StringComparison.OrdinalIgnoreCase)))];
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
            // Search for Game Client
            var clientQuery = new ContentSearchQuery
            {
                ProviderName = GeneralsOnlineConstants.PublisherType,
                ContentType = ContentType.GameClient,
                TargetGame = GameType.ZeroHour,
            };

            var clientResult = await contentOrchestrator.SearchAsync(clientQuery, cancellationToken);

            // Search for Map Packs (required dependency)
            var mapPackQuery = new ContentSearchQuery
            {
                ProviderName = GeneralsOnlineConstants.PublisherType,
                ContentType = ContentType.MapPack,
                TargetGame = GameType.ZeroHour,
            };

            var mapPackResult = await contentOrchestrator.SearchAsync(mapPackQuery, cancellationToken);

            var allResults = new List<ContentSearchResult>();

            if (clientResult.Success && clientResult.Data != null)
                allResults.AddRange(clientResult.Data);

            if (mapPackResult.Success && mapPackResult.Data != null)
                allResults.AddRange(mapPackResult.Data);

            if (allResults.Count == 0)
            {
                return OperationResult<List<ContentManifest>>.CreateFailure(
                    "No GeneralsOnline content found from provider");
            }

            foreach (var result in allResults)
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
    /// Enforces that profiles using the new GeneralsOnline client also have the new MapPack.
    /// </summary>
    private async Task EnforceMapPackDependencyAsync(
        List<ContentManifest> newManifests,
        CancellationToken cancellationToken)
    {
        // 1. Identify the new MapPack ID and GameClient IDs
        var newMapPack = newManifests.FirstOrDefault(m => m.ContentType == ContentType.MapPack);
        var newMapPackId = newMapPack?.Id.Value;

        // If not found in newManifests, try to find the latest MapPack in the pool
        if (newMapPackId == null)
        {
            var poolManifests = await manifestPool.GetAllManifestsAsync(cancellationToken);
            if (poolManifests.Success && poolManifests.Data != null)
            {
                var latestMapPack = poolManifests.Data
                    .Where(m => m.Publisher?.PublisherType == PublisherTypeConstants.GeneralsOnline && m.ContentType == ContentType.MapPack)
                    .OrderByDescending(m => m.Version) // Simple version sort should be enough for same publisher
                    .FirstOrDefault();

                newMapPackId = latestMapPack?.Id.Value;
            }
        }

        var newGameClients = newManifests
            .Where(m => m.ContentType == ContentType.GameClient)
            .Select(m => m.Id.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (newMapPackId == null || newGameClients.Count == 0)
        {
            logger.LogInformation("[GO Reconciler] No MapPack (found: {HasMapPack}) or GameClient ({ClientCount}) found for dependency enforcement.", newMapPackId != null, newGameClients.Count);
            return;
        }

        // 2. Get all profiles
        var allProfilesResult = await profileManager.GetAllProfilesAsync(cancellationToken);
        if (!allProfilesResult.Success || allProfilesResult.Data == null)
        {
            logger.LogWarning("[GO Reconciler] Failed to retrieve profiles for dependency enforcement.");
            return;
        }

        // 3. Iterate profiles and patch if needed
        foreach (var profile in allProfilesResult.Data)
        {
            // Check if profile uses one of the new GameClients
            // GameClient might be null for some profiles
            // Check if profile uses one of the new GameClients OR looks like a Generals Online profile
            // We do this relaxed check because sometimes the profile update might not be reflected immediately in the repository cache,
            // so checking strictly against 'newGameClients' might fail if the profile still has the old ID in memory.
            bool isGeneralsOnline = profile.GameClient != null &&
                                    (newGameClients.Contains(profile.GameClient.Id) ||
                                     profile.GameClient.Id.Contains("generalsonline", StringComparison.OrdinalIgnoreCase) ||
                                     (profile.GameClient.Name?.Contains("GeneralsOnline", StringComparison.OrdinalIgnoreCase) ?? false));

            if (!isGeneralsOnline)
            {
                continue;
            }

            // Check if profile already has the new MapPack
            // We also check enabled content IDs.
            var hasMapPack = profile.EnabledContentIds?.Contains(newMapPackId, StringComparer.OrdinalIgnoreCase) ?? false;

            if (!hasMapPack)
            {
                logger.LogInformation("[GO Reconciler] Adding required MapPack {MapPackId} to profile {ProfileName}", newMapPackId, profile.Name);

                var newEnabledContent = profile.EnabledContentIds != null
                    ? new List<string>(profile.EnabledContentIds)
                    : [];

                newEnabledContent.Add(newMapPackId);

                var updateRequest = new Core.Models.GameProfile.UpdateProfileRequest
                {
                    EnabledContentIds = newEnabledContent,
                };

                var updateResult = await profileManager.UpdateProfileAsync(profile.Id, updateRequest, cancellationToken);
                if (!updateResult.Success)
                {
                    logger.LogError("[GO Reconciler] Failed to add MapPack to profile {ProfileName}: {Error}", profile.Name, updateResult.FirstError);
                }
            }
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

    /// <summary>
    /// Creates new profiles for the update instead of replacing existing ones.
    /// </summary>
    private async Task<OperationResult<int>> CreateNewProfilesForUpdateAsync(
        List<ContentManifest> oldManifests,
        List<ContentManifest> newManifests,
        string newVersion,
        CancellationToken cancellationToken)
    {
        var oldIds = oldManifests.Select(m => m.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifestMapping = BuildManifestMapping(oldManifests, newManifests);
        int createdCount = 0;

        var allProfiles = await profileManager.GetAllProfilesAsync(cancellationToken);
        if (!allProfiles.Success || allProfiles.Data == null) return OperationResult<int>.CreateSuccess(0);

        foreach (var profile in allProfiles.Data)
        {
            // Check if profile is relevant (uses any Old GeneralsOnline manifest)
            bool isRelevant = (profile.GameClient != null && oldIds.Contains(profile.GameClient.Id)) ||
                              (profile.EnabledContentIds?.Any(id => oldIds.Contains(id)) == true);

            if (!isRelevant) continue;

            try
            {
                // Clone the profile
                var cloneRequest = new Core.Models.GameProfile.CreateProfileRequest
                {
                   Name = $"{profile.Name} (v{newVersion})",
                   GameInstallationId = profile.GameInstallationId,
                   PreferredStrategy = profile.WorkspaceStrategy,
                   GameClient = profile.GameClient,
                };

                // Calculate new content IDs
                var newEnabledContent = new List<string>();
                if (profile.EnabledContentIds != null)
                {
                    foreach (var id in profile.EnabledContentIds)
                    {
                        if (manifestMapping.TryGetValue(id, out var newId))
                        {
                            newEnabledContent.Add(newId);
                        }
                        else
                        {
                            newEnabledContent.Add(id); // Keep non-GO content
                        }
                    }
                }

                cloneRequest.EnabledContentIds = newEnabledContent;

                var createResult = await profileManager.CreateProfileAsync(cloneRequest, cancellationToken);
                if (createResult.Success)
                {
                    createdCount++;
                    logger.LogInformation("[GO Reconciler] Created new profile '{Name}' for update", cloneRequest.Name);
                }
                else
                {
                    logger.LogError("[GO Reconciler] Failed to create new profile for update: {Error}", createResult.FirstError);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GO Reconciler] Error creating profile for update");
            }
        }

        return OperationResult<int>.CreateSuccess(createdCount);
    }
}
