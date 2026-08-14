using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Server.AppCatalog;

public static class AppCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    public static AppCatalogSnapshot LoadFile(string path, bool requireCanonicalOrdering = true)
    {
        if (!File.Exists(path))
            throw new AppCatalogException($"App Catalog artifact was not found at '{path}'.");

        return Parse(File.ReadAllText(path, Encoding.UTF8), requireCanonicalOrdering);
    }

    public static AppCatalogSnapshot Parse(string json, bool requireCanonicalOrdering = true)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        }
        catch (JsonException exception)
        {
            throw new AppCatalogException($"App Catalog is not valid JSON: {exception.Message}");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            RequireObject(root, "root");
            RequireProperties(root, "root", "schemaVersion", "catalogVersion", "products");

            var schemaVersion = RequireInt32(root, "schemaVersion", "root");
            if (schemaVersion != SupportedSchemaVersion)
                throw new AppCatalogException(
                    $"Unsupported App Catalog schemaVersion {schemaVersion}; expected {SupportedSchemaVersion}.");

            var catalogVersion = RequireInt32(root, "catalogVersion", "root");
            if (catalogVersion < 1)
                throw new AppCatalogException("App Catalog catalogVersion must be at least 1.");

            var productsElement = RequireProperty(root, "products", "root");
            if (productsElement.ValueKind != JsonValueKind.Array)
                throw new AppCatalogException("App Catalog root.products must be an array.");

            var products = new List<AppCatalogProduct>();
            foreach (var (element, index) in productsElement.EnumerateArray().Select((x, i) => (x, i)))
            {
                var location = $"products[{index}]";
                RequireObject(element, location);
                RequireProperties(element, location, "key", "displayName", "identities");

                var key = RequireString(element, "key", location);
                var displayName = RequireString(element, "displayName", location);
                var identitiesElement = RequireProperty(element, "identities", location);
                if (identitiesElement.ValueKind != JsonValueKind.Array)
                    throw new AppCatalogException($"App Catalog {location}.identities must be an array.");

                var identities = identitiesElement.EnumerateArray()
                    .Select((x, identityIndex) => x.ValueKind == JsonValueKind.String
                        ? x.GetString()!
                        : throw new AppCatalogException(
                            $"App Catalog {location}.identities[{identityIndex}] must be a string."))
                    .ToList();
                products.Add(new AppCatalogProduct(key, displayName, identities));
            }

            Validate(products, requireCanonicalOrdering);
            var document = new AppCatalogDocument(schemaVersion, catalogVersion, products);
            var canonicalBytes = SerializeCanonical(document);
            return new AppCatalogSnapshot(
                document,
                canonicalBytes,
                Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant());
        }
    }

    public static byte[] SerializeCanonical(AppCatalogDocument document)
    {
        var products = document.Products
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x with
            {
                Identities = x.Identities.Order(StringComparer.Ordinal).ToArray()
            })
            .ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteNumber("catalogVersion", document.CatalogVersion);
            writer.WritePropertyName("products");
            writer.WriteStartArray();
            foreach (var product in products)
            {
                writer.WriteStartObject();
                writer.WriteString("key", product.Key);
                writer.WriteString("displayName", product.DisplayName);
                writer.WritePropertyName("identities");
                writer.WriteStartArray();
                foreach (var identity in product.Identities)
                    writer.WriteStringValue(identity);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void Validate(IReadOnlyList<AppCatalogProduct> products, bool requireCanonicalOrdering)
    {
        var productKeys = new HashSet<string>(StringComparer.Ordinal);
        var identityKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.Key) ||
                product.Key != product.Key.Trim() ||
                product.Key != AppIdentityKeys.ProductSlug(product.Key))
            {
                throw new AppCatalogException(
                    $"App Catalog product key '{product.Key}' must be a normalized lowercase product slug.");
            }

            if (!productKeys.Add(product.Key))
                throw new AppCatalogException($"Duplicate App Catalog product key '{product.Key}'.");

            if (string.IsNullOrWhiteSpace(product.DisplayName) || product.DisplayName != product.DisplayName.Trim())
                throw new AppCatalogException(
                    $"App Catalog product '{product.Key}' must have a non-blank, trimmed displayName.");

            if (product.Identities.Count == 0)
                throw new AppCatalogException(
                    $"App Catalog product '{product.Key}' must declare at least one identity.");

            foreach (var identity in product.Identities)
            {
                string normalized;
                try
                {
                    normalized = AppIdentityKeys.Normalize(identity);
                }
                catch (ArgumentException exception)
                {
                    throw new AppCatalogException(
                        $"App Catalog identity '{identity}' is invalid: {exception.Message}");
                }

                if (identity != normalized)
                    throw new AppCatalogException(
                        $"App Catalog identity '{identity}' is not normalized; expected '{normalized}'.");
                if (!identityKeys.Add(identity))
                    throw new AppCatalogException($"Duplicate App Catalog identity '{identity}'.");
            }

            if (requireCanonicalOrdering &&
                !product.Identities.SequenceEqual(product.Identities.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new AppCatalogException(
                    $"App Catalog identities for product '{product.Key}' must use canonical ordinal ordering.");
            }
        }

        if (requireCanonicalOrdering &&
            !products.Select(x => x.Key).SequenceEqual(
                products.Select(x => x.Key).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new AppCatalogException("App Catalog products must use canonical ordinal key ordering.");
        }
    }

    private static void RequireObject(JsonElement element, string location)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new AppCatalogException($"App Catalog {location} must be an object.");
    }

    private static void RequireProperties(JsonElement element, string location, params string[] expected)
    {
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new AppCatalogException(
                    $"App Catalog {location} contains unknown property '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw new AppCatalogException(
                    $"App Catalog {location} contains duplicate property '{property.Name}'.");
        }

        foreach (var property in expected)
        {
            if (!seen.Contains(property))
                throw new AppCatalogException(
                    $"App Catalog {location} is missing required property '{property}'.");
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string property, string location)
        => element.TryGetProperty(property, out var value)
            ? value
            : throw new AppCatalogException(
                $"App Catalog {location} is missing required property '{property}'.");

    private static int RequireInt32(JsonElement element, string property, string location)
    {
        var value = RequireProperty(element, property, location);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new AppCatalogException($"App Catalog {location}.{property} must be an integer.");
    }

    private static string RequireString(JsonElement element, string property, string location)
    {
        var value = RequireProperty(element, property, location);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new AppCatalogException($"App Catalog {location}.{property} must be a string.");
    }
}
