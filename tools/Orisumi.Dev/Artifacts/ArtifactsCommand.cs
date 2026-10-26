using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

namespace Orisumi.Dev;

internal sealed class ArtifactsCommand(RepositoryContext repository, TextWriter output)
{
    private readonly ArtifactStore _store = new(repository);

    public Command CreateCommand()
    {
        var command = new Command("artifacts", "List or prune verification evidence");
        command.SetAction(parse => new HelpAction().Invoke(parse));
        var list = new Command("list", "List verification runs");
        var listJson = new Option<bool>("--json") { Description = "Print JSON" };
        list.Options.Add(listJson);
        list.SetAction(parse => ListAsync(parse.GetValue(listJson)));
        var prune = new Command("prune", "Preview evidence pruning by the default retention policy; --apply deletes selected runs");
        var apply = new Option<bool>("--apply") { Description = "Delete selected verification runs" };
        var pruneJson = new Option<bool>("--json") { Description = "Print JSON" };
        prune.Options.Add(apply);
        prune.Options.Add(pruneJson);
        prune.SetAction((parse, token) => PruneAsync(parse.GetValue(apply), parse.GetValue(pruneJson), token));
        command.Subcommands.Add(list);
        command.Subcommands.Add(prune);
        return command;
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

    private async Task<int> PruneAsync(bool apply, bool json, CancellationToken cancellationToken)
    {
        var outcome = _store.SelectForPruning(RetentionPolicy.Default);
        if (apply)
        {
            foreach (var candidate in outcome.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _store.Delete(candidate);
            }
        }
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                applied = apply,
                bytes = outcome.Bytes,
                candidates = outcome.Candidates.Select(run => run.Id),
            }, JsonOptions.Indented));
        }
        else
        {
            var size = ArtifactStore.FormatBytes(outcome.Bytes);
            await output.WriteLineAsync(apply
                ? $"Pruned {outcome.Candidates.Count} verification artifact run(s), {size}."
                : $"Would prune {outcome.Candidates.Count} verification artifact run(s), {size}. Add --apply to delete them.");
        }
        return 0;
    }
}
