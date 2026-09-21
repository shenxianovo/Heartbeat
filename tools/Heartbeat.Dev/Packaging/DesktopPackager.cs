using System.Runtime.InteropServices;

namespace Heartbeat.Dev;

internal sealed record DesktopPackageOptions(string Runtime, string OutputDirectory)
{
    internal static readonly string[] SupportedRuntimes = ["osx-arm64", "osx-x64", "win-arm64", "win-x64"];

    public bool IsMac => Runtime.StartsWith("osx-", StringComparison.Ordinal);

    public static DesktopPackageOptions Create(
        RepositoryContext repository, string? runtime = null, string? output = null, string? hostRuntime = null)
    {
        hostRuntime ??= RuntimeInformation.RuntimeIdentifier;
        runtime ??= hostRuntime;
        if (!SupportedRuntimes.Contains(runtime, StringComparer.Ordinal))
            throw new CommandUsageException($"Unsupported desktop runtime '{runtime}'. Use {string.Join(", ", SupportedRuntimes)}.");
        var platform = runtime.Split('-')[0];
        if (!hostRuntime.StartsWith(platform + "-", StringComparison.Ordinal))
            throw new CommandUsageException($"Packaging {runtime} requires a {platform} host and its platform SDK; current host is {hostRuntime}.");
        if (output is not null && string.IsNullOrWhiteSpace(output))
            throw new CommandUsageException("--output requires a directory path.");
        var directory = output is null
            ? repository.Path(".artifacts", platform == "osx" ? "desktop" : "desktop-windows")
            : Path.GetFullPath(output);
        return new DesktopPackageOptions(runtime, directory);
    }
}

internal sealed record DesktopPackageResult(int ExitCode, string ApplicationPath);

/// <summary>Owns publishing, platform assets and signing, staging, and replacement of one named local artifact.</summary>
internal sealed class DesktopPackager(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public async Task<DesktopPackageResult> PackageAsync(DesktopPackageOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(options.OutputDirectory);
        var staging = Path.Combine(options.OutputDirectory, $".package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var name = options.IsMac ? "Heartbeat Dev.app" : "Heartbeat Dev";
        var bundle = Path.Combine(staging, name);
        var destination = Path.Combine(options.OutputDirectory, name);
        var application = options.IsMac ? destination : Path.Combine(destination, "Heartbeat.Desktop.Windows.exe");
        try
        {
            var exit = options.IsMac
                ? await BuildMacAsync(options.Runtime, bundle, staging, cancellationToken)
                : await BuildWindowsAsync(options.Runtime, bundle, cancellationToken);
            if (exit != 0) return new DesktopPackageResult(exit, application);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceBundle(bundle, destination);
            return new DesktopPackageResult(0, application);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<int> BuildMacAsync(string runtime, string bundle, string staging, CancellationToken token)
    {
        var published = await RunAsync("dotnet", ["publish", repository.Path("src", "Desktop", "Heartbeat.Desktop.Mac"),
            "-c", "Release", "-r", runtime, $"-p:AppBundleDir={bundle}", "-p:CreatePackage=false", "-p:EnableCodeSigning=false", "--nologo"], token);
        if (published != 0) return published;
        if (!File.Exists(Path.Combine(bundle, "Contents", "Info.plist")))
            throw new InvalidOperationException("The macOS SDK did not produce an application bundle with Info.plist.");
        var resources = Path.Combine(bundle, "Contents", "Resources");
        Directory.CreateDirectory(resources);
        var iconset = Path.Combine(staging, "heartbeat.iconset");
        Directory.CreateDirectory(iconset);
        var icons = await CreateIconsAsync(iconset, token);
        if (icons != 0) return icons;
        var converted = await RunAsync("iconutil", ["-c", "icns", iconset, "-o", Path.Combine(resources, "heartbeat.icns")], token);
        if (converted != 0) return converted;
        return await SignMacBundleAsync(bundle, token);
    }

    private async Task<int> SignMacBundleAsync(string bundle, CancellationToken token)
    {
        // MonoBundle is not a standard nested-code location: --deep alone skips its native libraries.
        foreach (var library in Directory.EnumerateFiles(bundle, "*.dylib", SearchOption.AllDirectories).Order())
        {
            var signedLibrary = await RunAsync("codesign", ["--force", "--sign", "-", library], token);
            if (signedLibrary != 0) return signedLibrary;
            var verifiedLibrary = await RunAsync("codesign", ["--verify", "--strict", library], token);
            if (verifiedLibrary != 0) return verifiedLibrary;
        }
        var signed = await RunAsync("codesign", ["--force", "--deep", "--sign", "-", bundle], token);
        return signed != 0 ? signed : await RunAsync("codesign", ["--verify", "--deep", "--strict", bundle], token);
    }

    private async Task<int> CreateIconsAsync(string iconset, CancellationToken token)
    {
        foreach (var size in new[] { 16, 32, 128, 256, 512 })
        {
            foreach (var scale in new[] { 1, 2 })
            {
                var pixels = (size * scale).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var suffix = scale == 1 ? "" : "@2x";
                var exit = await RunAsync("sips", ["-z", pixels, pixels, repository.Path("assets", "desktop-collector", "macos.png"),
                    "--out", Path.Combine(iconset, $"icon_{size}x{size}{suffix}.png")], token);
                if (exit != 0) return exit;
            }
        }
        return 0;
    }

    private async Task<int> BuildWindowsAsync(string runtime, string bundle, CancellationToken token)
    {
        var exit = await RunAsync("dotnet", ["publish", repository.Path("src", "Desktop", "Heartbeat.Desktop.Windows"),
            "-c", "Release", "-r", runtime, "--self-contained", "true", "-o", bundle, "--nologo"], token);
        if (exit == 0 && !File.Exists(Path.Combine(bundle, "Heartbeat.Desktop.Windows.exe")))
            throw new InvalidOperationException("The Windows SDK did not produce Heartbeat.Desktop.Windows.exe.");
        return exit;
    }

    private async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await output.WriteLineAsync($"Packaging: {executable} {string.Join(' ', arguments)}");
        var result = await runner.CaptureAsync(executable, arguments, null, token);
        await output.WriteAsync(result.StdOut);
        await output.WriteAsync(result.StdErr);
        return result.ExitCode;
    }

    private static void ReplaceBundle(string bundle, string destination)
    {
        // Keep the last successful artifact until all SDK, asset and signing steps succeed.
        // A failed rename restores it; backup lives outside staging so cleanup cannot erase it.
        var backup = destination + $".previous-{Guid.NewGuid():N}";
        var existed = Directory.Exists(destination);
        if (existed) Directory.Move(destination, backup);
        try
        {
            Directory.Move(bundle, destination);
        }
        catch
        {
            if (existed) Directory.Move(backup, destination);
            throw;
        }
        if (existed) Directory.Delete(backup, recursive: true);
    }
}
