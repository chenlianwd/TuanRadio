# AI 电台竞品差距与迭代建议

> 生成时间：2026-05-04  
> 项目：AIRadio  
> 参考来源：用户提供的抖音视频描述“把我的音乐 agent 又迭代了一版，他推荐的好好听啊...”以及当前代码仓库检查。  
> 说明：抖音短链未能在当前环境直接提取视频画面/字幕；本文已结合用户补充的 5 张视频截图进行二次修订。“当前项目现状”来自代码确认。

## 1. 一句话结论

当前 AIRadio 已经有播放器、多源音乐搜索、收藏、MiniMax 对话/TTS、语音识别、角色配置、Live2D 资源和测试基础，但体验仍像“播放器 + 聊天框 + 歌单”的拼装版；截图里的 Claudio 更像“复古终端电台 + AI DJ 聊天室 + 智能节目单 + 声音讲述卡片”。差距主要在 UI 完成度、推荐闭环、主播人格一致性、节目单编排、动态语音讲述、歌词/台词同步、状态反馈和产品叙事上。

最优先要做的不是再加零散功能，而是把主界面重构成“Claudio 式 AI 电台终端”：顶部是电台身份和系统状态，中部是巨大的时间/当前节目/播放器，下部是 DJ 与用户聊天区，左侧或队列区展示 AI 选出的歌曲卡片，底部是输入框和连接状态。

## 0. 基于截图的参考作品精确拆解

用户补充的截图显示，参考作品叫 **Claudio / Claudio DJ / Claudio FM**。它的关键不是二次元主播，也不是传统 Spotify 皮肤，而是一套非常明确的“复古像素终端电台”语言。

### 0.1 启动页/服务端页

第一张截图是命令行启动画面：

```text
> claudio start

Claudio Server
listening on :8765
connected to 网易云 / Spotify
Claudio DJ Taste loaded
mmguo 的飞书日程

Claudio DJ 2026-04-20 周一
正在为你定制今日电台...

起床时间：09:12
天气：晴 22/8°C 日落时间 18:56
日程：3 会议 · 1 运动 · 1 冥想
收藏夹：3247 首（近期音乐风格：摇滚、90s 华语）
```

这个画面说明它的 AI 电台不是只读歌单，而是接入了个人上下文：

- 音乐源：网易云 / Spotify。
- 品味数据：DJ Taste loaded。
- 日程数据：飞书日程。
- 当日上下文：日期、星期、起床时间、天气、日落时间、会议/运动/冥想。
- 收藏历史：3247 首，近期风格摘要。
- 行为目标：正在定制今日电台。

这点非常重要：它推荐好听，不只是因为模型会聊天，而是因为它把“今天是什么日子、用户今天要干什么、用户最近听什么”都输入给了推荐系统。

### 0.2 主界面：复古终端电台

第二张截图展示了完整主界面：

- 顶部是头像 + `Claudio` 标识，右侧有 `LOGIN`、`DARK`、`LIGHT`。
- 中央巨大像素时钟：`21:11`。
- 时钟下方：`Monday`、`20-APR-2026`、`ON AIR`。
- 中段是播放器控制区：
  - 当前歌曲：`If - Bread`
  - 左侧有频谱小图标和 `PLAYING`
  - 中间是上一首、暂停、下一首、停止、喜欢等圆形按钮
  - 有 `HIDE`、`FAV`、`VOL`
  - 有完整进度条
- 下面是 Queue 区：显示 `QUEUE`、`0 TRACKS`、`Claudio LIVE`。
- 聊天区有 DJ 头像、`CLAUDIO` 标签、黑色气泡、时间戳、`REPLAY`。
- 底部输入框：`Say something to the DJ...`，右侧麦克风按钮和发送按钮。
- 最底部状态栏：`CLAUDIO FM` / `CONNECTED`。

它的 UI 一眼成立，是因为它不是普通播放器，而是“一个正在运行的电台系统”。

### 0.3 歌曲讲述卡片/移动端形态

第三、第五张截图展示另一种沉浸卡片：

