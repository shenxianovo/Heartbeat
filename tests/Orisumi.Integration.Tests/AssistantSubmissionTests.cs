using System.Net;
using System.Text;
using System.Text.Json;
using Orisumi.Core;
using static Orisumi.Integration.Tests.AssistantTestFixture;

namespace Orisumi.Integration.Tests;

public sealed class AssistantSubmissionTests
{
    [Test, Property("caseId", "assistant-raw-entries")]
    public async Task RawBytesAndPointersRoundTripThroughRuntimeAndRealApi()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var fixture = new AssistantTestFixture(factory.Server.CreateHandler());
        var raw = ClaudeMessage("image", new object[] { new { type = "text", text = "before" },
            new { type = "image", source = new { type = "base64", media_type = "image/png", data = "AQID" } }, new { type = "text", text = "after" } });
        fixture.Write("claude/projects/project/main.jsonl", raw);
        var localImage = Path.Combine(fixture.Root, "picture.png");
        byte[] imageBytes = [137, 80, 78, 71, 13, 10, 26, 10];
        await File.WriteAllBytesAsync(localImage, imageBytes);
        var fields = JsonSerializer.SerializeToNode(new { content = new[] { new { type = "local_image", path = localImage },
            new { type = "local_image", path = Path.Combine(fixture.Root, "missing.png") } } })!.AsObject();
        fixture.Write("codex/sessions/rollout-main.jsonl", Meta(), Item("UserMessage", "local-image", fields),
            Item("CommandExecution", "call", Fields("""{"command":["echo","ok"],"aggregated_output":"complete output","status":"completed","exit_code":0}""")));
        await fixture.Collector.PollAsync();
        var versions = fixture.Interactions().Select(value => value.Version).ToArray();
        // Only the missing local image is diagnosed; every batch was saved.
        await Assert.That(fixture.Diagnostics.All(message => message.Contains("missing.png", StringComparison.Ordinal))).IsTrue();
        foreach (var version in versions)
        {
            using var response = await http.GetAsync("versions/" + version.VersionId.Value);
            response.EnsureSuccessStatusCode();
            var saved = (await EntityApiTests.Json(response)).GetProperty("content");
            var originals = new List<JsonDocument>();
            try
            {
                foreach (var descriptor in saved.GetProperty("sourceEntries").EnumerateArray())
                {
                    var bytes = await http.GetByteArrayAsync("contents/" + descriptor.GetProperty("digest").GetString());
                    if (saved.GetProperty("sourceId").GetString() == "image") await Assert.That(bytes.SequenceEqual(raw)).IsTrue();
                    originals.Add(JsonDocument.Parse(bytes));
                }
                foreach (var part in saved.GetProperty("parts").EnumerateArray())
                {
                    var value = Pointer(originals[part.GetProperty("entryIndex").GetInt32()].RootElement, part.GetProperty("pointer").GetString()!);
                    if (part.GetProperty("text").ValueKind == JsonValueKind.String)
                        await Assert.That(part.GetProperty("text").GetString()).IsEqualTo(part.GetProperty("type").GetString() == "json" ? value.GetRawText() : value.GetString());
                    if (part.GetProperty("file").ValueKind != JsonValueKind.Null)
                    {
                        var bytes = await http.GetByteArrayAsync("contents/" + part.GetProperty("file").GetProperty("digest").GetString());
                        await Assert.That(bytes.SequenceEqual(imageBytes)).IsTrue();
                    }
                }
                if (saved.GetProperty("sourceId").GetString() == "local-image")
                {
                    await Assert.That(saved.GetProperty("parts")[0].GetProperty("file").ValueKind).IsEqualTo(JsonValueKind.Object);
                    await Assert.That(saved.GetProperty("parts")[1].GetProperty("file").ValueKind).IsEqualTo(JsonValueKind.Null);
                }
            }
            finally { foreach (var original in originals) original.Dispose(); }
        }
        // The two tool occurrences share one stored source blob.
        var tools = versions.Where(version => ((BusinessContent)version.Content).Fields.GetProperty("sourceId").GetString() == "call").ToArray();
        var digest = ((BusinessContent)tools[0].Content).Fields.GetProperty("sourceEntries")[0].GetProperty("digest").GetString();
        await Assert.That(((BusinessContent)tools[1].Content).Fields.GetProperty("sourceEntries")[0].GetProperty("digest").GetString()).IsEqualTo(digest);
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task DeletedRecordsAreNotRecreatedAndARejectedSegmentIsReadAgain()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var http = factory.CreateClient();
        using var fixture = new AssistantTestFixture(factory.Server.CreateHandler());
        var path = fixture.Write("claude/projects/project/main.jsonl", ClaudeMessage("first", "hello"));
        await fixture.Collector.PollAsync();
        var original = fixture.Interactions().Single().Version;
        using (var deleted = await http.DeleteAsync("entities/" + original.EntityId.Value)) deleted.EnsureSuccessStatusCode();
        File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, "copy.jsonl"));
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(1);
        using (var read = await http.GetAsync("entities/" + original.EntityId.Value)) await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        var conversation = fixture.Sessions().Single().Version;
        using (var deleted = await http.DeleteAsync("entities/" + conversation.EntityId.Value)) deleted.EnsureSuccessStatusCode();
        var checkpoint = fixture.State.Read<Orisumi.Observers.AssistantSessions.FileProgress>(Orisumi.Observers.AssistantSessions.AssistantState.Key("file", path))!.Offset;
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(ClaudeMessage("second", "later")) + "\n");
        await fixture.Collector.PollAsync();
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(1);
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(fixture.State.Read<Orisumi.Observers.AssistantSessions.FileProgress>(Orisumi.Observers.AssistantSessions.AssistantState.Key("file", path))!.Offset).IsEqualTo(checkpoint);
    }
}
