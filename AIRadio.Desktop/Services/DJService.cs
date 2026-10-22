using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class DJService : IDJService
{
    private readonly ILLMService _llm;
    private readonly ITtsService? _tts;
    private readonly IMusicSearchService? _musicSearch;
    private readonly IListeningProfileService? _listeningProfile;
    private DJProfile _profile = new();
    private WeatherInfo? _weatherSnapshot;
    private string _currentEmotion = "neutral";
    private readonly List<ChatMessage> _chatHistory = new();
    private readonly SemaphoreSlim _chatGate = new(1, 1);

    /// <summary>DJ 现场发言短期记忆容量：最近 N 条成功串场/节目单开场白注入提示词防复读（百字级，本地小模型窗口安全）。</summary>
    private const int RecentOnAirMemoryCount = 3;

    /// <summary>听众话题注入串场的截断长度：只需表意主题，防长聊天原文撑爆提示词。</summary>
    private const int MaxListenerTopicLength = 50;

    /// <summary>聊天现场段的最近播放条数上限：表意足够且不撑本地小模型窗口。</summary>
    private const int StationContextRecentCount = 4;

    /// <summary>聊天现场段注入的自己近期现场发言条数：记忆队列容量 3，聊天只需最近 2 条。</summary>
    private const int StationContextOwnLineCount = 2;

    /// <summary>DJ 现场发言短期记忆（最旧在前）：串场词与节目单开场白都是"上过广播的话"，统一入队
    /// 防复读并供聊天链认领；只收 LLM 成功生成的文本，fallback 模板不入队，防复读基准被污染。</summary>
    private readonly Queue<string> _recentOnAirLines = new();

    /// <summary>听众最近一次聊天话题：聊天链与串场链的轻量桥接，串场可自然呼应而无需复读聊天历史。</summary>
    private string? _lastListenerTopic;

    /// <summary>电台现场快照：当前在播曲目（null=空闲）。聊天链凭它接住"这首歌 / X 的版本"类指代。</summary>
    private Track? _stationNowPlaying;

    /// <summary>电台现场快照：会话播放历史（旧→新），聊天链"最近放过什么"的事实源。</summary>
    private IReadOnlyList<Track> _stationRecentlyPlayed = Array.Empty<Track>();

    /// <summary>现场记忆读写锁：串场链（advance gate 串行）、聊天链（chat gate 串行）与
    /// UI 层现场快照推送跨域共享这些字段。</summary>
    private readonly object _memoryGate = new();

    /// <summary>记忆代次：Initialize（角色切换）递增——串场生成秒级窗口内切角色时，
    /// 旧代次的迟到串场凭代次不符被拒入队，防止污染新角色的记忆。</summary>
    private int _memoryEpoch;

    public string CurrentEmotion => _currentEmotion;
    public bool TtsEnabled => _profile.TtsEnabled;
    public string DjName => _profile.Name;
    public ApiFailureInfo? LastChatFailure { get; private set; }
    public ApiFailureInfo? LastTtsFailure { get; private set; }

    /// <summary>更新串场环境感知的天气快照（UI 层天气刷新落定时推送；null 表示当前无天气数据）。</summary>
    public void UpdateWeatherSnapshot(WeatherInfo? weather) => _weatherSnapshot = weather;

    /// <summary>更新聊天链的电台现场快照：当前在播曲目与会话播放历史（换曲经 TrackChanged 在 UI 线程、
    /// 进入播放态经 StateChanged 在 LibVLC 事件线程推送，_memoryGate 保证双字段一致性；
    /// nowPlaying 传 null 表示电台空闲）。串场提示词的 current/next 由调用方逐次传入，不经此快照。</summary>
    public void UpdateStationContext(Track? nowPlaying, IReadOnlyList<Track> recentlyPlayed)
    {
        lock (_memoryGate)
        {
            _stationNowPlaying = nowPlaying;
            _stationRecentlyPlayed = recentlyPlayed;
        }
    }

    /// <summary>记一条 DJ 现场发言进短期记忆（节目单开场白等"上过广播但不在聊天记录里"的话）。
    /// 与串场词共用队列：聊天链凭它记得自己说过什么，串场链凭它防复读。情绪标签/控制块/元数据标记
    /// 与换行（StripControlMarkers 内收编压平）入队前剥除。
    /// 已知窗口：调用方生成开场白的 await 续段内若切换角色，旧角色的开场白会入新角色记忆——
    /// 该气泡本就会以新角色名义显示（既有行为），记忆与之同口径，不做代次拦截。</summary>
    public void RememberOnAirLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var entry = PromptHygiene.StripControlMarkers(StripControlTags(text)).Trim();
        if (entry.Length == 0) return;
        lock (_memoryGate)
        {
            _recentOnAirLines.Enqueue(entry);
            while (_recentOnAirLines.Count > RecentOnAirMemoryCount)
                _recentOnAirLines.Dequeue();
        }
    }

    public DJService(ILLMService llm, ITtsService? tts = null, IMusicSearchService? musicSearch = null,
        IListeningProfileService? profile = null)
    {
        _llm = llm;
        _tts = tts;
        _musicSearch = musicSearch;
        _listeningProfile = profile;
    }

    public void Initialize(DJProfile profile)
    {
        _profile = profile;

        var commandRules = """

At the END of every response, append exactly ONE emotion tag in square brackets from:
[happy] [sad] [calm] [neutral] [angry] [surprised]

Command rules:
When the user asks to control music, append one JSON control block after the emotion tag:
<cmd>{"action":"play","query":"song name"}</cmd>
<cmd>{"action":"next"}</cmd>
<cmd>{"action":"pause"}</cmd>
<cmd>{"action":"resume"}</cmd>
<cmd>{"action":"recommend_more"}</cmd>
<cmd>{"action":"change_mood","mood":"calmer"}</cmd>
The spoken text must not mention the control block.
""";

        string systemPrompt;
        if (profile.Language == "en")
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + commandRules + "\nAlways respond in English only."
                : $@"You are an AI radio DJ named ""{profile.Name}"".
{profile.Description}

Response rules:
1. Always respond in English only.
2. Speak naturally like a real late-night radio DJ.
3. End with one emotion tag: [happy] [sad] [calm] [neutral] [angry] [surprised].
4. Use a JSON control block only when music playback should change.";
        }
        else
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + commandRules + "\n必须只用中文回复。"
                : $@"你是名叫 ""{profile.Name}"" 的 AI 电台 DJ。
{profile.Description}

