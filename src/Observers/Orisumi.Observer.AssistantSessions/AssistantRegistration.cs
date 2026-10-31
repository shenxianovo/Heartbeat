using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal static class AssistantRegistration
{
    internal static readonly Guid ObserverSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000007");
    internal static readonly Guid SessionSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000008");
    internal static readonly Guid InteractionSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000009");
    internal static readonly Guid SubagentSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-00000000000b");
    internal static readonly SchemaDefinition[] Definitions = SchemaDefinition.Load(typeof(AssistantRegistration).Assembly,
        "Orisumi.Observers.AssistantSessions.schemas.json");
    internal static async Task RegisterAsync(DataClient data, string programVersion, CancellationToken token = default)
    {
        await data.UseAsync([.. Definitions, .. DeviceContext.Definitions], ObserverSchemaId, new
        { platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux", hostName = Environment.MachineName, programVersion }, token);
        if (data.Device is { } device) await data.WriteAllAsync(device.Writes(data.Observer.EntityId!.Value), token);
    }
}
