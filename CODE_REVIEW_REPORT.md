# AIRadio.Desktop 全量代码审查报告

> 历史文档：本文记录 2026-06 当时的审查结论，其中部分问题已修复或已随架构迁移失效；当前状态以 `README.md` 和 `ai-radio-plan.md` 为准。

> 审查日期：2026-06-20
> 审查范围：全部 53 个 C# 文件 + 8 个 AXAML 视图文件 + 12 个测试文件
> 审查维度：Services 层、ViewModels 层、Views/AXAML、安全与 API、测试覆盖率

---

## 总览

| 维度 | CRITICAL | HIGH | MEDIUM | LOW |
|------|----------|------|--------|-----|
| Services 层 | 0 | 7 | 10 | 7 |
| ViewModels 层 | 1 | 5 | 7 | 4 |
| Views/AXAML | 2 | 7 | 9 | 6 |
| 安全与 API | 1 | 3 | 5 | 2 |
| 测试覆盖率 | 0 | 9 | 14 | 6 |
| **合计** | **4** | **31** | **45** | **25** |

**判定：BLOCK** — 4 个 CRITICAL 必须修复后才能提交。

---

## CRITICAL（必须立即修复）

### C1. `async void RecognizeFromWav` — 未处理异常可导致进程崩溃

- **文件：** `ViewModels/ChatViewModel.cs` 第 240 行
- **影响：** 语音识别异常可直接终止应用

**问题描述：**
`RecognizeFromWav` 声明为 `async void`，异常无法被调用方观察。虽然内部有 try/catch，但在 `await` 恢复和进入 catch 之间的异常会传播到同步上下文终止进程。

**修复方案：**
```csharp
// 改为 async Task
private async Task RecognizeFromWavAsync(string wavPath)
{
    // ... 原有逻辑 ...
}

// 调用处使用 fire-and-forget + 异常观察
_ = RecognizeFromWavAsync(wavPath).ContinueWith(
    t => Log.Warning(t.Exception, "RecognizeFromWav failed"),
    TaskContinuationOptions.OnlyOnFaulted);
```

---

### C2. 外部 API 响应使用 `GetProperty()` 而非 `TryGetProperty()` — 缺失属性直接崩溃

- **文件：**
  - `Services/KugouMusicService.cs` 第 50-53 行（`SearchAsync` 中 `SongName`、`SingerName`、`FileHash`）
  - `Services/NeteaseMusicService.cs` 第 87-93 行（`GetPlayUrlAsync` 中 `code`、`data`、`url`）
  - `Services/KuwoMusicService.cs` 第 85-87 行（`GetPlayUrlAsync` 中 `code`、`data.url`）
  - `Services/MinimaxService.cs` 第 67-68 行（`GetTtsAudioAsync` 中内层 `message.content` 未守护）
- **影响：** 音乐 API 响应结构变化时应用崩溃

**问题描述：**
`SearchAsync` 方法已正确使用 `TryGetProperty`，但同文件的其他方法使用了会抛 `KeyNotFoundException` 的 `GetProperty`。外部 API 响应不可信，结构可能变化。其中 Kugou 的 `SearchAsync` 本身（非 `GetPlayUrlAsync`）就存在此问题。

**修复方案：**
将所有 `GetProperty()` 替换为 `TryGetProperty()`，与 `SearchAsync` 保持一致：
```csharp
// 之前
var code = root.GetProperty("code").GetInt32();

// 之后
if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.Number)
    return null;
var code = codeElement.GetInt32();
```

---

### C3. 空 `catch` 吞掉所有异常 — 无法调试时钟渲染问题

- **文件：** `Views/MainWindow.axaml.cs` 第 265 行
- **影响：** 时钟渲染错误完全无迹可寻

**问题描述：**
`UpdateClock()` 方法使用 `catch { }` 静默吞掉所有异常，包括 NullReferenceException、InvalidOperationException 等。

**修复方案：**
```csharp
catch (Exception ex)
{
    Log.Debug(ex, "UpdateClock failed");
}
```

---

