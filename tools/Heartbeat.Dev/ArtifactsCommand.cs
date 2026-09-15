using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class ArtifactsCommand(RepositoryContext repository, TextWriter output)
{
    private readonly ArtifactStore _store = new(repository);

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            await output.WriteLineAsync("""
                Usage: heartbeat-dev artifacts <list|prune|inventory-local> [options]

                list options:  --json
                prune options: --keep N --older-than-days N --apply --json
                Pruning is a dry run unless --apply is present.
                inventory-local writes a read-only .local inventory; it never deletes files.
                """);
            return 0;
        }

        return args[0] switch
        {
            "list" => await ListAsync(args[1..]),
            "prune" => await PruneAsync(args[1..], cancellationToken),
            "inventory-local" => await InventoryLocalAsync(args[1..], cancellationToken),
            _ => throw new CommandUsageException($"Unknown artifacts action '{args[0]}'."),
        };
    }

    private async Task<int> InventoryLocalAsync(string[] args, CancellationToken cancellationToken)
    {
        var json = ParseJsonOnly(args);
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

    private async Task<int> ListAsync(string[] args)
    {
        var json = ParseJsonOnly(args);
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

    private async Task<int> PruneAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = PruneOptions.Parse(args);
        var candidates = _store.SelectForPruning(options.Keep, TimeSpan.FromDays(options.Days));
        if (options.Apply)
        {
            foreach (var candidate in candidates)
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
                keep = options.Keep,
                olderThanDays = options.Days,
                candidates = candidates.Select(run => run.Id),
            }, JsonOptions.Indented));
        }
        else
        {
            await output.WriteLineAsync(options.Apply
                ? $"Pruned {candidates.Count} verification artifact run(s)."
                : $"Would prune {candidates.Count} verification artifact run(s). Add --apply to delete them.");
        }
        return 0;
    }

    private static bool ParseJsonOnly(string[] args)
    {
        if (args.Length == 0) return false;
        if (args.Length == 1 && args[0] == "--json") return true;
        throw new CommandUsageException($"Unknown artifacts list option '{args[0]}'.");
    }

}

internal sealed record PruneOptions(int Keep, int Days, bool Apply, bool Json)
{
    public static PruneOptions Parse(IReadOnlyList<string> args)
    {
        var keep = 20;
        var days = 14;
        var apply = false;
        var json = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--keep": keep = ParseNonNegative(args, ref index, "--keep"); break;
                case "--older-than-days": days = ParseNonNegative(args, ref index, "--older-than-days"); break;
                case "--apply": apply = true; break;
                case "--json": json = true; break;
                default: throw new CommandUsageException($"Unknown artifacts prune option '{args[index]}'.");
            }
        }
        return new PruneOptions(keep, days, apply, json);
    }

    private static int ParseNonNegative(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || !int.TryParse(args[index], out var result) || result < 0)
            throw new CommandUsageException($"{option} requires a non-negative integer.");
        return result;
    }
}
