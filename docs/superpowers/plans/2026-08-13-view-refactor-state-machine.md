# 视图重构 + 统一状态机 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 MainWindow 单体（668 行 axaml + god-class code-behind）拆为独立 UserControl，引入统一 RadioState 状态机，合并重复 Converter，Theme 颜色 token 化，code-behind 逻辑迁移到 VM/子控件。

**Architecture:** 采用已有但被绕过的 ChatView/PlayerView/PlaylistView + 新建 TitleBar/ClockStage/StatusBar/CharacterPicker；RadioState 用派生投影（ObservableAsProperty）从现有 flags 推导；Theme 用 Avalonia 11 标准 ThemeDictionaries + RequestedThemeVariant。

**Tech Stack:** .NET 8, Avalonia 11.3.9, ReactiveUI 20.1.1 + ReactiveUI.Fody 19.5.41, LibVLCSharp, xUnit。

## Global Constraints

- 目标框架 `net8.0`；Nullable 启用；compiled Avalonia bindings (`x:DataType`)。
- 构建：`dotnet build AIRadio.Desktop/AIRadio.Desktop.csproj -v:minimal` —— 必须 0 警告 0 错误。
- 测试：`dotnet test AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/AIRadio.Desktop.Tests.csproj -v:minimal` —— **现有 141 测试零回归是硬门槛**（Git Bash 下若用 `/p:` 参数必须加引号 `"/p:..."`）。
- 代码风格沿用现有：`[Reactive]` + ReactiveUI.Fody；中文注释。
- **commit 受 AGENTS.md §5 约束：执行时每个 commit 前需用户明确授权，不得擅自提交。**
- commit message 格式（AGENTS.md §7）：`<type>:<中文一句话描述>`，无空格。
- 用户首选项：MainWindow.axaml < 200 行、.cs < 100 行（仅窗口 chrome）。

## File Structure

**新建：**
- `Models/RadioState.cs` — 状态枚举
- `Converters/InverseBoolConverter.cs`、`Converters/MessageAlignConverter.cs`、`Converters/BoolToAccentBrushConverter.cs` — 合并后的共享 Converter
- `Converters/RadioStateToTextConverter.cs`、`Converters/RadioStateToBrushConverter.cs` — 状态机呈现
- `Themes/Colors.xaml` — ThemeDictionaries（Light/Dark）token
- `Views/TitleBar.axaml(.cs)`、`Views/ClockStage.axaml(.cs)`、`Views/StatusBar.axaml(.cs)`、`Views/CharacterPicker.axaml(.cs)` — 新 UserControl

**修改：**
- `Models/Track.cs` — 不动（PlaybackState 已在 :144）
- `ViewModels/ChatViewModel.cs:39` — `_hasFailureNotice` 提升为 `[Reactive] public bool HasFailure`
- `ViewModels/MainWindowViewModel.cs` — 加 `Now` 属性 + `CurrentState` 派生 + Theme 切换
- `ViewModels/PlaylistViewModel.cs` — 加 `ImportFilesCommand`
- `Views/MainWindow.axaml` — 替换内联为 UserControl 引用（目标 < 200 行）
- `Views/MainWindow.axaml.cs` + `MainWindow.Theme.cs` — 删命令式 theming/嗅探，仅留窗口 chrome
- `Views/ChatView.axaml(.cs)` / `PlayerView.axaml(.cs)` / `PlaylistView.axaml(.cs)` — 调和差异后启用
- `Views/StarfieldView.axaml.cs` — 自订阅 SpectrumVM 事件

**测试：**
- `AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/RadioStateDerivationTests.cs`（新建）
- `AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/ConverterEquivalenceTests.cs`（新建）

---

## Task 1: 地基 — RadioState 枚举 + 目录

**Files:**
- Create: `AIRadio.Desktop/Models/RadioState.cs`

**Interfaces:**
- Produces: `enum AIRadio.Desktop.Models.RadioState { Idle, Curating, Searching, Speaking, Playing, Error }`

- [ ] **Step 1: 创建枚举**

`AIRadio.Desktop/Models/RadioState.cs`:
```csharp
namespace AIRadio.Desktop.Models;

/// <summary>电台整体状态。派生优先级见 spec §5.2.2：Error > Speaking > Searching > Curating > Playing > Idle。</summary>
public enum RadioState
{
    Idle,
    Curating,
    Searching,
    Speaking,
    Playing,
    Error
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build AIRadio.Desktop/AIRadio.Desktop.csproj -v:minimal`
Expected: 0 错误 0 警告（枚举只是新增，不影响现有）。

- [ ] **Step 3: Commit（需授权）**

```bash
git add AIRadio.Desktop/Models/RadioState.cs
git commit -m "feat:新增RadioState状态枚举"
```

---

## Task 2: ChatViewModel.HasFailure 提升

**Files:**
- Modify: `AIRadio.Desktop/ViewModels/ChatViewModel.cs:39`
- Test: `AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/ChatViewModelTests.cs`

