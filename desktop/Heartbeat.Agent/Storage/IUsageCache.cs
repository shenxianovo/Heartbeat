using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Agent.Storage
{
    public interface IUsageCache
    {
        void Add(List<AppUsageItem> items);
        List<AppUsageItem> Load();
        void Clear();
    }
}