### C4. StarfieldView 的 DispatcherTimer 在 Unloaded 时不停止 — 资源泄漏

- **文件：** `Views/StarfieldView.axaml.cs` 第 82 行
- **影响：** 控件移除后 Timer 仍以 ~30fps 运行，访问失效状态

**问题描述：**
`DispatcherTimer` 在 `StartAnimation()` 中创建，但没有 `Unloaded` 处理程序。控件从可视树移除后，Timer 继续触发 `OnTick`。

**修复方案：**
```csharp
public StarfieldView()
{
    // ... 现有代码 ...
    Unloaded += (_, _) =>
    {
        _timer?.Stop();
        _initialized = false;
    };
}
```

---

## HIGH（应在本次迭代修复）

### Services 层

#### H1. `AudioService.Next()/Previous()` 是 `async void`

- **文件：** `Services/AudioService.cs` 第 300、336 行
- **影响：** 回调异常可崩溃应用

**修复：** 修改 `IAudioService` 接口返回 `Task`，或在方法内包裹 try/catch。

#### H2. `PlayTrack` 不释放旧 `Media` 对象

- **文件：** `Services/AudioService.cs` 第 400-402 行
- **影响：** 长时间使用可能泄漏原生资源

**说明：** 代码注释明确表示这是有意为之的设计决策（`// do NOT dispose old media here, LibVLC may still be using it internally during cleanup`），直接 dispose 会导致 LibVLC 内部崩溃。但长时间播放仍可能导致原生句柄累积。

**修复：** 延迟释放旧 Media，确认 LibVLC 不再引用后 dispose：
```csharp
var oldMedia = _player.Media;
_player.Stop();
_player.Media = newMedia;
// Delayed dispose after LibVLC finishes internal cleanup
Task.Delay(1000).ContinueWith(_ => oldMedia?.Dispose());
```

#### H3. `DoFadeStep` 从线程池线程调用 `Next()` 无同步

- **文件：** `Services/AudioService.cs` 第 583-586 行
- **影响：** 并发访问损坏播放列表状态

**修复：** 将自动切歌调用调度到 UI 线程：
```csharp
Dispatcher.UIThread.Post(() => Next());
```

#### H4. `DJService._chatHistory` 无限增长

- **文件：** `Services/DJService.cs` 第 17 行
- **影响：** 长会话内存持续增长，API 请求越来越大

**修复：**
```csharp
const int MaxHistoryMessages = 20;
while (_chatHistory.Count > MaxHistoryMessages + 1)
    _chatHistory.RemoveAt(1);
```

#### H5. `MinimaxService._apiKey` 非线程安全

- **文件：** `Services/MinimaxService.cs` 第 18 行
- **影响：** 并发读写可能读到中间状态

**修复：** 方法开头复制到局部变量：
```csharp
var key = _apiKey;
if (string.IsNullOrEmpty(key)) return ApiFailureInfo.MissingApiKey();
```

#### H6. `RecommendationService._feedback` 无限增长

- **文件：** `Services/RecommendationService.cs` 第 15 行
- **影响：** 同 H4

**修复：** 同 H4，添加容量上限。

#### H7. `WhisperSttService` 循环中字符串拼接

- **文件：** `Services/WhisperSttService.cs` 第 73-77 行
- **影响：** O(n²) 性能，长语音转写时明显

**修复：**
```csharp
var sb = new StringBuilder();
await foreach (var segment in segments)
    sb.Append(segment.Text);
var result = sb.ToString().Trim();
```

---

### ViewModels 层

#### H8. 7 处 `fire-and-forget _ = SaveAsync()` 无错误观察

- **文件：** `ViewModels/PlaylistViewModel.cs` 第 66、73、140、167、381、391、419 行
- **影响：** 保存失败时无任何反馈，可能丢失数据

**修复：** 添加异常观察：
```csharp
_ = SaveAsync().ContinueWith(
    t => Log.Warning(t.Exception, "SaveAsync failed"),
    TaskContinuationOptions.OnlyOnFaulted);
```

#### H9. `PlaylistViewModel` / `SettingsViewModel` 未实现 `IDisposable`

