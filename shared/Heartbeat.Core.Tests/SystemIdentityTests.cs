namespace Heartbeat.Core.Tests;

public class SystemIdentityTests
{
    [Fact]
    public void AppName_CaseInsensitive()
        => Assert.Equal(SystemIdentity.Key("VSCode", "a"), SystemIdentity.Key("vscode", "a"));

    [Fact]
    public void Title_CaseSensitive()
        => Assert.NotEqual(SystemIdentity.Key("app", "A"), SystemIdentity.Key("app", "a"));

    [Fact]
    public void NullAndEmptyTitle_Fold()
        => Assert.Equal(SystemIdentity.Key("app", null), SystemIdentity.Key("app", ""));

    [Fact]
    public void Shape_MatchesMigrationBackfillSql()
    {
        // 与 20260702110038 迁移的回填 SQL 保持一致：lower(name) || E'\n' || coalesce(title, '')
        Assert.Equal("vscode\nmain.cs", SystemIdentity.Key("VSCode", "main.cs"));
        Assert.Equal("vscode\n", SystemIdentity.Key("VSCode", null));
    }
}
