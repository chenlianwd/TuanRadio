# 零配置 DJ 智能台呼兜底与国内 LLM 极简预置向导设计（P0）

日期：2026-11-09  
状态：已实施（阶段 1/2 全部落地，全量 1167 自动化测试全部通过）  
关联规划：[`AI电台竞品差距与迭代建议.md`](../../AI电台竞品差距与迭代建议.md) 第四章 P0 核心任务

---

## 1. 背景、目标与非目标

### 背景与痛点
1. **新用户冷启动“沉默/复读”痛点**：新用户初次下载安装 TuanRadio 时通常尚未配置 LLM API Key。此时 `DJService.GenerateTrackIntroductionAsync` 走 `BuildFallbackIntroduction(next)`，但现有实现仅返回单句静态模板（*“接下来为你带来《...》 - ...，一起听听这段情绪。”*），不区分**电台开播首曲**与**曲间串场**，也不利用应用内已有的**时段（6 档时间带）**、**主播昵称**与 **Open-Meteo 实时天气快照**，缺乏复古电台开箱即听的陪伴感与生命力。
2. **国内高性价比 LLM 配置门槛**：设置页目前仅提供 `OpenAI 兼容格式`、`Anthropic 兼容格式`、`本地模型` 三个协议级选项。普通用户想接入 DeepSeek、智谱 GLM、Kimi、通义千问或硅基流动时，需自行查阅各家 API 文档寻找 `Base URL` 与 `Model` 标识符，存在明显的配置摩擦。

### 目标
- **零 Key 开箱即有电台味（Zero-Config DJ Banter）**：未配置 LLM（或 LLM 请求异常/截断回退）时，DJ 串场与开台播报结合**当前时刻/时间段（清晨/上午/午后/傍晚/夜晚/深夜）**、**实时天气快照（城市、天气状况、气温）**、**主播人设名称**以及**上一首/下一首曲目信息**，通过内置的多套精编双语广播模板 + Edge TTS 自然播报，且连续串场自动轮转句式不复读。
- **一键点亮主流大模型（LLM Quick Presets）**：在设置页「AI 服务」页签顶部提供国内主流高性价比厂商（DeepSeek、智谱 GLM、Kimi、通义千问、硅基流动、本地 Ollama）快捷预置按钮，一键自动填充对应协议、`Base URL` 与推荐 `Model`，用户只需粘贴 API Key 并保存即可完成配置。

### 非目标（本轮不做）
- 不引入第三方收费或内置共享代理 Key（避免硬编码密钥泄漏与滥用风控）。
- 不改变 `llm_profiles` 的磁盘持久化 JSON 结构与 Windows 凭据管理器槽位（仍保持 `openai` / `anthropic` / `local` 三大协议槽位，预置仅作为表单快捷填充层）。
- 不把规则兜底台词写入 `_recentOnAirLines` 短期记忆（保持“仅真实 LLM 串场入队防复读”的既有不变量）。

---

## 2. 模块一：广播员规则台呼/报时/天气智能兜底（Zero-Config DJ Banter）

### 2.1 核心机制与签名设计

在 `AIRadio.Desktop/Services/DJService.cs` 中，将 `_weatherSnapshot` 声明为 `volatile`，并将原有单参 `BuildFallbackIntroduction(Track next)` 升级为感知完整电台上下文的多维模板生成器，同时暴露 `internal static` 纯函数入口供单元测试确定性直驱：

```csharp
private int _fallbackVariantCounter;

private DJScript BuildFallbackIntroduction(Track? current, Track next)
{
    var variant = Interlocked.Increment(ref _fallbackVariantCounter);
    return BuildFallbackIntroduction(_profile, current, next, DateTime.Now, _weatherSnapshot, variant);
}

internal static DJScript BuildFallbackIntroduction(
    DJProfile profile,
    Track? current,
    Track next,
    DateTime now,
    WeatherInfo? weather,
    int variant = 0)
```

