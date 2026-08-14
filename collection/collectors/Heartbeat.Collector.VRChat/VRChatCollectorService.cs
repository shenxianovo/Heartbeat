using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VRChat.API.Client;
using VRChat.API.Realtime;

namespace Heartbeat.Collector.VRChat;

public sealed class VRChatCollectorService(IVRChat vrc, ILogger<VRChatCollectorService> logger, bool hasSavedCookies) : BackgroundService
{
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly string CookieFile = Path.Combine(AppContext.BaseDirectory, ".vrchat-cookies.json");

    private SegmentSnapshot? _current;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (hasSavedCookies)
        {
            logger.LogInformation("Resuming with saved cookies (skipping login endpoint)");
        }
        else
        {
            await LoginInteractiveAsync();
        }

        // Initial state — also validates session
        try
        {
            await SyncCurrentState();
        }
        catch (ApiException ex) when (ex.ErrorCode == 401)
        {
            logger.LogWarning("Session expired, need interactive login");
            await LoginInteractiveAsync();
            await SyncCurrentState();
        }

        // WebSocket for real-time location changes
        var authToken = vrc.GetCookies().FirstOrDefault(c => c.Name == "auth")?.Value;
        if (string.IsNullOrEmpty(authToken))
        {
            logger.LogWarning("No auth token available, falling back to REST polling only");
            await PollLoop(ct);
            return;
        }

        var realtime = new VRChatRealtimeClientBuilder()
            .WithAuthToken(authToken)
            .WithAutoReconnect(AutoReconnectMode.OnDisconnect)
            .WithApplication(name: "Heartbeat.Collector.VRChat", version: "0.1.0", contact: "")
            .Build();

        realtime.OnUserLocation += (_, e) =>
        {
            var location = e.Message.Location;
            logger.LogDebug("WebSocket: user-location event, location={Location}", location);
            HandleLocationChange(location);
        };

        realtime.OnConnected += (_, _) =>
            logger.LogInformation("WebSocket connected");

        realtime.OnDisconnected += (_, _) =>
            logger.LogWarning("WebSocket disconnected");

        await realtime.ConnectAsync();
        logger.LogInformation("WebSocket listening for location changes");

        await PollLoop(ct);

        await realtime.DisconnectAsync();
        Flush(DateTimeOffset.UtcNow);
    }

    private async Task LoginInteractiveAsync()
    {
        logger.LogInformation("Logging in interactively...");
        var response = await RetryOn429(() => vrc.Authentication.GetCurrentUserAsync());

        if (response.RequiresTwoFactorAuth is { Count: > 0 } tfa)
        {
            if (tfa.Contains("emailOtp"))
            {
                Console.Write("Email verification code: ");
                var code = Console.ReadLine()?.Trim() ?? "";
                await vrc.Authentication.Verify2FAEmailCodeAsync(new global::VRChat.API.Model.TwoFactorEmailCode(code));
            }
            else if (tfa.Contains("totp"))
            {
                Console.Write("TOTP code: ");
                var code = Console.ReadLine()?.Trim() ?? "";
                await vrc.Authentication.Verify2FAAsync(new global::VRChat.API.Model.TwoFactorAuthCode(code));
            }
        }

        var me = await RetryOn429(() => vrc.Authentication.GetCurrentUserAsync());
        logger.LogInformation("Logged in as {Name}", me.DisplayName);
        PersistCookies();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(FallbackPollInterval, ct);
            try
            {
                await SyncCurrentState();
            }
            catch (ApiException ex) when (ex.ErrorCode == 429)
            {
                logger.LogWarning("Rate limited during poll, will retry next cycle");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Fallback poll failed");
            }
        }
    }

    private async Task SyncCurrentState()
    {
        var user = await vrc.Authentication.GetCurrentUserAsync();
        var location = user.Presence?.World;
        var instanceId = user.Presence?.Instance;

        var worldId = ParseWorldId(location);

        if (worldId == null)
        {
            if (_current != null)
                Flush(DateTimeOffset.UtcNow);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (worldId == _current?.Attributes?.WorldId)
        {
            _current.EndTime = now;
            EmitSnapshot(_current);
            return;
        }

        Flush(now);
        await OpenSegment(worldId, instanceId, now);
    }

    private void HandleLocationChange(string? location)
    {
        var now = DateTimeOffset.UtcNow;
        var worldId = ParseWorldId(location);
        var instanceId = ParseInstanceId(location);

        if (worldId == null)
        {
            Flush(now);
            return;
        }

        if (worldId == _current?.Attributes?.WorldId)
        {
            _current.EndTime = now;
            EmitSnapshot(_current);
            return;
        }

        Flush(now);
        _ = Task.Run(async () =>
        {
            try { await OpenSegment(worldId, instanceId, now); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to open segment for {WorldId}", worldId); }
        });
    }

    private async Task OpenSegment(string worldId, string? instanceId, DateTimeOffset now)
    {
        string? worldName = null;
        try
        {
            var world = await vrc.Worlds.GetWorldAsync(worldId);
            worldName = world.Name;
        }
        catch { }

        _current = new SegmentSnapshot
        {
            Id = Guid.CreateVersion7(),
            IdentityKey = worldId,
            Title = worldName ?? worldId,
            StartTime = now,
            EndTime = now,
            Attributes = new VRChatAttributes
            {
                WorldId = worldId,
                WorldName = worldName,
                InstanceId = instanceId,
            },
        };

        logger.LogInformation("Entered world: {Title} ({WorldId})", _current.Title, worldId);
        EmitSnapshot(_current);
    }

    private void Flush(DateTimeOffset now)
    {
        if (_current == null) return;
        _current.EndTime = now;
        EmitSnapshot(_current);
        logger.LogInformation("Closed segment: {Title}, duration {Duration}",
            _current.Title, _current.EndTime - _current.StartTime);
        _current = null;
    }

    private void EmitSnapshot(SegmentSnapshot seg)
    {
        // TODO: POST to hub
        Console.WriteLine(JsonSerializer.Serialize(seg, JsonOpts));
    }

    private static string? ParseWorldId(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        if (location is "offline" or "private" or "") return null;
        var colon = location.IndexOf(':');
        return colon > 0 ? location[..colon] : (location.StartsWith("wrld_") ? location : null);
    }

    private static string? ParseInstanceId(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        var colon = location.IndexOf(':');
        return colon > 0 ? location[(colon + 1)..] : null;
    }

    private async Task<T> RetryOn429<T>(Func<Task<T>> action)
    {
        while (true)
        {
            try { return await action(); }
            catch (ApiException ex) when (ex.ErrorCode == 429)
            {
                logger.LogWarning("Rate limited, waiting 60s...");
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
        }
    }

    private void PersistCookies()
    {
        try
        {
            var cookies = vrc.GetCookies();
            var data = cookies.Select(c => new CookieRecord(c.Name, c.Value)).ToList();
            File.WriteAllText(CookieFile, JsonSerializer.Serialize(data));
            logger.LogDebug("Cookies saved");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist cookies");
        }
    }

    private record CookieRecord(string Name, string Value);
}
