# 本地 Issue

进行中的规格和 issue 放在 `.scratch/`。一个功能使用一个目录：

```text
.scratch/<feature-slug>/
├── spec.md
└── issues/
    └── 01-<slug>.md
```

- 每个 issue 单独成文件，从 `01` 编号。
- 文件顶部用 `Status:` 记录 [triage 状态](triage-labels.md)。
- 讨论记录追加到 `## Comments`。
- 技能要求“发布 issue”时，在对应功能目录创建文件；要求“读取 ticket”时，读取用户给出的路径或编号。

需要表达任务依赖时，增加 `.scratch/<effort>/map.md`：

- 子任务用 `Type: research|prototype|grilling|task` 和 `Status: claimed|resolved`。
- `Blocked by: NN, NN` 表示依赖；依赖全部 `resolved` 后才可领取。
- 领取前将状态改为 `claimed`；完成后在 `## Answer` 写结论、改为 `resolved`，并把结论指针补到 `map.md`。

`.scratch` 只保存一次性实施材料。任务完成后删除对应目录，把长期知识分别移到 `CONTEXT.md`、`docs/adr/`、稳定契约文档或模块 README。