`GenerateTrackIntroductionAsync(Track? current, Track next, CancellationToken cancellationToken)` 内的三处回退调用点（未配置 LLM 前置拦截、歌名被截断回退、异常 catch 回退）统一传入 `(current, next)`。

### 2.2 上下文四维联动与边界规则

1. **元数据安全消毒与空字段优雅降级**：
   - `next.Title`、`next.Artist`、`current?.Title`、`current?.Artist`、`profile.Name` 及 `weather?.LocationName` 全部经 `PromptHygiene.StripControlMarkers(...)` 清洗。
   - **主播名兜底**：若 `profile.Name` 为空或空白，按 `profile.Language` 回退 `"Lumen"`（英文）或 `"小音"`（中文）。
   - **未知歌手免播报规则**：不使用依赖全局 `AppLanguage.Current` 的 `Track.DisplayArtist`（避免与 `profile.Language` 产生中英混杂，且避免播报出“为您带来未知艺术家的《晴天》”这种违和台词）。判定 `HasKnownArtist(artist)`（非空且非 `"未知"` / `"未知艺术家"` / `"Unknown"` / `"Unknown artist"`）：有歌手时念 `“{nextArtist}的《{nextTitle}》”` / `"\"{nextTitle}\" by {nextArtist}"`，无歌手时自然简化为 `“这首《{nextTitle}》”` / `"\"{nextTitle}\""`。
   - **上一首标题缺失守卫**：当 `current != null` 但清洗后 `current.Title` 为空时，不输出空的 `《》`，自动选用不引述上一首歌名的过渡句式。
2. **场景分支 A：开台首播台呼（`current == null`）**
   - 触发时机：应用启动 `StartStationPlaybackAsync` 首次开播（`stationOpening: true`）。
   - 结构：**[时间报时 + 时段问候] + [可选实时天气感知] + [电台与主播台呼] + [首曲引出]**。
   - 天气子句（当 `weather != null` 且 `Kind != WeatherKind.Unknown` 时生成，否则平滑省略）：
     - 中文：若 `LocationName` 非空则为 `"{location}现在{kind}{temp}，"`（如 `“上海现在有雨，气温 18°C，”`），无城市名则为 `“窗外现在{kind}{temp}，”`；`double.IsNaN(weather.TemperatureC)` 时自动省略温度段。
     - 英文：`"{kind}{temp} in {location}. "`（如 `"Raining, 18°C in Shanghai. "`）。
   - 多套变体轮转（按 `Math.Abs(variant) % count` 取模，以下为中文示例）：
     - 变体 0：`“现在是 {HH:mm}，{weatherClause}欢迎收听 TuanRadio，我是{djName}。{timeBand}的第一首歌，为你带来{nextTrackPhrase}。”`
     - 变体 1：`“{timeBandGreeting}，现在是北京时间 {HH:mm}。{weatherClause}这里是 TuanRadio，我是{djName}，先用{nextTrackPhrase}陪你开启这段时光。”`
     - 变体 2：`“锁定 TuanRadio，此刻是 {HH:mm}。{weatherClause}我是{djName}，电台已经上线，第一首为你送上{nextTrackPhrase}。”`
3. **场景分支 B：曲间串场（`current != null`）**
   - 触发时机：电台自动续播到达串场间隔（每 4 首）或跨越时间带（如午后→傍晚）时。
   - 设计克制：**曲间串场不机械报出具体时刻数字**（避免每次开口都像报时器），而是自然结合**当前时间带氛围（6 档 `GetTimeBand(now.Hour)`）**、**天气意象（有天气快照时偶发点缀）**与**上一首 `current` → 下一首 `next` 的衔接**。
   - 模板池设计（中英双语，按 `variant` 在多套模板间轮转防连续复读）：
     - 变体 0（经典承接）：`“刚才那首《{prevTitle}》先告一段落，在这段{timeBand}时光里，接下来为你带来{nextTrackPhrase}，一起听听这段情绪。”`
     - 变体 1（余韵过渡 + 天气点缀）：若存在天气快照则融入天气意象（如 `“伴着窗外的{weatherKind}，听完了《{prevTitle}》，接下来让{nextTrackPhrase}继续陪你。”`），无天气则为 `“《{prevTitle}》的余韵还没散去，{timeBand}的电波里，继续为你送上{nextTrackPhrase}。”`
     - 变体 2（时段契合）：`“告别了《{prevTitle}》，{timeBand}的节奏刚刚好。接下来这首是{nextTrackPhrase}。”`
     - 变体 3（主播陪伴）：`“这里是 TuanRadio，我是{djName}。听过《{prevTitle}》之后，换上{nextTrackPhrase}，希望你会喜欢。”`
