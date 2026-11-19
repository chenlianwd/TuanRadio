# 局域网 Web 遥控器扩展（Web Remote v2：选台、曲库与播客浏览）设计方案

> **文件路径**：`docs/plans/2026-11-18-web-remote-browse-extension-design.md`  
> **创建日期**：2026-11-18  
> **状态**：已实施（2026-11-18，提交 `2666fc2`）  
> **前置基础**：`docs/plans/2026-11-17-web-remote-design.md`（已落地播控、进度/音量调节、场景切换、歌词与 AI DJ 对话）

---

## 1. 目标与边界

在保留 `v1.1.0` Web Remote 零第三方依赖、单文件离线 SPA 与常量时间令牌鉴权模型的基础上，将手机端从「纯遥控器」扩展为「**选台与内容点播台**」：

1. **核心范围（v2 聚焦浏览与点播）**：
   - 支持在手机端直接浏览并点播 **AI 节目单**、**播放列表**、**我的收藏（歌曲 + 电台）**、**全球/自定义网络电台** 与 **播客订阅及单集（含 AI 速读触发）**。
   - 保持桌面端为配置管理主入口：自定义电台导入与播客 RSS 订阅管理仍由桌面端承担，避免在手机端引入复杂表单与文件上传。
2. **性能与架构原则**：
   - **热推送与按需浏览分离**：WebSocket `WebRemoteSnapshot` 继续保持轻量，仅推送播控状态、歌词与最近 30 条聊天；不将海量歌单/电台/播客列表打入每 200ms 的进度广播。
   - **按需鉴权拉取**：移动端仅在打开「选台 / 曲库」页签、切换子模块或搜索时请求 `GET /api/browse`，日常待机与播放零额外开销。

---

## 2. 服务端接口与契约设计

### 2.1 接口与路由扩展（`IWebRemoteService` / `WebRemoteService`）

- `IWebRemoteService` 新增带默认空实现的 `ConfigureBrowseHandler(Func<WebRemoteBrowseRequest, CancellationToken, Task<WebRemoteBrowseResponse>>)`，保持既有单测与 Stub 零破坏兼容。
- [WebRemoteService.cs](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Services/WebRemote/WebRemoteService.cs) 的 `ReadHttpRequestAsync` 保留完整 query 参数字典解析（支持大小写不敏感查参、`+` 与 `%` 解码，遇畸形转义跳过且保留 `ExtractTokenFromQuery` 触发 `401` 的既定行为），新增受令牌鉴权保护的路由：
  - **`GET /api/browse?section=<...>&category=<...>&feed=<...>&q=<...>&force=<0|1>`**
  - 为 `/api/browse` 绑定 15 秒超时 `CancellationTokenSource`（与服务 `_runCts.Token` 级联），防止慢网络挂死 HTTP 连接。
  - 严格沿用现有 `IsAuthorized`（`X-Remote-Token` / `Authorization: Bearer` 常量时间比对）、`Cache-Control: no-store`、`X-Content-Type-Options: nosniff`、`X-Frame-Options: DENY` 与 `Referrer-Policy: no-referrer`。

### 2.2 `GET /api/browse` 五大视图切片（均做条数上限保护与读写解耦）

- **读写解耦原则**：`library`、`favorites`、`podcasts` 的 `q` 关键字过滤均在服务端构造响应时对底层集合（`Tracks`、`Favorites`、`AllPodcastEpisodes`）做纯内存只读筛选，**严禁修改**桌面端 `LibraryFilterText`、`FavoriteFilterText`、`PodcastFilterText`，避免手机端搜歌污染桌面抽屉视图。
- **冷启动按需补齐**：当桌面抽屉从未打开过电台或播客标签时，`radio` 与 `podcasts` 切片自动按需触发 `LoadInitialRadioStationsAsync()` / `LoadInitialPodcastsAsync()`。
- **单集与分类同步无竞态**：通过 `PlaylistViewModel` 内部方法 `SelectAndLoadRadioCategoryAsync` 与 `SelectAndLoadPodcastFeedAsync` 统一同步分类/订阅源选择与单次拉取，避免双重并发请求竞态与桌面状态失步。

