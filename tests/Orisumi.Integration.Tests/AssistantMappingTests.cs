using System.Text;
using System.Text.Json;
using Orisumi.Core;
using Orisumi.Observers.AssistantSessions;
using static Orisumi.Integration.Tests.AssistantTestFixture;

namespace Orisumi.Integration.Tests;

public sealed class AssistantMappingTests
{
    private static readonly string[] UserKinds = ["user.text"];
    private static readonly string[] InstructionKinds = ["agents_md.instructions"];

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task UnsupportedCodexEventsAreDiagnosedRatherThanSilentlyTreatedAsImported()
    {
        var context = new SourceContext();
        CodexSource.Read(Meta(), context, null);
        var row = Json(new { type = "event_msg", payload = new { type = "unknown_future_event" } });
        await Assert.That(() => CodexSource.Read(row, context, null)).Throws<InvalidDataException>();
    }

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task EmptyThinkingWithSignatureAndEmptyTextAreValidSourceContent()
    {
        var reasoning = ClaudeSource.Read(ClaudeMessage("opaque", new[] { new { type = "thinking", thinking = "", signature = "signed" } }, "assistant"),
            new(), new("claude-code", "main"), null).Single();
        await Assert.That(reasoning.Parts[0].Text).IsEqualTo("");
        await Assert.That(reasoning.Parts[1].Type).IsEqualTo("opaque");
        var text = ClaudeSource.Read(ClaudeMessage("empty", new[] { new { type = "text", text = "" } }), new(), new("claude-code", "main"), null).Single();
        await Assert.That(text.Parts.Single().Text).IsEqualTo("");
        var context = new SourceContext();
        CodexSource.Read(Meta(), context, null);
        var codex = CodexSource.Read(Item("AgentMessage", "empty", Fields("""{"content":[{"type":"text","text":""}]}""")), context, null).Single();
        await Assert.That(codex.Parts.Single().Text).IsEqualTo("");
    }
    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task ClaudeSeparatesRolesAndPreservesMixedContentAndResponseBlocks()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("claude/projects/project/main.jsonl",
            ClaudeMessage("u1", new object[] { new { type = "text", text = "<system-reminder>my own words" },
                new { type = "image", source = new { type = "base64", media_type = "image/png", data = "AQID" } }, new { type = "text", text = "after" } }),
            ClaudeMessage("r1", new[] { new { type = "tool_result", tool_use_id = "call-b", content = "second", is_error = true },
                new { type = "tool_result", tool_use_id = "call-a", content = "first", is_error = false } }),
            ClaudeMessage("a1", new[] { new { type = "text", text = "reply" } }, "assistant"),
            ClaudeMessage("a2", new[] { new { type = "thinking", thinking = "source thought", signature = "opaque" } }, "assistant"),
            ClaudeMessage("a3", new[] { new { type = "tool_use", id = "call-a", name = "Read", input = new { path = "file" } } }, "assistant"),
            ClaudeMessage("ctx", "not user speech", meta: true),
            Json(new { type = "api-request-blob", anything = "duplicate" }));
        await fixture.Collector.PollAsync();
        var interactions = fixture.Interactions().Select(Content).ToArray();
        await Assert.That(interactions.Length).IsEqualTo(7);
        var user = interactions.Single(value => value.Text("sourceId") == "u1");
        await Assert.That(user.Text("actor")).IsEqualTo("user");
        await Assert.That(string.Join(',', user.GetProperty("parts").EnumerateArray().Select(value => value.Text("type")))).IsEqualTo("text,image,text");
        await Assert.That(user.GetProperty("parts")[0].Text("text")).IsEqualTo("<system-reminder>my own words");
        var result = interactions.Single(value => value.Text("sourceId") == "r1#0");
        await Assert.That(result.Text("actor")).IsEqualTo("tool");
        await Assert.That(result.Text("sourceRole")).IsEqualTo("user");
        await Assert.That(result.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(interactions.Where(value => value.Text("sourceId")!.StartsWith('a')).All(value => value.Text("sourceMessageId") == "response-one")).IsTrue();
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
        // A second scan does not create versions or observations for these immutable interactions.
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Interactions().Count).IsEqualTo(7);
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task ClaudeModeRowsAreNotInteractions()
    {
        var items = ClaudeSource.Read(Json(new { type = "mode", mode = "normal", sessionId = "main" }), new(), new("claude-code", "main"), null);
        await Assert.That(items.Count).IsEqualTo(0);
    }

