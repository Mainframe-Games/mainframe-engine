// Builds the Starlight content from the repository's Markdown (docs/design/docs-site.md):
//   docs/README.md               → /              (landing page)
//   docs/milestones.md           → /milestones/
//   docs/design/*.md             → /design/<name>/
//   docs/design/future/*.md      → /proposals/<name>/
//   docs/images/brand/README.md  → /brand/
// Plans (docs/superpowers/) and ADRs (memory/decisions/) are not published; links to them go to GitHub.
// Each page gets frontmatter (title from its first "# " heading, sidebar label, edit link). Links are rewritten: to
// another synced page → its route; into docs/images → the copied image; to any other repository file → GitHub.
// Generated output (src/content/docs, src/assets, public/images, public/favicon.svg, public/logo-512.png) is ignored by
// git. `node scripts/sync-docs.mjs` runs once; dev.mjs imports `syncAll`/`syncFile` and re-runs them on changes.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const repoRoot = path.resolve(siteRoot, '..');
export const repoUrl = 'https://github.com/Mainframe-Games/mainframe-engine';
export const branch = 'main';
/** The site's base path (GitHub Pages project site); DOCS_BASE overrides it, e.g. "/" for a custom domain. */
export const base = normaliseBase(process.env.DOCS_BASE ?? '/mainframe-engine/');

/**
 * The engine version the docs describe: the latest release tag (vX.Y.Z, docs/design/release.md) reachable from the
 * checked-out commit, or 'dev' without one. DOCS_VERSION overrides it. Needs the tags and the history back to them.
 */
export const version = process.env.DOCS_VERSION ?? releaseVersion();

function releaseVersion() {
  try {
    return execFileSync('git', ['describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*.[0-9]*.[0-9]*'], {
      cwd: repoRoot,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    }).trim();
  } catch {
    return 'dev';
  }
}

const contentDir = path.join(siteRoot, 'src', 'content', 'docs');
const publicDir = path.join(siteRoot, 'public');

/** Source folders watched by dev.mjs (relative to the repository). */
export const watchRoots = ['docs'];

function normaliseBase(b) {
  if (!b.startsWith('/')) b = '/' + b;
  return b.endsWith('/') ? b : b + '/';
}

const toPosix = (p) => p.split(path.sep).join('/');

/** The page route (no leading or trailing slash; '' for the landing page) of a repository-relative Markdown file, or null. */
export function routeOf(rel) {
  rel = toPosix(rel);
  if (rel === 'docs/README.md') return '';
  if (rel === 'docs/milestones.md') return 'milestones';
  if (rel === 'docs/images/brand/README.md') return 'brand';
  let m;
  if ((m = /^docs\/design\/future\/([^/]+)\.md$/.exec(rel))) return 'proposals/' + slug(m[1]);
  if ((m = /^docs\/design\/([^/]+)\.md$/.exec(rel))) return 'design/' + slug(m[1]);
  return null;
}

const slug = (name) => name.toLowerCase().replace(/[^a-z0-9._-]+/g, '-');

/** Every synced source file, repository-relative. */
export function listSources() {
  const out = [];
  const walk = (dir) => {
    const abs = path.join(repoRoot, dir);
    if (!fs.existsSync(abs)) return;
    for (const entry of fs.readdirSync(abs, { withFileTypes: true })) {
      const rel = dir + '/' + entry.name;
      if (entry.isDirectory()) walk(rel);
      else if (entry.name.endsWith('.md') && routeOf(rel) !== null) out.push(rel);
    }
  };
  for (const root of watchRoots) walk(root);
  return out.sort();
}

// ── Markdown transforms ─────────────────────────────────────────────────────

const yaml = (s) => JSON.stringify(s); // JSON strings are valid YAML double-quoted scalars

/** Plain text of a heading: inline code, emphasis and links reduced to their text. */
function plain(s) {
  return s
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_]{1,2}([^*_]+)[*_]{1,2}/g, '$1')
    .trim();
}

function sidebarLabel(route, title) {
  if (route.startsWith('proposals/')) return title.replace(/^Proposal:\s*/i, '');
  return title;
}