| `section` | 数据来源 | 支持查询参数 | 返回核心字段与能力 |
| :--- | :--- | :--- | :--- |
| `program` | 当前 AI 推荐节目单 | `force=1`（或走 `refresh_program` 指令） | 节目单开场提示与推荐曲目列表（`id`、`title`、`artist`、`durationSeconds`、`tags`、`playability`、`isCurrent`），上限 50 条 |
| `library` | `PlaylistVM.Tracks`（纯内存只读过滤） | `q`（按歌名/歌手/专辑过滤，上限 200 条） | `index`（真实歌单索引）、`id`、`title`、`artist`、`album`、`source`、`durationSeconds`、`isFavorite`、`isCurrent` |
| `favorites` | `PlaylistVM.Favorites`（歌曲/电台分流，纯内存只读过滤） | `q`（关键字过滤，上限各 200 条） | 分别返回 `songs` 与 `stations` 两个数组，对齐桌面端收藏隔离契约 |
| `radio` | `PlaylistVM.RadioCategories` / `RadioStations` | `category`、`q`（搜台）、`force=1` | 全量 10 个流派（含 `favorites` 与 `custom`）、当前选中分类及台站列表（`stationUuid`、`name`、`tags`、`codec`、`bitrate`、`isCustom`、`isFavorite`、`isCurrent`） |
| `podcasts` | `PlaylistVM.PodcastFeeds` / `AllPodcastEpisodes`（纯内存只读过滤） | `feed`（RSS URL 或 FeedId）、`q`、 `force=1` | 已订阅 `feeds` 列表、当前选中 `feedUrl` 及单集列表（`episodeKey`、`title`、`meta`、`shortSummary`、`resumePositionMs`、`isCompleted`、`progressBadge`、`hasSummary`、`isDigesting`） |

### 2.3 遥控指令与状态帧扩展（`MainWindowViewModel.WebRemote.cs` / `WebRemoteSnapshot`）

- **`WebRemoteSnapshot` 增量状态字段**：新增 `isProgramLoading`（`bool`）与 `digestingEpisodeKey`（`string`），供移动端实时渲染节目单刷新中及播客单集 AI 速读进行中状态。
- **非阻塞异步命令执行模型**：`RunWebSocketSessionAsync` 单客户端循环按顺序等待命令 ACK，因此耗时后台动作（`refresh_program` 涉 LLM/搜歌、`digest_podcast_episode` 涉 LLM/最长 60s TTS）在 UI 线程完成参数与前置校验后即启动后台任务并**立即返回 `WebRemoteCommandResult.Success()`**，通过 WebSocket 状态帧推播进度，严禁阻塞单客户端 WebSocket 通道；`play_podcast_episode` 校验目标单集安全策略后启动播放上下文。
- **电台跨视图解析与收藏切换修复**：
  - 修复 `PlaylistViewModel.ToggleFavoriteCommand` 取消收藏直播电台时因 `FindMatchingTrack` 仅查 `Tracks` 导致 `Favorites.Remove` 引用不匹配无法移除的问题（回退匹配 `Favorites` 内同 `Id`/`SourceId` 实例并置 `IsFavorite = false`）。
  - `play_radio_station` 与 `toggle_radio_favorite` 按 `RadioStations` → `CustomStationsCache`（按需加载）→ `Favorites` 三级路径兜底解析 `stationUuid`。

所有新增命令均通过 `DispatchWebRemoteCommandAsync` 调度至 UI 线程执行：

| `action` | 参数 | 行为与边界保护 |
| :--- | :--- | :--- |
| `play_program_track` | `text`（Track Id/SourceId）或 `intValue`（索引） | 调用桌面端节目单点播链路；未命中返回 `track_not_found`，不可播返回 `track_not_playable` |
| `refresh_program` | 无 | 在 UI 线程异步触发 `LoadProgramAsync(force: true)` 并立即返回 `ok=true`（不阻塞 WS） |
| `play_library_track` | `text`（Track Id/SourceId）或 `intValue`（索引） | 在 `PlaylistVM.Tracks` 定位并执行 `PlayFromLibraryCommand`；未命中返回 `track_not_found` |
| `enqueue_library_track` | `text`（Track Id/SourceId），`intValue`（可选，`2`=追加至队尾） | 在 `Tracks` / `Favorites` 查找单曲；默认调用 `PlayTrackNextCommand` 插播下一首，`intValue == 2` 时调用 `EnqueueTrackCommand` 追加至队尾 |
| `play_favorite` | `text`（Track Id/SourceId 或 `stationUuid`） | 在 `PlaylistVM.Favorites` 定位后执行 `PlayFavoriteCommand`（单曲按收藏歌单上下文连播，直播电台直接切台） |
| `play_radio_station` | `text`（`stationUuid`） | 按当前台站列表 → 自定义电台缓存 → 收藏电台三级路径解析并切台播放 |
| `toggle_radio_favorite` | `text`（`stationUuid`） | 按三级路径解析电台并切换收藏状态，同步 `IsCurrentFavorite` 与状态帧 |
| `play_podcast_episode` | `text`（`episodeKey`），`intValue`（`1`=从头听） | 调用 `PlaylistVM.PlayPodcastEpisodeAsync`（含 URI 安全校验，拦截时返回 `unsafe_audio_url`） |
| `digest_podcast_episode` | `text`（`episodeKey`） | 校验单集存在且 `Summary` 非空后，异步启动 `DigestPodcastEpisodeAsync` 并立即返回 `ok=true`，结果同步推入聊天流并朗读 |