**Interfaces:**
- Produces: `bool ChatViewModel.HasFailure`（[Reactive]，由现有 `SetFailureNotice` 置位、状态恢复清除）
- Consumes: 现有 `_hasFailureNotice` 私有字段语义

- [ ] **Step 1: 写失败测试**

在 `ChatViewModelTests.cs` 末尾追加（复用现有测试的 VM 构造方式）：
```csharp
[Fact]
public void HasFailure_ReflectsFailureNoticeLifecycle()
{
    var vm = CreateChatViewModel(); // 用现有测试辅助方法构造
    Assert.False(vm.HasFailure);

    // 触发失败路径（通过反射调用 SetFailureNotice，或触发一次失败回复）
    var failure = new ApiFailureInfo(ApiFailureKind.MissingApiKey, "no key", "", "");
    vm.GetType().GetMethod("SetFailureNotice",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
      .Invoke(vm, new object?[] { failure });

    Assert.True(vm.HasFailure);
}
```
> 若 `CreateChatViewModel` / `ApiFailureInfo` 命名与现有不符，先 grep 现有测试的构造方式对齐命名。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/AIRadio.Desktop.Tests.csproj -v:minimal --filter HasFailure`
Expected: FAIL（`HasFailure` 不存在，编译错误）。

- [ ] **Step 3: 提升字段**

`ChatViewModel.cs:39` 把：
```csharp
private bool _hasFailureNotice;
```
改为：
```csharp
[Reactive] public bool HasFailure { get; set; }
```
然后全局搜索 `_hasFailureNotice` 的所有赋值点（`SetFailureNotice` 内置位、清除路径），改为直接赋值 `HasFailure = ...`（保持原语义）。删除原 `_hasFailureNotice` 字段。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/AIRadio.Desktop.Tests.csproj -v:minimal`
Expected: 全部通过（141 + 1 新增）。

- [ ] **Step 5: Commit（需授权）**

```bash
git add AIRadio.Desktop/ViewModels/ChatViewModel.cs AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/ChatViewModelTests.cs
git commit -m "refactor:ChatViewModel显式化HasFailure为响应式属性"
```

---

## Task 3: CurrentState 派生 + 优先级测试

**Files:**
- Modify: `AIRadio.Desktop/ViewModels/MainWindowViewModel.cs`
- Create: `AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/RadioStateDerivationTests.cs`

**Interfaces:**
- Consumes: `ChatViewModel.HasFailure/IsSpeaking/IsProcessing`、`PlaylistViewModel.IsSearching`、`PlayerViewModel.IsPlaying`
- Produces: `RadioState MainWindowViewModel.CurrentState`（[ObservableAsProperty]）；纯函数 `static RadioState DeriveRadioState(bool hasFailure, bool isSpeaking, bool isSearching, bool isProcessing, bool isPlaying)`

- [ ] **Step 1: 写失败测试（优先级矩阵）**

`RadioStateDerivationTests.cs`:
```csharp
using AIRadio.Desktop.Models;
using AIRadio.Desktop.ViewModels;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class RadioStateDerivationTests
{
    [Fact] public void AllFalse_Idle() =>
        Assert.Equal(RadioState.Idle, MainWindowViewModel.DeriveRadioState(false, false, false, false, false));

    [Fact] public void HasFailure_Highest() =>
        Assert.Equal(RadioState.Error, MainWindowViewModel.DeriveRadioState(true, true, true, true, true));

    [Fact] public void Speaking_Beats_Playing() =>
        Assert.Equal(RadioState.Speaking, MainWindowViewModel.DeriveRadioState(false, true, false, false, true));

    [Fact] public void Searching_Beats_Curating() =>
        Assert.Equal(RadioState.Searching, MainWindowViewModel.DeriveRadioState(false, false, true, true, false));

    [Fact] public void Curating_When_Processing() =>
        Assert.Equal(RadioState.Curating, MainWindowViewModel.DeriveRadioState(false, false, false, true, false));

    [Fact] public void Playing_When_OnlyPlaying() =>
        Assert.Equal(RadioState.Playing, MainWindowViewModel.DeriveRadioState(false, false, false, false, true));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter RadioStateDerivation`
Expected: FAIL（`DeriveRadioState` 不存在）。

- [ ] **Step 3: 实现纯函数**

在 `MainWindowViewModel.cs` 加：
```csharp
public static RadioState DeriveRadioState(bool hasFailure, bool isSpeaking, bool isSearching, bool isProcessing, bool isPlaying)
    => hasFailure ? RadioState.Error
       : isSpeaking ? RadioState.Speaking
       : isSearching ? RadioState.Searching
       : isProcessing ? RadioState.Curating
       : isPlaying ? RadioState.Playing
       : RadioState.Idle;
```

- [ ] **Step 4: 跑纯函数测试确认通过**

Run: `dotnet test ... --filter RadioStateDerivation`
Expected: 6 个全过。

- [ ] **Step 5: 接入 CurrentState 派生属性**

