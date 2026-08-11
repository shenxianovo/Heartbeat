using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class AppMergeServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static string AppMatcher(string value) => MatcherCodec.Serialize(
        [new MatcherStepDto { Reading = "app", Op = MatcherOps.Equal, Value = value }]);

    private async Task<(App Source, App Target, AppIdentity Win, AppIdentity Mac)> SeedProductsAsync(AppDbContext db)
    {
        var source = new App
        {
            Key = "code",
            DisplayName = "Code.exe",
            IsProvisional = true
        };
        var target = new App
        {
            Key = "vscode",
            DisplayName = "Visual Studio Code"
        };
        var win = new AppIdentity { Key = "win:code", App = source };
        var mac = new AppIdentity { Key = "mac:com.microsoft.vscode", App = target };
        db.AddRange(source, target, win, mac);
        await db.SaveChangesAsync();
        return (source, target, win, mac);
    }

    [Fact]
    public async Task DryRun_IsAccurate_AndDoesNotMutate()
    {
        using var db = CreateDbContext();
        var (source, target, win, _) = await SeedProductsAsync(db);
        var device = new Device
        {
            OwnerId = "owner-1", HardwareId = "hw", DeviceName = "PC",
            CurrentApp = "Code.exe", CurrentAppIdentityId = win.Id
        };
        var strand = new Strand
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", Name = "Work", NormalizedName = "work"
        };
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", LocalDate = new DateOnly(2026, 8, 11), Text = "work"
        };
        db.AddRange(device, strand, episode);
        await db.SaveChangesAsync();

        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = ActivitySources.System,
            IdentityKey = "code|", AppId = source.Id, AppIdentityId = win.Id,
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-1), EndTime = DateTimeOffset.UtcNow
        });
        db.AppIcons.Add(new AppIcon
        {
            OwnerId = "owner-1", AppId = source.Id, IconData = [1], UpdatedAt = DateTimeOffset.UtcNow
        });
        var sourceMatcher = new StrandMatcher
        {
            Id = Guid.CreateVersion7(), StrandId = strand.Id, Source = ActivitySources.System,
            StepsJson = AppMatcher("code.exe")
        };
        var targetMatcher = new StrandMatcher
        {
            Id = Guid.CreateVersion7(), StrandId = strand.Id, Source = ActivitySources.System,
            StepsJson = AppMatcher("vscode")
        };
        db.StrandMatchers.AddRange(sourceMatcher, targetMatcher);
        db.MutedMatchers.Add(new MutedMatcher
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", Source = ActivitySources.System,
            StepsJson = AppMatcher("code"), CreatedAt = DateTimeOffset.UtcNow
        });
        db.RecurrenceProbes.Add(new RecurrenceProbe
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", EpisodeId = episode.Id,
            Source = ActivitySources.System, StepsJson = AppMatcher("win:code"),
            Status = ProbeStatuses.Active, CreatedAt = DateTimeOffset.UtcNow
        });
        var now = DateTimeOffset.UtcNow;
        db.DailyQuestionSets.Add(new DailyQuestionSet
        {
            OwnerId = "owner-1", WindowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            SegmentWatermark = now.UtcDateTime, GeneratedAt = now,
            PayloadVersion = DailyQuestionSet.CurrentPayloadVersion, PayloadJson = "[]"
        });
        await db.SaveChangesAsync();

        var result = await new AppMergeService(db).MergeAsync(new AppMergeRequest
        {
            SourceAppKey = "code", TargetAppKey = "vscode", DryRun = true
        });

        Assert.True(result.DryRun);
        Assert.False(result.Committed);
        Assert.Equal(["win:code"], result.AppIdentityKeys);
        Assert.Equal(1, result.LegacySegmentsRebound);
        Assert.Equal(1, result.CurrentDevicesAffected);
        Assert.Equal(1, result.Knowledge.StrandMatchers);
        Assert.Equal(1, result.Knowledge.MutedMatchers);
        Assert.Equal(1, result.Knowledge.RecurrenceProbes);
        Assert.Equal(1, result.Knowledge.QuestionCachesInvalidated);
        Assert.Collection(
            result.Knowledge.Changes,
            change =>
            {
                Assert.Equal("muted-matcher", change.Category);
                Assert.Equal(AppMatcher("code"), change.BeforeStepsJson);
                Assert.Equal(AppMatcher("vscode"), change.AfterStepsJson);
            },
            change =>
            {
                Assert.Equal("recurrence-probe", change.Category);
                Assert.Equal(AppMatcher("win:code"), change.BeforeStepsJson);
                Assert.Equal(AppMatcher("vscode"), change.AfterStepsJson);
            },
            change =>
            {
                Assert.Equal("strand-matcher", change.Category);
                Assert.Equal(AppMatcher("code.exe"), change.BeforeStepsJson);
                Assert.Equal(AppMatcher("vscode"), change.AfterStepsJson);
            });
        Assert.All(result.Knowledge.Changes, change => Assert.NotEqual(Guid.Empty, change.RowId));
        var deduplication = Assert.Single(result.Knowledge.Deduplications);
        Assert.Equal("strand-matcher", deduplication.Category);
        Assert.Equal(new[] { sourceMatcher.Id, targetMatcher.Id }.Min(), deduplication.KeptRowId);
        Assert.Equal([new[] { sourceMatcher.Id, targetMatcher.Id }.Max()], deduplication.RemovedRowIds);
        Assert.Null(deduplication.KeptStatus);
        Assert.Equal("move-source", Assert.Single(result.Icons).Resolution);
        Assert.Single(result.ProvisionalAppsRemoved);

        Assert.Equal(source.Id, (await db.AppIdentities.SingleAsync(x => x.Key == "win:code")).AppId);
        Assert.Equal(source.Id, (await db.ActivitySegments.SingleAsync()).AppId);
        Assert.True(await db.Apps.AnyAsync(x => x.Id == source.Id));
        Assert.Equal(2, await db.StrandMatchers.CountAsync());
        Assert.Equal("code.exe", MatcherCodec.Deserialize(sourceMatcher.StepsJson)[0].Value);
    }

    [Fact]
    public async Task Commit_ProbeDeduplication_PrefersStrongerTerminalResolution()
    {
        using var db = CreateDbContext();
        var (source, target, _, _) = await SeedProductsAsync(db);
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner", LocalDate = new DateOnly(2026, 8, 11), Text = "work"
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var denied = new RecurrenceProbe
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner", EpisodeId = episode.Id, Source = ActivitySources.System,
            StepsJson = AppMatcher("code"), Status = ProbeStatuses.Denied,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), ResolvedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var promoted = new RecurrenceProbe
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner", EpisodeId = episode.Id, Source = ActivitySources.System,
            StepsJson = AppMatcher("vscode"), Status = ProbeStatuses.Promoted,
            CreatedAt = DateTimeOffset.UtcNow, ResolvedAt = DateTimeOffset.UtcNow
        };
        db.RecurrenceProbes.AddRange(denied, promoted);
        await db.SaveChangesAsync();

        var preview = await new AppMergeService(db).MergeAsync(new AppMergeRequest
        {
            SourceAppKey = source.Key, TargetAppKey = target.Key, DryRun = true
        });
        var deduplication = Assert.Single(preview.Knowledge.Deduplications);
        Assert.Equal("recurrence-probe", deduplication.Category);
        Assert.Equal(promoted.Id, deduplication.KeptRowId);
        Assert.Equal([denied.Id], deduplication.RemovedRowIds);
        Assert.Equal(ProbeStatuses.Promoted, deduplication.KeptStatus);

        await new AppMergeService(db).MergeAsync(new AppMergeRequest
        {
            SourceAppKey = source.Key, TargetAppKey = target.Key, DryRun = false
        });
        var probe = await db.RecurrenceProbes.SingleAsync();
        Assert.Equal(promoted.Id, probe.Id);
        Assert.Equal(ProbeStatuses.Promoted, probe.Status);
    }

    [Fact]
    public async Task ConcurrentMerges_SharingTarget_AreSerialized_AndBothCommitConsistently()
    {
        string connectionString;
        using (var seedDb = CreateDbContext())
        {
            connectionString = seedDb.Database.GetDbConnection().ConnectionString;
            var target = new App { Key = "vscode", DisplayName = "Visual Studio Code" };
            var sourceA = new App { Key = "code", DisplayName = "Code.exe", IsProvisional = true };
            var sourceB = new App { Key = "cursor", DisplayName = "Cursor.exe", IsProvisional = true };
            sourceA.Identities.Add(new AppIdentity { Key = "win:code" });
            sourceB.Identities.Add(new AppIdentity { Key = "win:cursor" });
            target.Identities.Add(new AppIdentity { Key = "mac:com.microsoft.vscode" });
            seedDb.AddRange(target, sourceA, sourceB);
            await seedDb.SaveChangesAsync();
            seedDb.AppIcons.AddRange(
                new AppIcon { OwnerId = "owner", AppId = sourceA.Id, IconData = [1], UpdatedAt = DateTimeOffset.UtcNow },
                new AppIcon { OwnerId = "owner", AppId = sourceB.Id, IconData = [2], UpdatedAt = DateTimeOffset.UtcNow });
            await seedDb.SaveChangesAsync();
        }

        await using var gate = new NpgsqlConnection(connectionString);
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var gateCommand = gate.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText =
                "SELECT pg_advisory_xact_lock(hashtextextended('heartbeat.app-merge' || chr(10) || 'vscode', 0))";
            await gateCommand.ExecuteNonQueryAsync();
        }

        using var dbA = CreateDbContext();
        using var dbB = CreateDbContext();
        var mergeA = new AppMergeService(dbA).MergeAsync(new AppMergeRequest
        {
            SourceAppKey = "code", TargetAppKey = "vscode", DryRun = false
        });
        var mergeB = new AppMergeService(dbB).MergeAsync(new AppMergeRequest
        {
            SourceAppKey = "cursor", TargetAppKey = "vscode", DryRun = false
        });

        var bothWaiting = false;
        await using (var observer = new NpgsqlConnection(connectionString))
        {
            await observer.OpenAsync();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await using var waitCommand = observer.CreateCommand();
                waitCommand.CommandText =
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'";
                bothWaiting = Convert.ToInt32(await waitCommand.ExecuteScalarAsync()) >= 2;
                if (bothWaiting) break;
                await Task.Delay(20);
            }
        }

        await gateTransaction.CommitAsync();
        Assert.True(bothWaiting, "Both merges should wait on the shared target product lock.");
        var results = await Task.WhenAll(mergeA, mergeB);
        Assert.All(results, result => Assert.True(result.Committed));

        using var verify = CreateDbContext();
        Assert.False(await verify.Apps.AnyAsync(x => x.Key == "code" || x.Key == "cursor"));
        var targetId = await verify.Apps.Where(x => x.Key == "vscode").Select(x => x.Id).SingleAsync();
        Assert.Equal(3, await verify.AppIdentities.CountAsync(x => x.AppId == targetId));
        Assert.Single(await verify.AppIcons.Where(x => x.OwnerId == "owner" && x.AppId == targetId).ToListAsync());
        Assert.Equal(2, await verify.AppMergeReceipts.CountAsync());
    }

    [Fact]
    public async Task Commit_RebindsConsumers_ReconcilesIcons_MigratesKnowledge_AndIsIdempotent()
    {
        using var db = CreateDbContext();
        var (source, target, win, mac) = await SeedProductsAsync(db);
        var device = new Device
        {
            OwnerId = "owner-1", HardwareId = "hw", DeviceName = "PC",
            CurrentApp = "Code.exe", CurrentAppIdentityId = win.Id
        };
        var strand = new Strand
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", Name = "Work", NormalizedName = "work"
        };
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", LocalDate = new DateOnly(2026, 8, 11), Text = "work"
        };
        db.AddRange(device, strand, episode);
        await db.SaveChangesAsync();

        db.ActivitySegments.AddRange(
            new ActivitySegment
            {
                Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = ActivitySources.System,
                IdentityKey = "win", AppId = source.Id, AppIdentityId = win.Id,
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-2), EndTime = DateTimeOffset.UtcNow.AddMinutes(-1)
            },
            new ActivitySegment
            {
                Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = ActivitySources.System,
                IdentityKey = "mac", AppId = target.Id, AppIdentityId = mac.Id,
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-1), EndTime = DateTimeOffset.UtcNow
            });
        var targetIcon = new AppIcon
        {
            OwnerId = "owner-1", AppId = target.Id, IconData = [9], UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        db.AppIcons.AddRange(
            targetIcon,
            new AppIcon { OwnerId = "owner-1", AppId = source.Id, IconData = [1], UpdatedAt = DateTimeOffset.UtcNow },
            new AppIcon { OwnerId = "owner-2", AppId = source.Id, IconData = [2], UpdatedAt = DateTimeOffset.UtcNow });

        // 每类都预置 target 谓词，验证 merge 后唯一约束前的领域去重。
        db.StrandMatchers.AddRange(
            new StrandMatcher { Id = Guid.CreateVersion7(), StrandId = strand.Id, Source = "system", StepsJson = AppMatcher("code.exe") },
            new StrandMatcher { Id = Guid.CreateVersion7(), StrandId = strand.Id, Source = "system", StepsJson = AppMatcher("vscode") });
        db.MutedMatchers.AddRange(
            new MutedMatcher { Id = Guid.CreateVersion7(), OwnerId = "owner-1", Source = "system", StepsJson = AppMatcher("code"), CreatedAt = DateTimeOffset.UtcNow },
            new MutedMatcher { Id = Guid.CreateVersion7(), OwnerId = "owner-1", Source = "system", StepsJson = AppMatcher("vscode"), CreatedAt = DateTimeOffset.UtcNow });
        db.RecurrenceProbes.AddRange(
            new RecurrenceProbe { Id = Guid.CreateVersion7(), OwnerId = "owner-1", EpisodeId = episode.Id, Source = "system", StepsJson = AppMatcher("win:code"), Status = ProbeStatuses.Denied, CreatedAt = DateTimeOffset.UtcNow },
            new RecurrenceProbe { Id = Guid.CreateVersion7(), OwnerId = "owner-1", EpisodeId = episode.Id, Source = "system", StepsJson = AppMatcher("vscode"), Status = ProbeStatuses.Active, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var now = DateTimeOffset.UtcNow;
        db.DailyQuestionSets.Add(new DailyQuestionSet
        {
            OwnerId = "owner-1", WindowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            SegmentWatermark = now.UtcDateTime, GeneratedAt = now,
            PayloadVersion = 2, PayloadJson = "[]"
        });
        db.Recaps.Add(new Recap
        {
            OwnerId = "owner-1", WindowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            Narrative = "historical prose mentioning Code.exe", GeneratedAt = now,
            Model = "test", PromptHash = "12345678", SegmentWatermark = now
        });
        await db.SaveChangesAsync();

        var service = new AppMergeService(db);
        var request = new AppMergeRequest { SourceAppKey = "code", TargetAppKey = "vscode", DryRun = false };
        var first = await service.MergeAsync(request);
        db.ChangeTracker.Clear();
        var retry = await service.MergeAsync(request);

        Assert.True(first.Committed);
        Assert.True(retry.AlreadyMerged);
        Assert.Equal(target.Id, (await db.AppIdentities.SingleAsync(x => x.Key == "win:code")).AppId);
        Assert.All(await db.ActivitySegments.ToListAsync(), x => Assert.Equal(target.Id, x.AppId));
        Assert.False(await db.Apps.AnyAsync(x => x.Id == source.Id));
        Assert.Equal("Visual Studio Code", (await db.Devices.SingleAsync()).CurrentApp);

        var icons = await db.AppIcons.OrderBy(x => x.OwnerId).ToListAsync();
        Assert.Equal(2, icons.Count);
        Assert.Equal([9], icons[0].IconData); // target 已有则稳定保留
        Assert.Equal([2], icons[1].IconData); // target 缺席则迁移 source
        Assert.All(icons, x => Assert.Equal(target.Id, x.AppId));

        Assert.Equal("vscode", MatcherCodec.Deserialize((await db.StrandMatchers.SingleAsync()).StepsJson)[0].Value);
        Assert.Equal("vscode", MatcherCodec.Deserialize((await db.MutedMatchers.SingleAsync()).StepsJson)[0].Value);
        var probe = await db.RecurrenceProbes.SingleAsync();
        Assert.Equal("vscode", MatcherCodec.Deserialize(probe.StepsJson)[0].Value);
        Assert.Equal(ProbeStatuses.Denied, probe.Status); // terminal 裁决胜过 active duplicate
        Assert.Empty(await db.DailyQuestionSets.ToListAsync());
        Assert.Equal("historical prose mentioning Code.exe", (await db.Recaps.SingleAsync()).Narrative);
        Assert.Single(await db.AppMergeReceipts.ToListAsync());

        var usage = await new UsageService(db).GetUsageAsync("owner-1", null, null, null);
        Assert.Equal(2, usage.Count);
        Assert.All(usage, x =>
        {
            Assert.Equal("vscode", x.AppKey);
            Assert.Equal("Visual Studio Code", x.AppDisplayName);
        });
        var report = await new ReportService(db).GetDailyReportAsync("owner-1", null, DateTimeOffset.UtcNow);
        Assert.Single(report.Apps);
        Assert.Equal("vscode", report.Apps[0].AppKey);
    }

    [Fact]
    public async Task Commit_DatabaseFailure_RollsBackEveryMutation()
    {
        using var db = CreateDbContext();
        var (source, target, win, _) = await SeedProductsAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reject_app_delete() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'test rollback'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_app_delete BEFORE DELETE ON "Apps"
            FOR EACH ROW EXECUTE FUNCTION reject_app_delete();
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => new AppMergeService(db).MergeAsync(new AppMergeRequest
        {
            SourceAppKey = source.Key, TargetAppKey = target.Key, DryRun = false
        }));
        db.ChangeTracker.Clear();

        Assert.Equal(source.Id, (await db.AppIdentities.SingleAsync(x => x.Id == win.Id)).AppId);
        Assert.True(await db.Apps.AnyAsync(x => x.Id == source.Id));
        Assert.Empty(await db.AppMergeReceipts.ToListAsync());
    }

    [Fact]
    public async Task AdminController_RejectsOrdinarySubject_AllowsConfiguredSubject()
    {
        using var db = CreateDbContext();
        await SeedProductsAsync(db);
        var options = Options.Create(new AdministrationOptions { Subjects = ["admin-sub"] });
        var auth = new AdminAuthorizationService(options);
        var request = new AppMergeRequest { SourceAppKey = "code", TargetAppKey = "vscode", DryRun = true };

        var denied = new AdminAppController(new AppMergeService(db), auth, new FakeCurrentUser("ordinary"));
        Assert.IsType<ForbidResult>((await denied.Merge(request, default)).Result);

        var allowed = new AdminAppController(new AppMergeService(db), auth, new FakeCurrentUser("admin-sub"));
        var action = await allowed.Merge(request, default);
        Assert.True(action.Value!.DryRun);
    }

    [Fact]
    public async Task KnowledgeBackfill_UsesUnambiguousExistingProductAliases_WithoutChangingRecaps()
    {
        using var db = CreateDbContext();
        var app = new App { Key = "clash-for-windows", DisplayName = "Clash for Windows" };
        app.Identities.Add(new AppIdentity { Key = "win:clash for windows" });
        var strand = new Strand
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", Name = "Network", NormalizedName = "network"
        };
        db.AddRange(app, strand);
        await db.SaveChangesAsync();
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(), OwnerId = "owner-1", LocalDate = new DateOnly(2026, 8, 11), Text = "network"
        };
        db.Episodes.Add(episode);
        db.StrandMatchers.AddRange(
            new StrandMatcher
            {
                Id = Guid.CreateVersion7(), StrandId = strand.Id, Source = "system",
                StepsJson = AppMatcher("clash for windows")
            },
            new StrandMatcher
            {
                Id = Guid.CreateVersion7(), StrandId = strand.Id, Source = "browser",
                StepsJson = AppMatcher("clash for windows")
            });
        db.RecurrenceProbes.AddRange(
            new RecurrenceProbe
            {
                Id = Guid.CreateVersion7(), OwnerId = "owner-1", EpisodeId = episode.Id, Source = "system",
                StepsJson = AppMatcher("clash for windows"), Status = ProbeStatuses.Denied,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), ResolvedAt = DateTimeOffset.UtcNow.AddDays(-1)
            },
            new RecurrenceProbe
            {
                Id = Guid.CreateVersion7(), OwnerId = "owner-1", EpisodeId = episode.Id, Source = "system",
                StepsJson = AppMatcher("clash-for-windows"), Status = ProbeStatuses.Promoted,
                CreatedAt = DateTimeOffset.UtcNow, ResolvedAt = DateTimeOffset.UtcNow
            });
        var now = DateTimeOffset.UtcNow;
        db.DailyQuestionSets.Add(new DailyQuestionSet
        {
            OwnerId = "owner-1", WindowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            SegmentWatermark = now.UtcDateTime, GeneratedAt = now, PayloadVersion = 2, PayloadJson = "[]"
        });
        db.Recaps.Add(new Recap
        {
            OwnerId = "owner-1", WindowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
            Narrative = "old prose", GeneratedAt = now, Model = "test", PromptHash = "12345678",
            SegmentWatermark = now
        });
        await db.SaveChangesAsync();

        await AppKnowledgeBackfill.RunAsync(db);
        db.ChangeTracker.Clear();

        var system = await db.StrandMatchers.SingleAsync(x => x.Source == "system");
        var browser = await db.StrandMatchers.SingleAsync(x => x.Source == "browser");
        Assert.Equal("clash-for-windows", MatcherCodec.Deserialize(system.StepsJson)[0].Value);
        Assert.Equal("clash for windows", MatcherCodec.Deserialize(browser.StepsJson)[0].Value);
        Assert.Equal(ProbeStatuses.Promoted, (await db.RecurrenceProbes.SingleAsync()).Status);
        Assert.Empty(await db.DailyQuestionSets.ToListAsync());
        Assert.Equal("old prose", (await db.Recaps.SingleAsync()).Narrative);
    }

    private sealed class FakeCurrentUser(string id) : ICurrentUserService
    {
        public string GetUserId() => id;
        public string? GetUserIdOrNull() => id;
        public string? GetUsernameOrNull() => null;
    }
}
