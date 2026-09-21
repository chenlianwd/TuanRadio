# 音源架构演进阶段 1 实施计划（Provider 契约与 MusicSourceBroker）

> 日期：2026-09-20
> 状态：已实施（2026-09-21 按本计划七步全部落地，经两轮实施后代码评审；611/611 测试通过。实施记录与偏差见 §8，评审记录见 §8.1/§8.2）
> 上游计划：`docs/plans/2026-08-25-music-source-architecture-evolution-plan.md`（总架构演进；其阶段 0 已基本落地，本计划是阶段 1 的落地方案）
> 结论来源：用户两项决策（① 发布前人工耐久验证暂缓，先推进音源架构演进；② 先写实施计划文档、评审后再动代码）+ 2026-09-20 对 master `2d22997` 的代码核实（§2，行号以此为淮）。

## 1. 目标与非目标

**目标**

- 落地上游计划阶段 1：建立 `IMusicProvider` 契约与 `MusicSourceBroker`，五个现有音源经适配器接入，业务调用方全部迁到 Broker，最终删除 `MultiSourceMusicService`。
- 全程保持现有行为零回归：搜索、逐源状态报告、`FailureKind` 分类渲染、熔断、跨源回退择优、连接诊断、点歌/推荐/自动续播。
- 播放 URL 进入 LibVLC 前经过最小版 `MediaUriPolicy` 安全校验。

**非目标（本阶段不做）**

- 不重写任何音源的 HTTP 解析逻辑——适配器包装现有 `*MusicService`，一行实现代码不动。
- 不做阶段 2（本地曲库 / OpenSubsonic Provider）、阶段 3（Provider 独立包与构建瘦身）、阶段 4（开放曲库与 Provider 管理 UI）。
- 不做重定向逐跳校验与 DNS 重绑定完整对抗（LibVLC 内部跟随重定向，传输层无法逐 hop 拦截；列为后续 `PlaybackTransportAdapter` 增强，见 §3.4）。
- 不引入动态插件安装，不改用户可见 UI，不新增用户设置项。
- 不把网易试听流识别改移位置（与 2026-09-15 设计"试听检测不前移"决策一致）：现状试听判定在音源实现内部（`NeteaseMusicService.GetPlayUrlAsync:152` 对 `freeTrialInfo` 返回 null，触发聚合层换源）与聚合层搜索过滤（`MultiSourceMusicService:206`"试听或失效片段，已过滤"），适配器整段包装 `GetPlayUrlAsync` 自然保留该行为，不前移也不后移。

## 2. 现状核实（代码事实）

- **接口形态**（`IMusicSearchService.cs:15-33`）：`SearchAsync(keyword, limit, ct)` 与 `GetPlayUrlAsync` 返回裸 `string?` URL；`OnlineTrack.ProviderMetadata` 已承载解析所需稳定参数；`IsSlowSource` 标记 yt-dlp 慢源。
- **稳定身份已就绪**（`Models/ProviderTrackRef.cs`）：`ToSourceId`/`FromSourceId` 双向兼容 `"source:id"`；`Track.SourceId` 即该形态（`OnlineTrack.ToTrack`）。`MusicIdentity` 两档同曲判定（精确/宽松）与 `CandidateRanker` 多维评分已在跨源回退、聚合重排、推荐净化三处使用。
- **聚合器现状**（`MultiSourceMusicService.cs`，862 行，DI 单例 `App.axaml.cs:214-223` 注册为 `IMusicSearchService`）：
  - 构造函数硬编码 `new` 五源；酷我/咪咕由 `AIRADIO_ENABLE_LEGACY_WEB_SOURCES=1` 门控（:136-144）；YouTube 经 `extraSources` 最低优先级追加。
  - 特有能力：`SearchWithReportAsync`（AsyncLocal 逐请求报告作用域，:64）、`DiagnoseAsync`（设置页逐源诊断，:89）、`GetAlternativePlayUrlAsync`（跨源回退择优，:362）、`MusicSearchIntent` 显式/自动区分、`SourceHealthRegistry` 熔断（:31）、逐源硬超时。
