# 歌词模式设计（ClockStage 歌词图层 + 双源取词）

日期：2026-09-11
状态：评审通过（第 1 轮：实测修正 API 判定与关键词策略、按钮位置；第 2 轮：修正按钮落点为 BrandHeader、三态契约、状态真值表、二分边界、缓存顺序/null-key 防御；均可实施）
结论来源：前期调研确认两个默认音源的本地 Node 代理已内置歌词端点（网易云 `/lyric`、酷狗 `/search/lyric` + `/lyric`），C# 侧零调用；`AudioService` 已有 500ms 粒度的 `PositionChanged` 与切歌 `TrackChanged`，逐行同步可直接复用。

## 1. 目标与非目标

**目标**
- 播放在线曲目时可以切换到一个歌词视图，按当前播放进度逐行高亮同步。
- 覆盖两个默认音源：网易云音乐、酷狗音乐。
- 歌词模式可开关并记忆，与简洁模式（compact）相互独立。

**非目标（本期不做）**
- 翻译歌词 / 罗马音 / 逐字卡拉 OK（酷狗 KRC 逐字时间戳留待后续）。
- 简洁模式（CompactPlayer）内的单行跑马灯歌词。
- YouTube 音源与本地文件的歌词（显示"暂无歌词"降级）。
- 歌词持久化到磁盘（仅会话级缓存）。

## 2. 数据链路设计

### 2.1 新增服务：`LyricService`

文件：`Services/ILyricService.cs`（接口 + 数据模型，沿用 `OnlineTrack` 放在 `IMusicSearchService.cs` 的先例）、`Services/LyricService.cs`（实现）。

```csharp
public interface ILyricService
{
    /// <summary>
    /// 按曲目当前 SourceId 取词。三态契约：
    /// null = 无词或失败（含超时/网络异常，歌词是展示增强，不抛异常、不进熔断与健康统计）；
    /// IsInstrumental=true 且 Lines 为空 = 纯音乐；
    /// Lines 非空 = 有词。
    /// </summary>
    Task<LyricResult?> GetLyricsAsync(Track track, CancellationToken cancellationToken);
}

public sealed class LyricResult
{
    public bool IsInstrumental { get; init; }
    public IReadOnlyList<LyricLine> Lines { get; init; }
}

public sealed record LyricLine(TimeSpan Time, string Text);
```

- 构造签名：`LyricService(HttpClient httpClient, string neteaseBaseUrl = "http://127.0.0.1:37250", string kugouBaseUrl = "http://127.0.0.1:37251")`，注入共享单例 `HttpClient`；基地址为可选参便于单测（注意这是新设计——现网 `KugouMusicService.ProxyBase` 是 const，仅 `NeteaseMusicService` 的 baseUrl 可注入；单测走 `DelegateHandler` 伪造，不依赖基地址注入）。
- 取词整体超时 5 秒（LinkedCTS），失败/超时记 `Log.Debug` 后返回 null——歌词缺失不影响播放主链路。
- **处理顺序**：先按 `SourceId` 前缀分发——`SourceId` 为 null（本地文件）或非 `netease:`/`kugou:` 前缀（kuwo/migu/youtube）直接返回 null 且**不入缓存**（避免 null key 使 ConcurrentDictionary 抛 ArgumentNullException）；仅两源键走缓存查找。
- 会话级缓存：`ConcurrentDictionary<string, LyricResult?>`，键为 `SourceId`；命中直接返回。缓存值包含两类负形态：null（无词/失败/超时——电台模式不反复打超时请求）与 `IsInstrumental` 对象。容量上限 128，超限整体清空（电台模式无限续播需要防泄漏，粗粒度淘汰足够）。跨源重写 `SourceId` 后键自然不同，无需失效逻辑。

### 2.2 网易云取词（单步）

`Track.SourceId` 形如 `netease:{songId}`：

