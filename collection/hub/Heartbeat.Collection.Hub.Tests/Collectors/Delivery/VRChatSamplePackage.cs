using System.Diagnostics;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// A real VRChat Collector Package, produced by the collector's own <c>--create-package</c> entry
/// point from the framework-dependent build output. Registry tests deliberately publish this rather
/// than a synthetic payload, so the bytes that travel through the index, the download and the
/// Package loader are the ones a VRChat release would actually ship.
///
/// It is built once per test run because the package is a few megabytes.
/// </summary>
internal static class VRChatSamplePackage
{
    private static readonly Lazy<string> Built = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Directory holding <c>collector-manifest.json</c> and the collector's files.</summary>
    public static string PackageDirectory => Built.Value;

    public static (string PackageId, string Version) ReadIdentity(string packageDirectory)
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(packageDirectory, "collector-manifest.json")));
        return (
            manifest.RootElement.GetProperty("packageId").GetString()!,
            manifest.RootElement.GetProperty("version").GetString()!);
    }

    private static string Create()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManagedVRChatCollector");
        var executable = Path.Combine(
            source,
            OperatingSystem.IsWindows() ? "Heartbeat.Collector.VRChat.exe" : "Heartbeat.Collector.VRChat");
        if (!File.Exists(executable))
            throw new FileNotFoundException($"VRChat Collector build output was not copied: {executable}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var target = Path.Combine(Path.GetTempPath(), $"heartbeat-vrchat-sample-package-{Guid.NewGuid():N}");
        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            ArgumentList = { "--create-package", target }
        }) ?? throw new InvalidOperationException("Failed to start the VRChat Package builder."))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"VRChat --create-package exited with {process.ExitCode}.");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
            }
            catch (IOException)
            {
                // Leaving a temp directory behind must never fail a test run.
            }
        };
        return target;
    }
}
