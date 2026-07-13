# ADR-023: Recap——云端 LLM 叙事摘要与投影/生成分层

## Status: Accepted

## Date: 2026-07-13

## Context

Recap 在词汇表中规划已久（shared/CONTEXT.md）：对某时间段的自然语言叙事摘要，回答"x年前的今天我在做什么"，是 Replay 之上的意义层。当时定下的启动门槛——浏览器扩展落地、输入数据从窗口标题升级为 URL 级——已随 browser collector 达成（ADR-017/CONTEXT-MAP），意义层可以开工。

三个需要一次定清的张力：

1. **隐私敞口**：产品整体是单用户自部署、数据不出本机边界（自己的服务端），而 LLM 叙事的最短路径是云端 API——窗口标题与 URL 将出境到第三方。
2. **输入形状**：ADR-019 的标签升级（system 段标签被重叠插件段的语义替换）是纯展示层逻辑；Recap 的 LLM 输入若复刻它，产生第二份会漂移的实现，且标签升级是有损压缩（一个 2 小时 chrome 段内 15 个页面只剩一个代表标签），恰恰丢掉叙事最需要的细节。
3. **能力暴露**：竞品已有"Agent 接入"形态；外部 Agent 自带 LLM 大脑，要的是可推理的结构化原料而非成品叙事。Recap 的内部结构决定这扇门将来开在哪。

## Decision

### 1. 云端 LLM，OpenAI 兼容协议，供应商纯配置

代码只面向 OpenAI 兼容 chat completions（裸 HttpClient，不引 SDK），供应商由 `Recap:BaseUrl` / `Recap:ApiKey` / `Recap:Model` 三个配置项决定（key 走环境变量/user-secrets，不进仓库）。**显式接受窗口标题/URL 出境到云端 LLM**——与 ADR-012 接受键盘钩子同一格式的单用户 trade-off："用户 == 数据主人 == 部署者"，出境是部署者自己的选择。"先云后本地可逆"由协议形状直接兑现：本地 llama.cpp/Ollama 说同一协议，换 BaseUrl 即迁移，零改码。

### 2. 投影/生成两层，Agent 之门开在投影层

```
segments ──▶ Recap Projection（确定性纯函数）──▶ ① prompt + 云端 LLM ──▶ 叙事
                                             └▶ ② （未来）外部 Agent / MCP 出口
```

投影层是独立的类，segments in → 结构化摘要 out，可单测锁死；生成层（prompt 拼装 + LLM 调用）不可测，保持薄。②不预建（CONTEX-MAP："随需求生长，不预建"），但投影不焊死在 prompt 拼装里，将来接 Agent 是给同一投影加新出口。

### 3. 输入是双轨原样，不复刻标签升级

投影喂双轨：system 段按设备分轨作注意力骨架（轨内互斥、带时长），插件段按 IdentityKey 聚合作语义细节轨（同 URL 多次访问合成一条带总时长），prompt 讲清两轨模型，由 LLM 自行关联。否决服务端复刻 ADR-019 标签升级（漂移 + 有损）。token 控制在投影层：碎段/噪声段（<60s）合并或丢弃、同 App 连续段聚合——只影响 prompt 投影，不动数据（无损原则，数据层原样）。

多设备语义：每台活跃设备一条轨，跨设备重叠合法（away/锁屏段让"人在哪台机器"自然可读）；prompt 明令禁止跨设备时长求和（重叠轨相加超钟表时间），投影只给每设备各自的统计。

### 4. 缓存：派生物落库，历史不过期，今天看水位

Recap 是纯派生物——segments 是事实，叙事随时可重生成，因此**无主动失效机制**。缓存实体落库，(OwnerId, 日窗口起点 UTC) 唯一：

- 历史日期：命中即回，永不过期（"x年前的今天"翻旧账免费）。
- 今天：生成时记录 SegmentWatermark（当时消费到的最新 segment 时间）；请求时水位落后 >1 小时才重生成（快照 upsert 使当天数据持续生长）。另有显式"重新生成"入口，控制权在用户。
- 来源可诊断三字段：GeneratedAt、Model、PromptHash（提示词模板内容 SHA-256 前 8 位，启动时自动计算——否决存 commit hash：无关提交也换 hash，"哪些是旧配方"反而不可答；否决手动版本号：靠纪律必忘）。
- LLM 调用失败返回 502，**不写缓存**，下次请求自然重试。

### 5. 产品形状

- 端点 `GET /api/v1/recaps/daily?date=<DateTimeOffset>`，镜像报表契约（offset 携带用户时区切日窗口）；**无 deviceId 参数**——叙事的主语是"你这一天"不是"这台机器这一天"，跨设备聚合是语义而非默认值；将来按需加参数是兼容扩展。
- 口吻写死进提示词：日记/档案——只叙事，不评判、不打分、不给建议（anti-goals 的"档案馆与日记"美学；也是与职场汇报腔产品的区隔）。纯文本 2–4 段按时间顺序，中文，无结构化字段/列表/emoji。
- 空日（零 segments）不调 LLM，返回显式无数据态；稀疏日照常生成（短数据得短叙事，诚实即正确）。
- v1 阻塞式请求，不做流式（缓存使二次加载秒开，首次生成一个 loading 可接受）。

## Consequences

- ✅ 投影层可单测，LLM 层薄；供应商/云本地可换是配置行为不是工程。
- ✅ 缓存使历史回看零 token 成本，愿景入口（任意日期）天然就位。
- ✅ Agent/MCP 能力暴露的 seam 已留好，不花当下成本。
- ⚠️ 标题/URL 出境敞口成立（单用户 trade-off，多用户化时此决定需重审——见 CONTEXT-MAP 定位不变量 1）。
- ⚠️ 提示词是 Recap 的产品人格，修改无失效机制——旧缓存靠 PromptHash 可辨、靠用户重生成收敛。

## References

- shared/CONTEXT.md Recap 词条 —— 本 ADR 是其"正式 ADR 待动工补"的兑现
- [ADR-012](./012-input-event-tracking.md) —— 出境 trade-off 的同格式先例
- [ADR-017](./017-activity-segment-pluggable-collectors.md) —— 双轨数据模型与统计边界
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) —— 快照生长（"今天"需要水位的原因）
- [ADR-019](./019-replay-attention-line-label-upgrade.md) —— 被否决复刻的展示层标签升级
- 实现文件引用待落地后补
