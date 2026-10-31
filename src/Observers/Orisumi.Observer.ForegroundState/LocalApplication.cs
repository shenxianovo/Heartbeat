using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal sealed record LocalApplication(RecognitionKey Key, IReadOnlyList<ApplicationIdentifier> Identifiers);

/// <summary>Installation clues are stable; missing paths use a key limited to this collector's lifetime.</summary>
internal sealed class ApplicationKeys(string device)
{
    private readonly Dictionary<(int, DateTimeOffset, string?), RecognitionKey> _running = [];
    internal LocalApplication Resolve(ForegroundApplicationReading reading)
    {
        var identifiers = new List<ApplicationIdentifier>();
        if (!string.IsNullOrEmpty(reading.BundleIdentifier)) identifiers.Add(new("macos.bundle-id", reading.BundleIdentifier));
        if (!string.IsNullOrEmpty(reading.ExecutablePath)) identifiers.Add(new("macos.executable-path", reading.ExecutablePath));
        if (!string.IsNullOrEmpty(reading.ExecutablePath))
            return new(new(device, "macos.installation", reading.BundleIdentifier + "\n" + reading.ExecutablePath), identifiers);
        var temporary = new RecognitionKey(device, "macos.process", Guid.NewGuid().ToString());
        if (reading.ProcessId is not > 0 || reading.ProcessStartedAt is null) return new(temporary, identifiers);
        var process = (reading.ProcessId.Value, reading.ProcessStartedAt.Value, reading.BundleIdentifier);
        if (!_running.TryGetValue(process, out var key)) _running[process] = key = temporary;
        return new(key, identifiers);
    }
}
