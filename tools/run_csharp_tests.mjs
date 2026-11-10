#!/usr/bin/env node
/**
 * Headless C# build + test for the Unity project's engine-free assemblies
 * (SuUstasi.Core, .LevelGen, .Services, .Gameplay) and their EditMode NUnit tests —
 * no Unity install and no .NET SDK needed, only a .NET (6+) runtime.
 *
 *   node tools/run_csharp_tests.mjs              # compile + run NUnit tests + C#↔JS cross-check
 *   node tools/run_csharp_tests.mjs --no-tests   # only the cross-check CLI
 *
 * First run downloads two NuGet packages into tools/.cache/ (gitignored):
 *   Microsoft.Net.Compilers.Toolset 4.12.0 (Roslyn csc), NUnit 3.14.0 + NUnitLite 3.14.0
 * (NUnit 3 = same API as Unity Test Framework). Override the compiler with SU_CSC_DLL.
 */
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, readdirSync, statSync, copyFileSync, openSync, readSync, closeSync } from 'node:fs';
import { join, dirname, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateRawSync } from 'node:zlib';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const CACHE = join(ROOT, 'tools', '.cache');
const BUILD = join(CACHE, 'build');
const SCRIPTS = join(ROOT, 'unity-project', 'Assets', '_Game', 'Scripts');
const TESTS = join(ROOT, 'unity-project', 'Assets', '_Game', 'Tests', 'EditMode');
const PACKAGES = [
  ['microsoft.net.compilers.toolset', '4.12.0'],
  ['nunit', '3.14.0'],
  ['nunitlite', '3.14.0'],
];
const args = process.argv.slice(2);
const log = (...m) => console.log(...m);

// ------------------------------------------------------------------ helpers
function walk(dir, ext, out = []) {
  if (!existsSync(dir)) return out;
  for (const f of readdirSync(dir)) {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) walk(p, ext, out);
    else if (p.endsWith(ext)) out.push(p);
  }
  return out;
}

/** Minimal ZIP reader (nupkg = zip): central directory + stored/deflate entries. */
function unzip(file, destDir) {
  const buf = readFileSync(file);
  let eocd = buf.length - 22;
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('not a zip: ' + file);
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  for (let i = 0; i < count; i++) {
    const method = buf.readUInt16LE(p + 10);
    const csize = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28), extraLen = buf.readUInt16LE(p + 30), commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = decodeURIComponent(escape(buf.toString('latin1', p + 46, p + 46 + nameLen)));
    p += 46 + nameLen + extraLen + commentLen;
    if (name.endsWith('/')) continue;
    const lnameLen = buf.readUInt16LE(local + 26), lextraLen = buf.readUInt16LE(local + 28);
    const data = buf.subarray(local + 30 + lnameLen + lextraLen, local + 30 + lnameLen + lextraLen + csize);
    const out = join(destDir, ...name.split('/'));
    if (!out.startsWith(destDir)) continue; // zip-slip guard
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, method === 8 ? inflateRawSync(data) : data);
  }
}

async function ensurePackage(id, version) {
  const dir = join(CACHE, id);
  if (existsSync(join(dir, '.ok'))) return dir;
  mkdirSync(dir, { recursive: true });
  const url = `https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.${version}.nupkg`;
  log(`  downloading ${id} ${version} from nuget.org …`);
  const res = await fetch(url);
  if (!res.ok) throw new Error(`download failed ${res.status}: ${url}`);
  const pkg = join(CACHE, `${id}.${version}.nupkg`);
  writeFileSync(pkg, Buffer.from(await res.arrayBuffer()));
  unzip(pkg, dir);
  writeFileSync(join(dir, '.ok'), version);
  return dir;
}

/** True when a PE file carries a CLR header (managed assembly) — skips native runtime DLLs. */
function isManaged(path) {
  const fd = openSync(path, 'r');
  try {
    const head = Buffer.alloc(4096);
    readSync(fd, head, 0, 4096, 0);
    if (head.readUInt16LE(0) !== 0x5a4d) return false;
    const pe = head.readUInt32LE(0x3c);
    if (pe + 24 + 240 > head.length || head.readUInt32LE(pe) !== 0x4550) return false;
    const opt = pe + 24;
    const magic = head.readUInt16LE(opt);
    const dirs = opt + (magic === 0x20b ? 112 : 96);
    return head.readUInt32LE(dirs + 14 * 8 + 4) !== 0;
  } finally { closeSync(fd); }
}

function runtimeDir() {
  const out = execFileSync('dotnet', ['--list-runtimes'], { encoding: 'utf8' });
  const rts = [...out.matchAll(/Microsoft\.NETCore\.App (\d+)\.(\d+)\.(\d+)\S* \[(.+)\]/g)]
    .map((m) => ({ v: [+m[1], +m[2], +m[3]], dir: join(m[4], `${m[1]}.${m[2]}.${m[3]}`) }))
    .filter((r) => r.v[0] >= 6)
    .sort((a, b) => b.v[0] - a.v[0] || b.v[1] - a.v[1] || b.v[2] - a.v[2]);
  if (!rts.length) throw new Error('No .NET 6+ runtime found (dotnet --list-runtimes)');
  return rts[0];
}

