using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal sealed record ForegroundRegistration(VersionId ObserverSchema, VersionId ApplicationSchema, VersionId PeriodSchema)
{
    internal static readonly EntityId ObserverSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000003"));
    internal static readonly EntityId ApplicationSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000002"));
    internal static readonly EntityId PeriodSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000001"));

    internal static async Task<ForegroundRegistration> RegisterAsync(IObserverSession session, EntityId observer, string? programVersion = null, CancellationToken token = default)
    {
        var observerSchema = await RegisterSchemaAsync(ObserverSchemaId, new("前台状态 Observer",
            "表示一个前台状态 Observer 程序实例，保存它运行的平台、主机名和程序版本；它作为 Observer 对前台应用时段和窗口标题时段的观测负责。", "foreground-state-observers", Json("""
            {"platform":{"type":"string","description":"运行的平台：macos、windows、android 或 linux。","required":true,"nullable":false},
             "hostName":{"type":"string","description":"本机的名称，读不到时为 null。","required":true,"nullable":true},
             "programVersion":{"type":"string","description":"程序的版本。","required":true,"nullable":false}}
            """)), token);
        var applicationSchema = await RegisterSchemaAsync(ApplicationSchemaId, new("本机应用",
            "表示某台设备上的一个应用安装，保存用于识别安装的平台标识线索。", "local-applications", Json("""
            {"identifiers":{"type":"array","description":"平台标识线索，顺序无意义；没有线索时为空数组。","required":true,"nullable":false,
              "items":{"type":"object","description":"一项平台标识线索。","nullable":false,"fields":{
                "scheme":{"type":"string","description":"线索的种类：macos.bundle-id 是应用包标识，macos.executable-path 是可执行文件路径，android.package-name 是 Android 包名。","required":true,"nullable":false},
                "value":{"type":"string","description":"标识方案对应的原始值。","required":true,"nullable":false}}}}}
            """)), token);
        var periodSchema = await RegisterSchemaAsync(PeriodSchemaId, new("前台应用时段",
            "表达某个本机应用连续处于前台的一段活动；内容保存应用引用与当时读到的名称，Observation 保存时段。", "foreground-application-periods", Json("""
            {"localApplicationVersionId":{"type":"reference","description":"观测设备上的本机应用。","required":true,"nullable":false},
             "name":{"type":"string","description":"当时读到的应用名称，没读到时为 null。","required":true,"nullable":true}}
            """)), token);
        await session.RequestAsync("registerInstance", JsonLines.Json(new { schemaVersionId = observerSchema.Value, content = new
        {
            platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
            hostName = Environment.MachineName,
            programVersion = programVersion ?? typeof(ForegroundRegistration).Assembly.GetName().Version!.ToString(),
        } }), cancellationToken: token);
        return new(observerSchema, applicationSchema, periodSchema);

        async Task<VersionId> RegisterSchemaAsync(EntityId id, EntitySchema schema, CancellationToken cancellationToken)
        {
            var result = await session.RequestAsync("registerSchema", JsonLines.Json(new { entityId = id.Value, content = VersionJson.Content(schema) }), cancellationToken: cancellationToken);
            return new(result.GetProperty("versionId").GetGuid());
        }
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
