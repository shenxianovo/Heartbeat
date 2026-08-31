using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

internal sealed record CollectorRuntimeStopTarget(
    string Description,
    Func<ValueTask<CollectorActivationTerminalResult>> SubmitRuntimeStopping);

internal static class CollectorRuntimeTerminationBatch
{
    internal static async Task StopAllAsync(IReadOnlyList<CollectorRuntimeStopTarget> targets)
    {
        var submissions = new List<(CollectorRuntimeStopTarget Target, Task<CollectorActivationTerminalResult> Terminal)>(
            targets.Count);
        foreach (var target in targets)
        {
            try
            {
                submissions.Add((target, target.SubmitRuntimeStopping().AsTask()));
            }
            catch (Exception exception)
            {
                submissions.Add((target, Task.FromException<CollectorActivationTerminalResult>(exception)));
            }
        }

        var failures = new List<Exception>();
        foreach (var submission in submissions)
        {
            try
            {
                var terminal = await submission.Terminal.ConfigureAwait(false);
                if (!terminal.OwnershipReleased)
                    failures.Add(terminal.ReleaseError ?? new InvalidOperationException(
                        $"{submission.Target.Description} retained Collector Activation ownership."));
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"{submission.Target.Description} terminal transaction faulted.",
                    exception));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException(
                "One or more Collectors did not stop; Runtime ownership is retained.",
                failures);
    }
}