- 深色粒子/噪点背景，像声波或宇宙尘埃。
- 上半部分是黑色 DJ 区，显示 `Claudio`、`Speaking...` 和倒计时。
- 黑白频谱横跨卡片。
- 下半部分是白色内容卡片，展示：
  - 标题：`A Human Odyssey`
  - 歌曲信息：`Sign of the Times — Harry Styles`
  - 播放进度：例如 `1:16 / 5:41`
  - DJ 分段讲述文本，带时间戳：`Claudio · 0:27`
  - 当前正在讲述的句子高亮，后面的句子变浅。
  - 底部有波形进度和暂停按钮。

这说明它不是只“播放歌曲”，而是围绕一首歌生成了**分段式音乐讲述脚本**。AI DJ 会像播客一样解释歌曲背景、情绪、画面感、为什么适合现在听。

### 0.4 聊天 + 动态歌单

第四张截图最关键：

- 用户说：“我今天要做内容，主要是展示你哈哈，帮我挑一首 BGM，前奏入耳就能吊起晚上那种氛围感”
- Claudio 回复：“明白了，今天是周一，需要既宁静但又不死板的，我给你找了五首开头就走情绪的，先放给你听：”
- 左侧出现 AI 生成的候选歌单：
  - `Dreams - Fleetwood Mac`
  - 当前高亮：`If - Bread`
  - `Fade Into You - Mazzy Star`
  - `Wicked Game - Chris...`
- 当前歌曲卡片有绿色高亮、星标、边框。
- 用户输入框一直在底部，说明用户可以继续打断和调风格。

这就是它强的地方：AI 不只是“搜一首歌”，而是能把用户一句模糊需求变成一组有审美方向的候选 BGM，并用自然语言解释选择逻辑。

### 0.5 设计关键词

参考作品的设计关键词：

- 复古像素字体。
- 点阵网格背景。
- 黑色/深紫底色。
- 青绿色状态点。
- 终端窗口边框。
- 小头像 + DJ 身份。
- `ON AIR` / `LIVE` / `CONNECTED` 状态。
- 播放器像硬件控制台。
- 聊天气泡像电台后台消息。
- 歌单卡片像 AI 临时生成的节目单。
- 语音讲述卡片像“音乐解说播客”。

因此 AIRadio 后续 UI 不建议继续往 Live2D 大头像方向冲，除非用户特别想要二次元主播。更接近参考作品的方向是：**复古终端 + AI DJ 电台 + 动态节目单 + 语音讲述卡片**。

## 2. 参考视频中的 AI 电台应拆成哪些能力

### 2.1 核心体验

一个好听的 AI 电台不是简单搜索歌曲，而是：

1. 用户给出情绪、场景、风格或一句自然语言。
2. AI 先理解用户当下需求，例如“想要晚上写代码听的中文 R&B”“想要让人开心一点的歌”“像某首歌但不要太吵”。
3. AI 生成一组候选歌曲，并能解释为什么推荐。
4. AI 主播用自然语音串场：开场、推荐理由、下一首预告、歌曲之间的过渡。
5. 播放队列会动态更新，而不是一次性固定。
6. UI 实时展示“AI 正在想什么”：当前策略、推荐理由、下一首、氛围标签、相似依据。
7. 用户可以随时反馈“太吵了 / 换一点 / 喜欢这个 / 来点更冷门的”，AI 会立即调整后续队列。

### 2.2 界面设计特征

参考作品大概率强在以下界面点：

- 有明确的视觉中心：当前歌曲、封面、进度、AI 主播或 agent 状态。
- 推荐结果不是普通列表，而是“有理由的歌曲卡片”：歌名、歌手、封面、标签、推荐原因、操作按钮。
- 有“电台正在直播”的仪式感：LIVE 状态、主播气泡、实时台词、下一首预告。
- 有情绪/场景入口：深夜、学习、开车、快乐、失恋、电子、华语、随机探索等。
- 有播放队列的智能感：不是“导入文件列表”，而是“AI 为你排好的节目单”。
- 有反馈按钮：喜欢、不喜欢、再来一首类似的、换风格、收藏。
- 整体 UI 精致统一，图标、中文、间距、颜色、动效都服务于“音乐推荐”而不是通用桌面工具。

### 2.3 产品逻辑

真正的 AI 电台要有“循环学习”：

