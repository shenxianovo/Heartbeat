using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

internal static class CollectorDrainDriverConformance
{
    public static void AssertObserved(
        string driver,
        CollectorActivationTerminalResult terminal,
        bool hubInitiated,
        string deadlineAction)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Conformance",
            "collector-protocol-conformance.json")));
        var expected = corpus.RootElement.GetProperty("drainDrivers").EnumerateArray()
            .Single(item => item.GetProperty("driver").GetString() == driver);
        Assert.Equal(hubInitiated, expected.GetProperty("hubInitiated").GetBoolean());
        Assert.Equal(deadlineAction, expected.GetProperty("deadlineAction").GetString());
        switch (driver)
        {
            case "in_process":
                Assert.IsType<InProcessFencedExecution>(terminal.Execution);
                Assert.Equal(CollectorDrainReason.DeadlineExceeded, terminal.DrainOutcome.Reason);
                break;
            case "managed_process":
                Assert.IsType<ManagedProcessTerminatedExecution>(terminal.Execution);
                Assert.Equal(CollectorDrainReason.DeadlineExceeded, terminal.DrainOutcome.Reason);
                break;
            case "external_host":
                var external = Assert.IsType<ExternalHostLeaseRevokedExecution>(terminal.Execution);
                Assert.IsType<ExternalHostDrainEvidence.HostReported>(external.DrainEvidence);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(driver), driver, "Unknown Collector Execution Driver.");
        }
    }
}
