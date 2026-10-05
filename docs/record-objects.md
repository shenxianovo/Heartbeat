# Record 的对象引用

全应用共用 `objects` 中的 UUIDv7 与 Owner。Timeline、Collector、Track、Record、Hub，以及观测涉及的设备、应用、账号和世界，都使用这套身份。业务类型保留自己的字段，不要求每个对象都有名称或原生标识。决策见 [ADR-0030](adr/ADR-0030-unified-object-identity.md)。

## 公共信封

Hub 提交和上传中的每条 Record 必须包含 `objects` 数组，可以为空。生产者有两种指认方式：提供已登记对象的 `id`，或提供原生标识 `namespace` 与 `key`。也可以一起提供，为该对象补充识别地址。

```json
[
  { "role": "device", "namespace": "device", "key": "mac-a" },
  {
    "role": "application",
    "namespace": "app.macos.bundle_id",
    "key": "com.apple.finder",
    "name": "Finder",
    "scope": { "namespace": "device", "key": "mac-a" }
  }
]
```

| 字段 | 含义与限制 |
| --- | --- |
| `role` | 本次观测中的角色，最长 64 字符；不参与对象身份 |
| `id` | 可选的已登记 UUIDv7；必须属于认证 Owner |
| `namespace` | 与 key 一起提供的识别空间，最长 128 字符 |
| `key` | 识别空间内的原生标识，最长 512 字符，保留大小写 |
| `scope` | 可选的识别作用域，以另一对象的全局 namespace/key 指定；该对象必须同时在本条 Record 中被无作用域地引用 |
| `name` | 本次观测中的可选名称快照，最长 512 字符；空白等同于 null |

role 和 namespace 只允许小写字母、数字、点、短横线和下划线。字符串去除首尾空白。id 可以单独指认对象；namespace/key 必须成对；scope 只能用于原生标识，不能单独提供。当前 scope 只接受一层全局识别地址。

最多 64 项；同一 `(role, id, scope, namespace, key)` 不重复。同一指认方式在多个角色中出现时，名称必须一致。数组依次按 scope 的 namespace/key、引用的 namespace/key、id、role 排序；字符串按序号比较，空值在前。不同别名保留各自的历史快照。

共享定义与规范化实现为 `Heartbeat.Contracts.ObjectReference`。Hub 与后端只检查公共结构。引用与 value 都是 Record 的固定字段；名称或指认方式发生变化时创建新 Record，续期只延长结束时间。

## 发现与名称

`objects` 只保存身份与归属。`object_bindings` 将 `(Owner, scope 对象 ID, namespace, key)` 映射到对象；没有 scope 时在 Owner 内解析。一个地址只指向一个对象，一个对象可以有多个地址。

没有 UUID 的生产者可以直接提交识别依据。后端接受 Record 时才发现并登记对象。提供 UUID 时，未知或无权限的身份会被拒绝；同时提供的地址若已指向另一对象，也会被拒绝。发现、Record、名称及关联在同一事务提交，失败不会留下部分对象或覆盖旧记录。

`records.objects` 是原始声明和历史名称的权威。`record_objects` 按引用序号保存所解析的 UUID，查询时使用这份历史快照。`object_descriptions` 是当前展示投影：最新非空名称按 `observedAt ?? startedAt` 和 Record UUID 比较。空名称不清除已知名称，迟到旧观测不覆盖新名称；同一 Record 的多个别名解析到同一对象时，按名称的字符顺序选择最前的非空值展示。

## 查询与下钻

通用对象查找直接按 UUID 工作，不要求 namespace/key。目录包含 Timeline、Collector、Track、Hub 和被观测明确引用的对象，并按 UUID 分页。Record 身份可直接查找和引用，默认不逐条进入目录；被其他观测明确引用后也进入目录。

Record 的对象关系包括显式引用，以及 Record 本身、所属 Track、Collector、Timeline。来源由原有归属链确定，不向生产者的 objects 冗余来源。Hub 接管或在线状态不会自动创建观测关联。

对象页 `/objects/{id}` 查询符合条件的原始 Record。重复的 `context` 参数保存本次下钻经过的对象，所有条件必须在同一 Record 上成立。返回上一层恢复该层条件；“全部记录”清除条件。路径不构成永久父子树，不通过时间重叠或应用状态推断账号关系。

## 已使用的识别空间

| namespace | key | scope | role |
| --- | --- | --- | --- |
| `device` | 桌面 Collector 配置的稳定 Target | 无 | `device` |
| `app.macos.bundle_id` | 原生 bundle ID | 设备对象 | `application` |
| `app.macos.executable_path` | 原生可执行文件路径 | 设备对象 | `application` |
| `app.windows.executable_path` | 原生可执行文件路径 | 设备对象 | `application` |
| `vrchat.account` | 原生账号 ID | 无 | `account` 或 `friend` |
| `vrchat.world` | 原生世界 ID | 无 | `world` |

同设备的 Collector 共用设备 Target，复用该设备上的应用对象。不同设备上的同款应用具有不同对象身份；产品标识相同不证明对象相同。跨重装恢复及人工关联仍按 ADR-0003 待设计。

后端返回对象曾出现的去重 roles，不赋予对象全局展示等级。前端首页按设备、账号等角色组织观测入口；完整目录可查其他对象。具体协议决定摘要是否适用，不能把缺少观测显示为零活动。
