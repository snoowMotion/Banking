/* Budget tenable : frontend.
   Reprend la logique d'affichage du prototype (docs/reference/budget-tenable-reference.html).
   Les données vivent côté serveur : ce fichier ne garde que de l'état d'affichage. */
(function () {
  'use strict';

  /* ================= Utilitaires ================= */
  const $ = (id) => document.getElementById(id);
  const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ESC[c]);
  const sum = (a) => a.reduce((s, x) => s + x, 0);
  const pad = (n) => (n < 10 ? '0' : '') + n;
  const nf = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: 'EUR', maximumFractionDigits: 0 });
  const f2n = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const pfn = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1 });
  const f = (n) => nf.format(Math.round(n) + 0); // + 0 évite l'affichage « -0 € »
  const f2 = (n) => f2n.format(n);
  const fs = (n) => { const r = Math.round(n) + 0; return (r > 0 ? '+' : '') + nf.format(r); };
  const pc = (n) => pfn.format(n) + ' %';
  const kfmt = (v) => (Math.abs(v) >= 1000 ? (v / 1000).toLocaleString('fr-FR', { maximumFractionDigits: 1 }) + ' k€' : Math.round(v) + ' €');
  const parseAmt = (s) => { const v = parseFloat(String(s).replace(/\s/g, '').replace(',', '.')); return isFinite(v) ? v : NaN; };
  const MSH = ['janv.', 'févr.', 'mars', 'avr.', 'mai', 'juin', 'juil.', 'août', 'sept.', 'oct.', 'nov.', 'déc.'];
  const GROUPS = [
    ['variable', 'Variable mensuel'],
    ['pot', 'Pots de provisions'],
    ['fixe', 'Charges fixes'],
    ['ignore', 'Hors budget (revenus, remboursements santé…)'],
  ];

  function mshift(m, k) {
    const p = m.split('-');
    const d = new Date(+p[0], +p[1] - 1 + k, 1);
    return d.getFullYear() + '-' + pad(d.getMonth() + 1);
  }
  const parseDay = (s) => { const p = s.split('-'); return new Date(+p[0], +p[1] - 1, +p[2]); };
  const ymd = (d) => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
  const frDate = (s, withYear) => s.slice(8, 10) + '/' + s.slice(5, 7) + (withYear ? '/' + s.slice(0, 4) : '');

  /* ================= API ================= */
  let authed = false;

  class ApiError extends Error {
    constructor(message, status) { super(message); this.status = status; }
  }

  async function api(path, opt) {
    opt = opt || {};
    const init = { method: opt.method || 'GET', credentials: 'same-origin', headers: {} };
    if (opt.json !== undefined) { init.body = JSON.stringify(opt.json); init.headers['Content-Type'] = 'application/json'; }
    if (opt.body !== undefined) { init.body = opt.body; init.headers['Content-Type'] = 'application/octet-stream'; }
    let r;
    try { r = await fetch('/api' + path, init); }
    catch (e) { setSync('bad', 'Hors ligne'); throw new ApiError('Serveur injoignable.', 0); }
    if (r.status === 401 && path !== '/auth/login') { showLogin(); throw new ApiError('Connexion requise.', 401); }
    if (r.status === 204) return null;
    let data = null;
    try { data = await r.json(); } catch (e) { /* corps vide */ }
    if (!r.ok) throw new ApiError((data && data.error) || 'Erreur ' + r.status, r.status);
    return data;
  }

  /* ================= État ================= */
  let S = null;          // réglages du budget (forme du serveur)
  let CATS = [];         // catégories
  let ACCOUNTS = [];
  let TODAY = '';        // date du jour côté serveur (Europe/Paris)
  let M = '';            // période affichée (yyyy-MM)
  let SUM = null;        // synthèse de la période (serveur)
  let TX = [];           // journal de la période
  let TXTOTAL = 0;
  let TRI = [];          // boîte « À trier »
  let TRITOTAL = 0;
  let RULES = [];
  let editTx = null, pendDel = null, msgT = 0;
  let BP = '';           // période dont on affiche et modifie le budget (yyyy-MM)
  let LATER = [];        // changements déjà prévus après BP
  let PROPAGATE = false; // appliquer aussi aux périodes suivantes

  const INC = [
    { k: 'net', label: 'Ton net de base', note: 'Hors primes' },
    { k: 'partner', label: 'Part du/de la partenaire', note: 'Versée chaque mois' },
    { k: 'caf', label: 'CAF' },
  ];
  // Les catégories actives à la période du budget affiché (BP) ; CATS contient aussi celles arrêtées ou pas encore créées.
  const fixCats = () => CATS.filter((c) => c.active && c.group === 'fixe');
  const annualCats = () => CATS.filter((c) => c.active && c.annual);
  const allocCats = () => CATS.filter((c) => c.active && (c.group === 'variable' || (c.group === 'pot' && !c.annual)));
  const catById = (id) => CATS.find((c) => c.id === id);
  const catLabel = (id) => (catById(id) ? catById(id).label : id || '');

  /* ================= Enregistrement du budget ================= */
  let saveT = 0, inflight = null, dirty = false;

  function setSync(kind, text) {
    const ch = $('syncChip');
    if (!ch) return;
    ch.className = 'chip ' + kind;
    $('syncTxt').textContent = text;
  }

  function scheduleSave() {
    dirty = true;
    setSync('warn', 'Enregistrement…');
    clearTimeout(saveT);
    saveT = setTimeout(doSave, 600);
  }

  function doSave() {
    clearTimeout(saveT);
    if (inflight) { inflight = inflight.then(() => (dirty ? doSave() : null)); return inflight; }
    if (!dirty) return Promise.resolve();
    dirty = false;
    const body = {
      period: BP,
      propagate: PROPAGATE,
      settings: S,
      categories: CATS.filter((c) => c.active).map((c) => ({ id: c.id, percentage: c.percentage, amount: c.amount, accountId: c.accountId || null })),
    };
    inflight = api('/budget', { method: 'PUT', json: body })
      .then(() => {
        if (PROPAGATE && LATER.length) { LATER = []; PROPAGATE = false; renderLater(); }
        if (!dirty) setSync('ok', 'Synchronisé');
      })
      .catch((e) => { dirty = true; setSync('bad', e.status === 0 ? 'Hors ligne' : 'Non enregistré'); })
      .finally(() => { inflight = null; });
    return inflight;
  }

  const flushSave = () => (dirty || inflight ? doSave() : Promise.resolve());

  /* ================= Onglet Budget (calculs locaux, pour la simulation instantanée) ================= */
  function compute(extra) {
    const inc = S.income.net + S.income.partner + S.income.caf;
    const act = CATS.filter((c) => c.active).concat(extra || []);
    let fixedMonthly = 0, mandProv = 0;
    act.forEach((c) => {
      if (c.group === 'fixe') fixedMonthly += c.amount;
      else if (c.annual) mandProv += c.amount / 12;
    });
    const fixedTotal = fixedMonthly + mandProv;
    const env = inc - fixedTotal;
    const alloc = act.filter((c) => c.group === 'variable' || (c.group === 'pot' && !c.annual));
    const allocPct = sum(alloc.map((c) => c.percentage));
    const savePct = 100 - allocPct;
    const amt = {};
    alloc.forEach((c) => { amt[c.id] = env * c.percentage / 100; });
    const varTotal = sum(alloc.filter((c) => c.group === 'variable').map((c) => amt[c.id]));
    const provTotal = sum(alloc.filter((c) => c.group === 'pot').map((c) => amt[c.id]));
    const todayVar = sum(alloc.map((c) => c.currentAverage));
    return {
      inc, fixedMonthly, mandProv, fixedTotal, env, allocPct, savePct, amt,
      saveAmt: env * savePct / 100, varTotal, provTotal, todayVar,
      todayBal: inc - fixedTotal - todayVar, primesM: S.annualBonus / 12,
    };
  }

  function buildInputs() {
    const box = $('incFields');
    box.innerHTML = '';
    INC.forEach((x) => {
      const row = document.createElement('div');
      row.className = 'field';
      row.innerHTML = '<label for="inc-' + x.k + '">' + esc(x.label) + (x.note ? '<small>' + esc(x.note) + '</small>' : '') + '</label>' +
        '<div class="inp"><input id="inc-' + x.k + '" data-inc="' + x.k + '" type="number" inputmode="decimal" min="0" step="10"><span>&euro;</span></div>';
      box.appendChild(row);
    });
    const fb = $('fixFields'), pb = $('provFields');
    fb.innerHTML = ''; pb.innerHTML = '';
    const accOpts = '<option value="">Compte par défaut (' + esc((ACCOUNTS[0] || { label: 'Perso' }).label) + ')</option>' + ACCOUNTS.map((a) => '<option value="' + a.id + '">' + esc(a.label) + '</option>').join('');
    const add = (target, c) => {
      const row = document.createElement('div');
      row.className = 'field';
      row.innerHTML = '<div><label for="fx-' + esc(c.id) + '">' + esc(c.label) + (c.note ? '<small>' + esc(c.note) + '</small>' : '') + '</label>' +
        (c.group === 'fixe' ? '<select class="ctl acc" data-acc="' + esc(c.id) + '" aria-label="Compte de prélèvement de ' + esc(c.label) + '">' + accOpts + '</select>' : '') + '</div>' +
        '<div class="inp"><input id="fx-' + esc(c.id) + '" data-amount="' + esc(c.id) + '" type="number" inputmode="decimal" min="0" step="1"><span>' + (c.annual ? '&euro;/an' : '&euro;') + '</span></div>';
      target.appendChild(row);
    };
    fixCats().forEach((c) => add(fb, c));
    annualCats().forEach((c) => add(pb, c));
  }

  function fillInputs() {
    INC.forEach((x) => { $('inc-' + x.k).value = S.income[x.k]; });
    fixCats().concat(annualCats()).forEach((c) => {
      const inp = $('fx-' + c.id);
      inp.value = c.amount;
      const acc = document.querySelector('[data-acc="' + c.id + '"]');
      if (acc) acc.value = c.accountId ? String(c.accountId) : '';
    });
    $('primes').value = S.annualBonus;
    $('incPrimes').checked = S.includeBonus;
  }

  function buildTable() {
    const t = $('allocTbl');
    let h = '<thead><tr><th>Poste</th><th class="r">Part</th><th class="r">Par mois</th><th class="r">Rythme</th><th class="r">Aujourd&rsquo;hui</th><th class="r">Écart</th><th>Cible et aujourd&rsquo;hui</th></tr></thead><tbody>';
    let cur = '';
    const subRow = (g) => '<tr class="sub"><td>Sous-total ' + (g === 'variable' ? 'Variable mensuel' : 'Pots de provisions') + '</td><td class="n r" id="sp-' + g + '"></td><td class="n r" id="sa-' + g + '"></td><td class="n r mut" id="sr-' + g + '"></td><td class="n r mut" id="st-' + g + '"></td><td class="n r" id="sg-' + g + '"></td><td></td></tr>';
    // Variables d'abord, puis pots, quel que soit l'ordre de création : un seul bloc par type.
    allocCats().slice().sort((a, b) => (a.group === b.group ? a.order - b.order : a.group === 'variable' ? -1 : 1)).forEach((a) => {
      if (a.group !== cur) {
        if (cur) h += subRow(cur);
        cur = a.group;
        h += '<tr class="grp"><td colspan="7"><b style="background:var(--c-' + (a.group === 'variable' ? 'var' : 'prov') + ')"></b>' + (a.group === 'variable' ? 'Variable mensuel' : 'Pots de provisions') + '</td></tr>';
      }
      const color = a.group === 'variable' ? 'var' : 'prov';
      h += '<tr><td>' + esc(a.label) + (a.note ? '<span class="note">' + esc(a.note) + '</span>' : '') + '</td>' +
        '<td class="r"><label class="pct"><input type="number" inputmode="decimal" min="0" max="100" step="0.5" data-pct="' + esc(a.id) + '" aria-label="Part de ' + esc(a.label) + '"><span>%</span></label></td>' +
        '<td class="n r" data-ca="' + esc(a.id) + '"></td><td class="n r mut" data-cr="' + esc(a.id) + '"></td><td class="n r mut" data-ct="' + esc(a.id) + '"></td><td class="n r" data-cg="' + esc(a.id) + '"></td>' +
        '<td><div class="bars"><div class="c" data-bc="' + esc(a.id) + '" style="background:var(--c-' + color + ')"></div><div class="t" data-bt="' + esc(a.id) + '"></div></div></td></tr>';
    });
    if (cur) h += subRow(cur);
    h += '<tr class="grp"><td colspan="7"><b style="background:var(--c-save)"></b>Épargne</td></tr>' +
      '<tr><td><strong>Épargne courante</strong><span class="note">Ce qui reste de l&rsquo;enveloppe</span></td><td class="n r" id="savePct"></td><td class="n r" id="saveAmt"></td><td class="n r mut" id="saveRate"></td><td class="n r mut" id="saveToday"></td><td class="n r" id="saveGap"></td><td><div class="bars"><div class="c" id="saveBar" style="background:var(--c-save)"></div></div></td></tr>';
    h += '<tr class="tot"><td>Total de l&rsquo;enveloppe</td><td class="n r" id="totPct"></td><td class="n r" id="totAmt"></td><td></td><td class="n r mut" id="totToday"></td><td></td><td></td></tr></tbody>';
    t.innerHTML = h;
  }

  function donut(c) {
    const parts = [
      { n: 'Charges fixes', v: Math.max(0, c.fixedMonthly), raw: c.fixedMonthly, col: '--c-fixe' },
      { n: 'Variable mensuel', v: Math.max(0, c.varTotal), raw: c.varTotal, col: '--c-var' },
      { n: 'Provisions (LDDS)', v: Math.max(0, c.provTotal + c.mandProv), raw: c.provTotal + c.mandProv, col: '--c-prov' },
      { n: 'Épargne', v: Math.max(0, c.saveAmt), raw: c.saveAmt, col: '--c-save' },
    ];
    const total = sum(parts.map((p) => p.v)) || 1;
    const R = 70, C = 2 * Math.PI * R;
    let off = 0;
    let s = '<svg viewBox="0 0 200 200" role="img" aria-label="Répartition mensuelle des revenus"><circle cx="100" cy="100" r="' + R + '" fill="none" style="stroke:var(--line)" stroke-width="26"/>';
    parts.forEach((p) => {
      if (p.v <= 0) return;
      const len = C * p.v / total, gap = Math.min(2, len * 0.3);
      s += '<circle cx="100" cy="100" r="' + R + '" fill="none" style="stroke:var(' + p.col + ')" stroke-width="26" stroke-dasharray="' + (len - gap) + ' ' + (C - len + gap) + '" stroke-dashoffset="' + (-off) + '" transform="rotate(-90 100 100)"><title>' + p.n + ' : ' + f(p.v) + '</title></circle>';
      off += len;
    });
    s += '<text x="100" y="96" text-anchor="middle" style="fill:var(--fg);font-family:var(--font-num);font-size:20px;font-weight:600">' + f(c.inc) + '</text>' +
      '<text x="100" y="116" text-anchor="middle" style="fill:var(--muted);font-family:var(--font-ui);font-size:11px">par mois</text></svg>';
    $('donut').innerHTML = s;
    $('legend').innerHTML = parts.map((p) => {
      const share = c.inc > 0 ? p.raw / c.inc * 100 : 0;
      return '<li><b class="sw" style="background:var(' + p.col + ')"></b><span>' + p.n + '<span class="p">' + pc(share) + '</span></span><span class="a' + (p.raw < 0 ? ' neg' : '') + '">' + f(p.raw) + '</span></li>';
    }).join('');
  }

  function niceStep(x) {
    if (x <= 0) return 1;
    const p = Math.pow(10, Math.floor(Math.log(x) / Math.LN10)), n = x / p;
    return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p;
  }
  function niceTicks(mx) { const st = niceStep(mx / 4), top = Math.ceil(mx / st) * st; return { st: st, top: top || 1 }; }

  function line(c) {
    const host = $('line');
    const W = Math.max(300, host.clientWidth || 600), H = 250, m = { l: 58, r: 20, t: 16, b: 32 };
    const pr = S.includeBonus ? c.primesM : 0;
    const target = [], now = [];
    for (let i = 0; i <= 24; i++) { target.push(i * (c.saveAmt + pr)); now.push(i * (c.todayBal + pr)); }
    const all = target.concat(now);
    const mn = Math.min(0, Math.min.apply(null, all));
    let mx = Math.max(0, Math.max.apply(null, all));
    if (mx === mn) mx = mn + 1;
    const step = niceStep((mx - mn) / 4), y0 = Math.floor(mn / step) * step, y1 = Math.ceil(mx / step) * step;
    const X = (i) => m.l + (W - m.l - m.r) * i / 24;
    const Y = (v) => m.t + (H - m.t - m.b) * (1 - (v - y0) / (y1 - y0));
    let s = '<svg width="' + W + '" height="' + H + '" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Épargne cumulée sur 24 mois">';
    for (let v = y0; v <= y1 + step / 2; v += step) {
      const y = Y(v), zero = Math.abs(v) < step / 1000;
      s += '<line x1="' + m.l + '" x2="' + (W - m.r) + '" y1="' + y + '" y2="' + y + '" style="stroke:var(' + (zero ? '--line-strong' : '--line') + ')" stroke-width="1"/>' +
        '<text x="' + (m.l - 8) + '" y="' + (y + 4) + '" text-anchor="end" style="fill:var(--muted);font-family:var(--font-num);font-size:11px">' + kfmt(v) + '</text>';
    }
    [0, 6, 12, 18, 24].forEach((i) => {
      s += '<text x="' + X(i) + '" y="' + (H - 10) + '" text-anchor="middle" style="fill:var(--muted);font-family:var(--font-ui);font-size:11px">' + (i === 0 ? 'départ' : i + ' mois') + '</text>';
    });
    const path = (a) => a.map((v, i) => (i ? 'L' : 'M') + X(i).toFixed(1) + ' ' + Y(v).toFixed(1)).join('');
    s += '<path d="' + path(now) + '" fill="none" style="stroke:var(--muted)" stroke-width="2" stroke-dasharray="5 4"/>';
    s += '<path d="' + path(target) + '" fill="none" style="stroke:var(--c-save)" stroke-width="2.5"/>';
    const tEnd = target[24], nEnd = now[24], tUp = tEnd >= nEnd;
    s += '<circle cx="' + X(24) + '" cy="' + Y(tEnd) + '" r="4" style="fill:var(--c-save)"/><circle cx="' + X(24) + '" cy="' + Y(nEnd) + '" r="4" style="fill:var(--muted)"/>';
    s += '<text x="' + (X(24) - 8) + '" y="' + (Y(tEnd) + (tUp ? -10 : 18)) + '" text-anchor="end" style="fill:var(--c-save);font-family:var(--font-num);font-size:12px;font-weight:600">' + fs(tEnd) + '</text>';
    s += '<text x="' + (X(24) - 8) + '" y="' + (Y(nEnd) + (tUp ? 18 : -10)) + '" text-anchor="end" style="fill:var(--muted);font-family:var(--font-num);font-size:12px;font-weight:600">' + fs(nEnd) + '</text></svg>';
    host.innerHTML = s;
    $('projKey').innerHTML = '<span><i style="border-color:var(--c-save)"></i>Avec ce budget</span><span><i class="dash" style="border-color:var(--muted)"></i>Sans changement</span>';
    $('projSub').textContent = S.includeBonus ? 'Épargne courante plus primes versées en épargne (' + f(c.primesM) + ' par mois en moyenne).' : 'Épargne courante seule, sans les primes.';
  }

  function renderBudget() {
    const c = compute();
    $('incTotal').textContent = f(c.inc);
    $('fixTotal').textContent = f(c.fixedMonthly);
    $('provMandTotal').textContent = f(c.mandProv);

    const chip = $('chip');
    chip.className = 'chip';
    let txt;
    if (c.env <= 0 || c.savePct < 0) { chip.classList.add('bad'); txt = 'Déficitaire'; }
    else if (c.savePct < 5) { chip.classList.add('warn'); txt = 'Juste'; }
    else { chip.classList.add('ok'); txt = 'Tenable'; }
    $('chipTxt').textContent = txt;

    const varShare = c.inc > 0 ? c.varTotal / c.inc * 100 : 0;
    $('kpis').innerHTML =
      '<div class="kpi"><div class="l"><b style="background:var(--accent)"></b>Revenus</div><div class="v">' + f(c.inc) + '</div><div class="s">par mois, hors primes</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-fixe)"></b>Charges fixes</div><div class="v">' + f(c.fixedMonthly) + '</div><div class="s">prélevées chaque mois &middot; ' + pc(c.inc > 0 ? c.fixedMonthly / c.inc * 100 : 0) + ' des revenus</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-prov)"></b>Provisions</div><div class="v">' + f(c.provTotal + c.mandProv) + '</div><div class="s">virées chaque mois au LDDS, dont ' + f(c.mandProv) + ' pour la taxe foncière et l&rsquo;assurance auto</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-var)"></b>Variable mensuel</div><div class="v' + (c.env < 0 ? ' neg' : '') + '">' + f(c.varTotal) + '</div><div class="s">enveloppe de ' + f(c.env) + ' &middot; ' + pc(varShare) + ' des revenus</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-save)"></b>Épargne courante</div><div class="v' + (c.saveAmt < 0 ? ' neg' : '') + '">' + f(c.saveAmt) + '</div><div class="s">' + pc(c.savePct) + ' de l&rsquo;enveloppe</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-save)"></b>Avec les primes</div><div class="v">' + f(c.saveAmt + c.primesM) + '</div><div class="s">par mois, soit ' + f(12 * (c.saveAmt + c.primesM)) + ' sur 12 mois</div></div>';

    let l = c.todayBal < 0
      ? 'Sans changement, le budget est à <strong class="neg">' + f(c.todayBal) + ' par mois</strong> hors primes. '
      : 'Sans changement, le budget dégage <strong>' + f(c.todayBal) + ' par mois</strong> hors primes. ';
    l += 'Avec cette répartition : <strong>' + f(c.saveAmt) + ' par mois</strong> d&rsquo;épargne courante, <strong>' + f(c.saveAmt + c.primesM) + '</strong> en versant les primes.';
    $('lede').innerHTML = l;

    donut(c);
    line(c);

    const alloc = allocCats();
    let maxv = 1;
    alloc.forEach((a) => { maxv = Math.max(maxv, a.currentAverage, Math.max(0, c.amt[a.id])); });
    maxv = Math.max(maxv, Math.max(0, c.saveAmt));
    alloc.forEach((a) => {
      const amt = c.amt[a.id], gap = amt - a.currentAverage;
      const q = (attr) => document.querySelector('[' + attr + '="' + a.id + '"]');
      q('data-ca').textContent = f(amt);
      q('data-cr').textContent = a.group === 'variable' ? f(amt * 12 / 52) + ' / sem.' : f(amt * 12) + ' / an';
      q('data-ct').textContent = f(a.currentAverage);
      const g = q('data-cg');
      g.textContent = fs(gap);
      g.className = 'n r ' + (gap < -0.5 ? 'pos' : gap > 0.5 ? 'neg' : 'mut');
      q('data-bc').style.width = Math.max(0, amt) / maxv * 100 + '%';
      q('data-bt').style.width = a.currentAverage / maxv * 100 + '%';
      const pin = q('data-pct');
      if (document.activeElement !== pin) pin.value = a.percentage;
    });
    ['variable', 'pot'].forEach((g) => {
      const rows = alloc.filter((a) => a.group === g);
      const pct = sum(rows.map((a) => a.percentage));
      const am = g === 'variable' ? c.varTotal : c.provTotal;
      const td = sum(rows.map((a) => a.currentAverage));
      const gp = am - td;
      $('sp-' + g).textContent = pc(pct);
      $('sa-' + g).textContent = f(am);
      $('sr-' + g).textContent = g === 'variable' ? f(am * 12 / 52) + ' / sem.' : f(am * 12) + ' / an';
      $('st-' + g).textContent = f(td);
      const e = $('sg-' + g);
      e.textContent = fs(gp);
      e.className = 'n r ' + (gp < -0.5 ? 'pos' : gp > 0.5 ? 'neg' : 'mut');
    });
    const sp = $('savePct'); sp.textContent = pc(c.savePct); sp.className = 'n r ' + (c.savePct < 0 ? 'neg' : '');
    const sa = $('saveAmt'); sa.textContent = f(c.saveAmt); sa.className = 'n r ' + (c.saveAmt < 0 ? 'neg' : '');
    $('saveRate').textContent = f(c.saveAmt * 12) + ' / an';
    $('saveToday').textContent = f(c.todayBal);
    const sg = $('saveGap'); sg.textContent = fs(c.saveAmt - c.todayBal); sg.className = 'n r ' + ((c.saveAmt - c.todayBal) > 0 ? 'pos' : 'neg');
    $('saveBar').style.width = Math.max(0, c.saveAmt) / maxv * 100 + '%';
    $('totPct').textContent = pc(c.allocPct + c.savePct);
    $('totAmt').textContent = f(c.env);
    $('totToday').textContent = f(c.todayVar + c.todayBal);

    let ph = annualCats().map((x) => '<li><strong>' + esc(x.label) + '</strong><span class="n">' + f(x.amount / 12) + ' / mois</span><small>' + f(x.amount) + ' par an' + (x.note ? ' &middot; ' + esc(x.note) : '') + '</small></li>').join('');
    ph += alloc.filter((a) => a.group === 'pot').map((a) => '<li><strong>' + esc(a.label) + '</strong><span class="n">' + f(c.amt[a.id]) + ' / mois</span><small>' + f(c.amt[a.id] * 12) + ' par an' + (a.note ? ' &middot; ' + esc(a.note) : '') + '</small></li>').join('');
    $('pots').innerHTML = ph;
    $('potsTotal').textContent = f(c.provTotal + c.mandProv);
  }

  /* ================= Onglet Suivi du mois ================= */
  const pday = () => S.startDay || 1;
  const sstr = (m) => m + '-' + pad(pday());
  function pkeyOf(d) { const k = d.slice(0, 7); return +d.slice(8, 10) >= pday() ? k : mshift(k, -1); }
  const currentKey = () => pkeyOf(TODAY);
  const defaultDate = () => (M === currentKey() ? TODAY : sstr(M));

  /** Les catégories qui existent aujourd'hui ; avec <paramref name="all"/>, les arrêtées aussi (filtre du journal). */
  function catOptionsHtml(all) {
    return GROUPS.map(([g, label]) => {
      const rows = CATS.filter((c) => c.group === g && (all || c.activeNow));
      return rows.length ? '<optgroup label="' + esc(label) + '">' + rows.map((c) => '<option value="' + esc(c.id) + '">' + esc(c.label) + (c.activeNow ? '' : ' (arrêtée)') + '</option>').join('') + '</optgroup>' : '';
    }).join('');
  }

  function buildSelects() {
    const opts = catOptionsHtml();
    $('xCat').innerHTML = opts;
    $('jCat').innerHTML = '<option value="">Toutes les catégories</option><option value="__none">À trier</option>' + catOptionsHtml(true);
    $('rCat').innerHTML = '<option value="__internal">Virement interne (ignoré du budget)</option>' + opts;
    const accs = ACCOUNTS.map((a) => '<option value="' + a.id + '">' + esc(a.label) + '</option>').join('');
    $('xAcc').innerHTML = accs;
    $('ncAcc').innerHTML = '<option value="">Compte par défaut (' + esc((ACCOUNTS[0] || { label: 'Perso' }).label) + ')</option>' + accs;
    $('impAcc').innerHTML = accs;
    $('pasteAcc').innerHTML = accs;
    $('potOpen').innerHTML = CATS.filter((c) => c.group === 'pot' && c.activeNow).map((c) =>
      '<div class="field"><label for="open-' + esc(c.id) + '">' + esc(c.label) + '<small>Solde au premier mois</small></label><div class="inp"><input id="open-' + esc(c.id) + '" data-open="' + esc(c.id) + '" type="number" inputmode="decimal" step="10"><span>&euro;</span></div></div>').join('');
  }

  function fillPot() {
    $('potStart').value = S.potStart;
    const pd = $('pDay');
    if (document.activeElement !== pd) pd.value = S.startDay;
    CATS.filter((c) => c.group === 'pot' && c.activeNow).forEach((c) => {
      const i = $('open-' + c.id);
      if (i && document.activeElement !== i) i.value = S.potOpening[c.id] || 0;
    });
  }

  let suiviSeq = 0;
  function journalQuery() {
    const p = new URLSearchParams({ period: M, limit: '500' });
    const cat = $('jCat').value, q = $('jQ').value.trim();
    if (cat === '__none') p.set('unsorted', 'true'); else if (cat) p.set('category', cat);
    if (q) p.set('q', q);
    return p.toString();
  }

  async function loadSuivi() {
    const seq = ++suiviSeq;
    try {
      const [sm, tx] = await Promise.all([api('/periods/' + M), api('/transactions?' + journalQuery())]);
      if (seq !== suiviSeq) return;
      SUM = sm; TX = tx.items; TXTOTAL = tx.total;
      renderSuivi();
    } catch (e) { if (e.status !== 401) setMsg(e.message, true); }
  }

  function setMsg(t, err) {
    const m = $('xMsg');
    m.textContent = t || '';
    m.className = 'msg' + (err ? ' err' : '');
    clearTimeout(msgT);
    if (t && !err) msgT = setTimeout(() => { m.textContent = ''; }, 3500);
  }

  function paceChart(sm) {
    const host = $('paceChart');
    const D = sm.length, el = sm.elapsed, budget = sm.variableBudget;
    const W = Math.max(300, host.clientWidth || 600), H = 230, m = { l: 58, r: 20, t: 16, b: 30 };
    const shown = sm.dailyCumulative;
    const mx = Math.max(budget, Math.max.apply(null, shown.length ? shown : [0])) * 1.05;
    const tk = niceTicks(mx || 1);
    const XX = (d) => m.l + (W - m.l - m.r) * d / D;
    const YY = (v) => m.t + (H - m.t - m.b) * (1 - v / tk.top);
    const start = parseDay(sm.start);
    let s = '<svg width="' + W + '" height="' + H + '" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Dépenses variables cumulées de la période">';
    for (let v = 0; v <= tk.top + tk.st / 2; v += tk.st) {
      s += '<line x1="' + m.l + '" x2="' + (W - m.r) + '" y1="' + YY(v) + '" y2="' + YY(v) + '" style="stroke:var(' + (v === 0 ? '--line-strong' : '--line') + ')" stroke-width="1"/>' +
        '<text x="' + (m.l - 8) + '" y="' + (YY(v) + 4) + '" text-anchor="end" style="fill:var(--muted);font-family:var(--font-num);font-size:11px">' + kfmt(v) + '</text>';
    }
    [1, 8, 15, 22, D].filter((d, k, a) => d <= D && a.indexOf(d) === k).forEach((d) => {
      s += '<text x="' + XX(d) + '" y="' + (H - 9) + '" text-anchor="middle" style="fill:var(--muted);font-family:var(--font-ui);font-size:11px">' + new Date(start.getFullYear(), start.getMonth(), start.getDate() + d - 1).getDate() + '</text>';
    });
    s += '<line x1="' + XX(0) + '" y1="' + YY(0) + '" x2="' + XX(D) + '" y2="' + YY(budget) + '" style="stroke:var(--muted)" stroke-width="2" stroke-dasharray="5 4"/>';
    if (el > 0) {
      const p = shown.map((v, d) => (d ? 'L' : 'M') + XX(d).toFixed(1) + ' ' + YY(v).toFixed(1)).join('');
      s += '<path d="' + p + '" fill="none" style="stroke:var(--c-var)" stroke-width="2.5" stroke-linejoin="round"/>';
      const last = shown[shown.length - 1], ex = XX(shown.length - 1), over = last > budget * el / D;
      s += '<circle cx="' + ex + '" cy="' + YY(last) + '" r="4" style="fill:var(--c-var)"/>' +
        '<text x="' + (ex - 8) + '" y="' + (YY(last) + (over ? -10 : 18)) + '" text-anchor="end" style="fill:var(--c-var);font-family:var(--font-num);font-size:12px;font-weight:600">' + f(last) + '</text>';
    }
    host.innerHTML = s + '</svg>';
  }

  function histChart(sm) {
    const host = $('histChart');
    const W = Math.max(300, host.clientWidth || 600), H = 210, m = { l: 58, r: 16, t: 22, b: 30 };
    const hist = sm.history;
    const mx = Math.max(Math.max.apply(null, hist.map((h) => h.budget)), Math.max.apply(null, hist.map((h) => h.spent))) * 1.08;
    const tk = niceTicks(mx || 1);
    const YY = (v) => m.t + (H - m.t - m.b) * (1 - v / tk.top);
    const band = (W - m.l - m.r) / hist.length, bw = Math.min(54, band * 0.55);
    let s = '<svg width="' + W + '" height="' + H + '" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Dépenses variables des six dernières périodes">';
    for (let v = 0; v <= tk.top + tk.st / 2; v += tk.st) {
      s += '<line x1="' + m.l + '" x2="' + (W - m.r) + '" y1="' + YY(v) + '" y2="' + YY(v) + '" style="stroke:var(' + (v === 0 ? '--line-strong' : '--line') + ')" stroke-width="1"/>' +
        '<text x="' + (m.l - 8) + '" y="' + (YY(v) + 4) + '" text-anchor="end" style="fill:var(--muted);font-family:var(--font-num);font-size:11px">' + kfmt(v) + '</text>';
    }
    hist.forEach((h, j) => {
      const cx = m.l + band * (j + 0.5), val = h.spent, hh = YY(0) - YY(val);
      if (val > 0) {
        s += '<rect x="' + (cx - bw / 2) + '" y="' + YY(val) + '" width="' + bw + '" height="' + Math.max(1, hh) + '" rx="3" style="fill:var(' + (val > h.budget ? '--bad' : '--c-var') + ')"><title>' + esc(h.label) + ' : ' + f(val) + '</title></rect>' +
          '<text x="' + cx + '" y="' + (YY(val) - 6) + '" text-anchor="middle" style="fill:var(--fg);font-family:var(--font-num);font-size:11px;font-weight:600">' + kfmt(val) + '</text>';
      }
      s += '<line x1="' + (cx - bw / 2 - 5) + '" x2="' + (cx + bw / 2 + 5) + '" y1="' + YY(h.budget) + '" y2="' + YY(h.budget) + '" style="stroke:var(--muted)" stroke-width="2" stroke-dasharray="4 3"><title>Enveloppe de la période : ' + f(h.budget) + '</title></line>';
      const cur = h.key === sm.key;
      s += '<text x="' + cx + '" y="' + (H - 9) + '" text-anchor="middle" style="fill:var(' + (cur ? '--fg' : '--muted') + ');font-family:var(--font-ui);font-size:11px;font-weight:' + (cur ? 700 : 500) + '">' + MSH[h.monthIndex - 1] + '</text>';
    });
    host.innerHTML = s + '</svg>';
  }

  function envRow(e, paceFrac) {
    const bud = e.budget, spent = e.spent;
    const r = bud > 0 ? spent / bud : (spent > 0 ? 2 : 0);
    const cls = r >= 1 ? 'bad' : r >= 0.8 ? 'warn' : '';
    const left = bud - spent;
    return '<div class="env"><div class="envh"><span class="envn">' + esc(e.label) + '</span><span class="num">' + f(spent) + ' <span class="mut">/ ' + f(bud) + '</span></span></div>' +
      '<div class="prog"><div class="pf ' + cls + '" style="width:' + Math.min(100, Math.max(0, r * 100)) + '%"></div>' + (paceFrac !== null ? '<i class="pm" style="left:calc(' + (paceFrac * 100) + '% - 1px)"></i>' : '') + '</div>' +
      '<div class="envs"><span>' + pc(r * 100) + ' utilisé</span><span class="' + (left < 0 ? 'neg' : '') + '">' + (left >= 0 ? 'reste ' : 'dépasse de ') + f(Math.abs(left)) + '</span></div></div>';
  }

  function renderSuivi() {
    if (!SUM || $('view-suivi').hidden) return;
    const sm = SUM;
    $('mLabel').textContent = sm.label;
    $('mToday').disabled = sm.isCurrent;

    const D = sm.length, varSpent = sm.variableSpent, varBud = sm.variableBudget, rest = sm.rest;
    const future = !sm.isCurrent && !sm.isPast;
    const chip = $('sChip');
    chip.className = 'chip';
    let ct;
    if (future) ct = 'À venir';
    else if (varSpent > varBud) { chip.classList.add('bad'); ct = 'Enveloppe dépassée'; }
    else if (sm.isCurrent && sm.projection !== null && sm.projection > varBud) { chip.classList.add('warn'); ct = 'Rythme trop élevé'; }
    else if (sm.hasTransactions) { chip.classList.add('ok'); ct = 'Dans le budget'; }
    else ct = 'Aucune dépense saisie';
    $('sChipTxt').textContent = ct;

    const proj = sm.projection, perDay = sm.perDay, daysLeft = sm.daysLeft;
    $('sKpis').innerHTML =
      '<div class="kpi"><div class="l"><b style="background:var(--c-var)"></b>Dépensé</div><div class="v">' + f(varSpent) + '</div><div class="s">sur ' + f(varBud) + ' d&rsquo;enveloppe variable</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--accent)"></b>Reste</div><div class="v' + (rest < 0 ? ' neg' : '') + '">' + f(rest) + '</div><div class="s">' + (daysLeft > 0 ? 'pour ' + daysLeft + ' jour' + (daysLeft > 1 ? 's' : '') + ' restants' : sm.isPast ? 'période terminée' : 'période à venir') + '</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--accent)"></b>Par jour</div><div class="v">' + (perDay === null ? '&ndash;' : f(perDay)) + '</div><div class="s">' + (perDay === null ? 'période en cours uniquement' : 'à ne pas dépasser, jusqu&rsquo;à la fin de la période') + '</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-prov)"></b>Projection</div><div class="v' + (proj !== null && proj > varBud ? ' neg' : '') + '">' + (proj === null ? '&ndash;' : f(proj)) + '</div><div class="s">' + (proj === null ? 'pas encore de données' : proj > varBud ? 'dépasse l&rsquo;enveloppe de ' + f(proj - varBud) : 'dans l&rsquo;enveloppe') + '</div></div>' +
      '<div class="kpi"><div class="l"><b style="background:var(--c-save)"></b>Reste sur le compte</div><div class="v' + (sm.balance < 0 ? ' neg' : '') + '">' + f(sm.balance) + '</div><div class="s">objectif : ' + f(sm.savingsTarget) + '</div></div>';

    let l;
    if (sm.isCurrent) {
      l = rest >= 0
        ? 'Il reste <strong>' + f(rest) + '</strong> sur l&rsquo;enveloppe variable, soit <strong>' + f(perDay) + ' par jour</strong> pendant ' + daysLeft + ' jour' + (daysLeft > 1 ? 's' : '') + '.'
        : 'L&rsquo;enveloppe variable est dépassée de <strong class="neg">' + f(-rest) + '</strong>. Chaque euro de plus réduit ton épargne du mois.';
      if (proj !== null && proj > varBud && rest >= 0) l += ' Au rythme actuel, tu finis la période à <strong class="neg">' + f(proj) + '</strong>.';
    } else if (sm.isPast) {
      l = 'Période terminée : <strong>' + f(varSpent) + '</strong> dépensés sur <strong>' + f(varBud) + '</strong> prévus, soit ' + (rest >= 0 ? '<strong>' + f(rest) + '</strong> sous' : '<strong class="neg">' + f(-rest) + '</strong> au-dessus de') + ' l&rsquo;enveloppe.';
    } else l = 'Cette période n&rsquo;a pas encore commencé. Tu peux déjà saisir des dépenses prévues.';
    $('sLede').innerHTML = l;

    $('envSub').textContent = sm.isCurrent ? 'Le trait vertical marque où tu devrais en être aujourd’hui.' : 'Dépenses de la période face au budget mensuel de chaque poste.';
    const paceFrac = sm.isCurrent ? sm.elapsed / D : null;
    $('envList').innerHTML = sm.envelopes.map((e) => envRow(e, paceFrac)).join('');

    renderFixed(sm);
    paceChart(sm);
    histChart(sm);

    $('potList').innerHTML = sm.pots.map((p) =>
      '<div class="pot"><div class="envh"><span class="envn">' + esc(p.label) + '</span><span class="num' + (p.balance < 0 ? ' neg' : '') + '">' + f(p.balance) + '</span></div>' +
      '<div class="envs"><span>virement ' + f(p.monthly) + ' / mois</span><span>dépensé cette période : ' + f(p.spentThisPeriod) + '</span></div></div>').join('') +
      '<div class="total-line"><span>Total des pots</span><span class="num' + (sm.potsTotal < 0 ? ' neg' : '') + '">' + f(sm.potsTotal) + '</span></div>';
    fillPot();

    $('bilan').innerHTML =
      '<div><span>Revenus</span><span class="num">' + f(sm.income) + '</span></div>' +
      '<div><span>Charges fixes prélevées</span><span class="num">&minus;' + f(sm.fixedMonthly) + '</span></div>' +
      '<div><span>Virement vers le LDDS</span><span class="num">&minus;' + f(sm.provisions) + '</span></div>' +
      '<div><span>Variable dépensé</span><span class="num">&minus;' + f(varSpent) + '</span></div>' +
      '<div class="res"><span>Reste du mois</span><span class="num' + (sm.balance < 0 ? ' neg' : ' pos') + '">' + f(sm.balance) + '</span></div>';
    $('lddsDone').checked = !!sm.lddsDone;
    $('lddsTxt').firstChild.nodeValue = 'Virement de ' + f(sm.provisions) + ' vers le LDDS fait';

    renderJournal();
  }

  function txLabel(t) { return t.counterparty || t.remittance || t.note || '(sans libellé)'; }

  function renderJournal() {
    const list = TX;
    if (!list.length) {
      $('jList').innerHTML = '<div class="empty">' + (SUM && SUM.hasTransactions || $('jCat').value || $('jQ').value.trim()
        ? 'Aucune opération ne correspond à ce filtre.'
        : 'Aucune opération du ' + esc(SUM.label) + '. Ajoute la première avec le formulaire ci-dessus, ou importe un relevé.') + '</div>';
    } else {
      $('jList').innerHTML = list.map((t) => {
        const armed = pendDel === t.id;
        const cat = t.isInternalTransfer ? 'Virement interne' : t.categoryId ? catLabel(t.categoryId) : 'À trier';
        const tag = (t.isInternalTransfer ? '' : !t.categoryId ? '<span class="tag todo">à trier</span>' : '') + (t.status === 'pending' ? '<span class="tag pend">en attente</span>' : '');
        const detail = [t.note || (t.counterparty && t.remittance ? t.remittance : ''), t.note ? txLabel(t) : (t.counterparty || (t.remittance && !t.note ? t.remittance : '')), t.accountLabel].filter(Boolean);
        const sub = Array.from(new Set(detail)).map(esc).join(' &middot; ');
        const credit = t.amount > 0;
        return '<div class="jr"><span class="jd num">' + frDate(t.date) + '</span><div class="jm"><b>' + esc(cat) + tag + '</b><span>' + sub + '</span></div>' +
          '<span class="ja num ' + (credit ? 'pos' : '') + '">' + (credit ? '+' : '') + f2(Math.abs(t.amount)) + '</span>' +
          '<span class="acts"><button class="lnk" type="button" data-act="edit" data-id="' + t.id + '">Modifier</button><button class="lnk danger' + (armed ? ' armed' : '') + '" type="button" data-act="del" data-id="' + t.id + '">' + (armed ? 'Confirmer' : 'Supprimer') + '</button></span></div>';
      }).join('');
    }
    $('jTotLabel').textContent = TXTOTAL + ' ligne' + (TXTOTAL > 1 ? 's' : '') + (TXTOTAL > list.length ? ' (' + list.length + ' affichées)' : '');
    const net = sum(list.filter((t) => !t.isInternalTransfer).map((t) => t.amount)); // les virements internes ne comptent pas
    $('jTot').textContent = (net > 0 ? '+' : net < 0 ? '−' : '') + f2(Math.abs(net));
  }

  function resetForm(keepCat) {
    editTx = null;
    $('xAmt').value = ''; $('xNote').value = ''; $('xRef').checked = false;
    ['xAmt', 'xDate', 'xAcc', 'xRef'].forEach((id) => { $(id).disabled = false; });
    $('xSubmit').textContent = 'Ajouter';
    $('xCancel').hidden = true;
    if (!keepCat) $('xCat').selectedIndex = 0;
  }

  async function submitExp(ev) {
    ev.preventDefault();
    let amount, date, accountId;
    if (editTx && !editTx.isManual) {
      // Opération venue de la banque : seuls le classement et la note changent.
      amount = editTx.amount; date = editTx.date; accountId = editTx.accountId;
    } else {
      const v = parseAmt($('xAmt').value);
      if (!isFinite(v) || v === 0) { setMsg('Saisis un montant.', true); $('xAmt').focus(); return; }
      amount = (Math.round(Math.abs(v) * 100) / 100) * ($('xRef').checked ? 1 : -1);
      date = $('xDate').value || defaultDate();
      accountId = +$('xAcc').value;
    }
    const body = { date: date, amount: amount, categoryId: $('xCat').value, note: $('xNote').value.trim(), accountId: accountId, isInternalTransfer: false };
    const wasEdit = editTx;
    try {
      if (wasEdit) await api('/transactions/' + wasEdit.id, { method: 'PUT', json: body });
      else await api('/transactions', { method: 'POST', json: body });
    } catch (e) { setMsg(e.message, true); return; }
    M = pkeyOf(date);
    const keepDate = date;
    resetForm(true);
    $('xDate').value = keepDate;
    setMsg(wasEdit ? 'Opération modifiée.' : 'Ajouté : ' + f2(Math.abs(amount)) + ' en ' + catLabel(body.categoryId) + '.');
    await loadSuivi();
    refreshUnsorted();
    $('xAmt').focus();
  }

  async function onJournalClick(ev) {
    const b = ev.target.closest('[data-act]');
    if (!b) return;
    const id = +b.dataset.id, t = TX.find((x) => x.id === id);
    if (!t) return;
    if (b.dataset.act === 'del') {
      if (pendDel === id) {
        pendDel = null;
        try { await api('/transactions/' + id, { method: 'DELETE' }); setMsg('Opération supprimée.'); }
        catch (e) { setMsg(e.message, true); }
        await loadSuivi();
        refreshUnsorted();
      } else { pendDel = id; renderJournal(); }
      return;
    }
    pendDel = null;
    editTx = t;
    $('xAmt').value = String(Math.abs(t.amount)).replace('.', ',');
    $('xRef').checked = t.amount > 0;
    if (t.categoryId && !$('xCat').querySelector('option[value="' + t.categoryId + '"]'))
      $('xCat').insertAdjacentHTML('beforeend', '<option value="' + esc(t.categoryId) + '">' + esc(catLabel(t.categoryId)) + ' (arrêtée)</option>');
    $('xCat').value = t.categoryId || $('xCat').options[0].value;
    $('xDate').value = t.date;
    $('xAcc').value = String(t.accountId);
    $('xNote').value = t.note || '';
    ['xAmt', 'xDate', 'xAcc', 'xRef'].forEach((i) => { $(i).disabled = !t.isManual; });
    $('xSubmit').textContent = 'Enregistrer';
    $('xCancel').hidden = false;
    if (!t.isManual) setMsg('Opération bancaire : seuls la catégorie et la note se modifient.');
    $('xForm').scrollIntoView({ block: 'center' });
    $('xCat').focus();
  }

  async function exportCsv() {
    try {
      const r = await fetch('/api/export.csv', { credentials: 'same-origin' });
      if (r.status === 401) { showLogin(); return; }
      if (!r.ok) throw new Error('Export impossible.');
      const url = URL.createObjectURL(await r.blob());
      const a = document.createElement('a');
      a.href = url; a.download = 'depenses-budget.csv';
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 2000);
    } catch (e) { setMsg(e.message || 'Export impossible.', true); }
  }


  /* ================= Charges fixes de la période ================= */
  function renderFixed(sm) {
    const lines = sm.fixedLines || [];
    const past = sm.isPast, future = !sm.isCurrent && !sm.isPast;
    const done = lines.filter((l) => l.status !== 'due').length;
    $('fxSum').innerHTML = lines.length
      ? '<b>' + done + '</b> sur <b>' + lines.length + '</b> payées · <b>' + f(sum(lines.map((l) => l.paid))) + '</b> sur ' + f(sum(lines.map((l) => l.expected))) + ' prévus'
      : 'Aucune charge fixe à suivre.';
    $('fxList').innerHTML = lines.map((l) => {
      const acc = ACCOUNTS.find((a) => a.id === (l.accountId || (ACCOUNTS[0] || {}).id));
      const name = '<span class="nm"><b>' + esc(l.label) + '</b><span>' + f(l.expected) + ' prévus' + (acc ? ' · ' + esc(acc.label) : '') + '</span></span>';
      if (l.status === 'due') {
        const chip = past ? '<span class="chip bad"><i></i>Non payée</span>' : '<span class="chip"><i></i>' + (future ? 'À venir' : 'À payer') + '</span>';
        return '<button type="button" class="fx due" data-fxpay="' + esc(l.categoryId) + '" aria-label="Marquer ' + esc(l.label) + ' comme payée">' + name + '<span class="st">' + chip + '<span class="go">Marquer payée</span></span></button>';
      }
      const gap = l.paid - l.expected;
      const chip = l.status === 'gap'
        ? '<span class="chip warn"><i></i>Payée ' + f2(l.paid) + ' (' + (gap > 0 ? '+' : '−') + f2(Math.abs(gap)) + ')</span>'
        : '<span class="chip ok"><i></i>Payée' + (l.lastDate ? ' · ' + frDate(l.lastDate) : '') + '</span>';
      const undo = l.allManual && l.count === 1
        ? '<button class="btn sm" type="button" data-fxundo="' + esc(l.categoryId) + '">Annuler</button>'
        : '<span class="mut" style="font-size:12.5px">' + (l.allManual ? l.count + ' opérations' : 'via la banque') + '</span>';
      return '<div class="fx paid">' + name + '<span class="st">' + chip + undo + '</span></div>';
    }).join('');
  }

  async function onFixedClick(e) {
    const pay = e.target.closest('[data-fxpay]'), undo = e.target.closest('[data-fxundo]');
    const btn = pay || undo;
    if (!btn) return;
    const id = pay ? pay.dataset.fxpay : undo.dataset.fxundo, msg = $('fxMsg');
    btn.disabled = true;
    msg.className = 'msg';
    msg.textContent = '';
    try {
      await api('/periods/' + M + '/fixed/' + encodeURIComponent(id) + '/paid', { method: pay ? 'POST' : 'DELETE', json: pay ? {} : undefined });
      await loadSuivi();
      msg.textContent = pay ? catLabel(id) + ' marquée payée.' : 'Marquage annulé.';
    } catch (err) { btn.disabled = false; msg.className = 'msg err'; msg.textContent = err.message; }
  }

  /* ================= Onglet À trier ================= */
  async function refreshUnsorted() {
    try {
      const r = await api('/transactions/unsorted-count');
      setBadge(r.count);
    } catch (e) { /* le badge est facultatif */ }
  }
  function setBadge(n) {
    const b = $('triBadge');
    b.textContent = n > 99 ? '99+' : String(n);
    b.hidden = n <= 0;
  }

  async function loadTri() {
    try {
      const r = await api('/transactions?unsorted=true&limit=200');
      TRI = r.items; TRITOTAL = r.total;
      setBadge(TRITOTAL);
      renderTri();
    } catch (e) { if (e.status !== 401) $('triMsg').textContent = e.message; }
  }

  function renderTri() {
    const opts = '<option value="">Choisir une catégorie…</option>' + catOptionsHtml();
    if (!TRI.length) { $('triList').innerHTML = '<div class="empty">Rien à trier. Tout est classé.</div>'; return; }
    $('triList').innerHTML = TRI.map((t) => {
      const credit = t.amount > 0;
      const sub = [t.counterparty && t.remittance ? t.remittance : '', t.note, t.accountLabel].filter(Boolean).map(esc).join(' &middot; ');
      return '<div class="tri"><span class="jd num">' + frDate(t.date, true).slice(0, 5) + '<br>' + t.date.slice(0, 4) + '</span>' +
        '<div class="jm"><b>' + esc(txLabel(t)) + (t.status === 'pending' ? '<span class="tag pend">en attente</span>' : '') + '</b><span>' + sub + '</span></div>' +
        '<span class="ja num ' + (credit ? 'pos' : '') + '">' + (credit ? '+' : '−') + f2(Math.abs(t.amount)) + '</span>' +
        '<select class="ctl" data-tri="' + t.id + '" aria-label="Catégorie de ' + esc(txLabel(t)) + '">' + opts + '</select>' +
        '<span class="acts"><button class="btn sm" type="button" data-tri-act="internal" data-id="' + t.id + '">Virement interne</button></span></div>';
    }).join('') + (TRITOTAL > TRI.length ? '<p class="mutline" style="margin-top:12px">' + (TRITOTAL - TRI.length) + ' autres opérations s&rsquo;afficheront quand celles-ci seront triées.</p>' : '');
  }

  async function classify(id, categoryId, internal) {
    const t = TRI.find((x) => x.id === id);
    if (!t) return;
    const body = { date: t.date, amount: t.amount, accountId: t.accountId, note: t.note, categoryId: internal ? null : categoryId, isInternalTransfer: !!internal };
    try { await api('/transactions/' + id, { method: 'PUT', json: body }); }
    catch (e) { $('triMsg').textContent = e.message; return; }
    $('triMsg').textContent = '';
    if (t.suggestedPattern) showRuleOffer(t, internal ? null : categoryId, !!internal);
    else $('ruleOffer').hidden = true;
    await loadTri();
  }

  function showRuleOffer(t, categoryId, internal) {
    const box = $('ruleOffer');
    const target = internal ? 'virement interne' : catLabel(categoryId);
    box.hidden = false;
    box.innerHTML = '<div><strong>Toujours classer ainsi ?</strong> Les opérations contenant le motif ci-dessous iront en <strong>' + esc(target) + '</strong>.</div>' +
      '<div class="row"><input class="ctl" id="offerPat" type="text" maxlength="200" value="' + esc(t.suggestedPattern) + '" aria-label="Motif de la règle">' +
      '<button class="btn primary" type="button" id="offerYes">Créer la règle</button><button class="btn" type="button" id="offerNo">Non merci</button></div>' +
      '<p class="msg" id="offerMsg" role="status"></p>';
    $('offerNo').onclick = () => { box.hidden = true; };
    $('offerYes').onclick = async () => {
      const pattern = $('offerPat').value.trim();
      if (!pattern) { $('offerMsg').textContent = 'Le motif est vide.'; return; }
      try {
        const r = await api('/rules', { method: 'POST', json: { pattern: pattern, categoryId: internal ? null : categoryId, marksInternalTransfer: internal } });
        box.hidden = true;
        $('triMsg').textContent = 'Règle créée' + (r.applied ? ' : ' + r.applied + ' autre' + (r.applied > 1 ? 's' : '') + ' opération' + (r.applied > 1 ? 's' : '') + ' classée' + (r.applied > 1 ? 's' : '') + '.' : '.');
        await loadTri();
      } catch (e) { $('offerMsg').textContent = e.message; }
    };
  }


  /* ================= Coller des opérations de la banque ================= */
  let PV = null;   // aperçu en cours : { accountId, items, bank, ignored }
  const BK_GROUPS = [['variable', 'Variable mensuel (part de l’enveloppe)'], ['pot', 'Pot de provisions'], ['fixe', 'Charge fixe'], ['ignore', 'Hors budget']];

  async function refreshBudget() {
    await flushSave();
    applyBudget(await api('/budget?period=' + BP));
  }

  function pasteMsg(t, err) { const m = $('pasteMsg'); m.textContent = t || ''; m.className = 'msg' + (err ? ' err' : ''); }

  async function pasteAnalyze() {
    const text = $('pasteTxt').value;
    if (!text.trim()) { pasteMsg('Colle d’abord les opérations copiées depuis ta banque.', true); return; }
    pasteMsg('Analyse…');
    $('pasteGo').disabled = true;
    try {
      const r = await api('/paste/preview', { method: 'POST', json: { text: text, accountId: +$('pasteAcc').value } });
      PV = {
        accountId: r.accountId,
        ignored: r.ignored,
        bank: r.bankCategories.filter((b) => !b.known).map((b) => ({
          key: b.key, label: b.label, count: b.count, choice: b.matchingCategoryId || '', newLabel: b.label, newGroup: 'variable', newValue: '', newAccount: '', remember: true,
        })),
        items: r.items.map((i) => ({
          i: i, include: i.kind !== 'duplicate', action: i.kind === 'reconcile' && !i.ambiguous ? 'reconcile' : 'add',
          cat: i.suggestedCategoryId || '', override: false,
        })),
      };
      PV.bank.forEach((b) => applyBankChoice(b.key));
      pasteMsg('');
      renderPaste();
    } catch (e) { pasteMsg(e.message, true); }
    finally { $('pasteGo').disabled = false; }
  }

  /** Répercute le choix fait sur une catégorie bancaire aux opérations qui n'ont ni règle ni choix individuel. */
  function applyBankChoice(key) {
    const b = PV.bank.find((x) => x.key === key);
    PV.items.forEach((it) => {
      if (it.i.bankKey !== key || it.override || it.i.suggestionSource !== 'none') return;
      it.cat = b.choice === '' || b.choice === '__ignore' ? '' : b.choice === '__create' ? 'new:' + key : b.choice;
    });
  }

  function pasteCatOptions(selected) {
    const created = PV.bank.filter((b) => b.choice === '__create').map((b) => '<option value="new:' + esc(b.key) + '">➕ ' + esc(b.newLabel || b.label) + ' (nouvelle)</option>').join('');
    const html = '<option value="">À trier</option>' + created + catOptionsHtml();
    return html.replace('value="' + selected + '"', 'value="' + selected + '" selected');
  }

  function renderPaste() {
    const box = $('pastePreview');
    if (!PV) { box.hidden = true; box.innerHTML = ''; $('pasteInput').hidden = false; return; }
    $('pasteInput').hidden = true;
    box.hidden = false;
    const its = PV.items, picked = its.filter((x) => x.include);
    const nNew = its.filter((x) => x.i.kind === 'new').length, nRec = its.filter((x) => x.i.kind === 'reconcile').length, nDup = its.filter((x) => x.i.kind === 'duplicate').length;
    let h = '<p class="pv-sum"><b>' + its.length + '</b> opération' + (its.length > 1 ? 's' : '') + ' reconnue' + (its.length > 1 ? 's' : '') + ' : <b>' + nNew + '</b> nouvelle' + (nNew > 1 ? 's' : '') +
      (nRec ? ', <b>' + nRec + '</b> à rapprocher' : '') + (nDup ? ', <b>' + nDup + '</b> déjà connue' + (nDup > 1 ? 's' : '') : '') +
      (PV.ignored ? ' · ' + PV.ignored + ' bloc' + (PV.ignored > 1 ? 's' : '') + ' illisible' + (PV.ignored > 1 ? 's' : '') + ' ignoré' + (PV.ignored > 1 ? 's' : '') : '') + '.</p>';

    if (PV.bank.length) {
      h += '<h3 class="gh">Catégories de la banque à associer</h3><p class="mutline">Choisis une fois : la correspondance est retenue pour les prochains collages. Une nouvelle catégorie démarre à 0 % de l’enveloppe ; règle sa part dans l’onglet Budget.</p>';
      PV.bank.forEach((b) => {
        const opts = '<option value="">À choisir…</option><option value="__create">➕ Créer une catégorie</option><option value="__ignore">Ne pas utiliser cette catégorie</option>' + catOptionsHtml();
        h += '<div class="bk"><div><b>' + esc(b.label) + '</b><br><span class="mut">' + b.count + ' opération' + (b.count > 1 ? 's' : '') + '</span></div>' +
          '<select class="ctl" data-bk="' + esc(b.key) + '" aria-label="Catégorie pour ' + esc(b.label) + '">' + opts.replace('value="' + b.choice + '"', 'value="' + b.choice + '" selected') + '</select>';
        if (b.choice === '__create') {
          h += '<div class="sub2"><input class="ctl" data-bknew="' + esc(b.key) + '" value="' + esc(b.newLabel) + '" maxlength="120" aria-label="Nom de la nouvelle catégorie">' +
            '<select class="ctl" data-bkgroup="' + esc(b.key) + '" aria-label="Type de la nouvelle catégorie">' + BK_GROUPS.map((g) => '<option value="' + g[0] + '"' + (g[0] === b.newGroup ? ' selected' : '') + '>' + g[1] + '</option>').join('') + '</select>' +
            (b.newGroup === 'ignore' ? '' : '<input class="ctl" data-bkval="' + esc(b.key) + '" value="' + esc(b.newValue) + '" inputmode="decimal" placeholder="' + (b.newGroup === 'fixe' ? 'Montant mensuel (€)' : 'Part de l’enveloppe (%)') + '" aria-label="' + (b.newGroup === 'fixe' ? 'Montant mensuel' : 'Part de l’enveloppe') + '">') +
            (b.newGroup === 'fixe' ? '<select class="ctl" data-bkacc="' + esc(b.key) + '" aria-label="Compte de prélèvement"><option value="">Compte par défaut</option>' + ACCOUNTS.map((a) => '<option value="' + a.id + '"' + (String(a.id) === b.newAccount ? ' selected' : '') + '>' + esc(a.label) + '</option>').join('') + '</select>' : '') + '</div>';
        }
        if (b.choice !== '') h += '<label class="chk sub2" for="rem-' + esc(b.key) + '"><input type="checkbox" id="rem-' + esc(b.key) + '" data-bkrem="' + esc(b.key) + '"' + (b.remember ? ' checked' : '') + '><span>Retenir pour la suite</span></label>';
        h += '</div>';
      });
    }

    h += '<h3 class="gh">Opérations</h3>';
    h += its.map((it, idx) => {
      const p = it.i, dup = p.kind === 'duplicate';
      const credit = p.amount > 0;
      const tags = (p.pending ? '<span class="tag pend">en attente</span>' : '') + (dup ? '<span class="tag dup">déjà connue</span>' : '') + (p.kind === 'reconcile' ? '<span class="tag merge">à rapprocher</span>' : '') +
        (p.suggestionSource === 'rule' ? '<span class="tag">règle</span>' : '') +
        (p.suggestionSource === 'fixed' ? '<span class="tag merge" title="Même montant qu’une charge fixe encore à payer">charge fixe ?</span>' : '');
      const sub = [p.detail, p.bankCategory].filter(Boolean).map(esc).join(' &middot; ');
      let row = '<div class="pv' + (it.include ? '' : ' off') + '"><input type="checkbox" data-pvinc="' + idx + '"' + (it.include ? ' checked' : '') + (dup ? ' disabled' : '') + ' aria-label="Inclure ' + esc(p.label) + '">' +
        '<span class="jd num">' + frDate(p.date) + (p.time ? '<br>' + esc(p.time) : '') + '</span>' +
        '<div class="jm"><b>' + esc(p.label) + tags + '</b><span>' + sub + '</span></div>' +
        '<span class="ja num ' + (credit ? 'pos' : '') + '">' + (credit ? '+' : '−') + f2(Math.abs(p.amount)) + '</span>';
      if (!dup) row += '<select class="ctl" data-pvcat="' + idx + '" aria-label="Catégorie de ' + esc(p.label) + '">' + pasteCatOptions(it.cat) + '</select>';
      if (p.kind === 'reconcile' && p.existing) {
        const ex = p.existing;
        row += '<select class="ctl" data-pvact="' + idx + '" aria-label="Rapprochement"><option value="reconcile"' + (it.action === 'reconcile' ? ' selected' : '') + '>Rapprocher avec « ' + esc(ex.label || 'saisie') + ' » du ' + frDate(ex.date) + (ex.isPending ? ' (en attente)' : '') + '</option><option value="add"' + (it.action === 'add' ? ' selected' : '') + '>Ajouter en plus</option></select>';
        if (p.ambiguous) row += '<span class="hint">Plusieurs opérations possibles : vérifie avant de rapprocher.</span>';
      }
      return row + '</div>';
    }).join('');

    const n = picked.length;
    h += '<div class="row-actions" style="margin-top:14px"><button class="btn primary" type="button" id="pvOk"' + (n ? '' : ' disabled') + '>' + (n ? 'Valider ' + n + ' opération' + (n > 1 ? 's' : '') : 'Rien à ajouter') + '</button><button class="btn" type="button" id="pvCancel">Annuler</button><span class="msg" id="pvMsg" role="status" aria-live="polite"></span></div>';
    box.innerHTML = h;
  }

  async function pasteCommit() {
    const msg = $('pvMsg');
    const picked = PV.items.filter((x) => x.include);
    const used = new Set(picked.map((x) => x.cat).filter((c) => c.startsWith('new:')).map((c) => c.slice(4)));
    PV.bank.forEach((b) => { if (b.choice === '__create' && b.remember) used.add(b.key); });
    const missing = PV.bank.find((b) => b.choice === '__create' && used.has(b.key) && !b.newLabel.trim());
    if (missing) { msg.className = 'msg err'; msg.textContent = 'Donne un nom à la nouvelle catégorie.'; return; }

    const body = {
      accountId: PV.accountId,
      newCategories: PV.bank.filter((b) => b.choice === '__create' && used.has(b.key)).map((b) => {
        const v = parseAmt(b.newValue), val = isFinite(v) && v > 0 ? v : 0;
        return {
          key: b.key, label: b.newLabel.trim(), group: b.newGroup,
          amount: b.newGroup === 'fixe' ? val : null, percentage: b.newGroup === 'variable' || b.newGroup === 'pot' ? val : null,
          accountId: b.newGroup === 'fixe' && b.newAccount ? +b.newAccount : null,
        };
      }),
      mappings: PV.bank.filter((b) => b.choice !== '' && b.remember).map((b) => ({
        bankCategory: b.label, ignore: b.choice === '__ignore',
        categoryId: b.choice === '__create' || b.choice === '__ignore' ? null : b.choice, newCategoryKey: b.choice === '__create' ? b.key : null,
      })),
      items: picked.map((it) => {
        const p = it.i, isNew = it.cat.startsWith('new:');
        return {
          date: p.date, label: p.label, detail: p.detail, bankCategory: p.bankCategory, paymentMethod: p.paymentMethod, pending: p.pending, amount: p.amount, providerId: p.providerId,
          action: it.action, reconcileId: it.action === 'reconcile' && p.existing ? p.existing.id : null,
          categoryId: it.cat && !isNew ? it.cat : null, newCategoryKey: isNew ? it.cat.slice(4) : null, internal: p.suggestionSource === 'rule' && p.internal,
        };
      }),
    };
    $('pvOk').disabled = true;
    msg.className = 'msg'; msg.textContent = 'Enregistrement…';
    try {
      const r = await api('/paste/commit', { method: 'POST', json: body });
      const last = picked.map((x) => x.i.date).sort().pop();
      PV = null;
      $('pasteTxt').value = '';
      renderPaste();
      if (r.categoriesCreated) await refreshBudget();
      if (last) M = pkeyOf(last) > currentKey() ? currentKey() : pkeyOf(last);
      $('xDate').value = defaultDate();
      const bits = [r.added + ' ajoutée' + (r.added > 1 ? 's' : ''), r.reconciled ? r.reconciled + ' rapprochée' + (r.reconciled > 1 ? 's' : '') : '', r.categoriesCreated ? r.categoriesCreated + ' catégorie' + (r.categoriesCreated > 1 ? 's' : '') + ' créée' + (r.categoriesCreated > 1 ? 's' : '') : '', r.toSort ? r.toSort + ' à trier' : ''].filter(Boolean);
      pasteMsg(bits.join(' · ') + '.');
      setBadge(r.toSort);
      await loadSuivi();
    } catch (e) { $('pvOk').disabled = false; msg.className = 'msg err'; msg.textContent = e.message; }
  }

  function onPasteInput(e) {
    if (!PV) return;
    const t = e.target, d = t.dataset;
    if (d.bk !== undefined) { const b = PV.bank.find((x) => x.key === d.bk); b.choice = t.value; applyBankChoice(b.key); renderPaste(); }
    else if (d.bknew !== undefined) { PV.bank.find((x) => x.key === d.bknew).newLabel = t.value; if (e.type === 'change') renderPaste(); }
    else if (d.bkgroup !== undefined) { PV.bank.find((x) => x.key === d.bkgroup).newGroup = t.value; renderPaste(); }
    else if (d.bkval !== undefined) PV.bank.find((x) => x.key === d.bkval).newValue = t.value;
    else if (d.bkacc !== undefined) PV.bank.find((x) => x.key === d.bkacc).newAccount = t.value;
    else if (d.bkrem !== undefined) PV.bank.find((x) => x.key === d.bkrem).remember = t.checked;
    else if (d.pvinc !== undefined) { PV.items[+d.pvinc].include = t.checked; renderPaste(); }
    else if (d.pvcat !== undefined) { const it = PV.items[+d.pvcat]; it.cat = t.value; it.override = true; }
    else if (d.pvact !== undefined) PV.items[+d.pvact].action = t.value;
  }

  async function loadBankMap() {
    try {
      const list = await api('/bank-categories');
      $('bankList').innerHTML = list.length ? list.map((m) =>
        '<div class="bankrow"><div><strong>' + esc(m.label) + '</strong> &rarr; ' + (m.categoryId ? esc(catLabel(m.categoryId)) : '<span class="mut">catégorie non utilisée</span>') +
        '</div><button class="btn sm danger" type="button" data-bkdel="' + esc(m.key) + '">Oublier</button></div>').join('')
        : '<div class="empty">Aucune correspondance retenue pour l’instant.</div>';
    } catch (e) { if (e.status !== 401) $('bankList').textContent = e.message; }
  }

  /* ================= Connexion bancaire ================= */
  let BANK = null;       // état de la synchro bancaire (serveur)
  let bankReplace = null; // connexion à remplacer quand on choisit une banque (bouton « Reconnecter »)
  let bankBack = null;   // résultat du retour de la banque, à afficher une fois
  const BANK_STATUS = { active: ['ok', 'Active'], expired: ['bad', 'Expirée'], revoked: ['bad', 'Révoquée'] };
  const BANK_BACK = {
    ok: ['', 'Banque connectée. Indique ci-dessous quel compte de l’appli chaque compte bancaire alimente, puis lance la première synchronisation.'],
    refuse: ['warn', 'Tu as annulé l’autorisation sur le site de la banque : rien n’a été connecté.'],
    expire: ['warn', 'L’autorisation a pris plus d’une heure : recommence la connexion.'],
    invalide: ['warn', 'Ce retour de la banque ne correspond à aucune demande en cours (lien déjà utilisé ?). Recommence la connexion.'],
    'aucun-compte': ['warn', 'La banque n’a renvoyé aucun compte. En mode gratuit, Enable Banking ne donne accès qu’aux comptes liés à ton application dans son portail : lie-les d’abord là-bas (voir le README).'],
    erreur: ['warn', 'La connexion à la banque a échoué. Réessaie dans quelques minutes.'],
  };

  const frDateTime = (iso) => new Date(iso).toLocaleString('fr-FR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  const frDay = (iso) => new Date(iso).toLocaleDateString('fr-FR', { day: '2-digit', month: '2-digit', year: 'numeric' });

  /** Lit le résultat du retour de la banque dans l'adresse (?banque=…), puis le retire de l'adresse. */
  function readBankBack() {
    let v = null;
    try { v = new URLSearchParams(location.search).get('banque'); } catch (e) { /* adresse illisible */ }
    if (!v) return;
    bankBack = BANK_BACK[v] ? v : 'erreur';
    try { history.replaceState(null, '', location.pathname + location.hash); } catch (e) { /* facultatif */ }
  }

  async function loadBank() {
    try { BANK = await api('/bank/status'); }
    catch (e) { if (e.status !== 401) $('bankIntro').textContent = e.message; return; }
    renderBank();
    renderBankAlert();
  }

  /** Bandeau visible sur tous les onglets quand un consentement arrive à échéance ou n'est plus valable. */
  function renderBankAlert() {
    const el = $('bankAlert');
    const due = BANK ? BANK.connections.filter((c) => c.status !== 'active' || c.expiresSoon) : [];
    el.hidden = !due.length;
    el.innerHTML = due.map((c) => {
      const text = c.status === 'active'
        ? 'La connexion à ' + esc(c.bank) + ' expire ' + (c.daysLeft > 1 ? 'dans ' + c.daysLeft + ' jours' : c.daysLeft === 1 ? 'demain' : 'aujourd’hui') + '. Reconnecte-la pour que les opérations continuent d’arriver.'
        : 'La connexion à ' + esc(c.bank) + ' n’est plus valable : les opérations n’arrivent plus toutes seules.';
      return '<div class="row"><span>' + text + '</span>' + (BANK.configured ? '<button class="btn sm" type="button" data-bconn-re="' + c.id + '">Reconnecter</button>' : '') + '</div>';
    }).join('');
  }

  function renderBank() {
    const b = BANK;
    $('bankIntro').textContent = b.configured
      ? 'Les opérations de tes comptes arrivent toutes seules' + (b.syncHours > 0 ? ', toutes les ' + String(b.syncHours).replace('.', ',') + ' heures environ' : ' quand tu lances une synchronisation') + ', par Enable Banking. Tes identifiants bancaires ne passent jamais par cette application : tu t’authentifies sur le site de ta banque.'
      : 'La synchronisation automatique n’est pas configurée sur ce serveur. Le README explique comment créer l’application Enable Banking (gratuite pour ses propres comptes) et renseigner les variables BUDGET_EB_*. Le collage et l’import de relevés fonctionnent sans elle.';

    const note = $('bankNotice');
    if (bankBack) {
      note.className = 'banner ' + BANK_BACK[bankBack][0];
      note.textContent = BANK_BACK[bankBack][1];
      note.hidden = false;
      bankBack = null;
    }

    $('bankConns').innerHTML = b.connections.map((c) => {
      const st = BANK_STATUS[c.status] || BANK_STATUS.expired;
      const accOpts = (a) => '<option value="">Ne pas synchroniser</option>' +
        ACCOUNTS.map((x) => '<option value="' + x.id + '"' + (x.id === a.accountId ? ' selected' : '') + '>' + esc(x.label) + '</option>').join('') +
        '<option value="__new">Nouveau compte « ' + esc(a.label) + ' »</option>';
      const info = ['Consentement valable jusqu’au ' + frDay(c.validUntil) + (c.status === 'active' ? ' (' + c.daysLeft + ' jour' + (c.daysLeft > 1 ? 's' : '') + ')' : ''),
        c.lastSuccessAt ? 'dernière synchro réussie le ' + frDateTime(c.lastSuccessAt) : 'pas encore synchronisée'].join(' · ');
      return '<div class="bconn"><div class="bhead"><strong>' + esc(c.bank) + '</strong><span class="chip ' + st[0] + '"><i></i>' + st[1] + '</span></div>' +
        '<small>' + info + '</small>' +
        (c.lastError ? '<div class="msg err">' + esc(c.lastError) + '</div>' : '') +
        (c.accounts.length ? c.accounts.map((a) =>
          '<div class="bankrow"><div><strong>' + esc(a.label) + '</strong>' + (a.ibanMasked ? ' <span class="mut num">' + esc(a.ibanMasked) + '</span>' : '') + '</div>' +
          '<select class="ctl" data-blink="' + a.id + '" aria-label="Compte de l’appli alimenté par ' + esc(a.label) + '">' + accOpts(a) + '</select></div>').join('')
          : '<div class="mut">Aucun compte renvoyé par la banque.</div>') +
        '<div class="row-actions">' + (b.configured ? '<button class="btn sm" type="button" data-bconn-re="' + c.id + '">Reconnecter</button>' : '') +
        '<button class="btn sm danger" type="button" data-bconn-del="' + c.id + '">Déconnecter</button></div></div>';
    }).join('');

    $('bankActions').hidden = !b.configured;
    $('bankSync').hidden = !b.connections.some((c) => c.status === 'active' && c.accounts.some((a) => a.accountId));
  }

  function bankMsg(text, err) { const m = $('bankMsg'); m.className = err ? 'msg err' : 'msg'; m.textContent = text || ''; }

  /** Affiche le choix de la banque ; « replaces » = connexion à remplacer en cas de succès. */
  async function bankChoose(replaces) {
    bankReplace = replaces || null;
    bankMsg('Chargement des banques…');
    try {
      const list = await api('/bank/aspsps');
      const current = replaces && BANK ? (BANK.connections.find((c) => c.id === replaces) || {}).bank : null;
      $('bankPick').innerHTML = list.map((x) => '<option value="' + esc(x.name) + '"' + (x.name === current ? ' selected' : '') + '>' + esc(x.name) + (x.beta ? ' (bêta)' : '') + '</option>').join('');
      $('bankConnect').hidden = false;
      bankMsg(list.length ? '' : 'Aucune banque proposée pour ce pays.', !list.length);
    } catch (e) { bankMsg(e.message, true); }
  }

  async function bankConnect(name, replaces) {
    if (!name) return;
    bankMsg('Ouverture du site de la banque…');
    try {
      const r = await api('/bank/connect', { method: 'POST', json: { name: name, replaces: replaces || null } });
      location.href = r.url;   // l'authentification se fait chez la banque ; elle nous renvoie ensuite ici
    } catch (e) { bankMsg(e.message, true); }
  }

  async function bankReconnect(id) {
    const c = BANK && BANK.connections.find((x) => x.id === id);
    if (c) await bankConnect(c.bank, id);
  }

  async function bankSyncNow() {
    const out = $('bankResult'), btn = $('bankSync');
    out.hidden = true;
    bankMsg('Synchronisation en cours…');
    btn.disabled = true;
    try {
      const r = await api('/bank/sync', { method: 'POST' });
      bankMsg('');
      out.hidden = false;
      out.innerHTML = (r.results.length ? r.results.map((x) => x.error
        ? '<div><strong>' + esc(x.bank) + '</strong> : <span class="neg">' + esc(x.error) + '</span></div>'
        : '<div><strong>' + esc(x.bank) + '</strong> : <b>' + x.added + '</b> ajoutées, <b>' + x.reconciled + '</b> rapprochées, <b>' + x.duplicates + '</b> déjà connues' +
          (x.removedPending ? ', <b>' + x.removedPending + '</b> en attente retirées (annulées ou débitées pour un autre montant)' : '') + '.</div>').join('')
        : '<div>Aucune connexion active à synchroniser.</div>') +
        (r.toSort ? '<div><b>' + r.toSort + '</b> à trier au total. <a href="#tri">Aller trier</a></div>' : '');
      setBadge(r.toSort);
      await loadBank();
    } catch (e) { bankMsg(e.message, true); }
    finally { btn.disabled = false; }
  }

  async function bankLink(select) {
    const id = +select.dataset.blink, v = select.value;
    try {
      await api('/bank/accounts/' + id, { method: 'PUT', json: v === '__new' ? { create: true } : { accountId: v ? +v : null } });
      if (v === '__new') { ACCOUNTS = await api('/accounts'); buildSelects(); }
      bankMsg('');
    } catch (e) { bankMsg(e.message, true); }
    await loadBank();
  }

  async function bankDisconnect(id) {
    const c = BANK && BANK.connections.find((x) => x.id === id);
    if (!c || !window.confirm('Déconnecter ' + c.bank + ' ? Les opérations déjà récupérées restent ; le consentement est fermé chez Enable Banking.')) return;
    try { await api('/bank/connections/' + id, { method: 'DELETE' }); bankMsg(''); }
    catch (e) { bankMsg(e.message, true); }
    $('bankResult').hidden = true;
    await loadBank();
  }

  /* ================= Onglet Règles et import ================= */
  const FIELD_LABEL = { both: 'Contrepartie et libellé', counterparty: 'Contrepartie', remittance: 'Libellé' };
  const DIR_LABEL = { any: '', debit: 'débits seulement', credit: 'crédits seulement' };

  async function loadRules() {
    loadBank();
    loadBankMap();
    try { RULES = await api('/rules'); renderRules(); }
    catch (e) { if (e.status !== 401) $('ruleMsg').textContent = e.message; }
  }

  function renderRules() {
    if (!RULES.length) { $('ruleList').innerHTML = '<div class="empty">Aucune règle. Elles se créent depuis l&rsquo;onglet « À trier », ou ci-dessous.</div>'; return; }
    $('ruleList').innerHTML = RULES.map((r) => {
      const bits = [FIELD_LABEL[r.field], r.isRegex ? 'expression régulière' : '', DIR_LABEL[r.direction],
        r.minAmount != null || r.maxAmount != null ? (r.minAmount != null ? f2(r.minAmount) : '0') + ' à ' + (r.maxAmount != null ? f2(r.maxAmount) : '∞') : '',
        'priorité ' + r.priority].filter(Boolean).map(esc).join(' &middot; ');
      return '<div class="rule' + (r.enabled ? '' : ' off') + '"><div><code>' + esc(r.pattern) + '</code> &rarr; <strong>' + esc(r.marksInternalTransfer ? 'Virement interne' : catLabel(r.categoryId)) + '</strong></div>' +
        '<span class="acts"><button class="btn sm" type="button" data-ract="toggle" data-id="' + r.id + '">' + (r.enabled ? 'Désactiver' : 'Activer') + '</button><button class="btn sm danger" type="button" data-ract="del" data-id="' + r.id + '">Supprimer</button></span><small>' + bits + '</small></div>';
    }).join('');
  }

  async function onRuleClick(ev) {
    const b = ev.target.closest('[data-ract]');
    if (!b) return;
    const id = +b.dataset.id, r = RULES.find((x) => x.id === id);
    if (!r) return;
    try {
      if (b.dataset.ract === 'del') {
        if (!window.confirm('Supprimer la règle « ' + r.pattern + ' » ? Les opérations déjà classées ne bougent pas.')) return;
        await api('/rules/' + id, { method: 'DELETE' });
      } else {
        await api('/rules/' + id, { method: 'PUT', json: Object.assign({}, r, { enabled: !r.enabled }) });
      }
      await loadRules();
    } catch (e) { $('ruleMsg').textContent = e.message; }
  }

  async function submitRule(ev) {
    ev.preventDefault();
    const msg = $('ruleMsg');
    const cat = $('rCat').value, internal = cat === '__internal';
    const num = (id) => { const v = $(id).value.trim(); if (!v) return null; const n = parseAmt(v); return isFinite(n) ? Math.abs(n) : NaN; };
    const mn = num('rMin'), mx = num('rMax');
    if (Number.isNaN(mn) || Number.isNaN(mx)) { msg.className = 'msg err'; msg.textContent = 'Montant invalide.'; return; }
    const body = { pattern: $('rPat').value.trim(), isRegex: $('rRx').checked, field: $('rField').value, direction: $('rDir').value, categoryId: internal ? null : cat, marksInternalTransfer: internal, minAmount: mn, maxAmount: mx, priority: parseInt($('rPrio').value, 10) || 0 };
    try {
      const r = await api('/rules', { method: 'POST', json: body });
      msg.className = 'msg';
      msg.textContent = 'Règle ajoutée' + (r.applied ? ' : ' + r.applied + ' opération' + (r.applied > 1 ? 's' : '') + ' classée' + (r.applied > 1 ? 's' : '') + '.' : '.');
      $('rPat').value = '';
      await loadRules();
      refreshUnsorted();
    } catch (e) { msg.className = 'msg err'; msg.textContent = e.message; }
  }

  async function reapply() {
    const msg = $('ruleMsg');
    try {
      const r = await api('/rules/apply', { method: 'POST', json: { all: true } });
      msg.className = 'msg';
      msg.textContent = r.applied + ' opération' + (r.applied > 1 ? 's' : '') + ' classée' + (r.applied > 1 ? 's' : '') + ' par les règles.';
      refreshUnsorted();
    } catch (e) { msg.className = 'msg err'; msg.textContent = e.message; }
  }

  async function doImport() {
    const file = $('impFile').files[0], msg = $('impMsg'), out = $('impResult');
    out.hidden = true;
    if (!file) { msg.className = 'msg err'; msg.textContent = 'Choisis un fichier.'; return; }
    if (file.size > 8 * 1024 * 1024) { msg.className = 'msg err'; msg.textContent = 'Fichier trop volumineux (8 Mo maximum).'; return; }
    msg.className = 'msg'; msg.textContent = 'Import en cours…';
    $('impBtn').disabled = true;
    try {
      const r = await api('/import?accountId=' + encodeURIComponent($('impAcc').value), { method: 'POST', body: await file.arrayBuffer() });
      msg.textContent = '';
      out.hidden = false;
      out.innerHTML = '<div>Format détecté : <b>' + esc(r.format.toUpperCase()) + '</b></div>' +
        '<div><b>' + r.parsed + '</b> opérations lues, <b>' + r.added + '</b> ajoutées, <b>' + r.duplicates + '</b> déjà connues (ignorées).</div>' +
        '<div><b>' + r.categorized + '</b> classées par les règles, <b>' + r.toSort + '</b> à trier au total.</div>' +
        (r.toSort ? '<div><a href="#tri">Aller trier</a></div>' : '');
      setBadge(r.toSort);
      $('impFile').value = '';
    } catch (e) { msg.className = 'msg err'; msg.textContent = e.message; }
    finally { $('impBtn').disabled = false; }
  }

  /* ================= Saisie dans l'onglet Budget et les réglages ================= */
  function onInput(e) {
    const t = e.target, v = parseFloat(t.value);
    const clampMoney = () => (isFinite(v) ? Math.max(0, v) : 0);
    if (t.dataset.inc) S.income[t.dataset.inc] = clampMoney();
    else if (t.dataset.amount) { const c = catById(t.dataset.amount); if (c) c.amount = clampMoney(); }
    else if (t.dataset.pct) { const c = catById(t.dataset.pct); if (c) c.percentage = isFinite(v) ? Math.min(100, Math.max(0, v)) : 0; }
    else if (t.dataset.acc) { const c = catById(t.dataset.acc); if (c) c.accountId = t.value ? +t.value : null; }
    else if (t.dataset.open) S.potOpening[t.dataset.open] = isFinite(v) ? v : 0;
    else if (t.id === 'pDay') { if (!isFinite(v) || v < 1 || v > 28) return; S.startDay = Math.round(v); M = currentKey(); $('xDate').value = defaultDate(); }
    else if (t.id === 'potStart') { if (!/^\d{4}-\d{2}$/.test(t.value)) return; S.potStart = t.value; }
    else if (t.id === 'lddsDone') { if (t.checked) S.lddsDone[M] = true; else delete S.lddsDone[M]; }
    else if (t.id === 'primes') S.annualBonus = clampMoney();
    else if (t.id === 'bPropagate') { PROPAGATE = t.checked; }
    else if (t.id === 'incPrimes') S.includeBonus = t.checked;
    else return;
    scheduleSave();
    renderBudget();
    if (!$('view-suivi').hidden) saveThenReloadSuivi();
  }

  let reloadT = 0;
  function saveThenReloadSuivi() {
    clearTimeout(reloadT);
    reloadT = setTimeout(async () => { await flushSave(); await loadSuivi(); }, 700);
  }

  async function resetBudget() {
    if (!window.confirm('Revenir aux montants et aux pourcentages de départ à partir de ' + periodLabel(BP) + ' ? Les périodes d’avant, le réglage des pots et les virements cochés ne changent pas.')) return;
    try {
      await flushSave();
      applyBudget(await api('/budget/reset', { method: 'POST', json: { period: BP } }));
    } catch (e) { setSync('bad', 'Non enregistré'); }
  }


  /* ================= Budget par période : navigation, ajout et gestion des catégories ================= */
  function periodLabel(key) {
    const day = (S && S.startDay) || 1, p = key.split('-');
    const a = new Date(+p[0], +p[1] - 1, day), e = new Date(+p[0], +p[1], day - 1);
    return a.getDate() + ' ' + MSH[a.getMonth()] + ' → ' + e.getDate() + ' ' + MSH[e.getMonth()] + ' ' + e.getFullYear();
  }

  /** Affiche le budget reçu du serveur (valeurs en vigueur à la période BP) et reconstruit tout ce qui en dépend. */
  function applyBudget(b) {
    S = b.settings; CATS = b.categories; BP = b.period; LATER = b.laterChanges || []; PROPAGATE = false;
    buildInputs(); buildTable(); fillInputs(); buildSelects(); fillPot();
    renderBudgetBar(); renderLater(); renderManage(); syncAddForm();
    if (tab === 'budget') renderBudget();
  }

  function renderBudgetBar() {
    const cur = currentKey();
    $('bLabel').textContent = periodLabel(BP);
    $('bToday').disabled = BP === cur;
    $('bNote').textContent = BP < cur
      ? 'Période passée. Tes modifications valent à partir d’ici et corrigent l’historique, jusqu’à un changement déjà prévu.'
      : BP === cur
        ? 'Période en cours. Tes modifications valent pour ce mois-ci et les suivants ; les périodes d’avant gardent leurs valeurs.'
        : 'Période à venir. Tes modifications valent à partir d’ici ; ce mois-ci ne change pas.';
  }

  function renderLater() {
    const box = $('bLater');
    if (!LATER.length) { box.hidden = true; box.innerHTML = ''; return; }
    const first = LATER[0].from;
    const names = Array.from(new Set(LATER.map((l) => (l.kind === 'income' ? 'Revenus' : catLabel(l.categoryId))))).map(esc).join(', ');
    box.hidden = false;
    box.innerHTML = '<div><strong>Des changements sont déjà prévus</strong> à partir de ' + esc(periodLabel(first)) + ' pour : ' + names + '. Tes modifications ne s’appliqueront pas au-delà de cette date.</div>' +
      '<label class="chk" for="bPropagate"><input type="checkbox" id="bPropagate"><span>Les appliquer aussi aux périodes suivantes<small>Remplace ces changements prévus.</small></span></label>';
  }

  /** Recharge le budget affiché (compteurs d'opérations, catégories créées ailleurs) sans changer de période. */
  async function refreshBudgetData() {
    try { await flushSave(); applyBudget(await api('/budget?period=' + BP)); } catch (e) { /* l'écran garde ses valeurs */ }
  }

  async function gotoBudgetPeriod(key) {
    await flushSave();
    try { applyBudget(await api('/budget?period=' + key)); } catch (e) { setSync('bad', e.message); }
  }

  /* ---- ajout ---- */
  const NC = { fixe: ['Montant mensuel', '€'], variable: ['Part de l’enveloppe', '%'], pot: ['Part de l’enveloppe', '%'], annual: ['Montant annuel', '€/an'], ignore: null };

  function newCatDraft() {
    const type = $('ncType').value, v = parseAmt($('ncVal').value), val = isFinite(v) && v > 0 ? v : 0;
    return {
      id: '__new', group: type === 'annual' ? 'pot' : type, annual: type === 'annual', active: true, currentAverage: 0,
      percentage: type === 'variable' || type === 'pot' ? Math.min(100, val) : 0, amount: type === 'fixe' || type === 'annual' ? val : 0,
    };
  }

  function syncAddForm() {
    if (!$('ncType')) return;
    const type = $('ncType').value, meta = NC[type];
    $('ncValWrap').hidden = !meta;
    if (meta) { $('ncValLabel').textContent = meta[0]; $('ncUnit').textContent = meta[1]; }
    $('ncAccWrap').hidden = type !== 'fixe';
    $('ncOpenWrap').hidden = type !== 'pot';
    renderImpact();
  }

  function renderImpact() {
    const box = $('ncImpact');
    if (!S) return;
    const type = $('ncType').value;
    if (type === 'ignore') { box.innerHTML = 'Une catégorie « hors budget » ne change ni l’enveloppe ni l’épargne.'; return; }
    const a = compute(), b = compute([newCatDraft()]);
    const delta = (x, y) => (Math.abs(x - y) < 0.5 ? '<b>' + f(x) + '</b> (inchangé)' : '<b>' + f(x) + '</b> → <b' + (y < 0 ? ' class="neg"' : '') + '>' + f(y) + '</b>');
    let h = '<div>Sur ' + esc(periodLabel(BP)) + ' : enveloppe ' + delta(a.env, b.env) + ' · budget variable ' + delta(a.varTotal, b.varTotal) + '</div>' +
      '<div>Épargne courante ' + delta(a.saveAmt, b.saveAmt) + ' (' + pc(a.savePct) + ' → ' + pc(b.savePct) + ')</div>';
    if (b.savePct < 0 || b.env <= 0) h += '<div class="neg"><b>Le budget devient déficitaire.</b></div>';
    box.innerHTML = h;
  }

  async function submitNewCat(ev) {
    ev.preventDefault();
    const msg = $('ncMsg'), type = $('ncType').value, d = newCatDraft();
    const num = (id) => { const v = parseAmt($(id).value); return isFinite(v) && v > 0 ? v : 0; };
    const body = {
      label: $('ncLabel').value.trim(), group: d.group, annual: d.annual, from: BP, note: $('ncNote').value.trim() || null,
      percentage: d.percentage, amount: d.amount, accountId: type === 'fixe' && $('ncAcc').value ? +$('ncAcc').value : null,
      opening: type === 'pot' ? num('ncOpen') : null,
    };
    $('ncGo').disabled = true;
    msg.className = 'msg'; msg.textContent = '';
    try {
      await flushSave();
      await api('/categories', { method: 'POST', json: body });
      $('catForm').reset();
      applyBudget(await api('/budget?period=' + BP));
      msg.textContent = '« ' + body.label + ' » ajoutée à partir de ' + periodLabel(BP) + '.';
    } catch (e) { msg.className = 'msg err'; msg.textContent = e.message; }
    finally { $('ncGo').disabled = false; }
  }

  /* ---- gestion ---- */
  function renderManage() {
    const mg = $('mgMsg'); if (mg) mg.textContent = '';
    let h = '';
    GROUPS.forEach(([g, label]) => {
      const rows = CATS.filter((c) => c.group === g);
      if (!rows.length) return;
      h += '<h3 class="gh sp">' + esc(label) + '</h3>';
      h += rows.map((c) => {
        const bits = [];
        if (c.group === 'fixe') bits.push(f(c.amount) + ' / mois');
        else if (c.annual) bits.push(f(c.amount) + ' / an');
        else if (c.group !== 'ignore') bits.push(pc(c.percentage) + ' de l’enveloppe');
        if (!c.active) bits.push(c.endedFrom ? 'arrêtée à partir de ' + periodLabel(c.endedFrom) : 'existe à partir de ' + periodLabel(c.activeFrom));
        else if (c.activeFrom) bits.push('créée à partir de ' + periodLabel(c.activeFrom));
        bits.push(c.usedBy + ' opération' + (c.usedBy > 1 ? 's' : ''));
        const btn = (act, text, cls) => '<button class="btn sm' + (cls ? ' ' + cls : '') + '" type="button" data-mg="' + act + '" data-id="' + esc(c.id) + '">' + text + '</button>';
        const acts = btn('rename', 'Renommer') + btn('merge', 'Fusionner') +
          (c.active ? btn('stop', 'Arrêter') : c.endedFrom ? btn('restore', 'Réactiver') : '') +
          (c.usedBy === 0 ? btn('delete', 'Supprimer', 'danger') : '');
        return '<div class="mg' + (c.active ? '' : ' off') + '"><div class="nm"><b>' + esc(c.label) + '</b><span>' + bits.map(esc).join(' &middot; ') + '</span></div><span class="acts">' + acts + '</span></div>';
      }).join('');
    });
    $('manageList').innerHTML = h;
  }

  async function onManageClick(ev) {
    const b = ev.target.closest('[data-mg]');
    if (!b) return;
    const c = catById(b.dataset.id), act = b.dataset.mg, msg = $('mgMsg');
    if (!c) return;
    const run = async (fn, ok) => {
      msg.className = 'msg';
      try { await flushSave(); await fn(); applyBudget(await api('/budget?period=' + BP)); refreshUnsorted(); msg.textContent = ok; }
      catch (e) { msg.className = 'msg err'; msg.textContent = e.message; }
    };
    if (act === 'rename') {
      const name = window.prompt('Nouveau nom de la catégorie', c.label);
      if (name && name.trim() && name.trim() !== c.label) await run(() => api('/categories/' + encodeURIComponent(c.id), { method: 'PUT', json: { label: name.trim() } }), 'Catégorie renommée.');
    } else if (act === 'stop') {
      const extra = c.group === 'pot' ? ' Son solde ne sera plus suivi : fusionne-la plutôt dans un autre pot pour le garder.' : '';
      if (window.confirm('Arrêter « ' + c.label + ' » à partir de ' + periodLabel(BP) + ' ? Les périodes d’avant la gardent.' + extra))
        await run(() => api('/categories/' + encodeURIComponent(c.id) + '/archive', { method: 'POST', json: { from: BP } }), '« ' + c.label + ' » arrêtée à partir de ' + periodLabel(BP) + '.');
    } else if (act === 'restore') {
      await run(() => api('/categories/' + encodeURIComponent(c.id) + '/restore', { method: 'POST', json: { from: BP } }), '« ' + c.label + ' » réactivée à partir de ' + periodLabel(BP) + '.');
    } else if (act === 'delete') {
      if (window.confirm('Supprimer définitivement « ' + c.label + ' » ?')) await run(() => api('/categories/' + encodeURIComponent(c.id), { method: 'DELETE' }), 'Catégorie supprimée.');
    } else if (act === 'merge') openMerge(c);
  }

  function openMerge(c) {
    const others = CATS.filter((x) => x.id !== c.id);
    $('mergeTitle').textContent = 'Fusionner « ' + c.label + ' »';
    $('mergeText').textContent = c.usedBy + ' opération' + (c.usedBy > 1 ? 's' : '') + ', ses règles et ses correspondances bancaires passeront dans la catégorie choisie. « ' + c.label + ' » disparaîtra. Les opérations comptent ensuite selon la catégorie choisie, y compris dans l’historique.';
    $('mergeInto').innerHTML = GROUPS.map(([g, label]) => {
      const rows = others.filter((x) => x.group === g);
      return rows.length ? '<optgroup label="' + esc(label) + '">' + rows.map((x) => '<option value="' + esc(x.id) + '">' + esc(x.label) + (x.active ? '' : ' (arrêtée)') + '</option>').join('') + '</optgroup>' : '';
    }).join('');
    $('mergeDlg').dataset.id = c.id;
    $('mergeDlg').showModal();
  }

  async function onMergeClose() {
    const dlg = $('mergeDlg');
    if (dlg.returnValue !== 'ok') return;
    const id = dlg.dataset.id, into = $('mergeInto').value, msg = $('mgMsg');
    try {
      await flushSave();
      const r = await api('/categories/' + encodeURIComponent(id) + '/merge', { method: 'POST', json: { intoId: into } });
      applyBudget(await api('/budget?period=' + BP));
      refreshUnsorted();
      msg.className = 'msg';
      msg.textContent = 'Fusionnée : ' + r.moved + ' opération' + (r.moved > 1 ? 's' : '') + ' déplacée' + (r.moved > 1 ? 's' : '') + ' dans « ' + catLabel(into) + ' ».';
    } catch (e) { msg.className = 'msg err'; msg.textContent = e.message; }
  }

  /* ================= Connexion et navigation ================= */
  function showLogin(message) {
    authed = false;
    $('app').hidden = true;
    $('login').hidden = false;
    $('loginMsg').textContent = message || '';
    $('pin').value = '';
    $('pin').focus();
  }

  async function submitLogin(ev) {
    ev.preventDefault();
    const msg = $('loginMsg');
    msg.textContent = '';
    try {
      await api('/auth/login', { method: 'POST', json: { pin: $('pin').value } });
      await loadAll();
    } catch (e) {
      msg.textContent = e.status === 429 ? 'Trop d’essais. Réessaie dans une minute.' : e.status === 0 ? 'Serveur injoignable.' : e.message;
      $('pin').select();
    }
  }

  const TABS = { suivi: 'view-suivi', budget: 'view-budget', tri: 'view-tri', regles: 'view-regles' };
  const EYEBROW = { suivi: 'Foyer · suivi des dépenses', budget: 'Foyer · budget de base', tri: 'Foyer · opérations à classer', regles: 'Foyer · règles et import' };
  let tab = 'suivi';

  function route() {
    let h = location.hash.slice(1);
    if (!TABS[h]) { try { h = localStorage.getItem('bt-tab'); } catch (e) { h = null; } }
    if (!TABS[h]) h = 'suivi';
    tab = h;
    try { localStorage.setItem('bt-tab', h); } catch (e) { /* facultatif */ }
    Object.keys(TABS).forEach((k) => {
      $(TABS[k]).hidden = k !== h;
      $('tab' + k.charAt(0).toUpperCase() + k.slice(1)).setAttribute('aria-current', k === h ? 'page' : 'false');
    });
    $('budgetActions').hidden = h !== 'budget';
    $('eyebrow').textContent = EYEBROW[h];
    if (h === 'budget') { renderBudget(); refreshBudgetData(); }
    else if (h === 'suivi') flushSave().then(loadSuivi);
    else if (h === 'tri') loadTri();
    else if (h === 'regles') loadRules();
  }

  async function loadAll() {
    const b = await api('/budget');
    TODAY = b.today; M = b.currentPeriod;
    ACCOUNTS = await api('/accounts');
    applyBudget(b);
    $('xDate').value = defaultDate();
    authed = true;
    $('login').hidden = true;
    $('app').hidden = false;
    setSync('ok', 'Synchronisé');
    route();
    refreshUnsorted();
    if (tab !== 'regles') loadBank();   // pour le bandeau d'échéance, visible sur tous les onglets
  }

  async function start() {
    readBankBack();
    try {
      const r = await fetch('/api/auth/status', { credentials: 'same-origin' });
      const st = await r.json();
      if (st.required && !st.authenticated) { showLogin(); return; }
    } catch (e) { showLogin('Serveur injoignable. Réessaie quand tu es connecté.'); return; }
    try { await loadAll(); } catch (e) { if (e.status !== 401) showLogin(e.message); }
  }

  /* ================= Branchements ================= */
  document.addEventListener('input', (e) => { if (authed && e.target.type !== 'checkbox') onInput(e); });
  document.addEventListener('change', (e) => {
    if (!authed) return;
    if (e.target.type === 'checkbox' && !e.target.closest('#xForm, #ruleForm, .xrow')) onInput(e);
    else if (e.target.id === 'jCat') loadSuivi();
    else if (e.target.dataset && e.target.dataset.tri && e.target.value) classify(+e.target.dataset.tri, e.target.value, false);
  });
  $('loginForm').addEventListener('submit', submitLogin);
  $('xForm').addEventListener('submit', submitExp);
  $('xCancel').addEventListener('click', () => { resetForm(true); setMsg(''); $('xDate').value = defaultDate(); });
  $('jList').addEventListener('click', onJournalClick);
  $('fxList').addEventListener('click', onFixedClick);
  let qT = 0;
  $('jQ').addEventListener('input', () => { clearTimeout(qT); qT = setTimeout(loadSuivi, 250); });
  const go = (k) => () => { M = mshift(M, k); pendDel = null; $('xDate').value = defaultDate(); loadSuivi(); };
  $('mPrev').addEventListener('click', go(-1));
  $('mNext').addEventListener('click', go(1));
  $('mToday').addEventListener('click', () => { M = currentKey(); pendDel = null; $('xDate').value = defaultDate(); loadSuivi(); });
  $('exportBtn').addEventListener('click', exportCsv);
  $('exportBtn2').addEventListener('click', exportCsv);
  $('reset').addEventListener('click', resetBudget);
  $('triList').addEventListener('click', (e) => {
    const b = e.target.closest('[data-tri-act]');
    if (b) classify(+b.dataset.id, null, true);
  });
  $('ruleList').addEventListener('click', onRuleClick);
  $('bPrev').addEventListener('click', () => gotoBudgetPeriod(mshift(BP, -1)));
  $('bNext').addEventListener('click', () => gotoBudgetPeriod(mshift(BP, 1)));
  $('bToday').addEventListener('click', () => gotoBudgetPeriod(currentKey()));
  $('catForm').addEventListener('submit', submitNewCat);
  ['ncType', 'ncVal'].forEach((id) => $(id).addEventListener('input', syncAddForm));
  $('manageList').addEventListener('click', onManageClick);
  $('mergeDlg').addEventListener('close', onMergeClose);
  $('pasteGo').addEventListener('click', pasteAnalyze);
  const pv = $('pastePreview');
  pv.addEventListener('change', onPasteInput);
  pv.addEventListener('input', (e) => { if (e.target.dataset.bknew !== undefined || e.target.dataset.bkval !== undefined) onPasteInput(e); });
  pv.addEventListener('click', (e) => {
    if (e.target.id === 'pvOk') pasteCommit();
    else if (e.target.id === 'pvCancel') { PV = null; renderPaste(); pasteMsg(''); }
  });
  $('bankList').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-bkdel]');
    if (!b) return;
    try { await api('/bank-categories?key=' + encodeURIComponent(b.dataset.bkdel), { method: 'DELETE' }); await loadBankMap(); } catch (err) { $('bankList').textContent = err.message; }
  });
  $('ruleForm').addEventListener('submit', submitRule);
  $('reapplyBtn').addEventListener('click', reapply);
  $('impBtn').addEventListener('click', doImport);
  $('bankAdd').addEventListener('click', () => bankChoose(null));
  $('bankCancel').addEventListener('click', () => { $('bankConnect').hidden = true; bankMsg(''); });
  $('bankGo').addEventListener('click', () => bankConnect($('bankPick').value, bankReplace));
  $('bankSync').addEventListener('click', bankSyncNow);
  $('bankConns').addEventListener('change', (e) => { if (e.target.dataset && e.target.dataset.blink) bankLink(e.target); });
  $('bankConns').addEventListener('click', (e) => {
    const re = e.target.closest('[data-bconn-re]'), del = e.target.closest('[data-bconn-del]');
    if (re) bankReconnect(+re.dataset.bconnRe); else if (del) bankDisconnect(+del.dataset.bconnDel);
  });
  $('bankAlert').addEventListener('click', (e) => { const re = e.target.closest('[data-bconn-re]'); if (re) bankReconnect(+re.dataset.bconnRe); });
  $('logoutBtn').addEventListener('click', async () => { try { await api('/auth/logout', { method: 'POST' }); } catch (e) { /* déjà déconnecté */ } showLogin(); });
  window.addEventListener('hashchange', () => { if (authed) route(); });
  let raf = 0;
  window.addEventListener('resize', () => {
    cancelAnimationFrame(raf);
    raf = requestAnimationFrame(() => { if (!authed) return; if (tab === 'budget') renderBudget(); else if (tab === 'suivi') renderSuivi(); });
  });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && authed) { refreshUnsorted(); if (tab === 'suivi') loadSuivi(); else if (tab === 'tri') loadTri(); }
  });

  if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => { navigator.serviceWorker.register('sw.js').catch(() => { /* PWA facultative */ }); });
  }
  start();
})();
