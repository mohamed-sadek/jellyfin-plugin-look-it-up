(function () {
  'use strict';

  const CLIENT_VERSION = '1.2.36.0';
  const STYLE_ID = 'lookitup-styles';
  const STACK_ID = 'lookitup-stack';
  const POPUP_ID = 'lookitup-popup'; // legacy single-popup id (removed on upgrade)
  const SERIES_BTN_ID = 'lookitup-prepare-series-btn';
  const SERIES_STATUS_ID = 'lookitup-prepare-series-status';
  const DETAIL_PANEL_ID = 'lookitup-detail-panel';
  const POLL_MS = 200;
  const DEFAULT_POPUP_MS = 3000;
  const DEFAULT_POPUP_DELAY_MS = 1000;
  const MIN_MATCH_WINDOW_MS = 8000;
  const SETTINGS_REFRESH_MS = 5000;
  const PREPARE_AHEAD_MIN_INTERVAL_MS = 10000;
  const DEFAULT_INCREMENTAL_WINDOW_MS = 300000;
  const MAX_STACKED_POPUPS = 3;
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
    'taxi', 'dallas', 'ford', 'ltd', 'integra', 'supra', 'volvo',
    "i'm", 'im', "i've", 'ive', "i'd", "i'll", "you're", 'youre'
  ]);

  let annotations = [];
  let currentItemId = null;
  let lastShownTerm = null;
  let shownAtMs = 0;
  let hideTimer = null; // legacy; per-card timers live on card elements
  let pendingShowTimers = new Map(); // term -> timeout id
  let popupDurationMs = DEFAULT_POPUP_MS;
  let popupDelayMs = DEFAULT_POPUP_DELAY_MS;
  let popupStyle = {
    fontSizePx: 16,
    textColor: '#f7fafc',
    backgroundColor: 'rgba(8, 12, 20, 0.96)',
    placement: 'BottomCenter',
    edgeOffsetPct: 10
  };
  let missingItemTicks = 0;
  let lastResolvedLogId = null;
  let loadInFlight = false;
  let lastLoadErrorAt = 0;
  let lastDiagAt = 0;
  let lastCueLogTerm = null;
  let lastSettingsFetchAt = 0;
  let settingsFetchInFlight = false;
  let seriesUiItemId = null;
  let seriesUiInFlight = false;
  let seriesStatusTimer = null;
  let adminUserCached = null;
  let adminUserCheckedAt = 0;
  let incrementalPrepareOnPlayback = true;
  let incrementalPrepareWindowMs = DEFAULT_INCREMENTAL_WINDOW_MS;
  let preparedThroughMs = 0;
  let fullyPrepared = false;
  let prepareAheadInFlight = false;
  let lastPrepareAheadAt = 0;
  // Terms already shown for their current cue window (don't re-show after auto-hide).
  const shownThisPass = new Set();

  function pick(obj) {
    if (!obj) {
      return undefined;
    }
    for (var i = 1; i < arguments.length; i++) {
      var key = arguments[i];
      if (obj[key] !== undefined && obj[key] !== null && obj[key] !== '') {
        return obj[key];
      }
    }
    return undefined;
  }

  function sanitizeCssColor(value, fallback) {
    const raw = String(value || '').trim();
    if (!raw || raw.length > 64) {
      return fallback;
    }
    // Allow hex, rgb/rgba, hsl/hsla, and simple named colors ? block url() / expressions.
    if (/[;{}]|url\s*\(|expression\s*\(|javascript:/i.test(raw)) {
      return fallback;
    }
    return raw;
  }

  function placementCss(placement, edgePct) {
    const edge = Math.min(40, Math.max(2, Number(edgePct) || 10));
    const edgeCss = edge + 'vh';
    const sideCss = Math.max(2, Math.round(edge * 0.4)) + 'vw';
    const key = String(placement || 'BottomCenter').replace(/[\s_-]/g, '').toLowerCase();

    switch (key) {
      case 'bottomleft':
        return { top: 'auto', right: 'auto', bottom: edgeCss, left: sideCss, transform: 'none', align: 'flex-start' };
      case 'bottomright':
        return { top: 'auto', right: sideCss, bottom: edgeCss, left: 'auto', transform: 'none', align: 'flex-end' };
      case 'topcenter':
        return { top: edgeCss, right: 'auto', bottom: 'auto', left: '50%', transform: 'translateX(-50%)', align: 'center' };
      case 'topleft':
        return { top: edgeCss, right: 'auto', bottom: 'auto', left: sideCss, transform: 'none', align: 'flex-start' };
      case 'topright':
        return { top: edgeCss, right: sideCss, bottom: 'auto', left: 'auto', transform: 'none', align: 'flex-end' };
      case 'center':
        return { top: '50%', right: 'auto', bottom: 'auto', left: '50%', transform: 'translate(-50%, -50%)', align: 'center' };
      case 'bottomcenter':
      default:
        return { top: 'auto', right: 'auto', bottom: edgeCss, left: '50%', transform: 'translateX(-50%)', align: 'center' };
    }
  }

  function applyPopupSettings(raw) {
    const src = raw || {};
    const duration = Number(pick(src, 'durationMs', 'DurationMs', 'popupDurationMs', 'PopupDurationMs'));
    popupDurationMs = Math.min(30000, Math.max(1000, (Number.isFinite(duration) && duration > 0 ? duration : popupDurationMs) || DEFAULT_POPUP_MS));
    const delay = Number(pick(src, 'delayMs', 'DelayMs', 'popupDelayMs', 'PopupDelayMs'));
    popupDelayMs = Math.min(10000, Math.max(0, Number.isFinite(delay) && delay >= 0 ? delay : (popupDelayMs ?? DEFAULT_POPUP_DELAY_MS)));
    popupStyle = {
      fontSizePx: Math.min(48, Math.max(10, Number(pick(src, 'fontSizePx', 'FontSizePx')) || popupStyle.fontSizePx || 16)),
      textColor: sanitizeCssColor(pick(src, 'textColor', 'TextColor'), popupStyle.textColor || '#f7fafc'),
      backgroundColor: sanitizeCssColor(pick(src, 'backgroundColor', 'BackgroundColor'), popupStyle.backgroundColor || 'rgba(8, 12, 20, 0.96)'),
      placement: String(pick(src, 'placement', 'Placement') || popupStyle.placement || 'BottomCenter'),
      edgeOffsetPct: Math.min(40, Math.max(2, Number(pick(src, 'edgeOffsetPct', 'EdgeOffsetPct')) || popupStyle.edgeOffsetPct || 10))
    };
    ensureStyles();
    console.info('[Look it up] popup settings applied', {
      durationMs: popupDurationMs,
      durationSec: Math.round(popupDurationMs / 1000),
      delayMs: popupDelayMs,
      delaySec: Math.round(popupDelayMs / 100) / 10,
      fontSizePx: popupStyle.fontSizePx,
      placement: popupStyle.placement,
      textColor: popupStyle.textColor,
      backgroundColor: popupStyle.backgroundColor,
      edgeOffsetPct: popupStyle.edgeOffsetPct
    });
  }

  async function refreshPopupSettings(force) {
    const api = getApiClient();
    if (!api || settingsFetchInFlight) {
      return;
    }
    if (!force && Date.now() - lastSettingsFetchAt < SETTINGS_REFRESH_MS) {
      return;
    }
    settingsFetchInFlight = true;
    try {
      const status = await api.ajax({
        url: api.getUrl('LookItUp/status'),
        type: 'GET',
        dataType: 'json'
      });
      lastSettingsFetchAt = Date.now();
      const popupCfg = pick(status, 'popup', 'Popup');
      if (popupCfg) {
        applyPopupSettings(popupCfg);
      } else {
        console.warn('[Look it up] /LookItUp/status has no popup settings ? is the plugin updated to 1.2.15+?');
      }
      applyIncrementalSettings(status);
    } catch (err) {
      console.debug('[Look it up] settings refresh failed', err);
    } finally {
      settingsFetchInFlight = false;
    }
  }

  function applyInlineStackChrome(stack) {
    const pos = placementCss(popupStyle.placement, popupStyle.edgeOffsetPct);
    stack.style.setProperty('position', 'fixed', 'important');
    stack.style.setProperty('top', pos.top, 'important');
    stack.style.setProperty('right', pos.right, 'important');
    stack.style.setProperty('bottom', pos.bottom, 'important');
    stack.style.setProperty('left', pos.left, 'important');
    stack.style.setProperty('transform', pos.transform, 'important');
    stack.style.setProperty('z-index', '2147483647', 'important');
    stack.style.setProperty('align-items', pos.align || 'center', 'important');
  }

  function applyInlineCardChrome(card) {
    const fontPx = popupStyle.fontSizePx;
    card.style.setProperty('background', popupStyle.backgroundColor, 'important');
    card.style.setProperty('color', popupStyle.textColor, 'important');
    card.style.setProperty('font-size', fontPx + 'px', 'important');
    card.style.setProperty('opacity', '1', 'important');
    card.style.setProperty('visibility', 'visible', 'important');
    const termEl = card.querySelector('.lookitup-term');
    if (termEl) {
      termEl.style.setProperty('font-size', Math.round(fontPx * 1.1) + 'px', 'important');
      termEl.style.setProperty('color', popupStyle.textColor, 'important');
    }
    const bodyEl = card.querySelector('.lookitup-body');
    if (bodyEl) {
      bodyEl.style.setProperty('color', popupStyle.textColor, 'important');
    }
  }

  function ensureStyles() {
    let style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement('style');
      style.id = STYLE_ID;
      document.head.appendChild(style);
    }

    const pos = placementCss(popupStyle.placement, popupStyle.edgeOffsetPct);
    const fontPx = popupStyle.fontSizePx;
    const titlePx = Math.round(fontPx * 1.1);
    const text = popupStyle.textColor;
    const bg = popupStyle.backgroundColor;

    style.textContent = `
      #${STACK_ID} {
        position: fixed !important;
        top: ${pos.top} !important;
        right: ${pos.right} !important;
        bottom: ${pos.bottom} !important;
        left: ${pos.left} !important;
        transform: ${pos.transform} !important;
        z-index: 2147483647 !important;
        display: flex !important;
        flex-direction: column-reverse !important;
        gap: 10px !important;
        align-items: ${pos.align || 'center'} !important;
        max-width: min(560px, 92vw) !important;
        pointer-events: none !important;
        width: max-content !important;
      }
      #${STACK_ID} .lookitup-card {
        position: relative !important;
        max-width: min(560px, 92vw) !important;
        width: max-content !important;
        padding: 16px 20px !important;
        border-radius: 14px !important;
        border: 1px solid rgba(255, 255, 255, 0.22) !important;
        background: ${bg} !important;
        color: ${text} !important;
        font: 600 ${fontPx}px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif !important;
        box-shadow: 0 16px 48px rgba(0, 0, 0, 0.65) !important;
        opacity: 0;
        visibility: hidden;
        pointer-events: none !important;
        transition: opacity 160ms ease, visibility 160ms ease !important;
        text-align: left !important;
        display: block !important;
      }
      #${STACK_ID} .lookitup-card.visible {
        opacity: 1 !important;
        visibility: visible !important;
        pointer-events: auto !important;
      }
      #${STACK_ID} .lookitup-row {
        display: flex !important;
        gap: 12px !important;
        align-items: flex-start !important;
      }
      #${STACK_ID} .lookitup-photo {
        flex: 0 0 auto !important;
        width: 64px !important;
        height: 64px !important;
        border-radius: 10px !important;
        object-fit: cover !important;
        background: rgba(255, 255, 255, 0.08) !important;
      }
      #${STACK_ID} .lookitup-copy {
        flex: 1 1 auto !important;
        min-width: 0 !important;
      }
      #${STACK_ID} .lookitup-term {
        display: block !important;
        font-weight: 800 !important;
        font-size: ${titlePx}px !important;
        margin-bottom: 6px !important;
        color: ${text} !important;
      }
      #${STACK_ID} .lookitup-body {
        display: block !important;
        font-weight: 500 !important;
        color: ${text} !important;
        opacity: 0.92 !important;
      }
      #${SERIES_BTN_ID}, #${SERIES_STATUS_ID} {
        position: fixed !important;
        z-index: 100000 !important;
        font: 600 14px/1.3 system-ui, -apple-system, "Segoe UI", sans-serif !important;
      }
      #${SERIES_BTN_ID} {
        right: 20px !important;
        bottom: 20px !important;
        border: 0 !important;
        border-radius: 999px !important;
        padding: 12px 18px !important;
        background: #00a4dc !important;
        color: #fff !important;
        cursor: pointer !important;
        box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35) !important;
      }
      #${SERIES_BTN_ID}:disabled {
        opacity: 0.65 !important;
        cursor: default !important;
      }
      #${SERIES_STATUS_ID} {
        right: 20px !important;
        bottom: 68px !important;
        max-width: min(360px, 90vw) !important;
        padding: 10px 14px !important;
        border-radius: 10px !important;
        background: rgba(8, 12, 20, 0.92) !important;
        color: #f7fafc !important;
        display: none !important;
      }
      #${SERIES_STATUS_ID}.visible {
        display: block !important;
      }
      #${DETAIL_PANEL_ID} {
        position: fixed !important;
        z-index: 100000 !important;
        right: 20px !important;
        bottom: 20px !important;
        max-width: min(420px, 92vw) !important;
        padding: 12px 14px !important;
        border-radius: 12px !important;
        background: rgba(8, 12, 20, 0.94) !important;
        color: #f7fafc !important;
        font: 600 13px/1.35 system-ui, -apple-system, "Segoe UI", sans-serif !important;
        box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4) !important;
        display: none !important;
      }
      #${DETAIL_PANEL_ID}.visible {
        display: block !important;
      }
      #${DETAIL_PANEL_ID} .lookitup-detail-actions {
        display: flex !important;
        flex-wrap: wrap !important;
        gap: 8px !important;
        margin-top: 10px !important;
      }
      #${DETAIL_PANEL_ID} button {
        border: 0 !important;
        border-radius: 999px !important;
        padding: 8px 12px !important;
        background: #00a4dc !important;
        color: #fff !important;
        cursor: pointer !important;
        font: inherit !important;
      }
      #${DETAIL_PANEL_ID} button.secondary {
        background: rgba(255, 255, 255, 0.14) !important;
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

  function ensureStack() {
    ensureStyles();
    const root = getMountRoot() || document.body;
    // Drop legacy single popup if present.
    const legacy = document.getElementById(POPUP_ID);
    if (legacy) {
      legacy.remove();
    }

    let stack = document.getElementById(STACK_ID);
    if (!stack) {
      stack = document.createElement('div');
      stack.id = STACK_ID;
      stack.setAttribute('role', 'status');
      stack.setAttribute('aria-live', 'polite');
      root.appendChild(stack);
    } else if (stack.parentElement !== root) {
      root.appendChild(stack);
    }

    applyInlineStackChrome(stack);
    return stack;
  }

  function clearPendingShowTimers() {
    for (const timer of pendingShowTimers.values()) {
      clearTimeout(timer);
    }
    pendingShowTimers.clear();
  }

  function hideCard(card) {
    if (!card) {
      return;
    }
    if (card._lookitupHideTimer) {
      clearTimeout(card._lookitupHideTimer);
      card._lookitupHideTimer = null;
    }
    card.classList.remove('visible');
    card.style.opacity = '0';
    card.style.visibility = 'hidden';
    const term = card.getAttribute('data-term') || '';
    setTimeout(() => {
      if (card.parentElement) {
        card.remove();
      }
      if (lastShownTerm === term) {
        const top = document.querySelector('#' + STACK_ID + ' .lookitup-card.visible');
        lastShownTerm = top ? top.getAttribute('data-term') : null;
      }
    }, 180);
  }

  function hidePopup() {
    clearPendingShowTimers();
    const stack = document.getElementById(STACK_ID);
    if (stack) {
      for (const card of [...stack.querySelectorAll('.lookitup-card')]) {
        hideCard(card);
      }
    }
    lastShownTerm = null;
    shownAtMs = 0;
    if (hideTimer) {
      clearTimeout(hideTimer);
      hideTimer = null;
    }
  }

  function visibleCardCount() {
    const stack = document.getElementById(STACK_ID);
    if (!stack) {
      return 0;
    }
    return stack.querySelectorAll('.lookitup-card.visible').length;
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

  function proxyImageUrl(imageUrl) {
    const raw = String(imageUrl || '');
    if (!/^https?:\/\//i.test(raw)) {
      return null;
    }
    // Same-origin proxy ? Jellyfin / reverse-proxy CSP often blocks upload.wikimedia.org.
    return '/LookItUp/image?url=' + encodeURIComponent(raw);
  }

  function showPopup(annotation) {
    const stack = ensureStack();
    const term = String(readProp(annotation, 'term', 'Term') || '');
    const summary = String(readProp(annotation, 'summary', 'Summary') || '');
    const url = readProp(annotation, 'url', 'Url');
    const imageUrl = readProp(annotation, 'imageUrl', 'ImageUrl');
    const proxiedImage = proxyImageUrl(imageUrl);

    if (!term && !summary) {
      return;
    }

    // Already on screen for this term ? keep it; do not reset its timer.
    const existing = [...stack.querySelectorAll('.lookitup-card.visible')]
      .find((c) => (c.getAttribute('data-term') || '') === term);
    if (existing) {
      return;
    }

    // Cap stack size: drop oldest visible card.
    while (visibleCardCount() >= MAX_STACKED_POPUPS) {
      const oldest = stack.querySelector('.lookitup-card.visible');
      if (!oldest) {
        break;
      }
      hideCard(oldest);
    }

    lastShownTerm = term;
    shownAtMs = Date.now();

    const card = document.createElement('div');
    card.className = 'lookitup-card';
    card.setAttribute('data-term', term);

    const row = document.createElement('div');
    row.className = 'lookitup-row';

    if (proxiedImage) {
      const img = document.createElement('img');
      img.className = 'lookitup-photo';
      img.src = proxiedImage;
      img.alt = term || '';
      img.loading = 'eager';
      img.decoding = 'async';
      img.referrerPolicy = 'no-referrer';
      img.onerror = function () {
        console.warn('[Look it up] image failed to load', {
          term: term,
          imageUrl: imageUrl || null,
          proxied: proxiedImage
        });
        img.remove();
      };
      row.appendChild(img);
    }

    const copy = document.createElement('div');
    copy.className = 'lookitup-copy';

    const termEl = document.createElement('span');
    termEl.className = 'lookitup-term';
    termEl.textContent = term || 'Look it up';
    copy.appendChild(termEl);

    const body = document.createElement('div');
    body.className = 'lookitup-body';
    let text = summary;
    if (term && summary.toLowerCase().startsWith((term + ':').toLowerCase())) {
      text = summary.slice(term.length + 1).trim();
    }
    body.textContent = text || summary || 'Mentioned in subtitles';
    copy.appendChild(body);

    row.appendChild(copy);
    card.appendChild(row);
    // Newest on top visually (column-reverse): append so it becomes the flex "first"/top.
    stack.appendChild(card);

    void card.offsetWidth;
    card.classList.add('visible');
    applyInlineCardChrome(card);

    const duration = Math.max(1000, Number(popupDurationMs) || DEFAULT_POPUP_MS);
    card._lookitupHideTimer = setTimeout(() => hideCard(card), duration);
    console.info('[Look it up] name triggered', {
      term: term,
      summary: summary,
      url: url || null,
      imageUrl: imageUrl || null,
      proxiedImage: proxiedImage || null,
      durationMs: duration,
      durationSec: Math.round(duration / 1000),
      stacked: visibleCardCount(),
      fontSizePx: popupStyle.fontSizePx,
      placement: popupStyle.placement,
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
    const kind = (a.kind || '').toLowerCase();
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
    // Song / album / track titles — keep artists (kind=person), drop the musical work.
    if (['song', 'album', 'track', 'single', 'ep', 'mixtape', 'soundtrack', 'record'].includes(kind)) {
      return true;
    }
    if (!['person', 'people', 'artist', 'singer', 'musician', 'band'].includes(kind)) {
      let body = summary;
      const termLower = term.toLowerCase();
      if (body.startsWith(termLower)) {
        body = body.slice(termLower.length).replace(/^[\s:\-–]+/, '');
      }
      if (
        /\b(?:is|was)\s+(?:a|an|the)\s+(?:(?:hit|debut|studio|live|concept|cover)\s+)?(?:song|single|album|track|ep|record)\b/.test(body) ||
        /\b(?:song|single|track|album|ep)\s+by\b/.test(body) ||
        /\bfrom\s+the\s+(?:album|soundtrack|ep|mixtape)\b/.test(body) ||
        /\b(?:billboard|chart(?:ing)?)\s+(?:hit|single|song)\b|\bhit\s+single\b/.test(body)
      ) {
        return true;
      }
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
      imageUrl: readProp(a, 'imageUrl', 'ImageUrl') || null,
      kind: readProp(a, 'kind', 'Kind') || null,
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

  function mergeAnnotations(incoming) {
    if (!incoming || !incoming.length) {
      return 0;
    }
    const known = new Set(annotations.map((a) => a.term.toLowerCase()));
    let added = 0;
    for (const raw of incoming) {
      const list = Array.isArray(raw) ? raw : [raw];
      for (const a of list) {
        const norm = normalizeAnnotations([a]);
        if (!norm.length) {
          continue;
        }
        const item = norm[0];
        const key = item.term.toLowerCase();
        if (known.has(key)) {
          continue;
        }
        known.add(key);
        annotations.push(item);
        added += 1;
      }
    }
    if (added > 0) {
      annotations.sort((a, b) => a.startMs - b.startMs);
    }
    return added;
  }

  function applyIncrementalSettings(data) {
    if (!data) {
      return;
    }
    incrementalPrepareOnPlayback = pick(
      data,
      'incrementalPrepareOnPlayback',
      'IncrementalPrepareOnPlayback'
    ) !== false;
    const windowMs = Number(
      pick(data, 'incrementalPrepareWindowMs', 'IncrementalPrepareWindowMs')
    );
    if (Number.isFinite(windowMs) && windowMs >= 60000) {
      incrementalPrepareWindowMs = windowMs;
    }
    preparedThroughMs = Number(pick(data, 'preparedThroughMs', 'PreparedThroughMs') || preparedThroughMs || 0);
    fullyPrepared = !!(pick(data, 'fullyPrepared', 'FullyPrepared'));
  }

  async function maybePrepareAhead(itemId, playbackMs, force) {
    if (!itemId || !incrementalPrepareOnPlayback || fullyPrepared || prepareAheadInFlight) {
      return;
    }
    if (playbackMs == null || !Number.isFinite(playbackMs)) {
      return;
    }
    const aheadTarget = playbackMs + incrementalPrepareWindowMs;
    if (!force && preparedThroughMs >= aheadTarget) {
      return;
    }
    if (!force && Date.now() - lastPrepareAheadAt < PREPARE_AHEAD_MIN_INTERVAL_MS) {
      return;
    }

    const api = getApiClient();
    if (!api) {
      return;
    }

    prepareAheadInFlight = true;
    lastPrepareAheadAt = Date.now();
    try {
      const url = api.getUrl(
        'LookItUp/' + itemId + '/prepare-ahead?playbackMs=' + Math.max(0, Math.floor(playbackMs))
      );
      const data = await api.ajax({
        url,
        type: 'POST',
        dataType: 'json'
      });
      applyIncrementalSettings(data);
      const addedRaw = data.added || data.Added || [];
      const addedCount = mergeAnnotations(addedRaw);
      if (addedCount > 0) {
        console.info('[Look it up] incremental prepare added', addedCount, 'annotation(s)', {
          preparedThroughMs,
          fullyPrepared,
          mode: data.mode || data.Mode
        });
        tryShowForCurrentTime();
      } else if (data.changed || data.Changed) {
        console.info('[Look it up] incremental prepare advanced window', {
          preparedThroughMs,
          fullyPrepared,
          mode: data.mode || data.Mode
        });
      }
      if (data.warning || data.Warning) {
        console.warn('[Look it up] prepare-ahead warning', data.warning || data.Warning);
      }
    } catch (err) {
      console.warn('[Look it up] prepare-ahead failed', err);
    } finally {
      prepareAheadInFlight = false;
    }
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
      // Never force-rescan during playback ? that re-runs AI and often misses cue windows.
      const url = api.getUrl(`LookItUp/${itemId}`);
      const data = await api.ajax({
        url,
        type: 'GET',
        dataType: 'json'
      });

      if (!data || data.enabled === false || data.Enabled === false) {
        annotations = [];
        console.info('[Look it up] disabled or empty for', itemId, data);
        return;
      }

      const rawDuration = Number(data.popupDurationMs || data.PopupDurationMs || DEFAULT_POPUP_MS);
      const popupCfg = data.popup || data.Popup || {
        durationMs: rawDuration,
        fontSizePx: 16,
        textColor: '#f7fafc',
        backgroundColor: 'rgba(8, 12, 20, 0.96)',
        placement: 'BottomCenter',
        edgeOffsetPct: 10
      };
      if (popupCfg.durationMs == null && popupCfg.DurationMs == null) {
        popupCfg.durationMs = rawDuration;
      }
      applyPopupSettings(popupCfg);
      lastSettingsFetchAt = Date.now();
      applyIncrementalSettings(data);
      const rawList = data.annotations || data.Annotations || [];
      annotations = normalizeAnnotations(rawList);
      if (data.prepared === false && annotations.length === 0 && !incrementalPrepareOnPlayback) {
        console.warn(
          '[Look it up] no prepared annotations for this item. Run Prepare on the plugin page.',
          data.hint || ''
        );
      } else if (incrementalPrepareOnPlayback && !fullyPrepared && annotations.length === 0) {
        console.info('[Look it up] incremental prepare will run during playback', data.hint || '');
      }
      console.info('[Look it up] loaded', annotations.length, 'annotations for', itemId, {
        raw: rawList.length,
        durationMs: popupDurationMs,
        placement: popupStyle.placement,
        fontSizePx: popupStyle.fontSizePx,
        prepared: data.prepared,
        preparedAtUtc: data.preparedAtUtc || data.PreparedAtUtc || null
      });
      console.info(
        '[Look it up] names ready',
        annotations.map((a) => ({
          term: a.term,
          startMs: a.startMs,
          endMs: a.endMs,
          at: formatClock(a.startMs),
          hasSummary: !!a.summary
        }))
      );

      // Immediate pass after load so we don't wait another poll interval.
      tryShowForCurrentTime();
      if (incrementalPrepareOnPlayback && !fullyPrepared) {
        await maybePrepareAhead(itemId, getCurrentTimeMs(), true);
      }
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

  function formatClock(ms) {
    const totalSec = Math.max(0, Math.floor(Number(ms) / 1000));
    const m = Math.floor(totalSec / 60);
    const s = totalSec % 60;
    return m + ':' + String(s).padStart(2, '0');
  }

  function annotationWindowEnd(a) {
    return Math.max(a.endMs, a.startMs + MIN_MATCH_WINDOW_MS, a.startMs + popupDurationMs);
  }

  function tryShowForCurrentTime() {
    ensureStack();
    const now = getCurrentTimeMs();
    if (now == null || !annotations.length) {
      return;
    }

    const matches = annotations
      .filter((a) => now >= a.startMs && now <= annotationWindowEnd(a))
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

    const toShow = matches.filter((m) => !shownThisPass.has(m.term));
    if (!toShow.length) {
      return;
    }

    for (const active of toShow) {
      shownThisPass.add(active.term);
      if (lastCueLogTerm !== active.term) {
        lastCueLogTerm = active.term;
        console.info('[Look it up] cue match ? search/popup', {
          term: active.term,
          playbackMs: now,
          at: formatClock(now),
          window: [active.startMs, annotationWindowEnd(active)],
          delayMs: popupDelayMs,
          durationMs: popupDurationMs
        });
      }

      if (pendingShowTimers.has(active.term)) {
        continue;
      }

      const delay = Math.max(0, Number(popupDelayMs) || 0);
      const fire = () => {
        pendingShowTimers.delete(active.term);
        const t = getCurrentTimeMs();
        if (t == null || t < active.startMs || t > annotationWindowEnd(active)) {
          shownThisPass.delete(active.term);
          return;
        }
        showPopup(active);
      };

      if (delay <= 0) {
        fire();
      } else {
        pendingShowTimers.set(active.term, setTimeout(fire, delay));
      }
    }
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
      preparedThroughMs = 0;
      fullyPrepared = false;
      lastPrepareAheadAt = 0;
      hidePopup();
      refreshPopupSettings(true);
      loadAnnotations(itemId);
      return;
    }

    refreshPopupSettings(false);

    const now = getCurrentTimeMs();
    if (videoPlaying && incrementalPrepareOnPlayback && !fullyPrepared) {
      maybePrepareAhead(itemId, now, false);
    }
    if (Date.now() - lastDiagAt > 4000 && annotations.length) {
      lastDiagAt = Date.now();
      const activeNow = annotations.filter((a) => now != null && now >= a.startMs && now <= annotationWindowEnd(a));
      const next = annotations.find((a) => a.startMs >= (now || 0)) || annotations[0];
      const prev = [...annotations].reverse().find((a) => annotationWindowEnd(a) < (now || 0));
      console.info('[Look it up] playback tick', {
        client: CLIENT_VERSION,
        playbackMs: now,
        at: now != null ? formatClock(now) : null,
        annotations: annotations.length,
        activeNow: activeNow.map((a) => a.term),
        inCueGap: activeNow.length === 0,
        prevTerm: prev && prev.term,
        prevEndedMs: prev && annotationWindowEnd(prev),
        nextTerm: next && next.term,
        nextAtMs: next && next.startMs,
        nextAt: next && formatClock(next.startMs),
        nextInSec: next && now != null ? Math.max(0, Math.round((next.startMs - now) / 1000)) : null,
        popupVisible: visibleCardCount() > 0,
        stackedPopups: visibleCardCount()
      });
    }

    if (now == null) {
      return;
    }

    if (!annotations.length) {
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
            console.info('[Look it up] client', CLIENT_VERSION, '(waiting for ApiClient?)');
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
      console.warn('[Look it up] client', CLIENT_VERSION, '? could not read server /LookItUp/status', err);
    }
  }

  function start() {
    ensureStack();
    ensureStyles();
    setInterval(tick, POLL_MS);
    setInterval(syncSeriesPrepareUi, 1500);
    window.addEventListener('hashchange', () => { syncSeriesPrepareUi(); });
    console.info('[Look it up] overlay ready', CLIENT_VERSION);
    logServerVersion(0);
    refreshPopupSettings(true);
    syncSeriesPrepareUi();
  }

  function getDetailsItemIdFromLocation() {
    const hash = String(location.hash || '');
    const href = String(location.href || '');
    let match = hash.match(/(?:^|[#!/])details(?:\?|&)[^#]*\bid=([0-9a-fA-F-]{32,36})/i);
    if (!match) {
      match = href.match(/[?&#]id=([0-9a-fA-F-]{32,36})/i);
    }
    if (!match) {
      return null;
    }
    // Only treat as details page when hash/path looks like details (not video playback urls).
    if (!/details/i.test(hash) && !/details/i.test(href)) {
      return null;
    }
    return formatGuid(match[1]);
  }

  function ensureSeriesUi() {
    ensureStyles();
    let btn = document.getElementById(SERIES_BTN_ID);
    if (!btn) {
      btn = document.createElement('button');
      btn.id = SERIES_BTN_ID;
      btn.type = 'button';
      btn.style.display = 'none';
      btn.textContent = 'Look it up ? prepare this show';
      document.body.appendChild(btn);
    }
    let status = document.getElementById(SERIES_STATUS_ID);
    if (!status) {
      status = document.createElement('div');
      status.id = SERIES_STATUS_ID;
      document.body.appendChild(status);
    }
    return { btn, status };
  }

  function hideSeriesUi() {
    const btn = document.getElementById(SERIES_BTN_ID);
    const status = document.getElementById(SERIES_STATUS_ID);
    const panel = document.getElementById(DETAIL_PANEL_ID);
    if (btn) {
      btn.style.display = 'none';
    }
    if (status) {
      status.classList.remove('visible');
      status.textContent = '';
    }
    if (panel) {
      panel.classList.remove('visible');
      panel.innerHTML = '';
    }
    seriesUiItemId = null;
    if (seriesStatusTimer) {
      clearInterval(seriesStatusTimer);
      seriesStatusTimer = null;
    }
  }

  function ensureDetailPanel() {
    ensureStyles();
    let panel = document.getElementById(DETAIL_PANEL_ID);
    if (!panel) {
      panel = document.createElement('div');
      panel.id = DETAIL_PANEL_ID;
      document.body.appendChild(panel);
    }
    return panel;
  }

  async function renderDetailControls(api, detailsId, itemName) {
    const panel = ensureDetailPanel();
    const seriesBtn = document.getElementById(SERIES_BTN_ID);
    if (seriesBtn) {
      seriesBtn.style.display = 'none';
    }

    let info = null;
    try {
      info = await api.ajax({
        url: api.getUrl('LookItUp/' + detailsId),
        type: 'GET',
        dataType: 'json'
      });
    } catch (err) {
      console.debug('[Look it up] detail status failed', err);
    }

    const prepared = !!(info && (info.prepared || info.Prepared));
    const disabled = !!(info && (info.disabled || info.Disabled));
    const count = (info && (info.annotationCount ?? info.AnnotationCount ?? info.count)) || 0;
    const source = (info && (info.subtitleSource || info.SubtitleSource)) || '-';
    const matchedBy = (info && (info.matchedBy || info.MatchedBy)) || '-';
    const durationOk = info && (info.durationCheckOk ?? info.DurationCheckOk);
    const outcome = (info && (info.prepareOutcome || info.PrepareOutcome)) || '-';

    panel.innerHTML = '';
    const title = document.createElement('div');
    title.textContent = 'Look it up — ' + (itemName || 'item');
    panel.appendChild(title);

    const body = document.createElement('div');
    body.style.opacity = '0.9';
    body.style.marginTop = '6px';
    body.style.whiteSpace = 'pre-wrap';
    body.textContent = [
      prepared ? ('Prepared: ' + count + ' annotation(s)') : 'Not prepared',
      disabled ? 'Popups: DISABLED' : 'Popups: enabled',
      'Subtitle: ' + source + ' (' + matchedBy + ')',
      'Duration check: ' + (durationOk === false ? 'FAILED' : 'ok'),
      'Outcome: ' + outcome
    ].join('\n');
    panel.appendChild(body);

    const actions = document.createElement('div');
    actions.className = 'lookitup-detail-actions';

    const prepBtn = document.createElement('button');
    prepBtn.type = 'button';
    prepBtn.textContent = prepared ? 'Regenerate' : 'Prepare';
    prepBtn.onclick = async function () {
      prepBtn.disabled = true;
      prepBtn.textContent = 'Working…';
      try {
        await api.ajax({
          url: api.getUrl('LookItUp/' + detailsId + '/prepare'),
          type: 'POST',
          dataType: 'json',
          contentType: 'application/json',
          data: '{}'
        });
        await renderDetailControls(api, detailsId, itemName);
      } catch (err) {
        window.Dashboard?.alert?.('Prepare failed: ' + err);
        prepBtn.disabled = false;
        prepBtn.textContent = prepared ? 'Regenerate' : 'Prepare';
      }
    };
    actions.appendChild(prepBtn);

    if (prepared || count > 0) {
      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.className = 'secondary';
      toggle.textContent = disabled ? 'Enable popups' : 'Disable popups';
      toggle.onclick = async function () {
        toggle.disabled = true;
        try {
          await api.ajax({
            url: api.getUrl('LookItUp/' + detailsId + '/' + (disabled ? 'enable' : 'disable')),
            type: 'POST',
            dataType: 'json',
            contentType: 'application/json',
            data: '{}'
          });
          await renderDetailControls(api, detailsId, itemName);
        } catch (err) {
          window.Dashboard?.alert?.('Toggle failed: ' + err);
          toggle.disabled = false;
        }
      };
      actions.appendChild(toggle);
    }

    const openBtn = document.createElement('button');
    openBtn.type = 'button';
    openBtn.className = 'secondary';
    openBtn.textContent = 'Open prepare page';
    openBtn.onclick = function () {
      openPreparePage(detailsId);
    };
    actions.appendChild(openBtn);

    panel.appendChild(actions);
    panel.classList.add('visible');
  }

  function setSeriesStatus(text, visible) {
    const { status } = ensureSeriesUi();
    status.textContent = text || '';
    if (visible && text) {
      status.classList.add('visible');
    } else {
      status.classList.remove('visible');
    }
  }

  async function refreshSeriesPrepareStatus() {
    const api = getApiClient();
    if (!api) {
      return;
    }
    try {
      const s = await api.ajax({
        url: api.getUrl('LookItUp/prepare/status'),
        type: 'GET',
        dataType: 'json'
      });
      const running = !!(s.IsRunning ?? s.isRunning);
      const completed = s.Completed ?? s.completed ?? 0;
      const total = s.Total ?? s.total ?? 0;
      const current = (s.CurrentItem ?? s.currentItem) || '-';
      const withAnn = s.WithAnnotations ?? s.withAnnotations ?? 0;
      const failed = s.Failed ?? s.failed ?? 0;
      const { btn } = ensureSeriesUi();
      if (running) {
        btn.disabled = true;
        btn.textContent = 'Preparing? ' + completed + '/' + total;
        setSeriesStatus(
          'Preparing: ' + current + '\nDone ' + completed + '/' + total +
            ' ? with names ' + withAnn + ' ? failed ' + failed,
          true
        );
      } else {
        btn.disabled = false;
        if (total > 0 && (s.FinishedAtUtc || s.finishedAtUtc)) {
          setSeriesStatus(
            'Finished: ' + withAnn + ' with names, ' + failed + ' failed of ' + total,
            true
          );
        }
        if (seriesStatusTimer) {
          clearInterval(seriesStatusTimer);
          seriesStatusTimer = null;
        }
      }
    } catch (err) {
      console.debug('[Look it up] prepare status failed', err);
    }
  }

  function openPreparePage(itemId) {
    if (!itemId) {
      return;
    }
    const page = 'LookItUpPrepare';
    const target = 'configurationpage?name=' + encodeURIComponent(page) + '&id=' + encodeURIComponent(itemId);
    try {
      if (window.Dashboard && typeof Dashboard.navigate === 'function') {
        Dashboard.navigate(target);
        return;
      }
    } catch (_) {
      /* fall through */
    }
    window.location.hash = '#!/' + target;
  }

  // ---- Prepare page UI (driven from injected script.js ? config-page inline JS is unreliable) ----
  const PrepareUI = {
    pluginUniqueId: 'a8ab0fed-cac9-406d-b98b-58161bf970b8',
    rootItemId: null,
    preview: null,
    statusTimer: null,
    previewGen: 0,
    loading: false,
    lastPage: null
  };

  function preparePageFrom(el) {
    return el && el.closest ? el.closest('#LookItUpPreparePage') : document.querySelector('#LookItUpPreparePage');
  }

  function pq(page, sel) {
    return page ? page.querySelector(sel) : null;
  }

  function readPrepareQueryId() {
    try {
      const hash = String(location.hash || '');
      const search = String(location.search || '');
      const m = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/i) || search.match(/[?&]id=([0-9a-fA-F-]{32,36})/i);
      return m ? m[1] : null;
    } catch (_) {
      return null;
    }
  }

  function ensurePrepareItemId() {
    PrepareUI.rootItemId = readPrepareQueryId() || PrepareUI.rootItemId || seriesUiItemId;
    return PrepareUI.rootItemId;
  }

  function formatPrepareClock(ms) {
    const t = Math.max(0, Math.floor(Number(ms) / 1000));
    const m = Math.floor(t / 60);
    const s = t % 60;
    return m + ':' + String(s).padStart(2, '0');
  }

  function prepareEpisodeLabel(item) {
    const sn = item.seasonNumber ?? item.SeasonNumber;
    const en = item.episodeNumber ?? item.EpisodeNumber;
    const name = item.name || item.Name || 'Item';
    if (sn != null && en != null) {
      return 'S' + String(sn).padStart(2, '0') + 'E' + String(en).padStart(2, '0') + ' ? ' + name;
    }
    return name;
  }

  function prepareCountSelected(page) {
    return page.querySelectorAll('#prepareItemsHost input.lookitup-term:checked').length;
  }

  function prepareUpdateSummary(page) {
    const items = (PrepareUI.preview && (PrepareUI.preview.items || PrepareUI.preview.Items)) || [];
    let totalCand = 0;
    items.forEach((it) => {
      totalCand += ((it.candidates || it.Candidates) || []).length;
    });
    const el = pq(page, '#prepareSummary');
    if (el) {
      el.textContent =
        items.length + ' item(s), ' + totalCand + ' candidate(s), ' + prepareCountSelected(page) + ' selected for AI';
    }
  }

  function prepareSetBusy(page, busy, message) {
    PrepareUI.loading = !!busy;
    const btn = pq(page, '#btnLoadPreview');
    if (btn) {
      btn.disabled = !!busy;
    }
    if (message) {
      const label = pq(page, '#prepareRootLabel');
      if (label) {
        label.textContent = message;
      }
    }
  }

  function prepareRenderPreview(page, preview) {
    PrepareUI.preview = preview;
    const host = pq(page, '#prepareItemsHost');
    const label = pq(page, '#prepareRootLabel');
    if (!host || !label) {
      return;
    }
    host.innerHTML = '';
    const rootName = preview.rootItemName || preview.RootItemName || 'Item';
    const rootType = preview.rootItemType || preview.RootItemType || '';
    label.textContent = rootType + ': ' + rootName;

    const items = preview.items || preview.Items || [];
    if (!items.length) {
      host.innerHTML = '<p>' + (preview.warning || preview.Warning || 'No candidates found.') + '</p>';
      prepareUpdateSummary(page);
      return;
    }

    items.forEach((item) => {
      const itemId = item.itemId || item.ItemId;
      const section = document.createElement('div');
      section.className = 'lookitup-ep';
      section.style.cssText = 'margin:1.25em 0;padding:12px 14px;border-radius:10px;background:rgba(0,0,0,.06);';

      const head = document.createElement('div');
      head.style.cssText = 'display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:8px;';
      const title = document.createElement('strong');
      title.textContent = prepareEpisodeLabel(item);
      head.appendChild(title);

      if (item.alreadyPrepared || item.AlreadyPrepared) {
        const badge = document.createElement('span');
        badge.textContent = 'already prepared';
        badge.style.opacity = '0.7';
        head.appendChild(badge);
      }

      const warn = item.warning || item.Warning;
      if (warn) {
        const w = document.createElement('div');
        w.style.cssText = 'width:100%;opacity:.8;font-size:0.92em;';
        w.textContent = warn;
        head.appendChild(w);
      }

      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.className = 'raised emby-button';
      toggle.textContent = 'Toggle episode';
      toggle.addEventListener('click', () => {
        const boxes = section.querySelectorAll('input.lookitup-term');
        const anyOff = Array.prototype.some.call(boxes, (b) => !b.checked);
        boxes.forEach((b) => {
          b.checked = anyOff;
        });
        prepareUpdateSummary(page);
      });
      head.appendChild(toggle);
      section.appendChild(head);

      const list = document.createElement('div');
      const candidates = item.candidates || item.Candidates || [];
      if (!candidates.length) {
        const empty = document.createElement('div');
        empty.style.opacity = '0.75';
        empty.textContent = 'No name candidates.';
        list.appendChild(empty);
      }

      candidates.forEach((c) => {
        const term = c.term || c.Term || '';
        const suggested = !!(c.suggested ?? c.Suggested);
        const row = document.createElement('label');
        row.style.cssText = 'display:flex;gap:10px;align-items:flex-start;padding:6px 0;cursor:pointer;';
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.className = 'lookitup-term';
        cb.checked = suggested;
        cb.dataset.itemId = itemId;
        cb.dataset.term = term;
        cb.dataset.suggested = suggested ? '1' : '0';
        cb.addEventListener('change', () => prepareUpdateSummary(page));

        const body = document.createElement('div');
        body.style.flex = '1';
        const line1 = document.createElement('div');
        line1.innerHTML = '<strong></strong> <span style="opacity:.7"></span>';
        line1.querySelector('strong').textContent = term;
        line1.querySelector('span').textContent =
          '@' +
          formatPrepareClock(c.startMs ?? c.StartMs) +
          ' ? score ' +
          (c.score ?? c.Score ?? 0) +
          (suggested ? ' ? suggested' : '') +
          ' ? ' +
          (c.reason || c.Reason || '');
        const cue = document.createElement('div');
        cue.style.cssText = 'opacity:.75;font-size:0.92em;margin-top:2px;';
        cue.textContent = c.cueText || c.CueText || '';
        body.appendChild(line1);
        body.appendChild(cue);
        row.appendChild(cb);
        row.appendChild(body);
        list.appendChild(row);
      });

      section.appendChild(list);
      host.appendChild(section);
    });

    prepareUpdateSummary(page);
  }

  function prepareCollectSelections(page) {
    const map = {};
    page.querySelectorAll('#prepareItemsHost input.lookitup-term:checked').forEach((cb) => {
      const id = cb.dataset.itemId;
      const term = cb.dataset.term;
      if (!id || !term) return;
      if (!map[id]) map[id] = [];
      map[id].push(term);
    });
    return Object.keys(map).map((id) => ({ itemId: id, terms: map[id] }));
  }

  function prepareRenderJobStatus(page, s) {
    const el = pq(page, '#prepareJobStatus');
    if (!el) return;
    if (!s) {
      el.textContent = 'No status.';
      return;
    }
    el.textContent = [
      'Running: ' + !!(s.IsRunning ?? s.isRunning),
      'Progress: ' + ((s.Completed ?? s.completed) || 0) + ' / ' + ((s.Total ?? s.total) || 0),
      'With annotations: ' + ((s.WithAnnotations ?? s.withAnnotations) || 0),
      'Skipped: ' + ((s.Skipped ?? s.skipped) || 0),
      'Failed: ' + ((s.Failed ?? s.failed) || 0),
      'Queue pending/failed: ' + ((s.QueuePending ?? s.queuePending) || 0) + ' / ' + ((s.QueueFailed ?? s.queueFailed) || 0),
      'OpenSubtitles downloads: ' + ((s.OpenSubtitlesDownloads ?? s.openSubtitlesDownloads) || 0),
      'Current: ' + ((s.CurrentItem ?? s.currentItem) || '-'),
      'Note: ' + ((s.StatusNote ?? s.statusNote) || '-'),
      'Last error: ' + ((s.LastError ?? s.lastError) || '-'),
      'Finished: ' + ((s.FinishedAtUtc ?? s.finishedAtUtc) || '-')
    ].join('\n');
  }

  async function prepareRefreshStatus(page) {
    page = page || PrepareUI.lastPage || preparePageFrom(document.body);
    const api = getApiClient();
    if (!api || !page) return;
    try {
      const s = await api.ajax({
        url: api.getUrl('LookItUp/prepare/status'),
        type: 'GET',
        dataType: 'json'
      });
      prepareRenderJobStatus(page, s);
      const running = !!(s.IsRunning ?? s.isRunning);
      if (running && !PrepareUI.statusTimer) {
        PrepareUI.statusTimer = setInterval(() => prepareRefreshStatus(page), 2000);
      }
      if (!running && PrepareUI.statusTimer) {
        clearInterval(PrepareUI.statusTimer);
        PrepareUI.statusTimer = null;
      }
    } catch (err) {
      const el = pq(page, '#prepareJobStatus');
      if (el) el.textContent = 'Status error: ' + err;
    }
  }

  async function prepareLoadPreview(page) {
    page = page || PrepareUI.lastPage || preparePageFrom(document.body);
    const api = getApiClient();
    const id = ensurePrepareItemId();
    if (!api) {
      prepareSetBusy(page, false, 'ApiClient not ready. Refresh the page.');
      return;
    }
    if (!id) {
      prepareSetBusy(page, false, 'Missing item id. Open this page from the Look it up button on a show or episode.');
      return;
    }

    PrepareUI.previewGen += 1;
    const gen = PrepareUI.previewGen;
    const input = pq(page, '#txtNamesPerItem');
    const n = parseInt(input && input.value, 10) || 5;
    prepareSetBusy(page, true, 'Loading candidates?');
    const host = pq(page, '#prepareItemsHost');
    const summary = pq(page, '#prepareSummary');
    if (host) host.innerHTML = '';
    if (summary) summary.textContent = '';

    try {
      const preview = await api.ajax({
        url: api.getUrl('LookItUp/' + id + '/prepare-preview') + '?namesPerItem=' + encodeURIComponent(n),
        type: 'GET',
        dataType: 'json'
      });
      if (gen !== PrepareUI.previewGen) return;
      prepareSetBusy(page, false);
      const suggested = preview.suggestedNamesPerItem || preview.SuggestedNamesPerItem || n;
      if (input) input.value = suggested;
      prepareRenderPreview(page, preview);
    } catch (err) {
      if (gen !== PrepareUI.previewGen) return;
      prepareSetBusy(page, false, 'Failed to load preview: ' + err);
    }
  }

  async function prepareStartAll(page) {
    page = page || PrepareUI.lastPage || preparePageFrom(document.body);
    const api = getApiClient();
    const id = ensurePrepareItemId();
    if (!api || !id) {
      window.Dashboard?.alert?.('Missing item id or ApiClient.');
      return;
    }
    if (!window.confirm('Prepare this title with AI? Every local name candidate will be verified in rate-limited batches.')) {
      return;
    }
    const status = pq(page, '#prepareJobStatus');
    if (status) status.textContent = 'Starting prepare…';
    try {
      const result = await api.ajax({
        url: api.getUrl('LookItUp/' + id + '/prepare-series') + '?force=true',
        type: 'POST',
        dataType: 'json',
        contentType: 'application/json',
        data: '{}'
      });
      prepareRenderJobStatus(page, result.status || result.Status || result);
      if (!PrepareUI.statusTimer) {
        PrepareUI.statusTimer = setInterval(() => prepareRefreshStatus(page), 2000);
      }
      if (!(result.started || result.Started)) {
        window.Dashboard?.alert?.('Could not start: ' + (result.error || result.Error || 'unknown'));
      }
      prepareRefreshStatus(page);
    } catch (err) {
      window.Dashboard?.alert?.('Prepare failed: ' + err);
    }
  }

  async function prepareStop(page) {
    page = page || PrepareUI.lastPage || preparePageFrom(document.body);
    const api = getApiClient();
    PrepareUI.previewGen += 1;
    prepareSetBusy(page, false, 'Stopped. Adjust the number and click Load candidates when ready.');
    PrepareUI.preview = null;
    const host = pq(page, '#prepareItemsHost');
    const summary = pq(page, '#prepareSummary');
    const status = pq(page, '#prepareJobStatus');
    if (host) host.innerHTML = '';
    if (summary) summary.textContent = '';
    if (status) status.textContent = 'Stop requested?';
    if (!api) return;
    try {
      const result = await api.ajax({
        url: api.getUrl('LookItUp/prepare/cancel'),
        type: 'POST',
        dataType: 'json',
        contentType: 'application/json',
        data: '{}'
      });
      const cancelled = !!(result.cancelled ?? result.Cancelled);
      if (status) {
        status.textContent = cancelled
          ? 'Prepare job cancelled.'
          : 'Stopped (no background prepare job was running).';
      }
      prepareRefreshStatus(page);
    } catch (err) {
      if (status) status.textContent = 'Stop note: ' + err;
    }
  }

  function prepareSelectSuggested(page) {
    const input = pq(page, '#txtNamesPerItem');
    const n = Math.max(1, parseInt(input && input.value, 10) || 5);
    const boxes = Array.from(page.querySelectorAll('#prepareItemsHost input.lookitup-term'));
    // Prefer server "suggested" flags; fall back to list order (already score-ranked).
    const suggested = boxes.filter((cb) => cb.dataset.suggested === '1');
    const ordered = suggested.length ? suggested : boxes;
    boxes.forEach((cb) => {
      cb.checked = false;
    });
    ordered.slice(0, n).forEach((cb) => {
      cb.checked = true;
    });
    prepareUpdateSummary(page);
  }

  function prepareSelectNone(page) {
    page.querySelectorAll('#prepareItemsHost input.lookitup-term').forEach((cb) => {
      cb.checked = false;
    });
    prepareUpdateSummary(page);
  }

  function initPreparePageIfPresent() {
    const page = preparePageFrom(document.body);
    if (!page) {
      return;
    }
    PrepareUI.lastPage = page;
    ensurePrepareItemId();
    const label = pq(page, '#prepareRootLabel');
    if (label && !PrepareUI.loading) {
      label.textContent = PrepareUI.rootItemId
        ? 'Click Prepare all to verify every subtitle name candidate with AI (batched under your rate limit).'
        : 'Missing item id. Open this page from the Look it up button on a show or episode.';
    }
    const api = getApiClient();
    if (api) {
      prepareRefreshStatus(page);
    }
  }

  function onPrepareDocumentClick(e) {
    const t = e.target;
    if (!t || !t.closest) return;
    const page = t.closest('#LookItUpPreparePage');
    if (!page) return;
    PrepareUI.lastPage = page;

    const btn = t.closest('#btnStartPrepare, #btnCancelPrepare');
    if (!btn) return;

    e.preventDefault();
    e.stopPropagation();

    if (btn.id === 'btnStartPrepare') prepareStartAll(page);
    else if (btn.id === 'btnCancelPrepare') prepareStop(page);
  }

  function startPreparePageWatcher() {
    document.addEventListener('click', onPrepareDocumentClick, true);
    let initTimer = null;
    const scheduleInit = () => {
      if (initTimer) return;
      initTimer = setTimeout(() => {
        initTimer = null;
        initPreparePageIfPresent();
      }, 100);
    };
    const obs = new MutationObserver(() => {
      if (document.getElementById('LookItUpPreparePage')) {
        scheduleInit();
      }
    });
    obs.observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', scheduleInit);
    setInterval(initPreparePageIfPresent, 2000);
    initPreparePageIfPresent();
  }

  async function startSeriesPrepare(forceRebuild) {
    // Kept for status polling compatibility; primary UX is the prepare page.
    const api = getApiClient();
    const itemId = seriesUiItemId || getDetailsItemIdFromLocation();
    if (!api || !itemId) {
      return;
    }
    openPreparePage(itemId);
  }

  async function fetchDetailsItem(api, detailsId) {
    try {
      if (typeof api.getItem === 'function') {
        let userId = null;
        try {
          userId = typeof api.getCurrentUserId === 'function' ? api.getCurrentUserId() : null;
        } catch (_) {
          userId = null;
        }
        if (userId) {
          return await api.getItem(userId, detailsId);
        }
      }
    } catch (_) {
      /* fall through */
    }
    return api.ajax({
      url: api.getUrl('Items/' + detailsId),
      type: 'GET',
      dataType: 'json'
    });
  }

  async function isCurrentUserAdmin(api) {
    if (!api) {
      return false;
    }
    if (adminUserCached != null && Date.now() - adminUserCheckedAt < 60000) {
      return adminUserCached;
    }
    try {
      let user = null;
      if (typeof api.getCurrentUser === 'function') {
        user = await api.getCurrentUser();
      } else if (typeof api.getCurrentUserId === 'function') {
        const userId = api.getCurrentUserId();
        if (userId) {
          user = await api.ajax({
            url: api.getUrl('Users/' + userId),
            type: 'GET',
            dataType: 'json'
          });
        }
      }
      const policy = user && (user.Policy || user.policy);
      adminUserCached = !!(policy && (policy.IsAdministrator || policy.isAdministrator));
      adminUserCheckedAt = Date.now();
      return adminUserCached;
    } catch (err) {
      console.debug('[Look it up] admin check failed', err);
      adminUserCached = false;
      adminUserCheckedAt = Date.now();
      return false;
    }
  }

  async function syncSeriesPrepareUi() {
    if (seriesUiInFlight) {
      return;
    }
    const video = getVideoElement();
    if (video && !video.paused && video.readyState >= 2 && getCurrentItemId()) {
      hideSeriesUi();
      return;
    }

    const detailsId = getDetailsItemIdFromLocation();
    if (!detailsId) {
      hideSeriesUi();
      return;
    }

    const api = getApiClient();
    if (!api) {
      return;
    }

    seriesUiInFlight = true;
    try {
      if (!(await isCurrentUserAdmin(api))) {
        hideSeriesUi();
        return;
      }

      const item = await fetchDetailsItem(api, detailsId);
      const type = String((item && (item.Type || item.type)) || '');
      if (type !== 'Series' && type !== 'Season' && type !== 'Episode' && type !== 'Movie') {
        hideSeriesUi();
        return;
      }

      seriesUiItemId = detailsId;
      const name = (item && (item.Name || item.name)) || 'this title';

      if (type === 'Episode' || type === 'Movie') {
        await renderDetailControls(api, detailsId, name);
        return;
      }

      const panel = document.getElementById(DETAIL_PANEL_ID);
      if (panel) {
        panel.classList.remove('visible');
      }

      const { btn } = ensureSeriesUi();
      btn.style.display = 'block';
      btn.title = 'Prepare all episodes for ' + name;
      btn.disabled = false;
      btn.textContent = type === 'Season'
        ? 'Look it up — prepare this season'
        : 'Look it up — prepare this show';
      btn.onclick = async function () {
        if (!window.confirm('Prepare all episodes with AI? Every local name candidate will be verified in batches.')) {
          return;
        }
        btn.disabled = true;
        btn.textContent = 'Starting…';
        try {
          const result = await api.ajax({
            url: api.getUrl('LookItUp/' + detailsId + '/prepare-series') + '?force=false',
            type: 'POST',
            dataType: 'json',
            contentType: 'application/json',
            data: '{}'
          });
          if (!(result.started || result.Started)) {
            window.Dashboard?.alert?.('Could not start: ' + (result.error || result.Error || 'unknown'));
            btn.disabled = false;
            btn.textContent = type === 'Season'
              ? 'Look it up — prepare this season'
              : 'Look it up — prepare this show';
            return;
          }
          if (!seriesStatusTimer) {
            seriesStatusTimer = setInterval(refreshSeriesPrepareStatus, 2000);
          }
          refreshSeriesPrepareStatus();
        } catch (err) {
          window.Dashboard?.alert?.('Prepare failed: ' + err);
          btn.disabled = false;
        }
      };
    } catch (err) {
      console.debug('[Look it up] series UI item lookup failed', err);
      hideSeriesUi();
    } finally {
      seriesUiInFlight = false;
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
      start();
      startPreparePageWatcher();
    });
  } else {
    start();
    startPreparePageWatcher();
  }
})();