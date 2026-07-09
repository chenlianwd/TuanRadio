# AIRadio 架构重构设计文档

> 历史文档：本文记录 2026-06 架构迁移方案，当前状态以 `README.md` 和 `ai-radio-plan.md` 为准。

> 日期：2026-06-28
> 目标：替换 Minimax 为 Edge TTS + 可选 LLM，新增 YouTube 音乐源

---

## 1. 背景与目标

### 当前问题
- Minimax 订阅到期，AI 对话和语音合成功能不可用
- 现有四源音乐 API（网易/酷狗/酷我/咪咕）曲库覆盖不足，很多歌曲搜不到
- `IMinimaxService` 将聊天和 TTS 耦合在一起，难以独立替换

### 重构目标
1. **TTS 替换为 Edge TTS**：免费、中文效果好、无需 API Key
2. **LLM 可选**：支持 OpenAI/Claude/本地模型，用户可配置
3. **新增 YouTube 音乐源**：通过 yt-dlp 获取音频流，作为现有四源的兜底
4. **保持电台/DJ 功能完整**：AI 对话、语音播报、自动推荐均保留

---

## 2. 接口重构

### 2.1 拆分 IMinimaxService

**当前：**
```csharp
public interface IMinimaxService
{
    void SetApiKey(string apiKey);
    Task<string> ChatAsync(string userMessage, List<ChatMessage> history);
    Task<byte[]> TextToSpeechAsync(string text, string voiceId, string emotion);
    Task<DJScript?> GenerateTrackIntroductionAsync(Track current, Track next);
}
```

**改为：**
```csharp
// 纯聊天接口，可接任意 LLM
public interface ILLMService
{
    void Configure(LLMConfig config);
    Task<string> ChatAsync(string userMessage, List<ChatMessage> history);
    Task<string> GenerateTrackIntroductionAsync(Track current, Track next);
}

// 纯 TTS 接口，当前用 Edge TTS
public interface ITtsService
{
    Task<byte[]> SynthesizeAsync(string text, string voiceId, string emotion);
    Task<IReadOnlyList<VoiceOption>> GetVoicesAsync();
}

public record LLMConfig
{
    public string Provider { get; init; } = "openai"; // "openai", "claude", "ollama", "none"
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = ""; // 自定义端点（Ollama 等）
    public string Model { get; init; } = "gpt-4o-mini";
}
```

### 2.2 新增 YouTube 音乐源

```csharp
// 实现现有 IMusicSearchService 接口
public class YouTubeMusicService : IMusicSearchService
{
    // 通过 yt-dlp 命令行获取：
    // 1. 搜索：yt-dlp "ytsearch10:song name" --get-id --get-title
    // 2. 音频URL：yt-dlp -f ba --get-url <video_url>
}
```

---

## 3. 实现方案

### 3.1 Edge TTS 实现

**方案：使用 MsEdgeTts NuGet 包**

Edge TTS 使用微软的免费 TTS 服务。已有成熟的 C# 封装库：
- NuGet 包：`MsEdgeTts`（开源，MIT 协议）
- 支持中文语音：`zh-CN-XiaoxiaoNeural`（女声）、`zh-CN-YunxiNeural`（男声）等
- 支持 SSML 情感标签
- 返回 MP3 格式音频
- 无需 API Key，通过 WebSocket 连接微软服务

**核心流程：**
```
文本 + 语音ID → MsEdgeTtsClient.SynthesizeAsync() → byte[]
```

**实现步骤：**
1. 安装 NuGet 包：`dotnet add package MsEdgeTts`
2. 创建 `EdgeTtsService` 实现 `ITtsService`
3. 使用 `MsEdgeTtsClient` 的 `SynthesizeAsync` 方法
4. 实现情感映射（`happy` → SSML style `cheerful` 等）
5. 缓存常用语音（可选）

**文件结构：**
```
Services/
├── EdgeTtsService.cs          # ITtsService 实现
├── EdgeTtsVoiceCache.cs       # 语音缓存（可选）
```

### 3.2 LLM 服务实现

**方案：统一 OpenAI 兼容接口**

大多数 LLM 提供商都兼容 OpenAI API 格式：
- OpenAI：`https://api.openai.com/v1/chat/completions`
- Claude（通过 OpenAI 兼容层）：`https://api.anthropic.com/v1/messages`
- Ollama：`http://localhost:11434/v1/chat/completions`
- DeepSeek：`https://api.deepseek.com/v1/chat/completions`

**实现步骤：**
1. 创建 `OpenAICompatibleLLMService` 实现 `ILLMService`
2. 通过 `BaseUrl` 和 `Model` 配置不同提供商
3. 保持现有的 system prompt 和对话历史管理
4. `GenerateTrackIntroductionAsync` 逻辑从 `MinimaxService` 迁移

**文件结构：**
```
Services/
├── LLMService.cs              # ILLMService 实现（OpenAI 兼容）
├── LLMProviders.cs            # 提供商配置预设
```

### 3.3 YouTube 音乐源实现

**方案：通过 yt-dlp 命令行获取音频流**

yt-dlp 是成熟的开源工具，支持从 YouTube 提取音频：
- 搜索：`yt-dlp "ytsearch10:歌曲名" --print id --print title --print duration_string --no-download`
- 获取音频 URL：`yt-dlp -f ba --get-url "https://www.youtube.com/watch?v=VIDEO_ID"`

