using Heartbeat.Hub.Core.Http;

namespace Heartbeat.Hub.Core.Upload;

public interface IDeadLetterStore<T>
{
    int Count { get; }
    string? Location { get; }
    void Append(string stream, T item, ApiResult rejection);
}
