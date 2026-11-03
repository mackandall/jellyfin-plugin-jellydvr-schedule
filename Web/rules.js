/*
 * JellyDVR Schedule — Recording rules in jellyfin-web, on Live TV > Series: Jellyfin's series
 * recordings and the Recording Favourites in one list, with state (recording / paused / off /
 * not in the guide), next airings, what the library has, and Edit / Pause / Recordings / Remove.
 * In Priority sort the list is the conflict order: drag rows (or ↑ ↓) to change it.
 * Two layouts, switched in the bar and remembered per browser: Cards (one card per rule) and
 * Grouped (rules grouped by state with a 7-day strip each, the chosen one in a detail panel).
 *
 * Loaded by tuners.js. VERSION-SENSITIVE: jellyfin-web 12 draws Live TV with React; `#/livetv?tab=5`
 * is Series, inside `div#liveTvPage`. Jellyfin's own series cards are hidden while this list shows
 * (⋯ › Show Jellyfin's series cards puts them back).
 */
(function () {
  'use strict';

  if (window.__jdvrRules) {
    return;
  }
  window.__jdvrRules = true;

  var SECTION_ID = 'jdvrRulesSection';
  var SERIES_TAB = '5';
  var STORE = 'jdvrRules.';
  var data = null;
  var loadedAt = 0;
  var loading = null;
  var ui = {
    sort: read('sort', 'priority'),
    filter: 'all',
    search: '',
    open: null, // 'pause:<id>' / 'edit:<id>' / 'new' / 'more'
    armed: null, // rule id whose Remove was pressed once
    status: '',
    showNative: read('native', '') === '1',
    layout: read('layout', 'cards'), // 'cards' / 'grouped' (Grouped: by state, a day strip each, a detail panel)
    sel: null, // Grouped: the chosen rule
    prio: false, // Grouped: setting priorities
    closed: read('closed', 'wait,none').split(',')
  };

  function read(key, fallback) {
    try {
      return window.localStorage.getItem(STORE + key) || fallback;
    } catch (e) {
      return fallback;
    }
  }

  function write(key, value) {
    try {
      window.localStorage.setItem(STORE + key, value);
    } catch (e) {
      // private mode: the choice lasts this visit only
    }
  }

  function api(method, path, body) {
    var client = window.ApiClient;
    var opts = { type: method, url: client.getUrl(path), dataType: 'json' };
    if (body !== undefined) {
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

  // Made once: a render formats hundreds of dates.
  var FMT = {
    date: new Intl.DateTimeFormat([], { weekday: 'short', month: 'short', day: 'numeric' }),
    time: new Intl.DateTimeFormat([], { hour: 'numeric', minute: '2-digit' }),
    weekday: new Intl.DateTimeFormat([], { weekday: 'short' }),
    narrow: new Intl.DateTimeFormat([], { weekday: 'narrow' })
  };

  // A date, with the time when it isn't midnight (a pause that skips one episode ends just after it starts).
  function day(iso) {
    var d = new Date(iso);
    var text = FMT.date.format(d);
    return d.getHours() || d.getMinutes() ? text + ', ' + FMT.time.format(d) : text;
  }

  // Midnight, [offset] days from today.
  function dayStart(offset) {
    var d = new Date();
    d.setHours(0, 0, 0, 0);
    d.setDate(d.getDate() + offset);
    return d;
  }

  // Which of the next n days (0 = today) a time falls on, or -1.
  function dayIndex(t, n) {
    var d = new Date(t);
    d.setHours(0, 0, 0, 0);
    var k = Math.round((d - dayStart(0)) / 86400000);
    return k >= 0 && k < n ? k : -1;
  }

  // Cards: "today 7:00 PM", "Sun, Nov 8, 10:00 PM".
  function when(iso) {
    var k = dayIndex(iso, 2);
    var time = FMT.time.format(new Date(iso));
    return k === 0 ? 'today ' + time : k === 1 ? 'tomorrow ' + time : FMT.date.format(new Date(iso)) + ', ' + time;
  }

  function daysAhead(days) {
    return dayStart(days).toISOString();
  }

  function nextAiring(r) {
    return (r.Next || []).filter(function (a) { return !a.Held; })[0] || null;
  }

  function matches(r) {
    var words = ui.search.trim().toLowerCase();
    return !words || (r.Name + ' ' + r.Details).toLowerCase().indexOf(words) >= 0;
  }

  function onSeriesTab() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/livetv') !== 0) {
      return false;
    }
    var q = hash.indexOf('?') >= 0 ? new URLSearchParams(hash.slice(hash.indexOf('?') + 1)) : null;
    return !!q && q.get('tab') === SERIES_TAB;
  }

  function host() {
    var page = document.querySelector('#liveTvPage');
    return page && (page.querySelector('.padded-bottom-page') || page);
  }

  function load(force) {
    if (!force && data && Date.now() - loadedAt < 30000) {
      return Promise.resolve(data);
    }
    if (loading) {
      return loading;
    }
    loading = api('GET', 'JellyDvrSchedule/Rules').then(function (r) {
      data = r;
      loadedAt = Date.now();
      loading = null;
      return r;
    }, function (e) {
      loading = null;
      throw e;
    });
    return loading;
  }

  // The server works in the background after a change: look again shortly, and once more later.
  // A new change starts the looks again (several quick changes don't pile them up).
  var reloads = [];
  function reloadSoon() {
    reloads.forEach(clearTimeout);
    reloads = [1500, 9000, 25000, 45000].map(function (ms) {
      return setTimeout(function () { load(true).then(render, function () {}); }, ms);
    });
  }

  function say(msg) {
    ui.status = msg || '';
    var s = document.getElementById(SECTION_ID + 'Status');
    if (s) {
      s.textContent = ui.status;
    }
  }

  function act(promise, done) {
    promise.then(function (r) {
      say((r && r.Message) || done || 'Done.');
      reloadSoon();
    }, function (e) {
      say('That didn’t work' + (e && e.status ? ' (' + e.status + ')' : '') + '. Try again, or reload the page.');
    });
  }

  // ---- elements ---------------------------------------------------------------------------

  function el(tag, cls, html) {
    var e = document.createElement(tag);
    if (cls) {
      e.className = cls;
    }
    if (html != null) {
      e.innerHTML = html;
    }
    return e;
  }

  function button(label, kind, onClick) {
    var b = el('button', 'jdvrR-btn' + (kind ? ' ' + kind : ''));
    b.type = 'button';
    b.textContent = label;
    b.addEventListener('click', function (e) {
      e.stopPropagation();
      onClick(e);
    });
    return b;
  }

  var CSS =
    '#' + SECTION_ID + '{margin:0 0 2em;--r-fg:255,255,255;--r-pop:#202020;--r-popfg:#eee;--r-popline:#3a3a3a;--r-field:#151515;--r-recfg:#ff9b9e;--r-heldfg:#ffc46b;--r-now:#ff6b70;--r-line:rgba(128,128,128,.28);--r-card:rgba(var(--r-fg),.045);--r-muted:rgba(var(--r-fg),.66);--r-accent:#00a4dc;--r-amber:#f5a524;--r-green:#5fd39a;--r-gold:#f2c94c;--r-red:#ff8a8d}' +
    '#' + SECTION_ID + '.jdvrR-light{--r-pop:#fff;--r-popfg:#1a1a1a;--r-popline:rgba(0,0,0,.18);--r-field:#fff;--r-line:rgba(0,0,0,.18);--r-amber:#9a5a00;--r-green:#1b7a48;--r-gold:#8a6a00;--r-red:#c0262d;--r-recfg:#b3161d;--r-heldfg:#8a5000;--r-now:#c8161e;--r-accent:#0086b8}' +
    '#' + SECTION_ID + ' .jdvrR-bar{display:flex;flex-wrap:wrap;gap:.6em;align-items:center;margin:.4em 0 .7em}' +
    '#' + SECTION_ID + ' .jdvrR-search{flex:1 1 16em;min-width:12em;background:rgba(var(--r-fg),.07);color:inherit;border:1px solid var(--r-line);border-radius:.35em;padding:.5em .7em;font-size:1em}' +
    '#' + SECTION_ID + ' .jdvrR-seg{display:flex;border:1px solid var(--r-line);border-radius:.35em;overflow:hidden}' +
    '#' + SECTION_ID + ' .jdvrR-seg button{background:transparent;color:var(--r-muted);border:0;border-right:1px solid var(--r-line);padding:.5em .85em;font-size:.92em;cursor:pointer}' +
    '#' + SECTION_ID + ' .jdvrR-seg button:last-child{border-right:0}' +
    '#' + SECTION_ID + ' .jdvrR-seg button.on{background:var(--r-accent);color:#fff}' +
    '#' + SECTION_ID + ' .jdvrR-chips{display:flex;flex-wrap:wrap;gap:.45em;margin-bottom:.6em}' +
    '#' + SECTION_ID + ' .jdvrR-chip{border:1px solid var(--r-line);border-radius:99em;padding:.2em .75em;font-size:.88em;color:var(--r-muted);background:transparent;cursor:pointer}' +
    '#' + SECTION_ID + ' .jdvrR-chip.on{border-color:var(--r-accent);color:inherit}' +
    '#' + SECTION_ID + ' .jdvrR-chip b{font-weight:600;margin-left:.3em;font-variant-numeric:tabular-nums;opacity:.85}' +
    '#' + SECTION_ID + ' .jdvrR-btn{background:rgba(var(--r-fg),.1);color:inherit;border:1px solid var(--r-line);border-radius:.3em;padding:.42em .85em;font-size:.92em;cursor:pointer;white-space:nowrap}' +
    '#' + SECTION_ID + ' .jdvrR-btn.primary{background:var(--r-accent);border-color:var(--r-accent);color:#fff}' +
    '#' + SECTION_ID + ' .jdvrR-btn.warn{color:var(--r-red)}' +
    '#' + SECTION_ID + ' .jdvrR-btn:focus-visible,#' + SECTION_ID + ' .jdvrR-chip:focus-visible,#' + SECTION_ID + ' .jdvrR-seg button:focus-visible{outline:2px solid var(--r-accent);outline-offset:2px}' +
    '#' + SECTION_ID + ' .jdvrR-note{color:var(--r-muted);font-size:.9em;margin:.2em 0 .6em}' +
    '#' + SECTION_ID + ' .jdvrR-list{display:grid;gap:.55em}' +
    '#' + SECTION_ID + ' .jdvrR-row{display:grid;grid-template-columns:auto 9.5em 1fr auto;gap:1em;align-items:center;background:var(--r-card);border:1px solid var(--r-line);border-radius:.5em;padding:.65em .8em}' +
    '#' + SECTION_ID + ' .jdvrR-row.paused{background:rgba(245,165,36,.06);border-color:rgba(245,165,36,.3)}' +
    '#' + SECTION_ID + ' .jdvrR-row.off{opacity:.7}' +
    '#' + SECTION_ID + ' .jdvrR-row.dragging{opacity:.4}' +
    '#' + SECTION_ID + ' .jdvrR-row.drop{box-shadow:0 -3px 0 var(--r-accent)}' +
    '#' + SECTION_ID + ' .jdvrR-rank{width:2.2em;display:grid;justify-items:center;gap:.1em;color:var(--r-muted);font-variant-numeric:tabular-nums;font-size:1.1em;cursor:grab;user-select:none}' +
    '#' + SECTION_ID + ' .jdvrR-rank small{display:flex;gap:.15em}' +
    '#' + SECTION_ID + ' .jdvrR-rank small button{background:transparent;border:0;color:var(--r-muted);cursor:pointer;padding:0 .15em;font-size:.85em}' +
    '#' + SECTION_ID + ' .jdvrR-art{width:9.5em;aspect-ratio:16/9;border-radius:.3em;background:#222 center/cover no-repeat;position:relative;display:flex;align-items:flex-end;padding:.35em .5em;color:#fff;font-weight:600;font-size:.92em;line-height:1.15;text-shadow:0 1px 3px rgba(0,0,0,.8);overflow:hidden}' +
    '#' + SECTION_ID + ' .jdvrR-art .ov{position:absolute;inset:0;display:grid;place-items:center;background:rgba(10,8,4,.55);color:var(--r-amber);font-size:1.6em;text-shadow:none}' +
    '#' + SECTION_ID + ' .jdvrR-info{display:grid;gap:.15em;min-width:0}' +
    '#' + SECTION_ID + ' .jdvrR-title{display:flex;flex-wrap:wrap;align-items:center;gap:.5em}' +
    '#' + SECTION_ID + ' .jdvrR-title .name{font-size:1.12em;font-weight:600}' +
    '#' + SECTION_ID + ' .jdvrR-kind{font-size:.72em;text-transform:uppercase;letter-spacing:.05em;color:var(--r-muted);border:1px solid var(--r-line);border-radius:.25em;padding:0 .4em}' +
    '#' + SECTION_ID + ' .jdvrR-kind.fav{color:var(--r-gold);border-color:rgba(242,201,76,.4)}' +
    '#' + SECTION_ID + ' .jdvrR-pill{font-size:.8em;font-weight:600;border-radius:.25em;padding:.05em .5em}' +
    '#' + SECTION_ID + ' .jdvrR-pill.recording{background:rgba(46,163,107,.16);color:var(--r-green)}' +
    '#' + SECTION_ID + ' .jdvrR-pill.paused{background:rgba(245,165,36,.17);color:var(--r-amber)}' +
    '#' + SECTION_ID + ' .jdvrR-pill.off,#' + SECTION_ID + ' .jdvrR-pill.not-in-guide,#' + SECTION_ID + ' .jdvrR-pill.nothing-to-record{background:rgba(var(--r-fg),.09);color:var(--r-muted)}' +
    '#' + SECTION_ID + ' .jdvrR-facts{color:var(--r-muted);font-size:.88em}' +
    '#' + SECTION_ID + ' .jdvrR-sched{font-size:.93em}' +
    '#' + SECTION_ID + ' .jdvrR-sched .m{color:var(--r-muted)}' +
    '#' + SECTION_ID + ' .jdvrR-sched .h{color:var(--r-amber)}' +
    '#' + SECTION_ID + ' .jdvrR-actions{display:flex;flex-wrap:wrap;gap:.35em;justify-content:flex-end;position:relative}' +
    '#' + SECTION_ID + ' .jdvrR-pop{position:absolute;right:0;top:calc(100% + .5em);z-index:20;width:23em;max-width:80vw;background:var(--r-pop);color:var(--r-popfg);border:1px solid var(--r-popline);border-radius:.5em;padding:.8em;box-shadow:0 10px 30px rgba(0,0,0,.6);display:grid;gap:.5em;text-align:left}' +
    '#' + SECTION_ID + ' .jdvrR-pop h4{margin:0;font-size:1em}' +
    '#' + SECTION_ID + ' .jdvrR-pop .grid{display:grid;grid-template-columns:1fr 1fr;gap:.35em}' +
    '#' + SECTION_ID + ' .jdvrR-pop .grid .jdvrR-btn{text-align:left;white-space:normal}' +
    '#' + SECTION_ID + ' .jdvrR-pop .line{display:flex;gap:.35em;align-items:center}' +
    '#' + SECTION_ID + ' .jdvrR-pop input,#' + SECTION_ID + ' .jdvrR-form input,#' + SECTION_ID + ' .jdvrR-form select{background:var(--r-field);color:inherit;border:1px solid var(--r-popline);border-radius:.3em;padding:.4em .5em;font-size:.95em}' +
    '#' + SECTION_ID + ' .jdvrR-pop .hint{color:var(--r-muted);font-size:.85em}' +
    '#' + SECTION_ID + ' .jdvrR-form{display:flex;flex-wrap:wrap;gap:.5em;align-items:center;grid-column:1/-1;padding-top:.4em}' +
    '#' + SECTION_ID + ' .jdvrR-empty{color:var(--r-muted);padding:1em 0}' +
    '@media (max-width:760px){#' + SECTION_ID + ' .jdvrR-row{grid-template-columns:7em 1fr}#' + SECTION_ID + ' .jdvrR-rank{display:none}#' + SECTION_ID + ' .jdvrR-art{width:7em}#' + SECTION_ID + ' .jdvrR-actions{grid-column:1/-1;justify-content:flex-start}#' + SECTION_ID + ' .jdvrR-pop{left:0;right:auto}}';

  var BCSS =
    '#S .jdvrB{display:grid;grid-template-columns:minmax(330px,520px) 1fr;gap:1.4em;align-items:start}' +
    '@media (max-width:900px){#S .jdvrB{grid-template-columns:1fr}}' +
    '#S .jdvrB-sum{color:var(--r-muted);margin:-.2em 0 .7em;font-variant-numeric:tabular-nums}' +
    '#S .jdvrB-sum i{font-style:normal;color:inherit;font-weight:600}' +
    '#S .jdvrB-list{display:grid;gap:1em}' +
    '#S .jdvrB-group{background:rgba(var(--r-fg),.045);border-radius:.7em;overflow:hidden}' +
    '#S .jdvrB-ghead{display:grid;grid-template-columns:1fr 11em;align-items:center;gap:.7em;padding:.7em .85em .55em;cursor:pointer;user-select:none}' +
    '#S .jdvrB-ghead .t{display:flex;align-items:center;gap:.55em;font-weight:600}' +
    '#S .jdvrB-ghead .n{color:var(--r-muted);font-weight:500;font-variant-numeric:tabular-nums}' +
    '#S .jdvrB-ghead .chev{color:var(--r-muted);width:.9em;display:inline-block;transition:transform .15s}' +
    '#S .jdvrB-group.closed .chev{transform:rotate(-90deg)}' +
    '#S .jdvrB-group.closed .jdvrB-rows,#S .jdvrB-group.closed .jdvrB-gsub{display:none}' +
    '#S .jdvrB-days{display:grid;grid-template-columns:repeat(7,1fr);gap:2px;text-align:center;font-size:.75em;color:var(--r-muted)}' +
    '#S .jdvrB-days .today{color:inherit;font-weight:700}' +
    '#S .jdvrB-gsub{padding:0 .85em .55em;color:var(--r-muted);font-size:.86em;margin-top:-.3em}' +
    '#S .jdvrB-row{display:grid;grid-template-columns:5.2em 1fr 11em;gap:.7em;align-items:center;padding:.5em .85em;border-top:1px solid rgba(var(--r-fg),.06);cursor:pointer}' +
    '#S .jdvrB-row:hover{background:rgba(var(--r-fg),.04)}' +
    '#S .jdvrB-row.sel{background:rgba(0,164,220,.16);box-shadow:inset 3px 0 0 var(--r-accent)}' +
    '#S .jdvrB-row:focus-visible,#S .jdvrB-ghead:focus-visible{outline:2px solid var(--r-accent);outline-offset:-2px}' +
    '#S .jdvrB-thumb{width:5.2em;aspect-ratio:16/9;border-radius:.3em;background:rgba(var(--r-fg),.12) center/cover no-repeat}' +
    '#S .jdvrB-nm{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '#S .jdvrB-st{color:var(--r-muted);font-size:.86em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '#S .jdvrB-st.held{color:var(--r-amber)}' +
    '#S .jdvrB-star{color:var(--r-gold)}' +
    '#S .jdvrB-strip{display:grid;gap:2px;height:1.55em}' +
    '#S .jdvrB-strip.big{height:3.2em;gap:4px}' +
    '#S .jdvrB-cell{background:rgba(var(--r-fg),.07);border-radius:3px;display:flex;align-items:center;justify-content:center;gap:2px}' +
    '#S .jdvrB-cell.today{box-shadow:inset 0 0 0 1px rgba(var(--r-fg),.28)}' +
    '#S .jdvrB-dot{width:6px;height:6px;border-radius:50%;background:#e5484d}' +
    '#S .jdvrB-dot.held{background:transparent;border:1.5px solid var(--r-amber);width:7px;height:7px}' +
    '#S .jdvrB-cell .more{font-size:.65em;color:var(--r-muted)}' +
    '#S .jdvrB-strip.wait .jdvrB-cell{background:repeating-linear-gradient(135deg,rgba(var(--r-fg),.06) 0 4px,rgba(var(--r-fg),.02) 4px 8px)}' +
    '#S .jdvrB-strip.big .jdvrB-cell{flex-direction:column;font-size:.8em;color:var(--r-muted)}' +
    '#S .jdvrB-strip.big .ds{display:flex;gap:3px;height:8px;align-items:center}' +
    '#S .jdvrB-legend{display:flex;gap:1em;color:var(--r-muted);font-size:.85em;flex-wrap:wrap}' +
    '#S .jdvrB-legend span{display:inline-flex;gap:.4em;align-items:center}' +
    '#S .jdvrB-detail{background:rgba(var(--r-fg),.045);border-radius:.7em;padding:1.4em 1.5em;display:grid;gap:1.1em;position:sticky;top:5.5em}' +
    '#S .jdvrB-dhead{display:grid;grid-template-columns:15em 1fr;gap:1.2em;align-items:center}' +
    '@media (max-width:640px){#S .jdvrB-dhead{grid-template-columns:1fr}}' +
    '#S .jdvrB-dhead .art{width:100%;aspect-ratio:16/9;border-radius:.5em;background:rgba(var(--r-fg),.12) center/cover no-repeat}' +
    '#S .jdvrB-dhead h3{margin:0;font-size:1.5em;font-weight:600}' +
    '#S .jdvrB-dhead .k{color:var(--r-muted);margin:.2em 0 .7em}' +
    '#S .jdvrB-state{display:flex;gap:.7em;align-items:center;flex-wrap:wrap}' +
    '#S .jdvrB-state .why{color:var(--r-muted)}' +
    '#S .jdvrB-pill{display:inline-flex;align-items:center;gap:.4em;padding:.2em .75em;border-radius:99em;font-size:.88em;font-weight:600}' +
    '#S .jdvrB-pill.recording{background:rgba(229,72,77,.2);color:var(--r-recfg)}' +
    '#S .jdvrB-pill.paused{background:rgba(245,165,36,.2);color:var(--r-heldfg)}' +
    '#S .jdvrB-pill.quiet{background:rgba(var(--r-fg),.1);color:var(--r-muted)}' +
    '#S .jdvrB-sect h4{margin:0 0 .5em;font-size:.8em;font-weight:600;color:var(--r-muted);text-transform:uppercase;letter-spacing:.06em}' +
    '#S .jdvrB-facts{display:grid;grid-template-columns:repeat(auto-fit,minmax(8.5em,1fr));gap:.55em}' +
    '#S .jdvrB-fact{background:rgba(var(--r-fg),.06);border-radius:.5em;padding:.6em .85em}' +
    '#S .jdvrB-fact .v{font-size:1.2em;font-weight:600;font-variant-numeric:tabular-nums}' +
    '#S .jdvrB-fact .v small{color:var(--r-muted);font-weight:500;font-size:.7em}' +
    '#S .jdvrB-fact .l{color:var(--r-muted);font-size:.85em}' +
    '#S .jdvrB-air{display:grid;grid-template-columns:10.5em 1fr auto;gap:.7em;padding:.4em 0;border-top:1px solid rgba(var(--r-fg),.07);font-variant-numeric:tabular-nums}' +
    '#S .jdvrB-air:first-of-type{border-top:0}' +
    '#S .jdvrB-air .w,#S .jdvrB-air .tag{color:var(--r-muted)}' +
    '#S .jdvrB-air .tag{font-size:.86em}' +
    '#S .jdvrB-air.held .e,#S .jdvrB-air.held .tag{color:var(--r-amber)}' +
    '#S .jdvrB-air.now .w{color:var(--r-now);font-weight:600}' +
    '#S .jdvrB-actions{display:flex;gap:.5em;flex-wrap:wrap;border-top:1px solid var(--r-line);padding-top:1em;position:relative}' +
    '#S .jdvrB-actions .sp{flex:1}' +
    '#S .jdvrB-detail .jdvrR-pop{position:static;width:auto;max-width:none;box-shadow:none;background:rgba(var(--r-fg),.06);border-color:var(--r-line);color:inherit}' +
    '#S .jdvrB-detail .jdvrR-form{padding:0}' +
    '#S .jdvrB-prow{display:grid;grid-template-columns:2em 5.2em 1fr auto;gap:.7em;align-items:center;padding:.45em .85em;border-top:1px solid rgba(var(--r-fg),.06);cursor:grab}' +
    '#S .jdvrB-prow .rk{color:var(--r-muted);font-variant-numeric:tabular-nums;text-align:right}' +
    '#S .jdvrB-prow.dragging{opacity:.4}' +
    '#S .jdvrB-prow.drop{box-shadow:0 -3px 0 var(--r-accent)}' +
    '#S .jdvrB-prow .jdvrR-btn{padding:.2em .55em}' +
    '#S .jdvrB-empty{padding:1em .85em;color:var(--r-muted)}';
  BCSS = BCSS.split('#S ').join('#' + SECTION_ID + ' ');

  function image(rule) {
    if (!rule.Image) {
      return null;
    }
    var q = { fillWidth: 320, quality: 80 };
    if (rule.Image.Tag) {
      q.tag = rule.Image.Tag;
    }
    return window.ApiClient.getUrl('Items/' + rule.Image.ItemId + '/Images/' + rule.Image.Type, q);
  }

  function stateLabel(r) {
    switch (r.State) {
      case 'paused':
        return r.UntilPremiere ? 'Paused until the season starts' : r.Until ? 'Paused until ' + day(r.Until) : 'Paused';
      case 'off':
        return r.Until ? 'Off until ' + day(r.Until) : 'Off';
      case 'not-in-guide':
        return 'Not in the guide';
      case 'nothing-to-record':
        return 'Nothing to record';
      default:
        return 'Recording';
    }
  }

  function scheduleLine(r) {
    var parts = [];
    var next = nextAiring(r);
    if (r.State === 'paused') {
      if (r.Held) {
        parts.push('<span class="h">' + r.Held + ' episode' + (r.Held === 1 ? '' : 's') + ' on hold</span>');
      }
      parts.push(next ? 'records again from <b>' + esc(when(next.Start)) + '</b>' : (r.Until ? 'records again from ' + esc(day(r.Until)) : 'nothing records until resumed'));
    } else if (r.State === 'off') {
      parts.push('<span class="m">' + (r.Until ? 'records nothing before ' + esc(day(r.Until)) + ', then switches on' : 'records nothing until switched on') + '</span>');
    } else if (next) {
      parts.push((next.Now ? '<b style="color:var(--r-now)">● Recording now</b>' : 'Next <b>' + esc(when(next.Start)) + '</b>') + ' ' + esc(next.Channel) + ' · ' + r.Upcoming + ' upcoming');
    } else if (r.State === 'nothing-to-record') {
      parts.push('<span class="m">In the guide, but nothing to record (already in the library, or repeats)</span>');
    } else {
      parts.push('<span class="m">No airings in the guide</span>');
    }
    if (r.Recorded) {
      parts.push('<span class="m">' + r.Recorded + ' recorded' + (r.Unwatched ? ', ' + r.Unwatched + ' unwatched' : '') + '</span>');
    }
    return parts.join(' · ');
  }

  // ---- pause / switch off -----------------------------------------------------------------

  function pausePop(r) {
    var fav = r.Kind === 'favourite';
    var pop = el('div', 'jdvrR-pop');
    pop.setAttribute('role', 'dialog');
    pop.addEventListener('click', function (e) { e.stopPropagation(); });
    pop.appendChild(el('h4', null, esc((fav ? 'Switch off ' : 'Pause ') + r.Name)));
    pop.appendChild(el('div', 'hint', fav
      ? 'Its upcoming recordings are cancelled, and scheduled again when it’s back on.'
      : 'Its settings stay. Nothing records while paused, and resuming schedules its episodes again.'));
    var grid = el('div', 'grid');
    function pick(body) {
      ui.open = null;
      render();
      say((fav ? 'Switching off ' : 'Pausing ') + r.Name + '…');
      act(fav
        ? api('POST', 'JellyDvrSchedule/Favourites/' + r.Id.slice(4), { Enabled: false, OffUntil: body.Until || '' })
        : api('POST', 'JellyDvrSchedule/Pauses/' + r.Id, body));
    }
    grid.appendChild(button(fav ? 'Until I switch it on' : 'Until I resume it', 'primary', function () { pick({ Until: '' }); }));
    [[7, 'For 1 week'], [14, 'For 2 weeks'], [28, 'For 4 weeks']].forEach(function (c) {
      grid.appendChild(button(c[1], '', function () { pick({ Until: daysAhead(c[0]) }); }));
    });
    if (!fav) {
      grid.appendChild(button('Skip the next episode', '', function () { pick({ Mode: 'next' }); }));
      grid.appendChild(button('Until the season starts', '', function () { pick({ Mode: 'premiere' }); }));
    }
    pop.appendChild(grid);
    var line = el('div', 'line');
    var id = 'jdvrR-date-' + r.Id.replace(/[^a-z0-9]/gi, '');
    line.innerHTML = '<label class="hint" for="' + id + '">Until</label>';
    var date = el('input');
    date.type = 'date';
    date.id = id;
    var t = new Date(Date.now() + 86400000);
    date.min = t.getFullYear() + '-' + String(t.getMonth() + 1).padStart(2, '0') + '-' + String(t.getDate()).padStart(2, '0');
    date.style.flex = '1';
    line.appendChild(date);
    line.appendChild(button(fav ? 'Switch off' : 'Pause', 'primary', function () {
      if (!date.value) {
        date.focus();
        return;
      }
      var p = date.value.split('-');
      pick({ Until: new Date(+p[0], +p[1] - 1, +p[2]).toISOString() });
    }));
    pop.appendChild(line);
    var held = (r.Next || []).slice(0, 3).map(function (a) { return when(a.Start); });
    if (held.length) {
      pop.appendChild(el('div', 'hint', 'Next airings: ' + esc(held.join(' · ')) + (r.Upcoming + r.Held > 3 ? ' …' : '')));
    }
    pop.appendChild(button('Close', '', function () { ui.open = null; render(); }));
    return pop;
  }

  // ---- favourite form ----------------------------------------------------------------------

  function favouriteForm(fav, onDone) {
    var box = el('div', 'jdvrR-form');
    box.addEventListener('click', function (e) { e.stopPropagation(); });
    box.innerHTML =
      '<input data-f="Title" placeholder="Title or keyword" style="flex:2;min-width:14em" aria-label="Title or keyword" />' +
      '<select data-f="Match" aria-label="Match"><option value="title">Exact title</option><option value="keyword">Keyword</option></select>' +
      '<select data-f="Type" aria-label="Type"><option value="series">Shows</option><option value="movies">Movies</option><option value="any">Shows and movies</option></select>' +
      '<input data-f="Channels" placeholder="Channels (any)" title="Channel numbers or names, comma separated; empty = any channel" style="flex:1;min-width:9em" aria-label="Channels" />' +
      '<label style="white-space:nowrap"><input type="checkbox" data-f="NewOnly" /> New episodes only</label>' +
      '<label style="white-space:nowrap"><input type="checkbox" data-f="SkipInLibrary" /> Skip ones in the library</label>';
    var values = fav || { Title: '', Match: 'title', Type: 'series', Channels: '', NewOnly: false, SkipInLibrary: true };
    box.querySelectorAll('[data-f]').forEach(function (f) {
      var k = f.getAttribute('data-f');
      if (f.type === 'checkbox') {
        f.checked = !!values[k];
      } else {
        f.value = values[k] || f.value;
      }
    });
    box.appendChild(button(fav ? 'Save' : 'Add favourite', 'primary', function () {
      var body = {};
      box.querySelectorAll('[data-f]').forEach(function (f) {
        body[f.getAttribute('data-f')] = f.type === 'checkbox' ? f.checked : f.value;
      });
      if (!body.Title.trim()) {
        say('Enter a title or keyword.');
        return;
      }
      onDone(body);
    }));
    box.appendChild(button('Cancel', '', function () { ui.open = null; render(); }));
    setTimeout(function () { var t = box.querySelector('[data-f="Title"]'); if (t) { t.focus(); } }, 0);
    return box;
  }

  // The favourite's own settings, from the Favourites list (the rules list has them in words only).
  var favDetails = {};
  function loadFavDetails() {
    return api('GET', 'JellyDvrSchedule/Favourites').then(function (r) {
      favDetails = {};
      (r.Favourites || []).forEach(function (f) { favDetails['fav:' + f.Id] = f; });
    }, function () {});
  }

  // Up / down arrows (returned, in a [tag]) and drag-to-reorder on [node], for a rule in a priority list.
  function sortable(node, r, index, list, tag) {
    var arrows = el(tag);
    arrows.appendChild(button('↑', '', function () { move(list, index, index - 1); }));
    arrows.appendChild(button('↓', '', function () { move(list, index, index + 1); }));
    node.draggable = true;
    node.addEventListener('dragstart', function (ev) { dragId = r.Id; node.classList.add('dragging'); ev.dataTransfer.effectAllowed = 'move'; });
    node.addEventListener('dragend', function () { dragId = null; node.classList.remove('dragging'); });
    node.addEventListener('dragover', function (ev) { if (dragId && dragId !== r.Id) { ev.preventDefault(); node.classList.add('drop'); } });
    node.addEventListener('dragleave', function () { node.classList.remove('drop'); });
    node.addEventListener('drop', function (ev) {
      ev.preventDefault();
      node.classList.remove('drop');
      var from = list.map(function (x) { return x.Id; }).indexOf(dragId);
      if (from >= 0) {
        move(list, from, index > from ? index - 1 : index);
      }
    });
    return arrows;
  }

  function togglePause(r) {
    ui.open = ui.open === 'pause:' + r.Id ? null : 'pause:' + r.Id;
    render();
  }

  // A rule's buttons, in both layouts (Grouped has longer labels, Pause first, and Remove apart).
  function ruleActions(r, canManage, grouped) {
    var actions = el('div', grouped ? 'jdvrB-actions' : 'jdvrR-actions');
    var fav = r.Kind === 'favourite';
    var on = r.State !== 'paused' && r.State !== 'off';
    var pause = function (kind) { return button(fav ? 'Switch off…' : 'Pause…', kind, function () { togglePause(r); }); };
    if (canManage) {
      if (r.State === 'paused') {
        actions.appendChild(button('Resume now', 'primary', function () {
          say('Resuming ' + r.Name + '…');
          act(api('DELETE', 'JellyDvrSchedule/Pauses/' + r.Id));
        }));
        actions.appendChild(button(grouped ? 'Change pause…' : 'Change…', '', function () { togglePause(r); }));
      } else if (r.State === 'off') {
        actions.appendChild(button('Switch on', 'primary', function () {
          act(api('POST', 'JellyDvrSchedule/Favourites/' + r.Id.slice(4), { Enabled: true }));
        }));
        actions.appendChild(button('Change…', '', function () { togglePause(r); }));
      } else if (grouped) {
        actions.appendChild(pause('primary'));
      }
      actions.appendChild(button(grouped ? 'Edit settings' : 'Edit', '', function () {
        if (fav) {
          loadFavDetails().then(function () { ui.open = 'edit:' + r.Id; render(); });
        } else {
          window.location.hash = '#/details?seriesTimerId=' + r.Id + '&serverId=' + window.ApiClient.serverId();
        }
      }));
      if (on && !grouped) {
        actions.appendChild(pause(''));
      }
    }
    var rec = button((grouped ? 'Open recordings (' : 'Recordings (') + (r.Recorded || 0) + ')', '', function () {
      if (r.LibraryId) {
        window.location.hash = '#/details?id=' + r.LibraryId + '&serverId=' + window.ApiClient.serverId();
      }
    });
    rec.disabled = !r.LibraryId;
    if (!r.LibraryId) {
      rec.title = 'Nothing of this show is in the library yet';
      rec.style.opacity = '.5';
    }
    actions.appendChild(rec);
    if (canManage) {
      if (grouped) {
        actions.appendChild(el('span', 'sp'));
      }
      var armed = ui.armed === r.Id;
      actions.appendChild(button(armed ? 'Press again to remove' : grouped ? 'Remove rule' : 'Remove', 'warn', function () {
        if (!armed) {
          ui.armed = r.Id;
          render();
          setTimeout(function () { if (ui.armed === r.Id) { ui.armed = null; render(); } }, 4000);
          return;
        }
        ui.armed = null;
        // Gone from the list at once (the server's list catches up).
        data.Rules = data.Rules.filter(function (x) { return x.Id !== r.Id; });
        ui.sel = null;
        render();
        act(fav
          ? api('DELETE', 'JellyDvrSchedule/Favourites/' + r.Id.slice(4))
          : api('DELETE', 'LiveTv/SeriesTimers/' + r.Id).then(function () { return { Message: 'Series recording removed: ' + r.Name }; }));
      }));
    }
    return actions;
  }

  // A favourite's settings, opened by Edit.
  function editForm(r) {
    return favouriteForm(favDetails[r.Id], function (body) {
      ui.open = null;
      render();
      act(api('POST', 'JellyDvrSchedule/Favourites/' + r.Id.slice(4), body));
    });
  }

  // ---- rows --------------------------------------------------------------------------------

  var dragId = null;

  function row(r, canManage, ranked, index, list) {
    var e = el('div', 'jdvrR-row' + (r.State === 'paused' ? ' paused' : r.State === 'off' ? ' off' : ''));
    e.setAttribute('data-rule', r.Id);

    var rank = el('div', 'jdvrR-rank');
    if (ranked) {
      rank.innerHTML = '<span>' + r.Priority + '</span>';
      if (canManage) {
        rank.title = 'Drag to change the priority';
        var arrows = sortable(e, r, index, list, 'small');
        arrows.querySelectorAll('button').forEach(function (b) { b.className = ''; });
        rank.appendChild(arrows);
      }
    } else {
      rank.style.visibility = 'hidden';
    }
    e.appendChild(rank);

    var art = el('div', 'jdvrR-art');
    var src = image(r);
    if (src) {
      art.style.backgroundImage = 'url("' + src + '")';
    } else {
      art.textContent = r.Name;
      art.style.background = 'linear-gradient(135deg,#2b3a52,#121a26)';
    }
    if (r.State === 'paused') {
      art.appendChild(el('span', 'ov', '❚❚'));
    }
    e.appendChild(art);

    var info = el('div', 'jdvrR-info');
    info.appendChild(el('div', 'jdvrR-title',
      '<span class="name">' + esc(r.Name) + '</span>' +
      (r.Kind === 'favourite' ? '<span class="jdvrR-kind fav">★ Favourite</span>' : '<span class="jdvrR-kind">Series</span>') +
      '<span class="jdvrR-pill ' + esc(r.State) + '">' + esc(stateLabel(r)) + '</span>'));
    info.appendChild(el('div', 'jdvrR-facts', esc(r.Details)));
    info.appendChild(el('div', 'jdvrR-sched', scheduleLine(r)));
    e.appendChild(info);

    var actions = ruleActions(r, canManage, false);
    if (ui.open === 'pause:' + r.Id) {
      actions.appendChild(pausePop(r));
    }
    e.appendChild(actions);

    if (ui.open === 'edit:' + r.Id && favDetails[r.Id]) {
      e.appendChild(editForm(r));
    }
    return e;
  }

  // Priority: move a rule within the full priority list and save the order.
  function move(list, from, to) {
    if (to < 0 || to >= list.length || from === to) {
      return;
    }
    var all = data.Rules.slice().sort(function (a, b) { return a.Priority - b.Priority; });
    var item = list[from];
    var target = list[to];
    var ids = all.map(function (x) { return x.Id; }).filter(function (id) { return id !== item.Id; });
    var at = ids.indexOf(target.Id);
    ids.splice(to > from ? at + 1 : at, 0, item.Id);
    ids.forEach(function (id, i) {
      data.Rules.forEach(function (x) { if (x.Id === id) { x.Priority = i + 1; } });
    });
    render();
    act(api('POST', 'JellyDvrSchedule/Rules/Order', ids), 'Priorities saved.');
  }

  // ---- the section -------------------------------------------------------------------------

  var FILTERS = [
    ['all', 'All', function () { return true; }],
    ['recording', 'Recording', function (r) { return r.State === 'recording'; }],
    ['paused', 'Paused', function (r) { return r.State === 'paused'; }],
    ['off', 'Off', function (r) { return r.State === 'off'; }],
    ['not-in-guide', 'Not in the guide', function (r) { return r.State === 'not-in-guide' || r.State === 'nothing-to-record'; }],
    ['favourite', '★ Favourites', function (r) { return r.Kind === 'favourite'; }]
  ];
  var SORTS = [['name', 'A–Z'], ['next', 'Next airing'], ['upcoming', 'Most upcoming'], ['priority', 'Priority']];

  function sorted(rules) {
    var byName = function (a, b) { return a.Name.localeCompare(b.Name); };
    var nextStart = function (r) {
      var n = nextAiring(r);
      return n ? new Date(n.Start).getTime() : Infinity;
    };
    var list = rules.slice();
    switch (ui.sort) {
      case 'name':
        return list.sort(byName);
      case 'next':
        return list.sort(function (a, b) { return (nextStart(a) - nextStart(b)) || byName(a, b); });
      case 'upcoming':
        return list.sort(function (a, b) { return (b.Upcoming - a.Upcoming) || byName(a, b); });
      default:
        return list.sort(function (a, b) { return a.Priority - b.Priority; });
    }
  }

  // Follow the Jellyfin theme: the page's own text colour decides (dark text = a light theme such as Apple TV).
  function lightOrDark(section) {
    var m = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(window.getComputedStyle(section.parentNode || section).color || '');
    var light = !!m && (0.299 * m[1] + 0.587 * m[2] + 0.114 * m[3]) < 128;
    section.classList.toggle('jdvrR-light', light);
    section.style.setProperty('--r-fg', light ? '0,0,0' : '255,255,255');
  }

  function render() {
    var section = document.getElementById(SECTION_ID);
    if (!section || !data) {
      return;
    }
    section.innerHTML = '';
    memo = {};
    lightOrDark(section);
    var head = el('div', 'sectionTitleContainer', '<h2 class="sectionTitle" style="margin:.4em 0;">Recording rules</h2>');
    section.appendChild(head);

    var bar = el('div', 'jdvrR-bar');
    var search = el('input', 'jdvrR-search');
    search.id = 'jdvrR-search';
    search.type = 'search';
    search.placeholder = 'Find a rule (title or channel)';
    search.setAttribute('aria-label', 'Find a rule');
    search.value = ui.search;
    search.addEventListener('input', function () {
      ui.search = search.value;
      var pos = search.selectionStart;
      render();
      var s = document.getElementById('jdvrR-search');
      s.focus();
      s.setSelectionRange(pos, pos);
    });
    bar.appendChild(search);
    var grouped = ui.layout === 'grouped';
    var view = el('div', 'jdvrR-seg');
    view.setAttribute('aria-label', 'Layout');
    [['cards', 'Cards'], ['grouped', 'Grouped']].forEach(function (v) {
      var b = el('button', ui.layout === v[0] ? 'on' : '');
      b.type = 'button';
      b.textContent = v[1];
      b.title = v[0] === 'cards' ? 'One card per rule, with its buttons' : 'Rules grouped by state, with a day strip each; the chosen one opens on the right';
      b.addEventListener('click', function () { ui.layout = v[0]; ui.open = null; ui.prio = false; write('layout', v[0]); render(); });
      view.appendChild(b);
    });
    if (grouped) {
      if (data.CanManage) {
        var pb = button(ui.prio ? 'Done' : 'Set priorities', ui.prio ? 'primary' : '', function () { ui.prio = !ui.prio; ui.open = null; render(); });
        bar.appendChild(pb);
      }
    } else {
      var seg = el('div', 'jdvrR-seg');
      seg.setAttribute('aria-label', 'Sort');
      SORTS.forEach(function (s) {
        var b = el('button', ui.sort === s[0] ? 'on' : '');
        b.type = 'button';
        b.textContent = s[1];
        b.addEventListener('click', function () { ui.sort = s[0]; write('sort', s[0]); render(); });
        seg.appendChild(b);
      });
      bar.appendChild(seg);
    }
    if (data.CanManage) {
      bar.appendChild(button(grouped ? '+ Favourite' : '+ New favourite', 'primary', function () { ui.open = 'new'; render(); }));
      var moreWrap = el('div', 'jdvrR-actions');
      moreWrap.appendChild(button('⋯', '', function () { ui.open = ui.open === 'more' ? null : 'more'; render(); }));
      if (ui.open === 'more') {
        var pop = el('div', 'jdvrR-pop');
        pop.addEventListener('click', function (ev) { ev.stopPropagation(); });
        pop.appendChild(el('h4', null, 'All rules'));
        pop.appendChild(el('div', 'hint', 'Going away? Pause every series recording, and switch off every favourite for the same time.'));
        var g = el('div', 'grid');
        [[7, '1 week'], [14, '2 weeks'], [28, '4 weeks']].forEach(function (c) {
          g.appendChild(button('Pause all for ' + c[1], '', function () { ui.open = null; render(); act(api('POST', 'JellyDvrSchedule/Rules/PauseAll', { Until: daysAhead(c[0]) })); }));
        });
        g.appendChild(button('Resume all', 'primary', function () { ui.open = null; render(); act(api('POST', 'JellyDvrSchedule/Rules/ResumeAll', {})); }));
        pop.appendChild(g);
        pop.appendChild(button(ui.showNative ? 'Hide Jellyfin’s series cards' : 'Show Jellyfin’s series cards', '', function () {
          ui.showNative = !ui.showNative;
          write('native', ui.showNative ? '1' : '');
          ui.open = null;
          nativeCards();
          render();
        }));
        moreWrap.appendChild(pop);
      }
      bar.appendChild(moreWrap);
    }
    bar.appendChild(view);
    section.appendChild(bar);

    var rules = data.Rules || [];
    section.appendChild(grouped ? groupedSummary(rules) : filterChips(rules));
    var status = el('div', 'jdvrR-note');
    status.id = SECTION_ID + 'Status';
    status.textContent = ui.status;
    section.appendChild(status);
    if (ui.open === 'new') {
      section.appendChild(favouriteForm(null, function (body) {
        ui.open = null;
        render();
        act(api('POST', 'JellyDvrSchedule/Favourites', body));
      }));
    }
    if (grouped) {
      renderGrouped(section, rules);
    } else {
      renderCards(section, rules);
    }
  }

  function filterChips(rules) {
    var chips = el('div', 'jdvrR-chips');
    FILTERS.forEach(function (f) {
      var n = rules.filter(f[2]).length;
      if (f[0] !== 'all' && !n && ui.filter !== f[0]) {
        return;
      }
      var c = el('button', 'jdvrR-chip' + (ui.filter === f[0] ? ' on' : ''), esc(f[1]) + '<b>' + n + '</b>');
      c.type = 'button';
      c.addEventListener('click', function () { ui.filter = f[0]; render(); });
      chips.appendChild(c);
    });
    return chips;
  }

  function renderCards(section, rules) {
    var ranked = ui.sort === 'priority';
    if (ranked && data.CanManage) {
      section.appendChild(el('div', 'jdvrR-note', 'Priority order: drag a row, or use ↑ ↓. When more wants to record than there are tuners, the higher rule records and the lower one moves to another airing or is skipped.'));
    }
    var filter = FILTERS.filter(function (f) { return f[0] === ui.filter; })[0][2];
    var list = sorted(rules).filter(filter).filter(matches);
    var box = el('div', 'jdvrR-list');
    if (!list.length) {
      box.appendChild(el('div', 'jdvrR-empty', rules.length ? 'No rule matches.' : 'No recording rules yet. Record a series from the guide, or add a favourite with + New favourite.'));
    }
    list.forEach(function (r, i) {
      box.appendChild(row(r, data.CanManage, ranked, i, list));
    });
    section.appendChild(box);
  }

  // ---- Grouped layout ------------------------------------------------------------------------

  // Airings for the day strip (the next two weeks), worked out once per render.
  var memo = {};
  function marks(r) {
    if (!memo[r.Id]) {
      memo[r.Id] = (r.Days || []).map(function (a) { return { t: new Date(a.Start), held: !!a.Held }; });
    }
    return memo[r.Id];
  }

  var GROUPS = [
    ['week', 'Recording this week', ''],
    ['paused', 'Paused', 'Held episodes are scheduled again when the pause ends.'],
    ['later', 'Recording later', 'The next airing is more than a week away.'],
    ['wait', 'Waiting for the guide', 'No airings in the guide yet. They record as soon as some appear.'],
    ['none', 'Nothing to record', 'In the guide, but every airing is a repeat or already in the library.']
  ];

  function groupOf(r) {
    if (r.State === 'paused' || r.State === 'off') {
      return 'paused';
    }
    if (r.State === 'recording') {
      return marks(r).some(function (m) { return !m.held && dayIndex(m.t, 7) >= 0; }) ? 'week' : 'later';
    }
    return r.State === 'not-in-guide' ? 'wait' : 'none';
  }

  function timeOf(t) {
    return FMT.time.format(new Date(t));
  }

  // Grouped: "Today 7:00 PM", "Wed 7:00 PM", "Mon, Nov 9 7:00 PM".
  function shortWhen(t) {
    var k = dayIndex(t, 7);
    var d = new Date(t);
    var name = k === 0 ? 'Today' : k === 1 ? 'Tomorrow' : k > 1 ? FMT.weekday.format(d) : FMT.date.format(d);
    return name + ' ' + timeOf(t);
  }

  function strip(r, n, big) {
    var per = [];
    for (var i = 0; i < n; i++) {
      per.push([]);
    }
    marks(r).forEach(function (m) { var k = dayIndex(m.t, n); if (k >= 0) { per[k].push(m); } });
    var wait = r.State === 'not-in-guide';
    return '<div class="jdvrB-strip' + (big ? ' big' : '') + (wait ? ' wait' : '') + '" style="grid-template-columns:repeat(' + n + ',1fr)">' +
      per.map(function (ms, k) {
        var d = dayStart(k);
        var dots = ms.slice(0, 3).map(function (m) { return '<span class="jdvrB-dot' + (m.held ? ' held' : '') + '"></span>'; }).join('') +
          (ms.length > 3 ? '<span class="more">+' + (ms.length - 3) + '</span>' : '');
        var title = FMT.date.format(d) +
          (ms.length ? ': ' + ms.map(function (m) { return timeOf(m.t) + (m.held ? ' (on hold)' : ''); }).join(', ') : '');
        return '<div class="jdvrB-cell' + (k === 0 ? ' today' : '') + '" title="' + esc(title) + '">' +
          (big ? '<span>' + esc(FMT.narrow.format(d) + ' ' + d.getDate()) + '</span><span class="ds">' + dots + '</span>' : dots) +
          '</div>';
      }).join('') + '</div>';
  }

  function rowStatus(r) {
    var next = nextAiring(r);
    var lib = r.Recorded ? ' · ' + r.Recorded + ' recorded' : '';
    if (r.State === 'paused') {
      return '<div class="jdvrB-st held">❚❚ ' + esc(stateLabel(r)) + (r.Held ? ' · ' + r.Held + ' on hold' : '') + '</div>';
    }
    if (r.State === 'off') {
      return '<div class="jdvrB-st held">' + esc(stateLabel(r)) + '</div>';
    }
    if (r.State === 'not-in-guide') {
      return '<div class="jdvrB-st">No airings in the guide' + lib + '</div>';
    }
    if (r.State === 'nothing-to-record') {
      return '<div class="jdvrB-st">Only repeats or ones you have' + lib + '</div>';
    }
    if (!next) {
      return '<div class="jdvrB-st">—</div>';
    }
    return '<div class="jdvrB-st">' + (next.Now ? '<b style="color:var(--r-now)">● Recording now</b>' : esc(shortWhen(next.Start))) +
      ' · ' + esc(next.Channel) + (r.Upcoming > 1 ? ' · ' + r.Upcoming + ' coming up' : '') + '</div>';
  }

  function thumb(r, cls) {
    var src = image(r);
    return '<div class="' + cls + '"' + (src ? ' style="background-image:url(&quot;' + esc(src) + '&quot;)"' : ' style="background:linear-gradient(135deg,#2b3a52,#121a26)"') + '></div>';
  }

  function nameHtml(r) {
    return (r.Kind === 'favourite' ? '<span class="jdvrB-star">★</span> ' : '') + esc(r.Name);
  }

  function nextTime(r) {
    var m = marks(r).filter(function (x) { return !x.held; })[0];
    return m ? m.t.getTime() : Infinity;
  }

  function groupedList(rules) {
    var box = el('div', 'jdvrB-list');
    var dayHead = '<div class="jdvrB-days">' + [0, 1, 2, 3, 4, 5, 6].map(function (k) {
      return '<span class="' + (k === 0 ? 'today' : '') + '">' + esc(FMT.narrow.format(dayStart(k))) + '</span>';
    }).join('') + '</div>';
    GROUPS.forEach(function (g) {
      var rs = rules.filter(function (r) { return groupOf(r) === g[0] && matches(r); });
      if (!rs.length) {
        return;
      }
      rs.sort(g[0] === 'week' || g[0] === 'later'
        ? function (a, b) { return (nextTime(a) - nextTime(b)) || a.Name.localeCompare(b.Name); }
        : function (a, b) { return a.Name.localeCompare(b.Name); });
      var quiet = g[0] === 'wait' || g[0] === 'none';
      var closed = ui.closed.indexOf(g[0]) >= 0 && !ui.search.trim();
      var grp = el('div', 'jdvrB-group' + (closed ? ' closed' : ''));
      var head = el('div', 'jdvrB-ghead', '<span class="t"><span class="chev">▾</span>' + esc(g[1]) + ' <span class="n">' + rs.length + '</span></span>' + (quiet ? '' : dayHead));
      head.tabIndex = 0;
      head.setAttribute('role', 'button');
      head.setAttribute('aria-expanded', String(!closed));
      head.addEventListener('click', function () {
        var at = ui.closed.indexOf(g[0]);
        if (at >= 0) {
          ui.closed.splice(at, 1);
        } else {
          ui.closed.push(g[0]);
        }
        write('closed', ui.closed.join(','));
        render();
      });
      grp.appendChild(head);
      if (g[2]) {
        grp.appendChild(el('div', 'jdvrB-gsub', esc(g[2])));
      }
      var rows = el('div', 'jdvrB-rows');
      rs.forEach(function (r) {
        var row = el('div', 'jdvrB-row' + (r.Id === ui.sel ? ' sel' : ''),
          thumb(r, 'jdvrB-thumb') +
          '<div style="min-width:0"><div class="jdvrB-nm">' + nameHtml(r) + '</div>' + rowStatus(r) + '</div>' +
          (quiet ? '<div></div>' : strip(r, 7)));
        row.tabIndex = 0;
        row.setAttribute('data-rule', r.Id);
        row.addEventListener('click', function () {
          ui.sel = r.Id;
          ui.open = null;
          ui.armed = null;
          render();
          if (window.innerWidth <= 900) {
            var d = document.querySelector('#' + SECTION_ID + ' .jdvrB-detail');
            if (d) {
              d.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }
          }
        });
        rows.appendChild(row);
      });
      grp.appendChild(rows);
      box.appendChild(grp);
    });
    if (!box.children.length) {
      box.appendChild(el('div', 'jdvrB-empty', rules.length ? 'No rule matches.' : 'No recording rules yet. Record a series from the guide, or add a favourite with + Favourite.'));
    } else {
      box.appendChild(el('div', 'jdvrB-legend', '<span><span class="jdvrB-dot"></span>records</span><span><span class="jdvrB-dot held"></span>on hold</span><span>Point at a day for the times</span>'));
    }
    return box;
  }

  // Priorities, in the Grouped layout: one ranked list, drag or ↑ ↓.
  function priorityList(rules) {
    var list = rules.slice().sort(function (a, b) { return a.Priority - b.Priority; });
    var grp = el('div', 'jdvrB-group');
    grp.appendChild(el('div', 'jdvrB-gsub', 'When more wants to record than there are tuners, the higher rule records and the lower one moves to another airing or is skipped. Drag a row, or use ↑ ↓.'));
    grp.firstChild.style.paddingTop = '.8em';
    list.forEach(function (r, index) {
      var row = el('div', 'jdvrB-prow', '<span class="rk">' + (index + 1) + '</span>' + thumb(r, 'jdvrB-thumb') +
        '<div style="min-width:0"><div class="jdvrB-nm">' + nameHtml(r) + '</div>' + rowStatus(r) + '</div>');
      row.appendChild(sortable(row, r, index, list, 'div'));
      grp.appendChild(row);
    });
    var box = el('div', 'jdvrB-list');
    box.appendChild(grp);
    return box;
  }

  function groupedDetail(r, rules, canManage) {
    var box = el('div', 'jdvrB-detail');
    box.setAttribute('aria-live', 'polite');
    if (!r) {
      box.appendChild(el('div', 'jdvrB-empty', 'Choose a rule on the left.'));
      return box;
    }
    var fav = r.Kind === 'favourite';
    var pill, why;
    switch (r.State) {
      case 'paused':
        pill = '<span class="jdvrB-pill paused">❚❚ Paused</span>';
        why = r.UntilPremiere ? 'Comes back when the next season starts.' : r.Until ? 'Comes back by itself on ' + day(r.Until) + '.' : 'Comes back when you resume it.';
        break;
      case 'off':
        pill = '<span class="jdvrB-pill paused">Off</span>';
        why = r.Until ? 'Switches on by itself on ' + day(r.Until) + '.' : 'Records nothing until switched on.';
        break;
      case 'recording':
        pill = '<span class="jdvrB-pill recording">● Recording</span>';
        why = r.Upcoming + ' airing' + (r.Upcoming === 1 ? '' : 's') + ' scheduled.';
        break;
      case 'not-in-guide':
        pill = '<span class="jdvrB-pill quiet">Waiting for the guide</span>';
        why = 'Nothing in the guide for this title yet.';
        break;
      default:
        pill = '<span class="jdvrB-pill quiet">Nothing to record</span>';
        why = 'Every airing in the guide is a repeat or already in the library.';
    }
    box.appendChild(el('div', 'jdvrB-dhead', thumb(r, 'art') +
      '<div><h3>' + nameHtml(r) + '</h3><div class="k">' + (fav ? 'Recording favourite' : 'Series recording') + ' · ' + esc(r.Details) + '</div>' +
      '<div class="jdvrB-state">' + pill + '<span class="why">' + esc(why) + '</span></div></div>'));
    box.appendChild(el('div', 'jdvrB-sect', '<h4>Next two weeks</h4>' + strip(r, 14, true)));
    box.appendChild(el('div', 'jdvrB-facts',
      '<div class="jdvrB-fact"><div class="v">' + (r.Priority || '—') + (r.Priority ? '<small> of ' + rules.length + '</small>' : '') + '</div><div class="l">Priority</div></div>' +
      '<div class="jdvrB-fact"><div class="v">' + (r.Recorded || 0) + '</div><div class="l">Recorded</div></div>' +
      '<div class="jdvrB-fact"><div class="v">' + (r.Unwatched || 0) + '</div><div class="l">Unwatched</div></div>'));
    var airs = (r.Next || []).map(function (a) {
      return '<div class="jdvrB-air' + (a.Held ? ' held' : '') + (a.Now ? ' now' : '') + '"><span class="w">' + (a.Now ? '● Recording now' : esc(shortWhen(a.Start))) + '</span>' +
        '<span class="e">' + esc(a.Episode || r.Name) + '</span><span class="tag">' + esc(a.Held ? 'on hold' : a.Channel) + '</span></div>';
    }).join('');
    var more = r.Upcoming + r.Held - (r.Next || []).length;
    box.appendChild(el('div', 'jdvrB-sect', '<h4>Airings</h4>' + (airs || '<div class="jdvrB-empty" style="padding:0">No airings in the guide.</div>') +
      (more > 0 ? '<div class="jdvrB-st" style="padding-top:.4em">and ' + more + ' more</div>' : '')));

    if (ui.open === 'pause:' + r.Id) {
      box.appendChild(pausePop(r));
    }
    if (ui.open === 'edit:' + r.Id && favDetails[r.Id]) {
      box.appendChild(editForm(r));
    }
    var actions = ruleActions(r, canManage, true);
    box.appendChild(actions);
    return box;
  }

  function groupedSummary(rules) {
    var c = function (id) { return rules.filter(function (r) { return groupOf(r) === id; }).length; };
    return el('div', 'jdvrB-sum',
      '<i>' + c('week') + '</i> recording this week · <i>' + c('paused') + '</i> paused · <i>' + c('later') + '</i> later · <i>' +
      c('wait') + '</i> waiting for the guide · <i>' + c('none') + '</i> nothing to record');
  }

  function renderGrouped(section, rules) {
    var byId = {};
    rules.forEach(function (r) { byId[r.Id] = r; });
    if (!ui.sel || !byId[ui.sel]) {
      // First choice: the next one to record, else the first rule shown.
      var week = rules.filter(function (r) { return groupOf(r) === 'week'; }).sort(function (a, b) { return nextTime(a) - nextTime(b); });
      ui.sel = (week[0] || rules.slice().sort(function (a, b) { return a.Name.localeCompare(b.Name); })[0] || {}).Id || null;
    }
    var main = el('div', 'jdvrB');
    main.appendChild(ui.prio && data.CanManage ? priorityList(rules) : groupedList(rules));
    main.appendChild(groupedDetail(byId[ui.sel], rules, data.CanManage));
    section.appendChild(main);
  }

  // Jellyfin's own series cards: hidden while the rules list shows, unless asked for.
  function nativeCards() {
    var h = host();
    if (!h) {
      return;
    }
    [].slice.call(h.children).forEach(function (c) {
      if (c.id === SECTION_ID || c.tagName === 'STYLE') {
        return;
      }
      c.style.display = ui.showNative ? '' : 'none';
    });
  }

  function ensure() {
    var existing = document.getElementById(SECTION_ID);
    if (!onSeriesTab()) {
      if (existing) {
        existing.parentNode.removeChild(existing);
      }
      return;
    }
    var h = host();
    if (!h) {
      return;
    }
    if (!document.getElementById(SECTION_ID + 'Css')) {
      var st = document.createElement('style');
      st.id = SECTION_ID + 'Css';
      st.textContent = CSS + BCSS;
      document.head.appendChild(st);
    }
    if (existing && h.contains(existing)) {
      nativeCards();
      return;
    }
    if (existing) {
      existing.parentNode.removeChild(existing);
    }
    var section = el('div', 'verticalSection padded-left padded-right');
    section.id = SECTION_ID;
    section.innerHTML = '<div class="jdvrR-note">Loading recording rules…</div>';
    h.insertBefore(section, h.firstChild);
    nativeCards();
    load(false).then(render, function () {
      // Plugin too old (no Rules list) or no access: leave Jellyfin's page as it is.
      section.parentNode && section.parentNode.removeChild(section);
      ui.showNative = true;
      nativeCards();
    });
  }

  document.addEventListener('click', function () {
    if (ui.open === 'more' || (ui.open && ui.open.indexOf('pause:') === 0)) {
      ui.open = null;
      render();
    }
  });

  var pending = false;
  function check() {
    if (pending) {
      return;
    }
    pending = true;
    setTimeout(function () {
      pending = false;
      if (window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId()) {
        ensure();
      }
    }, 120);
  }

  new MutationObserver(check).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', check);
  // Keep next airings and states fresh while the page is open.
  setInterval(function () {
    if (onSeriesTab() && document.getElementById(SECTION_ID) && !ui.open && !document.hidden) {
      load(true).then(render, function () {});
    }
  }, 60000);
  check();
})();
