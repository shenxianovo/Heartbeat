using Heartbeat.Core;
using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Mac.Observations;

public sealed record MacApplication(
    string? BundleIdentifier,
    string? ExecutablePath,
    string? DisplayName,
    int ProcessIdentifier = 0);

public static class MacApplicationIdentity
{
    public static DesktopActivity ToActivity(MacApplication? application)
    {
        if (application == null)
            return DesktopActivity.None;

        var identity = FromBundleIdentifier(application.BundleIdentifier)
            ?? FromExecutablePath(application.ExecutablePath);
        return identity == null
            ? DesktopActivity.None
            : new DesktopActivity(identity, NullIfBlank(application.DisplayName), null);
    }

    private static string? FromBundleIdentifier(string? bundleIdentifier)
    {
        if (string.IsNullOrWhiteSpace(bundleIdentifier))
            return null;

        return AppIdentityKeys.Normalize($"{AppIdentityKeys.MacPrefix}{bundleIdentifier.Trim()}");
    }

    private static string? FromExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var executable = Path.GetFileName(executablePath.Trim());
        return string.IsNullOrWhiteSpace(executable)
            ? null
            : AppIdentityKeys.Normalize($"{AppIdentityKeys.MacPrefix}exe.{AppIdentityKeys.ProductSlug(executable)}");
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
