# 01 真实认证管线端到端覆盖

Status: `ready-for-human`
覆盖候选: `X-A2`（P1）、`S-04`（P2）；钉住 `S-02`、`S-03` 的当前行为

## 问题

`tests/Heartbeat.Integration.Tests/RecordingApiFactory.cs:38-47` 把整条认证管线换成测试 handler。于是所有 401 断言（`CollectorHttpTests.cs:55-68`、`RecordUploadHttpTests.cs:304`、`TrackHttpTests.cs:159`、`TrackCatalogHttpTests.cs:83`、`RecordReplayHttpTests.cs:221`、`AuthenticationContractTests.cs:9-40`）证明的都是替身的行为。`src/Backend/Heartbeat.Api/Authentication/AuthenticationExtensions.cs` 里的配置层缺陷，结构上不可能被现有测试发现——`OidcAudience` 在 `appsettings.json:11` 是空串，audience 校验被静默关掉，没有任何测试会响。

## 要做的

1. 让测试和 host 走同一段认证注册代码。现在的注册逻辑如果内联在 `Program.cs` / `AuthenticationExtensions` 里，就**行为零变化**地提取成一个可被测试复用的注册入口。
2. 加一组走真实 JWT 验签的集成测试（自签 RSA 密钥 + 内存 JWKS 或直接注入 `IssuerSigningKey`，不要连外部 IdP），至少覆盖：
   - 合法 token 通过
   - issuer 不匹配 → 401
   - 过期 token → 401
   - 签名不匹配 → 401
   - `typ` 路由：OIDC 与 session 两条 scheme 各自认哪种 token
   - **audience 当前行为**：`OidcAudience` 为空时 audience 不被校验（用测试名字把这件事写清楚，比如「audience 未配置时不校验 audience（当前行为，见 issue 说明）」）
3. 现有替身测试保留，不要为了新测试删掉它们——它们覆盖的是端点授权逻辑，不是验签。

## 不要做

- **不要**改 audience 校验的验收行为（不要 fail-fast、不要给 `appsettings.json` 填一个猜的 audience）。真实 IdP 的 audience 值我们不知道，改了会让本机登录挂掉。这是 `S-02`，属于下一轮的决策项，本轮只把它钉成一条显式测试 + 在报告里摆给用户。
- 不要引入新的认证依赖包，用已有的 `Microsoft.IdentityModel.*`。

## 验收

- 新测试在 `dotnet test tests/Heartbeat.Integration.Tests` 下全绿，且**故意把 issuer 改错能让对应用例变红**（自己验一次，别只看绿）。
- `git diff` 里 host 的认证行为没有变化：提取是纯搬运。
- `docs/recording-api.md:13` 关于认证的说法与测试覆盖一致。

## Comments

### 实施记录（2026-09-17）

**做法：不动 host，一行生产代码都没改。** `git diff -- src/Backend/Heartbeat.Api` 为空。认证注册本来已经是可复用的入口（`AuthenticationExtensions.AddHeartbeatAuthentication`），真正的问题只是集成测试在 `ConfigureTestServices` 里把它整段顶掉了，所以不需要「提取」，需要的是让测试别再替换它。

改动集中在测试侧：

- `tests/Heartbeat.Integration.Tests/RecordingApiFactory.cs`：把 host builder 抽成一个私有核心，两个入口共用。`Create(...)` 行为不变（仍是替身，注释里写清它只证明端点授权）；新增 `CreateWithRealAuthentication(...)`，走 `Program` 自己的认证注册。
- `tests/Heartbeat.Integration.Tests/TestIdentityProvider.cs`（新）：自签 RSA（2048）签发 token，只用已有的 `Microsoft.IdentityModel.*`，没加包，不连外部 IdP。`TestIdentityProviderMetadata : IPostConfigureOptions<JwtBearerOptions>` 只替换 discovery 文档（`StaticConfigurationManager`），**每条 scheme 的 `TokenValidationParameters` 与 events 全部保持 host 配置的原样**；被信任的 issuer 是从 `options.TokenValidationParameters.ValidIssuer` 读回来的，测试里不重写一份 scheme 名与 issuer，避免出现第二处权威。
- `tests/Heartbeat.Integration.Tests/RealAuthenticationPipelineTests.cs`（新）：12 个方法 / 14 个用例，全部打真实 `POST /api/v1/collectors`，成功路径还断言 Timeline 的 OwnerId 等于 token 的 `sub`（证明 owner 是从**真被验签的** token 里出来的）：

