# AIRadio — Project Context & Memory

## Project

AIRadio 是一个 Windows 桌面 AI 电台播放器。

技术栈：.NET 8、Avalonia 11.3.2、ReactiveUI、LibVLCSharp、NAudio、MiniMax、Whisper、多平台在线音乐搜索。

产品定位：复古 AI 电台、AI DJ、节目单推荐、TTS 串场、音乐搜索、星空/频谱视觉反馈。

## Current Direction

- 不再保留旧 Web 静态资源、模型资源或相关运行时依赖。
- AI DJ 角色保留为名称、声音、人设提示和轻量头像动效。
- 推荐闭环先做当前会话级，不做长期用户画像数据库。
- 天气、日历、歌词和真实 FFT 暂不进入第一轮开发。

## Recent Work

- Radio Mode 自动续播和播放列表同步。
- TTS 中断：发送新消息前停止当前 TTS。
- ViewModel 事件订阅释放，降低泄漏风险。
- OFF 模式不再自动续播。
- 收藏数据迁移到 `FavoriteIds`。
- DJ 推荐会根据当前曲目、收藏和排除列表寻找新歌。
- AI 控制协议升级为 JSON 控制块，并保留旧格式兼容。
- 推荐模型和 `RecommendationService` v1 已开始落地。
- 星空视觉反馈由频谱数据驱动，目前频谱仍为模拟视觉数据。

## Architecture Notes

- `AudioService` 管理播放和 TTS。
- `PlaylistViewModel` 管理展示歌单、收藏和搜索结果。
- `RecommendationService` 负责节目单候选生成、去重、可播状态和会话反馈。
- `DJService` 负责 AI 对话、串场、TTS 文本和单首推荐 fallback。
- `MainWindowViewModel` 组合各模块，并在 Radio Mode 中触发自动续播。

## Build

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal --no-restore /p:UseSharedCompilation=false
```

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
