/*
 * Su Ustası — deterministic level solver + difficulty metrics.
 * Mirrors unity-project/Assets/_Game/Scripts/LevelGen/LevelSolver.cs.
 *
 * Why greedy is exact for solvability: every successful exit only ever frees cells,
 * thaws ice and opens locks — it can never make another pipe harder to remove.
 * Removal is therefore monotone, the set of removable pipes is confluent, and
 * "repeatedly remove anything removable" reaches the same fixpoint as any search.
 * Leak timers are the only order-sensitive rule, so the play-out phase orders
 * moves by urgency (most-urgent leaking pipe and its blocker closure first) and
 * reports how many lives that perfect play would lose.
 */
(function (root, factory) {
  'use strict';
  const core = typeof module === 'object' && module.exports ? require('./core.js') : root.SuCore;
  const api = factory(core);
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.SuSolver = api;
})(typeof self !== 'undefined' ? self : this, function (C) {
  'use strict';

  const MAX_TAPS = 2000;

  /** Directions a rotary valve can exit through right now, in tap order (1..4 taps away). */
  function valveExitTaps(board, pipe) {
    let d = pipe.dir;
    for (let k = 1; k <= 4; k++) {
      d = C.rotateCW(d);
      if (board.scan(pipe, d).canExit) return k;
    }
    return 0;
  }

  /** Pipes that must leave before `pipe` can (transitive blockers + key valves). */
  function blockerClosure(session, pipe) {
    const board = session.board;
    const out = new Set();
    const stack = [pipe];
    while (stack.length) {
      const p = stack.pop();
      let blockers;
      if (p.type === 'rotary_valve') {
        // best direction = fewest blockers (tank-facing directions are never usable)
        let best = null;
        for (const d of C.DIRS) {
          const s = board.scan(p, d);
          if (s.hitsTank) continue;
          if (!best || s.blockingPipeIds.length < best.length) best = s.blockingPipeIds;
        }
        blockers = best || [];
      } else {
        blockers = board.scan(p).blockingPipeIds;
      }
      const extra = [];
      if (p.type === 'locked' && p.locked) {
        const key = board.pipes.find((k) => !k.removed && k.type === 'key_valve' && k.targetId === p.id);
        if (key) extra.push(key.id);
      }
      for (const id of blockers.concat(extra)) {
        if (out.has(id)) continue;
        out.add(id);
        stack.push(board.get(id));
      }
    }
    return out;
  }

  /** Remaining pipes whose current path is obstructed by `pipe` (or that it unlocks). */
  function dependentsCount(session, pipe) {
    const board = session.board;
    let n = 0;
    for (const o of board.pipes) {
      if (o.removed || o === pipe) continue;
      if (o.type === 'locked' && o.locked && pipe.type === 'key_valve' && pipe.targetId === o.id) { n++; continue; }
      const dirs = o.type === 'rotary_valve' ? C.DIRS : [o.dir]; // a valve may need any of its directions
      if (dirs.some((d) => board.scan(o, d).blockingPipeIds.includes(pipe.id))) n++;
    }
    return n;
  }

  /** Picks the next tap for perfect play; returns { id, action } or null when stuck. */
  function chooseMove(session) {
    const board = session.board;
    const live = board.pipes.filter((p) => !p.removed);
    const urgent = live.filter((p) => p.type === 'leaking').sort((a, b) => a.leakTimer - b.leakTimer || a.index - b.index)[0] || null;
    const urgentSet = urgent ? blockerClosure(session, urgent) : new Set();
    const iceWaiting = live.some((p) => p.type === 'frozen' && p.iceCount > 0);

    // 1) direct exits (one tap, pipe leaves)
    let best = null, bestScore = -Infinity;
    for (const p of live) {
      if (!session.wouldExitOnTap(p)) continue;
      const deps = dependentsCount(session, p);
      let score = 0;
      if (urgent && p === urgent) score += 10000;
      if (urgentSet.has(p.id)) score += 5000;
      if (p.water) score += 100;
      if (deps > 0) score += 50 + deps;
      if (p.type === 'key_valve') score += 40;
      if (iceWaiting) score += 10;
      if (!p.water && deps === 0 && !iceWaiting) continue; // useless dry pipe: never worth a tap
      score -= p.index * 1e-3; // deterministic tie-break
      if (score > bestScore) { bestScore = score; best = p; }
    }
    if (best) return { id: best.id, action: best.type === 'rotary_valve' ? 'rotate' : 'exit' };

    // 2) break rust (free), preferring pipes that could leave right after
    const rusty = live.filter((p) => p.type === 'rusty' && p.rustIntact);
    if (rusty.length) {
      rusty.sort((a, b) => {
        const sa = (board.scan(a).canExit ? 2 : 0) + (urgentSet.has(a.id) ? 1 : 0);
        const sb = (board.scan(b).canExit ? 2 : 0) + (urgentSet.has(b.id) ? 1 : 0);
        return sb - sa || a.index - b.index;
      });
      return { id: rusty[0].id, action: 'break_rust' };
    }

    // 3) rotate a valve toward a clear direction that is 2+ taps away
    const valves = live
      .filter((p) => p.type === 'rotary_valve' && !p.pendingExit)
      .map((p) => ({ p, k: valveExitTaps(board, p) }))
      .filter((v) => v.k > 0)
      .sort((a, b) => (urgentSet.has(b.p.id) - urgentSet.has(a.p.id)) || a.k - b.k || a.p.index - b.p.index);
    if (valves.length) return { id: valves[0].p.id, action: 'rotate' };

    // 4) fallback: any remaining exit (e.g. a dry pipe feeding frozen counters). Never harmful,
    //    so the planner always reaches the same fixpoint as peelLayers().
    const anyExit = live.find((p) => session.wouldExitOnTap(p));
    if (anyExit) return { id: anyExit.id, action: anyExit.type === 'rotary_valve' ? 'rotate' : 'exit' };
    return null;
  }

  /**
   * Layered peeling: remove every removable pipe simultaneously, repeat.
   * Number of layers until all water is gone = longest dependency chain.
   */
  function peelLayers(level) {
    const s = new C.GameSession(level, { ftue: false, instantValves: true });
    const b = s.board;
    let layers = 0;
    const layerSizes = [];
    while (s.remainingWaterPipes > 0) {
      const layer = b.pipes.filter((p) => {
        if (p.removed) return false;
        if (p.type === 'locked' && p.locked) return false;
        if (p.type === 'frozen' && p.iceCount > 0) return false;
        if (p.type === 'rotary_valve') return C.DIRS.some((d) => b.scan(p, d).canExit);
        return b.scan(p).canExit; // rust is assumed broken: it never blocks progress
      });
      if (!layer.length) return { layers, layerSizes, complete: false };
      for (const p of layer) s._exit(p, 'peel', []);
      layers++;
      layerSizes.push(layer.length);
    }
    return { layers, layerSizes, complete: true };
  }

  /** Arrow points at the pipe's nearest board edge, yet the pipe cannot leave on turn 1. */
  function deceptiveArrowRatio(session) {
    const b = session.board;
    const candidates = b.pipes.filter((p) => p.type !== 'rotary_valve');
    if (!candidates.length) return 0;
    let deceptive = 0;
    for (const p of candidates) {
      const cx = p.cells.reduce((s, c) => s + c[0], 0) / p.size;
      const cy = p.cells.reduce((s, c) => s + c[1], 0) / p.size;
      const dist = { left: cx, right: b.width - 1 - cx, up: cy, down: b.height - 1 - cy };
      const min = Math.min(dist.left, dist.right, dist.up, dist.down);
      if (Math.abs(dist[p.dir] - min) < 1e-9 && !b.scan(p).canExit) deceptive++;
    }
    return deceptive / candidates.length;
  }

  const MECH_WEIGHT = { straight: 0, elbow: 0.15, dry: 0.3, rusty: 0.45, frozen: 0.8, locked: 0.9, key_valve: 0.2, leaking: 1.1, rotary_valve: 0.8 };
  const clamp01 = (v) => Math.max(0, Math.min(1, v));

  /** 1..10 difficulty from structural metrics (same weights as DifficultyScorer.cs). */
  function difficultyScore(m) {
    const fCount = clamp01((m.pipeCount - 3) / 17);
    const fDepth = clamp01((m.maxChainDepth - 1) / 7);
    const fOpen = 1 - clamp01(m.initialExitableCount / Math.max(1, m.pipeCount));
    const fDecep = clamp01(m.deceptiveArrowRatio / 0.5);
    const fMech = clamp01(m.mechanicLoad / 6);
    const raw = 0.22 * fCount + 0.28 * fDepth + 0.18 * fOpen + 0.12 * fDecep + 0.2 * fMech;
    return Math.round((1 + 9 * raw) * 10) / 10;
  }

  /**
   * Full validation + solve. Returns
   * { valid, errors, solvable, solution:[{id,action}], taps, leaksLost, metrics }.
   */
  function solve(raw) {
    const v = C.validateLevel(raw);
    if (!v.ok) return { valid: false, errors: v.errors, solvable: false, solution: [], taps: 0, leaksLost: 0, metrics: null };
    const level = v.level;

    // --- perfect-play simulation (no FTUE protection: we want honest leak accounting)
    const s = new C.GameSession(level, { ftue: false, instantValves: true });
    s.leaks = 99; // measure losses without stopping at 0
    const solution = [];
    while (s.status === 'playing' && solution.length < MAX_TAPS) {
      const mv = chooseMove(s);
      if (!mv) break;
      const res = s.tap(mv.id);
      if (res.kind === 'ignored' || res.leakCost > 0) break; // the planner never makes losing taps
      solution.push({ id: mv.id, action: res.kind === 'exit' ? 'exit' : res.kind === 'rust_broken' ? 'break_rust' : 'rotate' });
    }
    const solvable = s.status === 'won';
    const leaksLost = 99 - s.leaks;

    // --- metrics on a fresh board
    const fresh = new C.GameSession(level, { ftue: false, instantValves: true });
    const peel = peelLayers(level);
    const counts = {};
    for (const p of fresh.board.pipes) counts[p.type] = (counts[p.type] || 0) + 1;
    const metrics = {
      pipeCount: fresh.board.pipes.length,
      waterPipeCount: fresh.board.pipes.filter((p) => p.water).length,
      waterCells: fresh.initialWaterCells,
      maxChainDepth: peel.layers,
      layerSizes: peel.layerSizes,
      initialExitableCount: fresh.exitablePipeIds().length,
      deceptiveArrowRatio: Math.round(deceptiveArrowRatio(fresh) * 1000) / 1000,
      mechanics: counts,
      mechanicLoad: Object.entries(counts).reduce((sum, [t, n]) => sum + (MECH_WEIGHT[t] || 0) * n, 0),
      solutionTaps: solution.length,
      leaksLostPerfectPlay: leaksLost,
    };
    metrics.mechanicLoad = Math.round(metrics.mechanicLoad * 100) / 100;
    metrics.difficultyScore = difficultyScore(metrics);
    return {
      valid: true,
      errors: [],
      solvable: solvable && peel.complete && leaksLost < level.leaks,
      perfect: solvable && leaksLost === 0,
      solution,
      taps: solution.length,
      leaksLost,
      metrics,
    };
  }

  /** Replays a solution on a real session (FTUE off) and reports the final status. */
  function replay(raw, solution) {
    const s = new C.GameSession(raw, { ftue: false, instantValves: true });
    for (const mv of solution) {
      const r = s.tap(mv.id);
      if (r.kind === 'ignored') return { status: 'invalid_move', at: mv.id, session: s };
    }
    return { status: s.status, session: s };
  }

  return { solve, replay, peelLayers, difficultyScore, deceptiveArrowRatio, blockerClosure, MECH_WEIGHT };
});
