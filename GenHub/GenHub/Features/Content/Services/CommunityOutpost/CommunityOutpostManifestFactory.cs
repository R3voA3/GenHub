using GenHub.Core.Constants;
using GenHub.Core.Interfaces.Common;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Models.CommunityOutpost;
using GenHub.Core.Models.Enums;
using GenHub.Core.Models.Manifest;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GenHub.Features.Content.Services.CommunityOutpost;

/// <summary>
/// Manifest factory for Community Outpost publisher.
/// Handles single-content releases (patches, addons, maps, etc.) from the GenPatcher catalog.
/// Creates manifests with proper file entries and install targets.
/// </summary>
public class CommunityOutpostManifestFactory(
    ILogger<CommunityOutpostManifestFactory> logger,
    IFileHashProvider hashProvider) : IPublisherManifestFactory
{
    /// <summary>
    /// Extracts the content code from manifest metadata tags.
    /// </summary>
    private static string GetContentCodeFromManifest(ContentManifest manifest)
    {
        // Look for contentCode tag in metadata
        var contentCodeTag = manifest.Metadata?.Tags?
            .FirstOrDefault(t => t.StartsWith("contentCode:", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(contentCodeTag))
        {
            return contentCodeTag["contentCode:".Length..];
        }

        // Try to extract from manifest ID
        // Format: 1.version.communityoutpost.contentType.contentName
        var idParts = manifest.Id.Value?.Split('.') ?? [];
        if (idParts.Length >= 5)
        {
            return idParts[4]; // The content name part
        }

        return "unknown";
    }

    /// <summary>
    /// Determines the install target for a specific file based on its path and content type.
    /// </summary>
    private static ContentInstallTarget DetermineFileInstallTarget(
        string relativePath,
        ContentInstallTarget defaultTarget)
    {
        // Normalize path separators
        var normalizedPath = relativePath.Replace('\\', '/').ToLowerInvariant();

        // Map files (.map extension or in Maps folder) always go to UserMapsDirectory
        if (normalizedPath.EndsWith(".map") ||
            normalizedPath.Contains("/maps/") ||
            normalizedPath.StartsWith("maps/"))
        {
            return ContentInstallTarget.UserMapsDirectory;
        }

        // Replay files go to UserReplaysDirectory
        if (normalizedPath.EndsWith(".rep") ||
            normalizedPath.Contains("/replays/") ||
            normalizedPath.StartsWith("replays/"))
        {
            return ContentInstallTarget.UserReplaysDirectory;
        }

        // Screenshot files go to UserScreenshotsDirectory
        if ((normalizedPath.EndsWith(".bmp") || normalizedPath.EndsWith(".png") || normalizedPath.EndsWith(".jpg")) &&
            (normalizedPath.Contains("/screenshots/") || normalizedPath.StartsWith("screenshots/")))
        {
            return ContentInstallTarget.UserScreenshotsDirectory;
        }

        // Game data files (BIG, INI, etc.) go to workspace
        if (normalizedPath.EndsWith(".big") ||
            normalizedPath.EndsWith(".ini") ||
            normalizedPath.EndsWith(".exe") ||
            normalizedPath.EndsWith(".dll") ||
            normalizedPath.Contains("/data/"))
        {
            return ContentInstallTarget.Workspace;
        }

        // Use the content type's default target
        return defaultTarget;
    }

    /// <inheritdoc />
    public string PublisherId => CommunityOutpostConstants.PublisherId;

    /// <inheritdoc />
    public bool CanHandle(ContentManifest manifest)
    {
        var publisherMatches = manifest.Publisher?.PublisherType?.Equals(
            CommunityOutpostConstants.PublisherType,
            StringComparison.OrdinalIgnoreCase) == true;

        logger.LogDebug(
            "CanHandle check for manifest {ManifestId}: Publisher={Publisher}, Type={PublisherType}, Result={Result}",
            manifest.Id,
            manifest.Publisher?.Name,
            manifest.Publisher?.PublisherType,
            publisherMatches);

        return publisherMatches;
    }

    /// <inheritdoc />
    public async Task<List<ContentManifest>> CreateManifestsFromExtractedContentAsync(
        ContentManifest originalManifest,
        string extractedDirectory,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Creating Community Outpost manifest from extracted content in: {Directory}",
            extractedDirectory);

        if (!Directory.Exists(extractedDirectory))
        {
            logger.LogError("Extracted directory does not exist: {Directory}", extractedDirectory);
            return [];
        }

        // Get the content code and install target from the original manifest metadata
        var contentCode = GetContentCodeFromManifest(originalManifest);
        var contentMetadata = GenPatcherContentRegistry.GetMetadata(contentCode);

        logger.LogInformation(
            "Processing content: {Name} ({ContentType}) with content code {Code}, InstallTarget={InstallTarget}",
            originalManifest.Name,
            originalManifest.ContentType,
            contentCode,
            contentMetadata.InstallTarget);

        // Check for variants in subdirectories (e.g., ZH/BIG EN, CCG/BIG DE)
        var variants = DetectVariants(extractedDirectory);
        if (variants.Count > 0)
        {
            logger.LogInformation("Detected {VariantCount} variants in package", variants.Count);
            var results = new List<ContentManifest>();

            foreach (var variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var variantManifest = await BuildVariantManifestAsync(
                    originalManifest,
                    variant.FullPath,
                    contentMetadata,
                    variant.Game,
                    variant.LanguageCode,
                    cancellationToken);

                if (variantManifest != null)
                {
                    results.Add(variantManifest);
                }
            }

            return results;
        }

        // Fallback to building a single manifest from the root directory
        var manifest = await BuildManifestWithFilesAsync(
            originalManifest,
            extractedDirectory,
            contentMetadata,
            cancellationToken);

        if (manifest == null)
        {
            logger.LogWarning("Failed to build manifest for {Name}", originalManifest.Name);
            return [];
        }

        logger.LogInformation(
            "Created manifest {ManifestId} with {FileCount} files",
            manifest.Id,
            manifest.Files.Count);

        return [manifest];
    }

    /// <inheritdoc />
    public string GetManifestDirectory(ContentManifest manifest, string extractedDirectory)
    {
        // Get the content code to determine the correct subdirectory
        var contentCode = GetContentCodeFromManifest(manifest);

        // Check if there's a subdirectory matching the content code
        var contentSubdir = Path.Combine(extractedDirectory, contentCode);
        if (Directory.Exists(contentSubdir))
        {
            return contentSubdir;
        }

        // Check for common subdirectory patterns (CCG for Generals, ZH for Zero Hour)
        var ccgSubdir = Path.Combine(extractedDirectory, "CCG");
        var zhSubdir = Path.Combine(extractedDirectory, "ZH");

        if (manifest.TargetGame == GameType.Generals && Directory.Exists(ccgSubdir))
        {
            return ccgSubdir;
        }

        if (manifest.TargetGame == GameType.ZeroHour && Directory.Exists(zhSubdir))
        {
            return zhSubdir;
        }

        // Default to extracted directory
        return extractedDirectory;
    }

    private sealed record VariantInfo(string FullPath, GameType Game, string? LanguageCode);

    private static List<VariantInfo> DetectVariants(string extractedDirectory)
    {
        var variants = new List<VariantInfo>();

        // Check for ZH and CCG (Generals) root folders
        var gameDirs = new Dictionary<string, GameType>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZH"] = GameType.ZeroHour,
            ["CCG"] = GameType.Generals,
        };

        foreach (var (dirName, game) in gameDirs)
        {
            var gamePath = Path.Combine(extractedDirectory, dirName);
            if (!Directory.Exists(gamePath)) continue;

            // Check for language subdirectories (BIG EN, BIG DE, etc.)
            var langDirs = Directory.GetDirectories(gamePath, "BIG *");
            if (langDirs.Length > 0)
            {
                foreach (var langPath in langDirs)
                {
                    var langSuffix = Path.GetFileName(langPath)["BIG ".Length..].Trim().ToLowerInvariant();
                    var langCode = langSuffix switch
                    {
                        "en" => "en",
                        "de" => "de",
                        "ru" => "ru",
                        "fr" => "fr",
                        "es" => "es",
                        "it" => "it",
                        "pl" => "pl",
                        "zh" => "zh",
                        "ko" => "ko",
                        _ => langSuffix,
                    };

                    variants.Add(new VariantInfo(langPath, game, langCode));
                }
            }
            else
            {
                // If no language dirs, just add the game dir as a variant
                variants.Add(new VariantInfo(gamePath, game, null));
            }
        }

        return variants;
    }

    private async Task<ContentManifest?> BuildVariantManifestAsync(
        ContentManifest originalManifest,
        string variantDirectory,
        GenPatcherContentMetadata metadata,
        GameType game,
        string? languageCode,
        CancellationToken cancellationToken)
    {
        var manifest = await BuildManifestWithFilesAsync(originalManifest, variantDirectory, metadata, cancellationToken);
        if (manifest == null) return null;

        // Update manifest identification for the variant
        manifest.TargetGame = game;

        var nameSuffix = string.Empty;
        if (!string.IsNullOrEmpty(languageCode))
        {
            nameSuffix += $" ({languageCode.ToUpperInvariant()})";
            manifest.Metadata.Tags ??= [];
            if (!manifest.Metadata.Tags.Contains(languageCode))
            {
                manifest.Metadata.Tags.Add(languageCode);
            }
        }

        if (game == GameType.Generals)
        {
            nameSuffix += " [Generals]";
        }

        manifest.Name += nameSuffix;

        // Append to ID to make it unique
        var variantIdPart = $"-{game.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrEmpty(languageCode))
        {
            variantIdPart += $"-{languageCode}";
        }

        manifest.Id = $"{manifest.Id}{variantIdPart}";

        return manifest;
    }

    /// <summary>
    /// Builds a manifest with all files from the extracted directory.
    /// </summary>
    private async Task<ContentManifest?> BuildManifestWithFilesAsync(
        ContentManifest originalManifest,
        string extractedDirectory,
        GenPatcherContentMetadata contentMetadata,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get all files from extracted directory
            var allFiles = Directory.GetFiles(extractedDirectory, "*.*", SearchOption.AllDirectories);

            if (allFiles.Length == 0)
            {
                logger.LogWarning("No files found in extracted directory: {Directory}", extractedDirectory);
                return null;
            }

            logger.LogDebug("Found {FileCount} files in extracted directory", allFiles.Length);

            var fileEntries = new List<ManifestFile>();

            foreach (var fullPath in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(extractedDirectory, fullPath);
                var hash = await hashProvider.ComputeFileHashAsync(fullPath, cancellationToken);
                var fileSize = new FileInfo(fullPath).Length;
                var isExecutable = relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                // Determine install target for this file
                var fileInstallTarget = DetermineFileInstallTarget(
                    relativePath,
                    contentMetadata.InstallTarget);

                fileEntries.Add(new ManifestFile
                {
                    RelativePath = relativePath,
                    Hash = hash,
                    Size = fileSize,
                    IsExecutable = isExecutable,
                    SourceType = ContentSourceType.ExtractedPackage,
                    SourcePath = fullPath,
                    InstallTarget = fileInstallTarget,
                });

                logger.LogDebug(
                    "Added file: {Path} (Size: {Size} bytes, InstallTarget: {Target})",
                    relativePath,
                    fileSize,
                    fileInstallTarget);
            }

            // Create the manifest preserving original data but with updated files
            var manifest = new ContentManifest
            {
                Id = originalManifest.Id,
                Name = originalManifest.Name,
                Version = originalManifest.Version,
                ManifestVersion = originalManifest.ManifestVersion,
                ContentType = originalManifest.ContentType,
                TargetGame = originalManifest.TargetGame,
                Files = fileEntries,

                // Always use the dependency builder to ensure correct dependencies (e.g., GameInstallation for Community Patch)
                Dependencies = contentMetadata.GetDependencies(),
                InstallationInstructions = originalManifest.InstallationInstructions ?? new InstallationInstructions(),
                Publisher = originalManifest.Publisher,
                Metadata = new ContentMetadata
                {
                    Description = originalManifest.Metadata.Description,
                    ReleaseDate = originalManifest.Metadata.ReleaseDate,
                    IconUrl = CommunityOutpostConstants.LogoSource,
                    CoverUrl = CommunityOutpostConstants.CoverSource,
                    ThemeColor = CommunityOutpostConstants.ThemeColor,
                    ScreenshotUrls = originalManifest.Metadata.ScreenshotUrls,
                    Tags = originalManifest.Metadata.Tags,
                    ChangelogUrl = originalManifest.Metadata.ChangelogUrl,
                },
            };

            logger.LogInformation(
                "Built manifest {ManifestId} for {ContentType} '{Name}' with {FileCount} files and {DependencyCount} dependencies",
                manifest.Id,
                manifest.ContentType,
                manifest.Name,
                fileEntries.Count,
                manifest.Dependencies?.Count ?? 0);

            // Log each dependency for debugging
            if (manifest.Dependencies != null && manifest.Dependencies.Count > 0)
            {
                foreach (var dep in manifest.Dependencies)
                {
                    logger.LogDebug(
                        "  Dependency: {DepName} ({DepId}) - Type: {DepType}",
                        dep.Name,
                        dep.Id,
                        dep.DependencyType);
                }
            }
            else
            {
                logger.LogWarning("Manifest {ManifestId} has NO dependencies! Category: {Category}", manifest.Id, contentMetadata.Category);
            }

            return manifest;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build manifest for {Name}", originalManifest.Name);
            return null;
        }
    }
}