- **十个具体类型 cast 点 + 两处构造点**（迁移对象；含两种写法）：`is MultiSourceMusicService` —— `ChatViewModel.cs:1334/:1345`、`DJService.cs:506/:523`、`RecommendationService.cs:654/:662`、`PlaylistViewModel.cs:736`（SearchWithReportAsync）`/:748`（GetPlayUrlAsync）；`is not MultiSourceMusicService` 早退 —— `MainWindowViewModel.cs:396`、`SettingsViewModel.cs:549`（诊断入口）。构造点：`App.axaml.cs:219`（DI 工厂）、`MainWindowViewModel.cs:691`（设计器/测试用无参构造 `new MultiSourceMusicService(new HttpClient())`）。另有与聚合器同文件、删壳时必须一并迁出的类型：`SourceSearchStatus`（:856-862）与嵌套 `SearchOutcome`（:43，`PlaylistViewModel:739` 消费）。
- **源路由靠类型名反射（平移时唯一必须改写的聚合器逻辑）**：`FindSource`（`MultiSourceMusicService.cs:811-820`）以 `s.GetType().Name.Replace("MusicService","")` 匹配 `"source:id"` 前缀——源包进适配器后类型名失配，前缀路由静默失效（错源请求 + 回退时失效源不被排除），且现有 mock 测试对任意 trackId 都返回 URL，**拦不住该失效**。
- **重载语义差异**：`GetPlayUrlAsync(string)` 命中前缀源后直接返回、无跨源回退；`GetPlayUrlAsync(OnlineTrack, ct)`（带 `ProviderMetadata`，酷狗 hash_std/hash_128/album 参数链依赖它）才续走跨源回退。`SearchAsync(keyword, limit, MusicSearchIntent, ct)` 意图重载（`DJService:507`、`RecommendationService:655` 传 `Automatic`）挡住 YouTube 慢源进入自动推荐链路。
- **AudioService 经三级 resolver 解耦（主路径是 Track 级，不是 id 级）**：`PlayTrack(Track)` 以 `FilePath`=URL 播放；`MainWindowViewModel.cs:195-199` 注入三级委托——`SetUrlResolver`（id 级，仅兜底）、`SetTrackUrlResolver` / `SetFallbackTrackUrlResolver`（Track 级，`AudioService.cs:1623` 优先走 `_trackUrlResolver`，**这才是播放解析主路径**）。Track 级解析入参是整只曲目（含 `ProviderMetadata`），跨源回退成功后把新 `Id`/`Source`/`ProviderMetadata` 回写共享 Track 实例并经 `TrackUrlResolution` 持久化（既有测试锚定：回退后 `track.Id` 变为 fallback 源前缀）。AudioService 不直接依赖搜索服务；当前无 User-Agent/Referer/Header 注入需求（五个源均裸 URL 播放）。
- **结构化失败链路已就绪**：`MusicSourceFailureKind` / `MusicSourceBusinessException` / `SourceSearchStatus.FailureKind`（2026-09-15 落地，七处抛出点标注）。
- **测试依赖面**：`MultiSourceMusicServiceTests` / `MusicServiceTests` / `SettingsViewModelTests` 直接构造 `MultiSourceMusicService`（类型引用 25 处，其中构造调用约 22 处），兼容期必须保持构造签名可用。

## 3. 设计

### 3.1 目录与契约（`Services/Music/`）

```text
AIRadio.Desktop/Services/Music/
├── Contracts/
│   ├── IMusicProvider.cs        // Descriptor + SearchAsync + ResolveAsync
│   ├── ResolvedMedia.cs         // 解析结果（Uri / ExpiresAt / Headers / IsPreview）
│   └── ProviderDiagnostics.cs   // 逐源诊断（复用 SourceSearchStatus，不另起模型）
├── Broker/
│   ├── MusicSourceBroker.cs
│   └── ResolvedMediaCache.cs
├── Playback/
│   └── MediaUriPolicy.cs
└── Adapters/
    └── MusicSearchServiceAdapter.cs  // IMusicSearchService → IMusicProvider
```

**契约裁剪决策**（相对上游计划 §3.2）：

- 上游的 `MusicCandidate`（含 Availability/Qualities/ISRC）**首版不引入**——当前唯一候选形状 `OnlineTrack` 与之基本重合，且可用性/音质/ISRC 均无数据来源。契约层直接复用 `OnlineTrack`，待阶段 2 本地曲库引入新元数据时再评估升级，避免先于数据虚设第二套模型。
- `ResolvedMedia` 保留 `Track`（`ProviderTrackRef`）、`Uri`、`ExpiresAt?`、`Headers?`、`IsPreview`；裁掉 `Codec/Container/BitrateKbps`（无来源），record 位置参数带默认值，后续加字段不破坏调用。
- `MusicProviderDescriptor` 保留 `Id`、`DisplayName`、`IsExperimental`、`NetworkScope`、`SearchBudget`、`ResolveBudget`，**增补 `IsSlowSource`**（适配器转发 `inner.IsSlowSource`；三处消费点：搜索回退排除 `:215`、播放回退排除 `:385`、诊断独立 10s 预算 `:99`——不得用 SearchBudget 阈值隐式推断，快速源调预算会被误判成慢源踢出自动回退）；裁掉 `Capabilities`。
- **两级解析入口**（跨源回退留在 Broker 聚合层，Provider 只解析本源）：
  - Provider 层：`IMusicProvider.ResolveAsync(ProviderTrackRef, IReadOnlyDictionary<string, string>? providerMetadata, ct) → ResolvedMedia?`——**null = 无可播地址**，与现状 `GetPlayUrlAsync` 返回 `string?` null 的语义逐点对齐（业务失败仍抛 `MusicSourceBusinessException`，传输异常照抛）。适配器**必须带元数据调用 `GetPlayUrlAsync(OnlineTrack, ct)` 重载**（酷狗多 hash 解析链依赖 `ProviderMetadata`，退到字符串重载会整链丢失），再把非 null URL 包装为 `ResolvedMedia`（`ExpiresAt=null`、`Headers=null`、`IsPreview=false`）。
  - Broker 层：`IMusicSourceBroker.ResolveTrackAsync(OnlineTrack, bool forceRefresh, ct) → ResolveTrackResult`——承载跨源回退与**回写**：回退成功时返回新的 `Id`/`Source`/`ProviderMetadata`，调用方据此回写共享 Track 实例并持久化（与现状 `TrackUrlResolution` 行为一致）。`ResolvedMedia` 解包只发生在 `MediaUriPolicy` 校验后交给播放的位置。

