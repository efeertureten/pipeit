#!/usr/bin/env node
/**
 * Su Ustası — level pack builder (reverse-build generator + solver validation).
 *
 *   node tools/generate_levels.mjs                 # build the default pack
 *   node tools/generate_levels.mjs --levels 1-12   # subset
 *   node tools/generate_levels.mjs --dry           # print metrics only, write nothing
 *
 * Writes identical JSON into
 *   web-playable/levels/                      (+ manifest.json, levels.bundle.js for file://)
 *   unity-project/Assets/_Game/Resources/Levels/  (+ manifest.json)
 * Levels listed in tools/reference_levels/ (the GDD Level 8) are copied verbatim.
 */
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync, writeFileSync, existsSync, readdirSync, unlinkSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const C = require(join(ROOT, 'web-playable/js/core.js'));
const S = require(join(ROOT, 'web-playable/js/solver.js'));
const G = require(join(ROOT, 'web-playable/js/generator.js'));

// 1–36 contiguous (every unlock up to `leaking`), then samples across the later bands
// (rotary valves at 61+, 9x11 boards at 80+). Multiples of 5 are breather levels.
export const DEFAULT_PACK = '1-36,38,42,47,50,54,58,61,62,63,68,74,81,88,95,100,107,116';
export const OUT_DIRS = [join(ROOT, 'web-playable/levels'), join(ROOT, 'unity-project/Assets/_Game/Resources/Levels')];
export const REFERENCE_DIR = join(ROOT, 'tools/reference_levels');

export function parseRange(spec) {
  const out = new Set();
  for (const part of spec.split(',').map((s) => s.trim()).filter(Boolean)) {
    const m = part.match(/^(\d+)(?:-(\d+))?$/);
    if (!m) throw new Error(`bad level range "${part}"`);
    const a = +m[1], b = m[2] ? +m[2] : a;
    for (let i = Math.min(a, b); i <= Math.max(a, b); i++) out.add(i);
  }
  return [...out].sort((x, y) => x - y);
}

export const fileName = (n) => `level_${String(n).padStart(3, '0')}.json`;

function main() {
  const args = process.argv.slice(2);
  const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
  const levels = parseRange(opt('--levels', DEFAULT_PACK));
  const attempts = +opt('--attempts', 40);
  const dry = args.includes('--dry');

  const manifest = { generatedBy: 'tools/generate_levels.mjs', algorithm: 'reverse-build + deterministic solver', levels: [] };
  const files = new Map();
  const t0 = Date.now();
  console.log('lvl  grid   tank  pipes  depth  init  decep  taps  diff  target  mechanics');
  for (const n of levels) {
    const refPath = join(REFERENCE_DIR, fileName(n));
    let text, json, res, seed = null, attempt = null;
    if (existsSync(refPath)) {
      text = readFileSync(refPath, 'utf8');
      json = JSON.parse(text);
      res = S.solve(json);
      if (!res.solvable) throw new Error(`reference ${fileName(n)} is not solvable: ${res.errors.join('; ')}`);
    } else {
      const g = G.generateLevel(n, { attempts });
      json = g.json; res = g.result; seed = g.seed; attempt = g.attempt;
      text = C.stringifyLevel(json);
    }
    // round-trip guard: what we write must parse back to the same level
    const back = JSON.parse(text);
    if (JSON.stringify(back) !== JSON.stringify(json)) throw new Error(`level ${n}: writer round-trip mismatch`);
    const m = res.metrics;
    const target = C.targetDifficulty(n);
    manifest.levels.push({
      level: n,
      file: fileName(n),
      source: seed === null ? 'gdd-reference' : 'generated',
      seed, attempt,
      grid: json.grid,
      theme: C.themeFor(n),
      rank: C.rankFor(n),
      breather: C.isBreather(n),
      targetDifficulty: target,
      difficultyScore: m.difficultyScore,
      pipeCount: m.pipeCount,
      waterPipeCount: m.waterPipeCount,
      maxChainDepth: m.maxChainDepth,
      initialExitableCount: m.initialExitableCount,
      deceptiveArrowRatio: m.deceptiveArrowRatio,
      solutionTaps: m.solutionTaps,
      mechanics: m.mechanics,
      solution: res.solution.map((s) => s.id),
    });
    files.set(fileName(n), text);
    const mech = Object.entries(m.mechanics).map(([k, v]) => `${k}:${v}`).join(' ');
    console.log(
      `${String(n).padStart(3)}  ${(json.grid.join('x')).padEnd(5)}  ${json.tank.w}x${json.tank.h}   ${String(m.pipeCount).padStart(3)}    ${String(m.maxChainDepth).padStart(3)}   ${String(m.initialExitableCount).padStart(3)}   ${m.deceptiveArrowRatio.toFixed(2)}  ${String(m.solutionTaps).padStart(4)}  ${m.difficultyScore.toFixed(1).padStart(4)}  ${target.toFixed(1).padStart(5)}${C.isBreather(n) ? 'B' : ' '}  ${mech}`,
    );
  }

  // coverage: all 8 GDD pipe mechanics must appear somewhere in the pack
  const seen = new Set(manifest.levels.flatMap((l) => Object.keys(l.mechanics)));
  const missing = C.PIPE_TYPES.filter((t) => !seen.has(t));
  if (missing.length && levels.length >= 20) throw new Error(`pack is missing mechanics: ${missing.join(', ')}`);
  console.log(`\n${levels.length} levels in ${((Date.now() - t0) / 1000).toFixed(1)}s — mechanics covered: ${[...seen].sort().join(', ')}`);
  if (dry) return;

  const manifestText = JSON.stringify(manifest, null, 2) + '\n';
  for (const dir of OUT_DIRS) {
    mkdirSync(dir, { recursive: true });
    for (const f of readdirSync(dir)) if (/^level_\d+\.json$/.test(f) && !files.has(f)) unlinkSync(join(dir, f)); // stale files
    for (const [f, text] of files) writeFileSync(join(dir, f), text);
    writeFileSync(join(dir, 'manifest.json'), manifestText);
  }
  // file:// fallback for the web playable (browsers block fetch() of local JSON)
  const bundle = { manifest, levels: Object.fromEntries([...files].map(([f, t]) => [f, JSON.parse(t)])) };
  writeFileSync(join(OUT_DIRS[0], 'levels.bundle.js'),
    '// AUTO-GENERATED by tools/generate_levels.mjs — same data as the JSON files, for file:// play.\n' +
    'window.SU_LEVEL_PACK = ' + JSON.stringify(bundle) + ';\n');
  console.log(`wrote ${files.size} levels + manifest to:\n  ${OUT_DIRS.join('\n  ')}`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) main();
