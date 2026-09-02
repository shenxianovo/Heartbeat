using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.UI.Diagnostics;

/// <summary>
/// 打包产物的启动 smoke：证明 Desktop 宿主在可选 Collector 缺席时仍能走完
/// <see cref="IHost.StartAsync"/> 并干净停止（ADR-048：Browser 独立发布，不是 Desktop 的启动前提）。
/// 只给 release 流水线用——它不拉起任何 UI，跑完就退出，并把结果写成一行 JSON 便于日志留痕。
/// </summary>
public static class DesktopStartupSmoke
{
    public const string ArgumentName = "--verify-startup";

    /// <param name="ReportPath">可选的报告落盘路径；Windows 的 GUI 子系统进程没有 stdout，靠它取回结果。</param>
    public sealed record Request(string? ReportPath = null);

    public static bool TryGetRequest(IEnumerable<string>? arguments, out Request request)
    {
        request = new Request();
        if (arguments is null)
            return false;
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, ArgumentName, StringComparison.Ordinal))
                return true;
            if (!argument.StartsWith(ArgumentName + "=", StringComparison.Ordinal))
                continue;
            var path = argument[(ArgumentName.Length + 1)..];
            request = new Request(string.IsNullOrWhiteSpace(path) ? null : path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// smoke 无法得出结论时的出口（例如单实例守卫已被别的进程占住）。宁可报失败，
    /// 也不要让「进程提前退出」被误读成「宿主启动正常」。
    /// </summary>
    public static int Inconclusive(Request request, string reason, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hostStarted"] = false,
            ["browser"] = "unknown",
            ["failure"] = reason
        });
        (output ?? Console.Out).WriteLine($"startup-smoke inconclusive {json}");
        WriteReport(request.ReportPath, json);
        return 1;
    }

    /// <returns>进程退出码：0 表示宿主起停成功。</returns>
    public static int Run(
        IHost host,
        Request request,
        TimeSpan? timeout = null,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        var budget = timeout ?? TimeSpan.FromSeconds(60);
        var report = new Dictionary<string, object?>
        {
            ["hostStarted"] = false,
            ["browser"] = "unknown",
            ["failure"] = null
        };

        try
        {
            using var cancellation = new CancellationTokenSource(budget);
            host.StartAsync(cancellation.Token).GetAwaiter().GetResult();
            report["hostStarted"] = true;
            report["browser"] = DescribeBrowser(host);
            host.StopAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            report["failure"] = $"{exception.GetType().Name}: {exception.Message}";
        }

        var succeeded = report["failure"] is null;
        var json = JsonSerializer.Serialize(report);
        (output ?? Console.Out).WriteLine($"startup-smoke {(succeeded ? "ok" : "failed")} {json}");
        WriteReport(request.ReportPath, json);
        return succeeded ? 0 : 1;
    }

    private static object DescribeBrowser(IHost host)
    {
        var runtime = host.Services.GetService<BrowserCollectorRuntime>();
        if (runtime is null)
            return "notRegistered";
        var snapshot = runtime.Current;
        return new Dictionary<string, object?>
        {
            ["installed"] = snapshot.IsInstalled,
            ["status"] = snapshot.RuntimeStatus.ToString(),
            ["statusDetail"] = snapshot.RuntimeStatusDetail,
            ["packageVersion"] = snapshot.PackageVersion
        };
    }

    private static void WriteReport(string? reportPath, string json)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
            return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 报告只是可观测性，写不下去不改变 smoke 结论。
        }
    }
}
