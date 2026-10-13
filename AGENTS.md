# TuanRadio — Project Context & Memory

## Project

TuanRadio 是一个 Windows 桌面 AI 电台播放器。

技术栈：.NET 10、Avalonia 11.3.9、ReactiveUI、LibVLCSharp、NAudio、LLM（OpenAI/Anthropic 兼容/本地三格式）、Edge TTS、Whisper、多平台在线音乐搜索。

产品定位：复古 AI 电台、AI DJ、节目单推荐、TTS 串场、音乐搜索、星空/频谱视觉反馈。

## Current Direction

- 不再保留旧 Web 静态资源、模型资源或相关运行时依赖。
- AI DJ 角色保留为名称、声音、人设提示和轻量头像动效。
- 长期用户画像已落地并双链路消费（推荐 + DJ 聊天），本地文件级，不做云端数据库。
- 天气已交付（ClockStage 环境指示器，日历徽标 2026-10-13 按用户裁决移除）；歌词模式此前已交付；供应链校验、旧协议清理、本地化与对比度修正均已落地。
- 本地曲库、OpenSubsonic 与 Audius 公开只读曲库已进入音源 Broker；Audius 可选 API Key 由设置页存入系统安全存储，仅搜索/详情的 `x-api-key` 请求头携带，播放 URL 不含 Key，未配置时保留无 Key 模式。支持无 Node 核心构建、裁剪代理包、音源启停/排序及 OpenSubsonic 传输音质偏好。实际播放编码/码率展示已落地（LibVLC 解析为准、音源声明值兜底、试听徽标前瞻性接线）；PlaybackHeaders 播放头传输已端到端贯通（UA 可靠/Cookie 通道建成/Referer 尽力投递——核心客户端不支持 Referer 是上游限制），首播经对象化解析（ResolveTrackAsync + ToTrack(ResolveTrackResult)）即携带头/声明值并按新鲜度跳过后台预刷新；网易 /song/url/v1 的 type/br 声明值经富解析契约（ResolvePlayInfoAsync）采集，首播徽标即显声明值；全局源策略（覆盖/稳定/音质优先）已落地（影响回退序与搜索源权重，不改首选源）。**音源架构阶段 3/4 全部收官**：音乐域已拆分为独立类库 AIRadio.Desktop.Music（命名空间不变、host 单向引用，2026-10-12 Phase 1；Phase 2 的 ProjectReference 条件裁剪/manifest/社区 Provider 留待发布期）。
- 剩余人工事项：发布前真实设备耐久验证（长时间在线播放、进程退出/内存观察、无输出设备/睡眠唤醒）；在线流（Audius 302、网易签名 CDN）码率徽标出现且播放不受扰的实机确认；重定向探测与播放头（酷我 Referer 防盗链源）实机播放确认。

## Recent Work

