#!/usr/bin/env node
/**
 * Su Ustası — level pack validator.  node tools/validate_levels.mjs
 *
 * Checks, for every level in both deliverables:
 *   - schema + structural rules (shapes, overlaps, tank paths, key/lock links)
 *   - solver: 100% solvable from the initial state, zero lives lost under perfect play
 *   - the stored solution replays to a win on a fresh GameSession
 *   - manifest metrics match a fresh solve (no stale data)
 *   - web + Unity copies are byte-identical, level_008.json == GDD reference
 *   - levels.bundle.js (file:// fallback) carries exactly the same data
 *   - all 8 GDD pipe mechanics (+ key_valve) appear in the pack
 * Exit code 1 on any failure.
 */
import { createRequire } from 'node:module';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const require = createRequire(import.meta.url);
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const C = require(join(ROOT, 'web-playable/js/core.js'));
const S = require(join(ROOT, 'web-playable/js/solver.js'));
const WEB = join(ROOT, 'web-playable/levels');
const UNITY = join(ROOT, 'unity-project/Assets/_Game/Resources/Levels');
const REF = join(ROOT, 'tools/reference_levels');

export function validatePack() {
  const failures = [];
  const fail = (m) => failures.push(m);
  const list = (d) => (existsSync(d) ? readdirSync(d).filter((f) => /^level_\d+\.json$/.test(f)).sort() : []);
  const webFiles = list(WEB), unityFiles = list(UNITY);
  if (!webFiles.length) fail('no levels in web-playable/levels');
  if (JSON.stringify(webFiles) !== JSON.stringify(unityFiles)) fail('web and Unity level sets differ');

  const manifest = JSON.parse(readFileSync(join(WEB, 'manifest.json'), 'utf8'));
  const unityManifest = readFileSync(join(UNITY, 'manifest.json'), 'utf8');
  if (unityManifest !== readFileSync(join(WEB, 'manifest.json'), 'utf8')) fail('manifest.json differs between web and Unity');
  const byFile = new Map(manifest.levels.map((l) => [l.file, l]));

  const seenTypes = new Set();
  const rows = [];
  for (const f of webFiles) {
    const text = readFileSync(join(WEB, f), 'utf8');
    if (existsSync(join(UNITY, f)) && readFileSync(join(UNITY, f), 'utf8') !== text) fail(`${f}: web and Unity copies differ`);
    if (existsSync(join(REF, f)) && readFileSync(join(REF, f), 'utf8') !== text) fail(`${f}: differs from GDD reference`);
    let raw;
    try { raw = JSON.parse(text); } catch (e) { fail(`${f}: invalid JSON (${e.message})`); continue; }
    const res = S.solve(raw);
    if (!res.valid) { fail(`${f}: ${res.errors.join('; ')}`); continue; }
    if (!res.solvable) fail(`${f}: NOT solvable`);
    if (!res.perfect) fail(`${f}: perfect play still loses ${res.leaksLost} lives`);
    const rep = S.replay(raw, res.solution);
    if (rep.status !== 'won') fail(`${f}: solution replay ended as ${rep.status}`);
    const m = byFile.get(f);
    if (!m) fail(`${f}: missing from manifest`);
    else {
      for (const k of ['difficultyScore', 'maxChainDepth', 'initialExitableCount', 'deceptiveArrowRatio', 'pipeCount', 'solutionTaps']) {
        if (m[k] !== res.metrics[k]) fail(`${f}: manifest ${k}=${m[k]} but solver says ${res.metrics[k]}`);
      }
      if (m.level !== raw.level) fail(`${f}: manifest level ${m.level} != file level ${raw.level}`);
      const stored = S.replay(raw, m.solution.map((id) => ({ id })));
      if (stored.status !== 'won') fail(`${f}: stored manifest solution does not win (${stored.status})`);
    }
    Object.keys(res.metrics.mechanics).forEach((t) => seenTypes.add(t));
    rows.push({ f, m: res.metrics });
  }
  for (const t of C.PIPE_TYPES) if (!seenTypes.has(t)) fail(`pack never uses pipe type "${t}"`);
  if (webFiles.length < 20) fail(`only ${webFiles.length} levels (GDD asks for >= 20)`);
  if (!webFiles.includes('level_008.json')) fail('GDD reference level_008.json missing');

  // file:// bundle
  const bundlePath = join(WEB, 'levels.bundle.js');
  if (!existsSync(bundlePath)) fail('levels.bundle.js missing');
  else {
    const sandbox = { window: {} };
    vm.runInNewContext(readFileSync(bundlePath, 'utf8'), sandbox);
    const pack = sandbox.window.SU_LEVEL_PACK;
    for (const f of webFiles) {
      const a = JSON.stringify(pack.levels[f]), b = JSON.stringify(JSON.parse(readFileSync(join(WEB, f), 'utf8')));
      if (a !== b) fail(`levels.bundle.js: ${f} differs from JSON file`);
    }
    if (JSON.stringify(pack.manifest) !== JSON.stringify(manifest)) fail('levels.bundle.js: manifest differs');
  }
  return { failures, rows, types: [...seenTypes].sort() };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const { failures, rows, types } = validatePack();
  console.log(`checked ${rows.length} levels — mechanics: ${types.join(', ')}`);
  if (failures.length) {
    console.error(`\n${failures.length} problem(s):\n  - ` + failures.join('\n  - '));
    process.exit(1);
  }
  console.log('all levels valid, 100% solvable with zero lives lost, copies identical ✓');
}
