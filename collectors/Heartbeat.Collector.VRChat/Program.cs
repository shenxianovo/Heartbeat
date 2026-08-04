using Heartbeat.Collector.VRChat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using VRChat.API.Client;


// Load .env file. Only used for development, delete in production in the future.
var envFile = FindDotEnv();
if (envFile != null)
{
    LoadDotEnv(envFile);
    Console.WriteLine($"Loaded env: {envFile}");
}

var builder = Host.CreateApplicationBuilder(args);

var config = builder.Configuration.GetSection("VRChat");
var cookieFile = Path.Combine(AppContext.BaseDirectory, ".vrchat-cookies.json");

bool hasSavedCookies = false;

builder.Services.AddSingleton<IVRChat>(_ =>
{
    var appName = config["Application:Name"] ?? "Heartbeat.Collector.VRChat";
    var appVersion = config["Application:Version"] ?? "0.1.0";
    var appContact = config["Application:Contact"] ?? "";

    var b = new VRChatClientBuilder()
        .WithUsername(config["Username"] ?? "")
        .WithPassword(config["Password"] ?? "")
        .WithApplication(name: appName, version: appVersion, contact: appContact);

    var secret = config["TwoFactorSecret"];
    if (!string.IsNullOrEmpty(secret))
        b.WithTwoFactorSecret(secret);

    if (File.Exists(cookieFile))
    {
        try
        {
            var cookies = JsonSerializer.Deserialize<List<CookieRecord>>(File.ReadAllText(cookieFile));
            var auth = cookies?.FirstOrDefault(c => c.Name == "auth")?.Value;
            var tfa = cookies?.FirstOrDefault(c => c.Name == "twoFactorAuth")?.Value;
            if (!string.IsNullOrEmpty(auth))
            {
                hasSavedCookies = true;
                b.WithAuthCookie(auth, tfa ?? "");
                Console.WriteLine("Loaded saved cookies.");
            }
        }
        catch { }
    }

    return b.Build();
});

builder.Services.AddHostedService(sp =>
    new VRChatCollectorService(sp.GetRequiredService<IVRChat>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VRChatCollectorService>>(),
        hasSavedCookies));

var host = builder.Build();
await host.RunAsync();

static string? FindDotEnv()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null)
    {
        var path = Path.Combine(dir, ".env");
        if (File.Exists(path)) return path;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static void LoadDotEnv(string path)
{
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

        var eq = trimmed.IndexOf('=');
        if (eq <= 0) continue;

        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();
        Environment.SetEnvironmentVariable(key, value);
    }
}

record CookieRecord(string Name, string Value);