在 `MainWindowViewModel` 类加（确认顶部有 `using ReactiveUI; using ReactiveUI.Fody.Helpers;`）：
```csharp
[ObservableAsProperty] public RadioState CurrentState { get; }

// 在构造函数末尾（子 VM 已注入后）：
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
并加字段 `private readonly ObservableAsPropertyHelper<RadioState> _currentState;`（若 Fody 自动生成则可省，build 验证）。

- [ ] **Step 6: 跑全量测试**

Run: `dotnet test AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/AIRadio.Desktop.Tests.csproj -v:minimal`
Expected: 全部通过；`dotnet build` 0 警告（**验证 [ObservableAsProperty] 被 Fody 正确生成**）。

- [ ] **Step 7: Commit（需授权）**

```bash
git add AIRadio.Desktop/ViewModels/MainWindowViewModel.cs AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/RadioStateDerivationTests.cs
git commit -m "feat:MainWindowViewModel派生CurrentState电台状态机"
```

---

## Task 4: Converter 合并 — InverseBool + MessageAlign

**Files:**
- Create: `AIRadio.Desktop/Converters/InverseBoolConverter.cs`、`AIRadio.Desktop/Converters/MessageAlignConverter.cs`
- Create: `AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/ConverterEquivalenceTests.cs`
- Modify: 删除 `MainWindow.axaml.cs:22,34`、`PlaylistView.axaml.cs:57`、`ChatView.axaml.cs:86` 的旧定义；更新 XAML 注册

**Interfaces:**
- Produces: `InverseBoolConverter`、`MessageAlignConverter`（均 `IValueConverter`，`MessageAlignConverter.Instance` 静态单例保留）

- [ ] **Step 1: 写等价性失败测试**

`ConverterEquivalenceTests.cs`:
```csharp
using AIRadio.Desktop.Converters;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ConverterEquivalenceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBool_RoundTrips(bool input, bool expected)
    {
        var c = new InverseBoolConverter();
        Assert.Equal(expected, c.Convert(input, typeof(bool), null, null));
        Assert.Equal(expected, c.ConvertBack(input, typeof(bool), null, null));
    }

    [Fact]
    public void MessageAlign_UserRight_OthersLeft()
    {
        var c = MessageAlignConverter.Instance;
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right,
            c.Convert(MessageRole.User, null, null, null));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left,
            c.Convert(MessageRole.Assistant, null, null, null));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter ConverterEquivalence`
Expected: FAIL（命名空间/类不存在）。

- [ ] **Step 3: 创建合并 Converter**

`Converters/InverseBoolConverter.cs`:
```csharp
using System.Globalization;
using Avalonia.Data.Converters;

namespace AIRadio.Desktop.Converters;

public class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
```

`Converters/MessageAlignConverter.cs`：从 `MainWindow.axaml.cs:22` 的现有 `MessageAlignConverter` 原样搬入（保留 `Instance` 静态字段），改命名空间为 `AIRadio.Desktop.Converters`。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test ... --filter ConverterEquivalence`
Expected: PASS。

- [ ] **Step 5: 删除旧定义 + 更新引用**

- 删 `MainWindow.axaml.cs` 的 `MessageAlignConverter`(:22) 和 `InverseBoolConverter`(:34)。
- 删 `PlaylistView.axaml.cs` 的 `InvertBoolValueConverter`(:57)；其 XAML/ctor 注册改用 `InverseBoolConverter`。
- 删 `ChatView.axaml.cs` 的 `MessageRoleToAlignmentConverter`(:86)；XAML 注册改用 `MessageAlignConverter`。
- `MainWindow.axaml:19-23` 的 `<Window.Resources>` 改为引用新命名空间（或后续 Task 8 统一在 App.axaml 注册）。

- [ ] **Step 6: 全量构建 + 测试**

Run: `dotnet build AIRadio.Desktop/AIRadio.Desktop.csproj -v:minimal` → 0 错误。
Run: `dotnet test ... -v:minimal` → 全过。

- [ ] **Step 7: Commit（需授权）**

```bash
git add AIRadio.Desktop/Converters AIRadio.Desktop/Views AIRadio.Desktop.Tests
git commit -m "refactor:合并InverseBool与MessageAlign到Converters目录"
```

---

## Task 5: Converter 合并 — BoolToAccentBrush 参数化

**Files:**
- Create: `AIRadio.Desktop/Converters/BoolToAccentBrushConverter.cs`
- Modify: `ChatView.axaml.cs:101,126`、`PlayerView.axaml.cs:40,52,64,76` 删除 6 个旧定义；XAML 用 `ConverterParameter` 传色

**Interfaces:**
- Produces: `BoolToAccentBrushConverter`（`ConverterParameter` = "active,inactive" 两个色值，返回 `ISolidColorBrush`）

- [ ] **Step 1: 写失败测试**

