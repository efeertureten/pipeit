// Rules-core tests (mirrors unity-project/Assets/_Game/Tests/EditMode). Run: npm test
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const C = require('../../web-playable/js/core.js');
import { LEVEL_8 } from './fixtures.mjs';

const TANK = { x: 2, y: 2, w: 2, h: 2 };
const lvl = (pipes, o = {}) => ({ level: o.level ?? 40, grid: o.grid ?? [6, 6], tank: o.tank ?? TANK, leaks: o.leaks ?? 3, pipes });
const P = (id, type, cells, dir, extra = {}) => Object.assign({ id, type, cells, dir }, extra);
const session = (pipes, o, so) => new C.GameSession(lvl(pipes, o), so);


describe('PathScanner', () => {
  test('clear straight pipe can exit and reports swept cells to the edge', () => {
    const s = session([P('a', 'straight', [[0, 0], [1, 0]], 'right')]);
    const r = s.board.scan(s.board.get('a'));
    assert.equal(r.canExit, true);
    assert.deepEqual(r.blockingPipeIds, []);
    assert.deepEqual(r.sweptCells, [[2, 0], [3, 0], [4, 0], [5, 0]]);
  });

  test('own cells never block (axial slide)', () => {
    const s = session([P('a', 'straight', [[0, 0], [1, 0], [2, 0]], 'left')]);
    assert.equal(s.board.scan(s.board.get('a')).canExit, true);
  });

  test('blockers are reported nearest-first and deduplicated', () => {
    const s = session([
      P('a', 'straight', [[0, 0], [0, 1]], 'right'),
      P('far', 'straight', [[5, 0], [5, 1]], 'up'),
      P('near', 'straight', [[2, 1], [3, 1]], 'up'),
    ]);
    const r = s.board.scan(s.board.get('a'));
    assert.equal(r.canExit, false);
    assert.deepEqual(r.blockingPipeIds, ['near', 'far']);
  });

  test('elbow: the WHOLE body sweeps — any blocked cell blocks the pipe', () => {
    // GDD Level 8: p4 (elbow, right) is blocked by p3 only through its top cell (4,1)
    const s = new C.GameSession(LEVEL_8);
    const r = s.board.scan(s.board.get('p4'));
    assert.equal(r.canExit, false);
    assert.deepEqual(r.blockingPipeIds, ['p3']);
    assert.deepEqual(r.sweptCells, [[5, 1]]);
  });

  test('perpendicular slide sweeps every body cell', () => {
    const s = session([P('a', 'straight', [[0, 4], [1, 4]], 'down'), P('b', 'straight', [[1, 5], [2, 5]], 'right')]);
    assert.deepEqual(s.board.scan(s.board.get('a')).blockingPipeIds, ['b']);
  });

  test('tank cells block and are flagged', () => {
    const raw = lvl([P('a', 'straight', [[0, 2], [1, 2]], 'left')]);
    const s = new C.GameSession(raw);
    const r = s.board.scan(s.board.get('a'), 'right');
    assert.equal(r.hitsTank, true);
    assert.equal(r.canExit, false);
  });
});

describe('Level validation', () => {
  test('GDD Level 8 reference is valid', () => assert.equal(C.validateLevel(LEVEL_8).ok, true));
  test('rejects an exit path through the tank', () => {
    const v = C.validateLevel(lvl([P('a', 'straight', [[0, 2], [1, 2]], 'right')]));
    assert.equal(v.ok, false);
    assert.match(v.errors.join(), /crosses the tank/);
  });
  test('rejects overlaps, tank cells, bad shapes and dangling keys', () => {
    assert.equal(C.validateLevel(lvl([P('a', 'straight', [[0, 0], [1, 0]], 'up'), P('b', 'straight', [[1, 0], [1, 1]], 'up')])).ok, false);
    assert.equal(C.validateLevel(lvl([P('a', 'straight', [[2, 2], [2, 1]], 'up')])).ok, false);
    assert.equal(C.validateLevel(lvl([P('a', 'straight', [[0, 0], [2, 0]], 'up')])).ok, false); // gap
    assert.equal(C.validateLevel(lvl([P('a', 'elbow', [[0, 0], [1, 0]], 'up')])).ok, false);   // elbow needs an L
    assert.equal(C.validateLevel(lvl([P('k', 'key_valve', [[0, 0], [1, 0]], 'up', { targetId: 'nope' })])).ok, false);
    assert.equal(C.validateLevel(lvl([P('l', 'locked', [[0, 0], [1, 0]], 'up')])).ok, false);  // lock without key
  });
  test('analyzeShape orders an L arm → corner → arm', () => {
    const s = C.analyzeShape([[4, 1], [5, 2], [4, 2]]);
    assert.equal(s.kind, 'L');
    assert.deepEqual(s.path, [[5, 2], [4, 2], [4, 1]]);
  });
});