```
用户输入/当前场景
 -> AI 分析偏好与意图
 -> 生成推荐策略
 -> 搜索多平台歌曲
 -> 排序、去重、过滤不可播
 -> 生成播放队列
 -> 播放 + 主播串场
 -> 收集用户反馈
 -> 更新下一批推荐
```

当前项目已经有其中一部分，但还没有形成闭环。

## 3. 当前 AIRadio 代码现状

### 3.1 已经具备的能力

从代码看，项目并不差，底层能力已经不少：

- 桌面框架：Avalonia 11 + ReactiveUI。
- 播放核心：`AudioService` 使用 LibVLCSharp，支持播放、暂停、上一首、下一首、进度、音量、淡入淡出、单曲循环、列表循环、随机播放。
- 在线音乐：`MultiSourceMusicService` 聚合 Kuwo、Kugou、Migu、Netease。
- AI DJ：`DJService` 调 MiniMax 做聊天、歌曲串场和 TTS。
- 聊天互动：`ChatViewModel` 支持文本输入、语音识别、对话模式、AI 指令执行。
- 推荐雏形：`MainWindowViewModel.AnnounceStartupRecommendationAsync()` 会在启动时从收藏/歌单里挑一首推荐。
- 歌单管理：`PlaylistViewModel` 支持列表、收藏、搜索、导入、本地保存。
- 数字人资源：`wwwroot/Resources` 下有多个 Live2D 模型资源。
- 频谱：`SpectrumViewModel` 和 `SpectrumView.axaml` 已有可视化雏形。
- 测试：已有 xUnit 测试项目。

### 3.2 当前主界面结构

`AIRadio.Desktop/Views/MainWindow.axaml` 当前布局是：

- 顶部栏：Tune / Radio / LIVE。
- 左侧：圆形字母头像 + 角色切换 pills + 聊天气泡 + 输入框。
- 右侧：`PlaylistView`，包含列表、收藏、搜索三个 tab。
- 底部：播放控制条、进度、音量、LIVE。

这个布局能用，但不像“AI 电台成品”，更像 demo 工具。

### 3.3 明显问题

1. 中文和图标乱码严重  
   多个文件中出现 `AI闄綘鍚瓕`、`鎼滅储`、`鈻?`、`馃攰` 这类乱码。涉及 `MainWindow.axaml`、`PlaylistView.axaml`、`PlayerViewModel.cs`、`Track.cs`、`DJService.cs`、`README.md` 等。  
   这会让界面、提示词、异常提示、AI 人格和文档全部显得不专业。

2. Live2D 还没有真正成为主视觉  
   主界面现在显示的是圆形字母头像，`OnLive2DCommand` 只是让 `AvatarBorder` bounce。虽然资源和 WebView 包存在，但主界面没有真正嵌入可动 Live2D 主播，也没有嘴型、表情、动作和 TTS 的同步。

3. 推荐逻辑太浅  
   当前启动推荐只是从收藏或歌单里随机挑，避开同歌手。它没有理解用户场景、历史偏好、歌曲特征、相似度，也没有生成持续队列。

4. AI 指令协议被乱码破坏  
   `DJService` 和 `ChatViewModel.ParseResponse()` 中命令格式出现乱码，例如 play/next/pause/resume 的包裹符号异常。这样会导致 AI 返回的控制命令难以稳定解析。

5. UI 缺少“推荐理由”和“下一首预告”  
   歌单列表只显示歌名、歌手、时长和按钮，没有“为什么推荐”“适合什么场景”“AI 认为它和当前歌有什么关系”。

6. 频谱是模拟数据，不是真实音频分析  
   `AudioService.EmitSimulatedSpectrum()` 是定时器模拟波形。视觉可以先用，但不能代表音乐实时律动。

7. 产品状态反馈不足  
   搜索、推荐、TTS、语音识别、AI 思考、播放 URL 刷新、失败重试等状态没有在界面上形成清晰反馈。

8. 主播人格没有贯穿 UI  
   `CharacterProfile` 有多个人设，但 UI 只是角色 pills。缺少主播名片、人格标签、声音试听、当前情绪、说话状态、台词历史和专属主题。

## 4. 和参考作品的主要差距

