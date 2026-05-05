# AIRadio — Project Context & Memory

> This file contains session history and project notes for AIRadio development.

## Project

**AIRadio** — AI 数字人电台桌面播放器
Windows Desktop App (.NET 8 / Avalonia 11.3.2 / ReactiveUI)
Live2D 数字人主播 + MiniMax AI DJ + 多平台在线音乐

## Recent Work (2026-05-05)

- **Radio Mode**: Implemented auto-continue after track ends. `HandleAutoRadioTrackEndedAsync` manages sync between `AudioService._playlist` and `PlaylistViewModel.Tracks`.
- **TTS Interruption**: `StopTts()` added to `IAudioService`; `ChatViewModel` calls it before sending new messages.
- **Event Subscription Leak**: `_trackEndedSub` and `_trackChangedSub` now properly disposed in `MainWindowViewModel.Dispose()`.
- **OFF Mode Fix**: Fixed auto-continue bug when repeat mode was OFF.
- **Legacy Favorites**: `IsFavorite` field migration to `FavoriteIds` HashSet.
- **DJ RecommendNextTrackAsync**: `DJService.RecommendNextTrackAsync` parses LLM song name response and searches via `IMusicSearchService`.
- **Brand Unification**: All Claudio references replaced with AIRadio.
- **StarfieldView**: Canvas-based starfield animation driven by spectrum data.
- **App Icon**: `airadio.ico` and `airadio.png` added to Assets.

## Architecture Notes

- AudioService manages playback; PlaylistViewModel manages the displayed playlist
- In radio mode, both lists stay in sync: new recommended tracks go to both AudioService and PlaylistVM
- TTS uses NAudio with `_ttsCancelled` flag to handle interruption
- DJ callbacks (`SetNextCallback`, `SetPreviousCallback`) enable AudioService to request track recommendations

## Build

```bash
cd AIRadio.Desktop && dotnet build
```

## Issues Fixed

- OFF mode auto-continue
- TTS interruption during playback
- Event subscription leak on ViewModel swap
- Legacy playlist favorites data migration
- Self-comparison bug in auto-radio handler

<claude-mem-context>
# Memory Context

# [AIRadio] recent context, 2026-05-05 10:32pm GMT+8

Legend: 🎯session 🔴bugfix 🟣feature 🔄refactor ✅change 🔵discovery ⚖️decision 🚨security_alert 🔐security_note
Format: ID TIME TYPE TITLE
Fetch details: get_observations([IDs]) | Search: mem-search skill

Stats: 50 obs (11,223t read) | 2,832,081t work | 100% savings

### May 5, 2026
S290 Response to user question about updating README.md (May 5, 7:24 PM)
S291 Update all project documentation to match current code state (May 5, 7:33 PM)
258 7:50p ✅ AGENTS.md project notes updated to current state
S292 修复DJ推荐和Next按钮功能 (May 5, 7:50 PM)
S294 Fix DJ recommendation system - DJ should recommend NEW songs based on favorites context, and fix UI not updating when DJ switches tracks (May 5, 8:46 PM)
263 8:46p 🟣 收藏歌单上下文推荐功能
264 8:47p 🔵 Track模型缺少Tag属性
265 8:48p 🟣 Track模型添加Tag属性
266 8:49p 🔵 Track.cs Tag属性可能未保存
267 8:50p 🟣 收藏歌单推荐功能完整实现
268 " 🔴 HandleAutoRadioTrackEndedAsync使用SetTag方法
269 8:52p 🔴 Fixed DJService.cs compilation error - missing System.Linq
270 " 🔵 Encoding issue with Chinese characters in logs
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
S303 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:20 PM)
S302 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:21 PM)
S304 Fix bugs from MiniMax's code generation session - song detection, favorites persistence, online track URL refresh (May 5, 10:24 PM)
**Investigated**: ChatViewModel, PlaylistViewModel, AudioService, test files

**Learned**: AI DJ misinterpreted bare song titles; confidence-based detection fixes it. Favorites never serialized. Online tracks with empty FilePath fail silently on restart.

**Completed**: All three bugs fixed with tests passing (67/67); build passes on first attempt

**Next Steps**: Awaiting user approval for escalated sandbox permissions for Avalonia build telemetry


Access 2832k tokens of past work via get_observations([IDs]) or mem-search skill.
</claude-mem-context>