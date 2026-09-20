using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class ArtifactsCommand(RepositoryContext repository, TextWriter output)
{
    private readonly ArtifactStore _store = new(repository);

    public Command CreateCommand()
    {
        var command = new Command("artifacts", "List, prune, or inventory verification evidence");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        var list = new Command("list", "List verification runs");
        var listJson = new Option<bool>("--json") { Description = "Print JSON" };
        list.Options.Add(listJson);
        list.SetAction(parse => ListAsync(parse.GetValue(listJson)));
        var inventory = new Command("inventory-local", "Write a read-only .local inventory; never delete files");
        var inventoryJson = new Option<bool>("--json") { Description = "Print JSON" };
        inventory.Options.Add(inventoryJson);
        inventory.SetAction((parse, token) => InventoryLocalAsync(parse.GetValue(inventoryJson), token));
        command.Subcommands.Add(list);
        command.Subcommands.Add(CreatePruneCommand());
        command.Subcommands.Add(inventory);
        return command;
    }

    private Command CreatePruneCommand()
    {
        var command = new Command("prune", "Preview evidence pruning; --apply deletes selected runs");
        var keep = RetentionOption("--keep", RetentionPolicy.Default.Keep, "Number of newest runs to keep");
        var failed = RetentionOption("--keep-failed", RetentionPolicy.Default.KeepFailed, "Number of newest failed runs to keep");
        var days = RetentionOption("--older-than-days", (int)RetentionPolicy.Default.OlderThan.TotalDays, "Minimum age in days");
        var apply = new Option<bool>("--apply") { Description = "Delete selected verification runs" };
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        command.Options.Add(keep);
        command.Options.Add(failed);
        command.Options.Add(days);
        command.Options.Add(apply);
        command.Options.Add(json);
        command.SetAction((parse, token) => PruneAsync(PruneOptions.Create(parse.GetValue(keep), parse.GetValue(failed), parse.GetValue(days),
            parse.GetValue(apply), parse.GetValue(json)), token));
        return command;
    }

    private static Option<int> RetentionOption(string name, int defaultValue, string description)
    {
        var option = new Option<int>(name) { DefaultValueFactory = _ => defaultValue, Description = description };
        return option;
    }

    private async Task<int> InventoryLocalAsync(bool json, CancellationToken cancellationToken)
    {
        var run = _store.Create("inventory", "local");
        var report = LocalInventory.Create(repository.Path(".local"));
        var reportPath = Path.Combine(run.Directory, "local-inventory.json");
        await File.WriteAllTextAsync(
            reportPath, JsonSerializer.Serialize(report, JsonOptions.Indented) + Environment.NewLine, cancellationToken);
        await ArtifactStore.WriteManifestAsync(run, new EvidenceManifest(
            run.Id, "inventory", "local", run.CreatedAt, DateTimeOffset.UtcNow, 0, true,
            ["heartbeat-dev artifacts inventory-local"], ["local-inventory.json"],
            ["This is a read-only inventory. No deletion candidates were applied."]), cancellationToken);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { reportPath, report }, JsonOptions.Indented));
        }
        else
        {
            await output.WriteLineAsync($"Inventoried {report.FileCount:N0} files ({LocalInventory.FormatBytes(report.Bytes)}) under .local.");
            await output.WriteLineAsync($"Review before deletion: {reportPath}");
        }
        return 0;
    }

    private async Task<int> ListAsync(bool json)
    {
        var runs = _store.List();
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                root = _store.Root,
                runs = runs.Select(run => new { run.Id, run.Directory, run.CreatedAt }),
            }, JsonOptions.Indented));
        }
        else if (runs.Count == 0)
        {
            await output.WriteLineAsync($"No verification artifacts under {_store.Root}.");
        }
        else
        {
            foreach (var run in runs)
            {
                await output.WriteLineAsync($"{run.CreatedAt:u}  {run.Id}");
            }
        }
        return 0;
    }

    private async Task<int> PruneAsync(PruneOptions options, CancellationToken cancellationToken)
    {
        var outcome = _store.SelectForPruning(options.Policy);
        if (options.Apply)
        {
            foreach (var candidate in outcome.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _store.Delete(candidate);
            }
        }
        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                applied = options.Apply,
                keep = options.Policy.Keep,
                keepFailed = options.Policy.KeepFailed,
                olderThanDays = options.Policy.OlderThan.TotalDays,
                bytes = outcome.Bytes,
                candidates = outcome.Candidates.Select(run => run.Id),
            }, JsonOptions.Indented));
        }
        else
        {
            var size = LocalInventory.FormatBytes(outcome.Bytes);
            await output.WriteLineAsync(options.Apply
                ? $"Pruned {outcome.Candidates.Count} verification artifact run(s), {size}."
                : $"Would prune {outcome.Candidates.Count} verification artifact run(s), {size}. Add --apply to delete them.");
        }
        return 0;
    }

}

internal sealed record PruneOptions(RetentionPolicy Policy, bool Apply, bool Json)
{
    public static PruneOptions Create(int keep, int failed, int days, bool apply, bool json)
    {
        if (keep < 0 || failed < 0 || days < 0 || days > TimeSpan.MaxValue.TotalDays)
            throw new CommandUsageException("Retention counts and days must be non-negative integers within TimeSpan range.");
        return new PruneOptions(new RetentionPolicy(keep, failed, TimeSpan.FromDays(days)), apply, json);
    }
}
