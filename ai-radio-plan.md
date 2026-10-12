# TuanRadio 当前实施计划

## 产品定位

TuanRadio 的方向已经收敛为“复古 AI 电台”：

- AI DJ 角色：名称、声音、人设提示、轻量头像动画。
- 音乐能力：本地播放、多平台搜索、收藏、在线播放 URL 刷新。
- 电台能力：根据用户意图、当前歌曲、收藏和排除列表生成 3-5 首节目单。
- 视觉反馈：星空、频谱、头像状态动效、统一状态文本。

## 已完成

- Avalonia + ReactiveUI 桌面框架。
- LibVLCSharp 播放核心和 NAudio TTS 播放。
- LLM 服务抽象：`ILLMService` + OpenAI 兼容 / Anthropic 兼容 / 本地模型三种格式的 `LLMService`。
- Edge TTS 服务抽象：`ITtsService` + `EdgeTtsService`。
- Whisper 本地语音识别入口。
- 多音源搜索：网易云、酷我、酷狗、咪咕、YouTube/yt-dlp 兜底。
- Radio Mode 自动续播和播放列表同步。
- 收藏持久化与旧数据迁移到 `FavoriteIds`。
- DJ 角色配置、声音覆盖、人格提示词覆盖和设置保存。
- 推荐服务 v1 模型与服务骨架。
- JSON DJ 控制块解析；旧文本尾标协议已移除。
- 启动欢迎语、启动推荐、TTS 中断和 DJ 视觉 cue。
- 视图重构 + 统一状态机：MainWindow 拆 7 个 UserControl，`RadioState` 状态机驱动 StatusBar。
- Theme 全量 token 化（Light/Dark 双套，`Themes/Colors.axaml`）。
- 真实 FFT 频谱：WasapiLoopbackCapture + FFT 替换模拟视觉数据。
- SongStory v1：STORY 按钮触发现曲 3-5 句 LLM 讲述 + TTS。
- 播放稳定性加固：在线 URL 过期刷新、异常结束重试、重复恢复抑制、LibVLC 操作串行化。
- 音源韧性加固：内置音源取消传播、逐源硬超时、首选源可播性检查、按歌曲元数据跨源 URL 回退。
- 退出与资源治理：应用级取消链路覆盖推荐/LLM/TTS/STT/搜索，LibVLC/WASAPI/NAudio 后台续清理。
- 频谱稳定性加固：真实 FFT 幅度改为 dB 映射、无回环数据视觉兜底、复用 FFT 工作缓冲区。
- 简洁播放模式：CompactPlayer 两行紧凑卡，窗口收缩还原、模式记忆与可选置顶。
- 歌词模式（2026-09-13，2026-09-22 增强）：`LyricService` 经本地代理双源取词（网易 /lyric 单步、酷狗 /search/lyric 两步 + 关键词兜底 + 时长过滤）、LrcParser 解析、`LyricsViewModel` 按 PositionChanged 逐行滚动（generation+SourceId 双卫兵防旧词晚到覆盖）；ClockStage 时钟/歌词图层切换经 BrandHeader 按钮并记忆 show_lyrics_in_stage。2026-09-22 增强为歌词与频谱共存：歌词只替换中栏时钟、两侧频谱与星空常驻共显，点击舞台空白处切换时钟/歌词（与标题栏按钮共用切换命令与偏好记忆），歌词中栏固定宽度防两侧频谱抖动，无在播曲目不进入空歌词层。
- 长期用户画像 v1（2026-09-14）：`ListeningProfileService` 把播放/完播/跳过/按钮反馈/氛围指令持久化为本地收听画像（`listener-profile.json`，事件为唯一事实），派生歌手亲和度（30 天半衰期、−5 下限）、Dislike 黑名单（180 天、音乐身份匹配）与 LLM 口味摘要（水位 25 条/老化 7 天/语言不匹配每会话一次触发、失败 3 次退避 24h、Reset 代次隔离）；跳过判定三重前置（前曲播放态 + 新曲身份不同 + 进度样本绑定本曲）；推荐侧注入画像提示词段与探索要求（冷启动门槛 ≥30 事件且 ≥3 歌手），黑名单拼入排除集；设置页「学习我的口味」开关 + 二次确认清除。
- DJ 聊天画像注入（2026-09-15）：`GenerateChatResponseAsync` 在历史快照副本的人设 system 尾部拼接画像段（每次取最新、不落持久历史、长对话裁剪不丢）；口味段走冷启动门槛，黑名单避雷不走门槛；DJ 可自然提及口味但受"不每句提、不播报清单"约束；跟随「学习我的口味」开关，无新设置项。
- 音源体验增强（2026-09-15）：播放回退候选在身份匹配每遍内按时长接近度择优（1−|候选−目标|/目标，目标无时长退化为取首个，候选缺时长劣后，两遍次序与源间"命中即停"优先级不变）；`MusicSourceFailureKind` 结构化分类（未登录/登录态或代理失效/风控验证/接口失效）经业务异常携带、聚合层透传、搜索状态行按类型渲染用户可读文案与恢复建议（风控文案注明自动验证仅播放路径且约 10 分钟冷却）。
- 产品化清理五件套（2026-09-15）：设置页新增音源逐源连接诊断（`DiagnoseAsync` 独立作用域报告 + 分类渲染，不污染搜索状态）；AI 控制协议旧文本尾标格式（【play:…】/【next】等）从解析与剥离链路移除，协议只认 JSON 控制块；MainWindow/PlaylistDrawer 弹层硬编码标题迁移到 S_* 本地化资源（VOL/LIVE 为复古电台设计元素有意保留）；Light 主题经 WCAG 对比度审计修正三处超标 token（聊天/时钟提示 #B9B1C4→#6A6478、LIVE 徽标 #167A5B→#146C4F、StatePlaying #0E7B58→#0C6E4E）；确认 Node.js/yt-dlp 供应链校验已实现（下载即 fail-closed 校验官方 SHA 值 + 固定版本，文档同步勾销技术债）。
- 天气/日历 + 自动化耐久测试（2026-09-17）：ClockStage 角落环境指示器——天气走 Open-Meteo（IP 定位/设置页城市经 geocoding，30 分钟缓存，失败静默隐藏图标），日历徽标纯本地（BCL 农历 + 寿星公式节气 + 农历/公历节日，节气/节日当天高亮），详情悬停 Tooltip；DurabilityTests 固化六项耐久场景（30 轮续播/自然结束并发单飞/真实 LibVLC 四曲连播/画像 30 会话/播放列表混合并发落盘/聊天 60 轮长对话注入）。实施规避 Avalonia 编译器陷阱：DataContext="{Binding VM}" 重定向与编译绑定组合会静默击穿程序集 XAML 预编译层，指示器子树改用 {Binding VM.X} 路径绑定（ChatAreaMicButtonTests 锚定）。
- 界面无障碍与双语文案产品化打磨（2026-09-20）：图标按钮 AutomationProperties.Name 全面接入本地化资源（聊天语音提示两处硬编码英文迁语言表）；日历 Tooltip 补齐 24 节气/12 节日与农历日期英译，切语言经 RebuildTooltips 即时重算；中文称谓"数字人"改"主播"、弯引号改直引号；歌名/歌手截断处与角色卡补悬停提示、酷狗歌单下拉限宽、设置页去重标题等细节收敛。
- 复古电台三大核心体验增强 A/C/D（2026-09-20）：**A 精简单行歌词**——CompactPlayer 行 1 荧光青绿单行动态歌词，无词/前奏平滑退回歌曲信息；单触点按切换、拖拽门限与双击还原撤销冲突（歌词/信息子元素 IsHitTestVisible=False 保双击同源校验，撤销分支以命中源门控），compact_show_lyrics 持久化，浅色主题歌词色随 AccentGreen 加深至 6.3:1；**C 智能排重打分引擎**——CandidateRanker 多维评分（标题 35%/歌手 30%/时长逼近 20%/源优先级 15%）与非预期特殊版本强惩罚（伴奏/翻唱/DJ 加速/Live），贯通跨源回退、聚合搜索重排与推荐候选净化；聚合搜索"先评分排序、后宽松去重"，同名副本保留评分最高版本；**D 复古收音机调频音效**——RadioSoundFxService 纯程序化 NAudio 合成 FM 调谐扫频（DJ TTS 开口前淡入）与台呼微鸣音（新节目单落地触发，引用门控防自动续播回灌重复鸣响），设置页 radio_sound_fx_enabled 开关。
- 音源架构演进阶段 1（2026-09-21）：`IMusicProvider` 契约 + `MusicSourceBroker`（聚合/逐源报告/跨源回退/诊断自 MultiSourceMusicService 平移，源路由改按 Descriptor.Id）；五个既有音源经 `MusicSearchServiceAdapter` 包装接入（音源实现零改动）；业务调用方 10 处 cast 全部迁移到 `IMusicSourceBroker` 接口；`MediaUriPolicy` 播放 URL 进 LibVLC 前统一校验（scheme 白名单 + 私网/回环/链路本地/组播/云元数据禁段，DNS 解析后检查、解析失败 fail-closed，全部解析路径在 Broker 单点收口）；`ResolvedMediaCache` 播放解析内存缓存（默认 TTL 10 分钟、恢复路径 forceRefresh 逐出防陈旧、酷狗/网易凭据变化清空，`MusicAccountStore` 新增 NeteaseCookieChanged 事件）；`MultiSourceMusicService` 兼容壳完成使命删除，新代码位于 `Services/Music/`（Contracts/Broker/Playback/Adapters），`SourceSearchStatus`/`SearchOutcome` 迁至 Contracts 且命名空间不变。设计经两轮评审，实施记录见 docs/plans/2026-09-20-music-source-broker-phase1-design.md §8。
- 反馈行即时确认与交互统一（2026-09-21~22）：PlayerDeck 反馈行六按钮（LIKE/NOPE/SIM/CALM/FIRE/STORY）每个动作经 DJ 气泡双语即时确认，空闲统一引导气泡，STORY 空产出兜底提示，消除"按钮无效"体感（PlayerDeckFeedbackButtonTests 三场景锚定）；简洁模式行 1 按钮顺序与正常模式标题栏对齐（收藏/置顶/展开/最小化/关闭，关闭恒居最右）。headless 测试确立"绑定断言 + 命令直驱 Execute()"模式（未渲染窗口无法投递指针输入，勿再尝试模拟按钮点击）。
- 阶段 1 审查修复包（2026-09-23，审查 76d6f08 往前 6 笔）：MediaUriPolicy 禁段补齐（127/8 全段、240/4 保留段、IPv4-compatible ::/96 解包）；策略内 DNS 复查套单源预算 WaitAsync（病态域名不再击穿 8s deadline，超时按拒绝不记熔断）；AudioService 关闭路径的音效清理并入 TTS 后台清理任务；CandidateRanker.ScoreTitle 包含分支改仿射映射 0.55+0.45×ratio（原恒定 0.70 地板把标题维度压成常数）；LiveRegex"音乐节"收紧为"音乐节(现场|版)"；NormalizeMusicText/NormalizeLoose 增加全半角折叠（U+FF01–FF5E 平移）；ResolvedMediaCache 容量上限 256（超限先清过期、仍满逐出 ExpiresAt 最早）；CompactPlayer 行 1 Tooltip 恢复完整曲目信息。
- 音源架构演进阶段 2/3（2026-09-25）：LocalLibraryProvider 将选定文件/文件夹索引到 %APPDATA%\AIRadio\local-library.json（支持搜索与手动重扫）；OpenSubsonicProvider 配置存系统安全存储、连接前 ping 验证、保持配置私有服务器同源 stream；Broker 先排本地/私有源，设置页音源启停与排序；`TuanRadioEnableNodeProviders=false` 生成不含 Node 代理目录的核心输出，正常构建裁剪酷狗路由并生成 providers-manifest.json；`IMusicProvider.ResolveAsync` 统一返回 `MediaResolutionResult`；后三首队列预检按代次取消并显示分类状态；音源诊断显示最近 20 次请求和熔断快照。
- 音源架构演进阶段 4（2026-09-28）：AudiusProvider 无 Key 只读搜索与曲目详情复核，过滤门控/不可流播结果，默认排在网易/酷狗后；可选 API Key 由设置页存入系统安全存储，仅搜索/详情的 x-api-key 请求头携带，播放 URL 不含 Key，未配置时保留无 Key 模式；OpenSubsonic 传输音质偏好（自动/原始 format=raw/省流量 maxBitRate=128）持久化，变更时清除该源解析缓存；搜索页和播放区显示实际音源。实际播放编码/码率展示与完整播放传输适配仍在计划中。
- 实际播放编码/码率展示 + 试听状态接线（2026-10-10）：AudioService 媒体挂载后经 Media.Parse（在线 ParseNetwork/本地 ParseLocal 分级预算）读取实际音频轨道，fourcc 短名映射（MediaCodecNames），以 TrackMediaInfo 事件发布（requestId 代次守卫、异常静默；在线预解析多发一次 CDN GET，常量留收缩点）；ResolvedMedia 声明字段（Codec/BitrateKbps/IsPreview）经 ResolveWithTimeout→GetPlayUrlCoreAsync→ResolveTrackResult→TrackUrlResolution 贯通到 Track（JsonIgnore 运行时字段，仅首选源命中与刷新路径，回退路径无声明值、首播徽标在解析事件前为空）；PlayerViewModel 组装质量文案（实际值覆盖声明值、TrackId 卫兵防旧值、双语重算）与试听徽标（前瞻性，当前无生产者）；PlayerDeck 音源行合并展示 + 试听芯片，CompactPlayer 行 2 短徽标 + Tooltip 采样率/声道。真实 LibVLC WAV 采集回归、声明值透传/缓存、徽标绑定等测试锚定；在线流 ParseNetwork 路径列入人工验收。
- 播放 URL 重定向逐跳复检（2026-10-10）：PlaybackRedirectGuard 解析期主动跟随 3xx 链（Range: bytes=0-0 探测、≤5 跳、违规跳转绝不发出请求），每跳过策略校验（公网 MediaUriPolicy/私有同源）；观察型语义——只拒绝观察到的违规跳转（不记熔断），探测超时/不可得放行原 URL（超时拒绝原设计被 15 例级联测试失败否决），独立 2s 短预算快速失败；LibVLC 仍拿原始 URL，残余窗口（停滞探测规避/TOCTOU）文档化。挂载于 ResolveWithTimeout 收口点（缓存命中不重复探测），搜索可播性探针跳过探测（防酷狗 20028 风控敏感的成倍 GET）。
- PlaybackHeaders 播放头传输适配（2026-10-12）：播放头端到端贯通（ResolvedMedia→ResolveTrackResult→Track[JsonIgnore]→MediaHttpOptions→LibVLC AddOption，缓存内存携带）；投递能力经 VLC 3.0.23.1 二进制验证——:http-user-agent/:http-cookies 核心客户端可靠，:http-referrer 仅旧插件支持属尽力投递；按源档案 PlaybackHeaderProfiles（youtube 浏览器 UA/kuwo Referer/migu UA+Referer，网易酷狗无档案）；注入防护两道（生产点 Sanitized 剔除控制字符/超长，消费点跳过），Cookie 额外约束可打印 ASCII。首播头缺口待 Broker 返回对象化。