| 用例 | 覆盖 |
|---|---|
| `ValidOidcAccessTokenAuthenticatesOwner` / `ValidSessionTokenAuthenticatesOwner` | 两条 scheme 的合法 token 通过 |
| `TokenFromUnexpectedIssuerIsRejected` | issuer 不匹配 → 401，且挑战里是 `The issuer` |
| `ExpiredTokenIsRejected` | 过期 → 401（`expired`） |
| `TokenSignedWithUnpublishedKeyIsRejected` | 未发布密钥签名 → 401（`signature`） |
| `AccessTokenTypeHeaderRoutesToTheOidcScheme` | 同一份 session claims，只改 header `typ` 为 `at+jwt`：`JWT` 时 200、`at+jwt` 时 401（挑战里是 session issuer），证明 `TokenSelector` 换了 scheme |
| `PlainJwtTypeHeaderRoutesToTheSessionScheme` | 反向同理；用「audience 被校验」作为落到 session scheme 的证据（OIDC 侧 audience 校验是关的） |
| `AccessTokenNotIssuedToTheConfiguredClientIsRejected` | `client_id` 是别人的 / 干脆没有 → 401 |
| `VerifiedTokenWithoutUuidSubjectIsRejected` | 验签通过但 `sub` 非 UUID / 全零 → 401（`S-04` 走真实链路） |
| `SessionTokenWithWrongAudienceIsRejected` | session scheme 的 audience 确实在校验 |
| `AudienceIsNotValidatedWhenOidcAudienceIsNotConfiguredWhichIsCurrentBehaviourPendingDecision` | **钉住 `S-02` 当前行为**：先断言 `AuthService:OidcAudience` 是空串，再证明一个签给别的 API 的 access token 被接受（200） |
| `AudienceIsValidatedOnceOidcAudienceIsConfigured` | 同一段 host 代码，配上 audience 后错 audience 就是 401——说明将来的修法只是填值，不需要改代码 |

替身测试一条没删，`appsettings.json` 没动，audience 的验收行为没动。

**破坏性验证（两次，都真的红了）**

1. 把 token 默认 issuer 改成 `OidcIssuer + "MUTATION"`：`Failed: 5, Passed: 8`。红的是 `ValidOidcAccessTokenAuthenticatesOwner`、`PlainJwtTypeHeaderRoutesToTheSessionScheme`、两条 audience 用例（`Expected: OK / Actual: Unauthorized` 之类的 `Assert.Equal() Failure: Values differ`），以及 `TokenSignedWithUnpublishedKeyIsRejected`（挑战理由从 `signature` 变成 issuer，`Sub-string not found: "signature"`）——说明断言认的是失败**原因**，不是随便一个 401。改回后 13/13 绿。
2. 反向验一次否定用例不是空转：让 metadata stub 把「未发布密钥」也当成可信密钥，`TokenSignedWithUnpublishedKeyIsRejected` 立刻红：`Expected: Unauthorized / Actual: OK`。改回后绿。

最终：`dotnet test tests/Heartbeat.Integration.Tests` → `Failed: 0, Passed: 107`（改动前基线 93，新增 14）。

**发现但没改的**

- `S-02` 的实际风险边界比「audience 没校验」更准确一点：OIDC 侧现在唯一约束「这个 token 是发给谁的」的检查是 `OnTokenValidated` 里的 `client_id` 比对（已补测试钉住）。但 `client_id` 说的是「发给哪个客户端」，不是「发给哪个资源」——同一个 IdP 下签给 `heartbeat-web` 的、面向其它 API 的 access token，当前会被 Heartbeat 接受。是否要求 audience、以及真实 IdP 的 audience 值是多少，需要作者拍板（可能连带一份 ADR）。本轮按 spec 只钉行为。
- `AuthService:Authority` 被两条 scheme 共用，即 agent session token 的验签密钥也来自同一个 IdP 的 discovery。测试已把这个现状钉住（两条 scheme 共用同一份已发布密钥），不算缺陷，但如果将来 session token 换成自签，这里要一起改。
- `docs/recording-api.md:13`「选中的 JwtBearer scheme 必须完成验签」现在真的有测试撑着，无需改文档；文档目录本轮有其它任务在并行改，没有去碰。

