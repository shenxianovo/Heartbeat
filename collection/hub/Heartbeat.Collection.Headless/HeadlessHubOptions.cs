using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Headless;

public sealed class HeadlessHubOptions
{
    public required string ApiKey { get; init; }
    public required string DataDirectory { get; init; }
    public required string PackageDirectory { get; init; }
    public required Guid SubjectId { get; init; }
    public SubjectKind SubjectKind { get; init; } = SubjectKind.Account;
    public string SubjectName { get; init; } = "Managed account";
    public int UploadIntervalSeconds { get; init; } = 60;
    public int ConfigVersion { get; init; } = 1;
    public JsonElement Config { get; init; } = JsonDocument.Parse("{}").RootElement.Clone();
    public int StartupTimeoutSeconds { get; init; } = 30;
    public int DrainGraceSeconds { get; init; } = 10;

    public string RuntimeStatePath => Path.Combine(DataDirectory, "collector-runtime.json");

    public static HeadlessHubOptions Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootNode = JsonNode.Parse(File.ReadAllBytes(fullPath)) as JsonObject
                       ?? throw new JsonException("Headless Hub configuration must be a JSON object.");
        if (rootNode.TryGetPropertyValue("configSchemaVersion", out var legacyVersion))
        {
            if (rootNode.ContainsKey("configVersion"))
                throw new JsonException(
                    "Headless Hub configuration contains both configSchemaVersion and configVersion.");
            rootNode.Remove("configSchemaVersion");
            rootNode["configVersion"] = legacyVersion?.DeepClone();
        }
        var options = rootNode.Deserialize<HeadlessHubOptions>(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new JsonException("Headless Hub configuration is null.");
        var root = Path.GetDirectoryName(fullPath)!;
        return options.WithPaths(
            Resolve(root, options.DataDirectory),
            Resolve(root, options.PackageDirectory));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidOperationException("dataDirectory is required.");
        if (string.IsNullOrWhiteSpace(PackageDirectory))
            throw new InvalidOperationException("packageDirectory is required.");
        if (SubjectId == Guid.Empty)
            throw new InvalidOperationException("subjectId must not be empty.");
        if (!Enum.IsDefined(SubjectKind))
            throw new InvalidOperationException("subjectKind is invalid.");
        if (string.IsNullOrWhiteSpace(SubjectName))
            throw new InvalidOperationException("subjectName is required.");
        if (UploadIntervalSeconds <= 0 || ConfigVersion <= 0 ||
            StartupTimeoutSeconds <= 0 || DrainGraceSeconds <= 0)
            throw new InvalidOperationException("Headless Hub numeric settings must be positive.");
        if (Config.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("config must contain a JSON value.");
    }

    private HeadlessHubOptions WithPaths(string dataDirectory, string packageDirectory) => new()
    {
        ApiKey = ApiKey,
        DataDirectory = dataDirectory,
        PackageDirectory = packageDirectory,
        SubjectId = SubjectId,
        SubjectKind = SubjectKind,
        SubjectName = SubjectName,
        UploadIntervalSeconds = UploadIntervalSeconds,
        ConfigVersion = ConfigVersion,
        Config = Config.Clone(),
        StartupTimeoutSeconds = StartupTimeoutSeconds,
        DrainGraceSeconds = DrainGraceSeconds
    };

    private static string Resolve(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
