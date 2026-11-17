# TuanRadio 播客生态接入与 AI 速读（Podcast RSS & AI Digest）设计方案

> **文件路径**：`docs/plans/2026-11-17-podcast-rss-and-ai-digest-design.md`  
> **创建日期**：2026-11-17  
> **状态**：已实施（2026-11-17）  
> **设计目标**：接入零版权风险、稳定长效的 Podcast RSS 长音频生态，形成「音乐点播 + 全球/自定义网络电台 + 播客订阅与 AI DJ 一分钟速读」的完整电台内容矩阵。

---

## 1. 产品定位与核心交互（v1 闭环）

1. **订阅体验（开箱即用 + 自由扩展）**
   - 在曲库抽屉新增独立页签 **Tab 6「播客 / Podcasts」**；
   - 首次启动自动以 **内置精选公开中英文播客**（科技 / 人文 / 新闻 / 故事）初始化订阅列表，开箱即可浏览与收听；
   - 支持点击 `+` 内嵌订阅面板粘贴任意 RSS / Atom URL 添加自定义播客，支持手动刷新 `↻`、取消订阅及按标题/内容即时过滤单集。
2. **长音频播放与断点续播记忆**
   - 区别于 24/7 网络直播电台（`IsLiveStream`）：播客单集保留真实时长与进度条拖拽能力（可 Seek、`SleepTimerMode.EndOfTrack` 可用），同时**不触发歌词抓取**、**不污染音乐口味画像与音乐推荐历史**、**失败/提前结束不进网易云/酷狗跨源搜歌回退**；
   - 播放过程中定期及在暂停/切曲/退出时持久化单集播放进度（`PositionMs`），再次点击继续收听时直接从上次断点（>3s）恢复，播完后自动标记为已播完；右键及菜单额外提供「从头播放」。
3. **AI DJ 播客一分钟速读（独立按钮，Shownotes 优先）**
   - 每条单集卡片提供独立 **「AI 速读」** 按钮（不阻塞正文点播，随时可听）：
     - 已配置大模型时，由 `DJService` 基于单集标题与清洗后的 Shownotes 生成 1 分钟左右自然口语导听（兼顾当前 `RadioSceneMode` 语气，如专注/伴眠保持 `[calm]`），写入聊天区并触发 Edge TTS + 徽章辉光朗读（自动压低在播声音量）；
     - 未配置大模型或请求异常时，平滑降级为从 Shownotes 提炼句边界摘要的零配置播报（不把“请先配置 AI 服务”当台词念出）；若无 Shownotes 则友好提示。

---

## 2. 架构与安全设计

### 2.1 领域模型（`AIRadio.Desktop.Music`）
- `PodcastFeed`：`FeedId`、`Title`、`Author`、`Description`、`RssUrl`、`ImageUrl`、`Category`、`Language`、`IsCurated`、`LastUpdatedUtc`；定义 `SourcePrefix = "podcast:"`。
- `PodcastEpisode`（`INotifyPropertyChanged`）：`EpisodeKey`（基于 `FeedUrl` + `Guid/AudioUrl` 的稳定 SHA256 摘要）、`FeedUrl`、`FeedTitle`、`Title`、`Author`、`AudioUrl`、`PublishedAt`、`Duration`、`Summary`、`Codec`、`BitrateKbps`，以及运行时状态 `ResumePositionMs`、`IsCompleted`、`IsDigesting`。
- `Track.IsPodcastEpisode`：`SourceId` 以 `podcast:` 开头或 `ProviderMetadata["is_podcast"] == "true"` 时返回 `true`（与 `IsLiveStream` 正交）。

