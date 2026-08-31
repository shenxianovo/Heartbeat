using System.IO.Compression;
using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.CollectorRelease;

/// <summary>
/// Packs and unpacks a Collector Package directory as a zip.
///
/// The archive is byte-deterministic for identical inputs — entries in ordinal name order, a fixed
/// timestamp, fixed compression — so re-running the same release is idempotent and a differing hash
/// means the content really changed. Unix permission bits are carried so the published artifact
/// stays runnable.
///
/// Unpacking exists only to let the release tool re-open what it just wrote, and it delegates to the
/// Runtime's <see cref="CollectorPackageArchiveExtractor" /> so archive safety has one implementation
/// rather than a publisher-side copy that could drift. The Runtime's Collector Installation path (its
/// own version directory, completion marker and cleanup) is a separate concern and is not here.
/// </summary>
public static class CollectorPackageArchive
{
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Pack(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory);
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: RelativeName(root, path)))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, name) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTimestamp;
                if (!OperatingSystem.IsWindows())
                    entry.ExternalAttributes = (int)File.GetUnixFileMode(path) << 16;
                using var source = File.OpenRead(path);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Unpacks into <paramref name="destinationDirectory" />, refusing any entry that would write
    /// outside it or is otherwise not a portable relative file.
    /// </summary>
    public static void Unpack(byte[] archiveBytes, string destinationDirectory)
    {
        using var buffer = new MemoryStream(archiveBytes, writable: false);
        var result = CollectorPackageArchiveExtractor.Extract(buffer, destinationDirectory);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Collector Package archive was refused ({result.Reason}): {result.Detail}");
    }

    private static string RelativeName(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
