using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;

namespace Orisumi.Dev;

internal sealed class ArtifactsCommand(RepositoryContext repository, TextWriter output)
{
    private readonly ArtifactStore _store = new(repository);

    public Command CreateCommand()
    {
        var command = new Command("runs", "List or prune verification and analysis records for this checkout");
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
            return 0;
        }
        var console = Terminal.Create(output);
        if (runs.Count == 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Waiting)} No runs under {Terminal.Esc(Terminal.Path(repository, _store.Root))}");
            return 0;
        }
        var size = ArtifactStore.FormatBytes(runs.Sum(ArtifactStore.Bytes));
        Terminal.Heading(console, "Runs", $"{runs.Count} kept", Terminal.Esc(size));
        var now = DateTimeOffset.Now;
        Terminal.Rows(console, runs.Select(run => Describe(ArtifactStore.Summarize(run), now)), 3);
        console.WriteLine();
        console.MarkupLine($"  [grey]in {Terminal.Esc(Terminal.Path(repository, _store.Root))}/; --json lists each directory[/]");
        return 0;
    }

    private static string[] Describe(RunSummary run, DateTimeOffset now)
    {
        var outcome = run.ExitCode is { } code ? Terminal.FromExitCode(code) : Outcome.Waiting;
        var result = outcome switch
        {
            Outcome.Passed => "passed",
            Outcome.Cancelled => "cancelled",
            Outcome.Failed => "failed",
            _ => "unfinished",
        };
        return
        [
            When(run.Run.CreatedAt, now),
            Terminal.Esc(run.What),
            $"{Terminal.Mark(outcome)} {Terminal.Verdict(outcome, result)}",
            run.Duration is { } duration ? Terminal.Duration(duration) : "",
            $"[grey]{Terminal.Esc(string.Join(", ", run.Failed))}[/]",
        ];
    }

    private static string When(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        var days = (now.Date - local.Date).Days;
        var day = days switch
        {
            0 => "today",
            1 => "yesterday",
            _ => local.ToString("MMM d", CultureInfo.InvariantCulture),
        };
        return $"{day,-9} {local:HH:mm}";
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
            return 0;
        }
        var console = Terminal.Create(output);
        var policy = RetentionPolicy.Default;
        var kept = $"[grey]keeps the newest {policy.Keep}, the newest {policy.KeepFailed} failed and everything from the last {policy.OlderThan.TotalDays:0} days[/]";
        if (outcome.Candidates.Count == 0)
        {
            console.MarkupLine($"{Terminal.Mark(Outcome.Passed)} Nothing to prune{Terminal.Dot}{kept}");
            return 0;
        }
        var size = Terminal.Esc(ArtifactStore.FormatBytes(outcome.Bytes));
        console.MarkupLine(apply
            ? $"{Terminal.Mark(Outcome.Passed)} Pruned {outcome.Candidates.Count} runs, {size}{Terminal.Dot}{kept}"
            : $"{Terminal.Mark(Outcome.Waiting)} Would prune {outcome.Candidates.Count} runs, {size}{Terminal.Dot}{kept}");
        if (!apply) Terminal.Hint(console, "orisumi runs prune --apply");
        return 0;
    }
}
