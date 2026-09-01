# Collection

采集桌面设备的前台应用使用时长与各应用内活动，上传至服务端。作为常驻托盘或菜单栏应用运行。

## Language

**Agent**:
桌面采集宿主：承载 Collector Runtime、system Collector、Browser ExternalHost binding、集面读模型以及统一缓存/上传。它不再提供通用 source 级 loopback segment ingress。
_Avoid_: Service, Worker（这些是 Agent 内部的实现层）

**Collector（采集器）**:
一个观测特定应用内活动并通过 Collector Protocol 向 Runtime 发布 Fact 的组件（browser 扩展、VRChat 账号采集器等）。system 采集器内置于 Desktop，同样经 Runtime 汇入，特例性仅剩两点：进程内 binding、不可停用。非内置采集器代码位于 `collection/collectors/`。
_Avoid_: 插件/Plugin（口语别名，UI 与文档统一用"采集器"；ADR-017 等历史文档中的 plugin 即此概念）

**Collector Package（采集器包）**:
Collector 的内容快照；精确候选由声明版本与 content hash 共同确定。正式发布物不可变；当前本地官方 Package 在发布安装 seam 建立前允许同一声明版本换内容。它不表示已经安装、配置或运行。
_Avoid_: Plugin Package、把 Collector Package 与 Collector 混称为“插件”

**Collector Runtime（采集器运行时）**:
管理采集器包的安装事实、Collector 的期望状态与实际激活状态，并协调二者收敛的运行边界。它不等同于 Collector 内部可能使用的细粒度组件运行时。
_Avoid_: Plugin Runtime、Package Manager（后者只覆盖制品管理）

**Collector Protocol（采集器协议）**:
Collector Activation 与 Hub 交换身份、能力、配置、生命周期和 Fact 的统一语义契约；它独立于具体传输，也独立于 Package 发布版本。
_Avoid_: 把当前 loopback HTTP 路由集合当成完整协议

**Collector Protocol Client（采集器协议客户端）**:
Collector 侧持有协议会话与未确认交付责任的参与者；它统一承担 Activation 生命周期、消息关联、ACK/重试、Stream Gap、授权、Collector Secret 与 drain，使 Collector 只需表达观测事实。
_Avoid_: 每个 Collector 自行拼装协议状态机、把 Client 与某一种 Transport Binding 混称

**Collector Ingress Queue（采集器入口队列）**:
隔离观测回调与 Collector Protocol 交付背压的 Collector 内部边界。平台 UI、窗口事件和输入 hook 只入队；后台 delivery pump 才能等待持久化、ACK 或重试。
回调返回只承诺进入进程内易失队列，不承诺 durable acceptance；System InputEvent 的后台容量判定必须
在一个 durable journal mutation 中提交 Event 或覆盖它的 Stream Gap，不能用两个独立存储动作制造
“已拒绝但无 Gap”的崩溃窗口。
_Avoid_: 在原生回调或 UI 线程同步等待 Fact 发布

**Stream Gap（流缺口）**:
Collector 对某个 Fact Stream 已知丢失范围的持久事实；稳定 UUIDv7 GapId 是幂等身份，协议 messageId 只标识一次传输尝试。时间使用非空半开区间，同一范围可以有多个不同 GapId，不能仅按 range/reason 去重而吞掉独立丢失。
_Avoid_: 用 messageId 充当 Gap 身份、把相同时间范围自动视为同一丢失、ACK 前删除本地 Gap

**Durable InputEvent Projection（持久输入事件投影）**:
Hub 已提交 Event Fact 后、Analytics InputEvent 上传确认前的持久责任窗口；`InputEventBuffer` 是该窗口唯一容量 owner。满容量返回 backpressure 并保留原 FactId，底层 JSON 文件只做原子持久化、不自行裁剪。
_Avoid_: 在 JsonFileCache 与投影层分别封顶、把 count clamp 当成成功、满容量丢 oldest 却不报告 Gap

**Collector Protocol Conformance Suite（采集器协议一致性套件）**:
跨语言共享的可执行协议行为语料，固定生命周期、ACK、重试、Gap 与 drain 结果；各语言实现通过同一组向量证明其 Binding 没有改变协议语义。它不是 wire-message schema，也不是完整请求/响应 transcript。
_Avoid_: 只共享 DTO、以某一种语言实现作为协议本身、把行为语料误当消息格式定义

