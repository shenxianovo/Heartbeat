#!/usr/bin/env python3
"""Verify historical System ingress loader refuses new journals without changing bytes.

Compiles the exact ingress + snapshot sources from e6564fc and the current checkout
in disposable directories, against the unchanged Hub/Core contracts. No installed
profile, production account, service, or database is opened. Requires .NET 10 and
this repository's restored/buildable Hub project. This is a loader fixture, not
proof that a particular installed Desktop package was upgraded or rolled back.
"""

import pathlib
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parents[1]
BASELINE = "e6564fc296f83df4fa9d32cb91560f3f954cf797"
SOURCES = [
    "collection/desktop/Heartbeat.Collector.System/Collection/SystemCollectorIngressStore.cs",
    "collection/desktop/Heartbeat.Collector.System/Collection/ForegroundSegmentSnapshot.cs",
]
PROGRAM = r'''
using System.Security.Cryptography;
using System.Text.Json;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Core.DTOs.Input;

var mode = args[0];
var root = args[1];
var paths = new[] { "input", "checkpoint", "reset" }
    .Select(name => Path.Combine(root, name, "system-collector-ingress.json")).ToArray();
if (mode == "prepare")
{
    var store = SystemCollectorIngressStore.Open(paths[0], 2);
    store.StageInputEvent(new InputEventItem {
        Id = Guid.Parse("0197ea40-3333-7000-8000-000000000001"),
        EventType = InputEventType.KeyDown, CodeSet = "heartbeat-key-position-v1", Code = 4,
        Timestamp = DateTimeOffset.Parse("2026-08-01T10:00:00.1234567+00:00") });
    var checkpoint = SystemCollectorIngressStore.Open(paths[1], 2);
    // Set the new optional property at the serialization boundary so this same
    // driver compiles with the unmodified historical snapshot source as well.
    var snapshot = JsonSerializer.Deserialize<ForegroundSegmentSnapshot>("""
        {"FactId":"0197ea40-2222-7000-8000-000000000001","Revision":9,"IdentityKey":"system|win:code|draft","AppIdentityKey":"win:code","AppDisplayName":"Code","Title":"draft","Start":"2026-08-01T10:00:00+00:00","End":"2026-08-01T10:02:00+00:00","IsFinal":false,"IsObservation":true}
        """)!;
    checkpoint.StageSegmentBatch([snapshot]);
    checkpoint.AcknowledgeSegmentBatches(checkpoint.PeekSegmentBatches(10));
    var reset = SystemCollectorIngressStore.Open(paths[2], 2);
    for (var index = 0; index < 200; index++)
        reset.AcknowledgeInputDeliveries([]);
    var resetFiles = Directory.GetFiles(Path.GetDirectoryName(paths[2])!);
    if (resetFiles.Length != 1) throw new Exception("Reset fixture was not compacted.");
    foreach (var line in File.ReadLines(resetFiles[0]))
    {
        using var entry = JsonDocument.Parse(line);
        if (entry.RootElement.GetProperty("Kind").GetString() != "reset" ||
            entry.RootElement.GetProperty("SchemaVersion").GetInt32() != 2)
            throw new Exception("Reset fixture is not exclusively native-schema reset records.");
    }
    Console.WriteLine("Current loader produced input, acknowledged active checkpoint, and reset-only journal fixtures.");
}
else if (mode == "reject")
{
    foreach (var path in paths)
    {
        var directory = Path.GetDirectoryName(path)!;
        var before = Fingerprint(directory);
        try
        {
            _ = SystemCollectorIngressStore.Open(path, 2);
            throw new Exception("Historical loader unexpectedly accepted the new journal.");
        }
        catch (JsonException)
        {
            if (!before.SequenceEqual(Fingerprint(directory)))
                throw new Exception("Historical loader changed the journal or emitted recovery files.");
            Console.WriteLine($"Historical loader rejected {Path.GetFileName(directory)}; file set and SHA256 unchanged.");
        }
    }
}
else if (mode == "resume")
{
    var input = SystemCollectorIngressStore.Open(paths[0], 2);
    if (input.PeekInputDeliveries(10).Count != 1) throw new Exception("Input disappeared.");
    var checkpoint = SystemCollectorIngressStore.Open(paths[1], 2);
    checkpoint.RecoverInterruptedSegment(DateTimeOffset.Parse("2026-08-02T10:00:00+00:00"));
    var final = checkpoint.PeekSegmentBatches(10).Single().Snapshots.Single();
    if (final.Revision != 10 || final.End != DateTimeOffset.Parse("2026-08-01T10:02:00+00:00"))
        throw new Exception("Checkpoint continuity changed after rollback rejection.");
    if (SystemCollectorIngressStore.Open(paths[2], 2).HasPending) throw new Exception("Reset resurrected pending data.");
    Console.WriteLine("Current loader resumes all three; checkpoint finalizes at observed boundary with Revision 10.");
}
else throw new Exception("Unknown fixture operation.");

static string[] Fingerprint(string directory) => Directory.GetFiles(directory)
    .Order(StringComparer.Ordinal)
    .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    .ToArray();
'''


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def main():
    hub_project = ROOT / "collection/hub/Heartbeat.Collection.Hub/Heartbeat.Collection.Hub.csproj"
    run("dotnet", "build", str(hub_project), "--nologo", "--verbosity", "quiet")
    with tempfile.TemporaryDirectory(prefix="heartbeat04-ingress-rollback-") as temporary:
        directory = pathlib.Path(temporary)
        for version in ("current", "historical"):
            project = directory / version
            project.mkdir()
            for source in SOURCES:
                target = project / pathlib.Path(source).name
                if version == "current":
                    shutil.copyfile(ROOT / source, target)
                else:
                    target.write_bytes(subprocess.check_output(
                        ["git", "show", f"{BASELINE}:{source}"], cwd=ROOT))
            hub = escape(str(ROOT / "collection/hub/Heartbeat.Collection.Hub/bin/Debug/net10.0/Heartbeat.Collection.Hub.dll"))
            core = escape(str(ROOT / "shared/Heartbeat.Core/bin/Debug/net10.0/Heartbeat.Core.dll"))
            (project / "Fixture.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
                '</PropertyGroup><ItemGroup>'
                f'<Reference Include="Heartbeat.Collection.Hub"><HintPath>{hub}</HintPath></Reference>'
                f'<Reference Include="Heartbeat.Core"><HintPath>{core}</HintPath></Reference>'
                '</ItemGroup></Project>')
            (project / "Program.cs").write_text(PROGRAM)
            run("dotnet", "build", str(project / "Fixture.csproj"), "--nologo", "--verbosity", "quiet")
        fixture_root = str(directory / "data")
        for version, operation in (("current", "prepare"), ("historical", "reject"), ("current", "resume")):
            run("dotnet", str(directory / version / "bin/Debug/net10.0/Fixture.dll"), operation, fixture_root)
        print(f"PASS: exact historical System ingress sources at {BASELINE} reject and preserve all fixtures.")


if __name__ == "__main__":
    main()
