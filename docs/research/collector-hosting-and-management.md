# Collector 运行组合与集中管理调研

> 访问日期：2026-09-20。仅采用官方一手资料。以下建议供设计讨论，不是已接受的 ADR。

## 来源事实

OpenTelemetry Collector 是一个可执行文件，内部由 receiver、processor、exporter 组合数据流水线；receiver 可以监听接收，也可以主动抓取。官方明确其为单一 binary，并支持多种部署方式。这说明模块化采集与统一部署可以共存。注意：OTel Collector 的职责包含接收、处理和导出，不能直接等同于 Heartbeat 只负责具体采集的 Collector。[架构](https://opentelemetry.io/docs/collector/architecture/)、[部署](https://opentelemetry.io/docs/collector/deploy/)

OTel 支持设备侧 agent 和集中 gateway。gateway 可以由多个实例提供统一入口；官方同时列明额外维护和故障点、级联延迟、资源成本。因此，集中管理需求本身不能证明所有数据必须再经过一层 gateway。[Gateway 部署模式](https://opentelemetry.io/docs/collector/deploy/gateway/)

Grafana Alloy 用一个工具收集多种遥测信号，通过配置连接职责单一的组件形成流水线。这是“统一运行、内部模块化”的另一个实践；并不意味着 Heartbeat 需要复制它的配置语言或组件框架。[Alloy 简介](https://grafana.com/docs/alloy/latest/introduction/)

Elastic Agent 主动通过 HTTP 长轮询连接 Fleet Server 获取配置、报告管理状态，同时按配置向 Elasticsearch 发送数据；Fleet Server 不反向连接 Agent。这体现了管理通信与数据交付的职责分离。Fleet Server 本身作为已部署 Elastic Agent 内的子进程运行，因此“一个部署入口”也不等于“一个进程”。[Fleet Server](https://www.elastic.co/docs/reference/fleet/fleet-server)

## 对 Heartbeat 的建议与取舍

以下为基于上述案例和当前需求的工程判断，不是来源对本项目的规定。

| 运行方式 | 适用边界 | 代价 |
|---|---|---|
| 同进程模块化 | 自有 Collector、同技术栈、一起发布，当前默认 | 共享故障与资源范围 |
| 同安装包多进程 | 原生依赖、浏览器运行时、需要独立重启或隔离的 Collector | 需要进程监督和本机通信 |
| 独立服务 | 确实需要独立扩容、部署、权限边界或远程执行 | 增加网络故障、认证、部署与运维负担 |

建议 Desktop 和无头应用各自组合 Hub 与适用 Collector，先提供一个启动入口；Hub 核心不依赖具体采集实现。同进程任务能分别处理普通错误，但不能保证进程崩溃或资源耗尽时隔离，不能把它表述为完整故障隔离。

两类 Hub 主动连接 API，Web 经 API 管理并查看状态；Record 继续由各 Hub 直接上传 API。管理与交付先在代码中分清职责，不必立即拆成两个后台服务，也不必增加服务器 Hub 中转。传输方式仍待选型，Fleet 的长轮询只是可参考方案。

本轮只定义已安装 Collector 的配置、启停、实际状态和 Hub 最近联络信息所需的边界。配置失联处理、自动故障迁移和插件安装暂不扩展；但具体实现仍须如实表达操作是否成功。先以一个真实服务器 Collector 验证接口，出现跨进程或独立扩容的实际需求时再拆分，避免先建立通用生命周期框架。
