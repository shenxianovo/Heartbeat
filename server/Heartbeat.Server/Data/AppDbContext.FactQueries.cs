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
    public IQueryable<ActivitySegment> ActivitySegments =>
        from s in Segments
        where EF.Functions.JsonTypeof(s.Payload.RootElement.GetProperty("activityKey")) == "string" &&
            s.Payload.RootElement.GetProperty("activityKey").GetString()!.Trim() != ""
        join c in ApplicationContexts on new { s.OwnerId, Id = s.TargetKind == "application-context" ? s.TargetId : null }
            equals new { c.OwnerId, Id = (long?)c.Id } into contexts
        from context in contexts.DefaultIfEmpty()
        join d in Devices on new { s.OwnerId, Id = s.TargetKind == "device" ? s.TargetId :
            context != null ? (long?)context.DeviceId : s.TargetKind == null ? s.Stream.Subject.DeviceId : null }
            equals new { d.OwnerId, Id = (long?)d.Id } into devices
        from device in devices.DefaultIfEmpty()
        join a in Apps on (context != null ? (long?)context.AppId : s.AppIdentity != null ? s.AppIdentity!.AppId : null)
            equals (long?)a.Id into apps
        from app in apps.DefaultIfEmpty()
        select new ActivitySegment
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
            DeviceId = device != null ? (long?)device.Id : null,
            Device = device,
            AppIdentityId = s.AppIdentityId,
            AppIdentity = s.AppIdentity,
            AppId = app != null ? (long?)app.Id : null,
            App = app,
            IdentityKey = s.Payload.RootElement.GetProperty("activityKey").GetString()!,
            Title = EF.Functions.JsonTypeof(s.Payload.RootElement.GetProperty("title")) == "string"
                ? s.Payload.RootElement.GetProperty("title").GetString() : null,
            Attributes = JsonAttribute(s.Payload, "attributes"),
            Payload = JsonText(s.Payload)
        };

    /// <summary>Only recognized input vocabulary participates in input counts. Other Events remain stored.</summary>
    public IQueryable<InputEvent> InputEvents => Events
        .SelectMany(e => Devices.Where(d => d.OwnerId == e.OwnerId &&
            (e.TargetKind == "device" && e.TargetId == d.Id ||
             e.TargetKind == "application-context" && ApplicationContexts.Any(c => c.OwnerId == e.OwnerId && c.Id == e.TargetId && c.DeviceId == d.Id) ||
             e.TargetKind == null && e.Stream.Subject.DeviceId == d.Id)), (e, device) => new { Event = e, Device = device })
        .Select(e => new
        {
            Event = e.Event,
            e.Device,
            Type = e.Event.Payload.RootElement.GetProperty("eventType").GetString(),
            CodeSet = e.Event.Payload.RootElement.GetProperty("codeSet").GetString(),
            // CASE protects casts even when PostgreSQL reorders filtering of arbitrary JSON.
            Code = EF.Functions.JsonTypeof(e.Event.Payload.RootElement.GetProperty("code")) == "number" &&
                Regex.IsMatch(e.Event.Payload.RootElement.GetProperty("code").GetString()!, "^-?[0-9]{1,5}$")
                ? (int?)e.Event.Payload.RootElement.GetProperty("code").GetInt32() : null
        })
        .Where(x => x.Code >= short.MinValue && x.Code <= short.MaxValue &&
            (x.CodeSet == InputCodeSets.WindowsVirtualKeyV1 || x.CodeSet == InputCodeSets.HeartbeatKeyPositionV1) &&
            (x.Type == "keyDown" && (x.CodeSet == InputCodeSets.WindowsVirtualKeyV1 || PhysicalKeys.Contains(x.Code!.Value)) ||
             x.Type == "mouseButton" && x.Code >= 1 && x.Code <= 3 ||
             x.Type == "mouseScroll" && x.Code >= 1 && x.Code <= 2))
        .Select(x => new InputEvent
        {
            Id = x.Event.Id,
            DeviceId = x.Device.Id,
            Device = x.Device,
            Timestamp = x.Event.Timestamp,
            CodeSet = x.CodeSet!,
            Code = (short)x.Code!.Value,
            EventType = x.Type == "keyDown" ? InputEventType.KeyDown :
                x.Type == "mouseButton" ? InputEventType.MouseButton : InputEventType.MouseScroll
        });
}
