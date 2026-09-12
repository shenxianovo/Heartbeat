using System.Text;
using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Hub.Tests;

public sealed class QueueFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"heartbeat-hub-{Guid.NewGuid():N}");

    public string DatabasePath => Path.Combine(DirectoryPath, "queue.sqlite");

    public DeliveryDestination Destination { get; init; } = new(new Uri("http://127.0.0.1:1"), Guid.NewGuid());

    public string Token => TokenFor(Destination.OwnerId);

    public RecordOutbox Open(int capacity = 10000) => new(DatabasePath, Destination, capacity);

    public static RecordSnapshot Snapshot(Guid? id = null, int minutes = 1) => new(
        id ?? Guid.CreateVersion7(), DateTimeOffset.Parse("2026-09-12T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-09-12T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddMinutes(minutes), null,
        JsonSerializer.SerializeToElement(new
        {
            device_id = "device-a",
            application = new { platform = "macos", id_kind = "bundle_id", id = "com.apple.finder" },
        }));

    public static CollectorDeclaration Collector(string displayName = "Test Mac") =>
        new("heartbeat.collector.desktop.macos", "device-a", displayName);

    public static TrackDeclaration Track() =>
        new("desktop.application.foreground", 1, "range", "explicit");

    public static HubSubmission Submission(params RecordSnapshot[] records) =>
        new(Collector(), Track(), records);

    public static string TokenFor(Guid owner) =>
        $"eyJhbGciOiJSUzI1NiJ9.{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = owner }))).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.test-signature";

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
