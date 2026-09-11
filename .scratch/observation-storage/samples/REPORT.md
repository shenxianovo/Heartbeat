# PostgreSQL 迁移前后取样

这是构造样本在真实隔离 PostgreSQL 中执行追加迁移后的查询结果，不是生产数据或示意数据。此前测试库按 fixture 生命周期删除，本次重新建库并取样。

采集时间：2026-09-11T04:20:37.8666370+00:00。迁移：`20260911004949_CompleteHistoricalTargets` → `20260911025354_ObservationObjects`。

[原始 before.json](before.json) · [原始 after.json](after.json) · [取样数据 SQL](seed.sql) · [核对脚本](inspect.py)

## 核对结果

- 原 Segments 6 行 + Events 3 行 → Facts 9 行。旧行 Id 一一对应。
- 9/9 的 Owner、StreamId、客户端 FactId、Revision、时间、Collector、Source、AppIdentity 以及完整 JSON 值一致。Payload 只改列名为 Result。
- 新增 Collectors 5、Objects 7、Relations 5、RelationMembers 10。每个关系的对象与有效时间均对照旧证据。
- 原资料仅增加 ObjectId；Streams、Subjects、PersonAssociations 等原内容不变。FactGaps：0 → 0。
- Measurement 尚未有落地生产者，当前 schema 不接收此 Kind，没有伪造 Measurement 样本。

## 每种事实

下表对象名只是对实际 UUID 的可读解析；原 UUID 在后文及原始 JSON 中保留。

| 样本 | 之前 | 现在 FoiId → Object | Aspect | 关系 |
| --- | --- | --- | --- | --- |
| System 桌面活动 | Segments；device:101 | machine:Mac1 | desktop-activity | app=app:Chrome, device=machine:Mac1 |
| System 键盘事件 | Events；device:101 | machine:Mac1 | input | 未生成事实专属设备关系 |
| Browser / Mac1 | Segments；application-context:221 | app:Chrome | selected-page | app=app:Chrome, device=machine:Mac1 |
| Browser / Windows1 | Segments；application-context:222 | app:Chrome | selected-page | app=app:Chrome, device=machine:Windows1 |
| VRChat 已知账号 | Segments；account:301 | account:usr_11111111-1111-4111-8111-111111111111 | account-location | 未生成事实专属设备关系 |
| VRChat 未知账号/Collector | Segments；account:302 | account:00000000-0000-0000-0000-00000000012e | account-location | 未生成事实专属设备关系 |
| Person 自定义事件 | Events；person:401 | person:00000000-0000-0000-0000-000000000191 | null | 未生成事实专属设备关系 |
| 未知对象和内容 | Events；null:null | null | null | 未生成事实专属设备关系 |
| System 无 App 的状态 | Segments；device:101 | machine:Mac1 | desktop-activity | 未生成事实专属设备关系 |

## Objects：四类对象

| 原资料 | 新 UUID | Kind / Scope / Key | Owner |
| --- | --- | --- | --- |
| Devices#101 | `978d698a-186b-4cb4-9f42-d08da6af9755` | machine / heartbeat.device / sample-mac-1 | sample-owner |
| Devices#102 | `69dc2eeb-8152-48f9-a458-f8dc5d35f284` | machine / heartbeat.device / sample-windows-1 | sample-owner |
| Apps#201 | `54be2b58-dad3-4155-80f5-12224976875b` | app / heartbeat.app / chrome | null（全局 App） |
| Apps#202 | `c577bf68-294a-41a7-9228-f0c67a511793` | app / heartbeat.app / vrchat | null（全局 App） |
| ServiceAccounts#301 | `047079bd-d4f1-4d1c-9e8e-f5c454cc6fbf` | account / vrchat / usr_11111111-1111-4111-8111-111111111111 | sample-owner |
| ServiceAccounts#302 | `aee8320a-1a84-44b6-94f5-2d704f1adce1` | account / vrchat / 00000000-0000-0000-0000-00000000012e | sample-owner |
| Persons#401 | `da599cfa-386d-4432-a942-8b3bc9ea2ea3` | person / heartbeat.person / 00000000-0000-0000-0000-000000000191 | sample-owner |