```
GET {neteaseBase}/lyric?id={songId}
→ { "lrc": { "lyric": "[00:12.34]..." }, "tlyric": {...}, "nolyric": true?, "pureMusic": true?, "uncollected": true?, "code": 200 }
```

- 根级 `code` 非 200 → null。
- 取 `lrc.lyric` 非空 → `LrcParser.Parse`。
- 纯音乐判定：`nolyric == true || pureMusic == true`。实测网易云会把"纯音乐，请欣赏"直接内嵌进 lrc 文本，因此**有可解析行时照常展示**（内容本身就是"纯音乐，请欣赏"）；标志位仅在解析结果为空行时用于返回 `IsInstrumental=true` 的空行结果（见 §4 状态文案）。
- 未收录占位防御（服务层规则，对两源统一应用）：实测不存在的 id 返回 `uncollected:true` 且 `lrc.lyric` 为 `"[00:00.00]暂无歌词"`——解析后所有行文本 Trim 后均为"暂无歌词"时视为无词返回 null，避免把占位符当歌词显示。酷狗 decodeContent 同样适用该判定。与"空列表视为无词"的关系：两条规则互斥——空列表判定针对解析结果为空（含 `lrc.lyric` 空串/字段缺失），占位判定针对非空解析结果；"空列表 → null"是**服务层**语义，`LrcParser` 本身只返回列表（可为空）。

### 2.3 酷狗取词（两步 + 关键词兜底）

`Track.SourceId` 形如 `kugou:{hash}`：

```
第一步（按 hash）：
GET {kugouBase}/search/lyric?hash={hash}&duration={DurationMs}
→ { "status": 200, "candidates": [ { "id": "...", "accesskey": "...", "duration": 210000 } ] }

无候选时（status==404 且 candidates 空）按序尝试关键词：
  2a. GET {kugouBase}/search/lyric?keywords={title}&duration={DurationMs}
  2b. GET {kugouBase}/search/lyric?keywords={title artist}&duration={DurationMs}

取候选：
GET {kugouBase}/lyric?id={id}&accesskey={accesskey}&fmt=lrc&decode=1
→ { "status": 200, "content": "<base64>", "decodeContent": "[00:12.34]..." }
```

- **404 语义**：实测无候选时上游返回 `status:404` + `candidates:[]`（并非 200 + 空数组）。实现口径：`status != 200`（含 404）或候选为空均视为"无候选"，允许继续走 2a/2b 关键词兜底；HTTP 层失败（非 2xx）抛异常归 null 不触发兜底。比"仅 404 触发兜底"略宽松，最终结果一致（全部失败 → null），代码评审第 1 轮确认接受。
- **关键词顺序**：先 title-only，再 title+artist（拼接为 `title + " " + artist` 后整体 `Uri.EscapeDataString`，先例 `KugouMusicService.cs:70`）。实测中文曲库 `"晴天 周杰伦"` 等组合词一律 404，仅 title-only 命中（英文曲库组合词可命中）。
- **候选筛选**：`Track.Duration > 0` 时优先选 `|candidate.duration - DurationMs| ≤ 3000` 的第一个候选（候选 duration 为 ms；候选缺 duration 字段或非数字视为不参与匹配，不抛错），无匹配再取 candidates[0]——缓解 title-only 搜索的误配（实测"晴天"能命中歌手同名的日文曲）。
- `decode=1` 让代理直接返回已解码文本（`fmt=lrc` 走 Base64 解码，见 `server-kugou/module/lyric.js:24-31`），C# 侧不再解 KRC。
- 其余形状防御与既有服务同口径：candidates 非数组、decodeContent 缺失 → null。
- 该链路不带登录 `Authorization` 头（实测歌词接口无需登录态，代理自行注入设备身份）。

### 2.4 LRC 解析：`LrcParser`

新文件 `Services/LrcParser.cs`，internal static class：

