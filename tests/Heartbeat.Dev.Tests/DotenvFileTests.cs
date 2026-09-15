using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DotenvFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"heartbeat-dotenv-{Guid.NewGuid():N}");

    [Fact]
    public void ReadsQuotedValuesCommentsAndEscapes()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "test.env");
        File.WriteAllText(path, """
            PLAIN=value # comment
            SINGLE='Mac\'Book'
            DOUBLE="Development\tMac"
            """);

        var dotenv = DotenvFile.Read(path);

        Assert.Equal("value", dotenv.Get("PLAIN"));
        Assert.Equal("Mac'Book", dotenv.Get("SINGLE"));
        Assert.Equal("Development\tMac", dotenv.Get("DOUBLE"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
