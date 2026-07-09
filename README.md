# AIRadio

AIRadio 是一个 Windows 桌面 AI 电台播放器：复古电台界面、AI DJ 对话和串场、Edge TTS 播报、多平台在线音乐搜索、节目单推荐、收藏歌单、星空/频谱视觉反馈。

## 技术栈

| 模块 | 技术 |
| --- | --- |
| 桌面框架 | .NET 8 / Avalonia 11.3.9 |
| MVVM | ReactiveUI + ReactiveUI.Fody |
| 音频播放 | LibVLCSharp + NAudio |
| AI DJ | OpenAI 兼容 LLM 接口，可配置 OpenAI / DeepSeek / OpenRouter / Claude / Ollama |
| TTS | Edge TTS WebSocket 服务 |
| 在线音乐 | NeteaseCloudMusicApi(Node.js) + 酷我/酷狗/咪咕 HTTP API + YouTube yt-dlp 兜底 |
| 语音识别 | Whisper 本地 ASR |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |

## 当前能力

- 本地音频和在线音源播放，支持播放/暂停/上一首/下一首/进度/音量。
- 歌单、收藏、搜索三类基础视图。
- AI DJ 聊天、点歌、串场、TTS 播报和 TTS 中断。
- 设置页可配置 LLM 提供商、API Key、Base URL、模型、回复语言、语音播报和说话混音方式。
- Radio Mode 自动续播：优先使用 `RecommendationService` 生成节目单，失败时退回 DJ 单首推荐。
- 推荐模型 v1：`ListeningContext`、`RecommendedTrack`、`RadioProgram`、`UserMusicFeedback`。
- 多平台音乐搜索：网易云优先，酷我/酷狗/咪咕并行 fallback，YouTube 作为最低优先级兜底。
- 收藏持久化到 `FavoriteIds`，并兼容旧的 `IsFavorite` 数据。
- 星光背景和频谱视觉反馈，目前频谱仍是视觉模拟数据。
- 通过 Node.js 启动网易云音乐 API，本地缺少 Node.js 时可下载便携版。

## 项目结构

```text
AIRadio.Desktop/
  Assets/                  应用图标
  Models/                  Track、ChatMessage、DJProfile、推荐模型等
  Services/                播放、AI DJ、LLM、TTS、推荐、搜索、ASR、环境服务
  ViewModels/              ReactiveUI ViewModel
  Views/                   Avalonia 视图和视觉反馈
  server/                  NeteaseCloudMusicApi Node.js 服务
AIRadio.Desktop.Tests/     xUnit 测试
```

## 构建运行

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet run --project AIRadio.Desktop\AIRadio.Desktop.csproj
```

## 验证

```bash
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal --no-restore /p:UseSharedCompilation=false
```

## 已知技术债

- 推荐闭环仍是当前会话级：反馈只影响本轮推荐，尚未持久化为长期用户画像。
- 节目单 UI 还需要把当前节目单、收藏、搜索进一步产品化区分。
- AI 控制协议已支持 JSON 控制块，但旧格式仍保留短期兼容。
- 多源音乐 API 响应格式不稳定，需要继续补充失败原因和 fallback UI。
- 频谱视觉目前是模拟数据，真实 FFT 评估放在后续阶段。