- 行格式 `[mm:ss.xx]text` 与 `[mm:ss.xxx]text`；一行多时间戳 `[00:12.00][01:30.00]text` 展开为多行。
- 元标签 `[ti:]` `[ar:]` `[al:]` `[by:]` 丢弃；`[offset:±ms]` 应用到全部时间后丢弃。
- 过滤空文本行；结果按 Time 升序排序（多时间戳展开后必须）。
- 解析失败的行静默跳过；整体解析结果为空列表时视为"无词"。

### 2.5 其他音源

- `kuwo:` / `migu:` / `youtube:` / 本地文件（无 SourceId 前缀）→ 直接返回 null，UI 显示"暂无歌词"。接口按 SourceId 前缀分发，未知前缀不请求网络。

## 3. UI 设计（ClockStage 歌词图层）

### 3.1 开关入口

`MainWindow.axaml` **BrandHeader**（首行右侧按钮组：数字人/搜索/设置/主题，`MainWindow.axaml:90-113`）在主题按钮之后新增同款 32×30 PathIcon 图标按钮（文本行图标），`Command="{Binding ToggleLyricsModeCommand}"`，ToolTip 用新增字符串键 `S_LyricsMode`（"歌词模式" / "Lyrics mode"）。注意：窗口镶边 `TitleBar.axaml` 右侧只有简洁模式/最小化/关闭三个 30×24 按钮，不是本按钮的落点。

**不放 PlayerDeck 的原因**（评审实测计算）：PlayerDeck 网格为 `165,*,Auto`，默认宽度 760 下中列仅约 289px，第二行反馈按钮组（LIKE/NOPE/SIM/CALM/FIRE/STORY）已达 288px 贴满容量，最小宽度 700 下中列约 229px 已经溢出；再塞一个按钮必然与左列内容重叠。BrandHeader 全宽（760）充裕（左侧品牌约占 200px + 右侧 5 个 32px 按钮约 190px），且歌词模式与主题切换同属"视图偏好"，语义一致。

### 3.2 ClockStage 图层切换

`ClockStage.axaml` 现有结构是 Grid 叠层（ClockDots → Starfield → 三栏频谱/时钟）。改为：

- 三栏频谱/时钟 Grid：`IsVisible="{Binding !IsLyricsMode}"`。
- 新增歌词面板（同一 Grid 的最后一层，`IsVisible="{Binding IsLyricsMode}"`，`IsHitTestVisible="False"`）：垂直居中三行——上一句（暗）/ 当前句（亮、加粗、稍大）/ 下一句（暗），下方一行小字状态（仅在无词/加载中/纯音乐时显示）。
- Starfield 与 ClockDots 保持可见（歌词背景继续有星空呼吸感）。
- 颜色全部复用现有 token：当前句 `C_FFFFFFFF` + FontWeight 700，上下句与状态 `C_FF858594`，中文字体 `Consolas, Microsoft YaHei UI`。**不新增主题 token**（Light 主题下这些 key 已有映射）。

### 3.3 状态机与持久化

- `MainWindowViewModel.IsLyricsMode` [Reactive]，`ToggleLyricsModeCommand` 翻转并立即 `SettingsVM.ShowLyricsInStage = IsLyricsMode` + `SaveUiStateCommand`（与 `ToggleCompactMode` 同款链路）。
- `SettingsViewModel.ShowLyricsInStage` [Reactive]，JSON 键 `show_lyrics_in_stage`，load/save 各加一处；**默认 false**（时钟舞台是产品默认脸面，歌词一键可达）。
- `LoadLocalStateAsync` 恢复：`IsLyricsMode = SettingsVM.ShowLyricsInStage`。
- 与 `RadioState` 无关（那是电台业务状态机）；与 `IsCompactMode` 正交：简洁模式下无歌词位，仅标准模式有效，互不干预。

## 4. ViewModel 设计：`LyricsViewModel`

新文件 `ViewModels/LyricsViewModel.cs`，由 `MainWindowViewModel` 组合（先例：`SpectrumVM`），DI 只注册 `ILyricService`。

