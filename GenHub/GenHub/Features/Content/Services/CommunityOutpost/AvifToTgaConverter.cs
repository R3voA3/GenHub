// Copyright (c) GenHub Contributors. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HeyRed.ImageSharp.Heif.Formats.Avif;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tga;

namespace GenHub.Features.Content.Services.CommunityOutpost;

/// <summary>
/// Converts AVIF images to TGA format for use with Command &amp; Conquer Generals/Zero Hour.
/// The game requires TGA textures, but GenPatcher dat archives contain AVIF files for compression.
/// </summary>
public class AvifToTgaConverter(ILogger<AvifToTgaConverter> logger)
{
    private readonly ILogger<AvifToTgaConverter> _logger = logger;

    // Configure ImageSharp to support AVIF decoding
    private readonly Configuration _imageSharpConfig = new(new AvifConfigurationModule());

    /// <summary>
    /// Converts all AVIF files in a directory (and subdirectories) to TGA format.
    /// The original AVIF files are replaced with TGA files using the same base filename.
    /// </summary>
    /// <param name="directory">The directory containing AVIF files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of files converted.</returns>
    public async Task<int> ConvertDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Directory does not exist: {Directory}", directory);
            return 0;
        }

        try
        {
            var avifFiles = Directory.EnumerateFiles(directory, "*.avif", SearchOption.AllDirectories);
            int converted = 0;
            int totalFound = 0;

            foreach (var avifFile in avifFiles)
            {
                totalFound++;
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var tgaFile = Path.ChangeExtension(avifFile, ".tga");
                    await ConvertFileAsync(avifFile, tgaFile, cancellationToken);

                    // Delete the original AVIF file only if TGA exists and has content
                    var tgaInfo = new FileInfo(tgaFile);
                    if (tgaInfo.Exists && tgaInfo.Length > 0)
                    {
                        File.Delete(avifFile);
                        converted++;
                        _logger.LogDebug("Converted {AvifFile} to {TgaFile}", avifFile, tgaFile);
                    }
                    else
                    {
                        _logger.LogWarning("Conversion produced no output for {AvifFile}", avifFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to convert {AvifFile}", avifFile);
                }
            }

            _logger.LogInformation("Successfully converted {Converted} of {Total} AVIF files to TGA in {Directory}", converted, totalFound, directory);
            return converted;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied to directory or subdirectories: {Directory}", directory);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate files in directory: {Directory}", directory);
            return 0;
        }
    }

    /// <summary>
    /// Converts a single AVIF file to TGA format.
    /// </summary>
    /// <param name="sourcePath">The path to the AVIF file.</param>
    /// <param name="destinationPath">The path for the output TGA file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ConvertFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var inputStream = File.OpenRead(sourcePath);

                var decoderOptions = new DecoderOptions
                {
                    Configuration = _imageSharpConfig,
                };

                cancellationToken.ThrowIfCancellationRequested();
                using var image = Image.Load(decoderOptions, inputStream);

                // Create directory for output if it doesn't exist
                var destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Save as TGA with appropriate settings for Generals
                // The game expects 32-bit BGRA TGA files with RLE compression
                // GenPatcher uses RLE compression (TGA type 10) for smaller file sizes
                var encoder = new TgaEncoder
                {
                    BitsPerPixel = TgaBitsPerPixel.Pixel32,
                    Compression = TgaCompression.RunLength,
                };

                image.SaveAsTga(destinationPath, encoder);
            },
            cancellationToken);
    }
}