- **文件：** `ViewModels/PlaylistViewModel.cs` 第 19 行、`ViewModels/SettingsViewModel.cs` 第 26 行
- **影响：** WhenAnyValue 订阅和 SemaphoreSlim 泄漏

**修复：** 实现 `IDisposable`，在 `MainWindowViewModel.Dispose()` 中添加释放调用。

#### H10. 三处不一致的曲目比较逻辑

- **文件：**
  - `ViewModels/MainWindowViewModel.cs` 第 557 行 (`IsSameTrack`)
  - `ViewModels/MainWindowViewModel.cs` 第 570 行 (`IsSameTrackIdentity`)
  - `ViewModels/ChatViewModel.cs` 第 881 行 (`IsSameTrack`)
- **影响：** 同一对曲目在不同上下文判断结果不同

**修复：** 提取为共享的 `TrackComparer` 工具类。

#### H11. `Subscribe(async => ...)` 异步 lambda 静默丢失异常

- **文件：** `ViewModels/ChatViewModel.cs` 第 87-100 行
- **影响：** TTS 状态变化处理中的异常完全丢失

**修复：** 使用 `SelectMany` 正确链接异步操作：
```csharp
_ttsSub = _audioService.TtsStateChanged
    .ObserveOn(RxApp.MainThreadScheduler)
    .Where(playing => !playing && _pendingCommand != null)
    .Select(playing => { var cmd = _pendingCommand; _pendingCommand = null; return cmd; })
    .SelectMany(cmd => Observable.FromAsync(() => DisposeCommandAsync(cmd!)))
    .Subscribe();
```

#### H12. `WaveFileWriter` / `_waveIn` 生命周期竞态

- **文件：** `ViewModels/ChatViewModel.cs` 第 183-200 行
- **影响：** Dispose 时可能访问已删除的临时文件

**修复：** 将 `WaveFileWriter` 存为字段，用 `CancellationTokenSource` 协调关闭。

---

### Views / AXAML

#### H13. 用 `MaxWidth == 380` 魔法数字识别消息气泡

- **文件：** `Views/MainWindow.axaml.cs` 第 159-175 行
- **影响：** AXAML 中 MaxWidth 值变化时主题静默失效

**修复：** 用附加属性或样式类标记消息气泡，而非魔法数字匹配。

#### H14. 功能等价的 Bool 反转 Converter 重复定义

- **文件：** `Views/MainWindow.axaml.cs` 第 32 行（`InverseBoolConverter`）、`Views/PlaylistView.axaml.cs` 第 67 行（`InvertBoolValueConverter`）
- **说明：** 类名不同，`ConvertBack` 默认值行为略有差异（一个返回 `false`，一个返回 `value`），但核心功能等价

**修复：** 统一为一个共享 Converter，放到 `Converters/` 目录。

#### H15. `MessageAlignConverter` 逻辑重复

- **文件：** `Views/MainWindow.axaml.cs` 第 20 行、`Views/ChatView.axaml.cs` 第 67 行

**修复：** 同 H14，提取共享 Converter。

#### H16. `MainWindow.axaml.cs` 467 行 — 大量 ViewModel 逻辑混入代码后台

- **文件：** `Views/MainWindow.axaml.cs`（整个文件）
- **影响：** 时钟、主题、文件选择器、麦克风状态管理都在代码后台

**修复：** 逐步将逻辑迁移到 ViewModel 或服务层。

#### H17. `ChatView.axaml.cs` 的 `CollectionChanged` 订阅未取消

- **文件：** `Views/ChatView.axaml.cs` 第 27-38 行
- **影响：** DataContext 变化时旧 ViewModel 无法被 GC

**修复：** 存储 handler，在 DataContext 变化或 Unloaded 时取消订阅。

#### H18. 每次 Converter 转换都 `new SolidColorBrush`

- **文件：** `Views/ChatView.axaml.cs` 第 51-117 行（3 个 Converter）
- **影响：** 频繁聊天消息产生不必要的 GC 压力

**修复：** 缓存为 `static readonly` 字段（参考 `PlayerView.axaml.cs` 已有正确做法）。

