(function () {
  var btn = document.querySelector('.menu'), side = document.getElementById('side');
  if (btn) btn.addEventListener('click', function () { var o = side.classList.toggle('open'); btn.setAttribute('aria-expanded', o); });
  var q = document.getElementById('q'), box = document.getElementById('results'), idx = null, cur = -1;
  function load() { if (idx) return Promise.resolve(idx); return fetch('search-index.json').then(function (r) { return r.json(); }).then(function (d) { idx = d; return d; }); }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return {'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;'}[c]; }); }
  function run() {
    var terms = q.value.toLowerCase().split(/\s+/).filter(Boolean);
    if (!terms.length) { box.hidden = true; return; }
    load().then(function (d) {
      var hits = [];
      d.forEach(function (e) {
        var t = e.t.toLowerCase(), b = (e.s + ' ' + e.x).toLowerCase(), score = 0;
        for (var i = 0; i < terms.length; i++) {
          if (t.indexOf(terms[i]) >= 0) score += 10; else if (b.indexOf(terms[i]) >= 0) score += 1; else return;
        }
        hits.push([score + (e.u.indexOf('#') < 0 ? 3 : 0), e]);
      });
      hits.sort(function (a, b) { return b[0] - a[0]; });
      box.innerHTML = hits.slice(0, 8).map(function (h) { return '<li><a href="' + h[1].u + '">' + esc(h[1].t) + (h[1].s ? '<small>' + esc(h[1].s) + '</small>' : '') + '</a></li>'; }).join('') || '<li><a>No matches</a></li>';
      box.hidden = false; cur = -1;
    });
  }
  if (q) {
    q.addEventListener('input', run);
    q.addEventListener('keydown', function (e) {
      var items = box.querySelectorAll('a');
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); cur = (cur + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length; items.forEach(function (a, i) { a.classList.toggle('on', i === cur); }); }
      else if (e.key === 'Enter' && items[Math.max(cur, 0)] && items[Math.max(cur, 0)].href) { location.href = items[Math.max(cur, 0)].href; }
      else if (e.key === 'Escape') { box.hidden = true; q.blur(); }
    });
    document.addEventListener('click', function (e) { if (!e.target.closest('.search')) box.hidden = true; });
    document.addEventListener('keydown', function (e) { if (e.key === '/' && document.activeElement !== q && !/INPUT|TEXTAREA/.test(document.activeElement.tagName)) { e.preventDefault(); q.focus(); } });
  }
})();
