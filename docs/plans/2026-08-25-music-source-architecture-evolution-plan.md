# TuanRadio 音源架构演进计划

> 日期：2026-08-25
> 状态：待实施
> 产品边界：个人工具、开源项目；项目方不经营商业音乐分发，但所选开源许可证仍允许依法商业使用
> 实施原则：小步迁移、保持现有播放能力、每阶段独立 build/test、先修安全与正确性再扩展新源

---

## 1. 结论与关键决策

TuanRadio 不重写现有 LibVLC 播放核心和播放恢复状态机。当前需要演进的是“音源发现、候选选择、临时播放地址管理和第三方适配器边界”。

本计划确定以下方向：

1. **保留现有五源能力，但不再把它们视为播放器核心。** 网易云、酷我、酷狗、咪咕、YouTube/yt-dlp 逐步迁移为可启停的实验性 Provider。
2. **核心仓库先建立稳定 Provider 契约。** 搜索结果、可用性、音质、临时 URL、过期时间、必要请求头和逐源诊断都使用显式模型表达。
3. **播放列表只持久化稳定身份。** 在线曲目保存 Provider ID、曲目 ID 和元数据，不再依赖或使用临时 URL 判断重复。
4. **本地曲库和 OpenSubsonic 作为长期稳定主路径。** 用户自有内容最符合个人工具、开源项目边界，也能完整支持 AI DJ、TTS 插播和频谱。
5. **开放曲库作为可选补充。** 后续优先评估 Audius、SoundCloud、Jamendo 等有公开开发接口或明确开放许可的来源。
6. **不直接复制 MusicFree 式远程 JavaScript 任意执行机制。** 仓库内随官方构建发布的 Provider 可以使用进程内 .NET 契约；任何用户自行安装的社区 Provider 必须进程外运行，并通过带版本协商的 JSON-RPC 通信。第一轮不开放动态社区插件安装。
7. **不开发表面上的付费限制绕过。** Provider 必须保留试听、登录、会员、地区限制等真实可用性状态；无法完整播放时返回明确失败或切换合法可用候选。

### 1.1 成功标准

最终架构应满足：

- 任意单一 Provider 超时、返回畸形 JSON、登录失效或退出，不会拖死搜索、播放或应用关闭。
- 搜索结果按匹配质量和可播性排序，不再仅以硬编码源顺序决定结果。
- 外部 Provider 返回的 URL、重定向和请求头在进入 LibVLC 前经过统一安全校验；只有用户明确配置的私有服务器可以访问局域网地址。
- 在线曲目的签名 URL、Cookie、token 不写入歌单文件和普通日志。
- 同一首歌即使刷新 URL 或切换 Provider，也不会重复加入播放列表。
- Provider 可以独立启用、禁用、排序、诊断和升级。
- 没有安装可选 Provider 时，核心项目仍能离线构建、测试和播放本地音乐。
- yt-dlp 等下载型工具必须通过版本和哈希校验，并能识别低于最低支持版本或被安全公告明确封禁的安装。

## 2. 当前基线

### 2.1 保留的能力

- `AudioService` 的 LibVLC 生命周期串行化、网络缓存、断流重连和播放请求代次保护。
- 当前的三级恢复顺序：刷新当前源 → 尝试替代源 → 进入下一首。
- 应用生命周期取消、逐源硬超时、逐源搜索状态报告。
- 网易云试听流识别、跨源按歌名和歌手重新匹配。
- `MusicAccountStore` 与 Windows 安全存储。
- 设置页的账号状态、扫码登录和诊断入口。

### 2.2 需要替换的行为

| 当前行为 | 问题 | 目标行为 |
|---|---|---|
| 网易前 3 条有一条可播就直接返回 | 其他源没有参与质量竞争 | 快速主源 + 有界并行补充，统一评分 |
| fallback 按固定源顺序保留第一条 | 可能选择错误版本或低质量流 | 标题、歌手、时长、专辑、版本、ISRC 综合评分 |
| `OnlineTrack` 只返回字符串 URL | 无法表达过期时间、音质、请求头和试听状态 | `ResolvedMedia` 显式建模 |
| 在线临时 URL 写入歌单 | URL 会过期，也可能泄露签名信息 | 只持久化稳定 Provider 身份，URL 仅内存缓存 |
| 使用 URL 判断重复 | URL 刷新后同一首歌会被视为新歌 | Provider ID + 标准化歌曲身份判断 |
| 所有 Provider 共用 5 秒播放解析预算 | yt-dlp 与外层 8 秒恢复预算冲突 | 每源预算 + 整体 deadline，剩余时间可见 |
| yt-dlp 首次下载 `latest` 后永不更新 | 无哈希校验，旧版本长期滞留 | 固定版本、SHA256、最低支持版本、安全封禁清单、周期检查 |
| 酷狗 Cookie 放在 query string | 容易进入日志、缓存和诊断信息 | 本地代理使用 header/POST body |
| 构建时执行 `npm install` 并复制完整代理目录 | 构建不够可复现，发布体积和攻击面过大 | `npm ci`、裁剪运行时文件、可选 Provider 包 |