describe('Tap rules', () => {
  test('exit frees cells IMMEDIATELY (non-blocking chain taps)', () => {
    const s = new C.GameSession(LEVEL_8);
    assert.equal(s.tap('p6').kind, 'blocked'); // p5 in the way
    assert.equal(s.tap('p2').kind, 'exit');
    assert.equal(s.board.pipeAt(4, 4), null);
    assert.equal(s.tap('p5').kind, 'exit');     // no waiting for the 0.6s animation
    assert.equal(s.tap('p6').kind, 'exit');
  });

  test('blocked tap costs exactly 1 leak and reports blockers', () => {
    const s = new C.GameSession(LEVEL_8);
    const r = s.tap('p6');
    assert.equal(r.leakCost, 1);
    assert.deepEqual(r.blockingPipeIds, ['p5']);
    assert.equal(s.leaks, 2);
    assert.equal(s.mistakes, 1);
  });

  test('tank ratio = remaining water cells / initial water cells', () => {
    const s = new C.GameSession(LEVEL_8); // 13 water cells
    s.tap('p1');
    assert.equal(s.waterRatio, 11 / 13);
    s.tap('p4'); // still blocked by p3
    s.tap('p3'); s.tap('p4');
    assert.equal(s.waterRatio, 6 / 13);
  });

  test('win when all water pipes are cleared — dry pipes may remain', () => {
    const s = session([P('w', 'straight', [[0, 0], [1, 0]], 'up'), P('d', 'dry', [[0, 5], [1, 5]], 'down')]);
    assert.equal(s.initialWaterCells, 2);
    const r = s.tap('w');
    assert.equal(s.status, 'won');
    assert.ok(r.events.some((e) => e.kind === 'won'));
  });

  test('dry pipes block paths until removed', () => {
    const s = session([P('w', 'straight', [[0, 0], [0, 1]], 'right'), P('d', 'dry', [[4, 0], [4, 1]], 'up')]);
    assert.equal(s.tap('w').kind, 'blocked');
    assert.equal(s.tap('d').kind, 'exit');
    assert.equal(s.waterRatio, 1); // dry removal drains nothing
    assert.equal(s.tap('w').kind, 'exit');
  });

  test('lose at 0 leaks', () => {
    const s = new C.GameSession(LEVEL_8);
    s.tap('p6'); s.tap('p6');
    const r = s.tap('p6');
    assert.equal(s.status, 'lost');
    assert.ok(r.events.some((e) => e.kind === 'lost'));
    assert.equal(s.tap('p1').kind, 'ignored');
  });

  test('FTUE levels 1-3 never lose leak lives', () => {
    const raw = Object.assign({}, LEVEL_8, { level: 2 });
    const s = new C.GameSession(raw);
    for (let i = 0; i < 5; i++) assert.equal(s.tap('p6').leakCost, 0);
    assert.equal(s.leaks, 3);
    assert.equal(s.status, 'playing');
    assert.equal(s.mistakes, 5); // still counted (no flawless bonus)
  });
});