**Collector Drain Outcome（采集器排空结果）**:
Collector 在同一个绝对 deadline 内停止观测、把 ingress tail 先交给本地 durable first stage / outbox、尝试交付并报告 remainder 的逻辑结果；`drained`、`deadline_exceeded`、`stop_failed`、`flush_cancelled`、`persistence_failed` 是跨 Execution Driver 的稳定 reason。逻辑结果与 Runtime 是否成功接收 completion 分层记录；只有逻辑 reason 为 `drained`、remainder durable、pending facts/gaps 均为零且 completion 成功时才是 fully drained。InProcess 到期 fence 并释放 writer，ManagedProcess 到期终止并释放，ExternalHost 保持 lease revoke 弱语义。
_Avoid_: 把未送达的 `activation.drained` 当成 completion 成功、用 pending=0 掩盖 non-durable/unknown tail、让 deadline 后的旧 Activation 继续写入

**Observation Declaration（观测声明）**:
Collector Package 携带的独立 JSON 声明，描述 Source 的有序观测深度、读数槽位和展示标签。Package loader 先验证其路径、hash、Source 与版本，再由 Hub 原文上行；它不属于 Fact payload，也不从 Fact Schema 或 Output Template 推导。
_Avoid_: Fact Schema、Output Template、运行时自报 declaration

**Artifact Descriptor（制品描述符）**:
Collector Package 中描述一个可验证执行制品的 `*.artifact.json`。它固定入口与内容；Browser ExternalHost 描述符还枚举整个 sideload payload 的路径、大小与 hash。它的内容 hash 由最终 manifest 引用。
_Avoid_: Collector Package manifest、浏览器内部 `manifest.json`、把 descriptor 当成可执行文件本身

**Transport Binding（传输绑定）**:
Collector Protocol 在某种执行边界上的承载方式；不同 Binding 不改变协议语义。
_Avoid_: 为每种执行方式发明不同协议

**Output Template（输出模板）**:
Collector Package 对一类可实例化 Fact Stream 的静态声明，限定其 Source、FactKind、schema、SubjectKind 与 identifying dimensions。
_Avoid_: 运行时任意注册 schema、把每个动态 dimension value 写成新清单项

**Fact Schema（事实模式）**:
可执行事实 payload 的版本化约束。唯一权威文件位于 `collection/contracts/facts/`；Package 内 schema 与最终 manifest 是 build staging 产物。Package 使用原始字节 hash 做完整性校验；baseline 与 Runtime 以解析后的 JSON 含义判断同一 `(SchemaId, Major, Revision)` 是否变化。当前 Collector Protocol v1 只执行 Segment 与 Event。
_Avoid_: 在 Collector C# / TypeScript 里复制 schema 字符串、把私有状态文件的 `schemaVersion` 混入 Fact Schema 版本体系

**Hub Instance（Hub 实例）**:
Collector Runtime 的一个持续运行宿主，可以是 Desktop Agent 内嵌 Hub，也可以是服务器上的无头 Hub。Hub Instance 是运维身份而非观测主体；一个无头 Hub 可以托管观测不同账号、身体或其他主体的多个 Collector Instance。
_Avoid_: Device、Subject、把无头 Hub 按某个 Collector 命名

**Hub Management Surface（Hub 管理界面）**:
由 Hub Instance 自己拥有的用户管理边界，用于需要交互授权的 Collector Instance 设置与恢复。Dashboard 可以提供入口，但 Analytics 不代理第三方账号凭据、授权应答或管理命令。
_Avoid_: Analytics Collector Control Plane、要求用户通过服务器终端完成账号授权

**Collector Installation（采集器安装）**:
本机完整持有某一精确 Collector Package 的事实；部分下载或未完成目录不是 Installation。安装不表示 Collector 已启用、已获授权、能够激活或正在运行，多个 Collector Instance 可以共享同一份已安装内容。
_Avoid_: Download、Staging、Registration、Active

