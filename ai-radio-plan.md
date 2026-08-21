# TuanRadio 当前实施计划

## 产品定位

TuanRadio 的方向已经收敛为“复古 AI 电台”：

- AI DJ 角色：名称、声音、人设提示、轻量头像动画。
- 音乐能力：本地播放、多平台搜索、收藏、在线播放 URL 刷新。
- 电台能力：根据用户意图、当前歌曲、收藏和排除列表生成 3-5 首节目单。
- 视觉反馈：星空、频谱、头像状态动效、统一状态文本。

## 已完成

- Avalonia + ReactiveUI 桌面框架。
- LibVLCSharp 播放核心和 NAudio TTS 播放。
- LLM 服务抽象：`ILLMService` + OpenAI 兼容 / Anthropic 兼容 / 本地模型三种格式的 `LLMService`。
- Edge TTS 服务抽象：`ITtsService` + `EdgeTtsService`。
- Whisper 本地语音识别入口。
- 多音源搜索：网易云、酷我、酷狗、咪咕、YouTube/yt-dlp 兜底。
- Radio Mode 自动续播和播放列表同步。
- 收藏持久化与旧数据迁移到 `FavoriteIds`。
- DJ 角色配置、声音覆盖、人格提示词覆盖和设置保存。
- 推荐服务 v1 模型与服务骨架。
- JSON DJ 控制块解析，兼容旧文本尾标。
- 启动欢迎语、启动推荐、TTS 中断和 DJ 视觉 cue。
- 视图重构 + 统一状态机：MainWindow 拆 7 个 UserControl，`RadioState` 状态机驱动 StatusBar。
- Theme 全量 token 化（Light/Dark 双套，`Themes/Colors.axaml`）。
- 真实 FFT 频谱：WasapiLoopbackCapture + FFT 替换模拟视觉数据。
- SongStory v1：STORY 按钮触发现曲 3-5 句 LLM 讲述 + TTS。
- 播放稳定性加固：在线 URL 过期刷新、异常结束重试、重复恢复抑制、LibVLC 操作串行化。
- 音源韧性加固：内置音源取消传播、逐源硬超时、首选源可播性检查、按歌曲元数据跨源 URL 回退。
- 退出与资源治理：应用级取消链路覆盖推荐/LLM/TTS/STT/搜索，LibVLC/WASAPI/NAudio 后台续清理。
- 频谱稳定性加固：真实 FFT 幅度改为 dB 映射、无回环数据视觉兜底、复用 FFT 工作缓冲区。
- 简洁播放模式：CompactPlayer 两行紧凑卡，窗口收缩还原、模式记忆与可选置顶。

## 当前流程

### 启动流程

1. `App` 初始化 DI、日志和 `MusicApiServer`。
2. 创建 `MainWindowViewModel` 并加载设置、歌单。
3. 初始化当前 DJ 角色和 LLM/TTS 配置。
4. 播放欢迎语。
5. 如果歌单非空，生成一次启动推荐。

### 点歌流程

1. `ChatViewModel` 识别明确点歌输入或 JSON 控制块。
2. `MultiSourceMusicService` 搜索在线音源。
3. 获取首选源播放 URL；失效时按歌名/歌手到其他源重新匹配。
4. 加入 `PlaylistViewModel` 和 `AudioService`。
5. 播放目标曲目，并避免重复加入同一首歌。

### Radio Mode 续播流程

1. `AudioService.TrackEnded` 发出当前曲目结束事件。
2. `MainWindowViewModel` 接管自动续播。
3. 优先通过 `RecommendationService` 获取当前节目单的下一首或生成新节目单。
4. 如果节目单推荐失败，退回 `DJService.RecommendNextTrackAsync` 单首推荐。
5. DJ 生成串场文本，TTS 播报后切到下一首。

### 关闭流程

1. `App` 取消应用生命周期令牌，停止初始化、推荐、LLM、TTS、STT、搜索和 yt-dlp 请求。
2. `MainWindowViewModel` 只释放自身订阅和子 ViewModel，不在 UI 线程同步停止原生音频设备。
3. DI 容器统一释放 `AudioService`、`EdgeTtsService` 和 `WhisperSttService`。
4. 原生回调在限定时间内未退出时，窗口继续关闭，清理任务在后台等待回调恢复后完成释放。

## 当前开发阶段

### P0 清理：基本完成

- 旧静态资源、模型资源和相关运行时依赖已不再作为主方向。
- 业务代码已迁到 LLM + Edge TTS 的服务边界。
- README 和实施计划已同步到当前产品定位。
- 未接入 DI 的旧 MiniMax 运行时代码与测试已删除，后续只保留当前 LLM + Edge TTS 边界。

### P1 推荐闭环：v1 已落地

- `RecommendationService` 根据用户输入、当前歌曲、收藏和排除列表生成节目单。
- 搜索结果去重、获取播放 URL、标记可播放状态。
- 用户反馈动作影响当前会话推荐。
- Radio Mode 优先消耗当前节目单，节目单耗尽后再生成新节目单。