账号缺真实 ID 时使用原 SubjectId，仍在 vrchat 作用域，Name 为 null；没有新增 legacy 命名空间。

## Collectors

| 原 ObserverId | 新 Collectors.Id | Kind |
| --- | --- | --- |
| `00000000-0000-0000-0000-000000000259` | 同值 | system |
| `00000000-0000-0000-0000-00000000025a` | 同值 | browser |
| `00000000-0000-0000-0000-00000000025b` | 同值 | browser |
| `00000000-0000-0000-0000-00000000025c` | 同值 | vrchat.account |
| `00000000-0000-0000-0000-00000000025d` | 同值 | journal |

ObserverId 为 null 的两条事实仍是 CollectorId=null，没有创建虚构观测者。

## Relations 与 RelationMembers

| Relation | Kind | Evidence | Members | 时间 |
| --- | --- | --- | --- | --- |
| `609900e1-4c70-47ed-88e0-8022f1cd0f94` | observed-on | `{"factId": "00000000-0000-0000-0000-0000000003ec"}` | app=app:Chrome; device=machine:Windows1 | 2026-09-11T01:00:00+00:00 → 2026-09-11T01:10:00+00:00 |
| `70a5dad7-d36b-4910-95fc-56fe10955ba5` | observed-on | `{"factId": "00000000-0000-0000-0000-0000000003e9"}` | app=app:Chrome; device=machine:Mac1 | 2026-09-11T01:00:00+00:00 → 2026-09-11T01:10:00+00:00 |
| `b3083238-be86-4f28-bc4c-15a30644a2f1` | used-by | `{"associationId": 502}` | account=account:usr_11111111-1111-4111-8111-111111111111; person=person:00000000-0000-0000-0000-000000000191 | 2026-09-11T01:00:00+00:00 → 2026-09-11T02:00:00+00:00 |
| `c569f430-5b35-4472-9cb2-1990cc8c278e` | used-by | `{"associationId": 501}` | device=machine:Mac1; person=person:00000000-0000-0000-0000-000000000191 | 2026-09-11T01:00:00+00:00 → 2026-09-11T02:00:00+00:00 |
| `cced7b94-2383-4f2b-9974-8c670b9c9454` | observed-on | `{"factId": "00000000-0000-0000-0000-0000000003eb"}` | app=app:Chrome; device=machine:Mac1 | 2026-09-11T01:00:00+00:00 → 2026-09-11T01:10:00+00:00 |

三条 observed-on 各自绑定 System/Mac Browser/Windows Browser 的准确 Fact。两条 used-by 来自原 PersonAssociations。无设备证据的 VRChat 事实没有被关联到 Mac、Windows 或 Quest。

## 逐条原始 diff

以下仅按字段名排序以方便阅读，没有改写字段值。`TargetKind/TargetId/Source/StreamId/FactId/AppIdentityId` 仍实际存在；本轮是存储迁移，没有同时替换上传协议和平台身份目录。

### System 桌面活动

```diff
--- Segments/00000000-0000-0000-0000-0000000003e9
+++ Facts/00000000-0000-0000-0000-0000000003e9
@@ -1,11 +1,14 @@
 {
   "AppIdentityId": 211,
+  "Aspect": "desktop-activity",
+  "CollectorId": "00000000-0000-0000-0000-000000000259",
   "EndTime": "2026-09-11T01:10:00+00:00",
   "FactId": "00000000-0000-0000-0000-0000000007d1",
+  "FoiId": "978d698a-186b-4cb4-9f42-d08da6af9755",
   "Id": "00000000-0000-0000-0000-0000000003e9",
-  "ObserverId": "00000000-0000-0000-0000-000000000259",
+  "Kind": "segment",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "activityKey": "chrome",
     "appIdentityKey": "mac:com.google.chrome",
     "extra": {
```