| 模块 | 当前项目 | 参考效果应有状态 | 差距 |
|---|---|---|---|
| 第一眼观感 | 暗色播放器 + 列表 + 聊天 | 沉浸式 AI 电台/音乐 agent | 缺少视觉中心和高级感 |
| 中文显示 | 多处乱码 | 全中文清晰、图标专业 | 必须先修复编码 |
| 主播形象 | 字母头像 + bounce | Live2D/数字人占据主视觉，能说话动起来 | 数字人未完成整合 |
| 推荐 | 随机挑收藏/列表 | 基于情绪、场景、历史、相似度的动态推荐 | 推荐算法和 UI 表达都不足 |
| 歌曲展示 | 普通列表 | 封面卡片、标签、推荐理由、来源、可播状态 | 内容层次太少 |
| 播放队列 | 用户歌单 | AI 生成的节目单/下一首预告 | 缺少电台编排 |
| 交互 | 输入聊天、搜索歌曲 | “我想听...”自然语言驱动整台电台 | 聊天没有成为主入口 |
| 反馈学习 | 收藏/删除 | 喜欢、不喜欢、相似推荐、调整风格 | 缺少偏好闭环 |
| 音频氛围 | 播放 + TTS ducking | 串场、淡入淡出、音量 duck、节目感 | 有底层但没有产品化 |
| 动效 | 简单切换动画 | 播放态、思考态、说话态、推荐态完整动效 | 状态机不足 |

## 5. UI 重设计建议：复刻 Claudio 式电台终端

### 5.1 总体布局

建议从当前 800x560 的小工具布局升级为 900x760 或 960x780 的“居中电台终端窗口”。参考截图不是宽屏三栏 SaaS，而是一个纵向卡片式应用，像独立的电台设备。

推荐布局：

```
┌──────────────────────────────────────────────┐
│ 顶栏：头像 AIRadio/Claudio 风格标题  设置/主题 │
├──────────────────────────────────────────────┤
│ 大时钟：21:11                                 │
│ Monday · 2026-05-04 · ON AIR                  │
├──────────────────────────────────────────────┤
│ 播放器：当前歌 + 频谱 + 控制按钮 + 进度 + 音量 │
├──────────────────────────────────────────────┤
│ 电台状态条：AI DJ / LIVE / connected           │
├──────────────────────────────────────────────┤
│ 主内容：左侧动态歌单 + 右侧 DJ/用户聊天气泡     │
│ 或小屏下改为上下结构                           │
├──────────────────────────────────────────────┤
│ 输入框：Say something to the DJ... 麦克风 发送  │
├──────────────────────────────────────────────┤
│ 底栏：AIRADIO FM                         ONLINE│
└──────────────────────────────────────────────┘
```

### 5.2 顶部：电台身份和时间

参考作品用巨大像素时间建立“正在直播”的仪式感。AIRadio 应加入：

- 顶栏头像：可以用当前选中角色头像或圆形缩略图，不必放大成 Live2D。
- 标题：`AIRadio` 或给产品改名为 `Claudio FM` 风格的 `AI Radio FM`。
- 大号点阵时钟：例如 `21:11`，使用像素字体。
- 日期和星期：`Monday · 2026-05-04`。
- 状态点：`ON AIR`、`CONNECTED`、`AI READY`。
- 主题切换：`DARK`、`LIGHT` 可以保留，但默认只做好 dark。

### 5.3 播放器控制区

播放器应该像参考截图那样紧凑但专业：

- 左侧：迷你频谱 + 当前歌名/歌手。
- 中间：上一首、暂停/播放、下一首、停止、喜欢。
- 右侧：`LIST`、`FAV`、音量。
- 下方：完整进度条，左右显示当前时间和总时长。
- 播放按钮全部使用统一圆形按钮，不要乱码字符。
- 当前播放状态显示 `PLAYING`，TTS 时显示 `SPEAKING`。

### 5.4 主内容区：AI 聊天 + 动态歌单

参考截图最值得学的是“聊天驱动歌单”：

- 用户输入一个自然语言需求。
- AI 回复一段判断。
- 左侧生成 3-5 首候选歌。
- 当前正在播放的歌卡片高亮。
- 每首歌可以显示播放小三角、收藏星标、歌名、歌手。
- 歌单不是固定列表，而是本轮对话生成的节目单。

