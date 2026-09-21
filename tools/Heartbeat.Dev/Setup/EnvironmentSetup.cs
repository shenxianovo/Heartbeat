using System.Security.Cryptography;
using System.Text.Json;

namespace Heartbeat.Dev;

/// <summary>Owns the local setup transaction; the real Hub remains the authority for Auth validation.</summary>
internal sealed class EnvironmentSetup(
    RepositoryContext repository, IProcessRunner runner, ISetupInput input, TextWriter output)
{
    private const string AuthDashboard = "https://auth.shenxianovo.com/dashboard/api-keys";

    public async Task RunAsync(CancellationToken token)
    {
        using var configuration = new SetupConfiguration(repository.Path(".env.local"));
        await output.WriteLineAsync("Heartbeat local setup. Ctrl-C cancels without replacing .env.local.");
        if (configuration.Exists && !await ConfirmUpdateAsync(token))
        {
            await output.WriteLineAsync("Kept .env.local unchanged.");
            return;
        }
        await PrepareCredentialsAsync(configuration, token);
        configuration.SaveStaging();

        await output.WriteLineAsync("2/3 Owner verification: build Hub and validate the API key without opening its database.");
        var owner = ReadOwner(await new SetupAuthCheck(repository, runner, output).RunAsync(configuration.StagingPath, token));
        var savedOwner = configuration.Get("HEARTBEAT_OWNER_ID");
        if (!string.IsNullOrEmpty(savedOwner) && (!Guid.TryParse(savedOwner, out var prior) || prior != owner))
            throw new InvalidOperationException("The saved Owner binding is invalid or belongs to another Owner. Stop Hub and explicitly reset its data and Owner binding before changing accounts; .env.local was preserved.");
        configuration.Set("HEARTBEAT_OWNER_ID", owner.ToString());

        await output.WriteLineAsync("3/3 Collector identity");
        foreach (var key in new[] { "HEARTBEAT_COLLECTOR_TARGET", "HEARTBEAT_COLLECTOR_DISPLAY_NAME" })
        {
            var value = await AskAsync(key, configuration.Get(key) ?? Environment.MachineName, secret: false, token);
            configuration.Set(key, value);
        }
        token.ThrowIfCancellationRequested();
        configuration.Commit();
        await output.WriteLineAsync("Setup complete: saved .env.local. API key and Hub token were not printed.");
    }

    private async Task PrepareCredentialsAsync(SetupConfiguration configuration, CancellationToken token)
    {
        await output.WriteLineAsync($"1/3 Auth API key: visit {AuthDashboard} and create or reuse a key.");
        if (configuration.Get("HEARTBEAT_API_KEY") is null)
        {
            try { runner.OpenApplication(AuthDashboard); }
            catch (System.ComponentModel.Win32Exception) { await output.WriteLineAsync("Open the Auth dashboard URL above in your browser."); }
        }
        var apiKey = await AskAsync("API key", configuration.Get("HEARTBEAT_API_KEY"), secret: true, token);
        configuration.Set("HEARTBEAT_API_KEY", apiKey);
        configuration.Set("AUTH_AUTHORITY", configuration.Get("AUTH_AUTHORITY") ?? "https://auth.shenxianovo.com");
        var hubToken = configuration.Get("HEARTBEAT_HUB_TOKEN");
        if (hubToken is null || hubToken.Length < 32 || hubToken == apiKey)
            hubToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        configuration.Set("HEARTBEAT_HUB_TOKEN", hubToken);
    }

    private async Task<string> AskAsync(string name, string? current, bool secret, CancellationToken token)
    {
        while (true)
        {
            var hint = string.IsNullOrEmpty(current) ? "" : " [Enter keeps saved/default value]";
            var value = await input.ReadAsync(name + hint + ":", secret, token);
            if (value.Length == 0) value = current ?? "";
            if (!secret) value = value.Trim();
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains('\n') && !value.Contains('\r') && (secret || value.Length <= 255))
                return value;
            await output.WriteLineAsync(secret ? "A non-empty, single-line API key is required." : "Use 1–255 characters on one line.");
        }
    }

    private async Task<bool> ConfirmUpdateAsync(CancellationToken token)
    {
        await output.WriteLineAsync("Existing .env.local found. Continuing will revalidate Auth; Enter keeps saved values at each field.");
        while (true)
        {
            var answer = (await input.ReadAsync("Update existing configuration? [y/N]:", false, token)).Trim().ToUpperInvariant();
            if (answer is "Y" or "YES") return true;
            if (answer is "" or "N" or "NO") return false;
            await output.WriteLineAsync("Enter y to update, or n to keep .env.local unchanged.");
        }
    }

    private static Guid ReadOwner(string result)
    {
        try
        {
            using var json = JsonDocument.Parse(result);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Count() == 1 &&
                json.RootElement.TryGetProperty("ownerId", out var owner) && owner.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(owner.GetString(), "D", out var id) && id != Guid.Empty) return id;
        }
        catch (JsonException) { }
        throw new InvalidOperationException("Hub returned an invalid authentication result; .env.local was preserved.");
    }
}
