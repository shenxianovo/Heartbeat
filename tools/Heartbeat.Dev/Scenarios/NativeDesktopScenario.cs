namespace Heartbeat.Dev;

internal sealed class NativeDesktopScenario(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Task<int> RunAsync(ScenarioOptions options, CancellationToken cancellationToken) =>
        ScenarioEnvironment.RunAsync(repository, runner, output, options,
        [
            "Human interaction is required to verify macOS input monitoring, accessibility, lock and sleep behaviour; queue evidence alone does not prove these actions.",
            "Only an isolated Hub is started; this scenario does not prove backend delivery or Web replay.",
            options.IncludeSensitiveEvidence ? "Collector logs were explicitly retained and may contain user context."
                : "Only timing, process, and Hub queue metadata were retained; no screen or input content was saved.",
        ], environment => ObserveAsync(environment, options, cancellationToken), cancellationToken);

    private async Task<int> ObserveAsync(ScenarioEnvironment environment, ScenarioOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("native-desktop requires a logged-in macOS desktop.");
        await environment.StartAsync(cancellationToken, ComposeService.Hub);
        using var hub = environment.ConnectHub();
        await HubQueueStatus.WaitReadyAsync(hub, cancellationToken);
        var before = await HubQueueStatus.ReadAsync(hub, cancellationToken);
        await using var collector = await ScenarioCollector.StartAsync(repository, runner, environment.Evidence,
            environment.Hub, environment.Token, $"verification-{Guid.NewGuid():N}", once: false,
            options.IncludeSensitiveEvidence, cancellationToken);
        await ScenarioWait.UntilAsync("the Collector's first Hub submission", async token =>
        {
            if (collector.Completion.IsCompleted)
                throw new InvalidOperationException($"Collector exited before the guided session (exit {(await collector.Completion).ExitCode}).");
            return HasNewQueueEvidence(before, await HubQueueStatus.ReadAsync(hub, token));
        }, cancellationToken);

        await output.WriteLineAsync("""
            Native Collector is running in an isolated verification environment and Hub custody is confirmed.
            Perform a short, non-sensitive sequence now: switch applications, type a few disposable characters,
            and use the mouse. Return here and press Enter to finish evidence collection.
            """);
        var confirmation = Console.In.ReadLineAsync(cancellationToken).AsTask();
        if (await Task.WhenAny(confirmation, collector.Completion) == collector.Completion)
            throw new InvalidOperationException($"Collector exited during the guided session (exit {(await collector.Completion).ExitCode}).");
        if (await confirmation is null) throw new InvalidOperationException("The guided session requires Enter to finish; input closed before confirmation.");
        var session = await collector.StopAsync(cancellationToken);
        var after = await HubQueueStatus.ReadAsync(hub, cancellationToken);
        await environment.WriteAsync("native-session.json", new
        {
            nativeStarted = session.Started,
            nativeCompleted = session.Completed,
            durationSeconds = (session.Completed - session.Started).TotalSeconds,
            collectorExitCode = session.ExitCode,
            hubStatusBefore = before,
            hubStatusAfter = after,
        }, cancellationToken);
        if (!HasNewQueueEvidence(before, after)) throw new InvalidOperationException("No new retryable Hub custody was confirmed during the guided session.");
        return session.ExitCode;
    }

    internal static bool HasNewQueueEvidence(HubQueueStatus before, HubQueueStatus after) =>
        after.Pending > before.Pending && before.Failed == 0 && after.Failed == 0;
}
