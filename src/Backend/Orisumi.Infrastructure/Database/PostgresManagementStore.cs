using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orisumi.Application.Management;
using Orisumi.Core;

namespace Orisumi.Infrastructure.Database;

public sealed class PostgresManagementStore(NpgsqlDataSource source, TimeProvider clock) : IManagementStore
{
    public Task<IReadOnlyList<ObserverHost>> HostsAsync(CancellationToken token) => ExecuteAsync<IReadOnlyList<ObserverHost>>(async db =>
        (await db.Set<StoredHost>().OrderBy(row => row.HostId).ToListAsync(token)).Select(Host).ToArray(), token);

    public Task<ObserverHost> HostAsync(Guid id, CancellationToken token) => ExecuteAsync(async db => Host(await FindHost(db, id, token)), token);

    public Task<ObserverHost> AuthenticateAsync(string hash, CancellationToken token) => ExecuteAsync(async db =>
    {
        var row = await db.Set<StoredHost>().SingleOrDefaultAsync(row => row.CredentialHash == hash, token)
            ?? throw new ManagementException(401, "credential_invalid", "Unknown host credential.");
        RequireActive(row);
        var now = VersionTime.Normalize(clock.GetUtcNow());
        if (row.LastSeenAt is null || now - row.LastSeenAt >= TimeSpan.FromMinutes(1)) row.LastSeenAt = now;
        return Host(row);
    }, token);

    public Task<Guid> PairAsync(Guid? id, string name, string kind, string hash, CancellationToken token) => ExecuteAsync(async db =>
    {
        var row = id is { } existing ? await FindHost(db, existing, token) : new StoredHost
        { HostId = Guid.CreateVersion7(), Name = name, Kind = kind };
        if (id is null) db.Add(row);
        row.CredentialHash = hash;
        row.Revoked = false;
        return row.HostId;
    }, token);

    public Task<ObserverHost> RenameAsync(Guid id, string name, CancellationToken token) => ExecuteAsync(async db =>
    {
        var row = await FindHost(db, id, token);
        row.Name = name;
        return Host(row);
    }, token);

    public Task RevokeAsync(Guid id, CancellationToken token) => ExecuteAsync(async db =>
    {
        (await FindHost(db, id, token)).Revoked = true;
        return true;
    }, token);

    public Task<IReadOnlyList<ObserverInstance>> InstancesAsync(Guid? host, CancellationToken token) => ExecuteAsync<IReadOnlyList<ObserverInstance>>(async db =>
        (await db.Set<StoredInstance>().Where(row => host == null || row.HostId == host).OrderBy(row => row.EntityId).ToListAsync(token)).Select(Instance).ToArray(), token);

    public Task<ObserverInstance> InstanceAsync(Guid id, CancellationToken token) => ExecuteAsync(async db =>
        Instance(await FindInstance(db, id, null, token)), token);

    public Task<IReadOnlyList<InstanceConfig>> ConfigurationsAsync(Guid host, CancellationToken token) => ExecuteAsync<IReadOnlyList<InstanceConfig>>(async db =>
    {
        RequireActive(await FindHost(db, host, token));
        return (await db.Set<StoredInstance>().Where(row => row.HostId == host).OrderBy(row => row.EntityId).ToListAsync(token)).Select(Config).ToArray();
    }, token);

