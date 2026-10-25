using System.CommandLine;
using System.CommandLine.Help;

namespace Heartbeat.Dev;

/// Builds, starts and stops the macOS client against this checkout's environment.
internal sealed class MacCommand(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    private readonly EnvironmentManager environment = new(repository, runner, output);

    public Command CreateCommand()
    {
        var command = new Command("mac", "Build and run the macOS client against this checkout's environment");
        command.SetAction(parse => new HelpAction().Invoke(parse));

        var build = new Command("build", "Build the macOS client into .artifacts/mac");
        var debug = new Option<bool>("--debug") { Description = "Build the Debug configuration" };
        build.Options.Add(debug);
        build.SetAction((parse, token) => BuildAsync(parse.GetValue(debug), token));
        command.Subcommands.Add(build);

        var run = new Command("run", "Start the built client connected to this checkout's API and Web console");
        var rebuild = new Option<bool>("--build") { Description = "Build the Release configuration first" };
        var window = new Option<bool>("--window") { Description = "Open the client window at launch" };
        run.Options.Add(rebuild);
        run.Options.Add(window);
        run.SetAction((parse, token) => RunAsync(parse.GetValue(rebuild), parse.GetValue(window), token));
        command.Subcommands.Add(run);

        var stop = new Command("stop", "Quit the clients started from this checkout; they end the active period first");
        stop.SetAction((_, token) => StopAsync(token));
        command.Subcommands.Add(stop);
        return command;
    }

    internal string Project => repository.Path("src", "Clients", "Heartbeat.Mac", "Heartbeat.xcodeproj");
    internal string DerivedData => repository.Path(".artifacts", "mac");
    internal string Application(string configuration) =>
        Path.Combine(DerivedData, "Build", "Products", configuration, "Heartbeat.app");

    /// Matches the main executable of any configuration built here, not the helper inside the bundle.
    internal string ProcessPattern =>
        System.Text.RegularExpressions.Regex.Escape(Path.Combine(DerivedData, "Build", "Products")) + "/[^/]+/Heartbeat\\.app/Contents/MacOS/Heartbeat( |$)";

    private async Task<int> BuildAsync(bool debug, CancellationToken token)
    {
        RequireMacOS();
        return await runner.StreamAsync("xcodebuild", BuildArguments(debug ? "Debug" : "Release"), output, token);
    }

    internal IReadOnlyList<string> BuildArguments(string configuration) =>
        ["-project", Project, "-scheme", "Heartbeat", "-configuration", configuration, "-destination", Destination,
            "-derivedDataPath", DerivedData, "-quiet", "build"];

    /// This Mac's architecture; the bundled helper is published for it.
    internal static string Destination =>
        $"platform=macOS,arch={(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64")}";

    private async Task<int> RunAsync(bool rebuild, bool window, CancellationToken token)
    {
        RequireMacOS();
        if (rebuild && await BuildAsync(false, token) is var built and not 0) return built;
        var running = await RunningAsync(token);
        if (running.Count > 0)
            throw new CommandUsageException($"The client is already running (PID {string.Join(", ", running)}). Run hb mac stop first.");
        var application = Application("Release");
        if (!Directory.Exists(application)) throw new CommandUsageException("No client is built. Run hb mac build first.");
        var status = await environment.StatusAsync(token);
        var arguments = LaunchArguments(status, window);
        var result = await runner.CaptureAsync("open", ["-n", application, "--args", .. arguments], null, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr.Trim());
        await output.WriteLineAsync($"Started {application}");
        await output.WriteLineAsync($"  API: {arguments[1]}");
        await output.WriteLineAsync($"  Web console: {arguments[3]}");
        await output.WriteLineAsync($"  Program instance: {arguments[5]}");
        return 0;
    }

    /// Each database volume gets its own program instance, so local SDK state never points at another backend.
    internal IReadOnlyList<string> LaunchArguments(EnvironmentStatus status, bool window)
    {
        if (!status.Running || status.Entries is null || status.Database is null)
            throw new CommandUsageException("The environment is not running. Start it with hb env up api.");
        var api = status.Services?.FirstOrDefault(service => service.Name == "api");
        if (api is null || api.State != "Running")
            throw new CommandUsageException("The API is not running. Start it with hb env up api.");
        string[] arguments =
        [
            "-backend", status.Entries.Api.TrimEnd('/') + "/",
            "-webConsole", status.Entries.Web,
            "-dataDirectory", Path.Combine(DerivedData, "instances", status.Database.Volume),
        ];
        return window ? [.. arguments, "-showWindow", "YES"] : arguments;
    }

    private async Task<int> StopAsync(CancellationToken token)
    {
        RequireMacOS();
        var running = await RunningAsync(token);
        if (running.Count == 0)
        {
            await output.WriteLineAsync("No client from this checkout is running.");
            return 0;
        }
        // The client handles SIGTERM like Quit: the helper ends the active period before the client exits.
        foreach (var id in running) await runner.CaptureAsync("kill", ["-TERM", id], null, token);
        for (var attempt = 0; attempt < 30 && (await RunningAsync(token)).Count > 0; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        var left = await RunningAsync(token);
        if (left.Count > 0) throw new InvalidOperationException($"The client did not quit (PID {string.Join(", ", left)}).");
        await output.WriteLineAsync($"Stopped PID {string.Join(", ", running)}.");
        return 0;
    }

    private async Task<IReadOnlyList<string>> RunningAsync(CancellationToken token)
    {
        var result = await runner.CaptureAsync("pgrep", ["-f", ProcessPattern], null, token);
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS()) throw new CommandUsageException("The macOS client builds and runs only on macOS.");
    }
}
