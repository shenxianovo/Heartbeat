# 04: browser v2——site 层提拔

Status: done

## Parent

[ADR-030](../../../docs/adr/030-collector-depth-declaration.md) §5

## What to build

值空间提拔的第一个实例,验证"采集器加深度、服务端零改动"。

- **normalize.ts 加 siteOf**:可注册域(eTLD+1 近似:常见多段公共后缀名单
  + 末两段回落),www 折叠进主站;单测覆盖 www、子域、com.cn 类后缀、IP/localhost。
- **段构造写 attributes.site**(fold.ts snapshotOf)。
- **声明 v2 上报**:site ← attributes.site 为新 L1,url、tab_title 顺延;
  version = 2。
- **验证零改动**:服务端不动任何代码,digest browser 轨长出三层
  (site → url → tab_title);老段(无 attributes.site)按"最深可用读数"挂 url 层。

## Acceptance criteria

- [ ] siteOf 单测(www / 子域 / 多段后缀 / IP / localhost)
- [ ] 段携带 attributes.site;声明 v2 上报后服务端生效表切 v2
- [ ] 服务端零 diff;digest 三层树、老段退化挂 url 的行为测试(服务端已有泛化测试覆盖,补 browser 三层用例)
- [ ] 判官提案可锚 (site equals ...)(prompt 词汇含 site,手测一轮)

## Blocked by

02, 03