    public Task<InstanceConfig> RegisterAsync(Guid host, Guid id, InstanceRegistration registration, CancellationToken token) => ExecuteAsync(async db =>
    {
        RequireActive(await FindHost(db, host, token));
        var row = await db.Set<StoredInstance>().FindAsync([id], token);
        if (row is not null)
        {
            RequireOwner(row, host);
            RequirePresent(row);
            return Config(row);
        }
        if (await db.Set<DeletedVersion>().AnyAsync(row => row.EntityId == id, token))
            throw new ManagementException(410, "instance_removed", "This identity was deleted.");
        var sameKey = db.Set<StoredInstance>().Where(row => !row.Removed && row.InstanceKey == registration.InstanceKey);
        if (await sameKey.AnyAsync(row => row.HostId == host, token))
            throw new ManagementException(409, "instance_key_taken", "This host already has an instance for this package.");
        if (registration.Scope == "deployment" && await sameKey.AnyAsync(token))
            throw new ManagementException(409, "deployment_singleton", "This deployment already has an instance for this package.");
        row = new StoredInstance { EntityId = id, HostId = host, InstanceKey = registration.InstanceKey, Name = registration.Name,
            Enabled = registration.Enabled, Settings = registration.Settings.GetRawText(), Revision = 1, LastLocalSequence = registration.LocalSequence };
        db.Add(row);
        return Config(row);
    }, token);

    public Task<InstanceConfig> ChangeAsync(Guid? host, Guid id, ConfigurationChange change, long? sequence, CancellationToken token) => ExecuteAsync(async db =>
    {
        if (host is { } owner) RequireActive(await FindHost(db, owner, token));
        var row = await FindInstance(db, id, host, token);
        RequirePresent(row);
        if (sequence is { } value)
        {
            if (value <= row.LastLocalSequence) return Config(row);
            if (value != row.LastLocalSequence + 1)
                throw new ManagementException(409, "sequence_gap", "Local changes must arrive in sequence.", row.LastLocalSequence);
            row.LastLocalSequence = value;
        }
        if (change.Enabled is { } enabled) row.Enabled = enabled;
        if (change.Settings is { } settings) row.Settings = settings.GetRawText();
        row.Revision++;
        return Config(row);
    }, token);

    public Task ReportAsync(Guid host, Guid id, JsonElement status, CancellationToken token) => ExecuteAsync(async db =>
    {
        RequireActive(await FindHost(db, host, token));
        var row = await FindInstance(db, id, host, token);
        row.Status = status.GetRawText();
        row.ReportedAt = VersionTime.Normalize(clock.GetUtcNow());
        return true;
    }, token);

    private async Task<T> ExecuteAsync<T>(Func<OrisumiDbContext, Task<T>> action, CancellationToken token)
    {
        await using var db = new OrisumiDbContext(new DbContextOptionsBuilder<OrisumiDbContext>().UseNpgsql(source).Options);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(724121180221)", token);
        var result = await action(db);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    private static async Task<StoredHost> FindHost(OrisumiDbContext db, Guid id, CancellationToken token) =>
        await db.Set<StoredHost>().FindAsync([id], token) ?? throw new ManagementException(404, null, "Host not found.");
    private static async Task<StoredInstance> FindInstance(OrisumiDbContext db, Guid id, Guid? host, CancellationToken token)
    {
        var row = await db.Set<StoredInstance>().FindAsync([id], token) ?? throw new ManagementException(404, null, "Instance not found.");
        if (host is { } owner) RequireOwner(row, owner);
        return row;
    }
    private static void RequireOwner(StoredInstance row, Guid host)
    {
        if (row.HostId != host) throw new ManagementException(403, "host_mismatch", "This instance belongs to another host.");
    }
    private static void RequirePresent(StoredInstance row)
    {
        if (row.Removed) throw new ManagementException(410, "instance_removed", "The instance was removed.");
    }
    private static void RequireActive(StoredHost row)
    {
        if (row.Revoked) throw new ManagementException(401, "credential_revoked", "The host credential was revoked.");
    }
    private static ObserverHost Host(StoredHost row) => new(row.HostId, row.Name, row.Kind, row.Revoked, row.LastSeenAt);
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static InstanceConfig Config(StoredInstance row) => new(row.EntityId, row.InstanceKey, row.Enabled, Json(row.Settings), row.Revision, row.LastLocalSequence, row.Removed);
    private static ObserverInstance Instance(StoredInstance row) => new(row.EntityId, row.HostId, row.InstanceKey, row.Name,
        row.Enabled, Json(row.Settings), row.Revision, row.Removed, row.Status is null ? null : Json(row.Status), row.ReportedAt);
}
