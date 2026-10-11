// `just docs` / `npm run dev`: syncs the docs, starts `astro dev` and re-syncs a page whenever its source changes, so
// edits to docs/ show up in the browser without restarting.
import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { base, repoRoot, siteRoot, syncAll, syncAssets, syncFile, watchRoots } from './sync-docs.mjs';

// First run, or package-lock.json changed since the last install (a pull): install the locked packages.
const installed = path.join(siteRoot, 'node_modules', '.package-lock.json');
const lock = path.join(siteRoot, 'package-lock.json');
if (!fs.existsSync(installed) || fs.statSync(lock).mtimeMs > fs.statSync(installed).mtimeMs) {
  console.log('docs-site: npm ci');
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  const r = spawnSync(npm, ['ci', '--no-audit', '--no-fund'], { cwd: siteRoot, stdio: 'inherit', shell: process.platform === 'win32' });
  if (r.status !== 0) process.exit(r.status ?? 1);
}

const { count, warnings } = syncAll();
console.log(`docs-site: synced ${count} pages` + (warnings.length ? ` (${warnings.length} link warnings)` : ''));

const astro = spawn(process.execPath, [path.join(siteRoot, 'node_modules', 'astro', 'bin', 'astro.mjs'), 'dev', ...process.argv.slice(2)], {
  cwd: siteRoot,
  stdio: 'inherit',
});
astro.on('exit', (code) => process.exit(code ?? 0));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => astro.kill(signal));

let assetsTimer = null;
for (const root of watchRoots) {
  fs.watch(path.join(repoRoot, root), { recursive: true }, (_event, file) => {
    if (!file) return;
    const rel = root + '/' + file.split(path.sep).join('/');
    if (rel.endsWith('.md')) {
      const w = [];
      if (syncFile(rel, w)) console.log(`docs-site: ${rel}`);
      for (const x of w) console.warn('warning: ' + x);
    } else if (rel.startsWith('docs/images/')) {
      clearTimeout(assetsTimer);
      assetsTimer = setTimeout(syncAssets, 200);
    }
  });
}
console.log(`docs-site: watching ${watchRoots.join(', ')} — open http://localhost:4321${base}`);
