/*
 * JellyDVR Schedule — Favourites list in jellyfin-web, at the top of Live TV > Series (next to the
 * series recordings), for everyone allowed to manage recordings: each favourite with its next
 * recordings, and add / edit / switch off / remove. Admins also have the plugin's settings page
 * (priority order, preview).
 *
 * Loaded by tuners.js (itself loaded by Episode History's guidemenu.js, already in index.html).
 * VERSION-SENSITIVE: jellyfin-web 12.0.0 draws Live TV with React: `#/livetv?tab=5` is Series,
 * inside `div#liveTvPage`. React may redraw the page, so the section is re-added when missing.
 */
(function () {
  'use strict';

  if (window.__jdvrFavourites) {
    return;
  }
  window.__jdvrFavourites = true;

  var SECTION_ID = 'jdvrFavouritesSection';
  var SERIES_TAB = '5';
  var data = null;
  var loadedAt = 0;
  var editing = null; // rule id being edited, 'new' for the add form, or null

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[JellyDvrSchedule] ' + msg);
    }
  }

  function api(method, path, body) {
    var client = window.ApiClient;
    var opts = { type: method, url: client.getUrl(path), dataType: 'json' };
    if (body) {
      opts.data = JSON.stringify(body);
      opts.contentType = 'application/json';
    }
    return client.ajax(opts);
  }

  function esc(s) {
    var d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  }

  function when(iso) {
    return new Date(iso).toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  }

  function onSeriesTab() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/livetv') !== 0) {
      return false;
    }
    var q = hash.indexOf('?') >= 0 ? new URLSearchParams(hash.slice(hash.indexOf('?') + 1)) : null;
    return !!q && q.get('tab') === SERIES_TAB;
  }

  function load(force) {
    if (!force && data && Date.now() - loadedAt < 30000) {
      return Promise.resolve(data);
    }
    return api('GET', 'JellyDvrSchedule/Favourites').then(function (r) {
      data = r;
      loadedAt = Date.now();
      return r;
    });
  }

  // Scheduling runs in the background on the server: refresh the list a little later too.
  function reloadSoon() {
    load(true).then(render, function () {});
    setTimeout(function () { load(true).then(render, function () {}); }, 8000);
  }

  var FIELD = 'background:rgba(255,255,255,.08);color:inherit;border:1px solid rgba(255,255,255,.25);border-radius:.3em;padding:.45em .55em;font-size:1em;';
  var BUTTON = 'border:0;border-radius:.3em;padding:.4em .9em;font-size:.95em;cursor:pointer;margin-right:.4em;';

  function button(label, primary, onClick) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'emby-button';
    b.style.cssText = BUTTON + (primary ? 'background:#00a4dc;color:#fff;' : 'background:rgba(255,255,255,.12);color:inherit;');
    b.textContent = label;
    b.addEventListener('click', onClick);
    return b;
  }

  function describe(f) {
    var parts = [];
    parts.push(f.Match === 'keyword' ? 'keyword' : 'exact title');
    parts.push(f.Type === 'movies' ? 'movies' : f.Type === 'series' ? 'shows' : 'shows and movies');
    parts.push(f.Channels ? 'channels ' + f.Channels : 'any channel');
    if (f.NewOnly) {
      parts.push('new episodes only');
    }
    if (f.SkipInLibrary) {
      parts.push('skips ones in the library');
    }
    return parts.join(' · ');
  }

  function form(f, onDone) {
    var box = document.createElement('div');
    box.style.cssText = 'display:flex;flex-wrap:wrap;gap:.5em;align-items:center;padding:.6em 0;';
    box.innerHTML =
      '<input data-f="Title" placeholder="Title or keyword" style="' + FIELD + 'flex:2;min-width:14em;" />' +
      '<select data-f="Match" style="' + FIELD + '"><option value="title">Exact title</option><option value="keyword">Keyword</option></select>' +
      '<select data-f="Type" style="' + FIELD + '"><option value="series">Shows</option><option value="movies">Movies</option><option value="any">Shows and movies</option></select>' +
      '<input data-f="Channels" placeholder="Channels (any)" title="Channel numbers or names, comma separated; empty = any channel" style="' + FIELD + 'flex:1;min-width:9em;" />' +
      '<label style="white-space:nowrap;"><input type="checkbox" data-f="NewOnly" /> New episodes only</label>' +
      '<label style="white-space:nowrap;"><input type="checkbox" data-f="SkipInLibrary" /> Skip ones in the library</label>';
    var values = f || { Title: '', Match: 'title', Type: 'series', Channels: '', NewOnly: false, SkipInLibrary: true };
    box.querySelectorAll('[data-f]').forEach(function (el) {
      var k = el.getAttribute('data-f');
      if (el.type === 'checkbox') {
        el.checked = !!values[k];
      } else {
        el.value = values[k] || el.value;
      }
    });
    var note = document.createElement('div');
    note.style.cssText = 'width:100%;opacity:.8;font-size:.9em;';
    var actions = document.createElement('div');
    actions.style.cssText = 'width:100%;';
    actions.appendChild(button(f ? 'Save' : 'Add favourite', true, function () {
      var body = {};
      box.querySelectorAll('[data-f]').forEach(function (el) {
        body[el.getAttribute('data-f')] = el.type === 'checkbox' ? el.checked : el.value;
      });
      if (!body.Title.trim()) {
        note.textContent = 'Enter a title or keyword.';
        return;
      }
      api('POST', f ? 'JellyDvrSchedule/Favourites/' + f.Id : 'JellyDvrSchedule/Favourites', body)
        .then(function (r) { onDone(r && r.Message); }, function () { note.textContent = 'Could not save it.'; });
    }));
    actions.appendChild(button('Cancel', false, function () { onDone(null); }));
    box.appendChild(actions);
    box.appendChild(note);
    setTimeout(function () { var t = box.querySelector('[data-f="Title"]'); if (t) { t.focus(); } }, 0);
    return box;
  }

  function row(f, canManage) {
    var el = document.createElement('div');
    el.style.cssText = 'padding:.7em 0;border-top:1px solid rgba(128,128,128,.25);' + (f.Enabled ? '' : 'opacity:.55;');
    if (editing === f.Id) {
      el.appendChild(form(f, function (msg) { editing = null; status(msg); reloadSoon(); }));
      return el;
    }
    var top = document.createElement('div');
    top.style.cssText = 'display:flex;flex-wrap:wrap;align-items:center;gap:.6em;';
    top.innerHTML = '<span style="color:#f5c518;font-size:1.2em;">★</span>' +
      '<span style="font-weight:600;font-size:1.1em;">' + esc(f.Title) + '</span>' +
      (f.Enabled ? '' : '<span style="font-size:.85em;border:1px solid currentColor;border-radius:.3em;padding:0 .35em;">Off</span>') +
      '<span style="flex:1;"></span>';
    if (canManage) {
      top.appendChild(button(f.Enabled ? 'Switch off' : 'Switch on', false, function () {
        api('POST', 'JellyDvrSchedule/Favourites/' + f.Id, { Enabled: !f.Enabled }).then(function (r) { status(r && r.Message); reloadSoon(); });
      }));
      top.appendChild(button('Edit', false, function () { editing = f.Id; render(); }));
      var armed = false;
      var remove = button('Remove', false, function () {
        if (!armed) {
          armed = true;
          remove.textContent = 'Press again to remove';
          remove.style.color = '#ff6b6b';
          setTimeout(function () { armed = false; remove.textContent = 'Remove'; remove.style.color = ''; }, 4000);
          return;
        }
        api('DELETE', 'JellyDvrSchedule/Favourites/' + f.Id).then(function (r) { status(r && r.Message); reloadSoon(); }, function () { status('Could not remove it.'); });
      });
      top.appendChild(remove);
    }
    el.appendChild(top);
    var sub = document.createElement('div');
    sub.style.cssText = 'opacity:.7;font-size:.9em;margin:.15em 0 .3em 1.8em;';
    sub.textContent = describe(f);
    el.appendChild(sub);
    var next = document.createElement('div');
    next.style.cssText = 'font-size:.92em;margin-left:1.8em;';
    if (!f.Enabled) {
      next.textContent = 'Switched off: records nothing new (already scheduled recordings stay).';
    } else if (!f.Next.length) {
      next.textContent = 'Nothing scheduled: not in the guide right now (it will be recorded when it appears).';
    } else {
      next.innerHTML = '<span style="opacity:.75;">Next:</span> ' + f.Next.map(function (n) {
        return esc(when(n.Start)) + ' <span style="opacity:.7;">' + esc(n.Channel) + '</span>';
      }).join(' · ') + (f.Upcoming > f.Next.length ? ' <span style="opacity:.7;">(+' + (f.Upcoming - f.Next.length) + ' more)</span>' : '');
    }
    el.appendChild(next);
    return el;
  }

  var statusText = '';
  function status(msg) {
    statusText = msg || '';
    var s = document.getElementById(SECTION_ID + 'Status');
    if (s) {
      s.textContent = statusText;
    }
  }

  function render() {
    var section = document.getElementById(SECTION_ID);
    if (!section || !data) {
      return;
    }
    var list = section.querySelector('.jdvrFavList');
    var head = section.querySelector('.jdvrFavHead');
    head.querySelectorAll('button').forEach(function (b) { b.parentNode.removeChild(b); });
    if (data.CanManage && editing !== 'new') {
      head.appendChild(button('+ Add', true, function () { editing = 'new'; render(); }));
    }
    list.innerHTML = '';
    if (editing === 'new') {
      list.appendChild(form(null, function (msg) { editing = null; status(msg); reloadSoon(); }));
    }
    if (!data.Favourites.length && editing !== 'new') {
      var empty = document.createElement('div');
      empty.style.cssText = 'opacity:.75;padding:.5em 0;';
      empty.textContent = 'No favourites yet. A favourite records a show by its title on any channel, whatever the guide calls it'
        + (data.CanManage ? '. Use + Add, or "Add to favourites" in the guide.' : '.');
      list.appendChild(empty);
    }
    data.Favourites.slice().sort(function (a, b) { return a.Title.localeCompare(b.Title); }).forEach(function (f) {
      list.appendChild(row(f, data.CanManage));
    });
  }

  function ensureSection() {
    var existing = document.getElementById(SECTION_ID);
    if (!onSeriesTab()) {
      if (existing) {
        existing.parentNode.removeChild(existing);
      }
      return;
    }
    var page = document.querySelector('#liveTvPage');
    var host = page && (page.querySelector('.padded-bottom-page') || page);
    if (!host || (existing && host.contains(existing))) {
      return;
    }
    if (existing) {
      existing.parentNode.removeChild(existing);
    }
    var section = document.createElement('div');
    section.id = SECTION_ID;
    section.className = 'verticalSection padded-left padded-right';
    section.style.cssText = 'margin-bottom:1.5em;';
    section.innerHTML = '<div class="jdvrFavHead sectionTitleContainer" style="display:flex;align-items:center;gap:1em;">' +
      '<h2 class="sectionTitle" style="margin:.4em 0;">Favourites</h2></div>' +
      '<div id="' + SECTION_ID + 'Status" style="opacity:.85;font-size:.9em;"></div>' +
      '<div class="jdvrFavList"></div>';
    host.insertBefore(section, host.firstChild);
    status(statusText);
    load(false).then(render, function () {
      section.parentNode && section.parentNode.removeChild(section); // plugin missing or no access
    });
  }

  var scheduled = false;
  function scheduleCheck() {
    if (scheduled) {
      return;
    }
    scheduled = true;
    setTimeout(function () {
      scheduled = false;
      if (window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()) {
        ensureSection();
      }
    }, 150);
  }

  new MutationObserver(scheduleCheck).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', scheduleCheck);
  scheduleCheck();
  log('favourites script loaded');
})();
