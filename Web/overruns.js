/*
 * JellyDVR Schedule — sports overruns in jellyfin-web's Live TV guide.
 *
 * Marks, from GET /JellyDvrSchedule/Overruns/Guide (polled every 60 s while a guide is on screen):
 *   - a game on now: "LIVE · 4th 2:10" (and the score, if the setting allows) under its title;
 *     running late: "· ends ≈ 10:42" and a striped strip along the top of the row from its
 *     listed end to the estimated end;
 *   - a recording given extra time: "+22" beside the title (dashed "≈ +22" while an estimate);
 *   - with "mark everyone": a thin amber bar under each show a late game pushes back, where it
 *     will really air;
 *   - the programme dialog of a marked recording: why, by how much, and (setting on, user may
 *     manage recordings) "Keep the normal end" / "Add 15 more minutes".
 *
 * Loaded by tuners.js (itself loaded by Episode History's guidemenu.js, already in index.html).
 * VERSION-SENSITIVE: written against jellyfin-web 12.x. Guide cells are absolutely positioned
 * `.programCell[data-id]` with percentage left/width inside a relative `.channelPrograms` row;
 * the programme dialog is `.recordingDialog.opened` holding `.recordingFields`.
 */
(function () {
  'use strict';

  if (window.__jdvrOverruns) {
    return;
  }
  window.__jdvrOverruns = true;

  var POLL_MS = 60000;
  var data = null;
  var fetchedAt = 0;
  var fetching = false;
  var lastProgramId = null;
  var applyTimer = null;

  function api() {
    return window.ApiClient;
  }

  function hm(t) {
    return new Date(t).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  }

  function esc(s) {
    var d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  }

  function addStyle() {
    if (document.getElementById('jdvrOverrunsStyle')) {
      return;
    }
    var st = document.createElement('style');
    st.id = 'jdvrOverrunsStyle';
    st.textContent =
      '.jdvrO-live{color:#ff7a70;font-weight:700;font-size:.85em;margin-right:.4em;white-space:nowrap}' +
      '.jdvrO-live.final{color:#9aa5b1}' +
      '.jdvrO-chip{display:inline-block;font-size:.72em;font-weight:700;padding:0 .4em;border-radius:3px;margin-left:.4em;vertical-align:.1em;line-height:1.5;background:#f5b544;color:#1b1404}' +
      '.jdvrO-chip.est{background:transparent;color:#f5b544;border:1px dashed #f5b544}' +
      '.jdvrO-chip.kept{background:transparent;color:#9aa5b1;border:1px solid #5a6470}' +
      '.jdvrO-chip.follow{background:#12394a;color:#7fd3f2}' +
      '.jdvrO-over{position:absolute;top:0;height:5px;z-index:3;pointer-events:none;border-radius:0 2px 2px 0;' +
      'background:repeating-linear-gradient(135deg,#f5b544 0 4px,rgba(245,181,68,.25) 4px 8px)}' +
      '.jdvrO-shift{position:absolute;bottom:2px;height:3px;z-index:3;pointer-events:none;background:#f5b544;opacity:.8;border-radius:2px}' +
      '.jdvrO-band{border-left:4px solid #f5b544;background:rgba(245,181,68,.1);border-radius:4px;padding:.7em .9em;margin:.2em 0 1em;line-height:1.5}' +
      '.jdvrO-band b{color:#f5b544}' +
      '.jdvrO-band .jdvrO-btns{display:flex;flex-wrap:wrap;gap:.5em;margin-top:.6em}' +
      '.jdvrO-band button{margin:0;font-size:.9em}' +
      '.jdvrO-band .jdvrO-msg{color:#ff8f86;margin-top:.4em}';
    document.head.appendChild(st);
  }

  // ---- fetching ---------------------------------------------------------------------------

  function guideOnScreen() {
    return !!document.querySelector('.programCell[data-id]');
  }

  function load(force) {
    var a = api();
    if (!a || fetching || (!force && Date.now() - fetchedAt < POLL_MS)) {
      return;
    }
    fetching = true;
    a.ajax({ type: 'GET', url: a.getUrl('JellyDvrSchedule/Overruns/Guide'), dataType: 'json' })
      .then(function (d) {
        data = d;
        fetchedAt = Date.now();
        apply();
        refreshDialog();
        observer.takeRecords();
      }, function () {
        fetchedAt = Date.now(); // not installed / no access: try again at the next poll
      })
      .then(function () {
        fetching = false;
      });
  }

  // ---- the guide --------------------------------------------------------------------------

  function cellOf(programId) {
    return document.querySelector('.programCell[data-id="' + programId + '"]');
  }

  // Percent of the row per minute, from the cell's own left/width and its listed length.
  function geometry(cell, startUtc, endUtc) {
    var left = parseFloat(cell.style.left);
    var width = parseFloat(cell.style.width);
    var minutes = (new Date(endUtc) - new Date(startUtc)) / 60000;
    if (isNaN(left) || isNaN(width) || minutes <= 0) {
      return null;
    }
    return { left: left, width: width, perMinute: width / minutes };
  }

  function clear() {
    [].slice.call(document.querySelectorAll('.jdvrO-live,.jdvrO-chip,.jdvrO-over,.jdvrO-shift')).forEach(function (el) {
      el.remove();
    });
  }

  function secondary(cell) {
    var info = cell.querySelector('.guideProgramSecondaryInfo');
    if (!info) {
      info = document.createElement('div');
      info.className = 'guideProgramSecondaryInfo';
      cell.appendChild(info);
    }
    return info;
  }

  function chip(cell, text, cls, title) {
    var name = cell.querySelector('.guideProgramNameText') || cell.querySelector('.guideProgramName') || cell;
    var c = document.createElement('span');
    c.className = 'jdvrO-chip' + (cls ? ' ' + cls : '');
    c.textContent = text;
    c.title = title || '';
    name.appendChild(c);
  }

  function apply() {
    clear();
    if (!data || data.Marks === 'off') {
      return;
    }
    var games = {};
    (data.Games || []).forEach(function (g) {
      games[g.ProgramId] = g;
      var cell = cellOf(g.ProgramId);
      if (!cell) {
        return;
      }
      var text = g.Final ? 'Final' : 'LIVE · ' + g.Clock;
      if (g.Score) {
        text += ' · ' + g.Score;
      }
      if (g.Late && !g.Final && g.EstimatedEndUtc) {
        text += ' · ends ≈ ' + hm(g.EstimatedEndUtc);
      }
      var live = document.createElement('span');
      live.className = 'jdvrO-live' + (g.Final ? ' final' : '');
      live.textContent = text;
      var info = secondary(cell);
      info.insertBefore(live, info.firstChild);

      if (g.Late && !g.Final && g.EstimatedEndUtc) {
        var geo = geometry(cell, g.ListedStartUtc, g.ListedEndUtc);
        if (geo) {
          var over = document.createElement('div');
          over.className = 'jdvrO-over';
          over.style.left = (geo.left + geo.width) + '%';
          over.style.width = Math.max(0.2, (new Date(g.EstimatedEndUtc) - new Date(g.ListedEndUtc)) / 60000 * geo.perMinute) + '%';
          over.title = 'Running late: expected to end about ' + hm(g.EstimatedEndUtc);
          cell.parentNode.appendChild(over);
        }
      }
    });

    (data.Recordings || []).forEach(function (r) {
      var cell = cellOf(r.ProgramId);
      if (!cell) {
        return;
      }
      var total = r.ExtraMinutes + r.UserExtraMinutes;
      if (r.Role === 'followup') {
        chip(cell, 'Follow-up', 'follow', 'Records the rest of ' + r.GameTitle + '; joined onto the game\'s recording afterwards');
      } else if (r.Kept) {
        chip(cell, r.UserExtraMinutes ? '+' + r.UserExtraMinutes : 'normal end', 'kept', 'Kept to its normal end by hand');
      } else if (r.Decided && total > 0) {
        chip(cell, '+' + total, '', 'Recording end moved ' + total + ' min later: ' + r.GameTitle + ' ran late');
      } else if (r.EstimateMinutes > 0 || r.UserExtraMinutes > 0) {
        if (r.UserExtraMinutes > 0 && !r.EstimateMinutes) {
          chip(cell, '+' + r.UserExtraMinutes, '', 'Added by hand');
        } else {
          chip(cell, '≈ +' + (r.EstimateMinutes + r.UserExtraMinutes), 'est', 'Estimate so far: decided 3 minutes before it starts' + (data.Preview ? ' (preview: nothing is changed)' : ''));
        }
      }
    });

    (data.Shifted || []).forEach(function (s) {
      var cell = cellOf(s.ProgramId);
      if (!cell) {
        return;
      }
      var geo = geometry(cell, s.StartUtc, s.EndUtc);
      if (!geo) {
        return;
      }
      var bar = document.createElement('div');
      bar.className = 'jdvrO-shift';
      bar.style.left = (geo.left + s.ShiftMinutes * geo.perMinute) + '%';
      bar.style.width = geo.width + '%';
      bar.title = 'Expected to air about ' + s.ShiftMinutes + ' min late (the game before it is running late)';
      cell.parentNode.appendChild(bar);
    });
  }

  function scheduleApply() {
    if (applyTimer) {
      return;
    }
    applyTimer = setTimeout(function () {
      applyTimer = null;
      if (!guideOnScreen()) {
        return;
      }
      load(false);
      // The guide re-draws rows as it scrolls: draw the marks again, and drop the changes made
      // by drawing them so they don't count as a re-draw.
      if (data) {
        apply();
        observer.takeRecords();
      }
    }, 400);
  }

  // ---- the programme dialog -----------------------------------------------------------------

  document.addEventListener('click', function (e) {
    var cell = e.target && e.target.closest && e.target.closest('.programCell[data-id]');
    if (cell) {
      lastProgramId = cell.getAttribute('data-id');
    }
  }, true);

  function recordingFor(programId) {
    return data && (data.Recordings || []).filter(function (r) {
      return r.ProgramId === programId;
    })[0];
  }

  function gameFor(programId) {
    return data && (data.Games || []).filter(function (g) {
      return g.ProgramId === programId;
    })[0];
  }

  function bandHtml(r, g) {
    if (g && !r) {
      var t = g.Final ? 'Final' : 'LIVE · ' + g.Clock;
      return '<div><b>' + esc(t) + '</b>' + (g.Score ? ' · ' + esc(g.Score) : '') + '</div>' +
        (g.Late && !g.Final && g.EstimatedEndUtc ? '<div>Running late: expected to end about ' + esc(hm(g.EstimatedEndUtc)) + ' (listed ' + esc(hm(g.ListedEndUtc)) + ')</div>' : '');
    }
    var total = r.ExtraMinutes + r.UserExtraMinutes;
    var lines = [];
    if (r.Role === 'followup') {
      lines.push('<div><b>Follow-up recording</b> · records the rest of ' + esc(r.GameTitle) + '; joined onto the game\'s recording afterwards</div>');
    } else if (r.Role === 'game') {
      lines.push('<div><b>Game recording</b> · extra time for overtime</div>');
    } else {
      lines.push('<div><b>Pushed back by the game</b> · ' + esc(r.GameTitle) + '</div>');
    }
    if (r.GameStatus) {
      lines.push('<div>Game: ' + esc(r.GameStatus) + (r.Score ? ' · ' + esc(r.Score) : '') + '</div>');
    }
    if (r.Kept) {
      lines.push('<div>Kept to its normal end by hand' + (r.UserExtraMinutes ? ', +' + r.UserExtraMinutes + ' min added by hand' : '') + '.</div>');
    } else if (r.Decided && total > 0) {
      lines.push('<div>Records until about <b>' + esc(hm(new Date(new Date(r.EndUtc).getTime() + total * 60000))) + '</b> (+' + total + ' min' +
        (r.UserExtraMinutes ? ', ' + r.UserExtraMinutes + ' of them by hand' : '') + ').</div>');
    } else if (r.EstimateMinutes > 0) {
      lines.push('<div>So far about +' + r.EstimateMinutes + ' min; decided 3 minutes before it starts' + (data.Preview ? ' (preview: nothing is changed)' : '') + '.</div>');
    } else if (r.UserExtraMinutes > 0) {
      lines.push('<div>+' + r.UserExtraMinutes + ' min added by hand.</div>');
    }
    if (r.Recording) {
      lines.push('<div style="opacity:.75">Already recording: its end can\'t change now.</div>');
    }
    return lines.join('');
  }

  function refreshDialog() {
    var dlg = document.querySelector('.recordingDialog.opened');
    if (!dlg || !lastProgramId) {
      return;
    }
    var r = recordingFor(lastProgramId);
    var g = gameFor(lastProgramId);
    var band = dlg.querySelector('.jdvrO-band');
    if (!r && !g) {
      if (band) {
        band.remove();
      }
      return;
    }
    if (!band) {
      var anchor = dlg.querySelector('.recordingFields');
      if (!anchor) {
        return;
      }
      band = document.createElement('div');
      band.className = 'jdvrO-band';
      anchor.parentNode.insertBefore(band, anchor);
    }
    band.setAttribute('data-program', lastProgramId);
    band.innerHTML = bandHtml(r, g);
    if (r && data.Buttons && !r.Recording && r.Role !== 'followup') {
      var btns = document.createElement('div');
      btns.className = 'jdvrO-btns';
      btns.appendChild(button(r.Kept ? 'Follow the game again' : 'Keep the normal end', r.Kept ? 'DELETE' : 'POST', r.TimerId, 'Keep', band));
      btns.appendChild(button('Add 15 more minutes', 'POST', r.TimerId, 'More', band));
      band.appendChild(btns);
    }
  }

  function button(label, method, timerId, action, band) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'raised emby-button';
    b.textContent = label;
    b.addEventListener('click', function () {
      var a = api();
      b.disabled = true;
      a.ajax({ type: method, url: a.getUrl('JellyDvrSchedule/Overruns/Timer/' + timerId + '/' + action), dataType: 'json' })
        .then(function () {
          load(true);
        }, function (err) {
          b.disabled = false;
          var msg = band.querySelector('.jdvrO-msg') || band.appendChild(document.createElement('div'));
          msg.className = 'jdvrO-msg';
          msg.textContent = 'Couldn\'t change it' + (err && err.status === 409 ? ': it may already be recording.' : '.');
        });
    });
    return b;
  }

  // ---- watching the page --------------------------------------------------------------------

  addStyle();
  var observer = new MutationObserver(function () {
    if (guideOnScreen()) {
      scheduleApply();
    }
    var dlg = document.querySelector('.recordingDialog.opened');
    if (dlg && !dlg.querySelector('.jdvrO-band') && lastProgramId && (recordingFor(lastProgramId) || gameFor(lastProgramId))) {
      refreshDialog();
    }
  });
  observer.observe(document.body, { childList: true, subtree: true });

  setInterval(function () {
    if (guideOnScreen()) {
      load(false);
    }
  }, POLL_MS);
})();
