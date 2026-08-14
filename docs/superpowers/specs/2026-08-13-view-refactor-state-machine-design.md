# 子项目 1：视图重构 + 统一状态机 — 设计文档

> 日期：2026-08-13
> 状态：待用户定稿
> 关联：本轮「补齐当前形态」共 5 个子项目，本文件是其中的第 1 个。
> 本文档遵循 `AGENTS.md` 的变更原则：小而可验证、优先沿用现有 ViewModel/服务边界。

---

## 1. 背景与动机

`MainWindow.axaml` 是一个 668 行的单体 Window，把本应分离的 UI 全部内联；`MainWindow.axaml.cs`（含 `MainWindow.Theme.cs`）是混入 9 个关注点的 god-class。与此同时，`ChatView` / `PlayerView` / `PlaylistView` 三个完整 UserControl **已经存在却被绕过** —— MainWindow 把它们的 UI 重写了一遍。状态散落在各 VM 的独立 `[Reactive]` 布尔里，没有统一的状态语义。

**本子项目要解决：**
- 视图单体过大、code-behind 混杂，阻碍后续 P2 产品化（节目单 UI、推荐理由层）。
- 状态散落、无显式语义，无法统一呈现「电台在做什么」。
- Converter 大量重复（18 个跨 5 文件）、Theme 用 `MaxWidth==380` 嗅探气泡（脆弱）。

## 2. 目标与非目标

### 目标
1. MainWindow 拆为独立 UserControl（采用已有 3 个 + 新建 5 个）。
2. 引入统一 `RadioState` 状态机（派生投影方案），StatusBar 绑定呈现。
3. 合并重复 Converter 到 `Converters/`。
4. Theme 硬编码颜色 token 化（Light/Dark），消除命令式涂色和气泡嗅探。
5. code-behind 逻辑迁移到 VM/服务/子控件；MainWindow.axaml.cs 只保留窗口固有职责。

### 非目标（本轮不做）
- 节目单 UI 产品化、推荐理由层 → 子项目 2。
- 真实 FFT → 子项目 3。
- SongStory → 子项目 4。
- 音源 fallback UI、连接诊断、孤儿类型清理、审查文档更新 → 子项目 5。
- 引入 `IRadioStateMachine` 服务（单一真相源）—— 风险过高，留待后续评估。
- i18n 本地化框架、无障碍 AutomationProperties 全覆盖 —— 列为后续技术债，不在本轮。

## 3. 关键决策（含工程判断）

| 决策 | 选择 | 理由 |
|---|---|---|
| 状态机形态 | **派生投影（方案 A）** | 由现有 flags 派生 `CurrentState`，底层 flags 保留，现有 141 测试绑定零改动，低风险渐进 |
| 已有 UserControl | **采用** ChatView/PlayerView/PlaylistView | 已完整实现且经过验证，重写是浪费 |
| Theme token 化 | **纳入本轮** | 是「完整重构 MainWindow」的核心，且消除气泡嗅探依赖 |
| 窗口 chrome | 留 MainWindow.axaml.cs | 拖拽/min/close 是 Window 固有职责，迁移无收益 |

> 这几处若用户在定稿时要求调整，回到设计修订。

## 4. 总体架构

### 4.1 重构后的视图树

```
MainWindow (Window)
├── TitleBar              (新建 UserControl)
├── ShellCard (容器 Border)
│   ├── BrandHeader       (并入 TitleBar 或独立)
│   ├── ClockStage        (新建：时钟 + Starfield + 双 SpectrumView)
│   ├── PlayerDeck        (采用已有 PlayerView)
│   ├── ChatArea          (采用已有 ChatView + InputDeck)
│   ├── StatusBar         (新建：绑 CurrentState)
│   ├── LibraryDrawer     (采用已有 PlaylistView，overlay)
│   ├── SettingsOverlay   (已是 UserControl)
│   └── CharacterPicker   (新建，overlay)
└── (code-behind 仅保留：窗口拖拽/min/close/Esc)
```