## 3. 目标架构

```text
MainWindowViewModel / PlaylistViewModel / RecommendationService
                         │
                         ▼
                 MusicSourceBroker
          ┌──────────────┼──────────────┐
          │              │              │
     SearchCoordinator  CandidateRanker  SourceHealthRegistry
          │              │              │
          └──────────────┼──────────────┘
                         ▼
                IMusicProvider[]
       ┌─────────────────┼──────────────────┐
       │                 │                  │
  Stable Providers  Open Providers   Experimental Providers
  Local/OpenSubsonic Audius/...       Netease/Kuwo/Kugou/
                                      Migu/YouTube
                         │
                         ▼
                  ResolvedMediaCache
                         │
                         ▼
          MediaUriPolicy / PlaybackTransportAdapter
                         │
                         ▼
                     AudioService
```

### 3.1 职责边界

#### `MusicSourceBroker`

- 接收搜索和解析请求。
- 根据启用状态、优先级、认证状态和健康度选择 Provider。
- 管理整体 deadline，不让嵌套超时互相打架。
- 聚合逐源诊断，不直接操作 UI。

#### `CandidateRanker`

- 标准化标题、歌手、专辑和版本标记。
- 区分原版、Live、Remix、伴奏、翻唱、纯音乐等候选。
- 使用时长差、ISRC、专辑和 Provider 质量信息评分。
- 输出匹配分数与可解释原因，供日志和测试使用。

#### `SourceHealthRegistry`

- 记录滚动延迟、连续失败、超时、鉴权失败和最近成功时间。
- 连续失败后短时熔断，避免每次搜索都重复等待已知故障源。
- 鉴权失败与网络故障分开处理；鉴权失败需要用户操作，不做高频自动重试。

#### `ResolvedMediaCache`

- 仅保存在内存。
- key 使用 `ProviderId + TrackId + QualityPreference + AuthContextVersion`，防止账号或凭据切换后复用旧签名 URL。
- `AuthContextVersion` 是账号状态变化时递增的非敏感版本号，由账号/Provider 配置层提供，不从 Cookie 或 token 内容派生，也不写入日志。
- 有 `ExpiresAt` 时在到期前 60 秒失效；没有明确过期时间时使用短 TTL。
- 不缓存试听流为完整播放候选。
- Provider 禁用、退出登录、Cookie/凭据变化、服务器地址变化时清空该 Provider 的缓存；应用关闭时释放包含敏感请求头的对象。

#### `MediaUriPolicy`

- 在 URL 交给 LibVLC 前统一校验 scheme、主机、解析后的 IP 和每一次重定向。
- `PublicInternet` Provider 禁止访问 loopback、link-local、RFC1918 私网、组播、未指定地址和云元数据端点。
- `UserConfiguredPrivateNetwork` 只用于用户显式配置的 OpenSubsonic 等私有服务器，并把允许的原始服务器范围绑定到该 Provider 配置。
- 防止 DNS 重绑定：连接前后均按实际解析地址检查，重定向后的目标重新执行完整策略。

#### `PlaybackTransportAdapter`

- 把 `ResolvedMedia` 转换为播放器可执行的请求，负责 User-Agent、Referer、Cookie 等有限传输参数。
- 只接受强类型、白名单字段，拒绝 CR/LF 和任意 LibVLC option 注入。
- Provider 声明的必要参数无法安全映射时返回 `UnsupportedTransport`，不降级为裸 URL 播放。

### 3.2 第一版核心契约

以下为方向性契约，实施时允许根据现有调用点微调命名，但字段语义不得退化为单一字符串 URL。

