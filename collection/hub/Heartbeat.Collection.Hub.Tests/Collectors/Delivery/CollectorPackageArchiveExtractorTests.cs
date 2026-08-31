using System.IO.Compression;
using System.Text;
using Heartbeat.Collection.CollectorRelease;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// Unpacking a downloaded Collector Package artifact. Length and SHA-256 only prove the bytes are
/// the ones the Registry named; they say nothing about what those bytes try to do to the filesystem.
/// So every hostile entry shape gets its own case here: traversal, absolute and rooted paths, drive
/// and UNC prefixes, backslash and percent-encoded separators, symlinks and other non-regular
/// entries, case-colliding duplicates, and archives that are simply too big.
///
/// A refused or interrupted extraction must never leave a completion marker behind, which is what
/// keeps a half-written directory from being mistaken for a Collector Installation.
/// </summary>
public sealed class CollectorPackageArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-extract-{Guid.NewGuid():N}");

    public CollectorPackageArchiveExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Destination => Path.Combine(_root, "content");

    private CollectorRegistryResult<CollectorPackageArchiveContent> Extract(
        byte[] archive,
        CollectorPackageArchiveLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(archive, writable: false);
        return CollectorPackageArchiveExtractor.Extract(stream, Destination, limits, cancellationToken);
    }

    private sealed record Entry(string Name, byte[] Content, int? ExternalAttributes = null);

    private static byte[] Zip(params Entry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var created = archive.CreateEntry(entry.Name, CompressionLevel.NoCompression);
                if (entry.ExternalAttributes is { } attributes)
                    created.ExternalAttributes = attributes;
                using var stream = created.Open();
                stream.Write(entry.Content);
            }
        }
        return buffer.ToArray();
    }

    private static Entry Text(string name, string content = "payload", int? externalAttributes = null) =>
        new(name, Encoding.UTF8.GetBytes(content), externalAttributes);

    private static int UnixAttributes(int mode) => mode << 16;

    private void AssertNothingAdmissible()
    {
        var marker = Path.Combine(Destination, CollectorInstallationMarker.FileName);
        Assert.False(File.Exists(marker), "A refused extraction must not leave a completion marker.");
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "heartbeat-extract-escape.txt")));
    }

    [Fact]
    public void Extract_RealVRChatPackage_ProducesALoadablePackage()
    {
        var archive = CollectorPackageArchive.Pack(VRChatSamplePackage.PackageDirectory);

        var result = Extract(archive);

        Assert.True(result.IsSuccess, result.Detail);
        var content = result.Require();
        Assert.Equal(Path.GetFullPath(Destination), content.Directory);
        Assert.True(content.FileCount > 1);
        Assert.True(content.UncompressedBytes > 0);
        var package = LocalCollectorPackage.Load(Destination);
        Assert.Equal("heartbeat.collector.vrchat", package.Manifest.PackageId);
    }

    [Fact]
    public void Extract_RealVRChatPackage_KeepsTheEntrypointExecutable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var archive = CollectorPackageArchive.Pack(VRChatSamplePackage.PackageDirectory);

        Assert.True(Extract(archive).IsSuccess);

        var package = LocalCollectorPackage.Load(Destination);
        var entrypoint = Path.Combine(Destination, package.Artifacts[0].Entrypoint);
        Assert.True(File.GetUnixFileMode(entrypoint).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Extract_DirectoryEntries_AreSkippedInsteadOfRefused()
    {
        var result = Extract(Zip(
            new Entry("nested/", [], UnixAttributes(0x41ED)),
            Text("nested/file.txt")));

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(1, result.Require().FileCount);
        Assert.True(File.Exists(Path.Combine(Destination, "nested", "file.txt")));
    }

    [Fact]
    public void Extract_ParentTraversalEntry_IsRefused()
    {
        var result = Extract(Zip(Text("../escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_TraversalAfterANestedSegment_IsRefused()
    {
        var result = Extract(Zip(Text("nested/../../escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_AbsolutePathEntry_IsRefused()
    {
        var result = Extract(Zip(Text("/escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_WindowsDrivePathEntry_IsRefused()
    {
        var result = Extract(Zip(Text("C:\\escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_UncPathEntry_IsRefused()
    {
        var result = Extract(Zip(Text("//host/share/escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_BackslashSeparatorEntry_IsRefused()
    {
        // A backslash is a plain character on Unix but a separator on Windows, so the same archive
        // would mean two different things. It is never a legitimate Collector Package entry name.
        var result = Extract(Zip(Text("nested\\..\\escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_PercentEncodedTraversalEntry_IsRefused()
    {
        // Nothing in this path decodes entry names, so this cannot escape today. It is refused
        // anyway: a Collector Package has no reason to carry an encoded separator, and accepting it
        // would leave the safety of the extraction dependent on nobody adding a decoder later.
        var result = Extract(Zip(Text("%2e%2e/escape.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_TrailingDotSegmentEntry_IsRefused()
    {
        var result = Extract(Zip(Text("nested./file.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_CurrentDirectorySegmentEntry_IsRefused()
    {
        var result = Extract(Zip(Text("./file.txt")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_SymbolicLinkEntry_IsRefused()
    {
        var result = Extract(Zip(new Entry(
            "link",
            Encoding.UTF8.GetBytes("/etc/passwd"),
            UnixAttributes(0xA1FF))));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        Assert.False(Path.Exists(Path.Combine(Destination, "link")));
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_NonRegularUnixEntry_IsRefused()
    {
        var result = Extract(Zip(new Entry("fifo", [], UnixAttributes(0x11B6))));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_WindowsReparsePointEntry_IsRefused()
    {
        var result = Extract(Zip(new Entry("reparse.txt", Encoding.UTF8.GetBytes("x"), 0x400)));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_SetuidBits_AreNotPreserved()
    {
        if (OperatingSystem.IsWindows())
            return;

        var archive = Zip(new Entry("tool", Encoding.UTF8.GetBytes("x"), UnixAttributes(0x8FED)));

        Assert.True(Extract(archive).IsSuccess);

        var mode = File.GetUnixFileMode(Path.Combine(Destination, "tool"));
        Assert.False(mode.HasFlag(UnixFileMode.SetUser));
        Assert.False(mode.HasFlag(UnixFileMode.SetGroup));
        Assert.False(mode.HasFlag(UnixFileMode.StickyBit));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Extract_DuplicateEntryNamesDifferingOnlyInCase_IsRefused()
    {
        // On a case-insensitive filesystem the second entry would silently overwrite the first, so
        // the verified content would depend on which machine unpacked it.
        var result = Extract(Zip(Text("File.txt", "first"), Text("file.txt", "second")));

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
    }

    [Fact]
    public void Extract_MoreEntriesThanTheLimit_IsRefused()
    {
        var entries = Enumerable.Range(0, 5).Select(index => Text($"file-{index}.txt")).ToArray();

        var result = Extract(Zip(entries), new CollectorPackageArchiveLimits { MaxEntryCount = 4 });

        Assert.Equal(CollectorRegistryFailureReason.ArchiveLimitExceeded, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_MoreUncompressedBytesThanTheLimit_IsRefused()
    {
        var bomb = Zip(new Entry("big.bin", new byte[64 * 1024]));

        var result = Extract(bomb, new CollectorPackageArchiveLimits { MaxUncompressedBytes = 1024 });

        Assert.Equal(CollectorRegistryFailureReason.ArchiveLimitExceeded, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_DefaultLimits_AcceptTheRealVRChatPackage()
    {
        // The shipped defaults have to leave room for the artifact the release pipeline produces,
        // otherwise the bomb guard would fail closed on the only Package the MVP delivers.
        var archive = CollectorPackageArchive.Pack(VRChatSamplePackage.PackageDirectory);
        var limits = CollectorPackageArchiveLimits.Default;

        var result = Extract(archive);

        Assert.True(result.IsSuccess, result.Detail);
        Assert.True(result.Require().FileCount < limits.MaxEntryCount);
        Assert.True(result.Require().UncompressedBytes < limits.MaxUncompressedBytes);
    }

    [Fact]
    public void Extract_NonArchiveBytes_FailsWithMalformedArchive()
    {
        var result = Extract(Encoding.ASCII.GetBytes("PK\u0003\u0004 not really an archive"));

        Assert.Equal(CollectorRegistryFailureReason.MalformedArchive, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_TruncatedArchive_FailsWithMalformedArchive()
    {
        var archive = CollectorPackageArchive.Pack(VRChatSamplePackage.PackageDirectory);

        var result = Extract(archive[..(archive.Length / 2)]);

        Assert.Equal(CollectorRegistryFailureReason.MalformedArchive, result.Reason);
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_FailingPartWayThroughTheEntries_LeavesNoMarker()
    {
        // The second entry needs a directory where the first entry already wrote a file, so the
        // extraction dies after having produced real files.
        var result = Extract(Zip(Text("clash", "file"), Text("clash/inner.txt")));

        Assert.Equal(CollectorRegistryFailureReason.InstallationStorageFailed, result.Reason);
        Assert.True(File.Exists(Path.Combine(Destination, "clash")));
        AssertNothingAdmissible();
    }

    [Fact]
    public void Extract_CancelledBeforeItStarts_FailsWithCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = Extract(
            CollectorPackageArchive.Pack(VRChatSamplePackage.PackageDirectory),
            limits: null,
            cancellation.Token);

        Assert.Equal(CollectorRegistryFailureReason.Cancelled, result.Reason);
        AssertNothingAdmissible();
    }
}