### 3.2 适配器策略（关键决策：包装而非重写）

现有五个 `*MusicService` 不改一行。`MusicSearchServiceAdapter` 持有 `IMusicSearchService`，实现 `IMusicProvider`：

- `SearchAsync` → 直通（含 CancellationToken 重载）；意图区分语义留在 Broker 层，`IsSlowSource` 经 Descriptor 转发。
- `ResolveAsync` → 带 `ProviderMetadata` 调 `GetPlayUrlAsync(OnlineTrack, ct)` 后包装为 `ResolvedMedia`；返回 null URL 时返回 null 而非抛异常（与现状 `null` 语义一致）。
- **源身份与键的平移规则**（防反射失配，A3 修复）：Broker 源路由从 `FindSource` 的类型名反射改为按 `Descriptor.Id` 匹配（OrdinalIgnoreCase）；适配器 `Descriptor.Id` 从 `inner.GetType().Name` 按既有 `Replace("MusicService","")` 约定派生（兼容持久化 SourceId 前缀与既有测试 mock 命名）；健康熔断键与 `SourceSearchStatus.Name` 沿用 `inner.Name` 显示名不变（UI 渲染与既有测试断言依赖显示名）。
- 酷狗 `KugouCredentialChanged` 事件、风控自动验证调用点、`ProviderMetadata` 传递全部维持原位。

**Provider 启用规则现状固化**：网易/酷狗默认启用；酷我/咪咕 env var 门控；YouTube 慢源最低优先级。顺序与启用收敛为 Broker 构造参数（`IMusicProvider[]` 数组顺序即优先级），本阶段不暴露用户设置（属阶段 4）。

### 3.3 Broker 与兼容层过渡（三步走）

1. **平移**：新建 `MusicSourceBroker`，把 `MultiSourceMusicService` 的聚合/报告/回退/诊断逻辑**代码平移**过来（`_sources` 改为 `IMusicProvider[]`，逐源调用点改走适配器）。这些逻辑刚经过 2026-09-01、2026-09-15 两轮加固且有 500+ 测试锚定，重写是本阶段最大风险源，禁止顺手重构。
2. **壳化**：`MultiSourceMusicService` 退化为薄转发壳，**同时实现 `IMusicSearchService` 与 `IMusicSourceBroker` 双接口**（单一实例双注册，从根上消除"两个注册点指向不同实例"和"测试把壳实例传给已改注入 Broker 的构造函数"两类编译/行为风险——`SettingsViewModelTests` 等现有测试直接把 `new MultiSourceMusicService(client)` 传给 ViewModel 构造参数，壳不实现 Broker 接口则这些测试在调用方迁移步骤全部编译失败）。**壳必须是零逻辑纯转发**：不得残留任何 `AddSearchReport`/AsyncLocal 写入——`CurrentSearchReport` 字段随 `SearchWithReportAsync`/`DiagnoseAsync` 整体移入 Broker（ExecutionContext 只单向流入，壳若自设 AsyncLocal 再调 Broker，Broker 读到的是自己的 null，报告会静默串写共享 `_lastSearchReport`）。壳的纯转发是步骤 3 的完成定义之一。
3. **迁移后删除**：10 个 cast 点改注入 `IMusicSourceBroker`（见 3.6），测试绿后删除壳；`SourceSearchStatus` 与 `SearchOutcome` 同步迁出聚合器文件；测试构造点与 `MainWindowViewModel:691` 无参构造一并迁到 Broker。DI 切换按 §3.6 清单逐项核对，不以计数为准。

**`IMusicSourceBroker : IMusicSearchService`** 承载特有能力，**成员清单必须显式包含以下各项**（两个默认接口方法陷阱：漏实现会静默回落弱路径，编译不报错、现有测试可能仍绿）：