回复规则：
1. 必须只用中文回复。
2. 像真实深夜电台 DJ 一样自然说话，可以有铺垫、画面感和情绪。
3. 末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]。
4. 只有需要控制音乐时，才在情绪标签后追加 JSON 控制块。";
        }

        lock (_chatHistory)
        {
            _chatHistory.Clear();
            _chatHistory.Add(new ChatMessage { Role = MessageRole.System, Content = systemPrompt });
        }

        // 角色切换 = 新 DJ 接管：现场发言记忆与听众话题一并清空，
        // 否则旧 DJ 的串场会被当成新角色的"自己说过的话"注入（语义错位）。
        // 在播/最近播放是环境事实，不随角色清理（同天气快照）。
        lock (_memoryGate)
        {
            _memoryEpoch++;
            _recentOnAirLines.Clear();
            _lastListenerTopic = null;
        }
    }

    public Task<DJScript> GenerateTrackIntroductionAsync(Track? current, Track next)
        => GenerateTrackIntroductionAsync(current, next, CancellationToken.None);

    public async Task<DJScript> GenerateTrackIntroductionAsync(
        Track? current,
        Track next,
        CancellationToken cancellationToken)
    {
        // 未配置时 ChatRawAsync 会抛 LlmApiException：前置检查拦截，直接回退模板串场，
        // 不把"请先配置 AI 服务"当台词播报（同 GenerateSongStoryAsync 的口径）
        if (_llm is LLMService llmService && !llmService.IsConfigured())
            return BuildFallbackIntroduction(next);

        try
        {
            List<string> recentOnAirLines;
            string? listenerTopic;
            int epoch;
            lock (_memoryGate)
            {
                epoch = _memoryEpoch;
                recentOnAirLines = _recentOnAirLines.ToList();
                listenerTopic = _lastListenerTopic;
            }

            var prompt = BuildTransitionPrompt(
                _profile, current, next, DateTime.Now, _weatherSnapshot, BuildProfileContextBlock(),
                recentOnAirLines, listenerTopic);
            var text = _llm is LLMService llm
                ? await llm.ChatRawAsync(prompt, cancellationToken)
                : await _llm.ChatAsync(prompt, new List<ChatMessage>())
                    .WaitAsync(cancellationToken);
            var emotion = DetectEmotion(text);
            var cleaned = StripControlTags(text);
            // 模型受字数上限压力可能在歌名中间停笔，半句会原样进气泡和 TTS；
            // 歌名不完整时回退模板，保证 DJ 念到完整歌名
            if (!string.IsNullOrWhiteSpace(next.Title) &&
                !cleaned.Contains(next.Title, StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Discarded track introduction with incomplete title: {Title}", next.Title);
                return BuildFallbackIntroduction(next);
            }

            // 只记 LLM 成功生成的串场：模板兜底不是 DJ 的真实语气，进了记忆会让"避免重复"的基准失真。
            // 代次守卫：LLM 生成窗口内角色被切换（Initialize 已清记忆）时，旧 DJ 的迟到串场不得入队；
            // 入队条目过 PromptHygiene（控制块/换行均在此剥除，与标题/话题同口径消毒）
            lock (_memoryGate)
            {
                if (epoch == _memoryEpoch)
                {
                    var entry = PromptHygiene.StripControlMarkers(cleaned).Trim();
                    if (!string.IsNullOrWhiteSpace(entry))
                    {
                        _recentOnAirLines.Enqueue(entry);
                        while (_recentOnAirLines.Count > RecentOnAirMemoryCount)
                            _recentOnAirLines.Dequeue();
                    }
                }
            }

            return new DJScript
            {
                Text = cleaned,
                Emotion = emotion,
                Expression = MapExpression(emotion),
                Motion = MapMotion(emotion)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate track introduction");
            return BuildFallbackIntroduction(next);
        }
    }

    /// <summary>
    /// 串场提示词组装：人设 + 环境上下文（时段/天气，定位为背景参考）+ 听众画像 + 会话短期记忆
    /// （最近串场/听众话题）+ 串场任务（60~120 字、完整念出下一首歌名）。开台场景 current 为 null
    /// 时按"第一首歌"开场，不再伪造"上一首"。独立于聊天历史组词（串场高频，进历史会挤掉真实对话轮）；
    /// 语言跟随 DJ 人设（与聊天/歌曲故事链路一致）。static 供测试直驱注入时间与快照。
    /// </summary>
    internal static string BuildTransitionPrompt(
        DJProfile profile,
        Track? current,
        Track next,
        DateTime now,
        WeatherInfo? weather,
        string? profileBlock,
        IReadOnlyList<string>? recentOnAirLines = null,
        string? listenerTopic = null)
    {
        // 元数据消毒：第三方音源标题可能携带控制块标记，进提示词前剥除（纵深防御）
        var nextTitle = PromptHygiene.StripControlMarkers(next.Title);
        var nextArtist = PromptHygiene.StripControlMarkers(next.Artist);
        var prevTitle = current == null ? null : PromptHygiene.StripControlMarkers(current.Title);
        var prevArtist = current == null ? null : PromptHygiene.StripControlMarkers(current.Artist);

        var isEn = profile.Language == "en";
        var sb = new StringBuilder();

        // 人设段：自定义 SystemPrompt 优先（角色选择器已按语言选好版本），否则 Name+Description
        if (!string.IsNullOrWhiteSpace(profile.SystemPrompt))
            sb.AppendLine(profile.SystemPrompt.Trim());
        else if (isEn)
            sb.AppendLine($"You are an AI radio DJ named \"{profile.Name}\". {profile.Description}");
        else
            sb.AppendLine($"你是名叫“{profile.Name}”的 AI 电台 DJ。{profile.Description}");
        sb.AppendLine();

        // 环境段：时段 + 天气（快照由 UI 层天气刷新推送，无快照时省略天气句）。
        // 只给时段不给时刻数字：模型对提示词里现成的数字有强复述倾向，"14:32"会被原样
        // 念成台词里的"两点半"，连续串场即复读报时（用户实测反馈）；时段/天气定位为
        // 酝酿语气的背景，不是必须说给听众的素材
        var weatherSentence = DescribeWeather(weather, isEn);
        if (isEn)
        {
            sb.Append($"It is {DescribeTimeOfDay(now.Hour, isEn)}.");
            if (weatherSentence != null) sb.Append(' ').Append(weatherSentence);
            sb.AppendLine();
            sb.AppendLine("The time of day and weather above are background for setting your tone only — never announce the clock time, and do not bring up the weather in every transition.");
        }
        else
        {
            sb.Append($"现在是{DescribeTimeOfDay(now.Hour, isEn)}。");
            if (weatherSentence != null) sb.Append(weatherSentence);
            sb.AppendLine();
            sb.AppendLine("上面的时段与天气只是你身处的环境背景，用来酝酿语气——不要报出时刻数字，也不要每条串场都聊天气。");
        }
        if (!string.IsNullOrWhiteSpace(profileBlock))
        {
            sb.AppendLine(profileBlock.Trim());
        }
        sb.AppendLine();

        // 会话短期记忆段：听众最近话题（可轻呼应）+ 自己最近的串场（配负向指令防话头/意象复读——
        // 串场不进聊天历史，模型对"自己刚说过什么"无感知，复读是必然；注入原文是 API 场景下
        // 防重复的标准做法，采样参数不可控）
        if (!string.IsNullOrWhiteSpace(listenerTopic))
        {
            sb.AppendLine(isEn
                ? $"The listener recently chatted with you about: \"{listenerTopic}\". Echo it briefly only if it connects naturally; otherwise don't mention it."
                : $"听众最近和你聊过：「{listenerTopic}」。若有自然关联可以轻轻呼应，没有就完全不提。");
        }
        if (recentOnAirLines is { Count: > 0 })
        {
            sb.AppendLine(isEn
                ? "Your own recent transitions are listed below. Do NOT reuse their openings, imagery, or phrasing — find a fresh angle:"
                : "你最近几次串场已经说过下面这些话，不要重复它们的开场、意象或句式，换一个全新的切入角度：");
            foreach (var line in recentOnAirLines)
                sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        // 任务段（第一条要求随开台/串场分支变化：开台时不存在"上一首余韵"可呼应）
        if (isEn)
        {
            sb.AppendLine(current == null
                ? $"The station has just started, and '{nextTitle}' by {nextArtist} is the first track today. Give the listeners a proper opening introduction."
                : $"You've just played '{prevTitle}' by {prevArtist}; next up is '{nextTitle}' by {nextArtist}'. Bridge the two tracks like a real radio DJ would.");
            var openingMove = current == null
                ? "say why this track makes a good start for right now"
                : "echo the mood left by the previous track, say why the next one fits this moment";
            sb.AppendLine($"""
Requirements:
- Speak in 2~4 sentences (about 40~80 words): {openingMove}.
- Sound conversational and vivid, like talking to one listener — no announcer-style recital, no listing information, never mention these instructions.
- Say the full title and artist name in their original language (never translate non-English names), and finish the sentence completely — never cut off mid-title or mid-sentence.
- Output only the transition line itself — in English, even if the persona above is written in another language. End with exactly one emotion tag: [happy] [sad] [calm] [neutral] [angry] [surprised].
""");
        }
        else
        {
            sb.AppendLine(current == null
                ? $"电台刚刚开播，《{nextTitle}》（{nextArtist}）是今天的第一首歌。请向听众做一段开场介绍。"
                : $"刚刚放完《{prevTitle}》（{prevArtist}），接下来要播放《{nextTitle}》（{nextArtist}）。请像真实电台 DJ 在两首歌之间串场那样自然衔接。");
            var openingMove = current == null
                ? "可以说说这首歌为什么适合作为此刻的开场"
                : "可以呼应上一首留下的余韵、说说下一首为什么适合现在听";
            sb.AppendLine($"""
串场要求：
- 一共 2~4 句、60~120 字：{openingMove}。
- 口语自然、有画面感，像只对一位听众说话——不要报幕腔、不要罗列信息清单、不要提到这些指令本身。
- 歌名和歌手名必须完整念出且按原文（外文歌名不要翻译），句子要完整收尾，严禁在歌名或句子中间截断。
- 只输出串场台词本身，必须只用中文。末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]。
""");
        }

        return sb.ToString().Trim();
    }

    /// <summary>时间带分档索引（0=清晨 1=上午 2=午后 3=傍晚 4=夜晚 5=深夜）：
    /// 环境段文案与 MWVM 串场节奏调度的跨时段判定共用这一处事实源，防止两处分档漂移。</summary>
    internal static int GetTimeBand(int hour) => hour switch
    {
        >= 5 and < 8 => 0,
        >= 8 and < 12 => 1,
        >= 12 and < 17 => 2,
        >= 17 and < 19 => 3,
        >= 19 and < 23 => 4,
        _ => 5
    };

    private static string DescribeTimeOfDay(int hour, bool isEn) => GetTimeBand(hour) switch
    {
        0 => isEn ? "early morning" : "清晨",
        1 => isEn ? "morning" : "上午",
        2 => isEn ? "afternoon" : "午后",
        3 => isEn ? "dusk" : "傍晚",
        4 => isEn ? "evening" : "夜晚",
        _ => isEn ? "late night" : "深夜"
    };

    // 天气描述词与 WeatherViewModel 的 UI 语言映射并存：那里跟随界面语言、这里跟随 DJ 人设语言，
    // 两处语义不同源（枚举仅 8 个值，重复成本低过抽象共享）
    private static string? DescribeWeather(WeatherInfo? weather, bool isEn)
    {
        if (weather == null)
            return null;

        var kind = weather.Kind switch
        {
            WeatherKind.Sunny => isEn ? "clear" : "晴",
            WeatherKind.Cloudy => isEn ? "partly cloudy" : "多云",
            WeatherKind.Overcast => isEn ? "overcast" : "阴",
            WeatherKind.Rain => isEn ? "raining" : "有雨",
            WeatherKind.Snow => isEn ? "snowing" : "有雪",
            WeatherKind.Thunder => isEn ? "storming" : "有雷雨",
            WeatherKind.Fog => isEn ? "foggy" : "有雾",
            _ => null
        };
        if (kind == null)
            return null;

        // LocationName 是 geocoding/ip-api 第三方返回值，与曲目元数据同口径过消毒（纵深防御）
        var location = PromptHygiene.StripControlMarkers(weather.LocationName);
        var temperature = double.IsNaN(weather.TemperatureC)
            ? string.Empty
            : isEn ? $", {weather.TemperatureC:0}°C" : $"，气温 {weather.TemperatureC:0}°C";
        return isEn
            ? $"Outside in {location} it's {kind}{temperature}."
            : $"{location}现在{kind}{temperature}。";
    }

    private DJScript BuildFallbackIntroduction(Track next) => new()
    {
        Text = _profile.Language == "en"
            ? $"Up next is \"{next.Title}\" by {next.DisplayArtist}. Let's settle into its mood."
            : $"接下来为你带来《{next.Title}》 - {next.DisplayArtist}，一起听听这段情绪。",
        Emotion = "happy",
        Expression = "smile",
        Motion = "wave"
    };

    public Task<SongStory> GenerateSongStoryAsync(Track track)
        => GenerateSongStoryAsync(track, CancellationToken.None);

    public async Task<SongStory> GenerateSongStoryAsync(Track track, CancellationToken cancellationToken)
    {
        // 未配置时 ChatAsync 返回“请先在设置中配置 AI 服务。”提示文案，
        // 不能当歌单故事朗读；返回空故事走与生成失败一致的静默跳过路径
        if (_llm is LLMService llmService && !llmService.IsConfigured())
            return new SongStory { Title = track.Title, Track = track, Lines = new() };

        try
        {
            var prompt = _profile.Language == "en"
                ? $"Tell radio listeners about '{track.Title}' by {track.Artist} in 3-5 friendly, conversational sentences. Cover its background, style, or an interesting fact, one sentence per line. Reply in English only."
                : $"歌曲《{track.Title}》- {track.Artist}。用 3-5 句话给电台听众讲讲这首歌的背景、风格或趣闻，亲切口语化，每句独占一行。";
            var text = _llm is LLMService llm
                ? await llm.ChatAsync(prompt, new List<ChatMessage>(), cancellationToken)
                : await _llm.ChatAsync(prompt, new List<ChatMessage>())
                    .WaitAsync(cancellationToken);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => t.Trim())
                             .Where(t => t.Length > 0)
                             .Take(5)
                             .Select(t => new DjScriptLine { Text = t, Emotion = DetectEmotion(t) })
                             .ToList();
            return new SongStory { Title = track.Title, Track = track, Lines = lines };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate song story for {Track}", track.Title);
            return new SongStory { Title = track.Title, Track = track, Lines = new() };
        }
    }

    public Task<string> GenerateChatResponseAsync(string userMessage)
        => GenerateChatResponseAsync(userMessage, CancellationToken.None);

    public async Task<string> GenerateChatResponseAsync(string userMessage, CancellationToken cancellationToken)
    {
        // 未配置时 ChatAsync 返回“请先在设置中配置 AI 服务。”提示文案，不能当 DJ 台词播报
        // （角色问候路径会直接朗读该文案）；按失败口径上报，让调用方走设置引导分支
        if (_llm is LLMService llmService && !llmService.IsConfigured())
        {
            LastChatFailure = ApiFailureInfo.MissingApiKey();
            return AppLanguage.T("请先在设置中配置 AI 服务。", "Configure the AI service in Settings first.");
        }

        // 串行化"取快照→LLM→落账"全程：角色问候（fire-and-forget）与用户聊天并发进入时，
        // 各自基于同一快照生成、按完成顺序落账，历史会乱序且上下文互相不可见
        await _chatGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                // 传给 LLM 的历史用快照：锁外仍是同一 _chatHistory，快照避免遍历与 Add 竞态
                List<ChatMessage> snapshot;
                lock (_chatHistory)
                {
                    snapshot = _chatHistory.ToList();
                }

                // 现场段 + 长期画像段：都拼在快照副本的人设 system 尾部，每次调用取最新快照、
                // 不落持久历史；BuildMessages 的 Take(1) 恒保首条 system，长对话不丢。
                // 现场段在画像段之前：聊天的第一职责是接住"此刻电台正在发生什么"。
                var contextSuffix = BuildStationContextBlock() + BuildProfileContextBlock();
                if (contextSuffix.Length > 0 &&
                    snapshot.Count > 0 &&
                    snapshot[0].Role == MessageRole.System)
                {
                    snapshot[0] = new ChatMessage
                    {
                        Role = MessageRole.System,
                        Content = snapshot[0].Content + contextSuffix
                    };
                }

                var response = _llm is LLMService llm
                    ? await llm.ChatAsync(userMessage, snapshot, cancellationToken)
                    : await _llm.ChatAsync(userMessage, snapshot)
                        .WaitAsync(cancellationToken);

                lock (_chatHistory)
                {
                    _chatHistory.Add(new ChatMessage { Role = MessageRole.User, Content = userMessage });
                    _chatHistory.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });

                    // Trim history to avoid unbounded growth (keep system prompt + last N messages)。
                    // 必须按 user/assistant 成对删除：Anthropic 要求首条非 system 消息必须是 user，
                    // 逐条 RemoveAt(1) 会让序列以 assistant 开头，长对话后请求被 400 拒绝
                    const int maxHistoryMessages = 20;
                    while (_chatHistory.Count > maxHistoryMessages + 1)
                    {
                        _chatHistory.RemoveAt(1);
                        if (_chatHistory.Count > 1)
                            _chatHistory.RemoveAt(1);
                    }
                }

                _currentEmotion = DetectEmotion(response);
                // 失败标记按流程分字段（LastChatFailure/LastTtsFailure）：并发路径互不覆盖
                LastChatFailure = null;

                // 串场记忆桥：记下听众最近话题（消毒+截断），串场提示词可轻呼应；
                // 失败/取消路径不记，避免兜底对话污染话题
                var sanitizedTopic = PromptHygiene.StripControlMarkers(userMessage).Trim();
                if (!string.IsNullOrWhiteSpace(sanitizedTopic))
                {
                    if (sanitizedTopic.Length > MaxListenerTopicLength)
                        sanitizedTopic = sanitizedTopic[..MaxListenerTopicLength];
                    lock (_memoryGate)
                        _lastListenerTopic = sanitizedTopic;
                }
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastChatFailure = ApiFailureInfo.FromException(ex);
                Log.Error(ex, "Failed to generate chat response");
                return _profile.Language == "en"
                    ? "Sorry, the signal drifted for a moment. Say that again? [calm]"
                    : "不好意思，刚才信号飘了一下，可以再说一遍吗？[calm]";
            }
        }
        finally
        {
            _chatGate.Release();
        }
    }

    /// <summary>
    /// 聊天现场段：在播曲目 + 最近播放 + 自己最近的广播发言。聊天 LLM 此前对"正在放什么"和
    /// "DJ 刚说过什么"零感知（串场/开场白不进聊天历史），听众说"这首歌 / X 的版本"时无从指代
    /// （实测反馈：DJ 反问"你说的是哪一首"）。每次调用取最新快照、不落持久历史；
    /// 文案跟随 DJ 人设语言（与画像段一致）。
    /// </summary>
    private string BuildStationContextBlock()
    {
        Track? nowPlaying;
        IReadOnlyList<Track> recent;
        List<string> ownLines;
        lock (_memoryGate)
        {
            nowPlaying = _stationNowPlaying;
            recent = _stationRecentlyPlayed;
            ownLines = _recentOnAirLines.ToList();
        }

        var isEn = _profile.Language == "en";
        var lines = new List<string>();

        // 元数据消毒：第三方音源标题可能携带控制块标记，进提示词前剥除（纵深防御）
        if (nowPlaying != null)
        {
            var title = PromptHygiene.StripControlMarkers(nowPlaying.Title);
            var artist = PromptHygiene.StripControlMarkers(nowPlaying.Artist);
            lines.Add(isEn
                ? $"On air right now: '{title}' by {artist}. When the listener says \"this song\" or \"the version by X\", they usually mean this track."
                : $"电台此刻正在播放：《{title}》 - {artist}。听众说的“这首歌”“刚才那首”“X 的版本”一般指它。");
        }
        else
        {
            lines.Add(isEn
                ? "Nothing is playing on the station right now."
                : "电台此刻没有在播的歌曲。");
        }

        var recentList = recent
            .Where(t => nowPlaying == null || !IsSameTrack(t, nowPlaying))
            .TakeLast(StationContextRecentCount)
            .Reverse()
            .Select(t => $"{PromptHygiene.StripControlMarkers(t.Title)} - {PromptHygiene.StripControlMarkers(t.Artist)}")
            .ToList();
        if (recentList.Count > 0)
            lines.Add((isEn ? "Recently played: " : "最近播放过：")
                      + string.Join(isEn ? "; " : "、", recentList)
                      + (isEn ? "." : "。"));

        if (ownLines.Count > 0)
        {
            lines.Add(isEn
                ? "Your own recent on-air lines below were spoken between songs and are NOT in the chat history — remember having said them (never act unaware), and don't recite them back:"
                : "下面这些是你自己最近在电台里说过的话（串场/开场白，不在聊天记录里）——记得自己说过、别一问三不知，也别原样复读：");
            foreach (var line in ownLines.Skip(Math.Max(0, ownLines.Count - StationContextOwnLineCount)))
                lines.Add($"- {line}");
        }

        var header = isEn
            ? "Radio station live context (internal reference; use it naturally, never recite this section):"
            : "电台现场情况（内部参考，回答时自然利用，不要把本段当播报稿念出来）：";
        return "\n\n" + header + "\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 聊天画像段（docs/plans/2026-09-15-dj-chat-profile-injection-design.md）：
    /// 口味段（digest+歌手+氛围）走冷启动门槛（≥30 事件且 ≥3 歌手），
    /// 黑名单避雷不走门槛（Dislike 是显式信号）；无内容可注入时返回 null。
    /// 文案跟随 DJ 人设语言（_profile.Language），黑名单条目取最近 5 首。
    /// </summary>
    private string? BuildProfileContextBlock()
    {
        var snapshot = _listeningProfile?.GetSnapshot();
        if (snapshot == null)
            return null;

        var isEn = _profile.Language == "en";
        var separator = isEn ? ", " : "、";
        var sections = new List<string>();

        if (snapshot.MeetsInjectionThreshold)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(snapshot.Digest?.Text))
                lines.Add(snapshot.Digest.Text.Trim());

            var statsParts = new List<string>
            {
                (isEn ? "Favorite artists: " : "常听歌手：") +
                string.Join(separator, snapshot.TopArtists.Take(5).Select(a => a.Artist)),
            };
            var avoidArtists = string.Join(separator, snapshot.AvoidArtists.Take(3).Select(a => a.Artist));
            if (avoidArtists.Length > 0)
                statsParts.Add((isEn ? "avoided artists: " : "回避歌手：") + avoidArtists);
            var moods = snapshot.MoodUsage.OrderByDescending(kv => kv.Value).Take(2).Select(kv => kv.Key).ToList();
            if (moods.Count > 0)
                statsParts.Add((isEn ? "frequent moods: " : "常见氛围：") + string.Join(separator, moods));
            lines.Add(string.Join(isEn ? "; " : "；", statsParts));

            lines.Add(isEn
                ? "You may weave these tastes naturally into music talk (e.g. occasionally mention styles they often listen to), but not in every reply and never as a list."
                : "聊到音乐时可以自然地结合这些口味（比如偶尔提到对方常听的风格），但不要每句都提、不要播报清单。");

            sections.Add((isEn
                ? "Listener long-term taste (internal reference; don't recite it like a dossier):"
                : "听众长期口味（内部参考，不要像档案一样罗列）：") + "\n" + string.Join("\n", lines));
        }

        var blacklist = snapshot.DislikeBlacklist
            .Take(5)
            .Select(x => $"{x.Title} - {x.Artist}")
            .ToList();
        if (blacklist.Count > 0)
        {
            sections.Add(isEn
                ? $"Never suggest these songs (the listener explicitly dislikes them): {string.Join("; ", blacklist)}."
                : $"不要主动推荐这些歌（听众明确不喜欢）：{string.Join("；", blacklist)}。");
        }

        return sections.Count == 0 ? null : "\n\n" + string.Join("\n", sections);
    }

    /// <summary>
    /// 语音识别结果的同音近音纠错。Whisper base 的中文词错率高（如"来点轻音乐"→"拿手青音樂"），
    /// 在文本进入点歌/搜索链路前用已绑定的 LLM 归一化；失败或超时返回原文，不阻塞语音流程。
    /// </summary>
    public async Task<string> CorrectTranscriptionAsync(string transcript, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return transcript;
        // 未配置时 ChatRawAsync 抛 LlmApiException：前置检查拦截，未配置直接回退原文
        if (_llm is LLMService llmService && !llmService.IsConfigured()) return transcript;

        try
        {
            // 两个 LLM 路径统一取消语义：mock/非 LLMService 路径没有前置 token 检查
            cancellationToken.ThrowIfCancellationRequested();

            // 英文界面下 Whisper 也切到英文识别：提示词必须语言中立，否则会把流利英文当"坏中文"往中文改写
            var prompt = AppLanguage.T(
                $"以下是中文语音识别的原始输出，可能包含同音或近音错别字（例如“拿手青音樂”应为“来点轻音乐”）。请结合中文常见歌曲名、歌手名和电台点歌的口语表达，推断用户真正想说的原话。只修正错别字，不要改写语气、不要增删内容；识别结果已通顺时原样输出。只输出修正后的原话，不要任何解释。\n识别结果：{transcript}",
                $"The following is raw speech recognition output and may contain misrecognized words. Infer what the user actually said based on common song titles, artist names and radio-request phrasing. Fix wrong words only without rephrasing or translating, and without adding or removing content; output as-is when already fluent. Output only the corrected sentence, no explanations.\nTranscript: {transcript}");

            // 纠错是体验增强而非关键路径：短超时快速回退原文，不拖慢按住说话的后续链路
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            var corrected = _llm is LLMService llm
                ? await llm.ChatRawAsync(prompt, timeoutCts.Token)
                : await _llm.ChatAsync(prompt, new List<ChatMessage>())
                    .WaitAsync(timeoutCts.Token);

            var normalized = NormalizeCorrectedTranscript(corrected, transcript);
            return normalized ?? transcript;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 纠错自身的 8 秒超时（非应用关闭）：本地模型慢时用户会一直拿不到纠错，需要现场可见
            Log.Warning("Transcript correction timed out; using raw transcript");
            return transcript;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Transcript correction failed; using raw transcript");
            return transcript;
        }
    }

    /// <summary>
    /// 清洗模型的纠错输出：剥掉标签前缀（"修正后："）、包裹引号与后随解释行；
    /// 结果异常膨胀（疑似整段解释）时返回 null 表示放弃修正、沿用原文。
    /// </summary>
    internal static string? NormalizeCorrectedTranscript(string? reply, string transcript)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        var cleaned = Regex.Replace(reply.Trim(), @"^(?:修正后|纠错后|corrected?)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
        cleaned = cleaned.Trim(' ', '"', '\'', '「', '」', '“', '”', '‘', '’');
        cleaned = cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        cleaned = cleaned.Trim(' ', '"', '\'', '「', '」', '“', '”', '‘', '’');

        if (cleaned.Length == 0) return null;
        // "只修正错别字"约束下长度应与原文相近；超出过多说明模型在解释而不是纠错
        if (cleaned.Length > transcript.Length + 20) return null;
        return cleaned;
    }

    public Task<byte[]?> GenerateSpeechAsync(string text)
        => GenerateSpeechAsync(text, CancellationToken.None);

    public async Task<byte[]?> GenerateSpeechAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        LastTtsFailure = null;
        try
        {
            if (_tts == null) return Array.Empty<byte>();
            var speechText = StripControlTags(text);
            return _tts is EdgeTtsService edgeTts
                ? await edgeTts.SynthesizeAsync(
                    speechText,
                    _profile.VoiceId,
                    _currentEmotion,
                    cancellationToken)
                : await _tts.SynthesizeAsync(speechText, _profile.VoiceId, _currentEmotion)
                    .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastTtsFailure = ApiFailureInfo.FromException(ex);
            Log.Warning(ex, "TTS failed");
            return null;
        }
    }

    public Task<Track?> RecommendNextTrackAsync(Track? current)
        => RecommendNextTrackAsync(current, CancellationToken.None);

    public async Task<Track?> RecommendNextTrackAsync(
        Track? current,
        CancellationToken cancellationToken)
    {
        if (_musicSearch == null) return null;

        // 未配置时 ChatAsync 返回“请先在设置中配置 AI 服务。”提示文案，
        // 不能当歌名解析去搜索；返回 null 让调用方走歌单轮播兜底
        if (_llm is LLMService llmService && !llmService.IsConfigured()) return null;

        try
        {
            var excludedTracks = GetExcludedTracks(current).ToList();
            if (current != null && !excludedTracks.Any(t => IsSameTrack(t, current)))
                excludedTracks.Add(current);

            var prompt = BuildRecommendationPrompt(current);
            // 必须走无人设调用：DJ 人设会让模型回整段台词，台词会被 ParseRecommendedSong
            // 的长度兜底整个当歌名拿去搜索，播出与推荐意图完全无关的歌
            var response = _llm is LLMService llm
                ? await llm.ChatAsync(prompt, new List<ChatMessage>())
                : await _llm.ChatAsync(prompt, new List<ChatMessage>())
                    .WaitAsync(cancellationToken);
            var cleaned = CleanRecommendationText(response);
            var (title, artist) = ParseRecommendedSong(cleaned);
            if (string.IsNullOrWhiteSpace(title)) return null;

            return await SearchRecommendedTrack(title, artist, excludedTracks, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DJ recommendation failed");
            return null;
        }
    }

    private string BuildRecommendationPrompt(Track? current)
    {
        // 元数据消毒：第三方音源标题可能携带控制块标记，进提示词前剥除（纵深防御）
        var favorites = GetFavorites(current);
        var favoriteContext = "";
        if (favorites.Count > 0)
        {
            var favList = favorites.Take(10)
                .Select(t => $"{PromptHygiene.StripControlMarkers(t.Title)} by {PromptHygiene.StripControlMarkers(t.Artist)}")
                .ToList();
            favoriteContext = $" The user likes these songs: {string.Join(", ", favList)}. ";
        }

        var recentlyPlayed = GetRecentlyPlayed(current)
            .Reverse()
            .Where(track => current == null || !IsSameTrack(track, current))
            .Take(8)
            .Select(track => $"{PromptHygiene.StripControlMarkers(track.Title)} by {PromptHygiene.StripControlMarkers(track.Artist)}")
            .ToList();
        var listeningContext = recentlyPlayed.Count == 0
            ? string.Empty
            : $" The user recently played: {string.Join(", ", recentlyPlayed)}. Infer their shared style and recommend a related track, rather than copying one title. ";

        return current != null
            ? $"Based on the song '{PromptHygiene.StripControlMarkers(current.Title)}' by '{PromptHygiene.StripControlMarkers(current.Artist)}', {listeningContext}{favoriteContext}recommend ONE NEW similar or related song that is NOT already in the user's playlist. Do not recommend '{PromptHygiene.StripControlMarkers(current.Title)}'. Reply with ONLY the song name and artist if known."
            : "Recommend one NEW popular song for an AI radio station. Reply with ONLY the song name and artist if known.";
    }

    private async Task<Track?> SearchRecommendedTrack(
        string title,
        string? artist,
        List<Track> excludedTracks,
        CancellationToken cancellationToken)
    {
        var searchQuery = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var results = _musicSearch is Music.IMusicSourceBroker multiSearch
            ? await multiSearch.SearchAsync(
                searchQuery,
                10,
                MusicSearchIntent.Automatic,
                cancellationToken)
            : await _musicSearch!.SearchAsync(searchQuery, 10)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        foreach (var item in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(item, excludedTracks))
            {
                Log.Debug("Skipped already-known DJ recommendation: {Title} by {Artist}", item.Title, item.Artist);
                continue;
            }

            Track? playableTrack;
            if (_musicSearch is Music.IMusicSourceBroker multi)
            {
                // broker 路径拿完整解析结果：首播即携带播放头/声明值
                var resolution = await multi.ResolveTrackAsync(item, forceRefresh: false, cancellationToken);
                playableTrack = resolution == null ? null : item.ToTrack(resolution);
            }
            else
            {
                var url = await _musicSearch.GetPlayUrlAsync(item.Id)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                playableTrack = string.IsNullOrWhiteSpace(url) ? null : item.ToTrack(url);
            }

            if (playableTrack != null)
            {
                Log.Information("DJ recommended: {Title} by {Artist}", item.Title, item.Artist);
                return playableTrack;
            }
        }

        Log.Warning("DJ could not find track for recommendation: {Title}", title);
        return null;
    }

    private static readonly string[] ValidEmotions = ["happy", "sad", "calm", "neutral", "angry", "surprised"];
    private static readonly string EmotionPattern = $@"\[({string.Join("|", ValidEmotions)})\]";

    private static string DetectEmotion(string text)
    {
        var match = Regex.Match(text, EmotionPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var emotion = match.Groups[1].Value.ToLowerInvariant();
            if (Array.IndexOf(ValidEmotions, emotion) >= 0)
                return emotion;
        }
        return "neutral";
    }

    private static string StripControlTags(string text)
    {
        var cleaned = Regex.Replace(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<cmd>\s*\{.*?\}\s*</cmd>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return cleaned.Trim();
    }

    private static string CleanRecommendationText(string response)
    {
        var cleaned = response.Trim();
        cleaned = Regex.Replace(cleaned, @"https?://\S+", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\[think\]:.*", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"^[\[\(【].*?[\]\)】]:", "", RegexOptions.IgnoreCase);
        return StripControlTags(cleaned).Trim();
    }

    private static (string? Title, string? Artist) ParseRecommendedSong(string cleaned)
    {
        if (cleaned.Contains(" - ", StringComparison.Ordinal))
        {
            var parts = cleaned.Split(new[] { " - " }, 2, StringSplitOptions.None);
            return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null);
        }

        if (cleaned.Contains(" by ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Regex.Split(cleaned, @"\s+by\s+", RegexOptions.IgnoreCase);
            return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null);
        }

        return cleaned.Length is >= 2 and <= 100 ? (cleaned, null) : (null, null);
    }

    private static string MapExpression(string emotion) => emotion switch
    {
        "happy" => "smile",
        "surprised" => "smile",
        "sad" => "droopy",
        "angry" => "droopy",
        _ => "idle"
    };

    private static string MapMotion(string emotion) => emotion switch
    {
        "happy" => "wave",
        "surprised" => "wave",
        "sad" => "nod",
        "angry" => "nod",
        _ => "idle"
    };

    private static IReadOnlyCollection<Track> GetFavorites(Track? current)
    {
        if (current?.Tag is RecommendationContext context)
            return context.Favorites;
        if (current?.Tag is IEnumerable<Track> favorites)
            return favorites.ToList();
        return Array.Empty<Track>();
    }

    private static IReadOnlyCollection<Track> GetRecentlyPlayed(Track? current)
    {
        if (current?.Tag is RecommendationContext context)
            return context.RecentlyPlayed;
        return Array.Empty<Track>();
    }

    private static IEnumerable<Track> GetExcludedTracks(Track? current)
    {
        if (current?.Tag is RecommendationContext context)
            return context.ExcludedTracks;
        return Array.Empty<Track>();
    }

    private static bool IsExcluded(OnlineTrack candidate, IEnumerable<Track> excludedTracks)
    {
        return excludedTracks.Any(track =>
            IsSameSource(track.SourceId, candidate.Id) ||
            IsSameMusicIdentity(track.Title, track.Artist, candidate.Title, candidate.Artist));
    }

    private static bool IsSameTrack(Track left, Track right)
    {
        return IsSameSource(left.SourceId, right.SourceId) ||
               IsSameMusicIdentity(left.Title, left.Artist, right.Title, right.Artist) ||
               (!string.IsNullOrWhiteSpace(left.FilePath) && left.FilePath == right.FilePath);
    }

    private static bool IsSameSource(string? left, string? right)
        => MusicIdentity.IsSameSource(left, right);

    private static bool IsSameMusicIdentity(string titleA, string artistA, string titleB, string artistB)
        => MusicIdentity.IsSameMusicIdentity(titleA, artistA, titleB, artistB);

}