describe('Pipe mechanics', () => {
  test('rusty: first tap breaks rust for free even when blocked, second tap exits', () => {
    const s = session([P('r', 'rusty', [[0, 0], [0, 1]], 'right'), P('b', 'straight', [[3, 0], [3, 1]], 'up')]);
    const r1 = s.tap('r');
    assert.equal(r1.kind, 'rust_broken');
    assert.equal(r1.leakCost, 0);
    assert.equal(s.leaks, 3);
    assert.equal(s.tap('r').kind, 'blocked'); // now a normal pipe
    s.tap('b');
    assert.equal(s.tap('r').kind, 'exit');
  });

  test('frozen: iceCount drops on every OTHER exit; tapping while frozen costs 1 leak', () => {
    const s = session([
      P('f', 'frozen', [[0, 0], [1, 0]], 'up', { iceCount: 2 }),
      P('a', 'straight', [[0, 5], [1, 5]], 'down'),
      P('b', 'straight', [[5, 4], [5, 5]], 'right'),
    ]);
    assert.equal(s.tap('f').kind, 'frozen');
    assert.equal(s.leaks, 2);
    s.tap('a');
    assert.equal(s.board.get('f').iceCount, 1);
    const r = s.tap('b');
    assert.ok(r.events.some((e) => e.kind === 'thawed' && e.pipeId === 'f'));
    assert.equal(s.tap('f').kind, 'exit');
  });

  test('locked pipe opens only after its key_valve exits; early taps cost 1 leak', () => {
    const s = session([
      P('l', 'locked', [[0, 0], [1, 0]], 'up'),
      P('k', 'key_valve', [[0, 5], [1, 5]], 'down', { targetId: 'l' }),
    ]);
    assert.equal(s.tap('l').kind, 'locked');
    assert.equal(s.leaks, 2);
    const r = s.tap('k');
    assert.ok(r.events.some((e) => e.kind === 'unlocked' && e.pipeId === 'l'));
    assert.equal(s.tap('l').kind, 'exit');
    assert.equal(s.status, 'won');
  });

  test('leaking: timer drops on every tap (valid or not); at 0 costs 1 leak and resets', () => {
    const s = session([
      P('g', 'leaking', [[0, 0], [0, 1]], 'right', { leakTimerMoves: 3 }),
      P('blk', 'straight', [[3, 0], [3, 1]], 'up'),
      P('x', 'straight', [[0, 5], [1, 5]], 'left'),
    ]);
    s.tap('g'); // blocked: -1 leak, timer 2
    assert.equal(s.board.get('g').leakTimer, 2);
    s.tap('x'); // timer 1
    const r = s.tap('blk'); // timer 0 -> burst
    assert.ok(r.events.some((e) => e.kind === 'leak_burst'));
    assert.equal(s.leaks, 1);
    assert.equal(s.board.get('g').leakTimer, 3);
    assert.equal(s.tap('g').kind, 'exit'); // exiting stops the timer
  });

  test('rotary valve rotates 90° clockwise for free and auto-exits when clear', () => {
    const s = session([
      P('v', 'rotary_valve', [[0, 0]], 'left'),   // left is clear but tapping always rotates first
      P('b', 'straight', [[3, 0], [4, 0]], 'right'),
    ]);
    const r1 = s.tap('v');
    assert.equal(r1.kind, 'valve_rotated');
    assert.equal(s.board.get('v').dir, 'up');
    assert.equal(r1.autoExit, true);
    assert.equal(r1.delay, 0.5);
    assert.equal(r1.leakCost, 0);
    assert.equal(s.board.get('v').pendingExit, true);
    assert.equal(s.tap('v').kind, 'ignored'); // input locked while it slides
    const ev = s.completeValveExit('v');
    assert.ok(ev.some((e) => e.kind === 'removed'));
    assert.equal(s.board.pipeAt(0, 0), null);
  });

  test('rotary valve keeps rotating while every direction is blocked (no leak cost)', () => {
    const s = session([
      P('v', 'rotary_valve', [[1, 1]], 'up'),
      P('t', 'straight', [[0, 0], [1, 0]], 'left'),
      P('l', 'straight', [[0, 1], [0, 2]], 'down'),
    ]);
    // up blocked by t, right -> (2,1),(3,1)... clear? (2,1) empty -> clear
    const r = s.tap('v');
    assert.equal(s.board.get('v').dir, 'right');
    assert.equal(r.autoExit, true);
    assert.equal(s.leaks, 3);
  });

  test('cycles up → right → down → left', () => {
    assert.deepEqual(['up', 'right', 'down', 'left'].map(C.rotateCW), ['right', 'down', 'left', 'up']);
  });
});

describe('Boosters', () => {
  test('XRay lists exactly the pipes a tap would remove now', () => {
    const s = new C.GameSession(LEVEL_8);
    assert.deepEqual(s.exitablePipeIds(), ['p1', 'p2', 'p3']);
  });
  test('Hammer removes a blocked pipe and counts as an exit (ice, keys)', () => {
    const s = session([
      P('f', 'frozen', [[0, 0], [1, 0]], 'up', { iceCount: 1 }),
      P('a', 'straight', [[0, 4], [0, 5]], 'right'),
      P('b', 'straight', [[2, 5], [3, 5]], 'down'),
    ]);
    assert.equal(s.tap('a').kind, 'blocked');
    s.hammer('a');
    assert.equal(s.board.get('a').removed, true);
    assert.equal(s.board.get('f').iceCount, 0);
    assert.equal(s.taps, 1); // hammer is not a tap
  });
  test('Patch adds +1 leak capped at 4', () => {
    const s = new C.GameSession(LEVEL_8);
    assert.equal(s.patch(), true);
    assert.equal(s.leaks, 4);
    assert.equal(s.patch(), false);
    assert.equal(s.leaks, 4);
  });
  test('revive after a loss', () => {
    const s = new C.GameSession(LEVEL_8);
    s.tap('p6'); s.tap('p6'); s.tap('p6');
    assert.equal(s.revive(1), true);
    assert.equal(s.status, 'playing');
    assert.equal(s.leaks, 1);
  });
});