## 当前流程

### 启动流程

1. `App` 初始化 DI、日志和 `MusicApiServer`。
2. 创建 `MainWindowViewModel` 并加载设置、歌单。
3. 初始化当前 DJ 角色和 LLM/TTS 配置。
4. 播放欢迎语。
5. 如果歌单非空，生成一次启动推荐。

### 点歌流程

1. `ChatViewModel` 识别明确点歌输入或 JSON 控制块。
2. `MusicSourceBroker` 经现有 Provider 适配器搜索在线音源。
3. 获取首选源播放 URL；失效时按歌名/歌手到其他源重新匹配。
4. 加入 `PlaylistViewModel` 和 `AudioService`。
5. 播放目标曲目，并避免重复加入同一首歌。

### Radio Mode 续播流程

1. `AudioService.TrackEnded` 发出当前曲目结束事件。
2. `MainWindowViewModel` 接管自动续播。
3. 优先通过 `RecommendationService` 获取当前节目单的下一首或生成新节目单。
4. 如果节目单推荐失败，退回 `DJService.RecommendNextTrackAsync` 单首推荐。
5. DJ 生成串场文本，TTS 播报后切到下一首。

### 关闭流程

1. `App` 取消应用生命周期令牌，停止初始化、推荐、LLM、TTS、STT、搜索和 yt-dlp 请求。
2. `MainWindowViewModel` 只释放自身订阅和子 ViewModel，不在 UI 线程同步停止原生音频设备。
3. DI 容器统一释放 `AudioService`、`EdgeTtsService` 和 `WhisperSttService`。
4. 原生回调在限定时间内未退出时，窗口继续关闭，清理任务在后台等待回调恢复后完成释放。

