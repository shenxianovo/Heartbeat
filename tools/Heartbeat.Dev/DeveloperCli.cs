using System.CommandLine;
using System.CommandLine.Help;

namespace Heartbeat.Dev;

internal sealed class DeveloperCli(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter error)
{
    internal RootCommand CreateCommand()
    {
        var root = new RootCommand("Heartbeat development, packaging and verification")
        {
            new EnvironmentCommand(repository, runner, output, error).CreateCommand(),
            new PackageCommand(repository, runner, output).CreateCommand(),
            new SigningCommand(runner, output).CreateCommand(),
            new VerificationCommand(repository, runner, output).CreateCommand(),
            new QualityCommand(repository, runner, output).CreateCommand(),
            new ScenarioCommand(repository, runner, output).CreateCommand(),
            new ProbeCommand(repository, runner, output).CreateCommand(),
            new ArtifactsCommand(repository, output).CreateCommand(),
        };
        root.SetAction(parse => new HelpAction().Invoke(parse));
        return root;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = CreateCommand().Parse(args);
        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
            // Program owns cancellation. Allow evidence and environment cleanup to finish.
            ProcessTerminationTimeout = null,
        };
        try
        {
            var result = await parsed.InvokeAsync(configuration, cancellationToken);
            return parsed.Errors.Count > 0 ? 2 : result;
        }
        catch (CommandUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
    }
}

internal sealed class CommandUsageException(string message) : Exception(message);