追加到 `ConverterEquivalenceTests.cs`：
```csharp
[Fact]
public void BoolToAccent_ReturnsActiveOrInactiveBrush()
{
    var c = new BoolToAccentBrushConverter();
    var param = "#FF56F5C4,#FF333333";
    var active = c.Convert(true, typeof(object), param, null) as Avalonia.Media.ISolidColorBrush;
    var inactive = c.Convert(false, typeof(object), param, null) as Avalonia.Media.ISolidColorBrush;
    Assert.NotNull(active);
    Assert.NotNull(inactive);
    Assert.NotEqual(active!.Color, inactive!.Color);
}
```

- [ ] **Step 2: 跑确认失败 → Step 3: 实现**

`Converters/BoolToAccentBrushConverter.cs`:
```csharp
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AIRadio.Desktop.Converters;

/// <summary>ConverterParameter="activeHex,inactiveHex"；bool→对应 SolidColorBrush（缓存静态实例避免 GC）。</summary>
public class BoolToAccentBrushConverter : IValueConverter
{
    private static readonly BrushCache Cache = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string p || !p.Contains(',')) return Brushes.Gray;
        var parts = p.Split(',');
        var hex = (value is bool b && b) ? parts[0] : parts[1];
        return Cache.Get(hex);
    }
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;

    private sealed class BrushCache
    {
        private readonly Dictionary<string, SolidColorBrush> _m = new();
        public SolidColorBrush Get(string hex)
        {
            if (!_m.TryGetValue(hex, out var b))
            {
                b = new SolidColorBrush(Color.Parse(hex));
                _m[hex] = b;
            }
            return b;
        }
    }
}
```

- [ ] **Step 4: 跑测试通过**

- [ ] **Step 5: 替换 6 个旧 converter 的 XAML 引用**

把 `ChatView.axaml` / `PlayerView.axaml` 里原 `MicBackgroundConverter`/`ConversationModeBackgroundConverter`/`BoolToAccentBgConverter`/`BoolToAccentFgConverter`/`RepeatToBgConverter`/`RepeatToFgConverter` 的绑定改为 `BoolToAccentBrushConverter` + `ConverterParameter="<激活色>,<非激活色>"`（色值从各自旧实现复制）。保留 `RepeatIconConverter`、`MicIconConverter`、`ConversationModeIconConverter`（图标文本，语义不同）。

- [ ] **Step 6: 删旧定义 + 构建测试**

删除 `ChatView.axaml.cs:101,126`、`PlayerView.axaml.cs:40,52,64,76`。Run build + test → 0 错误、全过。

- [ ] **Step 7: Commit（需授权）**

```bash
git commit -m "refactor:BoolToAccent参数化合并六个强调色Converter"
```

---

## Task 6: Theme token PoC（验证 Avalonia 11 机制）

**Files:**
- Create: `AIRadio.Desktop/Themes/Colors.xaml`
- Modify: `AIRadio.Desktop/App.axaml`（合并资源字典）、`MainWindowViewModel.cs`（切换逻辑）

**目标：** spec §5.4.2 要求先做最小 PoC 验证 `RequestedThemeVariant` 在 11.3.9 能刷新 DynamicResource。

- [ ] **Step 1: 写最小 Colors.xaml（2 个 token 验证机制）**

`Themes/Colors.xaml`:
```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ThemeDictionaries>
        <ResourceDictionary x:Key="Light">
            <Color x:Key="TestToken">#FF000000</Color>
        </ResourceDictionary>
        <ResourceDictionary x:Key="Dark">
            <Color x:Key="TestToken">#FFFFFFFF</Color>
        </ResourceDictionary>
    </ThemeDictionaries>
</ResourceDictionary>
```

- [ ] **Step 2: App.axaml 合并字典**

在 `App.axaml` 的 `Application.Resources` 加 `<ResourceInclude Source="avares://AIRadio.Desktop/Themes/Colors.xaml"/>`。

- [ ] **Step 3: 临时验证控件**

在 MainWindow 任一 TextBlock 临时加 `Foreground="{DynamicResource TestToken}"`。

- [ ] **Step 4: 实现切换 + 运行验证**

`MainWindowViewModel`：在 `IsDarkMode` 的 setter 逻辑里（或 `ToggleThemeCommand`）加：
```csharp
Avalonia.Application.Current!.RequestedThemeVariant =
    IsDarkMode ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
```
Run: `dotnet run --project AIRadio.Desktop/AIRadio.Desktop.csproj`
手动切换主题，确认该 TextBlock 颜色随主题刷新。

- [ ] **Step 5: PoC 失败回退（仅当 Step 4 不刷新时）**

改用两文件 `Colors.Dark.xaml`/`Colors.Light.xaml` + 切换时 `Application.Current.Resources.MergedDictionaries` 增删。记录决策到本计划。

- [ ] **Step 6: 删除临时控件，保留 token 基础**

- [ ] **Step 7: Commit（需授权）**

```bash
git commit -m "feat:ThemeDictionaries主题切换PoC验证"
```

---

## Task 7: Theme token 完整提取 + 删命令式 theming

