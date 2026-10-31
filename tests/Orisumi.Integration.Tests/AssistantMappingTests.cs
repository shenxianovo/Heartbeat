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
    private static readonly string[] TwoSessions = ["codex-one", "codex-two"];

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
        await Assert.That(result.GetProperty("toolCallVersionId").ValueKind).IsEqualTo(JsonValueKind.Null);
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
        await Assert.That(fixture.Titles().Select(Content).Single().Text("name")).IsEqualTo("My title");
    }

    [Test, Property("caseId", "assistant-session-reading")]
    public async Task TitlesAreSeparateAndRepeatedClaudeTitlesDoNotCreateVersions()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("codex/session_index.jsonl",
            Json(new { id = "main", thread_name = "first", updated_at = "2026-10-30T10:00:00Z" }),
            Json(new { id = "main", thread_name = "second", updated_at = "2026-10-30T11:00:00Z" }));
        fixture.Write("claude/projects/project/main.jsonl",
            Json(new { type = "custom-title", sessionId = "main", customTitle = "title" }),
            Json(new { type = "custom-title", sessionId = "main", customTitle = "title" }));
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
        var titles = fixture.Titles();
        await Assert.That(titles.Count).IsEqualTo(3);
        await Assert.That(fixture.Sessions().All(v => v.GetProperty("category").GetString() == "thing")).IsTrue();
        await fixture.Collector.PollAsync();
        await Assert.That(fixture.Titles().Select(v => v.VersionId).SequenceEqual(titles.Select(v => v.VersionId))).IsTrue();
        var times = fixture.Titles().Select(v => (Observation)v.Content).ToArray();
        await Assert.That(times.Count(t => t.StartAt == t.EndAt && t.StartAt is not null)).IsEqualTo(2);
        await Assert.That(times.Count(t => t.StartAt is null && t.EndAt is null)).IsEqualTo(1);
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
        await Assert.That(Content(result).GetProperty("toolCallVersionId").GetGuid()).IsEqualTo(call.VersionId.Value);
        await Assert.That(Content(call).GetProperty("sourceEntries")[0].Text("digest")).IsEqualTo(Content(result).GetProperty("sourceEntries")[0].Text("digest"));
        await Assert.That(Content(result).GetProperty("parts")[0].GetProperty("text").ValueKind).IsEqualTo(JsonValueKind.Null);
        if (fields.Contains("\"failed\"", StringComparison.Ordinal))
            await Assert.That(Content(result).GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(fixture.Diagnostics.Count).IsEqualTo(0);
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task CodexNumberedItemsStayInTheirOwnSession()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("codex/sessions/rollout-one.jsonl", Meta("codex-one"), Item("UserMessage", "item-1", Fields("""{"content":[{"type":"text","text":"one"}]}""")));
        fixture.Write("codex/sessions/rollout-two.jsonl", Meta("codex-two"), Item("AgentMessage", "item-1", Fields("""{"content":[{"type":"text","text":"two"}]}""")));
        await fixture.Collector.PollAsync();
        var items = fixture.Interactions().Where(value => Content(value).Text("sourceId") == "item-1").ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(SessionsOf(fixture, items)).IsEquivalentTo(TwoSessions);
        var observed = fixture.Observations().Select(value => value.VersionId.Value).ToArray();
        await Assert.That(items.All(value => observed.Count(id => id == value.VersionId.Value) == 1)).IsTrue();
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task CodexRepeatedNumberedReasoningFormsOneInteractionFromTheLastRow()
    {
        using var fixture = new AssistantTestFixture();
        fixture.Write("codex/sessions/rollout-one.jsonl", Meta("codex-one"),
            Item("Reasoning", "item-3", Fields("""{"summary_text":["first"],"raw_content":[]}""")),
            Item("Reasoning", "item-3", Fields("""{"summary_text":["first","second"],"raw_content":[]}""")),
            Json(new { type = "event_msg", payload = new { type = "task_complete", turn_id = "turn-one" } }));
        await fixture.Collector.PollAsync();
        var reasoning = fixture.Interactions().Single(value => Content(value).Text("sourceId") == "item-3");
        await Assert.That(Content(reasoning).GetProperty("parts").GetArrayLength()).IsEqualTo(2);
        await Assert.That(reasoning.PreviousVersionId).IsNull();
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task NumberedReasoningWaitsUntilNextItemAndClaudeForksKeepSeparateObservations()
    {
        using var f = new AssistantTestFixture();
        var path = f.Write("codex/sessions/rollout-one.jsonl", Meta("one"),
            Item("Reasoning", "item-1", Fields("""{"summary_text":["first"],"raw_content":[]}""")));
        await f.Collector.PollAsync();
        await Assert.That(f.Interactions().Any(v => Content(v).Text("sourceId") == "item-1")).IsFalse();
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(Item("Reasoning", "item-1", Fields("""{"summary_text":["last"],"raw_content":[]}"""))) + "\n"
            + Encoding.UTF8.GetString(Item("AgentMessage", "item-2", Fields("""{"text":"done"}"""))) + "\n");
        f.Write("claude/projects/project/parent.jsonl", ClaudeMessage("shared", "hello", sessionId: "parent"));
        f.Write("claude/projects/project/fork.jsonl", ClaudeMessage("shared", "hello", sessionId: "fork"));
        await f.Collector.PollAsync();
        var reasoning = f.Interactions().Single(v => Content(v).Text("sourceId") == "item-1");
        await Assert.That(Content(reasoning).GetProperty("parts")[0].GetProperty("text").GetString()).IsEqualTo("last");
        await Assert.That(reasoning.PreviousVersionId).IsNull();
        var forked = f.Interactions().Where(v => Content(v).Text("sourceId") == "shared").ToArray();
        await Assert.That(forked.Length).IsEqualTo(2);
        await Assert.That(forked.Select(v => Content(v).GetProperty("sessionId").GetGuid()).Distinct().Count()).IsEqualTo(2);
    }

    [Test, Property("caseId", "assistant-interaction-mapping")]
    public async Task ChangedSessionMetadataBytesCreateAnotherContextObservation()
    {
        using var f = new AssistantTestFixture();
        var path = f.Write("codex/sessions/rollout-one.jsonl", Meta("one"));
        await f.Collector.PollAsync();
        await File.AppendAllTextAsync(path, Encoding.UTF8.GetString(Meta("one")).Replace("{", "{ ") + "\n");
        await f.Collector.PollAsync();
        var contexts = f.Interactions().Where(v => Content(v).Text("sourceKind") == "session_meta").ToArray();
        await Assert.That(contexts.Length).IsEqualTo(2);
        await Assert.That(contexts.All(v => v.PreviousVersionId is null)).IsTrue();
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task CodexNumberedToolItemsLinkInsideTheirOwnSession()
    {
        using var fixture = new AssistantTestFixture();
        foreach (var session in TwoSessions)
            fixture.Write($"codex/sessions/rollout-{session}.jsonl", Meta(session),
                Item("CommandExecution", "item-2", Fields($$"""{"command":["echo","{{session}}"],"aggregated_output":"ok"}""")));
        await fixture.Collector.PollAsync();
        var values = fixture.Interactions().ToArray();
        var calls = values.Where(value => Content(value).Text("kind") == "tool-call").ToArray();
        var results = values.Where(value => Content(value).Text("kind") == "tool-result").ToArray();
        await Assert.That(calls.Length).IsEqualTo(2);
        await Assert.That(results.Length).IsEqualTo(2);
        foreach (var result in results)
        {
            var call = calls.Single(value => value.VersionId.Value == Content(result).GetProperty("toolCallVersionId").GetGuid());
            await Assert.That(Content(call).GetProperty("sessionId").GetGuid()).IsEqualTo(Content(result).GetProperty("sessionId").GetGuid());
        }
    }

    [Test, Property("caseId", "assistant-interaction-relations")]
    public async Task CodexGlobalItemsAreSeparateObservationsAcrossSessions()
    {
        using var fixture = new AssistantTestFixture();
        var replayed = Item("CommandExecution", "exec-1", Fields("""{"command":["echo","ok"],"aggregated_output":"ok"}"""));
        fixture.Write("codex/sessions/rollout-parent.jsonl", Meta("codex-parent"), replayed);
        fixture.Write("codex/sessions/rollout-fork.jsonl", Meta("codex-fork"), replayed);
        await fixture.Collector.PollAsync();
        var values = fixture.Interactions().Select(Content).ToArray();
        await Assert.That(values.Count(value => value.Text("kind") == "tool-call")).IsEqualTo(2);
        await Assert.That(values.Count(value => value.Text("kind") == "tool-result")).IsEqualTo(2);
    }

    private static string[] SessionsOf(AssistantTestFixture fixture, IEnumerable<EntityVersion> interactions)
    {
        var sessions = fixture.Sessions().ToDictionary(value => value.GetProperty("entityId").GetGuid(), value => value.GetProperty("keys")[0].GetProperty("value").GetString()!);
        return interactions.Select(value => sessions[Content(value).GetProperty("sessionId").GetGuid()]).ToArray();
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
            await Assert.That(Content(result).GetProperty("toolCallVersionId").GetGuid()).IsEqualTo(calls[Content(result).Text("callId")!].VersionId.Value);
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
        foreach (var child in fixture.Subagents())
        {
            var parentId = Content(child).GetProperty("parentSessionId").GetGuid();
            await Assert.That(fixture.Sessions().Any(value => value.GetProperty("entityId").GetGuid() == parentId)).IsTrue();
        }
        await Assert.That(fixture.Interactions().Where(value => Content(value).Text("kind") == "message").All(value => Content(value).Text("actor") == "agent")).IsTrue();
        await Assert.That(fixture.Subagents().Select(Content).Single(value => value.Text("agentName") == "reader").Text("spawnCallId")).IsEqualTo("spawn");
    }
}