构造：`(IAudioService audioService, ILyricService lyricService)`。

绑定属性：
- `string PreviousLineText` / `CurrentLineText` / `NextLineText`
- `bool HasLyrics`（`UpdatePosition` 的内部门控；XAML 侧三行 TextBlock 不绑定可见性——无词时绑定值为空串、空 TextBlock 不占渲染，效果等价，少三条绑定）
- `string LyricStatusText` 与 `bool HasStatusText`（状态行可见性 = LyricStatusText 非空，VM 内联判定，不新增字符串转换器）
- 状态真值表（LyricStatusText 经 `AppLanguage.Changed` 重算，先例：`PlayerViewModel._onLanguageChanged`）：

| 场景 | HasLyrics | 三行 | LyricStatusText |
|---|---|---|---|
| null track（清空列表） | false | 空串 | 空串（面板全空） |
| 加载中 | false | 空串 | "正在获取歌词…" |
| 有词 | true | 按进度滚动 | 空串 |
| 纯音乐（IsInstrumental） | false | 空串 | "纯音乐，请欣赏" |
| 无词/失败（null） | false | 空串 | "暂无歌词" |
| Dispose 后 | 冻结 | 冻结 | 冻结（回调直接返回，不再写属性） |

行为：
- 订阅 `TrackChanged`（ObserveOn MainThreadScheduler）：`track == null`（清空列表时会上发 null）→ 按真值表首行清空；否则按"加载中"行初始化，自增 `_generation`，后台取词；完成后仅当未 Dispose、generation 未变且 `track.SourceId` 与 `_audioService.CurrentTrack?.SourceId` 一致才应用（防止快速切歌时旧词晚到覆盖新词——`SourceId` 一致性同时覆盖跨源重写：换源即换词）。本地文件/未知前缀的取词不发网络、几乎即时完成，"正在获取歌词…"至多闪现一帧后变"暂无歌词"，接受此行为不做特殊分支。
- 订阅 `PositionChanged`（同样 ObserveOn MainThreadScheduler——发射源是 AudioService 的 Timer 线程）：`idx = 最后一个 Time <= pos 的行下标`；**边界**：`idx < 0`（前奏段）→ PreviousLineText=空串、CurrentLineText=空串、NextLineText=第一行；`idx = 末行` → NextLineText=空串；`idx = 0` → PreviousLineText=空串。索引变化才更新属性（500ms 粒度足够行级同步；TTS 暂停期间 PositionChanged 停发，歌词高亮自然冻结）。
- **无条件取词**（不随 IsLyricsMode 门控）：电台模式约 4 分钟一首，多一次本地代理 GET 可忽略，换来中途开启歌词即刻可用、状态机简单。
- 自持 `CancellationTokenSource`，`Dispose` 时取消在途请求并退订全部订阅与 `AppLanguage.Changed`。

`MainWindowViewModel` 改动：
- 新属性 `public LyricsViewModel LyricsVM { get; }` 与 `[Reactive] bool IsLyricsMode`、`ToggleLyricsModeCommand`。
- 构造函数末尾追加可选参 `ILyricService? lyricService = null`（null 时自建 `LyricService(httpClient ?? new HttpClient())`，保持参数化构造与设计器构造都成立）；`App.axaml.cs` DI 注册 `ILyricService` 单例并显式传入。
- `Dispose` 增加 `LyricsVM?.Dispose()`。

## 5. 涉及文件清单