**Files:**
- Modify: `AIRadio.Desktop/Themes/Colors.xaml`（补全 token）、所有 `.axaml`（硬编码色→DynamicResource）
- Modify: `AIRadio.Desktop/Views/MainWindow.Theme.cs`（删 `ApplyChatMessageTheme:41`/`SetShellTextForeground:68`/`GetVisualDescendants` 嗅探）、`MainWindow.axaml.cs:46`（删 `ChatBubbleMaxWidth` 常量）

**目标：** spec §5.4 全部落地；删 `MaxWidth==380` 气泡嗅探。

- [ ] **Step 1: 提取颜色清单**

Run: `grep -oE "#FF[0-9A-Fa-f]{6}|#F[0-9A-Fa-f]{7}|#[0-9A-Fa-f]{8}" AIRadio.Desktop/Views/*.axaml | sort -u`
把结果按 spec §5.4.1 分类（Surface/Accent/Text/Border/State）写入 `Colors.xaml` 的 ThemeDictionaries 两套（Light/Dark 色值参照现有 `MainWindow.Theme.cs:23-37` 的明/暗映射）。

- [ ] **Step 2: 全部 axaml 硬编码色改 DynamicResource**

逐文件把 `Background="#FF030305"` 等改为 `Background="{DynamicResource SurfaceRoot}"`。工作量按 grep 清单逐项。

- [ ] **Step 3: 气泡改 Style class（消除 MaxWidth 嗅探）**

`MainWindow.axaml:325` 气泡 Border 加 `Classes="chat-bubble"`；在 `Window.Styles` 加：
```xml
<Style Selector="Border.chat-bubble">
    <Setter Property="Background" Value="{DynamicResource SurfaceBubble}"/>
    <Setter Property="CornerRadius" Value="4"/>
    <Setter Property="Padding" Value="14,10"/>
    <Setter Property="MaxWidth" Value="380"/>
</Style>
```

- [ ] **Step 4: 删除 MainWindow.Theme.cs 命令式 theming**

删除 `ApplyChatMessageTheme`、`SetShellTextForeground`、`IsChatBubble`(82)、`ChatBubbleMaxWidth` 常量及 `UpdateThemeColors` 里的 `GetVisualDescendants` 调用。`OnChatMessagesChanged`(axaml.cs:119) 里调用 `ApplyChatMessageTheme` 的行一并删除（Style 已自动应用）。

- [ ] **Step 5: 全量 build + test + 手动主题验证**

Run build/test → 0 错误、全过。Run app → 切换 Light/Dark，对照 spec §8.3 主题专项 checklist（播放器/气泡/抽屉/设置/状态条/时钟颜色全刷新）。

- [ ] **Step 6: Commit（需授权）**

```bash
git commit -m "refactor:Theme颜色token化并删除气泡嗅探"
```

---

## Task 8: StatusBar 新建（替换 FooterBar）

**Files:**
- Create: `AIRadio.Desktop/Views/StatusBar.axaml(.cs)`、`AIRadio.Desktop/Converters/RadioStateToTextConverter.cs`、`RadioStateToBrushConverter.cs`
- Modify: `MainWindow.axaml:457-464`（FooterBar → StatusBar）

**Interfaces:**
- Consumes: `MainWindowViewModel.CurrentState`
- Produces: `<local:StatusBar DataContext="{Binding}"/>`（绑主 VM 读 CurrentState）

- [ ] **Step 1: 创建两个状态 Converter**

`RadioStateToTextConverter.cs`：枚举→字符串（Idle→"AIRADIO FM"、Curating→"CURATING"、Searching→"SEARCHING"、Speaking→"SPEAKING"、Playing→"ON AIR"、Error→"ERROR"）。
`RadioStateToBrushConverter.cs`：枚举→DynamicResource 刷子 key 或固定色（绿/青/黄/紫/红/灰）。

- [ ] **Step 2: 写 Converter 单测（追加 ConverterEquivalenceTests）**

覆盖 6 个状态→正确文本/颜色。

- [ ] **Step 3: 创建 StatusBar UserControl**

`StatusBar.axaml`：Grid 两列（左 "AIRADIO FM" 标识、右 TextBlock 绑 `{Binding CurrentState, Converter={x:Static conv:RadioStateToTextConverter.Instance}}`，Foreground 用 `RadioStateToBrushConverter`）。`x:DataType="vm:MainWindowViewModel"`。

- [ ] **Step 4: 替换 MainWindow FooterBar**

`MainWindow.axaml:457-464` 的 `<Border x:Name="FooterBar">...` 整段替换为 `<local:StatusBar Grid.Row="6" DataContext="{Binding}"/>`。

- [ ] **Step 5: build + test + 手动**

Run → 0 错误、全过。Run app → 操作播放/搜索/聊天，确认 StatusBar 文本随 `CurrentState` 变化。

- [ ] **Step 6: Commit（需授权）**

```bash
git commit -m "feat:StatusBar显示统一电台状态替换FooterBar"
```

---

## Task 9: TitleBar UserControl

**Files:**
- Create: `AIRadio.Desktop/Views/TitleBar.axaml(.cs)`
- Modify: `MainWindow.axaml:64-90`（TitleBar 区 → 控件引用）；窗口 chrome handler 留 `MainWindow.axaml.cs`

