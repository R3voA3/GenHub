using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GenHub.Features.Content.Services.CommunityOutpost;

/// <summary>
/// Packs files into a .big archive format (Generals/Zero Hour).
/// </summary>
public static class BigFilePacker
{
    private const string Signature = "BIGF";

    /// <summary>
    /// Packs the contents of a directory into a .big file.
    /// </summary>
    /// <param name="sourceDirectory">The directory containing files to pack.</param>
    /// <param name="destinationPath">The output .big file path.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task PackAsync(string sourceDirectory, string destinationPath)
    {
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        var entries = new List<BigFileEntry>();

        // Calculate header size
        // Header: Signature (4) + TotalSize (4) + NumFiles (4) + HeaderSize (4) = 16 bytes
        long headerSize = 16;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('/', '\\');
            relativePath = NormalizeBigPath(relativePath);
            var encoding = Encoding.ASCII;
            var nameBytes = encoding.GetBytes(relativePath);

            // Entry: Offset (4) + Size (4) + Name (n) + Null Terminator (1)
            headerSize += 4 + 4 + nameBytes.Length + 1;

            entries.Add(new BigFileEntry
            {
                FullPath = file,
                RelativePath = relativePath,
                Size = new FileInfo(file).Length,
            });
        }

        // Calculate total size
        long totalSize = headerSize + entries.Sum(e => e.Size);

        using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs);

        // Write Header
        writer.Write(Encoding.ASCII.GetBytes(Signature));
        WriteUInt32BigEndian(writer, (uint)totalSize);
        WriteUInt32BigEndian(writer, (uint)entries.Count);
        WriteUInt32BigEndian(writer, (uint)headerSize);

        // Calculate initial offset
        long currentOffset = headerSize;

        // Write Index
        foreach (var entry in entries)
        {
            WriteUInt32BigEndian(writer, (uint)currentOffset);
            WriteUInt32BigEndian(writer, (uint)entry.Size);
            writer.Write(Encoding.ASCII.GetBytes(entry.RelativePath));
            writer.Write((byte)0); // Null terminator

            currentOffset += entry.Size;
        }

        // Write Data
        foreach (var entry in entries)
        {
            using var fileStream = File.OpenRead(entry.FullPath);
            await fileStream.CopyToAsync(fs);
        }
    }

    /// <summary>
    /// Writes a 32-bit unsigned integer in big-endian format.
    /// </summary>
    /// <param name="writer">The binary writer.</param>
    /// <param name="value">The value to write.</param>
    private static void WriteUInt32BigEndian(BinaryWriter writer, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        writer.Write(bytes);
    }

    private static string NormalizeBigPath(string relativePath)
    {
        var path = relativePath.TrimStart('.', '\\').Replace('/', '\\');

        if (path.StartsWith("ZH\\BIG", StringComparison.OrdinalIgnoreCase))
        {
            path = path[6..].TrimStart('\\', ' ');
        }
        else if (path.StartsWith("CCG\\BIG", StringComparison.OrdinalIgnoreCase))
        {
            path = path[7..].TrimStart('\\', ' ');
        }
        else if (path.StartsWith("BIG", StringComparison.OrdinalIgnoreCase))
        {
            path = path[3..].TrimStart('\\', ' ');
        }

        // If the path still contains extra leading folders, cut to known game roots
        var roots = new[]
        {
            "Data\\",
            "Art\\",
            "Audio\\",
            "W3D\\",
            "Textures\\",
            "Shaders\\",
            "Maps\\",
            "INI\\",
        };

        var bestIndex = -1;
        foreach (var root in roots)
        {
            var idx = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (bestIndex < 0 || idx < bestIndex))
            {
                bestIndex = idx;
            }
        }

        return bestIndex > 0 ? path[bestIndex..] : path;
    }

    private class BigFileEntry
    {
        public string FullPath { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public long Size { get; set; }
    }
}