# AIRadio

AIRadio 是一个在线优先的 Windows 桌面 AI 电台播放器：复古电台界面、AI DJ 对话和串场、Edge TTS 播报、多平台在线音乐搜索、节目单推荐、收藏歌单、星空/频谱视觉反馈。本地文件导入仅作为兼容能力，不是产品主流程。

## 技术栈

| 模块 | 技术 |
| --- | --- |
| 桌面框架 | .NET 10 / Avalonia 11.3.9 |
| MVVM | ReactiveUI + ReactiveUI.Fody |
| 音频播放 | LibVLCSharp + NAudio |
| AI DJ | 支持 OpenAI 兼容、Anthropic 兼容和本地模型三种接口格式 |
| TTS | Edge TTS WebSocket 服务 |
| 在线音乐 | NeteaseCloudMusicApi(Node.js) + 酷我/酷狗/咪咕 HTTP API + YouTube yt-dlp 兜底 |
| 语音识别 | Whisper 本地 ASR |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |

## 当前能力

- 在线音源播放为主，支持播放/暂停/上一首/下一首/进度/音量；保留本地文件导入兼容。
- 库抽屉四类视图：歌单、收藏、搜索、当前节目单（含推荐标签）。
- AI DJ 聊天、点歌、串场、TTS 播报和 TTS 中断，DJ 角色可切换（名称/声音/人设）。
- SongStory：STORY 按钮触发现曲 3-5 句 DJ 讲述，走 LLM 生成 + TTS 播报。
- 设置页可配置 LLM 提供商、API Key、Base URL、模型、回复语言、语音播报和说话混音方式，带连接测试与失败原因提示。
- Radio Mode 自动续播：优先使用 `RecommendationService` 生成节目单，失败时退回 DJ 单首推荐。
- 推荐模型 v1：`ListeningContext`、`RecommendedTrack`、`RadioProgram`、`UserMusicFeedback`；会话级反馈中 NOPE 本轮排除、CALM/FIRE 切换氛围偏好，聊天的 change_mood 指令同样生效。
- 多平台音乐搜索：网易云优先，酷我/酷狗/咪咕并行 fallback，YouTube 作为最低优先级兜底；每个源有独立硬超时，搜索状态逐源显示成功/超时/失败。
- 播放 URL 失效时会依据歌名和歌手跨源重新匹配，不会把一个平台的歌曲 ID 直接交给另一个平台。
- 真实 FFT 频谱：WasapiLoopbackCapture 采集系统输出 + 1024 点 FFT 转 32 频段；无有效回环数据时启用播放态视觉兜底，并限制幅度与刷新分配。
- 统一电台状态机 `RadioState`（Idle/Curating/Searching/Speaking/Playing/Error），StatusBar 实时显示。
- Light/Dark 双主题，全部颜色走 `Themes/Colors.axaml` token。
- 收藏持久化到 `FavoriteIds`，并兼容旧的 `IsFavorite` 数据。
- 通过 Node.js 启动网易云音乐 API，本地缺少 Node.js 时可下载便携版。

## 稳定性设计

- URL 刷新和音量排空在后台执行；LibVLC 播放器操作统一串行，自动续播与聊天入口停止 TTS 时采用 2 秒有界后台等待，避免设备异常拖死 Avalonia UI。
- 在线搜索、LLM、推荐、Edge TTS、Whisper 和 yt-dlp 支持超时或应用生命周期取消；关闭窗口后不再继续更新 ViewModel。
- LibVLC、WASAPI 与 NAudio 的释放采用有界等待和后台续清理，窗口关闭不会无限等待原生回调。
- 播放列表和设置使用串行、临时文件替换写入，降低并发保存及异常退出造成半份 JSON 的风险。
- 日志默认写入 `%APPDATA%\AIRadio\logs\airadio-*.log`，播放中断、音源超时和退出清理问题优先从这里排查。

## 项目结构

```text
AIRadio.Desktop/
  Assets/                  应用图标
  Converters/              共享 XAML Converter
  Models/                  Track、ChatMessage、DJProfile、推荐模型等
  Services/                播放、AI DJ、LLM、TTS、推荐、搜索、ASR、环境服务
  Themes/                  Colors.axaml 主题 token（Light/Dark）
  ViewModels/              ReactiveUI ViewModel
  Views/                   Avalonia 视图（TitleBar/ClockStage/PlayerDeck/ChatArea/PlaylistDrawer/StatusBar 等 UserControl）
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
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal --no-restore "/p:UseSharedCompilation=false"
```

## 已知技术债

- 推荐闭环仍是当前会话级：反馈只影响本轮推荐，尚未持久化为长期用户画像。
- AI 控制协议已支持 JSON 控制块，但旧格式仍保留短期兼容。
- 外部音乐 API 和 yt-dlp 仍可能因上游接口、地区限制或版权状态变化而失效；当前以硬超时、逐源状态和跨源回退降级。
- Node.js 便携包目前记录 SHA256 供审计，但尚未自动对照官方 `SHASUMS256.txt` 做完整性校验。
- 原生音频设备异常属于运行环境问题，发布前仍需执行连续播放、TTS 插播、切歌和关闭窗口的人工稳定性测试。
- Light/Dark 已使用独立配色 token，主界面、设置页和弹层会随主题完整切换。