- [ ] **Step 1: 提取 TitleBar**

把 `MainWindow.axaml:64-90`（`<Border x:Name="TitleBar">...`，含品牌 + min/close 按钮）移入新 `TitleBar.axaml`。min/close 按钮的 `Click="OnMinimizeClicked"`/`OnCloseClicked` 改为控件内事件，`TitleBar.axaml.cs` 调用 `((Window)VisualRoot).WindowState`/`Close()`。`PointerPressed` 拖拽（`OnTitleBarPointerPressed`）也移入并调用 `((Window)VisualRoot).BeginMoveDrag`。

- [ ] **Step 2: MainWindow 替换为引用**

`<Border x:Name="TitleBar" ...>` 段替换为 `<local:TitleBar Grid.Row="0"/>`。

- [ ] **Step 3: 删 MainWindow.axaml.cs 对应 handler**

删 `OnTitleBarPointerPressed`、`OnMinimizeClicked`、`OnCloseClicked`（已迁入 TitleBar）。保留 `OnClosed`、`Dispose`。

- [ ] **Step 4: build + 手动验证窗口拖拽/min/close**

- [ ] **Step 5: Commit（需授权）**

```bash
git commit -m "refactor:提取TitleBar控件并迁移窗口chrome"
```

---

## Task 10: ClockStage + 时钟迁移到 VM

**Files:**
- Create: `AIRadio.Desktop/Views/ClockStage.axaml(.cs)`
- Modify: `MainWindowViewModel.cs`（加 `Now`）、`MainWindow.axaml:144-174`

**Interfaces:**
- Consumes: `MainWindowViewModel.Now`（[Reactive] DateTimeOffset，1s 推进）
- Produces: `<local:ClockStage DataContext="{Binding}"/>`

- [ ] **Step 1: VM 加 Now 属性 + timer**

`MainWindowViewModel`：
```csharp
[Reactive] public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

private readonly IDisposable _clockSub;
// ctor 末尾：
_clockSub = Observable.Interval(TimeSpan.FromSeconds(1))
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(_ => Now = DateTimeOffset.Now);
// Dispose 里 _clockSub.Dispose();
```

- [ ] **Step 2: 创建 ClockStage UserControl**

`ClockStage.axaml`：搬入 `MainWindow.axaml:144-174`（ClockDots + Starfield + 双 SpectrumView + ClockDisplay/DayDisplay/DateDisplay），但 3 个 TextBlock 改绑：
```xml
<TextBlock Text="{Binding Now, StringFormat='HH:mm'}"/>
<TextBlock Text="{Binding Now, StringFormat='dddd'}"/>
<TextBlock Text="{Binding Now, StringFormat='MM月dd日'}"/>
```
删 `x:Name="ClockDisplay/DayDisplay/DateDisplay"`。`x:DataType="vm:MainWindowViewModel"`。

- [ ] **Step 3: MainWindow 替换 + 删 code-behind 时钟**

`MainWindow.axaml:144-174` 替换为 `<local:ClockStage Grid.Row="1"/>`。删 `MainWindow.axaml.cs` 的 `_clockTimer`、`StartClock`(129)、`UpdateClock`(137-150)。

- [ ] **Step 4: build + test + 手动验证时钟走动**

- [ ] **Step 5: Commit（需授权）**

```bash
git commit -m "refactor:时钟迁移到VM并提取ClockStage控件"
```

---

## Task 11: 采用 PlayerView

**Files:**
- Modify: `MainWindow.axaml:177-265`（PlayerDeck 内联 → `<local:PlayerView>`）、`PlayerView.axaml(.cs)`、`PlayerViewModel.cs`

**核查（spec §5.6）：** PlayerView 自带 5 converter（Task 5 已合并处理）、PlayerView markup 与内联段 diff。

- [ ] **Step 1: diff PlayerView 与内联 PlayerDeck**

Run: 对比 `PlayerView.axaml` 与 `MainWindow.axaml:177-265`，记录差异（控件、绑定、样式）到本计划。目标：让 PlayerView 等价于内联行为。

- [ ] **Step 2: 调和 PlayerView markup**

把内联 PlayerDeck 的细节（LIKE/NOPE/SIM/CALM/FIRE 按钮行、收藏按钮、进度条 PointerReleased）补进 PlayerView，确保绑定 `PlayerVM.*` + 主 VM 命令（`ToggleCurrentFavoriteCommand` 等）通过 DataContext 链可达。`PointerReleased` seek/volume 迁为 PlayerView code-behind 事件 → 调 `PlayerVM.SeekTo`/`Volume`。

- [ ] **Step 3: MainWindow 引用 PlayerView**

`MainWindow.axaml:177-265` 替换为 `<local:PlayerView Grid.Row="2" DataContext="{Binding PlayerVM}"/>`（注意：LIKE/SIM 等命令在主 VM，需 PlayerView 通过 `{Binding $parent[Window].((vm:MainWindowViewModel)DataContext).LikeCurrentTrackCommand}` 或改为主 VM 暴露——决策记录到计划）。