## 当前开发阶段

### P0 清理：基本完成

- 旧静态资源、模型资源和相关运行时依赖已不再作为主方向。
- 业务代码已迁到 LLM + Edge TTS 的服务边界。
- README 和实施计划已同步到当前产品定位。
- 未接入 DI 的旧 MiniMax 运行时代码与测试已删除，后续只保留当前 LLM + Edge TTS 边界。

### P1 推荐闭环：v1 已落地

- `RecommendationService` 根据用户输入、当前歌曲、收藏和排除列表生成节目单。
- 搜索结果去重、获取播放 URL、标记可播放状态。
- 用户反馈动作影响当前会话推荐。
- Radio Mode 优先消耗当前节目单，节目单耗尽后再生成新节目单。

### P2 产品化：本节列出的工作已完成

**已完成（播放、音源和生命周期稳定性，2026-08-19~20）：**
- 播放恢复统一以请求代次去重；提前结束依次刷新当前源、按元数据切换替代源，仍失败才进入自动续播。
- LibVLC 播放器操作、音量更新和原生回调释放建立串行边界，避免 UI 卡死及并发 Dispose。
- 多音源加入 3-5 秒分级硬超时、调用方取消和首选源可播性验证。
- 播放 URL 失效或网易云返回明确试听流时使用歌曲元数据跨源匹配，并同步实际生效的音源 ID。
- Edge TTS、Whisper、MusicApiServer、yt-dlp 和 ViewModel 后台任务纳入关闭取消链路。
- 频谱改用 dB 幅度映射并复用 FFT 缓冲区，修复频谱顶满、停滞和高频分配。
- PlayerDeck 限制边界并调整右侧自适应列，修复音量滑块溢出。

