using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Storage;
using GenHub.Core.Interfaces.Workspace;
using GenHub.Core.Models.GameClients;
using GenHub.Core.Models.GameProfile;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using GenHub.Features.Storage.Services;
using Microsoft.Extensions.Logging;

namespace GenHub.Features.Content.Services;

/// <summary>
/// Core implementation of the unified content reconciliation service.
/// </summary>
public class ContentReconciliationService(
    IGameProfileManager profileManager,
    IWorkspaceManager workspaceManager,
    IContentManifestPool manifestPool,
    CasReferenceTracker referenceTracker,
    ICasService casService,
    ILogger<ContentReconciliationService> logger) : IContentReconciliationService
{
    /// <inheritdoc />
    public Task<OperationResult<int>> ReconcileManifestReplacementAsync(
        string oldId,
        string newId,
        CancellationToken cancellationToken = default)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { oldId, newId } };
        return ReconcileBulkManifestReplacementAsync(replacements, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OperationResult<int>> ReconcileBulkManifestReplacementAsync(
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken = default)
    {
        if (replacements == null || replacements.Count == 0)
        {
            return OperationResult<int>.CreateSuccess(0);
        }

        var oldIds = replacements.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("Reconciling: Performing bulk replacement of {Count} manifests in all profiles", replacements.Count);

        var profilesResult = await profileManager.GetAllProfilesAsync(cancellationToken);
        if (!profilesResult.Success)
        {
            return OperationResult<int>.CreateFailure($"Failed to retrieve profiles: {profilesResult.FirstError}");
        }

        var affectedProfiles = profilesResult.Data?.Where(p =>
            (p.EnabledContentIds?.Any(id => oldIds.Contains(id)) == true) ||
            (p.GameClient != null && oldIds.Contains(p.GameClient.Id))).ToList() ?? [];

        if (affectedProfiles.Count == 0)
        {
            logger.LogInformation("No profiles referenced affected manifests for bulk reconciliation");
            return OperationResult<int>.CreateSuccess(0);
        }

        logger.LogInformation("Found {Count} affected profiles for bulk reconciliation", affectedProfiles.Count);

        int updatedCount = 0;
        foreach (var profile in affectedProfiles)
        {
            try
            {
                var newContentIds = profile.EnabledContentIds!
                    .Select(id => replacements.TryGetValue(id, out var newId) ? newId : id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                GameClient? newGameClient = null;
                if (profile.GameClient != null && replacements.TryGetValue(profile.GameClient.Id, out var newClientId))
                {
                    // Find the new manifest to create a proper GameClient object
                    var manifestResult = await manifestPool.GetManifestAsync(ManifestId.Create(newClientId), cancellationToken);
                    if (manifestResult.Success && manifestResult.Data != null)
                    {
                        var m = manifestResult.Data;
                        newGameClient = new GameClient
                        {
                            Id = m.Id.Value,
                            Name = m.Name,
                            Version = m.Version ?? string.Empty,
                            GameType = m.TargetGame,
                            SourceType = m.ContentType,
                            PublisherType = m.Publisher?.PublisherType,
                            InstallationId = profile.GameClient.InstallationId, // Preserve installation link
                        };
                    }
                    else
                    {
                        logger.LogError("Failed to resolve new game client manifest '{NewId}' for profile '{ProfileName}'. Skipping update to prevent corruption.", newClientId, profile.Name);
                        continue;
                    }
                }

                // Clear workspace to force launch-time sync
                if (!string.IsNullOrEmpty(profile.ActiveWorkspaceId))
                {
                    logger.LogDebug("Cleaning up workspace '{WorkspaceId}' for stale profile '{ProfileName}'", profile.ActiveWorkspaceId, profile.Name);
                    await workspaceManager.CleanupWorkspaceAsync(profile.ActiveWorkspaceId, cancellationToken);
                }

                var updateRequest = new UpdateProfileRequest
                {
                    EnabledContentIds = newContentIds,
                    GameClient = newGameClient,
                    ActiveWorkspaceId = string.Empty,
                };

                var updateResult = await profileManager.UpdateProfileAsync(profile.Id, updateRequest, cancellationToken);
                if (updateResult.Success)
                {
                    updatedCount++;
                    NotifyProfileUpdated(profile.Id, cancellationToken);
                }
                else
                {
                    logger.LogWarning("Failed to update profile '{ProfileName}': {Error}", profile.Name, updateResult.FirstError);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error reconciling profile '{ProfileName}'", profile.Name);
            }
        }

        foreach (var replacement in replacements)
        {
            WeakReferenceMessenger.Default.Send(new ManifestReplacedMessage(replacement.Key, replacement.Value));
        }

        logger.LogInformation("Bulk reconciliation complete. Updated {Count} profiles.", updatedCount);

        return OperationResult<int>.CreateSuccess(updatedCount);
    }

    /// <inheritdoc />
    public async Task<OperationResult<int>> ReconcileManifestRemovalAsync(
        string manifestId,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Reconciling: Removing manifest '{Id}' from all profiles", manifestId);

        var profilesResult = await profileManager.GetAllProfilesAsync(cancellationToken);
        if (!profilesResult.Success)
        {
            return OperationResult<int>.CreateFailure($"Failed to retrieve profiles: {profilesResult.FirstError}");
        }

        var affectedProfiles = profilesResult.Data?.Where(p =>
            p.EnabledContentIds?.Contains(manifestId, StringComparer.OrdinalIgnoreCase) == true).ToList() ?? [];

        if (affectedProfiles.Count == 0)
        {
            return OperationResult<int>.CreateSuccess(0);
        }

        int updatedCount = 0;
        foreach (var profile in affectedProfiles)
        {
            try
            {
                var newContentIds = profile.EnabledContentIds!
                    .Where(id => !id.Equals(manifestId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!string.IsNullOrEmpty(profile.ActiveWorkspaceId))
                {
                    await workspaceManager.CleanupWorkspaceAsync(profile.ActiveWorkspaceId, cancellationToken);
                }

                var updateRequest = new UpdateProfileRequest
                {
                    EnabledContentIds = newContentIds,
                    ActiveWorkspaceId = string.Empty,
                };

                var updateResult = await profileManager.UpdateProfileAsync(profile.Id, updateRequest, cancellationToken);
                if (updateResult.Success)
                {
                    updatedCount++;
                    NotifyProfileUpdated(profile.Id, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error removing manifest from profile '{ProfileName}'", profile.Name);
            }
        }

        return OperationResult<int>.CreateSuccess(updatedCount);
    }

    /// <inheritdoc />
    public async Task<OperationResult> OrchestrateLocalUpdateAsync(
        string oldId,
        ContentManifest newManifest,
        CancellationToken cancellationToken = default)
    {
        string newId = newManifest.Id.Value;
        bool idChanged = !string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase);

        try
        {
            // 1. Track new manifest CAS references FIRST (before any workspace invalidation)
            // This ensures CAS objects are tracked before workspace rebuild attempts to use them
            await referenceTracker.TrackManifestReferencesAsync(newId, newManifest, cancellationToken);
            logger.LogDebug("Tracked CAS references for manifest '{ManifestId}'", newId);

            // 2. Reconcile Profiles
            if (idChanged)
            {
                // Ensure the new manifest is available in the pool before attempting reconciliation
                // This prevents race conditions where GetManifestAsync fails to find the just-created manifest
                await manifestPool.AddManifestAsync(newManifest, cancellationToken);

                await ReconcileManifestReplacementAsync(oldId, newId, cancellationToken);
            }
            else
            {
                // Even if ID is same, content might have changed (files removed/added).
                // We clear workspaces to ensure deltas are applied at launch.
                // This is safe because we've already tracked the new CAS references above.
                await InvalidateWorkspacesForManifestAsync(newId, cancellationToken);
            }

            // 3. Untrack old manifest if ID changed
            if (idChanged)
            {
                logger.LogInformation("Untracking old manifest references for '{OldId}'", oldId);
                await referenceTracker.UntrackManifestAsync(oldId, cancellationToken);

                // 4. Remove Old Manifest from pool
                await manifestPool.RemoveManifestAsync(ManifestId.Create(oldId), cancellationToken);
            }

            return OperationResult.CreateSuccess();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to orchestrate local content update for '{OldId}'", oldId);
            return OperationResult.CreateFailure($"Orchestration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedules garbage collection. Should be called AFTER all untrack operations complete.
    /// </summary>
    /// <param name="force">If set to true, forces garbage collection even if not strictly needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public Task<OperationResult> ScheduleGarbageCollectionAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            async () =>
            {
                try
                {
                    await casService.RunGarbageCollectionAsync(force, cancellationToken);
                    return OperationResult.CreateSuccess();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scheduled garbage collection failed");
                    return OperationResult.CreateFailure($"GC failed: {ex.Message}");
                }
            },
            cancellationToken);
    }

    private async Task InvalidateWorkspacesForManifestAsync(string manifestId, CancellationToken cancellationToken)
    {
        var profilesResult = await profileManager.GetAllProfilesAsync(cancellationToken);
        if (!profilesResult.Success) return;

        var affectedProfiles = profilesResult.Data.Where(p =>
            p.EnabledContentIds?.Contains(manifestId, StringComparer.OrdinalIgnoreCase) == true &&
            !string.IsNullOrEmpty(p.ActiveWorkspaceId)).ToList();

        foreach (var profile in affectedProfiles)
        {
            logger.LogDebug("Invalidating workspace for profile '{ProfileName}' due to manifest update", profile.Name);
            await workspaceManager.CleanupWorkspaceAsync(profile.ActiveWorkspaceId!, cancellationToken);
            await profileManager.UpdateProfileAsync(profile.Id, new UpdateProfileRequest { ActiveWorkspaceId = string.Empty }, cancellationToken);
            NotifyProfileUpdated(profile.Id, cancellationToken);
        }
    }

    private void NotifyProfileUpdated(string profileId, CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await profileManager.GetProfileAsync(profileId, cancellationToken);
                    if (result.Success && result.Data is GameProfile updatedProfile)
                    {
                        WeakReferenceMessenger.Default.Send(new Core.Models.GameProfile.ProfileUpdatedMessage(updatedProfile));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to notify profile update for '{ProfileId}'", profileId);
                }
            },
            cancellationToken);
    }
}
