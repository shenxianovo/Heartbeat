using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed record AssistantRegistration(VersionId SessionSchema, VersionId InteractionSchema)
{
    internal static readonly EntityId ObserverSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000007"));
    internal static readonly EntityId SessionSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000008"));
    internal static readonly EntityId InteractionSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000009"));

    internal static async Task<AssistantRegistration> RegisterAsync(IObserverSession session, string programVersion, CancellationToken token = default)
    {
        var observer = await Register(ObserverSchemaId, "助手会话 Observer", "assistant-sessions-observers",
            "本机程序实例，读取 Claude Code 与 Codex 日志，对交互观测负责；平台、主机名和程序版本变化时形成新版本。", new()
            {
                ["platform"] = Field("string", "运行的平台：macos、windows 或 linux。", false),
                ["hostName"] = Field("string", "本机名称，无法取得时未知。"),
                ["programVersion"] = Field("string", "安装包版本。", false),
            });
        var sessions = await Register(SessionSchemaId, "助手会话", "assistant-sessions",
            "来源的一个会话，包括子 Agent 会话；身份为产品与来源会话标识，属性变化形成新版本。会话没有事实时间。", new()
            {
                ["product"] = Field("string", "产生会话的产品：claude-code 或 codex。", false),
                ["sourceSessionId"] = Field("string", "Claude Code 的 sessionId 或 agentId，Codex 的会话 id。", false),
                ["title"] = Field("string", "来源最新的会话标题或子 Agent 说明。"),
                ["client"] = Field("string", "来源 entrypoint 或 originator。"),
                ["parentSessionVersionId"] = Field("reference", "发起子 Agent 的父会话版本；主会话未知。"),
                ["agentName"] = Field("string", "来源的 agentType 或 agent_path。"),
                ["spawnCallId"] = Field("string", "父会话发起子 Agent 的调用标识。"),
            });
        var fields = new JsonObject
        {
            ["sessionVersionId"] = Field("reference", "交互所在会话的版本。", false),
            ["kind"] = Field("string", "message、tool-call、tool-result、reasoning 或 context。", false),
            ["actor"] = Field("string", "user、assistant、tool、application 或 agent；不能判断时未知。"),
            ["sourceRole"] = Field("string", "来源协议角色，原样保留。"),
            ["sourceKind"] = Field("string", "来源行、block 或 item 的种类。", false),
            ["sourceId"] = Field("string", "与产品、kind 一起识别交互的来源标识。", false),
            ["sourceVersion"] = Field("string", "Claude Code 的 version 或 Codex 的 cli_version。"),
            ["sourceTurnId"] = Field("string", "Claude Code 的 promptId 或 Codex 的 turn_id。"),
            ["sourceParentId"] = Field("string", "来源 parentUuid，保留分支。"),
            ["sourceMessageId"] = Field("string", "Claude Code 响应的 message.id，各 block 共用。"),
            ["callId"] = Field("string", "同一会话中调用与结果共用的来源标识。"),
            ["toolCallVersionId"] = Field("reference", "结果所对应的已读调用；其他交互或调用缺失时未知。"),
            ["toolName"] = Field("string", "来源提供的工具名。"),
            ["toolServer"] = Field("string", "来源提供的 MCP 服务器。"),
            ["isError"] = Field("boolean", "来源报告的工具结果是否失败。"),
            ["exitCode"] = Field("integer", "来源提供的命令退出码。"),
            ["phase"] = Field("string", "来源提供的助手消息阶段。"),
            ["model"] = Field("string", "Claude Code 的 message.model 或 Codex 本轮 turn_context.model。"),
            ["workingDirectory"] = Field("string", "Claude Code 行的 cwd 或 Codex 本轮 turn_context.cwd。"),
        };
        var parts = Field("object", "一个内容片段及其原始位置。", false);
        parts.Remove("required");
        parts["fields"] = new JsonObject
        {
            ["type"] = Field("string", "text、summary、json、image 或 opaque。", false),
            ["text"] = Field("string", "对话、推理或摘要原文，工具参数的 JSON；其余内容在原始条目中。"),
            ["entryIndex"] = Field("integer", "sourceEntries 的从零开始的序号。", false),
            ["pointer"] = Field("string", "原始 JSON 中的 RFC 6901 位置。", false),
            ["mediaType"] = Field("string", "来源图片的媒体类型。"),
            ["file"] = Field("binary", "来源只给路径且读取时仍存在的文件字节。"),
        };
        fields["parts"] = Array("按来源顺序排列的内容片段。", parts);
        var raw = Field("binary", "一行 JSON 的原始字节，不含换行，application/json。", false);
        raw.Remove("required");
        fields["sourceEntries"] = Array("形成交互的来源条目，按读取顺序排列。", raw);
        var interactions = await Register(InteractionSchemaId, "助手交互", "assistant-interactions",
            "来源的一次消息、调用、结果、推理或上下文，身份为产品、kind 和 sourceId，形成后不变。Observation 保存来源的事实时间，输入、调用、结果和上下文为时间点；助手回复与推理有开始时为时段，否则开始未知。", fields);
        await session.RequestAsync("registerInstance", JsonLines.Json(new { schemaVersionId = observer.Value,
            content = new { platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
                hostName = Environment.MachineName, programVersion } }), cancellationToken: token);
        return new(sessions, interactions);

        async Task<VersionId> Register(EntityId id, string name, string resource, string description, JsonObject declaration)
        {
            var schema = new EntitySchema(name, description, resource, JsonSerializer.SerializeToElement(declaration));
            var result = await session.RequestAsync("registerSchema", JsonLines.Json(new { entityId = id.Value, content = VersionJson.Content(schema) }), cancellationToken: token);
            return new(result.GetProperty("versionId").GetGuid());
        }
    }

    private static JsonObject Field(string type, string description, bool nullable = true) =>
        new() { ["type"] = type, ["description"] = description, ["required"] = true, ["nullable"] = nullable };
    private static JsonObject Array(string description, JsonObject item)
    {
        var field = Field("array", description, false);
        field["items"] = item;
        return field;
    }
}
