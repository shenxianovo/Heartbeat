using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class EnvironmentResetTests
{
    [Fact]
    public async Task ResetPreviewsByDefaultAndOnlyDeletesWithExplicitApply()
    {
        var directory = Directory.CreateTempSubdirectory("heartbeat-reset-").FullName;
        try
        {
            var runner = new ResetRunner();
            using var output = new StringWriter();
            var cli = new DeveloperCli(new RepositoryContext(directory), runner, output, TextWriter.Null);

            Assert.Equal(0, await cli.RunAsync(["env", "reset"], CancellationToken.None));
            Assert.Empty(runner.Calls);
            using var preview = JsonDocument.Parse(output.ToString());
            Assert.False(preview.RootElement.GetProperty("applied").GetBoolean());

            Assert.Equal(7, await cli.RunAsync(["env", "reset", "--apply"], CancellationToken.None));
            var call = Assert.Single(runner.Calls);
            Assert.Equal("docker", call.File);
            Assert.Equal("compose", call.Arguments[0]);
            Assert.Equal(["down", "--volumes", "--remove-orphans"], call.Arguments.TakeLast(3));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ResetRunner : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(7);
        }

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