- 舞台光标诚实化 + 日历徽标整体移除（2026-10-13，用户反馈两连）：ClockStage 手型光标不再常显——`HasCurrentTrack` 由无通知计算属性改 `[Reactive]`（TrackChanged 事件流维护，AudioService 换曲/列表增删/清空全走 NotifyTrackChanged 含 null），根 Grid `Classes.clickable` 绑定 + `Grid#StageRoot.clickable` 样式置 Hand，无在播曲目光标回落默认箭头（点击仍静默，与既有"无曲目不进空歌词层"守门语义一致；环境指示器显式 Cursor="Arrow" 覆盖不变）。日历徽标先做公历去重（中间时钟已显星期+日期，徽标曾重复"13"与"10月13日 星期二"字样），用户裁决连农历也不要——**整链删除**：ClockStage 徽标 Border 与 calendar-badge 样式族、WeatherViewModel 日历三属性/UpdateCalendar/ApplyCalendar/农历节气节日本地化族（RebuildTooltips 保留仅重建天气）、MainWindowViewModel 1s 时钟里的跨日驱动点、ChineseCalendar.cs（含 CalendarDayInfo，唯一消费者即徽标，已 grep 确认零残留）与 ChineseCalendarTests、WeatherServiceTests 四个日历测试；环境指示器只剩天气图标。测试：StageCursor 类绑定随 TrackChanged 翻转（headless StageRoot Cursor Null/Hand 断言）。663/663。
- Provider 程序集拆分 Phase 1（2026-10-12，音源架构阶段 3 收官）：新建类库 **AIRadio.Desktop.Music**（net10.0，依赖仅 Serilog + TagLibSharp），音乐域 40 文件 git mv 入库（契约/五源/Broker/账号凭据家族/CandidateRanker/MusicIdentity/**Track 模型**——FromFile 用 TagLibSharp 故随库），**命名空间全部不变**使 host/测试 using 零改动、迁移成本≈挪文件+csproj。**AppLanguage 去 Avalonia 化**：库侧删 ResourceDictionary/Dispatcher 逻辑、改 `SetUiMarshal(Action<Action>?)` 注入编组 + `GetStringTable` 只读暴露；host 新增 `AppLanguageUi.Attach`（资源字典宿主 + Changed 订阅重填 + Dispatcher 注入），App.axaml.cs 换调 AppLanguageUi.Attach——单测路径（无宿主→调用线程直接广播）语义不变。**依赖面审计（步骤 0）裁决**：PlaybackPreflightService 因 RxUI/Avalonia 调度留守 host；KugouVerificationService 弹浏览器纯 Process.Start 可入库。构建修复三处：OnlineTrack.ToTrack 因 Track 迁库回迁实例方法（曾试 host 扩展桥后随 Track 入库撤销）、SensitiveDataSanitizer internal→public（host 设置页使用）、XAML 编译绑定跨程序集免 assembly= 后缀（Avalonia 自动探测引用）。slim 构建/`#if` 组合根/npm 目标全部留守主工程语义不变；类库加 IVT Tests。双模式构建 0 错误 + 688/688。**Phase 2 留档**：ProjectReference 条件裁剪、Provider manifest/版本检测、社区 Provider 沙箱、独立 NuGet 打包。
- 全局源策略：覆盖/稳定/音质优先（2026-10-12，阶段 4 收官）：`MusicSourcePolicy` 枚举 + `SourcePolicyClasses` 静态分级表（稳定类按自建性/风控史 local(0)/opensubsonic(0)/audius(1)/netease(2)/kugou(3)/kuwo(3)/migu(3)/youtube(4)；音质类 opensubsonic(0)/netease(1)/kugou(1)/local(2)/audius(2)/kuwo(2)/migu(2)/youtube(4)——**local 音质取中性 2 是复查修正**，原 3 会把本地库排酷我/咪咕后）。Broker `SourcePolicy`（int 支撑 Volatile，接口 IMusicSourceBroker 暴露）两个生效点：`GetFallbackPlayUrlAsync` 源列表经 `ApplySourcePolicyOrder` 按 class 稳定重排（类内保用户序；`GetAlternativePlayUrlAsync` 复用同漏斗一并生效；搜索本就全源并发，重排只改命中即停的解析序，零额外请求）+ CandidateRanker 15% 源权重（ScoreSource 增 policy 参数，Coverage 沿用既有硬编码表零回归、Stability/Quality 线性映射 1.0−0.05×class；全部公开方法默认参数 Coverage，既有测试零改动）。设置页 `music_source_policy` 键（照 OpenSubsonic 音质模板全链：ComboBox + 双语词条 S_MusicSourcePolicy/Hint，**Hint 明示"影响回退与搜索源偏好、不改首选源"**——复查修正的诚实预期管理）。测试：分级表映射（含"网易"/"本地曲库"别名与未知默认）、Stability 重排回退先试 audius 且 kugou 零解析 / Coverage 用户序不回归、Ranker 源权重随策略翻转、SettingsViewModel round-trip（策略→broker 属性 + settings.json 持久化）。688/688。
- 网易 br/type 声明值采集（2026-10-12，声明值链路收尾）：`IMusicSearchService` 增默认方法 `ResolvePlayInfoAsync` → 新 `ResolvedPlayInfo(Url/Codec/BitrateKbps/IsPreview)`，**契约二义性消解（两轮计划复查发现原设计缺陷）**：null=服务不支持（回退字符串路径）、非 null=已解析（Url 空=失败，**不得回退**——否则已实现富解析的源在失败路径对同一 API 双重请求）；默认 null 使 21 处宽松 mock 与四个未实现源零改动（已 grep 确认无 strict mock）。NeteaseMusicService 解析体抽核心 `ResolvePlayInfoCoreAsync`（试听抛 PreviewOnly/非 http URL 判失败语义逐字保留），提取 `type`→Codec（ToUpperInvariant 对齐 MediaCodecNames 短名）、`br`/1000→BitrateKbps（≤0 null）；适配器按契约接线（富命中/空 URL 均不走字符串路径，双重请求断言锚定）。首播点歌网易曲目徽标即显 `MP3 · 320 kbps`（首播对象化的直接收益）。测试：网易字段提取/缺字段非 null 记录/试听仍抛/字符串路径不回归 + Adapter 富命中与空 URL 防回退。683/683。
- Broker 返回对象化：首播即携带完整解析结果（2026-10-12）：四处首播点（ChatViewModel/PlaylistViewModel/RecommendationService/DJService）broker 分支改 `ResolveTrackAsync(forceRefresh:false)`（与原 `GetPlayUrlAsync(OnlineTrack)` 同一缓存漏斗零行为差异）+ 新 `OnlineTrack.ToTrack(ResolveTrackResult)` 重载——**首播 Media 即带播放头**（此前头靠播放开始后的后台 fire-and-forget 刷新回写、只惠及重播）+ 码率徽标声明值即时显示。`Track.ResolvedAtUtc`（[JsonIgnore]）新鲜度标记：`ApplyTrackUrlResolution` 总刷新、ToTrack 仅 `!FellBack` 标记（**回退结果无头，保留后台刷新补齐，防"跳过刷新+回退无头"叠加回归**）；`PlayTrackCore` 后台预刷新触发条件加 `!IsRecentlyResolved`（2min 窗口，省每次首播一次后台同源 CDN 请求与 forceRefresh 缓存逐出），阻塞刷新（isRetry/空 FilePath）与重试链路不动。**两轮计划复查否决原前提**："播前总会阻塞 forceRefresh"是错的——正常首播立即播放、后台刷新并行（PlayTrackCore :1341-1347），收益模型随之修正。测试：ToTrack 携带字段/回退不标新鲜、真实 AudioService 新鲜跳过与过期触发（计数解析委托+轮询后台异步）、ChatViewModel 真实 Broker 点歌集成（断言 AddTracks 收到的 Track 带声明值/头/新鲜度）。678/678。
- PlaybackHeaders 请求头传输适配（2026-10-12）：播放头端到端贯通 `ResolvedMedia.Headers → ResolveTrackResult.Headers → TrackUrlResolution → Track.PlaybackHeaders（[JsonIgnore]）→ MediaHttpOptions → LibVLC AddOption`，缓存随内存携带。**投递能力经二进制验证**（VLC 3.0.23.1 win-x64：grep libvlccore.dll/libhttp_plugin.dll 的选项字符串）：`:http-user-agent`/`:http-cookies` 在核心 HTTP 客户端可靠；`:http-referrer` 仅旧 libhttp_plugin 有（默认不接管媒体播放，核心客户端连 "Referer" 头都不发）——Referer 档案为尽力投递。按源静态档案 `PlaybackHeaderProfiles`（宿主侧配置，youtube→浏览器 UA 可靠、kuwo→Referer 尽力、migu→UA+Referer、网易/酷狗签名 URL 鉴权无档案），`MusicSearchServiceAdapter.CreateWithPlaybackHeaderProfile` 工厂统一查表。注入防护两道：`PlaybackHeaders.IsSafeHeaderValue`（CR/LF/控制字符/≤2048，Cookie 额外可打印 ASCII）生产点 Sanitized 剔除 + `MediaHttpOptions.Build` 消费点跳过；libvlc 每条 AddOption 一个选项、值域到串尾，控制字符是唯一注入向量。局限与声明值同构：仅首选源透传、回退无头、首播经 ToTrack 不带头。**测试陷阱**：C# `\x` 变长转义会贪婪吞并后续十六进制字符（`"a\x7Fb"` 实为 U+0F7B 非 DEL），控制字符测试必须用 `\u` 定长转义。673/673 通过。
- 播放 URL 重定向逐跳复检（2026-10-10）：`PlaybackRedirectGuard`（Services/Music/Playback/）在解析期主动跟随 3xx 链（GET + Range: bytes=0-0 + ResponseHeadersRead，≤5 跳，违规跳转绝不发出请求），每跳过策略校验（公网源 MediaUriPolicy、私有源 IPrivateMediaOriginPolicy 同源）。**观察型语义（对计划决策 2 的自我否决）**：只拒绝"观察到的违规跳转"（TransportRejected 不记熔断），探测超时/连接异常/4xx/5xx/缺 Location 一律放行原 URL——原"超时按拒绝"设计在测试中被 15 例级联失败否决（探测挂起拖满预算、慢 CDN 会被误伤）；残余风险为恶意源停滞探测规避 + TOCTOU 窗口，文档明示。独立 2s 短预算（RedirectProbeBudget，linked CTS CancelAfter）快速失败，最坏给解析加 2s；LibVLC 仍拿原始 URL 不替换。挂载点在 ResolveWithTimeout 初始策略/私源同源校验之后（全部解析路径收口、缓存命中不重复探测）；搜索期可播性探针显式 verifyRedirects:false（防对网易/酷狗 CDN 成倍多发 GET，酷狗 20028 敏感）；私有探测客户端 AllowAutoRedirect=false + 10s 兜底，`internal RedirectGuard` 测试注入口（InternalsVisibleTo）。测试：Guard 7 场景 + Broker 集成（私网跳转拒绝且 ConsecutiveTransportFailures=0）；既有挂起源时序测试注入即时探测客户端保持聚焦。666/666 通过。
- 实际播放编码/码率展示（2026-10-10）：AudioService 媒体挂载后 fire-and-forget `CollectTrackMediaInfoAsync`——`Media.Parse`（在线 ParseNetwork 8s/本地 2.5s 预算）读首个音频轨道，fourcc 经 `MediaCodecNames` 转短名（MP3/AAC/FLAC/PCM 家族等，未识别回退 CodecDescription 再退 hex），`TrackMediaInfo` Subject 发布（requestId 代次守卫防快切歌旧值、全程异常静默不扰播放；在线预解析对音源 CDN 多发一次 GET，`CollectMediaInfoForOnlineTracks` 常量留收缩点）。声明值链路：`ResolveWithTimeout` 改返回 `ResolvedMedia?`（URL 形态经 `ToPlayableUrl` 转换），`GetPlayUrlCoreAsync` 直接产出携带 `Codec/BitrateKbps/IsPreview` 的 `ResolveTrackResult`（缓存值随之携带），经 `TrackUrlResolution`→`ApplyTrackUrlResolution` 落到 `Track.DeclaredCodec/DeclaredBitrateKbps/IsPreviewStream`（JsonIgnore 不落盘）；仅首选源命中透传、跨源回退无声明值、首播徽标在 LibVLC 事件到达前为空（Broker `GetPlayUrlAsync` 返回对象化是后续解法）。PlayerViewModel 组装 `TrackQualityText/Short/Detail`（实际值覆盖声明值、TrackId 卫兵丢迟到事件、语言切换重算）+ 试听徽标 `TrackIsPreview`（当前无生产者播放试听流，前瞻性接线）；PlayerDeck 音源行合并显示 + 试听描边芯片，CompactPlayer 行 2 短徽标（CompactQualityBadge）+ Tooltip 增补采样率/声道。测试：真实 LibVLC WAV 采集回归（PCM/8kHz/单声道）、VM 格式化/卫兵/语言切换、Broker 声明值透传与缓存携带、紧凑卡徽标绑定断言；SettingsViewModel 诊断测试改构建变体感知（账号后缀仅实验性源构建拼接——无 Node 机器上 `-p:TuanRadioEnableNodeProviders=false` 的既有失败非回归，已 stash 基线验证）。**构建注意**：本机无 Node.js 时 build/test 必须带 `-p:TuanRadioEnableNodeProviders=false`（Git Bash 下 `/p:` 会被 MSYS 吞掉斜杠，用 `-p:` 形式）。在线流 ParseNetwork 路径无法自动化，列入人工验收。
- 音源架构阶段 4 增量（2026-09-28）：AudiusProvider 无 Key 只读搜索和曲目详情复核，过滤门控/不可流播结果，stream URL 仍经 Broker 初始公网校验；默认排在网易/酷狗后。OpenSubsonic 音质偏好（自动、原始 format=raw、省流量 maxBitRate=128）持久化并在变更时清除该源解析缓存；搜索页和播放区显示实际音源。公开 API 已实测搜索/详情 200、stream 302，跟随跳转后以 Range 请求取得 206 audio/mpeg；真实 LibVLC 播放、逐跳重定向/DNS 防护及实际码率显示未完成。
- 音源架构阶段 2/3 增量（2026-09-25）：LocalLibraryProvider 将选定文件/文件夹索引到 `%APPDATA%\AIRadio\local-library.json`，支持搜索和手动重扫；OpenSubsonicProvider 使用系统安全存储保存配置，连接前 ping 验证，搜索/stream 保持配置的私有服务器来源；Broker 先排本地/私有源，设置页可启停并排序。`TuanRadioEnableNodeProviders=false` 生成不含 Node 代理目录的核心输出；正常构建裁剪酷狗路由并生成 `providers-manifest.json`。`IMusicProvider.ResolveAsync` 统一返回 `MediaResolutionResult`；后三首队列预检按代次取消并显示分类状态；音源诊断显示最近 20 次请求和熔断快照。网易云代理固定 4.32.0，兼容范围依赖已更新，但 `npm audit --omit=dev` 仍有 3 项上游告警（1 中危、2 高危）；酷狗生产依赖审计 0 项。自动化测试不替代真实设备验收。
- 显式搜索页在快速源无结果时后台尝试 YouTube 等慢源；新搜索、修改搜索词和选择歌曲会取消旧请求，代次校验阻止迟到结果覆盖当前搜索。自动电台与播放恢复仍不启动慢源。
- 视图重构 + 统一状态机落地：MainWindow 拆为 TitleBar/ClockStage/PlayerDeck/ChatArea/PlaylistDrawer/StatusBar/CharacterPicker UserControl，`RadioState` 状态机驱动 StatusBar。
- Theme 全量 token 化：全部颜色走 `Themes/Colors.axaml`，Light/Dark 切换可用。
- 真实 FFT 频谱：WasapiLoopbackCapture + FFT 替换模拟数据，驱动频谱与星空。
- SongStory：STORY 按钮触发现曲 LLM 讲述。
- 节目单视图（库抽屉第 4 个 tab）显示推荐标签，推荐理由经 DjOpening 走 DJ 气泡。
- 搜索状态逐源透传（成功/超时/失败），设置页带连接诊断。
- 升级 .NET 10，Tmds.DBus.Protocol 固定 0.21.3 修复漏洞通告。
- 业务断点修复 A 包：上一首恢复标准语义（>3s 回开头/列表上一首）；change_mood 指令与 CALM/FIRE 按钮接通会话级氛围偏好；SongStory 补 TTS 播报。
- 播放与退出稳定性加固：LibVLC/NAudio 生命周期串行化，多音源硬超时与元数据跨源回退，应用关闭取消推荐/LLM/TTS/STT/搜索，频谱 dB 映射与缓冲区复用。
- 全量代码审查集中修复：麦克风按钮崩溃、进度条拖动失效、YouTube 源解析、Anthropic 历史裁剪、播放列表跨线程锁、EdgeTTS 无声重连、逐源业务失败透传等高危与中危缺陷。
- 简洁播放模式：CompactPlayer 两行紧凑卡（播放控制/进度/收藏/迷你频谱），标题栏收缩、拖动/双击/Esc 还原、模式记忆与置顶设置。
- 酷狗 20028 风控滑块验证：server-kugou 新增会话桥（verify/bridge/*，token 不进 URL）+ verify_auto.html 自动验证页 + WEBGL 指纹进程级稳定化；C# 端 KugouVerificationService 负责挑战检测（四形状分类）、自动弹浏览器验证（10 分钟冷却）与恢复轮询，设置页提供手动「滑块验证」入口。
- 修复按住麦克风说话整体失效：Avalonia Button 类处理器会把左键按下/释放标记为 Handled，XAML 属性挂载收不到事件；ChatArea 改为代码内 AddHandler(handledEventsToo:true) 订阅，测试工程引入 Avalonia.Headless.XUnit 并新增 ChatAreaMicButtonTests 回归测试。
- 修复推荐与播放脱节及按住被拒无反馈：节目单搜索词生成改走 LLMService 新增的无人设 ChatRawAsync（原 ChatAsync 固定注入 DJ 人设，模型回整段台词、开场白碎片被当搜索词搜出无关歌曲直接播放），RecommendationService 增加 SanitizeSearchQueries 台词净化兜底；BeginHoldToTalk 改为返回是否真正开始录音，AI 回复/识别中被拒时不再出现按压视觉（原静默拒绝被用户当成第二次按住失效）。
- 语音识别 LLM 纠错：Whisper base 中文同音错误率高（"来点轻音乐"→"拿手青音樂"），识别文本进入点歌/搜索链路前先经 DJService.CorrectTranscriptionAsync（无人设 ChatRawAsync + 8 秒短超时）归一化，失败/超时回退原文不阻塞语音流程。
- 歌词模式：LyricService 经本地代理双源取词（网易 /lyric 单步、酷狗 /search/lyric 两步 + 关键词兜底 + 时长过滤）、LrcParser 解析、LyricsViewModel 按 PositionChanged 逐行滚动（generation+SourceId 双卫兵防旧词晚到）；ClockStage 时钟/歌词图层互斥，BrandHeader 按钮切换并记忆 show_lyrics_in_stage。
- 长期收听画像：ListeningProfileService 持久化收听事件（%APPDATA%\AIRadio\listener-profile.json，事件为唯一事实、统计现算），歌手亲和度 30 天半衰期、Dislike 黑名单 180 天音乐身份匹配、LLM 口味摘要（水位/老化/语言触发、失败退避、Reset 代次隔离）；跳过信号三重前置判定（前曲播放态+新曲身份不同+进度样本绑定本曲，TrackChanged 有 8 处触发点不能直接当切歌）；推荐搜索词注入画像段与探索要求（冷启动门槛 ≥30 事件且 ≥3 歌手），黑名单拼入排除集（不走冷启动门槛）；设置页学习开关（listener_profile_enabled）+ 二次确认清除。
- DJ 聊天画像注入：GenerateChatResponseAsync 在历史快照副本的人设 system 尾部拼画像段（LLMService.BuildMessages 恒前置内置小音 system，双 system 合并是现状常态，注入不新增第三条；Take(1) 恒保首条 system 故长对话裁剪不丢）；口味段（digest+歌手+氛围）走冷启动门槛、黑名单避雷不走门槛；文案跟随 DJ 人设语言；拼接只在快照副本上，持久历史不落画像、角色切换 Initialize 重建无残留。
- 音源体验增强：播放回退候选在身份匹配每遍内按时长接近度择优（ScoreFallbackCandidate：目标无时长取首个、候选缺时长记 -0.1，两遍次序与外层"高优先级源命中即停"不变——跨源不比时长是预算效率的有意取舍）；MusicSourceFailureKind 结构化分类（NotSignedIn/AuthExpired/RiskControl(20028)/ApiBroken/Unknown）由 MusicSourceBusinessException.Kind 携带，七处抛出点标注，经 SourceSearchStatus.FailureKind 透传（注意 AddSearchReport 脱敏重建必须保字段），搜索状态行按类型渲染；AuthExpired 文案须兼顾酷狗登录态失效与网易代理未就绪两类场景，风控文案注明自动验证仅播放路径且约 10 分钟冷却。
- 天气/日历 + 耐久测试：ClockStage 角落环境指示器（WeatherService 走 Open-Meteo：设置页城市经 geocoding / 空 city 走 ip-api 定位，30 分钟缓存失败静默；ChineseCalendar 纯本地 BCL 农历 + 寿星公式节气 + 节日表，徽标节气/节日高亮，详情 Tooltip）；DurabilityTests 六场景（含真实 LibVLC 静音 WAV 四曲连播）。**Avalonia 编译器陷阱**：DataContext="{Binding VM}" 重定向 + 编译绑定组合会静默击穿程序集 XAML 预编译（ChatAreaMicButtonTests"找不到预编译 XAML"），ClockStage 指示器子树必须用 {Binding VM.X} 路径绑定，已注释锚定。
- 产品化清理五件套：设置页音源逐源连接诊断（MusicSourceBroker.DiagnoseAsync 独立 AsyncLocal 作用域报告不污染搜索状态，复用 FormatSourceStatus 分类渲染）；AI 控制协议旧文本尾标（【play:…】/【next】）从 ParseDjResponse 与两处 StripControlTags 移除，协议只认 JSON 控制块；弹层硬编码标题 SETTINGS/LIBRARY 迁 S_Settings/S_Library（VOL/LIVE 为复古电台设计元素有意保留英文）；Light 主题 WCAG 审计修正三处超标（提示字 #6A6478、LIVE 徽标 #146C4F、StatePlaying #0C6E4E，均 ≥4.5:1）；Node.js/yt-dlp 供应链校验确认已实现（EnvironmentManager 下载即 fail-closed 校验、YtdlpManager 固定版本+SHA256），技术债清单同步。
- 复古电台三大核心体验增强（A/C/D）：
  - **A 精简单行歌词**：CompactPlayer 行 1 荧光青绿单行动态歌词展示，无词/前奏平滑退回歌曲信息；支持单触点按切换、拖拽门限与双击还原撤销冲突；设置项 compact_show_lyrics 持久化。**双击手势时序坑（评审修复）**：Avalonia 的 Tapped/DoubleTapped 经 RouteFinished 在整条按下路由完成后才触发，且双击要求两次按下命中同一元素——歌词/信息两个子元素必须 IsHitTestVisible=False（否则单击切换歌词后第二次按下命中源改变，DoubleTapped 不触发、双击还原失效）；按下处理器不得重置 _lastInfoTappedToggle（否则撤销分支永远读到 false，双击展开会静默翻转设置）；OnExpandDoubleTapped 以 e.Source==TrackInfoArea 门控撤销（行内其它区域双击不得吞掉早前单击），并清 _infoPressed 防展开后游离 release 再翻转。CompactPlayerTrackInfoTests 三个 headless 场景锚定。浅色主题歌词色随 AccentGreen token 加深（#178A65→#0F6B57，SurfaceShell 上 4.24:1→6.3:1）。
  - **C 智能排重打分引擎**：CandidateRanker 多维评分（标题 35%、歌手 30%、时长逼近 20%、源优先级 15%）；非预期特殊版本强惩罚约束（伴奏 -0.7、翻唱 -0.6、DJ/加速 -0.5、Live -0.3）；贯通跨源回退、聚合搜索重排与推荐候选净化。聚合回退搜索为"先评分排序、后宽松去重"：合并阶段不去重（工作容量 3 倍，排序去重后截回 limit*2），否则同名副本中评分更高的那份会在排序前被源优先级去重丢掉；推荐过滤 IsUnwantedVersion 不含 Live（有意保留，避免误杀）。
  - **D 复古收音机调频音效**：RadioSoundFxService 纯程序化 NAudio 声学合成（零外部大资产依赖）；FM 快速调谐扫频（250Hz~1400Hz + 带通白噪，-18dB）在 DJ TTS 开口前自然淡入；台呼微鸣音（StationChime）接线到新节目单落地——UpdateCurrentProgram 以引用门控（自动续播每首都回灌服务侧当前节目单，只有真正新生成的那份才响）；设置页 radio_sound_fx_enabled 开关与双语字典。
- 音源架构演进阶段 1（2026-09-21）：`IMusicProvider` 契约 + `MusicSourceBroker` 承接原 MultiSourceMusicService 全部聚合职责（搜索/逐源报告/跨源回退择优/连接诊断/熔断，代码平移非重写），源路由从类型名反射改按 `Descriptor.Id`；五个既有音源经 `MusicSearchServiceAdapter` 包装接入（音源实现零改动，Descriptor.Id 逐字沿用 Replace("MusicService","") 约定）；业务层 10 处 `is/is not MultiSourceMusicService` cast 全部迁 `IMusicSourceBroker` 接口（含显式 OnlineTrack 解析重载与 MusicSearchIntent 意图重载——漏实现会静默回落默认接口方法弱路径，编译期不可见）；`MediaUriPolicy` 播放 URL 进 LibVLC 前统一校验（scheme 白名单 + IPv4/IPv6 字节级禁段 + DNS 解析后检查 fail-closed，接入点在 Broker ResolveWithTimeout 单点收口全部解析路径；校验对象是播放 URL 不是 API 请求，本地代理 127.0.0.1 不受影响）；`ResolvedMediaCache` 播放解析内存缓存（默认 TTL 10 分钟/ExpiresAt−60s，恢复路径 forceRefresh 先逐出防陈旧 URL 反复命中，酷狗/网易凭据变化 ClearProvider，MusicAccountStore 新增 NeteaseCookieChanged 事件；**缓存读路径是 `GetPlayUrlAsync(OnlineTrack)`（点歌/推荐/歌单重复解析），播放刷新/恢复链路必须走 `ResolveTrackAsync(forceRefresh:true)`——AudioService 的 _trackUrlResolver 委托只在刷新语境被调用**；回退结果不入缓存防身份/URL 错位）；**MultiSourceMusicService 已删除**，`SourceSearchStatus`/`SearchOutcome` 迁 `Services/Music/Contracts/ProviderDiagnostics.cs`（命名空间保持 AIRadio.Desktop.Services 不变）；新代码结构 `Services/Music/{Contracts,Broker,Playback,Adapters}`，DI 双接口注册同一 Broker 实例。测试假 URL 用 TEST-NET-3 IP 字面量（203.0.113.x）——策略对 DNS 主机名 fail-closed，域名假 URL 会拖 DNS 超时。设计两轮评审与实施记录见 docs/plans/2026-09-20-music-source-broker-phase1-design.md。
- 反馈行"全排无效"修复（2026-09-22）：PlayerDeck 反馈行（LIKE/NOPE/SIM/CALM/FIRE/STORY）接线经 headless 验证完好（Button.Command 编译绑定断言锚定），根因是动作静默——反馈/画像只影响后续节目单无即时反馈、空闲无曲目时六命令全部静默早退、STORY 在 LLM 失败（空 Lines）时也静默。修复：每个动作经 DJ 气泡给即时确认（AppLanguage.T 双语），空闲统一引导气泡（"现在没有在播的歌…"，RecordCurrentTrackFeedback 与 TellSongStoryAsync 两处），STORY 空 Lines 兜底气泡。PlayerDeckFeedbackButtonTests 三场景锚定。**headless 测试陷阱**：未渲染窗口 TransformedBounds 为 null，InputHitTest 全空、window.MouseDown/Up 投递不到控件（RunJobs 也无效）；合成 RaiseEvent 直投 Button 只补 PointerEntered+Press+Release 也不触发 Click（Button 状态机依赖真实输入管线维护 _isPointerOver）——UI 级命令回归用"绑定断言 + 命令直驱 Execute()"模式，勿再尝试 headless 模拟按钮点击。
- 近期提交集中审查修复包（2026-09-23，审查范围 76d6f08 往前 6 笔）：MediaUriPolicy 禁段补齐三处——127/8 全段（原只封 127.0.0.1 精确值，127.0.0.2 等 IP 字面量直通，属 SSRF 缺口）、240/4 保留段（含受限广播 255.255.255.255）、IPv4-compatible ::/96 解包（与既有 ::ffff: 映射同一分支）；策略内 DNS 复查套单源预算 WaitAsync（原只包 provider 调用，病态域名可击穿 8s deadline，超时按拒绝不记熔断）；AudioService 关闭路径的音效 WaveOutEvent 清理并入 TTS 后台清理任务（原在 Avalonia 关闭线程同步 Stop/Dispose，与自家"NAudio 不能前台同步释放"规则冲突）；CandidateRanker.ScoreTitle 包含分支由恒定 0.70 地板改仿射映射 0.55+0.45×ratio（"歌手+歌名"查询下所有正常候选都是子串，地板把标题维度压成常数，"Love"/"Love Story" 并列；下限 0.55 仍稳高于 Dice 典型 0.2~0.4）；LiveRegex "音乐节" 收紧为 "音乐节(现场|版)"（歌名含"音乐节"的录音室作品不再吃 -0.3）；MusicIdentity.NormalizeMusicText/NormalizeLoose 增加全半角折叠（U+FF01–FF5E 平移 −0xFEE0，全角标题跨源比对原必不相等）；ResolvedMediaCache 容量上限 256（超限先清过期、仍满逐出 ExpiresAt 最早，防整夜电台单调增长）；CompactPlayer 行 1 Tooltip 恢复完整曲目信息（歌名加粗+歌手+末行保留手势提示，原静态提示顶掉了截断歌名的唯一查看入口）。**.NET IPAddress.TryParse 语义实测**：0177.0.0.1/0x7f.0.0.1/127.1/2130706433 在 .NET 10 全部正确解析为 127.0.0.1（与 inet_addr 同语义），八进制/十六进制/短形 IP 字面量不是绕过向量，无需额外处理。

## Architecture Notes

- `AudioService` 管理播放、TTS 播报与 `RadioSoundFxService` 调频音效。
- `CandidateRanker` 负责歌曲搜索与跨源回退的规范化多维打分、版本冲突惩罚与智能排重。
- `MusicSourceBroker`（**AIRadio.Desktop.Music 类库**，`Services/Music/`）承接音源聚合：多源搜索编排、逐源状态报告（AsyncLocal 作用域）、跨源回退择优、连接诊断与熔断、全局源策略（覆盖/稳定/音质优先，影响回退序与搜索源权重）；本地曲库/OpenSubsonic 为原生 Provider，五个实验性音源经 `MusicSearchServiceAdapter` 接入；`MediaUriPolicy` 在播放解析路径收口校验初始 URL，`PlaybackRedirectGuard` 解析期观察型逐跳复检重定向链（搜索可播性探针跳过），`ResolvedMediaCache` 提供解析缓存；`PlaybackPreflightService`（主程序集，依赖 RxUI 调度）检查后三首。业务层依赖 `IMusicSourceBroker`，不得对聚合器做具体类型 cast。音乐域整体在类库（契约/五源/账号凭据家族/Track 模型/AppLanguage 文本核心），host 单向引用且命名空间不变；AppLanguage 的 UI 编组经 `SetUiMarshal` 注入（host 的 AppLanguageUi.Attach）。
- `PlaylistViewModel` 管理展示歌单、收藏和搜索结果。
- `RecommendationService` 负责节目单候选生成、去重、可播状态和会话反馈。
- `ListeningProfileService` 负责长期收听画像：事件采集/统计/持久化与 LLM 口味摘要，供 `RecommendationService` 跨会话消费。
- `DJService` 负责 AI 对话、串场、TTS 文本和单首推荐 fallback。
- `MainWindowViewModel` 组合各模块，并在 Radio Mode 中触发自动续播。

## Build

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
```

> Git Bash 下 `/p:` 参数的斜杠会被 MSYS 吞掉（加引号也无效），属性一律用 `-p:` 形式；
> 项目路径用正斜杠。
>
> Node 工具链：构建自动探测标准安装目录（`%ProgramFiles%\nodejs` 等）并以绝对路径调用
> npm/node，Node 未进 PATH 也能完整构建；非标准位置可用 `-p:TuanRadioNodeDir=...` 指定。
> 首次构建需联网跑 `npm ci`。彻底无 Node 的机器仍可 `-p:TuanRadioEnableNodeProviders=false`
> 构建核心版（设置页实验性音源区块隐藏）。VSCode F5 依赖 `.vscode/launch.json` 的
> `preLaunchTask: build`（该文件与 tasks.json 随仓库共享）。

## Notes For Future Agents

- 保持变更小而可验证，优先沿用现有 ViewModel 和服务边界。
- 不要把推荐逻辑继续塞进 `MainWindowViewModel`，应尽量放在 `RecommendationService`。
- 每个阶段完成后先跑 build/test，再继续下一阶段。


<claude-mem-context>
# Memory Context

# [AIRadio] recent context, 2026-05-17 9:53pm GMT+8

Legend: 🎯session 🔴bugfix 🟣feature 🔄refactor ✅change 🔵discovery ⚖️decision 🚨security_alert 🔐security_note
Format: ID TIME TYPE TITLE
Fetch details: get_observations([IDs]) | Search: mem-search skill

Stats: 50 obs (11,988t read) | 2,739,246t work | 100% savings

### May 5, 2026
S294 Fix DJ recommendation system - DJ should recommend NEW songs based on favorites context, and fix UI not updating when DJ switches tracks (May 5, 8:46 PM)
269 8:52p 🔴 Fixed DJService.cs compilation error - missing System.Linq
271 8:55p 🔵 System.Linq edit may not have persisted - file still shows old content
272 " ✅ 11 files modified - DJ recommendation and favorites context
273 9:01p 🟣 Committed: DJ recommendations based on favorites context
274 " 🔴 Fixed duplicate track recommendation in radio mode
275 " 🔴 TTS interruption and event subscription leak fixes
276 " ✅ Pushed commit 1e3a13c to GitHub
277 " 🔵 DJ command parsing works but UI may not update track info
S293 Fix DJ recommendation system - DJ should recommend NEW songs based on favorites context, not recommend songs from the favorites list directly (May 5, 9:01 PM)
S295 Fix DJ track switch UI not updating - PlayTrack missing _currentIndex assignment (May 5, 9:02 PM)
278 9:05p 🔵 TrackChanged event is subscribed in MainWindowViewModel
279 9:06p 🔵 PlayerView bindings map to PlayerViewModel properties
280 " 🔵 PlayerViewModel updates track display via TrackChanged subscription
281 " 🔵 DJ play command calls PlayAtIndex directly
282 " 🔵 DJ play command flow: AddTracks then PlayAtIndex
283 " 🔵 PlayTrack calls NotifyTrackChanged() after playing
284 " 🔵 TrackTitle/TrackArtist only updated via TrackChanged subscription
285 9:07p 🔵 BUG FOUND: _currentIndex not set in PlayTrack/PlayAtIndex
286 9:10p 🔴 BUG: PlayTrack doesn't set _currentIndex before NotifyTrackChanged
287 9:15p 🔴 ROOT CAUSE: PlayTrack never sets _currentIndex field
288 9:17p 🔴 DJ track switch UI not updating - _currentIndex not set in PlayTrack
290 9:20p 🔵 AddExternalTrack delegates to AudioService.AddTracks - not used by DJ
291 9:28p 🔵 ChatViewModel receives AddExternalTrack callback - should sync playlist
289 9:29p 🔴 PlayTrack missing _currentIndex assignment - confirmed fix location
292 9:33p 🔵 NotifyTrackChanged emits CurrentTrack which depends on _currentIndex
293 9:35p 🔴 FIX APPLIED: PlayTrack now sets _currentIndex before NotifyTrackChanged
294 9:36p 🔴 Fix verified - build succeeds and all 60 tests pass
S301 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 9:40 PM)
307 9:45p 🔴 TTS Session Race Condition Fixed with HashSet-based Tracking
308 " 🔴 Radio Next/Previous Now Use FindTrackIndex to Prevent Duplicate Tracks
309 " 🔴 MainWindowViewModel Subscription Cleanup Implemented
310 " 🔴 ChatViewModel Implements IDisposable for TTS Subscription Cleanup
311 " 🔴 MainWindow OnClosed Override Ensures Proper Resource Disposal
312 " 🟣 Regression Tests Added for TTS State and Radio Mode Playlist Sync
304 9:51p 🔵 AIRadio project build and tests pass after code review
318 9:54p 🔵 Complete Code Review and Fix Session Concluded Successfully
319 9:55p 🔴 AI DJ Chat Interpretation Issue: Song Names Misinterpreted as Insults
320 " 🔴 Favorites/Playlist Persistence Not Working
321 9:56p 🔵 ChatViewModel DJ Command Flow: ParseResponse Uses Regex Pattern
322 " 🔵 PlaylistViewModel Has LoadAsync and SaveAsync with JSON Persistence
313 9:57p ✅ AIRadio comprehensive code review and fixes completed
314 9:58p 🔴 AIRadio comprehensive bug fixes completed - 0 warnings, 63 tests passing
315 " ✅ README and Plan Documents Clarify Live2D Status as Retained Resources
316 " 🔴 Build Warnings Resolved for Nullable and Platform Compatibility
317 " 🔵 Full Test Suite Passes: 63 Tests Including 3 New Regression Tests
327 10:10p ✅ 修复提交完成，AGENTS.md 待处理
S303 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:20 PM)
328 10:21p ✅ Committed AI DJ song detection and favorites persistence fixes
331 " 🔴 App crash on playback caused by test pollution of real playlist.json
332 " 🔴 ChatViewModel duplicate track prevention via TrackAdded callback coordination
S307 Fix AIRadio bugs: app crash, test pollution, duplicate track addition (May 5, 10:21 PM)
S302 Fix MiniMax-generated bugs: AI DJ song detection confidence, favorites persistence, online track URL refresh (May 5, 10:21 PM)
S304 Fix bugs from MiniMax's code generation session - song detection, favorites persistence, online track URL refresh (May 5, 10:23 PM)
S305 Fix AI DJ song detection confidence, favorites persistence, online track URL refresh; commit all changes (May 5, 10:24 PM)
329 10:25p ✅ AGENTS.md 已单独暂存待提交
330 10:33p ✅ 全部修复提交完成，工作区干净
S306 Fix multiple AIRadio bugs: app crash on playback, test pollution of real playlist, duplicate track addition (May 5, 10:39 PM)
333 10:48p 🔴 Auto-radio interrupting manual playback - track switching bug fixed
### May 16, 2026
335 9:45a ✅ Pulled latest code from repository

Access 2739k tokens of past work via get_observations([IDs]) or mem-search skill.
</claude-mem-context>
