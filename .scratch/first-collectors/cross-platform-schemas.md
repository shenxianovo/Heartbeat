# 跨平台桌面观测定义草案

四份跨平台结构草案现已按用户要求分别写入文档站，见 [Observers 概览](../../src/Docs/Heartbeat.Docs/content/docs/observers/index.mdx#目标与职责)，以站内页面为审阅依据。本文件保留讨论笔记和待确认事项，不作为已确认的模型。

## 已确认

- 前台状态、输入事件和设备状态分别负责观测，可以运行在同一程序中。
- 前台状态使用应用和标题两份 Schema，各自保存时间线。
- 相同事实、字段、约束与时间规则共用平台无关的 SchemaId。
- 应用使用先表示前台时长；标题时段、键鼠事件时间点和设备休眠／唤醒状态属于另外的事实。
- 应用读数完整相同且采集连续时延长同一段，任一字段变化时新开一段，每段引用的数据保持一致。软件归类由查询规则处理。
- 当前已实现的单次应用读数字段为 bundleIdentifier、name、executablePath。用户提出用 0～N 个带 Scheme 的标识线索替代单一标识，并要求先写入 Schema 文档供审阅。下方列表结构已作为草案写入应用时段页面，未修改代码或数据库定义。

## 整体方案

| 定义 | 事实时间 | 数据设计状态 |
| --- | --- | --- |
| ForegroundApplication | 时间段 | 提议 name、identifiers；路径也作为有 Scheme 的线索保存 |
| ForegroundWindowTitle | 时间段 | 站内草案 title；标题边界及缺失信息表达待确认 |
| InputEvent | 时间点 | 站内草案 kind；键鼠事件类别、细节和记录粒度待确认 |
| DeviceState | 时间段 | 站内草案 state；状态值与边界待确认，用户活跃不与设备清醒混用 |

以上名称为讨论用名，未分配固定 ID。ObserverId、DataId、SchemaId 与事实时间继续由现有实体表达，不重复加入领域数据。

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
- name 建议保持字符串或 null。当前来源名称可为空，不通过编造名称补齐；用户提供的示例为必需的非空名称，这一差异需要确认。
- 同 Scheme、同值不直接承诺跨设备或跨平台软件等价。路径是本地线索；包族标识与具体应用标识的粒度不同。采集层不选 PrimaryIdentifier、不设置强弱权重、不做软件归一化。
- 完整读数分段的原则继续适用。列表顺序是否影响相等、重复项处理、线索增减是否新开段等规则待确认，避免采集返回顺序变化造成无意义分段。
- 不引入各平台独立 Schema 或 platform 扩展袋；线索列表保留多种实际证据。

## 平台依据

- Apple NSRunningApplication 的 bundleIdentifier 表示 CFBundleIdentifier；localizedName 是本地化显示名，executableURL 指向可执行文件：[Apple 应用属性](https://developer.apple.com/documentation/appkit/nsrunningapplication/localizedname)。
- Windows AppUserModelID 用于把进程、窗口和文件关联到应用，具有应用模型语义，不能当成 macOS bundle ID：[Microsoft System.AppUserModel.ID](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)。本提案不承诺每个窗口都能取得该值。
- Windows 显式 AppUserModelID 是可选的；系统分配的内部 AppUserModelID 不能由应用取回：[Application-Defined and System-Defined AppUserModelIDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids#application-defined-and-system-defined-appusermodelids)。不存在可取回 ID 与缺少 stable ID 不是相同判断，本提案不要求每次读取都取得 AUMID。
- Package Family Name 由包名和发布者派生。包身份与应用身份不同，同一个包可以包含多个应用；因此包族相同不能单独证明具体应用相同：[Package identity vs application identity](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview#package-identity-vs-application-identity)。
- Desktop File ID 根据桌面条目文件位置形成，属于桌面条目标识，不等同于任意 Linux 窗口的可用标识：[Desktop Entry Specification](https://xdg.pages.freedesktop.org/xdg-specs/desktop-entry/latest-single/#desktop-file-id)。本提案不把 Wayland app_id 或 X11 WM_CLASS 自动当成 Desktop File ID。

## 下一项确认

用户要求将 name: string | null 和 identifiers: { scheme, value }[] 的具体结构先写入 Schema 文档供审阅。页面已给出字段、JSON 结构与示例，并标为草案。现有代码和时间点定义仍使用原三个字段；时段草案的 Scheme、字段约束与列表相等规则尚需确认。

## ActivityWatch 对照

核对 `aw-watcher-window` 提交 `c8814e49780ec735caba0bca15982ef28d71900b` 的标准前台窗口路径，未运行 AW：

- currentwindow 的基本数据是 app 和 title。macOS Swift 的 NetworkMessage 另有可选 url；没有 identifiers 列表。
- macOS Swift 的 app 使用 localizedName，缺失时回退到 bundleIdentifier，再缺失时使用空字符串；并没有把两项同时保留。
- Windows 的 app 从可执行路径取 basename，失败时尝试 WMI 的进程 Name，再失败使用 unknown。最终窗口事件不保留查询过的完整路径或 AUMID。
- Linux X11 路径的 app 使用窗口 class；不同平台共用字符串字段，但值的来源语义不同。
- AW 的相同完整 data 在合并容差内延长事件；应用和标题在一份 data 中，因此标题变化也会切开事件。

来源：[数据模型](https://docs.activitywatch.net/en/latest/buckets-and-events.html)、[Windows 名称](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/windows.py#L26-L33)、[平台返回值](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/lib.py#L6-L60)、[macOS 结构](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L58-L62)、[macOS 值选择](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L559-L560)、[heartbeat 合并](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_transform/heartbeats.py)。

从这些实现推断，AW 的标准模型主要提供可统计和展示的应用标签，没有完成跨平台应用身份统一。Heartbeat 是否保存多线索及 nullable name 仍待确认，不能因 AW 采用字符串字段而自动改变已有决策。
