# Windows 桌面 Collector

由 WinUI 宿主创建，Collector key 为 `heartbeat.collector.desktop.windows`。共享的 `Heartbeat.Collector.Desktop` 负责 Record 投影、标题站稳、重复按键过滤和 Hub 交接；本模块仅提供 Windows 原生读数。

- 使用当前会话中的 WinEvent 前台切换与窗口名称变化通知，周期采样仍由共享会话负责。应用身份是规范化为小写的可执行文件完整路径，标题通过 `GetWindowTextW` 读取。无法读取目标进程或标题时报告不可用，不伪造应用身份。
- 独立隐藏窗口和消息线程接收 Raw Input，不阻断其他应用的输入。键盘使用 Set 1 扫描码映射到共享物理键位置；E0 区分左右修饰键及数字键盘。未知键、E1/Pause 多字节序列不记录，不调用字符翻译接口。重复键由共享投影过滤。
- 鼠标按钮为 1 起始的左、右、中、X1、X2；滚动保留符号，离散滚轮的 WHEEL_DELTA 换算成 line 单位。鼠标移动不产生输入 Record。
- WTS 当前会话通知提供锁屏和会话失活；电源通知提供系统休眠与显示器关闭。不同离开原因在共享投影中独立处理，单个恢复通知不清除其他原因。
- Raw Input 注册失败只使输入能力不可用；消息线程或前台观察器失败时报告观测不可用，暂停后重新开始创建新会话。退出先撤销原生订阅再结束线程。
- Target 读取 SMBIOS 2.6+ 系统 UUID，固件缺失、全零或全一 UUID 会失败；不以随机值或系统安装标识回退。它不实现跨 Collector 的 Device Identity 解析。

原生行为需 Windows 实机验证；跨平台自动测试只证明键位、按钮/滚动翻译与 SMBIOS 解析，不证明 Win32 回调、权限隔离、安全桌面、休眠或凭据库。入口与验收矩阵见[桌面客户端](../../Desktop/README.md)。
