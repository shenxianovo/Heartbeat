using System.Runtime.CompilerServices;
using System.Text.Json;
using Heartbeat.Observers.Sources.Communication;

namespace Heartbeat.Observers.Sources.Gmail;

/// <summary>Imports one captured successful users.history.list page. Does not fetch subsequent pages.</summary>
public sealed class GmailHistorySource : IRecordSource
{
    public string Name => "gmail-history";
    public static SourceSchema ChangeSchema { get; } = new(
        Guid.Parse("019a0000-0000-7000-8000-000000000503"), "gmail-history-changes", "Gmail mailbox history change",
        SourceFields.WithIdentity(new
        {
            historyId = SourceFields.String(), operation = SourceFields.String(), messageId = SourceFields.String(),
            messageEntityId = SourceFields.Reference(), threadEntityId = SourceFields.Reference(required: false),
            labelIds = GmailNative.Strings()
        }));
    public static SourceSchema PageSchema { get; } = new(
        Guid.Parse("019a0000-0000-7000-8000-000000000504"), "gmail-history-pages", "Gmail captured history page",
        SourceFields.WithIdentity(new
        {
            historyId = SourceFields.String(), hasMorePages = SourceFields.Boolean(),
            continuationFetched = SourceFields.Boolean(), completeness = SourceFields.String()
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GmailNative.Context(context);
        using var document = await NativeJson.ReadAsync(context.Input, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement; GmailNative.Object(root);
        if (root.TryGetProperty("error", out _))
            throw new InvalidDataException("Gmail history error response: no changes imported; investigate the native error before resynchronizing.");
        var currentHistory = NativeJson.RequiredString(root, "historyId");
        var more = NativeJson.OptionalString(root, "nextPageToken") is { Length: > 0 };
        if (root.TryGetProperty("history", out var history))
        {
            if (history.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail history must be an array.");
            foreach (var entry in history.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested(); GmailNative.Object(entry);
                var historyId = NativeJson.RequiredString(entry, "id");
                var detailed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var operation in new[] { "messagesAdded", "messagesDeleted", "labelsAdded", "labelsRemoved" })
                {
                    if (!entry.TryGetProperty(operation, out var changes)) continue;
                    if (changes.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail history changes must be arrays.");
                    foreach (var change in changes.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested(); GmailNative.Object(change);
                        if (!change.TryGetProperty("message", out var message)) throw new InvalidDataException("Gmail history change requires message.");
                        var record = Change(message, change, context.Scope, historyId, operation);
                        detailed.Add(record.Properties.GetProperty("messageId").GetString()!);
                        yield return record;
                    }
                }
                // Generic messages can overlap specific changes. Keep only otherwise unrepresented identities,
                // with an explicitly unknown operation rather than inventing additions or deletions.
                if (entry.TryGetProperty("messages", out var messages))
                {
                    if (messages.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail history messages must be an array.");
                    foreach (var message in messages.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested(); GmailNative.Object(message);
                        if (!detailed.Contains(NativeJson.RequiredString(message, "id")))
                            yield return Change(message, default, context.Scope, historyId, "unspecified-change");
                    }
                }
            }
        }
        var properties = SourceFields.Of(new
        {
            historyId = currentHistory, hasMorePages = more, continuationFetched = false,
            completeness = more ? "incomplete-page-sequence" : "terminal-page-only"
        });
        // Even a terminal page says nothing about the starting cursor or prior pages supplied by the caller.
        var key = NativeJson.Key(context.Scope, context.CaptureKey, SourceJson.Hash(SourceJson.Canonical(root)));
        yield return new("gmail-history-page", key, key, PageSchema, properties, new Dictionary<string, SourceReference>());
    }

    private static SourceRecord Change(JsonElement message, JsonElement change, string scope, string historyId, string operation)
    {
        GmailNative.Object(message);
        var messageId = NativeJson.RequiredString(message, "id");
        var properties = new Dictionary<string, object?> { ["historyId"] = historyId, ["operation"] = operation, ["messageId"] = messageId };
        if (change.ValueKind != JsonValueKind.Undefined) GmailNative.CopyStrings(change, properties, "labelIds");
        if (operation is "labelsAdded" or "labelsRemoved" && !properties.ContainsKey("labelIds"))
            throw new InvalidDataException("Gmail label change requires labelIds.");
        var references = new Dictionary<string, SourceReference>
        { ["messageEntityId"] = new("gmail-message", NativeJson.Key(scope, messageId)) };
        if (NativeJson.OptionalString(message, "threadId") is { Length: > 0 } thread)
            references.Add("threadEntityId", new("gmail-thread", NativeJson.Key(scope, thread)));
        var body = SourceFields.Of(properties);
        var key = NativeJson.Key(scope, historyId, operation, messageId, SourceJson.Hash(SourceJson.Canonical(body)));
        // historyId is an ordering cursor, not a timestamp. Never substitute internalDate/capture time.
        return new("gmail-history-change", key, NativeJson.Key(key, SourceJson.Hash(SourceJson.Canonical(SourceFields.Of(references)))), ChangeSchema, body, references);
    }
}
