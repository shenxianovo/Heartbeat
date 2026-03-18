# Heartbeat

本人的 Windows PC 应用使用情况监视器  
https://shenxianovo.com/heartbeat

## 项目结构

```
Heartbeat
├─desktop
│  └─Heartbeat.Agent          // 监测&上传组件   .NET Class Library
│  └─Heartbeat.Agent.Runner   // 客户端         .NET Console
│  └─Heartbeat.WPF            // 客户端          WPF
├─frontend                    // 前端            Vue
├─server
│  └─Heartbeat.Server         // 服务端          ASP.NET Core
├─shared
│  └─Heartbeat.Core           // DTO           .NET Class Library
└─docs                        // 文档
```

## 项目文档

- [API设计](./docs/api.md)
- [数据库设计](./docs/db.md)