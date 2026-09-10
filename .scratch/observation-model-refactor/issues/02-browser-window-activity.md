# Browser 窗口活动观测模型

Status: done

## 验收

- [x] 单窗口活动模型只表达页面活动开始、读数更新及窗口关闭，不生成 Fact 身份/时间。
- [x] fold 将模型结果映射为原有 Segment；沿用 open[windowId] 的单份会话状态和字段形状。
- [x] 不改变规范化规则、Facts 输出、协议或存储；接入既有后台回调、对账、自动 Reload。
- [x] 构建和 Browser 全部测试通过，补足会话恢复、关闭一窗与重开窗口的组合场景。
- [x] 本地开发扩展连接当前构建，真实窗口验证并行活动及窗口内页面切换；记录覆盖范围。
- [x] 用户在开发 Chrome 中确认关闭/重开窗口的实际行为；自动测试及用户真机验收均已完成。

## 设计与范围

window-activity.ts 中的 WindowActivity 是窗口的页面活动读数；observeWindow 在当前窗口活动上
判定 started / updated / closed。原有 fold 的 OpenActivity 同时保留这些读数与 Segment 身份/起点，
不为职责分开再建第二份状态或临时/持久对象登记。windowId 只在浏览器会话内辨认窗口。

本项基于启动时工作区，包括用户尚未提交的开发自动 Reload 改动；不改 background.ts、connection.ts、
delivery.ts 等交付逻辑。生产构建按既有约定再生成 Package/browser-extension/background.js。

## 验证

- 改造前 Browser 105 tests passed。
- npm run build：类型检查和 Vite 构建通过。
- npm test：106 tests passed，13 files passed，包含真实 .NET TestHost 协议互通。
- 新组合场景从旧形状的 JSON 会话状态恢复：关闭 17 不影响 23，重用 17 时产生新 Fact，
  23 继续原 FactId/起点，之前的会话快照不被修改。
- 本次手写生产 TypeScript：fold +10/-20，新观测模型 35 行，合计净增加 25 行；
  测试、说明及构建产物单列，不把用户开发工具 diff 计入本项。

## Comments

2026-09-10：用户已建立 `start-local.sh --stack --collector browser` 与自动 Reload，授权继续 Browser 观测模型改造。

2026-09-10 本地运行：通过用户的统一入口启动完整栈，扩展连接到本次精确 Package，
通用 discovery 返回 connectedExternalHosts=1。本机窗口 1708104915 与 1708104931
同时产生非终态快照；第二窗口连续切换页面时第一窗口仍沿用原 FactId/Start 延长结束时间。
新构建运行期间的终态记录已出现 delivered=true，证明至少该记录获得 Analytics 上传确认；
不把未确认记录或旧构建历史统称为本次端到端通过。
脱敏证据：`.local/browser-observation/runtime-evidence.json`，只保存包引用、连接数、Fact 身份/时间与窗口号。

已运行 `collector-contracts.mjs check`，Package 引用一致；`git diff --check` 通过。
106 项测试包含关闭一窗、恢复原会话状态、重用窗口号产生新 Fact 和另一窗续接的组合验证。
当前电脑控制工具只能访问日常 Chrome，无法选择开发独立进程，未自动操作真实窗口关闭/重开。
当时保持开发栈与 watcher 运行，等待开发 Chrome 的手动使用确认。

2026-09-10 closeout：用户明确反馈“那我验收过了，行了”，并授权提交。
关闭/重开窗口的剩余真机验收由用户完成，本项收口；未因此推断 Windows/Edge 或其他 Collector 已验收。
