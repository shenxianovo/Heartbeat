using System.CommandLine;
using System.CommandLine.Help;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Spectre.Console;

namespace Orisumi.Dev;

/// Builds Runtime and Observer packages for one platform. The Mac client's build and the server image use the output.
internal sealed class ObserversCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Command CreateCommand()
    {
        var command = new Command("observers", "Build Runtime and Observer packages");
        command.SetAction(parse => new HelpAction().Invoke(parse));

        var pack = new Command("pack", "Publish Runtime and build the packages of the given Observers for one platform");
        var packages = new Argument<string[]>("package") { Description = "Package ids, for example foreground-state", Arity = ArgumentArity.OneOrMore };
        var target = new Option<string>("--target") { Description = ".NET runtime identifier, for example osx-arm64 or linux-x64", Required = true };
        var destination = new Option<string>("--output") { Description = "Directory for runtime/ and bundled/", Required = true };
        var sign = new Option<string?>("--sign") { Description = "Code signing identity for macOS; - signs ad hoc" };
        pack.Arguments.Add(packages);
        pack.Options.Add(target);
        pack.Options.Add(destination);
        pack.Options.Add(sign);
        pack.SetAction((parse, token) => PackAsync(parse.GetValue(packages)!, parse.GetValue(target)!,
            Path.GetFullPath(parse.GetValue(destination)!), parse.GetValue(sign), token));
        command.Subcommands.Add(pack);
        return command;
    }

    internal string Build => repository.Path(".artifacts", "observers", "build");

    private async Task<int> PackAsync(string[] ids, string target, string destination, string? sign, CancellationToken token)
    {
        if (sign is not null && !OperatingSystem.IsMacOS()) throw new CommandUsageException("--sign works only on macOS.");
        var known = Projects();
        var unknown = ids.Where(id => !known.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
            throw new CommandUsageException($"Unknown package {string.Join(", ", unknown)}. Known: {string.Join(", ", known.Keys.Order())}.");

        var runtime = Path.Combine(destination, "runtime");
        var bundled = Path.Combine(destination, "bundled");
        Recreate(runtime);
        Recreate(bundled);

        if (await PublishAsync(repository.Path("src", "Observers", "Orisumi.Observer.Runtime"), runtime, target, token,
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true") is var published and not 0)
            return published;
        if (sign is not null && await SignAsync([Path.Combine(runtime, "Orisumi.Observer.Runtime")], sign, token) is var signed and not 0)
            return signed;

        foreach (var id in ids)
            if (await PackageAsync(known[id], target, bundled, sign, token) is var packed and not 0) return packed;
        Terminal.Create(output).MarkupLine($"{Terminal.Mark(Outcome.Passed)} Runtime  {Terminal.Esc(Terminal.Path(repository, runtime))}");
        return 0;
    }

    /// Publishes one Observer, writes its manifest and zips both into bundled/.
    private async Task<int> PackageAsync(ObserverProject project, string target, string bundled, string? sign, CancellationToken token)
    {
        var properties = await PropertiesAsync(project.Path, token);
        var staging = Path.Combine(Build, "packages", project.Id);
        Recreate(staging);
        if (await PublishAsync(project.Path, Path.Combine(staging, "bin"), target, token) is var result and not 0) return result;
        // --sign is refused elsewhere than macOS before packing; the platform check here is for the analyzer.
        if (sign is not null && OperatingSystem.IsMacOS()
            && await SignAsync(Executables(Path.Combine(staging, "bin")), sign, token) is var code and not 0)
            return code;
        var version = properties["Version"];
        await File.WriteAllTextAsync(Path.Combine(staging, "observer.json"),
            Manifest(project, version, target, "bin/" + properties["AssemblyName"]), token);
        var archive = Path.Combine(bundled, $"{project.Id}-{version}-{target}.zip");
        ZipFile.CreateFromDirectory(staging, archive);
        Terminal.Create(output).MarkupLine($"{Terminal.Mark(Outcome.Passed)} Package  {Terminal.Esc(Terminal.Path(repository, archive))}");
        return 0;
    }

    /// The Observer projects under src/Observers that declare ObserverPackageId, by package id.
    internal IReadOnlyDictionary<string, ObserverProject> Projects() =>
        Directory.EnumerateFiles(repository.Path("src", "Observers"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => (path, properties: XDocument.Load(path).Descendants("PropertyGroup").Elements()
                .GroupBy(element => element.Name.LocalName).ToDictionary(group => group.Key, group => group.Last().Value)))
            .Where(item => item.properties.ContainsKey("ObserverPackageId"))
            .ToDictionary(item => item.properties["ObserverPackageId"], item => new ObserverProject(item.path,
                item.properties["ObserverPackageId"], item.properties["ObserverName"], item.properties.GetValueOrDefault("ObserverScope")));

    /// observer.json, see Clients · Packages.
    internal static string Manifest(ObserverProject project, string version, string target, string entry) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["format"] = 1,
            ["id"] = project.Id,
            ["name"] = project.Name,
            ["version"] = version,
            ["target"] = target,
            ["protocol"] = 1,
            ["entry"] = entry,
            ["scope"] = project.Scope ?? "host",
        }, JsonOptions.Indented);

    private Task<int> PublishAsync(string project, string into, string target, CancellationToken token, params string[] extra) =>
        runner.StreamAsync("dotnet", ["publish", project, "--configuration", "Release", "--runtime", target, "--self-contained",
            "-p:DebugType=none", "--artifacts-path", Build, "--output", into, .. extra], output, token);

    private async Task<IReadOnlyDictionary<string, string>> PropertiesAsync(string project, CancellationToken token)
    {
        var result = await runner.CaptureAsync("dotnet", ["msbuild", project, "-getProperty:Version", "-getProperty:AssemblyName"], null, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr.Trim() + result.StdOut.Trim());
        using var json = JsonDocument.Parse(result.StdOut);
        return json.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
    }

    private async Task<int> SignAsync(IEnumerable<string> files, string identity, CancellationToken token)
    {
        foreach (var file in files)
            if (await runner.StreamAsync("codesign", ["--force", "--sign", identity, file], output, token) is var code and not 0)
                return code;
        return 0;
    }

    /// Mach-O files that macOS checks: executables and dynamic libraries.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static IEnumerable<string> Executables(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".dylib", StringComparison.Ordinal)
                || (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0)
            .Order(StringComparer.Ordinal);

    private static void Recreate(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
    }
}

internal sealed record ObserverProject(string Path, string Id, string Name, string? Scope);
