# 01: 观测深度 —— digest 长成深度树

Status: ready-for-agent

## Parent

[ADR-029](../../../docs/adr/029-observation-depth-matcher.md) §2

## What to build

纯投影层改造,无 LLM、无存储变更。把 digest 的身份维度从"块 + 抽样标题"改成**深度树**。

- **读数提取(确定性,镜像采集器契约)**:`DepthReadings.For(segment) → 读数路径`——
  system:L1 = 进程/App,L2 = 窗口标题;browser:L1 = URL、标签页标题(取 Attributes,缺失走既有回退链)。
  知识层/投影层不出现 app/url 字段名硬编码之外的语义——命名归采集器契约,这里只是 server 侧镜像,文件头注释指回契约。
- **RecapProjection 深度树**:块(L1 读数聚合)内挂下一深度分解——去重读数 + 并集时长,
  确定性预算剪枝:展开门槛(块时长)、每节点子数封顶、尾部折叠"其他 N 个"。
  `MaxTitlesPerBlock` 抽样退役。渲染示例:`Code 3.2h ── hyperframes-workspace 2.1h · heartbeat 0.8h · 其他 5 个 0.3h`。
- **确定性注释**:digest 尾部两行——近 14 天高频读数列表(输入由 service 提供,投影只渲染)、已知脉络块保留(单位下片换 Matcher,本片不动)。
- 时间结构(轨/块/会话、碎段合并)不动;`localhost` 等不做任何剔除(哨兵拆除在 issue 03,本片投影层本来就不剔)。

## Acceptance criteria

- [ ] 读数提取纯函数单测:system 两层、browser L1 双读数、缺失字段回退
- [ ] 深度树分解单测:去重聚合、并集时长不双计、展开门槛、子数封顶、尾部折叠
- [ ] 近 14 天高频注释渲染单测
- [ ] 旧标题抽样路径删除,RecapProjectionTests 全部迁移到树断言,服务端套件绿

## Blocked by

（无）
