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
public class AvifToTgaConverter
{
    private readonly ILogger<AvifToTgaConverter> _logger;
    private readonly Configuration _imageSharpConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="AvifToTgaConverter"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public AvifToTgaConverter(ILogger<AvifToTgaConverter> logger)
    {
        _logger = logger;

        // Configure ImageSharp to support AVIF decoding
        _imageSharpConfig = new Configuration(new AvifConfigurationModule());
    }

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

        var avifFiles = Directory.GetFiles(directory, "*.avif", SearchOption.AllDirectories);
        _logger.LogInformation("Found {Count} AVIF files to convert in {Directory}", avifFiles.Length, directory);

        int converted = 0;

        foreach (var avifFile in avifFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var tgaFile = Path.ChangeExtension(avifFile, ".tga");
                await ConvertFileAsync(avifFile, tgaFile, cancellationToken);

                // Delete the original AVIF file
                File.Delete(avifFile);
                converted++;

                _logger.LogDebug("Converted {AvifFile} to {TgaFile}", avifFile, tgaFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert {AvifFile}", avifFile);
            }
        }

        _logger.LogInformation("Successfully converted {Converted} of {Total} AVIF files to TGA", converted, avifFiles.Length);
        return converted;
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
                using var inputStream = File.OpenRead(sourcePath);

                var decoderOptions = new DecoderOptions
                {
                    Configuration = _imageSharpConfig,
                };

                using var image = Image.Load(decoderOptions, inputStream);

                // Create directory for output if it doesn't exist
                var destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

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