**Installation Completion Marker（安装完成标记）**:
安装目录内声明该目录已完整持有某一精确 Collector Package 的记录；它是 Installation 的必要条件而非充分条件，标记缺失、指向别的精确候选或与目录内容不再一致时，该目录都不是 Installation。它只描述本机安装完整性，不表示批准、Ready、Last-Known-Good 或任何运行意图。
_Avoid_: Install Journal、Lock File、把标记当作批准或 Ready 记录、用它保存 Desired State

**Collector Instance（采集器实例）**:
一个稳定、已配置的 Collector 身份；更换其 Collector Package 版本或重新激活时，实例身份保持不变，同样不会因为长期离线而自动删除。同一采集器包可以对应多个实例。browser Collector 在同一 Machine Subject 上按 App 分 Instance：Chrome、Edge 等 App 各有独立启用意图与运行状态，但共享一份 Collector Installation；同一 App 的浏览器 Profile 不是独立 Instance。首次发现新的 browser App Instance 默认启用，删除身份与配置只能是显式用户操作。
_Avoid_: 用 Source、进程 ID 或包版本充当实例身份

**Collector Instance Key（采集器实例键）**:
Hub 为需要幂等发现的 Collector Instance 分配的不透明、不可变稳定槽位；它在同一 `PackageId + Subject` 内唯一，不替代 CollectorInstanceId。browser adapter 以 App 形成 Instance Key，使同一 App 重连时回到原 Instance，而不是把 App 身份塞进可变配置。
_Avoid_: 第二个 CollectorInstanceId、Instance Config、把 Source 当作 Instance Key

**Collector Activation（采集器激活）**:
Collector Instance 的一次协议会话和实际运行身份；重启、重连、成功或失败都不改写 Instance 身份。ExternalHost Instance 可以同时拥有多个 Activation，各自代表一份独立运行的外部宿主；同一 External Host Identity 的新 Activation 只接替该 Host 的旧会话，不影响同 App 的其他 Host 或其他 App Instance。
_Avoid_: Run（含义过泛）、Active（后者是按 Source 流量推断的既有状态）

**Collector Activation Lifetime（采集器激活生命周期）**:
从 Hub 接受一次 Activation 到其 writer/lease 最终释放的单一所有权事务；Hub 与 Collector Protocol Client
在协议两侧各自拥有生命周期，彼此交换结果而不形成跨进程 owner。
_Avoid_: 多个 caller 各自 Stop/释放、跨进程 Lifecycle Coordinator、Artifact Delivery

**Collector Delivery Ownership（采集器交付所有权）**:
Collector Protocol Client 对未确认 Fact/Stream Gap 的保留与交付责任；drain 时它从 background 原子转移到
有绝对 deadline 的 drain owner，fenced 后不能再 ACK 或改变 durable responsibility。
_Avoid_: background pump task、用 CancellationToken 或异常消费顺序表示 ownership

**Interactive Authorization（交互授权）**:
Collector Activation 通过 Collector Protocol 请求用户完成的一次非阻塞授权交互。Collector 声明当前 challenge，Hub Management Surface 收集应答；敏感应答只在内存中传递，不成为配置或运行状态。
_Avoid_: Hub 内置第三方登录流程、把登录设为 Dashboard 的强制前置步骤

**Collector Secret（采集器秘密）**:
按 Collector Instance 隔离保存、供 Collector 恢复第三方会话的不透明秘密。它与 Collector Runtime State 分库存放，不进入 Manifest、Fact、普通日志或用户配置。
_Avoid_: Instance Config、Runtime State、在 Package 目录保存 cookie

**External Host Identity（外部宿主身份）**:
ExternalHost Collector 中一份独立宿主安装的内部稳定身份，例如某个浏览器 Profile 中加载的扩展；它由宿主首次运行时生成并本地持久化，区分并发 Activation，并使同一 App Instance 下的各 Host 拥有独立 Fact Stream、writer 与重传连续性。宿主数据被清理或重装后产生新的 External Host Identity 与 Stream，不根据 Profile 名、路径或进程猜回旧身份；旧 Stream 只变为不活跃。它不形成独立 Collector Instance、启用意图或主 UI 管理项。
_Avoid_: Collector Instance、Browser Profile 设置、把外部宿主身份暴露为用户必须管理的采集器