/** Rewrites one link target found in `fromRel`; returns the new target. */
function rewriteTarget(target, fromRel, warnings) {
  if (/^([a-z][a-z0-9+.-]*:|#|\/\/)/i.test(target)) return target; // absolute URL, mailto:, in-page anchor
  const hashAt = target.indexOf('#');
  const rawPath = hashAt >= 0 ? target.slice(0, hashAt) : target;
  const hash = hashAt >= 0 ? target.slice(hashAt) : '';
  if (rawPath === '') return target;
  let decoded;
  try { decoded = decodeURIComponent(rawPath); } catch { decoded = rawPath; }
  const abs = path.resolve(repoRoot, path.dirname(fromRel), decoded);
  const rel = toPosix(path.relative(repoRoot, abs));
  if (rel.startsWith('..')) return target; // outside the repository: leave it

  const route = routeOf(rel);
  if (route !== null) return base + (route ? route + '/' : '') + hash;
  if (rel.startsWith('docs/images/')) return base + 'images/' + rel.slice('docs/images/'.length) + hash;

  const exists = fs.existsSync(abs);
  if (!exists) warnings.push(`${fromRel}: link to missing ${rel}`);
  const kind = exists && fs.statSync(abs).isDirectory() ? 'tree' : 'blob';
  return `${repoUrl}/${kind}/${branch}/${rel.split('/').map(encodeURIComponent).join('/')}${hash}`;
}

/** Rewrites inline links/images, reference definitions and HTML src/href outside code. */
function rewriteLine(line, fromRel, warnings) {
  // Mask inline code spans so links inside them stay as written.
  const spans = [];
  const masked = line.replace(/(`+)[\s\S]*?\1/g, (m) => {
    spans.push(m);
    return `\u0000${spans.length - 1}\u0000`;
  });
  let out = masked
    .replace(/(\]\()(<[^>]*>|[^)\s]+)((?:\s+"[^"]*")?\))/g, (_, open, target, close) => {
      const angle = target.startsWith('<');
      const inner = angle ? target.slice(1, -1) : target;
      const next = rewriteTarget(inner, fromRel, warnings);
      return open + (angle ? `<${next}>` : next) + close;
    })
    .replace(/^(\s{0,3}\[[^\]]+\]:\s*)(\S+)/, (_, head, target) => head + rewriteTarget(target, fromRel, warnings))
    .replace(/\b(src|href)="([^"]+)"/g, (_, attr, target) => `${attr}="${rewriteTarget(target, fromRel, warnings)}"`);
  return out.replace(/\u0000(\d+)\u0000/g, (_, i) => spans[Number(i)]);
}

/** Converts one source file into the page Starlight reads. */
export function transform(rel, text, warnings = []) {
  const route = routeOf(rel);
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  let title = null;
  let fence = null;
  const body = [];
  for (const line of lines) {
    const f = /^\s{0,3}(`{3,}|~{3,})/.exec(line);
    if (f) {
      if (fence === null) fence = f[1][0];
      else if (f[1][0] === fence) fence = null;
      body.push(line);
      continue;
    }
    if (fence !== null) {
      body.push(line);
      continue;
    }
    if (title === null && /^# /.test(line)) {
      title = plain(line.slice(2));
      continue;
    }
    body.push(rewriteLine(line, rel, warnings));
  }
  title ??= path.basename(rel, '.md');

  const fm = ['---', `title: ${yaml(title)}`];
  if (route === '') {
    fm.push(
      'template: splash',
      'hero:',
      '  tagline: "A C# game engine on Vulkan with a Godot-style node tree, an editor built on its own game UI, and the design docs that describe it."',
      '  image:',
      `    file: ../../assets/logo.svg`,
      '  actions:',
      '    - text: Architecture overview',
      `      link: ${yaml(base + 'design/architecture-overview/')}`,
      '      icon: right-arrow',
      '    - text: Milestones',
      `      link: ${yaml(base + 'milestones/')}`,
      '      variant: secondary',
      '    - text: GitHub',
      `      link: ${yaml(repoUrl)}`,
      '      icon: github',
      '      variant: minimal',
    );
  } else {
    const label = sidebarLabel(route, title);
    if (label !== title) fm.push('sidebar:', `  label: ${yaml(label)}`);
  }
  fm.push(`editUrl: ${yaml(`${repoUrl}/edit/${branch}/${rel}`)}`, '---', '');
  return fm.join('\n') + body.join('\n').replace(/^\n+/, '');
}

// ── Output ──────────────────────────────────────────────────────────────────

function outPath(rel) {
  const route = routeOf(rel);
  return path.join(contentDir, (route === '' ? 'index' : route) + '.md');
}

function writeIfChanged(file, content) {
  if (fs.existsSync(file) && fs.readFileSync(file, 'utf8') === content) return false;
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content);
  return true;
}

/** Re-syncs one repository-relative source (or removes its page when the source is gone). */
export function syncFile(rel, warnings = []) {
  if (routeOf(rel) === null) return false;
  const src = path.join(repoRoot, rel);
  const dest = outPath(rel);
  if (!fs.existsSync(src)) {
    if (fs.existsSync(dest)) fs.rmSync(dest);
    return true;
  }
  return writeIfChanged(dest, transform(rel, fs.readFileSync(src, 'utf8'), warnings));
}

function copyFile(from, to) {
  fs.mkdirSync(path.dirname(to), { recursive: true });
  fs.copyFileSync(from, to);
}

/** Copies the images, brand logo and editor fonts the site uses. */
export function syncAssets() {
  const images = path.join(repoRoot, 'docs', 'images');
  fs.rmSync(path.join(publicDir, 'images'), { recursive: true, force: true });
  fs.cpSync(images, path.join(publicDir, 'images'), {
    recursive: true,
    filter: (src) => !src.endsWith('.md'),
  });
  const brand = path.join(images, 'brand');
  copyFile(path.join(brand, 'logo.svg'), path.join(siteRoot, 'src', 'assets', 'logo.svg'));
  copyFile(path.join(brand, 'logo-small.svg'), path.join(publicDir, 'favicon.svg'));
  copyFile(path.join(brand, 'png', 'logo-512.png'), path.join(publicDir, 'logo-512.png'));
  // The editor's UI fonts (MainframeEngine/Content/UI/fonts, SIL OFL): Lato for text, Roboto Mono for code.
  const fonts = path.join(repoRoot, 'MainframeEngine', 'Content', 'UI', 'fonts');
  for (const f of ['LatoLatin-Regular.ttf', 'LatoLatin-Bold.ttf', 'LatoLatin-Italic.ttf', 'RobotoMono-Regular.ttf',
    'OFL-Lato.txt', 'OFL-RobotoMono.txt'])
    copyFile(path.join(fonts, f), path.join(siteRoot, 'src', 'assets', 'fonts', f));
}

/** Full sync: clears the generated pages, writes every page and copies the assets. */
export function syncAll() {
  const warnings = [];
  fs.rmSync(contentDir, { recursive: true, force: true });
  const sources = listSources();
  for (const rel of sources) syncFile(rel, warnings);
  syncAssets();
  return { count: sources.length, warnings };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { count, warnings } = syncAll();
  for (const w of warnings) console.warn('warning: ' + w);
  console.log(`docs-site: synced ${count} pages (base ${base})` + (warnings.length ? `, ${warnings.length} warnings` : ''));
}
