using Heartbeat.Core;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

public class HandleDerivationTests
{
    [Fact]
    public void System_TokenIsAppName()
    {
        var handle = HandleDerivation.Derive(ActivitySources.System, "blender.exe", null, "blender.exe|Untitled");

        Assert.Equal((ActivitySources.System, "blender.exe"), handle);
    }

    [Fact]
    public void System_MissingAppName_NoHandle()
    {
        Assert.Null(HandleDerivation.Derive(ActivitySources.System, null, null, "x|y"));
        Assert.Null(HandleDerivation.Derive(ActivitySources.System, " ", null, "x|y"));
    }

    [Fact]
    public void Browser_TokenIsDomainAttribute()
    {
        var handle = HandleDerivation.Derive(
            ActivitySources.Browser, "msedge.exe",
            """{"url":"https://huasheng.com/dashboard?tab=1","domain":"huasheng.com","windowId":3}""",
            "https://huasheng.com/dashboard");

        Assert.Equal((ActivitySources.Browser, "huasheng.com"), handle);
    }

    [Fact]
    public void Browser_NoDomainAttribute_FallsBackToUrlHost()
    {
        var handle = HandleDerivation.Derive(
            ActivitySources.Browser, null,
            """{"url":"https://sub.example.com/a/b"}""",
            "https://sub.example.com/a/b");

        Assert.Equal((ActivitySources.Browser, "sub.example.com"), handle);
    }

    [Fact]
    public void Browser_MalformedAttributes_FallsBackToIdentityKeyHost()
    {
        var handle = HandleDerivation.Derive(
            ActivitySources.Browser, null, "{not json", "https://example.com/path");

        Assert.Equal((ActivitySources.Browser, "example.com"), handle);
    }

    [Fact]
    public void Browser_NoAttributes_FallsBackToIdentityKeyHost()
    {
        var handle = HandleDerivation.Derive(
            ActivitySources.Browser, null, null, "https://example.com/docs");

        Assert.Equal((ActivitySources.Browser, "example.com"), handle);
    }

    [Fact]
    public void Browser_Underivable_NoHandle()
    {
        // 自定义 scheme 的 IdentityKey（edge:// 掐 query 后的原串）既无 attributes 也无 host 可取。
        Assert.Null(HandleDerivation.Derive(ActivitySources.Browser, null, null, "not-a-url"));
    }

    [Fact]
    public void UnknownSource_NoHandle()
    {
        // vscode 采集器未落地：仓库根形状待定，当前不产把手。
        Assert.Null(HandleDerivation.Derive("vscode", null, null, "E:/repo/file.cs"));
        Assert.Null(HandleDerivation.Derive("someday-collector", "x", null, "y"));
    }
}
