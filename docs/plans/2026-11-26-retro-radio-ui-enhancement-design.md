# TuanRadio 复古 DJ 电台界面增强设计方案

> **状态**：Phase 1–3 已全部实现、通过两轮自审与双变体 Release 门禁并推送（`699ce91`），待实机体验调优  
> **日期**：2026-11-26  
> **适用版本**：v1.3.0+（.NET 10 / Avalonia 11.3.9 / ReactiveUI）

---

## 1. 背景与设计目标

当前 TuanRadio 已具备完整的复古电台内核（程序化 FM 静电杂音与台呼、WASAPI 1024 点硬件回环频谱、AI DJ 语音串场与点歌、专注/深夜伴眠场景、四套 WCAG 门禁调色板），但主界面视觉仍偏**现代深色播放器 + 标准 IM 聊天框**，尚未充分释放「复古 DJ 电台（A retro radio experience powered by AI DJs.）」的硬件仪式感与演播室临场感。

### 核心设计原则

1. **克制拟物，拒绝廉价堆砌**：保留当前极简、清晰的桌面信息层级，只引入最具辨识度的模拟收音机与电台演播室语汇（调频刻度、VFD 分格电平表、演播室红灯、微浮雕玻璃表窗）。
2. **零布局抖动与高对比度门禁**：所有动效仅作用于 `Opacity`、`RenderTransform` 或自绘 `DrawingContext`，严禁触发布局重排；新增颜色 Token 必须统一声明为 `<Color>`（`*Color` 命名，与现有 `Colors.axaml` 保持一致）并在四套色板的 Dark/Light 成对同步，全量通过 `AccentPaletteTests`（WCAG 2.1 文字 ≥4.5:1、图形 ≥3:1）。
3. **模块化可取舍**：六个增强模块彼此解耦，通过分阶段实现与清晰边界保持低成本回退，方便实机测试后自由取舍，不为验收增加一次性设置开关（`vfd` 频谱样式本身已是可选项）。
4. **守住既有交互与测试契约**：不破坏 `ClockStage` 分区域点击路由、`CompactMode` 两行紧凑布局、XAML 编译绑定作用域与最小化/隐藏托盘/简洁模式低功耗规则。

---

## 2. 六大界面增强模块详细设计

### 模块 A：模拟 FM 频率刻度尺（Analog Tuning Scale）

- **视觉定位**：
  - 将 [MainWindow.axaml](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Views/MainWindow.axaml) 中 `ClockStage` 行高从 `168` 微调至 `184`（聊天室为 `*` 行，空间充裕），在 `ClockStage` 的 `StageRoot` 底部留出独立 `22px` 频段底带，彻底避免与中栏日期或第 3 行歌词纵向碰撞；
  - 刻度尺为精致模拟调频刻度带（`88 · · 92 · · 96 · · 100 · · 104 · · 108 MHz`），配一根带微弱辉光的细竖指针；
  - **横向与模式防碰撞**：刻度尺左对齐留出右侧 `WeatherIndicator` 安全边距（或当天气不可见时自适应居中），并在歌词模式（`IsLyricsMode`）下淡出隐藏，避免与歌词状态文本争抢底缘空间。
- **频率映射规则（纯确定性 FNV-1a 32-bit 哈希，严禁使用进程随机加盐的 `string.GetHashCode()`）**：
  - 优先级由高到低：
    1. **无曲目 / Idle 态**：停靠基准频点 `98.0 MHz`（`FM 98.0`，`StatusBar` 左侧台标保持 `TUANRADIO FM` 不追加频点）；
    2. **直播网络电台（`Track.IsLiveStream`）**：按 `SourceId`/`Title` FNV-1a 哈希映射到 `88.5–91.9 MHz`（步进 `0.1 MHz`，标注 `LIVE`）；
    3. **播客单集（`Track.IsPodcastEpisode`）**：按 `SourceId`/`Title` FNV-1a 哈希映射到 `107.1–107.9 MHz`（步进 `0.1 MHz`，标注 `PODCAST`）；
    4. **场景模式专属频段**：
       - `Focus`（专注伴学）：锁定 `102.4 MHz`（`FM 102.4 · FOCUS`）
       - `Sleep`（深夜伴眠）：锁定 `98.7 MHz`（`FM 98.7 · NIGHT`）
    5. **常规单曲 / AI 推荐**：按曲目 `SourceId` 或 `Title` FNV-1a 哈希映射到 `90.0–106.8 MHz`（步进 `0.1 MHz`），切歌时触发阻尼滑动。
