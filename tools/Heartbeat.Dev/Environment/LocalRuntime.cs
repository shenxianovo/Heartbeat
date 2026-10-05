namespace Heartbeat.Dev;

using System.Net;
using System.Net.Sockets;

internal static class ComposeInvocation
{
    public static IReadOnlyList<string> Create(
        RepositoryContext repository,
        string envFile,
        bool release,
        string? projectName = null)
    {
        var arguments = new List<string>
        {
            "compose", "--project-directory", repository.Root,
        };
        if (projectName is not null)
        {
            arguments.Add("--project-name");
            arguments.Add(projectName);
        }
        arguments.Add("--env-file");
        arguments.Add(envFile);
        arguments.Add("--file");
        arguments.Add(repository.Path("compose.yaml"));
        if (!release)
        {
            arguments.Add("--file");
            arguments.Add(repository.Path("compose.dev.yaml"));
        }
        return arguments;
    }
}

internal static class TcpPort
{
    public static int Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal static class PlaywrightEvidenceEnvironment
{
    public static IReadOnlyDictionary<string, string?> Create(
        ArtifactRun run,
        IReadOnlyDictionary<string, string?>? existing = null)
    {
        var environment = existing is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(existing);
        environment["HEARTBEAT_EVIDENCE_DIR"] = Path.Combine(run.Directory, "playwright");
        environment["HEARTBEAT_PLAYWRIGHT_JSON"] = Path.Combine(run.Directory, "playwright-report.json");
        environment["HEARTBEAT_PLAYWRIGHT_PORT"] = TcpPort.Reserve()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return environment;
    }
}
