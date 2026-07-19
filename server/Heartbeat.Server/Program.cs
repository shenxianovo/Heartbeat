using Heartbeat.Server.Data;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
);

builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<AppService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<InputEventService>();
builder.Services.AddScoped<RecapService>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<QuestionService>();
builder.Services.Configure<RecapOptions>(builder.Configuration.GetSection(RecapOptions.Section));
builder.Services.AddHttpClient<IRecapGenerator, OpenAiCompatibleRecapGenerator>();
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
const string OidcScheme = "OidcBearer";
const string SessionScheme = "SessionBearer";

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
            return JwtTypeSniffer.IsOidcAccessToken(token) ? OidcScheme : SessionScheme;
        };
    })
    .AddJwtBearer(OidcScheme, options =>
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
    .AddJwtBearer(SessionScheme, options =>
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
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
