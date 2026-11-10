/*
 * Su Ustası — web playable client: rendering, input, game flow, boosters, FTUE, meta.
 * Rules live in core.js (GameSession) — this file only plays the events it returns,
 * so input is never blocked by animations (cells are freed the moment a pipe exits).
 */
(function () {
  'use strict';
  const C = window.SuCore;
  const S = window.SuSolver;
  const A = window.SuAudio;
  const $ = (sel) => document.querySelector(sel);
  const params = new URLSearchParams(location.search);
  const DEV = params.has('dev');

  // =================================================================== persistence (ISaveService mock)
  const SAVE_KEY = 'suUstasi.save.v1';
  const Save = {
    data: null,
    defaults() {
      return {
        coins: C.ECONOMY.startCoins, maxIndex: 0, completed: {}, boosters: Object.assign({}, C.ECONOMY.startBoosters),
        sound: true, seenIntro: {}, boosterGift: {}, noAds: false, ftue: {}, stats: { wins: 0, fails: 0 },
      };
    },
    load() {
      let raw = null;
      try { raw = localStorage.getItem(SAVE_KEY); } catch (e) { /* storage blocked */ }
      try { this.data = Object.assign(this.defaults(), raw ? JSON.parse(raw) : {}); } catch (e) { this.data = this.defaults(); }
      return this.data;
    },
    save() { try { localStorage.setItem(SAVE_KEY, JSON.stringify(this.data)); } catch (e) { /* ignore */ } },
    reset() { this.data = this.defaults(); this.save(); },
  };

  // =================================================================== analytics / ads / iap (mock services)
  const Analytics = {
    log: [],
    track(event, data) {
      if (!C.ANALYTICS_EVENTS.includes(event)) console.warn('[analytics] unknown event', event);
      const entry = Object.assign({ event, t: Math.round(performance.now()) }, data || {});
      this.log.push(entry);
      console.info('[analytics]', event, data || {});
      if (DEV) renderDev();
    },
  };
  window.SU_ANALYTICS = Analytics;

  const adPolicy = new C.AdPolicy();
  const nowSec = () => performance.now() / 1000;

  const IAP_PRODUCTS = [
    { id: 'coins_500', title: '500 Jeton', price: 29.99, coins: 500 },
    { id: 'coins_1500', title: '1500 Jeton', price: 69.99, coins: 1500 },
    { id: 'no_ads', title: 'Reklamsız', price: 49.99, noAds: true },
  ];

  // =================================================================== state
  const G = {
    pack: null,            // { manifest, levels: { file: json } }
    order: [],             // manifest entries in play order
    index: 0,
    session: null,
    raw: null,
    views: new Map(),
    state: 'boot',         // boot | playing | hammer | won | lost | overlay
    xrayUntil: 0,
    pendingValves: [],     // { id, at }
    particles: [],
    sweep: null,           // red swept-path hint after a blocked tap
    tankShown: 1,
    drain: null,
    startedAt: 0,
    boostersUsed: 0,
    revived: false,
    hand: null,            // FTUE pointer target pipe id
    introPulseUntil: 0,
    customLevel: false,
  };
  window.SU_GAME = G;
  // automation / QA hook: SU_GAME.debug.tap('p3'), SU_GAME.debug.start(index)
  G.debug = {
    tap: (id) => { const p = G.session && G.session.board.get(id); if (p && (G.state === 'playing' || G.state === 'hammer')) (G.state === 'hammer' ? useHammer(p) : handleTap(p)); return G.session && G.session.status; },
    start: (i) => startLevel(i),
    analytics: () => Analytics.log.map((e) => e.event),
  };

  // =================================================================== DOM refs + canvas
  const app = $('#app');
  const canvas = $('#board');
  const ctx = canvas.getContext('2d');
  const fx = $('#fx');
  const fctx = fx.getContext('2d');
  const wrap = $('#board-wrap');
  const geo = { cs: 40, ox: 0, oy: 0, w: 0, h: 0, dpr: 1 };

  function resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2.5);
    const r = wrap.getBoundingClientRect();
    geo.w = r.width; geo.h = r.height; geo.dpr = dpr;
    canvas.width = Math.round(r.width * dpr); canvas.height = Math.round(r.height * dpr);
    const ar = app.getBoundingClientRect();
    fx.width = Math.round(ar.width * dpr); fx.height = Math.round(ar.height * dpr);
    layoutBoard();
  }
  function layoutBoard() {
    if (!G.session) return;
    const b = G.session.board;
    const margin = 14;
    geo.cs = Math.max(16, Math.floor(Math.min((geo.w - margin * 2) / b.width, (geo.h - margin * 2) / b.height)));
    geo.ox = Math.round((geo.w - geo.cs * b.width) / 2);
    geo.oy = Math.round((geo.h - geo.cs * b.height) / 2);
  }
  new ResizeObserver(resize).observe(wrap);
  window.addEventListener('resize', resize);

  // =================================================================== colour helpers
  const INK = '#4A2E22';
  const UI_FONT = getComputedStyle(document.body).fontFamily || 'system-ui, sans-serif';
  const BRASS = '#C9A13B';
  const PAIR_TAGS = ['#E5484D', '#8E5BD9', '#2F9C9A', '#E8743B'];
  const THEME_STYLE = {
    'Banyo': { frame: '#E7C9A1', edge: '#B8875F', cell: '#F8E6CA' },
    'Mutfak': { frame: '#EBBE9E', edge: '#B9785A', cell: '#FBE6D4' },
    'Apartman Bodrumu': { frame: '#D2C0AB', edge: '#8F7A66', cell: '#F1E6D7' },
    'Fabrika': { frame: '#C4CAD1', edge: '#7D8792', cell: '#EEF0F3' },
    'Şehir Kanalı': { frame: '#BFD2B5', edge: '#7C946F', cell: '#EAF1E4' },
    'Arıtma Tesisi': { frame: '#AFD6D9', edge: '#5F9EA3', cell: '#E5F4F5' },
  };
  function hexRgb(h) { const n = parseInt(h.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; }
  function shade(h, amt) {
    const [r, g, b] = hexRgb(h);
    const f = (c) => Math.round(amt >= 0 ? c + (255 - c) * amt : c * (1 + amt));
    return `rgb(${f(r)},${f(g)},${f(b)})`;
  }
  function rgba(h, a) { const [r, g, b] = hexRgb(h); return `rgba(${r},${g},${b},${a})`; }
  const ease = { inQuad: (p) => p * p, outBack: (p) => 1 + 2.7 * Math.pow(p - 1, 3) + 1.7 * Math.pow(p - 1, 2), outCubic: (p) => 1 - Math.pow(1 - p, 3) };
  const DIR_ANGLE = { right: 0, down: Math.PI / 2, left: Math.PI, up: -Math.PI / 2 };

  // seeded per-pipe decoration noise (rust spots etc.)
  function decoRng(seed) { let s = (seed * 2654435761) >>> 0; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296); }

  // =================================================================== pipe views
  class PipeView {
    constructor(pipe, pairTag) {
      this.pipe = pipe;
      this.id = pipe.id;
      this.pairTag = pairTag;
      this.exit = null;        // { t0, dur, dir, cause }
      this.shakeT0 = -9; this.shakeDir = pipe.dir;
      this.flashT0 = -9;
      this.badgeT0 = -9;
      this.chainT0 = -9;
      this.flakeT0 = -9;
      this.angle = DIR_ANGLE[pipe.dir];
      this.angleFrom = this.angle; this.rotT0 = -9;
      this.wheel = 0;
      const rnd = decoRng(pipe.index + 7);
      this.spots = Array.from({ length: 6 }, () => [rnd(), rnd(), 0.5 + rnd() * 0.6]);
      this.dripPhase = rnd();
      this.done = false;
    }
    get removedVisual() { return this.done; }
  }

  function buildViews() {
    G.views.clear();
    const pairs = new Map();
    let tag = 0;
    for (const p of G.session.board.pipes) if (p.type === 'key_valve') pairs.set(p.targetId, PAIR_TAGS[tag++ % PAIR_TAGS.length]);
    for (const p of G.session.board.pipes) {
      const t = p.type === 'locked' ? pairs.get(p.id) : p.type === 'key_valve' ? pairs.get(p.targetId) : null;
      G.views.set(p.id, new PipeView(p, t));
    }
  }

  // =================================================================== level pack loading
  async function loadPack() {
    try {
      const res = await fetch('levels/manifest.json', { cache: 'no-store' });
      if (!res.ok) throw new Error(res.status);
      const manifest = await res.json();
      const entries = await Promise.all(manifest.levels.map(async (l) => [l.file, await (await fetch('levels/' + l.file, { cache: 'no-store' })).json()]));
      return { manifest, levels: Object.fromEntries(entries), source: 'json' };
    } catch (e) {
      // file:// — browsers refuse fetch(); fall back to the generated bundle (same data).
      await new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = 'levels/levels.bundle.js';
        s.onload = resolve; s.onerror = () => reject(new Error('levels.bundle.js missing — run: node tools/generate_levels.mjs'));
        document.head.appendChild(s);
      });
      return Object.assign({ source: 'bundle' }, window.SU_LEVEL_PACK);
    }
  }

  // =================================================================== game flow (web GameStateMachine)
  function startLevel(index, rawOverride) {
    clearTimers();
    G.customLevel = !!rawOverride;
    if (!rawOverride) G.index = Math.max(0, Math.min(index, G.order.length - 1));
    const entry = G.order[G.index];
    const raw = rawOverride || G.pack.levels[entry.file];
    let session;
    try { session = new C.GameSession(raw); } catch (e) { toast('Geçersiz seviye: ' + (e.errors ? e.errors[0] : e.message)); return; }
    G.raw = raw;
    G.session = session;
    G.state = 'playing';
    G.xrayUntil = 0; G.pendingValves = []; G.particles = []; G.sweep = null; G.drain = null;
    G.tankShown = 1; G.startedAt = nowSec(); G.boostersUsed = 0; G.revived = false; G.hand = null;
    setHammer(false);
    buildViews();
    layoutBoard();
    hideOverlay();
    hideTooltip();
    updateHud();
    const n = session.levelNumber;
    Analytics.track('level_start', { level: n, pipes: session.board.pipes.length, theme: C.themeFor(n), custom: G.customLevel });
    if (DEV) renderDev();
    const intro = !G.customLevel && mechanicIntroFor(n);
    if (intro && !Save.data.seenIntro[intro.type]) showMechanicIntro(intro);
    else afterIntro();
  }

  function afterIntro() {
    const n = G.session.levelNumber;
    giftBoosterIfUnlocked(n);
    startFtue(n);
  }

  function clearTimers() { G.timers && G.timers.forEach(clearTimeout); G.timers = []; }
  function later(fn, ms) { const t = setTimeout(fn, ms); (G.timers || (G.timers = [])).push(t); return t; }

  function onWin() {
    G.state = 'won';
    setHammer(false);
    hideTooltip();
    const n = G.session.levelNumber;
    const reward = C.winReward(n, G.session.mistakes);
    later(() => { G.drain = { t0: nowSec(), from: G.tankShown }; A.drain(); }, 450);
    later(() => {
      if (!G.customLevel) {
        Save.data.coins += reward.total;
        Save.data.completed[n] = true;
        Save.data.maxIndex = Math.max(Save.data.maxIndex, Math.min(G.index + 1, G.order.length - 1));
        Save.data.stats.wins++;
        Save.save();
      }
      Analytics.track('level_complete', {
        level: n, mistakes: G.session.mistakes, taps: G.session.taps, leaksLeft: G.session.leaks,
        seconds: Math.round(nowSec() - G.startedAt), boostersUsed: G.boostersUsed, coins: reward.total,
      });
      if (n === C.RULES.ftueLevels) ftueStep('ftue_complete');
      showWin(reward);
    }, 1900);
  }

  function onLose() {
    G.state = 'lost';
    setHammer(false);
    hideTooltip();
    Save.data.stats.fails++;
    Save.save();
    Analytics.track('level_fail', {
      level: G.session.levelNumber, taps: G.session.taps, waterLeft: Math.round(G.session.waterRatio * 100) / 100,
      seconds: Math.round(nowSec() - G.startedAt),
    });
    // GDD: never show an interstitial on fail.
    void adPolicy.onLevelFail();
    later(() => { A.lose(); showLose(); }, 650);
  }

  function nextLevel() {
    const n = G.session ? G.session.levelNumber : 0;
    const proceed = () => startLevel(G.customLevel ? G.index : G.index + 1);
    if (!G.customLevel && !Save.data.noAds && adPolicy.onLevelWin(n, nowSec())) {
      showInterstitial('level_complete', () => { adPolicy.markShown(nowSec()); proceed(); });
    } else proceed();
  }

  // =================================================================== input
  canvas.addEventListener('pointerdown', (e) => {
    A.unlock();
    if (!G.session || (G.state !== 'playing' && G.state !== 'hammer')) return;
    const r = canvas.getBoundingClientRect();
    const px = e.clientX - r.left, py = e.clientY - r.top;
    const cx = Math.floor((px - geo.ox) / geo.cs), cy = Math.floor((py - geo.oy) / geo.cs);
    let pipe = G.session.board.pipeAt(cx, cy);
    if (!pipe) pipe = nearestPipe(px, py);
    if (!pipe) return;
    if (G.state === 'hammer') useHammer(pipe);
    else handleTap(pipe);
  });

  /** Forgiving touch: a tap within 0.3 cells of a pipe edge still counts. */
  function nearestPipe(px, py) {
    const b = G.session.board;
    let best = null, bestD = geo.cs * 0.3;
    for (const p of b.pipes) {
      if (p.removed) continue;
      for (const [x, y] of p.cells) {
        const dx = Math.max(geo.ox + x * geo.cs - px, 0, px - (geo.ox + (x + 1) * geo.cs));
        const dy = Math.max(geo.oy + y * geo.cs - py, 0, py - (geo.oy + (y + 1) * geo.cs));
        const d = Math.hypot(dx, dy);
        if (d < bestD) { bestD = d; best = p; }
      }
    }
    return best;
  }

  function handleTap(pipe) {
    const s = G.session;
    const view = G.views.get(pipe.id);
    const t = nowSec();
    const preScan = s.board.scan(pipe);
    const res = s.tap(pipe.id);
    if (res.kind === 'ignored') return;
    switch (res.kind) {
      case 'exit':
        if (G.hand === pipe.id) { G.hand = null; ftueStep('l1_first_exit'); later(() => showTooltip('Harika! Depodaki su azalıyor. Hepsini boşalt!'), 300); later(hideTooltip, 3200); }
        break;
      case 'blocked':
        view.shakeT0 = t; view.shakeDir = pipe.dir;
        for (const id of res.blockingPipeIds) { const v = G.views.get(id); if (v) v.flashT0 = t; }
        G.sweep = { t0: t, cells: preScan.sweptCells, blockers: new Set(res.blockingPipeIds), tank: preScan.hitsTank };
        A.bonk();
        loseLifeFx(res.leakCost);
        Analytics.track('blocked_tap', { level: s.levelNumber, pipeId: pipe.id, reason: 'blocked', blockers: res.blockingPipeIds, leakCost: res.leakCost });
        if (s.levelNumber === 2 && !Save.data.ftue.l2_blocked_tap) { ftueStep('l2_blocked_tap'); showTooltip('Takıldı! Önündeki boru yolu kapatıyor. Normalde bu 1 damlaya mal olur.'); later(hideTooltip, 3800); }
        break;
      case 'locked': {
        view.shakeT0 = t; view.shakeDir = pipe.dir;
        const key = s.board.pipes.find((k) => k.type === 'key_valve' && k.targetId === pipe.id && !k.removed);
        if (key) G.views.get(key.id).flashT0 = t;
        A.bonk();
        loseLifeFx(res.leakCost);
        Analytics.track('blocked_tap', { level: s.levelNumber, pipeId: pipe.id, reason: 'locked', leakCost: res.leakCost });
        toastOnce('locked', 'Zincirli! Önce aynı renkli anahtar vanalı boruyu çıkar.');
        break;
      }
      case 'frozen':
        view.shakeT0 = t; view.shakeDir = pipe.dir; view.badgeT0 = t;
        burst(pipe, '#FFFFFF', 10, 0.6);
        A.ice(); A.bonk();
        loseLifeFx(res.leakCost);
        Analytics.track('blocked_tap', { level: s.levelNumber, pipeId: pipe.id, reason: 'frozen', iceCount: pipe.iceCount, leakCost: res.leakCost });
        toastOnce('frozen', `Donmuş! ${pipe.iceCount} boru daha çıkınca buz çözülecek.`);
        break;
      case 'rust_broken':
        view.flakeT0 = t;
        burst(pipe, C.PALETTE.rusty, 16, 0.9);
        A.crack();
        toastOnce('rusty', 'Pas kırıldı! Yol açıksa bir dokunuşla kayar.');
        break;
      case 'valve_rotated':
        view.angleFrom = view.angle;
        view.angle = view.angleFrom + Math.PI / 2; // always clockwise
        view.rotT0 = t;
        A.rotate();
        if (res.autoExit) {
          // Logic timer (not the render loop) so throttled/background frames never stall gameplay.
          G.pendingValves.push({ id: pipe.id, at: t + res.delay });
          const session = s;
          later(() => {
            G.pendingValves = G.pendingValves.filter((q) => q.id !== pipe.id);
            if (G.session !== session || session.status !== 'playing') return;
            handleEvents(session.completeValveExit(pipe.id));
            updateHud();
          }, res.delay * 1000);
        }
        break;
    }
    handleEvents(res.events);
    updateHud();
  }

  function handleEvents(events) {
    const s = G.session;
    const t = nowSec();
    let iceSfx = false;
    for (const ev of events) {
      const view = ev.pipeId ? G.views.get(ev.pipeId) : null;
      switch (ev.kind) {
        case 'removed':
          startExit(view, ev.cause);
          if (ev.cause === 'tap' || ev.cause === 'valve') A.water(s.combo);
          break;
        case 'ice_cracked': view.badgeT0 = t; iceSfx = true; break;
        case 'thawed': view.badgeT0 = t; burst(view.pipe, '#FFFFFF', 18, 1.1); A.thaw(); break;
        case 'unlocked': view.chainT0 = t; A.unlockChain(); break;
        case 'leak_burst':
          view.badgeT0 = t; view.shakeT0 = t;
          burst(view.pipe, C.PALETTE.water, 22, 1.4);
          A.leak();
          loseLifeFx(ev.leakCost);
          toastOnce('leak', 'Sızıntı! Yeşil boru sayacı bitmeden onu çıkar.');
          break;
        case 'won': onWin(); break;
        case 'lost': onLose(); break;
      }
    }
    if (iceSfx) A.ice();
  }

  function startExit(view, cause) {
    if (!view) return;
    view.exit = { t0: nowSec(), dur: cause === 'hammer' ? 0.45 : C.TIMING.exitDuration, dir: view.pipe.dir, cause };
  }

  function loseLifeFx(cost) {
    if (!cost) {
      if (G.session.ftue) { const l = $('#lives'); l.classList.remove('shake'); void l.offsetWidth; l.classList.add('shake'); }
      return;
    }
    const l = $('#lives');
    l.classList.remove('shake'); void l.offsetWidth; l.classList.add('shake');
    renderLives(true);
  }

  // =================================================================== boosters
  const BOOSTER_INFO = {
    xray: { name: 'Röntgen', desc: 'Şu an çıkabilen tüm boruları 3 saniye gösterir.' },
    hammer: { name: 'Çekiç', desc: 'Seçtiğin boruyu engellere bakmadan kırar.' },
    patch: { name: 'Yama', desc: '+1 damla can (en fazla 4).' },
  };

  document.querySelectorAll('.booster').forEach((btn) => btn.addEventListener('click', () => {
    A.unlock(); A.click();
    const b = btn.dataset.booster;
    if (!G.session || (G.state !== 'playing' && G.state !== 'hammer')) return;
    const n = G.session.levelNumber;
    if (!C.boosterUnlocked(b, n) && !G.customLevel) { toast(`${BOOSTER_INFO[b].name} ${C.BOOSTERS[b].unlock}. seviyede açılır.`); return; }
    if (b === 'hammer' && G.state === 'hammer') { setHammer(false); return; }
    if ((Save.data.boosters[b] || 0) <= 0) { showBoosterShop(b); return; }
    if (b === 'xray') {
      Save.data.boosters.xray--; Save.save();
      G.xrayUntil = nowSec() + C.TIMING.xrayDuration;
      A.xray();
      trackBooster('xray');
      if (!G.session.exitablePipeIds().length) toast('Şu an çıkabilen boru yok — önce pası kır ya da vanayı çevir.');
    } else if (b === 'hammer') {
      setHammer(true);
      toast('Kırmak istediğin boruya dokun.');
    } else if (b === 'patch') {
      if (!G.session.patch()) { toast('Damlalar zaten dolu (en fazla 4).'); return; }
      Save.data.boosters.patch--; Save.save();
      A.patch();
      trackBooster('patch');
      renderLives(false, true);
    }
    updateHud();
  }));

  function trackBooster(b) { G.boostersUsed++; Analytics.track('booster_used', { booster: b, level: G.session.levelNumber, left: Save.data.boosters[b] }); }

  function setHammer(on) {
    if (on && G.state === 'playing') G.state = 'hammer';
    if (!on && G.state === 'hammer') G.state = 'playing';
    app.dataset.state = G.state;
    document.querySelector('.booster[data-booster="hammer"]').classList.toggle('active', G.state === 'hammer');
  }

  function useHammer(pipe) {
    const res = G.session.hammer(pipe.id);
    setHammer(false);
    if (!res) return;
    Save.data.boosters.hammer--; Save.save();
    A.hammer();
    burst(pipe, C.pipeColorHex(pipe.type, pipe.index, G.session.hasLeaking, pipe.rustIntact), 26, 1.6);
    trackBooster('hammer');
    G.pendingValves = G.pendingValves.filter((v) => v.id !== pipe.id);
    handleEvents(res.events);
    updateHud();
  }

  function giftBoosterIfUnlocked(n) {
    for (const b of Object.keys(C.BOOSTERS)) {
      if (n === C.BOOSTERS[b].unlock && !Save.data.boosterGift[b] && !G.customLevel) {
        Save.data.boosterGift[b] = true;
        Save.data.boosters[b] = (Save.data.boosters[b] || 0) + 1;
        Save.save();
        later(() => toast(`Yeni güçlendirici: ${BOOSTER_INFO[b].name}! 1 tane hediye. ${BOOSTER_INFO[b].desc}`, 4200), 600);
      }
    }
    updateHud();
  }

  // =================================================================== HUD
  function updateHud() {
    if (!G.session) return;
    const n = G.session.levelNumber;
    $('#level-title').textContent = `Seviye ${n}`;
    $('#level-sub').textContent = `${C.themeFor(n)} · ${C.rankFor(n)}${C.isBreather(n) ? ' · Mola' : ''}`;
    $('#coin-count').textContent = Save.data.coins;
    renderLives(false);
    document.querySelectorAll('.booster').forEach((btn) => {
      const b = btn.dataset.booster;
      const locked = !C.boosterUnlocked(b, n) && !G.customLevel;
      btn.classList.toggle('locked', locked);
      btn.querySelector('.lock-tag').textContent = `🔒 Lv ${C.BOOSTERS[b].unlock}`;
      const count = Save.data.boosters[b] || 0;
      const badge = btn.querySelector('.badge');
      badge.textContent = count > 0 ? count : '+';
      badge.classList.toggle('plus', count <= 0);
    });
  }

  let shownLives = -1;
  function renderLives(lostAnim, popAnim) {
    const el = $('#lives');
    const s = G.session;
    const slots = Math.max(s.level.leaks, s.leaks);
    el.classList.toggle('ftue', s.ftue);
    if (el.children.length !== slots) {
      el.innerHTML = '';
      for (let i = 0; i < slots; i++) { const img = document.createElement('img'); img.src = 'assets/sprites/icon_drop.png'; img.alt = ''; el.appendChild(img); }
    }
    [...el.children].forEach((img, i) => {
      const alive = i < s.leaks;
      const wasAlive = !img.classList.contains('lost');
      img.classList.toggle('lost', !alive);
      if (alive) img.classList.remove('drain'); // drain keyframes use fill-mode:forwards
      if (lostAnim && wasAlive && !alive) { img.classList.remove('drain'); void img.offsetWidth; img.classList.add('drain'); }
      if (popAnim && alive && i === s.leaks - 1) { img.classList.remove('pop'); void img.offsetWidth; img.classList.add('pop'); }
    });
    el.setAttribute('aria-label', `Kalan damla: ${s.leaks}`);
    shownLives = s.leaks;
  }

  // =================================================================== main loop + rendering
  let lastT = nowSec();
  function frame() {
    const t = nowSec();
    const dt = Math.min(0.05, t - lastT);
    lastT = t;
    if (G.session) {
      const target = G.drain ? Math.max(0, G.drain.from * (1 - (t - G.drain.t0) / 1.3)) : G.session.waterRatio;
      G.tankShown += (target - G.tankShown) * Math.min(1, dt * (G.drain ? 30 : 5));
      drawBoard(t, dt);
    }
    drawFx(t, dt);
    requestAnimationFrame(frame);
  }

  function roundRect(c, x, y, w, h, r) {
    c.beginPath();
    c.moveTo(x + r, y); c.arcTo(x + w, y, x + w, y + h, r); c.arcTo(x + w, y + h, x, y + h, r);
    c.arcTo(x, y + h, x, y, r); c.arcTo(x, y, x + w, y, r); c.closePath();
  }

  function drawBoard(t, dt) {
    const c = ctx, s = G.session, b = s.board, cs = geo.cs;
    c.setTransform(geo.dpr, 0, 0, geo.dpr, 0, 0);
    c.clearRect(0, 0, geo.w, geo.h);
    const theme = THEME_STYLE[C.themeFor(s.levelNumber)] || THEME_STYLE.Banyo;
    const bw = b.width * cs, bh = b.height * cs, fr = cs * 0.16;

    // board frame + cells
    roundRect(c, geo.ox - fr, geo.oy - fr, bw + fr * 2, bh + fr * 2, cs * 0.32);
    c.fillStyle = theme.frame; c.fill();
    c.lineWidth = 3; c.strokeStyle = theme.edge; c.stroke();
    const g = cs * 0.045;
    for (let y = 0; y < b.height; y++) {
      for (let x = 0; x < b.width; x++) {
        if (b.isTank(x, y)) continue;
        roundRect(c, geo.ox + x * cs + g, geo.oy + y * cs + g, cs - 2 * g, cs - 2 * g, cs * 0.16);
        c.fillStyle = theme.cell; c.fill();
      }
    }

    // swept-path hint after a blocked tap
    if (G.sweep) {
      const p = (t - G.sweep.t0) / 0.75;
      if (p >= 1) G.sweep = null;
      else {
        c.fillStyle = rgba(C.PALETTE.error, 0.28 * (1 - p));
        for (const [x, y] of G.sweep.cells) {
          roundRect(c, geo.ox + x * cs + g * 2, geo.oy + y * cs + g * 2, cs - 4 * g, cs - 4 * g, cs * 0.14);
          c.fill();
        }
      }
    }

    drawTank(t);

    const ox = geo.ox, oy = geo.oy;
    const exiting = [];
    for (const v of G.views.values()) {
      if (v.done) continue;
      if (v.exit) { exiting.push(v); continue; }
      drawPipe(c, v, t, ox, oy);
    }
    for (const v of exiting) drawPipe(c, v, t, ox, oy);

    // FTUE pointer
    if (G.hand) {
      const pv = G.views.get(G.hand);
      if (pv && !pv.pipe.removed) {
        const mid = pv.pipe.path[Math.floor(pv.pipe.path.length / 2)];
        const hx = ox + (mid[0] + 0.5) * cs, hy = oy + (mid[1] + 0.5) * cs;
        const k = (Math.sin(t * 5) + 1) / 2;
        c.beginPath(); c.arc(hx, hy, cs * (0.25 + 0.2 * k), 0, Math.PI * 2);
        c.strokeStyle = rgba('#FFFFFF', 0.9 - 0.5 * k); c.lineWidth = 4; c.stroke();
        c.font = `${Math.round(cs * 0.72)}px system-ui, "Segoe UI Emoji", "Apple Color Emoji", sans-serif`;
        c.textAlign = 'center'; c.textBaseline = 'top';
        c.fillText('👆', hx + cs * 0.12, hy + cs * (0.05 + 0.12 * k));
      } else G.hand = null;
    }

    // particles
    for (const p of G.particles) {
      p.life -= dt; p.vy += p.g * dt; p.x += p.vx * dt; p.y += p.vy * dt;
      if (p.life <= 0) continue;
      const a = Math.min(1, p.life / p.max * 1.6);
      c.fillStyle = rgba(p.color, a);
      c.beginPath();
      if (p.kind === 'drop') { c.ellipse(p.x, p.y, p.r * 0.75, p.r, Math.atan2(p.vy, p.vx) - Math.PI / 2, 0, Math.PI * 2); }
      else { c.rect(p.x - p.r / 2, p.y - p.r / 2, p.r, p.r); }
      c.fill();
    }
    G.particles = G.particles.filter((p) => p.life > 0);
  }

  function drawTank(t) {
    const c = ctx, s = G.session, cs = geo.cs, tk = s.board.tank;
    const inset = cs * 0.1;
    const x = geo.ox + tk.x * cs + inset, y = geo.oy + tk.y * cs + inset;
    const w = tk.w * cs - inset * 2, h = tk.h * cs - inset * 2;
    const r = cs * 0.22;
    // glass
    roundRect(c, x, y, w, h, r);
    c.fillStyle = '#E4F3F9'; c.fill();
    c.save();
    roundRect(c, x + 4, y + 4, w - 8, h - 8, r * 0.7); c.clip();
    // water
    const level = Math.max(0, Math.min(1, G.tankShown));
    const top = y + 4 + (h - 8) * (1 - level);
    const amp = cs * 0.05 * (0.6 + 0.4 * Math.sin(t * 1.3));
    c.beginPath();
    c.moveTo(x, y + h);
    for (let i = 0; i <= 24; i++) {
      const px = x + (w * i) / 24;
      c.lineTo(px, top + Math.sin(t * 3 + i * 0.7) * amp);
    }
    c.lineTo(x + w, y + h); c.closePath();
    const grad = c.createLinearGradient(0, top, 0, y + h);
    grad.addColorStop(0, shade(C.PALETTE.water, 0.25)); grad.addColorStop(0.25, C.PALETTE.water); grad.addColorStop(1, shade(C.PALETTE.water, -0.12));
    c.fillStyle = grad; c.fill();
    // bubbles
    if (level > 0.02) {
      c.strokeStyle = 'rgba(255,255,255,.75)'; c.lineWidth = 1.5;
      for (let i = 0; i < 7; i++) {
        const ph = (t * (0.25 + i * 0.05) + i * 0.37) % 1;
        const bx = x + w * (0.15 + ((i * 0.27) % 0.7)) + Math.sin(t * 2 + i) * 3;
        const by = y + h - ph * (y + h - top);
        c.beginPath(); c.arc(bx, by, cs * (0.035 + (i % 3) * 0.015), 0, Math.PI * 2); c.stroke();
      }
    }
    // drain swirl on win
    if (G.drain) {
      const p = Math.min(1, (t - G.drain.t0) / 1.3);
      c.strokeStyle = rgba('#FFFFFF', 0.6 * (1 - p)); c.lineWidth = 3;
      c.beginPath();
      for (let a = 0; a < Math.PI * 4; a += 0.2) {
        const rr = (a / (Math.PI * 4)) * Math.min(w, h) * 0.38;
        const px = x + w / 2 + Math.cos(a + t * 8) * rr, py = y + h * 0.75 + Math.sin(a + t * 8) * rr * 0.4;
        a === 0 ? c.moveTo(px, py) : c.lineTo(px, py);
      }
      c.stroke();
    }
    c.restore();
    // glass highlight + outline
    c.fillStyle = 'rgba(255,255,255,.45)';
    roundRect(c, x + w * 0.1, y + h * 0.08, w * 0.12, h * 0.55, w * 0.06); c.fill();
    roundRect(c, x, y, w, h, r);
    c.lineWidth = Math.max(3, cs * 0.06); c.strokeStyle = INK; c.stroke();
    roundRect(c, x + 5, y + 5, w - 10, h - 10, r * 0.7);
    c.lineWidth = 2; c.strokeStyle = 'rgba(255,255,255,.8)'; c.stroke();
  }

  // ---- pipe geometry: centerline through cell centers with rounded elbow corners
  function centerline(pipe, cs) {
    const pts = pipe.path.map(([x, y]) => [(x + 0.5) * cs, (y + 0.5) * cs]);
    const n = pts.length;
    const ext = cs * 0.42;
    const unit = (a, b) => { const dx = a[0] - b[0], dy = a[1] - b[1]; const l = Math.hypot(dx, dy) || 1; return [dx / l, dy / l]; };
    const u0 = unit(pts[0], pts[1]), u1 = unit(pts[n - 1], pts[n - 2]);
    return { pts, start: [pts[0][0] + u0[0] * ext, pts[0][1] + u0[1] * ext], end: [pts[n - 1][0] + u1[0] * ext, pts[n - 1][1] + u1[1] * ext], u0, u1 };
  }
  function traceCenterline(c, cl, trim, cs) {
    const { pts, start, end, u0, u1 } = cl;
    c.beginPath();
    c.moveTo(start[0] - u0[0] * trim, start[1] - u0[1] * trim);
    for (let i = 1; i < pts.length - 1; i++) c.arcTo(pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1], cs * 0.5);
    c.lineTo(end[0] - u1[0] * trim, end[1] - u1[1] * trim);
  }

  function pipeColor(pipe) {
    if (pipe.type === 'frozen' && pipe.iceCount === 0) return C.PALETTE[C.waterColorFor(pipe.index, G.session.hasLeaking)];
    return pipe.color && C.PALETTE[pipe.color] ? C.PALETTE[pipe.color] : C.pipeColorHex(pipe.type, pipe.index, G.session.hasLeaking, pipe.rustIntact);
  }

  function drawPipe(c, v, t, ox, oy) {
    const p = v.pipe, cs = geo.cs;
    let dx = 0, dy = 0, alpha = 1, scale = 1;
    if (v.exit) {
      const k = Math.min(1, (t - v.exit.t0) / v.exit.dur);
      if (k >= 1) { v.done = true; return; }
      if (v.exit.cause === 'hammer') { scale = 1 - 0.6 * k; alpha = 1 - k; }
      else {
        const [vx, vy] = C.DIR_VEC[v.exit.dir];
        const b = G.session.board;
        const xs = p.cells.map((q) => q[0]), ys = p.cells.map((q) => q[1]);
        const travel = vx > 0 ? b.width - Math.min(...xs) + 1 : vx < 0 ? Math.max(...xs) + 2 : vy > 0 ? b.height - Math.min(...ys) + 1 : Math.max(...ys) + 2;
        const dist = ease.inQuad(k) * travel * cs;
        dx = vx * dist; dy = vy * dist;
        alpha = k > 0.75 ? 1 - (k - 0.75) / 0.25 : 1;
        if (k < 0.55 && p.water && Math.random() < 0.9) spray(p, dx, dy, v.exit.dir);
        if (k < 0.4 && !p.water && Math.random() < 0.4) spray(p, dx, dy, v.exit.dir, C.PALETTE.dry);
      }
    }
    const sk = t - v.shakeT0;
    if (sk < C.TIMING.shakeDuration) {
      const [vx, vy] = C.DIR_VEC[v.shakeDir];
      const lunge = Math.sin(Math.min(1, sk / 0.12) * Math.PI) * cs * 0.14;
      const wob = Math.sin(sk * 70) * cs * 0.06 * (1 - sk / C.TIMING.shakeDuration);
      dx += vx * lunge + vy * wob; dy += vy * lunge + vx * wob;
    }
    c.save();
    c.globalAlpha = alpha;
    c.translate(ox + dx, oy + dy);
    if (scale !== 1) {
      const mid = p.path[Math.floor(p.path.length / 2)];
      const mx = (mid[0] + 0.5) * cs, my = (mid[1] + 0.5) * cs;
      c.translate(mx, my); c.scale(scale, scale); c.translate(-mx, -my);
    }
    const base = pipeColor(p);
    const xray = t < G.xrayUntil && !v.exit && G.session.wouldExitOnTap(p);
    const intro = t < G.introPulseUntil && v.introPulse;
    if (p.shape === 'single') drawValve(c, v, t, base, xray || intro);
    else drawTube(c, v, t, base, xray || intro);
    // red flash on blockers
    const fk = (t - v.flashT0) / C.TIMING.flashDuration;
    if (fk >= 0 && fk < 1 && !v.exit) {
      c.globalAlpha = alpha * 0.85 * (1 - fk) * (0.6 + 0.4 * Math.sin(fk * 26));
      if (p.shape === 'single') { c.fillStyle = C.PALETTE.error; c.beginPath(); c.arc((p.cells[0][0] + 0.5) * cs, (p.cells[0][1] + 0.5) * cs, cs * 0.45, 0, Math.PI * 2); c.fill(); }
      else {
        const cl = centerline(p, cs);
        c.lineCap = 'round'; c.lineJoin = 'round';
        traceCenterline(c, cl, 0, cs); c.strokeStyle = C.PALETTE.error; c.lineWidth = cs * 0.66; c.stroke();
      }
    }
    c.restore();
    drawBadges(c, v, t, ox + dx, oy + dy, alpha);
  }

  function drawTube(c, v, t, base, glow) {
    const p = v.pipe, cs = geo.cs;
    const T = cs * 0.5, ow = Math.max(2.5, cs * 0.055);
    const cl = centerline(p, cs);
    c.lineJoin = 'round';
    c.lineCap = 'butt';
    if (glow) {
      c.save();
      c.shadowColor = '#FFFFFF'; c.shadowBlur = cs * 0.5;
      traceCenterline(c, cl, -cs * 0.04, cs); c.strokeStyle = rgba('#FFFFFF', 0.55 + 0.35 * Math.sin(t * 8)); c.lineWidth = T + ow * 2 + cs * 0.14; c.stroke();
      c.restore();
    }
    // outline, body, shadow, highlight
    traceCenterline(c, cl, 0, cs); c.strokeStyle = INK; c.lineWidth = T + ow * 2; c.stroke();
    traceCenterline(c, cl, 0, cs); c.strokeStyle = base; c.lineWidth = T; c.stroke();
    c.save();
    c.translate(T * 0.17, T * 0.17);
    traceCenterline(c, cl, T * 0.45, cs); c.strokeStyle = shade(base, -0.16); c.lineWidth = T * 0.3; c.stroke();
    c.restore();
    c.save();
    c.translate(-T * 0.18, -T * 0.18);
    traceCenterline(c, cl, T * 0.45, cs); c.strokeStyle = shade(base, 0.42); c.lineWidth = T * 0.17; c.stroke();
    c.restore();

    // type decorations on the body
    if (p.type === 'rusty') drawRust(c, v, cl, T, p.rustIntact);
    if (p.type === 'dry') drawDust(c, v, cl, T);
    if (p.type === 'frozen' && p.iceCount > 0) drawIce(c, v, cl, T, t);
    if (p.type === 'leaking') drawDrips(c, v, cl, T, t);

    // joint rings at cell boundaries + end flanges
    const pts = cl.pts;
    for (let i = 0; i < pts.length - 1; i++) {
      const mx = (pts[i][0] + pts[i + 1][0]) / 2, my = (pts[i][1] + pts[i + 1][1]) / 2;
      const horiz = pts[i][1] === pts[i + 1][1];
      ring(c, mx, my, horiz, T * 1.16, cs * 0.09, base, ow);
    }
    const endRing = (pt, u) => ring(c, pt[0] - u[0] * cs * 0.06, pt[1] - u[1] * cs * 0.06, Math.abs(u[0]) > 0.5, T * 1.28, cs * 0.13, base, ow);
    endRing(cl.start, cl.u0); endRing(cl.end, cl.u1);

    // direction arrows (darker chevrons, like the HUD reference)
    const [ax, ay] = C.DIR_VEC[p.dir];
    const ang = Math.atan2(ay, ax);
    for (let i = 0; i < pts.length; i++) {
      const corner = i > 0 && i < pts.length - 1 && (pts[i - 1][0] !== pts[i + 1][0] && pts[i - 1][1] !== pts[i + 1][1]);
      if (corner) continue;
      if (pts.length >= 4 && i % 2 === 1) continue;
      arrow(c, pts[i][0], pts[i][1], ang, cs * 0.15, shade(base, -0.38));
    }
    if (p.type === 'locked') drawChain(c, v, cl, T, t);
    if (p.type === 'key_valve') drawKeyHandle(c, v, cl, T);
  }

  function ring(c, x, y, horiz, len, thick, base, ow) {
    const w = horiz ? thick : len, h = horiz ? len : thick;
    roundRect(c, x - w / 2 - ow, y - h / 2 - ow, w + ow * 2, h + ow * 2, Math.min(w, h) * 0.4 + ow);
    c.fillStyle = INK; c.fill();
    roundRect(c, x - w / 2, y - h / 2, w, h, Math.min(w, h) * 0.4);
    c.fillStyle = shade(base, -0.06); c.fill();
    c.fillStyle = shade(base, 0.35);
    if (horiz) c.fillRect(x - w / 2 + 1, y - h / 2 + 2, w * 0.35, h - 4); else c.fillRect(x - w / 2 + 2, y - h / 2 + 1, w - 4, h * 0.35);
  }

  function arrow(c, x, y, ang, size, color) {
    c.save();
    c.translate(x, y); c.rotate(ang);
    c.beginPath();
    c.moveTo(size, 0); c.lineTo(-size * 0.7, -size * 0.85); c.lineTo(-size * 0.7, size * 0.85); c.closePath();
    c.fillStyle = color; c.fill();
    c.restore();
  }

  function pointsAlong(cl, count) { // evenly spaced points along the cell centers
    const out = [];
    const pts = cl.pts;
    for (let i = 0; i < count; i++) {
      const f = (i + 0.5) / count * (pts.length - 1);
      const a = Math.floor(f), b = Math.min(pts.length - 1, a + 1), k = f - a;
      out.push([pts[a][0] + (pts[b][0] - pts[a][0]) * k, pts[a][1] + (pts[b][1] - pts[a][1]) * k, pts[a][1] === pts[b][1]]);
    }
    return out;
  }

  function drawRust(c, v, cl, T, intact) {
    const pts = pointsAlong(cl, v.pipe.size * 2);
    c.save();
    if (intact) {
      v.spots.forEach(([a, b, s], i) => {
        const pt = pts[i % pts.length];
        c.fillStyle = i % 2 ? 'rgba(90,45,20,.55)' : 'rgba(200,120,60,.45)';
        c.beginPath(); c.ellipse(pt[0] + (a - 0.5) * T * 0.8, pt[1] + (b - 0.5) * T * 0.6, T * 0.16 * s, T * 0.1 * s, a * 3, 0, Math.PI * 2); c.fill();
      });
      c.strokeStyle = 'rgba(60,30,15,.8)'; c.lineWidth = 1.6;
      const q = pts[Math.floor(pts.length / 2)];
      c.beginPath(); c.moveTo(q[0] - T * 0.2, q[1] - T * 0.3); c.lineTo(q[0], q[1] - T * 0.05); c.lineTo(q[0] - T * 0.08, q[1] + T * 0.12); c.lineTo(q[0] + T * 0.12, q[1] + T * 0.3); c.stroke();
    } else {
      c.strokeStyle = 'rgba(80,50,30,.45)'; c.lineWidth = 1.4;
      const q = pts[Math.floor(pts.length / 2)];
      c.beginPath(); c.moveTo(q[0] - T * 0.15, q[1] - T * 0.2); c.lineTo(q[0], q[1]); c.lineTo(q[0] + T * 0.1, q[1] + T * 0.18); c.stroke();
    }
    c.restore();
  }

  function drawDust(c, v, cl, T) {
    const pts = pointsAlong(cl, v.pipe.size * 3);
    c.fillStyle = 'rgba(70,78,92,.35)';
    pts.forEach((pt, i) => { const s = v.spots[i % v.spots.length]; c.beginPath(); c.arc(pt[0] + (s[0] - 0.5) * T * 0.7, pt[1] + (s[1] - 0.5) * T * 0.5, T * 0.05 * s[2] + 1, 0, Math.PI * 2); c.fill(); });
  }

  function drawIce(c, v, cl, T, t) {
    const pts = pointsAlong(cl, v.pipe.size * 3);
    c.save();
    c.fillStyle = '#FFFFFF';
    c.strokeStyle = 'rgba(74,46,34,.55)'; c.lineWidth = 1.2;
    for (const [x, y, horiz] of pts) {
      // snow cap along the top (or left) edge
      c.beginPath();
      if (horiz) c.ellipse(x, y - T * 0.42, T * 0.26, T * 0.13, 0, 0, Math.PI * 2);
      else c.ellipse(x - T * 0.42, y, T * 0.13, T * 0.26, 0, 0, Math.PI * 2);
      c.fill();
    }
    // icicles under horizontal runs
    pts.forEach(([x, y, horiz], i) => {
      if (!horiz || i % 2) return;
      const len = T * (0.3 + 0.25 * v.spots[i % v.spots.length][2]);
      c.beginPath(); c.moveTo(x - T * 0.1, y + T * 0.45); c.lineTo(x + T * 0.1, y + T * 0.45); c.lineTo(x, y + T * 0.45 + len); c.closePath();
      c.fillStyle = 'rgba(255,255,255,.95)'; c.fill(); c.stroke();
    });
    // sparkle
    c.fillStyle = 'rgba(255,255,255,.9)';
    pts.forEach(([x, y], i) => { const k = (Math.sin(t * 3 + i * 1.7) + 1) / 2; if (k > 0.85) { c.beginPath(); c.arc(x + T * 0.15, y - T * 0.05, 2, 0, Math.PI * 2); c.fill(); } });
    c.restore();
  }

  function drawDrips(c, v, cl, T, t) {
    const pts = pointsAlong(cl, 2);
    c.fillStyle = C.PALETTE.water;
    pts.forEach(([x, y], i) => {
      const ph = (t * 0.9 + v.dripPhase + i * 0.5) % 1;
      const dy = T * 0.45 + ph * T * 1.3;
      c.globalAlpha = 1 - ph;
      c.beginPath(); c.ellipse(x + (i ? T * 0.2 : -T * 0.15), y + dy, T * 0.08, T * 0.12, 0, 0, Math.PI * 2); c.fill();
    });
    c.globalAlpha = 1;
  }

  function drawChain(c, v, cl, T, t) {
    const fall = Math.max(0, t - v.chainT0);
    const unlocked = !v.pipe.locked;
    if (unlocked && fall > 0.6) return;
    c.save();
    if (unlocked) { c.globalAlpha *= 1 - fall / 0.6; c.translate(0, fall * fall * geo.cs * 3); }
    const pts = pointsAlong(cl, v.pipe.size * 3);
    c.lineWidth = Math.max(2, T * 0.1);
    pts.forEach(([x, y, horiz], i) => {
      c.beginPath();
      if (horiz) c.ellipse(x, y + (i % 2 ? T * 0.12 : -T * 0.12), T * 0.2, T * 0.11, (i % 2 ? 0.5 : -0.5), 0, Math.PI * 2);
      else c.ellipse(x + (i % 2 ? T * 0.12 : -T * 0.12), y, T * 0.11, T * 0.2, (i % 2 ? 0.5 : -0.5), 0, Math.PI * 2);
      c.strokeStyle = INK; c.lineWidth = Math.max(3, T * 0.16); c.stroke();
      c.strokeStyle = '#9AA1AB'; c.lineWidth = Math.max(1.5, T * 0.08); c.stroke();
    });
    // padlock
    const mid = cl.pts[Math.floor(cl.pts.length / 2)];
    const lx = mid[0], ly = mid[1] + T * 0.1, lw = T * 0.62, lh = T * 0.5;
    c.beginPath(); c.arc(lx, ly - lh * 0.45, lw * 0.28, Math.PI, 0); c.strokeStyle = INK; c.lineWidth = T * 0.16; c.stroke();
    c.strokeStyle = '#9AA1AB'; c.lineWidth = T * 0.08; c.stroke();
    roundRect(c, lx - lw / 2, ly - lh / 2, lw, lh, T * 0.1); c.fillStyle = '#8D949E'; c.fill(); c.strokeStyle = INK; c.lineWidth = 2; c.stroke();
    c.fillStyle = INK; c.beginPath(); c.arc(lx, ly - lh * 0.05, T * 0.07, 0, Math.PI * 2); c.fill(); c.fillRect(lx - T * 0.03, ly, T * 0.06, lh * 0.25);
    if (v.pairTag) { c.fillStyle = v.pairTag; c.beginPath(); c.arc(lx + lw * 0.42, ly - lh * 0.42, T * 0.13, 0, Math.PI * 2); c.fill(); c.strokeStyle = INK; c.lineWidth = 1.5; c.stroke(); }
    c.restore();
  }

  function drawKeyHandle(c, v, cl, T) {
    const mid = cl.pts[Math.floor(cl.pts.length / 2)];
    const x = mid[0], y = mid[1];
    const col = v.pairTag || C.PALETTE.error;
    c.save();
    c.lineCap = 'round';
    c.strokeStyle = INK; c.lineWidth = T * 0.34;
    c.beginPath(); c.moveTo(x, y); c.lineTo(x, y - T * 0.75); c.moveTo(x - T * 0.4, y - T * 0.75); c.lineTo(x + T * 0.4, y - T * 0.75); c.stroke();
    c.strokeStyle = col; c.lineWidth = T * 0.2;
    c.beginPath(); c.moveTo(x, y); c.lineTo(x, y - T * 0.75); c.moveTo(x - T * 0.4, y - T * 0.75); c.lineTo(x + T * 0.4, y - T * 0.75); c.stroke();
    c.fillStyle = INK; c.beginPath(); c.arc(x, y, T * 0.2, 0, Math.PI * 2); c.fill();
    c.fillStyle = shade(col, 0.2); c.beginPath(); c.arc(x, y, T * 0.12, 0, Math.PI * 2); c.fill();
    c.restore();
  }

  function drawValve(c, v, t, base, glow) {
    const p = v.pipe, cs = geo.cs;
    const cx = (p.cells[0][0] + 0.5) * cs, cy = (p.cells[0][1] + 0.5) * cs;
    const rk = Math.min(1, (t - v.rotT0) / 0.22);
    const ang = v.angleFrom + (v.angle - v.angleFrom) * ease.outBack(rk);
    const T = cs * 0.34, ow = Math.max(2.5, cs * 0.05);
    const brass = BRASS;
    c.save();
    c.translate(cx, cy);
    if (glow) { c.shadowColor = '#FFF'; c.shadowBlur = cs * 0.5; c.fillStyle = rgba('#FFFFFF', 0.6 + 0.3 * Math.sin(t * 8)); c.beginPath(); c.arc(0, 0, cs * 0.5, 0, Math.PI * 2); c.fill(); c.shadowBlur = 0; }
    const pending = G.pendingValves.some((q) => q.id === p.id);
    if (pending) { c.fillStyle = rgba(C.PALETTE.water, 0.35 + 0.25 * Math.sin(t * 20)); c.beginPath(); c.arc(0, 0, cs * 0.5, 0, Math.PI * 2); c.fill(); }
    c.rotate(ang);
    for (let k = 0; k < 4; k++) {
      c.save(); c.rotate((k * Math.PI) / 2);
      const len = k === 0 ? cs * 0.48 : cs * 0.38;
      roundRect(c, 0, -T / 2 - ow, len + ow, T + ow * 2, T * 0.2); c.fillStyle = INK; c.fill();
      roundRect(c, 0, -T / 2, len, T, T * 0.2); c.fillStyle = k === 0 ? shade(brass, 0.08) : shade(brass, -0.08); c.fill();
      c.fillStyle = shade(brass, 0.45); c.fillRect(cs * 0.1, -T / 2 + 2, len - cs * 0.12, T * 0.18);
      ring(c, len - cs * 0.04, 0, true, T * 1.25, cs * 0.1, brass, ow);
      c.restore();
    }
    // wheel
    const R = cs * 0.25;
    c.beginPath(); c.arc(0, 0, R + ow, 0, Math.PI * 2); c.fillStyle = INK; c.fill();
    c.beginPath(); c.arc(0, 0, R, 0, Math.PI * 2); c.fillStyle = shade(brass, -0.15); c.fill();
    c.beginPath(); c.arc(0, 0, R * 0.72, 0, Math.PI * 2); c.strokeStyle = shade(brass, 0.25); c.lineWidth = R * 0.18; c.stroke();
    c.strokeStyle = INK; c.lineWidth = R * 0.16;
    for (let k = 0; k < 3; k++) { const a = -ang * 0.5 + (k * Math.PI * 2) / 3; c.beginPath(); c.moveTo(0, 0); c.lineTo(Math.cos(a) * R * 0.72, Math.sin(a) * R * 0.72); c.stroke(); }
    c.beginPath(); c.arc(0, 0, R * 0.22, 0, Math.PI * 2); c.fillStyle = shade(brass, 0.3); c.fill(); c.strokeStyle = INK; c.lineWidth = 1.5; c.stroke();
    arrow(c, cs * 0.36, 0, 0, cs * 0.11, INK);
    c.restore();
    // curved "rotate" hint arrows
    c.save();
    c.translate(cx, cy);
    c.strokeStyle = rgba(C.PALETTE.yellow, 0.95); c.lineWidth = Math.max(2, cs * 0.045); c.lineCap = 'round';
    const spin = t * 0.6;
    for (let k = 0; k < 2; k++) {
      const a0 = spin + k * Math.PI;
      c.beginPath(); c.arc(0, 0, cs * 0.4, a0, a0 + 1.1); c.stroke();
      const ax = Math.cos(a0 + 1.1) * cs * 0.4, ay = Math.sin(a0 + 1.1) * cs * 0.4;
      arrow(c, ax, ay, a0 + 1.1 + Math.PI / 2, cs * 0.07, C.PALETTE.yellow);
    }
    c.restore();
  }

  function drawBadges(c, v, t, ox, oy, alpha) {
    const p = v.pipe, cs = geo.cs;
    let label = null, color = '#FFFFFF', txt = INK, icon = null;
    if (p.type === 'frozen' && p.iceCount > 0) { label = String(p.iceCount); color = '#E9F7FF'; icon = '❄'; }
    if (p.type === 'leaking') { label = String(p.leakTimer); color = p.leakTimer <= 2 ? C.PALETTE.error : '#FFFFFF'; txt = p.leakTimer <= 2 ? '#FFFFFF' : INK; icon = '💧'; }
    if (!label || v.exit) return;
    const mid = p.path[Math.floor((p.path.length - 1) / 2)];
    const x = ox + (mid[0] + 0.78) * cs, y = oy + (mid[1] + 0.2) * cs;
    const pop = Math.max(0, 1 - (t - v.badgeT0) / 0.35);
    const pulse = p.type === 'leaking' && p.leakTimer <= 2 ? 0.08 * Math.sin(t * 10) : 0;
    const r = cs * 0.2 * (1 + pop * 0.45 + pulse);
    c.save();
    c.globalAlpha = alpha;
    c.beginPath(); c.arc(x, y, r + 2.5, 0, Math.PI * 2); c.fillStyle = INK; c.fill();
    c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fillStyle = color; c.fill();
    c.fillStyle = txt; c.textAlign = 'center'; c.textBaseline = 'middle';
    c.font = `900 ${Math.round(r * 1.15)}px ${UI_FONT}`;
    c.fillText(label, x, y + 1);
    c.font = `${Math.round(r * 0.8)}px system-ui, "Segoe UI Emoji", sans-serif`;
    c.fillText(icon, x - r * 1.1, y - r * 0.9);
    c.restore();
  }

  // ---- particles
  function spray(pipe, dx, dy, dir) {
    const cs = geo.cs;
    const [vx, vy] = C.DIR_VEC[dir];
    // leading edge = the cell furthest along the exit direction
    let lead = pipe.cells[0];
    for (const q of pipe.cells) if (q[0] * vx + q[1] * vy > lead[0] * vx + lead[1] * vy) lead = q;
    const x = geo.ox + dx + (lead[0] + 0.5 + vx * 0.5) * cs, y = geo.oy + dy + (lead[1] + 0.5 + vy * 0.5) * cs;
    const color = arguments[4] || (Math.random() < 0.5 ? C.PALETTE.water : '#8FD0FF');
    for (let i = 0; i < 2; i++) {
      const sp = cs * (3 + Math.random() * 4);
      G.particles.push({
        x, y, vx: vx * sp + (Math.random() - 0.5) * cs * 2.5 - (vx ? 0 : 0), vy: vy * sp + (Math.random() - 0.5) * cs * 2.5 - cs * 1.2,
        g: cs * 14, r: cs * (0.05 + Math.random() * 0.06), life: 0.6, max: 0.6, color, kind: arguments[4] ? 'chip' : 'drop',
      });
    }
  }

  function burst(pipe, color, count, power) {
    const cs = geo.cs;
    for (let i = 0; i < count; i++) {
      const q = pipe.cells[i % pipe.cells.length];
      const a = Math.random() * Math.PI * 2, sp = cs * (1.5 + Math.random() * 3) * power;
      G.particles.push({
        x: geo.ox + (q[0] + 0.5) * cs, y: geo.oy + (q[1] + 0.5) * cs, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - cs * 2,
        g: cs * 12, r: cs * (0.05 + Math.random() * 0.07), life: 0.7, max: 0.7, color, kind: color === C.PALETTE.water ? 'drop' : 'chip',
      });
    }
  }

  // ---- full-screen fx (confetti)
  let confetti = [];
  function launchConfetti() {
    const w = fx.width / geo.dpr;
    const cols = [C.PALETTE.yellow, C.PALETTE.orange, C.PALETTE.green, C.PALETTE.water, C.PALETTE.error, '#FFFFFF'];
    for (let i = 0; i < 120; i++) {
      confetti.push({ x: w / 2 + (Math.random() - 0.5) * w * 0.4, y: fx.height / geo.dpr * 0.35, vx: (Math.random() - 0.5) * 700, vy: -300 - Math.random() * 600, r: Math.random() * Math.PI, vr: (Math.random() - 0.5) * 14, s: 6 + Math.random() * 7, c: cols[i % cols.length], life: 3 + Math.random() });
    }
  }
  function drawFx(t, dt) {
    fctx.setTransform(geo.dpr, 0, 0, geo.dpr, 0, 0);
    fctx.clearRect(0, 0, fx.width, fx.height);
    if (!confetti.length) return;
    for (const p of confetti) {
      p.life -= dt; p.vy += 900 * dt; p.vx *= 0.99; p.x += p.vx * dt; p.y += p.vy * dt; p.r += p.vr * dt;
      fctx.save(); fctx.translate(p.x, p.y); fctx.rotate(p.r); fctx.globalAlpha = Math.min(1, p.life);
      fctx.fillStyle = p.c; fctx.fillRect(-p.s / 2, -p.s / 4, p.s, p.s / 2); fctx.restore();
    }
    confetti = confetti.filter((p) => p.life > 0 && p.y < fx.height / geo.dpr + 40);
  }

  // =================================================================== overlays
  const overlay = $('#overlay');
  function showOverlay(html, onMount) {
    overlay.innerHTML = `<div class="card">${html}</div>`;
    overlay.classList.remove('hidden');
    if (G.state === 'playing' || G.state === 'hammer') { setHammer(false); G.prevState = G.state; G.state = 'overlay'; }
    if (onMount) onMount(overlay);
  }
  function hideOverlay() {
    overlay.classList.add('hidden');
    overlay.innerHTML = '';
    if (G.state === 'overlay') G.state = G.prevState || 'playing';
    app.dataset.state = G.state;
  }
  function bind(sel, fn) { const el = overlay.querySelector(sel); if (el) el.addEventListener('click', () => { A.click(); fn(el); }); }

  function showWin(reward) {
    A.win();
    launchConfetti();
    const n = G.session.levelNumber;
    const flawless = G.session.mistakes === 0;
    const rows = [['Seviye ödülü', reward.base]];
    if (reward.flawless) rows.push(['Kusursuz bonus', reward.flawless]);
    if (reward.breather) rows.push(['Mola seviyesi bonusu', reward.breather]);
    const nextRank = C.rankFor(n + 1) !== C.rankFor(n) ? `<p>Yeni rütbe: <span class="rank-badge">${C.rankFor(n + 1)}</span></p>` : '';
    const last = G.index >= G.order.length - 1 && !G.customLevel;
    showOverlay(`
      <img class="mascot happy" src="assets/sprites/mascot_happy.png" alt="Usta mutlu">
      <h2>${flawless ? 'Kusursuz İş, Usta!' : 'Harika İş!'}</h2>
      <p>Seviye ${n} tamam · <span class="rank-badge">${C.rankFor(n)}</span></p>
      <div class="rewards">
        ${rows.map(([k, v], i) => `<div class="reward-row" style="animation-delay:${0.15 + i * 0.15}s"><span>${k}</span><b><span class="coin-icon"></span>+${v}</b></div>`).join('')}
        <div class="reward-row total" style="animation-delay:${0.15 + rows.length * 0.15}s"><span>Toplam</span><b><span class="coin-icon"></span>+${reward.total}</b></div>
      </div>
      ${nextRank}
      <div class="btns">
        <button class="btn green" id="w-next">${last ? 'Bölüm Seç' : 'Devam Et ▶'}</button>
        <button class="btn secondary" id="w-menu">Bölümler</button>
      </div>`, () => {
      later(() => { A.coin(); const el = $('#coins'); el.classList.remove('bump'); void el.offsetWidth; el.classList.add('bump'); updateHud(); }, 500);
      bind('#w-next', () => (last ? showLevelSelect() : nextLevel()));
      bind('#w-menu', showLevelSelect);
    });
  }

  function showLose() {
    const canAd = !G.revived;
    const patches = Save.data.boosters.patch || 0;
    const patchOk = patches > 0 && (C.boosterUnlocked('patch', G.session.levelNumber) || G.customLevel);
    showOverlay(`
      <img class="mascot worried" src="assets/sprites/mascot_worried.png" alt="Usta endişeli">
      <h2>Eyvah, Su Kaçtı!</h2>
      <p>Damlalar bitti. Depoda hâlâ %${Math.round(G.session.waterRatio * 100)} su var.</p>
      <div class="btns">
        ${patchOk ? `<button class="btn blue" id="l-patch">Yama ile Devam<small>1 Yama kullan (${patches} adet)</small></button>` : ''}
        ${canAd ? '<button class="btn yellow" id="l-ad">+1 Damla ile Devam<small>Ödüllü reklam izle</small></button>' : ''}
        <button class="btn green" id="l-retry">Tekrar Dene ↻</button>
        <button class="btn secondary" id="l-menu">Bölümler</button>
      </div>`, () => {
      bind('#l-retry', () => startLevel(G.index, G.customLevel ? G.raw : null));
      bind('#l-menu', showLevelSelect);
      bind('#l-patch', () => { Save.data.boosters.patch--; Save.save(); trackBooster('patch'); revive(); });
      bind('#l-ad', () => showRewarded('revive', () => { G.revived = true; revive(); }));
    });
  }

  function revive() {
    G.session.revive(1);
    G.state = 'playing';
    hideOverlay();
    A.patch();
    renderLives(false, true);
    updateHud();
  }

  function showInterstitial(placement, done) {
    let left = 3;
    Analytics.track('ad_impression', { format: 'interstitial', placement, level: G.session ? G.session.levelNumber : 0 });
    G.state = 'ad';
    showOverlay(`<h2>Reklam</h2><div class="ad-box">Sponsorlu içerik<br><small>(mock interstitial)</small></div>
      <div class="btns"><button class="btn secondary" id="ad-close" disabled>Kapat (${left})</button></div>`, (o) => {
      const btn = o.querySelector('#ad-close');
      const tick = () => { left--; if (left > 0) { btn.textContent = `Kapat (${left})`; later(tick, 1000); } else { btn.disabled = false; btn.textContent = 'Kapat ✕'; } };
      later(tick, 1000);
      btn.addEventListener('click', () => { if (!btn.disabled) { hideOverlay(); done(); } });
    });
  }

  function showRewarded(placement, onReward) {
    Analytics.track('ad_impression', { format: 'rewarded', placement, level: G.session ? G.session.levelNumber : 0 });
    const prev = G.state;
    showOverlay(`<h2>Ödüllü Reklam</h2><div class="ad-box">Video oynuyor…<br><small>(mock rewarded)</small></div>
      <div class="btns"><button class="btn green" id="rw-done" disabled>Ödülü Al</button></div>`, (o) => {
      const btn = o.querySelector('#rw-done');
      later(() => { btn.disabled = false; }, 1500);
      btn.addEventListener('click', () => { if (btn.disabled) return; G.state = prev === 'overlay' ? G.prevState : prev; hideOverlay(); onReward(); });
    });
  }

  function showBoosterShop(b) {
    const info = BOOSTER_INFO[b];
    const price = C.BOOSTERS[b].price;
    showOverlay(`
      <img class="sprite" src="assets/sprites/booster_${b}.png" alt="">
      <h2>${info.name}</h2><p>${info.desc}</p>
      <div class="btns">
        <button class="btn" id="bs-buy" ${Save.data.coins < price ? 'disabled' : ''}>Satın Al<small>${price} jeton (sende ${Save.data.coins})</small></button>
        <button class="btn blue" id="bs-ad">Reklam İzle<small>+1 ${info.name}</small></button>
        <button class="btn secondary" id="bs-close">Vazgeç</button>
      </div>`, () => {
      bind('#bs-buy', (el) => { if (el.disabled) return; Save.data.coins -= price; Save.data.boosters[b] = (Save.data.boosters[b] || 0) + 1; Save.save(); A.coin(); hideOverlay(); updateHud(); });
      bind('#bs-ad', () => showRewarded('booster_' + b, () => { Save.data.boosters[b] = (Save.data.boosters[b] || 0) + 1; Save.save(); updateHud(); }));
      bind('#bs-close', hideOverlay);
    });
  }

  function showShop() {
    showOverlay(`
      <h2>Mağaza</h2><p>Satın alımlar simülasyondur (mock IAP).</p>
      <div class="settings-list">
        ${IAP_PRODUCTS.map((p) => `<div class="shop-item"><span class="coin-icon" style="width:40px;height:40px"></span><div class="grow">${p.title}<small>${p.noAds ? 'Geçiş reklamlarını kaldırır' : 'Güçlendirici almak için'}</small></div>
          <button class="btn" data-iap="${p.id}" ${p.noAds && Save.data.noAds ? 'disabled' : ''}>${p.noAds && Save.data.noAds ? 'Alındı' : p.price.toFixed(2).replace('.', ',') + ' ₺'}</button></div>`).join('')}
      </div>
      <div class="btns"><button class="btn secondary" id="shop-close">Kapat</button></div>`, (o) => {
      o.querySelectorAll('[data-iap]').forEach((btn) => btn.addEventListener('click', () => {
        if (btn.disabled) return;
        const prod = IAP_PRODUCTS.find((p) => p.id === btn.dataset.iap);
        if (prod.coins) Save.data.coins += prod.coins;
        if (prod.noAds) Save.data.noAds = true;
        Save.save();
        Analytics.track('iap_purchase', { productId: prod.id, price: prod.price, currency: 'TRY', mock: true });
        A.coin();
        updateHud();
        showShop();
      }));
      bind('#shop-close', hideOverlay);
    });
  }

  function showSettings() {
    showOverlay(`
      <h2>Ayarlar</h2>
      <div class="btns">
        <button class="btn secondary" id="s-sound">Ses: ${Save.data.sound ? 'Açık 🔊' : 'Kapalı 🔇'}</button>
        <button class="btn" id="s-restart">Seviyeyi Yeniden Başlat ↻</button>
        <button class="btn green" id="s-levels">Bölüm Seç</button>
        <button class="btn blue" id="s-shop">Mağaza</button>
        <button class="btn red" id="s-reset">İlerlemeyi Sıfırla</button>
        <button class="btn secondary" id="s-close">Kapat</button>
      </div>
      <p style="font-size:12px;margin-top:12px">${G.pack.manifest.levels.length} seviye · kaynak: ${G.pack.source} · ?dev=1 geliştirici paneli</p>`, () => {
      bind('#s-sound', () => { Save.data.sound = !Save.data.sound; Save.save(); A.setEnabled(Save.data.sound); showSettings(); });
      bind('#s-restart', () => startLevel(G.index, G.customLevel ? G.raw : null));
      bind('#s-levels', showLevelSelect);
      bind('#s-shop', showShop);
      bind('#s-reset', () => { Save.reset(); A.setEnabled(true); startLevel(0); });
      bind('#s-close', hideOverlay);
    });
  }

  function showLevelSelect() {
    if (G.state === 'won' || G.state === 'lost') G.prevState = 'playing';
    let lastTheme = null;
    const cells = G.order.map((e, i) => {
      let head = '';
      if (e.theme !== lastTheme) { lastTheme = e.theme; head = `<h3>${e.theme} · ${e.rank}</h3>`; }
      const locked = i > Save.data.maxIndex && !DEV;
      const cls = ['lvl', Save.data.completed[e.level] ? 'done' : '', i === G.index ? 'current' : '', e.breather ? 'breather' : '', locked ? 'locked' : ''].join(' ');
      return `${head}<button class="${cls}" data-i="${i}" ${locked ? 'disabled' : ''} title="Zorluk ${e.difficultyScore}">${locked ? '🔒' : e.level}</button>`;
    }).join('');
    showOverlay(`<h2>Bölümler</h2><p>Jeton: ${Save.data.coins} · Rütbe: <span class="rank-badge">${C.rankFor(G.order[Save.data.maxIndex].level)}</span></p>
      <div class="level-grid">${cells}</div><div class="btns"><button class="btn secondary" id="ls-close">Kapat</button></div>`, (o) => {
      o.querySelectorAll('.lvl[data-i]').forEach((btn) => btn.addEventListener('click', () => { if (btn.disabled) return; A.click(); startLevel(+btn.dataset.i); }));
      bind('#ls-close', () => {
        if (G.session && G.session.status === 'won') startLevel(G.customLevel ? G.index : G.index + 1);
        else if (G.session && G.session.status === 'lost') startLevel(G.index, G.customLevel ? G.raw : null);
        else hideOverlay();
      });
    });
  }

  // ---- mechanic intro cards (pipe sprites from pipes_spritesheet.jpg)
  const MECHANIC_INTROS = [
    { type: 'elbow', level: 8, sprite: 'pipe_elbow', title: 'Dirsek Boru', text: 'L şeklindeki borular da okun yönünde <b>bütün gövdesiyle</b> kayar. Gövdenin her hücresinin önü açık olmalı.' },
    { type: 'dry', level: 12, sprite: 'pipe_dry', title: 'Kuru Boru', text: 'İçinde su yok — kazanmak için çıkarman gerekmez. Ama yolu tıkıyorsa önce onu kaldır.' },
    { type: 'rusty', level: 16, sprite: 'pipe_rusty', title: 'Paslı Boru', text: 'İlk dokunuş pası kırar ve <b>damla kaybettirmez</b>. İkinci dokunuşta yolu açıksa kayar.' },
    { type: 'frozen', level: 22, sprite: 'pipe_frozen', title: 'Donmuş Boru', text: 'Üstündeki sayı kadar başka boru çıkınca buz çözülür. Erken dokunursan 1 damla kaybedersin.' },
    { type: 'locked', level: 30, sprite: 'pipe_locked', title: 'Kilitli Boru & Anahtar Vana', text: 'Zincirli boru, <b>aynı renkli anahtar vanalı</b> boru çıkana kadar açılmaz. Erken dokunmak 1 damla.' },
    { type: 'leaking', level: 35, sprite: 'pipe_leaking', title: 'Sızdıran Boru', text: 'Her dokunuşta sayacı 1 azalır. Sayaç bitmeden çıkarmazsan 1 damla kaybedersin (sayaç yeniden başlar).' },
    { type: 'rotary_valve', level: 61, sprite: 'pipe_rotary_valve', title: 'Döner Vana', text: 'Dokununca saat yönünde 90° döner, damla kaybettirmez. Yol açılınca <b>0,5 sn</b> sonra kendiliğinden kayar.' },
  ];
  function mechanicIntroFor(n) {
    return MECHANIC_INTROS.find((m) => m.level === n && G.session.board.pipes.some((p) => p.type === m.type)) || null;
  }
  function showMechanicIntro(intro) {
    showOverlay(`<p class="rank-badge">Yeni Boru</p><img class="sprite" src="assets/sprites/${intro.sprite}.png" alt=""><h2>${intro.title}</h2><p>${intro.text}</p>
      <div class="btns"><button class="btn green" id="mi-ok">Anladım!</button></div>`, () => {
      bind('#mi-ok', () => {
        Save.data.seenIntro[intro.type] = true; Save.save();
        hideOverlay();
        for (const v of G.views.values()) v.introPulse = v.pipe.type === intro.type || (intro.type === 'locked' && v.pipe.type === 'key_valve');
        G.introPulseUntil = nowSec() + 2.5;
        afterIntro();
      });
    });
  }

  // ---- FTUE (levels 1-3)
  function ftueStep(step) {
    if (Save.data.ftue[step]) return;
    Save.data.ftue[step] = true; Save.save();
    Analytics.track('ftue_step', { step, level: G.session ? G.session.levelNumber : 0 });
  }
  function startFtue(n) {
    if (G.customLevel || n > C.RULES.ftueLevels) return;
    if (n === 1) {
      const target = G.session.exitablePipeIds()[0];
      G.hand = target;
      ftueStep('l1_tap_hint');
      showTooltip('Okun gösterdiği yol açıksa boruya dokun — boru kayar, su boşalır!');
    } else if (n === 2) {
      ftueStep('l2_blocking_hint');
      showTooltip('Önü kapalı boru takılır. Önce yolu açan boruyu çıkar!');
      later(hideTooltip, 5000);
    } else if (n === 3) {
      ftueStep('l3_goal_hint');
      showTooltip('Tüm sulu boruları çıkar, depoyu boşalt! İlk 3 seviyede damla kaybetmezsin.');
      later(hideTooltip, 5000);
    }
  }

  // ---- toasts / tooltips
  let toastTimer = null;
  function toast(msg, ms) {
    const el = $('#toast');
    el.textContent = msg;
    el.classList.remove('hidden');
    el.style.animation = 'none'; void el.offsetWidth; el.style.animation = '';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.add('hidden'), ms || 2600);
  }
  const toasted = new Set();
  function toastOnce(key, msg) { if (toasted.has(key)) return; toasted.add(key); toast(msg, 3200); }
  /** Tooltip sits on the board half away from the FTUE target so it never hides it. */
  function showTooltip(msg) {
    const el = $('#tooltip');
    el.textContent = msg;
    let bottom = false;
    const target = G.hand && G.session && G.session.board.get(G.hand);
    if (target) bottom = target.cells.reduce((s, c) => s + c[1], 0) / target.size < G.session.board.height / 2;
    el.classList.toggle('bottom', bottom);
    el.classList.remove('hidden');
  }
  function hideTooltip() { $('#tooltip').classList.add('hidden'); }

  $('#btn-settings').addEventListener('click', () => { A.unlock(); A.click(); if (G.pack) showSettings(); });
  $('#coins').addEventListener('click', () => { A.unlock(); A.click(); if (G.pack) showShop(); });

  // =================================================================== dev panel (?dev=1): metrics, auto-solve, custom JSON
  function renderDev() {
    if (!DEV || !G.session) return;
    const el = $('#dev');
    el.classList.remove('hidden');
    const e = !G.customLevel && G.order[G.index];
    const m = e || {};
    const s = G.session;
    el.innerHTML = `<div class="dev-head">DEV · L${s.levelNumber}${G.customLevel ? ' (custom)' : ''} ${el.classList.contains('collapsed') ? '▸' : '▾'}</div><div class="dev-body">
      zorluk ${m.difficultyScore ?? '-'} / hedef ${m.targetDifficulty ?? '-'}<br>
      zincir ${m.maxChainDepth ?? '-'} · ilk çıkış ${m.initialExitableCount ?? '-'} · aldatıcı ${m.deceptiveArrowRatio ?? '-'}<br>
      dokunuş ${s.taps} · hata ${s.mistakes} · su %${Math.round(s.waterRatio * 100)}<br>
      son olay: ${Analytics.log.length ? Analytics.log[Analytics.log.length - 1].event : '-'}<br>
      <button id="dev-solve">Çöz ▶</button><button id="dev-prev">◀</button><button id="dev-next">▶</button><button id="dev-load">JSON…</button></div>`;
    el.querySelector('.dev-head').onclick = () => { el.classList.toggle('collapsed'); renderDev(); };
    el.querySelector('#dev-solve').onclick = autoSolve;
    el.querySelector('#dev-prev').onclick = () => startLevel(G.index - 1);
    el.querySelector('#dev-next').onclick = () => startLevel(G.index + 1);
    el.querySelector('#dev-load').onclick = pickJson;
  }
  function autoSolve() {
    const res = S.solve(G.raw);
    if (!res.solvable) { toast('Çözücü: çözülemez!'); return; }
    // replay from a fresh start so the plan matches the state
    startLevel(G.index, G.customLevel ? G.raw : null);
    hideOverlay();
    const moves = res.solution.slice();
    const step = () => {
      if (!moves.length || G.session.status !== 'playing') return;
      if (G.state !== 'playing') return later(step, 300);
      if (G.pendingValves.length) return later(step, 120); // wait for the 0.5s valve slide
      const mv = moves.shift();
      handleTap(G.session.board.get(mv.id));
      later(step, 330);
    };
    later(step, 400);
  }
  function pickJson() {
    const inp = document.createElement('input');
    inp.type = 'file'; inp.accept = '.json,application/json';
    inp.onchange = () => inp.files[0] && inp.files[0].text().then(loadCustom);
    inp.click();
  }
  function loadCustom(text) {
    try {
      const raw = JSON.parse(text);
      const v = C.validateLevel(raw);
      if (!v.ok) { toast('Geçersiz seviye: ' + v.errors[0], 4000); return; }
      const r = S.solve(raw);
      toast(r.solvable ? `Çözülebilir ✓ zorluk ${r.metrics.difficultyScore}, zincir ${r.metrics.maxChainDepth}` : 'Uyarı: çözülemez seviye!', 3500);
      startLevel(G.index, raw);
    } catch (e) { toast('JSON okunamadı: ' + e.message, 4000); }
  }
  document.addEventListener('dragover', (e) => e.preventDefault());
  document.addEventListener('drop', (e) => { e.preventDefault(); const f = e.dataTransfer.files[0]; if (f) f.text().then(loadCustom); });
  document.addEventListener('keydown', (e) => {
    if (!G.session) return;
    if (e.key === 'r' || e.key === 'R') startLevel(G.index, G.customLevel ? G.raw : null);
    if (e.key === 'm' || e.key === 'M') { Save.data.sound = !Save.data.sound; Save.save(); A.setEnabled(Save.data.sound); toast(Save.data.sound ? 'Ses açık' : 'Ses kapalı'); }
    if (DEV && e.key === 'n') startLevel(G.index + 1);
    if (DEV && e.key === 'p') startLevel(G.index - 1);
    if (DEV && e.key === 's') autoSolve();
    if (e.key === 'Escape' && G.state === 'hammer') setHammer(false);
  });

  // =================================================================== boot
  async function boot() {
    Save.load();
    A.setEnabled(Save.data.sound);
    resize();
    try {
      G.pack = await loadPack();
    } catch (e) {
      showOverlay(`<h2>Seviyeler yüklenemedi</h2><p>${e.message}</p>`);
      return;
    }
    G.order = G.pack.manifest.levels.slice().sort((a, b) => a.level - b.level);
    const want = parseInt(params.get('level'), 10);
    let start = Math.min(Save.data.maxIndex, G.order.length - 1);
    if (want) { const i = G.order.findIndex((e) => e.level === want); if (i >= 0) start = i; }
    requestAnimationFrame(frame);
    startLevel(start);
  }
  boot();
})();