### P2 产品化：进行中

**已完成（播放、音源和生命周期稳定性，2026-08-19~20）：**
- 播放恢复统一以请求代次去重；提前结束依次刷新当前源、按元数据切换替代源，仍失败才进入自动续播。
- LibVLC 播放器操作、音量更新和原生回调释放建立串行边界，避免 UI 卡死及并发 Dispose。
- 多音源加入 3-5 秒分级硬超时、调用方取消和首选源可播性验证。
- 播放 URL 失效或网易云返回明确试听流时使用歌曲元数据跨源匹配，并同步实际生效的音源 ID。
- Edge TTS、Whisper、MusicApiServer、yt-dlp 和 ViewModel 后台任务纳入关闭取消链路。
- 频谱改用 dB 幅度映射并复用 FFT 缓冲区，修复频谱顶满、停滞和高频分配。
- PlayerDeck 限制边界并调整右侧自适应列，修复音量滑块溢出。

**已完成（全量审查修复 + 简洁播放模式，2026-08-20）：**
- 全量代码审查集中修复：麦克风按钮 InvalidCastException、进度条拖动与 seek 接线、YouTube 源 duration 解析与兜底分层、Anthropic 历史裁剪与 system 合并、本地 API 端口身份校验、播放列表跨线程安全、续播回调硬超时、EdgeTTS 无声重连、四源逐条容错与业务失败状态透传、RetryPolicy 超时覆盖、推荐已播记忆与双管线串行化。
- 简洁播放模式：标题栏收缩为两行紧凑卡（状态点/曲目/收藏/窗口控制 + 播放控制/进度/迷你频谱），进入时关闭浮层、快照还原窗口边界（含最大化），模式记忆与置顶设置写入 settings.json；无关设置保存不再触发角色重初始化清空聊天历史。

**已完成（视图重构 + 状态机，2026-08）：**
- 统一状态机 `RadioState`（Idle/Curating/Searching/Speaking/Playing/Error）派生 `MainWindowViewModel.CurrentState`，StatusBar 绑定显示。
- MainWindow 拆为 UserControl：TitleBar / ClockStage / PlayerDeck / ChatArea / PlaylistDrawer / CharacterPicker / StatusBar（MainWindow.axaml 668→163 行，.cs 467→113 行）。
- Converter 合并到 `Converters/`（InverseBool / MessageAlign / RadioStateTo*）。
- Theme ThemeDictionaries + RequestedThemeVariant PoC 落地（`Themes/Colors.axaml`）。
- 时钟迁 VM（`Now` 属性），Starfield 自订阅频谱。

**已全部落地（原待做，2026-08-14）：**
- 节目单 UI 区分当前节目单、收藏、搜索（子项目 2）→ 库抽屉 4 tab。
- 推荐理由放在 DJ 气泡，卡片只保留短标签（子项目 2）→ 卡片只留 Tags，理由经 DjOpening 入气泡。
- 外部音源失败原因和 fallback UI 继续细化（子项目 5）→ 逐源成功/超时/失败透传。
- 设置页连接诊断完善（子项目 5）→ TestConnection 覆盖空值/成功/失败+RecoveryHint。
- Theme 全量 token 化 → spec §9 零残留。

### 子项目 1 收尾（2026-08-14）

- Theme 全量 token 化达成 spec §9 零残留：MainWindow.axaml / SettingsView.axaml / SpectrumView.axaml / ChatArea.axaml.cs 全部迁移到 `Themes/Colors.axaml` 的 `C_<HEX>` token；Light/Dark 双套字典均已配置独立配色。
- 孤儿旧 View（ChatView/PlayerView/PlaylistView）删除，其中活跃引用的 `TabVisibleConverter` / `FavoriteIconConverter` 迁入 `Converters/` 目录。
- 子项目 2「节目单卡片只留短标签」落地：移除卡片 Reason，节目单整体推荐理由通过 `DjOpening` 已进 DJ 气泡；修复 PlaylistDrawer 标签背景 `C_2221ED76}0` 拼写 typo。
- 子项目 5「音源 fallback UI + 设置连接诊断」确认落地：`BuildSearchStatusMessage` 透传各源成功/超时/失败；`TestConnectionAsync` 覆盖空 Key/模型名/成功/失败+RecoveryHint。

### P3 增强：后续评估

- 长期用户画像和跨会话推荐偏好。
- 外部音源失败原因的用户可读分类，以及同一歌曲多候选的质量评分。
- Node.js/yt-dlp 下载文件的官方校验和验证与版本固定策略。
- 连续播放、TTS 插播、快速切歌、睡眠唤醒和关闭窗口的自动化耐久测试。
- 持续根据视觉验收微调 Light/Dark 模式的对比度与可读性。
- 天气、日历、歌词暂不进入第一轮开发。

## 构建与测试

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
```
