using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed record EnvironmentVolume(string Name, string Checkout, bool Temporary, bool Orphaned);

internal sealed class EnvironmentVolumes(EnvironmentManager manager)
{
    public async Task<IReadOnlyList<EnvironmentVolume>> ListAsync(CancellationToken token)
    {
        var names = await manager.ExecuteAsync("docker", ["volume", "ls", "--filter",
            $"label={EnvironmentManager.CheckoutLabel}", "--format", "{{.Name}}"], token);
        var result = new List<EnvironmentVolume>();
        foreach (var name in names.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var inspected = await manager.ExecuteAsync("docker", ["volume", "inspect", name], token);
            using var json = JsonDocument.Parse(inspected.StdOut);
            var labels = json.RootElement[0].GetProperty("Labels");
            var checkout = labels.GetProperty(EnvironmentManager.CheckoutLabel).GetString()!;
            var temporary = labels.TryGetProperty(EnvironmentManager.TemporaryLabel, out var value) && value.GetString() == "true";
            result.Add(new(name, checkout, temporary, !Directory.Exists(checkout)));
        }
        return result;
    }

    public async Task<IReadOnlyList<EnvironmentVolume>> PruneAsync(bool apply, CancellationToken token)
    {
        var candidates = (await ListAsync(token)).Where(volume => volume.Orphaned).ToArray();
        if (apply)
            foreach (var volume in candidates)
                // Docker refuses removal of any volume still attached to a container.
                await manager.ExecuteAsync("docker", ["volume", "rm", volume.Name], token);
        return candidates;
    }
}
