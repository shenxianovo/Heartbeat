namespace Heartbeat.Collection.Hub.Configuration;

public readonly record struct HubRuntimeSettings(
    string ApiKey,
    TimeSpan UploadInterval,
    int IngestPort);

/// <summary>hub runtime 所需的最小配置表面；持久化与 UI 由 composition adapter 所有。</summary>
public interface IHubConfiguration
{
    HubRuntimeSettings Current { get; }
    event Action? Changed;
}

/// <summary>认证请求所需的设备 headers，由平台 head 提供。</summary>
public interface IDeviceIdentity
{
    string HardwareId { get; }
    string DeviceName { get; }
}