建议主内容区这样做：

```
左侧 260px：AI 推荐歌单
  [播放图标] Dreams
            Fleetwood Mac
  [星标高亮] If
            Bread
  [播放图标] Fade Into You
            Mazzy Star

右侧剩余：聊天流
  用户气泡：帮我挑一首 BGM...
  AI 气泡：明白了，今天需要...
```

小窗口时可以改成上下结构：先聊天，再推荐歌单。

### 5.5 歌曲讲述卡片模式

第三、第五张截图展示的是另一个很强的模式：AI DJ 对单首歌进行分段讲述。AIRadio 可以新增一个 `Story Mode` 或 `Song Story` 面板：

- 上半部分黑色：`AIRadio DJ`、`Speaking...`、倒计时、频谱。
- 下半部分白色或浅色卡片：歌名、歌手、进度、AI 台词列表。
- 台词带时间戳，例如 `DJ · 0:27`。
- 当前正在说的句子高亮，未说/已说的句子降低透明度。
- 底部波形进度条。

这个功能会让“推荐好听”升级成“讲得有画面感”。例如 AI 不只说“给你放 Sign of the Times”，而是说：

> 这首歌的开头像一盏慢慢亮起来的灯，适合在晚上把注意力拉回来。

### 5.6 视觉风格

应该从 Spotify 风改成复古终端风：

- 背景：近黑 `#08080B`。
- 面板：深紫黑 `#171722`、`#202033`。
- 点阵网格：用很淡的 `rgba(255,255,255,0.08)` 做 background pattern。
- 主强调：荧光青绿 `#5EFCE8` 或 `#56F5C4`。
- 次强调：淡紫 `#B7A7FF`。
- 文字：白色 + 灰白，不要高饱和彩虹。
- 气泡：AI 用黑色，用户用深灰或深紫。
- 当前播放卡片：绿色半透明背景 + 亮边框。
- 字体：标题/数字用像素字体；正文用清晰中文字体。

字体建议：

- 英文像素字体：`Press Start 2P`、`Pixelify Sans` 或内置等宽字体替代。
- 中文正文：`Microsoft YaHei UI`。
- 数字时钟：可以先用 `Consolas` / `Cascadia Mono` 粗体模拟点阵。

### 5.7 背景与细节

参考作品的高级感来自细节，不是复杂组件：

- 外层窗口圆角 12px 左右。
- 顶部模仿 macOS 三色小圆点可选。
- 状态点使用青绿色小圆点。
- 所有分区有细线分隔。
- 按钮 hover 轻微变亮。
- AI 思考时显示 `Tuning...` / `Curating...`。
- TTS 时显示 `Speaking...`。
- 搜索时显示 `Finding tracks...`。
- 推荐完成显示 `5 tracks tuned for tonight`。

## 6. 推荐逻辑改进建议

### 6.1 新增推荐模型

建议新增这些模型字段：

```csharp
public class ListeningContext
{
    public string UserIntent { get; set; }
    public string Mood { get; set; }
    public string Scene { get; set; }
    public string Energy { get; set; } // low/mid/high
    public string Language { get; set; }
    public string DayOfWeek { get; set; }
    public string TimeOfDay { get; set; }
    public string Weather { get; set; }
    public List<string> CalendarHints { get; set; }
    public List<string> PreferredGenres { get; set; }
    public List<string> AvoidArtists { get; set; }
}

public class RecommendedTrack
{
    public OnlineTrack Track { get; set; }
    public double Score { get; set; }
    public List<string> Tags { get; set; }
    public string Reason { get; set; }
    public string OpeningHook { get; set; } // 前奏/入耳点说明
    public string Source { get; set; }
    public bool IsPlayable { get; set; }
}

public class RadioProgram
{
    public string Title { get; set; }
    public ListeningContext Context { get; set; }
    public List<RecommendedTrack> Tracks { get; set; }
    public string DjOpening { get; set; }
}

public class DjScriptLine
{
    public TimeSpan At { get; set; }
    public string Text { get; set; }
    public string Emotion { get; set; }
}

public class UserMusicFeedback
{
    public string TrackId { get; set; }
    public string Action { get; set; } // like/dislike/skip/replay/similar
    public DateTime Time { get; set; }
}
```

