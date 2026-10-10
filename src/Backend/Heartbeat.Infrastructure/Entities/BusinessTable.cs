using System.Text;
using System.Text.Json;

namespace Heartbeat.Infrastructure.Entities;

internal sealed record BusinessTable(string Name, BusinessColumn[] Columns)
{
    internal static BusinessTable? From(string resourceName, JsonElement fields)
    {
        var name = "entity_" + resourceName.Replace('-', '_');
        if (Encoding.UTF8.GetByteCount(name) > 63) return null;
        var columns = fields.EnumerateObject().Select(field => new BusinessColumn(field.Name,
            JsonNamingPolicy.SnakeCaseLower.ConvertName(field.Name), field.Value)).ToArray();
        if (columns.Any(c => c.Name is "id" or "__omitted_fields" || c.Name.Length == 0
                || c.Name.Contains('\0') || Encoding.UTF8.GetByteCount(c.Name) > 63)
            || columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != columns.Length) return null;
        return new(name, columns);
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    internal string CreateSql() => $"CREATE TABLE {Quote(Name)} (id uuid PRIMARY KEY REFERENCES entities(id) ON DELETE CASCADE, "
        + "__omitted_fields text[] NOT NULL"
        + string.Concat(Columns.Select(c => $", {Quote(c.Name)} {c.SqlType}")) + ")";
}

internal sealed record BusinessColumn(string FieldName, string Name, JsonElement Definition)
{
    internal string Type => Definition.GetProperty("type").GetString()!;
    internal bool Reference => Type == "reference";
    internal string SqlType => Type switch
    {
        "string" => "text", "boolean" => "boolean", "integer" => "bigint", "number" => "numeric",
        "reference" => "uuid", _ => "jsonb",
    };
}
