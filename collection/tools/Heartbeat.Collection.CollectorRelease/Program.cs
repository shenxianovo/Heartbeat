using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Collection.CollectorRelease;

// Explicit Collector release tooling.
//
//   dry-run   Build the collector from source, stage a complete Registry tree into a scratch
//             directory and verify it. No tag, no network, no deployed state is touched.
//   stage     Stage the tree for a real tag into an output directory an operator can copy to the
//             server. Publishing that directory (domain, reverse proxy, rsync) stays a human step.
//
// The registry base URI is a parameter with a documented placeholder default; the real host belongs
// to the deployment issue, not to this repository.

const string defaultRegistryBaseUri = "https://registry.example/collector-registry/v1/";

var command = args.Length > 0 ? args[0] : "help";
var options = ParseOptions(args.Skip(1));

switch (command)
{
    case "stage":
        return Stage(
            Require(options, "tag"),
            Require(options, "package-directory"),
            RegistryBaseUri(options),
            Require(options, "output"));
    case "dry-run":
        return DryRun(options);
    default:
        Console.Error.WriteLine(
            """
            Usage:
              dry-run [--collector vrchat] [--output <dir>] [--registry-base-uri <uri>]
                      [--configuration Release] [--package-directory <dir>] [--repository-root <dir>]
              stage --tag collector-vrchat/vX.Y.Z --package-directory <dir> --output <dir>
                    [--registry-base-uri <uri>]
            """);
        return 2;
}

int Stage(string tag, string packageDirectory, Uri registryBaseUri, string output)
{
    var result = CollectorReleaseStager.Stage(
        new CollectorReleaseRequest(tag, packageDirectory, registryBaseUri, output));
    if (!result.Succeeded)
    {
        Console.Error.WriteLine($"release refused ({result.Failure}): {result.Detail}");
        return 1;
    }
    foreach (var line in result.Report)
        Console.WriteLine(line);
    return 0;
}

int DryRun(IReadOnlyDictionary<string, string> parsed)
{
    var slug = parsed.GetValueOrDefault("collector", CollectorReleaseTarget.VRChat.Slug);
    if (CollectorReleaseTarget.Find(slug) is not { } target)
    {
        Console.Error.WriteLine($"release refused (UnknownReleaseTarget): no Collector publishes under slug '{slug}'.");
        return 1;
    }

    var scratch = Path.Combine(Path.GetTempPath(), $"heartbeat-collector-release-dry-run-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);
    try
    {
        var packageDirectory = parsed.GetValueOrDefault("package-directory");
        if (packageDirectory is null)
        {
            var repositoryRoot = parsed.GetValueOrDefault("repository-root") ?? FindRepositoryRoot();
            var publishDirectory = Path.Combine(scratch, "publish");
            var configuration = parsed.GetValueOrDefault("configuration", "Release");
            Console.WriteLine($"publishing {target.ProjectPath} ({configuration}, framework-dependent)");
            if (Run(
                    repositoryRoot,
                    "dotnet",
                    [
                        "publish", target.ProjectPath,
                        "--configuration", configuration,
                        "--self-contained", "false",
                        "--output", publishDirectory
                    ]) != 0)
            {
                Console.Error.WriteLine("release refused: dotnet publish failed.");
                return 1;
            }

            packageDirectory = Path.Combine(scratch, "package");
            var executable = Path.Combine(
                publishDirectory,
                OperatingSystem.IsWindows()
                    ? Path.GetFileNameWithoutExtension(target.ProjectPath) + ".exe"
                    : Path.GetFileNameWithoutExtension(target.ProjectPath));
            if (Run(publishDirectory, executable, ["--create-package", packageDirectory]) != 0)
            {
                Console.Error.WriteLine("release refused: --create-package failed.");
                return 1;
            }
        }

        var version = ReadManifestVersion(packageDirectory);
        var tag = parsed.GetValueOrDefault("tag") ?? $"collector-{target.Slug}/v{version}";
        var output = parsed.GetValueOrDefault("output") ?? Path.Combine(scratch, "staging");
        Console.WriteLine($"staging {tag} into {output}");
        return Stage(tag, packageDirectory, RegistryBaseUri(parsed), output);
    }
    finally
    {
        if (parsed.GetValueOrDefault("output") is not null && Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }
}

string ReadManifestVersion(string packageDirectory)
{
    using var manifest = JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(packageDirectory, "collector-manifest.json")));
    return manifest.RootElement.GetProperty("version").GetString()!;
}

int Run(string workingDirectory, string fileName, IEnumerable<string> arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
    process.WaitForExit();
    return process.ExitCode;
}

string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Heartbeat.slnx")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("Run this from the repository, or pass --repository-root.");
}

Uri RegistryBaseUri(IReadOnlyDictionary<string, string> parsed) =>
    new(parsed.GetValueOrDefault("registry-base-uri", defaultRegistryBaseUri), UriKind.Absolute);

string Require(IReadOnlyDictionary<string, string> parsed, string name) =>
    parsed.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");

Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    string? pending = null;
    foreach (var argument in arguments)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            pending = argument[2..];
            parsed[pending] = string.Empty;
            continue;
        }
        if (pending is null)
            throw new ArgumentException($"Unexpected argument '{argument}'.");
        parsed[pending] = argument;
        pending = null;
    }
    return parsed;
}
