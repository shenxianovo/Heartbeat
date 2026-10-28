using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orisumi.Core;

namespace Orisumi.Observer.Runtime;

internal sealed record PackageManifest(int Format, string Id, string Name, string Version, string Target, int Protocol, string Entry);
internal sealed record CatalogPackage(string Id, string Name, string Description, string Version, string Target, string Url, string Sha256, int Protocol = 1, int Format = 1);
internal sealed record InstalledObserver(string PackageId, string Name, string Version, Guid InstanceId, bool Installed, bool Enabled, JsonElement Settings);

internal sealed partial class PackageStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly FileStream _lock;
    private readonly string _registry;
    private readonly string? _bundled;
    private readonly Uri? _catalog;
    private readonly HttpClient _http;
    private readonly Dictionary<string, InstalledObserver> _observers;
    public string Root { get; }
    public static string Target => RuntimeInformation.RuntimeIdentifier;
    public IReadOnlyList<InstalledObserver> Observers => _observers.Values.ToArray();

    public PackageStore(string root, string? bundled, Uri? catalog, HttpClient http)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _lock = new FileStream(Path.Combine(Root, "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _registry = Path.Combine(Root, "registry.json");
            _bundled = bundled;
            _catalog = catalog;
            _http = http;
            if (catalog is not null && catalog.Scheme != "https") throw new ArgumentException("The catalog requires HTTPS.");
            _observers = (File.Exists(_registry) ? JsonSerializer.Deserialize<InstalledObserver[]>(File.ReadAllText(_registry), Json)! : [])
                .ToDictionary(item => item.PackageId, StringComparer.Ordinal);
            // A process interruption before registry replacement leaves only an unused staged package.
            var staging = Path.Combine(Root, "staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
        }
        catch { _lock.Dispose(); throw; }
    }

    public InstalledObserver Find(string packageId) => _observers.GetValueOrDefault(packageId)
        ?? throw new KeyNotFoundException("Observer is not installed.");
    public string InstanceDirectory(Guid instance) => Path.Combine(Root, "instances", instance.ToString());
    public string PackageDirectory(string id, string version) => Path.Combine(Root, "packages", id, version);
    public PackageManifest Manifest(InstalledObserver item) => ReadManifest(File.ReadAllText(Path.Combine(PackageDirectory(item.PackageId, item.Version), "observer.json")));

    public void Save(InstalledObserver observer)
    {
        var next = new Dictionary<string, InstalledObserver>(_observers, StringComparer.Ordinal) { [observer.PackageId] = observer };
        var temporary = _registry + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, next.Values.ToArray(), Json);
            file.Flush(true);
        }
        File.Move(temporary, _registry, true);
        _observers[observer.PackageId] = observer;
    }

    public IReadOnlyList<CatalogPackage> Bundled()
    {
        if (_bundled is null || !Directory.Exists(_bundled)) return [];
        return Directory.EnumerateFiles(_bundled, "*.zip").Select(path =>
        {
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader((zip.GetEntry("observer.json") ?? throw new InvalidDataException("Missing observer.json.")).Open());
            var manifest = ReadManifest(reader.ReadToEnd());
            using var stream = File.OpenRead(path);
            return new CatalogPackage(manifest.Id, manifest.Name, "", manifest.Version, manifest.Target, new Uri(Path.GetFullPath(path)).AbsoluteUri,
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), manifest.Protocol, manifest.Format);
        }).Where(package => package.Target == Target).ToArray();
    }

    public async Task<IReadOnlyList<CatalogPackage>> CatalogAsync(CancellationToken token)
    {
        var packages = Bundled().ToList();
        if (_catalog is not null)
        {
            using var response = await _http.GetAsync(_catalog, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var remote = json.RootElement.GetProperty("packages").Deserialize<CatalogPackage[]>(Json)!;
            foreach (var item in remote)
            {
                ValidateIdentity(item.Id, item.Version);
                if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var url) || url.Scheme != "https") throw new InvalidDataException("Package downloads require HTTPS.");
                if (!DigestPattern().IsMatch(item.Sha256)) throw new InvalidDataException("Invalid SHA-256 digest.");
            }
            packages.AddRange(remote.Where(package => package.Target == Target));
        }
        return packages.GroupBy(package => package.Id, StringComparer.Ordinal)
            .Select(group => group.MaxBy(package => Version.Parse(package.Version))!).ToArray();
    }

    public async Task<PackageManifest> PrepareAsync(CatalogPackage package, CancellationToken token)
    {
        ValidateIdentity(package.Id, package.Version);
        var stage = Path.Combine(Root, "staging", Guid.NewGuid().ToString());
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, "package.zip");
            await using (var file = File.Create(archive))
            {
                if (new Uri(package.Url).IsFile)
                {
                    await using var source = File.OpenRead(new Uri(package.Url).LocalPath);
                    await source.CopyToAsync(file, token);
                }
                else
                {
                    using var response = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        if (file.Length + read > 256L * 1024 * 1024) throw new InvalidDataException("Package exceeds 256 MiB.");
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
            }
            await using (var file = File.OpenRead(archive))
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, token)), package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package checksum does not match the catalog.");
            var unpacked = Path.Combine(stage, "unpacked");
            Directory.CreateDirectory(unpacked);
            using (var zip = ZipFile.OpenRead(archive))
            {
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    total += entry.Length;
                    if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Unpacked package exceeds 1 GiB.");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Package links are not supported.");
                    var destination = ChildPath(unpacked, entry.FullName.TrimEnd('/'));
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination);
                    if (!OperatingSystem.IsWindows())
                    {
                        var permissions = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF);
                        File.SetUnixFileMode(destination, permissions == 0 ? UnixFileMode.UserRead | UnixFileMode.UserWrite : permissions);
                    }
                }
            }
            var manifest = ReadManifest(await File.ReadAllTextAsync(Path.Combine(unpacked, "observer.json"), token));
            if (manifest.Id != package.Id || manifest.Version != package.Version || manifest.Target != Target)
                throw new InvalidDataException("Package identity does not match the catalog or this device.");
            if (manifest.Format != 1 || manifest.Protocol != 1) throw new InvalidDataException("Update the Client to use this package protocol.");
            var executable = ChildPath(unpacked, manifest.Entry);
            if (!File.Exists(executable)) throw new InvalidDataException("The package entry does not exist.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            var destinationDirectory = PackageDirectory(manifest.Id, manifest.Version);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);
            var digestPath = Path.Combine(destinationDirectory, ".package-sha256");
            if (Directory.Exists(destinationDirectory))
            {
                if (!File.Exists(digestPath) || !string.Equals(await File.ReadAllTextAsync(digestPath, token), package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("This package version already has different contents.");
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(unpacked, ".package-sha256"), package.Sha256, token);
                Directory.Move(unpacked, destinationDirectory);
            }
            return manifest;
        }
        finally { Directory.Delete(stage, true); }
    }

    public InstalledObserver Register(PackageManifest manifest, bool enabled)
    {
        var observer = _observers.TryGetValue(manifest.Id, out var existing)
            ? existing with { Name = manifest.Name, Version = manifest.Version, Installed = true, Enabled = enabled }
            : new(manifest.Id, manifest.Name, manifest.Version, EntityId.New().Value, true, enabled, JsonSerializer.SerializeToElement(new { }));
        Save(observer);
        return observer;
    }
    internal static PackageManifest ReadManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, Json) ?? throw new InvalidDataException("Invalid package manifest.");
        ValidateIdentity(manifest.Id, manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Target)) throw new InvalidDataException("Package name and target are required.");
        return manifest;
    }
    private static void ValidateIdentity(string id, string version)
    {
        if (id is null || !IdPattern().IsMatch(id) || version is null || !VersionPattern().IsMatch(version) || !Version.TryParse(version, out _))
            throw new InvalidDataException("Invalid package id or version.");
    }
    internal static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\\')) throw new InvalidDataException("Expected a relative package path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Package path leaves its directory.");
        return path;
    }
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")] private static partial Regex VersionPattern();
    [GeneratedRegex("^[a-fA-F0-9]{64}$")] private static partial Regex DigestPattern();
    public void Dispose() => _lock.Dispose();
}