**范围说明**：只做了实现完成 + 自动验证完成（上面的定向测试与两次变异），没有人工验收；本机 `env up desktop` 的进程没有动过，也没有跑整解决方案构建。改动留在工作树里，未提交。

### 结构闸门挂住了这个新测试文件（2026-09-17）

改造后的 `quality --base HEAD` 第一次运行就 `EXIT=1`，3 个**新增**重复簇全部落在 `RealAuthenticationPipelineTests.cs`：

- `23-31 <-> 39-47`（9 行 / 83 tokens）
- `31-39 <-> 47-55`（9 行 / 76 tokens）
- `31-39 <-> 233-241`（9 行 / 70 tokens）

三处都指向同一个根因：`ValidOidcAccessTokenAuthenticatesOwner` 与 `ValidSessionTokenAuthenticatesOwner` 除了铸 token 的那一行完全一样，且它们的「200 + 读库断言 OwnerId」尾巴和 audience 钉桩用例的尾巴也一样。

**怎么消的（没删任何覆盖）**

1. 两条 valid 用例合成一个 `[Theory] ValidTokenOfEitherSchemeAuthenticatesOwner(Scheme scheme)`，`[InlineData]` 各给一次 `Scheme.OidcAccess` / `Scheme.Session`；新增私有 `Mint(identityProvider, scheme, subject)` 做 scheme → 铸币方法的映射。两条 scheme 合法通过仍是两个独立用例，只是共用编排。
2. 抽出 `AssertOwnerRegisteredAsync(response, ownerId)`（断言 200 + 时间线单条且 OwnerId 相符），在合法通过、`OidcAudience` 为空钉桩、`OidcAudience` 配置后三处复用。`AudienceIsValidatedOnceOidcAudienceIsConfigured` 里原先那句独立的 `Assert.Equal(OK, accepted.StatusCode)` 被 helper 覆盖，去掉后断言语义不变。

**用例数**：方法 12 → 11，用例数 **14 → 14 不变**（少了一个 Fact、多了一个两条 InlineData 的 Theory）。断言语义只增不减：合法通过的两条现在都过库校验 helper（原先也是），audience 配置后那条多了「accepted 必须写出 owner 时间线」的复用断言。保留的覆盖逐条核对过：两条 scheme 合法通过、issuer 不匹配、过期、未发布密钥签名、`typ` 双向路由、`client_id` 错/缺、`sub` 非 UUID/全零、session audience 生效、`OidcAudience` 为空时不校验 audience（`S-02` 钉桩），一条没少。

**没有硬抽的地方（保留，理由）**：`TokenFromUnexpectedIssuerIsRejected` / `ExpiredTokenIsRejected` / `TokenSignedWithUnpublishedKeyIsRejected` 三条结构相似（各自铸一份 oidc + 一份 session 再断言挑战理由），但每条变化的是**不同的命名参数**（`issuer` / `expires` / `credentials`），抽成统一委托会把「哪个字段被污染」藏进回调里，读者要跳两跳才看得到失败原因。闸门对它们没有意见（jscpd 未判定为簇），所以原样保留。

**结果**：`dotnet test tests/Heartbeat.Integration.Tests` → `Failed: 0, Passed: 107`（与重构前基线一致）。重跑 `./scripts/heartbeat-dev quality --base HEAD` → `EXIT=0`，tests clones `18 current / 3 new` 变成 `15 current / 0 new`（重复行占比 1.92% → 1.62%），生产侧 0 new、复杂度 0 new、erosion 0.00% 无变化。证据：`.artifacts/verification/20260917T093720Z-quality-gate-73d960b0ecc741e697eb9eff2aa91635`。
