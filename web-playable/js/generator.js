/*
 * Su Ustası — reverse-build level generator.
 * Mirrors unity-project/Assets/_Game/Scripts/LevelGen/LevelGenerator.cs.
 *
 * Reverse build: pipes are placed in REVERSE exit order. The first pipe placed is the
 * last one to leave, so its exit path only has to avoid the tank. Every later pipe
 *   - must keep its own swept exit path clear of the tank and of every pipe placed so far
 *     (those are still on the board when it leaves), and
 *   - may put its body inside the exit paths of earlier pipes — that is exactly what
 *     creates the dependency DAG (they stay blocked until it has left).
 * Exiting in reverse placement order is therefore always a valid solution. Mechanics are
 * overlaid afterwards so that canonical order stays valid (ice counts never exceed the
 * number of earlier exits, keys always leave before their lock, ...). Every candidate
 * is then re-verified by the independent solver and scored against the sawtooth curve.
 */
(function (root, factory) {
  'use strict';
  const isNode = typeof module === 'object' && module.exports;
  const core = isNode ? require('./core.js') : root.SuCore;
  const solver = isNode ? require('./solver.js') : root.SuSolver;
  const api = factory(core, solver);
  if (isNode) module.exports = api;
  else root.SuGenerator = api;
})(typeof self !== 'undefined' ? self : this, function (C, S) {
  'use strict';

  // ---------------------------------------------------------------- level bands
  function boardFor(level) {
    if (level <= 3) return { w: 5, h: 5, tw: 2, th: 2 };
    if (level <= 14) return { w: 6, h: 6, tw: 2, th: 2 };
    if (level <= 29) return { w: 7, h: 7, tw: 3, th: 3 };
    if (level <= 49) return { w: 7, h: 8, tw: 3, th: 3 };
    if (level <= 79) return { w: 8, h: 9, tw: 3, th: 3 };
    return { w: 9, h: 11, tw: 3, th: 3 };
  }

  function pipeCountFor(level, breather, bd) {
    if (level <= 3) return [3, 4, 5][level - 1];
    let n = 3 + Math.round(13 * (1 - Math.exp(-(level - 1) / 28)));
    if (bd.w * bd.h >= 99) n += 2;
    if (breather) n = Math.max(3, Math.round(n * 0.7));
    const free = bd.w * bd.h - bd.tw * bd.th;
    return Math.min(n, Math.floor(free / 3.2)); // keep room for clear exit lanes
  }

  function lengthLimits(level, breather) {
    const straightMax = breather ? 3 : level < 8 ? 3 : level < 30 ? 4 : 5;
    const elbowMax = breather ? 4 : level < 30 ? 4 : 5;
    return { straightMax, elbowMax };
  }

  function targetDepthFor(level, target, breather) {
    if (level === 1) return 1;
    if (level <= 3) return 2;
    const d = Math.round(1 + target * 0.55);
    return Math.max(2, Math.min(7, breather ? d - 1 : d));
  }

  /** How many of each mechanic a level gets: intro levels showcase the new type. */
  function mechanicPlan(level, n, rng, breather) {
    const unlocked = (t) => level >= C.UNLOCK_LEVEL[t];
    const since = (t) => level - C.UNLOCK_LEVEL[t];
    const plan = { elbows: 0, dry: 0, rusty: 0, frozen: 0, lockedPairs: 0, leaking: 0, valves: 0 };
    const pickCount = (t, introCount, lo, hi) => {
      if (!unlocked(t)) return 0;
      if (since(t) <= 1) return introCount;            // intro + follow-up level
      if (since(t) <= 4) return Math.max(1, rng.int(lo, hi)); // reinforcement window
      return rng.int(lo, hi);
    };
    if (unlocked('elbow')) plan.elbows = since('elbow') <= 1 ? 2 : Math.max(1, Math.round(n * (0.18 + rng.next() * 0.14)));
    plan.dry = pickCount('dry', 2, 0, 2);
    plan.rusty = pickCount('rusty', 2, 0, 2);
    plan.frozen = pickCount('frozen', 2, 0, 2);
    plan.lockedPairs = pickCount('locked', 1, 0, 1) + (level >= 70 && rng.next() < 0.4 ? 1 : 0);
    plan.leaking = pickCount('leaking', 1, 0, 1);
    plan.valves = pickCount('rotary_valve', 2, 0, 2);
    if (breather) {
      for (const k of ['dry', 'rusty', 'frozen', 'leaking', 'valves']) {
        const t = k === 'valves' ? 'rotary_valve' : k;
        if (since(t) > 1) plan[k] = Math.floor(plan[k] / 2);
      }
      if (since('locked') > 1) plan.lockedPairs = 0;
    }
    // Keep at least ~40% plain pipes so mechanics stay readable.
    const budget = Math.max(1, Math.floor(n * 0.6));
    const order = ['valves', 'leaking', 'lockedPairs', 'frozen', 'rusty', 'dry'];
    let used = () => plan.dry + plan.rusty + plan.frozen + plan.lockedPairs * 2 + plan.leaking + plan.valves;
    for (const k of order.slice().reverse()) {
      while (used() > budget && plan[k] > 0) {
        const t = k === 'valves' ? 'rotary_valve' : k === 'lockedPairs' ? 'locked' : k;
        if (since(t) <= 1 && plan[k] <= 1) break; // never drop the showcase instance
        plan[k]--;
      }
    }
    return plan;
  }

  // ---------------------------------------------------------------- shapes
  function lineShapes(maxLen) {
    const out = [];
    for (let len = 2; len <= maxLen; len++) {
      out.push(Array.from({ length: len }, (_, i) => [i, 0]));
      out.push(Array.from({ length: len }, (_, i) => [0, i]));
    }
    return out;
  }

  function lShapes(maxCells) {
    const out = [];
    for (let a = 2; a <= 4; a++) {
      for (let b = 2; b <= 4; b++) {
        if (a + b - 1 > maxCells || a + b - 1 < 3) continue;
        // corner at each bbox corner: horizontal arm length a, vertical arm length b
        for (const [sx, sy] of [[1, 1], [-1, 1], [1, -1], [-1, -1]]) {
          const cells = [];
          for (let i = 0; i < a; i++) cells.push([i * sx, 0]);
          for (let j = 1; j < b; j++) cells.push([0, j * sy]);
          const minX = Math.min(...cells.map((c) => c[0])), minY = Math.min(...cells.map((c) => c[1]));
          out.push(cells.map(([x, y]) => [x - minX, y - minY]));
        }
      }
    }
    return out;
  }

  // ---------------------------------------------------------------- one build attempt
  function buildOnce(levelNumber, rng, target) {
    const breather = C.isBreather(levelNumber);
    const bd = boardFor(levelNumber);
    const W = bd.w, H = bd.h;
    const tank = { x: Math.floor((W - bd.tw) / 2), y: Math.floor((H - bd.th) / 2), w: bd.tw, h: bd.th };
    const n = pipeCountFor(levelNumber, breather, bd);
    const plan = mechanicPlan(levelNumber, n, rng, breather);
    const lim = lengthLimits(levelNumber, breather);
    const targetDepth = targetDepthFor(levelNumber, target, breather);

    const lCount = Math.min(n - plan.valves, plan.elbows + plan.rusty + plan.leaking);
    const kinds = [];
    for (let i = 0; i < plan.valves; i++) kinds.push('single');
    for (let i = 0; i < lCount; i++) kinds.push('L');
    while (kinds.length < n) kinds.push('line');
    rng.shuffle(kinds);

    const TANK = -2, EMPTY = -1;
    const occ = new Int16Array(W * H).fill(EMPTY);
    for (let y = tank.y; y < tank.y + tank.h; y++) for (let x = tank.x; x < tank.x + tank.w; x++) occ[y * W + x] = TANK;
    const placed = []; // {cells, dir, kind, height, blocks:number[], path:Set}
    const shapesFor = { single: [[[0, 0]]], line: lineShapes(lim.straightMax), L: lShapes(lim.elbowMax), short: lineShapes(2) };

    // Dependency appetite: breathers avoid stacking, harder levels seek it.
    // Late breathers still get a little interlocking so they don't collapse to trivial.
    const depWeight = levelNumber === 1 ? -3 : breather ? (levelNumber < 20 ? -0.4 : 0.1 + target * 0.06) : 0.35 + target * 0.12;

    const findBest = (kind, shapes) => {
      let best = null, bestScore = -Infinity;
      for (const shape of shapes) {
        const sw = Math.max(...shape.map((c) => c[0])) + 1, sh = Math.max(...shape.map((c) => c[1])) + 1;
        for (let oy = 0; oy + sh <= H; oy++) {
          for (let ox = 0; ox + sw <= W; ox++) {
            const cells = shape.map(([x, y]) => [x + ox, y + oy]);
            if (cells.some(([x, y]) => occ[y * W + x] !== EMPTY)) continue;
            const own = new Set(cells.map(([x, y]) => y * W + x));
            for (const dir of C.DIRS) {
              const [dx, dy] = C.DIR_VEC[dir];
              const path = new Set();
              let clear = true;
              for (const [cx, cy] of cells) {
                for (let x = cx + dx, y = cy + dy; x >= 0 && y >= 0 && x < W && y < H; x += dx, y += dy) {
                  const i = y * W + x;
                  if (own.has(i)) continue;
                  if (occ[i] !== EMPTY) { clear = false; break; }
                  path.add(i);
                }
                if (!clear) break;
              }
              if (!clear) continue;
              // which earlier pipes would this body block?
              const blocks = [];
              for (let j = 0; j < placed.length; j++) if (cells.some(([x, y]) => placed[j].path.has(y * W + x))) blocks.push(j);
              const height = 1 + blocks.reduce((m, j) => Math.max(m, placed[j].height), 0);
              let adj = 0;
              for (const [x, y] of cells) {
                for (const [ax, ay] of [[x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]]) {
                  if (ax < 0 || ay < 0 || ax >= W || ay >= H) continue;
                  if (!own.has(ay * W + ax) && occ[ay * W + ax] !== EMPTY) adj++;
                }
              }
              const axial = kind === 'line' && ((dir === 'left' || dir === 'right') === (shape[0][1] === (shape[1] || shape[0])[1]));
              let score = 0;
              score += depWeight * (blocks.length > 0 ? 1 : 0) + 0.2 * Math.min(blocks.length, 3) * Math.sign(depWeight);
              score += height <= targetDepth ? 0.7 * (height / targetDepth) : -0.9 * (height - targetDepth);
              score += 0.22 * adj + 0.12 * cells.length + (axial ? 0.25 : 0);
              score += rng.next() * 0.9;
              if (score > bestScore) { bestScore = score; best = { cells, dir, kind, height, blocks, path }; }
            }
          }
        }
      }
      return best;
    };

    for (const kind of kinds) {
      // Crowded late placements fall back to a short straight rather than dropping the pipe.
      const best = findBest(kind, shapesFor[kind]) || (kind !== 'single' ? findBest('line', shapesFor.short) : null);
      if (!best) continue;
      for (const [x, y] of best.cells) occ[y * W + x] = placed.length;
      placed.push(best);
    }
    if (placed.length < Math.max(3, n - 2)) return null;

    // ------------------------------------------------------------ mechanics overlay
    const N = placed.length;
    const exitStep = (k) => N - 1 - k;          // canonical order: last placed leaves first
    const blockedBy = placed.map(() => []);
    placed.forEach((p, j) => p.blocks.forEach((k) => blockedBy[k].push(j)));
    const ancestors = (k) => { const seen = new Set(); const st = [...blockedBy[k]]; while (st.length) { const j = st.pop(); if (seen.has(j)) continue; seen.add(j); st.push(...blockedBy[j]); } return seen.size; };
    const type = new Array(N).fill(null);
    const extra = placed.map(() => ({}));
    const free = (pred) => placed.map((p, k) => k).filter((k) => type[k] === null && pred(k));
    const pickPref = (list, prefer) => { const good = list.filter(prefer); return good.length ? rng.pick(good) : list.length ? rng.pick(list) : -1; };

    placed.forEach((p, k) => {
      if (p.kind === 'single') {
        type[k] = 'rotary_valve';
        extra[k].designDir = p.dir;
        let d = p.dir; const r = rng.int(1, 3);
        for (let i = 0; i < r; i++) d = C.rotateCCW(d);
        p.dir = d;
      }
    });
    for (let i = 0; i < plan.lockedPairs; i++) {
      const lockK = pickPref(free((k) => exitStep(k) >= 1 && placed[k].kind !== 'single'), (k) => blockedBy[k].length === 0);
      if (lockK < 0) break;
      type[lockK] = 'locked';
      const keyK = pickPref(free((k) => k > lockK && placed[k].kind !== 'single'), (k) => placed[k].kind === 'line');
      if (keyK < 0) { type[lockK] = null; break; }
      type[keyK] = 'key_valve';
      extra[keyK].targetIndex = lockK;
    }
    for (let i = 0; i < plan.frozen; i++) {
      const k = pickPref(free((k) => exitStep(k) >= 1 && placed[k].kind === 'line'), (k) => blockedBy[k].length === 0);
      if (k < 0) break;
      type[k] = 'frozen';
      extra[k].iceCount = rng.int(1, Math.min(exitStep(k), 3));
    }
    for (let i = 0; i < plan.leaking; i++) {
      const pool = free((k) => placed[k].kind !== 'single').sort((a, b) => ancestors(a) - ancestors(b) || exitStep(a) - exitStep(b));
      const lPool = pool.filter((k) => placed[k].kind === 'L' && ancestors(k) <= 2);
      const k = lPool.length ? lPool[0] : pool.length && ancestors(pool[0]) <= 2 ? pool[0] : -1;
      if (k < 0) break;
      type[k] = 'leaking';
      extra[k].leakTimerMoves = C.RULES.defaultLeakTimer;
    }
    for (let i = 0; i < plan.rusty; i++) {
      const k = pickPref(free((k) => placed[k].kind !== 'single'), (k) => placed[k].kind === 'L');
      if (k < 0) break;
      type[k] = 'rusty';
    }
    for (let i = 0; i < plan.dry; i++) {
      const k = pickPref(free((k) => placed[k].kind !== 'single'), (k) => placed[k].blocks.length > 0);
      if (k < 0) break;
      type[k] = 'dry';
    }
    placed.forEach((p, k) => { if (type[k] === null) type[k] = p.kind === 'L' ? 'elbow' : 'straight'; });
    if (!type.some((t) => C.isWaterType(t))) return null;

    // ------------------------------------------------------------ ids in reading order
    const order = placed.map((p, k) => k).sort((a, b) => {
      const pa = placed[a].cells, pb = placed[b].cells;
      const ya = Math.min(...pa.map((c) => c[1])), yb = Math.min(...pb.map((c) => c[1]));
      const xa = Math.min(...pa.filter((c) => c[1] === ya).map((c) => c[0])), xb = Math.min(...pb.filter((c) => c[1] === yb).map((c) => c[0]));
      return ya - yb || xa - xb;
    });
    const idOf = new Array(N);
    order.forEach((k, i) => { idOf[k] = 'p' + (i + 1); });
    const pipes = order.map((k) => {
      const p = placed[k];
      const shape = C.analyzeShape(p.cells);
      const out = { id: idOf[k], type: type[k], cells: shape.path, dir: p.dir };
      if (type[k] === 'frozen') out.iceCount = extra[k].iceCount;
      if (type[k] === 'key_valve') out.targetId = idOf[extra[k].targetIndex];
      if (type[k] === 'leaking') out.leakTimerMoves = extra[k].leakTimerMoves;
      return out;
    });
    return {
      json: { level: levelNumber, grid: [W, H], tank, leaks: C.RULES.defaultLeaks, pipes },
      plan,
      targetDepth,
      canonicalOrder: placed.map((p, k) => idOf[k]).reverse(),
    };
  }

  // ---------------------------------------------------------------- acceptance
  function meetsConstraints(levelNumber, res, plan) {
    const m = res.metrics;
    if (!res.valid || !res.solvable || !res.perfect) return false;
    if (m.initialExitableCount < 1) return false;
    if (levelNumber <= 3 && m.maxChainDepth > 2) return false;
    if (C.isBreather(levelNumber) && m.initialExitableCount / m.pipeCount < 0.4) return false;
    // intro levels must actually showcase their mechanic
    const need = { elbow: 'elbows', dry: 'dry', rusty: 'rusty', frozen: 'frozen', locked: 'lockedPairs', leaking: 'leaking', rotary_valve: 'valves' };
    for (const [t, k] of Object.entries(need)) {
      if (levelNumber - C.UNLOCK_LEVEL[t] <= 1 && levelNumber >= C.UNLOCK_LEVEL[t] && plan[k] > 0 && !(m.mechanics[t] > 0)) return false;
    }
    return true;
  }

  /**
   * Generates one validated level. Tries `attempts` deterministic sub-seeds and keeps the
   * candidate whose DifficultyScore is closest to the sawtooth target.
   */
  function generateLevel(levelNumber, opts = {}) {
    const attempts = opts.attempts || 40;
    const seed = opts.seed !== undefined ? opts.seed : ((levelNumber * 7919) ^ 0x5eed) >>> 0;
    const target = C.targetDifficulty(levelNumber);
    let best = null;
    for (let a = 0; a < attempts; a++) {
      const rng = C.mulberry32((seed + a * 104729) >>> 0);
      const built = buildOnce(levelNumber, rng, target);
      if (!built) continue;
      const res = S.solve(built.json);
      if (!meetsConstraints(levelNumber, res, built.plan)) continue;
      const err = Math.abs(res.metrics.difficultyScore - target);
      if (!best || err < best.err - 1e-9) best = { json: built.json, result: res, err, attempt: a, seed, target, canonicalOrder: built.canonicalOrder };
      if (err <= 0.25) break;
    }
    if (!best) throw new Error(`could not generate a valid level ${levelNumber} in ${attempts} attempts`);
    return best;
  }

  return { generateLevel, buildOnce, meetsConstraints, boardFor, pipeCountFor, mechanicPlan, lengthLimits, targetDepthFor, lineShapes, lShapes };
});
