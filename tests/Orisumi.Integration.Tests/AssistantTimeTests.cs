using System.Text;
using Orisumi.Core;
using Orisumi.Observers.AssistantSessions;
using static Orisumi.Integration.Tests.AssistantTestFixture;

namespace Orisumi.Integration.Tests;

public sealed class AssistantTimeTests
{
    private static readonly string[] EnvironmentKinds = ["environments.environment_context"];

    [Test, Property("caseId", "assistant-interaction-time")]
    public async Task ClaudeHistoryLiveAppendAndStoppedAppendHaveDifferentZoneEvidence()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("claude/projects/project/main.jsonl", ClaudeMessage("history", "old", cwd: "/old"),
            ClaudeMessage("reply", new[] { new { type = "text", text = "reply" } }, "assistant", cwd: null));
        await fixture.Collector.PollAsync();
        var initial = fixture.Observations().Select(value => (Observation)value.Content).ToArray();
        await Assert.That(initial.All(value => value.TimeZone is null)).IsTrue();
        await Assert.That(initial.Count(value => value.StartAt is null && value.EndAt is not null)).IsEqualTo(1);
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(ClaudeMessage("live", "new", cwd: "/new")) + "\n");
        await fixture.Collector.PollAsync();
        await Assert.That(((Observation)fixture.Observations()[^1].Content).TimeZone).IsEqualTo("Asia/Tokyo");
        fixture.Restart();
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(ClaudeMessage("stopped", "while stopped")) + "\n");
        await fixture.Collector.PollAsync();
        await Assert.That(((Observation)fixture.Observations()[^1].Content).TimeZone).IsNull();
        await Assert.That(fixture.Interactions().Select(Content).Single(value => value.Text("sourceId") == "history").Text("workingDirectory")).IsEqualTo("/old");
        await Assert.That(fixture.Interactions().Select(Content).Single(value => value.Text("sourceId") == "live").Text("workingDirectory")).IsEqualTo("/new");
        await Assert.That(fixture.Interactions().Select(Content).Single(value => value.Text("sourceId") == "reply").Text("workingDirectory")).IsNull();
    }

    [Test, Property("caseId", "assistant-interaction-time")]
    public async Task CodexEnvironmentAndTurnContextOverrideCollectionEnvironment()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("codex/sessions/rollout-main.jsonl", Meta(),
            Json(new { type = "response_item", timestamp = "2026-10-30T10:00:00Z", payload = new { type = "message", id = "env", role = "user",
                content = new[] { new { type = "input_text", text = "<environment_context><timezone>Asia/Shanghai</timezone></environment_context>" } },
                internal_chat_message_metadata_passthrough = new { content_item_kinds = EnvironmentKinds } } }),
            Json(new { type = "turn_context", payload = new { cwd = "/first", model = "source-model", turn_id = "turn-one" } }),
            Item("AgentMessage", "first", Fields("""{"content":[{"type":"text","text":"answer"}]}""")),
            Json(new { type = "turn_context", payload = new { cwd = "/second", turn_id = "turn-two" } }),
            Item("CommandExecution", "call", Fields("""{"command":["true"],"aggregated_output":"","status":"completed","exit_code":0}""")));
        await fixture.Collector.PollAsync();
        var interactions = fixture.Interactions().ToDictionary(value => value.VersionId);
        foreach (var entry in fixture.Observations())
        {
            var observation = (Observation)entry.Content;
            var content = Content(interactions[entry.VersionId]);
            if (content.Text("sourceId") == "first")
            {
                await Assert.That(observation.EndAt - observation.StartAt).IsEqualTo(TimeSpan.FromSeconds(2));
                await Assert.That(observation.TimeZone).IsEqualTo("Asia/Shanghai");
                await Assert.That(content.Text("workingDirectory")).IsEqualTo("/first");
                await Assert.That(content.Text("model")).IsEqualTo("source-model");
            }
            if (content.Text("sourceId") == "call")
            {
                await Assert.That(observation.StartAt).IsEqualTo(observation.EndAt);
                await Assert.That(content.Text("workingDirectory")).IsEqualTo("/second");
                await Assert.That(content.Text("model")).IsNull();
            }
        }
    }
}
