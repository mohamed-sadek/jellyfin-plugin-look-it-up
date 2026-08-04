(function () {
  'use strict';

  const STYLE_ID = 'lookitup-styles';
  const POPUP_ID = 'lookitup-popup';
  const POLL_MS = 250;
  const MIN_VISIBLE_MS = 4500;

  let annotations = [];
  let currentItemId = null;
  let lastShownTerm = null;
  let shownAtMs = 0;
  let hideTimer = null;
  let popupDurationMs = 5000;
  let missingItemTicks = 0;

  function ensureStyles() {
    let style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement('style');
      style.id = STYLE_ID;
      document.head.appendChild(style);
    }

    // Re-apply so updates win over older cached CSS.
    style.textContent = `
      #${POPUP_ID} {
        position: absolute !important;
        left: 50% !important;
        bottom: max(8%, 64px) !important;
        transform: translateX(-50%) !important;
        z-index: 2147483647 !important;
        max-width: min(560px, 90%) !important;
        width: max-content !important;
        padding: 16px 20px !important;
        border-radius: 14px !important;
        border: 1px solid rgba(255, 255, 255, 0.18) !important;
        background: rgba(8, 12, 20, 0.94) !important;
        color: #f7fafc !important;
        font: 600 16px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif !important;
        box-shadow: 0 16px 48px rgba(0, 0, 0, 0.55) !important;
        opacity: 0;
        visibility: hidden;
        pointer-events: none !important;
        transition: opacity 160ms ease, visibility 160ms ease !important;
        text-align: left !important;
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
      .videoPlayerContainer, .videoPlayerContainer-withCredits, .htmlvideoplayer, .videoosdhide {
        /* ensure positioned ancestor for absolute popup */
      }
      .videoPlayerContainer {
        position: relative !important;
      }
    `;
  }

  function getPlayerRoot() {
    return (
      document.querySelector('.videoPlayerContainer') ||
      document.querySelector('.htmlvideoplayer') ||
      document.querySelector('#videoOsdPage') ||
      document.body
    );
  }

  function ensurePopup() {
    ensureStyles();
    let popup = document.getElementById(POPUP_ID);
    const root = getPlayerRoot();

    if (!popup) {
      popup = document.createElement('div');
      popup.id = POPUP_ID;
      popup.setAttribute('role', 'status');
      popup.setAttribute('aria-live', 'polite');
      root.appendChild(popup);
    } else if (popup.parentElement !== root && root) {
      root.appendChild(popup);
    }

    return popup;
  }

  function hidePopup(force) {
    if (!force && lastShownTerm && Date.now() - shownAtMs < MIN_VISIBLE_MS) {
      return;
    }

    const popup = document.getElementById(POPUP_ID);
    if (popup) {
      popup.classList.remove('visible');
    }
    lastShownTerm = null;
    shownAtMs = 0;
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

    if (lastShownTerm === term && popup.classList.contains('visible')) {
      // Refresh hide timer while still in the cue window.
      if (hideTimer) {
        clearTimeout(hideTimer);
      }
      hideTimer = setTimeout(() => hidePopup(true), Math.max(popupDurationMs, MIN_VISIBLE_MS));
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
    body.textContent = text || summary;
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

    // Force a reflow so the opacity transition always runs.
    popup.classList.remove('visible');
    void popup.offsetWidth;
    popup.classList.add('visible');

    if (hideTimer) {
      clearTimeout(hideTimer);
    }
    hideTimer = setTimeout(() => hidePopup(true), Math.max(popupDurationMs, MIN_VISIBLE_MS));
    console.info('[Look it up] name triggered', {
      term: term,
      summary: summary,
      url: url || null,
      startMs: readProp(annotation, 'startMs', 'StartMs'),
      endMs: readProp(annotation, 'endMs', 'EndMs'),
      playbackMs: getCurrentTimeMs()
    });
  }

  function getApiClient() {
    return window.ApiClient || null;
  }

  function getCurrentItemId() {
    try {
      if (window.playbackManager && typeof window.playbackManager.getCurrentItem === 'function') {
        const item = window.playbackManager.getCurrentItem();
        if (item && (item.Id || item.id)) {
          return item.Id || item.id;
        }
      }
    } catch (_) {
      /* ignore */
    }

    try {
      if (window.playbackManager && typeof window.playbackManager.currentItem === 'function') {
        const item = window.playbackManager.currentItem();
        if (item && (item.Id || item.id)) {
          return item.Id || item.id;
        }
      }
    } catch (_) {
      /* ignore */
    }

    const video = document.querySelector('.videoPlayerContainer video, video');
    if (video) {
      const haystack = [video.src, video.currentSrc, location.href].join(' ');
      const match = haystack.match(/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/);
      if (match) {
        return match[0];
      }
    }

    return null;
  }

  function getCurrentTimeMs() {
    const video = document.querySelector('.videoPlayerContainer video, video');
    if (video && !Number.isNaN(video.currentTime)) {
      return Math.floor(video.currentTime * 1000);
    }

    try {
      if (window.playbackManager && typeof window.playbackManager.currentTime === 'function') {
        const value = window.playbackManager.currentTime();
        // Jellyfin may return seconds or ticks depending on version/path.
        if (typeof value === 'number' && !Number.isNaN(value)) {
          return value > 1000000 ? Math.floor(value / 10000) : Math.floor(value * 1000);
        }
      }
    } catch (_) {
      /* ignore */
    }

    return null;
  }

  function normalizeAnnotations(list) {
    return (list || []).map((a) => ({
      term: readProp(a, 'term', 'Term') || '',
      summary: readProp(a, 'summary', 'Summary') || '',
      url: readProp(a, 'url', 'Url') || null,
      startMs: Number(readProp(a, 'startMs', 'StartMs') || 0),
      endMs: Number(readProp(a, 'endMs', 'EndMs') || 0)
    })).filter((a) => a.term && a.endMs >= a.startMs);
  }

  async function loadAnnotations(itemId) {
    const api = getApiClient();
    if (!api || !itemId) {
      return;
    }

    try {
      const url = api.getUrl(`LookItUp/${itemId}`);
      const data = await api.ajax({
        url,
        type: 'GET',
        dataType: 'json'
      });

      if (!data || data.enabled === false || data.Enabled === false) {
        annotations = [];
        console.info('[Look it up] disabled or empty for', itemId);
        return;
      }

      popupDurationMs = Number(data.popupDurationMs || data.PopupDurationMs || 5000);
      annotations = normalizeAnnotations(data.annotations || data.Annotations || []);
      console.info('[Look it up] loaded', annotations.length, 'annotations for', itemId);
      console.info(
        '[Look it up] names ready',
        annotations.map((a) => ({
          term: a.term,
          startMs: a.startMs,
          endMs: a.endMs
        }))
      );
    } catch (err) {
      console.warn('[Look it up] failed to load annotations', err);
      annotations = [];
    }
  }

  function tick() {
    const itemId = getCurrentItemId();
    const video = document.querySelector('.videoPlayerContainer video, video');
    const videoPlaying = !!(video && !video.paused && video.readyState >= 2);

    if (!itemId) {
      // Don't wipe state on brief detection gaps during OSD transitions.
      if (currentItemId && !videoPlaying) {
        missingItemTicks += 1;
        if (missingItemTicks > 8) {
          currentItemId = null;
          annotations = [];
          hidePopup(true);
          missingItemTicks = 0;
        }
      }
      return;
    }

    missingItemTicks = 0;

    if (itemId !== currentItemId) {
      currentItemId = itemId;
      annotations = [];
      hidePopup(true);
      loadAnnotations(itemId);
      return;
    }

    // Keep popup mounted on the active player root (fullscreen-safe).
    ensurePopup();

    const now = getCurrentTimeMs();
    if (now == null || !annotations.length) {
      return;
    }

    const active = annotations.find((a) => now >= a.startMs && now <= a.endMs);
    if (active) {
      if (lastShownTerm !== active.term) {
        console.info('[Look it up] cue match → search/popup', {
          term: active.term,
          playbackMs: now,
          window: [active.startMs, active.endMs]
        });
      }
      showPopup(active);
    }
  }

  function start() {
    ensurePopup();
    setInterval(tick, POLL_MS);
    console.info('[Look it up] overlay ready');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
