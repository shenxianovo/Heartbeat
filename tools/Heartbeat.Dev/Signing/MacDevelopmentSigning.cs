using System.Text.RegularExpressions;

namespace Heartbeat.Dev;

/// <summary>The login keychain owns one persistent identity shared by this user's checkouts.</summary>
internal sealed partial class MacDevelopmentSigning(IProcessRunner runner, TextWriter output, string? keychain = null)
{
    internal const string IdentityName = "Heartbeat Development";
    public string Keychain { get; } = keychain ?? Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");

    public async Task<string> RequireIdentityAsync(CancellationToken token)
    {
        var identity = await FindIdentityAsync(token);
        if (identity is null)
            throw new InvalidOperationException($"'{IdentityName}' is missing or invalid in {Keychain}. Run dotnet run --project tools/Heartbeat.Dev -- signing setup.");
        await output.WriteLineAsync($"Development signing: {IdentityName} ({identity}) in {Keychain}");
        return identity;
    }

    public async Task SetupAsync(CancellationToken token)
    {
        // The lock is outside the checkout, so simultaneous setup in two worktrees cannot rotate identity.
        using var setupLock = new FileStream(Path.Combine(Path.GetDirectoryName(Keychain)!, ".heartbeat-signing-setup.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (await FindIdentityAsync(token) is not null)
        {
            await RequireIdentityAsync(token);
            await output.WriteLineAsync("Reusing the existing identity; no keychain changes.");
            return;
        }
        var certificate = await runner.CaptureAsync("security", ["find-certificate", "-c", IdentityName, Keychain], null, token);
        if (certificate.ExitCode == 0)
            throw new InvalidOperationException($"A certificate named '{IdentityName}' already exists but is not a valid signing identity. Repair its trust, expiry or private key in Keychain Access; setup will not replace it.");
        // errSecItemNotFound (-25300) becomes exit status 44. Other failures must not create a new identity.
        if (certificate.ExitCode != 44) throw new InvalidOperationException(certificate.StdErr.Trim());
        await CreateIdentityAsync(token);
        await RequireIdentityAsync(token);
    }

    private async Task<string?> FindIdentityAsync(CancellationToken token)
    {
        var result = await runner.CaptureAsync("security", ["find-identity", "-v", "-p", "codesigning", Keychain], null, token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr.Trim());
        var identities = IdentityLine().Matches(result.StdOut)
            .Where(match => match.Groups[2].Value == IdentityName)
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Length > 1)
            throw new InvalidOperationException($"Multiple '{IdentityName}' identities exist in {Keychain}. Resolve the ambiguity in Keychain Access; no certificate was changed.");
        return identities.SingleOrDefault();
    }

    private async Task CreateIdentityAsync(CancellationToken token)
    {
        using var files = DevelopmentCertificate.Create();
        await RunSecurityAsync(["import", files.IdentityPath, "-k", Keychain, "-f", "pemseq", "-T", "/usr/bin/codesign"], token);
        await output.WriteLineAsync("Imported development identity. macOS may request permission to trust it for code signing.");
        await RunSecurityAsync(["add-trusted-cert", "-r", "trustRoot", "-p", "codeSign", "-k", Keychain, files.CertificatePath], token);
    }

    private async Task RunSecurityAsync(string[] arguments, CancellationToken token)
    {
        var result = await runner.CaptureAsync("security", arguments, null, token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Development signing setup failed: {result.StdErr.Trim()} Existing keychain items were preserved. Run signing status and inspect Keychain Access before retrying.");
    }

    [GeneratedRegex("^\\s*\\d+\\) ([0-9A-Fa-f]{40}) \\\"([^\\\"]+)\\\"", RegexOptions.Multiline)]
    private static partial Regex IdentityLine();
}
