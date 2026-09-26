using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class DesktopReplayScenario(RepositoryContext repository, IProcessRunner runner, TextWriter output)
{
    public Task<int> RunAsync(ScenarioOptions options, CancellationToken token) =>
        ScenarioEnvironment.RunAsync(repository, runner, output, options,
        [
            "Packaged macOS Dev first use through real native UI, Auth, Collector, in-process Hub, isolated PostgreSQL/API and production Web.",
            options.InteractiveLogin ? "Interactive real OIDC login." : "Real Auth-issued token in a temporary browser session; interactive OIDC is separate.",
            "Requires an unlocked macOS desktop and Accessibility/Automation permission for osascript. Does not cover distribution, installer, production Keychain, Windows, permission grants, lock/sleep or upgrades.",
            "Recovery covers Hub-accepted Records, not observations still in Collector memory before custody.",
        ], environment => RunAsync(environment, options, token), token);

    private async Task<int> RunAsync(ScenarioEnvironment environment, ScenarioOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("desktop-replay requires macOS.");
        await AuthenticatedReplayBrowser.AuthenticateAsync(environment.Configuration, token);
        await StageAsync(environment, "package-and-services", token);
        await environment.StartAsync(token, ComposeService.Web);
        var package = await new DesktopPackageStep(repository, runner).BuildAsync(environment.Evidence, token);
        await using var desktop = new DesktopJourney(repository, environment.Configuration, package.Executable);
        await StageAsync(environment, "first-launch-and-configure", token);
        desktop.Launch();
        await desktop.Ui.ConfigureAsync(environment.Web, token);
        var settings = desktop.Settings;
        if (settings.OwnerId != environment.Owner || settings.BackendUrl != environment.Web || settings.WebUrl != environment.Web)
            throw new InvalidOperationException("Native configuration did not preserve the verified Owner and destination.");
        await StageAsync(environment, "first-collection", token);
        var started = DateTimeOffset.UtcNow;
        await desktop.Ui.StartCollectionAsync(token);
        var first = await WaitForWitnessAsync(environment, settings.Target, package.Identifier, started, token);
        await desktop.PauseAsync(token);
        await desktop.WaitForDrainAsync(token);
        var baseline = await ReadRecordsAsync(environment, token);
        await ReplayAsync(environment, options, first.RecordId, baseline, package.DisplayName, "normal", token);
        if (options.Recovery)
            await RecoverAsync(environment, desktop, baseline, package.Identifier, package.DisplayName, options, token);
        await StageAsync(environment, "clean-quit", token);
        await desktop.QuitAsync(token);
        await StageAsync(environment, "completed", token);
        return 0;
    }

    private async Task RecoverAsync(ScenarioEnvironment environment, DesktopJourney desktop, RecoveryRecord[] baseline,
        string applicationId, string displayName, ScenarioOptions options, CancellationToken token)
    {
        await StageAsync(environment, "offline-custody", token);
        await environment.StopAsync(ComposeService.Api, token);
        await desktop.Ui.StartCollectionAsync(token);
        await ScenarioWait.UntilAsync("offline application custody", _ => Task.FromResult(
            desktop.Custody.Records.Any(item => IsWitness(item, applicationId))), token);
        await desktop.PauseAsync(token);
        var accepted = desktop.Custody;
        await environment.WriteAsync("custody-before-crash.json", new { accepted.HubId, records = RecoveryEvidence.Summary(accepted.Records) }, token);
        await StageAsync(environment, "forced-exit-and-restart", token);
        await desktop.RestartAfterCrashAsync(token);
        await desktop.PauseAsync(token);
        var restarted = desktop.Custody;
        if (restarted.HubId != accepted.HubId) throw new InvalidOperationException("Restart replaced the Hub identity.");
        var acceptedIds = accepted.Records.Select(item => item.Record.Id).ToHashSet();
        RecoveryEvidence.RequireSameRecords(accepted.Records, restarted.Records.Where(item => acceptedIds.Contains(item.Record.Id)).ToArray());
        await environment.WriteAsync("custody-after-restart.json", new { restarted.HubId, records = RecoveryEvidence.Summary(restarted.Records) }, token);
        await StageAsync(environment, "delivery-recovery", token);
        await environment.StartAsync(token, ComposeService.Api);
        await desktop.WaitForDrainAsync(token);
        var recovered = await ReadRecordsAsync(environment, token);
        RecoveryEvidence.RequireSameRecords([.. baseline, .. restarted.Records], recovered);
        await environment.WriteAsync("reconciliation.json", new { passed = true, accepted = accepted.Records.Length,
            delivered = recovered.Length, pending = desktop.Custody.Records.Length }, token);
        var witness = accepted.Records.First(item => IsWitness(item, applicationId));
        await ReplayAsync(environment, options, witness.Record.Id, recovered, displayName, "recovered", token);
    }

    private static bool IsWitness(RecoveryRecord item, string applicationId) =>
        item.Track.Type == "desktop.application.foreground"
        && item.Record.EndedAt - item.Record.StartedAt >= TimeSpan.FromSeconds(2)
        && item.Record.Value.GetProperty("application").GetProperty("id").GetString() == applicationId;

    private static async Task<RecoveryRecord[]> ReadRecordsAsync(ScenarioEnvironment environment, CancellationToken token) =>
        JsonSerializer.Deserialize<RecoveryRecord[]>(await environment.QueryDatabaseAsync(RecoveryEvidence.DatabaseQuery, token), JsonSerializerOptions.Web)!;

    private static async Task<DesktopReplayEvidence> WaitForWitnessAsync(ScenarioEnvironment environment, string target,
        string applicationId, DateTimeOffset since, CancellationToken token)
    {
        DesktopReplayEvidence? witness = null;
        await ScenarioWait.UntilAsync("the desktop client's delivered application Record", async cancellation =>
        {
            var json = await environment.QueryDatabaseAsync(DesktopReplayEvidence.Query(environment.Owner, target,
                applicationId, since, DateTimeOffset.UtcNow), cancellation);
            witness = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DesktopReplayEvidence>(json);
            return witness is not null;
        }, token);
        return witness!;
    }

    private async Task ReplayAsync(ScenarioEnvironment environment, ScenarioOptions options, Guid witnessId,
        RecoveryRecord[] records, string displayName, string phase, CancellationToken token)
    {
        await StageAsync(environment, $"{phase}-web-replay", token);
        var item = records.Single(item => item.Record.Id == witnessId);
        var witness = new { record = new { item.TrackId, recordId = item.Record.Id, item.Record.StartedAt, item.Record.EndedAt },
            target = item.Target, applicationId = item.Record.Value.GetProperty("application").GetProperty("id").GetString(),
            applicationName = displayName, applicationKind = "bundle_id" };
        await environment.WriteAsync("replay-expectation.json", witness, token);
        if (options.InteractiveLogin) await DesktopReplayBrowser.RunAsync(repository, environment.Evidence, environment.Web, token);
        else await AuthenticatedReplayBrowser.RunAsync(repository, environment, records, token);
        File.Copy(Path.Combine(environment.Evidence.Run.Directory, "replay.json"),
            Path.Combine(environment.Evidence.Run.Directory, $"{phase}-replay.json"), overwrite: true);
    }

    private async Task StageAsync(ScenarioEnvironment environment, string stage, CancellationToken token)
    {
        await output.WriteLineAsync($"Desktop journey: {stage}");
        await environment.WriteAsync("journey.json", new { stage, passed = stage == "completed" }, token);
    }
}
