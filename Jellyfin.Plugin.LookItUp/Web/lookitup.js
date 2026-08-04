(function () {
  'use strict';

  const STYLE_ID = 'lookitup-styles';
  const POPUP_ID = 'lookitup-popup';
  const POLL_MS = 400;

  let annotations = [];
  let currentItemId = null;
  let lastShownTerm = null;
  let hideTimer = null;
  let popupDurationMs = 5000;

  function ensureStyles() {
    if (document.getElementById(STYLE_ID)) {
      return;
    }

    const style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = `
      #${POPUP_ID} {
        position: fixed;
        left: 50%;
        bottom: 12%;
        transform: translateX(-50%);
        z-index: 99999;
        max-width: min(520px, 86vw);
        padding: 14px 18px;
        border-radius: 12px;
        background: rgba(12, 16, 24, 0.88);
        color: #f4f7fb;
        font: 500 15px/1.45 system-ui, -apple-system, Segoe UI, sans-serif;
        box-shadow: 0 10px 40px rgba(0, 0, 0, 0.35);
        backdrop-filter: blur(8px);
        opacity: 0;
        pointer-events: none;
        transition: opacity 180ms ease;
      }
      #${POPUP_ID}.visible {
        opacity: 1;
        pointer-events: auto;
      }
      #${POPUP_ID} .lookitup-term {
        display: block;
        font-weight: 700;
        margin-bottom: 4px;
        letter-spacing: 0.01em;
      }
      #${POPUP_ID} a {
        color: #9ecbff;
        text-decoration: none;
      }
      #${POPUP_ID} a:hover {
        text-decoration: underline;
      }
    `;
    document.head.appendChild(style);
  }

  function ensurePopup() {
    ensureStyles();
    let popup = document.getElementById(POPUP_ID);
    if (!popup) {
      popup = document.createElement('div');
      popup.id = POPUP_ID;
      popup.setAttribute('role', 'status');
      popup.setAttribute('aria-live', 'polite');
      document.body.appendChild(popup);
    }
    return popup;
  }

  function hidePopup() {
    const popup = document.getElementById(POPUP_ID);
    if (popup) {
      popup.classList.remove('visible');
    }
    lastShownTerm = null;
  }

  function showPopup(annotation) {
    const popup = ensurePopup();
    const term = annotation.term || '';
    const summary = annotation.summary || '';
    const url = annotation.url;

    if (lastShownTerm === term) {
      return;
    }

    lastShownTerm = term;
    popup.innerHTML = '';

    const termEl = document.createElement('span');
    termEl.className = 'lookitup-term';
    termEl.textContent = term;
    popup.appendChild(termEl);

    const body = document.createElement('div');
    // summary already includes "Term: explanation" from the server; avoid duplicating the term line.
    const text = summary.startsWith(term + ':')
      ? summary.slice(term.length + 1).trim()
      : summary;
    body.textContent = text;
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

    popup.classList.add('visible');
    if (hideTimer) {
      clearTimeout(hideTimer);
    }
    hideTimer = setTimeout(hidePopup, popupDurationMs);
  }

  function getApiClient() {
    return window.ApiClient || window.ApiClientClass || null;
  }

  function getCurrentItemId() {
    try {
      if (window.playbackManager && typeof window.playbackManager.getCurrentItem === 'function') {
        const item = window.playbackManager.getCurrentItem();
        if (item && item.Id) {
          return item.Id;
        }
      }
    } catch (_) {
      /* ignore */
    }

    const video = document.querySelector('video');
    if (video && video.src) {
      const match = video.src.match(/\/Items\/([0-9a-fA-F-]{36})\//);
      if (match) {
        return match[1];
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
        return Math.floor(window.playbackManager.currentTime() * 1000);
      }
    } catch (_) {
      /* ignore */
    }

    return null;
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

      if (!data || data.enabled === false) {
        annotations = [];
        return;
      }

      popupDurationMs = data.popupDurationMs || 5000;
      annotations = Array.isArray(data.annotations) ? data.annotations : [];
    } catch (err) {
      console.warn('[Look it up] failed to load annotations', err);
      annotations = [];
    }
  }

  function tick() {
    const itemId = getCurrentItemId();
    if (!itemId) {
      if (currentItemId) {
        currentItemId = null;
        annotations = [];
        hidePopup();
      }
      return;
    }

    if (itemId !== currentItemId) {
      currentItemId = itemId;
      annotations = [];
      hidePopup();
      loadAnnotations(itemId);
      return;
    }

    const now = getCurrentTimeMs();
    if (now == null || !annotations.length) {
      return;
    }

    const active = annotations.find((a) => now >= a.startMs && now <= a.endMs);
    if (active) {
      showPopup(active);
    } else if (lastShownTerm) {
      // Keep visible until popupDuration timeout; do not force-hide mid-fade.
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
