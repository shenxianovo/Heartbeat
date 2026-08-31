using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Heartbeat.Collection.Hub.Collectors.Packages;

public enum FactKind
{
    Segment,
    Event,
    Measurement
}

public enum FactEvolutionMode
{
    SegmentSnapshot,
    ImmutableEvent,
    MutableEvent,
    MeasurementCorrection
}

public sealed record CollectorPackageManifest(
    int ManifestVersion,
    string PackageId,
    string Version,
    IReadOnlyList<int> ProtocolMajors,
    IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedCapabilities,
    CollectorConfigManifest Config,
    IReadOnlyList<CollectorOutputTemplate> Outputs,
    IReadOnlyList<CollectorArtifactManifest> Artifacts);

public sealed record CollectorConfigManifest(
    int Version,
    IReadOnlyList<int> AcceptedVersions);

public sealed record CollectorOutputTemplate(
    string OutputId,
    string Source,
    FactKind FactKind,
    FactSchemaReference Schema,
    IReadOnlyList<string> SubjectKinds,
    IReadOnlyList<string> DimensionKeys);

public sealed record FactSchemaReference(
    string Id,
    int Major,
    int Revision,
    string Document,
    string Hash);

public sealed record CollectorArtifactManifest(
    string ArtifactId,
    string Driver,
    IReadOnlyList<string> OperatingSystems,
    IReadOnlyList<string> Architectures,
    string Entrypoint,
    long Size,
    string ContentHash);

public sealed record VerifiedObservationDeclaration(
    string Source,
    int Version,
    string Json,
    string ContentHash);

public sealed class VerifiedCollectorArtifact
{
    private readonly ImmutableArray<byte> _content;

    internal VerifiedCollectorArtifact(
        string artifactId,
        string entrypoint,
        string contentHash,
        ImmutableArray<byte> content)
    {
        ArtifactId = artifactId;
        Entrypoint = entrypoint;
        ContentHash = contentHash;
        _content = content;
    }

    public string ArtifactId { get; }
    public string Entrypoint { get; }
    public string ContentHash { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
}

public sealed class FactSchemaDocument
{
    private readonly ImmutableArray<byte> _content;

    internal FactSchemaDocument(
        string schemaId,
        int schemaMajor,
        int schemaRevision,
        FactKind factKind,
        FactEvolutionMode evolutionMode,
        bool allowRetraction,
        IReadOnlyList<string> mutablePayloadPaths,
        JsonElement payloadSchema,
        JsonSchema payloadValidator,
        string contentHash,
        ImmutableArray<byte> content)
    {
        SchemaId = schemaId;
        SchemaMajor = schemaMajor;
        SchemaRevision = schemaRevision;
        FactKind = factKind;
        EvolutionMode = evolutionMode;
        AllowRetraction = allowRetraction;
        MutablePayloadPaths = mutablePayloadPaths;
        PayloadSchema = payloadSchema;
        PayloadValidator = payloadValidator;
        ContentHash = contentHash;
        _content = content;
    }

