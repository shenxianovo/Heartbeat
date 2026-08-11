using System.Text.Json;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Core.Tests;

public class StrictDtoWireContractTests
{
    [Fact]
    public void StrictDtos_WebJson_OmitLegacyDetectorFields()
    {
        var segmentJson = JsonSerializer.Serialize(new SegmentUploadRequest
        {
            Segments =
            [
                new ActivitySegmentItem
                {
                    AppIdentityKey = "win:code",
                    AppDisplayName = "Code"
                }
            ]
        }, JsonSerializerOptions.Web);
        var presenceJson = JsonSerializer.Serialize(new DeviceStatusRequest
        {
            CurrentAppIdentityKey = "win:code",
            CurrentAppDisplayName = "Code"
        }, JsonSerializerOptions.Web);
        var iconJson = JsonSerializer.Serialize(new IconUploadRequest
        {
            AppIdentityKey = "win:code",
            AppDisplayName = "Code",
            IconData = [1]
        }, JsonSerializerOptions.Web);

        using var segment = JsonDocument.Parse(segmentJson);
        Assert.False(segment.RootElement.GetProperty("segments")[0].TryGetProperty("appName", out _));
        using var presence = JsonDocument.Parse(presenceJson);
        Assert.False(presence.RootElement.TryGetProperty("currentApp", out _));
        using var icon = JsonDocument.Parse(iconJson);
        Assert.False(icon.RootElement.TryGetProperty("appName", out _));
    }

    [Fact]
    public void LegacyEmptyWireFields_DeserializeAsPresentDetectors()
    {
        var segment = JsonSerializer.Deserialize<SegmentUploadRequest>(
            """{"segments":[{"appName":""}]}""",
            JsonSerializerOptions.Web)!;
        var presence = JsonSerializer.Deserialize<DeviceStatusRequest>(
            """{"currentApp":""}""",
            JsonSerializerOptions.Web)!;
        var icon = JsonSerializer.Deserialize<IconUploadRequest>(
            """{"appName":"","iconData":""}""",
            JsonSerializerOptions.Web)!;

        Assert.Equal("", Assert.Single(segment.Segments).AppName);
        Assert.Equal("", presence.CurrentApp);
        Assert.Equal("", icon.AppName);
    }
}