4. **情绪与动效保持**：
   - 返回的 `DJScript` 保持 `Emotion = "happy"`, `Expression = "smile"`, `Motion = "wave"`（与现有单测契约及主播登场动效完全兼容）。

---

## 3. 模块二：国内高性价比 LLM 极简预置向导（LLM Quick Presets）

### 3.1 预置厂商清单与标准参数

在 `SettingsViewModel` 中定义不可变预置表（全部映射到现有三大底层协议 `openai` / `anthropic` / `local`，零破坏持久化与凭据槽）：

| Preset Id | 中文显示名 | 英文显示名 | 映射协议 (`SelectedProvider`) | 预填 `BaseUrl` | 预填推荐 `Model` |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `deepseek` | `DeepSeek` | `DeepSeek` | `openai` | `https://api.deepseek.com/v1` | `deepseek-chat` |
| `zhipu` | `智谱 GLM` | `Zhipu GLM` | `openai` | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-flash` |
| `kimi` | `Kimi (月之暗面)` | `Kimi (Moonshot)` | `openai` | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` |
| `qwen` | `通义千问 (百炼)` | `Qwen (DashScope)` | `openai` | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-plus` |
| `siliconflow` | `硅基流动` | `SiliconFlow` | `openai` | `https://api.siliconflow.cn/v1` | `Qwen/Qwen2.5-7B-Instruct` |
| `ollama` | `Ollama 本地` | `Local Ollama` | `local` | `http://localhost:11434/v1` | `""`（触发本地模型自动枚举） |

### 3.2 ViewModel 交互契约（`SettingsViewModel.cs`）

1. **新增类型、属性与命令**：
   - `public sealed record LlmPresetOption(string Id, string DisplayName, string Provider, string BaseUrl, string DefaultModel);`
   - `[Reactive] public partial List<LlmPresetOption> LlmPresets { get; set; } = new();`（在 `RebuildLocalizedOptionLists()` 中按当前语言重建）。
   - `public ReactiveCommand<string, Unit> ApplyLlmPresetCommand { get; }`
2. **`ApplyLlmPreset(string? presetId)` 执行顺序与不变量**：
   - 若 `_isLoading` 为 `true` 或找不到匹配的 `presetId`，安全无操作返回。
   - **第一步：切换 `SelectedProvider = preset.Provider`**。若此前处于不同协议（如从 `local` 切到 `openai`），`_llmProfileSwapSub` 会同步先执行 `SwapLlmProfile` 将旧协议表单存回档案，并载入目标协议槽的档案与凭据槽 `ApiKey`。
   - **第二步：覆写表单 `BaseUrl = preset.BaseUrl` 与 `Model = preset.DefaultModel`，并同步更新 `_llmProfiles[preset.Provider]` 缓存中的 `BaseUrl` 与 `Model`**。在 `SelectedProvider` 之后赋值，确保预置值不会被 `SwapLlmProfile` 的旧档案值冲掉，且防止首次切槽时异步完成的 `LoadProfileKeyFromSlotAsync` 用旧 `cached.BaseUrl` 回写档案；同时**保留当前已有的 `ApiKey`**。
   - **第三步：经 `SetStatusMessage(Func<string>)` 设置可随语言重算的引导提示**：
     - 必须走 `SetStatusMessage(() => ...)` 而非直接写 `StatusMessage` 属性，确保用户在设置页切换中英文语言时 `_statusMessageFactory` 能同步刷新提示语：
     - 云端预置：`SetStatusMessage(() => AppLanguage.T($"已填入 {GetPresetDisplayName(preset.Id)} 预置配置，填入 API Key 后点击测试或保存即可生效", $"Applied {GetPresetDisplayName(preset.Id)} preset; enter your API key, then Test or Save"));`
     - 本地 Ollama 预置：`SetStatusMessage(() => AppLanguage.T("已切到本地 Ollama 预置，确保本机 Ollama 已运行后点击测试或保存", "Applied Local Ollama preset; make sure Ollama is running locally, then Test or Save"));`
   - **严守安全边界**：`ApplyLlmPreset` 仅修改设置表单字段，**绝不直接调用 `ConfigureLlm()`**（与现有 `SelectingLocalProvider_DoesNotTouchLiveServiceConfig` 契约一致，防止用户在设置页点按预置按钮时打断后台正在播放的电台会话）。