#### H19. `MainWindow.axaml` 657 行

- **文件：** `Views/MainWindow.axaml`
- **影响：** 难以维护和定位 UI 元素

**修复：** 拆分为独立的 UserControl：TitleBar、ClockStage、PlayerDeck、ChatArea 等。

---

### 安全与 API

#### H20. 酷我音乐 API 使用 HTTP

- **文件：** `Services/KuwoMusicService.cs` 第 27、74 行
- **影响：** 搜索关键词和播放 URL 明文传输

**修复：** 改为 `https://www.kuwo.cn/...`。

#### H21. 播放 URL 无任何验证

- **文件：** 所有音乐服务 + `Services/AudioService.cs` 第 411-429 行
- **影响：** 恶意 API 响应可返回任意 URL

**修复：** 验证 URL scheme、host 是否在预期的音乐 CDN 域名列表中。

#### H22. `trackId` 未转义直接拼入 URL

- **文件：** `Services/NeteaseMusicService.cs` 第 82 行
- **影响：** 注入 URL 参数

**修复：** 使用 `Uri.EscapeDataString(trackId)`。

---

### 测试覆盖率

#### H23. 永真断言 `Assert.True(ttsEnded || true)`

- **文件：** `ChatViewModelTests.cs` 第 257 行

**修复：** 删除 `|| true`，改为有意义的断言。

#### H24. `MusicServiceTests` 全部仅 `Assert.NotNull`

- **文件：** `MusicServiceTests.cs`

**修复：** 添加对结果数量、字段完整性的断言，或标记为 `[Trait("Category", "Integration")]`。

#### H25. `ParseResponse_StripsEmotionTags` 测试的是 `string.Contains`

- **文件：** `ChatViewModelTests.cs` 第 89 行

**修复：** 调用实际的 `ParseDjResponse` 方法并验证标签被移除。

#### H26. `SpectrumViewModel` 零测试覆盖

**修复：** 添加构造初始化、数据转发、Dispose 清理的测试。

#### H27. `WhisperSttService` 零测试覆盖

**修复：** 添加模型缺失、空 WAV、异常处理的测试。

#### H28. `ApiFailureInfo.FromException/FromStatusCode` 零覆盖

**修复：** 这是纯确定性逻辑，最适合单元测试。覆盖所有异常类型和状态码映射。

#### H29. `SendMessageAsync` 错误路径零测试

**修复：** 测试 DJ 异常、空搜索结果、null 播放 URL 等场景。

#### H30. 语音输入流程零测试

**修复：** 测试 `ToggleVoiceInput`、`BeginHoldToTalk`、`EndHoldToTalk`、`RecognizeFromWav`。

#### H31. 测试中全局修改 `RxApp.MainThreadScheduler` 不恢复

- **文件：** `MainWindowViewModelTests.cs`

**修复：** 在 finally 块中恢复原值。

---

## MEDIUM（建议修复）

### Services 层（10 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| M1 | AudioService 775 行，超出 800 行限制 | AudioService.cs | 拆分为 TtsPlaybackManager、CrossfadeController、PlaylistManager |
| M2 | PlayTtsAudio 超 50 行 | AudioService.cs:663 | 提取临时文件创建和 NAudio 设置为辅助方法 |
| M3 | RecommendNextTrackAsync 超 50 行，4 层嵌套 | DJService.cs:146 | 拆分为 AI 提示生成、搜索过滤、URL 解析三个方法 |
| M4 | DJService 与 RecommendationService 重复的音乐身份逻辑 | DJService.cs:283, RecommendationService.cs:237 | 提取为共享 `MusicIdentityComparer` 工具类 |
| M5 | MusicApiServer.KillProcessOnPort 空 catch | MusicApiServer.cs:163 | 至少 Debug 级别日志 |
| M6 | EnvironmentManager 空 catch 吞掉 Node.js 检测异常 | EnvironmentManager.cs:46 | 改为 `catch (Exception ex)` 并 Debug 日志 |
| M7 | Node.js ZIP 全量下载到内存（~30MB） | EnvironmentManager.cs:72 | 改用 `GetStreamAsync` + FileStream |
| M8 | MultiSourceMusicService 空 catch | MultiSourceMusicService.cs:75 | 添加 Warning 级别日志 |
| M9 | AudioService Subject 订阅未释放 | AudioService.cs:125,157 | 存储订阅并在 Dispose 中释放 |
| M10 | MusicApiServer 创建临时 HttpClient | MusicApiServer.cs:81 | 注入单例 HttpClient（启动时仅调用一次，风险低） |

