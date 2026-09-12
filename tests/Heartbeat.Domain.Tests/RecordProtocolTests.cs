using System.Text.Json;
using Heartbeat.Recording;
using Heartbeat.Recording.Protocols;

namespace Heartbeat.Domain.Tests;

public sealed class RecordProtocolTests
{
    [Fact]
    public void ForegroundApplicationUsesExplicitContinuousRanges()
    {
        var protocol = RecordProtocols.Find("desktop.application.foreground", 1);

        Assert.NotNull(protocol);
        Assert.Equal(TimeMode.Range, protocol.TimeMode);
        Assert.Equal(EndMode.Explicit, protocol.EndMode);
        Assert.True(protocol.SupportsExtension);
    }

    [Theory]
    [InlineData("unknown", 1)]
    [InlineData("desktop.application.foreground", 2)]
    [InlineData("Desktop.Application.Foreground", 1)]
    public void UnknownProtocolsDoNotFallBackToAnotherVersion(string type, int version)
    {
        Assert.Null(RecordProtocols.Find(type, version));
    }

    [Fact]
    public void ForegroundApplicationAcceptsNativeIdentityWithoutRewritingIt()
    {
        using var value = JsonDocument.Parse("""
            {"device_id":"device-a","application":{"platform":"macos","id_kind":"bundle_id","id":"com.google.Chrome"}}
            """);

        RecordProtocols.ForegroundApplication.ValidateValue(value.RootElement);

        Assert.Equal("com.google.Chrome", value.RootElement.GetProperty("application").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"device_id":"device-a","application":null}""")]
    [InlineData("""{"device_id":" ","application":{"platform":"macos","id_kind":"bundle_id","id":"app"}}""")]
    [InlineData("""{"device_id":"device-a","application":{"platform":"macos","id":"app"}}""")]
    [InlineData("""{"device_id":"device-a","application":{"platform":"macos","id_kind":"bundle_id","id":123}}""")]
    [InlineData("""{"device_id":"device-a","application":{"platform":"macos","id_kind":"bundle_id","id":"app"},"activity":"reading"}""")]
    [InlineData("""{"device_id":"device-a","application":{"platform":"macos","id_kind":"bundle_id","id":"app","url":"https://example.com"}}""")]
    public void InvalidOrOutOfProtocolValuesAreRejected(string json)
    {
        using var value = JsonDocument.Parse(json);

        Assert.ThrowsAny<ArgumentException>(() =>
            RecordProtocols.ForegroundApplication.ValidateValue(value.RootElement));
    }
}
