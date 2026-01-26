using System;
using System.Collections.Generic;
using System.Linq;
using GenHub.Core.Interfaces.Providers;
using GenHub.Core.Models.Providers;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace GenHub.Features.Content.Services.Catalog;

/// <summary>
/// Filters content releases based on version display policy.
/// Implements "Latest Stable Only" by default to address user feedback about version clutter.
/// </summary>
public class VersionSelector(ILogger<VersionSelector> logger) : IVersionSelector
{
    private readonly ILogger<VersionSelector> _logger = logger;

    /// <inheritdoc />
    public IEnumerable<ContentRelease> SelectReleases(
        IEnumerable<ContentRelease> releases,
        VersionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var releasesList = releases.ToList();
        if (releasesList.Count == 0)
        {
            return releasesList;
        }

        return policy switch
        {
            VersionPolicy.LatestStableOnly => GetLatestStableReleases(releasesList),
            VersionPolicy.AllVersions => releasesList,
            VersionPolicy.LatestIncludingPrereleases => GetLatestWithPrereleases(releasesList),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown version policy"),
        };
    }

    /// <summary>
    /// Selects the most recent stable (non-prerelease) content release, preferring a release marked <c>IsLatest</c>.
    /// </summary>
    /// <param name="releases">The collection of releases to search.</param>
    /// <returns>The stable release marked <c>IsLatest</c> with the latest <c>ReleaseDate</c>, or if none is marked <c>IsLatest</c> the stable release with the latest <c>ReleaseDate</c>; returns <c>null</c> if no stable releases are found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="releases"/> is <c>null</c>.</exception>
    public ContentRelease? GetLatestStable(IEnumerable<ContentRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);

        // Cache filtered and sorted collection to avoid double enumeration
        var stableReleases = releases
            .Where(r => !r.IsPrerelease)
            .OrderByDescending(r => r.ReleaseDate)
            .ThenByDescending(r => r.Version, new SemanticVersionComparer())
            .ToList();

        return stableReleases.FirstOrDefault(r => r.IsLatest) ?? stableReleases.FirstOrDefault();
    }

    /// <inheritdoc />
    public ContentRelease? GetLatest(IEnumerable<ContentRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);

        return releases
            .OrderByDescending(r => r.ReleaseDate)
            .ThenByDescending(r => r.Version, new SemanticVersionComparer())
            .FirstOrDefault();
    }

    /// <summary>
    /// Selects the most recent stable (non-prerelease) release from the provided list and returns it as a single-element sequence, or an empty sequence if none are found.
    /// </summary>
    /// <param name="releases">The list of candidate releases to search.</param>
    /// <returns>A sequence containing the latest stable release if one exists; otherwise an empty sequence.</returns>
    private IEnumerable<ContentRelease> GetLatestStableReleases(List<ContentRelease> releases)
    {
        var latest = GetLatestStable(releases);
        if (latest != null)
        {
            _logger.LogDebug("Selected latest stable release: {Version}", latest.Version);
            return [latest];
        }

        _logger.LogWarning("No stable releases found");
        return [];
    }

    /// <summary>
    /// Selects the single most recent release from the provided list, including prereleases.
    /// </summary>
    /// <param name="releases">The candidate releases to evaluate.</param>
    /// <returns>A collection containing the latest release if one exists; otherwise an empty collection.</returns>
    private IEnumerable<ContentRelease> GetLatestWithPrereleases(List<ContentRelease> releases)
    {
        var latest = GetLatest(releases);
        if (latest != null)
        {
            _logger.LogDebug("Selected latest release (including prereleases): {Version}", latest.Version);
            return [latest];
        }

        return [];
    }

    private class SemanticVersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.IsNullOrWhiteSpace(x)) return -1;
            if (string.IsNullOrWhiteSpace(y)) return 1;

            // Try to parse as semantic versions
            if (SemanticVersion.TryParse(CleanVersion(x), out var vX) &&
                SemanticVersion.TryParse(CleanVersion(y), out var vY))
            {
                return vX.CompareTo(vY);
            }

            // Fallback to string comparison
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return version;

            // Remove 'v' prefix if present
            var clean = version.TrimStart('v', 'V');

            // Handle simple major.minor cases that might be treated oddly
            return clean;
        }
    }
}