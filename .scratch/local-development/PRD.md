# 本地开发项目选择

Status: done

用户确认将 sh / pwsh 统一为项目选择式启动：无项目参数默认后端、前端、Hub；显式参数替换默认集合，
只补齐必要依赖，不操作未选择的实例。Browser 是 Collector，浏览器应用另选。独立服务独立验活。
实施与验收见 [issue 01](issues/01-project-selection.md)，操作见 [开发指南](../../docs/development.md)。

项目选择实现与本条验收已完成；跨平台原生 Browser/Console 验收仍沿既有 Browser issue 跟踪，不由此处关闭。