### ViewModels 层（7 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| M11 | Track 对象就地修改违反不可变性 | PlaylistViewModel.cs:130,136, MainWindowViewModel.cs:523 | 创建新 Track 实例（如果 Track 是 record 用 `with`） |
| M12 | HandleAutoRadioTrackEndedAsync 86 行需拆分 | MainWindowViewModel.cs:415 | 提取为 TryFreshRadioRecommendation、TryPlaylistRotation、PlayWithDjIntro |
| M13 | LoadAsync 顺序刷新在线 URL 导致启动慢 | PlaylistViewModel.cs:198 | 使用 `Task.WhenAll` 并行刷新 |
| M14 | ChatViewModel 空 catch 吞文件删除异常 | ChatViewModel.cs:282,920 | 添加 `Log.Debug` |
| M15 | ParseJsonCommand 空 catch 应只捕获 JsonException | ChatViewModel.cs:580 | 改为 `catch (JsonException)` |
| M16 | 构造函数创建 HttpClient 未释放 | MainWindowViewModel.cs:258 | 使用单例 HttpClient |
| M17 | SettingsViewModel 的 SemaphoreSlim 未释放 | SettingsViewModel.cs:31 | 实现 IDisposable |

### Views / AXAML（9 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| M18 | 无 AutomationProperties 无障碍属性 | 所有 .axaml | 为所有交互控件添加 `AutomationProperties.Name` |
| M19 | 无 TabIndex 键盘导航 | 所有 .axaml | 设置 `TabIndex` 和 `KeyboardNavigation` |
| M20 | 全局硬编码颜色无 ResourceDictionary | 所有 .axaml | 创建 `ResourceDictionary` 集中管理主题色 |
| M21 | OnImportFiles 重复逻辑 | MainWindow.axaml.cs:365, PlaylistView.axaml.cs:32 | 提取为共享服务或 ViewModel 命令 |
| M22 | MainWindow.axaml.cs 未正确实现 IDisposable | MainWindow.axaml.cs:441 | 实现接口，添加 GC.SuppressFinalize |
| M23 | SetShellTextForeground 遍历整个视觉树 | MainWindow.axaml.cs:152 | 用附加属性标记需主题化的元素 |
| M24 | SpectrumView 隐藏时 Timer 仍运行 | SpectrumView.axaml.cs:15 | 添加 Unloaded 处理停止数据接收 |
| M25 | 时钟用 x:Name 而非绑定 | MainWindow.axaml:148 | 改为 ViewModel 属性绑定 |
| M26 | ListBox SelectionMode 不一致 | PlaylistView.axaml:38,58,82 | 统一：要么都绑定 SelectedItem，要么移除 SelectionMode |

### 安全与 API（5 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| M27 | 多处静默吞异常，调试困难 | MultiSourceMusicService.cs:75 等 | 至少 Debug 级别日志 |
| M28 | Whisper 日志记录用户语音全文 | WhisperSttService.cs:80 | 改为记录长度或截断哈希 |
| M29 | API 错误原始响应体暴露给用户 | ApiFailureInfo.cs:77 | 对 body 做脱敏处理 |
| M30 | Music API Server 绑定 0.0.0.0 | server/start.js:6 | 设置 `process.env.HOST = '127.0.0.1'` |
| M31 | HttpClient 无超时配置 | App.axaml.cs:103 | 设置 `Timeout = TimeSpan.FromSeconds(30)` |

