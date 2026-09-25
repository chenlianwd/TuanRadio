# TuanRadio

TuanRadio 是一个 Windows 桌面 AI 电台播放器：复古电台界面、AI DJ 对话和串场、Edge TTS 播报、多音源搜索、节目单推荐、收藏歌单、歌词逐行同步、星空/频谱视觉反馈。现已支持可搜索的本地曲库和用户配置的 OpenSubsonic 服务器；实验性在线平台仍可作为补充。

## 技术栈

| 模块 | 技术 |
| --- | --- |
| 桌面框架 | .NET 10 / Avalonia 11.3.9 |
| MVVM | ReactiveUI + ReactiveUI.Fody |
| 音频播放 | LibVLCSharp + NAudio |
| AI DJ | 支持 OpenAI 兼容、Anthropic 兼容和本地模型三种接口格式 |
| TTS | Edge TTS WebSocket 服务 |
| 音源 | 本地曲库、OpenSubsonic；完整构建另含 NeteaseCloudMusicApi(Node.js)、酷狗和 YouTube yt-dlp；酷我、咪咕需显式设置 `AIRADIO_ENABLE_LEGACY_WEB_SOURCES=1` 才注册 |
| 语音识别 | Whisper 本地 ASR |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |

## 当前能力

- 支持播放/暂停/上一首/下一首/进度/音量；本地文件夹可建立可搜索索引并手动重扫，导入文件同步加入索引。
- OpenSubsonic 可在设置页配置服务器与账号，连接时测试认证；凭据存入系统安全存储，搜索结果和播放流与本地曲库一起进入音源 Broker。
- 库抽屉四类视图：歌单、收藏、搜索、当前节目单（含推荐标签）。
- AI DJ 聊天、点歌、串场、TTS 播报和 TTS 中断，DJ 角色可切换（名称/声音/人设）。
- SongStory：STORY 按钮触发现曲 3-5 句 DJ 讲述，走 LLM 生成 + TTS 播报。
- 歌词模式：经本地代理双源取词（网易单步、酷狗两步 + 关键词兜底 + 时长过滤），ClockStage 中栏在时钟与歌词间切换、两侧频谱常驻共显，点击舞台或标题栏按钮均可切换并记忆偏好，按播放进度逐行滚动显示。
- 复古收音机调频音效：纯程序化 NAudio 声学合成（无外部音频资产），DJ 开口前 FM 调谐扫频自然淡入，新节目单落地时台呼微鸣；设置页开关。
- 设置页可配置 LLM 提供商、API Key、Base URL、模型、回复语言、语音播报和说话混音方式，带连接测试与失败原因提示；音源区可启停及上下调整每个已注册音源，并显示逐源连接诊断、最近 20 条健康记录中的正常接口响应数、搜索/解析成功时间和熔断状态。
- Radio Mode 自动续播：优先使用 `RecommendationService` 生成节目单，失败时退回 DJ 单首推荐。
- 推荐模型 v1：`ListeningContext`、`RecommendedTrack`、`RadioProgram`、`UserMusicFeedback`；会话级反馈中 NOPE 本轮排除、CALM/FIRE 切换氛围偏好，聊天的 change_mood 指令同样生效。
- 长期收听画像：播放/完播/跳过/按钮反馈/氛围指令持久化到 `%APPDATA%\AIRadio\listener-profile.json`，派生歌手亲和度（30 天半衰期）、Dislike 曲目黑名单（180 天过期、音乐身份跨源匹配）与 LLM 口味摘要，注入节目单搜索词生成与排除逻辑（冷启动门槛 ≥30 事件且 ≥3 歌手，强制 1 个探索方向防茧房）；DJ 聊天的 system 上下文同样注入口味段与黑名单避雷（可自然提及但受克制约束，跟随「学习我的口味」开关）；设置页提供「学习我的口味」开关与二次确认清除。
- 多平台音乐搜索：本地曲库和 OpenSubsonic 排在前面，完整构建还包含网易云、酷狗等实验性源；YouTube 只参与显式搜索/播放。搜索页先返回快速源结果，无结果时后台尝试慢源，新搜索或修改搜索词会取消旧慢源请求。每个源有独立硬超时，搜索状态逐源显示成功/超时/失败，业务失败按结构化分类渲染恢复建议；酷狗风控触发时自动弹出浏览器滑块验证（约 10 分钟冷却），设置页提供手动验证入口。
- CandidateRanker 智能排重打分：标题/歌手/时长逼近/源优先级四维加权（35%/30%/20%/15%），伴奏/翻唱/DJ 加速版等非预期特殊版本强惩罚，贯通聚合搜索重排、跨源回退与推荐候选净化；播放 URL 失效或返回明确试听流时依据歌名和歌手跨源重新匹配（同遍候选按时长接近度择优，防截断版/live 版误选），并同步实际生效的音源 ID。
- 真实 FFT 频谱：WasapiLoopbackCapture 采集系统输出 + 1024 点 FFT 转 32 频段；无有效回环数据时启用播放态视觉兜底，并限制幅度与刷新分配。
- 统一电台状态机 `RadioState`（Idle/Curating/Searching/Speaking/Playing/Error），StatusBar 实时显示。
- Light/Dark 双主题，全部颜色走 `Themes/Colors.axaml` token。
- 简洁播放模式：一键收缩为两行紧凑卡（曲目信息/播放控制/进度/收藏/迷你频谱/窗口控制），行 1 单行动态歌词、无词或前奏时平滑退回歌曲信息（设置开关）；拖动、双击或 Esc 还原，窗口模式记忆，置顶可选（设置页开关）。
- 时钟舞台环境指示器：天气（Open-Meteo，IP 自动定位或设置页手填城市）常驻图标显示阴晴雨雪，悬停显示温度与位置；日历徽标显示公历日号，农历节气/节日当天高亮，悬停显示农历与节日详情；取数失败只隐藏图标，不打扰播放。
- 自动化耐久测试：电台 30 轮连续续播、自然结束并发单飞、真实 LibVLC 四曲连播、画像 30 会话跨会话积累、播放列表混合并发落盘、聊天 60 轮长对话注入（发布前人工清单中的设备/睡眠/内存项仍需真人执行）。
- 收藏持久化到 `FavoriteIds`，并兼容旧的 `IsFavorite` 数据。
- 通过 Node.js 启动网易云音乐 API，本地缺少 Node.js 时可下载便携版。

