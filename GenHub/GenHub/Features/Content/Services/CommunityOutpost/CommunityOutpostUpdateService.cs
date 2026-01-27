using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Constants;
using GenHub.Core.Helpers;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Models.Content;
using GenHub.Core.Models.Results.Content;
using Microsoft.Extensions.Logging;

namespace GenHub.Features.Content.Services.CommunityOutpost;

/// <summary>
/// Background service that periodically checks for new Community Outpost patches.
/// </summary>
/// <param name="discoverer">Content discoverer.</param>
/// <param name="resolver">Content resolver.</param>
/// <param name="manifestPool">Manifest pool.</param>
/// <param name="logger">Logger instance.</param>
public class CommunityOutpostUpdateService(
    CommunityOutpostDiscoverer discoverer,
    CommunityOutpostResolver resolver,
    IContentManifestPool manifestPool,
    ILogger<CommunityOutpostUpdateService> logger)
    : ContentUpdateServiceBase(logger)
{
    /// <inheritdoc/>
    protected override string ServiceName => CommunityOutpostConstants.PublisherName;

    /// <inheritdoc/>
    protected override TimeSpan UpdateCheckInterval => TimeSpan.FromDays(1);

    /// <inheritdoc/>
    public override async Task<ContentUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Checking for Community Outpost patch updates...");

            // Discover latest content
            var discoveryResult = await discoverer.DiscoverAsync(new ContentSearchQuery(), cancellationToken);
            if (!discoveryResult.Success || discoveryResult.Data?.Items == null || !discoveryResult.Data.Items.Any())
            {
                logger.LogWarning("No Community Outpost content discovered");
                return ContentUpdateCheckResult.CreateNoUpdateAvailable();
            }

            // Get currently installed manifests from this publisher
            var manifestsResult = await manifestPool.GetAllManifestsAsync(cancellationToken);
            var installedManifests = (manifestsResult.Data ?? [])
                .Where(m => m.Publisher?.PublisherType == CommunityOutpostConstants.PublisherType)
                .ToList();

            if (installedManifests.Count == 0)
            {
                logger.LogInformation("No Community Outpost content installed. No updates possible.");
                return ContentUpdateCheckResult.CreateNoUpdateAvailable();
            }

            // Check if any installed manifest has a newer version in the catalog
            ContentSearchResult? latestToResolve = null;
            string? currentVersion = null;

            foreach (var discovered in discoveryResult.Data.Items)
            {
                var installed = installedManifests.FirstOrDefault(m =>
                    m.Id.Value.Equals(discovered.Id, StringComparison.OrdinalIgnoreCase));

                if (installed != null)
                {
                    if (VersionComparer.CompareVersions(discovered.Version, installed.ManifestVersion, CommunityOutpostConstants.PublisherType) > 0)
                    {
                        logger.LogInformation("Update found for {Id}: {OldVersion} -> {NewVersion}", discovered.Id, installed.ManifestVersion, discovered.Version);

                        // Return the first update found
                        if (latestToResolve == null)
                        {
                            latestToResolve = discovered;
                            currentVersion = installed.ManifestVersion;
                        }
                    }
                }
            }

            if (latestToResolve == null)
            {
                logger.LogInformation("All installed Community Outpost content is up to date");
                return ContentUpdateCheckResult.CreateNoUpdateAvailable(installedManifests.FirstOrDefault()?.ManifestVersion);
            }

            var latestVersion = latestToResolve.Version;
            logger.LogInformation("Update available: {Id} version {Version}", latestToResolve.Id, latestVersion);

            // Resolve new content to manifest for verification
            var resolveResult = await resolver.ResolveAsync(latestToResolve, cancellationToken);

            if (!resolveResult.Success || resolveResult.Data == null)
            {
                logger.LogError("Failed to resolve Community Outpost content: {Error}", resolveResult.FirstError);
                return ContentUpdateCheckResult.CreateFailure($"Failed to resolve: {resolveResult.FirstError}", currentVersion);
            }

            return ContentUpdateCheckResult.CreateUpdateAvailable(
                latestVersion,
                currentVersion);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking for Community Outpost updates");
            return ContentUpdateCheckResult.CreateFailure(ex.Message);
        }
    }
}