### 6.2 推荐流程

推荐流程应从“随机选一首”升级为 Claudio 截图里的“今日电台/本轮节目单”：

1. LLM 把用户自然语言解析成结构化 `ListeningContext`。
2. 合并当天上下文：星期、时间、天气、日程、近期收藏/播放风格。
3. AI 先生成一段 DJ 判断，例如“今天是周一，需要既宁静但又不死板”。
4. 根据 context 生成 3-5 个搜索 query，例如歌手、风格、年代、语种、氛围词。
5. 多平台并发搜索。
6. 去重：同名同歌手合并。
7. 可播验证：提前拿播放 URL，失败的降权或隐藏。
8. LLM 或规则给每首歌生成推荐理由，尤其要说明“为什么适合这句话需求”。
9. 排序：收藏偏好、最近少听、同风格但避免同歌手连续、可播状态优先。
10. 形成 3-5 首本轮候选节目单，并优先播放第一首。
11. 每播放/跳过/收藏后更新偏好。

注意：参考截图里 AI 一次给的是 5 首左右，不是 20 首大列表。UI 上少而准更高级。

### 6.3 AI 主播提示词

要把主播从“聊天机器人”变成“电台 DJ”。提示词要输出稳定 JSON 或稳定标签，避免乱码符号。

建议命令协议改为纯 ASCII：

```text
At the end of the response, output a control block:
<cmd>{"action":"play","query":"..."}</cmd>
<emotion>happy</emotion>
```

或者直接要求 JSON：

```json
{
  "text": "这首歌很适合今晚慢慢听。",
  "emotion": "calm",
  "action": {
    "type": "play",
    "query": "歌曲名 歌手"
  }
}
```

不要继续使用现在乱码的 `銆恜lay` 这类协议。

### 6.4 歌曲讲述脚本

参考截图里 `A Human Odyssey` 卡片展示的是按时间推进的 DJ 台词。建议新增 `GenerateSongStoryAsync(track, context)`。

输出示例：

```json
{
  "title": "A Human Odyssey",
  "track": "Sign of the Times - Harry Styles",
  "lines": [
    {
      "at": "00:03",
      "text": "这首歌的开头像一扇慢慢打开的窗。",
      "emotion": "calm"
    },
    {
      "at": "00:09",
      "text": "它不是马上把你推向高潮，而是让你一点点进入画面。",
      "emotion": "calm"
    }
  ]
}
```

这类脚本可以在 TTS 前生成，也可以只生成 3-5 句，跟随播放进度逐句显示。它会显著提升“AI 电台在讲故事”的感觉。

## 7. 声音、频谱与状态联动

基于截图，优先级应该从 Live2D 改为声音/频谱/文字状态。要实现“活的 AI DJ”，建议做完整状态机：

| 状态 | UI 表现 | Live2D 动作 |
|---|---|---|
| Idle | `CONNECTED`，输入框待命 | 可无 |
| Curating | `正在为你定制今日电台...` / `Curating...` | 可无 |
| Searching | `Finding tracks...`，候选卡片逐个出现 | 可无 |
| Speaking | `Speaking...`，DJ 台词高亮，TTS 倒计时 | 可无 |
| Playing | `PLAYING`，频谱律动，进度条推进 | 可无 |
| Live | `ON AIR` / `LIVE` 状态点常亮 | 可无 |
| Error | 播放失败/搜索失败气泡提示 | 可无 |

技术建议：

- 短期：保留 Live2D 资源，但主 UI 先不依赖 Live2D，避免方向跑偏。
- 频谱：当前 `SpectrumData` 是模拟数据，可以先用于视觉；后续做真实 FFT。
- TTS 播放：显示 `Speaking...`、倒计时、当前台词高亮。
- 音乐播放：显示 `PLAYING`、频谱、当前进度。
- 推荐生成：显示 `Curating today's radio...`。
- 连接状态：底栏固定显示 `AIRADIO FM` 和 `CONNECTED`。

## 8. 必须优先修复的问题

### P0：编码和显示

这些必须先做，否则后续 AI 继续开发会越改越乱：