### 测试覆盖率（14 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| M32 | RetryPolicy 零覆盖 | RetryPolicy.cs | 测试重试次数、指数退避、首次成功 |
| M33 | WindowsSecureStorage 零覆盖 | WindowsSecureStorage.cs | 测试保存/读取/删除周期 |
| M34 | EnvironmentManager 零覆盖 | EnvironmentManager.cs | 测试检测逻辑和回退 |
| M35 | PlayerViewModel.DraggingState 测试无断言 | PlayerViewModelTests.cs:33 | 断言 IsDragging 和 DisplaySeconds |
| M36 | PlayerViewModel.NextPrevious 测试仅验证不抛异常 | PlayerViewModelTests.cs:69 | 断言曲目索引变化 |
| M37 | PlayAtIndex 测试无状态断言 | AudioServiceTests.cs:121 | 断言 CurrentTrack 保持有效 |
| M38 | TestConnection 测试仅验证命令存在 | SettingsViewModelTests.cs:99 | 测试空 key、连接成功/失败 |
| M39 | Volume 断言过松 | AudioServiceTests.cs:109 | 改为 `Assert.Equal(1.0f, svc.Volume)` |
| M40 | DJService chat history 累积未测试 | DJServiceTests.cs | 验证两次调用后历史消息数 |
| M41 | RecommendationService 空搜索结果未测试 | RecommendationServiceTests.cs | 验证返回零曲目节目 |
| M42 | PlaylistViewModel 异常 JSON 未测试 | PlaylistViewModelTests.cs | 验证损坏文件的优雅处理 |
| M43 | Mock 未配置 ISttService | ChatViewModelTests.cs:33 | 根据需要配置或验证不调用 |
| M44 | 测试用真实 AudioService 不隔离 | ChatViewModelTests.cs:229, MainWindowViewModelTests.cs:46 | 改用 Mock |
| M45 | 临时目录不清理 | PlaylistViewModelTests.cs:37 | 测试后删除临时目录 |

---

## LOW（可选优化）

### Services 层（7 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| L1 | Kuwo 硬编码 CSRF token 和 cookie | KuwoMusicService.cs:30 | 添加注释说明或动态获取 |
| L2 | Kugou 硬编码 appid=1014, platid=4 | KugouMusicService.cs:82 | 提取为常量并添加注释 |
| L3 | Minimax 硬编码模型名和 TTS 参数 | MinimaxService.cs:42,81 | 提取为配置常量 |
| L4 | MusicApiServer 硬编码端口和超时 | MusicApiServer.cs:19,82 | 提取为常量 |
| L5 | Whisper 硬编码语言 "zh" | WhisperSttService.cs:66 | 接受语言参数或从配置读取 |
| L6 | DJService ValidEmotions 三处重复 | DJService.cs:201 | 统一为单一数据源 |
| L7 | NeteaseMusicService Source 命名不一致 | NeteaseMusicService.cs:65 | 统一为中文或英文 |

### ViewModels 层（4 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| L8 | VoiceOption 应用 record | SettingsViewModel.cs:21 | `public record VoiceOption(string Id, string DisplayName)` |
| L9 | PickDiversifiedTrack 应用 Random.Shared | MainWindowViewModel.cs:513 | 替换 `new Random()` 为 `Random.Shared` |
| L10 | PlaylistData/PlaylistTrack 可变 setter | PlaylistViewModel.cs:435 | DTO 可接受，但注意 JSON 序列化 |
| L11 | FindIndex 扩展方法重复实现 | PlaylistViewModel.cs:423 | 考虑用 LINQ `.ToList()` 后的标准方法 |

### Views / AXAML（6 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| L12 | 中英文混用无本地化 | 所有 .axaml | 引入资源文件本地化框架 |
| L13 | StarCount 魔法数字 55 | StarfieldView.axaml.cs:19 | 提取为命名常量 |
| L14 | ClockDisplay 硬编码占位文本 | MainWindow.axaml:156 | 移除或改为空字符串 |
| L15 | OnSearchTextChanged 空方法 | MainWindow.axaml.cs:360 | 删除空方法和 AXAML 绑定 |
| L16 | Converter 通过代码后台注册 | ChatView.axaml.cs | 移到 XAML Resources |
| L17 | ListBox SelectionMode 无对应绑定 | PlaylistView.axaml:38,58 | 移除不需要的 SelectionMode |

