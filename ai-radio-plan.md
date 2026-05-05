# AIRadio 项目实施计划

> **状态**：⚠️ 已部分实施，部分内容过时，请以 README.md 和实际代码为准

## 一、项目概述

| 项目 | 内容 |
|------|------|
| **项目名称** | AIRadio |
| **项目类型** | Windows 桌面应用 |
| **核心功能** | AI 数字人主播电台，用户导入歌单后 AI 自动串场、介绍歌曲、聊天互动、主播有表情动画 |

---

## 二、技术栈（已实现）

| 模块 | 技术方案 | 状态 |
|------|----------|------|
| **桌面框架** | Avalonia 11.3.2 + ReactiveUI | ✅ |
| **语言** | C# / .NET 8 | ✅ |
| **音频播放** | LibVLCSharp | ✅ |
| **虚拟形象** | WebView2 + Live2D Cubism Web SDK | ✅ |
| **LLM 对话** | MiniMax API (OpenAI 兼容) | ✅ |
| **TTS 语音** | MiniMax T2A API + NAudio | ✅ |
| **ASR** | Whisper (本地) | ✅ |
| **状态管理** | ReactiveUI + ReactiveUI.Fody | ✅ |
| **依赖注入** | Microsoft.Extensions.DI | ✅ |

---

## 三、目标平台

| 平台 | 支持状态 |
|------|----------|
| **Windows 10/11** | ✅ 目标平台 |

---

## 四、当前项目结构（已实现）

```
AIRadio/
├── AIRadio.Desktop/                # 主应用程序
│   ├── App.axaml / App.axaml.cs   # 应用入口 + DI 配置
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs  # 主窗口：Radio Mode 自动续播逻辑
│   │   ├── PlayerViewModel.cs      # 播放器状态
│   │   ├── PlaylistViewModel.cs    # 歌单管理 + 搜索 + 收藏
│   │   ├── ChatViewModel.cs        # 聊天 + TTS 中断
│   │   ├── SettingsViewModel.cs    # 设置面板
│   │   └── SpectrumViewModel.cs    # 频谱数据
│   ├── Views/
│   │   ├── MainWindow.axaml        # 主窗口 (Live2D + 星空粒子)
│   │   ├── PlayerView.axaml        # 播放器控制栏
│   │   ├── PlaylistView.axaml      # 歌单/收藏/搜索面板
│   │   ├── ChatView.axaml          # 聊天面板
│   │   ├── SettingsView.axaml      # 设置面板
│   │   ├── SpectrumView.axaml      # 频谱可视化
│   │   └── StarfieldView.axaml     # 星空粒子动画
│   ├── Services/
│   │   ├── AudioService.cs          # LibVLC + NAudio TTS + TTS 中断
│   │   ├── DJService.cs             # AI DJ + RecommendNextTrackAsync
│   │   ├── MinimaxService.cs        # MiniMax API (LLM + TTS)
│   │   ├── MultiSourceMusicService.cs  # 多音源聚合搜索
│   │   ├── NeteaseMusicService.cs   # 网易云音乐
│   │   ├── KuwoMusicService.cs      # 酷我音乐
│   │   ├── KugouMusicService.cs     # 酷狗音乐
│   │   ├── MiguMusicService.cs      # 咪咕音乐
│   │   ├── WhisperSttService.cs     # Whisper ASR
│   │   ├── Live2DStaticServer.cs    # HttpListener 静态服务 :18080
│   │   ├── MusicApiServer.cs        # Node.js 子进程 :37250
│   │   ├── EnvironmentManager.cs    # Node.js/WebView2 自动安装
│   │   └── WindowsSecureStorage.cs  # Windows Credential Manager
│   ├── Models/                     # Track, ChatMessage, DJProfile, CharacterProfile
│   ├── Assets/                     # airadio.ico, airadio.png
│   ├── server/                     # NeteaseCloudMusicApi (Node.js)
│   └── wwwroot/                    # Live2D Cubism SDK 静态资源
├── README.md                       # 最新项目文档
├── AGENTS.md                       # Session 历史记录
└── DESIGN.md                       # Spotify 设计系统参考（参考资料）
```

---

## 五、核心接口（当前实现）

### IAudioService

```csharp
public interface IAudioService
{
    bool IsPlaying { get; }
    TimeSpan CurrentPosition { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    Track? CurrentTrack { get; }
    IReadOnlyList<Track> Playlist { get; }
    bool IsShuffled { get; }
    string RepeatMode { get; }

    IObservable<float[]> SpectrumData { get; }
    IObservable<Track?> TrackChanged { get; }
    IObservable<PlaybackState> StateChanged { get; }
    IObservable<TimeSpan> PositionChanged { get; }
    IObservable<Track?> TrackEnded { get; }
    IObservable<bool> TtsStateChanged { get; }

    void LoadTracks(IEnumerable<Track> tracks);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void Next();
    void Previous();
    void Shuffle();
    void SetRepeatMode(string mode);  // "none" | "single" | "list" | "radio"
    void PlayAtIndex(int index);
    void AddTracks(IEnumerable<Track> tracks);
    void RemoveTrack(Track track);
    void ClearPlaylist();
    void SetNextCallback(Func<Task<Track?>>? callback);   // Radio Mode 推荐回调
    void SetPreviousCallback(Func<Task<Track?>>? callback);
    void SetUrlResolver(Func<string, Task<string?>> resolver);
    void SetSpeechMixMode(string mode);  // "duck" | "pause"
    void PlayTtsAudio(byte[] audioData);
    void StopTts();
}
```

### IDJService

```csharp
public interface IDJService
{
    void Initialize(DJProfile profile);
    Task<DJScript> GenerateTrackIntroductionAsync(Track current, Track next);
    Task<Track?> RecommendNextTrackAsync(Track? current);  // AI 推荐下一首歌
    Task<string> GenerateChatResponseAsync(string userMessage);
    Task<byte[]?> GenerateSpeechAsync(string text);
    string CurrentEmotion { get; }
    bool TtsEnabled { get; }
}
```

---

## 六、Radio Mode 自动续播流程

```
歌曲播放结束 (TrackEnded 事件)
    │
    ▼
MainWindowViewModel.HandleAutoRadioTrackEndedAsync()
    │
    ├─── 列表 ≤ 1 首歌 ─────────────────────────
    │    DJService.RecommendNextTrackAsync(current)
    │    → 搜索 → 获取 URL → AddExternalTrack
    │    → 生成串场文案 → TTS 播报 → 播放
    │
    └─── 列表 > 1 首歌 ─────────────────────────
         PickDiversifiedTrack(pool, current) // 避免同艺术家
         → 生成串场文案 → TTS 播报 → 播放
```

四种循环模式：
- **OFF**：关闭自动续播，播放完停止
- **single**：单曲循环
- **list**：列表循环
- **radio**（默认）：自动从 AI 推荐新歌续播

---

## 七、版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| v1.0 | 2026-05-03 | 项目启动，最初设计 |
| v1.1 | 2026-05-04 | UI 重构，DJ 播报，TTS，主题切换 |
| v1.2 | 2026-05-05 | Radio Mode，OFF 模式修复，TTS 中断，星空粒子，品牌统一为 AIRadio |

---

**文档版本**：v1.3
**创建日期**：2026-05-03
**最后更新**：2026-05-05