- **动画体验**：自绘控件 `FmDialView : Control`，内部持 `TargetFrequency` 与 `CurrentFrequency`，切台时通过 60fps 指数阻尼插值（约 450ms 收敛后自动停帧，**静止态 0 CPU 占用**），控件从可视树卸载或不可见时立即停帧。
- **命中契约**：`IsHitTestVisible="False"`，完全不干扰 `ClockStage` 中栏切歌词与两侧频谱切样式的点击路由。

### 模块 B：复古分格 VFD/LED 频谱 + Peak Hold 峰值慢落（`vfd` 样式）

- **视觉定位**：在 [SpectrumRenderer.cs](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Views/SpectrumRenderer.cs) 现有 4 种样式（`bars` / `mirror` / `wave` / `particles`）之外，新增第 5 种复古功放专属样式 **`vfd`**（复古分格荧光电平表）。
- **渲染与状态推进设计（热路径零分配）**：
  - 新增 `PeakBrushProperty` 依赖属性，由 [SpectrumView.axaml](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Views/SpectrumView.axaml) 与 [CompactPlayer.axaml](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Views/CompactPlayer.axaml) 绑定 `{DynamicResource VfdPeakColor}` 传入；
  - 将每根频谱柱纵向切分为等高小亮块（主舞台每格高 `3px`、间距 `2px`，圆角 `1px`）；
  - 维护同长度 `float[] _peaks` 与 `int[] _peakHoldFrames` 数组，**仅在 `SetBands(float[] data)` 中推进峰值保持（约 8 帧 ≈ 250ms）与柔和重力下落**，严禁在 `Render()` 内推进物理状态（避免悬停/窗口重绘导致峰值加速坠落）；
  - **三段电平渐变**：底部 0–65% 使用 `PrimaryBrush`（薄荷绿/暖琥珀等主题主色）、65–85% 过渡到 `SecondaryBrush`、85–100% 及顶部 Peak Hold 峰值横杠使用 `PeakBrush`（暖琥珀/珊瑚红警告色，深浅主题各过 3:1 图形对比度）；
  - **紧凑模式适配**：`CompactPlayer` 迷你频谱高度为 `20px`，在高度 `< 32px` 时自动切为细密 `2px` 分格 + `1px` 间距，保持清晰不糊。
- **设置与循环联动**：同步更新 `SettingsViewModel` / `MainWindowViewModel` 频谱样式列表、中英文词条（`复古荧光` / `Retro VFD`）与 `ClockStage` / `CompactPlayer` 点击循环顺序，并更新 `ClockStageLyricsCoexistenceTests` 断言。

### 模块 C：演播室 `ON AIR` 红灯牌 & 物理运转微动效

