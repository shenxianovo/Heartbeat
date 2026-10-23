using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

/// 每个用例使用独立数据库，用例结束时删除。
public abstract class DatabaseTest
{
    private TestDatabase? _database;

    protected TestDatabase Database => _database ?? throw new InvalidOperationException("The test database has not been created.");

    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken) =>
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);

    [After(Test)]
    public async Task DeleteDatabaseAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }

    protected static async Task AssertProblemAsync(
        HttpResponseMessage response,
        System.Net.HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        await Assert.That(response.StatusCode).IsEqualTo(expectedStatus);
        await Assert.That(response.Content.Headers.ContentType?.MediaType)
            .IsEqualTo("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("about:blank");
        await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo((int)expectedStatus);
        await Assert.That(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString())).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString())).IsFalse();
    }
}
