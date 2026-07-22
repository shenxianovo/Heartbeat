# 01: 声明表 + 通用解释器 + Layer 退出 Matcher 身份

Status: done

## Parent

[ADR-030](../../../docs/adr/030-collector-depth-declaration.md) §1/§2/§4/§6

## What to build

纯服务端片,行为零断层(种子 = 现行为)。通道与树泛化在后续片。

- **CollectorDeclarations 表**:`UNIQUE (Source, Version)`,PayloadJson(jsonb)、
  ReportedAt;迁移预插种子行 system v1(L1 app ← appName,L2 title ← title)、
  browser v1(L1 url ← identityKey,L2 tab_title ← title——tab_title 随本片挪 L2)。
- **声明模型与校验**:layers 有序、读数 name source 内唯一、from 槽位语法
  (appName | title | identityKey | attributes.<path>)、label 可选。
- **通用解释器替换 DepthReadings**:`For(声明, segment) → 读数路径`,唯一动作按槽
  取值;未声明 source 通用回落(L1 identity ← identityKey,L2 title ← title);
  生效表 = 每 source max(Version),按请求缓存读取。
- **Layer 退出 Matcher 身份**:MatcherStepDto 去 Layer;Normalizer canonical =
  (reading, op, value);Eval 按 (source, reading name) 匹配;判官输出 schema 去层;
  KnowledgeIdentityBackfill 扩展——存量 StepsJson 剥 Layer、撞身份去重(保最早)。
- NSwag 重生成(MatcherDto 破坏性变更),前端编译修复(展示处不再读 layer)。

## Acceptance criteria

- [ ] 声明校验单测:读数重名拒收、非法槽位拒收、layers 序保持
- [ ] 解释器单测:种子声明产出与旧 DepthReadings 一致(system/browser/未知源回落);attributes.<path> 取值
- [ ] 生效规则测试:同 source 多版本取 max;同 (Source,Version) 重写幂等覆盖
- [ ] Matcher 去层:Eval 按名匹配单测;canonical 无 Layer;backfill 剥层+去重行为测试
- [ ] 服务端套件绿;vue-tsc 干净

## Blocked by

(无)
