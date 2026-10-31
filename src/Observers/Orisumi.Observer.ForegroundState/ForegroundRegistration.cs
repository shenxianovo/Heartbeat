using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal static class ForegroundRegistration
{
    internal static readonly Guid ObserverSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000003");
    internal static readonly Guid ApplicationSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000002");
    internal static readonly Guid PeriodSchemaId = Guid.Parse("019e1bbb-ec00-7000-8000-000000000001");
    internal static readonly SchemaDefinition[] Definitions = SchemaDefinition.Load(typeof(ForegroundRegistration).Assembly,
        "Orisumi.Observers.ForegroundState.schemas.json");
    internal static async Task RegisterAsync(DataClient data, string? programVersion = null, CancellationToken token = default)
    {
        await data.UseAsync([.. Definitions, DeviceContext.Definition], ObserverSchemaId, new
        {
            platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
            hostName = Environment.MachineName,
            programVersion = programVersion ?? data.PackageVersion ?? typeof(ForegroundRegistration).Assembly.GetName().Version!.ToString(),
        }, token);
        if (data.Device is { } device) await data.WriteAsync(device.Write, token);
    }
}
