# 03: 声明传输通道——loopback 动词 + hub 上行

Status: done

## Parent

[ADR-030](../../../docs/adr/030-collector-depth-declaration.md) §3

## What to build

- **loopback 新动词**:`POST /v1/collectors/{source}/declaration`(body = 声明
  JSON);SegmentIngestRequestHandler 校验 source 一致、拒收 system 冒充(沿用
  既有守卫);写入 registry。
- **hub registry 存声明**:CollectorEntry 加 Declaration(原文 JSON)+ 版本;
  system 采集器进程内声明(C# 常量,同 schema)。
- **hub → server 上行**:HeartbeatApiClient 新调用
  `POST /api/v1/collectors/declarations`(批量,启动时 + registry 声明变更时,
  按 (source, version) 幂等);服务端端点落 CollectorDeclarations(同版覆盖)。
- **browser 采集器上报**:声明常量(v1:url → tab_title,与种子一致,幂等收敛),
  每次拉 config 前若未成功上报过则 POST(带退避,复用 backoff)。

## Acceptance criteria

- [ ] loopback 动词单测:写入 registry、source 不一致拒收、system 冒充拒收
- [ ] hub 上行:启动上报、变更再报、失败退避不阻塞 segments 上传
- [ ] 服务端端点行为测试:落表、同版幂等、坏声明 400(校验复用 issue 01)
- [ ] browser 扩展构建绿;端到端手测一轮(hub 日志见上报、服务端表见行)

## Blocked by

01
