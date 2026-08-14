using Heartbeat.Desktop.Mac.Identity;

namespace Heartbeat.Desktop.Mac.Tests.Identity;

public sealed class IoregPlatformUuidTests
{
    [Fact]
    public void Parse_FindsIOPlatformUuidWithoutDependingOnOutputOrder()
    {
        const string output = """
            +-o MacBookPro
              {
                "board-id" = <"Mac-123">
                "IOPlatformUUID" = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"
              }
            """;

        Assert.Equal(
            "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE",
            IoregPlatformUuid.Parse(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"IOPlatformSerialNumber\" = \"secret\"")]
    public void Parse_MissingUuid_ReturnsNull(string output)
    {
        Assert.Null(IoregPlatformUuid.Parse(output));
    }
}