- 全仓库统一 UTF-8。
- 修复 XAML、C#、README、DESIGN、ai-radio-plan 中的中文乱码。
- 把按钮乱码字符替换成稳定图标资源。
- 修复 `DJService` 系统提示词和命令协议。
- 修复 `ChatViewModel.ParseResponse()` 的正则。
- 修复 `Track.cs` 默认文案和 `ChatMessage.SenderName`。

### P0：主界面重构

- `MainWindow.axaml` 改为三栏 + 中央舞台 + 底部播放条。
- 真正展示 `SpectrumView`。
- 真正嵌入 Live2D，而不是圆形字母头像。
- PlaylistView 改成推荐队列/搜索/收藏多视图。

### P1：推荐闭环

- 新增 `RecommendationService`。
- 新增用户反馈存储。
- 推荐结果模型加入 `Reason`、`Tags`、`Score`、`Source`、`IsPlayable`。
- 支持“再来点类似的”“别放这个歌手”“更安静一点”“更燃一点”。

### P1：AI 电台节目单

- 启动时不是随机播报，而是生成“今日电台”。
- 当前歌快结束前提前生成下一首介绍。
- TTS 结束后再切歌，当前已有一部分逻辑，可以继续完善。
- 支持“下一首预告”和“刚刚听过总结”。

### P2：体验增强

- 歌词/字幕区。
- 封面主色提取。
- 播放历史。
- 歌手/风格偏好页。
- 真实频谱。
- 缓存可播 URL 和搜索结果。

## 9. 建议给国产 AI 的任务清单

可以按下面顺序派给后续 AI：

### 任务 1：修复编码和图标

目标：让所有中文显示正常，所有按钮图标清晰。

范围：

- `AIRadio.Desktop/Views/*.axaml`
- `AIRadio.Desktop/ViewModels/*.cs`
- `AIRadio.Desktop/Models/Track.cs`
- `AIRadio.Desktop/Services/DJService.cs`
- `README.md`

验收：

- UI 中不再出现 `鈻?`、`鎼滅储`、`馃攰`、`銆恜lay` 等乱码。
- 播放、暂停、上一首、下一首、收藏、搜索、删除等按钮语义明确。
- AI 回复和命令解析稳定。

### 任务 2：重做主界面为 Claudio 式复古电台终端

目标：把播放器 demo 改成截图里的“复古点阵电台终端”，不要做成普通三栏播放器。

范围：

- `MainWindow.axaml`
- `MainWindow.axaml.cs`
- `PlaylistView.axaml`
- 必要的 ViewModel 属性

验收：

- 顶部有头像、AIRadio 标识、主题/设置按钮。
- 中央有大号时钟、星期、日期、`ON AIR`。
- 播放器区域有当前歌、频谱、圆形控制按钮、进度条、音量。
- 中部有 `AIRadio DJ` / `LIVE` 状态条。
- 主体区域包含聊天气泡和 AI 生成歌单卡片。
- 底部有输入框、麦克风、发送按钮、`AIRADIO FM` / `CONNECTED` 状态。
- 900x760 和当前最小窗口下都不重叠。

### 任务 3：实现推荐队列模型

目标：从普通 Track 列表升级为 AI 推荐列表。

新增：

- `RecommendedTrack`
- `ListeningContext`
- `RadioProgram`
- `DjScriptLine`
- `RecommendationService`
- `UserMusicFeedback`

验收：

- 用户输入“我想听适合写代码的中文歌”，能生成搜索 query。
- 搜索结果能去重、验证可播、生成推荐理由。
- UI 能展示 3-5 首本轮候选节目单，而不是一大堆搜索结果。
- 当前播放歌曲卡片高亮，有星标/播放状态。

### 任务 4：完善 AI 主播协议

目标：让 AI 聊天、点歌、控制播放稳定。

改造：

- `DJService` 使用 JSON 或 ASCII 标签输出。
- `ChatViewModel.ParseResponse()` 解析新协议。
- 支持 action：play、next、pause、resume、recommend_more、change_mood。

验收：

- 用户说“放一首周杰伦适合晚上听的”，AI 先说一句，再播放。
- 用户说“下一首”“暂停”“继续”稳定执行。
- TTS 文本不读出控制标签。

### 任务 5：实现 Song Story 歌曲讲述卡片

