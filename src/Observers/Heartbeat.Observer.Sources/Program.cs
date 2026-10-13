using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Heartbeat.Observers.Sources.Local;
using Heartbeat.Observers.Sources.Communication;
using Heartbeat.Observers.Sources.Telemetry;
using Heartbeat.Observers.Sources.Gmail;
using Heartbeat.Observers.Sources.PhysicalCalendar;

namespace Heartbeat.Observers.Sources;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var source = new Option<string>("--source") { Description = "git, file, process, graph-messages, slack-events, otlp-json, cloudevents-json, debezium-json, gmail-messages, gmail-drafts, gmail-history, icalendar-ics, or gpx-xml." };
        var input = new Option<string>("--input") { Description = "Explicit local repository/path/PID or native JSON, ICS, or GPX export." };
        var scope = new Option<string>("--scope") { Description = "Stable source account/repository/device namespace, never an access token." };
        var state = new Option<string>("--state") { DefaultValueFactory = _ => ".local/observers/sources.sqlite" };
        var api = new Option<string>("--api") { DefaultValueFactory = _ => "http://localhost:8080/api/" };
        var capture = new Option<string?>("--capture-key") { Description = "Optional stable retry key. Imports otherwise use file SHA256; live snapshots use a new UUID." };
        var offline = new Option<bool>("--offline") { Description = "Persist locally without sending." };
        var drain = new Option<bool>("--drain") { Description = "Only retry the durable outbox; no new input is read." };
        var root = new RootCommand("Capture one bounded source or import one native export; preserve identities/history and verify every saved entity.");
        foreach (var option in new Option[] { source, input, scope, state, api, capture, offline, drain }) root.Options.Add(option);
        root.SetAction(async (parsed, cancellationToken) =>
        {
            Uri? address = null;
            if (!parsed.GetValue(offline))
            {
                if (!Uri.TryCreate(parsed.GetValue(api), UriKind.Absolute, out address) || address.Scheme is not ("http" or "https")
                    || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0)
                    throw new ArgumentException("--api must be HTTP(S), without credentials, query or fragment.");
            }
            using var store = new SourceStore(parsed.GetValue(state)!);
            var receipts = new List<SourceReceipt>();
            if (!parsed.GetValue(drain))
            {
                var name = parsed.GetValue(source) ?? throw new ArgumentException("--source is required unless --drain is selected.");
                var path = parsed.GetValue(input) ?? throw new ArgumentException("--input is required.");
                var sourceScope = parsed.GetValue(scope) ?? throw new ArgumentException("--scope is required.");
                IRecordSource adapter = name switch
                {
                    "git" => new GitSource(), "file" => new FileSource(), "process" => new ProcessSource(),
                    "graph-messages" => new GraphMessagesSource(), "slack-events" => new SlackEventsSource(),
                    "otlp-json" => new OtlpJsonSource(), "cloudevents-json" => new CloudEventsJsonSource(), "debezium-json" => new DebeziumJsonSource(),
                    "gmail-messages" => new GmailMessagesSource(), "gmail-drafts" => new GmailDraftsSource(), "gmail-history" => new GmailHistorySource(),
                    "icalendar-ics" => new ICalendarSource(), "gpx-xml" => new GpxSource(),
                    _ => throw new ArgumentException("Unknown source. See --help for supported native adapters."),
                };
                var key = parsed.GetValue(capture);
                if (key is null) key = name is "git" or "file" or "process" ? Guid.CreateVersion7().ToString()
                    : Convert.ToHexStringLower(SHA256.HashData(await SourceJson.ReadBytesAsync(path, cancellationToken)));
                var context = new SourceReadContext(path, sourceScope, key, store.CaptureTime(sourceScope, key));
                var records = new List<SourceRecord>();
                long bytes = 0;
                await foreach (var record in adapter.ReadAsync(context, cancellationToken))
                {
                    bytes += record.Properties.GetRawText().Length;
                    if (records.Count >= 10000 || bytes > 32 * 1024 * 1024) throw new InvalidDataException("Mapped capture exceeds 10,000 records or 32 MiB; split the source export explicitly.");
                    records.Add(record);
                }
                receipts.AddRange(store.EnqueueBatch(sourceScope, records, cancellationToken));
            }
            if (!parsed.GetValue(offline))
            {
                using var client = new HttpClient { BaseAddress = new Uri(address!.AbsoluteUri.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
                var sender = new SourceSender(store, client);
                // The second pass is an independent readback, not acknowledgement by PUT response.
                await sender.SynchronizeAsync(cancellationToken);
                await sender.SynchronizeAsync(cancellationToken);
            }
            var pending = store.Pending();
            Console.WriteLine(JsonSerializer.Serialize(new { observerId = store.ObserverId, receipts, pending = pending.Count,
                errors = pending.Where(p => p.LastError is not null).Select(p => new { p.ObservationId, p.LastError }) }, JsonSerializerOptions.Web));
            return parsed.GetValue(offline) || pending.Count == 0 ? 0 : 3;
        });
        try
        {
            var parsed = root.Parse(args);
            var result = await parsed.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
            return parsed.Errors.Count == 0 ? result : 2;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Stopped; durable records remain available for retry."); return 130; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
        { Console.Error.WriteLine(error.Message); return 1; }
    }
}
