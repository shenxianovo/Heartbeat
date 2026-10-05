using System.Security.AccessControl;
using System.Security.Principal;

namespace Heartbeat.Dev;

/// <summary>Stages private dotenv values next to the destination, replacing it only after all checks pass.</summary>
internal sealed class SetupConfiguration : IDisposable
{
    private readonly string _path;
    private readonly string? _original;
    private readonly DotenvFile _saved;
    private readonly Dictionary<string, string> _updates = new(StringComparer.OrdinalIgnoreCase);
    public string StagingPath { get; }
    public bool Exists => _original is not null;

    public SetupConfiguration(string path)
    {
        EnsureRegularFile(path);
        _path = path;
        _original = File.Exists(path) ? File.ReadAllText(path) : null;
        _saved = _original is null ? new DotenvFile(new Dictionary<string, string>()) : DotenvFile.Read(path);
        StagingPath = path + $".setup.{Guid.NewGuid():N}";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(StagingPath, options);
        try
        {
            if (OperatingSystem.IsWindows()) RestrictWindowsAccess(StagingPath);
        }
        catch
        {
            stream.Dispose();
            File.Delete(StagingPath);
            throw;
        }
    }

    public string? Get(string key)
    {
        var value = _updates.GetValueOrDefault(key) ?? _saved.GetSaved(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void Set(string key, string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new InvalidOperationException("Configuration values must fit on one line.");
        _updates[key] = value;
    }

    public void SaveStaging()
    {
        var lines = (_original ?? "").Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.RemoveAll(line =>
        {
            var separator = line.IndexOf('=');
            return separator > 0 && _updates.ContainsKey(line[..separator].Trim());
        });
        lines.AddRange(_updates.Select(pair => $"{pair.Key}={Encode(pair.Value)}"));
        File.WriteAllLines(StagingPath, lines);
    }

    private static string Encode(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("$", "$$", StringComparison.Ordinal) + "\"";

    public void Commit()
    {
        EnsureRegularFile(_path);
        var current = File.Exists(_path) ? File.ReadAllText(_path) : null;
        if (current != _original)
            throw new InvalidOperationException(".env.local changed during setup; it was preserved. Run env setup again.");
        SaveStaging();
        File.Move(StagingPath, _path, overwrite: true);
    }

    private static void EnsureRegularFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || Directory.Exists(path))
            throw new InvalidOperationException(".env.local must be a regular file, not a symbolic link or directory.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictWindowsAccess(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Cannot identify the current Windows account.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public void Dispose() => File.Delete(StagingPath);
}