- [ ] **Step 4: build + test + 手动播放验证**

跑 app → 播放/暂停/上下首/进度/音量/收藏/LIKE 全部正常。

- [ ] **Step 5: Commit（需授权）**

```bash
git commit -m "refactor:MainWindow启用PlayerView替换内联播放器"
```

---

## Task 12: 采用 ChatView（含 InputDeck + mic + scroll + DJ cue）

**Files:**
- Modify: `MainWindow.axaml:315-454`（chat + 输入 + 浮层 → `<local:ChatView>`）、`ChatView.axaml(.cs)`

**核查（spec §5.6）：** ChatView 自带 converter 注册（Task 4 已处理）、自带 scroll-to-end(`:41-47`)、`MessageRoleToBrushConverter`(`:66`) 未用。

- [ ] **Step 1: diff ChatView 与内联 chat 段**

对比 `ChatView.axaml` 与 `MainWindow.axaml:315-428`（消息列表）+ `339-428`（状态浮层）+ `432-454`（InputDeck）。记录差异。

- [ ] **Step 2: 调和 ChatView — 合并 InputDeck + 浮层**

把 InputDeck（输入框 + mic + send）、状态浮层（ShowStatusNotice/IsListening/IsRecognizing）并入 ChatView。`OnChatInputKeyDown`(axaml.cs:168)、`OnMicPointer*`(176-209)、`ResetMicButton`(211)、`OnChatMessagesChanged`(119-127) 的 scroll 逻辑迁入 ChatView.axaml.cs（合并其已有 scroll-to-end）。DJ 头像 cue `OnDjVisualCue`(259) 也迁入（订阅 VM 事件 + `Animations.PlayBounceAsync`）。

- [ ] **Step 3: MainWindow 引用 ChatView**

`MainWindow.axaml:315-454` 替换为 `<local:ChatView Grid.Row="4" DataContext="{Binding ChatVM}"/>`（DJ cue 事件从主 VM 暴露或 ChatVM 暴露）。

- [ ] **Step 4: 删 MainWindow code-behind 对应 handler**

删 `OnChatInputKeyDown`、`OnMicPointer*`、`ResetMicButton`、`OnChatMessagesChanged`、`OnDjVisualCue`、`OnSearchKeyDown`、相关字段（`_micButton`/`_chatHandler`/`_djVisualCueHandler`）。

- [ ] **Step 5: build + test + 手动聊天/麦克风验证**

跑 app → 文字聊天点歌、hold-to-talk 麦克风、状态浮层、DJ cue 动画全部正常。

- [ ] **Step 6: Commit（需授权）**

```bash
git commit -m "refactor:MainWindow启用ChatView合并输入与麦克风逻辑"
```

---

## Task 13: 采用 PlaylistView（LibraryDrawer）

**Files:**
- Modify: `MainWindow.axaml:471-609`（library drawer → `<local:PlaylistView>`）、`PlaylistView.axaml(.cs)`、`PlaylistViewModel.cs`

**核查（spec §5.6）：** PlaylistView 自带 `InvertBoolValueConverter`（Task 4 已合并）、`TabBg/Fg`、6× ctor 实例化。

- [ ] **Step 1: 加 ImportFilesCommand 到 PlaylistVM**

`PlaylistViewModel`：
```csharp
public ReactiveCommand<Unit, Unit> ImportFilesCommand { get; }
// ctor: ImportFilesCommand = ReactiveCommand.CreateFromTask(ImportFilesAsync);
private async Task ImportFilesAsync()
{
    var files = await Views.FilePickerHelper.PickAudioFilesAsync(
        Avalonia.Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleLifetimeLifetime dl ? dl.MainWindow : null!);
    // 复用现有 AddFiles 逻辑
}
```
> 实际签名以 `FilePickerHelper.PickAudioFilesAsync` 现有定义为准（grep 确认参数类型）。

- [ ] **Step 2: diff PlaylistView 与内联 drawer + 调和**

把内联 library drawer 的三 ListBox 模板、搜索框、导入按钮并入 PlaylistView。导入按钮 `Click="OnImportFiles"` 改 `Command="{Binding ImportFilesCommand}"`。

- [ ] **Step 3: MainWindow 引用 PlaylistView**

`MainWindow.axaml:471-609` 的 library drawer Border 内容替换为 `<local:PlaylistView DataContext="{Binding PlaylistVM}"/>`（外层 overlay Border + IsVisible 保留）。

- [ ] **Step 4: 删 MainWindow OnImportFiles + OnSearchKeyDown**

- [ ] **Step 5: build + test + 手动库/搜索/导入验证**

跑 app → 开库、切 Playlist/Favorites/Search tab、搜索、导入本地文件、收藏切换全部正常。

- [ ] **Step 6: Commit（需授权）**

```bash
git commit -m "refactor:MainWindow启用PlaylistView并迁移导入命令"
```

---

