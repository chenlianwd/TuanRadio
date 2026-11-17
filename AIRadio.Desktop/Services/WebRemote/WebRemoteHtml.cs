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

    <div class="vol-row">
      <span style="font-size:12px;color:var(--muted);">VOL</span>
      <input id="volBar" type="range" min="0" max="100" step="1" value="80">
      <span id="volText" class="vol-label">80%</span>
    </div>
  </div>

  <div class="feedback-row">
    <button class="chip-btn" data-fb="like" type="button" id="fbLike">Like</button>
    <button class="chip-btn" data-fb="dislike" type="button" id="fbDislike">Skip</button>
    <button class="chip-btn" data-fb="similar" type="button" id="fbSimilar">Similar</button>
    <button class="chip-btn" data-fb="calm" type="button" id="fbCalm">Calm</button>
    <button class="chip-btn" data-fb="energetic" type="button" id="fbEnergetic">Upbeat</button>
    <button class="chip-btn" data-fb="story" type="button" id="fbStory">Story</button>
  </div>

  <div class="tabs">
    <button id="tabLyrics" class="tab-btn active" type="button">Lyrics</button>
    <button id="tabChat" class="tab-btn" type="button">AI DJ / Request</button>
  </div>

  <div id="lyricsPanel" class="panel">
    <div id="lyricsScroll" class="scroll-area"></div>
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

  const I18N = {
    zh: {
      focus: "专注", sleep: "定时", live: "直播", pod: "播客", shuffle: "随机",
      fbLike: "喜欢", fbDislike: "不喜欢", fbSimilar: "相似", fbCalm: "舒缓", fbEnergetic: "欢快", fbStory: "故事",
      tabLyrics: "歌词", tabChat: "点歌 / AI DJ",
      chatPlaceholder: "输入歌名、歌手，或对 AI DJ 说...", send: "发送", sending: "处理中",
      sleepTitle: "睡眠定时", close: "关闭", sleepOff: "关闭", sleepTrackEnd: "播完当前曲停止",
      authTitle: "连接 TuanRadio 遥控器", authDesc: "使用电脑端二维码扫码直连，或粘贴当前访问令牌：",
      authPlaceholder: "输入访问令牌", authConnect: "连接", authUnauthorized: "访问令牌无效或已刷新，请重新扫码或输入新令牌。",
      djRole: "AI 主播", userRole: "我"
    },
    en: {
      focus: "Focus", sleep: "Sleep", live: "LIVE", pod: "PODCAST", shuffle: "Shuffle",
      fbLike: "Like", fbDislike: "Dislike", fbSimilar: "Similar", fbCalm: "Calm", fbEnergetic: "Upbeat", fbStory: "Story",
      tabLyrics: "Lyrics", tabChat: "Request / AI DJ",
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
    el("tabChat").textContent = l.tabChat;
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
      if (r.status === 401) showAuth(t().authUnauthorized);
      else refreshViaRest();
    }).catch(() => {});
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
    el("repeatBtn").textContent = s.repeatLabel || s.repeatMode || "DJ";
    el("sleepTrackEnd").disabled = !s.canStopAfterCurrentTrack;

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

  el("tabLyrics").addEventListener("click", () => {
    activeTab = "lyrics";
    el("tabLyrics").classList.add("active");
    el("tabChat").classList.remove("active");
    el("lyricsPanel").classList.remove("hidden");
    el("chatPanel").classList.add("hidden");
  });
  el("tabChat").addEventListener("click", () => {
    activeTab = "chat";
    el("tabChat").classList.add("active");
    el("tabLyrics").classList.remove("active");
    el("chatPanel").classList.remove("hidden");
    el("lyricsPanel").classList.add("hidden");
    const scroll = el("chatScroll");
    scroll.scrollTop = scroll.scrollHeight;
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
