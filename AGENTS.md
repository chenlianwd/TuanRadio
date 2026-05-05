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