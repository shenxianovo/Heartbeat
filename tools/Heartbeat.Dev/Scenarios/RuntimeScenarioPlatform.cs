using Heartbeat.Collector.Desktop;
using Heartbeat.Desktop;

namespace Heartbeat.Dev;

// Only OS observations and credential storage are controlled. Projection/collection stays real.
internal sealed class RuntimeScenarioPlatform(string directory) : IDesktopPlatform, ICredentialStore
{
    public static readonly ReplayApplication Application = new("com.heartbeat.regression", "Heartbeat Regression", "heartbeat.collector.desktop.scenario");
    public string CollectorKey => Application.CollectorKey;
    public string DisplayName => Application.DisplayName;
    public string GetTarget() => "controlled-desktop";
    public TimeProvider Clock => TimeProvider.System;
    public ICredentialStore Credentials => this;
    public IDesktopObservationSource CreateObservationSource() => new Source();
    private string CredentialPath => Path.Combine(directory, "scenario-key");

    public string? Read(string account) => File.Exists(CredentialPath) ? File.ReadAllText(CredentialPath) : null;
    public void Write(string account, string secret)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(CredentialPath, options);
        using var writer = new StreamWriter(file);
        writer.Write(secret);
    }

    private sealed class Source : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation { add { } remove { } }
        public DesktopSnapshot Capture() => new(new(new("macos", "bundle_id", Application.Identifier, Application.DisplayName), null),
            [new(ObservationCapability.Application, ObservationState.Available)]);
        public void StartObserving() { }
        public void StopObserving() { }
        public void RefreshCapabilities() { }
        public void Dispose() { }
    }
}
