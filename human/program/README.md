# 程序模型（ProgramModel）

[返回阅读树](../../HUMAN.md) · [实体](../entities/README.md) · [观测](../observations/README.md) · [场景检验](model-check/README.md)

本文记录当前程序草案。程序模型从上层信息模型继续推导。现有代码中的类型和组件不作为推导前提。

## 核心接口

```csharp
public readonly record struct EntityId(Guid Value)
{
    public static EntityId New() => new(Guid.CreateVersion7());
}

public interface IEntity
{
    EntityId Id { get; }
}

public interface ITimed
{
    DateTimeOffset? StartAt { get; }
    DateTimeOffset? EndAt { get; }
}
```

`EntityId` 包装 `Guid`，表达 UUIDv7 实体标识。创建新的实体表示时分配一次标识。标识分配后保持不变。UUIDv7 中的时间字段不作为描述时间或获取时间的依据。

包装类型与接口本身不保证版本和非空约束。具体构造与入口校验待实现。

实体通过组合与接口表达共同能力，不建立实体共同父类。只有具有时间语义的类型实现 `ITimed`。

## 时间边界

| StartAt | EndAt | 含义 |
| --- | --- | --- |
| `null` | `null` | 两个边界均未知 |
| `t` | `t` | 时间点 |
| `t1` | `t2`，且 `t1 < t2` | 时间段 |
| `t` | `null` | 已知开始，结束未知 |
| `null` | `t` | 开始未知，已知结束 |

`null` 只表示未知。时间字段不表达正在持续、已经结束、失败或不存在。两者均已知时，必须满足 `StartAt <= EndAt`。

具体状态由业务信息表达。日期精度、相对时间、媒体位置及明确无界等含义由具体内容保留，不用未知边界冒充。

## 观测（Observation）

每份被保留的观测都是实体。观测标识就是该观测的实体标识。

```text
Observation : IEntity, ITimed
├── Id: EntityId
├── ObserverId: EntityId
├── DataId: EntityId
├── SchemaId: EntityId
├── StartAt: DateTimeOffset?
└── EndAt: DateTimeOffset?
```

`ObserverId` 指向取得这份观测的具体来源。`DataId` 可以指向任意 `IEntity`，不要求 Data 基类。`SchemaId` 指向解释这份观测内容的定义。

观测上的时间边界表达描述时间。获取时间与描述时间仍是不同的信息；获取时间的统一程序表示待推导。

当前 Observation 不声明通用状态字段。运行、完成或失败等状态由具体内容或过程实体表达。

实体身份不保证实体属性保持不变。DataId 怎样指向可找回的历史内容，是[场景检验](model-check/README.md#历史内容)暴露的待确认约定。

## 解释定义（ObservationSchema）

```text
ObservationSchema : IEntity, ITimed
├── Id: EntityId
├── Name: string
├── Schema: JsonElement
├── StartAt: DateTimeOffset?
└── EndAt: DateTimeOffset?
```

`Name` 提供可读名称。`Schema` 保存解释定义，包括内容表示规则和信息含义。当前不限定为某一 JSON Schema 标准。

被引用的解释必须可找回。定义变化时怎样保留旧解释，以及 Schema 自身时间边界的具体含义，见[检验记录](model-check/README.md#schema-解释)。

## 观测者（Observer）

```text
Observer : IEntity
├── Id: EntityId
└── Name: string
```

Observer 是取得观测的具体来源。Observer 不因所取得的信息带时间而必须实现 `ITimed`。

## 关系（Relation）

```text
Relation : IEntity, ITimed
├── Id: EntityId
├── FromId: EntityId
├── ToId: EntityId
├── StartAt: DateTimeOffset?
└── EndAt: DateTimeOffset?
```

Relation 是关系类型的共同抽象类。具体关系按业务含义定义，例如 ContainsRelation、OwnsRelation。具体类型可以增加业务字段。

关系自身也是实体，可以成为观测的 DataId。关系的参与者、方向和时间含义由具体关系定义。多元关系采用二元关系时，必须保留同一次关系的共同锚点和参与角色；具体表达见[检验记录](model-check/README.md#多元关系)。

Contains 不自动表达顺序、单父级或无环。业务需要这些性质时，应明确保存或校验。

## 表达检验与下一步

[场景检验](model-check/README.md)记录 main 的 8 个业务场景与 18 个额外场景。这是独立模型演练，不是应用接入或真实采集验收。

下一步先确认历史内容的指向约定，再推导关系锚点、通用登记和读取。存储布局、Schema 校验和具体采集实现仍未确定。
