using System.Globalization;
using System.Runtime.CompilerServices;

namespace Heartbeat.Observers.Sources.Local;

/// <summary>One explicit PID in procfs. Command arguments, environment, memory and credentials are never read.</summary>
public sealed class ProcessSource : IRecordSource
{
    public string Name => "process";
    private static readonly SourceSchema Schema = new(Guid.Parse("019a1280-0000-7000-8000-000000000030"), "process-source-snapshots", "Observed Linux process incarnation",
        SourceFields.WithIdentity(new { pid = SourceFields.Integer(), bootId = SourceFields.String(), startTicks = SourceFields.String(),
            name = SourceFields.String(), state = SourceFields.String(), parentPid = SourceFields.Integer(),
            namespacePids = SourceFields.Array(new { type = "integer", nullable = false }), processNamespace = SourceFields.String(true) }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The process source currently requires Linux procfs.");
        var pid = context.Input == "self" ? Environment.ProcessId : int.TryParse(context.Input, NumberStyles.None, CultureInfo.InvariantCulture, out var requested) ? requested : 0;
        if (pid <= 0) throw new InvalidDataException("A positive PID or self is required.");
        var root = $"/proc/{pid}";
        var before = ParseStat(await File.ReadAllTextAsync(root + "/stat", cancellationToken));
        var boot = (await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id", cancellationToken)).Trim();
        var status = await File.ReadAllLinesAsync(root + "/status", cancellationToken);
        var pidLine = status.FirstOrDefault(line => line.StartsWith("NSpid:", StringComparison.Ordinal));
        var namespacePids = pidLine is null ? [] : pidLine[6..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var pidNamespace = new FileInfo(root + "/ns/pid").LinkTarget;
        var after = ParseStat(await File.ReadAllTextAsync(root + "/stat", cancellationToken));
        if (before.Pid != after.Pid || before.StartTicks != after.StartTicks) throw new InvalidDataException("PID changed incarnation during capture.");
        var key = boot + ":" + pidNamespace + ":" + before.Pid + ":" + before.StartTicks;
        yield return new("linux-process", key, context.CaptureKey, Schema,
            SourceFields.Of(new { pid = before.Pid, bootId = boot, startTicks = before.StartTicks, name = before.Name,
                state = before.State, parentPid = before.ParentPid, namespacePids, processNamespace = pidNamespace }),
            new Dictionary<string, SourceReference>(), context.CapturedAt, context.CapturedAt);
    }

    public static (int Pid, string Name, string State, int ParentPid, string StartTicks) ParseStat(string text)
    {
        var open = text.IndexOf('('); var close = text.LastIndexOf(')');
        if (open <= 0 || close <= open) throw new InvalidDataException("Invalid proc stat header.");
        var rest = text[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 20) throw new InvalidDataException("Truncated proc stat record.");
        return (int.Parse(text[..open].Trim(), CultureInfo.InvariantCulture), text[(open + 1)..close], rest[0],
            int.Parse(rest[1], CultureInfo.InvariantCulture), rest[19]);
    }
}