**已完成（全量审查修复 + 简洁播放模式，2026-08-20）：**
- 全量代码审查集中修复：麦克风按钮 InvalidCastException、进度条拖动与 seek 接线、YouTube 源 duration 解析与兜底分层、Anthropic 历史裁剪与 system 合并、本地 API 端口身份校验、播放列表跨线程安全、续播回调硬超时、EdgeTTS 无声重连、四源逐条容错与业务失败状态透传、RetryPolicy 超时覆盖、推荐已播记忆与双管线串行化。
- 简洁播放模式：标题栏收缩为两行紧凑卡（状态点/曲目/收藏/窗口控制 + 播放控制/进度/迷你频谱），进入时关闭浮层、快照还原窗口边界（含最大化），模式记忆与置顶设置写入 settings.json；无关设置保存不再触发角色重初始化清空聊天历史。

**已完成（视图重构 + 状态机，2026-08）：**
- 统一状态机 `RadioState`（Idle/Curating/Searching/Speaking/Playing/Error）派生 `MainWindowViewModel.CurrentState`，StatusBar 绑定显示。
- MainWindow 拆为 UserControl：TitleBar / ClockStage / PlayerDeck / ChatArea / PlaylistDrawer / CharacterPicker / StatusBar（MainWindow.axaml 668→163 行，.cs 467→113 行）。
- Converter 合并到 `Converters/`（InverseBool / MessageAlign / RadioStateTo*）。
- Theme ThemeDictionaries + RequestedThemeVariant PoC 落地（`Themes/Colors.axaml`）。
- 时钟迁 VM（`Now` 属性），Starfield 自订阅频谱。

