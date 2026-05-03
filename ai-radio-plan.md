# AI Radio 数字人电台 - 项目实施计划

## 一、项目概述

| 项目 | 内容 |
|------|------|
| **项目名称** | AI Radio |
| **项目类型** | Windows 桌面应用 |
| **核心功能** | AI 数字人主播电台，用户导入歌单后 AI 自动串场、介绍歌曲、聊天互动、主播有表情动画 |

---

## 二、技术栈

| 模块 | 技术方案 | 说明 |
|------|----------|------|
| **桌面框架** | Avalonia 11 + ReactiveUI | Windows 桌面 UI |
| **语言** | C# / .NET 8 | 最新 LTS 版本 |
| **音频播放** | LibVLCSharp | 支持全格式音频解码 |
| **虚拟形象** | WebView2 + Live2D Cubism Web SDK | Edge Chromium 内嵌渲染 |
| **LLM 对话** | Minimax API (OpenAI 兼容) | AI 串场文案生成 |
| **TTS 语音** | Minimax T2A API | 语音合成 |
| **ASR** | 保留（Whisper / Web Speech API） | 待后续功能扩展 |
| **状态管理** | ReactiveUI + ObservableObject | 响应式 MVVM |
| **依赖注入** | Microsoft.Extensions.DI | 服务注册 |

---

## 三、目标平台

| 平台 | 支持状态 | 说明 |
|------|----------|------|
| **Windows 10/11** | ✅ 目标平台 | Edge WebView2 Runtime（需安装） |

> **注意**：当前版本仅支持 Windows 平台。WebView2 Runtime 需要单独安装，或在应用安装包中捆绑。

---

## 四、项目结构

```
AIRadio/
├── AIRadio.Desktop/                # 主应用程序
│   ├── App.axaml                   # 应用入口
│   ├── App.axaml.cs
│   ├── MainWindow.axaml            # 主窗口
│   ├── MainWindow.axaml.cs
│   │
│   ├── ViewModels/                 # ViewModels（ReactiveUI）
│   │   ├── MainWindowViewModel.cs
│   │   ├── PlayerViewModel.cs      # 播放器逻辑
│   │   ├── PlaylistViewModel.cs    # 歌单管理
│   │   ├── ChatViewModel.cs         # 听众互动
│   │   └── SettingsViewModel.cs     # 设置面板
│   │
│   ├── Views/                      # Views（Avalonia XAML）
│   │   ├── MainWindow.axaml
│   │   ├── PlayerView.axaml
│   │   ├── PlaylistView.axaml
│   │   ├── ChatView.axaml
│   │   └── SettingsView.axaml
│   │
│   ├── Services/                   # 业务服务层
│   │   ├── IAudioService.cs        # 音频服务接口
│   │   ├── AudioService.cs         # LibVLCSharp 实现
│   │   ├── IMinimaxService.cs      # Minimax API 接口
│   │   ├── MinimaxService.cs       # Minimax 实现
│   │   ├── IDJService.cs           # DJ 服务接口
│   │   ├── DJService.cs            # AI 主播串场逻辑
│   │   ├── ISecureStorage.cs       # 安全存储接口
│   │   └── SecureStorage.cs        # Windows Credential Manager 实现
│   │
│   ├── Models/                     # 数据模型
│   │   ├── Track.cs                # 音乐曲目
│   │   ├── ChatMessage.cs          # 聊天消息
│   │   ├── DJProfile.cs            # 主播配置
│   │   └── RadioSettings.cs        # 应用设置
│   │
│   ├── Assets/
│   │   └── models/                 # Live2D 模型文件
│   │       └── Hiyori/             # 示例模型
│   │
│   └── AIRadio.Desktop.csproj
│
├── AIRadio.Web/                    # Live2D Web 部分
│   ├── index.html                  # 内嵌页面
│   ├── js/
│   │   ├── live2dcubismcore.min.js # Cubism Core
│   │   ├── live2d.min.js           # Cubism SDK
│   │   ├── app.js                  # 主程序
│   │   ├── avatar-controller.js   # 形象控制
│   │   └── lip-sync.js            # 口型同步
│   ├── css/
│   │   └── main.css
│   └── assets/
│       └── models/                 # Web 用模型
│
├── AIRadio.sln                     # 解决方案文件
├── README.md
└── LICENSE
```

