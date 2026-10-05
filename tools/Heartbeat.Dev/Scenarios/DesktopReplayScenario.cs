namespace Heartbeat.Dev;

internal sealed class DesktopReplayScenario(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Task<int> RunAsync(ScenarioOptions options, CancellationToken token) =>
        ScenarioEnvironment.RunAsync(repository, runner, output, options,
        [
            options.Foreground
                ? "Packaged macOS Dev first use through real native UI, Auth, Collector, in-process Hub, isolated PostgreSQL/API and production Web."
                : "Background DesktopRuntime, real Collector projection, Hub, Auth, PostgreSQL/API and headless Web; controlled OS observations and temporary file credentials.",
            options.InteractiveLogin ? "Interactive real OIDC login." : "Real Auth-issued token in a temporary browser session; interactive OIDC is separate.",
            options.Foreground
                ? "Occupies the unlocked macOS desktop; requires Accessibility/Automation permission. Does not cover Windows, installer, production Keychain, permission grants, lock/sleep or upgrades."
                : "No native UI, OS collection, system credential store, permissions, packaging or interactive OIDC evidence; no foreground activation.",
            "Recovery covers Hub-accepted Records, not observations still in Collector memory before custody.",
        ], environment => RunAsync(environment, options, token), token);

    private async Task<int> RunAsync(ScenarioEnvironment environment, ScenarioOptions options, CancellationToken token)
    {
        var journey = new DesktopJourneyReport(environment, output);
        await journey.RunAsync(DesktopStage.Authentication, _ => DesktopReplayBrowser.AuthenticateAsync(environment.Configuration, token));
        await using var desktop = await journey.RunAsync(options.Foreground ? DesktopStage.PackageAndServices : DesktopStage.ServicesAndRuntime,
            artifacts => PrepareAsync(environment, options, artifacts, token));
        await journey.RunAsync(options.Foreground ? DesktopStage.FirstLaunchAndConfigure : DesktopStage.ConfigureRuntime, async _ =>
        {
            await desktop.ConfigureAsync(environment.Web, token);
            var settings = desktop.Settings;
            if (settings.OwnerId != environment.Owner || settings.BackendUrl != environment.Web || settings.WebUrl != environment.Web)
                throw new InvalidOperationException("Desktop configuration did not preserve the verified Owner and destination.");
        });
        var baseline = await journey.RunAsync(DesktopStage.FirstCollection,
            artifacts => CollectAsync(environment, desktop, artifacts, token));
        await ReplayAsync(DesktopStage.NormalWebReplay, baseline);
        if (options.Recovery)
        {
            var recovered = await RecoverAsync(environment, desktop, journey, baseline, token);
            await ReplayAsync(DesktopStage.RecoveredWebReplay, recovered);
        }
        await journey.RunAsync(DesktopStage.CleanQuit, _ => desktop.QuitAsync(token));
        return 0;

        Task ReplayAsync(DesktopStage stage, DesktopReplayBatch batch) => journey.RunAsync(stage,
            artifacts => DesktopReplayBrowser.RunAsync(repository, environment, batch, desktop.Application, desktop.Custody.HubId, artifacts, options.InteractiveLogin, token));
    }

    private async Task<DesktopScenarioSession> PrepareAsync(ScenarioEnvironment environment, ScenarioOptions options,
        StageArtifacts artifacts, CancellationToken token)
    {
        if (options.Foreground && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("desktop-replay requires macOS.");
        await environment.StartAsync(token, ComposeService.Web);
        if (!options.Foreground) return new RuntimeScenarioSession(repository, environment.Configuration);
        var package = await new DesktopPackageStep(repository, runner).BuildAsync(environment.Evidence, artifacts, token);
        return new DesktopSession(repository, environment.Configuration, package);
    }

    private static async Task<DesktopReplayBatch> CollectAsync(ScenarioEnvironment environment, DesktopScenarioSession desktop,
        StageArtifacts artifacts, CancellationToken token)
    {
        var expectation = new DesktopObservationExpectation(environment.Owner, desktop.Settings.Target, desktop.Application.Identifier, DateTimeOffset.UtcNow, desktop.Application.CollectorKey);
        await desktop.StartCollectionAsync(token);
        var witness = await new DesktopCollectionStep(desktop, artifacts).WaitAsync(expectation,
            cancellation => RecordReconciliation.ReadDatabaseAsync(environment, cancellation), token);
        await desktop.PauseAsync(token);
        await desktop.WaitForDrainAsync(token);
        var batch = DesktopReplayBatch.From(await RecordReconciliation.ReadDatabaseAsync(environment, token), witness.RecordId);
        batch.Witness.RequireContinuationOf(witness);
        return batch;
    }

    private static async Task<DesktopReplayBatch> RecoverAsync(ScenarioEnvironment environment, DesktopScenarioSession desktop,
        DesktopJourneyReport journey, DesktopReplayBatch baseline, CancellationToken token)
    {
        var accepted = await journey.RunAsync(DesktopStage.OfflineCustody, async artifacts =>
        {
            await environment.StopAsync(ComposeService.Api, token);
            var expectation = new DesktopObservationExpectation(environment.Owner, desktop.Settings.Target, desktop.Application.Identifier, DateTimeOffset.UtcNow, desktop.Application.CollectorKey);
            await desktop.StartCollectionAsync(token);
            var witness = await new DesktopCollectionStep(desktop, artifacts).WaitAsync(expectation,
                _ => Task.FromResult(desktop.Custody.Records), token);
            await desktop.PauseAsync(token);
            var snapshot = desktop.Custody;
            await artifacts.WriteAsync("custody.json", new { snapshot.HubId, records = RecordReconciliation.Summary(snapshot.Records) }, token);
            return (Snapshot: snapshot, WitnessId: witness.RecordId);
        });
        var restarted = await journey.RunAsync(DesktopStage.ForcedExitAndRestart, async artifacts =>
        {
            await desktop.RestartAfterCrashAsync(token);
            await desktop.PauseAsync(token);
            var snapshot = desktop.Custody;
            if (snapshot.HubId != accepted.Snapshot.HubId) throw new InvalidOperationException("Restart replaced the Hub identity.");
            var acceptedIds = accepted.Snapshot.Records.Select(item => item.Record.Id).ToHashSet();
            RecordReconciliation.RequireSameRecords(accepted.Snapshot.Records, snapshot.Records.Where(item => acceptedIds.Contains(item.Record.Id)).ToArray());
            await artifacts.WriteAsync("custody.json", new { snapshot.HubId, records = RecordReconciliation.Summary(snapshot.Records) }, token);
            return snapshot;
        });
        return await journey.RunAsync(DesktopStage.DeliveryRecovery, async artifacts =>
        {
            await environment.StartAsync(token, ComposeService.Api);
            await desktop.WaitForDrainAsync(token);
            var records = await RecordReconciliation.ReadDatabaseAsync(environment, token);
            RecordReconciliation.RequireSameRecords([.. baseline.Records, .. restarted.Records], records);
            await artifacts.WriteAsync("reconciliation.json", new { passed = true, accepted = accepted.Snapshot.Records.Length,
                delivered = records.Length, pending = desktop.Queue.Pending }, token);
            return DesktopReplayBatch.From(records, accepted.WitnessId);
        });
    }
}