**实现步骤：**
1. 创建 `YouTubeMusicService` 实现 `IMusicSearchService`
2. 搜索时调用 `yt-dlp ytsearch` 获取结果列表
3. 获取播放 URL 时调用 `yt-dlp -f ba --get-url`
4. 将结果转换为 `OnlineTrack` 格式
5. 修改 `MultiSourceMusicService` 构造函数，接受可选的额外源列表：
   ```csharp
   public MultiSourceMusicService(HttpClient httpClient, params IMusicSearchService[] extraSources)
   {
       _sources = new List<IMusicSearchService>
       {
           new NeteaseMusicService(httpClient),
           new KuwoMusicService(httpClient),
           new KugouMusicService(httpClient),
           new MiguMusicService(httpClient)
       };
       _sources.AddRange(extraSources); // YouTube 作为最低优先级
   }
   ```

**依赖：**
- yt-dlp 可执行文件（随应用分发或自动下载，类似 Node.js 的处理方式）

**文件结构：**
```
Services/
├── YouTubeMusicService.cs     # IMusicSearchService 实现
├── YtdlpManager.cs            # yt-dlp 可执行文件管理（下载/更新）
```

---

## 4. 依赖关系变更

### 4.1 DI 注册变更

**当前 App.axaml.cs：**
```csharp
services.AddSingleton<IMinimaxService, MinimaxService>();
services.AddSingleton<IDJService>(sp =>
    new DJService(sp.GetRequiredService<IMinimaxService>(), ...));
```

**改为：**
```csharp
// LLM 服务
services.AddSingleton<ILLMService, LLMService>();

// TTS 服务
services.AddSingleton<ITtsService, EdgeTtsService>();

// 音乐源（新增 YouTube 作为最低优先级兜底）
services.AddSingleton<IMusicSearchService>(sp =>
{
    var http = sp.GetRequiredService<HttpClient>();
    var yt = new YouTubeMusicService(YtdlpManager.GetYtdlpPath());
    return new MultiSourceMusicService(http, yt);
});

// DJ 服务
services.AddSingleton<IDJService>(sp =>
    new DJService(
        sp.GetRequiredService<ILLMService>(),
        sp.GetRequiredService<ITtsService>(),
        sp.GetRequiredService<IMusicSearchService>()));
```

### 4.2 DJService 构造函数变更

**当前：**
```csharp
public DJService(IMinimaxService minimax, IMusicSearchService? musicSearch = null)
```

**改为：**
```csharp
public DJService(ILLMService llm, ITtsService tts, IMusicSearchService? musicSearch = null)
```

### 4.3 SettingsViewModel 变更

**当前：** 设置页配置 Minimax API Key
**改为：** 设置页配置 LLM 提供商、API Key、Base URL、Model、TTS 语音选择

---

## 5. 文件变更清单

### 新增文件
| 文件 | 说明 |
|------|------|
| `Services/LLMService.cs` | ILLMService 实现 |
| `Services/EdgeTtsService.cs` | ITtsService 实现 |
| `Services/YouTubeMusicService.cs` | YouTube 音乐源 |
| `Services/YtdlpManager.cs` | yt-dlp 可执行文件管理 |

### 修改文件
| 文件 | 变更 |
|------|------|
| `Services/IMinimaxService.cs` | 拆分为 `ILLMService.cs` + `ITtsService.cs` |
| `Services/DJService.cs` | 构造函数改用 ILLMService + ITtsService |
| `Services/RecommendationService.cs` | 改用 ILLMService |
| `ViewModels/SettingsViewModel.cs` | 设置项改为 LLM/TTS 配置 |
| `ViewModels/MainWindowViewModel.cs` | DI 注册变更 |
| `App.axaml.cs` | DI 注册变更 |
| `Views/SettingsView.axaml` | UI 更新 |
| `Models/Track.cs` | LLMConfig 替代 MinimaxApiKey |

### 删除文件
| 文件 | 说明 |
|------|------|
| `Services/MinimaxService.cs` | 被 LLMService 替代 |
| `Services/IMinimaxService.cs` | 被 ILLMService + ITtsService 替代 |

---

## 6. 兼容性考虑

### 6.1 API Key 迁移
- 现有用户的 Minimax API Key 存储在 Windows Credential Manager
- 如果用户有 OpenAI API Key，可直接复用
- 首次启动时检测并提示用户配置 LLM

### 6.2 语音切换
- Edge TTS 无需 API Key，开箱即用
- 提供中文语音列表供用户选择
- 保留情感标签支持（通过 SSML express-as）

### 6.3 离线模式
- 如果用户未配置 LLM API Key，电台功能降级为纯音乐播放
- TTS 始终可用（Edge TTS 免费）
- 音乐搜索和播放不受影响

---

## 7. 实施计划

### 阶段 1：Edge TTS 替换 Minimax TTS
- 实现 `EdgeTtsService`
- 修改 `DJService` 使用 `ITtsService`
- 移除 Minimax TTS 依赖
- **验证：** DJ 语音播报正常工作

### 阶段 2：LLM 服务替换
- 实现 `LLMService`（OpenAI 兼容）
- 修改 `DJService` 和 `RecommendationService` 使用 `ILLMService`
- 更新设置页面
- **验证：** DJ 对话和歌曲推荐正常工作

### 阶段 3：YouTube 音乐源
- 实现 `YouTubeMusicService`
- 实现 `YtdlpManager`（自动下载 yt-dlp）
- 集成到 `MultiSourceMusicService`
- **验证：** 搜索不到的歌曲自动走 YouTube

### 阶段 4：清理和测试
- 删除 Minimax 相关代码
- 更新测试
- 全量回归测试