### 2.2 RSS / Atom 安全抓取与解析（`IPodcastRssService` / `PodcastRssService`）
- **SSRF 防护**：初始 `rssUrl` 与手动跟随的每一跳 3xx 重定向（最多 5 跳）均经过 `MediaUriPolicy.ValidateCustomRadioAsync` 校验（允许公网与局域网自托管服务，严格拦截 `localhost`、`127.0.0.0/8`、`::1`、`169.254.0.0/16`、组播及含 `UserInfo` 的 URL）；解析出的音频 `<enclosure>` URL 同步做 scheme/host 安全拦截。
- **体积与 XML 防护**：15 秒超时 + 最大响应流读取上限 **5 MB**；`XmlReaderSettings` 强制 `DtdProcessing = DtdProcessing.Prohibit` 且 `XmlResolver = null` 彻底杜绝 XXE 攻击。
- **双协议兼容**：同时支持 **RSS 2.0**（`<rss><channel><item>`，兼容 `<itunes:duration>`、`HH:MM:SS` / `MM:SS` / 秒数、`<content:encoded>` / `<itunes:summary>` / `<description>` 与 `<enclosure>`）及 **Atom**（`<feed><entry>` 与 `<link rel="enclosure">`）；Shownotes 自动剥离 HTML 标签并解码实体。
- **内存缓存**：按 RSS URL 缓存 10 分钟（支持 `forceRefresh: true` 绕过）。

### 2.3 持久化与断点续播（`IPodcastStorage` / `PodcastStorage`）
- 持久化至 `%APPDATA%\AIRadio\podcasts.json`（与 `CustomRadioStorage` 同款 `SemaphoreSlim` 串行 + `.tmp` 原子替换 + 损坏自愈）。
- 首次加载若文件不存在，自动注入 `CuratedPodcastRegistry` 精选源并标记已初始化（用户取消订阅不会在下次启动被强行覆盖回来）。
- 断点进度字典以 `EpisodeKey` 为键保存 `PositionMs`、`DurationMs`、`IsCompleted`、`UpdatedAtUtc`（上限保留最近 500 条）；播放中按 10 秒节流落盘，暂停/切曲/自然结束/退出时即时 `FlushAsync`。
- `AudioService.PlayTrackCore` 在挂载 `track.IsPodcastEpisode` 时读取 `ProviderMetadata["resume_ms"]`（> 3000ms），通过 LibVLC `:start-time=<seconds>` 选项直接从断点起播。

---

## 3. UI 与交互细节

- **抽屉页签**：[PlaylistDrawer.axaml](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Views/PlaylistDrawer.axaml) 顶栏按钮簇在网络电台之后新增 `TabIndex = 6` 播客图标按钮（`OpenPodcastsCommand`）。
- **页签内容**：
  - 顶部栏：订阅播客 `ComboBox`（展示播客名 + 作者）+ 刷新 `↻` + 订阅 `+` + 取消订阅 `✕`；
  - 次行：单集关键字即时过滤输入框；
  - 可折叠内嵌面板：RSS URL 输入框 +「订阅并加载」+ 校验提示；
  - 单集卡片列表：标题、时长/日期/编码信息、断点/已播完状态胶囊、2 行摘要预览、「AI 速读」按钮、「播放」按钮及右键菜单（继续播放 / 从头播放 / AI 速读）。

---

## 4. 测试规划

1. **RSS 解析与安全单测**（`PodcastRssServiceTests`）：RSS 2.0 / Atom 解析、多种时长格式、HTML Shownotes 清洗、XXE DTD 拦截、SSRF（`127.0.0.1` / `169.254.169.254` / `302 -> 127.0.0.1`）拦截、5 MB 超限拦截与内存缓存命中/强制刷新。
2. **存储与断点进度单测**（`PodcastStorageTests`）：首次精选初始化、自定义订阅/去重/退订、断点保存/完播归零/500 条上限淘汰与原子写入损坏自愈。
3. **播放生命期、AI 速读与 UI 联测**（`PodcastPlaybackAndDigestTests`）：`Track.IsPodcastEpisode` 旁路歌词/听歌画像/推荐历史/歌曲跨源回退、LibVLC `:start-time` 选项注入逻辑、LLM 速读 Prompt（含消毒与场景情绪）及无 Key 兜底摘要、以及 `PlaylistDrawer` Tab 6 控件绑定与单集列表渲染。