### System 键盘事件

```diff
--- Events/00000000-0000-0000-0000-0000000003ea
+++ Facts/00000000-0000-0000-0000-0000000003ea
@@ -1,10 +1,14 @@
 {
   "AppIdentityId": null,
+  "Aspect": "input",
+  "CollectorId": "00000000-0000-0000-0000-000000000259",
+  "EndTime": null,
   "FactId": "00000000-0000-0000-0000-0000000007d2",
+  "FoiId": "978d698a-186b-4cb4-9f42-d08da6af9755",
   "Id": "00000000-0000-0000-0000-0000000003ea",
-  "ObserverId": "00000000-0000-0000-0000-000000000259",
+  "Kind": "event",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "code": 65,
     "codeSet": "windows-vk-v1",
     "eventType": "keyDown",
@@ -12,8 +16,8 @@
   },
   "Revision": 3,
   "Source": "system",
+  "StartTime": "2026-09-11T01:05:00+00:00",
   "StreamId": "00000000-0000-0000-0000-000000000bba",
   "TargetId": 101,
-  "TargetKind": "device",
-  "Timestamp": "2026-09-11T01:05:00+00:00"
+  "TargetKind": "device"
 }
```

### Browser / Mac1

```diff
--- Segments/00000000-0000-0000-0000-0000000003eb
+++ Facts/00000000-0000-0000-0000-0000000003eb
@@ -1,11 +1,14 @@
 {
   "AppIdentityId": 211,
+  "Aspect": "selected-page",
+  "CollectorId": "00000000-0000-0000-0000-00000000025a",
   "EndTime": "2026-09-11T01:10:00+00:00",
   "FactId": "00000000-0000-0000-0000-0000000007d3",
+  "FoiId": "54be2b58-dad3-4155-80f5-12224976875b",
   "Id": "00000000-0000-0000-0000-0000000003eb",
-  "ObserverId": "00000000-0000-0000-0000-00000000025a",
+  "Kind": "segment",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "activityKey": "https://example.com/docs",
     "attributes": {
       "url": "https://example.com/docs",
```

### Browser / Windows1

```diff
--- Segments/00000000-0000-0000-0000-0000000003ec
+++ Facts/00000000-0000-0000-0000-0000000003ec
@@ -1,11 +1,14 @@
 {
   "AppIdentityId": 212,
+  "Aspect": "selected-page",
+  "CollectorId": "00000000-0000-0000-0000-00000000025b",
   "EndTime": "2026-09-11T01:10:00+00:00",
   "FactId": "00000000-0000-0000-0000-0000000007d4",
+  "FoiId": "54be2b58-dad3-4155-80f5-12224976875b",
   "Id": "00000000-0000-0000-0000-0000000003ec",
-  "ObserverId": "00000000-0000-0000-0000-00000000025b",
+  "Kind": "segment",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "activityKey": "https://example.com/work",
     "attributes": {
       "url": "https://example.com/work",
```

### VRChat 已知账号

```diff
--- Segments/00000000-0000-0000-0000-0000000003ed
+++ Facts/00000000-0000-0000-0000-0000000003ed
@@ -1,11 +1,14 @@
 {
   "AppIdentityId": null,
+  "Aspect": "account-location",
+  "CollectorId": "00000000-0000-0000-0000-00000000025c",
   "EndTime": "2026-09-11T01:10:00+00:00",
   "FactId": "00000000-0000-0000-0000-0000000007d5",
+  "FoiId": "047079bd-d4f1-4d1c-9e8e-f5c454cc6fbf",
   "Id": "00000000-0000-0000-0000-0000000003ed",
-  "ObserverId": "00000000-0000-0000-0000-00000000025c",
+  "Kind": "segment",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "activityKey": "world|instance",
     "instanceId": "instance",
     "title": "World",
```

