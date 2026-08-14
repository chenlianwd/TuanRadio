# AIRadio 当前实施计划

## 产品定位

AIRadio 的方向已经收敛为“复古 AI 电台”：

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
3. 获取播放 URL。
4. 加入 `PlaylistViewModel` 和 `AudioService`。
5. 播放目标曲目，并避免重复加入同一首歌。

### Radio Mode 续播流程

1. `AudioService.TrackEnded` 发出当前曲目结束事件。
2. `MainWindowViewModel` 接管自动续播。
3. 优先通过 `RecommendationService` 获取当前节目单的下一首或生成新节目单。
4. 如果节目单推荐失败，退回 `DJService.RecommendNextTrackAsync` 单首推荐。
5. DJ 生成串场文本，TTS 播报后切到下一首。

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

**已完成（视图重构 + 状态机，2026-08）：**
- 统一状态机 `RadioState`（Idle/Curating/Searching/Speaking/Playing/Error）派生 `MainWindowViewModel.CurrentState`，StatusBar 绑定显示。
- MainWindow 拆为 UserControl：TitleBar / ClockStage / PlayerDeck / ChatArea / PlaylistDrawer / CharacterPicker / StatusBar（MainWindow.axaml 668→163 行，.cs 467→113 行）。
- Converter 合并到 `Converters/`（InverseBool / MessageAlign / RadioStateTo*）。
- Theme ThemeDictionaries + RequestedThemeVariant PoC 落地（`Themes/Colors.axaml`）。
- 时钟迁 VM（`Now` 属性），Starfield 自订阅频谱。

**待做：**
- 节目单 UI 区分当前节目单、收藏、搜索（子项目 2）。
- 推荐理由放在 DJ 气泡，卡片只保留短标签（子项目 2）。
- 外部音源失败原因和 fallback UI 继续细化（子项目 5）。
- 设置页连接诊断完善（子项目 5）。
- Theme 全量 token 化（融入拆分进行中，Light 模式部分控件待统一）、MainWindow.Theme.cs 命令式 theming 待随全量 token 退场。

### 子项目 1 收尾（2026-08-14）

- Theme 全量 token 化达成 spec §9 零残留：MainWindow.axaml / SettingsView.axaml / SpectrumView.axaml / ChatArea.axaml.cs 全部迁移到 `Themes/Colors.xaml` 的 `C_<HEX>` token；Light/Dark 双套字典已就位（Light 暂占位同值）。
- 孤儿旧 View（ChatView/PlayerView/PlaylistView）删除，其中活跃引用的 `TabVisibleConverter` / `FavoriteIconConverter` 迁入 `Converters/` 目录。
- 子项目 2「节目单卡片只留短标签」落地：移除卡片 Reason，节目单整体推荐理由通过 `DjOpening` 已进 DJ 气泡；修复 PlaylistDrawer 标签背景 `C_2221ED76}0` 拼写 typo。
- 子项目 5「音源 fallback UI + 设置连接诊断」确认落地：`BuildSearchStatusMessage` 透传各源成功/超时/失败；`TestConnectionAsync` 覆盖空 Key/模型名/成功/失败+RecoveryHint。

### P3 增强：后续评估

- Song Story v1：单曲 3-5 句 DJ 讲述脚本。
- 真实 FFT 可行性评估。
- 长期用户画像和跨会话推荐偏好。
- 天气、日历、歌词暂不进入第一轮开发。

## 构建与测试

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal --no-restore
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal --no-restore /p:UseSharedCompilation=false
```
