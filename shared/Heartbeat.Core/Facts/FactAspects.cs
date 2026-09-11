using System.Text.Json;

namespace Heartbeat.Core.Facts;

/// <summary>Shared observation contracts. A Collector's Source is independent of these meanings.</summary>
public static class FactAspects
{
    public const string DesktopActivity = "desktop-activity";
    public const string SelectedPage = "selected-page";
    public const string AccountLocation = "account-location";
    public const string Activity = "activity";
    public const string Input = "input";

    public static bool IsValid(string? aspect) => aspect is null ||
        aspect.Length is > 0 and <= 128 && !string.IsNullOrWhiteSpace(aspect) && aspect == aspect.Trim();
}

/// <summary>Only for pre-Aspect uploads/cache records; inventory, owner and removal gates are in docs/architecture/compatibility-debt.md.</summary>
public static class FactAspectCompatibility
{
    public static string? Infer(string source, string kind, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        if (kind == "segment" && result.TryGetProperty("activityKey", out var key) && key.ValueKind == JsonValueKind.String)
            return source switch
            {
                "system" => FactAspects.DesktopActivity,
                "browser" => FactAspects.SelectedPage,
                "vrchat.account" => FactAspects.AccountLocation,
                _ => FactAspects.Activity
            };
        if (kind == "event" && result.TryGetProperty("eventType", out var type) && type.ValueKind == JsonValueKind.String &&
            type.GetString() is "keyDown" or "mouseButton" or "mouseScroll") return FactAspects.Input;
        return null;
    }
}
