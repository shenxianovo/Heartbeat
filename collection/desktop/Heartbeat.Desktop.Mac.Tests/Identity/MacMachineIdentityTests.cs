using Heartbeat.Desktop.Mac.Identity;

namespace Heartbeat.Desktop.Mac.Tests.Identity;

public sealed class MacMachineIdentityTests
{
    [Fact]
    public void HardwareId_IsTrimmedIOPlatformUuid()
    {
        var identity = new MacMachineIdentity(
            new StubPlatformUuid("  AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\n"));

        Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", identity.HardwareId);
    }

    [Fact]
    public void MissingIOPlatformUuid_FailsInsteadOfUsingHostname()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new MacMachineIdentity(new StubPlatformUuid(null)).HardwareId);

        Assert.Contains("IOPlatformUUID", error.Message);
    }

    private sealed class StubPlatformUuid(string? value) : IMacPlatformUuid
    {
        public string? Read() => value;
    }
}
