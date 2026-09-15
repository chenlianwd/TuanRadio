# TuanRadio

TuanRadio 是一个在线优先的 Windows 桌面 AI 电台播放器：复古电台界面、AI DJ 对话和串场、Edge TTS 播报、多平台在线音乐搜索、节目单推荐、收藏歌单、星空/频谱视觉反馈。本地文件导入仅作为兼容能力，不是产品主流程。

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
- 设置页可配置 LLM 提供商、API Key、Base URL、模型、回复语言、语音播报和说话混音方式，带连接测试与失败原因提示；音源账号区提供逐源连接诊断（按结构化分类渲染各源状态与恢复建议）。
- Radio Mode 自动续播：优先使用 `RecommendationService` 生成节目单，失败时退回 DJ 单首推荐。
- 推荐模型 v1：`ListeningContext`、`RecommendedTrack`、`RadioProgram`、`UserMusicFeedback`；会话级反馈中 NOPE 本轮排除、CALM/FIRE 切换氛围偏好，聊天的 change_mood 指令同样生效。
- 长期收听画像：播放/完播/跳过/按钮反馈/氛围指令持久化到 `%APPDATA%\AIRadio\listener-profile.json`，派生歌手亲和度（30 天半衰期）、Dislike 曲目黑名单（180 天过期、音乐身份跨源匹配）与 LLM 口味摘要，注入节目单搜索词生成与排除逻辑（冷启动门槛 ≥30 事件且 ≥3 歌手，强制 1 个探索方向防茧房）；DJ 聊天的 system 上下文同样注入口味段与黑名单避雷（可自然提及但受克制约束，跟随「学习我的口味」开关）；设置页提供「学习我的口味」开关与二次确认清除。
- 多平台音乐搜索：网易云优先，酷我/酷狗/咪咕并行 fallback，YouTube 作为最低优先级兜底；每个源有独立硬超时，搜索状态逐源显示成功/超时/失败，业务失败按结构化分类（未登录/登录态或代理失效/风控验证/接口失效）渲染用户可读文案与恢复建议。
- 播放 URL 失效或返回明确试听流时会依据歌名和歌手跨源重新匹配（同遍候选按时长接近度择优，防截断版/live 版误选），并同步实际生效的音源 ID。
- 真实 FFT 频谱：WasapiLoopbackCapture 采集系统输出 + 1024 点 FFT 转 32 频段；无有效回环数据时启用播放态视觉兜底，并限制幅度与刷新分配。
- 统一电台状态机 `RadioState`（Idle/Curating/Searching/Speaking/Playing/Error），StatusBar 实时显示。
- Light/Dark 双主题，全部颜色走 `Themes/Colors.axaml` token。
- 简洁播放模式：一键收缩为两行紧凑卡（曲目信息/播放控制/进度/收藏/迷你频谱/窗口控制），拖动、双击或 Esc 还原；窗口模式记忆，置顶可选（设置页开关）。
- 收藏持久化到 `FavoriteIds`，并兼容旧的 `IsFavorite` 数据。
- 通过 Node.js 启动网易云音乐 API，本地缺少 Node.js 时可下载便携版。

## 稳定性设计

- URL 刷新和音量排空在后台执行；在线歌曲提前结束时依次刷新当前源、尝试替代源、再进入续播，避免同一试听片段循环重放。
- LibVLC 播放器操作统一串行，自动续播与聊天入口停止 TTS 时采用 2 秒有界后台等待，避免设备异常拖死 Avalonia UI。
- 在线搜索、LLM、推荐、Edge TTS、Whisper 和 yt-dlp 支持超时或应用生命周期取消；关闭窗口后不再继续更新 ViewModel。
- LibVLC、WASAPI 与 NAudio 的释放采用有界等待和后台续清理，窗口关闭不会无限等待原生回调。
- 播放列表和设置使用串行、临时文件替换写入，降低并发保存及异常退出造成半份 JSON 的风险。
- 播放列表与当前索引跨线程同步访问并返回快照；电台续播推荐链路带硬超时兜底，推荐挂起不会拖停电台。
- Edge TTS 合成被服务端中断或返回空音频时自动换新连接重试；音源业务失败（鉴权/风控）逐源透传为失败状态而非"成功 0 条"。
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
  Views/                   Avalonia 视图（TitleBar/ClockStage/PlayerDeck/CompactPlayer/ChatArea/PlaylistDrawer/StatusBar 等 UserControl）
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
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
```

## 已知技术债

- 长期收听画像已落地且推荐/聊天双链路注入（见 docs/plans/2026-09-14 与 2026-09-15 设计文档），但画像纯本地、不跨设备同步（有意不做云端）。
- 外部音乐 API 和 yt-dlp 仍可能因上游接口、地区限制或版权状态变化而失效；当前以硬超时、逐源状态和跨源回退降级。
- Node.js 便携包下载即按官方 SHASUMS256 值做 fail-closed 校验，yt-dlp 固定官方 release 版本 + SHA256 校验（不追随 latest）。
- 原生音频设备异常属于运行环境问题，发布前仍需执行连续播放、TTS 插播、切歌和关闭窗口的人工稳定性测试。
- Light/Dark 双主题经 WCAG 对比度审计（文字对 ≥4.5:1，Light 主题三处超标 token 已修正）；后续视觉调整需保持该标准。
