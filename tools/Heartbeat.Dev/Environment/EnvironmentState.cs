using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Heartbeat.Dev;

internal sealed record EnvironmentPorts(int Api, int Docs, int Web, int Database)
{
    public static EnvironmentPorts Allocate(EnvironmentPorts? previous, Func<int, bool> available, Func<int> allocate)
    {
        var used = new HashSet<int>();
        int Pick(int port)
        {
            if (port <= 0 || used.Contains(port) || !available(port))
                do { port = allocate(); } while (used.Contains(port));
            used.Add(port);
            return port;
        }
        return new(Pick(previous?.Api ?? 0), Pick(previous?.Docs ?? 0),
            Pick(previous?.Web ?? 0), Pick(previous?.Database ?? 0));
    }

    public static bool Available(int port)
    {
        try { using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); return true; }
        catch (SocketException) { return false; }
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed record EnvironmentState(EnvironmentPorts Ports, string Volume, bool Temporary);
internal sealed record ServiceStatus(string Name, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Health = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExitCode = null);
internal sealed record EnvironmentEntries(string Api, string Docs, string Web);
internal sealed record EnvironmentDatabase(string ConnectionString, string Volume, bool Temporary);
internal sealed record EnvironmentStatus(string Checkout, bool Running,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ServiceStatus>? Services = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnvironmentEntries? Entries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dashboard = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnvironmentDatabase? Database = null);

internal sealed class EnvironmentFiles(RepositoryContext repository)
{
    public string StatePath => repository.Path(".artifacts", "env", "environment.json");
    public EnvironmentState? Read() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<EnvironmentState>(File.ReadAllText(StatePath), JsonOptions.Indented) : null;
    public void Save(EnvironmentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions.Indented));
    }
    public string DefaultVolume(bool worktree) => worktree ? WorktreeVolume(repository.Root) : Defaults().Volume;
    public EnvironmentState Defaults()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(repository.Path("tools", "Heartbeat.AppHost", "appsettings.json")));
        var root = json.RootElement;
        var ports = root.GetProperty("Ports");
        return new(new(ports.GetProperty("Api").GetInt32(), ports.GetProperty("Docs").GetInt32(),
            ports.GetProperty("Web").GetInt32(), ports.GetProperty("Database").GetInt32()),
            root.GetProperty("DatabaseVolume").GetString()!, false);
    }
    public static string WorktreeVolume(string root)
    {
        var name = Regex.Replace(Path.GetFileName(root).ToLowerInvariant(), "[^a-z0-9_.-]", "-");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12];
        return $"heartbeat-{name}-{hash}";
    }
    public static string TemporaryVolume() => $"heartbeat-temp-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
}
