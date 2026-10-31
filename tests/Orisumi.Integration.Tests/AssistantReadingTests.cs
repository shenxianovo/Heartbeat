using System.Text;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.AssistantSessions;
using ZstdSharp;
using static Orisumi.Integration.Tests.AssistantTestFixture;

namespace Orisumi.Integration.Tests;

public sealed class AssistantReadingTests
{
    [Test, Property("caseId", "assistant-session-reading")]
    public async Task PartialUtf8WaitsForNewlineAndInvalidCompleteRowsDoNotBlockFollowingRows()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("claude/projects/project/main.jsonl", ClaudeMessage("first", "old"));
        var row = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ClaudeMessage("second", "placeholder")).Replace("placeholder", "猫", StringComparison.Ordinal));
        var cut = Array.IndexOf(row, (byte)0xe7) + 1;
        await using (var append = new FileStream(path, FileMode.Append)) await append.WriteAsync(row.AsMemory(0, cut));
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(1);
        await using (var append = new FileStream(path, FileMode.Append))
        { await append.WriteAsync(row.AsMemory(cut)); append.WriteByte((byte)'\n'); }
        await File.AppendAllTextAsync(path, "not-json\n" + Encoding.UTF8.GetString(ClaudeMessage("third", "new")) + "\n");
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(3);
        await Assert.That(fixture.Interactions().Select(Content).Single(value => value.Text("sourceId") == "second").GetProperty("parts")[0].Text("text")).IsEqualTo("猫");
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(1);
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(1);
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task CopyTruncationAndSameSizeReplacementDoNotLoseOrDuplicateOccurrences()
    {
        using var fixture = new AssistantTestFixture();
        var first = ClaudeMessage("one", "same");
        var second = ClaudeMessage("two", "same");
        var path = fixture.Write("claude/projects/project/main.jsonl", first, second);
        await fixture.Collector.PollAsync();
        File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "copy.jsonl"));
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(2);
        fixture.Write("claude/projects/project/main.jsonl", first);
        await fixture.Collector.PollAsync();
        var oldIdentity = FileIdentity.Read(path);
        var replacement = fixture.Write("replacement.jsonl", ClaudeMessage("new", "same"));
        File.Move(replacement, path, true);
        await Assert.That(FileIdentity.Read(path) == oldIdentity).IsFalse();
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(3);
        File.Delete(path);
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(3);
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task ZstdArchiveReusesIdentitiesAndDoesNotRequireExternalExecutable()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("codex/sessions/rollout-main.jsonl", Meta(), Item("UserMessage", "user", Fields("""{"content":[{"type":"text","text":"hello"}]}""")));
        await fixture.Collector.PollAsync();
        var original = fixture.Interactions().Select(value => value.Version.VersionId).ToArray();
        var archive = Path.Combine(fixture.Codex, "archived_sessions", "rollout-main.jsonl.zst");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using (var compressor = new Compressor()) await File.WriteAllBytesAsync(archive, compressor.Wrap(await File.ReadAllBytesAsync(path)).ToArray());
        File.Delete(path);
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Select(value => value.Version.VersionId).SequenceEqual(original)).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(fixture.StateDirectory, "archives")).Length).IsEqualTo(0);
        fixture.Restart();
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(2);
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task LiveInputIsProcessedWhileHistoryAdvancesInBoundedSlices()
    {
        using var fixture = new AssistantTestFixture();
        var history = fixture.Write("claude/projects/project/main.jsonl", Enumerable.Range(0, 100).Select(i => ClaudeMessage("history-" + i, "old")).ToArray());
        File.SetLastWriteTimeUtc(history, DateTime.UtcNow.AddDays(-10));
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(16);
        fixture.Write("claude/projects/project/live.jsonl", ClaudeMessage("live", "new", sessionId: "live"));
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Any(value => Content(value).Text("sourceId") == "live")).IsTrue();
        await Assert.That(fixture.Interactions().Count < 101).IsTrue();
        for (var i = 0; i < 6; i++) await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(101);
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task LostReceiptAndRestartReplayTheExactBatchBeforeAdvancingProgress()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("claude/projects/project/main.jsonl", ClaudeMessage("u1", "one"));
        var proxy = new LoseReceipt(fixture.Session);
        var collector = fixture.CreateCollector(proxy);
        await Assert.That(async () => await collector.ReadFileAsync(new(path, "claude-code", new("claude-code", "main")))).Throws<IOException>();
        var original = fixture.Interactions().Single().Version;
        await Assert.That(fixture.State.Read<FileProgress>(AssistantState.Key("file", path))).IsNull();
        fixture.Restart();
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(1);
        await Assert.That(fixture.Interactions().Single().Version.EntityId).IsEqualTo(original.EntityId);
        await Assert.That(fixture.Interactions().Single().Version.VersionId).IsEqualTo(original.VersionId);
        await Assert.That(fixture.State.Read<FileProgress>(AssistantState.Key("file", path))!.Offset).IsEqualTo(new FileInfo(path).Length);
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task InteractionsAndLinesBeyondTheLimitsAreDiagnosedAndSkipped()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("claude/projects/project/main.jsonl", ClaudeMessage("one", "small"), ClaudeMessage("large", new string('a', 40_000)),
            ClaudeMessage("three", "small"), ClaudeMessage("huge", new string('b', 200_000)), ClaudeMessage("four", "small"));
        var collector = fixture.CreateCollector(fixture.Session, new(BatchBytes: 30_000, BatchOperations: 512, LineBytes: 100_000));
        for (var i = 0; i < 10; i++) await collector.PollAsync();
        var ids = fixture.Interactions().Select(value => Content(value).Text("sourceId")).Order(StringComparer.Ordinal).ToArray();
        await Assert.That(string.Join(',', ids)).IsEqualTo("four,one,three");
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(fixture.Diagnostics.Any(value => value.Contains("batch limits", StringComparison.Ordinal))).IsTrue();
        await Assert.That(fixture.Diagnostics.Any(value => value.Contains("100000 bytes", StringComparison.Ordinal))).IsTrue();
    }

    [Test, Property("caseId", "assistant-raw-entries")]
    public async Task ReasoningWithoutItsResponseItemIsFormedWhenTheTurnEnds()
    {
        using var fixture = new AssistantTestFixture();
        var completed = Item("Reasoning", "reason", Fields("""{"summary_text":["summary"],"raw_content":[]}"""));
        fixture.Write("codex/sessions/rollout-main.jsonl", Meta(), completed,
            Json(new { type = "event_msg", timestamp = "2026-10-30T10:00:05Z", payload = new { type = "task_complete", turn_id = "turn-one" } }));
        await fixture.Collector.PollAsync();
        var reasoning = fixture.Interactions().Select(Content).Single(value => value.Text("kind") == "reasoning");
        await Assert.That(reasoning.GetProperty("sourceEntries").GetArrayLength()).IsEqualTo(1);
        await Assert.That(string.Join(',', reasoning.GetProperty("parts").EnumerateArray().Select(part => part.Text("type")))).IsEqualTo("summary");
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "assistant-raw-entries")]
    public async Task ReasoningPairSurvivesPollBoundaryAndRestartWithoutLosingOpaqueMaterial()
    {
        using var fixture = new AssistantTestFixture();
        var completed = Item("Reasoning", "reason", Fields("""{"summary_text":["summary"],"raw_content":["thought"]}"""));
        var path = fixture.Write("codex/sessions/rollout-main.jsonl", Meta(), completed);
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(1); // only session context until paired
        fixture.Restart();
        var raw = Json(new { type = "response_item", payload = new { type = "reasoning", id = "reason", encrypted_content = "opaque-value" } });
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(raw) + "\n");
        await fixture.Collector.PollAsync();
        var reasoning = fixture.Interactions().Select(Content).Single(value => value.Text("kind") == "reasoning");
        await Assert.That(reasoning.GetProperty("sourceEntries").GetArrayLength()).IsEqualTo(2);
        await Assert.That(string.Join(',', reasoning.GetProperty("parts").EnumerateArray().Select(part => part.Text("type")))).IsEqualTo("summary,text,opaque");
        var descriptor = reasoning.GetProperty("sourceEntries")[1];
        var bytes = fixture.Runtime.Execute(local => local.ReadContent(descriptor.Text("digest")!))!;
        await Assert.That(bytes.SequenceEqual(raw)).IsTrue();
    }

    private sealed class LoseReceipt(IObserverSession inner) : IObserverSession
    {
        public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, string? requestId = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.RequestAsync(method, parameters, requestId, cancellationToken);
            if (method == "commit") throw new IOException("Injected lost local receipt.");
            return result;
        }
    }
}
