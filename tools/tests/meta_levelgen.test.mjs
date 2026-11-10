// Economy / progression / ads / solver / generator / pack tests. Run: npm test
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { LEVEL_8 } from './fixtures.mjs';

const require = createRequire(import.meta.url);
const C = require('../../web-playable/js/core.js');
const S = require('../../web-playable/js/solver.js');
const G = require('../../web-playable/js/generator.js');

describe('Economy & progression', () => {
  test('win reward: 20 + 10 flawless + 20 breather (every 5th level)', () => {
    assert.deepEqual(C.winReward(7, 0), { base: 20, flawless: 10, breather: 0, total: 30 });
    assert.equal(C.winReward(7, 2).total, 20);
    assert.equal(C.winReward(10, 0).total, 50);
    assert.equal(C.winReward(15, 1).total, 40);
  });
  test('ranks by level', () => {
    assert.equal(C.rankFor(1), 'Çırak');
    assert.equal(C.rankFor(19), 'Çırak');
    assert.equal(C.rankFor(20), 'Kalfa');
    assert.equal(C.rankFor(49), 'Kalfa');
    assert.equal(C.rankFor(50), 'Usta');
    assert.equal(C.rankFor(99), 'Usta');
    assert.equal(C.rankFor(100), 'Başusta');
  });
  test('themes cycle every 20 levels through all 6 neighbourhoods', () => {
    assert.equal(C.themeFor(1), 'Banyo');
    assert.equal(C.themeFor(20), 'Banyo');
    assert.equal(C.themeFor(21), 'Mutfak');
    assert.equal(C.themeFor(61), 'Fabrika');
    assert.equal(C.themeFor(101), 'Arıtma Tesisi');
    assert.equal(C.themeFor(121), 'Banyo');
  });
  test('booster unlock levels 5 / 10 / 15', () => {
    assert.equal(C.boosterUnlocked('xray', 4), false);
    assert.equal(C.boosterUnlocked('xray', 5), true);
    assert.equal(C.boosterUnlocked('hammer', 9), false);
    assert.equal(C.boosterUnlocked('hammer', 10), true);
    assert.equal(C.boosterUnlocked('patch', 15), true);
  });
});

describe('AdPolicy', () => {
  test('no interstitials in levels 1-3', () => {
    const a = new C.AdPolicy();
    for (let l = 1; l <= 3; l++) assert.equal(a.onLevelWin(l, 1000 * l), false);
  });
  test('every 2nd eligible win, never on fail, 60s cooldown', () => {
    const a = new C.AdPolicy();
    assert.equal(a.onLevelFail(), false);
    assert.equal(a.onLevelWin(4, 100), false); // 1st eligible win
    assert.equal(a.onLevelWin(5, 110), true);  // 2nd
    a.markShown(110);
    assert.equal(a.onLevelWin(6, 120), false);
    assert.equal(a.onLevelWin(7, 130), false); // 2nd win but only 20s since last ad
    assert.equal(a.onLevelWin(8, 171), true);  // cooldown passed, counter still due
  });
});

describe('Difficulty curve', () => {
  test('every 5th level is a breather dip below its neighbours', () => {
    for (const l of [10, 25, 50, 100]) {
      assert.ok(C.targetDifficulty(l) < C.targetDifficulty(l - 1), `L${l} should dip`);
      assert.ok(C.targetDifficulty(l) < C.targetDifficulty(l + 1));
    }
  });
  test('stays within 1..10 and trends upward', () => {
    for (let l = 1; l <= 300; l++) { const d = C.targetDifficulty(l); assert.ok(d >= 1 && d <= 10); }
    assert.ok(C.targetDifficulty(104) > C.targetDifficulty(4));
  });
});