    public string SchemaId { get; }
    public int SchemaMajor { get; }
    public int SchemaRevision { get; }
    public FactKind FactKind { get; }
    public FactEvolutionMode EvolutionMode { get; }
    public bool AllowRetraction { get; }
    public IReadOnlyList<string> MutablePayloadPaths { get; }
    public JsonElement PayloadSchema { get; }
    public string ContentHash { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
    internal JsonSchema PayloadValidator { get; }

    public bool IsPayloadValid(JsonElement payload)
    {
        try
        {
            return PayloadValidator.Evaluate(payload).IsValid;
        }
        catch (Exception exception) when (exception is
            RefResolutionException or JsonSchemaException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed class PackageValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Reads a local Collector Package into an immutable, verified memory snapshot. No caller can
/// observe later changes to the package directory through the returned value.
/// </summary>
public sealed class LocalCollectorPackage
{
    private sealed record ObservationDeclarationReference(string Document, string Hash);

    private const string ManifestFileName = "collector-manifest.json";
    private static readonly Regex PackageIdPattern = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SemVerPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DimensionKeyPattern = new(
        "^[a-z][a-zA-Z0-9]*$",
        RegexOptions.CultureInvariant);
    private static readonly ImmutableHashSet<string> SupportedSubjectKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal, "machine", "account", "person");

    private LocalCollectorPackage(
        string packageDirectory,
        CollectorPackageManifest manifest,
        IReadOnlyList<VerifiedCollectorArtifact> artifacts,
        IReadOnlyList<FactSchemaDocument> factSchemas,
        VerifiedObservationDeclaration? observationDeclaration,
        string packageContentHash)
    {
        PackageDirectory = packageDirectory;
        Manifest = manifest;
        Artifacts = artifacts.ToImmutableArray();
        FactSchemas = factSchemas.ToImmutableArray();
        ObservationDeclaration = observationDeclaration;
        PackageContentHash = packageContentHash;
    }

    public CollectorPackageManifest Manifest { get; }
    public string PackageDirectory { get; }
    public IReadOnlyList<VerifiedCollectorArtifact> Artifacts { get; }
    public IReadOnlyList<FactSchemaDocument> FactSchemas { get; }
    public VerifiedObservationDeclaration? ObservationDeclaration { get; }
    /// <summary>
    /// SHA-256 of the exact UTF-8 Manifest bytes. Because the Manifest fixes every Artifact and
    /// Fact Schema hash, this fingerprints the verified local package; it is not a trust proof.
    /// </summary>
    public string PackageContentHash { get; }

    public static LocalCollectorPackage Load(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);

        var root = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(root))
            throw new PackageValidationException($"Collector Package directory does not exist: {root}");

        var manifestPath = Path.Combine(root, ManifestFileName);
        RejectSymbolicLinks(root, [ManifestFileName], "Collector Package manifest");
        var manifestBytes = ReadStrictUtf8File(manifestPath, "Collector Package manifest");
        var manifest = ParseManifest(manifestBytes);
        var declarationReference = ReadObservationDeclarationReference(manifestBytes);

        var artifacts = manifest.Artifacts
            .Select(artifact => VerifyArtifact(root, artifact))
            .ToArray();
        var schemas = manifest.Outputs
            .Select(output => VerifyFactSchema(root, output))
            .GroupBy(schema => (schema.SchemaId, schema.SchemaMajor, schema.SchemaRevision))
            .Select(group => group.First())
            .ToArray();
        var declaration = declarationReference is null
            ? null
            : VerifyObservationDeclaration(root, declarationReference, manifest);

        return new LocalCollectorPackage(
            root,
            manifest,
            artifacts,
            schemas,
            declaration,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
    }

    private static CollectorPackageManifest ParseManifest(byte[] bytes)
    {
        using var document = ParseJson(bytes, "Collector Package manifest");
        var root = document.RootElement;
        RequireObject(
            root,
            "Collector Package manifest",
            ["manifestVersion", "packageId", "version", "protocolMajors", "supportedCapabilities", "config", "outputs", "artifacts", "observationDeclaration"],
            ["manifestVersion", "packageId", "version", "protocolMajors", "supportedCapabilities", "config", "outputs", "artifacts"]);

        var manifestVersion = ReadPositiveInt(root, "manifestVersion", "Collector Package manifest");
        if (manifestVersion != 1)
            throw new PackageValidationException($"Unsupported manifestVersion {manifestVersion}.");

        var packageId = ReadNonEmptyString(root, "packageId", "Collector Package manifest");
        if (!PackageIdPattern.IsMatch(packageId))
            throw new PackageValidationException($"Invalid packageId '{packageId}'.");

        var version = ReadNonEmptyString(root, "version", "Collector Package manifest");
        if (!IsValidSemVer(version))
            throw new PackageValidationException($"Invalid Collector Package SemVer '{version}'.");

        var protocolMajors = ReadPositiveIntArray(root, "protocolMajors", "Collector Package manifest");
        var capabilities = ReadCapabilities(root.GetProperty("supportedCapabilities"));
        var config = ReadConfig(root);
        var outputs = ReadOutputs(root.GetProperty("outputs"));
        var artifacts = ReadArtifacts(root.GetProperty("artifacts"));

        var ambiguousSchema = outputs
            .GroupBy(output => (output.Schema.Id, output.Schema.Major, output.Schema.Revision))
            .FirstOrDefault(group => group
                .Select(output => (output.Schema.Document, output.Schema.Hash))
                .Distinct()
                .Skip(1)
                .Any());
        if (ambiguousSchema is not null)
            throw new PackageValidationException(
                $"Fact schema identity '{ambiguousSchema.Key.Id}/{ambiguousSchema.Key.Major}/{ambiguousSchema.Key.Revision}' " +
                "must resolve to exactly one document and content hash.");

        if (!capabilities.TryGetValue("diagnostics.stream-gap", out var gapVersions) || !gapVersions.Contains(1))
            throw new PackageValidationException(
                "A v1 Collector Package with Fact outputs must declare diagnostics.stream-gap version 1.");

        foreach (var output in outputs)
        {
            var requiredCapability = output.FactKind switch
            {
                FactKind.Segment => "facts.segment",
                FactKind.Event => "facts.event",
                FactKind.Measurement => "facts.measurement.gauge",
                _ => throw new InvalidOperationException($"Unsupported FactKind '{output.FactKind}'.")
            };
            if (!capabilities.ContainsKey(requiredCapability))
                throw new PackageValidationException(
                    $"Output '{output.OutputId}' requires capability '{requiredCapability}'.");
        }

        return new CollectorPackageManifest(
            manifestVersion,
            packageId,
            version,
            protocolMajors,
            capabilities,
            config,
            outputs,
            artifacts);
    }

    private static CollectorConfigManifest ReadConfig(JsonElement root)
    {
        var config = root.GetProperty("config");
        RequireObject(config, "Collector Package config", ["version", "accepts"], ["version", "accepts"]);
        var version = ReadPositiveInt(config, "version", "Collector Package config");
        var acceptedVersions = ReadPositiveIntArray(config, "accepts", "Collector Package config");
        if (!acceptedVersions.Contains(version))
            throw new PackageValidationException(
                "Collector Package config.accepts must include its current config.version.");
        return new CollectorConfigManifest(version, acceptedVersions);
    }

    private static ObservationDeclarationReference? ReadObservationDeclarationReference(byte[] manifestBytes)
    {
        using var document = ParseJson(manifestBytes, "Collector Package manifest");
        if (!document.RootElement.TryGetProperty("observationDeclaration", out var element))
            return null;
        RequireObject(
            element,
            "observationDeclaration",
            ["document", "hash"],
            ["document", "hash"]);
        return new ObservationDeclarationReference(
            ReadNonEmptyString(element, "document", "observationDeclaration"),
            ReadSha256(element, "hash", "observationDeclaration"));
    }

    private static VerifiedObservationDeclaration VerifyObservationDeclaration(
        string root,
        ObservationDeclarationReference reference,
        CollectorPackageManifest manifest)
    {
        var path = ResolvePackageFile(root, reference.Document, "Observation Depth declaration");
        var bytes = ReadStrictUtf8File(path, "Observation Depth declaration");
        VerifyHash(bytes, reference.Hash, "Observation Depth declaration");
        using var document = ParseJson(bytes, "Observation Depth declaration");
        var declaration = document.RootElement;
        RejectDuplicateObjectKeys(declaration, "Observation Depth declaration");
        RequireObject(
            declaration,
            "Observation Depth declaration",
            ["source", "version", "collectorVersion", "layers"],
            ["source", "version", "layers"]);
        var source = ReadNonEmptyString(declaration, "source", "Observation Depth declaration");
        if (!manifest.Outputs.Any(output => output.Source == source))
            throw new PackageValidationException(
                $"Observation Depth declaration source '{source}' is not produced by this Package.");
        var version = ReadPositiveInt(declaration, "version", "Observation Depth declaration");
        if (declaration.GetProperty("layers").ValueKind != JsonValueKind.Array ||
            declaration.GetProperty("layers").GetArrayLength() == 0)
            throw new PackageValidationException("Observation Depth declaration layers must be a non-empty array.");
        return new VerifiedObservationDeclaration(
            source,
            version,
            Encoding.UTF8.GetString(bytes),
            reference.Hash);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ReadCapabilities(JsonElement element)
    {
        RequireObject(element, "supportedCapabilities", null, []);
        var capabilities = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !capabilities.TryAdd(
                    property.Name,
                    ReadPositiveIntArray(property.Value, $"supportedCapabilities.{property.Name}")))
                throw new PackageValidationException(
                    $"supportedCapabilities contains an invalid or duplicate capability '{property.Name}'.");
        }
        return capabilities.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static IReadOnlyList<CollectorOutputTemplate> ReadOutputs(JsonElement element)
    {
        RequireNonEmptyArray(element, "outputs");
        var outputs = new List<CollectorOutputTemplate>();
        var outputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(
                item,
                "output",
                ["outputId", "source", "factKind", "schema", "subjectKinds", "dimensionKeys"],
                ["outputId", "source", "factKind", "schema", "subjectKinds", "dimensionKeys"]);
            var outputId = ReadNonEmptyString(item, "outputId", "output");
            if (!outputIds.Add(outputId))
                throw new PackageValidationException($"Duplicate outputId '{outputId}'.");

            var source = ReadNonEmptyString(item, "source", $"output '{outputId}'");
            var factKind = ParseFactKind(ReadNonEmptyString(item, "factKind", $"output '{outputId}'"));
            if (factKind == FactKind.Measurement)
                throw new PackageValidationException(
                    $"Output '{outputId}' uses FactKind '{factKind}', but this runtime slice does not yet support Measurement outputs.");
            var schema = ReadSchemaReference(item.GetProperty("schema"), outputId);
            var subjectKinds = ReadStringArray(item, "subjectKinds", $"output '{outputId}'");
            if (subjectKinds.Distinct(StringComparer.Ordinal).Count() != subjectKinds.Count ||
                subjectKinds.Any(kind => !SupportedSubjectKinds.Contains(kind)))
                throw new PackageValidationException(
                    $"Output '{outputId}' subjectKinds must be unique v1 kinds: machine, account, or person.");
            var dimensionKeys = ReadStringArray(item, "dimensionKeys", $"output '{outputId}'", allowEmpty: true);
            if (dimensionKeys.Any(key => !DimensionKeyPattern.IsMatch(key)))
                throw new PackageValidationException($"Output '{outputId}' has an invalid dimension key.");

            outputs.Add(new CollectorOutputTemplate(
                outputId,
                source,
                factKind,
                schema,
                subjectKinds,
                dimensionKeys));
        }
        return outputs.ToImmutableArray();
    }

    private static FactSchemaReference ReadSchemaReference(JsonElement element, string outputId)
    {
        RequireObject(
            element,
            $"output '{outputId}' schema",
            ["id", "major", "revision", "document", "hash"],
            ["id", "major", "revision", "document", "hash"]);
        return new FactSchemaReference(
            ReadNonEmptyString(element, "id", $"output '{outputId}' schema"),
            ReadPositiveInt(element, "major", $"output '{outputId}' schema"),
            ReadPositiveInt(element, "revision", $"output '{outputId}' schema"),
            ReadNonEmptyString(element, "document", $"output '{outputId}' schema"),
            ReadSha256(element, "hash", $"output '{outputId}' schema"));
    }

    private static IReadOnlyList<CollectorArtifactManifest> ReadArtifacts(JsonElement element)
    {
        RequireNonEmptyArray(element, "artifacts");
        var artifacts = new List<CollectorArtifactManifest>();
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(
                item,
                "artifact",
                ["artifactId", "selector", "entrypoint", "size", "contentHash"],
                ["artifactId", "selector", "entrypoint", "size", "contentHash"]);
            var artifactId = ReadNonEmptyString(item, "artifactId", "artifact");
            if (!artifactIds.Add(artifactId))
                throw new PackageValidationException($"Duplicate artifactId '{artifactId}'.");

            var selector = item.GetProperty("selector");
            RequireObject(selector, $"artifact '{artifactId}' selector", ["driver", "os", "arch"], ["driver", "os", "arch"]);
            var size = ReadNonNegativeLong(item, "size", $"artifact '{artifactId}'");
            artifacts.Add(new CollectorArtifactManifest(
                artifactId,
                ReadNonEmptyString(selector, "driver", $"artifact '{artifactId}' selector"),
                ReadStringArray(selector, "os", $"artifact '{artifactId}' selector"),
                ReadStringArray(selector, "arch", $"artifact '{artifactId}' selector"),
                ReadNonEmptyString(item, "entrypoint", $"artifact '{artifactId}'"),
                size,
                ReadSha256(item, "contentHash", $"artifact '{artifactId}'")));
        }
        return artifacts.ToImmutableArray();
    }