---

## 3. 移动端单文件 SPA 交互设计（`WebRemoteHtml.cs`）

1. **三页签底部/中段切换**：
   - 将原「**歌词** / **点歌 · AI DJ**」扩展为「**歌词** / **选台 · 曲库** / **点歌 · AI DJ**」。
2. **「选台 · 曲库」视图结构**（单手操作友好，动态文本严格 `.textContent` 赋值防 XSS）：
   - **子模块分段胶囊**：`节目单` / `歌单` / `收藏` / `电台` / `播客`。
   - **上下文工具栏**：关键字输入框 + 刷新按钮；电台模块展示横向可滚动流派芯片，播客模块展示订阅源下拉选择器。
   - **卡片交互**：
     - **节目单 / 歌单 / 收藏歌曲**：单行紧凑条目，当前在播高亮，点行即播（歌单行右侧提供快捷「下一首」）。
     - **网络电台 / 收藏电台**：显示台名、流派标签与编码码率徽标，点卡片即刻切台，右侧附独立红心收藏切换按钮。
     - **播客单集**：展示标题、日期/时长/编码、续播或已播完胶囊与 2 行摘要；提供「收听」「从头」「AI 速读」三个动作按钮。
3. **状态联动与双语**：
   - 桌面端切换曲目或触发速读后，通过既有 WebSocket 状态帧实时更新当前在播高亮与聊天消息区。
   - 全部新增 UI 词条、空态与错误提示随 `WebRemoteSnapshot.language`（`zh` / `en`）自动切换。

---

## 4. 线程模型与安全约束

- `GET /api/browse` 中若触发异步加载（如 `LoadRadioStationsByCategoryAsync`、`LoadPodcastEpisodesAsync`），统一调度至 UI 线程通过 `SelectAndLoadRadioCategoryAsync` / `SelectAndLoadPodcastFeedAsync` 执行以避免并发修改 `ObservableCollection` 与双发竞态，并由前端做 220ms 请求防抖与竞态序号丢弃；服务端绑定 15 秒请求超时保护。
- 耗时指令（`refresh_program`、`digest_podcast_episode`）在完成前置校验后异步后台运行并立即回传 `ack`，通过 WebSocket 状态帧（`isProgramLoading`、`digestingEpisodeKey`）推送进展，防止阻塞单客户端 WebSocket 接收循环。
- 播放前安全校验继续复用桌面端既有的 `MediaUriPolicy` 与 `PlayPodcastEpisodeAsync` 链路，手机端仅传递业务标识（`id` / `stationUuid` / `episodeKey`），不接受来自客户端的任意 `streamUrl` / `audioUrl` 注入。

---

## 5. 测试与验收计划

1. **服务端与路由单测（`WebRemoteServiceTests`）**：
   - `GET /api/browse` 未鉴权/错令牌返回 `401`，合法令牌携带 `query` 参数正确透传至 browse handler 并返回 `200` + `no-store`。
2. **ViewModel 与指令联动单测（`WebRemoteViewModelTests`）**：
   - 校验 `program` / `library` / `favorites` / `radio` / `podcasts` 五类 `browse` 响应构造与纯内存关键词过滤（不污染桌面 `LibraryFilterText` / `FavoriteFilterText`）；
   - 校验点播节目单、歌单播放/下一首插播/队尾追加、收藏播放、电台切台及收藏/取消收藏闭环、播客续播/从头播/AI 速读（非阻塞返回）及错误码兜底（如不存在 ID、空消息、不可播等）；
   - 校验新增浏览与播放链路不绕过播客 URI 安全策略。
3. **构建与全量回归**：
   - Debug 与 Release（标准 + 精简）均需 0 警告 0 错误，全量测试通过。
