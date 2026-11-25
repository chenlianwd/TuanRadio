namespace AIRadio.Desktop.Services.WebRemote;

/// <summary>
/// 极简离线移动端遥控 SPA（单文件零外链资源，完全依赖局域网内嵌 HTTP + WebSocket 服务），
/// 所有动态数据渲染均采用 textContent 赋值以防 XSS 注入。
/// </summary>
public static class WebRemoteHtml
{
    public const string Page = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, viewport-fit=cover">
<title>TuanRadio Remote</title>
<link rel="icon" type="image/svg+xml" href="/favicon.svg">
<style>
:root {
  --bg: #0b0b12;
  --surface: #141420;
  --surface-2: #1c1c2c;
  --border: rgba(255,255,255,0.09);
  --text: #ededf5;
  --muted: #9898ad;
  --accent: #56f5c4;
  --accent-dim: rgba(86,245,196,0.16);
  --amber: #f5a623;
  --danger: #ff6b7a;
}
* { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
html, body {
  margin: 0; padding: 0; height: 100%;
  background: radial-gradient(circle at top right, #19192a 0%, var(--bg) 55%);
  color: var(--text);
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei UI", sans-serif;
  overflow: hidden;
}
.app {
  display: flex; flex-direction: column;
  height: 100dvh; max-width: 540px; margin: 0 auto;
  padding: env(safe-area-inset-top, 10px) 14px calc(env(safe-area-inset-bottom, 10px) + 8px);
  gap: 10px;
}
.header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 6px 4px 2px; gap: 8px;
}
.brand { display: flex; align-items: center; gap: 8px; min-width: 0; }
.dot {
  width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0;
  background: var(--muted); transition: background .25s, box-shadow .25s;
}
.dot.online { background: var(--accent); box-shadow: 0 0 8px var(--accent); }
.dot.speaking { background: var(--amber); box-shadow: 0 0 10px var(--amber); animation: pulse 1s infinite; }
@keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: .45; } }
.brand-title { font-size: 14px; font-weight: 700; letter-spacing: .3px; white-space: nowrap; }
.dj-badge {
  font-size: 11px; color: var(--muted); background: var(--surface);
  border: 1px solid var(--border); border-radius: 999px;
  padding: 3px 9px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.header-pills { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }
.pill-btn {
  border: 1px solid var(--border); background: var(--surface); color: var(--muted);
  border-radius: 999px; padding: 5px 10px; font-size: 12px; cursor: pointer;
  display: inline-flex; align-items: center; gap: 4px;
}
.pill-btn.active { border-color: var(--accent); color: var(--accent); background: var(--accent-dim); }
.card {
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 14px; padding: 12px 14px;
}
.now-top { display: flex; justify-content: space-between; align-items: flex-start; gap: 10px; }
.track-meta { min-width: 0; flex: 1; }
.track-title {
  font-size: 16px; font-weight: 700; line-height: 1.3;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.track-artist {
  font-size: 13px; color: var(--muted); margin-top: 2px;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.badges { display: flex; flex-wrap: wrap; gap: 5px; margin-top: 6px; }
.badge {
  font-size: 10px; font-family: ui-monospace, Consolas, monospace;
  color: var(--muted); background: var(--surface-2);
  border: 1px solid var(--border); border-radius: 5px; padding: 2px 6px;
}
.badge.live { color: var(--amber); border-color: rgba(245,166,35,.4); }
.badge.accent { color: var(--accent); border-color: rgba(86,245,196,.35); }
.fav-btn {
  width: 38px; height: 38px; border-radius: 12px; border: 1px solid var(--border);
  background: var(--surface-2); color: var(--muted); font-size: 18px;
  cursor: pointer; flex-shrink: 0; display: flex; align-items: center; justify-content: center;
}
.fav-btn.active { color: #ff5c7c; border-color: rgba(255,92,124,.45); background: rgba(255,92,124,.12); }
.seek-wrap { margin-top: 10px; }
.time-row {
  display: flex; justify-content: space-between; font-size: 11px;
  font-family: ui-monospace, Consolas, monospace; color: var(--muted); margin-top: 3px;
}
input[type=range] {
  -webkit-appearance: none; appearance: none; width: 100%; height: 6px;
  border-radius: 999px; background: var(--surface-2); outline: none; cursor: pointer;
}
input[type=range]::-webkit-slider-thumb {
  -webkit-appearance: none; appearance: none; width: 16px; height: 16px;
  border-radius: 50%; background: var(--accent); border: 2px solid #0b0b12; cursor: pointer;
}
input[type=range]:disabled { opacity: .4; cursor: default; }
.controls-row {
  display: flex; align-items: center; justify-content: space-between; margin-top: 10px; gap: 8px;
}
.podcast-row {
  display: flex; align-items: center; justify-content: center; gap: 10px; margin-top: 10px;
}
.podcast-row .ctrl-btn.ghost {
  font-family: ui-monospace, Consolas, monospace; min-width: 64px; height: 32px;
}
.ctrl-btn {
  border: 1px solid var(--border); background: var(--surface-2); color: var(--text);
  border-radius: 12px; height: 42px; min-width: 44px; padding: 0 12px;
  font-size: 14px; font-weight: 600; cursor: pointer;
  display: inline-flex; align-items: center; justify-content: center;
}
.ctrl-btn.primary {
  background: var(--accent); color: #08120e; border-color: transparent;
  min-width: 72px; font-size: 15px; border-radius: 14px;
}
.ctrl-btn.ghost { background: transparent; color: var(--muted); font-size: 12px; height: 34px; min-width: 42px; }
.ctrl-btn.ghost.active { color: var(--accent); border-color: var(--accent); }
.ctrl-btn:disabled { opacity: .4; cursor: default; }
.ctrl-btn:active { transform: scale(.96); }
.vol-row { display: flex; align-items: center; gap: 10px; margin-top: 10px; }
.vol-label { font-size: 11px; color: var(--muted); width: 34px; text-align: right; font-family: ui-monospace, Consolas, monospace; }
.feedback-row {
  display: flex; gap: 6px; overflow-x: auto; padding-bottom: 2px; scrollbar-width: none;
}
.feedback-row::-webkit-scrollbar { display: none; }
.chip-btn {
  border: 1px solid var(--border); background: var(--surface); color: var(--muted);
  border-radius: 8px; padding: 5px 10px; font-size: 11px; white-space: nowrap; cursor: pointer;
}
.chip-btn:active { border-color: var(--accent); color: var(--accent); }
.tabs { display: flex; gap: 6px; }
.tab-btn {
  flex: 1; padding: 8px; border-radius: 10px; border: 1px solid var(--border);
  background: var(--surface); color: var(--muted); font-size: 13px; font-weight: 600; cursor: pointer;
}
.tab-btn.active { background: var(--accent-dim); border-color: var(--accent); color: var(--accent); }
.panel {
  flex: 1; min-height: 0; background: var(--surface); border: 1px solid var(--border);
  border-radius: 14px; display: flex; flex-direction: column; overflow: hidden;
}
.scroll-area { flex: 1; overflow-y: auto; padding: 12px 14px; }
.lyric-empty { color: var(--muted); text-align: center; margin-top: 36px; font-size: 13px; }
.lyric-line {
  padding: 7px 8px; border-radius: 8px; color: var(--muted); font-size: 14px;
  line-height: 1.45; transition: color .2s, background .2s; cursor: pointer;
}
.lyric-line.active {
  color: var(--accent); font-weight: 700; font-size: 16px;
  background: var(--accent-dim);
}
.chat-list { display: flex; flex-direction: column; gap: 8px; }
.msg {
  max-width: 88%; padding: 8px 11px; border-radius: 11px; font-size: 13px; line-height: 1.45;
  white-space: pre-wrap; word-break: break-word;
}
.msg.assistant { align-self: flex-start; background: var(--surface-2); border: 1px solid var(--border); }
.msg.user { align-self: flex-end; background: var(--accent-dim); border: 1px solid rgba(86,245,196,.35); }
.msg-author { font-size: 10px; color: var(--muted); margin-bottom: 2px; }
.chat-compose {
  display: flex; gap: 8px; padding: 10px; border-top: 1px solid var(--border); background: #10101a;
}
.chat-input {
  flex: 1; min-width: 0; border: 1px solid var(--border); background: var(--surface-2);
  color: var(--text); border-radius: 10px; padding: 9px 11px; font-size: 14px; outline: none;
}
.chat-input:focus { border-color: var(--accent); }
.send-btn {
  border: none; background: var(--accent); color: #08120e; font-weight: 700;
  border-radius: 10px; padding: 0 14px; font-size: 13px; cursor: pointer;
}
.send-btn:disabled { opacity: .45; }
.sleep-sheet {
  position: fixed; inset: 0; background: rgba(0,0,0,.62); display: none;
  align-items: flex-end; justify-content: center; z-index: 40; padding: 12px;
}
.sleep-sheet.open { display: flex; }
.sheet-card {
  width: 100%; max-width: 460px; background: var(--surface); border: 1px solid var(--border);
  border-radius: 16px; padding: 14px; display: flex; flex-direction: column; gap: 10px;
}
.sheet-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
.auth-overlay {
  position: fixed; inset: 0; background: var(--bg); display: none;
  align-items: center; justify-content: center; padding: 20px; z-index: 50;
}
.auth-overlay.open { display: flex; }
.auth-card {
  width: 100%; max-width: 360px; background: var(--surface); border: 1px solid var(--border);
  border-radius: 16px; padding: 20px; display: flex; flex-direction: column; gap: 12px;
}
.auth-err { color: var(--danger); font-size: 12px; min-height: 16px; }
.browse-head { padding: 10px 12px 8px; border-bottom: 1px solid var(--border); display: flex; flex-direction: column; gap: 8px; background: #11111c; }
.sub-pills { display: flex; gap: 6px; overflow-x: auto; scrollbar-width: none; }
.sub-pills::-webkit-scrollbar { display: none; }
.sub-pill { border: 1px solid var(--border); background: var(--surface-2); color: var(--muted); border-radius: 999px; padding: 5px 11px; font-size: 12px; font-weight: 600; white-space: nowrap; cursor: pointer; }
.sub-pill.active { border-color: var(--accent); color: var(--accent); background: var(--accent-dim); }
.browse-toolbar { display: flex; gap: 6px; align-items: center; }
.browse-search { flex: 1; min-width: 0; border: 1px solid var(--border); background: var(--surface-2); color: var(--text); border-radius: 9px; padding: 7px 10px; font-size: 13px; outline: none; }
.browse-search:focus { border-color: var(--accent); }
.browse-refresh { border: 1px solid var(--border); background: var(--surface-2); color: var(--text); border-radius: 9px; padding: 7px 11px; font-size: 12px; font-weight: 600; cursor: pointer; white-space: nowrap; }
.browse-refresh:disabled { opacity: .45; cursor: default; }
.browse-subbar { display: flex; gap: 6px; overflow-x: auto; scrollbar-width: none; align-items: center; }
.browse-subbar::-webkit-scrollbar { display: none; }
.browse-select { width: 100%; border: 1px solid var(--border); background: var(--surface-2); color: var(--text); border-radius: 9px; padding: 7px 10px; font-size: 13px; outline: none; }
.browse-list { display: flex; flex-direction: column; gap: 7px; }
.browse-item { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 9px 11px; border-radius: 11px; background: var(--surface-2); border: 1px solid var(--border); cursor: pointer; }
.browse-item.pod { flex-direction: column; align-items: stretch; cursor: default; }
.browse-item.active { border-color: var(--accent); background: var(--accent-dim); }
.browse-item.disabled { opacity: .55; cursor: default; }
.browse-main { min-width: 0; flex: 1; }
.browse-title { font-size: 13px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.browse-sub { font-size: 11px; color: var(--muted); margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.browse-summary { font-size: 12px; color: var(--muted); line-height: 1.4; margin-top: 4px; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.browse-tags { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 4px; }
.browse-actions { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }
.browse-pod-actions { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px; }
.mini-btn { border: 1px solid var(--border); background: var(--surface); color: var(--text); border-radius: 8px; padding: 5px 9px; font-size: 11px; font-weight: 600; cursor: pointer; white-space: nowrap; }
.mini-btn.accent { border-color: rgba(86,245,196,.45); color: var(--accent); background: var(--accent-dim); }
.mini-btn.fav.active { color: #ff5c7c; border-color: rgba(255,92,124,.45); background: rgba(255,92,124,.12); }
.mini-btn:disabled { opacity: .45; cursor: default; }
.browse-banner { font-size: 12px; color: var(--muted); background: var(--surface-2); border: 1px solid var(--border); border-radius: 10px; padding: 8px 10px; line-height: 1.4; }
.browse-section-hdr { font-size: 11px; font-weight: 700; color: var(--muted); letter-spacing: .4px; padding: 4px 2px 0; }
.browse-notice { font-size: 11px; color: var(--accent); padding: 2px 2px 0; }
.browse-notice.err { color: var(--danger); }
.hidden { display: none !important; }
</style>
</head>
<body>
<div class="app">
  <div class="header">
    <div class="brand">
      <span id="connDot" class="dot"></span>
      <span class="brand-title">TuanRadio</span>
      <span id="djBadge" class="dj-badge">DJ</span>
    </div>
    <div class="header-pills">
      <button id="focusBtn" class="pill-btn" type="button">Focus</button>
      <button id="sleepBtn" class="pill-btn" type="button">Sleep</button>
    </div>
  </div>

  <div class="card">
    <div class="now-top">
      <div class="track-meta">
        <div id="trackTitle" class="track-title">---</div>
        <div id="trackArtist" class="track-artist">---</div>
        <div class="badges">
          <span id="stateBadge" class="badge accent">Idle</span>
          <span id="sourceBadge" class="badge hidden"></span>
          <span id="qualityBadge" class="badge hidden"></span>
          <span id="liveBadge" class="badge live hidden">LIVE</span>
        </div>
      </div>
      <button id="favBtn" class="fav-btn" type="button" aria-label="Favorite">&#9825;</button>
    </div>

    <div class="seek-wrap">
      <input id="seekBar" type="range" min="0" max="100" step="0.5" value="0">
      <div class="time-row">
        <span id="posText">0:00</span>
        <span id="durText">0:00</span>
      </div>
    </div>

    <div class="controls-row">
      <button id="shuffleBtn" class="ctrl-btn ghost" type="button">Shuffle</button>
      <div style="display:flex;gap:8px;align-items:center;">
        <button id="prevBtn" class="ctrl-btn" type="button">&#9198;</button>
        <button id="playBtn" class="ctrl-btn primary" type="button">&#9654;</button>
        <button id="nextBtn" class="ctrl-btn" type="button">&#9197;</button>
      </div>
      <button id="repeatBtn" class="ctrl-btn ghost" type="button">DJ</button>
    </div>

    <div id="podcastRow" class="podcast-row hidden">
      <button id="podBackBtn" class="ctrl-btn ghost" type="button">-15s</button>
      <button id="podSpeedBtn" class="ctrl-btn ghost" type="button">1.0x</button>
      <button id="podFwdBtn" class="ctrl-btn ghost" type="button">+30s</button>
    </div>

    <div class="vol-row">
      <span style="font-size:12px;color:var(--muted);">VOL</span>
      <input id="volBar" type="range" min="0" max="100" step="1" value="80">
      <span id="volText" class="vol-label">80%</span>
    </div>
  </div>

  <div id="feedbackRow" class="feedback-row">
    <button class="chip-btn" data-fb="like" type="button" id="fbLike">Like</button>
    <button class="chip-btn" data-fb="dislike" type="button" id="fbDislike">Skip</button>
    <button class="chip-btn" data-fb="similar" type="button" id="fbSimilar">Similar</button>
    <button class="chip-btn" data-fb="calm" type="button" id="fbCalm">Calm</button>
    <button class="chip-btn" data-fb="energetic" type="button" id="fbEnergetic">Upbeat</button>
    <button class="chip-btn" data-fb="story" type="button" id="fbStory">Story</button>
  </div>

  <div class="tabs">
    <button id="tabLyrics" class="tab-btn active" type="button">Lyrics</button>
    <button id="tabBrowse" class="tab-btn" type="button">Stations / Library</button>
    <button id="tabChat" class="tab-btn" type="button">AI DJ / Request</button>
  </div>

  <div id="lyricsPanel" class="panel">
    <div id="lyricsScroll" class="scroll-area"></div>
  </div>

  <div id="browsePanel" class="panel hidden">
    <div class="browse-head">
      <div class="sub-pills">
        <button class="sub-pill active" data-sec="program" type="button" id="secProgram">Program</button>
        <button class="sub-pill" data-sec="library" type="button" id="secLibrary">Playlist</button>
        <button class="sub-pill" data-sec="favorites" type="button" id="secFavorites">Favorites</button>
        <button class="sub-pill" data-sec="radio" type="button" id="secRadio">Radio</button>
        <button class="sub-pill" data-sec="podcasts" type="button" id="secPodcasts">Podcasts</button>
      </div>
      <div class="browse-toolbar">
        <input id="browseSearch" class="browse-search" type="text" autocomplete="off" placeholder="Filter or search...">
        <button id="browseRefreshBtn" class="browse-refresh" type="button">Refresh</button>
      </div>
      <div id="browseRadioCats" class="browse-subbar hidden"></div>
      <div id="browsePodFeedWrap" class="browse-subbar hidden">
        <select id="browsePodFeedSelect" class="browse-select"></select>
      </div>
      <div id="browseNotice" class="browse-notice hidden"></div>
    </div>
    <div id="browseScroll" class="scroll-area">
      <div id="browseList" class="browse-list"></div>
    </div>
  </div>

  <div id="chatPanel" class="panel hidden">
    <div id="chatScroll" class="scroll-area">
      <div id="chatList" class="chat-list"></div>
    </div>
    <form id="chatForm" class="chat-compose">
      <input id="chatInput" class="chat-input" type="text" maxlength="500" autocomplete="off" placeholder="Request a song or talk to AI DJ...">
      <button id="sendBtn" class="send-btn" type="submit">Send</button>
    </form>
  </div>
</div>

<div id="sleepSheet" class="sleep-sheet">
  <div class="sheet-card">
    <div style="display:flex;justify-content:space-between;align-items:center;">
      <strong id="sleepTitle">Sleep Timer</strong>
      <button id="sleepCloseBtn" class="pill-btn" type="button">Close</button>
    </div>
    <div class="sheet-grid">
      <button class="ctrl-btn ghost" data-sleep="0" type="button" id="sleepOff">Off</button>
      <button class="ctrl-btn ghost" data-sleep="15" type="button">15m</button>
      <button class="ctrl-btn ghost" data-sleep="30" type="button">30m</button>
      <button class="ctrl-btn ghost" data-sleep="45" type="button">45m</button>
      <button class="ctrl-btn ghost" data-sleep="60" type="button">60m</button>
      <button class="ctrl-btn ghost" data-sleep="90" type="button">90m</button>
    </div>
    <button class="ctrl-btn ghost" data-sleep="-1" type="button" id="sleepTrackEnd" style="width:100%;">Stop after track</button>
  </div>
</div>

<div id="authOverlay" class="auth-overlay">
  <form id="authForm" class="auth-card">
    <strong id="authTitle">TuanRadio Pairing</strong>
    <div id="authDesc" style="font-size:13px;color:var(--muted);">Scan the desktop QR code or paste the pairing token to connect.</div>
    <input id="tokenInput" class="chat-input" type="text" placeholder="Access token" autocomplete="off">
    <div id="authErr" class="auth-err"></div>
    <button id="authSubmit" class="send-btn" style="padding:10px;" type="submit">Connect</button>
  </form>
</div>

<script>
(() => {
  const TOKEN_KEY = "tuanradio_remote_token";
  const el = id => document.getElementById(id);
  let token = "";
  let state = null;
  let ws = null;
  let reconnectTimer = null;
  let draggingSeek = false;
  let draggingVol = false;
  let activeTab = "lyrics";
  let lastSnapshotMs = 0;
  let browseSection = "program";
  let browseCategory = "";
  let browseFeed = "";
  let browseData = null;
  let browseLoading = false;
  let browseReqSeq = 0;
  let browseDebounce = null;
  let browseNoticeTimer = null;
  let prevProgramLoading = false;
  let prevDigestKey = "";
  let prevTrackTitle = "";
  let prevFavorite = false;
  let browseStale = false;
  let browseSnapshotTitle = "";

  const I18N = {
    zh: {
      focus: "专注", sleep: "定时", live: "直播", pod: "播客", shuffle: "随机",
      fbLike: "喜欢", fbDislike: "不喜欢", fbSimilar: "相似", fbCalm: "舒缓", fbEnergetic: "欢快", fbStory: "故事",
      tabLyrics: "歌词", tabBrowse: "选台 · 曲库", tabChat: "点歌 / AI DJ",
      secProgram: "节目单", secLibrary: "歌单", secFavorites: "收藏", secRadio: "电台", secPodcasts: "播客",
      browseSearchPh: "筛选关键词或搜索...", browseSearchRadioPh: "搜索全球 / 自定义电台...", browseRefresh: "刷新",
      browseLoading: "正在加载...", browseEmpty: "暂无内容", browsePlayNext: "下一首",
      browseFavSongs: "收藏歌曲", browseFavStations: "收藏电台", browseCustom: "自定义",
      podPlay: "收听", podResume: "续播", podRestart: "从头", podDigest: "AI 速读", podDigesting: "速读中...",
      queuedNext: "已安排下一首播放", digestStarted: "AI 正在生成速读并加入聊天流...",
      errGeneric: "操作未成功", errUnsafeUrl: "播客地址被安全策略拦截", errNotPlayable: "该曲目暂不可播", errNoShownotes: "该单集暂无 Shownotes 简介",
      chatPlaceholder: "输入歌名、歌手，或对 AI DJ 说...", send: "发送", sending: "处理中",
      sleepTitle: "睡眠定时", close: "关闭", sleepOff: "关闭", sleepTrackEnd: "播完当前曲停止",
      authTitle: "连接 TuanRadio 遥控器", authDesc: "使用电脑端二维码扫码直连，或粘贴当前访问令牌：",
      authPlaceholder: "输入访问令牌", authConnect: "连接", authUnauthorized: "访问令牌无效或已刷新，请重新扫码或输入新令牌。",
      djRole: "AI 主播", userRole: "我"
    },
    en: {
      focus: "Focus", sleep: "Sleep", live: "LIVE", pod: "PODCAST", shuffle: "Shuffle",
      fbLike: "Like", fbDislike: "Dislike", fbSimilar: "Similar", fbCalm: "Calm", fbEnergetic: "Upbeat", fbStory: "Story",
      tabLyrics: "Lyrics", tabBrowse: "Stations · Library", tabChat: "Request / AI DJ",
      secProgram: "Program", secLibrary: "Playlist", secFavorites: "Favorites", secRadio: "Radio", secPodcasts: "Podcasts",
      browseSearchPh: "Filter keyword...", browseSearchRadioPh: "Search global / custom radio...", browseRefresh: "Refresh",
      browseLoading: "Loading...", browseEmpty: "Nothing here", browsePlayNext: "Next",
      browseFavSongs: "Favorite Songs", browseFavStations: "Favorite Radio", browseCustom: "Custom",
      podPlay: "Play", podResume: "Resume", podRestart: "Restart", podDigest: "AI Digest", podDigesting: "Digesting...",
      queuedNext: "Queued to play next", digestStarted: "Generating AI digest in chat...",
      errGeneric: "Action failed", errUnsafeUrl: "Audio URL blocked by security policy", errNotPlayable: "Track is not playable", errNoShownotes: "No shownotes available",
      chatPlaceholder: "Request a song or chat with AI DJ...", send: "Send", sending: "Busy",
      sleepTitle: "Sleep Timer", close: "Close", sleepOff: "Off", sleepTrackEnd: "After track",
      authTitle: "Connect TuanRadio Remote", authDesc: "Scan the desktop QR code or paste the pairing token:",
      authPlaceholder: "Access token", authConnect: "Connect", authUnauthorized: "Invalid or rotated token. Rescan QR or enter the new token.",
      djRole: "AI DJ", userRole: "You"
    }
  };

  function t() { return (state && state.language === "en") ? I18N.en : I18N.zh; }

  function applyLabels() {
    const l = t();
    el("focusBtn").textContent = l.focus;
    el("sleepBtn").textContent = (state && state.isSleepTimerActive && state.sleepTimerText) ? state.sleepTimerText : l.sleep;
    el("shuffleBtn").textContent = l.shuffle;
    el("fbLike").textContent = l.fbLike;
    el("fbDislike").textContent = l.fbDislike;
    el("fbSimilar").textContent = l.fbSimilar;
    el("fbCalm").textContent = l.fbCalm;
    el("fbEnergetic").textContent = l.fbEnergetic;
    el("fbStory").textContent = l.fbStory;
    el("tabLyrics").textContent = l.tabLyrics;
    el("tabBrowse").textContent = l.tabBrowse;
    el("tabChat").textContent = l.tabChat;
    el("secProgram").textContent = l.secProgram;
    el("secLibrary").textContent = l.secLibrary;
    el("secFavorites").textContent = l.secFavorites;
    el("secRadio").textContent = l.secRadio;
    el("secPodcasts").textContent = l.secPodcasts;
    el("browseSearch").placeholder = browseSection === "radio" ? l.browseSearchRadioPh : l.browseSearchPh;
    el("browseRefreshBtn").textContent = l.browseRefresh;
    el("chatInput").placeholder = l.chatPlaceholder;
    el("sendBtn").textContent = (state && state.isDjProcessing) ? l.sending : l.send;
    el("sleepTitle").textContent = l.sleepTitle;
    el("sleepCloseBtn").textContent = l.close;
    el("sleepOff").textContent = l.sleepOff;
    el("sleepTrackEnd").textContent = l.sleepTrackEnd;
    el("authTitle").textContent = l.authTitle;
    el("authDesc").textContent = l.authDesc;
    el("tokenInput").placeholder = l.authPlaceholder;
    el("authSubmit").textContent = l.authConnect;
  }

  function fmtTime(sec) {
    if (!isFinite(sec) || sec < 0) sec = 0;
    const m = Math.floor(sec / 60);
    const s = Math.floor(sec % 60);
    return m + ":" + (s < 10 ? "0" + s : s);
  }

  function initToken() {
    const params = new URLSearchParams(window.location.search);
    const fromQuery = params.get("token");
    if (fromQuery) {
      token = fromQuery.trim();
      try { localStorage.setItem(TOKEN_KEY, token); } catch {}
      try {
        params.delete("token");
        const clean = window.location.pathname + (params.toString() ? "?" + params.toString() : "");
        window.history.replaceState({}, "", clean);
      } catch {}
    } else {
      try { token = localStorage.getItem(TOKEN_KEY) || ""; } catch {}
    }
  }

  function showAuth(errMsg) {
    el("authErr").textContent = errMsg || "";
    el("authOverlay").classList.add("open");
    el("connDot").className = "dot";
  }

  function hideAuth() {
    el("authErr").textContent = "";
    el("authOverlay").classList.remove("open");
  }

  function sendCommand(payload) {
    if (!token) { showAuth(); return; }
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(payload));
      return;
    }
    fetch("/api/command", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Remote-Token": token },
      body: JSON.stringify(payload)
    }).then(r => {
      if (r.status === 401) { showAuth(t().authUnauthorized); return null; }
      return r.json().catch(() => null);
    }).then(res => {
      if (res) handleAck({ action: payload.action, ok: !!res.ok, message: res.message });
      refreshViaRest();
    }).catch(() => {});
  }

  function showBrowseNotice(msg, isErr) {
    const box = el("browseNotice");
    if (!box) return;
    clearTimeout(browseNoticeTimer);
    if (!msg) {
      box.textContent = "";
      box.classList.add("hidden");
      return;
    }
    box.textContent = msg;
    box.classList.toggle("err", !!isErr);
    box.classList.remove("hidden");
    browseNoticeTimer = setTimeout(() => {
      box.classList.add("hidden");
    }, 2600);
  }

  function handleAck(msg) {
    if (!msg) return;
    const l = t();
    if (!msg.ok) {
      const code = msg.message || "";
      const text = code === "unsafe_audio_url" ? l.errUnsafeUrl
        : code === "track_not_playable" ? l.errNotPlayable
        : code === "empty_shownotes" ? l.errNoShownotes
        : (code || l.errGeneric);
      if (activeTab === "browse") showBrowseNotice(text, true);
      return;
    }
    if (msg.action === "enqueue_library_track") {
      showBrowseNotice(l.queuedNext, false);
    } else if (msg.action === "digest_podcast_episode") {
      showBrowseNotice(l.digestStarted, false);
    } else if (msg.action === "toggle_radio_favorite" && activeTab === "browse") {
      loadBrowse(false);
    } else if (msg.action === "refresh_program" && activeTab === "browse" && browseSection === "program") {
      setTimeout(() => {
        if (!state || !state.isProgramLoading) loadBrowse(false);
      }, 600);
    }
  }

  function refreshViaRest() {
    if (!token) return;
    fetch("/api/state", { headers: { "X-Remote-Token": token } })
      .then(r => {
        if (r.status === 401) { showAuth(t().authUnauthorized); return null; }
        return r.json();
      })
      .then(data => { if (data) renderState(data); })
      .catch(() => {});
  }

  function renderState(s) {
    state = s;
    lastSnapshotMs = Date.now();
    hideAuth();
    applyLabels();

    const dot = el("connDot");
    dot.className = s.isDjSpeaking ? "dot speaking" : "dot online";
    el("djBadge").textContent = s.djName || "AI DJ";
    el("focusBtn").classList.toggle("active", !!s.isFocusMode);
    el("sleepBtn").classList.toggle("active", !!s.isSleepTimerActive);

    el("trackTitle").textContent = s.title || "---";
    el("trackArtist").textContent = s.artist || "";
    el("stateBadge").textContent = s.radioState || "Idle";
    const srcEl = el("sourceBadge");
    srcEl.textContent = s.source || "";
    srcEl.classList.toggle("hidden", !s.source);
    const qualEl = el("qualityBadge");
    qualEl.textContent = s.quality || "";
    qualEl.classList.toggle("hidden", !s.quality);
    const liveEl = el("liveBadge");
    liveEl.textContent = s.isLiveStream ? t().live : (s.isPodcast ? t().pod : "");
    liveEl.classList.toggle("hidden", !s.isLiveStream && !s.isPodcast);

    const favEl = el("favBtn");
    favEl.textContent = s.isFavorite ? "\u2665" : "\u2661";
    favEl.classList.toggle("active", !!s.isFavorite);

    el("playBtn").textContent = s.isPlaying ? "\u23F8" : "\u25B6";
    el("shuffleBtn").classList.toggle("active", !!s.isShuffled);
    const repeatEl = el("repeatBtn");
    repeatEl.textContent = s.repeatLabel || s.repeatMode || "DJ";
    repeatEl.classList.toggle("active", s.repeatMode !== "none");
    el("sleepTrackEnd").disabled = !s.canStopAfterCurrentTrack;

    const isPod = !!s.isPodcast;
    el("podcastRow").classList.toggle("hidden", !isPod);
    el("feedbackRow").classList.toggle("hidden", isPod);
    if (isPod) {
      const rate = (typeof s.playbackRate === "number" && s.playbackRate > 0) ? s.playbackRate : 1.0;
      const rateStr = (Math.abs(rate - Math.round(rate)) < 0.005 ? rate.toFixed(1) : Number(rate.toFixed(2)).toString()) + "x";
      const speedBtn = el("podSpeedBtn");
      speedBtn.textContent = rateStr;
      speedBtn.classList.toggle("active", Math.abs(rate - 1.0) > 0.01);
    }

    const seek = el("seekBar");
    seek.disabled = !s.canSeek;
    const dur = Math.max(0, s.durationSeconds || 0);
    const pos = Math.max(0, Math.min(dur || 100, s.positionSeconds || 0));
    seek.max = dur > 0 ? dur : 100;
    if (!draggingSeek) {
      seek.value = pos;
      el("posText").textContent = fmtTime(pos);
      el("durText").textContent = s.isLiveStream ? t().live : fmtTime(dur);
    }

    if (!draggingVol) {
      const volPct = Math.round((s.volume ?? 0.8) * 100);
      el("volBar").value = volPct;
      el("volText").textContent = volPct + "%";
    }

    el("sendBtn").disabled = !!s.isDjProcessing;
    renderLyrics(s);
    renderChat(s);

    const progLoading = !!s.isProgramLoading;
    const digestKey = s.digestingEpisodeKey || "";
    const curTitle = s.title || "";
    const curFav = !!s.isFavorite;

    const progFinished = prevProgramLoading && !progLoading;
    const trackOrFavChanged = prevTrackTitle !== curTitle || prevFavorite !== curFav;

    if (activeTab === "browse") {
      if ((progFinished && browseSection === "program") || trackOrFavChanged) {
        loadBrowse(false);
      } else if (browseData && (prevDigestKey !== digestKey || prevProgramLoading !== progLoading)) {
        renderBrowse();
      }
    } else if (progFinished || trackOrFavChanged || prevDigestKey !== digestKey) {
      browseStale = true;
    }
    prevProgramLoading = progLoading;
    prevDigestKey = digestKey;
    prevTrackTitle = curTitle;
    prevFavorite = curFav;
  }

  function loadBrowse(force) {
    if (!token) { showAuth(); return; }
    const seq = ++browseReqSeq;
    browseLoading = true;
    el("browseRefreshBtn").disabled = true;
    if (!browseData || browseData.section !== browseSection || force) {
      const empty = document.createElement("div");
      empty.className = "lyric-empty";
      empty.textContent = t().browseLoading;
      el("browseList").replaceChildren(empty);
    }
    const params = new URLSearchParams();
    params.set("section", browseSection);
    const q = el("browseSearch").value.trim();
    if (q) params.set("q", q);
    if (browseSection === "radio" && browseCategory) params.set("category", browseCategory);
    if (browseSection === "podcasts" && browseFeed) params.set("feed", browseFeed);
    if (force) params.set("force", "1");

    fetch("/api/browse?" + params.toString(), { headers: { "X-Remote-Token": token } })
      .then(r => {
        if (r.status === 401) { showAuth(t().authUnauthorized); return null; }
        return r.json();
      })
      .then(data => {
        if (seq !== browseReqSeq || !data) return;
        browseLoading = false;
        el("browseRefreshBtn").disabled = false;
        browseData = data;
        browseSnapshotTitle = (state && state.title) || "";
        browseStale = false;
        if (data.section === "radio" && data.selectedRadioCategory) browseCategory = data.selectedRadioCategory;
        if (data.section === "podcasts" && data.selectedPodcastFeedUrl) browseFeed = data.selectedPodcastFeedUrl;
        renderBrowse();
      })
      .catch(() => {
        if (seq !== browseReqSeq) return;
        browseLoading = false;
        el("browseRefreshBtn").disabled = false;
      });
  }

  function appendBrowseEmpty(list, msg) {
    const empty = document.createElement("div");
    empty.className = "lyric-empty";
    empty.textContent = msg || t().browseEmpty;
    list.appendChild(empty);
  }

  function makeTrackRow(item, opts) {
    const row = document.createElement("div");
    const useServerCurrent = !state || (state.title || "") === browseSnapshotTitle;
    const isCur = useServerCurrent
      ? !!item.isCurrent
      : !!(state && state.hasTrack && item.title && state.title === item.title && (!item.artist || state.artist === item.artist));
    row.className = "browse-item" + (isCur ? " active" : "") + (opts && opts.disabled ? " disabled" : "");
    const main = document.createElement("div");
    main.className = "browse-main";
    const title = document.createElement("div");
    title.className = "browse-title";
    title.textContent = item.title || "---";
    const subParts = [];
    if (item.artist) subParts.push(item.artist);
    if (item.source) subParts.push(item.source);
    if (item.quality) subParts.push(item.quality);
    if (item.durationSeconds > 0) subParts.push(fmtTime(item.durationSeconds));
    const sub = document.createElement("div");
    sub.className = "browse-sub";
    sub.textContent = subParts.join(" · ");
    main.appendChild(title);
    main.appendChild(sub);
    if (opts && opts.tags && opts.tags.length) {
      const tagsEl = document.createElement("div");
      tagsEl.className = "browse-tags";
      opts.tags.slice(0, 4).forEach(tag => {
        const b = document.createElement("span");
        b.className = "badge";
        b.textContent = tag;
        tagsEl.appendChild(b);
      });
      main.appendChild(tagsEl);
    }
    row.appendChild(main);
    if (!opts || !opts.disabled) {
      row.addEventListener("click", () => { if (opts && opts.onPlay) opts.onPlay(); });
    }
    if (opts && opts.actions && opts.actions.length) {
      const actWrap = document.createElement("div");
      actWrap.className = "browse-actions";
      opts.actions.forEach(a => actWrap.appendChild(a));
      row.appendChild(actWrap);
    }
    return row;
  }

  function renderBrowse() {
    const l = t();
    const radioBar = el("browseRadioCats");
    const podBar = el("browsePodFeedWrap");
    const list = el("browseList");
    radioBar.classList.toggle("hidden", browseSection !== "radio");
    podBar.classList.toggle("hidden", browseSection !== "podcasts");
    el("browseSearch").placeholder = browseSection === "radio" ? l.browseSearchRadioPh : l.browseSearchPh;

    if (!browseData || browseData.section !== browseSection) {
      list.replaceChildren();
      if (browseLoading) appendBrowseEmpty(list, l.browseLoading);
      return;
    }

    const d = browseData;
    list.replaceChildren();

    if (browseSection === "program") {
      if (d.programOpening) {
        const banner = document.createElement("div");
        banner.className = "browse-banner";
        banner.textContent = d.programOpening;
        list.appendChild(banner);
      }
      if (state && state.isProgramLoading) {
        const loadingBanner = document.createElement("div");
        loadingBanner.className = "browse-banner";
        loadingBanner.textContent = l.browseLoading;
        list.appendChild(loadingBanner);
      }
      const items = d.programTracks || [];
      if (items.length === 0) {
        appendBrowseEmpty(list, d.statusText || l.browseEmpty);
        return;
      }
      items.forEach((item, idx) => {
        list.appendChild(makeTrackRow(item, {
          disabled: !item.isPlayable,
          tags: item.tags || [],
          onPlay: () => sendCommand({ action: "play_program_track", text: item.id, intValue: idx })
        }));
      });
      return;
    }

    if (browseSection === "library") {
      const items = d.libraryTracks || [];
      if (items.length === 0) {
        appendBrowseEmpty(list, d.statusText || l.browseEmpty);
        return;
      }
      items.forEach(item => {
        const nextBtn = document.createElement("button");
        nextBtn.className = "mini-btn";
        nextBtn.type = "button";
        nextBtn.textContent = l.browsePlayNext;
        nextBtn.addEventListener("click", ev => {
          ev.stopPropagation();
          sendCommand({ action: "enqueue_library_track", text: item.id });
        });
        list.appendChild(makeTrackRow(item, {
          actions: [nextBtn],
          onPlay: () => sendCommand({ action: "play_library_track", text: item.id, intValue: item.index })
        }));
      });
      return;
    }

    if (browseSection === "favorites") {
      const songs = d.songs || [];
      const stations = d.stations || [];
      if (songs.length === 0 && stations.length === 0) {
        appendBrowseEmpty(list, d.statusText || l.browseEmpty);
        return;
      }
      if (songs.length > 0) {
        const hdr = document.createElement("div");
        hdr.className = "browse-section-hdr";
        hdr.textContent = l.browseFavSongs + " (" + songs.length + ")";
        list.appendChild(hdr);
        songs.forEach(item => {
          const nextBtn = document.createElement("button");
          nextBtn.className = "mini-btn";
          nextBtn.type = "button";
          nextBtn.textContent = l.browsePlayNext;
          nextBtn.addEventListener("click", ev => {
            ev.stopPropagation();
            sendCommand({ action: "enqueue_library_track", text: item.id });
          });
          list.appendChild(makeTrackRow(item, {
            actions: [nextBtn],
            onPlay: () => sendCommand({ action: "play_favorite", text: item.id })
          }));
        });
      }
      if (stations.length > 0) {
        const hdr = document.createElement("div");
        hdr.className = "browse-section-hdr";
        hdr.textContent = l.browseFavStations + " (" + stations.length + ")";
        list.appendChild(hdr);
        stations.forEach(item => {
          const favBtn = document.createElement("button");
          favBtn.className = "mini-btn fav active";
          favBtn.type = "button";
          favBtn.textContent = "\u2665";
          favBtn.addEventListener("click", ev => {
            ev.stopPropagation();
            sendCommand({ action: "toggle_radio_favorite", text: item.stationUuid || item.id });
          });
          list.appendChild(makeTrackRow(item, {
            actions: [favBtn],
            onPlay: () => sendCommand({ action: "play_favorite", text: item.id })
          }));
        });
      }
      return;
    }

    if (browseSection === "radio") {
      radioBar.replaceChildren();
      (d.radioCategories || []).forEach(cat => {
        const chip = document.createElement("button");
        chip.type = "button";
        chip.className = "sub-pill" + (cat.key === (d.selectedRadioCategory || browseCategory) ? " active" : "");
        chip.textContent = cat.name;
        chip.addEventListener("click", () => {
          browseCategory = cat.key;
          el("browseSearch").value = "";
          loadBrowse(false);
        });
        radioBar.appendChild(chip);
      });
      const stations = d.radioStations || [];
      if (stations.length === 0) {
        appendBrowseEmpty(list, d.statusText || l.browseEmpty);
        return;
      }
      stations.forEach(s => {
        const favBtn = document.createElement("button");
        favBtn.className = "mini-btn fav" + (s.isFavorite ? " active" : "");
        favBtn.type = "button";
        favBtn.textContent = s.isFavorite ? "\u2665" : "\u2661";
        favBtn.addEventListener("click", ev => {
          ev.stopPropagation();
          sendCommand({ action: "toggle_radio_favorite", text: s.stationUuid });
        });
        const metaParts = [];
        if (s.isCustom) metaParts.push(l.browseCustom);
        if (s.tags) metaParts.push(s.tags);
        const itemObj = {
          title: s.name,
          artist: metaParts.join(" · "),
          quality: s.quality,
          isCurrent: s.isCurrent
        };
        list.appendChild(makeTrackRow(itemObj, {
          actions: [favBtn],
          onPlay: () => sendCommand({ action: "play_radio_station", text: s.stationUuid })
        }));
      });
      return;
    }

    if (browseSection === "podcasts") {
      const sel = el("browsePodFeedSelect");
      sel.replaceChildren();
      (d.podcastFeeds || []).forEach(f => {
        const opt = document.createElement("option");
        opt.value = f.rssUrl;
        opt.textContent = f.title;
        if (f.rssUrl === (d.selectedPodcastFeedUrl || browseFeed)) opt.selected = true;
        sel.appendChild(opt);
      });
      const eps = d.podcastEpisodes || [];
      if (eps.length === 0) {
        appendBrowseEmpty(list, d.statusText || l.browseEmpty);
        return;
      }
      const activeDigest = (state && state.digestingEpisodeKey) || "";
      eps.forEach(ep => {
        const card = document.createElement("div");
        const isDigesting = state
          ? (activeDigest !== "" && activeDigest.toLowerCase() === (ep.episodeKey || "").toLowerCase())
          : !!ep.isDigesting;
        const useServerCurrent = !state || (state.title || "") === browseSnapshotTitle;
        const isCur = useServerCurrent
          ? !!ep.isCurrent
          : !!(state && state.isPodcast && state.title === ep.title);
        card.className = "browse-item pod" + (isCur ? " active" : "");

        const title = document.createElement("div");
        title.className = "browse-title";
        title.textContent = ep.title || "---";
        card.appendChild(title);

        const sub = document.createElement("div");
        sub.className = "browse-sub";
        sub.textContent = ep.progressBadge ? (ep.meta ? ep.meta + " · " + ep.progressBadge : ep.progressBadge) : (ep.meta || "");
        card.appendChild(sub);

        if (ep.shortSummary) {
          const sum = document.createElement("div");
          sum.className = "browse-summary";
          sum.textContent = ep.shortSummary;
          card.appendChild(sum);
        }

        const actRow = document.createElement("div");
        actRow.className = "browse-pod-actions";

        const playBtn = document.createElement("button");
        playBtn.type = "button";
        playBtn.className = "mini-btn accent";
        playBtn.textContent = (!ep.isCompleted && ep.resumePositionMs > 3000) ? l.podResume : l.podPlay;
        playBtn.addEventListener("click", () => sendCommand({ action: "play_podcast_episode", text: ep.episodeKey, intValue: 0 }));
        actRow.appendChild(playBtn);

        const restartBtn = document.createElement("button");
        restartBtn.type = "button";
        restartBtn.className = "mini-btn";
        restartBtn.textContent = l.podRestart;
        restartBtn.addEventListener("click", () => sendCommand({ action: "play_podcast_episode", text: ep.episodeKey, intValue: 1 }));
        actRow.appendChild(restartBtn);

        if (ep.hasSummary) {
          const digBtn = document.createElement("button");
          digBtn.type = "button";
          digBtn.className = "mini-btn";
          digBtn.disabled = !!isDigesting;
          digBtn.textContent = isDigesting ? l.podDigesting : l.podDigest;
          digBtn.addEventListener("click", () => sendCommand({ action: "digest_podcast_episode", text: ep.episodeKey }));
          actRow.appendChild(digBtn);
        }

        card.appendChild(actRow);
        list.appendChild(card);
      });
    }
  }

  let lastLyricSignature = "";
  let lastActiveLyricIdx = -99;
  function renderLyrics(s) {
    const lines = s.lyricLines || [];
    const firstText = lines.length ? lines[0].text : "";
    const lastText = lines.length ? lines[lines.length - 1].text : "";
    const sig = (s.title || "") + ":" + (s.hasLyrics ? "1" : "0") + ":" + lines.length + ":" + (s.lyricStatusText || "") + ":" + firstText + ":" + lastText;
    const container = el("lyricsScroll");
    if (sig !== lastLyricSignature) {
      lastLyricSignature = sig;
      lastActiveLyricIdx = -99;
      container.replaceChildren();
      if (!s.hasLyrics || lines.length === 0) {
        const empty = document.createElement("div");
        empty.className = "lyric-empty";
        empty.textContent = s.lyricStatusText || "";
        container.appendChild(empty);
        return;
      }
      lines.forEach((line, idx) => {
        const div = document.createElement("div");
        div.className = "lyric-line";
        div.dataset.idx = String(idx);
        div.textContent = line.text || " ";
        div.addEventListener("click", () => {
          if (state && state.canSeek) {
            sendCommand({ action: "seek", value: line.timeMs / 1000.0 });
          }
        });
        container.appendChild(div);
      });
    }
    if (s.hasLyrics && s.currentLyricIndex !== lastActiveLyricIdx) {
      lastActiveLyricIdx = s.currentLyricIndex;
      const children = container.children;
      for (let i = 0; i < children.length; i++) {
        const active = i === s.currentLyricIndex;
        children[i].classList.toggle("active", active);
        if (active && activeTab === "lyrics") {
          children[i].scrollIntoView({ block: "center", behavior: "smooth" });
        }
      }
    }
  }

  let lastChatSig = "";
  function renderChat(s) {
    const msgs = s.messages || [];
    const sig = s.language + "|" + msgs.map(m => (m.role || "") + "|" + (m.timestamp || "") + "|" + (m.author || "") + "|" + (m.text || "")).join("\n");
    if (sig === lastChatSig) return;
    lastChatSig = sig;
    const list = el("chatList");
    list.replaceChildren();
    msgs.forEach(m => {
      const box = document.createElement("div");
      box.className = "msg " + (m.role === "user" ? "user" : "assistant");
      const author = document.createElement("div");
      author.className = "msg-author";
      author.textContent = m.author || (m.role === "user" ? t().userRole : t().djRole);
      const body = document.createElement("div");
      body.textContent = m.text || "";
      box.appendChild(author);
      box.appendChild(body);
      list.appendChild(box);
    });
    const scroll = el("chatScroll");
    scroll.scrollTop = scroll.scrollHeight;
  }

  function connectWs() {
    clearTimeout(reconnectTimer);
    if (!token) { showAuth(); return; }
    if (ws) { try { ws.close(); } catch {} ws = null; }
    const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
    const url = proto + "//" + window.location.host + "/ws?token=" + encodeURIComponent(token);
    ws = new WebSocket(url);
    ws.onopen = () => {
      el("connDot").className = "dot online";
      refreshViaRest();
    };
    ws.onmessage = ev => {
      try {
        const msg = JSON.parse(ev.data);
        if (msg.type === "state" && msg.data) renderState(msg.data);
        else if (msg.type === "ack") handleAck(msg);
      } catch {}
    };
    ws.onclose = ev => {
      el("connDot").className = "dot";
      if (ev.code === 4001) {
        token = "";
        try { localStorage.removeItem(TOKEN_KEY); } catch {}
        showAuth(t().authUnauthorized);
        return;
      }
      reconnectTimer = setTimeout(connectWs, 2500);
    };
    ws.onerror = () => {};
  }

  setInterval(() => {
    if (!state || !state.isPlaying || !state.canSeek || draggingSeek) return;
    const elapsed = (Date.now() - lastSnapshotMs) / 1000.0;
    const nextPos = Math.min(state.durationSeconds || 0, (state.positionSeconds || 0) + elapsed);
    el("seekBar").value = nextPos;
    el("posText").textContent = fmtTime(nextPos);
  }, 500);

  el("playBtn").addEventListener("click", () => sendCommand({ action: "toggle_play" }));
  el("prevBtn").addEventListener("click", () => sendCommand({ action: "previous" }));
  el("nextBtn").addEventListener("click", () => sendCommand({ action: "next" }));
  el("favBtn").addEventListener("click", () => sendCommand({ action: "toggle_favorite" }));
  el("shuffleBtn").addEventListener("click", () => sendCommand({ action: "toggle_shuffle" }));
  el("repeatBtn").addEventListener("click", () => sendCommand({ action: "cycle_repeat" }));
  el("focusBtn").addEventListener("click", () => sendCommand({ action: "toggle_focus_mode" }));

  el("sleepBtn").addEventListener("click", () => el("sleepSheet").classList.add("open"));
  el("sleepCloseBtn").addEventListener("click", () => el("sleepSheet").classList.remove("open"));
  el("sleepSheet").addEventListener("click", e => {
    if (e.target === el("sleepSheet")) el("sleepSheet").classList.remove("open");
  });
  document.querySelectorAll("[data-sleep]").forEach(btn => {
    btn.addEventListener("click", () => {
      const mins = parseInt(btn.getAttribute("data-sleep"), 10);
      sendCommand({ action: "set_sleep_timer", intValue: mins });
      el("sleepSheet").classList.remove("open");
    });
  });
  document.querySelectorAll("[data-fb]").forEach(btn => {
    btn.addEventListener("click", () => {
      sendCommand({ action: "feedback", text: btn.getAttribute("data-fb") });
    });
  });

  el("podBackBtn").addEventListener("click", () => sendCommand({ action: "seek_relative", value: -15 }));
  el("podSpeedBtn").addEventListener("click", () => sendCommand({ action: "cycle_playback_rate" }));
  el("podFwdBtn").addEventListener("click", () => sendCommand({ action: "seek_relative", value: 30 }));

  const seekBar = el("seekBar");
  seekBar.addEventListener("input", () => {
    draggingSeek = true;
    el("posText").textContent = fmtTime(parseFloat(seekBar.value));
  });
  seekBar.addEventListener("change", () => {
    draggingSeek = false;
    sendCommand({ action: "seek", value: parseFloat(seekBar.value) });
  });

  const volBar = el("volBar");
  volBar.addEventListener("input", () => {
    draggingVol = true;
    el("volText").textContent = volBar.value + "%";
  });
  volBar.addEventListener("change", () => {
    draggingVol = false;
    sendCommand({ action: "set_volume", value: parseFloat(volBar.value) / 100.0 });
  });

  function switchTab(tab) {
    activeTab = tab;
    el("tabLyrics").classList.toggle("active", tab === "lyrics");
    el("tabBrowse").classList.toggle("active", tab === "browse");
    el("tabChat").classList.toggle("active", tab === "chat");
    el("lyricsPanel").classList.toggle("hidden", tab !== "lyrics");
    el("browsePanel").classList.toggle("hidden", tab !== "browse");
    el("chatPanel").classList.toggle("hidden", tab !== "chat");
    if (tab === "chat") {
      const scroll = el("chatScroll");
      scroll.scrollTop = scroll.scrollHeight;
    } else if (tab === "browse") {
      if (!browseData || browseData.section !== browseSection || browseStale) {
        browseStale = false;
        loadBrowse(false);
      }
    }
  }

  el("tabLyrics").addEventListener("click", () => switchTab("lyrics"));
  el("tabBrowse").addEventListener("click", () => switchTab("browse"));
  el("tabChat").addEventListener("click", () => switchTab("chat"));

  document.querySelectorAll("[data-sec]").forEach(btn => {
    btn.addEventListener("click", () => {
      const sec = btn.getAttribute("data-sec");
      if (!sec || sec === browseSection) return;
      browseSection = sec;
      document.querySelectorAll("[data-sec]").forEach(b => b.classList.toggle("active", b.getAttribute("data-sec") === sec));
      el("browseSearch").value = "";
      showBrowseNotice("");
      loadBrowse(false);
    });
  });

  el("browseSearch").addEventListener("input", () => {
    clearTimeout(browseDebounce);
    browseDebounce = setTimeout(() => loadBrowse(false), 220);
  });

  el("browseRefreshBtn").addEventListener("click", () => {
    if (browseSection === "program") {
      sendCommand({ action: "refresh_program" });
    } else {
      loadBrowse(true);
    }
  });

  el("browsePodFeedSelect").addEventListener("change", () => {
    browseFeed = el("browsePodFeedSelect").value || "";
    loadBrowse(false);
  });

  el("chatForm").addEventListener("submit", e => {
    e.preventDefault();
    const msg = el("chatInput").value.trim();
    if (!msg) return;
    el("chatInput").value = "";
    sendCommand({ action: "chat", text: msg });
  });

  el("authForm").addEventListener("submit", e => {
    e.preventDefault();
    const val = el("tokenInput").value.trim();
    if (!val) return;
    token = val;
    try { localStorage.setItem(TOKEN_KEY, token); } catch {}
    connectWs();
  });

  initToken();
  applyLabels();
  connectWs();
})();
</script>
</body>
</html>
""";

    public const string FaviconSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="14" fill="#141420"/>
  <circle cx="32" cy="32" r="20" fill="none" stroke="#56f5c4" stroke-width="4"/>
  <circle cx="32" cy="32" r="7" fill="#f5a623"/>
</svg>
""";
}
