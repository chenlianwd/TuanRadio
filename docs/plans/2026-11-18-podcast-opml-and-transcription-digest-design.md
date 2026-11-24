# TuanRadio 播客 OPML 导入导出与无 Shownotes 转写速读（v2）设计方案

> **文件路径**：`docs/plans/2026-11-18-podcast-opml-and-transcription-digest-design.md`  
> **创建日期**：2026-11-18  
> **状态**：已实施  
> **前置基础**：[2026-11-17-podcast-rss-and-ai-digest-design.md](2026-11-17-podcast-rss-and-ai-digest-design.md)

---

## 1. 目标

在播客 v1（RSS 订阅、断点续播、基于 Shownotes 的 AI 一分钟速读）基础上补齐两项高频刚需：

1. **OPML 订阅迁移闭环**：支持导入标准 OPML 1.0/2.0 文件批量订阅播客，并支持将当前订阅列表导出为规范 OPML 2.0 XML 备份。
2. **无 Shownotes 单集转写速读**：当播客单集未提供文字简介（`Summary` 为空）时，安全流式抓取音频前段、本地转码并调用内置 Whisper STT 识别口述开篇，再由 AI DJ 生成一分钟导听（并在桌面端与局域网 Web Remote 共享同一能力）。

---

## 2. OPML 导入导出设计

- **解析与序列化（`AIRadio.Desktop.Music` — `IPodcastOpmlService` / `PodcastOpmlService`）**：
  - **安全限制**：`DtdProcessing = DtdProcessing.Prohibit`、`XmlResolver = null`、`MaxCharactersFromEntities = 1024`，单文件上限 5 MB、最多 1000 个 `<outline>` 节点。
  - **结构兼容**：递归遍历 `<opml><body><outline>`，兼容嵌套分类目录（父节点无 `xmlUrl` 时继承 `title`/`text` 作为 `Category`），兼容 `xmlUrl` 大小写及 `type="rss"`/`"atom"` 回退读取 `url`，仅放行 `http`/`https` 绝对地址并按规范化 URL 去重。
  - **导出**：生成 UTF-8 OPML 2.0 XML，含 `<head><title>`、`dateCreated`，按 `Category` 自动分组嵌套。
- **批量原子入库与 SSRF 防护**：
  - `IPodcastStorage` / `PodcastStorage` 新增 `AddFeedsIfMissingAsync`，单次加锁去重并原子替换写入 `podcasts.json`。
  - `PlaylistViewModel.ImportOpmlFileAsync` 对解析出的每个 `RssUrl` 经过 `MediaUriPolicy.ValidateCustomRadioAsync` 校验，拦截回环/链路本地/云元数据地址；导入完成后自动切至首批新增播客并刷新单集，给出双语统计摘要（新增/重复/无效或受限条数）。
- **UI 交互（`PlaylistDrawer` Tab 6）**：
  - 在 `+` 内嵌面板内复用 `.sub-tab` 切换「订阅 RSS」与「OPML 导入/导出」，接通 Avalonia `OpenFilePickerAsync`（`*.opml, *.xml`）与 `SaveFilePickerAsync`（`tuanradio-podcasts.opml`）。

---

## 3. 无 Shownotes 音频转写速读设计

- **转码与识别服务（`AIRadio.Desktop` — `IPodcastTranscriptionService` / `PodcastTranscriptionService`）**：
  - **边界下载**：发起 HTTP GET 请求携带 `Range: bytes=0-3145727`（上限 3 MB、25 秒超时），禁用自动重定向并在每跳（最多 5 跳）复检 `MediaUriPolicy.ValidateCustomRadioAsync`。
  - **WAV 转码**：经 `NAudio`（`MediaFoundationReader` → `ToMono()` → `WdlResamplingSampleProvider` 重采样至 16kHz → `WaveFileWriter` 写入 16-bit PCM WAV，上限截取前 90 秒），临时文件在 `finally` 中必删。
  - **语种提示**：`ISttService` / `WhisperSttService` 新增带 `languageHint` 重载；`PodcastTranscriptionService.InferWhisperLanguage` 结合 RSS `<language>` 与节目标题字符特征推断 `"zh"` / `"en"` / 其他 ISO 语种代码，提升外语播客识别准确率。
- **速读调度与缓存（`DJService` + `MainWindowViewModel`）**：
  - `PodcastEpisode` 新增运行时状态 `IsTranscribing`（按钮展示「转写中...」）与 `TranscriptExcerpt`（同一单集会话内复用，避免重复拉流与转写）。
  - `IDJService` / `DJService` 新增 `GeneratePodcastDigestAsync(PodcastEpisode, string? transcriptExcerpt, CancellationToken)`：当无 Shownotes 且存在转写片段时，提示词与无 LLM 兜底脚本均明确以“音频开篇转写片段”为依据并做 `PromptHygiene.StripControlMarkers` 消毒。
  - `MainWindowViewModel.DigestPodcastEpisodeAsync` 与 `MainWindowViewModel.WebRemote` 统一打通该兜底链；若无可用音频链接或转写结果为空，则向聊天区反馈明确引导（`S_PodcastTranscribeFailed` / `S_PodcastNoShownotes`）。
