namespace Heartbeat.Dev;

internal static class ComposeInvocation
{
    public static IReadOnlyList<string> Create(
        RepositoryContext repository, string? envFile, bool release, string? projectName = null)
    {
        var arguments = new List<string> { "compose", "--project-directory", repository.Root };
        if (projectName is not null) arguments.AddRange(["--project-name", projectName]);
        if (envFile is not null) arguments.AddRange(["--env-file", envFile]);
        arguments.AddRange(["--file", repository.Path("compose.yaml")]);
        if (!release) arguments.AddRange(["--file", repository.Path("compose.dev.yaml")]);
        return arguments;
    }
}
