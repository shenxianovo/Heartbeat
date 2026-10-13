using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Heartbeat.Observers.Sources.Local;

/// <summary>One explicitly selected Linux path; no recursive scan or implicit symlink traversal.</summary>
public sealed class FileSource : IRecordSource
{
    public string Name => "file";
    private static readonly SourceSchema Schema = new(Guid.Parse("019a1280-0000-7000-8000-000000000020"), "file-source-snapshots", "Observed Linux filesystem node",
        SourceFields.WithIdentity(new { path = SourceFields.String(), device = SourceFields.String(), inode = SourceFields.String(), linkCount = SourceFields.Integer(),
            birthTimeText = SourceFields.String(true), modifiedTimeText = SourceFields.String(), identityEvidence = SourceFields.String(),
            isSymbolicLink = SourceFields.Boolean(), byteLength = SourceFields.Integer(), sha256 = SourceFields.String(true), contentBase64 = SourceFields.String(true),
            linkTargetBase64 = SourceFields.String(true) }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The file source currently requires Linux GNU stat.");
        var path = Path.GetFullPath(context.Input);
        var before = await StatAsync(path, cancellationToken);
        var mode = int.Parse(before[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0xf000;
        if (mode is not (0x8000 or 0xa000)) throw new InvalidDataException("Only regular files and symbolic links are captured; devices and pipes are rejected.");
        var link = mode == 0xa000;
        byte[]? bytes = null;
        byte[]? target = null;
        if (link)
        {
            target = await SourceProcess.RunAsync("readlink", ["--zero", "--", path], cancellationToken);
            if (target.Length == 0 || target[^1] != 0) throw new InvalidDataException("Unexpected readlink output.");
            target = target[..^1];
        }
        else bytes = await SourceJson.ReadBytesAsync(path, cancellationToken);
        var after = await StatAsync(path, cancellationToken);
        if (!before.SequenceEqual(after)) throw new InvalidDataException("File metadata changed during capture; no mixed snapshot was accepted.");
        var birth = before[6] == "-" ? null : before[6];
        var key = birth is null ? "capture:" + context.CaptureKey + ":" + path : before[0] + ":" + before[1] + ":" + birth;
        yield return new("linux-filesystem-node", key, context.CaptureKey + ":" + path, Schema,
            SourceFields.Of(new { path, device = before[0], inode = before[1], linkCount = long.Parse(before[2], CultureInfo.InvariantCulture),
                birthTimeText = birth, modifiedTimeText = before[5], identityEvidence = birth is null ? "capture-only; birth time unavailable" : "device/inode/birth-time",
                isSymbolicLink = link, byteLength = long.Parse(before[3], CultureInfo.InvariantCulture),
                sha256 = bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes)), contentBase64 = bytes is null ? null : Convert.ToBase64String(bytes),
                linkTargetBase64 = target is null ? null : Convert.ToBase64String(target) }),
            new Dictionary<string, SourceReference>(), context.CapturedAt, context.CapturedAt);
    }

    private static async Task<string[]> StatAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await SourceProcess.RunAsync("stat", ["--printf=%d\\n%i\\n%h\\n%s\\n%Y\\n%y\\n%w\\n%f", "--", path], cancellationToken);
        var parts = SourceProcess.StrictText(bytes).Split('\n');
        if (parts.Length != 8) throw new InvalidDataException("Unexpected GNU stat record.");
        return parts;
    }
}
