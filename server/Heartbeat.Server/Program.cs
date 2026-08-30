using Heartbeat.Server.Data;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var catalogPath = Path.Combine(
    builder.Environment.ContentRootPath, "AppCatalog", "app-catalog.json");
var builtInCatalog = AppCatalogLoader.LoadFile(catalogPath);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
);

builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<ISegmentIngestApplicationService, SegmentIngestApplicationService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<AppService>();
builder.Services.AddScoped<AppIdentityService>();
builder.Services.AddScoped<AppMergeService>();
builder.Services.AddScoped<AppProductReconciliationService>();
builder.Services.AddScoped<AppCatalogOverrideService>();
builder.Services.AddScoped<AppCatalogAdminQueryService>();
builder.Services.AddScoped<AppCatalogExportService>();
builder.Services.AddScoped<AppCatalogStartupService>();
builder.Services.AddSingleton(builtInCatalog);
builder.Services.AddSingleton<AppCatalogRuntimeSnapshot>();
builder.Services.AddScoped<AdminAuthorizationService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<InputEventService>();
builder.Services.AddScoped<RecapService>();
// 生成互斥必须是 singleton（ADR-042 §7 / ADR-044 §3）：锁的状态是"进程内哪些 (owner, WindowKey) 正在生成"，
// scoped 会让每个请求拿到各自的空集合，撞车永远撞不上，409 那条路等于不存在。
builder.Services.AddSingleton<RecapGenerationLock>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<EpisodeService>();
builder.Services.AddScoped<DigestAssembler>();
builder.Services.AddScoped<QuestionService>();
builder.Services.AddScoped<KnowledgeProposalService>();
builder.Services.AddScoped<KnowledgeCommitService>();
builder.Services.Configure<RecapOptions>(builder.Configuration.GetSection(RecapOptions.Section));
builder.Services.Configure<AdministrationOptions>(builder.Configuration.GetSection(AdministrationOptions.Section));
// LLM 传输一处实现（ADR-029 issue 03）：叙事与发问共享 ChatCompletionClient，generator 退成 prompt+解析。
builder.Services.AddHttpClient<ChatCompletionClient>(client =>
{
    // 时限一律交给 CTS（ADR-042 §5）。HttpClient.Timeout 管不住这里想管的事、又会误伤别的：
    // 它覆盖"拿到响应头"以及默认 ResponseContentRead 下的缓冲正文，但流式那条路是拿到头就自己
    // 读流，那段读取不在它管辖内（.NET 10 真 socket 实测，见 ChatCompletionClientTests）；而对
    // 非流式的一次性问答它是唯一上限——一个值同时充当两种语义，改哪边都是错。
    // 于是把它关掉：流式的静默/整段时限由 RecapService 的两层 CTS 施加，非流式的上限由
    // ChatCompletionClient 自己 link 一个 CTS 施加，两条路都可注入、可测。
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddScoped<IRecapGenerator, OpenAiCompatibleRecapGenerator>();
builder.Services.AddScoped<IAskingGenerator, OpenAiCompatibleAskingGenerator>();
builder.Services.AddScoped<IProposalGenerator, OpenAiCompatibleProposalGenerator>();
builder.Services.AddHttpClient("AuthService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["AuthService:Authority"]!);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddHealthChecks();

// 上游 AuthService 签发两种令牌（同一 RSA 密钥/JWKS）：
//   - OIDC access token（Web 用户，authorization code + PKCE）：typ=at+jwt，issuer 带尾斜杠
//   - 会话 JWT（桌面 Agent 经 /api/v1/apikeys/exchange）：typ=JWT，issuer/audience 不带斜杠
// 按 JWT header 的 typ 路由到各自的 scheme，分别精确校验。
const string oidcScheme = "OidcBearer";
const string sessionScheme = "SessionBearer";

var authSection = builder.Configuration.GetSection("AuthService");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "TokenSelector";
        options.DefaultChallengeScheme = "TokenSelector";
    })
    .AddPolicyScheme("TokenSelector", "Selects bearer scheme by JWT typ", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ") ? header["Bearer ".Length..] : null;
            return JwtTypeSniffer.IsOidcAccessToken(token) ? oidcScheme : sessionScheme;
        };
    })
    .AddJwtBearer(oidcScheme, options =>
    {
        options.Authority = authSection["Authority"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;

        // 上游未注册任何 resource，OIDC access token 不带 aud；留配置项以便上游补上后开启
        var oidcAudience = authSection["OidcAudience"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = !string.IsNullOrEmpty(oidcAudience),
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = authSection["OidcIssuer"],
            ValidAudience = oidcAudience,
            ValidTypes = ["at+jwt"],
            NameClaimType = "preferred_username",
            RoleClaimType = "role",
        };

        // aud 缺席的补偿：只接受签发给本应用客户端的令牌，
        // 拒绝同一 IdP 签给其他下游应用的 access token
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var expected = authSection["OidcClientId"];
                var actual = context.Principal?.FindFirst("client_id")?.Value;
                if (!string.IsNullOrEmpty(expected) && actual != expected)
                    context.Fail("Access token was issued to a different client.");
                return Task.CompletedTask;
            },
        };
    })
    .AddJwtBearer(sessionScheme, options =>
    {
        options.Authority = authSection["Authority"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = authSection["Issuer"],
            ValidAudience = authSection["Audience"],
            NameClaimType = "preferred_username",
            RoleClaimType = "role",
        };
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 全环境启动时自动应用迁移（见 ADR-013，取代 ADR-007）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    // NormalizeMatcherIdentity 迁移的 C# 半边：StepsJson canonical 字节只有
    // System.Text.Json 能产（见 KnowledgeIdentityBackfill 注释）。幂等，干净库空转。
    await KnowledgeIdentityBackfill.RunAsync(db);
    // AppIdentity expand 后，system/app 的权威 Matcher 值统一到产品 App.Key；
    // 只重写能唯一解析到既有产品的旧表示，不做启发式产品合并。
    await AppKnowledgeBackfill.RunAsync(db);
    // AddCollectorDeclarations 的种子半边（同理走 C#）：system/browser v1 幂等补插（ADR-030 §4）。
    await SeedDeclarations.SeedAsync(db);
    // 在开始接收请求前验证并记录内置 App Catalog。票 02 在同一启动边界加入映射协调。
    var catalogStartup = scope.ServiceProvider.GetRequiredService<AppCatalogStartupService>();
    await catalogStartup.ApplyAsync(builtInCatalog);
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
