# ADR-059：Observations 的五表最小存储

Status: Accepted（2026-09-11，服务端存储及追加迁移已实现；业务库未迁移）

用户从第一性原理确认：Collector 对 FOI 产生 Fact，Fact 包含 Aspect、Result、Time。
为使机器、App 产品、服务账号和个人独立存在，并表达有时间与证据的多对象关系，
采用 Collectors、Objects、Facts、Relations、RelationMembers 五张核心表。
字段与后续迁移问题以[最小存储方案](../architecture/observation-storage-minimal.md)为准。

2026-09-11 后续：[迁移映射与实施](../architecture/observation-storage-migration.md)记录已授权落地的
身份保全、历史未知、Owner 和精确关系绑定。当前支持现有 Segment/Event，Measurement 业务输入待具体需求。

Facts 以 Kind 区分 Event、Measurement、Segment，共享单份结果存储。对象间的联系通过
有角色的关系成员表达，不要求额外 Target、应用上下文实体、窗口登记或 ObservationContexts。
关系缺失表示缺少依据，不阻止保存对象事实。DataSource 暂不纳入。

本决定替代 ADR-055 的家族物理分表目标，及 ADR-056 中 Target、应用上下文与不统一登记对象的
目标选择；不恢复早期已暂停的三表候选。代价是需要重新证明统一事实表的家族约束、查询性能、
迁移成本及关系成员完整性，收益是直接表达已确认的对象、事实及多元关系。

五表是目标业务核心，并非对现有数据库的直接改名或完整上线 DDL。Owner 隔离、旧事实复合身份、
历史未知、单份原始内容、缓存重放与发布恢复仍须在迁移设计中明确。已上线迁移不得改写，
本决定不代表已有数据已迁移，也不授权恢复暂停的资源演练或执行生产部署。

2026-09-11 后续：用户确认继续完成[显式 Aspect 与消费语义解耦](../architecture/observation-semantics.md)。
Collector 解释原始输入，存储保管 Fact，Analytics 按 Aspect 契约分析，Dashboard 按 Aspect 选择视图。
这替代 ADR-017/018 将活动分析种类绑定 Source 的实现规则；ADR-030 的 Source 深度声明/Matcher 身份继续有效。

2026-09-11 端到端收敛：原生协议直接传 Collector/FOI/Relations；ApplicationContexts、PersonAssociations
及旧反推触发器退役，本人关联直接维护 Relations，查询与 Dashboard 使用 Object UUID。
旧 HTTP/缓存只在边界转换，Runtime 退休无生产消费者的旧投影模式。详见[收敛 PRD](../../.scratch/observation-convergence/PRD.md)。
