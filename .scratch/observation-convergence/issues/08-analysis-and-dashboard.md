# 08 — 分析与 Dashboard 消费新事实

**What to build:** 无旧 Stream 的新观测进入适用的报表、回放、Recap、Question 和 Dashboard。分析按 Aspect 解释，未知结果完整展示，Source 保留实际来源与知识声明职责。

**Blocked by:** [01 — 独立观测保存与读取](01-independent-observation-custody.md).

Status: ready-for-agent

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 通过 01 的真实原生 HTTP 入口产生无旧 Stream 的代表性观测，从实际分析 API 到 Dashboard 验证结果；本项不等待 04–06 的现场生产者或 07 的维护流程。
- [ ] 盘点报表、活动与输入分析、Experience/回放、Recap、Question、相关投影及 Dashboard 的活跃消费者；每个旧交付字段依赖有处理结果，不能以原始查询可读替代全体消费者验收。
- [ ] 适用的 desktop-activity/input/selected-page/account-location/activity 事实参与对应已有分析；缺少旧 Stream 不导致遗漏、异常或错误归属，现有跨窗裁剪、分页与时间口径保持。
- [ ] 未知 Aspect 和未知 JSON 字段完整读取/展示，不按 Source 或偶然出现的字段猜成已知活动/输入；新来源采用已有 Aspect 可进入既有解释。
- [ ] Source 继续准确服务来源展示、明确筛选、深度声明及 Matcher/知识引用，与 Collector 身份、Aspect 和产品身份分别表达；不为缺少旧交付资料伪造来源。
- [ ] 多窗口/多设备回放仍保留实际独立事实及必要细节证据，不因 FOI 为同一 App 而合并；设备等基础归属使用 01 已提供的准确关系，完整维护行为由 07 验收。
- [ ] Recap/Question 读取和投影支持新契约，知识确认保持；若派生缓存语义受影响，按既有规则失效而非改写原 Facts 或批量调用真实 LLM。
- [ ] Dashboard 的事实视图、过滤、页面关联、未知结果和空资料状态正确；请求/响应及生成客户端同步，既有活动与输入的可见行为不因新契约退化。
- [ ] 以真实查询/投影和前端可观察行为完成对应回归，依照已有测试替代外部账号/LLM 调用；产品纠错与人工关联的联合回归在 09 集成验收。
- [ ] 记录各消费者覆盖与尚需人工验证的步骤/承接者，不将静态编译或单个页面正常视为全部消费路径完成。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。