- `SearchWithReportAsync` / `DiagnoseAsync` / `GetAlternativePlayUrlAsync` / `LastSearchReport`；
- `GetPlayUrlAsync(OnlineTrack, ct)` **显式覆写**（接口默认实现会回落字符串重载——无跨源回退、无 `ProviderMetadata`，`DJService:523`/`ChatViewModel:1345`/`RecommendationService:662`/`PlaylistViewModel:748` 四条链路受影响）；
- `SearchAsync(keyword, limit, MusicSearchIntent, ct)` 意图重载（`DJService:507`/`RecommendationService:655` 依赖 `Automatic` 挡 YouTube 慢源进自动链路，防 30s 搜索 + 30s 解析顶穿续播预算）；
- `ResolveTrackAsync(OnlineTrack, forceRefresh, ct)`（§3.1 Broker 级解析入口）。

业务层依赖该接口而非具体类型，满足上游计划"业务代码不得出现对 `MultiSourceMusicService` 的具体类型转换"的验收。壳期 DI：`IMusicSearchService` 与 `IMusicSourceBroker` 双注册到**同一个壳实例**；10 个 cast 点全部迁完，才允许把注册切到裸 Broker 并删壳——早切会让尚未迁移的 `is`/`is not` 判定静默失配（早退分支），逐源报告/诊断/跨源回退静默降级而非报错。

### 3.4 URL 解析路径与 MediaUriPolicy（最小版）

- 三条解析路径全部改经 Broker + `MediaUriPolicy`：id 级 `SetUrlResolver`（:195，兜底）、Track 级 `SetTrackUrlResolver`/`SetFallbackTrackUrlResolver`（:196-199，**主路径**，改走 `Broker.ResolveTrackAsync`，回写语义见 §3.1）、`GetAlternativePlayUrlAsync` 备选结果——每条路径产出的 URL 在进 LibVLC 前统一过 `MediaUriPolicy.Validate`。
- 校验规则：scheme ∈ {http, https}；五个现有源均 `PublicInternet` 作用域，拒绝 loopback、link-local、RFC1918 私网、组播、未指定地址、云元数据端点（`169.254.169.254` 等）——按解析后 IP 检查。`file` scheme 本阶段直接拒绝（本地曲库属阶段 2，届时以 `LocalFileOnly` 作用域放行）。
- **校验对象澄清**：校验的是"交给 LibVLC 的播放 URL"，不是音源 API 请求。网易/酷狗的 API 调用走本地代理（`127.0.0.1:37250`/`:37251`），但其返回的播放 URL 是上游 CDN 公网地址（`NeteaseMusicService:158-163` 直通 `data[0].url`；`KugouMusicService.ExtractPlayUrl` 只收 http(s) 直链）——loopback 拒绝不影响 API 链路。现状网易/酷狗/YouTube 三处 provider 已各自实现 `IsHttpUrl` scheme 检查（代码注释明言"同口径"），`MediaUriPolicy` 是把零散检查收口统一并增补 IP 类检查，不是从零引入新约束。
- **已知局限（诚实记录）**：LibVLC 内部跟随重定向，无法逐 hop 复检目标；DNS 重绑定的完整对抗需要传输层接管，不在本阶段承诺。校验只承诺"交给播放器的初始 URL 不指向私网/回环"。
- **判定细节**：IPv6 同口径覆盖（`::1`、`fc00::/7`、`fe80::/10`、组播 `ff00::/8`、未指定 `::`）；多 A 记录**任一地址落入禁段即拒**；DNS 解析失败同样拒绝（一律 fail-closed，不静默放行）。**接入面三条路径全覆盖**：id 级 resolver（`SetUrlResolver`）、Track 级主 resolver（`SetTrackUrlResolver`/`SetFallbackTrackUrlResolver` 的返回值）、`GetAlternativePlayUrlAsync` 备选结果——不是只挂"播放主路径"一处。

### 3.5 ResolvedMediaCache（小步）

- 内存字典：key = `ProviderTrackRef`，值 = `ResolvedMedia`；TTL = `ExpiresAt − 60s`，无过期时间默认 10 分钟。
- 失效钩子：`KugouCredentialChanged`（已有事件）→ 清空酷狗缓存 **+ 重置酷狗熔断**（现状订阅在聚合器构造函数 `:131-135`，平移时订阅归属落 Broker 构造或工厂组装处，不得丢失——丢了用户重新扫码后熔断不清，最坏 60 秒内已恢复的源仍被拒）；**为 `MusicAccountStore` 新增 `NeteaseCookieChanged` 事件**（网易 Cookie 写入口唯一，代价小）→ 清网易缓存，满足上游"退出登录清空该 Provider 缓存"验收——不加则登出后 VIP 直链残留至多 10 分钟，相对现状（登出立即生效）构成回归。应用关闭统一释放。
- `ExpiresAt` 现阶段恒 null（各源不返回过期时间），全部条目吃默认 10 分钟 TTL；正确性依赖"播放失败 → 恢复路径 forceRefresh 逐出"自愈，后续可从直链参数解析过期时刻再收紧。
- **陈旧 URL 防护**（现状每次播放都重新解析，缓存是行为变化，必须防"坏 URL 反复命中"）：① 播放恢复/重试路径强制绕过缓存——`ResolveAsync` 增加 `forceRefresh` 参数，恢复链路传 `true`；② 缓存命中的 URL 若实际播放失败，恢复链路重新解析时逐出该条目，不再回写旧值。
- 首版只服务播放解析路径（点歌/续播/恢复同一曲目重复解析时命中）；搜索列表仍不预解析（现状不变）。