目标：复刻截图里 `A Human Odyssey` 那种“AI DJ 分段讲歌”的体验。

范围：

- 新增歌曲讲述脚本模型：标题、歌曲、分段台词、时间戳、情绪。
- 新增 `GenerateSongStoryAsync(track, context)`。
- 新增讲述卡片 UI：黑色 Speaking 区、频谱、白色台词卡、进度、底部波形。
- 当前 TTS 台词高亮，未播放台词降低透明度。

验收：

- 播放一首歌时可以生成 3-5 句讲述台词。
- TTS 播放时 UI 显示 `Speaking...`。
- 当前台词能跟随 TTS/时间变化高亮。
- 用户能返回普通电台聊天界面。

## 10. UI 细节规范

### 推荐卡片

每张卡片建议结构：

```
[播放小三角] Dreams
            Fleetwood Mac

[星标高亮] If
          Bread

[播放小三角] Fade Into You
            Mazzy Star
```

当前播放卡片用绿色半透明背景和青绿色边框；普通候选卡片用深灰背景。卡片信息不要太多，推荐理由放在 AI 气泡里说，歌单本身保持简洁。

### DJ 聊天气泡

聊天区要像截图一样成为主叙事：

- 用户气泡靠右，显示用户头像/名字/时间。
- AI 气泡靠左，显示 `AIRADIO DJ`、头像、时间。
- AI 回复要解释选择逻辑，例如“今天是周一，需要既宁静但又不死板”。
- 支持 `REPLAY` 按钮，重新播放 AI 这段 TTS。
- 气泡不要太圆，保持复古终端硬朗感。

### Song Story 卡片

讲述卡片建议结构：

```
┌─────────────────────────────┐
│ AIRadio DJ              0:30 │
│ • Speaking...                │
│        频谱柱                │
├─────────────────────────────┤
│ A Human Odyssey              │
│ Sign of the Times — Harry... │
│ 播放进度                     │
│                              │
│ DJ · 0:03 当前讲述句子       │
│ DJ · 0:09 下一句浅色显示     │
│ DJ · 0:14 下一句浅色显示     │
│                              │
│ 0:08  波形波形波形      暂停 │
└─────────────────────────────┘
```

### 场景入口

建议内置场景：

- 深夜放松
- 专注写作
- 写代码
- 开车
- 运动
- 下班路上
- 华语怀旧
- 冷门探索
- 女声频道
- 电子氛围

每个场景对应默认 prompt 和推荐权重。

## 11. 技术架构建议

建议新增层次：

```
ViewModels
  RadioViewModel.cs              // 今日电台/推荐队列/场景状态
  RecommendationItemViewModel.cs

Services
  IRecommendationService.cs
  RecommendationService.cs
  IUserPreferenceService.cs
  UserPreferenceService.cs
  ILive2DController.cs
  Live2DController.cs

Models
  ListeningContext.cs
  RecommendedTrack.cs
  UserMusicFeedback.cs
  RadioSession.cs
```

不要把推荐逻辑继续塞进 `MainWindowViewModel`。`MainWindowViewModel` 只负责组合子模块。

## 12. 最终目标版本描述

用户打开 AIRadio 后，看到的是一个正在直播的复古 AI 电台终端：

- 顶部显示 `AIRadio`、大号时间、日期、`ON AIR`。
- 启动时显示“正在为你定制今日电台”，读取当前时间、天气、日程、近期音乐偏好。
- 用户输入“我今天要做内容，帮我挑一首前奏入耳、晚上有氛围感的 BGM”。
- AI 回复：“明白了，今天需要宁静但不死板，我给你找了五首开头就走情绪的。”
- 左侧/队列区出现 3-5 首候选歌，当前播放卡片高亮。
- AI 先说一段短 TTS，然后播放第一首。
- 播放时状态变成 `PLAYING`，频谱和进度条动起来。
- 进入 Song Story 模式时，AI 分段讲述歌曲背景和画面感，当前台词高亮。
- TTS 说完后音乐开始，底部播放条显示进度。
- 快结束时主播预告下一首。
- 用户点“不喜欢”，后续队列马上变得更贴近。

做到这个闭环，才会接近参考视频里“他推荐的好好听”的效果。
