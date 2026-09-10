# 观测模型改造

Status: done

先在 Collector 中建立可运行的观测模型，再由真实使用需求决定存储演进。
本机桌面和浏览器窗口分别是 System、Browser 的直接观测对象；观测模型不与持久实体一一对应。

## 实施

- [x] [01 — System 桌面活动](issues/01-system-activity.md)：提取业务转场，复用现有 Fact 输出；
  92 项测试通过，本机 App 转场和定时快照通过，用户确认运行无问题，已提交 `1d6f447`。
- [x] [02 — Browser 窗口活动](issues/02-browser-window-activity.md)：实现、106 项测试、本地精确包连接、
  真实双窗口并行及页面切换已验证，用户已完成关闭/重开窗口的真机验收。

System 的标题、away/恢复及服务器上传未单独做真机验收；不据前台活动验证推断全部能力已验收。
Browser 的首次 Load unpacked、开发 Profile 绑定和自动 Reload 属于既有开发工具，本项复用该链路。

本阶段不包括 Input Event 重构、通用 SDK、协议或数据库变更；不把统一
Objects/ObjectRelations/ObservationContexts 的历史草案作为实施基线。

前置演练：[Browser 临时 FOI 原型](../observation-runtime-prototype/PRD.md)。