---

## 五、核心模块设计

### 5.1 音频服务（AudioService）

**职责**：管理音频播放、频谱分析、播放列表

```csharp
public interface IAudioService
{
    bool IsPlaying { get; }
    TimeSpan CurrentPosition { get; }
    TimeSpan Duration { get; }

    Task LoadTracksAsync(IEnumerable<string> filePaths);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);

    Track CurrentTrack { get; }
    IReadOnlyList<Track> Playlist { get; }
    void Next();
    void Previous();
    void Shuffle();

    IObservable<float[]> SpectrumData { get; }

    IObservable<Track> TrackChanged { get; }
    IObservable<PlaybackState> StateChanged { get; }
}
```

**技术实现**：
- 使用 `LibVLCSharp.Shared.MediaPlayer` 播放
- `MediaPlayer.AudioCallback` 获取原始 PCM 数据
- 手动实现 FFT 计算频谱

---

### 5.2 Minimax API 服务（MinimaxService）

**职责**：封装 Minimax API 调用（LLM + TTS）

```csharp
public interface IMinimaxService
{
    Task<string> ChatAsync(string userMessage, ChatHistory history);
    Task<byte[]> TextToSpeechAsync(string text, TTSConfig config);
    Task<string> GenerateTrackIntroductionAsync(Track current, Track next);
}
```

**API 端点**：
- Chat: `POST https://api.minimaxi.com/v1/text/chatcompletion_v2` (OpenAI 兼容格式)
- TTS: `POST https://api.minimaxi.com/v1/t2a_v2`
- 备用 TTS: `https://api-bj.minimaxi.com/v1/t2a_v2`

**认证方式**：Bearer Token (API Key)

**支持模型**：
- 文本：MiniMax-M2.7, MiniMax-M2.5, MiniMax-M2.1, MiniMax-M2
- 语音：speech-2.8-hd, speech-2.8-turbo, speech-2.6-hd, speech-2.6-turbo

---

### 5.3 DJ 服务（DJService）

**职责**：AI 主播核心逻辑，生成串场文案、检测情绪、触发动作

```csharp
public interface IDJService
{
    void Initialize(DJProfile profile);
    Task<DJScript> GenerateTrackIntroductionAsync(Track current, Track next);
    Task<string> GenerateChatResponseAsync(string userMessage);
    string CurrentEmotion { get; }
    void SetExpression(string expressionName);
    void TriggerMotion(string motionName);
    ILive2DViewer Live2DViewer { get; set; }
}
```

**情绪 → 表情映射**：
| 情绪 | 表达式 | 动作 |
|------|--------|------|
| happy | smile | wave |
| sad | droopy | - |
| excited | smile + 眼睛发光 | jump |
| neutral | idle | idle |
| thinking | - | nod |

---

### 5.4 Live2D 视图控制（Live2DViewer）

**职责**：通过 WebView2 控制 Live2D 模型

```csharp
public interface ILive2DViewer
{
    Task LoadModelAsync(string modelDirectory);
    void SetExpression(string expressionName);
    void PlayMotion(string motionName);
    void UpdateLipSync(float[] spectrumData);
    IObservable<string> MotionFinished { get; }
}
```

**WebView2 通信机制**：
```
.NET (C#)                    JavaScript
    │                            │
    │  ExecuteScriptAsync()      │
    │ ─────────────────────────► │
    │                            │
    │  DispatchEvent()           │
    │ ◄───────────────────────── │
```

---

### 5.5 安全存储（SecureStorage）

**职责**：安全存储 API Key 等敏感信息

```csharp
public interface ISecureStorage
{
    Task SaveApiKeyAsync(string service, string apiKey);
    Task<string?> GetApiKeyAsync(string service);
    void DeleteApiKey(string service);
}
```

**Windows 实现**：使用 Windows Credential Manager (CredWrite/CredRead API)

