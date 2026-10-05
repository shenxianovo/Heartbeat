namespace Heartbeat.Dev;

internal sealed record RepositoryContext(string Root)
{
    public string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    public static async Task<RepositoryContext> DiscoverAsync(string start)
    {
        var result = await ProcessRunner.CaptureAsync(
            start,
            "git",
            ["rev-parse", "--show-toplevel"],
            CancellationToken.None);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new InvalidOperationException("heartbeat-dev must run inside the Heartbeat Git repository.");
        }

        return new RepositoryContext(System.IO.Path.GetFullPath(result.StdOut.Trim()));
    }
}
