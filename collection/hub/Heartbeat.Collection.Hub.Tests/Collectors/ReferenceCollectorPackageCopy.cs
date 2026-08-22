using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Heartbeat.Collection.Hub.Tests.Collectors;

internal sealed class ReferenceCollectorPackageCopy : IDisposable
{
    private ReferenceCollectorPackageCopy(string path) => Path = path;

    public string Path { get; }

    public static ReferenceCollectorPackageCopy Create(string source)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"heartbeat-reference-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(path, System.IO.Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, System.IO.Path.Combine(path, System.IO.Path.GetRelativePath(source, file)));
        return new ReferenceCollectorPackageCopy(path);
    }

    public JsonObject ReadManifest() => JsonNode.Parse(
        File.ReadAllText(System.IO.Path.Combine(Path, "collector-manifest.json")))!.AsObject();

    public void WriteManifest(JsonObject manifest) => File.WriteAllText(
        System.IO.Path.Combine(Path, "collector-manifest.json"),
        manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public void UpdateSchemaHash(string schemaPath)
    {
        var manifest = ReadManifest();
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(schemaPath)));
        manifest["outputs"]![0]!["schema"]!["hash"] = hash;
        WriteManifest(manifest);
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
