/*
 * JellyDVR Schedule — "all tuners are busy" in jellyfin-web.
 *
 * When every tuner input is recording (or watched), a live channel can't start and jellyfin-web
 * only shows a generic "Playback Error". This script notices the failed start (a non-OK
 * POST /Items/{channel}/PlaybackInfo), asks the plugin what the inputs are busy with
 * (GET /JellyDvrSchedule/Tuners), and if they're all taken replaces that error with a list of
 * the busy channels and when they free up. Each has a Watch button: a channel already open
 * costs no extra input, so those always play.
 *
 * Loaded by Episode History's guidemenu.js (already in index.html), so no extra index.html patch.
 * VERSION-SENSITIVE: written against jellyfin-web 12.0.0 (alert dialog title "Playback Error",
 * fetch-based ApiClient).
 */
(function () {
  'use strict';

  if (window.__jdvrTuners) {
    return;
  }
  window.__jdvrTuners = true;

  var OVERLAY_ID = 'jdvrTunersOverlay';
  var PLAYBACK_INFO = /\/Items\/([0-9a-f-]{32,36})\/PlaybackInfo/i;

  function idFrom(url) {
    var m = PLAYBACK_INFO.exec(url || '');
    return m ? m[1].replace(/-/g, '').toLowerCase() : null;
  }

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[JellyDvrSchedule] ' + msg);
    }
  }

  function api() {
    return window.ApiClient;
  }

  var originalFetch = window.fetch;
  window.fetch = function (input, init) {
    var url = typeof input === 'string' ? input : (input && input.url) || '';
    var method = (init && init.method) || (input && input.method) || 'GET';
    var promise = originalFetch.apply(this, arguments);
    var itemId = method.toUpperCase() === 'POST' ? idFrom(url) : null;
    if (itemId) {
      promise.then(function (response) {
        if (!response.ok) {
          check(itemId);
        } else {
          // Jellyfin reports some failures inside a 200 reply.
          response.clone().json().then(function (body) {
            if (body && body.ErrorCode) {
              check(itemId);
            }
          }, function () {});
        }
      }, function () {
        check(itemId);
      });
    }
    return promise;
  };

  // jellyfin-web 12 also requests through the Jellyfin SDK (axios, i.e. XMLHttpRequest).
  var xhrOpen = XMLHttpRequest.prototype.open;
  var xhrSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url) {
    this.__jdvrItem = String(method).toUpperCase() === 'POST' ? idFrom(String(url)) : null;
    return xhrOpen.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function () {
    var xhr = this;
    if (xhr.__jdvrItem) {
      checkBeforeStart(xhr.__jdvrItem, xhr);
      xhr.addEventListener('loadend', function () {
        var failed = xhr.status === 0 || xhr.status >= 400;
        if (!failed) {
          try {
            var body = typeof xhr.response === 'string' ? JSON.parse(xhr.response) : xhr.response;
            failed = !!(body && body.ErrorCode);
          } catch (e) { /* not JSON */ }
        }
        if (failed && !xhr.__jdvrHandled) {
          check(xhr.__jdvrItem);
        }
      });
    }
    return xhrSend.apply(this, arguments);
  };

  /*
   * Right as the channel start is requested: if every input is already busy (and nothing has this
   * channel open), say so at once instead of after BroadcastHub's ~30 s of nothing and Jellyfin's
   * "Playback Error". The doomed request is cancelled; the error Jellyfin shows for that is closed.
   */
  function checkBeforeStart(itemId, xhr) {
    var client = api();
    if (!client) {
      return;
    }
    client.ajax({ type: 'GET', url: client.getUrl('JellyDvrSchedule/Tuners', { channelId: itemId }), dataType: 'json' })
      .then(function (status) {
        if (!status || !status.IsChannel || !status.Full || xhr.readyState === 4) {
          return; // tuners free, not a channel, or the start already finished
        }
        log('all ' + status.Capacity + ' inputs busy: not starting ' + (status.RequestedChannel || itemId));
        xhr.__jdvrHandled = true;
        lastCheck = { id: itemId, at: Date.now() };
        suppressErrors(15000);
        show(status);
        try {
          xhr.abort();
        } catch (e) { /* already done */ }
      })
      .catch(function () { /* plugin missing or no Live TV access */ });
  }

  var lastCheck = { id: null, at: 0 };
  function check(itemId) {
    // Called on a failed start: keep Jellyfin's error hidden until we know why.
    holdErrors();
    // One failed start can surface through both hooks.
    if (lastCheck.id === itemId && Date.now() - lastCheck.at < 5000) {
      return;
    }
    lastCheck = { id: itemId, at: Date.now() };
    var client = api();
    if (!client) {
      return;
    }
    client.ajax({ type: 'GET', url: client.getUrl('JellyDvrSchedule/Tuners', { channelId: itemId }), dataType: 'json' })
      .then(function (status) {
        if (status && status.IsChannel && status.Full) {
          log('live channel failed with all ' + status.Capacity + ' inputs busy');
          suppressErrors(8000);
          show(status);
        } else {
          releaseHeld(false);
        }
      })
      .catch(function () { releaseHeld(false); });
  }

  /*
   * jellyfin-web's own "Playback Error" box never shows for a start we handle. A MutationObserver
   * callback runs before the browser paints, so the box (and its dark backdrop) is hidden the
   * moment it's inserted:
   *  - suppress: our box is already up (or about to be) — hide Jellyfin's and close it properly;
   *  - hold: a channel start just failed and we're asking the server why — keep it hidden until
   *    the answer: tuners all busy → close it (ours shows instead); anything else → reveal it,
   *    since then it's the real message. A hold always ends within 3 s.
   */
  var suppressUntil = 0;
  var holdUntil = 0;
  var held = [];

  function errorDialogs() {
    return [].slice.call(document.querySelectorAll('.dialogContainer')).filter(function (c) {
      return !c.closest('#' + OVERLAY_ID) && /Playback Error/i.test(c.textContent || '');
    });
  }

  function hide(el) {
    if (el && !el.hasAttribute('data-jdvr-hidden')) {
      el.setAttribute('data-jdvr-hidden', el.style.visibility || '');
      el.style.visibility = 'hidden';
    }
  }

  function reveal(el) {
    if (el && el.hasAttribute('data-jdvr-hidden')) {
      el.style.visibility = el.getAttribute('data-jdvr-hidden');
      el.removeAttribute('data-jdvr-hidden');
    }
  }

  // The backdrop jellyfin-web's dialog helper adds for the box (the most recent one).
  function backdropOf() {
    var all = document.querySelectorAll('.dialogBackdrop');
    return all.length ? all[all.length - 1] : null;
  }

  function closeDialog(container) {
    var button = container.querySelector('button');
    if (button) {
      button.click();
    }
  }

  new MutationObserver(function () {
    var now = Date.now();
    if (now >= suppressUntil && now >= holdUntil) {
      return;
    }
    errorDialogs().forEach(function (c) {
      if (c.hasAttribute('data-jdvr-seen')) {
        return;
      }
      c.setAttribute('data-jdvr-seen', '');
      var backdrop = backdropOf();
      hide(c);
      hide(backdrop);
      if (now < suppressUntil) {
        closeDialog(c);
      } else {
        held.push({ dialog: c, backdrop: backdrop });
      }
    });
  }).observe(document.body, { childList: true, subtree: true, characterData: true });

  function holdErrors() {
    holdUntil = Date.now() + 3000;
    setTimeout(function () {
      if (Date.now() >= holdUntil) {
        releaseHeld(false);
      }
    }, 3100);
  }

  function releaseHeld(close) {
    holdUntil = 0;
    held.forEach(function (h) {
      if (close) {
        closeDialog(h.dialog);
      } else {
        reveal(h.dialog);
        reveal(h.backdrop);
      }
    });
    held = [];
  }

  function suppressErrors(ms) {
    suppressUntil = Date.now() + ms;
    // One already on screen (inserted before we knew) goes too.
    errorDialogs().forEach(function (c) {
      hide(c);
      closeDialog(c);
    });
    releaseHeld(true);
  }

  function time(iso) {
    return new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  }

  function ownSession() {
    var client = api();
    return client.getSessions({ deviceId: client.deviceId() }).then(function (sessions) {
      return (sessions || []).filter(function (s) { return s.DeviceId === client.deviceId(); })[0] || null;
    });
  }

  function watch(channelId) {
    close();
    var client = api();
    ownSession().then(function (session) {
      if (!session) {
        return;
      }
      return client.ajax({
        type: 'POST',
        url: client.getUrl('Sessions/' + session.Id + '/Playing', { playCommand: 'PlayNow', itemIds: channelId })
      });
    }).catch(function () { log('could not start channel ' + channelId); });
  }

  function close() {
    var el = document.getElementById(OVERLAY_ID);
    if (el) {
      el.parentNode.removeChild(el);
    }
    document.removeEventListener('keydown', onKey, true);
  }

  function onKey(e) {
    if (e.key === 'Escape' || e.key === 'Backspace' || e.keyCode === 10009 || e.keyCode === 461) {
      e.preventDefault();
      e.stopPropagation();
      close();
    }
  }

  function el(tag, css, text) {
    var e = document.createElement(tag);
    if (css) {
      e.style.cssText = css;
    }
    if (text != null) {
      e.textContent = text;
    }
    return e;
  }

  function button(label, onClick, primary) {
    var b = el('button', 'margin:.25em .5em .25em 0;padding:.5em 1em;border:0;border-radius:.3em;cursor:pointer;font-size:1em;'
      + (primary ? 'background:#00a4dc;color:#fff;' : 'background:rgba(255,255,255,.15);color:#fff;'), label);
    b.type = 'button';
    b.addEventListener('click', onClick);
    return b;
  }

  function show(status) {
    close();
    var overlay = el('div', 'position:fixed;inset:0;z-index:100000;background:rgba(0,0,0,.7);display:flex;align-items:center;justify-content:center;');
    overlay.id = OVERLAY_ID;
    var box = el('div', 'background:#202020;color:#fff;border-radius:.5em;padding:1.5em;max-width:34em;width:90%;max-height:85vh;overflow:auto;box-shadow:0 0 2em rgba(0,0,0,.6);');
    box.appendChild(el('h2', 'margin:0 0 .4em;font-size:1.4em;', 'All tuners are busy'));
    box.appendChild(el('div', 'opacity:.85;margin-bottom:1em;',
      (status.RequestedChannel ? status.RequestedChannel + " can't start: " : '') + 'all ' + status.Capacity
      + ' tuner inputs are in use. You can watch one of these channels (it costs no extra tuner), or try again when one frees up.'));

    var first = null;
    (status.Busy || []).forEach(function (b) {
      var row = el('div', 'display:flex;align-items:center;gap:.8em;padding:.5em 0;border-top:1px solid rgba(255,255,255,.12);');
      var what = [];
      if (b.Recording.length) {
        what.push('Recording ' + b.Recording.join(', ') + (b.EndsUtc ? ' (until ' + time(b.EndsUtc) + ')' : ''));
      }
      if (b.Watching.length) {
        what.push('Watched on ' + b.Watching.join(', '));
      }
      var text = el('div', 'flex:1;');
      text.appendChild(el('div', 'font-weight:bold;', (b.Number ? b.Number + ' ' : '') + b.ChannelName));
      text.appendChild(el('div', 'opacity:.75;font-size:.9em;', what.join(' · ') || 'In use'));
      row.appendChild(text);
      var w = button('Watch', function () { watch(b.ChannelId); }, true);
      first = first || w;
      row.appendChild(w);
      box.appendChild(row);
    });

    var footer = el('div', 'margin-top:1em;text-align:right;');
    var ok = button('Close', close, false);
    footer.appendChild(ok);
    box.appendChild(footer);
    overlay.appendChild(box);
    overlay.addEventListener('click', function (e) {
      if (e.target === overlay) {
        close();
      }
    });
    document.body.appendChild(overlay);
    document.addEventListener('keydown', onKey, true);
    (first || ok).focus();
  }

  // The Favourites list under Live TV > Series lives in its own file.
  (function () {
    var s = document.createElement('script');
    s.defer = true;
    s.src = '/JellyDvrSchedule/web/favourites.js';
    document.head.appendChild(s);
  })();

  log('tuners script loaded');
})();
