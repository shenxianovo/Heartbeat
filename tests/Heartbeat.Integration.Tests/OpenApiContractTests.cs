using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Integration.Tests;

public sealed class OpenApiContractTests
{
    private static readonly string[] Categories = ["entity", "observation", "observation_schema", "observer"];
    [Test]
    [Arguments("/entities/observers/{id}", "Observer", "name")]
    [Arguments("/entities/observations/{id}", "Observation", "observerId")]
    [Arguments("/entities/observation-schemas/{id}", "ObservationSchema", "schema")]
    public async Task GeneratedRequestsDescribeTheirRequiredFields(string path, string schemaName, string field)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "openapi.json")));
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty(path).GetProperty("put");
        var identity = operation.GetProperty("parameters")[0].GetProperty("schema");
        await Assert.That(Regex.IsMatch(identity.GetProperty("examples")[0].GetString()!,
            identity.GetProperty("pattern").GetString()!)).IsTrue();
        var body = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        await Assert.That(body.GetProperty("$ref").GetString()).IsEqualTo($"#/components/schemas/{schemaName}");
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        await Assert.That(schema.GetProperty("required").EnumerateArray()
            .Any(item => item.GetString() == field)).IsTrue();
        await Assert.That(schema.GetProperty("not").GetProperty("required")[0].GetString()).IsEqualTo("id");
        foreach (var code in new[] { "201", "204" })
        {
            await Assert.That(operation.GetProperty("responses").GetProperty(code)
                .TryGetProperty("content", out _)).IsFalse();
        }

        await Assert.That(operation.GetProperty("responses").GetProperty("413")
            .GetProperty("content").TryGetProperty("application/problem+json", out _)).IsTrue();

        if (schemaName != "Observer")
        {
            foreach (var boundary in new[] { "startAt", "endAt" })
            {
                await Assert.That(schema.GetProperty("properties").GetProperty(boundary)
                    .GetProperty("type").EnumerateArray().Any(item => item.GetString() == "null")).IsTrue();
                await Assert.That(schema.GetProperty("required").EnumerateArray()
                    .Any(item => item.GetString() == boundary)).IsTrue();
            }
        }
    }

    [Test]
    public async Task GeneratedReadAndErrorsKeepTheirResponseContracts()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "openapi.json")));
        await Assert.That(document.RootElement.GetProperty("servers")[0].GetProperty("url")
            .GetString()).IsEqualTo("/api");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var branches = schemas.GetProperty("EntityResponse").GetProperty("oneOf");
        await Assert.That(branches.GetArrayLength()).IsEqualTo(4);
        await Assert.That(branches.EnumerateArray().Select(branch => branch.GetProperty("properties")
            .GetProperty("category").GetProperty("const").GetString()).Order()
            .SequenceEqual(Categories)).IsTrue();
        foreach (var branch in branches.EnumerateArray().Skip(2))
        {
            var utc = branch.GetProperty("properties").GetProperty("entity").GetProperty("allOf")[1];
            await Assert.That(utc.GetProperty("properties").GetProperty("startAt")
                .GetProperty("pattern").GetString()).IsEqualTo("Z$");
        }

        var problem = schemas.GetProperty("ProblemDetails");
        await Assert.That(problem.GetProperty("required").GetArrayLength()).IsEqualTo(4);
        await Assert.That(problem.GetProperty("properties").GetProperty("type")
            .GetProperty("const").GetString()).IsEqualTo("about:blank");
    }
}