**Browser Window Activity（浏览器窗口活动）**:
browser Collector 对每个浏览器窗口当前所选 active tab 的如实观察，不判断该窗口或浏览器是否处于操作系统前台。多个窗口、浏览器或 Profile 的活动可以合法重叠；它们是对应 App 的内部细节，不是可求和的用户注意力或使用时长。
_Avoid_: 浏览器前台活动、把 active tab 解释为 OS 前台窗口、按 Host/Profile 拆成用户事实

**Ready（激活就绪）**:
Collector Activation 已完成协议协商并打开所需 Fact Stream，可以承担运行责任；Ready 不要求已经产生第一条 Fact。
_Avoid_: 进程存活、首次产生数据、Active

**候选稳定窗口（Candidate Stability Period）**:
ManagedProcess 候选 Activation 到达 Ready 后、被判定为成功更新前的有界观察期；窗口内退出触发该 Collector Instance 的 Last-Known-Good 回滚，窗口结束时候选晋升为该 Instance 新的 Last-Known-Good，之后退出属于普通运行故障。共享同一 Installation 的其他 Instance 不因其中一个 Instance Ready 而被宣称更新成功。开发期 VRChat 纵切不实现该窗口（[ADR-047](../docs/adr/047-lean-development-collector-web-delivery.md)）：候选 Ready 即视为更新成功，Ready 后退出按普通运行故障处理。
_Avoid_: 把 Ready 等同于已通过稳定观察、无限期自动回滚

**Collector Desired State（采集器期望状态）**:
用户对 Collector Instance 的发布 channel、版本范围、启用与配置意图；暂时的发现、下载、解析或运行失败不会改写它。
_Avoid_: 把当前运行事实反写成用户意图

**Resolved Collector Set（已解析采集器集合）**:
为实现 Collector Desired State 而选出的精确、完整且兼容的 Collector Package 集合。它描述选择结果，不证明制品已安装或激活成功。
_Avoid_: Installed State、Runtime State

**Collector Runtime State（采集器运行状态）**:
Collector Runtime 对当前 Collector Activation 的阶段、健康结果与失败原因的观察事实。运行状态可随重试和宿主变化而改变，不是用户配置。它同时是该 Instance 交付事实的唯一持久化归属：Last-Known-Good、最近一次读到的 Registry current 及其时间、已安装候选、Approved Collector Package Candidate 与最后一次检查失败都记在这里，管理面的 Current 只是它的投影，不是第二份账本。
_Avoid_: Desired State、Active（后者只回答某 Source 最近是否有流量）

**Artifact Delivery（制品交付）**:
谁负责把 Collector Package 交付给运行位置并维持其版本；它与谁执行代码相互独立。
_Avoid_: Lifecycle Ownership（把交付与执行混成单轴）

**BuiltIn Delivery（内置交付）**:
Collector Package 随宿主应用一起构建和发布的 Artifact Delivery；System Collector 随 Desktop 升级，不进入 Web 更新候选或逐 Instance 包审批。
_Avoid_: 把 System 当作可远程替换的独立 Package、把 BuiltIn 等同于 InProcess

**Official Collector Package Registry（官方采集器包注册源）**:
发布方为非 BuiltIn 官方 Collector Package 提供的版本目录与制品来源；它提供精确候选的发现和下载事实，不保存用户 Desired State，也不承担 Installation、批准或 Activation。来源认证强度是部署能力，不改变 Registry 的领域身份。
_Avoid_: Collector Registry（旧 source 级配置账本）、Analytics 控制面、把 channel 指针当作不可变版本

**Collector Registry Index（采集器注册源索引）**:
Official Collector Package Registry 中某个 Package 的 `current.json`：schema version、PackageId、Version 与
唯一 artifact 的 URL、字节长度、SHA-256。它只回答“当前精确候选在哪、应当是哪些字节”，不重述 Package 身份
（那是 Collector Package manifest 的权威），也不携带 channel、签名、兼容矩阵或发布时间。artifact URL 必须与
Registry base URI 同 origin 且落在该 Package 该 Version 的目录内，redirect 同样受这条边界约束。
_Avoid_: release manifest、channel 指针、把 index 当成 Package 身份或兼容性来源