1. **演播室 `ON AIR` 开麦红灯语义（`StatusBar.axaml`）**：
   - 保留现有音乐播放态（`Playing`）的主题色宝石灯（薄荷/琥珀等）及 `RadioStateToBrushConverter` 的既有契约；
   - 在 `MainWindowViewModel` 暴露统一派生属性 `IsStudioLive`（`CurrentState == RadioState.Speaking || ChatVM.IsListening`）；
   - 将 `StatusBar.axaml` 中 `StateLampHalo.Fill`、`StateLamp.Fill`、`StateText.Foreground` 的默认 `<MultiBinding>` 迁入基础 `<Style>`，再通过 `Classes.studio-live="{Binding IsStudioLive}"` 后声明样式覆写为 `{DynamicResource StudioOnAirColor}`（Dark `#FFFF5A5F`、Light `#FFC41E2A`，文字对比度均 ≥4.5:1），同时在 `.studio-live` 态强制点亮灯珠、开启红光呼吸并给灯箱边框叠加演播室红光描边（解决本地 `MultiBinding` 优先级高于样式类及 `Idle` 录音不亮灯问题）；
   - 左侧台标从固定 `TUANRADIO FM` 升级为绑定 `MainWindowViewModel.StationCallSign`（有曲目时显示 `TUANRADIO FM · 98.7 MHz`，无曲目时回退 `TUANRADIO FM`）。
2. **时钟心跳冒号与 VFD 微辉光（`ClockStage.axaml`）**：
   - 将 `HH:mm` 拆分为 `HH` + `:` + `mm` 三段横向 `StackPanel`（`TextBlock` 共享 `Consolas 56px Bold`）；
   - 中间冒号由 `MainWindowViewModel` 现有 1Hz 时钟源派生 `IsClockColonDim`（`Now.Second % 2 == 1`）驱动 `Classes.dim` + `0.35s` `DoubleTransition`（`Opacity` `1.0 ↔ 0.35`），**零额外定时器**且与系统秒针同步；
   - 为时钟容器在 Dark 变体下叠加克制的主题色/暖白微辉光（Light 变体关闭避免发脏）。
3. **黑胶盘双速旋转、播放电平微动与统一视觉活跃门控（`MainWindow.axaml.cs` & `PlayerDeck.axaml(.cs)`）**：
   - **视觉活跃门控**：`MainWindow` 监听 `IsVisible`、`WindowState` 与 `IsCompactMode`，仅在 `IsVisible && WindowState != WindowState.Minimized && !IsCompactMode` 时运行黑胶旋转、TTS 辉光与 `PlayerDeck` 3 条电平刷新；
   - **黑胶单一角速度状态机**：整合 `PlayerVM.IsPlaying`（**15°/s**）与 `ChatVM.IsSpeaking`（**30°/s**，说话优先），仅在目标角速度归 `0`（既不播歌也不说话）时停转并回正 `Angle = 0`，避免 DJ 插播结束切回播歌时盘面瞬移跳零；
   - **`PlayerDeck` 左侧 3 根小绿条**：在 `PlayerDeck.axaml.cs` 订阅 `SpectrumVM.SpectrumReceived`（窗口/控件不可见时立即跳过），取低/中/高频三段平滑值通过 `ScaleTransform.ScaleY` 驱动（纯变换零重排），暂停时平滑回落基线。

### 模块 D：内嵌玻璃表窗与机身微浮雕触感

- **舞台内嵌表窗（`ClockStage.axaml`）**：
  - 在 `StageRoot` 内增加 `Border` 凹槽边框（顶部 1px 暗线 + 底部 1px 极淡高光线，模拟嵌入机壳的玻璃视窗）；
  - 叠加一层 `IsHitTestVisible="False"` 的极淡斜向线性渐变高光（基于 `StageGlassHighlightColor`，Dark 态微白光、Light 态暖白高光），不降低文字对比度且增添玻璃质感。
- **双线金属机身接缝与物理按键触感（`MainWindow.axaml` & `PlayerDeck.axaml`）**：
  - 品牌头、时钟舞台、播放甲板与聊天室之间的水平分割线采用双线微浮雕（主暗线 + 极淡下沿高光）；
  - 为播控键与 `NOPE / SIM / CALM / FIRE / STORY` 及播客控制键添加显式样式类 `deck-key`，在 `Button.deck-key:pressed` 态叠加 `RenderTransform="translateY(1px)"`（纯视觉 1px 下沉，零布局影响且不误伤其他按钮）。

