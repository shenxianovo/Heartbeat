using Foundation;
using System.Runtime.Versioning;

namespace Heartbeat.Desktop.Mac;

[SupportedOSPlatform("macos")]
internal static class MacCredentialStorage
{
    private const string DevelopmentBundleIdentifier = "com.shenxianovo.heartbeat.desktop.dev";

    private static bool IsDevelopment =>
        NSBundle.MainBundle.BundleIdentifier == DevelopmentBundleIdentifier;

    public static string SaveHint => IsDevelopment
        ? "开发凭据保存在本机独立目录，首次接入需要联网验证。"
        : "API key 保存在系统钥匙串，首次接入需要联网验证。";

    public static ICredentialStore Create(string directory) =>
        IsDevelopment ? new MacDevelopmentCredentials(directory) : new MacKeychain();

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Heartbeat",
        IsDevelopment ? "DesktopDev" : "Desktop");
}
