using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Filters;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class StrictIngestProtocolTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("garbage")]
    public async Task ProtocolFilter_MissingOrWrongVersion_ReturnsStable426BeforeEndpoint(string? version)
    {
        using var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        if (version != null)
            httpContext.Request.Headers[HeartbeatProtocol.VersionHeader] = version;

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var executingContext = new ResourceExecutingContext(
            actionContext,
            [],
            []);
        var endpointCalled = false;

        await new RequireHeartbeatProtocolAttribute().OnResourceExecutionAsync(
            executingContext,
            () =>
            {
                endpointCalled = true;
                throw new InvalidOperationException("The endpoint must not run for an incompatible protocol.");
            });

        Assert.False(endpointCalled);
        var result = Assert.IsType<ObjectResult>(executingContext.Result);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", httpContext.Response.Headers.Upgrade);

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(HeartbeatProtocol.UpdateRequiredCode, payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(HeartbeatProtocol.RequiredVersion, payload.RootElement.GetProperty("requiredVersion").GetString());

        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task ProtocolFilter_RequiredVersion_AllowsEndpoint()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HeartbeatProtocol.VersionHeader] = HeartbeatProtocol.RequiredVersion;
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var executingContext = new ResourceExecutingContext(actionContext, [], []);
        var endpointCalled = false;

        await new RequireHeartbeatProtocolAttribute().OnResourceExecutionAsync(
            executingContext,
            () =>
            {
                endpointCalled = true;
                return Task.FromResult(new ResourceExecutedContext(actionContext, []));
            });

        Assert.True(endpointCalled);
        Assert.Null(executingContext.Result);
    }

    [Fact]
    public async Task SegmentUpload_EmptyLegacyField_RejectsWholeMixedBatchWithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var request = new SegmentUploadRequest
        {
            Segments =
            [
                StrictSystemSegment("win:code", Now.AddMinutes(-4), Now.AddMinutes(-3)),
                new ActivitySegmentItem
                {
                    Id = Guid.CreateVersion7(),
                    Source = ActivitySources.System,
                    IdentityKey = "win:legacy\nLegacy",
                    AppIdentityKey = "win:legacy",
                    AppDisplayName = "Legacy",
                    AppName = "",
                    StartTime = Now.AddMinutes(-2),
                    EndTime = Now.AddMinutes(-1)
                }
            ]
        };

        var result = Assert.IsType<ObjectResult>(await controller.Upload(request));

        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", controller.Response.Headers.Upgrade);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task PresenceUpload_EmptyLegacyField_Returns426WithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateDeviceController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<ObjectResult>(await controller.Upload(new DeviceStatusRequest
        {
            CurrentAppIdentityKey = "win:code",
            CurrentAppDisplayName = "Visual Studio Code",
            CurrentApp = ""
        }));

        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", controller.Response.Headers.Upgrade);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task IconUpload_EmptyLegacyField_Returns426WithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateAppController(db, "user-1");

        var result = Assert.IsType<ObjectResult>(await controller.UploadIcon(new IconUploadRequest
        {
            AppIdentityKey = "win:code",
            AppDisplayName = "Visual Studio Code",
            AppName = "",
            IconData = [1, 2, 3]
        }));

        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", controller.Response.Headers.Upgrade);
        await AssertNoIngestFactsAsync(db);
        Assert.Empty(await db.AppIcons.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SegmentUpload_MalformedV2SystemIdentity_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var segment = StrictSystemSegment("sys:", Now.AddMinutes(-2), Now.AddMinutes(-1));

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(new SegmentUploadRequest
        {
            Segments = [segment]
        }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_MissingV2SystemIdentity_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var segment = StrictSystemSegment("win:code", Now.AddMinutes(-2), Now.AddMinutes(-1));
        segment.AppIdentityKey = null;

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(new SegmentUploadRequest
        {
            Segments = [segment]
        }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task PresenceUpload_DisplayHintWithoutIdentity_Returns400BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateDeviceController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<BadRequestObjectResult>(await controller.Upload(new DeviceStatusRequest
        {
            CurrentAppDisplayName = "Visual Studio Code"
        }));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task IconUpload_MissingIdentity_Returns400WithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateAppController(db, "user-1");

        var result = Assert.IsType<BadRequestObjectResult>(await controller.UploadIcon(new IconUploadRequest
        {
            AppDisplayName = "Visual Studio Code",
            IconData = [1, 2, 3]
        }));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
        Assert.Empty(await db.AppIcons.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InputUpload_MissingCodeSet_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateInputController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new InputEventUploadRequest
            {
                Events =
                [
                    new InputEventItem
                    {
                        Id = Guid.CreateVersion7(),
                        EventType = InputEventType.KeyDown,
                        Code = 65,
                        Timestamp = Now
                    }
                ]
            }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task InputUpload_UnknownCodeSet_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateInputController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new InputEventUploadRequest
            {
                Events =
                [
                    new InputEventItem
                    {
                        Id = Guid.CreateVersion7(),
                        EventType = InputEventType.KeyDown,
                        CodeSet = "future-v9",
                        Code = 4,
                        Timestamp = Now
                    }
                ]
            }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_UnknownValidIdentity_CreatesProvisionalAppForResolvedOwnerDevice()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var segment = StrictSystemSegment("mac:com.example.focus", Now.AddMinutes(-2), Now.AddMinutes(-1));
        segment.AppDisplayName = "Focus";

        var result = await controller.Upload(new SegmentUploadRequest { Segments = [segment] });

        Assert.IsType<OkResult>(result);
        var device = await db.Devices.AsNoTracking().SingleAsync();
        Assert.Equal("user-1", device.OwnerId);
        Assert.Equal("shared-hardware", device.HardwareId);

        var identity = await db.AppIdentities.Include(x => x.App).AsNoTracking().SingleAsync();
        Assert.Equal("mac:com.example.focus", identity.Key);
        Assert.True(identity.App.IsProvisional);
        Assert.Equal("Focus", identity.App.DisplayName);

        var stored = await db.ActivitySegments.AsNoTracking().SingleAsync();
        Assert.Equal(device.Id, stored.DeviceId);
        Assert.Equal(identity.Id, stored.AppIdentityId);
    }

    [Fact]
    public async Task CollectorAppHint_FlowsThroughStrictUploadIntoReplayAppDimension()
    {
        var hubBuffer = new SegmentIngestService(new FixedClock(Now));
        var loopback = new SegmentIngestRequestHandler(
            hubBuffer,
            new EnabledCollectorRegistry(),
            new EdgeHintResolver());
        var start = Now.AddMinutes(-2);
        var id = Guid.CreateVersion7();
        var body = new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {"segments":[{
              "id":"{{id}}",
              "source":"browser",
              "identityKey":"https://example.com/work",
              "appHint":"edge",
              "title":"Example",
              "startTime":"{{start:O}}",
              "endTime":"{{start.AddMinutes(1):O}}",
              "attributes":{"url":"https://example.com/work?q=1"}
            }]}
            """));

        var loopbackResult = await loopback.HandleAsync("POST", "/v1/segments", body);

        Assert.Equal(200, loopbackResult.StatusCode);
        var strictSegment = Assert.Single(hubBuffer.GetAndClearSegments());
        Assert.Equal("win:msedge", strictSegment.AppIdentityKey);
        Assert.Null(strictSegment.AppName);

        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "desktop-1");
        var uploadResult = await controller.Upload(new SegmentUploadRequest
        {
            Segments = [strictSegment]
        });

        Assert.IsType<OkResult>(uploadResult);
        var identity = await db.AppIdentities.Include(x => x.App).AsNoTracking().SingleAsync();
        Assert.Equal("win:msedge", identity.Key);
        var evidence = Assert.Single(await new UsageService(db).GetSegmentsAsync(
            "user-1", null, ActivitySources.Browser, identity.AppId, start.AddMinutes(-1), Now));
        Assert.Equal(id, evidence.Id);
        Assert.Equal(identity.AppId, evidence.AppId);
        Assert.Equal("https://example.com/work", evidence.IdentityKey);
    }

    [Fact]
    public async Task SegmentUpload_SameHardwareForDifferentOwners_RemainsDeviceIsolated()
    {
        using var db = CreateDbContext();
        var first = CreateSegmentController(db, "user-1", "shared-hardware");
        var second = CreateSegmentController(db, "user-2", "shared-hardware");

        var firstSegment = StrictSystemSegment("win:code", Now.AddMinutes(-4), Now.AddMinutes(-3));
        var secondSegment = StrictSystemSegment("win:code", Now.AddMinutes(-2), Now.AddMinutes(-1));

        Assert.IsType<OkResult>(await first.Upload(new SegmentUploadRequest { Segments = [firstSegment] }));
        Assert.IsType<OkResult>(await second.Upload(new SegmentUploadRequest { Segments = [secondSegment] }));

        var devices = await db.Devices.AsNoTracking().OrderBy(x => x.OwnerId).ToListAsync();
        Assert.Equal(2, devices.Count);
        Assert.Equal(["user-1", "user-2"], devices.Select(x => x.OwnerId).ToArray());
        Assert.All(devices, x => Assert.Equal("shared-hardware", x.HardwareId));

        var segments = await db.ActivitySegments.AsNoTracking().ToListAsync();
        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, x => x.Id == firstSegment.Id && x.DeviceId == devices[0].Id);
        Assert.Contains(segments, x => x.Id == secondSegment.Id && x.DeviceId == devices[1].Id);
    }

    private static ActivitySegmentItem StrictSystemSegment(
        string identityKey,
        DateTimeOffset start,
        DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = ActivitySources.System,
        IdentityKey = identityKey + "\nWindow",
        AppIdentityKey = identityKey,
        AppDisplayName = "Window",
        StartTime = start,
        EndTime = end
    };

    private static SegmentController CreateSegmentController(
        AppDbContext db,
        string userId,
        string hardwareId)
    {
        var controller = new SegmentController(
            new UsageService(db),
            new DeviceService(db),
            new FakeCurrentUser(userId));
        AttachHttpContext(controller, hardwareId);
        return controller;
    }

    private static DeviceController CreateDeviceController(
        AppDbContext db,
        string userId,
        string hardwareId)
    {
        var controller = new DeviceController(new DeviceService(db), new FakeCurrentUser(userId));
        AttachHttpContext(controller, hardwareId);
        return controller;
    }

    private static AppController CreateAppController(AppDbContext db, string userId)
    {
        var controller = new AppController(new AppService(db), new FakeCurrentUser(userId));
        AttachHttpContext(controller);
        return controller;
    }

    private static InputEventController CreateInputController(
        AppDbContext db,
        string userId,
        string hardwareId)
    {
        var controller = new InputEventController(
            new InputEventService(db),
            new DeviceService(db),
            new FakeCurrentUser(userId));
        AttachHttpContext(controller, hardwareId);
        return controller;
    }

    private static void AttachHttpContext(ControllerBase controller, string? hardwareId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[HeartbeatProtocol.VersionHeader] = HeartbeatProtocol.RequiredVersion;
        if (hardwareId != null)
            context.Request.Headers[DeviceService.HardwareIdHeader] = hardwareId;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private static async Task AssertNoIngestFactsAsync(AppDbContext db)
    {
        Assert.Empty(await db.Devices.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Apps.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AppIdentities.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ActivitySegments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.InputEvents.AsNoTracking().ToListAsync());
    }

    private sealed class FakeCurrentUser(string userId) : ICurrentUserService
    {
        public string GetUserId() => userId;
        public string? GetUserIdOrNull() => userId;
        public string? GetUsernameOrNull() => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class EnabledCollectorRegistry : ICollectorRegistry
    {
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; } =
            new Dictionary<string, CollectorRegistration>();
        public CollectorRegistration Touch(string source, int? flushPeriodMs = null) =>
            new(true, flushPeriodMs, null, null);
        public void Discover(IEnumerable<string> sources) { }
        public void StoreDeclaration(string source, string declarationJson, int version) { }
    }

    private sealed class EdgeHintResolver : ICollectorAppHintResolver
    {
        public CollectorAppHintResolution Resolve(string appHint) =>
            appHint == "edge"
                ? CollectorAppHintResolution.Resolved("win:msedge")
                : CollectorAppHintResolution.Unknown;
    }
}