### 3.6 调用方迁移矩阵

| 调用点 | 现状 | 迁移后 |
| --- | --- | --- |
| `ChatViewModel:1334/:1345` | cast `MultiSourceMusicService` | 注入 `IMusicSourceBroker`，cast 消失 |
| `DJService:506/:523` | 同上 | 同上 |
| `RecommendationService:654/:662` | 同上 | 同上 |
| `PlaylistViewModel:736/:748` | `SearchWithReportAsync` + `GetPlayUrlAsync(track)` | 注入 `IMusicSourceBroker`（`SearchOutcome` 消费点 :739 同步） |
| `MainWindowViewModel:195-199/:396/:401/:420` | 三级 resolver 注入 + 2 处 cast | 三条解析路径改走 `Broker.ResolveTrackAsync` + `MediaUriPolicy`；cast 消失 |
| `SettingsViewModel:549` | `is not` 早退后调 `DiagnoseAsync` | 注入 `IMusicSourceBroker`，早退分支删除 |
| `App.axaml.cs:214-223` | 工厂内 new 五源 + 聚合器 | 工厂组装 `IMusicProvider[]`（经适配器），壳期双注册见 §3.3 |
| `MainWindowViewModel:691` | 无参构造 `new MultiSourceMusicService(new HttpClient())` | 改构造 Broker（无参/兜底形态保持等价） |
| 删壳步骤 | `SourceSearchStatus`/`SearchOutcome` 与聚合器同文件（:43/:856-862） | 类型迁至 `Services/Music/Contracts/`，消费方 using 同步 |

## 4. 测试计划

- **BrokerTests**（新）：从 `MultiSourceMusicServiceTests` 镜像核心场景——逐源超时、业务失败透传 `FailureKind`、并发搜索报告隔离（AsyncLocal 作用域）、熔断冷却、回退择优时长接近度、诊断独立作用域、慢源独立预算、`Automatic` 意图下慢源不参与、OnlineTrack 解析含跨源回退。适配器路径全量跑一遍保证行为等价。**另补三个静默失效回归锚**（现有 mock 测试锚不住的路径）：带前缀 id 只请求对应源（mock 计数请求数）、回退后原失效源被排除不得再次命中、回退成功回写 `track.Id`/`ProviderMetadata`。
- **MediaUriPolicyTests**（新）：scheme 白名单；loopback/私网/链路本地/组播/云元地址拒绝（IPv4 + IPv6 全口径）；多 A 记录任一禁段即拒；DNS 解析失败拒绝；公网 IP 直链放行（防误伤 CDN）。
- **ResolvedMediaCacheTests**（新）：默认 TTL、`ExpiresAt − 60s` 提前失效、酷狗凭据变化清缓存 + 重置熔断、网易 Cookie 变化清缓存、命中不重复解析、`forceRefresh` 绕过、播放失败后逐出。
- **壳期注册对照**：双注册指向同一壳实例的断言；壳与 Broker 行为一致性；壳删除时测试构造点统一迁移。
- **门禁**：每步 `dotnet build` 0 错误 + 全量测试通过（当前基线 500+）。

## 5. 实施拆分（每步 build+test 通过后再进下一步）

1. **契约落地**：`Services/Music/Contracts/` 三文件 + `OnlineTrack` 复用决策（纯新增，零行为变化）。
2. **适配器**：`MusicSearchServiceAdapter`（纯新增，尚无人消费）。
3. **Broker 平移 + 壳化**：`MusicSourceBroker` 承接聚合体（含 `FindSource` 改按 `Descriptor.Id` 路由——平移中唯一必须改写的逻辑；AsyncLocal 报告作用域整体随迁）；`MultiSourceMusicService` 变双接口纯转发壳，单实例双注册；全量测试必须绿。
4. **调用方迁移**：10 个 cast 点 + resolver 改造（可拆 2-3 个小提交：DJ/推荐 → 聊天/播放列表/设置 → MainWindow resolver 与无参构造）。
5. **MediaUriPolicy**：最小版校验接入播放路径。
6. **ResolvedMediaCache**：缓存 + 凭据失效钩子。
7. **删壳与文档同步**：删除 `MultiSourceMusicService`、迁出同文件类型（`SourceSearchStatus`/`SearchOutcome`）、迁移测试构造点与 `MainWindowViewModel:691` 无参构造；同步 README / ai-radio-plan / AGENTS.md / 上游计划状态行。