    private static VerifiedCollectorArtifact VerifyArtifact(string root, CollectorArtifactManifest artifact)
    {
        var path = ResolvePackageFile(root, artifact.Entrypoint, $"artifact '{artifact.ArtifactId}'");
        var content = ReadFile(path, $"artifact '{artifact.ArtifactId}'");
        if (content.LongLength != artifact.Size)
            throw new PackageValidationException(
                $"Artifact '{artifact.ArtifactId}' size is {content.LongLength}, expected {artifact.Size}.");
        VerifyHash(content, artifact.ContentHash, $"artifact '{artifact.ArtifactId}'");
        return new VerifiedCollectorArtifact(
            artifact.ArtifactId,
            artifact.Entrypoint,
            artifact.ContentHash,
            ImmutableArray.CreateRange(content));
    }

    private static FactSchemaDocument VerifyFactSchema(string root, CollectorOutputTemplate output)
    {
        var reference = output.Schema;
        var path = ResolvePackageFile(root, reference.Document, $"schema '{reference.Id}'");
        var bytes = ReadStrictUtf8File(path, $"Fact Schema Document '{reference.Id}'");
        VerifyHash(bytes, reference.Hash, $"Fact Schema Document '{reference.Id}'");
        return ParseFactSchema(bytes, reference, output.FactKind);
    }