**已全部落地（原待做，2026-08-14）：**
- 节目单 UI 区分当前节目单、收藏、搜索（子项目 2）→ 库抽屉 4 tab。
- 推荐理由放在 DJ 气泡，卡片只保留短标签（子项目 2）→ 卡片只留 Tags，理由经 DjOpening 入气泡。
- 外部音源失败原因和 fallback UI 继续细化（子项目 5）→ 逐源成功/超时/失败透传。
- 设置页连接诊断完善（子项目 5）→ TestConnection 覆盖空值/成功/失败+RecoveryHint。
- Theme 全量 token 化 → spec §9 零残留。

### 子项目 1 收尾（2026-08-14）

- Theme 全量 token 化达成 spec §9 零残留：MainWindow.axaml / SettingsView.axaml / SpectrumView.axaml / ChatArea.axaml.cs 全部迁移到 `Themes/Colors.axaml` 的 `C_<HEX>` token；Light/Dark 双套字典均已配置独立配色。
- 孤儿旧 View（ChatView/PlayerView/PlaylistView）删除，其中活跃引用的 `TabVisibleConverter` / `FavoriteIconConverter` 迁入 `Converters/` 目录。
- 子项目 2「节目单卡片只留短标签」落地：移除卡片 Reason，节目单整体推荐理由通过 `DjOpening` 已进 DJ 气泡；修复 PlaylistDrawer 标签背景 `C_2221ED76}0` 拼写 typo。
- 子项目 5「音源 fallback UI + 设置连接诊断」确认落地：`BuildSearchStatusMessage` 透传各源成功/超时/失败；`TestConnectionAsync` 覆盖空 Key/模型名/成功/失败+RecoveryHint。