## 6. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 聚合逻辑平移引入回归（该逻辑刚经两轮加固） | 只平移不重构；`MultiSourceMusicServiceTests` 场景镜像为 `BrokerTests`；壳期双实现可对照排障 |
| AsyncLocal 报告作用域在新结构中失效 | `SearchWithReportAsync` 整体平移、绑定逻辑不动；并发隔离测试锚定 |
| MediaUriPolicy 误伤合法直链（部分 CDN 返回 IP 直链） | 只拒私网/回环/链路本地/云元数据，公网 IP 放行；接入前用真实搜索样本抽样验证一轮 |
| 契约字段虚设（ISRC/音质无数据来源） | 首版裁剪、复用 `OnlineTrack`，阶段 2 再评估扩展 |
| 测试工程 25 处旧构造阻塞删壳 | 壳期构造签名不变；删壳步骤内统一迁移，不提前 |
| 酷狗风控/验证链路在适配器后行为漂移 | 验证服务仍由 DI 注入构造函数原样传递；`KugouVerificationService` 相关测试保持不动 |
| 壳期 DI 误切导致 cast 静默失配（功能降级无报错） | §3.3 中间态约束：壳期双注册指向同一壳实例；10 个 cast 点按 §3.6 清单迁完才切注册，切换提交单独可回滚 |
| 缓存陈旧 URL 反复命中，恢复链路空转 | 恢复路径 `forceRefresh` 绕过 + 播放失败逐出（§3.5），测试锚定 |
| 源路由类型名反射在适配器后失配（错源请求、回退排除失效，现有 mock 测试拦不住） | `FindSource` 平移时改按 `Descriptor.Id` 匹配，适配器 Id 按既有 Replace 约定派生；"带前缀 id 只请求对应源"回归测试锚定 |
| 网易登出后缓存残留 VIP 直链（现状登出立即生效） | 新增 `NeteaseCookieChanged` 事件接入清空（§3.5），满足上游验收 |

## 7. 评审记录

**第 1 轮（对照代码逐条核实事实声明，修正 6 处）**：cast 点 6→8（补 `is not` 写法的 `MainWindowViewModel:396`/`SettingsViewModel:549`；第 2 轮进一步纠为 10 处）；试听识别位置更正——在音源实现内部（`NeteaseMusicService.GetPlayUrlAsync:152` 对 `freeTrialInfo` 返 null 触发换源）+ 聚合层搜索过滤（:206），非原稿所写"播放恢复链路"；MediaUriPolicy 校验对象澄清——播放 URL 非 API 请求，网易/酷狗本地代理（127.0.0.1:37250/37251）不受 loopback 拒绝影响，且现状三处 provider 已有 `IsHttpUrl` 同口径检查，策略是收口不是从零引入；DI 壳期中间态约束（防 cast 静默失配）；ResolvedMediaCache 陈旧 URL 防护（`forceRefresh` + 播放失败逐出）；`ResolveAsync` null 语义显式化。

**第 2 轮（独立评审人设计推演：3 阻断 + 6 应修 + 4 建议；关键声明经抽查复核属实后全部采纳）**：
- **A1（阻断）** 播放主路径实为 Track 级三级 resolver（`SetTrackUrlResolver`/`SetFallbackTrackUrlResolver` 于 `MainWindowViewModel:196-199`，`AudioService:1623` 优先于 id 级），跨源回退需回写 `Id`/`Source`/`ProviderMetadata` 且酷狗解析依赖元数据——原契约 `ResolveAsync(ProviderTrackRef)` 表达力不足。修复：两级解析入口（Provider 层带元数据、Broker 层 `ResolveTrackAsync` 承载回退与回写），§2 主路径描述改写。
- **A2（阻断）** `IMusicSourceBroker` 若缺 `GetPlayUrlAsync(OnlineTrack, ct)` 显式覆写与 `MusicSearchIntent` 意图重载，默认接口方法会静默回落弱路径：四条链路丢跨源回退、YouTube 慢源顶穿续播预算。修复：接口成员清单显式化并写明陷阱。
- **A3（阻断）** `FindSource`（:811-820）类型名反射在前缀路由上于适配器包装后必然失配，且现有 mock 测试拦不住。修复：新增"源身份与键的平移规则"（§3.2），Descriptor 增补 `IsSlowSource`，三个回归锚用例入 §4。
- **应修级**：迁移面纠为 10 cast + 2 构造 + 2 同文件类型迁出（B1）；酷狗凭据事件补熔断重置归属、新增 `NeteaseCookieChanged`（B2/B3）；壳改双接口单实例双注册，兼护测试构造兼容（B4）；MediaUriPolicy 补 IPv6/多 A 记录/DNS 失败/三条接入面（B5）；`IsSlowSource` 入 Descriptor 而非预算推断（B6）。
- **建议级**：AsyncLocal 壳纯转发升级为步骤 3 完成定义（C1）；`ExpiresAt` 恒 null 的 TTL 自愈依赖如实记录（C4）；行号与计数口径校正（:206、:736/:748、25 处=类型引用含 22 处构造）（C3）；C2 的三个回归锚用例并入 §4。
- **总体结论**：方向与三步走策略成立；三处阻断级缺陷均击中"零回归"承诺且编译期/现有测试不可见，修复入正文后可进入实施。