```csharp
public interface IMusicProvider
{
    MusicProviderDescriptor Descriptor { get; }

    Task<ProviderSearchResult> SearchAsync(
        MusicSearchQuery query,
        CancellationToken cancellationToken);

    Task<MediaResolutionResult> ResolveAsync(
        ProviderTrackRef track,
        MediaQualityPreference quality,
        CancellationToken cancellationToken);
}

public sealed record MusicProviderDescriptor(
    string Id,
    string DisplayName,
    MusicProviderCapabilities Capabilities,
    bool IsExperimental,
    ProviderNetworkScope NetworkScope,
    TimeSpan SearchBudget,
    TimeSpan ResolveBudget);

public enum ProviderNetworkScope
{
    LocalFileOnly,
    PublicInternet,
    UserConfiguredPrivateNetwork
}

// Phase 0 即落地的最小稳定身份模型，持久化和后续 Provider 契约共用。
public sealed record ProviderTrackRef(
    string ProviderId,
    string TrackId);

public sealed record MusicCandidate(
    ProviderTrackRef Track,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    TimeSpan? Duration,
    string? Isrc,
    string? Version,
    Uri? Artwork,
    PlaybackAvailability Availability,
    IReadOnlyList<MediaQuality> Qualities);

public sealed record ResolvedMedia(
    ProviderTrackRef Track,
    Uri Uri,
    DateTimeOffset? ExpiresAt,
    PlaybackHeaders? Headers,
    string? Codec,
    string? Container,
    int? BitrateKbps,
    bool IsPreview);

public sealed record PlaybackHeaders(
    string? UserAgent,
    Uri? Referer,
    string? Cookie);

public sealed record PlaybackRequest(
    ProviderTrackRef Track,
    Uri Uri,
    PlaybackHeaders? Headers,
    ProviderNetworkScope NetworkScope);
```

`PlaybackHeaders` 只存在于内存，不参与歌单序列化、普通日志或诊断报告。若后续确实需要新增传输字段，必须逐项扩展强类型契约和白名单测试，不能恢复为任意字符串字典。

`PlaybackTransportAdapter` 以 `ResolvedMedia` 和受宿主信任的 `MusicProviderDescriptor` 为输入，产生 `PlaybackRequest`；`NetworkScope` 必须取自宿主注册信息，不能接受 Provider 搜索/解析响应自行提权。

### 3.3 Provider ID 与歌曲身份

- `ProviderTrackRef` 必须拆分为 `ProviderId` 和 `TrackId`，不再依靠 `"source:id"` 字符串到处切割。
- 保留旧 `SourceId` 读取兼容层，用一次性迁移解析为新结构。
- 同源唯一性优先使用 `ProviderId + TrackId`。
- 跨源歌曲身份依次参考：ISRC → 标准化标题/歌手 + 时长 → 标题/歌手/专辑。
- 时长差建议：正常歌曲 `<= 3 秒` 为强匹配，`3~8 秒` 降权，超过 `8 秒` 默认不自动替换；Live、Remix 等显式版本另行处理。

## 4. 分阶段实施计划

### 阶段 0：安全、持久化与预算修正

> 目标：不改变现有 UI 和 Provider 总体行为，先消除最明确的安全与正确性问题。

#### 0.0 建立基线与量化门禁

在改动实现前，用固定的脱敏查询样本记录：

- 各 Provider 搜索和解析的成功率、p50/p95 延迟与错误分类。
- Top-1/Top-3 候选可播率，以及原版、Live、Remix、伴奏、翻唱的误匹配样本。
- 播放 URL 失效后完成刷新、跨源替代或进入下一首的总耗时。
- 核心构建是否依赖 Node.js/yt-dlp，以及发布目录中实验性 Provider 的体积。

第一轮默认量化门禁：

- 普通在线搜索硬截止不超过 8 秒；非 YouTube 快速源 p95 目标不超过 5 秒。
- 播放恢复在 12 秒内完成“恢复播放或明确进入下一首”，测试中的故障 Provider 不得突破整体 deadline。
- 歌单、日志和测试产物中敏感 URL、Cookie、token 命中数为 0。
- 各阶段相对于基线不得出现超过 20% 的无解释延迟回退；若真实基线表明目标不合理，先在文档记录调整理由。

#### 0.1 yt-dlp 安装治理

涉及文件：

- `AIRadio.Desktop/Services/YtdlpManager.cs`
- `AIRadio.Desktop/Services/YouTubeMusicService.cs`
- 新增对应测试

任务：