### 4.2 模块依赖

- `RadioState`（Models）← MainWindowViewModel 派生 ← 子 VM flags
- StatusBar / TitleBar / ClockStage → 绑定 MainWindowViewModel 属性
- PlayerView → PlayerViewModel；ChatView → ChatViewModel；PlaylistView → PlaylistViewModel（均已存在）
- Converter 全部移到 `Converters/`，在 `App.axaml` 或 MainWindow Resources 统一注册

## 5. 详细设计

### 5.1 UserControl 划分

| 控件 | 来源 | DataContext | 关键工作 |
|---|---|---|---|
| `TitleBar` | 新建 | MainWindowVM | 品牌 + min/close 按钮；拖拽 handler 留 MainWindow.axaml.cs |
| `ClockStage` | 新建 | MainWindowVM | 时钟 TextBlock 去掉 x:Name，绑 `MainWindowVM.Now`（含时间/星期/日期）；Starfield + 双 SpectrumView 装入 |
| `PlayerDeck` | 采用 PlayerView | PlayerVM | 迁移 `OnProgressSliderReleased`/`OnVolumeSliderReleased` 为 VM Command 或 EventToCommand；Converter 换合并版 |
| `ChatArea` | 采用 ChatView | ChatVM | 消息列表 + 状态浮层 + 麦克风浮层 + InputDeck；迁入 mic hold-to-talk、scroll-to-end、DJ 头像 cue |
| `LibraryDrawer` | 采用 PlaylistView | PlaylistVM | 三 tab + 搜索框 + 导入入口；`OnImportFiles` → `PlaylistVM.ImportFilesCommand` |
| `SettingsOverlay` | 已是 UserControl | SettingsVM | 仅替换 Converter 引用 |
| `CharacterPicker` | 新建 | MainWindowVM | 角色列表；迁入字符切换动画 |
| `StatusBar` | 新建（**替换现有 FooterBar**，axaml:457-464） | MainWindowVM | 绑 `CurrentState` → 状态文本 + 颜色 token；原 `ChatVM.StatusText` 并入或废弃 |

**InputDeck 归属**：并入 ChatArea（输入与麦克风强相关，分离无收益）。

### 5.2 统一状态机（方案 A：派生投影）

#### 5.2.1 枚举
```csharp
// Models/RadioState.cs
public enum RadioState
{
    Idle,      // 空闲
    Curating,  // DJ 思考/推荐中（ChatVM.IsProcessing）
    Searching, // 搜索音源中（PlaylistVM.IsSearching）
    Speaking,  // TTS 播报中（ChatVM.IsSpeaking）
    Playing,   // 音乐播放中（PlayerVM.IsPlaying）
    Error      // 有未清除的失败（ChatVM.HasFailure）
}
```

#### 5.2.2 派生规则（优先级从高到低）
| 优先级 | 条件 | 状态 |
|---|---|---|
| 1 | `ChatVM.HasFailure == true` | Error |
| 2 | `ChatVM.IsSpeaking == true` | Speaking |
| 3 | `PlaylistVM.IsSearching == true` | Searching |
| 4 | `ChatVM.IsProcessing == true` | Curating |
| 5 | `PlayerVM.IsPlaying == true` | Playing |
| 6 | （以上皆否） | Idle |

> 语义说明：Speaking 优先于 Playing，因为 TTS 串场时音乐被 ducked，此时对用户而言「电台正在说话」是主语义。Searching 优先于 Curating，因为搜索是更具体的子动作。

