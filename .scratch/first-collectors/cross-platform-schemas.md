# 跨平台桌面观测定义草案

四份观测定义已分别写入文档站。应用身份改为观测内容、本机应用实体与跨平台应用实体分层，见 [Observers 概览](../../src/Docs/Heartbeat.Docs/content/docs/observers/index.mdx#目标与职责)，以站内页面为审阅依据。本文件保留讨论笔记和待确认事项，不作为已确认的模型。

## 已确认

- 前台状态、输入事件和设备状态分别负责观测，可以运行在同一程序中。
- 前台状态使用应用和标题两份 Schema，各自保存时间线。
- 相同事实、字段、约束与时间规则共用平台无关的 SchemaId。
- 应用使用先表示前台时长；标题时段、键鼠事件时间点和设备休眠／唤醒状态属于另外的事实。
- 应用读数完整相同且采集连续时延长同一段，任一字段变化时新开一段，每段引用的数据保持一致。软件归类由查询规则处理。
- 当前已实现的单次应用读数字段为 bundleIdentifier、name、executablePath。用户提出用 0～N 个带 Scheme 的标识线索替代单一标识，并要求先写入 Schema 文档供审阅。列表数据结构已确认并写入应用时段页面，未修改代码或数据库定义。

## 应用身份分层（当前决定）

- 本机应用实体表示某台设备上的一个应用安装。
- 两台 Mac 上的 VS Code 分别拥有本机应用实体，可以关联到同一个跨平台应用实体。
- Observer 维护本机应用身份。跨平台对应关系在采集后关联。
- identifiers 移到本机应用实体。观测保留 localApplicationId 与当时读到的 name，字段和命名已确认。
- 下方 name 加 identifiers 的 JSON 保留为前一轮讨论材料，不作为最终观测契约。

## 通用实体引用

Core 将统一的实体引用枚举直接纳入 IEntity 契约。普通实体引用参与关系图。业务定义关系，Core 不提供通用 Relation 基类。需要独立关系记录时，业务定义普通实体；需要时间边界时实现 ITimed。没有引用的实体返回空集合。规则见站内 IEntity 的“约定”节与 ADR 0009；Core 主图中 IEntity 关联独立的 Id 与 GetReferences() 节点。

GetReferences() 返回 IEnumerable<EntityId>，只枚举目标标识，不返回字段名或关系名称，也不读取目标实体。Core 与当前采集数据已实现。传输方式和反向查询仍需逐项确定。LocalApplication.deviceId 与独立 Device 实体尚未采用，设备范围的表示继续讨论。

## 整体方案

| 定义 | 事实时间 | 数据设计状态 |
| --- | --- | --- |
| ForegroundApplication | 时间段 | localApplicationId 与 name，字段已确认；identifiers 移到本机应用实体 |
| ForegroundWindowTitle | 时间段 | 站内草案 title；标题边界及缺失信息表达待确认 |
| InputEvent | 时间点 | 站内草案 kind；键鼠事件类别、细节和记录粒度待确认 |
| DeviceState | 时间段 | 站内草案 state；状态值与边界待确认，用户活跃不与设备清醒混用 |

以上名称为讨论用名，未分配固定 ID。ObserverId、ContentId、SchemaId 与事实时间继续由现有实体表达，不重复加入领域数据。

## 应用标识提案

```json
{
  "name": "Safari",
  "identifiers": [
    {
      "scheme": "macos.bundle-id",
      "value": "com.apple.Safari"
    },
    {
      "scheme": "macos.executable-path",
      "value": "/Applications/Safari.app/Contents/MacOS/Safari"
    }
  ]
}
```

- identifiers 是 0～N 个 { scheme, value } 对象组成的数组，无法取得线索时保存 []，不要求一个稳定平台 ID。空数组表示本次未取得线索，不断言应用没有任何标识。
- scheme 标明线索的语义体系，value 保留来源值，不生成伪造的统一应用标识。
- 示例 Scheme：macos.bundle-id、macos.executable-path、windows.app-user-model-id、windows.executable-path、windows.package-family-name、freedesktop.desktop-file-id。支持哪些 Scheme 及各自的解释范围待确认。
- name 为字符串或 null，分层后继续保留在观测内容中。
- 同 Scheme、同值不直接承诺跨设备或跨平台软件等价。路径是本地线索；包族标识与具体应用标识的粒度不同。采集层不选 PrimaryIdentifier、不设置强弱权重、不做软件归一化。
- 完整读数分段的原则继续适用。列表比较必须忽略条目顺序。重复项处理与线索变化的具体分段规则待确认。
- 不引入各平台独立 Schema 或 platform 扩展袋；线索列表保留多种实际证据。

## 平台依据

- Apple NSRunningApplication 的 bundleIdentifier 表示 CFBundleIdentifier；localizedName 是本地化显示名，executableURL 指向可执行文件：[Apple 应用属性](https://developer.apple.com/documentation/appkit/nsrunningapplication/localizedname)。
- Windows AppUserModelID 用于把进程、窗口和文件关联到应用，具有应用模型语义，不能当成 macOS bundle ID：[Microsoft System.AppUserModel.ID](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)。本提案不承诺每个窗口都能取得该值。
- Windows 显式 AppUserModelID 是可选的；系统分配的内部 AppUserModelID 不能由应用取回：[Application-Defined and System-Defined AppUserModelIDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids#application-defined-and-system-defined-appusermodelids)。不存在可取回 ID 与缺少 stable ID 不是相同判断，本提案不要求每次读取都取得 AUMID。
- Package Family Name 由包名和发布者派生。包身份与应用身份不同，同一个包可以包含多个应用；因此包族相同不能单独证明具体应用相同：[Package identity vs application identity](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview#package-identity-vs-application-identity)。
- Desktop File ID 根据桌面条目文件位置形成，属于桌面条目标识，不等同于任意 Linux 窗口的可用标识：[Desktop Entry Specification](https://xdg.pages.freedesktop.org/xdg-specs/desktop-entry/latest-single/#desktop-file-id)。本提案不把 Wayland app_id 或 X11 WM_CLASS 自动当成 Desktop File ID。

## 已确认观测字段

前台应用观测的 观测内容 包含 localApplicationId 与 name。localApplicationId 引用设备内的 LocalApplication（本机应用）实体，name 保留本次读到的名称，未知时为 null。起止时间继续由 Observation 保存。Entities 分区下新增本机应用独立页面。

## ActivityWatch 对照

核对 `aw-watcher-window` 提交 `c8814e49780ec735caba0bca15982ef28d71900b` 的标准前台窗口路径，未运行 AW：

- currentwindow 的基本数据是 app 和 title。macOS Swift 的 NetworkMessage 另有可选 url；没有 identifiers 列表。
- macOS Swift 的 app 使用 localizedName，缺失时回退到 bundleIdentifier，再缺失时使用空字符串；并没有把两项同时保留。
- Windows 的 app 从可执行路径取 basename，失败时尝试 WMI 的进程 Name，再失败使用 unknown。最终窗口事件不保留查询过的完整路径或 AUMID。
- Linux X11 路径的 app 使用窗口 class；不同平台共用字符串字段，但值的来源语义不同。
- AW 的相同完整 data 在合并容差内延长事件；应用和标题在一份 data 中，因此标题变化也会切开事件。

来源：[数据模型](https://docs.activitywatch.net/en/latest/buckets-and-events.html)、[Windows 名称](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/windows.py#L26-L33)、[平台返回值](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/lib.py#L6-L60)、[macOS 结构](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L58-L62)、[macOS 值选择](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L559-L560)、[heartbeat 合并](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_transform/heartbeats.py)。

从这些实现推断，AW 的标准模型主要提供可统计和展示的应用标签，没有完成跨平台应用身份统一。Heartbeat 已确认保存多线索及 nullable name，不能因 AW 采用字符串字段而自动改变已有决策。