**Collector Update Offer（采集器更新候选）**:
Runtime 已验证并解析出的某个 Collector Instance 的精确更新候选，绑定 PackageId、Version、内容 hash 与宿主兼容结果；只有 owner 明确批准该精确候选后才可开始该 Instance 的激活尝试。
_Avoid_: latest、opaque workflow token、未验证的 Registry 响应、把发现或下载等同于批准和更新成功

**Approved Collector Package Candidate（已批准采集器包候选）**:
owner 对某个 Collector Instance 明确批准的一个精确候选：PackageId、Version 与 artifact SHA-256 三者逐字段
匹配，且批准时它仍是本机真实的 Collector Installation。批准不解析 latest，也不要求它仍是 Registry current：
已下载验证并安装的候选在 Registry 前进后依然可批准。批准只是取用许可，不是 Ready，也不改写 Desired State、
Activation 或 Last-Known-Good。它随 Collector Runtime State 持久化，重启后仍是同一个已批准 ref。
_Avoid_: latest/channel 批准、opaque offer token 或可重放的审批工作流、把批准当作更新成功

**Collector Package Switch（采集器包切换）**:
owner 明确要求某个 Collector Instance 开始使用其 Approved Collector Package Candidate 的一次尝试，成功条件
只有一个：候选 Activation 到达 Ready。Ready 之前失败（启动失败、握手或声明不兼容、Ready 超时、被取消）不改写
任何交付事实——旧 Package 重新激活、Last-Known-Good 保持原样、批准与 Installation 都还在，只写一条结构化最后
错误等人再次触发；Ready 之后退出是普通运行故障，既不是更新失败也不触发回滚。切换只取用批准时那份逐字段一致
的精确 Installation，不重读 Registry、不解析 channel。它是 per-Instance 的：共享同一 Installation 的另一个
Instance 既不被一起晋升，也不被一起记失败。宿主重启后启动的是"已经到达过 Ready 的那份 Package"，因此从未
Ready 的已批准候选不会靠重启接管，也不会因此出现第二个 Fact Stream writer。
_Avoid_: 自动切换/自动重试、把批准或安装当作切换、候选稳定窗口、Ready 后回滚、跨 Instance 连带晋升

**Manual Collector Update Check（手动采集器更新检查）**:
owner 触发的一次性检查：读一次 Collector Registry Index、下载并按长度与 SHA-256 验证一次、安装一次。它不
调度、不重试、不轮询；失败写入结构化最后错误（reason + 说明 + 时间）并保留既有 Installation、已批准候选与
Last-Known-Good，下一次尝试只由人再次触发。
_Avoid_: 后台 timer/自动检查、失败自动退避重试、把检查或下载等同于批准或 Ready

**Execution Driver（执行驱动器）**:
Collector Runtime 协调 Collector Activation 的方式，可以是进程内、托管进程或外部宿主；它不表示 Runtime 一定持有对应制品。
_Avoid_: Lifecycle Driver（未区分制品交付）、假定 Hub 能直接停止所有 Collector

**App Hint（应用提示）**:
ExternalHost Collector 上报的平台无关产品 slug（如 `edge`）。browser adapter 用非空、稳定的 App Hint 选择对应 Collector Instance，并由平台 adapter 尝试解析为本机可观测的 AppIdentity（Windows 进程或 macOS bundle）；暂时无法解析的稳定 slug 仍形成独立 App Instance 并保留事实，只显示身份未解析，不按名字猜成其他 App。缺失或不稳定的 App Hint 无法形成 Instance 身份，拒绝 Activation。`AppHint` 是 binding/Stream 维度，不进入 canonical Fact payload 或 Analytics 事实；Browser 本地 outbox 可为迁移恢复暂存它。
_Avoid_: 让 Collector 写 `win:`/`mac:` 身份；把 App Hint 当作 App Key 或 AppIdentity

**Upload Stream（上传流）**:
泛化的出网流（ADR-020/022）：绑定一个出网源（IUploadSource），drain 一轮 = 先重传离线缓存，再取 fresh 出网——送达，或落离线缓存，否则自动重注入源（重注入不回滚更新的快照）。"批次不蒸发"是流自持的不变量。compact 为按流策略（segments 出网前压缩快照，input-events 不压缩）。segments 与 input-events 各一实例。
_Avoid_: UploadService（退役的三份同构模板）、Upload Channel（ADR-022 前的旧名，彼时退回项由调用方重注入）

