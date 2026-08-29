using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class AskingHttpTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private readonly FakeAsking _asking = new();
    private readonly FakeProposer _proposer = new();

    protected override async Task SeedAsync(AppDbContext db)
    {
        db.Users.Add(new User { Id = "user-1", Username = "alice" });
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "sometool" };
        db.AddRange(device, app);
        await db.SaveChangesAsync();
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = device.Id,
            Source = ActivitySources.System,
            IdentityKey = "sometool|",
            AppId = app.Id,
            StartTime = DateTimeOffset.Parse("2026-03-08T05:00:00Z"),
            EndTime = DateTimeOffset.Parse("2026-03-08T06:00:00Z"),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task QuestionsAndProposalUseTheSameVerifiedWindowAndRejectDriftOrMissingCaches()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var questions = await client.GetAsync("/api/v1/knowledge/questions?" + NewYorkWindowQuery());
        questions.EnsureSuccessStatusCode();
        var response = JsonDocument.Parse(await questions.Content.ReadAsStringAsync());
        var question = response.RootElement.GetProperty("questions")[0];
        var questionId = question.GetProperty("id").GetGuid();
        var windowKey = question.GetProperty("windowKey").GetString()!;

        using var accepted = await client.PostAsync(
            $"/api/v1/knowledge/questions/{questionId}/propose?{NewYorkWindowQuery()}",
            JsonContent(new { windowKey, answer = "这是实习调研" }));
        accepted.EnsureSuccessStatusCode();

        using var drifted = await client.PostAsync(
            $"/api/v1/knowledge/questions/{questionId}/propose?{UtcWindowQuery()}",
            JsonContent(new { windowKey, answer = "这是实习调研" }));
        Assert.Equal(HttpStatusCode.BadRequest, drifted.StatusCode);
        var driftedError = JsonDocument.Parse(await drifted.Content.ReadAsStringAsync());
        Assert.Equal("question_window_mismatch", driftedError.RootElement.GetProperty("code").GetString());

        var missingWindowKey = ResolveUtcWindow().WindowKey.Value;
        using var missing = await client.PostAsync(
            $"/api/v1/knowledge/questions/{questionId}/propose?{UtcWindowQuery()}",
            JsonContent(new { windowKey = missingWindowKey, answer = "这是实习调研" }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingError = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("question_not_found", missingError.RootElement.GetProperty("code").GetString());

        Assert.Equal(1, _asking.Calls);
        Assert.Equal(1, _proposer.Calls);
    }

    [Fact]
    public async Task AskingRoutesRequireTheCompleteEnvelopeAndRejectRuleMismatchBeforeLlmCalls()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var missing = await client.GetAsync("/api/v1/knowledge/questions?localDate=2026-03-08");
        var mismatch = await client.GetAsync(
            "/api/v1/knowledge/questions?" + NewYorkWindowQuery("2026-03-09T04:00:01Z"));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var error = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
        Assert.Equal("calendar_rules_mismatch", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, _asking.Calls);
        Assert.Equal(0, _proposer.Calls);
    }

    [Fact]
    public async Task OpenApiMarksEveryAskingEnvelopeFieldAsRequired()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var readParameters = json.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/knowledge/questions")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(6, readParameters.Length);
        Assert.All(readParameters, parameter => Assert.True(parameter.GetProperty("required").GetBoolean()));

        var proposalParameters = json.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/knowledge/questions/{id}/propose")
            .GetProperty("post")
            .GetProperty("parameters")
            .EnumerateArray()
            .Where(parameter => parameter.GetProperty("name").GetString() != "id")
            .ToArray();
        Assert.Equal(6, proposalParameters.Length);
        Assert.All(proposalParameters, parameter => Assert.True(parameter.GetProperty("required").GetBoolean()));
    }

    private WebApplicationFactory<KnowledgeController> CreateApplication() =>
        new WebApplicationFactory<KnowledgeController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IAskingGenerator>();
                services.RemoveAll<IProposalGenerator>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
                services.AddSingleton<IAskingGenerator>(_asking);
                services.AddSingleton<IProposalGenerator>(_proposer);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                        options.DefaultScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

    private static StringContent JsonContent(object value) => new(
        JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string NewYorkWindowQuery(string endExclusive = "2026-03-09T04:00:00Z") =>
        "version=1&kind=day&localDate=2026-03-08&timeZone=America%2FNew_York" +
        "&start=2026-03-08T05%3A00%3A00Z&endExclusive=" + Uri.EscapeDataString(endExclusive);

    private static string UtcWindowQuery() =>
        "version=1&kind=day&localDate=2026-03-08&timeZone=UTC" +
        "&start=2026-03-08T00%3A00%3A00Z&endExclusive=2026-03-09T00%3A00%3A00Z";

    private static ResolvedCalendarWindow ResolveUtcWindow() =>
        LocalCalendarWindowValidator.ResolveDay(new LocalCalendarWindowEnvelope
        {
            Version = 1,
            Kind = "day",
            LocalDate = "2026-03-08",
            TimeZone = "UTC",
            Start = DateTimeOffset.Parse("2026-03-08T00:00:00Z"),
            EndExclusive = DateTimeOffset.Parse("2026-03-09T00:00:00Z"),
        }).Window!;

    private sealed class FakeAsking : IAskingGenerator
    {
        public int Calls;

        public Task<IReadOnlyList<AskingCandidate>?> AskAsync(
            string digest, AskingContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<AskingCandidate>?>(
                [new AskingCandidate("这是什么？", new MatcherDto
                {
                    Source = ActivitySources.System,
                    Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = "sometool" }],
                })]);
        }
    }

    private sealed class FakeProposer : IProposalGenerator
    {
        public int Calls;

        public Task<RawKnowledgeProposal?> ProposeAsync(
            AskingQuestionResponse question, string answer, ProposalContext context,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<RawKnowledgeProposal?>(new RawKnowledgeProposal
            {
                Explanation = "这是实习调研",
            });
        }

        public Task<RawKnowledgeProposal?> ProposeCorrectionAsync(
            string digest, string correction, ProposalContext context, CancellationToken ct = default) =>
            Task.FromResult<RawKnowledgeProposal?>(new RawKnowledgeProposal());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims = [new("sub", "user-1"), new("preferred_username", "alice")];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