| 文件 | 动作 |
|---|---|
| `Services/ILyricService.cs` | 新增：接口 + `LyricResult`/`LyricLine` |
| `Services/LyricService.cs` | 新增：双源取词 + 缓存 + 超时 |
| `Services/LrcParser.cs` | 新增：LRC 解析 |
| `ViewModels/LyricsViewModel.cs` | 新增 |
| `ViewModels/MainWindowViewModel.cs` | LyricsVM/IsLyricsMode/命令/恢复/释放 |
| `ViewModels/SettingsViewModel.cs` | ShowLyricsInStage 属性 + load/save |
| `Views/ClockStage.axaml` | 歌词图层 + 时钟层可见性互斥 |
| `Views/MainWindow.axaml` | BrandHeader 加 LYRIC 切换按钮 |
| `Services/AppLanguage.cs` | `S_LyricsMode` 中英两表（键集奇偶由 `AppLanguageTests.StringTables_HaveIdenticalKeySetsAndNonEmptyValues` 校验，两表同步加） |
| `App.axaml.cs` | DI 注册 `ILyricService` |
| `AGENTS.md` | Recent Work 补一行；Current Direction 中"歌词暂不进入第一轮开发"改为已交付（本设计即第二轮交付物） |
| 测试工程 | 见 §6 |

## 6. 测试计划

- `LrcParserTests`：常规时间戳、毫秒三位、一行多时间戳展开、offset 应用、元标签丢弃、空行/畸形行跳过、结果排序。
- `LyricServiceTests`（DelegateHandler 伪造，先例 `KugouMusicServiceTests`）：
  - 网易云：正常取词、`pureMusic:true`（有内嵌行照常展示 / 无行走 IsInstrumental）、`uncollected:true` 且仅"暂无歌词"占位行 → null、空 lrc → null、URL 携带正确 id。
  - 酷狗：hash 有候选两步成功、hash 404 无候选 → 先 title-only 再 title+artist 的兜底顺序、组合词仍无候选 → null、候选按 duration ±3s 过滤（含无匹配回退 candidates[0]）、`fmt=lrc&decode=1` 参数正确。
  - 通用：未知 SourceId 前缀不发网络请求、缓存命中只发一次请求、网络异常 → null 不抛、取消令牌透传。
- `LyricsViewModelTests`：TrackChanged 触发取词、TrackChanged(null) 清空且不发请求、PositionChanged 推进当前行、**前奏段（pos 早于首行）与末句（pos 晚于末行）的边界显示**、切歌后旧响应被丢弃（generation）、无词/纯音乐状态文案、语言切换重算 LyricStatusText、Dispose 后不再更新（先例 `PlayerViewModelTests` 的 `RxApp.MainThreadScheduler=CurrentThreadScheduler` + Moq Subject 模式）。
- `SettingsViewModelTests` 增补：`show_lyrics_in_stage` 保存→加载回读。
- `AppLanguageTests.StringTables_HaveIdenticalKeySetsAndNonEmptyValues` 自动覆盖新字符串键（两表键集一致）。

验收标准：`dotnet build` 0 error；`dotnet test` 全绿（旧用例零回归 + 新用例全过）。

## 7. 风险与边界

- **代理未启动/启动慢**：取词 5 秒超时 → "暂无歌词"，不影响播放；缓存负结果避免电台模式反复打超时请求。
- **跨源换源**：`GetAlternativePlayUrlAsync` 重写的是 `OnlineTrack.Id`，`AudioService.ApplyTrackUrlResolution` 再回写 `track.SourceId`（`AudioService.cs:1791-1803`）；该曲此后再次 `TrackChanged`（重播/切回）时按新 SourceId 取词自然正确。同一首歌在两源的歌词内容一致，中途换源不重取也无碍。
- **行级精度**：500ms 轮询最坏半秒延迟，行级高亮无感；后续做逐字歌词时再细化轮询。
- **酷狗歌词候选误配**：关键词兜底为 title-only 时可能命中同名异曲（实测"晴天"命中歌手同名的日文曲）；`duration` 参数随请求传递 + 候选侧 ±3s 时长过滤双重缓解，仍误配时展示内容为错误歌词但不影响播放。
- **UI 线程安全**：所有 Reactive 属性仅在 MainThreadScheduler 回调中写入；取词在后台线程。
