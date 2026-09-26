namespace Heartbeat.Dev;

// Parsed once per isolated run; secrets are never part of evidence or diagnostic ToString output.
internal sealed class ScenarioConfiguration(DotenvFile settings)
{
    public Guid Owner { get; } = Guid.Parse(settings.Get("HEARTBEAT_OWNER_ID")
        ?? throw new InvalidOperationException("Run env setup to configure the scenario Owner."));
    public string ApiKey { get; } = !string.IsNullOrWhiteSpace(settings.Get("HEARTBEAT_API_KEY"))
        ? settings.Get("HEARTBEAT_API_KEY")! : throw new InvalidOperationException("Run env setup to configure an Auth API key.");
    public string Authority { get; } = settings.Get("AUTH_AUTHORITY") ?? "https://auth.shenxianovo.com";
    public string ClientId { get; } = settings.Get("AUTH_OIDC_CLIENT_ID") ?? "heartbeat-web";
}

internal sealed class ComposeService(string name)
{
    public string Name { get; } = name;
    public static ComposeService Database { get; } = new("db");
    public static ComposeService Api { get; } = new("api");
    public static ComposeService Web { get; } = new("web");
    public static ComposeService Hub { get; } = new("hub");
}
