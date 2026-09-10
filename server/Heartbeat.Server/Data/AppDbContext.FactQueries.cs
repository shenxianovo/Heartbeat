using System.Text.RegularExpressions;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Data;

public partial class AppDbContext
{
    public static string JsonText(System.Text.Json.JsonDocument payload) => payload.RootElement.GetRawText();
    public static string? JsonAttribute(System.Text.Json.JsonDocument payload, string key) =>
        payload.RootElement.TryGetProperty(key, out var value) ? value.GetRawText() : null;

    private static readonly int[] PhysicalKeys = Enum.GetValues<InputKeyPosition>().Select(x => (int)x).ToArray();

    /// <summary>Activity vocabulary projected in SQL from the single stored Segment payload.</summary>
    public IQueryable<ActivitySegment> ActivitySegments => Segments
        .Where(s => EF.Functions.JsonTypeof(s.Payload.RootElement.GetProperty("activityKey")) == "string" &&
            s.Payload.RootElement.GetProperty("activityKey").GetString()!.Trim() != "")
        .Select(s => new ActivitySegment
        {
            Id = s.Id,
            ObserverId = s.ObserverId, TargetKind = s.TargetKind, TargetId = s.TargetId,
            OwnerId = s.OwnerId,
            StreamId = s.StreamId,
            FactId = s.FactId,
            Revision = s.Revision,
            Stream = s.Stream,
            Source = s.Source,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            DeviceId = s.TargetKind == "device" ? s.TargetId : s.TargetKind == null ? s.Stream.Subject.DeviceId : null,
            Device = s.TargetKind == "device" ? Devices.FirstOrDefault(d => d.Id == s.TargetId && d.OwnerId == s.OwnerId) : s.TargetKind == null ? s.Stream.Subject.Device : null,
            AppIdentityId = s.AppIdentityId,
            AppIdentity = s.AppIdentity,
            AppId = s.AppIdentity != null ? s.AppIdentity.AppId : null,
            App = s.AppIdentity != null ? s.AppIdentity.App : null,
            IdentityKey = s.Payload.RootElement.GetProperty("activityKey").GetString()!,
            Title = EF.Functions.JsonTypeof(s.Payload.RootElement.GetProperty("title")) == "string"
                ? s.Payload.RootElement.GetProperty("title").GetString() : null,
            Attributes = JsonAttribute(s.Payload, "attributes"),
            Payload = JsonText(s.Payload)
        });

    /// <summary>Only recognized input vocabulary participates in input counts. Other Events remain stored.</summary>
    public IQueryable<InputEvent> InputEvents => Events
        .Where(e => e.TargetKind == "device" && e.TargetId != null || e.TargetKind == null && e.Stream.Subject.DeviceId != null)
        .Select(e => new
        {
            Event = e,
            Type = e.Payload.RootElement.GetProperty("eventType").GetString(),
            CodeSet = e.Payload.RootElement.GetProperty("codeSet").GetString(),
            // CASE protects casts even when PostgreSQL reorders filtering of arbitrary JSON.
            Code = EF.Functions.JsonTypeof(e.Payload.RootElement.GetProperty("code")) == "number" &&
                Regex.IsMatch(e.Payload.RootElement.GetProperty("code").GetString()!, "^-?[0-9]{1,5}$")
                ? (int?)e.Payload.RootElement.GetProperty("code").GetInt32() : null
        })
        .Where(x => x.Code >= short.MinValue && x.Code <= short.MaxValue &&
            (x.CodeSet == InputCodeSets.WindowsVirtualKeyV1 || x.CodeSet == InputCodeSets.HeartbeatKeyPositionV1) &&
            (x.Type == "keyDown" && (x.CodeSet == InputCodeSets.WindowsVirtualKeyV1 || PhysicalKeys.Contains(x.Code!.Value)) ||
             x.Type == "mouseButton" && x.Code >= 1 && x.Code <= 3 ||
             x.Type == "mouseScroll" && x.Code >= 1 && x.Code <= 2))
        .Select(x => new InputEvent
        {
            Id = x.Event.Id,
            DeviceId = x.Event.TargetKind == "device" ? x.Event.TargetId!.Value : x.Event.Stream.Subject.DeviceId!.Value,
            Device = x.Event.TargetKind == "device" ? Devices.First(d => d.Id == x.Event.TargetId && d.OwnerId == x.Event.OwnerId) : x.Event.Stream.Subject.Device!,
            Timestamp = x.Event.Timestamp,
            CodeSet = x.CodeSet!,
            Code = (short)x.Code!.Value,
            EventType = x.Type == "keyDown" ? InputEventType.KeyDown :
                x.Type == "mouseButton" ? InputEventType.MouseButton : InputEventType.MouseScroll
        });
}