## Task 14: CharacterPicker + Starfield 自订阅

**Files:**
- Create: `AIRadio.Desktop/Views/CharacterPicker.axaml(.cs)`
- Modify: `MainWindow.axaml:634-664`、`MainWindow.axaml.cs:104,108-109,267-284`、`StarfieldView.axaml.cs`

- [ ] **Step 1: 提取 CharacterPicker**

把 `MainWindow.axaml:634-664` 的角色 ItemsControl 移入 `CharacterPicker.axaml`，`x:DataType="vm:MainWindowViewModel"`，绑 `Characters` + `SelectCharacterCommand`。`OnCharacterSelected`(axaml.cs:267-284) 切换动画迁入 CharacterPicker.axaml.cs。

- [ ] **Step 2: MainWindow 引用**

`MainWindow.axaml:634-664` 替换为 overlay Border 内 `<local:CharacterPicker DataContext="{Binding}"/>`。

- [ ] **Step 3: StarfieldView 自订阅 SpectrumVM**

`MainWindow.axaml.cs:104,108-109` 的 `_starfieldVisSub` + `PushSpectrum` 中转删除。改为 `StarfieldView.axaml.cs` 通过自身 DataContext（绑 `SpectrumVM`）订阅 `SpectrumReceived` 事件直接 `PushSpectrum`，并在 `SettingsVM.EnableStarfield` 变化时自管 IsVisible（或绑主 VM）。

- [ ] **Step 4: build + test + 手动切角色验证**

跑 app → 切 DJ 角色、动画、头像字母、星空频谱推送全部正常。

- [ ] **Step 5: Commit（需授权）**

```bash
git commit -m "refactor:提取CharacterPicker并Starfield自订阅频谱"
```

---

## Task 15: 收尾验证 + 文档同步

**Files:**
- Verify: `MainWindow.axaml` 行数、`MainWindow.axaml.cs` 行数
- Modify（可选）: `README.md`、`ai-radio-plan.md` 标注进度

- [ ] **Step 1: 行数门槛**

Run: `wc -l AIRadio.Desktop/Views/MainWindow.axaml AIRadio.Desktop/Views/MainWindow.axaml.cs`
Expected: axaml < 200、axaml.cs < 100。若超出，回看是否有遗漏的内联段未提取。

- [ ] **Step 2: 全量构建 + 测试**

Run: `dotnet build AIRadio.Desktop/AIRadio.Desktop.csproj -v:minimal` → 0 警告 0 错误。
Run: `dotnet test AIRadio.Desktop.Tests/AIRadio.Desktop.Tests/AIRadio.Desktop.Tests.csproj -v:minimal` → 141 + 新增（RadioState 6 + Converter 等价 + HasFailure 1 + 状态 Converter）全过。

- [ ] **Step 3: 全量手动 checklist（spec §8.3）**

启动 → 欢迎语 → 播放 → 暂停/继续 → 上下首 → 聊天点歌 → hold-to-talk → 切主题（Light/Dark 全区域颜色）→ 开库/搜索/收藏/导入 → 开设置 → 切 DJ 角色 → 关闭。每步对照 StatusBar 状态文本。

- [ ] **Step 4: 更新文档**

`README.md` 「已知技术债」移除已解决项；`ai-radio-plan.md` P2 标注状态机/视图重构完成。

- [ ] **Step 5: Commit（需授权）**

```bash
git commit -m "docs:同步视图重构与状态机完成状态"
```

---

## Self-Review（writing-plans 要求）

**1. Spec 覆盖：**
- §5.1 UserControl 划分 → Task 8(StatusBar)/9(TitleBar)/10(ClockStage)/11(PlayerView)/12(ChatView)/13(PlaylistView)/14(CharacterPicker) ✓
- §5.2 状态机 → Task 1/2/3 ✓
- §5.3 Converter 合并 → Task 4/5 ✓
- §5.4 Theme → Task 6/7 ✓
- §5.5 code-behind 迁移 → 散见 Task 9-14，每项映射到具体 Task ✓
- §5.6 差异调和 → Task 11/12/13 各有 diff 步骤 ✓
- §8 测试 → Task 2/3/4/5/8 含测试 + Task 15 全量 ✓
- §9 验证标准 → Task 15 ✓

**2. Placeholder 扫描：** 无 TBD/TODO；UI 搬移步骤给精确文件:行操作（markup 已存在，搬移非新写，符合 DRY）；逻辑代码（状态机/Converter/VM）给完整可编译代码。Task 11/13 的 `ImportFilesAsync` / PlayerView 命令 DataContext 链标注"以现有签名为准/决策记录"——这些是依赖运行时 diff 的实施细节，非 placeholder。

**3. 类型一致性：** `RadioState`、`CurrentState`、`HasFailure`、`DeriveRadioState`、`InverseBoolConverter`、`MessageAlignConverter`、`BoolToAccentBrushConverter`、`RadioStateToTextConverter`、`Now`、`ImportFilesCommand` 在各 Task 间命名一致。

**执行交接：** 见下条消息。
