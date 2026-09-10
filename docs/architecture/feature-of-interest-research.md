# Feature of Interest：Heartbeat 观测模型研究

状态：研究笔记，2026-09-09；不是架构决策。SDK/协议暂停，[上一版字段方案](collector-observation-model-proposal.md)待重审。本次不改 schema、实现或权威 glossary。

## 可借鉴的概念

[W3C SOSA/SSN 2017 Recommendation，§4](https://www.w3.org/TR/2017/REC-vocab-ssn-20171019/#SOSASSN)区分以下角色：

| 概念 | 回答的问题 |
| --- | --- |
| FeatureOfInterest（FOI） | 哪个对象具有被观测属性？ |
| ObservableProperty / observedProperty | 观测什么属性？ |
| Observation | 执行什么观测活动？ |
| Result | 得到什么结果？ |
| Sensor | 谁观测？可包括软件。 |
| Procedure | 使用什么步骤或算法？ |
| Platform | 谁承载观测者？ |

Observation 是活动，不能直接等同结果记录。结果适用时间与结果产生时间也分开。Sample 是观测策略中的样本/代理，不是一般所有权关系。[Observation](https://www.w3.org/TR/2017/REC-vocab-ssn-20171019/#SOSAObservation)、[Sample](https://www.w3.org/TR/2017/REC-vocab-ssn-20171019/#SOSASample)

## 两个不能照搬的地方

[SensorThings 1.1 §8.2.4、§8.2.7，表11/19](https://docs.ogc.org/is/18-088/18-088.html#datastream)：Datastream 固定 Sensor、ObservedProperty、Thing；FOI 属于每次 Observation，流内不必相同。因此它不等于现有 Fact Stream。它按结果类型区分类别、计数、数值等，也不等于 Segment/Event/Measurement 家族。

2017 SOSA 没有独立的 `hasUltimateFeatureOfInterest`；此关系见于 [2020 SSN Extensions Working Draft](https://www.w3.org/TR/2020/WD-vocab-ssn-ext-20200116/)，该文不是 Recommendation。[OGC OMS 3.0（2023批准标准）§7.2.2、§9.2.6–9.2.7](https://docs.ogc.org/as/20-082r4/20-082r4.html)正式区分 proximate 与 ultimate FOI，用于直接对象与最终关注对象有别的观测，不能泛化成任意实体父子树。

## 项目推演：先配对对象与属性

以下不是标准强制映射：

| 观测句子 | 可选 FOI | 属性与结果 |
| --- | --- | --- |
| Mac1 前台应用是 Edge | Mac1 | 前台应用 → Edge |
| 某浏览器窗口选中页面 A | 具体窗口 | active tab/页面 → A |
| VRChat 账号 A 位于世界 W | 账号 A | 所在世界 → W |
| 微信报告本人当天累计步数 N | 已确认的本人 | 当日累计步数 → N |

Collector 可类比软件 Sensor，采集方法可类比 Procedure。搬运微信结果不等于 Collector 亲自计步。Server 是运行位置，不是 VRChat 活动对象；手机可承载计步，步数却描述本人。账号关联本人不自动使账号成为人的 Sample，也不自动构成 proximate/ultimate 关系。

## 对当前模型的影响

依据[现有 glossary](../../shared/CONTEXT.md)：

- Subject 的 Machine/Account/Person 粒度不一定等于直接 FOI。Browser 的窗口是重要反例；也可以讨论“机器的带窗口索引的状态”，先不新增 Window Subject。
- AppIdentity 是平台应用身份，不是具体进程或窗口，不能代替所有观测对象。
- Fact 是时间事实；一次轮询、一个 Segment、一次 Revision 与一次 Observation 的映射尚未定义。观测到的活动与进行观测的活动必须分开。
- Stream 的固定主体及分组职责，应在对象粒度确定后重审，不能由 SDK 接口倒推。

先用四个场景确定对象身份、属性、时间、结果及来源证据，再判断哪些信息放 Payload、哪些需要独立身份。标准提供概念检查工具，不要求引入整套本体、通用关系引擎或新校验框架。