### 安全与 API（2 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| L18 | Node.js 下载无完整性校验 | EnvironmentManager.cs:66 | 下载后校验 SHA256 |
| L19 | 设置文件明文存储 | SettingsViewModel.cs:247 | 桌面应用可接受，记录即可 |

### 测试覆盖率（6 项）

| # | 问题 | 位置 | 修复建议 |
|---|------|------|----------|
| L20 | HttpClient 未释放 | MusicServiceTests.cs:12 | 实现 IDisposable 或用 using |
| L21 | MusicServiceTests 仅集成测试 | MusicServiceTests.cs | 添加 Mock 单元测试 |
| L22 | 未知 role 的 SenderName 未测试 | ModelCleanupTests.cs | 测试 default 分支 |
| L23 | 多种边界用例未覆盖 | 各测试文件 | 长输入、特殊字符、混合语言 |
| L24 | Mock 未配置所有接口成员 | MainWindowViewModelTests.cs:27 | 补充 IsPlaying、Volume 等 |
| L25 | PlayerViewModel 测试用真实 AudioService | PlayerViewModelTests.cs | 改用 Mock |

---

## 优先修复路线图

### 第一批（立即修复，预计 1-2 小时）

1. **C1** — `async void RecognizeFromWav` → 10 分钟
2. **C2** — `GetProperty` → `TryGetProperty` → 10 分钟
3. **C3** — 空 catch 加日志 → 5 分钟
4. **C4** — StarfieldView Timer 泄漏 → 5 分钟
5. **H1** — `async void` → `async Task` → 15 分钟
6. **H23** — 永真断言修复 → 2 分钟

### 第二批（本周修复，预计 3-4 小时）

7. **H4/H6** — 无界列表增长（chatHistory、feedback）
8. **H7** — StringBuilder 替换字符串拼接
9. **H8** — fire-and-forget 添加异常观察
10. **H9** — 实现 IDisposable
11. **H11** — async lambda 用 SelectMany 替换
12. **H17** — CollectionChanged 订阅泄漏
13. **H18** — Converter 缓存 SolidColorBrush
14. **H20** — HTTP → HTTPS

### 第三批（后续迭代）

15. **H10** — 提取统一 TrackComparer
16. **H13-H19** — Views 重构（Converter 合并、代码后台瘦身、AXAML 拆分）
17. **H21-H22** — URL 验证
18. **H26-H31** — 补充测试覆盖
19. **M 项** — 逐项处理
20. **L 项** — 按需处理

---

## 2026-08 后续修复进度

子项目 1（视图重构 + 状态机）+ 子项目 2-5 已落地，对应审查项状态更新：

- **C1/C2/C3/C4（CRITICAL）**：均已修复（async Task、TryGetProperty、catch 日志、StarfieldView Unloaded）。
- **H1-H7（Services HIGH）**：H1（async void→Task）、H4/H6（无界列表上限）、H5（Minimax 已迁移）、H7（StringBuilder）已修。
- **H8-H19（VM/Views HIGH）**：H8/H9（异常观察 + IDisposable）、H10（TrackComparer 提取）、H11（async lambda）、H13-H19（Converter 合并到 `Converters/`、MainWindow 拆 7 UserControl、x:Name→VM 绑定）已落地。
- **H20-H22（API 安全）**：H20（Kuwo HTTPS）、H21（URL 验证部分）、H22（EscapeDataString）。
- **H23-H31（测试）**：Converter 等价性、RadioState 派生、HasFailure 等已补。
- **M20（Theme token）**：ThemeDictionaries PoC 落地，全量 token 待随 Light 模式统一。
- **L18（Node SHA256）**：已加下载哈希审计日志。

**已知残留**：MainWindow.Theme.cs 命令式 theming 待随全量 token 退场；Light 模式部分 UserControl 颜色待统一；音源 fallback UI 与连接诊断（P2 增强）后续迭代。
