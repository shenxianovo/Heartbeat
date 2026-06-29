# Heartbeat 数据库设计

本项目使用 EF Core (PostgreSQL) 作为数据持久化方案。实体关系图如下：

```mermaid
erDiagram
    Device {
        bigint Id PK "主键"
        text DeviceName "设备名称 (Unique)"
        text ApiKey "API 鉴权密钥"
        text CurrentApp "当前正在使用的前台应用"
        timestamp LastSeen "设备最后活跃时间"
    }

    App {
        bigint Id PK "主键"
        text Name "应用名称/进程名 (Unique)"
    }

    AppIcon {
        bigint Id PK "主键"
        bigint AppId FK "外键 -> App.Id (Unique)"
        bytea IconData "图标二进制数据"
        timestamp UpdatedAt "最后更新时间"
    }

    AppUsage {
        bigint Id PK "主键"
        bigint DeviceId FK "外键 -> Device.Id"
        bigint AppId FK "外键 -> App.Id"
        timestamp StartTime "该段使用记录的开始时间"
        timestamp EndTime "该段使用记录的结束时间"
        integer DurationSeconds "持续时长(秒)"
    }

    InputEvent {
        uuid Id PK "主键兼去重键 (UUIDv7, Agent 生成)"
        bigint DeviceId FK "外键 -> Device.Id"
        smallint EventType "事件类型: 1=KeyDown 2=MouseButton 3=MouseScroll"
        smallint Code "键盘=VK码; 鼠标按钮=1左/2右/3中; 滚轮=1上/2下"
        timestamp Timestamp "事件发生时刻 (毫秒精度, 算速度的权威时间源)"
    }

    Device ||--o{ AppUsage : "产生"
    App ||--o{ AppUsage : "被使用"
    App ||--o| AppIcon : "拥有 (1对1)"
    Device ||--o{ InputEvent : "产生"
```

## InputEvent 说明

原始输入事件流，一行一个键盘按下/鼠标操作事件，不做时间桶聚合或 delta 编码（详见 [ADR-012](adr/012-input-event-tracking.md)）。

- `Id` 为 UUIDv7，既是主键又是唯一去重键，服务端插入用 `ON CONFLICT (Id) DO NOTHING` 保证离线重传幂等。
- 推荐索引 `(DeviceId, Timestamp)`，支撑按设备 + 时间范围的计数查询。