## 稳定性设计

- URL 刷新和音量排空在后台执行；在线歌曲提前结束时依次刷新当前源、尝试替代源、再进入续播，避免同一试听片段循环重放。
- 播放 URL 进 LibVLC 前经 `MediaUriPolicy` 统一安全校验（scheme 白名单、IPv4/IPv6 字节级禁段、DNS 解析后复查、禁止 URL 内嵌凭据，全部 fail-closed），在 `MusicSourceBroker` 解析路径单点收口；用户配置的 OpenSubsonic 私有服务器只允许同源 stream 路径。本地代理回环地址属于 API 通道，不是播放 URL。
- `ResolvedMediaCache` 播放解析内存缓存（默认 10 分钟 TTL）：点歌/推荐/歌单重复解析直接命中，播放刷新与恢复链路强制刷新并先逐出旧条目防陈旧 URL，音源凭据变化自动清空对应源。
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
  Services/                播放、AI DJ、LLM、TTS、推荐、歌词、搜索、ASR、环境服务
  Services/Music/          音源子系统：Contracts、Broker、Playback（URL 安全校验与队列预检）、Providers（本地曲库/OpenSubsonic）、Adapters（实验性音源适配）
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
# 不包含 Node/yt-dlp 音源的核心构建：
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -p:TuanRadioEnableNodeProviders=false -v:minimal
```

## 验证

```bash
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
```

## 已知技术债

- 长期收听画像已落地且推荐/聊天双链路注入（见 docs/plans/2026-09-14 与 2026-09-15 设计文档），但画像纯本地、不跨设备同步（有意不做云端）。
- 外部音乐 API 和 yt-dlp 仍可能因上游接口、地区限制或版权状态变化而失效；当前以硬超时、逐源状态、队列后三首解析预检和跨源回退降级。可搜索本地曲库、OpenSubsonic、无 Node 核心构建、裁剪后的代理包与音源启停/排序已实现；Provider 独立程序集、开放曲库 PoC、音质偏好和完整播放传输适配仍待完成，详见 `docs/plans/2026-08-25-music-source-architecture-evolution-plan.md`。
- 网易云代理固定 `NeteaseCloudMusicApi` 4.32.0，生产依赖经过兼容更新并生成 `providers-manifest.json`；当前 `npm audit --omit=dev` 仍报告上游 `music-metadata`/`file-type` 链上的 3 项告警（1 中危、2 高危），升级到不兼容旧接口的版本前需单独评估。
- 酷狗代理生产锁文件已在兼容范围内更新，`npm ci --omit=dev --ignore-scripts` 与 `npm audit --omit=dev` 验证通过（0 项告警）；代理源码基线与本地补丁记录在 `server-kugou/VENDOR.md`。
- 当前 `MediaUriPolicy` 校验交给 LibVLC 的初始播放 URL；LibVLC 内部重定向无法逐跳复检，完整 DNS 重绑定防护与请求头传输适配仍属后续工作。
- Node.js 便携包下载即按官方 SHASUMS256 值做 fail-closed 校验，yt-dlp 固定官方 release 版本 + SHA256 校验（不追随 latest）。
- 原生音频设备异常属于运行环境问题，发布前仍需执行连续播放、TTS 插播、切歌和关闭窗口的人工稳定性测试。
- Light/Dark 双主题经 WCAG 对比度审计（文字对 ≥4.5:1，Light 主题三处超标 token 已修正）；后续视觉调整需保持该标准。
