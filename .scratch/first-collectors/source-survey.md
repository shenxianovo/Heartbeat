# 会话来源结构初查

只检查本机各来源最近三个 JSONL 文件的前 200 行，输出记录类型和字段名，没有输出或保存会话正文。这是当前本机格式样本，不代表所有版本的完整格式契约。

## Codex

- 外层记录含 `type`、`timestamp`、`payload`；本机样本还含 `ordinal`。
- 记录类型包括会话元信息、响应项、事件、轮次上下文及用量。
- 响应项包含消息、工具调用和工具结果；事件中也出现消息相关记录。解析前必须核对这些记录的对应关系，避免把同一消息重复导入。
- 会话和消息标识、调用标识、轮次标识的可用性不同，不能假设每种记录都有相同的身份字段。

官方配置参考：<https://developers.openai.com/codex/config-reference>。该页面不能替代本地 JSONL 格式核验。

## Claude Code

- 本机样本包含 user、assistant、system、attachment 及其他会话管理记录。
- 对话记录外层通常含 `uuid`、`parentUuid`、`sessionId`、`timestamp` 和 `message`；部分管理记录没有消息时间或标识。
- `message.content` 需要按内容块解析，不能假设只有纯文本。
- 样本有父子关联和侧链字段；消息顺序和会话分支语义仍需核对。

官方说明：<https://code.claude.com/docs/en/how-claude-code-works>、<https://code.claude.com/docs/en/cli-reference>。

## 尚未决定

本调查不决定保留范围、实体划分、导入身份或采集进程结构。下一项先确认需要保留的会话内容。
