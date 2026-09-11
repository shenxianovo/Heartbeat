# Observations 端到端对象契约收敛

Status: done

2026-09-11 用户授权：收敛采集、上传、查询中的 FOI/Relations，可并行子代理。基线 `05e892a`。
沿用已确认的 Collector→Runtime→HTTP→PostgreSQL→查询/ACK 验证路径。

## 当前契约

- Fact 新输入直接携带 CollectorId、Foi(Kind/Scope/Key)、Relations、Aspect、Payload 和家族时间。
- 对象引用可离线产生：machine/heartbeat.device/既有设备引用；app/heartbeat.app-identity/平台身份由目录解析成全局产品；app/heartbeat.app/产品Key；account/服务/账号；person/heartbeat.person/既有Reference。
- 关系随事实提供种类和角色成员，作用域为该 Fact 的时间，Evidence 绑定准确 Fact；无证据则空列表。
- Source/Stream/Subject 保留交付、配置和知识声明职责，不决定新 Fact 的对象。
- System 保持机器 FOI，前台关系明确引用机器/App；Browser 直接是 App FOI 和 observed-on；VRChat 直接是账号，不补设备。
- Query/Dashboard 以对象 UUID 和关系读取/分组；旧数字ID只用于产品资料/过滤，不是FOI身份。
- 旧HTTP/缓存兼容入口仍支持已确认安装窗口，新链路不生成Target/application-context。历史迁移不改写，身份/Revision/时间/Result/ACK不变，不造分类或Gap。
- 不新增没有业务生产者的 Measurement 契约，不改时长口径，不执行业务库迁移、部署及暂停的资源演练。

## 验收

- [x] 三个第一方 Collector 直接发布 Collector/FOI/Relations，跨平台产品解析不造两个App。
- [x] SDK/Runtime持久缓存迁移、协商、精确ACK与旧包回退保护完整。
- [x] 原生摄入直接保管FOI/关系，旧触发器不能覆盖；同修订冲突与Owner隔离成立。
- [x] Experience/活动/本人查询与Dashboard按FOI/Relations运行，账号缺设备不影响事实。
- [x] 旧缓存重放、产品纠错、本人关联修改与历史数据库迁移保全通过。
- [x] 相关/全仓验证、规范及规格审查、文档和兼容退出收口完成。

## 实施与验证记录

- 已替换原生采集/传输、数据库写入、查询与 Dashboard 对象契约；删除应用上下文实体、本人关联旧表与旧反推触发器。
- 生产已无消费者的 Runtime 旧投影模式及专属测试一起退休；实际旧缓存仍在入口迁移，不另造 Gap。
- 基线 `05e892a` 的隔离 worktree 上，新 FOI/Relations HTTP 测试因缺失 FOI 响应按预期失败；临时 worktree 已删除。
- 数据库专项 27 项通过；DirectObservations 迁移保留 Facts 全字段与关系 UUID/Evidence，重复升级及 EF 模型检查通过。
- 演练脚本 SQL 已在隔离 PostgreSQL 18 执行逐行/对象归属/聚合及分页对照通过，未恢复暂停的完整副本演练。
- Browser 113 测试与构建通过；Frontend 291 测试、类型检查、构建与 NSwag 生成通过。
- 最终 .NET 1336 项通过（全仓运行及失败项目修复后局部复验）；共享 HTTP fixture 合并后长会话再验 1 项通过，命名检查和 diff 检查通过。
- 按用户要求先完成代码再集中回归，删除旧投影专属测试并合并重复 fixture；手写代码净删 459 行，含生成文件的整体 diff 也为净删除。
- 部署、真实安装与业务库资源/恢复门禁继续由 observation-storage PRD 承接，本任务不声明完成这些门禁。