    internal static FactSchemaDocument RestoreFactSchema(
        ReadOnlyMemory<byte> content,
        string schemaId,
        int schemaMajor,
        int schemaRevision,
        FactKind factKind,
        string contentHash)
    {
        var bytes = content.ToArray();
        var description = $"Durable Fact Schema Document '{schemaId}/{schemaMajor}/{schemaRevision}'";
        ValidateStrictUtf8(bytes, description);
        VerifyHash(bytes, contentHash, description);
        return ParseFactSchema(
            bytes,
            new FactSchemaReference(
                schemaId,
                schemaMajor,
                schemaRevision,
                "durable-schema-snapshot.json",
                contentHash),
            factKind);
    }

    private static FactSchemaDocument ParseFactSchema(
        byte[] bytes,
        FactSchemaReference reference,
        FactKind expectedFactKind)
    {
        using var document = ParseJson(bytes, $"Fact Schema Document '{reference.Id}'");
        var schema = document.RootElement;
        RejectDuplicateObjectKeys(schema, $"Fact Schema Document '{reference.Id}'");
        RequireObject(
            schema,
            $"Fact Schema Document '{reference.Id}'",
            ["documentVersion", "schemaId", "schemaMajor", "schemaRevision", "factKind", "evolution", "payloadSchemaDialect", "payloadSchema"],
            ["documentVersion", "schemaId", "schemaMajor", "schemaRevision", "factKind", "evolution", "payloadSchemaDialect", "payloadSchema"]);

        var dialect = ReadNonEmptyString(schema, "payloadSchemaDialect", $"Fact Schema Document '{reference.Id}'");
        if (dialect != "https://json-schema.org/draft/2020-12/schema")
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' must use JSON Schema Draft 2020-12.");
        if (ReadPositiveInt(schema, "documentVersion", $"Fact Schema Document '{reference.Id}'") != 1)
            throw new PackageValidationException($"Fact Schema Document '{reference.Id}' has an unsupported documentVersion.");

        var schemaId = ReadNonEmptyString(schema, "schemaId", $"Fact Schema Document '{reference.Id}'");
        var major = ReadPositiveInt(schema, "schemaMajor", $"Fact Schema Document '{reference.Id}'");
        var revision = ReadPositiveInt(schema, "schemaRevision", $"Fact Schema Document '{reference.Id}'");
        var factKind = ParseFactKind(ReadNonEmptyString(schema, "factKind", $"Fact Schema Document '{reference.Id}'"));
        if (schemaId != reference.Id || major != reference.Major || revision != reference.Revision || factKind != expectedFactKind)
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' identity or FactKind does not match its Manifest reference.");

        var evolution = schema.GetProperty("evolution");
        RequireObject(
            evolution,
            $"Fact Schema Document '{reference.Id}' evolution",
            ["mode", "allowRetraction", "mutablePayloadPaths"],
            ["mode", "allowRetraction"]);
        var mode = ParseEvolutionMode(ReadNonEmptyString(evolution, "mode", $"Fact Schema Document '{reference.Id}' evolution"));
        if (!EvolutionMatches(factKind, mode))
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' uses evolution mode '{mode}' for FactKind '{factKind}'.");
        if (!evolution.GetProperty("allowRetraction").TryGetBoolean(out var allowRetraction))
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' evolution.allowRetraction must be boolean.");
        if (factKind == FactKind.Segment && !allowRetraction)
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' must allow Segment retraction in v1.");

        IReadOnlyList<string> mutablePayloadPaths = [];
        if (mode == FactEvolutionMode.MutableEvent)
        {
            if (!evolution.TryGetProperty("mutablePayloadPaths", out _))
                throw new PackageValidationException(
                    $"Fact Schema Document '{reference.Id}' mutableEvent evolution requires mutablePayloadPaths.");
            mutablePayloadPaths = ReadStringArray(
                evolution,
                "mutablePayloadPaths",
                $"Fact Schema Document '{reference.Id}' evolution");
            if (mutablePayloadPaths.Any(path => !IsJsonPointer(path)))
                throw new PackageValidationException(
                    $"Fact Schema Document '{reference.Id}' mutablePayloadPaths must contain JSON Pointers.");
        }
        else if (evolution.TryGetProperty("mutablePayloadPaths", out _))
        {
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' only permits mutablePayloadPaths for mutableEvent evolution.");
        }

        var payloadSchema = schema.GetProperty("payloadSchema");
        if (payloadSchema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' payloadSchema must be a JSON Schema object or boolean.");

        ValidateSchemaReferences(payloadSchema, payloadSchema, reference.Id, dialect);
        JsonSchema payloadValidator;
        try
        {
            payloadValidator = JsonSchema.FromText(
                payloadSchema.GetRawText(),
                new BuildOptions { Dialect = Dialect.Draft202012 });
        }
        catch (Exception exception) when (exception is
            JsonException or JsonSchemaException or InvalidOperationException or ArgumentException)
        {
            throw new PackageValidationException(
                $"Fact Schema Document '{reference.Id}' payloadSchema is not valid JSON Schema Draft 2020-12.",
                exception);
        }

        return new FactSchemaDocument(
            schemaId,
            major,
            revision,
            factKind,
            mode,
            allowRetraction,
            mutablePayloadPaths,
            payloadSchema.Clone(),
            payloadValidator,
            reference.Hash,
            ImmutableArray.CreateRange(bytes));
    }

    private static void ValidateSchemaReferences(
        JsonElement resourceRoot,
        JsonElement schema,
        string schemaId,
        string dialect)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("$id", out _))
            resourceRoot = schema;

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "$ref" or "$dynamicRef")
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new PackageValidationException(
                        $"Fact Schema Document '{schemaId}' payloadSchema {property.Name} must be a string.");
                var reference = property.Value.GetString()!;
                if (!reference.StartsWith('#'))
                    throw new PackageValidationException(
                        $"Fact Schema Document '{schemaId}' payloadSchema must be self-contained; external references are not allowed.");
                if (!LocalSchemaReferenceResolves(resourceRoot, reference))
                    throw new PackageValidationException(
                        $"Fact Schema Document '{schemaId}' payloadSchema local reference '{reference}' cannot be resolved.");
            }
            if (property.Name == "$schema" &&
                (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != dialect))
                throw new PackageValidationException(
                    $"Fact Schema Document '{schemaId}' payloadSchema $schema must match payloadSchemaDialect.");
        }

        foreach (var subschema in EnumerateSubschemas(schema))
            ValidateSchemaReferences(resourceRoot, subschema, schemaId, dialect);
    }

    private static IEnumerable<JsonElement> EnumerateSubschemas(JsonElement schema)
    {
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is
                "additionalItems" or
                "additionalProperties" or
                "contains" or
                "contentSchema" or
                "else" or
                "if" or
                "items" or
                "not" or
                "propertyNames" or
                "then" or
                "unevaluatedItems" or
                "unevaluatedProperties")
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
                    yield return property.Value;
                continue;
            }

            if (property.Name is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in property.Value.EnumerateArray())
                        yield return child;
                }
                continue;
            }

            if (property.Name is "$defs" or "definitions" or "dependentSchemas" or "patternProperties" or "properties")
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in property.Value.EnumerateObject())
                        yield return child.Value;
                }
                continue;
            }

            if (property.Name == "dependencies" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in property.Value.EnumerateObject())
                {
                    if (child.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
                        yield return child.Value;
                }
            }
        }
    }

    private static bool LocalSchemaReferenceResolves(JsonElement root, string reference)
    {
        string fragment;
        try
        {
            fragment = Uri.UnescapeDataString(reference[1..]);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (fragment.Length == 0)
            return true;
        if (fragment[0] != '/')
            return ContainsSchemaAnchor(root, fragment);

        var current = root;
        foreach (var encodedToken in fragment[1..].Split('/'))
        {
            if (!TryDecodeJsonPointerToken(encodedToken, out var token))
                return false;
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out current))
                    return false;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.GetArrayLength())
                    return false;
                current = current[index];
            }
            else
            {
                return false;
            }
        }
        return current.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;
    }

    private static bool ContainsSchemaAnchor(JsonElement schema, string anchor)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "$anchor" or "$dynamicAnchor" &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() == anchor)
                return true;
        }

        foreach (var subschema in EnumerateSubschemas(schema))
        {
            if (subschema.ValueKind == JsonValueKind.Object && subschema.TryGetProperty("$id", out _))
                continue;
            if (ContainsSchemaAnchor(subschema, anchor))
                return true;
        }
        return false;
    }

    private static bool TryDecodeJsonPointerToken(string encoded, out string token)
    {
        var builder = new StringBuilder(encoded.Length);
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != '~')
            {
                builder.Append(encoded[index]);
                continue;
            }
            if (++index >= encoded.Length || encoded[index] is not ('0' or '1'))
            {
                token = string.Empty;
                return false;
            }
            builder.Append(encoded[index] == '0' ? '~' : '/');
        }
        token = builder.ToString();
        return true;
    }

    private static void RejectDuplicateObjectKeys(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new PackageValidationException(
                        $"{context} contains duplicate field '{property.Name}'.");
                RejectDuplicateObjectKeys(property.Value, context);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateObjectKeys(item, context);
        }
    }

    /// <summary>
    /// Package identity string validity lives here, next to the Manifest that owns identity, so the
    /// Registry index reader validates PackageId and Version against the same rule instead of
    /// growing a second authority.
    /// </summary>
    internal static bool IsValidPackageId(string value) => PackageIdPattern.IsMatch(value);

    internal static bool IsValidSemVer(string value)
    {
        if (!SemVerPattern.IsMatch(value))
            return false;
        var prereleaseStart = value.IndexOf('-');
        if (prereleaseStart < 0)
            return true;
        var buildStart = value.IndexOf('+', prereleaseStart + 1);
        var prerelease = buildStart < 0
            ? value[(prereleaseStart + 1)..]
            : value[(prereleaseStart + 1)..buildStart];
        return prerelease.Split('.').All(identifier =>
            identifier.Length == 1 || identifier[0] != '0' ||
            identifier.Any(character => !char.IsAsciiDigit(character)));
    }

    private static bool IsJsonPointer(string value)
    {
        if (!value.StartsWith('/'))
            return false;
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] != '~')
                continue;
            if (++index >= value.Length || value[index] is not ('0' or '1'))
                return false;
        }
        return true;
    }

    private static bool EvolutionMatches(FactKind factKind, FactEvolutionMode mode) => factKind switch
    {
        FactKind.Segment => mode == FactEvolutionMode.SegmentSnapshot,
        FactKind.Event => mode is FactEvolutionMode.ImmutableEvent or FactEvolutionMode.MutableEvent,
        FactKind.Measurement => mode == FactEvolutionMode.MeasurementCorrection,
        _ => false
    };

    private static FactKind ParseFactKind(string value) => value switch
    {
        "segment" => FactKind.Segment,
        "event" => FactKind.Event,
        "measurement" => FactKind.Measurement,
        _ => throw new PackageValidationException($"Unknown FactKind '{value}'.")
    };

    private static FactEvolutionMode ParseEvolutionMode(string value) => value switch
    {
        "segmentSnapshot" => FactEvolutionMode.SegmentSnapshot,
        "immutableEvent" => FactEvolutionMode.ImmutableEvent,
        "mutableEvent" => FactEvolutionMode.MutableEvent,
        "measurementCorrection" => FactEvolutionMode.MeasurementCorrection,
        _ => throw new PackageValidationException($"Unknown Fact evolution mode '{value}'.")
    };

    private static string ResolvePackageFile(string root, string relativePath, string description)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new PackageValidationException($"{description} path must be a portable package-relative path.");
        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new PackageValidationException($"{description} path escapes or aliases the package root.");

        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new PackageValidationException($"{description} path escapes the package root.");
        RejectSymbolicLinks(root, segments, description);
        return path;
    }

    private static void RejectSymbolicLinks(string root, IReadOnlyList<string> segments, string description)
    {
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new PackageValidationException($"{description} package root must not be a symbolic link.");

        var current = root;
        for (var index = 0; index < segments.Count; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo entry = index == segments.Count - 1
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (entry.LinkTarget is not null)
                throw new PackageValidationException($"{description} path must not traverse a symbolic link.");
        }
    }

    private static byte[] ReadStrictUtf8File(string path, string description)
    {
        var bytes = ReadFile(path, description);
        ValidateStrictUtf8(bytes, description);
        return bytes;
    }

    private static void ValidateStrictUtf8(byte[] bytes, string description)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw new PackageValidationException($"{description} must be UTF-8 without a BOM.");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PackageValidationException($"{description} is not valid UTF-8.", exception);
        }
    }

    private static byte[] ReadFile(string path, string description)
    {
        try
        {
            if (!File.Exists(path))
                throw new PackageValidationException($"{description} file does not exist: {path}");
            return File.ReadAllBytes(path);
        }
        catch (PackageValidationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PackageValidationException($"Unable to read {description}: {path}", exception);
        }
    }

    private static JsonDocument ParseJson(byte[] bytes, string description)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new PackageValidationException($"{description} is not valid strict JSON.", exception);
        }
    }

    private static void VerifyHash(byte[] content, string expected, string description)
    {
        var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected)))
            throw new PackageValidationException(
                $"{description} content hash is {actual}, expected {expected}.");
    }

    private static void RequireObject(
        JsonElement element,
        string context,
        IReadOnlyCollection<string>? allowed,
        IReadOnlyCollection<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PackageValidationException($"{context} must be an object.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new PackageValidationException($"{context} contains duplicate field '{property.Name}'.");
            if (allowed is not null && !allowed.Contains(property.Name))
                throw new PackageValidationException($"{context} contains unknown field '{property.Name}'.");
        }
        foreach (var name in required.Where(name => !names.Contains(name)))
            throw new PackageValidationException($"{context} is missing required field '{name}'.");
    }

    private static string ReadNonEmptyString(JsonElement parent, string name, string context)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new PackageValidationException($"{context}.{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static int ReadPositiveInt(JsonElement parent, string name, string context)
    {
        var value = parent.GetProperty(name);
        if (!value.TryGetInt32(out var result) || result <= 0)
            throw new PackageValidationException($"{context}.{name} must be a positive integer.");
        return result;
    }

    private static long ReadNonNegativeLong(JsonElement parent, string name, string context)
    {
        var value = parent.GetProperty(name);
        if (!value.TryGetInt64(out var result) || result < 0)
            throw new PackageValidationException($"{context}.{name} must be a non-negative integer.");
        return result;
    }

    private static string ReadSha256(JsonElement parent, string name, string context)
    {
        var value = ReadNonEmptyString(parent, name, context);
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value[7..].Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new PackageValidationException($"{context}.{name} must be a lowercase sha256 content hash.");
        return value;
    }

    private static IReadOnlyList<int> ReadPositiveIntArray(JsonElement parent, string name, string context) =>
        ReadPositiveIntArray(parent.GetProperty(name), $"{context}.{name}");

    private static IReadOnlyList<int> ReadPositiveIntArray(JsonElement element, string context)
    {
        RequireNonEmptyArray(element, context);
        var result = new List<int>();
        foreach (var item in element.EnumerateArray())
        {
            if (!item.TryGetInt32(out var value) || value <= 0 || result.Contains(value))
                throw new PackageValidationException($"{context} must contain distinct positive integers.");
            result.Add(value);
        }
        return result.ToImmutableArray();
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement parent,
        string name,
        string context,
        bool allowEmpty = false)
    {
        var element = parent.GetProperty(name);
        if (element.ValueKind != JsonValueKind.Array || (!allowEmpty && element.GetArrayLength() == 0))
            throw new PackageValidationException($"{context}.{name} must be a{(allowEmpty ? string.Empty : " non-empty")} array.");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) ||
                values.Contains(item.GetString()!, StringComparer.Ordinal))
                throw new PackageValidationException($"{context}.{name} must contain distinct non-empty strings.");
            values.Add(item.GetString()!);
        }
        return values.ToImmutableArray();
    }

    private static void RequireNonEmptyArray(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new PackageValidationException($"{context} must be a non-empty array.");
    }
}

internal static class JsonElementBooleanExtensions
{
    public static bool TryGetBoolean(this JsonElement element, out bool value)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (element.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        value = default;
        return false;
    }
}