#### 5.2.3 实现
- `ChatViewModel`：把现有 `private bool _hasFailureNotice`（**ChatViewModel.cs:39，已存在**）**提升为 `[Reactive] public bool HasFailure`**（复用同字段语义，置位/清除路径不变——`SetFailureNotice` 置位、状态恢复清除）。
- `MainWindowViewModel` 新增（用 ReactiveUI 的 ObservableAsProperty 派生属性，配合 ReactiveUI.Fody；**注意：项目现有代码全部用 `[Reactive]`，这是首次引入 `[ObservableAsProperty]`，引入后立即 build 验证 Weaver 正确生成属性**）：
  ```csharp
  [ObservableAsProperty] public RadioState CurrentState { get; }

  // 在 ctor / InitializeAsync 中
  this.WhenAnyValue(
      x => x.ChatVM.HasFailure,
      x => x.ChatVM.IsSpeaking,
      x => x.PlaylistVM.IsSearching,
      x => x.ChatVM.IsProcessing,
      x => x.PlayerVM.IsPlaying,
      DeriveRadioState)
      .ObserveOn(RxApp.MainThreadScheduler)
      .ToProperty(this, x => x.CurrentState, out _currentState);
  ```
- `DeriveRadioState` 为纯函数（便于单测）。
- **底层 flags 全部保留**，现有 StatusBar/Footer 绑定 `ChatVM.StatusText` 的逻辑不动（渐进迁移到 `CurrentState` 属可选后续）。

#### 5.2.4 StatusBar 呈现
- StatusBar 绑 `CurrentState`，经 `RadioStateToTextConverter` / `RadioStateToBrushConverter`（走 token）映射为：
  - Idle → "AIRADIO FM" / 中性灰
  - Curating → "CURATING" / 紫
  - Searching → "SEARCHING" / 黄
  - Speaking → "SPEAKING" / 青
  - Playing → "ON AIR" / 绿
  - Error → "ERROR" / 红
- StatusBar **替换** MainWindow 现有 FooterBar（axaml:457-464），避免两处状态显示冗余；原 Footer 的 `ChatVM.StatusText` 文本并入 StatusBar 作为辅助副文本或废弃（属可选后续）。

### 5.3 Converter 合并

新建 `Converters/` 目录，迁移并合并：

| 合并后 | 吞并 | 位置（原） |
|---|---|---|
| `InverseBoolConverter` | 3 个反相器 | MainWindow.axaml.cs:34, PlaylistView.axaml.cs:57, (PlayerView 内同义) |
| `MessageAlignConverter` | 2 个对齐 | MainWindow.axaml.cs:22, ChatView.axaml.cs:86 |
| `BoolToAccentBrushConverter`（参数化） | 6 个 bool→强调色 | ChatView.axaml.cs:101/126, PlayerView.axaml.cs:40/52/64/76 |

**保留独立**（语义不同）：`FavoriteIconConverter`、`RepeatIconConverter`、`MicIconConverter`、`ConversationModeIconConverter`、`TabVisibleConverter`、`TabBgConverter`/`TabFgConverter`、`SpectrumBarHeightConverter`、`MessageRoleToBrushConverter`。

**注册方式统一**：全部在 XAML `<Window.Resources>` 或 `App.axaml` `Application.Resources` 声明，删除各 View 构造函数里的 `Resources[...]` 命令式注册。

**等价性保证**：每个合并 Converter 配单测，验证与原行为一致（含 ConvertBack）。

### 5.4 Theme token 化

#### 5.4.1 新建 `Themes/Colors.xaml`
提取硬编码颜色为语义 token，Light/Dark 两套：

| Token 类别 | 示例 token | 示例值（Dark） |
|---|---|---|
| 表面 | `SurfaceRoot`, `SurfaceCard`, `SurfaceElevated` | `#FF030305`, `#F0131320`, `#CC1B1B2A` |
| 强调 | `AccentPrimary` | `#FF56F5C4` |
| 文本 | `TextPrimary`, `TextSecondary`, `TextMuted` | `#FFEDEDF5`, `#FF9A9AA8`, `#FF8A8A96` |
| 边框 | `BorderSubtle`, `BorderStrong` | `#333A3D4A`, `#667B7898` |
| 状态 | `StatePlaying`, `StateSpeaking`, `StateSearching`, `StateCurating`, `StateError` | 绿/青/黄/紫/红 |

