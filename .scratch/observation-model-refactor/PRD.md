# 观测模型改造

Status: done

先在 Collector 中建立可运行的观测模型，再由真实使用需求决定存储演进。
第一步选择 System 的桌面活动采集：本机桌面是直接观测对象，前台应用、标题及 away 是活动读数。
应用/窗口切换可以改变读数与活动连续性，不要求登记新的持久对象。

本 PRD 仅记录第一步：System 桌面活动代码与自动验证完成；不代表整个观测模型改造已结束。

## 当前范围

- [x] 从 AppMonitorService 提取独立运行的活动观测规则并接回正式采集路径。
- [x] 将业务转场和 Fact 身份、修订、快照职责分开，复用现有交付链路。
- [x] 以现有场景/协议回归及独立模型测试验证，记录构建与命名检查结果。
- [x] 更新领域决定和 System 说明，区分自动验证与真机验收。

本阶段不包括 Input Event 重构、Browser 生产改造、通用 SDK、协议或数据库变更。
不把 Objects/ObjectRelations/ObservationContexts 历史草案作为实施基线。
已用独立开发 Profile 完成本机启动、App 转场和本地定时快照检查，证据见实施记录。
未部署；标题、away/恢复和服务端上传仍未做真机验收。

实施：[01 — System 桌面活动](issues/01-system-activity.md)。
前置演练：[Browser 临时 FOI 原型](../observation-runtime-prototype/PRD.md)。
