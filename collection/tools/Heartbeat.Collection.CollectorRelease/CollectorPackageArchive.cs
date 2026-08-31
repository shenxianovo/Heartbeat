using System.IO.Compression;

namespace Heartbeat.Collection.CollectorRelease;

/// <summary>
/// Packs and unpacks a Collector Package directory as a zip.
///
/// The archive is byte-deterministic for identical inputs — entries in ordinal name order, a fixed
/// timestamp, fixed compression — so re-running the same release is idempotent and a differing hash
/// means the content really changed. Unix permission bits are carried so the published artifact
/// stays runnable.
///
/// The extractor here exists only to let the release tool re-open what it just wrote. The Runtime's
/// Collector Installation path (its own version directory, completion marker and cleanup) is a
/// separate concern and is not implemented here.
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
    /// outside it.
    /// </summary>
    public static void Unpack(byte[] archiveBytes, string destinationDirectory)
    {
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var buffer = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/'))
                continue;
            if (Path.IsPathRooted(entry.FullName) ||
                entry.FullName.Contains('\\') ||
                entry.FullName.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' is not a portable relative path.");

            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(entry.FullName.Split('/'))));
            if (!target.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' escapes the destination directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var source = entry.Open())
            using (var file = File.Create(target))
                source.CopyTo(file);

            var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0xFFF);
            if (!OperatingSystem.IsWindows() && mode != UnixFileMode.None)
                File.SetUnixFileMode(target, mode);
        }
    }

    private static string RelativeName(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
