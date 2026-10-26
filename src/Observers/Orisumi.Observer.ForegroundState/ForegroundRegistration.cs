using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal sealed record ForegroundRegistration(VersionId ObserverSchema, VersionId ApplicationSchema, VersionId PeriodSchema)
{
    internal static readonly EntityId ObserverSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000003"));
    internal static readonly EntityId ApplicationSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000002"));
    internal static readonly EntityId PeriodSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000001"));

    internal static async Task<ForegroundRegistration> RegisterAsync(ObserverClient sdk, EntityId observer, CancellationToken token = default)
    {
        var observerSchema = await sdk.RegisterSchemaAsync(ObserverSchemaId, new("前台状态 Observer",
            "表示一个前台状态 Observer 程序实例，保存它运行的平台、主机名和程序版本；它作为 Observer 对前台应用时段和窗口标题时段的观测负责。", "foreground-state-observers", Json("""
            {"platform":{"type":"string","required":true,"nullable":false},
             "hostName":{"type":"string","required":true,"nullable":true},
             "programVersion":{"type":"string","required":true,"nullable":false}}
            """)), token);
        var applicationSchema = await sdk.RegisterSchemaAsync(ApplicationSchemaId, new("本机应用",
            "表示某台设备上的一个应用安装，保存用于识别安装的平台标识线索。", "local-applications", Json("""
            {"identifiers":{"type":"array","required":true,"nullable":false,
              "items":{"type":"object","nullable":false,"fields":{
                "scheme":{"type":"string","required":true,"nullable":false},
                "value":{"type":"string","required":true,"nullable":false}}}}}
            """)), token);
        var periodSchema = await sdk.RegisterSchemaAsync(PeriodSchemaId, new("前台应用时段",
            "表达某个本机应用连续处于前台的一段活动；内容保存应用引用与当时读到的名称，Observation 保存时段。", "foreground-application-periods", Json("""
            {"localApplicationVersionId":{"type":"reference","required":true,"nullable":false},
             "name":{"type":"string","required":true,"nullable":true}}
            """)), token);
        sdk.RegisterInstance(observer, observerSchema, new(JsonSerializer.SerializeToElement(new
        {
            platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
            hostName = Environment.MachineName,
            programVersion = typeof(ForegroundRegistration).Assembly.GetName().Version!.ToString(),
        })));
        return new(observerSchema, applicationSchema, periodSchema);
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