### 3.3 本地化词条与 UI 布局设计

1. **字符串表词条（[`AIRadio.Desktop.Music/Services/AppLanguage.cs`](../../AIRadio.Desktop.Music/Services/AppLanguage.cs)）**：
   - 在 `ZhStrings` 与 `EnStrings` 中成对新增 `["S_LlmPresetsLabel"]`（`"快速预置（一键填入地址与推荐模型）"` / `"Quick presets (one-click endpoint & model)"`），满足 `AppLanguageTests.StringTables_HaveIdenticalKeySetsAndNonEmptyValues` 奇偶门禁。
2. **XAML 布局（[`AIRadio.Desktop/Views/SettingsView.axaml`](../../AIRadio.Desktop/Views/SettingsView.axaml)）**：
   - 在 `PageAi`（`StackPanel x:Name="PageAi"`）顶部、协议 `ComboBox` 上方，新增快速预置区块：
     ```xml
     <StackPanel Spacing="4">
         <TextBlock Text="{DynamicResource S_LlmPresetsLabel}" Foreground="{DynamicResource C_FFB3B3B3}" FontSize="12"/>
         <ItemsControl x:Name="LlmPresetItems" ItemsSource="{Binding LlmPresets}">
             <ItemsControl.ItemsPanel>
                 <ItemsPanelTemplate>
                     <WrapPanel/>
                 </ItemsPanelTemplate>
             </ItemsControl.ItemsPanel>
             <ItemsControl.ItemTemplate>
                 <DataTemplate DataType="vm:LlmPresetOption">
                     <Button Content="{Binding DisplayName}"
                             Command="{Binding $parent[ItemsControl].((vm:SettingsViewModel)DataContext).ApplyLlmPresetCommand}"
                             CommandParameter="{Binding Id}"
                             Padding="10,4" FontSize="11" Margin="0,0,6,6" CornerRadius="6"/>
                 </DataTemplate>
             </ItemsControl.ItemTemplate>
         </ItemsControl>
     </StackPanel>
     ```
   - 采用标准 Fluent `Button`（不写死自定义背景色对），天然适配 4 套主题 × 深浅双变体的 WCAG 对比度门禁；
   - `DataTemplate` 显式声明 `DataType="vm:LlmPresetOption"`，命令采用与同文件 `WeatherCitySuggestionList` 一致的 `$parent[ItemsControl].((vm:SettingsViewModel)DataContext)` 编译绑定写法；
   - 保持原有 `SelectedProvider` `ComboBox`、`ApiKey` `TextBox`、`BaseUrlInput`、`ModelInputBox` 的控件名称、层级和绑定属性完全不变。

---

## 4. 测试计划（Test Plan）

### 4.1 `DJServiceTests`
1. **开台首播台呼（`current == null`）含报时、主播名与歌名**：
   - 验证 `DJService.BuildFallbackIntroduction(profile, current: null, next, now, weather)` 输出包含格式化时间（`HH:mm`）、主播名称 `profile.Name`、以及完整的 `next.Title` 与 `next.Artist`。
