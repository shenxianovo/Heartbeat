using Heartbeat.Core;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>观测深度读数提取（ADR-029 §2）：server 侧对采集器契约深度表的镜像。</summary>
public class DepthReadingsTests
{
    [Fact]
    public void System_TwoLayers_AppThenTitle()
    {
        var readings = DepthReadings.For(ActivitySources.System, "Code", "hyperframes — a.ts", "Code|hyperframes — a.ts");

        DepthReading[] expected = [new(1, "app", "Code"), new(2, "title", "hyperframes — a.ts")];
        Assert.Equal(expected, readings);
    }

    [Fact]
    public void System_MissingTitle_OnlyL1()
    {
        var readings = DepthReadings.For(ActivitySources.System, "Code", null, "Code|");

        DepthReading[] expected = [new(1, "app", "Code")];
        Assert.Equal(expected, readings);
    }

    [Fact]
    public void System_MissingApp_FallsBackToUnknown()
    {
        var readings = DepthReadings.For(ActivitySources.System, "  ", null, "x");

        Assert.Equal("(unknown)", Assert.Single(readings).Value);
    }

    [Fact]
    public void Browser_DualReadingsAtL1_UrlAndTabTitle()
    {
        var readings = DepthReadings.For(ActivitySources.Browser, "chrome", "花生看板", "huasheng.com/dashboard");

        DepthReading[] expected = [new(1, "url", "huasheng.com/dashboard"), new(1, "tab_title", "花生看板")];
        Assert.Equal(expected, readings);
    }

    [Fact]
    public void Browser_MissingTitle_UrlOnly()
    {
        var readings = DepthReadings.For(ActivitySources.Browser, null, "", "example.com/a");

        DepthReading[] expected = [new(1, "url", "example.com/a")];
        Assert.Equal(expected, readings);
    }

    [Fact]
    public void UnknownSource_FallsBackToIdentityAndTitle()
    {
        var readings = DepthReadings.For("vscode", null, "Program.cs", "repo-root");

        DepthReading[] expected = [new(1, "identity", "repo-root"), new(2, "title", "Program.cs")];
        Assert.Equal(expected, readings);
    }
}
