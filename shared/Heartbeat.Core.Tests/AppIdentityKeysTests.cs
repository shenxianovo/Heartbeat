namespace Heartbeat.Core.Tests;

public class AppIdentityKeysTests
{
    [Theory]
    [InlineData("Code.exe", "win:code")]
    [InlineData("VSCode", "win:vscode")]
    [InlineData("__away__", "sys:away")]
    public void LegacyWindowsName_NormalizesToPlatformIdentity(string name, string expected)
        => Assert.Equal(expected, AppIdentityKeys.FromLegacyWindowsAppName(name));

    [Theory]
    [InlineData("WIN:Code.EXE", "win:code")]
    [InlineData("mac:COM.MICROSOFT.VSCODE", "mac:com.microsoft.vscode")]
    [InlineData("SYS:Away", "sys:away")]
    public void Normalize_UsesCanonicalLowercaseIdentity(string key, string expected)
        => Assert.Equal(expected, AppIdentityKeys.Normalize(key));

    [Fact]
    public void MacProvisionalKey_IsShortUntilCollisionNeedsQualifier()
    {
        Assert.Equal("vscode", AppIdentityKeys.ProvisionalProductKey("mac:com.microsoft.vscode"));
        Assert.Equal("microsoft.vscode", AppIdentityKeys.QualifiedProductKey("mac:com.microsoft.vscode"));
    }
}
