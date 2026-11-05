# 第三方组件声明 / Third-Party Notices

TuanRadio（本仓库）以 **GPL-3.0-only** 许可证分发，见根目录 [LICENSE](LICENSE)。
本文件列出随发布包分发的第三方组件及其许可证；许可证全文见 [licenses/](licenses/) 或各组件上游链接。

## 原生/音视频组件（LGPL 系，随包分发二进制）

| 组件 | 版本 | 许可证 | 上游 |
|---|---|---|---|
| VideoLAN.LibVLC.Windows（libvlc 原生库） | 3.0.23.1 | LGPL-2.1-or-later | <https://www.videolan.org> / [NuGet](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows) |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later | [NuGet](https://www.nuget.org/packages/LibVLCSharp) |
| TagLibSharp | 2.3.0 | LGPL-2.1-only | <https://github.com/mono/taglib-sharp> |

LGPL-2.1 全文随包附于 [licenses/lgpl-2.1.txt](licenses/lgpl-2.1.txt)。

## .NET 托管组件（MIT / Apache-2.0）

| 组件 | 版本 | 许可证 | 上游 |
|---|---|---|---|
| Avalonia（含 Desktop/Themes.Fluent/Fonts.Inter/ReactiveUI） | 11.3.9 | MIT | <https://github.com/AvaloniaUI/Avalonia> |
| AvaloniaUI.DiagnosticsSupport | 2.2.1 | MIT | [NuGet](https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport) |
| SkiaSharp（Avalonia 传递依赖） | 2.88.9 | MIT | <https://github.com/mono/SkiaSharp> |
| ReactiveUI | 20.1.1 | MIT | <https://github.com/reactiveui/ReactiveUI> |
| ReactiveUI.SourceGenerators | 2.6.30 | MIT | [NuGet](https://www.nuget.org/packages/ReactiveUI.SourceGenerators) |
| System.Reactive | 6.1.0 | MIT | <https://github.com/dotnet/reactive> |
| Microsoft.Extensions.DependencyInjection / .Http | 8.0.1 | MIT | <https://github.com/dotnet/runtime> |
| NAudio | 2.3.0 | MIT | <https://github.com/naudio/NAudio> |
| Whisper.net + Whisper.net.Runtime | 1.7.4 | MIT | <https://github.com/sandrohanea/whisper.net> |
| Tmds.DBus.Protocol | 0.21.3 | MIT | <https://github.com/tmds/Tmds.DBus> |
| Velopack | 1.2.161 | MIT | <https://github.com/velopack/velopack> |
| Serilog + Serilog.Sinks.Console + Serilog.Sinks.File | 4.3.1 / 6.1.1 / 7.0.0 | Apache-2.0 | <https://github.com/serilog> |

NuGet 完整依赖图（含传递依赖与精确版本）见各工程 `packages.lock.json`。

## Node.js 代理组件（随包分发 npm 依赖）

| 组件 | 版本 | 许可证 | 上游 |
|---|---|---|---|
| NeteaseCloudMusicApi | 4.32.0 | MIT | <https://www.npmjs.com/package/NeteaseCloudMusicApi> |
| KuGouMusicApi（vendored） | 1.6.2 基线 | MIT | <https://github.com/MakcRe/KuGouMusicApi>（基线 commit 见 `server-kugou/VENDOR.md`） |

npm 依赖的逐包 SBOM（名称/版本/许可证/integrity）由构建时生成的 `providers-manifest.json` 随发布包提供。

## 代码移植声明

- `AIRadio.Desktop/Services/EdgeTtsService.cs` 中的 Sec-MS-GEC v2 令牌算法移植自
  [edge-tts](https://github.com/rany2/edge-tts)（GPL-3.0）的 `src/edge_tts/drm.py`，出处已在源码注释标明。

## 运行时按需下载（不随包分发）

| 组件 | 用途 | 许可证 | 来源 |
|---|---|---|---|
| Node.js v20.18.3（node.exe） | 网易/酷狗音源本地代理 | MIT | <https://nodejs.org> |
| yt-dlp（固定版本） | YouTube 音源解析 | Unlicense | <https://github.com/yt-dlp/yt-dlp> |
| Whisper ggml-base 模型 | 语音识别 | MIT | <https://huggingface.co/sandrohanea/whisper.net> |