### 模块 E：聊天区演播室场记 / 点歌纸条语汇（`ChatArea.axaml` & `ChatMessage`）

- **模型支持（[Track.cs](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop.Music/Models/Track.cs)）**：
  - 为 `ChatMessage` 新增 `IsUser`、`IsAssistant` 布尔便捷属性，以及格式化时间戳 `TimeText`（`Timestamp.ToString("HH:mm")`）与 `HeaderText`（`{SenderName} · {TimeText}`），并在 `NotifyLanguageChanged()` 中同步触发 `HeaderText` 刷新。
- **AI DJ 消息（播音台字幕/场记条）**：
  - 通过 `Classes.assistant="{Binding IsAssistant}"` 启用左侧 `2px` 强调色竖条（`BorderThickness="2,0,0,0"`，`BorderBrush="{DynamicResource AccentGreen}"`）；
  - 消息头显示 `主播名 · HH:mm`（如 `Lumen · 23:14`），更贴近电台值班日志；`MessageRole.System` 走中性无竖条兜底样式。
- **听众消息（点歌纸条）**：
  - 通过 `Classes.user="{Binding IsUser}"` 保持右侧对齐与紧凑最大宽度，消息头右对齐显示 `我 · HH:mm`（或 `Me · HH:mm`），边框带极淡强调色描边（`C_3356F5C4`），与 AI DJ 发言形成清晰的“演播室 vs 听众来信”视觉区隔。
- **输入框电台化占位文案**：
  - 更新双语占位提示（中文：`给值班主播递张点歌纸条…` / 英文：`Send a song request note to the DJ...`），不改 `AutomationId` 与控件结构。

### 模块 F：Warm Amber 复古深夜收音机氛围协同

