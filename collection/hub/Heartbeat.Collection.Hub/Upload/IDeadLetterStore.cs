using Heartbeat.Collection.Hub.Http;

namespace Heartbeat.Collection.Hub.Upload;

public interface IDeadLetterStore<T>
{
    int Count { get; }
    string? Location { get; }
    void Append(string stream, T item, ApiResult rejection);
}