```csharp
public class WindowsSecureStorage : ISecureStorage
{
    public Task SaveApiKeyAsync(string service, string apiKey)
    {
        // 使用 CredentialManager API
        var credential = new Credential
        {
            Target = $"AIRadio:{service}",
            Username = service,
            Password = apiKey,
            Type = CredentialType.Generic,
            PersistanceType = PersistanceType.LocalComputer
        };
        credential.Save();
        return Task.CompletedTask;
    }

    public Task<string?> GetApiKeyAsync(string service)
    {
        var credential = new Credential { Target = $"AIRadio:{service}" };
        if (credential.Load())
        {
            return Task.FromResult<string?>(credential.Password);
        }
        return Task.FromResult<string?>(null);
    }

    public void DeleteApiKey(string service)
    {
        var credential = new Credential { Target = $"AIRadio:{service}" };
        credential.Delete();
    }
}
```

---

## 六、数据模型

### 6.1 Track（音乐曲目）

```csharp
public class Track
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Artist { get; set; }
    public string Album { get; set; }
    public TimeSpan Duration { get; set; }
    public string FilePath { get; set; }
    public byte[]? CoverArt { get; set; }

    public static Track FromFile(string filePath);
}
```

### 6.2 ChatMessage（聊天消息）

```csharp
public class ChatMessage
{
    public string Id { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum MessageRole
{
    System,
    User,
    Assistant
}
```

### 6.3 DJProfile（主播配置）

```csharp
public class DJProfile
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string AvatarModelPath { get; set; }
    public string DefaultExpression { get; set; }
    public string VoiceId { get; set; }
    public string SystemPrompt { get; set; }
}
```

### 6.4 DJScript（串场脚本）

```csharp
public class DJScript
{
    public string Text { get; set; }
    public string Emotion { get; set; }
    public string Expression { get; set; }
    public string Motion { get; set; }
    public TimeSpan Duration { get; set; }
}
```

---

## 七、AI 串场流程

### 7.1 歌曲切换触发串场

```
歌曲切换事件 → 生成串场文案 → Minimax LLM → 情绪检测
                                       ↓
                        触发表情+动作    TTS 合成语音
                              ↓              ↓
                      Live2D 播放动画  ←  WebView2 播放音频
```

### 7.2 系统提示词模板

```csharp
const string DJ_SYSTEM_PROMPT = @"
你是一个电台AI主播，名字叫""{dj_name}""。

性格特点：
- 活泼开朗，善于与听众互动
- 说话自然流畅，像朋友聊天
- 熟悉流行音乐，能准确介绍歌曲
- 语气亲切，有时会开玩笑

发言规则：
1. 每次发言不超过60字（短小精悍）
2. 介绍歌曲时包含：歌名、歌手、专辑
3. 根据歌曲类型调整语气（摇滚热烈，抒情温柔）
4. 适当加入口头禅（如""好听的来啦""、""这首歌我超喜欢""）

当前播放：{current_track}
即将播放：{next_track}
";
```

---

## 八、依赖包清单

### 8.1 NuGet 包

| 包名 | 版本 | 用途 |
|------|------|------|
| `Avalonia` | 11.x | UI 框架 |
| `Avalonia.Desktop` | 11.x | 桌面支持 |
| `Avalonia.ReactiveUI` | 11.x | ReactiveUI 集成 |
| `LibVLCSharp` | 3.x | 音视频播放 |
| `LibVLCSharp.Avalonia` | 3.x | Avalonia 绑定 |
| `VideoLAN.LibVLC.Windows` | 3.x | Windows VLC 运行时 |
| `Microsoft.Extensions.DependencyInjection` | 8.x | DI 容器 |
| `Microsoft.Extensions.Http` | 8.x | HTTP 客户端 |
| `System.Text.Json` | 8.x | JSON 序列化 |
| `TagLibSharp` | 2.x | 音频元数据解析 |
| `Serilog` | 3.x | 日志记录 |
| `Microsoft.Web.WebView2` | 1.x | Edge WebView2 (Windows) |

### 8.2 外部依赖（系统级）