- 使用明确版本的官方 release URL，不直接依赖永久漂移的 `latest`。
- 预期 SHA256 必须随应用版本固定发布，或来自通过固定公钥/可信发布流程验证的版本清单；不能仅同时下载同一来源的未签名二进制和校验文件后互相验证。
- 下载到临时文件，校验成功后原子替换。
- 保存已安装版本；启动只做低频版本检查，不阻塞 UI。常规检查只提示可信清单中的新固定版本，不静默追随 `latest`。
- 定义最低支持版本；根据官方安全公告另行维护明确的封禁版本/区间。低于支持线或命中封禁清单时禁用 YouTube Provider 并给出可理解诊断。
- `--cookies-from-browser` 首次启用时显示隐私提示；日志不打印 Cookie 路径和内容。

验收：

- 哈希不匹配时不会替换现有可执行文件。
- 下载取消后不残留可执行的半成品。
- 已安装旧版本能被识别；离线时保留满足最低版本的现有安装。

#### 0.2 在线曲目持久化迁移

涉及文件：

- `AIRadio.Desktop/Models/Track.cs`
- `AIRadio.Desktop/ViewModels/PlaylistViewModel.cs`
- 播放列表序列化模型与测试

任务：

- 先引入 3.2 中的最小 `ProviderTrackRef`，供持久化和 Phase 1 Provider 契约共用，避免临时身份模型和二次迁移。
- 播放列表格式增加显式版本号，首个新格式固定为 `Version = 2`。
- 新记录保存 Provider/Track 稳定身份和元数据，不保存在线签名 URL。
- 读取旧数据时保留 `SourceId`，忽略可能过期的在线 `FilePath`，首次播放时懒解析。
- 删掉启动时批量刷新所有在线 URL 的必需依赖；只允许可取消的按需预取。
- 重复判断从 URL 改为稳定身份和标准化歌曲身份。
- 迁移先在内存完成；首次成功写入 v2 时使用临时文件 + 原子替换，并保留一代 `playlist.v1.bak`。重复启动和重复保存必须幂等。
- 本地曲目继续保存本地路径；只有 `IsOnline = true` 的曲目禁止保存已解析的远程播放 URL。
- 旧应用不保证读取 v2；README/发布说明记录降级步骤，降级时由用户恢复 `playlist.v1.bak`，新版应用不得自动删除该备份。

建议的持久化形状：

```json
{
  "version": 2,
  "tracks": [
    {
      "provider": { "providerId": "netease", "trackId": "123" },
      "title": "Example",
      "artist": "Artist",
      "isOnline": true
    }
  ]
}
```

验收：

- 旧版 `playlist.json` 可无损读取，收藏状态不丢失。
- 新保存文件中不出现 `http(s)` 在线播放直链和签名参数。
- 同一曲目 URL 变化后再次添加不会产生重复项。
- v1 → v2 连续迁移两次结果一致；模拟写入中断后原文件或备份仍可恢复。

#### 0.3 Cookie 与日志边界

涉及文件：

- `AIRadio.Desktop/Services/KugouMusicService.cs`
- `AIRadio.Desktop/server-kugou/start.js` 及最小路由包装
- 日志和账号相关测试

任务：

- 酷狗本地代理改从自定义 header 或 POST body 接收 Cookie。
- URL、异常文本和逐源报告统一脱敏 `token`、`userid`、`dfid`、Cookie、签名参数。
- 明确浏览器 Cookie 读取只用于用户主动启用的 YouTube Provider。

验收：

- 搜索、播放和错误日志中不出现完整 Cookie/token。
- 本地代理仍只绑定 `127.0.0.1`。

#### 0.4 统一 deadline

涉及文件：

- `AIRadio.Desktop/Services/MultiSourceMusicService.cs`
- `AIRadio.Desktop/Services/AudioService.cs`
- `MultiSourceMusicServiceTests`

任务：

- 区分单源预算与整体搜索/解析 deadline。
- 调用下一源前检查剩余预算，禁止“内层每源 5 秒、外层总共 8 秒”的隐式冲突。
- YouTube 搜索和解析使用独立预算，但必须受整体用户操作 deadline 约束。
- 播放恢复总预算到期后明确进入下一首，不无限叠加重试。

验收：

- 故障源忽略取消时，整体操作仍在 deadline 内结束。
- 替代源恢复至少能按策略尝试两个快速 Provider，或明确记录因总预算不足而跳过。

#### 0.5 构建确定性止血

涉及文件：

- `AIRadio.Desktop/AIRadio.Desktop.csproj`
- `AIRadio.Desktop/server-kugou/package.json`
- `AIRadio.Desktop/server-kugou/package-lock.json`