> 完整颜色清单实施时从 axaml 逐行提取，spec 给命名规范与类别。

#### 5.4.2 切换机制（Avalonia 11 标准 ThemeDictionaries）
- 项目当前**零 DynamicResource / ThemeDictionaries / MergedDictionaries 基础**（grep 全空），token 化是从零搭建。
- 用 Avalonia 11 标准 `ResourceDictionary.ThemeDictionaries`（`ThemeVariant.Light` / `ThemeVariant.Dark`）在同一个 `Themes/Colors.xaml` 内定义两套 token；颜色控件绑 `{DynamicResource TokenName}`。
- 切换通过 `Application.Current.RequestedThemeVariant = ThemeVariant.Dark/Light` 实现，Avalonia 自动刷新所有 ThemeDictionaries 绑定（无需手动增删字典）。
- 触发点：`MainWindowViewModel.IsDarkMode` 变化时，VM 内设置 `RequestedThemeVariant`。
- **风险缓解**：此机制在本项目首次使用，实施阶段 4 第一步做最小 PoC（一个 DynamicResource 颜色 + 切换）验证 Avalonia 11.3.9 行为符合预期；若 `RequestedThemeVariant` 路径异常，回退到 `MergedDictionaries` 增删两个独立 Colors.xaml。

#### 5.4.3 消除气泡嗅探
- 删除 `MainWindow.Theme.cs` 的 `ApplyChatMessageTheme` / `SetShellTextForeground`（基于 `MaxWidth==380` 遍历可视树）。
- 气泡改用 `Style` class（如 `class="chat-bubble"`），背景/前景绑 DynamicResource token。
- 删除 `ChatBubbleMaxWidth` 常量与 axaml 注释里的「必须匹配」约束（MaxWidth 改为 Style setter 或绑定 VM 属性）。

### 5.5 code-behind 迁移映射

| 当前逻辑 | 当前位置 | 迁移目标 | 方式 |
|---|---|---|---|
| 时钟 1s timer + UpdateClock | MainWindow.axaml.cs:129-150 | `MainWindowViewModel.Now`（[Reactive] DateTimeOffset，1s 推进） | DispatcherTimer 移到 VM 或 ClockService；ClockStage 绑定 |
| 主题命令式涂色 | MainWindow.Theme.cs | DynamicResource token（5.4） | 删除命令式，改绑定 |
| 气泡主题/遍历 | MainWindow.Theme.cs | Style class + token | 删除嗅探 |
| 文件选择 OnImportFiles | MainWindow.axaml.cs:244-257 | `PlaylistViewModel.ImportFilesCommand` | Command 调 FilePickerHelper（已存在） |
| 麦克风 hold-to-talk | MainWindow.axaml.cs:176-216 | `ChatArea.axaml.cs` | PointerPressed/Released/CaptureLost 跟随输入控件 |
| 气泡 scroll-to-end | MainWindow.axaml.cs:119-127 | `ChatArea.axaml.cs`（ChatView 已有类似） | 合并到一处 CollectionChanged |
| 字符切换动画 | MainWindow.axaml.cs:267-284 | `CharacterPicker.axaml.cs` | 动画跟随控件 |
| Starfield 频谱推送 | MainWindow.axaml.cs:104 | `StarfieldView` 自订阅 SpectrumVM 事件 | 解除 code-behind 中转 |
| DJ 头像 cue | MainWindow.axaml.cs:259-265 | `ChatArea.axaml.cs` 订阅 VM 事件 | Animations.PlayBounceAsync 已是静态 API |
| 窗口拖拽/min/close/Esc | MainWindow.axaml.cs:227-300 | **留 MainWindow.axaml.cs** | Window 固有职责 |

迁移后 `MainWindow.axaml.cs` 预期 < 100 行（仅 ctor + 窗口 chrome + Dispose 桩）。

