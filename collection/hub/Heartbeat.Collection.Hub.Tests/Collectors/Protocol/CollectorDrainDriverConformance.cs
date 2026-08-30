using System.Text.Json;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

internal static class CollectorDrainDriverConformance
{
    public static void AssertObserved(
        string driver,
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
    }
}
