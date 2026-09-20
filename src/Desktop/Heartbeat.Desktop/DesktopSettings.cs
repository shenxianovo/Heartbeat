using Heartbeat.Hub;

namespace Heartbeat.Desktop;

public sealed record DesktopSettings(Uri BackendUrl, Uri AuthUrl, Uri WebUrl, Guid OwnerId, string Target)
{
    public DeliveryDestination Destination => new(BackendUrl, OwnerId);

    public void Validate()
    {
        _ = Destination;
        RequireOrigin(AuthUrl);
        RequireOrigin(WebUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(Target);
    }

    public static Uri RequireOrigin(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https") || value.UserInfo.Length != 0 ||
            value.AbsolutePath != "/" || value.Query.Length != 0 || value.Fragment.Length != 0)
            throw new ArgumentException("请输入完整的 HTTP(S) 服务地址，不包含路径、查询参数或账号。", nameof(value));
        return value;
    }
}