function compile(csc, rt, refs, sources, out, extraFlags = []) {
  const rsp = [
    '-nologo', '-langversion:9.0', '-nullable:disable', '-target:exe', '-optimize+', '-deterministic',
    '-nowarn:CS1591,CS0649,CS0414,CS8632', `-out:"${out}"`,
    ...refs.map((r) => `-r:"${r}"`), ...extraFlags, ...sources.map((s) => `"${s}"`),
  ].join('\n');
  const rspFile = out + '.rsp';
  writeFileSync(rspFile, rsp);
  const r = spawnSync('dotnet', [csc, '@' + rspFile], { encoding: 'utf8' });
  const msg = (r.stdout || '') + (r.stderr || '');
  if (r.status !== 0) {
    console.error(msg.split('\n').map((l) => l.replace(ROOT + sep, '')).join('\n'));
    throw new Error('C# compilation failed: ' + relative(ROOT, out));
  }
  if (msg.trim()) log(msg.split('\n').filter((l) => /warning/.test(l)).slice(0, 20).map((l) => '  ' + l.replace(ROOT + sep, '')).join('\n'));
  writeFileSync(out.replace(/\.dll$/, '.runtimeconfig.json'), JSON.stringify({
    runtimeOptions: { tfm: `net${rt.v[0]}.0`, framework: { name: 'Microsoft.NETCore.App', version: `${rt.v[0]}.0.0` }, rollForward: 'Major' },
  }, null, 2));
}

// ------------------------------------------------------------------ main
async function main() {
  mkdirSync(BUILD, { recursive: true });
  const rt = runtimeDir();
  log(`.NET runtime ${rt.v.join('.')}  (${rt.dir})`);
  const dirs = {};
  for (const [id, v] of PACKAGES) dirs[id] = await ensurePackage(id, v);
  const csc = process.env.SU_CSC_DLL || join(dirs['microsoft.net.compilers.toolset'], 'tasks', 'netcore', 'bincore', 'csc.dll');
  if (!existsSync(csc)) throw new Error('csc.dll not found: ' + csc);

  const frameworkRefs = readdirSync(rt.dir).filter((f) => f.endsWith('.dll')).map((f) => join(rt.dir, f)).filter(isManaged);
  const nunit = join(dirs.nunit, 'lib', 'netstandard2.0', 'nunit.framework.dll');
  const nunitlite = join(dirs.nunitlite, 'lib', 'netstandard2.0', 'nunitlite.dll');

  // engine-free assemblies only (Runtime/ and Editor/ need UnityEngine)
  const pure = ['Core', 'LevelGen', 'Services', 'Gameplay'].flatMap((d) => walk(join(SCRIPTS, d), '.cs'));
  log(`compiling ${pure.length} engine-free source files …`);

  // 1) cross-check CLI
  const cli = join(BUILD, 'SuUstasi.HeadlessCli.dll');
  compile(csc, rt, frameworkRefs, [...pure, join(ROOT, 'tools', 'csharp', 'HeadlessCli.cs')], cli);
  const levels = join(ROOT, 'unity-project', 'Assets', '_Game', 'Resources', 'Levels');
  log('\n— C# validation of the shipped pack —');
  const v = spawnSync('dotnet', [cli, 'validate', levels], { stdio: 'inherit' });
  log('\n— C# ↔ JS cross-check (solver metrics + generator output) —');
  const x = spawnSync('dotnet', [cli, 'crosscheck', levels], { stdio: 'inherit' });

  // 2) NUnit tests
  let t = { status: 0 };
  if (!args.includes('--no-tests')) {
    const tests = walk(TESTS, '.cs');
    const out = join(BUILD, 'SuUstasi.HeadlessTests.dll');
    log(`\ncompiling ${tests.length} NUnit test files …`);
    compile(csc, rt, [...frameworkRefs, nunit, nunitlite], [...pure, ...tests, join(ROOT, 'tools', 'csharp', 'TestMain.cs')], out);
    copyFileSync(nunit, join(BUILD, 'nunit.framework.dll'));
    copyFileSync(nunitlite, join(BUILD, 'nunitlite.dll'));
    log('— NUnit (NUnitLite) —');
    t = spawnSync('dotnet', [out, '--noresult', '--labels=Off', `--work=${BUILD}`], { stdio: 'inherit', env: { ...process.env, SU_LEVELS_DIR: levels } });
  }
  const failed = [v.status, x.status, t.status].some((s) => s !== 0);
  log(failed ? '\nC# checks FAILED' : '\nC# checks passed ✓');
  process.exit(failed ? 1 : 0);
}

main().catch((e) => { console.error(e.message); process.exit(1); });