## 8. 实施记录（2026-09-21）

七步全部落地，逐步 build + 全量测试通过（589 → 609：新增 MediaUriPolicyTests 11、ResolvedMediaCache/Broker 缓存漏斗 9）。

**落地内容**

- 契约（`Services/Music/Contracts/`）：`IMusicProvider` / `MusicProviderDescriptor`（含 `IsSlowSource`）/ `ResolvedMedia`（`RawUrl` = OriginalString，交给播放器时不得用 `Uri.ToString()`）/ `PlaybackHeaders` / `ResolveTrackResult` / `ProviderNetworkScope`；`OnlineTrack` 复用决策落实。
- 适配器 `MusicSearchServiceAdapter`：`DeriveProviderId` 逐字沿用原 `FindSource` 的 Replace 约定（A3 锚点）；ResolveAsync 带 `ProviderMetadata` 走 OnlineTrack 重载。
- `MusicSourceBroker`（`Services/Music/Broker/`）：聚合体自 MultiSourceMusicService 平移（AsyncLocal 报告作用域整体随迁、熔断键/报告名沿用 DisplayName）；`FindProvider` 改按 `Descriptor.Id`；`ResolveWithTimeout` 收口全部单源解析；`KugouCredentialChanged` 订阅落 Broker 构造（重置熔断 + 清缓存）。
- 10 处 cast 点全部迁移 `IMusicSourceBroker`；`IMusicSourceBroker` 显式承载意图重载与 OnlineTrack 解析重载（`new` 隐藏标注强制显式实现，A2 修复）。
- `MediaUriPolicy`（`Services/Music/Playback/`）：scheme 白名单 + IPv4/IPv6 字节级禁段判定 + DNS 解析后检查（多地址任一禁段即拒、解析失败 fail-closed）；接入点在 `ResolveWithTimeout`——id 级/曲目级/跨源回退/可播性探针全部路径单点收口（优于 §3.4 预设的三处接入面）；拒绝不记熔断，按"无可播地址"处理让回退链继续。
- `ResolvedMediaCache`：TTL（默认 10 分钟 / ExpiresAt−60s）、`forceRefresh` 读跳过 + 解析前逐出（失败不回写）、`ClearProvider` 凭据失效；`MusicAccountStore` 新增 `NeteaseCookieChanged` 事件（写入口唯一）。
- 删壳：`MultiSourceMusicService` 删除；`SourceSearchStatus`/`SearchOutcome` 迁 `Services/Music/Contracts/ProviderDiagnostics.cs`（命名空间保持 `AIRadio.Desktop.Services`，消费方零改动）；DI 双接口注册同一 `MusicSourceBroker` 实例；测试构造点与 `MainWindowViewModel` 无参构造全部迁 Broker。

**实施偏差（如实记录）**

1. `IPAddress.IsIPv4Mapped` API 不存在（编译期发现），禁段判定改纯字节实现（含 IPv4-mapped 解包），行为与设计一致。
2. 测试假 URL 从 `.invalid` 域名换成 TEST-NET-3 IP 字面量（203.0.113.x）：策略对 DNS 主机名 fail-closed，`.invalid` 必然 NXDOMAIN → 单测失败且每次拖 11s DNS 超时；字面量路径不触发 DNS，测试离线且快速。AudioService/PlaylistViewModel 测试中的 `.invalid` 不经 Broker 解析路径，未动。
3. 取消传播断言 1s→3s（`GetPlayUrlAsync_DoesNotWaitForLowerPriorityHangingFallback`）：该断言验证"取消最终传播到挂起源"（无泄漏）而非速度，Broker/适配器/策略多层异步跃点在并行测试负载下传播偏慢导致偶发超时；响应速度已由同测试的 2s elapsed 断言单独守护。
4. 测试类名/文件名保留 `MultiSourceMusicServiceTests`：经 legacy 兼容构造测 Broker，重命名属纯装饰性改动不做。
5. MainWindowViewModel 主解析路径切 `ResolveTrackAsync`（缓存漏斗）；恢复路径 `GetAlternativePlayUrlAsync` 不读缓存（天然新鲜解析），与 §3.5"恢复路径绕过缓存"一致。
6. §3.6 预设的"三个静默失效回归锚"用例：其中"带前缀 id 只请求对应源"与"回退后失效源被排除"已被既有用例（`GetPlayUrlAsync_DoesNotWaitForLowerPriorityHangingFallback` 的 SearchCount 断言、`GetAlternativePlayUrlAsync_SkipsCurrentSource`）覆盖；"回退回写 track.Id/ProviderMetadata"由 `GetPlayUrlAsync_UsesMetadataFallbackWhenPreferredSourceHasNoUrl`（断言 `track.Id == "fallback:456"`）覆盖，未另造重复用例。
   **（实施后评审更正：上段关于"带前缀 id 只请求对应源"的覆盖声明不成立——既有 mock 对任意请求都应答，前缀路由退化为 Try-all 时这些测试照样绿。已补显式锚 `ResolveTrackAsync_PrefixedId_OnlyRequestsMatchingProvider`。）**

