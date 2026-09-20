using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Heartbeat.Collector.Desktop;

namespace Heartbeat.Desktop.Tests;

internal sealed class MemoryCredentials : ICredentialStore
{
    public string? Secret { get; private set; }
    public string? Read(string account) => Secret;
    public void Write(string account, string secret) => Secret = secret;
}

internal sealed class TestPlatform : IDesktopPlatform
{
    public string CollectorKey => "test.desktop";
    public string DisplayName => "Test desktop";
    public string GetTarget() => "test-target";
    public TimeProvider Clock => TimeProvider.System;
    public ICredentialStore Credentials { get; } = new MemoryCredentials();
    public IDesktopObservationSource CreateObservationSource() => new TestSource();
}

internal sealed class TestSource : IDesktopObservationSource
{
    public event Action<DesktopObservation>? Observation { add { } remove { } }
    public DesktopSnapshot Capture() => new(new DesktopActivitySample(
        new ForegroundApplication("test", "bundle_id", "test.application"), null), []);
    public void RefreshCapabilities() { }
    public void StartObserving() { }
    public void StopObserving() { }
    public void Dispose() { }
}

internal sealed class AuthHandler(Guid owner) : HttpMessageHandler
{
    public const string ApiKey = "ak_test_a_full_length_key_for_input_binding_verification";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.RequestUri!.AbsolutePath.EndsWith("/exchange", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (body.GetProperty("apiKey").GetString() != ApiKey)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { sub = owner, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { accessToken = $"header.{payload}.signature", expiresIn = 3600 })) };
    }
}