    [Test, Arguments("permission-mode"), Arguments("cost-state"), Arguments("ai-title"), Property("caseId", "assistant-interaction-mapping")]
    public async Task ClaudeSessionMetadataRowsAreNotInteractions(string type)
    {
        var items = ClaudeSource.Read(Json(new { type, sessionId = "main" }), new(), new("claude-code", "main"), null);
        await Assert.That(items.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task UnsupportedClaudeRowsAreStillDiagnosed()
    {
        var row = Json(new { type = "unknown_future_row", sessionId = "main" });
        await Assert.That(() => ClaudeSource.Read(row, new(), new("claude-code", "main"), null)).Throws<InvalidDataException>();
    }

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task ClaudeSessionMetadataKeepsCustomTitleAndDoesNotInterruptCapture()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("claude/projects/project/main.jsonl",
            Json(new { type = "custom-title", sessionId = "main", customTitle = "My title" }),
            Json(new { type = "permission-mode", sessionId = "main", permissionMode = "default" }),
            Json(new { type = "cost-state", sessionId = "main", totalCostUSD = 0.25, modelUsage = new { } }),
            Json(new { type = "ai-title", sessionId = "main", aiTitle = "Automatic title" }),
            ClaudeMessage("message-after-metadata", "hello"));

        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(Content(fixture.Interactions().Single()).Text("sourceId")).IsEqualTo("message-after-metadata");
        await Assert.That(fixture.Sessions().Select(Content).Last().Text("title")).IsEqualTo("My title");
    }

    [Test, Arguments("human", "user"), Arguments("peer", "agent"), Arguments("coordinator", "agent"),
        Arguments("task-notification", "application"), Arguments(null, null), Property("caseId", "assistant-interaction-mapping")]
    public async Task OriginControlsActor(string? origin, string? expected)
    {
        var items = ClaudeSource.Read(ClaudeMessage("u", "hello", actor: origin), new(), new("claude-code", "main"), null);
        await Assert.That(items.Single().Actor).IsEqualTo(expected);
    }

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task CodexUsesCompletedItemsAndDoesNotReplayContextSnapshots()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("codex/sessions/2026/10/30/rollout-main.jsonl", Meta(),
            Json(new { type = "response_item", payload = new { type = "message", id = "u1", role = "user",
                content = new[] { new { type = "input_text", text = "hello" } },
                internal_chat_message_metadata_passthrough = new { content_item_kinds = UserKinds } } }),
            Item("UserMessage", "u1", Fields("""{"content":[{"type":"text","text":"hello"}]}""")),
            Item("AgentMessage", "a1", Fields("""{"content":[{"type":"text","text":"reply"}],"phase":"final_answer"}""")),
            Json(new { type = "response_item", payload = new { type = "message", id = "ctx", role = "user",
                content = new[] { new { type = "input_text", text = "AGENTS and environment" } },
                internal_chat_message_metadata_passthrough = new { content_item_kinds = InstructionKinds } } }),
            Json(new { type = "response_item", payload = new { type = "additional_tools", id = "tools", role = "developer", tools = Array.Empty<object>() } }),
            Json(new { type = "compacted", timestamp = "2026-10-30T10:10:00Z", payload = new { window_id = "window", replacement_history = new[] { new { text = "old reply" } } } }),
            Json(new { type = "event_msg", payload = new { type = "token_count" } }));
        await fixture.Collector.PollAsync();
        var values = fixture.Interactions().Select(Content).ToArray();
        await Assert.That(values.Count(value => value.Text("kind") == "message")).IsEqualTo(2);
        await Assert.That(values.Count(value => value.Text("kind") == "context")).IsEqualTo(4);
        await Assert.That(values.Single(value => value.Text("sourceId") == "tools").Text("sourceRole")).IsEqualTo("developer");
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
    }

