using System.CommandLine;
using System.CommandLine.Help;

namespace Orisumi.Dev;

internal sealed class DeveloperCli(
    RepositoryContext repository,
    IProcessRunner runner,
    TextWriter output,
    TextWriter error)
{
    internal Command CreateCommand()
    {
        var root = new Command("orisumi", "Orisumi development and verification")
        {
            new HelpOption { Recursive = true },
            new VersionOption(),
            new EnvironmentCommand(repository, runner, output).CreateCommand(),
            new MacCommand(repository, runner, output).CreateCommand(),
            new ObserversCommand(repository, runner, output).CreateCommand(),
            new VerificationCommand(repository, runner, output, error).CreateCommand(),
            Group("analyze", "Analyze code structure and size",
                new QualityCommand(repository, runner, output).CreateCommand(),
                new LocCommand(repository, runner, output).CreateCommand()),
            new ArtifactsCommand(repository, output).CreateCommand(),
            Group("tool", "Install or update the developer tool itself",
                new InstallCommand(repository, runner, output).CreateCommand()),
        };
        root.SetAction(parse => new HelpAction().Invoke(parse));
        return root;
    }

    internal static Command Group(string name, string description, params Command[] commands)
    {
        var group = new Command(name, description);
        group.SetAction(parse => new HelpAction().Invoke(parse));
        foreach (var command in commands) group.Subcommands.Add(command);
        return group;
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
            Terminal.Error(error, exception.Message);
            return 2;
        }
    }
}

internal sealed class CommandUsageException(string message) : Exception(message);