任务：

- 把现有构建路径中的 `npm install` 改为 `npm ci --omit=dev`，普通构建不得隐式更新 lockfile。
- 将 Node 准备步骤放入显式、可关闭的实验性 Provider 构建目标；Phase 0 暂不要求完成 Provider 项目拆分。
- CI 校验 `package.json` 与 lockfile 一致；依赖缺失时输出明确诊断，不回退执行 `npm install`。
- 完整的代理路由裁剪、独立 Provider 包和“无 Node 核心发布”仍在 Phase 3 完成。

验收：

- 连续两次相同提交的依赖安装不会修改 lockfile。
- 禁用实验性 Node Provider 的构建配置不会执行 npm。
- npm 准备失败不会留下被误判为完整安装的半成品目录。

#### 阶段 0 完成门禁

```powershell
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
git diff --check
```

另执行一次人工验证：旧歌单迁移、在线曲目首次播放、URL 失效重试、YouTube 未安装/旧版本/哈希失败三个场景。

### 阶段 1：Provider 契约与 Broker

> 目标：把硬编码聚合器迁移为可测试、可配置的 Provider 系统，暂不追求动态插件安装。

#### 1.1 建议目录

```text
AIRadio.Desktop/Services/Music/
├── Contracts/
│   ├── IMusicProvider.cs
│   ├── MusicCandidate.cs
│   ├── ResolvedMedia.cs
│   └── ProviderDiagnostics.cs
├── Broker/
│   ├── MusicSourceBroker.cs
│   ├── CandidateRanker.cs
│   ├── SourceHealthRegistry.cs
│   └── ResolvedMediaCache.cs
├── Playback/
│   ├── MediaUriPolicy.cs
│   └── PlaybackTransportAdapter.cs
└── Providers/
    ├── NeteaseProvider.cs
    ├── KuwoProvider.cs
    ├── KugouProvider.cs
    ├── MiguProvider.cs
    └── YouTubeProvider.cs
```

不要求一次性移动全部文件。先新增契约和兼容适配器，使现有 `IMusicSearchService` 调用继续工作，再逐个迁移 Provider。

#### 1.1.1 调用方迁移矩阵

| 调用方 | 当前职责 | Phase 1 目标 | 验收 |
|---|---|---|---|
| `RecommendationService` | 搜索候选并提前解析播放 URL | 通过 Broker 搜索；仅对进入最终节目单的 Top-K 候选有界解析 | 推荐对象不持久化临时 URL |
| `DJService` / `ChatViewModel` | 点歌、选择结果并直接调用现有聚合器 | 只依赖 Broker/兼容接口，不转换为 `MultiSourceMusicService` | 点歌可得到逐请求诊断且支持取消 |
| `PlaylistViewModel` | 保存 URL、按 URL 去重和启动刷新 | 保存 `ProviderTrackRef`，播放时懒解析 | 无在线 URL 落盘，无 URL 去重逻辑 |
| `AudioService` | 接收 URL/SourceId 并直接创建 LibVLC Media | 接收经过 URI 策略和传输适配的 `PlaybackRequest` | 必需 Header 可用且无法注入任意 option |
| `MainWindowViewModel` | 组合搜索、推荐与播放状态 | 仅做编排，不新增音源评分或重试逻辑 | 无具体 Provider 类型依赖 |

阶段结束时，除 DI 注册、Provider 适配器和临时兼容层外，业务代码不得出现对 `MultiSourceMusicService` 的具体类型转换。兼容层在所有调用方迁移完成后删除。

#### 1.2 注册与配置

- 使用 DI 注册 `IEnumerable<IMusicProvider>`，删除 `MultiSourceMusicService` 构造函数里的具体类型 `new`。
- Provider 设置包含：启用状态、用户优先级、默认音质、是否允许跨源替代。
- 对旧设置提供默认迁移；已有用户保持当前启用状态，新安装首次启用实验性在线源时展示说明。
- Provider 的“未登录”“登录失效”“网络故障”“业务限制”使用不同诊断码。

#### 1.3 搜索协调