### 8.1 实施后代码评审（2026-09-21）

评审方式：对照被删旧实现（`git show HEAD:...MultiSourceMusicService.cs`）对 `MusicSourceBroker` 做逐段平移保真复核（搜索两级路径/探针注释/慢源门控/容量上限/deadline 捕获过滤/回退并发与互斥回写/解析超时阶梯/报告脱敏/预算常量——全部一致），并核查适配器口径、策略字节判定、缓存时序、DI 双注册与调用方迁移完整性。发现并修复两处应修级：

1. **缓存命中不重放身份回写 → 持久化错位风险**：`ResolveTrackAsync` 缓存命中直接返回，不回写传入 track 的身份；若同一输入身份换实例重解析（写回持久化失败的窄路径），会得到"Id 指向 A 源、URL 是 B 源直链"的持久化结果——正是旧代码互斥锁严防的状态。修复：**回退结果（FellBack=true）不入缓存**；此类条目以回退后身份为键本就几乎不会命中，缓存价值全在非回退条目。测试锚 `ResolveTrackAsync_FallbackResult_IsNotCached`（脚本化双源真实回退场景，断言换实例重解析两源计数各 +1）。
2. **A3 前缀路由缺显式回归锚**：§8.6 原声明不成立（见上更正）。补 `ResolveTrackAsync_PrefixedId_OnlyRequestsMatchingProvider`：双假源解析 `Fake:1`，断言非匹配源零请求。

其余核查项无缺陷：`DeriveProviderId` 与旧 `FindSource` 反射规则逐字一致；策略禁段字节判定（172.16/12、169.254/16、fc00::/7、fe80::/10、fec0::/10、IPv4-mapped 解包）推演正确；拒绝路径不记熔断与"传输故障才熔断"语义一致；`forceRefresh` 先逐出后解析、失败不回写；DI 双接口同一实例；`MultiSourceMusicService` 代码级引用残留为零。评审后全量测试 611/611 通过（609 + 2 个新锚）。

### 8.2 实施后代码评审第 2 轮（2026-09-21）

评审方式：换新视角——AudioService 侧三级解析委托的真实调用流分析（缓存到底被谁读、会在哪条链路产生陈旧重试），与适配器 carrier 的字段完整性核对。发现并修复一处应修级：

1. **缓存读路径错位（实现与 §3.5 意图相反，已修）**：原实现只有 `ResolveTrackAsync` 读写缓存，而它的唯一生产调用方是 MainWindowViewModel 注入 AudioService 的刷新委托——经 `AudioService.cs:1316`（重试前刷新）/`:1326`（播放前后台换新直链）核实，**该委托只在刷新/恢复语境被调用**，且实现里传了 `forceRefresh:false`：等于缓存只被最不该读它的恢复链路读取（重试可能拿回刚失败的陈旧 URL，再浪费一次恢复预算），而点歌/推荐/歌单等 §3.5 预期的受益调用方走 `GetPlayUrlAsync(OnlineTrack)` 完全不经过缓存——收益为零、风险为正。
   **修复**：缓存漏斗下沉到 `GetPlayUrlCoreAsync`——`GetPlayUrlAsync(OnlineTrack, ct)` 成为缓存读路径（点歌/推荐/歌单重复解析命中）；MainWindowViewModel 刷新委托改传 `forceRefresh:true`（刷新/恢复恒走上游新鲜解析，与改造前行为逐点一致）；`ResolveTrackAsync(forceRefresh)` 保留"先逐出、失败不回写、成功回写"语义，刷新结果回填缓存供后续探测复用。缓存读/绕过路径用例相应改挂（`GetPlayUrlAsync_SecondCallHitsCache` 等）。
2. **适配器 carrier 完整性（核实无缺陷）**：五源中仅酷狗覆写 `GetPlayUrlAsync(OnlineTrack)` 重载且只消费 `Id`+`ProviderMetadata`；网易/酷我/咪咕/YouTube 走默认接口方法只取 `track.Id`（YouTube 实现自剥前缀取 videoId）。旧 `CreateProviderTrack` 拷贝的 Title/Artist/Album/DurationMs 无任何解析期消费者，最小 carrier 行为等价。
3. 其余保持无缓存路径复核：id 级 `GetPlayUrlAsync(string)`（AudioService 兜底 resolver）、搜索内可播性探针（`ResolveWithTimeout` 直连）、`GetAlternativePlayUrlAsync` 恢复换源——三条路径均不读缓存，与 §3.4/§3.5 语义一致。

评审后全量测试 611/611 通过。