**Segment Rotation Boundary（段轮换边界）**:
连续观察在摄入最大时长之前封口当前 Segment Fact、并从同一 instant 开始新 Fact 的共享边界；轮换前后 union 连续且不重叠。
_Avoid_: 等服务端拒绝后切段、各 Collector 自定阈值、复用旧 FactId

**Active（采集器活跃）**:
旧 source 级读模型术语。统一 Runtime 后，Collector 的实际状态由各 Instance/Activation 的 Runtime State 与 lease 表达；ExternalHost lease 过期只结束对应 Host Activation，不改写 Desired State，也不删除 Package、Instance 或历史 Stream。

**Collector Registry（采集器注册表）**:
历史 source 级配置/声明账本；不再作为 browser 的安装、启用、发现或协议准入来源。新代码应依赖 Collector Installation、Instance Desired State、Runtime State 或更窄的声明存储 seam。
_Avoid_: 为新 Collector 恢复 source 级自动注册/config HTTP 协议

**Current Activity（当前活动）**:
集面读模型中"此刻在干什么"的条目：由 system 采集器在转场点（前台切换、进出 away）把 AppIdentityKey 推送进 hub，进程内事件驱动、零延迟；away 原样暴露为 `sys:away`，语义解释留给消费者。桌面 UI 与 Heartbeat 的唯一数据源。
_Avoid_: 从段流量派生（快照节律 + ≥1s 噪声闸门使派生值在转场后最长 30s 指向上一个 app，ADR-021 否决）

**Heartbeat（心跳）**:
presence 通道：周期 keepalive（活性，间隔为代码常量、不进配置）+ 变了就推（新鲜度），Current Activity 搭车上行。无缓存无重试（易逝信息，下一个心跳自然覆盖）。服务端在线窗口 ≥ 2× keepalive 间隔（ADR-021）。
_Avoid_: Status Upload（旧名，只描述了周期维度）

**Interaction Signal（交互信号）**:
只存在于本机内存中的最近点击信号，用于同窗标题变化的噪声门控；不持久化、不上传。它是缺少专用 Collector 时的可选 fallback，Windows 与 macOS 都由用户独立开关；UI 使用直白名称“点击辅助判断”并说明其 fallback 与本地瞬时性。它与 InputEvent Recording 可以共享底层平台 hook 或系统权限，但启用交互信号不代表允许保存输入事件。
_Avoid_: InputEvent（后者是持久化并上传的事实流）

**前台应用采集（Foreground App Collection）**:
system 采集器不可关闭的基线观测深度：记录当前前台 AppIdentity 与 away 转场。即使所有可选深度能力关闭，system 仍以该基线持续工作。
_Avoid_: 把它呈现为 system 总开关或可关闭的采集能力

**窗口活动采集（Window Activity Collection）**:
system 采集器的一项可选观测深度，统一包含 focused-window 切换与原始窗口标题观测。同窗标题变化是否切段由 Interaction Signal 的点击门控决定，不影响 AppIdentity 激活或 focused-window 切换。
_Avoid_: 把聚焦窗口与标题拆成两个用户能力开关

**采集能力（Collection Capability）**:
某个 Collector 拥有的一项用户可理解观测深度。固定基线没有开关；可选能力把用户的启用意图与实际运行状态分开，权限缺失、撤销或依赖未满足会暂停能力，但不改写用户意图。
_Avoid_: 用一个 bool 同时表示用户开关、权限与实际可用性

**Deactivate（停用采集器）**:
用户把某个 Collector Instance 的 Desired State 设为 `enabled=false`；Collector Runtime 负责停止或拒绝该 Instance 的 Activation，并保留 Installation、Instance 身份与配置。ExternalHost 的停用只约束对应 Instance，不以 Source 级全局开关代替；主卡上的“全部启用/停用”只是对多个 Instance 执行批量变更，不形成另一份 Desired State。

