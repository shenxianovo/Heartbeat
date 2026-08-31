using System.IO.Compression;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// Bounds on what a downloaded Collector Package artifact may unpack into. The defaults leave ample
/// room for the framework-dependent VRChat artifact the release pipeline produces while keeping a
/// hostile or accidental archive from filling the disk.
/// </summary>
public sealed record CollectorPackageArchiveLimits
{
    public static readonly CollectorPackageArchiveLimits Default = new();

    /// <summary>Maximum number of archive entries, directory entries included.</summary>
    public int MaxEntryCount { get; init; } = 4096;

    /// <summary>Maximum total uncompressed bytes the archive may produce.</summary>
    public long MaxUncompressedBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>What an accepted extraction produced.</summary>
public sealed record CollectorPackageArchiveContent(string Directory, int FileCount, long UncompressedBytes);

/// <summary>
/// Unpacks a downloaded Collector Package artifact into a destination directory, refusing every
/// entry that is not a plain relative file below it.
///
/// The declared length and SHA-256 prove the bytes are the ones the Registry named; they say nothing
/// about what those bytes ask the filesystem to do. This is where that is decided, and it is the only
/// place in the repository that decides it: traversal, rooted, drive-qualified and UNC names,
/// backslash and percent-encoded separator variants, symbolic links and other non-regular entries,
/// and case-colliding duplicates are all refused, and the canonical destination path is re-checked
/// against the destination root for every entry that survives the name rules.
///
/// A refused or interrupted extraction leaves whatever it had already written in place. That is safe
/// because it never writes a completion marker, so the directory is not a Collector Installation and
/// the next attempt at the same candidate rebuilds it.
/// </summary>
public static class CollectorPackageArchiveExtractor
{
    private static readonly string[] EncodedSeparatorVariants = ["%2e", "%2f", "%5c"];

    public static CollectorRegistryResult<CollectorPackageArchiveContent> Extract(
        Stream archive,
        string destinationDirectory,
        CollectorPackageArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var bounds = limits ?? CollectorPackageArchiveLimits.Default;
        if (bounds.MaxEntryCount <= 0 || bounds.MaxUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Collector Package archive limits must be positive.");

        var root = Path.GetFullPath(destinationDirectory);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var written = 0L;
        var files = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.Entries.Count > bounds.MaxEntryCount)
                return Fail(
                    CollectorRegistryFailureReason.ArchiveLimitExceeded,
                    $"Archive declares {zip.Entries.Count} entries, more than the {bounds.MaxEntryCount} allowed.");
            var declared = zip.Entries.Sum(entry => entry.Length);
            if (declared > bounds.MaxUncompressedBytes)
                return Fail(
                    CollectorRegistryFailureReason.ArchiveLimitExceeded,
                    $"Archive declares {declared} uncompressed bytes, more than the {bounds.MaxUncompressedBytes} allowed.");

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPortableRelativeName(entry.FullName, out var isDirectoryEntry))
                    return Fail(
                        CollectorRegistryFailureReason.UnsafeArchiveEntry,
                        $"Archive entry '{entry.FullName}' is not a portable relative path.");
                if (isDirectoryEntry)
                    continue;
                if (!IsRegularFileEntry(entry))
                    return Fail(
                        CollectorRegistryFailureReason.UnsafeArchiveEntry,
                        $"Archive entry '{entry.FullName}' is a symbolic link or another non-regular entry.");
                if (!taken.Add(entry.FullName))
                    return Fail(
                        CollectorRegistryFailureReason.UnsafeArchiveEntry,
                        $"Archive repeats entry '{entry.FullName}'; the extracted content would depend on the filesystem.");

                var target = Path.GetFullPath(Path.Combine(root, Path.Combine(entry.FullName.Split('/'))));
                if (!target.StartsWith(rootPrefix, StringComparison.Ordinal))
                    return Fail(
                        CollectorRegistryFailureReason.UnsafeArchiveEntry,
                        $"Archive entry '{entry.FullName}' resolves to '{target}', outside '{rootPrefix}'.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var source = entry.Open())
                using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = source.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                            break;
                        written += read;
                        if (written > bounds.MaxUncompressedBytes)
                            return Fail(
                                CollectorRegistryFailureReason.ArchiveLimitExceeded,
                                $"Archive produced more than the {bounds.MaxUncompressedBytes} bytes allowed.");
                        file.Write(buffer, 0, read);
                    }
                }
                files++;
                ApplyPermissions(entry, target);
            }
        }
        catch (InvalidDataException exception)
        {
            return Fail(CollectorRegistryFailureReason.MalformedArchive, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(CollectorRegistryFailureReason.Cancelled, $"Unpacking into '{root}' was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             NotSupportedException or PathTooLongException)
        {
            return Fail(CollectorRegistryFailureReason.InstallationStorageFailed, exception.Message);
        }

        return CollectorRegistryResult<CollectorPackageArchiveContent>.Success(
            new CollectorPackageArchiveContent(root, files, written));
    }

    /// <summary>
    /// Accepts only <c>a/b/c.ext</c> shaped names. Zip stores names with <c>/</c> separators, so a
    /// backslash, a drive letter, a leading separator or a dot segment is either an attack or a
    /// non-portable archive; both are refused rather than normalized. Percent-encoded separators are
    /// refused too: nothing here decodes names, and accepting them would leave this boundary
    /// depending on no decoder ever being introduced upstream.
    /// </summary>
    private static bool IsPortableRelativeName(string name, out bool isDirectoryEntry)
    {
        isDirectoryEntry = name.EndsWith('/');
        var candidate = isDirectoryEntry ? name[..^1] : name;
        if (candidate.Length == 0 ||
            candidate.Contains('\\', StringComparison.Ordinal) ||
            candidate.Contains(':', StringComparison.Ordinal) ||
            candidate.Any(char.IsControl) ||
            Path.IsPathRooted(candidate) ||
            EncodedSeparatorVariants.Any(variant =>
                candidate.Contains(variant, StringComparison.OrdinalIgnoreCase)))
            return false;

        foreach (var segment in candidate.Split('/'))
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                segment.StartsWith(' '))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Refuses anything that is not a regular file. Unix producers put the file type in the high half
    /// of <see cref="ZipArchiveEntry.ExternalAttributes" />, so a symlink, fifo, socket or device
    /// entry is visible there; Windows producers put DOS attributes in the low half, where a reparse
    /// point is visible instead.
    /// </summary>
    private static bool IsRegularFileEntry(ZipArchiveEntry entry)
    {
        const int reparsePoint = 0x400;
        const int regularFile = 0x8000;
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType is not (0 or regularFile))
            return false;
        return (entry.ExternalAttributes & reparsePoint) == 0;
    }

    /// <summary>
    /// Carries the Unix permission bits so a published apphost stays executable, masked to the nine
    /// permission bits: an archive must not be able to hand out setuid, setgid or sticky.
    /// </summary>
    private static void ApplyPermissions(ZipArchiveEntry entry, string target)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF);
        if (mode == UnixFileMode.None)
            return;
        File.SetUnixFileMode(target, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static CollectorRegistryResult<CollectorPackageArchiveContent> Fail(
        CollectorRegistryFailureReason reason,
        string detail) =>
        CollectorRegistryResult<CollectorPackageArchiveContent>.Failure(reason, detail);
}