- 第一波启动低延迟、高健康度 Provider。
- 第一版采用**批量返回语义**：获得足够高质量候选或到达软截止后，取消尚未完成的 Provider，收集已完成结果并一次性返回；返回后结果集合不再被后台任务修改。
- 所有已启动任务仍受同一个硬截止和取消令牌约束。若未来需要渐进展示，另行引入 `IAsyncEnumerable<SearchUpdate>`，不得在 `Task<List<...>>` 返回后隐式补写结果。
- 不再因为网易前三条有一条可播就完全跳过其他来源。
- 每个 Provider 搜索结果只做轻量元数据处理；普通搜索列表在用户播放时解析 URL。
- AI 推荐先按元数据取 Top-K，再在剩余 deadline 内有界解析，只有已确认满足播放意图的候选进入最终节目单；不得对全部搜索结果预解析。

建议第一版权重：

| 评分项 | 权重 |
|---|---:|
| 标题标准化精确匹配 | 25 |
| 主要歌手匹配 | 25 |
| 时长接近 | 15 |
| 专辑/版本一致 | 10 |
| 搜索阶段可用性（完整/试听/受限） | 15 |
| Provider 健康度与用户优先级 | 10 |

存在 ISRC 精确匹配时可直接提升为最高置信候选；出现 Live/Remix/伴奏/翻唱冲突时施加显著降权。

元数据匹配分与播放适合度分别保存，避免“标题高度匹配但只能试听”掩盖完整可播候选。用户主动点击试听结果时允许播放试听；AI 自动节目单默认过滤 `Blocked`、`AuthRequired` 和不满足完整播放意图的 Preview。

#### 1.4 熔断与诊断

- 网络类连续 3 次失败后进入短暂冷却，例如 60 秒。
- 认证失败立即标记 `AuthRequired`，直到账号状态变化或用户主动重试。
- 成功请求逐步恢复健康度，不永久惩罚单次抖动。
- `LastSearchReport` 改为每次请求自带的不可变报告，避免并发搜索共享全局可变列表。

#### 阶段 1 验收

- `MusicSourceBroker` 不依赖任何具体 Provider 类型。
- 相同测试输入不受 Provider 完成先后顺序影响，排名结果稳定。
- 并发两次搜索不会串用逐源状态报告。
- 批量搜索返回后没有后台 Provider 再修改结果；取消后不存在遗留搜索任务。
- 单一 Provider 熔断不影响其他 Provider。
- 公网 Provider 返回私网/回环/重定向私网 URL 时在进入 LibVLC 前被拒绝；用户配置的 OpenSubsonic 私网地址仍可按显式信任范围访问。
- 切换账号、Cookie 或服务器配置后不会命中旧 `ResolvedMedia` 缓存。
- 除临时兼容层外，业务服务不再具体依赖或转换 `MultiSourceMusicService`。
- 原有搜索、点歌、推荐和自动续播行为保持可用。

### 阶段 2：稳定主源

> 目标：让 TuanRadio 在所有实验性在线源都失效时仍是完整可用的个人 AI 电台。

#### 2.1 LocalLibraryProvider

- 将当前“导入文件”能力提升为可搜索的本地 Provider。
- 索引标题、歌手、专辑、时长和本地路径；首版可使用轻量 JSON/SQLite 索引。
- 文件变更采用手动重新扫描或低频增量扫描，首版不引入复杂实时监听。
- 本地文件始终优先于跨源同曲候选，除非用户调整优先级。

#### 2.2 OpenSubsonicProvider

- 支持 Navidrome 等 OpenSubsonic 服务的连接配置、认证测试、搜索和 stream URL。
- 凭据进入现有安全存储，不写普通 settings JSON。
- 使用 OpenSubsonic 稳定接口，不调用 Navidrome 未承诺兼容的内部 API。
- 支持服务端转码信息映射到 `ResolvedMedia`。

#### 2.3 稳定主源验收

- 禁用五个实验性 Provider 后，本地搜索、节目单生成、播放、收藏和自动续播仍完整工作。
- OpenSubsonic 断线时可降级到本地曲库，不阻塞 AI DJ。
- TTS duck/pause、频谱、seek、上一首/下一首对两类稳定源行为一致。

### 阶段 3：Provider 包与构建瘦身

> 目标：把高变动、重依赖的第三方适配器从核心运行时中隔离。

#### 3.1 迁移策略

1. 先把每个现有 Provider 迁移到独立命名空间和独立测试 fixture。
2. 再评估拆成独立 .NET 项目，例如：
   - `TuanRadio.Providers.Netease`
   - `TuanRadio.Providers.Kugou`
   - `TuanRadio.Providers.YouTube`
