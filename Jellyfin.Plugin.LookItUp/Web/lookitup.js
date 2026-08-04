(function () {
  'use strict';

  const CLIENT_VERSION = '1.1.0.0';
  const STYLE_ID = 'lookitup-styles';
  const POPUP_ID = 'lookitup-popup';
  const POLL_MS = 200;
  const DEFAULT_POPUP_MS = 2000;
  const JUNK_TERMS = new Set([
    'all', 'new', 'seem', 'consumer', 'yeah', 'heh', 'done', 'away', 'let', 'now',
    'take', 'lie', 'thud', 'street', 'pop', 'car', 'limited', 'tim', 'vic', 'mom',
    'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday',
    'january', 'february', 'march', 'april', 'may', 'june', 'july', 'august',
    'september', 'october', 'november', 'december', 'today', 'tomorrow', 'yesterday',
    'tonight', 'morning', 'afternoon', 'night', 'week', 'month', 'year', 'day', 'time',
    'heh', 'huh', 'whoa', 'wow', 'okay', 'right', 'sure', 'really', 'maybe',
    'nothing', 'everything', 'anything', 'something', 'someone', 'everyone',
    'ownership', 'apparently', 'excuse', 'dentist', 'watch', 'eve', 'jon',
    'taxi', 'dallas', 'ford', 'ltd', 'integra', 'supra', 'volvo'
  ]);

  let annotations = [];
  let currentItemId = null;
  let lastShownTerm = null;
  let shownAtMs = 0;
  let hideTimer = null;
  let popupDurationMs = DEFAULT_POPUP_MS;
  let missingItemTicks = 0;
  let lastResolvedLogId = null;
  let loadInFlight = false;
  let lastLoadErrorAt = 0;
  let lastDiagAt = 0;
  let lastCueLogTerm = null;
  // Terms already shown for their current cue window (don't re-show after auto-hide).
  const shownThisPass = new Set();

  function ensureStyles() {
    let style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement('style');
      style.id = STYLE_ID;
      document.head.appendChild(style);
    }

    style.textContent = `
      #${POPUP_ID} {
        position: fixed !important;
        left: 50% !important;
        bottom: max(10vh, 72px) !important;
        transform: translateX(-50%) !important;
        z-index: 2147483647 !important;
        max-width: min(560px, 92vw) !important;
        width: max-content !important;
        padding: 16px 20px !important;
        border-radius: 14px !important;
        border: 1px solid rgba(255, 255, 255, 0.22) !important;
        background: rgba(8, 12, 20, 0.96) !important;
        color: #f7fafc !important;
        font: 600 16px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif !important;
        box-shadow: 0 16px 48px rgba(0, 0, 0, 0.65) !important;
        opacity: 0;
        visibility: hidden;
        pointer-events: none !important;
        transition: opacity 160ms ease, visibility 160ms ease !important;
        text-align: left !important;
        display: block !important;
      }
      #${POPUP_ID}.visible {
        opacity: 1 !important;
        visibility: visible !important;
        pointer-events: auto !important;
      }
      #${POPUP_ID} .lookitup-term {
        display: block !important;
        font-weight: 800 !important;
        font-size: 17px !important;
        margin-bottom: 6px !important;
        color: #ffffff !important;
      }
      #${POPUP_ID} .lookitup-body {
        display: block !important;
        font-weight: 500 !important;
        color: #e8eef7 !important;
      }
      #${POPUP_ID} a {
        color: #9ecbff !important;
        text-decoration: none !important;
      }
      #${POPUP_ID} a:hover {
        text-decoration: underline !important;
      }
    `;
  }

  function getMountRoot() {
    return (
      document.fullscreenElement ||
      document.webkitFullscreenElement ||
      document.querySelector('.videoPlayerContainer') ||
      document.querySelector('.htmlvideoplayer') ||
      document.querySelector('#videoOsdPage') ||
      document.body
    );
  }

  function ensurePopup() {
    ensureStyles();
    let popup = document.getElementById(POPUP_ID);
    const root = getMountRoot() || document.body;

    if (!popup) {
      popup = document.createElement('div');
      popup.id = POPUP_ID;
      popup.setAttribute('role', 'status');
      popup.setAttribute('aria-live', 'polite');
      root.appendChild(popup);
    } else if (popup.parentElement !== root) {
      root.appendChild(popup);
    }

    return popup;
  }

  function hidePopup() {
    const popup = document.getElementById(POPUP_ID);
    if (popup) {
      popup.classList.remove('visible');
      popup.style.opacity = '0';
      popup.style.visibility = 'hidden';
    }
    lastShownTerm = null;
    shownAtMs = 0;
    if (hideTimer) {
      clearTimeout(hideTimer);
      hideTimer = null;
    }
  }

  function readProp(obj, camel, pascal) {
    if (!obj) {
      return undefined;
    }
    if (obj[camel] !== undefined && obj[camel] !== null) {
      return obj[camel];
    }
    return obj[pascal];
  }

  function showPopup(annotation) {
    const popup = ensurePopup();
    const term = String(readProp(annotation, 'term', 'Term') || '');
    const summary = String(readProp(annotation, 'summary', 'Summary') || '');
    const url = readProp(annotation, 'url', 'Url');

    if (!term && !summary) {
      return;
    }

    // Already on screen for this term — do not refresh the hide timer.
    if (lastShownTerm === term && popup.classList.contains('visible')) {
      return;
    }

    lastShownTerm = term;
    shownAtMs = Date.now();
    popup.innerHTML = '';

    const termEl = document.createElement('span');
    termEl.className = 'lookitup-term';
    termEl.textContent = term || 'Look it up';
    popup.appendChild(termEl);

    const body = document.createElement('div');
    body.className = 'lookitup-body';
    let text = summary;
    if (term && summary.toLowerCase().startsWith((term + ':').toLowerCase())) {
      text = summary.slice(term.length + 1).trim();
    }
    body.textContent = text || summary || 'Mentioned in subtitles';
    popup.appendChild(body);

    if (url) {
      const link = document.createElement('div');
      link.style.marginTop = '8px';
      const a = document.createElement('a');
      a.href = url;
      a.target = '_blank';
      a.rel = 'noopener noreferrer';
      a.textContent = 'Learn more';
      link.appendChild(a);
      popup.appendChild(link);
    }

    popup.classList.remove('visible');
    void popup.offsetWidth;
    popup.classList.add('visible');

    popup.style.cssText +=
      ';position:fixed!important;left:50%!important;bottom:max(10vh,72px)!important;' +
      'transform:translateX(-50%)!important;z-index:2147483647!important;opacity:1!important;visibility:visible!important;';

    if (hideTimer) {
      clearTimeout(hideTimer);
    }
    const duration = Math.max(1000, Number(popupDurationMs) || DEFAULT_POPUP_MS);
    hideTimer = setTimeout(() => hidePopup(), duration);
    console.info('[Look it up] name triggered', {
      term: term,
      summary: summary,
      url: url || null,
      durationMs: duration,
      startMs: readProp(annotation, 'startMs', 'StartMs'),
      endMs: readProp(annotation, 'endMs', 'EndMs'),
      playbackMs: getCurrentTimeMs()
    });
  }

  function getApiClient() {
    return window.ApiClient || window.ServerConnections?.currentApiClient?.() || null;
  }

  function getPlaybackManager() {
    return window.playbackManager || window.PlaybackManager || null;
  }

  function getActivePlayer(pm) {
    if (!pm) {
      return null;
    }
    return pm._currentPlayer || (typeof pm.getCurrentPlayer === 'function' ? pm.getCurrentPlayer() : null);
  }

  function formatGuid(value) {
    if (!value) {
      return null;
    }
    const raw = String(value).replace(/[{}]/g, '');
    if (/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(raw)) {
      return raw;
    }
    const hex = raw.replace(/-/g, '');
    if (!/^[0-9a-fA-F]{32}$/.test(hex)) {
      return null;
    }
    const h = hex.toLowerCase();
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
  }

  function extractItemId(text) {
    if (!text) {
      return null;
    }

    const str = String(text);
    let match = str.match(/\/Videos\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})(?=[/?#]|$)/i);
    if (match) {
      return formatGuid(match[1]);
    }

    match = str.match(/\/Items\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})(?=[/?#]|$)/i);
    if (match) {
      return formatGuid(match[1]);
    }

    match = str.match(/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/);
    if (match) {
      return match[0];
    }

    return null;
  }

  function getVideoElement() {
    const videos = Array.from(document.querySelectorAll('video'));
    if (!videos.length) {
      return null;
    }

    // Prefer the visibly playing / largest player, not a tiny preview.
    const ranked = videos
      .map((v) => {
        const rect = v.getBoundingClientRect();
        const area = Math.max(0, rect.width) * Math.max(0, rect.height);
        const playing = !v.paused && v.readyState >= 2 ? 1 : 0;
        const hasTime = v.currentTime > 0 ? 1 : 0;
        return { v, score: playing * 1e9 + hasTime * 1e8 + area };
      })
      .sort((a, b) => b.score - a.score);

    return ranked[0].v;
  }

  function getCurrentItemId() {
    const pm = getPlaybackManager();
    const player = getActivePlayer(pm);

    try {
      if (pm && player && typeof pm.currentItem === 'function') {
        const item = pm.currentItem(player);
        if (item && (item.Id || item.id)) {
          return formatGuid(item.Id || item.id);
        }
      }
    } catch (err) {
      console.debug('[Look it up] currentItem(player) failed', err);
    }

    try {
      if (pm && typeof pm.getPlayerState === 'function') {
        const state = pm.getPlayerState(player || undefined);
        const item = state?.NowPlayingItem || state?.nowPlayingItem;
        if (item && (item.Id || item.id)) {
          return formatGuid(item.Id || item.id);
        }
      }
    } catch (_) {
      /* ignore */
    }

    const video = getVideoElement();
    const candidates = [
      video?.currentSrc,
      video?.src,
      location.href,
      location.hash
    ];

    if (video) {
      try {
        Array.from(video.querySelectorAll('source')).forEach((el) => candidates.push(el.src));
      } catch (_) {
        /* ignore */
      }
    }

    for (const candidate of candidates) {
      const id = extractItemId(candidate);
      if (id) {
        if (lastResolvedLogId !== id) {
          lastResolvedLogId = id;
          console.info('[Look it up] resolved item id from media url', id);
        }
        return id;
      }
    }

    return null;
  }

  function getCurrentTimeMs() {
    const video = getVideoElement();
    if (video && Number.isFinite(video.currentTime)) {
      return Math.floor(video.currentTime * 1000);
    }

    const pm = getPlaybackManager();
    const player = getActivePlayer(pm);
    try {
      if (pm && player && typeof pm.currentTime === 'function') {
        const seconds = pm.currentTime(player);
        if (typeof seconds === 'number' && Number.isFinite(seconds)) {
          // Jellyfin may return seconds or ticks depending on player.
          if (seconds > 1e10) {
            return Math.floor(seconds / 10000);
          }
          return Math.floor(seconds * 1000);
        }
      }
    } catch (_) {
      /* ignore */
    }

    try {
      if (pm && player && typeof pm.getCurrentTicks === 'function') {
        const ticks = pm.getCurrentTicks(player);
        if (typeof ticks === 'number' && Number.isFinite(ticks)) {
          return Math.floor(ticks / 10000);
        }
      }
    } catch (_) {
      /* ignore */
    }

    try {
      if (player && typeof player.currentTime === 'function') {
        const seconds = player.currentTime();
        if (typeof seconds === 'number' && Number.isFinite(seconds)) {
          return Math.floor(seconds * 1000);
        }
      }
    } catch (_) {
      /* ignore */
    }

    return null;
  }

  function isJunkAnnotation(a) {
    const term = (a.term || '').trim();
    const summary = (a.summary || '').toLowerCase();
    if (!term) {
      return true;
    }
    if (JUNK_TERMS.has(term.toLowerCase())) {
      return true;
    }
    if (term.includes('(disambiguation)') || /disambiguation/i.test(term)) {
      return true;
    }
    if (
      summary.includes('may refer to') ||
      summary.includes('can refer to') ||
      summary.includes('commonly refers to') ||
      summary.includes('usually refers to') ||
      summary.includes('most often refers to')
    ) {
      return true;
    }
    if (/^list of /i.test(term)) {
      return true;
    }
    // Single ALL-CAPS / tiny filler
    if (!term.includes(' ') && term.length <= 3 && term === term.toUpperCase()) {
      return true;
    }
    // Bare calendar words / generic single tokens with dictionary-y blurbs
    if (!term.includes(' ') && /day of the week|month of the year|is a day\b/i.test(summary)) {
      return true;
    }
    return false;
  }

  function normalizeAnnotations(list) {
    return (list || []).map((a) => ({
      term: readProp(a, 'term', 'Term') || '',
      summary: readProp(a, 'summary', 'Summary') || '',
      url: readProp(a, 'url', 'Url') || null,
      startMs: Number(readProp(a, 'startMs', 'StartMs') || 0),
      endMs: Number(readProp(a, 'endMs', 'EndMs') || 0)
    })).filter((a) =>
      a.term &&
      Number.isFinite(a.startMs) &&
      Number.isFinite(a.endMs) &&
      a.endMs >= a.startMs &&
      !isJunkAnnotation(a)
    );
  }

  async function loadAnnotations(itemId) {
    const api = getApiClient();
    if (!api || !itemId) {
      console.warn('[Look it up] cannot load annotations', {
        hasApiClient: !!api,
        itemId
      });
      return;
    }

    if (loadInFlight) {
      return;
    }

    if (Date.now() - lastLoadErrorAt < 15000) {
      return;
    }

    loadInFlight = true;
    console.info('[Look it up] fetching annotations for', itemId);

    try {
      // Rescan when server plugin updates (cache key includes server version).
      let serverVer = 'unknown';
      try {
        const st = await api.ajax({ url: api.getUrl('LookItUp/status'), type: 'GET', dataType: 'json' });
        serverVer = (st && (st.version || st.Version)) || 'unknown';
      } catch (_) {
        /* ignore */
      }
      const forceKey = 'lookitup-force-' + itemId + '-' + serverVer;
      const force = !sessionStorage.getItem(forceKey);
      const url = api.getUrl(`LookItUp/${itemId}`) + (force ? '?force=true' : '');
      const data = await api.ajax({
        url,
        type: 'GET',
        dataType: 'json'
      });
      if (force) {
        sessionStorage.setItem(forceKey, '1');
      }

      if (!data || data.enabled === false || data.Enabled === false) {
        annotations = [];
        console.info('[Look it up] disabled or empty for', itemId, data);
        return;
      }

      // Hard-cap at 2s even if server config is still 5000.
      const rawDuration = Number(data.popupDurationMs || data.PopupDurationMs || DEFAULT_POPUP_MS);
      popupDurationMs = Math.min(DEFAULT_POPUP_MS, Math.max(1000, rawDuration || DEFAULT_POPUP_MS));
      const rawList = data.annotations || data.Annotations || [];
      annotations = normalizeAnnotations(rawList);
      if (data.prepared === false && annotations.length === 0) {
        console.warn(
          '[Look it up] no prepared annotations for this item. Run Prepare library on the plugin page (or Scheduled Tasks).',
          data.hint || ''
        );
      }
      console.info('[Look it up] loaded', annotations.length, 'annotations for', itemId, {
        raw: rawList.length,
        durationMs: popupDurationMs,
        prepared: data.prepared,
        preparedAtUtc: data.preparedAtUtc || data.PreparedAtUtc || null
      });
      console.info(
        '[Look it up] names ready',
        annotations.map((a) => ({
          term: a.term,
          startMs: a.startMs,
          endMs: a.endMs,
          hasSummary: !!a.summary
        }))
      );

      // Immediate pass after load so we don't wait another poll interval.
      tryShowForCurrentTime();
    } catch (err) {
      lastLoadErrorAt = Date.now();
      let detail = err;
      try {
        if (err && typeof err.text === 'function') {
          detail = await err.text();
        } else if (err && typeof err.json === 'function') {
          detail = await err.json();
        }
      } catch (_) {
        /* ignore */
      }
      console.warn('[Look it up] failed to load annotations', detail || err);
      annotations = [];
    } finally {
      loadInFlight = false;
    }
  }

  function tryShowForCurrentTime() {
    ensurePopup();
    const now = getCurrentTimeMs();
    if (now == null || !annotations.length) {
      return;
    }

    const matches = annotations
      .filter((a) => now >= a.startMs && now <= a.endMs)
      .sort((a, b) => {
        const aw = (a.term.match(/\s+/g) || []).length;
        const bw = (b.term.match(/\s+/g) || []).length;
        if (bw !== aw) {
          return bw - aw;
        }
        return b.term.length - a.term.length;
      });

    // Drop remembered terms once their cue window ends.
    for (const term of [...shownThisPass]) {
      if (!matches.some((m) => m.term === term)) {
        shownThisPass.delete(term);
      }
    }

    if (!matches.length) {
      return;
    }

    const active = matches.find((m) => !shownThisPass.has(m.term));
    if (!active) {
      return;
    }

    shownThisPass.add(active.term);
    if (lastCueLogTerm !== active.term) {
      lastCueLogTerm = active.term;
      console.info('[Look it up] cue match → search/popup', {
        term: active.term,
        playbackMs: now,
        window: [active.startMs, active.endMs],
        durationMs: popupDurationMs
      });
    }
    showPopup(active);
  }

  let noItemLogAt = 0;

  function tick() {
    const itemId = getCurrentItemId();
    const video = getVideoElement();
    const videoPlaying = !!(video && !video.paused && video.readyState >= 2);

    if (!itemId) {
      if (videoPlaying && Date.now() - noItemLogAt > 5000) {
        noItemLogAt = Date.now();
        const pm = getPlaybackManager();
        console.warn('[Look it up] video playing but no item id', {
          hasPlaybackManager: !!pm,
          hasPlayer: !!getActivePlayer(pm),
          hash: location.hash,
          videoSrc: video?.currentSrc || video?.src || null
        });
      }
      if (currentItemId && !videoPlaying) {
        missingItemTicks += 1;
        if (missingItemTicks > 8) {
          currentItemId = null;
          annotations = [];
          shownThisPass.clear();
          hidePopup();
          missingItemTicks = 0;
        }
      }
      return;
    }

    missingItemTicks = 0;

    if (itemId !== currentItemId) {
      currentItemId = itemId;
      annotations = [];
      shownThisPass.clear();
      hidePopup();
      loadAnnotations(itemId);
      return;
    }

    const now = getCurrentTimeMs();
    if (Date.now() - lastDiagAt > 4000 && annotations.length) {
      lastDiagAt = Date.now();
      const activeNow = annotations.filter((a) => now != null && now >= a.startMs && now <= a.endMs);
      const next = annotations.find((a) => a.startMs >= (now || 0)) || annotations[0];
      const prev = [...annotations].reverse().find((a) => a.endMs < (now || 0));
      console.info('[Look it up] playback tick', {
        playbackMs: now,
        annotations: annotations.length,
        activeNow: activeNow.map((a) => a.term),
        inCueGap: activeNow.length === 0,
        prevTerm: prev && prev.term,
        prevEndedMs: prev && prev.endMs,
        nextTerm: next && next.term,
        nextAtMs: next && next.startMs,
        nextInSec: next && now != null ? Math.max(0, Math.round((next.startMs - now) / 1000)) : null,
        popupVisible: !!document.getElementById(POPUP_ID)?.classList.contains('visible')
      });
    }

    if (now == null || !annotations.length) {
      return;
    }

    tryShowForCurrentTime();
  }

  async function logServerVersion(attempt) {
    const n = attempt || 0;
    try {
      const api = getApiClient();
      if (!api) {
        if (n < 10) {
          setTimeout(() => logServerVersion(n + 1), 500);
          if (n === 0) {
            console.info('[Look it up] client', CLIENT_VERSION, '(waiting for ApiClient…)');
          }
          return;
        }
        console.info('[Look it up] client', CLIENT_VERSION, '(no ApiClient for server version)');
        return;
      }
      const status = await api.ajax({
        url: api.getUrl('LookItUp/status'),
        type: 'GET',
        dataType: 'json'
      });
      const server = (status && (status.version || status.Version)) || 'unknown';
      console.info('[Look it up] loaded', {
        client: CLIENT_VERSION,
        server: server,
        enabled: status && (status.enabled ?? status.Enabled),
        targetServer: status && (status.targetServer || status.TargetServer)
      });
      if (String(server).startsWith('1.0.5') || String(server).startsWith('1.0.4') || String(server).startsWith('1.0.3')) {
        console.warn('[Look it up] server plugin is outdated (' + server + '). Install 1.0.6.0+ for better name filtering.');
      }
    } catch (err) {
      if (n < 5) {
        setTimeout(() => logServerVersion(n + 1), 500);
        return;
      }
      console.warn('[Look it up] client', CLIENT_VERSION, '— could not read server /LookItUp/status', err);
    }
  }

  function start() {
    ensurePopup();
    setInterval(tick, POLL_MS);
    console.info('[Look it up] overlay ready', CLIENT_VERSION);
    logServerVersion(0);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
