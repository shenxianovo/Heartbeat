using System.Text.Json;
using Heartbeat.Application.Entities;

namespace Heartbeat.Model.Tests;

public sealed class ScenarioTests
{
    public static IEnumerable<(string Id, string Json)> Cases()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in document.RootElement.EnumerateArray())
                yield return (item.GetProperty("id").GetString()!, item.GetRawText());
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task DeclaredScenarioMatchesActualValidator(string id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        var fields = item.GetProperty("fields");
        var valid = EntitySchemaRules.ValidFields(fields)
            && EntitySchemaRules.ValidEntity(fields, item.GetProperty("references"), item.GetProperty("properties"));
        await Assert.That(id).IsEqualTo(item.GetProperty("id").GetString());
        await Assert.That(valid).IsEqualTo(item.GetProperty("expectedValid").GetBoolean());
    }
}