3. 核心只依赖 Provider 契约，不引用具体 Provider 项目。
4. 首版可以随官方构建一起发布，但必须可以在构建配置中排除。
5. 第一轮不开放用户动态安装 Provider；仓库内审查过、随官方构建发布的 Provider 才允许进程内运行。
6. 后续若允许社区 Provider，必须进程外运行，采用 manifest + API/协议版本 + 哈希 + 能力声明，并限制启动时间、请求并发、内存和退出超时。哈希只用于完整性识别，不视为沙箱或可信证明。
7. 宿主进程不执行远程下载的任意 JS，也不加载用户提供的社区 .NET 程序集。

#### 3.2 Node 代理治理

- 延续 Phase 0 的 `npm ci --omit=dev` 和显式构建开关，在本阶段完成 Node Provider 独立包与核心发布解耦。
- 酷狗代理只发布实际使用的登录、搜索、播放路由和运行时依赖，不复制文档、演示页面、无关 API 模块和开发工具。
- 生成第三方组件清单、版本、许可证和来源 commit。
- Provider 缺失时显示“未安装/不可用”，不能导致整个桌面项目构建失败。

#### 阶段 3 验收

- 核心项目在无 Node.js、无 yt-dlp、无实验性 Provider 的环境可构建并运行。
- 发布目录不包含无关酷狗 API 页面、开发配置和模块。
- Provider 包升级不要求修改 `AudioService`、`PlaylistViewModel` 或 `RecommendationService`。

### 阶段 4：开放曲库与体验完善

> 目标：在不依赖主流平台私有接口的情况下扩充可合法访问的在线候选池。

按以下顺序评估，不要求全部实现：

1. **AudiusProvider**：公开搜索和流式能力与 AI 电台契合，先做技术 PoC。
2. **SoundCloudProvider**：遵守署名、来源链接和可播放状态要求，只使用允许站外播放的曲目。
3. **JamendoProvider**：适合作为独立音乐、氛围音乐和 Creative Commons 候选池。
4. **RadioBrowserProvider**：作为真实网络电台模式，不参与精确点歌和逐曲节目单替换。

UI 后续增加：

- Provider 启用、优先级、登录状态、最近健康度。
- 搜索结果显示实际 Provider、音质、试听/完整播放状态。
- 跨源替换时保留原推荐理由，但显示实际播放来源。
- 用户可以选择“稳定优先”“音质优先”“覆盖优先”。

## 5. 测试策略

### 5.1 单元测试

- 标题、歌手、版本和时长标准化。
- 同曲、Live、Remix、伴奏、翻唱的评分矩阵。
- 单源超时、忽略取消、认证失败、畸形响应。
- deadline 剩余预算和熔断状态转换。
- 临时 URL 缓存命中、提前过期和刷新失败。
- 账号/Cookie/服务器配置变化后的缓存隔离和敏感 Header 释放。
- 公网 URL 指向 loopback、IPv4/IPv6 私网、link-local、云元数据地址、DNS 重绑定和重定向私网的拒绝测试。
- OpenSubsonic 显式私网范围允许测试，以及跨越已配置范围的重定向拒绝测试。
- User-Agent、Referer、Cookie 映射和 CR/LF/任意 LibVLC option 注入拒绝测试。
- 旧歌单迁移、新格式幂等回写、写入中断恢复、降级备份和敏感字段不落盘。
- Provider 请求报告的并发隔离。
- 批量搜索返回后集合不变、遗留任务已取消；未来若改渐进接口则另建契约测试。
- 架构测试确保业务层不具体依赖 `MultiSourceMusicService` 或任意 Provider 实现。

### 5.2 响应 fixture 测试

- 为每个实验性 Provider 保存脱敏后的成功、空结果、试听、未登录、风控和字段变形响应。
- fixture 必须删除 Cookie、token、用户 ID、签名 URL。
- Provider 解析测试默认零联网、可重复运行。

### 5.3 联网 canary

- 保留 `AIRADIO_INTEGRATION_TESTS=1` 显式开关。
- 匿名接口只验证响应形状和错误分类，不把“必须搜到某首商业歌曲”作为 CI 硬条件。
- 登录态 Provider 不在公共 CI 注入个人账号 Cookie。
- 可配置定时 canary 输出音源健康报告，但不影响普通 PR 的单元测试结果。

### 5.4 人工耐久矩阵

每个阶段完成后至少覆盖：

- 连续播放 30 分钟。
- TTS 插播期间暂停/duck 与恢复。
- 快速连续切歌、seek、上一首。
- 播放 URL 过期、403、连接中断和过早结束。
- Provider 登录过期和重新登录。
- 睡眠唤醒、网络切换、关闭窗口。

