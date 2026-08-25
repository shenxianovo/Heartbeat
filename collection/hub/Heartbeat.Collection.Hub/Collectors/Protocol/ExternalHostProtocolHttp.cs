namespace Heartbeat.Collection.Hub.Collectors.Protocol;

public sealed record ProtocolHttpResponse(int StatusCode, string Body, bool IsJson = true);

public interface IExternalHostProtocolHttpHandler
{
    ValueTask<ProtocolHttpResponse?> HandleAsync(
        string httpMethod,
        string? path,
        Stream body,
        CancellationToken cancellationToken = default);
}

internal sealed class NullExternalHostProtocolHttpHandler : IExternalHostProtocolHttpHandler
{
    public ValueTask<ProtocolHttpResponse?> HandleAsync(
        string httpMethod,
        string? path,
        Stream body,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ProtocolHttpResponse?>(null);
}
