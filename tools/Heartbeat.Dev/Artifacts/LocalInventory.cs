namespace Heartbeat.Dev;

internal sealed record LocalInventoryEntry(
    string Name,
    long Bytes,
    int FileCount,
    DateTimeOffset? LastWrite,
    IReadOnlyList<string> Scripts);

internal sealed record LocalInventoryReport(
    string Root,
    long Bytes,
    int FileCount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LocalInventoryEntry> Entries);

internal static class LocalInventory
{
    public static LocalInventoryReport Create(string root)
    {
        if (!Directory.Exists(root)) return new(root, 0, 0, DateTimeOffset.UtcNow, []);
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Select(Inspect)
            .OrderByDescending(item => item.Bytes)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        return new LocalInventoryReport(
            root,
            entries.Sum(entry => entry.Bytes),
            entries.Sum(entry => entry.FileCount),
            DateTimeOffset.UtcNow,
            entries);
    }

    private static LocalInventoryEntry Inspect(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray()
            : [path];
        var details = files.Select(file => new FileInfo(file)).ToArray();
        var scripts = details
            .Where(file => file.Extension is ".sh" or ".ps1" or ".py" or ".mjs" or ".js")
            .Select(file => Path.GetRelativePath(path, file.FullName).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .Take(100)
            .ToArray();
        return new LocalInventoryEntry(
            Path.GetFileName(path),
            details.Sum(file => file.Length),
            details.Length,
            details.Length == 0 ? null : details.Max(file => new DateTimeOffset(file.LastWriteTimeUtc)),
            scripts);
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.##} {suffixes[suffix]}";
    }
}