## 6. 开源发布门禁

当前仓库根目录没有明确的主项目许可证。正式公开发布前必须完成：

- 选择主仓库许可证：若希望衍生版本继续开源，可评估 GPL-3.0-or-later；若优先降低复用门槛，可评估 MIT/Apache-2.0。
- 明确“项目方不经营商业音乐分发”是产品运营边界，不是许可证中的 Non-Commercial 附加条款；MIT、Apache-2.0、GPL 等 OSI 开源许可证都允许商业使用。若加入禁止商业使用条款，项目将不再属于通常意义上的开源软件。
- 添加根目录 `LICENSE`、`THIRD-PARTY-NOTICES.md` 和依赖来源清单。
- 确认所有随包复制的 Node、WASM、二进制和素材允许再分发，并保留相应许可证文本。
- README 明确：项目是个人媒体工具；实验性 Provider 与对应平台无隶属关系；可用性取决于地区、账号、版权和上游接口；用户应遵守所在地区法律和平台条款。
- 不在 README、UI 或日志中宣称能够“解锁会员”“绕过版权”或保证所有歌曲可播放。
- 发布包提供依赖版本或 SBOM，并记录 yt-dlp/Node 运行时的校验信息。

许可证选择属于发布决策，本计划不代替项目所有者作最终选择。

## 7. 明确非目标

- 不在本轮引入长期用户画像数据库。
- 不把音源协调逻辑继续塞入 `MainWindowViewModel`。
- 不重写 LibVLC 播放器、FFT、TTS 或现有 Radio Mode 状态机。
- 不承诺第三方私有接口永久可用。
- 不提供下载受版权保护音乐、解密 DRM 或绕过会员限制的能力。
- 不为了插件化立即引入复杂插件商城、自动执行远程脚本或多进程沙箱。
- 第一轮不支持用户动态安装社区 Provider；未来开放时，进程外隔离和资源限制是前置条件，不作为可选增强。
- 不在尚未完成契约和持久化迁移前继续增加新的私有网页音源。

## 8. 推荐提交拆分

为保持变更小而可验证，建议按以下提交顺序实施：

1. `test:建立音源延迟可播率与恢复耗时基线`
2. `security:校验并治理yt-dlp安装版本`
3. `refactor:引入稳定音源身份并迁移在线歌单`
4. `security:移除酷狗Cookie查询参数并统一日志脱敏`
5. `fix:统一音源搜索与恢复deadline`
6. `build:固定Node依赖安装并增加实验性Provider开关`
7. `refactor:新增Music Provider契约与兼容适配层`
8. `security:增加播放URL策略与传输参数适配`
9. `feat:引入候选评分、可播探测与音源健康熔断`
10. `refactor:迁移搜索推荐播放调用方并移除具体类型依赖`
11. `feat:新增本地曲库Provider`
12. `feat:新增OpenSubsonic Provider`
13. `build:拆分实验性Provider并裁剪Node运行时`
14. `docs:补齐开源许可证与第三方组件声明`

每个提交均需保持 build/test 通过；涉及序列化格式、账号存储或播放恢复的提交必须包含回归测试，不把多个高风险迁移合并为一次大改。

## 9. 参考项目与资料

- [FeelUOwn](https://github.com/feeluown/FeelUOwn)：Provider 插件、智能换源、多音质设计。
- [MusicFree](https://github.com/maotoumao/MusicFree)：播放器与音源插件解耦，可参考协议边界但不直接采用任意 JS 执行。
- [Listen1](https://github.com/listen1/listen1_chrome_extension)：多平台搜索和自动换源。
- [LX Music](https://github.com/lyswhut/lx-music-desktop) / [Any Listen](https://github.com/any-listen/any-listen)：从内置在线源转向自定义源、私有曲库和 WebDAV。
- [Navidrome](https://github.com/navidrome/navidrome) / [OpenSubsonic](https://github.com/opensubsonic/open-subsonic-api)：个人音乐服务器和稳定客户端协议。
- yt-dlp 官方[发布页](https://github.com/yt-dlp/yt-dlp/releases)与[安全公告](https://github.com/yt-dlp/yt-dlp/security/advisories)。
- [Audius](https://docs.audius.co/)、[SoundCloud](https://developers.soundcloud.com/docs/api/)、[Jamendo](https://developer.jamendo.com/v3.0) 开发资料。