### 5.6 采用已有控件的差异调和清单

`ChatView` / `PlayerView` / `PlaylistView` 虽完整存在，但各自带与 MainWindow 内联实现不同的细节，采用前必须逐项核查并显式决策：

| 控件 | 已知差异（来自探索） | 处理 |
|---|---|---|
| ChatView | 自带 converter 命令式注册（axaml.cs:22-27） | 改用统一 `Converters/`，删自带注册 |
| ChatView | 自带 scroll-to-end（axaml.cs:41-47） | 与 MainWindow `OnChatMessagesChanged` 合并到 ChatArea 一处 |
| ChatView | `MessageRoleToBrushConverter`（axaml.cs:66）未被 MainWindow 用 | 评估启用或删除 |
| PlayerView | 自带 5 converter（BoolToAccentBg/Fg、RepeatToBg/Fg、RepeatIcon） | 合并版替换，RepeatIcon 保留 |
| PlaylistView | 自带 `InvertBoolValueConverter`、`TabBg/Fg`、6× ctor 实例化（axaml.cs:23-28） | 反相器合并；Tab 系列保留 |
| 三者共用 | `ViewLocator.cs` 已就绪（VM→View 约定） | 可选 VM-first 组合，或直接 `<local:ChatView DataContext="{Binding ChatVM}"/>` |

**核查原则**：每采用一个控件，先 diff 它的 markup 与 MainWindow 内联段，差异列入此表再决策，避免静默行为变化。

## 6. 数据流

### 6.1 状态派生流
```
ChatVM.HasFailure ─┐
ChatVM.IsSpeaking ─┤
PlaylistVM.IsSearching ─┼─► WhenAnyValue ─► DeriveRadioState ─► CurrentState ─► StatusBar
ChatVM.IsProcessing ─┤
PlayerVM.IsPlaying ─┘
```

### 6.2 主题切换流
```
用户点 Theme 按钮 ─► ToggleThemeCommand ─► IsDarkMode flip
                                          ─► ThemeService 切换 MergedDictionaries
                                          ─► DynamicResource 自动刷新所有绑定的颜色
```

## 7. 错误处理

- `DeriveRadioState` 是纯函数，不抛异常；任何 flag 异常值回退 Idle。
- Theme 切换失败（资源缺失）捕获并 Log.Warning，不崩溃，回退 Dark。
- Converter ConvertBack 对非常规输入返回 `Binding.DoNothing`（Avalonia 等价），不抛。
- code-behind 迁移过程中若某 VM 属性不存在，构建期即暴露（编译错误），无运行时隐患。

## 8. 测试策略

### 8.1 硬门槛
- 现有 **141 测试零回归**。每个实施阶段结束跑 `dotnet test`。

### 8.2 新增单测
- `RadioStateDerivationTests`：覆盖优先级矩阵（6 状态 × 关键 flag 组合，含同时为 true 的冲突场景）。
- `ConverterEquivalenceTests`：合并后的 Converter 与原行为等价（正向/反向/边界）。
- `MainWindowViewModelTests` 扩展：`CurrentState` 随子 VM flag 变化正确推进。
- `RadioStateDerivationTests` **显式覆盖争议场景**：`IsPlaying=true && IsSpeaking=true` → 期望 `Speaking`（锁定优先级是有意设计，非偶然）；`IsProcessing=true && IsSearching=true` → 期望 `Searching`。

### 8.3 手动验证 checklist
启动 → 欢迎语 → 播放一首 → 暂停/继续 → 上一首/下一首 → 聊天点歌 → 麦克风 hold-to-talk → 切主题（Light/Dark）→ 开库抽屉/搜索/收藏 tab → 导入本地文件 → 开设置 → 切 DJ 角色 → 关闭。

每步对照 StatusBar 状态文本是否随操作正确变化。
**主题专项**：切换 Light/Dark 后，逐一检查播放器、聊天气泡、库抽屉、设置、状态条、时钟区域颜色全部正确刷新（验证 ThemeDictionaries 绑定无遗漏）。

