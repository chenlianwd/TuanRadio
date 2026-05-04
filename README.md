# AI Radio

AI 数字电台桌面播放器 — 集成多平台在线音乐搜索、Live2D 数字人主播、AI DJ 语音播报。

## 技术栈

| 组件 | 技术 |
|------|------|
| 框架 | .NET 8 / Avalonia 11.3.9 |
| MVVM | ReactiveUI 20.1.1 + ReactiveUI.Fody |
| 音频播放 | LibVLCSharp (VLC 内核) |
| 数字人 | Cubism SDK for Web 5-r.5 + WebView2 (WebView.Avalonia) |
| AI DJ | MiniMax API（大模型对话 + TTS 语音合成） |
| 在线音乐 | NeteaseCloudMusicApi (Node.js) + 酷我/酷狗/咪咕 HTTP API |
| DI | Microsoft.Extensions.DependencyInjection |

## 架构概览

```
App.axaml.cs (启动入口)
├── DI 容器注册
├── Live2DStaticServer (HttpListener :18080, 静态文件服务)
├── MusicApiServer (Node.js 子进程 :37250, 网易云 API)
└── MainWindowViewModel
    ├── PlayerViewModel      → IAudioService (LibVLC 播放)
    ├── PlaylistViewModel    → IMusicSearchService (多音源搜索+播放)
    ├── ChatViewModel        → IDJService (MiniMax AI 对话)
    ├── SettingsViewModel    → IMinimaxService + ISecureStorage
    └── SpectrumViewModel    → IAudioService (频谱数据)
```

## 项目结构

```
AIRadio.Desktop/
├── Models/              数据模型 (Track, ChatMessage, DJProfile 等)
├── ViewModels/          ReactiveUI ViewModel 层
├── Views/               Avalonia AXAML 视图层
├── Services/            业务服务层
│   ├── AudioService.cs          LibVLC 播放引擎
│   ├── DJService.cs             AI DJ (MiniMax 大模型 + TTS)
│   ├── MinimaxService.cs        MiniMax API 客户端
│   ├── Live2DStaticServer.cs    HttpListener 静态文件服务
│   ├── MusicApiServer.cs        Node.js 子进程管理 (网易云)
│   ├── EnvironmentManager.cs    环境自动安装 (Node.js / WebView2)
│   ├── MultiSourceMusicService.cs  多音源聚合搜索
│   ├── NeteaseMusicService.cs   网易云音乐 API
│   ├── KuwoMusicService.cs      酷我音乐 API
│   ├── KugouMusicService.cs     酷狗音乐 API
│   └── MiguMusicService.cs      咪咕音乐 API
├── server/              NeteaseCloudMusicApi Node.js 服务
│   ├── package.json
│   └── start.js         启动脚本 (PORT=37250)
└── wwwroot/             静态资源
    ├── live2d-demo/     Cubism SDK 示例 (含 Ren 模型)
    ├── Core/            Cubism Core JS 库
    ├── Framework/       Cubism Framework (Shader/渲染)
    ├── Resources/       Live2D 模型资源 (Haru, Hiyori 等)
    └── assets/          Vite 打包的渲染框架
```

## 核心模块说明

### 1. 音频播放 (IAudioService)

- 基于 LibVLCSharp，支持本地文件和 HTTP 流媒体
- 自动检测 URL 类型：HTTP 用 `FromType.FromLocation`，本地文件用 `FromType.FromPath`
- 播放状态通过 Rx Subject 广播：`TrackChanged` / `StateChanged` / `PositionChanged` / `TrackEnded`
- 频谱数据由定时器驱动（~30fps 模拟，真实 FFT 需 VLC 音频回调）
- 单曲循环 / 列表循环 / 随机播放

### 2. 在线音乐 (IMusicSearchService)

- `MultiSourceMusicService` 并行搜索 4 个音源
- TrackId 格式：`source:id`（如 `netease:3369522598`）
- 搜索时自动加前缀，播放时根据前缀路由到对应音源
- 音源获取失败时 fallback 尝试其他音源

| 音源 | 搜索 | 播放 | 备注 |
|------|------|------|------|
| 网易云 | 本地 Node.js API | /song/url/v1 | 需要 NeteaseCloudMusicApi 服务 |
| 酷我 | kuwo.cn HTTP API | /api/v1/www/music/playUrl | 需要 Referer/CSRF 头 |
| 酷狗 | complexsearch JSONP | wwwapi.kugou.com | 返回 JSONP 需剥离 callback |
| 咪咕 | m.music.migu.cn | 双端点 fallback | 移动端 API + Web API |

### 3. 环境自动安装 (EnvironmentManager)

- 启动时检查系统 Node.js → 便携式 Node.js → 自动下载
- 便携式路径：`%AppData%/AIRadio/node/`
- 下载 `node-v20.18.3-win-x64.zip`，仅解压 `node.exe`
- 通过注册表检测 WebView2 Runtime 是否已安装

### 4. 数字人 (Live2D)

- 静态服务 (port 18080) 提供 Cubism SDK Web 资源
- WebView2 通过 WebView.Avalonia 嵌入 Avalonia 窗口
- 加载 Cubism SDK 示例页面，使用 Ren 模型（Vite 打包渲染框架）
- 支持多个模型：Haru / Hiyori / Mao / Mark / Natori / Ren / Rice / Wanko

### 5. AI DJ (IDJService)

- 基于 MiniMax 大模型 API
- 播放列表：聊天对话 / 歌曲过渡播报 / 语音合成
- 音色配置：`male-qn-qingse`（可在设置中修改）
- 语音合成支持：MiniMax T2A 接口

## 构建与运行

```bash
cd AIRadio/AIRadio.Desktop
dotnet build
dotnet run
```

### 依赖要求

- .NET 8 SDK
- Node.js（启动时自动下载便携版，也可使用系统已安装版本）
- WebView2 Runtime（Windows 10/11 通常已内置）
- VLC 播放器（LibVLC 运行时已包含在 NuGet 包中）

### 首次运行

1. 启动后自动下载 Node.js（如系统未安装）
2. 自动启动 NeteaseCloudMusicApi 服务 (port 37250)
3. 自动启动 Live2D 静态服务 (port 18080)
4. 等待 WebView2 加载数字人模型

## 已知问题

- 酷狗/咪咕 API 响应格式不稳定，搜索或播放可能失败（不影响网易云/酷我）
- Live2D 模型使用 Cubism SDK 示例，非自定义 DJ 形象
- 频谱可视化为模拟数据，非真实音频 FFT 分析
- 仅支持 Windows（WebView2 / 注册表检测 / 便携式 Node.js 下载均为 Windows 实现）