describe('LevelSolver', () => {
  test('GDD Level 8: solvable, depth 3, 3 initial exits, 2/6 deceptive', () => {
    const r = S.solve(LEVEL_8);
    assert.equal(r.solvable, true);
    assert.equal(r.perfect, true);
    assert.equal(r.metrics.maxChainDepth, 3);
    assert.deepEqual(r.metrics.layerSizes, [3, 2, 1]);
    assert.equal(r.metrics.initialExitableCount, 3);
    assert.equal(r.metrics.deceptiveArrowRatio, 0.333);
    assert.ok(r.metrics.difficultyScore >= 1 && r.metrics.difficultyScore <= 10);
    assert.equal(S.replay(LEVEL_8, r.solution).status, 'won');
  });
  test('detects an unsolvable deadlock (mutual blocking)', () => {
    const raw = { level: 40, grid: [6, 6], tank: { x: 2, y: 2, w: 2, h: 2 }, leaks: 3, pipes: [
      { id: 'a', type: 'straight', cells: [[0, 0], [1, 0]], dir: 'right' },
      { id: 'b', type: 'straight', cells: [[5, 0], [5, 1]], dir: 'left' },
    ] };
    // a -> right hits b at (5,0); b -> left from (5,0) hits a, from (5,1) is clear but (5,0) is blocked
    const r = S.solve(raw);
    assert.equal(r.valid, true);
    assert.equal(r.solvable, false);
  });
  test('leaking pipe is played first so perfect play loses nothing', () => {
    const raw = { level: 40, grid: [6, 6], tank: { x: 2, y: 2, w: 2, h: 2 }, leaks: 3, pipes: [
      { id: 'a', type: 'straight', cells: [[0, 0], [1, 0]], dir: 'up' },
      { id: 'b', type: 'straight', cells: [[2, 0], [3, 0]], dir: 'up' },
      { id: 'c', type: 'straight', cells: [[4, 0], [5, 0]], dir: 'up' },
      { id: 'g', type: 'leaking', cells: [[0, 5], [1, 5]], dir: 'down', leakTimerMoves: 1 },
    ] };
    const r = S.solve(raw);
    assert.equal(r.solution[0].id, 'g');
    assert.equal(r.leaksLost, 0);
  });
});

describe('LevelGenerator (reverse build)', () => {
  test('deterministic for a given level + seed', () => {
    assert.deepEqual(G.generateLevel(23).json, G.generateLevel(23).json);
  });
  test('canonical reverse-build order is itself a valid solution', () => {
    for (const n of [9, 14, 21, 47]) {
      const g = G.generateLevel(n);
      const s = new C.GameSession(g.json, { ftue: false, instantValves: true });
      for (const id of g.canonicalOrder) {
        const p = s.board.get(id);
        if (p.removed) continue;
        for (let k = 0; k < 4 && !p.removed; k++) s.tap(id); // rust: 2 taps, valves: rotate until out
      }
      assert.equal(s.status, 'won', `L${n}`);
      assert.equal(s.mistakes, 0, `L${n}: canonical order needed a blocked tap`);
    }
  });
  test('generated levels are 100% solvable, perfect, and respect unlock levels', () => {
    for (const n of [1, 4, 12, 16, 22, 30, 35, 61, 88]) {
      const g = G.generateLevel(n);
      assert.equal(g.result.solvable && g.result.perfect, true, `L${n}`);
      for (const p of g.json.pipes) assert.ok(n >= C.UNLOCK_LEVEL[p.type], `L${n} uses ${p.type} too early`);
      const v = C.validateLevel(g.json);
      assert.equal(v.ok, true, v.errors.join());
    }
  });
  test('intro levels showcase their new mechanic', () => {
    const intro = { 12: 'dry', 16: 'rusty', 22: 'frozen', 30: 'locked', 35: 'leaking', 61: 'rotary_valve' };
    for (const [n, t] of Object.entries(intro)) assert.ok(G.generateLevel(+n).result.metrics.mechanics[t] > 0, `L${n} lacks ${t}`);
  });
  test('board sizes scale 5x5 (2x2 tank) → 9x11 (3x3 tank)', () => {
    assert.deepEqual(G.boardFor(1), { w: 5, h: 5, tw: 2, th: 2 });
    assert.deepEqual(G.boardFor(120), { w: 9, h: 11, tw: 3, th: 3 });
  });
});

describe('Shipped level pack', () => {
  test('validator passes (both folders, manifest, bundle, Level 8 reference)', async () => {
    const { validatePack } = await import('../validate_levels.mjs');
    const { failures, rows } = validatePack();
    assert.deepEqual(failures, []);
    assert.ok(rows.length >= 20);
  });
});
