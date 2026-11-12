#!/usr/bin/env node
/**
 * Su Ustası — UI screenshots of the web playable via headless Chrome (zero dependencies).
 *
 *   node tools/ui_shots.mjs                     # -> ui-shots/*.png
 *   node tools/ui_shots.mjs --prefix baseline_  # -> ui-shots/baseline_*.png
 *   CHROME_PATH="C:\path\to\chrome.exe" node tools/ui_shots.mjs
 *
 * Talks the Chrome DevTools Protocol over Node's built-in WebSocket. The game is opened from
 * file:// (levels come from levels/levels.bundle.js), so no server is needed. Scenes are driven
 * through the existing window.SU_GAME.debug hooks — the game code itself is not modified.
 */
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'ui-shots');
const args = process.argv.slice(2);
const prefix = args.includes('--prefix') ? args[args.indexOf('--prefix') + 1] : '';
const VIEWPORT = { width: 430, height: 932, deviceScaleFactor: 2, mobile: true };

function findChrome() {
  const env = process.env;
  const candidates = [
    env.CHROME_PATH,
    env.ProgramFiles && join(env.ProgramFiles, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    env['ProgramFiles(x86)'] && join(env['ProgramFiles(x86)'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
    env.LOCALAPPDATA && join(env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    env.ProgramFiles && join(env.ProgramFiles, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    env['ProgramFiles(x86)'] && join(env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].filter(Boolean);
  const hit = candidates.find((p) => existsSync(p));
  if (!hit) throw new Error('Chrome not found. Set CHROME_PATH to chrome.exe. Tried:\n  ' + candidates.join('\n  '));
  return hit;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function launch() {
  const exe = findChrome();
  const profile = mkdtempSync(join(tmpdir(), 'su-ustasi-shots-'));
  const proc = spawn(exe, [
    '--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run',
    '--no-default-browser-check', '--hide-scrollbars', '--mute-audio', '--disable-gpu', 'about:blank',
  ], { stdio: 'ignore' });
  const portFile = join(profile, 'DevToolsActivePort');
  for (let i = 0; i < 100 && !existsSync(portFile); i++) await sleep(100);
  if (!existsSync(portFile)) throw new Error('Chrome did not start (no DevToolsActivePort)');
  const [port] = readFileSync(portFile, 'utf8').split('\n');
  const { webSocketDebuggerUrl } = await (await fetch(`http://127.0.0.1:${port.trim()}/json/version`)).json();
  const ws = new WebSocket(webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let seq = 0;
  const pending = new Map();
  ws.onmessage = (ev) => {
    const msg = JSON.parse(typeof ev.data === 'string' ? ev.data : ev.data.toString());
    const p = msg.id && pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result);
  };
  const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params, sessionId }));
  });
  const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
  const page = (method, params) => send(method, params, sessionId);
  await page('Page.enable');
  await page('Emulation.setDeviceMetricsOverride', VIEWPORT);
  console.log(`headless ${exe.split(/[\\/]/).pop()} · viewport ${VIEWPORT.width}x${VIEWPORT.height}@${VIEWPORT.deviceScaleFactor}x`);
  return {
    page,
    async close() {
      try { await send('Browser.close'); } catch { /* already gone */ }
      ws.close();
      proc.kill();
      await sleep(300);
      try { rmSync(profile, { recursive: true, force: true }); } catch { /* Chrome may still hold files */ }
    },
  };
}

async function main() {
  mkdirSync(OUT, { recursive: true });
  const { page, close } = await launch();
  const base = pathToFileURL(join(ROOT, 'web-playable', 'index.html')).href;
  const evaluate = async (expression) => {
    const r = await page('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
    return r.result.value;
  };
  const open = async (query) => {
    await page('Page.navigate', { url: `${base}?${query}` });
    for (let i = 0; i < 100; i++) {
      if (await evaluate('!!(window.SU_GAME && SU_GAME.session && document.readyState === "complete")').catch(() => false)) return;
      await sleep(100);
    }
    throw new Error('game did not boot for ?' + query);
  };
  const shot = async (name, waitMs = 0) => {
    if (waitMs) await sleep(waitMs);
    const { data } = await page('Page.captureScreenshot', { format: 'png' });
    const file = join(OUT, `${prefix}${name}.png`);
    writeFileSync(file, Buffer.from(data, 'base64'));
    console.log('  ' + file.slice(ROOT.length + 1));
  };
  const index = (level) => `SU_GAME.order.findIndex((e) => e.level === ${level})`;
  const solve = (level) => `(async () => { const w = (ms) => new Promise((r) => setTimeout(r, ms));
      for (const id of SU_GAME.order[${index(level)}].solution) { while (SU_GAME.pendingValves.length) await w(60); SU_GAME.debug.tap(id); await w(140); } })()`;

  try {
    await open('level=1');
    await evaluate('localStorage.clear()');
    await open('level=1');
    await shot('01_ftue_level1', 900);

    await open('level=8');
    await shot('02_intro_card_level8', 700);
    await evaluate('document.querySelector("#mi-ok") && document.querySelector("#mi-ok").click()');
    await shot('03_gameplay_level8', 700);
    await evaluate('SU_GAME.debug.tap("p6")');
    await shot('04_blocked_tap', 110);

    await open('level=8');
    await evaluate(solve(8));
    await shot('05_win_screen', 2800);

    await open('level=62');
    await shot('06_mechanics_level62', 800);
    await evaluate('["p5","p6","p10"].forEach((id) => SU_GAME.debug.tap(id))');
    await shot('07_lose_screen', 1700);

    await open('level=30');
    await evaluate('document.querySelector("#mi-ok") && document.querySelector("#mi-ok").click(); document.querySelector("#btn-settings").click()');
    await shot('08_settings', 500);
    await evaluate('document.querySelector("#s-levels").click()');
    await shot('09_level_select', 500);
    await evaluate('document.querySelector("#ls-close").click(); document.querySelector("#coins").click()');
    await shot('10_shop', 500);
  } finally {
    await close();
  }
}

main().catch((e) => { console.error(e.message); process.exit(1); });