### P3 增强：后续评估

- 长期用户画像已落地（见「已完成」）；后续仅评估跨设备同步与画像维度扩展（时段偏好、语种配比等）。
- 音源架构阶段 2 已接入可搜索的本地曲库与 OpenSubsonic，阶段 3 已提供无 Node 核心构建、代理路由裁剪及依赖清单；设置页支持已注册音源的启停和排序。阶段 4（2026-09-28）已接入 Audius 开放曲库（无 Key 只读 + 可选 API Key 本机安全存储）与 OpenSubsonic 传输音质偏好；实际播放编码/码率展示已落地（2026-10-10，LibVLC 解析为准 + 声明值兜底 + 试听徽标前瞻性接线）；重定向逐跳复检与播放头传输（PlaybackHeaders）已落地（2026-10-10/10-12）。独立 Provider 程序集拆分、Broker 返回对象化（首播头/声明值缺口）与网易声明值采集仍待实现。总计划见 docs/plans/2026-08-25-music-source-architecture-evolution-plan.md。
- 在线音源可靠性专项已接入结构化解析结果、后三首队列预检与逐首状态、最近 20 次请求健康度；网易云代理升级到 4.32.0。完整权益/设备/版本诊断、带请求版本隔离的 YouTube 后台候选代理仍待实现，代理的上游依赖链仍有 3 项 npm audit 告警。
- 音源体验增强与设置页逐源连接诊断均已落地（2026-09-15，见「已完成」）。
- Node.js/yt-dlp 供应链校验已落地（下载即 fail-closed 校验官方 SHA 值 + 固定 release 版本）。
- 自动化耐久测试已落地（2026-09-17，见「已完成」）；发布前仍需人工执行：长时间真实在线播放、进程退出/内存观察、无输出设备/睡眠唤醒。
- Light/Dark 对比度已按 WCAG 审计一轮（文字对 ≥4.5:1），后续视觉调整持续保持该标准。
- 天气、日历已交付（2026-09-17，见「已完成」）。

## 构建与测试

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
```