2. **开台与串场实时天气注入与缺失降级**：
   - 传入 `new WeatherInfo(WeatherKind.Rain, 18, "上海")` 时，中文/英文台词包含城市、天气现象与 `18°C`；
   - 传入 `weather: null` 或 `TemperatureC: double.NaN` 时，不出现 `"NaN"`、不残留断裂标点，台词自然通顺。
3. **曲间串场（`current != null`）承上启下与时段感知**：
   - 验证传入 `current` 与 `next` 时，台词包含上一首歌名 `current.Title` 与下一首歌名 `next.Title`、不机械报出具体时刻 `HH:mm`，且在不同时间带与不同 `variant` 下生成不同句式。
4. **未知歌手与空字段防御**：
   - 当 `next.Artist` 为空或 `"未知艺术家"` 时，台词不出现违和的 `"未知艺术家的"` 或 `"by Unknown artist"`；当 `current.Title` 为空时，台词不出现空书名号 `《》`。
5. **连续调用防复读轮转**：
   - 连续调用实例方法 `GenerateTrackIntroductionAsync`（未配置 LLM），验证相邻两次返回的 `DJScript.Text` 自动轮转不重样。
6. **元数据消毒与不入短期记忆回归**：
   - 含 `<cmd>` 控制标记的脏歌名经兜底生成后被干净剥除；
   - 更新 `GenerateTrackIntroductionAsync_FallbackTemplateNotRecorded`（原第 315 行硬编码断言旧静态句 `"接下来为你带来"`，更新为断言含歌名 `"《晴天》"` 且 `nextPrompt` 不包含 `fallback.Text` 及 `"最近几次串场"`），锚定兜底台词绝不进入 `_recentOnAirLines`。

### 4.2 `SettingsViewModelTests` & `SettingsNavTests`
1. **一键预置填充（`ApplyLlmPresetCommand`）全厂商覆盖**：
   - 遍历 `deepseek` / `zhipu` / `kimi` / `qwen` / `siliconflow` / `ollama`，验证执行后 `SelectedProvider`、`BaseUrl`、`Model` 精确匹配预置表，且 `StatusMessage` 给出对应引导文案。
2. **跨协议切换顺序与异步凭据回填防覆盖**：
   - 先处于 `local`（或 `anthropic`）协议，执行 `ApplyLlmPresetCommand("deepseek")`，验证最终 `SelectedProvider == "openai"` 且 `BaseUrl == "https://api.deepseek.com/v1"`、`Model == "deepseek-chat"`（既不被 `SwapLlmProfile` 冲掉，也不被慢速返回的 `LoadProfileKeyFromSlotAsync` 覆盖）。
3. **保留已有 API Key、语言联动与不触碰运行中服务**：
   - 当 `openai` 槽已有 `ApiKey = "sk-existing"` 时，在 `deepseek` 与 `zhipu` 预置间切换，`ApiKey` 保持 `"sk-existing"` 不丢失；
   - 切换界面语言（`AppLanguage.Apply("en")`）后，`LlmPresets` 显示名与 `StatusMessage` 同步切换为英文；
   - 执行预置命令时不触发 `_llmService.Configure(...)`。
4. **UI 端到端接线（`SettingsNavTests`）**：
   - 锚定 `SettingsView` 内 `LlmPresetItems` 控件存在且渲染出 6 个预置项。

---

## 5. 实施阶段拆分

1. **阶段 1：DJ 规则台呼/报时/天气兜底落地**
   - 升级 `DJService.BuildFallbackIntroduction`（实例轮转 + `internal static` 多维模板生成）；
   - 更新并新增 `DJServiceTests` 单元测试，运行测试验证全绿。
2. **阶段 2：国内 LLM 极简预置向导落地**
   - 在 `AIRadio.Desktop.Music/Services/AppLanguage.cs` 的 `ZhStrings` / `EnStrings` 成对补充 `S_LlmPresetsLabel`；
   - 在 `SettingsViewModel.cs` 实现 `LlmPresets` 与 `ApplyLlmPresetCommand`；
   - 在 `SettingsView.axaml` 的 `PageAi` 顶部接入 `LlmPresetItems` 预置按钮栏；
   - 补充 `SettingsViewModelTests` 与 `SettingsNavTests` 用例。
