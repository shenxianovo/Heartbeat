namespace Heartbeat.Dev;

internal sealed class DeveloperCli(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter error)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync(Help.Text);
            return 0;
        }

        var remaining = args[1..];
        return args[0] switch
        {
            "env" => await new EnvironmentCommand(repository, runner, output, error)
                .RunAsync(remaining, cancellationToken),
            "verify" => await new VerificationCommand(repository, runner, output)
                .RunAsync(remaining, cancellationToken),
            "quality" => await new QualityCommand(repository, runner, output)
                .RunAsync(remaining, cancellationToken),
            "scenario" => await new ScenarioCommand(repository, runner, output)
                .RunAsync(remaining, cancellationToken),
            "probe" => await new ProbeCommand(repository, runner, output)
                .RunAsync(remaining, cancellationToken),
            "artifacts" => await new ArtifactsCommand(repository, output)
                .RunAsync(remaining, cancellationToken),
            _ => throw new CommandUsageException($"Unknown command '{args[0]}'."),
        };
    }
}

internal static class Help
{
    public const string Text = """
        Usage: heartbeat-dev <command> [options]

        Commands:
          env        Start, inspect, stop, or reset the local environment
          verify     Run checks selected from a Git change set
          quality    Compare structural quality with a Git base
          scenario   Run a reproducible verification scenario
          probe      Measure real host behaviour before choosing a rule parameter
          artifacts  List, prune, or inventory verification evidence

        Run heartbeat-dev <command> --help for command-specific usage.
        """;
}

internal sealed class CommandUsageException(string message) : Exception(message);