    [Test, Arguments("CommandExecution", """{"command":["echo","ok"],"aggregated_output":"ok","status":"failed","exit_code":1}"""),
        Arguments("McpToolCall", """{"server":"test","tool":"read","arguments":{"path":"a"},"result":{"content":[]},"status":"completed"}"""),
        Arguments("FileChange", """{"changes":{"a":{"type":"add","content":"ok"}},"stdout":"ok","stderr":"","status":"completed"}"""),
        Arguments("Extension", """{"action":{"type":"search","query":"q"},"results":[]}"""),
        Arguments("ImageView", """{"path":"/missing.png"}"""), Property("caseId", "assistant-interaction-relations")]
    public async Task CodexToolItemsBecomeTwoLinkedOccurrences(string type, string fields)
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("codex/sessions/rollout-main.jsonl", Meta(), Item(type, "call", Fields(fields)));
        await fixture.Collector.PollAsync();
        var call = fixture.Interactions().Single(value => Content(value).Text("kind") == "tool-call");
        var result = fixture.Interactions().Single(value => Content(value).Text("kind") == "tool-result");
        await Assert.That(Content(result).GetProperty("toolCallVersionId").GetGuid()).IsEqualTo(call.Version.VersionId.Value);
        await Assert.That(Content(call).GetProperty("sourceEntries")[0].Text("digest")).IsEqualTo(Content(result).GetProperty("sourceEntries")[0].Text("digest"));
        await Assert.That(Content(result).GetProperty("parts")[0].GetProperty("text").ValueKind).IsEqualTo(JsonValueKind.Null);
        if (fields.Contains("\"failed\"", StringComparison.Ordinal))
            await Assert.That(Content(result).GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task ParallelCallsAndLateResultsUseCallIdsAndKeepBranches()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("claude/projects/project/main.jsonl",
            ClaudeMessage("a", new[] { new { type = "tool_use", id = "a", name = "Read", input = new { } } }, "assistant", parent: "branch-a"),
            ClaudeMessage("b", new[] { new { type = "tool_use", id = "b", name = "Read", input = new { } } }, "assistant", parent: "branch-b"));
        await fixture.Collector.PollAsync();
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(ClaudeMessage("r", new[] {
            new { type = "tool_result", tool_use_id = "b", content = "B" }, new { type = "tool_result", tool_use_id = "a", content = "A" } })) + "\n");
        await fixture.Collector.PollAsync();
        var calls = fixture.Interactions().Where(value => Content(value).Text("kind") == "tool-call").ToDictionary(value => Content(value).Text("callId")!);
        foreach (var result in fixture.Interactions().Where(value => Content(value).Text("kind") == "tool-result"))
            await Assert.That(Content(result).GetProperty("toolCallVersionId").GetGuid()).IsEqualTo(calls[Content(result).Text("callId")!].Version.VersionId.Value);
        await Assert.That(Content(calls["a"]).Text("sourceParentId")).IsEqualTo("branch-a");
        await Assert.That(calls.Count).IsEqualTo(2);
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task ChildFilesBeforeParentsKeepParentIdentityAndSpawnCall()
    {
        using var fixture = new AssistantTestFixture();
        var path = fixture.Write("claude/projects/project/parent/subagents/agent-child.jsonl", ClaudeMessage("child-input", "task", sessionId: "parent"));
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".meta.json"), """{"description":"task","agentType":"reader","toolUseId":"spawn"}""");
        fixture.Write("codex/sessions/rollout-child.jsonl", Meta("codex-child", "codex-parent"), Item("UserMessage", "child-user", Fields("""{"content":[{"type":"text","text":"task"}]}""")));
        await fixture.Collector.PollAsync();
        foreach (var child in fixture.Sessions().Where(value => Content(value).GetProperty("parentSessionVersionId").ValueKind != JsonValueKind.Null))
        {
            var parentId = Content(child).GetProperty("parentSessionVersionId").GetGuid();
            await Assert.That(fixture.Sessions().Any(value => value.Version.VersionId.Value == parentId)).IsTrue();
        }
        await Assert.That(fixture.Interactions().Where(value => Content(value).Text("kind") == "message").All(value => Content(value).Text("actor") == "agent")).IsTrue();
        await Assert.That(fixture.Sessions().Select(Content).Single(value => value.Text("sourceSessionId") == "child").Text("spawnCallId")).IsEqualTo("spawn");
    }
}
