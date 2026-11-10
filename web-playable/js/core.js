/*
 * Su Ustası (Flush Master) — shared rules core.
 *
 * Zero-dependency UMD module: loaded as a classic <script> by the web playable
 * (exposes window.SuCore, works from file://) and via require() by the Node tooling
 * (level generator, validator, tests). The C# implementation under
 * unity-project/Assets/_Game/Scripts/Core mirrors this file 1:1.
 *
 * Coordinates: (0,0) top-left, x grows right, y grows down.
 */
(function (root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.SuCore = api;
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // ------------------------------------------------------------------ constants
  const DIRS = ['up', 'right', 'down', 'left']; // clockwise order
  const DIR_VEC = { up: [0, -1], right: [1, 0], down: [0, 1], left: [-1, 0] };
  const rotateCW = (d) => DIRS[(DIRS.indexOf(d) + 1) % 4];
  const rotateCCW = (d) => DIRS[(DIRS.indexOf(d) + 3) % 4];

  const PALETTE = Object.freeze({
    water: '#36A2FF',
    yellow: '#F2B632',
    orange: '#E8743B',
    green: '#5BBE7A',
    dry: '#8D99AE',
    rusty: '#A5673F',
    frozen: '#BDE9FF',
    error: '#E5484D',
    background: '#FFF4E0',
  });

  const PIPE_TYPES = ['straight', 'elbow', 'dry', 'rusty', 'frozen', 'locked', 'key_valve', 'leaking', 'rotary_valve'];

  /** First level a pipe type may appear in (GDD). */
  const UNLOCK_LEVEL = Object.freeze({
    straight: 1, elbow: 8, dry: 12, rusty: 16, frozen: 22, locked: 30, key_valve: 30, leaking: 35, rotary_valve: 61,
  });

  const TIMING = Object.freeze({
    exitDuration: 0.6,        // visual slide-out; logic frees cells immediately
    valveAutoExitDelay: 0.5,  // rotary valve: delay between rotation and auto slide-out
    xrayDuration: 3.0,
    shakeDuration: 0.35,
    flashDuration: 0.55,
  });

  const RULES = Object.freeze({
    defaultLeaks: 3,
    maxLeaks: 4,          // Patch cap
    ftueLevels: 3,        // levels 1..3 never cost leak lives and never show interstitials
    defaultLeakTimer: 6,
  });

  const BOOSTERS = Object.freeze({
    xray: { unlock: 5, price: 60 },
    hammer: { unlock: 10, price: 120 },
    patch: { unlock: 15, price: 90 },
  });

  const ECONOMY = Object.freeze({
    winCoins: 20,
    flawlessBonus: 10,
    breatherBonus: 20,
    breatherEvery: 5,
    startCoins: 100,
    startBoosters: Object.freeze({ xray: 2, hammer: 1, patch: 1 }),
  });

  const RANKS = Object.freeze([
    { name: 'Çırak', min: 1 },
    { name: 'Kalfa', min: 20 },
    { name: 'Usta', min: 50 },
    { name: 'Başusta', min: 100 },
  ]);

  const THEMES = Object.freeze(['Banyo', 'Mutfak', 'Apartman Bodrumu', 'Fabrika', 'Şehir Kanalı', 'Arıtma Tesisi']);

  const ADS = Object.freeze({ noInterstitialUpToLevel: 3, cooldownSec: 60, everyNWins: 2 });

  const ANALYTICS_EVENTS = Object.freeze([
    'level_start', 'level_complete', 'level_fail', 'blocked_tap', 'booster_used', 'ad_impression', 'iap_purchase', 'ftue_step',
  ]);

  // ------------------------------------------------------------------ helpers
  const isWaterType = (t) => t !== 'dry';
  const key = (x, y) => x + ',' + y;

  /** Water pipes cycle yellow/orange/green; green is reserved for `leaking` when a level has any. */
  function waterColorFor(pipeIndex, levelHasLeaking) {
    const cycle = levelHasLeaking ? ['yellow', 'orange'] : ['yellow', 'orange', 'green'];
    return cycle[pipeIndex % cycle.length];
  }

  function pipeColorHex(type, pipeIndex, levelHasLeaking, rustIntact) {
    switch (type) {
      case 'dry': return PALETTE.dry;
      case 'rusty': return rustIntact ? PALETTE.rusty : PALETTE[waterColorFor(pipeIndex, levelHasLeaking)];
      case 'frozen': return PALETTE.frozen;
      case 'leaking': return PALETTE.green;
      default: return PALETTE[waterColorFor(pipeIndex, levelHasLeaking)];
    }
  }

  // ------------------------------------------------------------------ shapes
  /**
   * Classifies a cell set. Returns { kind: 'single'|'line'|'L'|'invalid', path } where
   * `path` is the cells ordered head-to-tail (an L goes arm end → corner → other arm end).
   */
  function analyzeShape(cells) {
    const n = cells.length;
    if (n === 0) return { kind: 'invalid', path: [] };
    const set = new Set(cells.map((c) => key(c[0], c[1])));
    if (set.size !== n) return { kind: 'invalid', path: [] };
    if (n === 1) return { kind: 'single', path: [cells[0].slice()] };
    const xs = cells.map((c) => c[0]);
    const ys = cells.map((c) => c[1]);
    const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
    if (minY === maxY && maxX - minX === n - 1) {
      const path = [];
      for (let x = minX; x <= maxX; x++) path.push([x, minY]);
      return { kind: 'line', path };
    }
    if (minX === maxX && maxY - minY === n - 1) {
      const path = [];
      for (let y = minY; y <= maxY; y++) path.push([minX, y]);
      return { kind: 'line', path };
    }
    // L: the corner sits on a bounding-box corner; a full horizontal run and a full vertical run meet there.
    const w = maxX - minX + 1, h = maxY - minY + 1;
    if (w < 2 || h < 2 || w + h - 1 !== n) return { kind: 'invalid', path: [] };
    const corners = [[minX, minY], [maxX, minY], [minX, maxY], [maxX, maxY]];
    for (const [cx, cy] of corners) {
      if (!set.has(key(cx, cy))) continue;
      const ox = cx === minX ? maxX : minX; // far x end of horizontal arm
      const oy = cy === minY ? maxY : minY; // far y end of vertical arm
      let ok = true;
      const hArm = [], vArm = [];
      for (let x = Math.min(cx, ox); x <= Math.max(cx, ox); x++) { if (!set.has(key(x, cy))) ok = false; hArm.push([x, cy]); }
      for (let y = Math.min(cy, oy); y <= Math.max(cy, oy); y++) { if (!set.has(key(cx, y))) ok = false; vArm.push([cx, y]); }
      if (!ok) continue;
      // order: horizontal arm end -> corner -> vertical arm end
      if (hArm[0][0] === cx) hArm.reverse();
      if (vArm[0][1] !== cy) vArm.reverse();
      return { kind: 'L', path: hArm.concat(vArm.slice(1)) };
    }
    return { kind: 'invalid', path: [] };
  }

  // ------------------------------------------------------------------ level parsing / validation
  class LevelError extends Error {
    constructor(errors) { super('Invalid level: ' + errors.join('; ')); this.errors = errors; }
  }

  const isInt = (v) => Number.isInteger(v);

  /**
   * Validates raw level JSON and returns { ok, errors, level } where `level` is the
   * normalized form: { level, width, height, tank:{x,y,w,h}, leaks, pipes:[...] }.
   */
  function validateLevel(raw) {
    const errors = [];
    const err = (m) => errors.push(m);
    if (!raw || typeof raw !== 'object') return { ok: false, errors: ['level must be an object'], level: null };
    const levelNo = raw.level;
    if (!isInt(levelNo) || levelNo < 1) err('"level" must be a positive integer');
    const grid = raw.grid;
    if (!Array.isArray(grid) || grid.length !== 2 || !isInt(grid[0]) || !isInt(grid[1]) || grid[0] < 3 || grid[1] < 3 || grid[0] > 16 || grid[1] > 16) {
      err('"grid" must be [width, height] with 3..16 cells');
      return { ok: false, errors, level: null };
    }
    const [W, H] = grid;
    const t = raw.tank || {};
    if (![t.x, t.y, t.w, t.h].every(isInt) || t.w < 1 || t.h < 1 || t.x < 0 || t.y < 0 || t.x + t.w > W || t.y + t.h > H) {
      err('"tank" must be {x,y,w,h} inside the grid');
      return { ok: false, errors, level: null };
    }
    const leaks = raw.leaks === undefined ? RULES.defaultLeaks : raw.leaks;
    if (!isInt(leaks) || leaks < 1 || leaks > RULES.maxLeaks) err(`"leaks" must be 1..${RULES.maxLeaks}`);
    const inTank = (x, y) => x >= t.x && x < t.x + t.w && y >= t.y && y < t.y + t.h;

    if (!Array.isArray(raw.pipes) || raw.pipes.length === 0) {
      err('"pipes" must be a non-empty array');
      return { ok: false, errors, level: null };
    }
    const ids = new Set();
    const owner = new Map();
    const pipes = [];
    raw.pipes.forEach((p, i) => {
      const tag = `pipe[${i}]${p && p.id ? ' "' + p.id + '"' : ''}`;
      if (!p || typeof p !== 'object') { err(`${tag}: must be an object`); return; }
      if (typeof p.id !== 'string' || !p.id) err(`${tag}: missing string id`);
      else if (ids.has(p.id)) err(`${tag}: duplicate id`);
      else ids.add(p.id);
      if (!PIPE_TYPES.includes(p.type)) err(`${tag}: unknown type "${p.type}"`);
      if (!DIRS.includes(p.dir)) err(`${tag}: dir must be one of ${DIRS.join('/')}`);
      if (!Array.isArray(p.cells) || p.cells.length === 0) { err(`${tag}: cells must be a non-empty array`); return; }
      const cells = [];
      for (const c of p.cells) {
        if (!Array.isArray(c) || c.length !== 2 || !isInt(c[0]) || !isInt(c[1])) { err(`${tag}: bad cell ${JSON.stringify(c)}`); continue; }
        const [x, y] = c;
        if (x < 0 || y < 0 || x >= W || y >= H) err(`${tag}: cell [${x},${y}] out of bounds`);
        else if (inTank(x, y)) err(`${tag}: cell [${x},${y}] overlaps the tank`);
        else if (owner.has(key(x, y))) err(`${tag}: cell [${x},${y}] overlaps "${owner.get(key(x, y))}"`);
        else owner.set(key(x, y), p.id);
        cells.push([x, y]);
      }
      const shape = analyzeShape(cells);
      const n = cells.length;
      if (shape.kind === 'invalid') err(`${tag}: cells must form a straight line or an L`);
      else if (p.type === 'straight' && !(shape.kind === 'line' && n >= 2 && n <= 5)) err(`${tag}: straight must be a 2–5 cell line`);
      else if (p.type === 'elbow' && !(shape.kind === 'L' && n >= 3 && n <= 5)) err(`${tag}: elbow must be a 3–5 cell L`);
      else if (p.type === 'rotary_valve' && !((shape.kind === 'single' || shape.kind === 'line') && n <= 5)) err(`${tag}: rotary_valve must be 1–5 cells in a line`);
      else if (!['straight', 'elbow', 'rotary_valve'].includes(p.type) && !((shape.kind === 'line' && n >= 2) || shape.kind === 'L') ) err(`${tag}: ${p.type} must be a 2–5 cell line or a 3–5 cell L`);
      else if (n > 5) err(`${tag}: at most 5 cells`);

      const norm = { id: p.id, type: p.type, cells, path: shape.path, shape: shape.kind, dir: p.dir };
      if (p.type === 'frozen') {
        if (!isInt(p.iceCount) || p.iceCount < 1) err(`${tag}: frozen needs iceCount >= 1`);
        norm.iceCount = p.iceCount;
      }
      if (p.type === 'leaking') {
        const lt = p.leakTimerMoves === undefined ? RULES.defaultLeakTimer : p.leakTimerMoves;
        if (!isInt(lt) || lt < 1) err(`${tag}: leakTimerMoves must be >= 1`);
        norm.leakTimerMoves = lt;
      }
      if (p.type === 'key_valve') {
        if (typeof p.targetId !== 'string') err(`${tag}: key_valve needs targetId (the locked pipe it opens)`);
        norm.targetId = p.targetId;
      }
      if (typeof p.color === 'string') norm.color = p.color;
      pipes.push(norm);
    });

    // Cross-pipe checks.
    const byId = new Map(pipes.map((p) => [p.id, p]));
    const lockedTargets = new Map();
    for (const p of pipes) {
      if (p.type !== 'key_valve' || typeof p.targetId !== 'string') continue;
      const target = byId.get(p.targetId);
      if (!target) err(`key_valve "${p.id}": targetId "${p.targetId}" not found`);
      else if (target.type !== 'locked') err(`key_valve "${p.id}": target "${p.targetId}" is not a locked pipe`);
      else lockedTargets.set(target.id, (lockedTargets.get(target.id) || 0) + 1);
    }
    for (const p of pipes) {
      if (p.type === 'locked' && !lockedTargets.has(p.id)) err(`locked "${p.id}": no key_valve targets it`);
    }
    if (!pipes.some((p) => isWaterType(p.type))) err('level needs at least one water-bearing pipe');

    // Tank rule: no exit path may ever cross the tank.
    for (const p of pipes) {
      const crosses = (dir) => {
        const [dx, dy] = DIR_VEC[dir];
        return p.cells.some(([cx, cy]) => {
          for (let x = cx + dx, y = cy + dy; x >= 0 && y >= 0 && x < W && y < H; x += dx, y += dy) if (inTank(x, y)) return true;
          return false;
        });
      };
      if (p.type === 'rotary_valve') {
        if (DIRS.every(crosses)) err(`rotary_valve "${p.id}": every direction crosses the tank`);
      } else if (DIRS.includes(p.dir) && crosses(p.dir)) {
        err(`pipe "${p.id}": exit path toward ${p.dir} crosses the tank`);
      }
    }

    const level = errors.length ? null : { level: levelNo, width: W, height: H, tank: { x: t.x, y: t.y, w: t.w, h: t.h }, leaks, pipes };
    return { ok: errors.length === 0, errors, level };
  }

  function parseLevel(raw) {
    const r = validateLevel(raw);
    if (!r.ok) throw new LevelError(r.errors);
    return r.level;
  }

  // ------------------------------------------------------------------ board
  const TANK = -2, EMPTY = -1;

  class PipeState {
    constructor(def, index) {
      this.id = def.id;
      this.index = index;
      this.type = def.type;
      this.cells = def.cells.map((c) => c.slice());
      this.path = def.path.map((c) => c.slice());
      this.shape = def.shape;
      this.dir = def.dir;
      this.initialDir = def.dir;
      this.water = isWaterType(def.type);
      this.rustIntact = def.type === 'rusty';
      this.iceCount = def.type === 'frozen' ? def.iceCount : 0;
      this.locked = def.type === 'locked';
      this.targetId = def.targetId || null;
      this.leakTimerMax = def.type === 'leaking' ? def.leakTimerMoves : 0;
      this.leakTimer = this.leakTimerMax;
      this.removed = false;
      this.pendingExit = false;
      this.color = def.color || null;
    }
    get size() { return this.cells.length; }
  }

  class Board {
    /** @param level normalized level (from parseLevel) */
    constructor(level) {
      this.width = level.width;
      this.height = level.height;
      this.tank = Object.assign({}, level.tank);
      this.occ = new Int16Array(this.width * this.height).fill(EMPTY);
      const t = this.tank;
      for (let y = t.y; y < t.y + t.h; y++) for (let x = t.x; x < t.x + t.w; x++) this.occ[y * this.width + x] = TANK;
      this.pipes = level.pipes.map((p, i) => new PipeState(p, i));
      this.byId = new Map(this.pipes.map((p) => [p.id, p]));
      for (const p of this.pipes) for (const [x, y] of p.cells) this.occ[y * this.width + x] = p.index;
    }
    inBounds(x, y) { return x >= 0 && y >= 0 && x < this.width && y < this.height; }
    isTank(x, y) { return this.inBounds(x, y) && this.occ[y * this.width + x] === TANK; }
    pipeAt(x, y) {
      if (!this.inBounds(x, y)) return null;
      const o = this.occ[y * this.width + x];
      return o >= 0 ? this.pipes[o] : null;
    }
    get(id) { return this.byId.get(id) || null; }
    remaining() { return this.pipes.filter((p) => !p.removed); }
    /** Frees the pipe's cells immediately (non-blocking input: the view animates separately). */
    remove(pipe) {
      if (pipe.removed) return;
      for (const [x, y] of pipe.cells) this.occ[y * this.width + x] = EMPTY;
      pipe.removed = true;
      pipe.pendingExit = false;
    }
    /** Swept-path scan; see PathScanner.scan. */
    scan(pipe, dir) { return scanPath(this, pipe, dir || pipe.dir); }
  }

  /**
   * PathScanner: every cell of the pipe casts a ray in the nozzle direction to the
   * board edge, ignoring the pipe's own cells. Returns
   *   { canExit, blockingPipeIds (ordered by distance), sweptCells, hitsTank }.
   */
  function scanPath(board, pipe, dir) {
    const [dx, dy] = DIR_VEC[dir];
    const W = board.width;
    const best = new Map(); // pipe index -> min distance
    const swept = [];
    const sweptSeen = new Set();
    let hitsTank = false;
    for (const [cx, cy] of pipe.cells) {
      let dist = 1;
      for (let x = cx + dx, y = cy + dy; x >= 0 && y >= 0 && x < W && y < board.height; x += dx, y += dy, dist++) {
        const o = board.occ[y * W + x];
        if (o === pipe.index) continue;
        if (o === TANK) hitsTank = true;
        else if (o >= 0) {
          const prev = best.get(o);
          if (prev === undefined || dist < prev) best.set(o, dist);
        }
        const k = y * W + x;
        if (!sweptSeen.has(k)) { sweptSeen.add(k); swept.push([x, y]); }
      }
    }
    const blockingPipeIds = [...best.entries()]
      .sort((a, b) => a[1] - b[1] || a[0] - b[0])
      .map(([idx]) => board.pipes[idx].id);
    return { canExit: !hitsTank && blockingPipeIds.length === 0, blockingPipeIds, sweptCells: swept, hitsTank };
  }

  // ------------------------------------------------------------------ game session (rules engine)
  /**
   * Pure, deterministic rules for one level attempt. The view layer calls tap()/hammer()/
   * patch() and plays the returned events; logic never waits for animations.
   *
   * Tap outcome kinds: exit | blocked | locked | frozen | rust_broken | valve_rotated | ignored
   * Extra events:      ice_cracked | thawed | unlocked | leak_burst | valve_exit | won | lost
   */
  class GameSession {
    constructor(levelOrRaw, opts = {}) {
      this.level = levelOrRaw.pipes && levelOrRaw.width ? levelOrRaw : parseLevel(levelOrRaw);
      this.levelNumber = this.level.level;
      this.board = new Board(this.level);
      this.leaks = this.level.leaks;
      this.maxLeaks = RULES.maxLeaks;
      this.ftue = opts.ftue !== undefined ? !!opts.ftue : this.levelNumber <= RULES.ftueLevels;
      this.instantValves = !!opts.instantValves; // solver/tests: skip the 0.5s auto-exit delay
      this.initialWaterCells = this.board.pipes.reduce((s, p) => s + (p.water ? p.size : 0), 0);
      this.remainingWaterCells = this.initialWaterCells;
      this.hasLeaking = this.board.pipes.some((p) => p.type === 'leaking');
      this.taps = 0;
      this.mistakes = 0;
      this.exits = 0;
      this.combo = 0;
      this.status = 'playing';
    }

    get waterRatio() { return this.initialWaterCells ? this.remainingWaterCells / this.initialWaterCells : 0; }
    get remainingWaterPipes() { return this.board.pipes.filter((p) => p.water && !p.removed).length; }

    /** True when tapping this pipe right now would make it leave the board (XRay). */
    wouldExitOnTap(pipe) {
      if (!pipe || pipe.removed || pipe.pendingExit) return false;
      if (pipe.type === 'locked' && pipe.locked) return false;
      if (pipe.type === 'frozen' && pipe.iceCount > 0) return false;
      if (pipe.type === 'rusty' && pipe.rustIntact) return false;
      if (pipe.type === 'rotary_valve') return this.board.scan(pipe, rotateCW(pipe.dir)).canExit;
      return this.board.scan(pipe).canExit;
    }
    exitablePipeIds() { return this.board.pipes.filter((p) => this.wouldExitOnTap(p)).map((p) => p.id); }

    tap(id) {
      const pipe = this.board.get(id);
      if (this.status !== 'playing' || !pipe || pipe.removed || pipe.pendingExit) return { kind: 'ignored', pipeId: id, events: [] };
      this.taps++;
      const events = [];
      let out;
      if (pipe.type === 'locked' && pipe.locked) {
        out = this._penalize(pipe, 'locked', [], events);
      } else if (pipe.type === 'frozen' && pipe.iceCount > 0) {
        out = this._penalize(pipe, 'frozen', [], events);
      } else if (pipe.type === 'rusty' && pipe.rustIntact) {
        pipe.rustIntact = false; // free: never costs a leak, even if blocked
        out = { kind: 'rust_broken', pipeId: id, leakCost: 0 };
      } else if (pipe.type === 'rotary_valve') {
        pipe.dir = rotateCW(pipe.dir);
        const scan = this.board.scan(pipe);
        out = { kind: 'valve_rotated', pipeId: id, dir: pipe.dir, leakCost: 0, autoExit: scan.canExit, delay: TIMING.valveAutoExitDelay };
        if (scan.canExit) {
          if (this.instantValves) this._exit(pipe, 'valve', events);
          else pipe.pendingExit = true;
        }
      } else {
        const scan = this.board.scan(pipe);
        if (scan.canExit) {
          this.combo++;
          out = { kind: 'exit', pipeId: id, leakCost: 0, combo: this.combo, sweptCells: scan.sweptCells };
          this._exit(pipe, 'tap', events);
        } else {
          out = this._penalize(pipe, 'blocked', scan.blockingPipeIds, events);
          out.hitsTank = scan.hitsTank;
        }
      }
      if (this.status === 'playing') this._tickLeakTimers(events);
      this._checkLose(events);
      out.events = events;
      return out;
    }

    /** Rotary valve auto slide-out, called by the view after TIMING.valveAutoExitDelay. */
    completeValveExit(id) {
      const pipe = this.board.get(id);
      const events = [];
      if (!pipe || pipe.removed || !pipe.pendingExit || this.status !== 'playing') return events;
      this.combo++;
      events.push({ kind: 'valve_exit', pipeId: id, combo: this.combo });
      this._exit(pipe, 'valve', events);
      return events;
    }

    /** Hammer booster: removes any pipe regardless of blockers (counts as an exit, not a tap). */
    hammer(id) {
      const pipe = this.board.get(id);
      if (this.status !== 'playing' || !pipe || pipe.removed) return null;
      const events = [];
      this._exit(pipe, 'hammer', events);
      return { kind: 'hammered', pipeId: id, events };
    }

    /** Patch booster: +1 leak life, capped at RULES.maxLeaks. */
    patch() {
      if (this.status !== 'playing' || this.leaks >= this.maxLeaks) return false;
      this.leaks++;
      return true;
    }

    /** Continue after a loss (rewarded ad or a Patch on the fail screen). */
    revive(lives = 1) {
      if (this.status !== 'lost') return false;
      this.leaks = Math.min(this.maxLeaks, Math.max(1, lives));
      this.status = 'playing';
      return true;
    }

    _penalize(pipe, kind, blockers, events) {
      this.combo = 0;
      this.mistakes++;
      const cost = this.ftue ? 0 : 1;
      this.leaks -= cost;
      return { kind, pipeId: pipe.id, leakCost: cost, blockingPipeIds: blockers };
    }

    _exit(pipe, cause, events) {
      this.board.remove(pipe);
      this.exits++;
      if (pipe.water) this.remainingWaterCells -= pipe.size;
      events.push({ kind: 'removed', pipeId: pipe.id, cause, water: pipe.water });
      for (const other of this.board.pipes) {
        if (other.removed || other.type !== 'frozen' || other.iceCount <= 0) continue;
        other.iceCount--;
        events.push({ kind: other.iceCount === 0 ? 'thawed' : 'ice_cracked', pipeId: other.id, iceCount: other.iceCount });
      }
      if (pipe.type === 'key_valve' && pipe.targetId) {
        const target = this.board.get(pipe.targetId);
        if (target && !target.removed && target.locked) {
          target.locked = false;
          events.push({ kind: 'unlocked', pipeId: target.id, by: pipe.id });
        }
      }
      if (this.status === 'playing' && this.remainingWaterPipes === 0) {
        this.status = 'won';
        events.push({ kind: 'won' });
      }
    }

    _tickLeakTimers(events) {
      for (const p of this.board.pipes) {
        if (p.removed || p.type !== 'leaking') continue;
        p.leakTimer--;
        if (p.leakTimer <= 0) {
          this.mistakes++;
          if (!this.ftue) this.leaks--;
          p.leakTimer = p.leakTimerMax; // GDD: deduct one life and reset
          events.push({ kind: 'leak_burst', pipeId: p.id, leakCost: this.ftue ? 0 : 1 });
        }
      }
    }

    _checkLose(events) {
      if (this.status === 'playing' && this.leaks <= 0) {
        this.leaks = 0;
        this.status = 'lost';
        events.push({ kind: 'lost' });
      }
    }
  }

  // ------------------------------------------------------------------ meta: economy, progression, ads
  const isBreather = (level) => level % ECONOMY.breatherEvery === 0;

  function winReward(level, mistakes) {
    const base = ECONOMY.winCoins;
    const flawless = mistakes === 0 ? ECONOMY.flawlessBonus : 0;
    const breather = isBreather(level) ? ECONOMY.breatherBonus : 0;
    return { base, flawless, breather, total: base + flawless + breather };
  }

  function rankFor(level) {
    let r = RANKS[0].name;
    for (const rank of RANKS) if (level >= rank.min) r = rank.name;
    return r;
  }

  function themeFor(level) { return THEMES[Math.floor((Math.max(1, level) - 1) / 20) % THEMES.length]; }

  function boosterUnlocked(booster, level) { return !!BOOSTERS[booster] && level >= BOOSTERS[booster].unlock; }

  /** Interstitial rules: none in levels 1–3, never on fail, every 2nd eligible win, 60s cooldown. */
  class AdPolicy {
    constructor(cfg = {}) {
      this.cooldownSec = cfg.cooldownSec !== undefined ? cfg.cooldownSec : ADS.cooldownSec;
      this.everyNWins = cfg.everyNWins !== undefined ? cfg.everyNWins : ADS.everyNWins;
      this.noAdsUpTo = cfg.noInterstitialUpToLevel !== undefined ? cfg.noInterstitialUpToLevel : ADS.noInterstitialUpToLevel;
      this.lastShownAt = -Infinity;
      this.winsSinceAd = 0;
    }
    onLevelWin(level, nowSec) {
      if (level <= this.noAdsUpTo) return false;
      this.winsSinceAd++;
      if (this.winsSinceAd < this.everyNWins) return false;
      if (nowSec - this.lastShownAt < this.cooldownSec) return false;
      return true;
    }
    onLevelFail() { return false; }
    markShown(nowSec) { this.lastShownAt = nowSec; this.winsSinceAd = 0; }
  }

  // ------------------------------------------------------------------ difficulty curve
  /** Sawtooth target: smooth rise with level, every 5th level a breather dip. */
  function targetDifficulty(level) {
    const base = 1.6 + 7.4 * (1 - Math.exp(-(level - 1) / 45));
    const saw = [-0.6, -0.2, 0.2, 0.6, -1.8][(level - 1) % 5];
    return Math.round(Math.min(10, Math.max(1, base + saw)) * 10) / 10;
  }

  // ------------------------------------------------------------------ deterministic RNG (mirrors C# DeterministicRandom)
  function mulberry32(seed) {
    let a = seed >>> 0;
    const next = () => {
      a = (a + 0x6D2B79F5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
    return {
      next,
      int: (lo, hi) => lo + Math.floor(next() * (hi - lo + 1)), // inclusive
      pick: (arr) => arr[Math.floor(next() * arr.length)],
      shuffle(arr) {
        for (let i = arr.length - 1; i > 0; i--) { const j = Math.floor(next() * (i + 1)); [arr[i], arr[j]] = [arr[j], arr[i]]; }
        return arr;
      },
    };
  }

  // ------------------------------------------------------------------ level JSON writer (one pipe per line, like the GDD)
  function stringifyLevel(level) {
    const pipeLine = (p) => {
      const parts = [`"id": ${JSON.stringify(p.id)}`, `"type": ${JSON.stringify(p.type)}`,
        `"cells": [${p.cells.map((c) => `[${c[0]},${c[1]}]`).join(',')}]`, `"dir": ${JSON.stringify(p.dir)}`];
      if (p.iceCount !== undefined) parts.push(`"iceCount": ${p.iceCount}`);
      if (p.targetId !== undefined) parts.push(`"targetId": ${JSON.stringify(p.targetId)}`);
      if (p.leakTimerMoves !== undefined) parts.push(`"leakTimerMoves": ${p.leakTimerMoves}`);
      return `    { ${parts.join(', ')} }`;
    };
    const t = level.tank;
    return '{\n' +
      `  "level": ${level.level},\n` +
      `  "grid": [${level.grid[0]}, ${level.grid[1]}],\n` +
      `  "tank": { "x": ${t.x}, "y": ${t.y}, "w": ${t.w}, "h": ${t.h} },\n` +
      `  "leaks": ${level.leaks},\n` +
      '  "pipes": [\n' + level.pipes.map(pipeLine).join(',\n') + '\n  ]\n}\n';
  }

  return {
    DIRS, DIR_VEC, rotateCW, rotateCCW, PALETTE, PIPE_TYPES, UNLOCK_LEVEL, TIMING, RULES, BOOSTERS, ECONOMY, RANKS, THEMES,
    ADS, ANALYTICS_EVENTS,
    isWaterType, waterColorFor, pipeColorHex, analyzeShape,
    LevelError, validateLevel, parseLevel, stringifyLevel,
    Board, PipeState, scanPath, GameSession,
    isBreather, winReward, rankFor, themeFor, boosterUnlocked, AdPolicy, targetDifficulty,
    mulberry32,
  };
});