## 9. 验证标准（完成定义）

- [ ] `dotnet build` 0 警告 0 错误
- [ ] `dotnet test` ≥ 141 + 新增测试全过
- [ ] MainWindow.axaml < 200 行（从 668 降）
- [ ] MainWindow.axaml.cs < 100 行（仅窗口 chrome）
- [ ] `MainWindow.Theme.cs` 中基于 MaxWidth 的气泡嗅探代码删除
- [ ] `Converters/` 目录存在，重复 Converter 已合并
- [ ] `RadioState` 枚举与 `CurrentState` 派生落地，StatusBar 绑定
- [ ] `Themes/Colors.xaml` 存在，硬编码颜色已 token 化
- [ ] 手动 checklist 全通过

## 10. 实施阶段（高层，供 writing-plans 展开）

1. **地基**：建 `Converters/`、`Themes/`、`Models/RadioState.cs` 骨架；不破坏现有构建。
2. **状态机**：`ChatViewModel.HasFailure` 显式化；`MainWindowViewModel.CurrentState` 派生 + 单测。
3. **Converter 合并**：迁移 + 等价性测试 + 统一注册。
4. **Theme token 化**：提取 `Colors.xaml`，切换机制，删气泡嗅探。
5. **UserControl 采用**：让 MainWindow 引用已有 ChatView/PlayerView/PlaylistView，删内联 markup。
6. **新建控件**：TitleBar / ClockStage / StatusBar / CharacterPicker。
7. **code-behind 迁移**：按 5.5 表逐项迁移。
8. **收尾验证**：跑完整 checklist + 全量测试。

每阶段独立可验证、可回滚（单次 commit 一个阶段）。

## 11. 风险与回滚

| 风险 | 缓解 |
|---|---|
| 采用已有 ChatView/PlayerView/PlaylistView 时行为不一致（它们可能有内联实现里没有的小差异） | 采用前逐控件比对已有 markup 与 MainWindow 内联；差异记录并显式决策 |
| Theme token 化遗漏颜色导致外观退化 | 实施时从 axaml 逐行提取颜色建清单，token 覆盖率自查 |
| 派生状态与现有 `StatusText` 显示语义冲突 | 本轮不强改 StatusText，CurrentState 只驱动新 StatusBar；StatusText 渐进迁移 |
| 单次重构过大难回滚 | 按「实施阶段」分 8 个 commit，每个可独立 revert |
| ThemeDictionaries/RequestedThemeVariant 机制首次使用，Avalonia 11.3.9 行为未验证 | 阶段 4 先做最小 PoC，异常则回退 MergedDictionaries（见 5.4.2） |
| 项目首次引入 `[ObservableAsProperty]`（现有代码全用 `[Reactive]`），Weaver 生成未验证 | 引入后立即 build 确认 ReactiveUI.Fody 正确生成属性 |
| 采用已有 ChatView/PlayerView/PlaylistView 时它们各自带 converter 注册与 scroll 逻辑，与 MainWindow 内联实现有差异 | 采用前逐控件核查已知差异（见 §5.6），差异显式决策 |
| 派生 `CurrentState` 的底层 flag 在后台线程变更 | 派生链 `.ObserveOn(RxApp.MainThreadScheduler)` 后再 `ToProperty`（见 5.2.3） |

## 12. 附录：本轮 5 子项目分解

本子项目是「补齐当前形态」5 个子项目的第 1 个，顺序：

1. **视图重构 + 统一状态机**（本文档）
2. 节目单 UI + 推荐理由层（依赖 1）
3. 真实 FFT 频谱（独立）
4. SongStory 单曲讲述（依赖 2 的气泡机制）
5. fallback UI + 诊断 + 清理收尾（独立）

每个子项目独立 spec → plan → 实施 → 验证。
