using Heartbeat.Hub.Host;

if (args.Contains("--check-auth", StringComparer.Ordinal)) return await AuthCheck.RunAsync();
await using var app = HubApplication.Create(args);
await app.RunAsync();
return 0;

namespace Heartbeat.Hub.Host
{
    public partial class HubProgram;
}