### VRChat 未知账号/Collector

```diff
--- Segments/00000000-0000-0000-0000-0000000003ee
+++ Facts/00000000-0000-0000-0000-0000000003ee
@@ -1,11 +1,14 @@
 {
   "AppIdentityId": null,
+  "Aspect": "account-location",
+  "CollectorId": null,
   "EndTime": "2026-09-11T01:10:00+00:00",
   "FactId": "00000000-0000-0000-0000-0000000007d6",
+  "FoiId": "aee8320a-1a84-44b6-94f5-2d704f1adce1",
   "Id": "00000000-0000-0000-0000-0000000003ee",
-  "ObserverId": null,
+  "Kind": "segment",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "activityKey": "world-old|instance-old",
     "extra": {
       "kept": true
```

### Person 自定义事件

```diff
--- Events/00000000-0000-0000-0000-0000000003ef
+++ Facts/00000000-0000-0000-0000-0000000003ef
@@ -1,10 +1,14 @@
 {
   "AppIdentityId": null,
+  "Aspect": null,
+  "CollectorId": "00000000-0000-0000-0000-00000000025d",
+  "EndTime": null,
   "FactId": "00000000-0000-0000-0000-0000000007d7",
+  "FoiId": "da599cfa-386d-4432-a942-8b3bc9ea2ea3",
   "Id": "00000000-0000-0000-0000-0000000003ef",
-  "ObserverId": "00000000-0000-0000-0000-00000000025d",
+  "Kind": "event",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "entry": "Went for a walk",
     "extra": 17,
     "tags": [
@@ -13,8 +17,8 @@
   },
   "Revision": 3,
   "Source": "journal",
+  "StartTime": "2026-09-11T01:05:00+00:00",
   "StreamId": "00000000-0000-0000-0000-000000000bbf",
   "TargetId": 401,
-  "TargetKind": "person",
-  "Timestamp": "2026-09-11T01:05:00+00:00"
+  "TargetKind": "person"
 }
```

### 未知对象和内容

```diff
--- Events/00000000-0000-0000-0000-0000000003f0
+++ Facts/00000000-0000-0000-0000-0000000003f0
@@ -1,10 +1,14 @@
 {
   "AppIdentityId": null,
+  "Aspect": null,
+  "CollectorId": null,
+  "EndTime": null,
   "FactId": "00000000-0000-0000-0000-0000000007d8",
+  "FoiId": null,
   "Id": "00000000-0000-0000-0000-0000000003f0",
-  "ObserverId": null,
+  "Kind": "event",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "opaque": {
       "a": [
         1,
@@ -15,8 +19,8 @@
   },
   "Revision": 3,
   "Source": "custom",
+  "StartTime": "2026-09-11T01:05:00+00:00",
   "StreamId": "00000000-0000-0000-0000-000000000bc0",
   "TargetId": null,
-  "TargetKind": null,
-  "Timestamp": "2026-09-11T01:05:00+00:00"
+  "TargetKind": null
 }
```

### System 无 App 的状态

```diff
--- Segments/00000000-0000-0000-0000-0000000003f1
+++ Facts/00000000-0000-0000-0000-0000000003f1
@@ -1,11 +1,14 @@
 {
   "AppIdentityId": null,
+  "Aspect": "desktop-activity",
+  "CollectorId": "00000000-0000-0000-0000-000000000259",
   "EndTime": "2026-09-11T01:10:00+00:00",
   "FactId": "00000000-0000-0000-0000-0000000007d9",
+  "FoiId": "978d698a-186b-4cb4-9f42-d08da6af9755",
   "Id": "00000000-0000-0000-0000-0000000003f1",
-  "ObserverId": "00000000-0000-0000-0000-000000000259",
+  "Kind": "segment",
   "OwnerId": "sample-owner",
-  "Payload": {
+  "Result": {
     "activityKey": "away",
     "title": "Away"
   },
```
