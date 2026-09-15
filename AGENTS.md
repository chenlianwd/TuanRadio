# TuanRadio — Project Context & Memory

## Project

TuanRadio 是一个 Windows 桌面 AI 电台播放器。

技术栈：.NET 10、Avalonia 11.3.9、ReactiveUI、LibVLCSharp、NAudio、LLM（OpenAI/Anthropic 兼容/本地三格式）、Edge TTS、Whisper、多平台在线音乐搜索。

产品定位：复古 AI 电台、AI DJ、节目单推荐、TTS 串场、音乐搜索、星空/频谱视觉反馈。

## Current Direction

- 不再保留旧 Web 静态资源、模型资源或相关运行时依赖。
- AI DJ 角色保留为名称、声音、人设提示和轻量头像动效。
- 长期用户画像已落地并双链路消费（推荐 + DJ 聊天），本地文件级，不做云端数据库。
- 天气、日历暂不进入第一轮开发；歌词显示已作为第二轮特性交付（ClockStage 歌词模式）。

## Recent Work

- 视图重构 + 统一状态机落地：MainWindow 拆为 TitleBar/ClockStage/PlayerDeck/ChatArea/PlaylistDrawer/StatusBar/CharacterPicker UserControl，`RadioState` 状态机驱动 StatusBar。
- Theme 全量 token 化：全部颜色走 `Themes/Colors.axaml`，Light/Dark 切换可用。
- 真实 FFT 频谱：WasapiLoopbackCapture + FFT 替换模拟数据，驱动频谱与星空。
- SongStory：STORY 按钮触发现曲 LLM 讲述。
- 节目单视图（库抽屉第 4 个 tab）显示推荐标签，推荐理由经 DjOpening 走 DJ 气泡。
- 搜索状态逐源透传（成功/超时/失败），设置页带连接诊断。
- 升级 .NET 10，Tmds.DBus.Protocol 固定 0.21.3 修复漏洞通告。
- 业务断点修复 A 包：上一首恢复标准语义（>3s 回开头/列表上一首）；change_mood 指令与 CALM/FIRE 按钮接通会话级氛围偏好；SongStory 补 TTS 播报。
- 播放与退出稳定性加固：LibVLC/NAudio 生命周期串行化，多音源硬超时与元数据跨源回退，应用关闭取消推荐/LLM/TTS/STT/搜索，频谱 dB 映射与缓冲区复用。
- 全量代码审查集中修复：麦克风按钮崩溃、进度条拖动失效、YouTube 源解析、Anthropic 历史裁剪、播放列表跨线程锁、EdgeTTS 无声重连、逐源业务失败透传等高危与中危缺陷。
- 简洁播放模式：CompactPlayer 两行紧凑卡（播放控制/进度/收藏/迷你频谱），标题栏收缩、拖动/双击/Esc 还原、模式记忆与置顶设置。
- 酷狗 20028 风控滑块验证：server-kugou 新增会话桥（verify/bridge/*，token 不进 URL）+ verify_auto.html 自动验证页 + WEBGL 指纹进程级稳定化；C# 端 KugouVerificationService 负责挑战检测（四形状分类）、自动弹浏览器验证（10 分钟冷却）与恢复轮询，设置页提供手动「滑块验证」入口。
- 修复按住麦克风说话整体失效：Avalonia Button 类处理器会把左键按下/释放标记为 Handled，XAML 属性挂载收不到事件；ChatArea 改为代码内 AddHandler(handledEventsToo:true) 订阅，测试工程引入 Avalonia.Headless.XUnit 并新增 ChatAreaMicButtonTests 回归测试。
- 修复推荐与播放脱节及按住被拒无反馈：节目单搜索词生成改走 LLMService 新增的无人设 ChatRawAsync（原 ChatAsync 固定注入 DJ 人设，模型回整段台词、开场白碎片被当搜索词搜出无关歌曲直接播放），RecommendationService 增加 SanitizeSearchQueries 台词净化兜底；BeginHoldToTalk 改为返回是否真正开始录音，AI 回复/识别中被拒时不再出现按压视觉（原静默拒绝被用户当成第二次按住失效）。
- 语音识别 LLM 纠错：Whisper base 中文同音错误率高（"来点轻音乐"→"拿手青音樂"），识别文本进入点歌/搜索链路前先经 DJService.CorrectTranscriptionAsync（无人设 ChatRawAsync + 8 秒短超时）归一化，失败/超时回退原文不阻塞语音流程。
- 歌词模式：LyricService 经本地代理双源取词（网易 /lyric 单步、酷狗 /search/lyric 两步 + 关键词兜底 + 时长过滤）、LrcParser 解析、LyricsViewModel 按 PositionChanged 逐行滚动（generation+SourceId 双卫兵防旧词晚到）；ClockStage 时钟/歌词图层互斥，BrandHeader 按钮切换并记忆 show_lyrics_in_stage。
- 长期收听画像：ListeningProfileService 持久化收听事件（%APPDATA%\AIRadio\listener-profile.json，事件为唯一事实、统计现算），歌手亲和度 30 天半衰期、Dislike 黑名单 180 天音乐身份匹配、LLM 口味摘要（水位/老化/语言触发、失败退避、Reset 代次隔离）；跳过信号三重前置判定（前曲播放态+新曲身份不同+进度样本绑定本曲，TrackChanged 有 8 处触发点不能直接当切歌）；推荐搜索词注入画像段与探索要求（冷启动门槛 ≥30 事件且 ≥3 歌手），黑名单拼入排除集（不走冷启动门槛）；设置页学习开关（listener_profile_enabled）+ 二次确认清除。
- DJ 聊天画像注入：GenerateChatResponseAsync 在历史快照副本的人设 system 尾部拼画像段（LLMService.BuildMessages 恒前置内置小音 system，双 system 合并是现状常态，注入不新增第三条；Take(1) 恒保首条 system 故长对话裁剪不丢）；口味段（digest+歌手+氛围）走冷启动门槛、黑名单避雷不走门槛；文案跟随 DJ 人设语言；拼接只在快照副本上，持久历史不落画像、角色切换 Initialize 重建无残留。
- 音源体验增强：播放回退候选在身份匹配每遍内按时长接近度择优（ScoreFallbackCandidate：目标无时长取首个、候选缺时长记 -1，两遍次序与外层"高优先级源命中即停"不变——跨源不比时长是预算效率的有意取舍）；MusicSourceFailureKind 结构化分类（NotSignedIn/AuthExpired/RiskControl(20028)/ApiBroken/Unknown）由 MusicSourceBusinessException.Kind 携带，七处抛出点标注，经 SourceSearchStatus.FailureKind 透传（注意 AddSearchReport 脱敏重建必须保字段），搜索状态行按类型渲染；AuthExpired 文案须兼顾酷狗登录态失效与网易代理未就绪两类场景，风控文案注明自动验证仅播放路径且约 10 分钟冷却。
- 产品化清理五件套：设置页音源逐源连接诊断（MultiSourceMusicService.DiagnoseAsync 独立 AsyncLocal 作用域报告不污染搜索状态，复用 FormatSourceStatus 分类渲染）；AI 控制协议旧文本尾标（【play:…】/【next】）从 ParseDjResponse 与两处 StripControlTags 移除，协议只认 JSON 控制块；弹层硬编码标题 SETTINGS/LIBRARY 迁 S_Settings/S_Library（VOL/LIVE 为复古电台设计元素有意保留英文）；Light 主题 WCAG 审计修正三处超标（提示字 #6A6478、LIVE 徽标 #146C4F、StatePlaying #0C6E4E，均 ≥4.5:1）；Node.js/yt-dlp 供应链校验确认已实现（EnvironmentManager 下载即 fail-closed 校验、YtdlpManager 固定版本+SHA256），技术债清单同步。

## Architecture Notes

- `AudioService` 管理播放和 TTS。
- `PlaylistViewModel` 管理展示歌单、收藏和搜索结果。
- `RecommendationService` 负责节目单候选生成、去重、可播状态和会话反馈。
- `ListeningProfileService` 负责长期收听画像：事件采集/统计/持久化与 LLM 口味摘要，供 `RecommendationService` 跨会话消费。
- `DJService` 负责 AI 对话、串场、TTS 文本和单首推荐 fallback。
- `MainWindowViewModel` 组合各模块，并在 Radio Mode 中触发自动续播。

## Build

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
```

> Git Bash 下 `/p:` 参数必须加引号，否则 MSBuild 解析报 MSB1008。

## Notes For Future Agents

- 保持变更小而可验证，优先沿用现有 ViewModel 和服务边界。
- 不要把推荐逻辑继续塞进 `MainWindowViewModel`，应尽量放在 `RecommendationService`。
- 每个阶段完成后先跑 build/test，再继续下一阶段。


<claude-mem-context>
# Memory Context

# [AIRadio] recent context, 2026-05-17 9:53pm GMT+8

Legend: 🎯session 🔴bugfix 🟣feature 🔄refactor ✅change 🔵discovery ⚖️decision 🚨security_alert 🔐security_note
Format: ID TIME TYPE TITLE
Fetch details: get_observations([IDs]) | Search: mem-search skill

Stats: 50 obs (11,988t read) | 2,739,246t work | 100% savings

### May 5, 2026
S294 Fix DJ recommendation system - DJ should recommend NEW songs based on favorites context, and fix UI not updating when DJ switches tracks (May 5, 8:46 PM)
269 8:52p 🔴 Fixed DJService.cs compilation error - missing System.Linq
271 8:55p 🔵 System.Linq edit may not have persisted - file still shows old content
272 " ✅ 11 files modified - DJ recommendation and favorites context
273 9:01p 🟣 Committed: DJ recommendations based on favorites context
274 " 🔴 Fixed duplicate track recommendation in radio mode
275 " 🔴 TTS interruption and event subscription leak fixes
276 " ✅ Pushed commit 1e3a13c to GitHub
277 " 🔵 DJ command parsing works but UI may not update track info
S293 Fix DJ recommendation system - DJ should recommend NEW songs based on favorites context, not recommend songs from the favorites list directly (May 5, 9:01 PM)
S295 Fix DJ track switch UI not updating - PlayTrack missing _currentIndex assignment (May 5, 9:02 PM)
278 9:05p 🔵 TrackChanged event is subscribed in MainWindowViewModel
279 9:06p 🔵 PlayerView bindings map to PlayerViewModel properties
280 " 🔵 PlayerViewModel updates track display via TrackChanged subscription
281 " 🔵 DJ play command calls PlayAtIndex directly
282 " 🔵 DJ play command flow: AddTracks then PlayAtIndex
283 " 🔵 PlayTrack calls NotifyTrackChanged() after playing
284 " 🔵 TrackTitle/TrackArtist only updated via TrackChanged subscription
285 9:07p 🔵 BUG FOUND: _currentIndex not set in PlayTrack/PlayAtIndex
286 9:10p 🔴 BUG: PlayTrack doesn't set _currentIndex before NotifyTrackChanged
287 9:15p 🔴 ROOT CAUSE: PlayTrack never sets _currentIndex field
288 9:17p 🔴 DJ track switch UI not updating - _currentIndex not set in PlayTrack
290 9:20p 🔵 AddExternalTrack delegates to AudioService.AddTracks - not used by DJ
291 9:28p 🔵 ChatViewModel receives AddExternalTrack callback - should sync playlist
289 9:29p 🔴 PlayTrack missing _currentIndex assignment - confirmed fix location
292 9:33p 🔵 NotifyTrackChanged emits CurrentTrack which depends on _currentIndex
293 9:35p 🔴 FIX APPLIED: PlayTrack now sets _currentIndex before NotifyTrackChanged
294 9:36p 🔴 Fix verified - build succeeds and all 60 tests pass
S301 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 9:40 PM)
307 9:45p 🔴 TTS Session Race Condition Fixed with HashSet-based Tracking
308 " 🔴 Radio Next/Previous Now Use FindTrackIndex to Prevent Duplicate Tracks
309 " 🔴 MainWindowViewModel Subscription Cleanup Implemented
310 " 🔴 ChatViewModel Implements IDisposable for TTS Subscription Cleanup
311 " 🔴 MainWindow OnClosed Override Ensures Proper Resource Disposal
312 " 🟣 Regression Tests Added for TTS State and Radio Mode Playlist Sync
304 9:51p 🔵 AIRadio project build and tests pass after code review
318 9:54p 🔵 Complete Code Review and Fix Session Concluded Successfully
319 9:55p 🔴 AI DJ Chat Interpretation Issue: Song Names Misinterpreted as Insults
320 " 🔴 Favorites/Playlist Persistence Not Working
321 9:56p 🔵 ChatViewModel DJ Command Flow: ParseResponse Uses Regex Pattern
322 " 🔵 PlaylistViewModel Has LoadAsync and SaveAsync with JSON Persistence
313 9:57p ✅ AIRadio comprehensive code review and fixes completed
314 9:58p 🔴 AIRadio comprehensive bug fixes completed - 0 warnings, 63 tests passing
315 " ✅ README and Plan Documents Clarify Live2D Status as Retained Resources
316 " 🔴 Build Warnings Resolved for Nullable and Platform Compatibility
317 " 🔵 Full Test Suite Passes: 63 Tests Including 3 New Regression Tests
327 10:10p ✅ 修复提交完成，AGENTS.md 待处理
S303 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:20 PM)
328 10:21p ✅ Committed AI DJ song detection and favorites persistence fixes
331 " 🔴 App crash on playback caused by test pollution of real playlist.json
332 " 🔴 ChatViewModel duplicate track prevention via TrackAdded callback coordination
S307 Fix AIRadio bugs: app crash, test pollution, duplicate track addition (May 5, 10:21 PM)
S302 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:21 PM)
S304 Fix bugs from MiniMax's code generation session - song detection, favorites persistence, online track URL refresh (May 5, 10:23 PM)
S305 Fix AI DJ song detection confidence, favorites persistence, online track URL refresh; commit all changes (May 5, 10:24 PM)
329 10:25p ✅ AGENTS.md 已单独暂存待提交
330 10:33p ✅ 全部修复提交完成，工作区干净
S306 Fix multiple AIRadio bugs: app crash on playback, test pollution of real playlist, duplicate track addition (May 5, 10:39 PM)
333 10:48p 🔴 Auto-radio interrupting manual playback - track switching bug fixed
### May 16, 2026
335 9:45a ✅ Pulled latest code from repository

Access 2739k tokens of past work via get_observations([IDs]) or mem-search skill.
</claude-mem-context>
