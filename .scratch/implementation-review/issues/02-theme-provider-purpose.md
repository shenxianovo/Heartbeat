# 确认空壳 ThemeProvider 是否保留

Status: needs-info

2026-09-26 核对：ThemeProvider 只返回 children，主题状态已由模块 store 管理；注释称为保持旧 main 结构而保留。

用户对同一问题中的图标方案选择了 Lucide，已实施；尚未确认移除 ThemeProvider。待确认该包装是否还有业务用途，再删除包装与调用。
