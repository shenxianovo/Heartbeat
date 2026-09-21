using System.CommandLine;

namespace Heartbeat.Dev;

internal sealed class SetupCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("setup", "Interactively verify Auth and atomically save local Hub configuration");
        command.SetAction(async (_, token) =>
        {
            if (Console.IsInputRedirected)
                throw new CommandUsageException("env setup needs an interactive terminal; API keys are entered without echo.");
            await new EnvironmentSetup(repository, runner, new SetupConsole(output), output).RunAsync(token);
            return 0;
        });
        return command;
    }
}