| 依赖 | 安装方式 | 说明 |
|------|----------|------|
| **WebView2 Runtime** | [官方下载](https://developer.microsoft.com/microsoft-edge/webview2/) | Edge Chromium 运行时，应用启动时检查 |
| **VLC 运行时** | NuGet 包自动包含 | LibVLCSharp 依赖 |

---

## 九、开发阶段规划

### Phase 1：项目搭建（第1周）

| 任务 | 内容 | 产出 |
|------|------|------|
| 1.1 | 创建 .NET Solution + 项目 | `AIRadio.sln` |
| 1.2 | 配置 Avalonia + ReactiveUI | 空白窗口可运行 |
| 1.3 | 集成 LibVLCSharp | 基础音频播放 |
| 1.4 | 实现播放列表 UI | 文件导入、列表显示 |
| 1.5 | 实现播放器控制 | 播放/暂停/上一首/下一首 |

**验证**：可导入本地音频文件并播放

---

### Phase 2：Live2D 集成（第2周）

| 任务 | 内容 | 产出 |
|------|------|------|
| 2.1 | 创建 WebView2 基础页面 | Edge WebView2 内嵌 |
| 2.2 | 集成 Live2D Cubism SDK | 模型加载、渲染 |
| 2.3 | 实现表情切换 | happy/sad/excited/idle |
| 2.4 | 实现动作触发 | wave/nod/idle |
| 2.5 | WebView 与 .NET 通信 | C# 控制 JS |

**验证**：Live2D 模型正常显示，可切换表情/动作

---

### Phase 3：Minimax API 集成（第3周）

| 任务 | 内容 | 产出 |
|------|------|------|
| 3.1 | 实现 MinimaxService | API 封装 |
| 3.2 | 实现 LLM 对话功能 | 串场文案生成 |
| 3.3 | 实现 TTS 语音合成 | 文字转语音 |
| 3.4 | 安全存储 API Key | Windows Credential Manager |
| 3.5 | 配置面板 UI | API Key 配置界面 |

**验证**：可调用 Minimax API 生成文案并合成语音

---

### Phase 4：AI 串场 + 互动（第4周）

| 任务 | 内容 | 产出 |
|------|------|------|
| 4.1 | 实现 DJService | 串场逻辑核心 |
| 4.2 | 实现情绪检测 | 文案 → 表情映射 |
| 4.3 | 实现自动串场 | 歌曲切换时触发 |
| 4.4 | 实现听众聊天 | ChatPanel + 对话 |
| 4.5 | 语音 + 动画同步 | TTS 播放时口型动 |

**验证**：歌曲切换时 AI 自动串场，听众可聊天互动

---

### Phase 5：UI 美化 + 测试（第5周）

| 任务 | 内容 | 产出 |
|------|------|------|
| 5.1 | 音频可视化 | 频谱动画 |
| 5.2 | 过渡效果 | 淡入淡出、交叉混合 |
| 5.3 | 界面美化 | 配色、布局优化 |
| 5.4 | Windows 测试 | WebView2 兼容性测试 |
| 5.5 | 性能测试 | 内存、CPU 占用优化 |

**验证**：Windows 平台功能完整、性能达标

---

### Phase 6：打包发布（第6周）

| 任务 | 内容 | 产出 |
|------|------|------|
| 6.1 | Windows 打包 | `.exe` / `.msi` 安装包 |
| 6.2 | 依赖检查 | WebView2 Runtime 检测/捆绑 |
| 6.3 | 版本测试 | 完整功能测试 |
| 6.4 | 文档编写 | README、使用说明 |

---

## 十、技术风险与应对

| 风险 | 概率 | 影响 | 应对措施 |
|------|------|------|----------|
| WebView2 Runtime 未安装 | 中 | 高 | 应用启动时检测，引导用户下载安装 |
| Live2D 模型获取困难 | 中 | 高 | 先使用官方示例模型（Hiyori） |
| LibVLCSharp 初始化失败 | 低 | 中 | 检查 VLC 运行时完整性 |
| Minimax API 限流 | 低 | 中 | 添加请求间隔 + 指数退避重试 |
| .NET 8 运行时缺失 | 低 | 中 | 使用自包含发布模式 |

---

## 十一、后续扩展功能

| 功能 | 优先级 | 说明 |
|------|--------|------|
| ASR 语音识别 | P2 | Whisper 本地或 Minimax ASR |
| 更多 Live2D 模型 | P2 | 用户可选择不同主播形象 |
| 音效混音 | P2 | 背景音乐、环境音 |
| 歌曲推荐 | P3 | 基于播放历史推荐 |
| 录音功能 | P3 | 录制广播输出 |
| 皮肤主题 | P3 | 深色/浅色模式 |

---

## 十二、参考资源

| 资源 | 链接 |
|------|------|
| Avalonia 官方文档 | https://docs.avalonia.net/ |
| LibVLCSharp | https://code.videolan.org/videolan/LibVLCSharp |
| Live2D Cubism SDK Web | https://docs.live2d.com/cubism-sdk-tutorials/ |
| Minimax API 文档 | https://www.minimaxi.com/document |

---

## 附录A：Live2D WebView2 集成详细设计

### A.1 整体架构

```
┌─────────────────────────────────────────────────────────┐
│                    Avalonia Desktop App                  │
│  ┌──────────────────────────────────────────────────┐   │
│  │              Edge WebView2 (Chromium)              │   │
│  │  ┌────────────────────────────────────────────┐   │   │
│  │  │            C# ↔ JavaScript 通信层            │   │   │
│  │  └────────────────────────────────────────────┘   │   │
│  └──────────────────────────────────────────────────┘   │
│                         │                                │
│                   ┌─────▼─────┐                         │
│                   │ 本地 HTTP  │                         │
│                   │ 服务器     │                         │
│                   │ :18080    │                         │
│                   └─────┬─────┘                         │
│                         │                                │
│                   ┌─────▼─────┐                         │
│                   │ 静态资源   │                         │
│                   │ 文件目录   │                         │
│                   └───────────┘                         │
└─────────────────────────────────────────────────────────┘
```

### A.2 静态资源服务器

由于 WebView2 需要通过 HTTP 加载本地资源，应用启动时需开启本地静态文件服务器。

**实现方案**（.NET 8 轻量级）：
- 使用 `HttpListener`（无需额外包，内置于 .NET）
- 监听本地端口（默认 `18080`）
- 仅绑定 `localhost`，禁止外部访问

**托管目录结构**：
```
/wwwroot/
├── index.html                 # 入口页面
├── css/main.css               # 样式
├── js/
│   ├── live2dcubismcore.min.js # Cubism Core
│   ├── live2d.min.js           # Cubism SDK
│   ├── app.js                  # 主程序
│   ├── avatar-controller.js    # 形象控制
│   └── lip-sync.js             # 口型同步
└── assets/models/              # Live2D 模型
```

**C# 静态服务器实现**（使用 HttpListener）：
```csharp
public class Live2DStaticServer : IDisposable
{
    private HttpListener _listener;
    private string _contentRoot;

    public void Start(int port, string contentRoot)
    {
        _contentRoot = contentRoot;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _listener.BeginGetContext(OnRequest, null);
    }

    private void OnRequest(IAsyncResult result)
    {
        var context = _listener.EndGetContext(result);
        _listener.BeginGetContext(OnRequest, null);
        
        var requestPath = context.Request.Url.LocalPath.TrimStart('/');
        var filePath = Path.Combine(_contentRoot, requestPath);
        
        if (File.Exists(filePath))
        {
            var content = File.ReadAllBytes(filePath);
            context.Response.ContentType = GetMimeType(filePath);
            context.Response.OutputStream.Write(content, 0, content.Length);
        }
        else
        {
            context.Response.StatusCode = 404;
        }
        context.Response.Close();
    }

    public void Dispose()
    {
        _listener?.Stop();
        _listener?.Close();
    }
}
```

### A.3 C# ↔ JavaScript 通信协议

#### C# → JS 指令

通过 `WebView.ExecuteScriptAsync()` 调用 JS 函数：

| 指令 | JS 函数 | 参数 | 说明 |
|------|---------|------|------|
| 加载模型 | `window.avatar.loadModel(modelPath)` | 模型路径 | 加载 Live2D 模型 |
| 设置表情 | `window.avatar.setExpression(name)` | 表情名 | 切换表情 |
| 播放动作 | `window.avatar.playMotion(name)` | 动作名 | 触发动作 |
| 口型同步 | `window.avatar.updateLipSync(data)` | JSON数组 | 频谱数据 |

#### JS → C# 回调

通过 `window.chrome.webview.postMessage()` 发送消息到 C#（WebView2 标准 API）：

| 事件 | 消息格式 | 触发时机 |
|------|----------|----------|
| 模型加载完成 | `{"type":"model_loaded"}` | 模型初始化完成 |
| 动作播放完成 | `{"type":"motion_finished","name":"wave"}` | 动作播放结束 |
| 错误 | `{"type":"error","message":"..."}` | JS 端异常 |

#### C# 端消息接收

```csharp
// 在 WebView2 初始化时注册消息处理
_webView.CoreWebView2.WebMessageReceived += (sender, e) =>
{
    var message = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson);
    switch (message.Type)
    {
        case "model_loaded":
            ModelLoaded?.Invoke();
            break;
        case "motion_finished":
            MotionFinished?.Invoke(message.Name);
            break;
        case "error":
            Error?.Invoke(message.Message);
            break;
    }
};
```

### A.4 安全沙箱策略

| 限制项 | 策略 |
|--------|------|
| 本地文件访问 | 禁止，所有资源通过 HTTP 服务器加载 |
| 外部网络请求 | 禁止，CSP 设置为 `default-src 'self'` |
| 摄像头/麦克风 | 禁止 |
| 用户输入 | 禁止，无需交互 |
| 同源策略 | 仅允许 `localhost:18080` |

**CSP 头部配置**：
```html
<meta http-equiv="Content-Security-Policy" content="default-src 'self' 'unsafe-inline' 'unsafe-eval';">
```

---

## 附录B：配置管理设计

### B.1 配置文件格式

配置文件采用 JSON 格式，存储用户个性化设置：

```json
{
  "minimax_api_key": "",
  "dj_profile": {
    "name": "小音",
    "description": "活泼开朗的电台主播，熟悉各类音乐风格",
    "voice_id": "female_clean",
    "model_path": "Assets/models/Hiyori",
    "default_expression": "idle",
    "system_prompt": "你是一个电台AI主播..."
  },
  "playback": {
    "crossfade_duration": 2.0,
    "auto_transition": true,
    "transition_mode": "track_start",
    "volume": 0.8,
    "shuffle": false,
    "repeat_mode": "list"
  },
  "audio": {
    "spectrum_enabled": true,
    "spectrum_update_rate": 30
  },
  "ui": {
    "theme": "dark",
    "window_width": 1000,
    "window_height": 700,
    "splitter_ratio": 0.65
  }
}
```

### B.2 配置存储路径

Windows 平台使用 `%APPDATA%` 目录：

```
%APPDATA%/AIRadio/settings.json
```

**获取配置路径**：
```csharp
public static string GetSettingsDirectory()
{
    var appData = Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData);
    return Path.Combine(appData, "AIRadio");
}

public static string GetSettingsFilePath()
{
    return Path.Combine(GetSettingsDirectory(), "settings.json");
}
```

### B.3 配置加载流程

```
应用启动
    │
    ├──► 检查配置文件是否存在
    │         │
    │    ┌────▼────┐
    │    │  存在？  │
    │    └────┬────┘
    │    是/  │  \否
    │    ◄────┘
    │
    ├──► 读取并反序列化 JSON
    │         │
    │    ┌────▼─────────┐
    │    │ 验证完整性    │
    │    │ 必填字段检查  │
    │    └────┬─────────┘
    │         │
    │    ┌────▼──────────┐
    │    │ 通过？         │
    │    └────┬──────────┘
    │    是/  │  \否
    │    ◄────┘
    │
    ├──► 使用默认配置创建
    │
    └──► 注入 DI 容器
```

### B.4 配置保存时机

| 场景 | 触发方式 |
|------|----------|
| 用户修改设置并点击"保存" | 即时写入 |
| 应用正常退出 | 自动保存 |
| API Key 更新 | 即时写入 |
| 窗口大小改变 | 延迟写入（防抖 500ms） |

### B.5 配置文件异常处理

| 异常场景 | 处理方式 |
|----------|----------|
| 文件不存在 | 使用默认配置，首次保存时创建 |
| JSON 解析失败 | 备份损坏文件，使用默认配置 |
| 版本不兼容 | 自动升级配置格式，保留已有值 |
| 文件写入失败 | 提示用户检查权限，使用内存配置 |

---

## 附录C：错误处理策略

### C.1 错误分类与处理

| 错误类型 | 典型场景 | 处理方式 | 用户提示 |
|----------|----------|----------|----------|
| **网络错误** | API 调用超时/拒绝 | 指数退避重试（最多3次） | "网络连接异常，正在重试..." |
| **认证错误** | API Key 无效/过期 | 停止重试，提示检查配置 | "API Key 无效，请检查设置" |
| **播放错误** | 文件损坏/格式不支持 | 跳到下一首 | "无法播放此文件，已跳过" |
| **模型错误** | Live2D 模型加载失败 | 显示占位图 | "模型加载失败，请检查文件" |
| **TTS 错误** | 语音合成失败 | 跳过语音，显示文字 | "语音合成失败，显示文字" |
| **存储错误** | 配置文件写入失败 | 使用内存配置 | "配置保存失败" |

### C.2 重试策略

```csharp
public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxRetries = 3,
        int baseDelayMs = 1000)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                attempt++;
                var delay = baseDelayMs * Math.Pow(2, attempt - 1);
                await Task.Delay(TimeSpan.FromMilliseconds(delay));
            }
        }
    }
}
```

### C.3 全局异常处理

```
异常产生
    │
    ├──► 应用级异常（ApplicationException）
    │         │
    │    ┌────▼──────────┐
    │    │ 记录日志       │
    │    │ 显示用户提示   │
    │    │ 恢复应用状态   │
    │    └───────────────┘
    │
    └──► 未处理异常（UnhandledException）
              │
         ┌────▼──────────┐
         │ 记录崩溃日志   │
         │ 显示错误对话框 │
         │ 优雅退出       │
         └───────────────┘
```

### C.4 日志记录

使用 Serilog 记录日志：

| 日志级别 | 内容 |
|----------|------|
| **Debug** | API 调用详情、WebView2 通信 |
| **Info** | 应用启动、播放切换、模型加载 |
| **Warning** | 重试、降级处理 |
| **Error** | API 失败、播放异常 |
| **Fatal** | 应用崩溃 |

---

## 附录D：线程安全设计

### D.1 线程模型（Windows）

```
┌────────────────────────────────────────────────────┐
│                   主线程 (UI)                       │
│  Avalonia Dispatcher / UI 绑定 / 命令处理          │
├────────────────────────────────────────────────────┤
│                   音频线程                          │
│  LibVLC MediaPlayer 回调 → PCM 数据 → FFT 分析     │
├────────────────────────────────────────────────────┤
│                   WebView2 渲染线程                 │
│  Live2D 模型渲染（WebGL 上下文）                   │
├────────────────────────────────────────────────────┤
│                   网络线程                          │
│  HttpClient 异步请求 → Minimax API                 │
└────────────────────────────────────────────────────┘
```

### D.2 线程同步规则

| 操作 | 执行线程 | 同步方式 |
|------|----------|----------|
| UI 更新（绑定、属性通知） | 必须主线程 | `Dispatcher.UIThread.InvokeAsync()` |
| 音频播放控制 | LibVLC 内部线程 | MediaPlayer API（线程安全） |
| 频谱数据更新 | 音频线程 → 主线程 | `Subject<T>` + Rx 调度 |
| WebView JS 调用 | 主线程 | `Dispatcher.UIThread.InvokeAsync()` |
| HTTP API 调用 | 线程池线程 | `async/await`（不阻塞 UI） |

### D.3 Rx 调度策略

```csharp
// 频谱数据：音频线程生成，主线程消费
SpectrumData = _spectrumSubject
    .ObserveOn(RxApp.MainThreadScheduler)
    .AsObservable();

// Track 变更：LibVLC 回调线程，主线程消费
TrackChanged = _trackChangedSubject
    .ObserveOn(RxApp.MainThreadScheduler)
    .AsObservable();
```

### D.4 WebView2 线程安全

WebView2 不是线程安全的，所有操作必须在主线程：

```csharp
// 安全的调用方式
await Dispatcher.UIThread.InvokeAsync(() =>
{
    _webView.ExecuteScriptAsync("window.avatar.setExpression('happy')");
});

// 错误的方式（会抛出异常）
// _webView.ExecuteScriptAsync(...)  // 非主线程
```

### D.5 音频与主线程通信

```
音频回调线程                主线程
     │                        │
     │  PCM 数据              │
     │  ──────────────────►   │
     │                        │
     │  FFT 计算              │
     │  频谱数据              │
     │  ──────────────────►   │  Subject.OnNext()
     │                        │
     │                        │  ObserveOn(MainThread)
     │                        │  UI 更新 / 口型同步
```

---

## 附录E：测试策略

### E.1 测试框架

| 框架 | 用途 |
|------|------|
| **xUnit** | 单元测试框架 |
| **Moq** | Mock 框架，模拟外部依赖 |
| **FluentAssertions** | 断言库，更可读的断言 |
| **Avalonia.Headless** | Avalonia UI 组件测试 |

### E.2 测试覆盖范围

| 模块 | 测试类型 | 测试内容 |
|------|----------|----------|
| **MinimaxService** | 单元测试 | API 请求构造、响应解析（Mock HttpClient） |
| **DJService** | 单元测试 | 情绪检测逻辑、文案拼接、系统提示词生成 |
| **AudioService** | 单元测试 | 播放列表管理（添加/删除/排序/随机） |
| **Track 模型** | 单元测试 | 序列化/反序列化、元数据解析 |
| **SettingsService** | 单元测试 | 配置加载/保存、默认值处理 |
| **RetryPolicy** | 单元测试 | 重试逻辑、退避策略 |
| **Live2D Viewer** | 集成测试 | WebView 通信、消息格式 |

### E.3 单元测试示例

```csharp
public class DJServiceTests
{
    [Fact]
    public void GenerateSystemPrompt_ShouldContainDJName()
    {
        var profile = new DJProfile { Name = "小音" };
        var service = new DJService();
        service.Initialize(profile);

        var prompt = service.BuildSystemPrompt();

        Assert.Contains("小音", prompt);
    }

    [Fact]
    public async Task DetectEmotion_ShouldReturnExcited_ForRockSong()
    {
        var service = new DJService();
        var track = new Track { Title = "摇滚之夜", Artist = "Test" };

        var emotion = await service.DetectEmotionAsync(track);

        Assert.Equal("excited", emotion);
    }
}
```

### E.4 Mock 策略

| 接口 | Mock 方式 | 说明 |
|------|-----------|------|
| `HttpClient` | Mock `HttpMessageHandler` | 模拟 Minimax API 响应 |
| `IAudioService` | Mock 实现 | 播放列表逻辑测试 |
| `ILive2DViewer` | Mock 实现 | 情绪→表情映射测试 |
| `ISecureStorage` | Mock 实现 | Windows Credential Manager 测试 |

### E.5 集成测试

| 场景 | 测试内容 |
|------|----------|
| API 集成 | Minimax API 真实调用（需要有效 API Key） |
| Live2D 集成 | WebView 加载、JS 通信 |
| 音频集成 | 实际文件播放、频谱数据获取 |

集成测试默认跳过，仅在 CI/CD 环境配置 API Key 后执行。

---

**文档版本**：v1.2
**创建日期**：2026-05-03
**最后更新**：2026-05-03
**状态**：待实施