3. **阶段 3：全量验证与文档同步**
   - 运行双构建变体与全量单元测试，确保 0 警告 0 错误；
   - 同步更新 `AI电台竞品差距与迭代建议.md` 进度勾选与 `AGENTS.md`。

---

## 6. 计划自审记录（Self-Review）

**第 1 轮（对照代码库与单测基线核实，修正 6 处）**：
1. **修正多语言词条落点路径**：初版实施拆分误写为 `Strings.zh-CN.axaml` / `Strings.en-US.axaml`；核对代码库确认本项目无 `.axaml` 字符串文件，所有 `S_*` 词条集中定义在 `AIRadio.Desktop.Music/Services/AppLanguage.cs` 的 `ZhStrings` 与 `EnStrings` 字典中，并受 `AppLanguageTests.StringTables_HaveIdenticalKeySetsAndNonEmptyValues` 奇偶校验保护。已修正为在 `AppLanguage.cs` 成对添加 `S_LlmPresetsLabel`。
2. **修正 `StatusMessage` 语言切换刷新链路**：`SettingsViewModel` 在 `AppLanguage.Changed` 时会调用 `_statusMessageFactory()` 重算状态栏文本。若 `ApplyLlmPreset` 直接对 `StatusMessage` 赋静态字符串，切语言时会被旧 `_statusMessageFactory` 覆盖或停留旧语言。已修正为统一走 `SetStatusMessage(() => AppLanguage.T(...))`。
3. **修补跨协议切换时 `LoadProfileKeyFromSlotAsync` 的异步档案回写竞态**：当从 `local` 首次切向 `openai` 预置且缓存中尚无 `ApiKey` 时，`SwapLlmProfile` 会发起异步 `LoadProfileKeyFromSlotAsync("openai")`，其完成回调会用 `_llmProfiles["openai"]` 中的 `cached.BaseUrl` / `cached.Model` 重写字典。已在 §3.2 明确：`ApplyLlmPreset` 在设置 `BaseUrl` / `Model` 后立即同步更新 `_llmProfiles[preset.Provider]` 的 `BaseUrl` 与 `Model`，杜绝异步回填把档案冲回旧空值。
4. **规避 `Track.DisplayArtist` 的语言混用与“未知艺术家”播报违和感**：`Track.DisplayArtist` 依赖全局 `AppLanguage.Current` 而非 DJ 的 `profile.Language`，且在无歌手曲目上会拼出“为你带来未知艺术家的《XXX》”。已在 §2.2 增加 `HasKnownArtist` 守卫：无已知歌手时自然简化为“这首《XXX》”，并增加 `current.Title` 为空时的过渡句式守卫与 `profile.Name` 空值兜底。
5. **对齐 `SettingsView.axaml` 编译绑定规范與对比度门禁**：核查 `SettingsView.axaml` 确认根元素启用了 `x:DataType="vm:SettingsViewModel"`，列表项内按钮命令采用与同文件 `WeatherCitySuggestionList` 一致的 `$parent[ItemsControl].((vm:SettingsViewModel)DataContext).ApplyLlmPresetCommand` + `DataType="vm:LlmPresetOption"`，按钮采用标准 Fluent `Button` 以确保 4 主题 × 深浅变体对比度 100% 达标。
6. **识别既有单测硬编码断言点**：排查 `DJServiceTests` 发现第 315 行 `GenerateTrackIntroductionAsync_FallbackTemplateNotRecorded` 硬编码断言了旧静态兜底词片段 `Assert.Contains("接下来为你带来", fallback.Text)`（当时 `current: null` 走开台分支）。已在 §4.1 明确同步更新该断言为检查歌名 `"《晴天》"` 及 `Assert.DoesNotContain(fallback.Text, nextPrompt)`，保持“兜底模板不进短期记忆”的测试意图与覆盖力不变。
