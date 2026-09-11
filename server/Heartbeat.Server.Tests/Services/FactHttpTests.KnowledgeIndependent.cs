using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Entities;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task IndependentObservations_RecapAndQuestionsKeepSourceOptionalAndMatchOnlyActualSource()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var sourceLess = IndependentFact("segment");
        sourceLess.Aspect = "selected-page";
        sourceLess.Result = JsonSerializer.SerializeToElement(new { activityKey = "unattributed-page", title = "Unattributed page" });
        sourceLess.End = sourceLess.Start!.Value.AddHours(1);
        await PostIndependent(http, sourceLess, HttpStatusCode.OK);
        var sourced = IndependentFact("segment");
        sourced.Source = "new-page-collector";
        sourced.Aspect = "selected-page";
        sourced.Result = JsonSerializer.SerializeToElement(new { activityKey = "page-instance", title = "Anchored page", attributes = new { topic = "anchored-page" } });
        sourced.End = sourced.Start!.Value.AddHours(1);
        await PostIndependent(http, sourced, HttpStatusCode.OK);
        var unknown = IndependentFact("segment");
        unknown.Source = "new-page-collector";
        unknown.Result = JsonSerializer.SerializeToElement(new { activityKey = "must-not-be-activity", title = "Unknown result", extra = new[] { 42 } });
        await PostIndependent(http, unknown, HttpStatusCode.OK);

        var rows = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
        Assert.All(rows, row => Assert.Null(row.StreamId));
        Assert.Null(rows.Single(row => row.Id == sourceLess.Id).Source);
        var window = LocalCalendarWindowValidator.ResolveDay(LocalCalendarWindowTestData.UtcDay(sourceLess.Start.Value)).Window!;
        await using var db = CreateDbContext();
        db.CollectorDeclarations.Add(new CollectorDeclaration
        {
            Source = "new-page-collector", Version = 1,
            PayloadJson = JsonSerializer.Serialize(new CollectorDeclarationDto
            {
                Source = "new-page-collector", Version = 1,
                Layers = [new() { Readings = [new() { Name = "identity", From = "attributes.topic" }] }]
            }),
            ReportedAt = window.Start
        });
        await db.SaveChangesAsync();
        var assembler = new DigestAssembler(db);
        var projection = await assembler.AssembleAsync("owner", window);
        Assert.False(projection.IsEmpty);
        Assert.Contains("unattributed-page", projection.Digest);
        Assert.Contains("anchored-page", projection.Digest);
        Assert.DoesNotContain("page-instance", projection.Digest);
        Assert.DoesNotContain("must-not-be-activity", projection.Digest);
        Assert.Equal(window.Start.AddHours(1).UtcDateTime, projection.SegmentWatermarkUtc);
        Assert.Equal(projection.KnowledgeHash, await assembler.ComputeKnowledgeHashAsync("owner", window));

        var fake = new IndependentAsking();
        var service = new QuestionService(db, assembler, fake);
        var questions = await service.GetDailyQuestionsAsync("owner", window);
        var question = Assert.Single(questions.Questions);
        Assert.Equal("anchored-page", question.Matcher.Steps[0].Value);
        Assert.Equal(projection.Digest, fake.Digest);
        var sideEvidence = Assert.Single(question.Observations, o => o.Value == "unattributed-page");
        Assert.Null(sideEvidence.Source);
        Assert.False(sideEvidence.MatchesFingerprint);
        Assert.Equal(3600, sideEvidence.Seconds);
        Assert.Equal(question.Id, Assert.Single((await service.GetDailyQuestionsAsync("owner", window)).Questions).Id);
        Assert.Equal(1, fake.Calls);
        var knowledge = new KnowledgeService(db);
        await knowledge.MuteMatcherAsync("owner", question.Matcher);
        Assert.Empty((await service.GetDailyQuestionsAsync("owner", window)).Questions);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task IndependentDesktopObservation_OldCachesExpireLazilyAndRecapOnlyRegeneratesExplicitly()
    {
        await SeedIndependentOwner();
        await using var app = CreateApplication();
        using var http = IndependentClient(app);
        var fact = IndependentFact("segment");
        fact.Aspect = "desktop-activity";
        fact.Foi = new("machine", "heartbeat.device", "native-desktop");
        fact.Result = JsonSerializer.SerializeToElement(new { activityKey = "native-editor", title = "Writing" });
        fact.End = fact.Start!.Value.AddHours(1);
        await PostIndependent(http, fact, HttpStatusCode.OK);
        var window = LocalCalendarWindowValidator.ResolveDay(LocalCalendarWindowTestData.UtcDay(fact.Start.Value)).Window!;
        await using var db = CreateDbContext();
        db.Recaps.Add(new Recap
        {
            OwnerId = "owner", WindowKey = window.WindowKey.Value, WindowStart = window.Start,
            WindowEndExclusive = window.EndExclusive, Narrative = "previous narrative", GeneratedAt = window.Start,
            // Pre-independent-observation canonical empty knowledge hash for 2026-01-01.
            KnowledgeHash = "B70DA75E2312B0348DD52060F6480A8A8A69A66E9C3565C052DE0AF7B0483A2B",
            SegmentWatermark = fact.End.Value
        });
        db.DailyQuestionSets.Add(new DailyQuestionSet
        {
            OwnerId = "owner", WindowKey = window.WindowKey.Value, WindowStart = window.Start,
            WindowEndExclusive = window.EndExclusive, PayloadVersion = 2, PayloadJson = "[]",
            GeneratedAt = window.Start, SegmentWatermark = fact.End.Value.UtcDateTime
        });
        await db.SaveChangesAsync();
        var assembler = new DigestAssembler(db);
        var generator = new IndependentRecapGenerator();
        var recap = new RecapService(db, generator, assembler);
        var before = await recap.GetDailyRecapAsync("owner", window);
        Assert.True(before.KnowledgeStale);
        Assert.Equal("previous narrative", before.Narrative);
        Assert.Equal(0, generator.Calls);
        var events = new List<RecapStreamEvent>();
        await foreach (var item in recap.GenerateDailyRecapStreamAsync("owner", window)) events.Add(item);
        Assert.Contains(events, item => item.Type == RecapStreamEvent.DoneType);
        Assert.Contains("注意力轨", generator.Digest);
        Assert.Contains("native-editor", generator.Digest);
        Assert.Contains("1小时00分", generator.Digest);
        var after = await recap.GetDailyRecapAsync("owner", window);
        Assert.False(after.KnowledgeStale);
        Assert.Equal("native narrative", after.Narrative);
        Assert.Equal(1, generator.Calls);

        var asking = new IndependentAsking();
        var questions = new QuestionService(db, assembler, asking);
        Assert.Empty((await questions.GetDailyQuestionsAsync("owner", window)).Questions);
        Assert.Equal(1, asking.Calls);
        Assert.Contains("native-editor", asking.Digest);
        Assert.Empty((await questions.GetDailyQuestionsAsync("owner", window)).Questions);
        Assert.Equal(1, asking.Calls);
    }

    private sealed class IndependentRecapGenerator : IRecapGenerator
    {
        public int Calls;
        public string? Digest;
        public string Model => "test";
        public string PromptHash => "test";
        public async IAsyncEnumerable<LlmChunk> GenerateStreamAsync(string digest, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            Digest = digest;
            await Task.CompletedTask;
            yield return LlmChunk.OfContent("native narrative");
        }
    }

    private sealed class IndependentAsking : IAskingGenerator
    {
        public string? Digest;
        public int Calls;
        public Task<IReadOnlyList<AskingCandidate>?> AskAsync(string digest, AskingContext context, CancellationToken ct = default)
        {
            Digest = digest;
            Calls++;
            return Task.FromResult<IReadOnlyList<AskingCandidate>?>(new[] { "anchored-page", "unattributed-page", "must-not-be-activity" }
                .Select(value => new AskingCandidate("What was this?", new MatcherDto
                {
                    Source = "new-page-collector",
                    Steps = [new() { Reading = "identity", Op = MatcherOps.Equal, Value = value }]
                })).ToList());
        }
    }
}
