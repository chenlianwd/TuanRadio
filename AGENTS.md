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

# [AIRadio] recent context, 2026-05-05 10:39pm GMT+8

Legend: 🎯session 🔴bugfix 🟣feature 🔄refactor ✅change 🔵discovery ⚖️decision 🚨security_alert 🔐security_note
Format: ID TIME TYPE TITLE
Fetch details: get_observations([IDs]) | Search: mem-search skill

Stats: 50 obs (11,607t read) | 2,859,553t work | 100% savings

### May 5, 2026
S292 修复DJ推荐和Next按钮功能 (May 5, 7:50 PM)
S294 Fix DJ recommendation system - DJ should recommend NEW songs based on favorites context, and fix UI not updating when DJ switches tracks (May 5, 8:46 PM)
268 8:50p 🔴 HandleAutoRadioTrackEndedAsync使用SetTag方法
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
327 10:10p ✅ 修复提交完成，AGENTS.md 待处理
S303 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:20 PM)
328 10:21p ✅ Committed AI DJ song detection and favorites persistence fixes
331 " 🔴 App crash on playback caused by test pollution of real playlist.json
332 " 🔴 ChatViewModel duplicate track prevention via TrackAdded callback coordination
S304 Fix bugs from MiniMax's code generation session - song detection, favorites persistence, online track URL refresh (May 5, 10:21 PM)
S302 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:21 PM)
S305 Fix AI DJ song detection confidence, favorites persistence, online track URL refresh; commit all changes (May 5, 10:24 PM)
329 10:25p ✅ AGENTS.md 已单独暂存待提交
330 10:33p ✅ 全部修复提交完成，工作区干净
S306 Fix multiple AIRadio bugs: app crash on playback, test pollution of real playlist, duplicate track addition (May 5, 10:39 PM)
**Investigated**: App crash logs showing playback error on "Current" track, JSON parsing failures in Kugou/Migu/Netease services, playlist.json content with fake test tracks, MainWindowViewModelTests and ChatViewModel code paths

**Learned**: - MainWindowViewModelTests used real AudioService with default AppData playlist path, polluting user's playlist.json with test:current/test:recommended fake tracks
    - App loaded these fake tracks with placeholder URLs (http://example.com/*) causing playback failures and crash (exit -1)
    - ChatViewModel was calling both _audioService.AddTracks AND _trackAdded callback, causing double insertion
    - Avalonia build blocked by telemetry write to %LocalAppData% despite --no-restore; workaround uses isolated OutDir
    - VBCSCompiler locks default obj directory when test/build run in parallel

**Completed**: - playlistFile parameter added to MainWindowViewModel constructor and PlaylistViewModel
    - MainWindowViewModelTests and PlaylistViewModelTests now use temp GUID-based playlist files
    - ChatViewModel refactored with FindAudioTrackIndex to prevent duplicate track addition
    - User's real playlist.json backed up and cleaned (test:current/test:recommended removed, netease:167827 素颜 preserved)
    - reviewbin/reviewtestbin/obj_review directories cleaned from workspace
    - 68 tests passing
    - Commit ba4b263 already pushed

**Next Steps**: Cleaning user's real playlist.json (pending escalated permission approval); then commit remaining fixes and document cleanup


Access 2860k tokens of past work via get_observations([IDs]) or mem-search skill.
</claude-mem-context>