- [Amber.axaml](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Themes/Palettes/Amber.axaml) 已具备精调的暖黑（`#FF0D0A07`）与暖白（`#FFF6EFE3`）底色，无需大改现有基座。
- 为新增的 5 个复古 `<Color>` Token（`FmDialNeedleColor`、`FmDialTickColor`、`VfdPeakColor`、`StudioOnAirColor`、`StageGlassHighlightColor`）在基座 [Colors.axaml](file:///d:/WorkProject/TuanRadio/AIRadio.Desktop/Themes/Colors.axaml) 与 3 套叠加色板（`Amber` / `Ice` / `Synthwave`）的 Dark/Light 两套字典中**成对补齐**，在 `Amber` 主题下赋予浓郁的“暖钨丝表盘指针 + 老式琥珀荧光”观感，并全量纳入 `AccentPaletteTests` 自动化对比度门禁。

---

## 3. 涉及文件与分阶段实施计划

| 阶段 | 包含模块 | 主要触达文件 | 独立可验证目标 |
| :--- | :--- | :--- | :--- |
| **Phase 1（核心复古辨识度）** | 模块 A（FM 刻度尺）+ 模块 B（VFD 频谱）+ 模块 F（`<Color>` Token 与门禁） | `Themes/Colors.axaml`、`Themes/Palettes/{Amber,Ice,Synthwave}.axaml`、`Views/FmDialView.cs`（新）、`Views/ClockStage.axaml(.cs)`、`Views/MainWindow.axaml`、`Views/SpectrumRenderer.cs`、`Views/SpectrumView.axaml`、`Views/CompactPlayer.axaml`、`ViewModels/MainWindowViewModel.cs`、`ViewModels/SettingsViewModel.cs`、`Services/AppLanguage*.cs`、`AccentPaletteTests.cs`、`ClockStageLyricsCoexistenceTests.cs` | 舞台底部出现不重叠、可阻尼滑动的 FM 刻度尺；频谱可切换至第 5 种 VFD 分格 Peak Hold 样式；`AccentPaletteTests` 与频谱循环测试全绿 |
| **Phase 2（演播室灯牌与物理动效）** | 模块 C（ON AIR 红灯、冒号心跳、黑胶双速状态机与降耗门控、3 根电平条） | `Views/StatusBar.axaml`、`Views/ClockStage.axaml`、`Views/MainWindow.axaml.cs`、`Views/PlayerDeck.axaml(.cs)`、`ViewModels/MainWindowViewModel.cs`、`Converters/RadioStateLampConverters.cs` | DJ 开麦/录音亮演播室红灯且 `ConverterEquivalenceTests` 全绿；时钟冒号随 1Hz 柔和呼吸；播歌 15°/s、说话 30°/s 切换无跳零且隐藏/最小化/简洁模式自动降耗；左侧 3 条随音乐律动 |
| **Phase 3（材质触感与聊天室语汇）** | 模块 D（玻璃表窗、双线接缝、微浮雕按键）+ 模块 E（电台场记/点歌纸条） | `Views/ClockStage.axaml`、`Views/MainWindow.axaml`、`Views/PlayerDeck.axaml`、`Views/ChatArea.axaml`、`AIRadio.Desktop.Music/Models/Track.cs`、`Services/AppLanguage*.cs` | 舞台具备内嵌玻璃层深与机身双线接缝；播控/反馈键有 1px 按压触感；聊天区升级为电台场记与点歌纸条风格 |

---

## 4. 方案前置 Review（工程风险与契约审查）

1. **`AccentPaletteTests` 键集与 `<Color>` 类型门禁**：所有新增 Token 必须为 `<Color>`（`*Color` 命名），先在 `Colors.axaml` 的 Dark/Light 声明，再在 `Amber`/`Ice`/`Synthwave` 的 Dark/Light 成对覆盖；将 `StudioOnAirColor`、`VfdPeakColor`、`FmDialNeedleColor` 纳入 `AuditedPairs` 自动化对比度断言。
2. **`ConverterEquivalenceTests` 与 Avalonia 属性优先级**：不破坏 `RadioStateToBrushConverter` 的既有 `Speaking` 返回值；`StatusBar.axaml` 将默认 `Fill`/`Foreground` 绑定迁入基础 `<Style>`，再由 `.studio-live` 覆写，同时处理 `Idle` 态录音点亮与呼吸。
3. **`ClockStage` 空间与点击路由契约**：行高微调至 `184` 并预留右侧天气安全区，歌词模式隐藏刻度尺；`FmDialView` 与玻璃高光层严格设 `IsHitTestVisible="False"`；`vfd` 纳入合法样式集并更新循环测试断言。
4. **XAML 编译绑定与 `DataContext` 隔离**：`FmDialView` 声明独立 `StyledProperty`，在 `ClockStage.axaml` 直接从 `MainWindowViewModel` 绑定，不做 `DataContext` 重定向。
5. **动画能耗与生命周期清理**：`FmDialView` 按需插值收敛停帧；黑胶旋转、TTS 辉光与 `PlayerDeck` 3 条电平受 `IsVisible && WindowState != Minimized && !IsCompactMode` 统一门控；所有订阅在 `Dispose()` / `DetachedFromVisualTree` 严格释放。

---

## 5. 实机测试取舍清单（供后续验收对照）

- [ ] **FM 刻度尺**：指针滑动速度与刻度密度是否合适？
- [ ] **VFD 频谱**：Peak Hold 保持时长与下落速度手感如何？是否设为默认频谱样式？
- [ ] **演播室红灯**：AI DJ 说话/录音时的红光强度是否舒适（深夜伴眠模式是否需要降低亮度）？
- [ ] **黑胶慢转与冒号闪烁**：15°/s 转速与 1Hz 冒号呼吸是否自然不晃眼？
- [ ] **玻璃表窗与按键微沉**：深色与浅色主题下层深感是否恰到好处？
- [ ] **电台场记聊天区**：2px 竖线 + 时间戳与点歌纸条视觉是否比原气泡更有氛围？