**采集器页（Collector page）**:
共享桌面 UI 中管理采集器的页面，并容纳采集器设置。可管理性**分级**：system 采集器不可停用，前台应用采集作为无开关的固定基线，其他可选观测深度作为独立采集能力管理；外部采集器按 Collector Instance 展示启用意图与由运行事实推导的用户状态。每项能力的开关、实际状态、权限恢复动作与说明都归属该 Collector 条目，不另建脱离所有者的全局“采集能力”区块。窗口活动采集是一个用户能力，不把 focused-window 切换与原始标题拆成两个开关。

browser Collector 只占一个主卡；Chrome、Edge 等 App 作为子项分别展示添加入口、启用意图与“尚未连接 / 等待启动 / 正在采集 / 需要修复”状态，主卡可提供批量启停快捷操作。App Instance 不重复呈现为多份 Package，Profile/Host 也不成为主 UI 管理项；Package、Activation、External Host Identity、目录和协议错误只在高级诊断中展示。
_Avoid_: 采集器栏、Collector panel

**Setup**:
Velopack 生成的安装器（Setup.exe），用户首次安装时下载运行。
_Avoid_: Installer

**Release**:
一次发布产物的集合，包含 Setup、完整包（.nupkg）和元数据文件（RELEASES），托管在 GitHub Releases。
_Avoid_: Build, Package

**Update**:
应用检测到新版本后，下载增量/完整包并在用户确认重启后应用的过程。生命周期为 Idle → UpdateAvailable → Downloading → ReadyToApply，下载失败退回 UpdateAvailable（携带失败原因）；**只有 ReadyToApply 的更新才允许被应用**。"检查"是瞬时动作而非状态，结果三分：UpToDate / UpdateFound / CheckFailed——检查失败 ≠ 已是最新。
_Avoid_: Upgrade, Patch; Pending Update（旧名，混淆了"发现"与"已下载"）

## Relationships

- 一个 **Release** 包含一个 **Setup** 和一个完整包
- **Agent** 在应用生命周期内持续运行，**Update** 需重启应用才能生效
- `Heartbeat.Collection.Hub` 提供纯 .NET 的 hub 运行时（loopback ingest、Collector Registry/declaration、认证客户端、段缓冲、Current Activity、Upload Stream、presence 与缓存 seam），可由桌面或无头 host 组合，不依赖桌面采集、UI、平台 API 或发布供应商
- `Heartbeat.Collection.Headless` 是带 owner-only 管理 API 的无头 Web host：一个 Collector Runtime 从本地配置托管多个 ManagedProcess Collector Instance；深 `HeadlessInstancePipelines` module 按 Instance 吸收投影、当前状态、Analytics 上传身份、缓存与终态 drain，Fleet 不接触这些实现；服务器 Machine 身份不进入 Fact 归属
- `Heartbeat.Collector.System` 消费 App 激活、focused-window 切换、同窗标题变化与 away 等语义观察，产出 system ActivitySegment；平台 adapter 不把原生回调形状泄漏进状态机
- `Heartbeat.Desktop.Updater.Velopack` 统一承载 Windows/macOS 的 Velopack Update 生命周期（检查、下载、重试、ReadyToApply 门控与调度应用），并作为供应商依赖防火墙；platform head 只选择 Release channel，并在 updater 成功启动后停止 Agent、退出当前进程
- `Heartbeat.Desktop.Windows` 组合 Win32 观察、MachineGuid、图标、自启动、共享 Avalonia UI、托盘与 Velopack Update
- `Heartbeat.Desktop.Mac` 组合 NSWorkspace App/硬 away 观察、IOPlatformUUID、bundle 图标、App Hint、共享 Avalonia UI、菜单栏 accessory 生命周期与逐用户 login start
- `Heartbeat.Desktop.UI` 是共享 Avalonia presentation module；ViewModel 只依赖 platform-head seam，可在不创建原生窗口时测试

## Flagged ambiguities

- "安装" 既指首次 Setup 安装，也指更新后的应用替换 — 统一用 **Setup**（首次）和 **Update**（后续）区分。
- "插件" 曾与 **Collector** 混用（口语、UI、ADR-017 中的 "plugin"）— 已统一：唯一规范术语是 **Collector（采集器）**，UI 栏与文档一律用"采集器"。
