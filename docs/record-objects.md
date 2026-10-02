# Record 的对象引用

一条 Record 可以明确引用多个对象。对象不取代 Collector、Track 或 Hub；它提供跨来源的查询维度。决策见 [ADR-0029](adr/ADR-0029-record-object-references.md)。

## 公共信封

上传和 Hub 提交的每条 Record 必须包含 `objects` 数组，可以为空。引用格式：

```json
{
  "role": "application",
  "namespace": "app.macos.bundle_id",
  "key": "com.apple.finder",
  "name": "Finder"
}
```

- `role` 表示对象在本次观测中的角色，不参与对象身份；最长 64 个字符。
- `namespace` 表示原生身份空间，最长 128 个字符。与 `role` 一样只允许小写字母、数字、点、短横线、下划线。
- `key` 是该空间内的原生身份，最长 512 个字符，保留大小写。生产者负责按原生身份规则提供规范形式。
- `name` 是本次观测的可选名称快照，最长 512 个字符；空白等同于 null。
- 字符串去除首尾空白；最多 64 项。同一 `(role, namespace, key)` 不重复；同一身份出现在多个角色时名称必须一致。数组按 namespace、key、role 排序后保存。
- 引用和 value 都是 Record 的固定字段。续期只能延长结束时间，不能修改身份或名称快照；观测到名称变化时创建新 Record。
- 共享定义和规范化实现为 `Heartbeat.Contracts.ObjectReference`。Hub 与后端只检查公共结构，不识别特定 Collector。

## 发现和查询

同一 Timeline 中 `(namespace, key)` 唯一对应一个对象 UUID。Collector 提交原生身份，无须查询或注册 UUID；后端只在接受 Record 时原子发现对象并建立关联。被拒绝的 Record 不产生对象或名称更新。

查询返回的引用额外包含对象 `id`，供路由和后续查询使用；`name` 仍是这条 Record 的历史快照。对象目录的 `name` 则取最新非空名称：比较 `observedAt ?? startedAt`，时间相同按 Record UUID 排序，迟到的旧观测不会覆盖新名称。续期不改变这个观察时间。

对象页 `/objects/{id}` 查询所有直接引用此对象的 Record；重复的 `context` 参数依次记录下钻经过的对象，要求同一 Record 明确引用全部上下文对象。继续下钻保留所有条件；返回上一层或点击路径中的对象时，恢复该层条件；“全部记录”清除全部上下文。从设备进入应用时保留设备条件，移除条件后可看所有设备。没有永久父子树，不做间接关系推理，不把应用前台状态推断为某个游戏账号的活动。

## 已使用的身份空间

| namespace | key | role |
| --- | --- | --- |
| `device` | 当前桌面 Collector 配置的稳定 Target | `device` |
| `app.macos.bundle_id` | 原生 bundle ID | `application` |
| `app.macos.executable_path` | JSON 字符串数组 `[deviceKey, executablePath]`，避免不同设备的同路径误合并 | `application` |
| `vrchat.account` | 原生账号 ID | `account`（主要观测账号）或 `friend` |
| `vrchat.world` | 原生世界 ID | `world` |

同一 bundle ID 跨设备复用对象。设备 Target 必须由同设备的 Collector 共用；跨重装恢复及人工关联仍按 ADR-0003 待设计。对象 UUID 不代表跨平台统一 Application Identity，本轮不建设别名或合并系统。

后端返回对象出现过的去重 roles，不判断对象的展示地位。前端首页展示 `device` 或 `account` 角色出现过的对象，并为未知类型保留入口；已知应用、世界和仅作为好友出现的账号通过完整目录或下钻访问。只有具备前台应用协议的设备展示每日时长和应用数量，其他对象只展示入口。VRChat 账号收进一个展示分组；只作为 friend 出现的账号仍可从完整对象目录和记录进入。这个展示分组不改变记录归属。
