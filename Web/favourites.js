/*
 * JellyDVR Schedule — Favourites list in jellyfin-web, at the top of Live TV > Series (next to the
 * series recordings) and of Jellyfin's own Favorites page, for everyone allowed to manage recordings: each favourite with its next
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

  function tabOf(route) {
    var hash = window.location.hash || '';
    if (hash.indexOf(route) !== 0) {
      return null;
    }
    var q = hash.indexOf('?') >= 0 ? new URLSearchParams(hash.slice(hash.indexOf('?') + 1)) : null;
    return q ? q.get('tab') : null;
  }

  /*
   * Where the list goes: the top of Live TV > Series, and the top of Jellyfin's own Favorites page
   * (the header's "Favorites" link, the home page's tab 1: `#indexPage .tabContent[data-index="1"]`),
   * since that's where people look for their favourites. The hearted items stay below it there.
   */
  function hostElement() {
    if (tabOf('#/livetv') === SERIES_TAB) {
      var page = document.querySelector('#liveTvPage');
      return page && (page.querySelector('.padded-bottom-page') || page);
    }
    if (tabOf('#/home') === '1') {
      return document.querySelector('#indexPage:not(.hide) .tabContent[data-index="1"]');
    }
    return null;
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
      empty.textContent = 'No recording favourites yet. A recording favourite records a show by its title on any channel, whatever the guide calls it'
        + (data.CanManage ? '. Use + Add, or "Add to Recording Favourites" in the guide or a show\'s ⋮ menu.' : '.');
      list.appendChild(empty);
    }
    data.Favourites.slice().sort(function (a, b) { return a.Title.localeCompare(b.Title); }).forEach(function (f) {
      list.appendChild(row(f, data.CanManage));
    });
  }

  function ensureSection() {
    var existing = document.getElementById(SECTION_ID);
    var host = hostElement();
    if (!host) {
      if (existing) {
        existing.parentNode.removeChild(existing);
      }
      return;
    }
    if (existing && host.contains(existing)) {
      return;
    }
    if (existing) {
      existing.parentNode.removeChild(existing);
    }
    var section = document.createElement('div');
    section.id = SECTION_ID;
    section.className = 'verticalSection padded-left padded-right';
    section.style.cssText = 'margin-bottom:1.5em;';
    var onFavoritesPage = tabOf('#/home') === '1';
    if (onFavoritesPage) {
      section.style.cssText += 'padding-top:1em;';
    }
    section.innerHTML = '<div class="jdvrFavHead sectionTitleContainer" style="display:flex;align-items:center;gap:1em;">' +
      '<h2 class="sectionTitle" style="margin:.4em 0;">Recording Favourites</h2></div>' +
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
        checkMenus();
      }
    }, 100);
  }

  /*
   * "Add to Recording Favourites" wherever a show turns up: the ⋮ menu (or right-click) of
   * programme cards (Programs > On Now / Shows…), recordings, series, episodes and movies, the ⋮
   * button on their details pages, and below Record on a programme's details page. For a show a
   * favourite already covers it says "Remove from Recording Favourites" (second press confirms;
   * removing cancels the upcoming recordings it scheduled).
   */
  var MENU_ID = 'jdvr-favourite';
  var ROW_CLASS = 'jdvrFavouriteDetailsRow';
  var SHOW_TYPES = ['Program', 'Series', 'Season', 'Episode', 'Movie', 'Recording', 'Video'];
  var menuItemId = null;
  var menuClickedAt = 0;
  var forItem = {};

  function detailsId() {
    var hash = window.location.hash || '';
    return hash.indexOf('#/details') === 0 ? new URLSearchParams(hash.slice(hash.indexOf('?') + 1)).get('id') : null;
  }

  function rememberMenuItem(e, isContextMenu) {
    var target = e.target;
    if (!target || !target.closest) {
      return;
    }
    var card = target.closest('[data-id][data-type]');
    if (card && !card.classList.contains('actionSheetMenuItem') && SHOW_TYPES.indexOf(card.getAttribute('data-type')) !== -1
      && (isContextMenu || target.closest('[data-action="menu"]'))) {
      menuItemId = card.getAttribute('data-id');
      menuClickedAt = Date.now();
      return;
    }
    // The ⋮ button on a details page.
    if (target.closest('.btnMoreCommands') && detailsId()) {
      menuItemId = detailsId();
      menuClickedAt = Date.now();
    }
  }

  document.addEventListener('click', function (e) { rememberMenuItem(e, false); }, true);
  document.addEventListener('contextmenu', function (e) { rememberMenuItem(e, true); }, true);

  // What the plugin says about an item's show (cached a little; null = not a show / no plugin).
  function lookup(id) {
    var c = forItem[id];
    if (c && (c === 'pending' || Date.now() - c.at < 20000)) {
      return c === 'pending' ? null : c.info;
    }
    forItem[id] = 'pending';
    api('GET', 'JellyDvrSchedule/Favourites/ForItem/' + id)
      .then(function (info) { forItem[id] = { at: Date.now(), info: info }; scheduleCheck(); },
        function () { forItem[id] = { at: Date.now(), info: null }; });
    return null;
  }

  function toast(msg) {
    var t = document.createElement('div');
    t.textContent = msg;
    t.style.cssText = 'position:fixed;left:50%;bottom:8%;transform:translateX(-50%);z-index:100001;background:#303030;color:#fff;padding:.8em 1.2em;border-radius:.4em;box-shadow:0 0 1em rgba(0,0,0,.5);max-width:80%;';
    document.body.appendChild(t);
    setTimeout(function () { t.parentNode && t.parentNode.removeChild(t); }, 4000);
  }

  // Add or remove; calls back with the message once done.
  function toggle(id, info, done) {
    var call = info.Favourite
      ? api('DELETE', 'JellyDvrSchedule/Favourites/' + info.Favourite.Id)
      : api('POST', 'JellyDvrSchedule/Favourites/FromItem/' + id);
    call.then(function (r) {
      delete forItem[id];
      data = null; // the lists reload with the change
      done((r && r.Message) || '');
    }, function () { done('Could not change the recording favourite.'); });
  }

  function label(info, armed) {
    return info.Favourite ? (armed ? 'Press again to remove from Recording Favourites' : 'Remove from Recording Favourites') : 'Add to Recording Favourites';
  }

  function checkActionSheet() {
    var scroller = document.querySelector('.actionSheet.opened .actionSheetScroller');
    if (!scroller || !menuItemId || Date.now() - menuClickedAt > 5000 || scroller.querySelector('[data-id="' + MENU_ID + '"]')) {
      return;
    }
    var id = menuItemId;
    var info = lookup(id);
    if (!info) {
      return; // not loaded yet (re-checked when it is), or not a show
    }
    var armed = false;
    var item = document.createElement('button');
    item.setAttribute('is', 'emby-button');
    item.type = 'button';
    item.className = 'listItem listItem-button actionSheetMenuItem emby-button';
    item.setAttribute('data-id', MENU_ID);
    item.innerHTML = '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons" aria-hidden="true" style="color:#f5c518;"></span>'
      + '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText"></div></div>';
    item.querySelector('.material-icons').textContent = info.Favourite ? 'star' : 'star_border';
    item.querySelector('.actionSheetItemText').textContent = label(info, false);
    item.addEventListener('click', function (e) {
      if (info.Favourite && !armed) {
        // Keep the sheet open for the confirming press.
        e.stopPropagation();
        e.preventDefault();
        armed = true;
        item.querySelector('.actionSheetItemText').textContent = label(info, true);
        return;
      }
      toggle(id, info, toast);
    }, true);
    scroller.appendChild(item);
  }

  // Below Record on a programme's details page (opened from a programme tile).
  function checkDetailsRow() {
    var id = detailsId();
    var page = document.querySelector('.page:not(.hide)');
    if (!page) {
      return;
    }
    [].slice.call(page.querySelectorAll('.' + ROW_CLASS)).forEach(function (el) {
      if (el.getAttribute('data-item') !== id) {
        el.parentNode.removeChild(el);
      }
    });
    if (!id || page.querySelector('.' + ROW_CLASS)) {
      return;
    }
    var record = [].slice.call(page.querySelectorAll('.recordingFields-buttons'))
      .filter(function (r) { return r.offsetParent !== null && !r.classList.contains(ROW_CLASS); }).pop();
    if (!record) {
      return;
    }
    var info = lookup(id);
    if (!info) {
      return;
    }
    var row = document.createElement('div');
    row.className = 'recordingFields-buttons flex align-items-center ' + ROW_CLASS;
    row.setAttribute('data-item', id);
    row.innerHTML = '<div><button is="emby-button" type="button" class="raised recordingButton emby-button">'
      + '<span class="material-icons recordingIcon" aria-hidden="true" style="color:#f5c518;"></span><span class="buttonText"></span></button>'
      + '<div class="fieldDescription" style="margin:.3em 0 0 .2em;opacity:.8;"></div></div>';
    var button = row.querySelector('button');
    var note = row.querySelector('.fieldDescription');
    var armed = false;
    function show() {
      row.querySelector('.recordingIcon').textContent = info.Favourite ? 'star' : 'star_border';
      row.querySelector('.buttonText').textContent = label(info, armed);
    }
    show();
    button.addEventListener('click', function () {
      if (info.Favourite && !armed) {
        armed = true;
        show();
        setTimeout(function () { armed = false; show(); }, 4000);
        return;
      }
      button.disabled = true;
      toggle(id, info, function (msg) {
        note.textContent = msg;
        button.disabled = false;
        armed = false;
        info = { Title: info.Title, IsMovie: info.IsMovie, Favourite: info.Favourite ? null : { Id: '', Title: info.Title } };
        // Re-read the real state (the new favourite's id) shortly.
        setTimeout(function () { delete forItem[id]; var fresh = lookup(id); if (fresh) { info = fresh; show(); } }, 1500);
        show();
      });
    });
    record.parentNode.insertBefore(row, record.nextSibling);
  }

  /*
   * Live TV > Schedule: jellyfin-web shows the upcoming recordings as large picture cards in a
   * sideways row per day, which is slow to get through when a show has many recordings in a day.
   * A Cards | List switch adds a compact list instead: one row per recording, grouped by day,
   * stacked so the arrow keys go straight down it; selecting a row opens the programme.
   */
  var SCHEDULE_TAB = '4';
  var VIEW_KEY = 'jdvr.scheduleView';
  var BAR_ID = 'jdvrScheduleBar';
  var LIST_ID = 'jdvrScheduleList';
  var upcoming = null;
  var upcomingAt = 0;

  function scheduleView() {
    try {
      return localStorage.getItem(VIEW_KEY) || 'cards';
    } catch (e) {
      return 'cards';
    }
  }

  function setScheduleView(v) {
    try {
      localStorage.setItem(VIEW_KEY, v);
    } catch (e) { /* private window */ }
    checkSchedule(true);
  }

  function timeOnly(iso) {
    return new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  }

  function dayOf(iso) {
    return new Date(iso).toLocaleDateString([], { weekday: 'long', month: 'short', day: 'numeric' });
  }

  function badge(text, colour, fill) {
    return '<span style="font-size:.8em;padding:.05em .45em;border-radius:.3em;margin-left:.4em;white-space:nowrap;'
      + (fill ? 'background:' + colour + ';color:#111;font-weight:600;' : 'border:1px solid ' + colour + ';color:' + colour + ';') + '">' + esc(text) + '</span>';
  }

  function renderSchedule(list) {
    list.innerHTML = '';
    if (!upcoming) {
      list.textContent = 'Loading…';
      return;
    }
    if (!upcoming.length) {
      list.textContent = 'Nothing scheduled.';
      return;
    }
    var day = null;
    upcoming.forEach(function (r) {
      var d = dayOf(r.Start);
      if (d !== day) {
        day = d;
        var h = document.createElement('h2');
        h.className = 'sectionTitle';
        h.style.cssText = 'margin:1em 0 .3em;';
        h.textContent = d + ' (' + upcoming.filter(function (x) { return dayOf(x.Start) === d; }).length + ')';
        list.appendChild(h);
      }
      var row = document.createElement('button');
      row.type = 'button';
      row.className = 'emby-button focusable';
      row.style.cssText = 'display:flex;width:100%;align-items:center;gap:1em;text-align:left;background:rgba(255,255,255,.04);'
        + 'border:0;border-bottom:1px solid rgba(128,128,128,.2);color:inherit;padding:.55em .8em;font-size:1em;cursor:pointer;';
      var sub = [r.SeasonEpisode, r.EpisodeTitle].filter(function (x) { return !!x; }).join(' · ');
      row.innerHTML = '<span style="width:9.5em;flex:none;opacity:.9;">' + esc(timeOnly(r.Start)) + ' – ' + esc(timeOnly(r.End)) + '</span>'
        + '<span style="width:9em;flex:none;opacity:.75;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">'
        + esc([r.ChannelNumber, r.ChannelName].filter(function (x) { return !!x; }).join(' ')) + '</span>'
        + '<span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;"><b>' + esc(r.Name) + '</b>'
        + (sub ? ' <span style="opacity:.75;">— ' + esc(sub) + '</span>' : '') + '</span>'
        + '<span style="flex:none;">'
        + (r.RecordingNow ? badge('● Recording', '#e53935', true) : '')
        + (r.Kind === 'favourite' ? badge('★ ' + (r.Favourite || 'Favourite'), '#f5c518', false) : r.Kind === 'series' ? badge('Series', '#9aa0a6', false) : badge('One-off', '#9aa0a6', false))
        + (r.IsRepeat ? badge('Repeat', '#9aa0a6', false) : '')
        + '</span>';
      if (r.ProgramId) {
        row.addEventListener('click', function () {
          window.location.hash = '#/details?id=' + r.ProgramId.replace(/-/g, '') + '&serverId=' + encodeURIComponent(window.ApiClient.serverId());
        });
      }
      list.appendChild(row);
    });
  }

  function loadUpcoming(force) {
    if (!force && upcoming && Date.now() - upcomingAt < 60000) {
      return Promise.resolve(upcoming);
    }
    return api('GET', 'JellyDvrSchedule/Upcoming').then(function (r) {
      upcoming = r;
      upcomingAt = Date.now();
      return r;
    });
  }

  function checkSchedule(force) {
    var page = document.querySelector('#liveTvPage');
    var bar = document.getElementById(BAR_ID);
    var list = document.getElementById(LIST_ID);
    if (!page || tabOf('#/livetv') !== SCHEDULE_TAB) {
      if (bar) { bar.parentNode.removeChild(bar); }
      if (list) { list.parentNode.removeChild(list); }
      if (page) { page.classList.remove('jdvr-list-mode'); }
      return;
    }
    if (!document.getElementById('jdvrScheduleStyle')) {
      var style = document.createElement('style');
      style.id = 'jdvrScheduleStyle';
      style.textContent = '#liveTvPage.jdvr-list-mode > .verticalSection { display: none !important; }'
        + ' #' + LIST_ID + ' button:focus { outline: 2px solid #00a4dc; background: rgba(0,164,220,.15) !important; }';
      document.head.appendChild(style);
    }
    var view = scheduleView();
    if (!bar || bar.parentNode !== page) {
      bar && bar.parentNode && bar.parentNode.removeChild(bar);
      bar = document.createElement('div');
      bar.id = BAR_ID;
      bar.className = 'padded-left padded-right';
      bar.style.cssText = 'display:flex;gap:.4em;align-items:center;padding:.6em 0;';
      page.insertBefore(bar, page.firstChild);
    }
    if (force || bar.getAttribute('data-view') !== view) {
      bar.setAttribute('data-view', view);
      bar.innerHTML = '<span style="opacity:.75;margin-right:.4em;">View:</span>';
      ['cards', 'list'].forEach(function (v) {
        var b = button(v === 'cards' ? 'Cards' : 'List', v === view, function () { setScheduleView(v); });
        bar.appendChild(b);
      });
    }
    page.classList.toggle('jdvr-list-mode', view === 'list');
    if (view !== 'list') {
      if (list) { list.parentNode.removeChild(list); }
      return;
    }
    if (!list || list.parentNode !== page) {
      list && list.parentNode && list.parentNode.removeChild(list);
      list = document.createElement('div');
      list.id = LIST_ID;
      list.className = 'padded-left padded-right';
      page.insertBefore(list, bar.nextSibling);
      renderSchedule(list);
      force = true;
    }
    if (force || Date.now() - upcomingAt > 60000) {
      upcomingAt = Date.now(); // one load at a time
      loadUpcoming(true).then(function () { var l = document.getElementById(LIST_ID); if (l) { renderSchedule(l); } }, function () {
        var l = document.getElementById(LIST_ID);
        if (l) { l.textContent = 'Could not load the schedule.'; }
      });
    }
  }

  function checkMenus() {
    if (!(window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId())) {
      return;
    }
    checkActionSheet();
    checkDetailsRow();
    checkSchedule(false);
  }

  new MutationObserver(scheduleCheck).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', scheduleCheck);
  scheduleCheck();
  log('favourites script loaded');
})();
