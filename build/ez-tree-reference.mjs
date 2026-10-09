// Reference fixture for the Ez Tree port (Tests/MainframeEngine.Tests/Trees/TreeParityTests.cs).
//
// Runs Ez Tree's own generator (src/lib/tree.js at commit dcf309bd86bd521083d9c70f01f2de45fdc7c457, three.js 0.167.1
// from its package-lock.json) for every tree and bush preset and each of its default levels of detail, and writes
// Tests/Content/Trees/ez-tree-reference.json: per surface the vertex and index counts, FNV-1a hashes of the indices, of
// the float32 positions and normals rounded to 1e-4 and of the UVs (in the engine's top-left convention), and every
// 97th position and normal; per preset the skeleton's branch tips (to name the first diverging branch); and RNG vectors.
//
// A dev-time tool, run by hand (never in CI) when the pin moves or the fixture's shape changes:
//
//   git clone https://github.com/dgreenheck/ez-tree <dir>
//   git -C <dir> checkout dcf309bd86bd521083d9c70f01f2de45fdc7c457
//   npm --prefix <dir> ci
//   node build/ez-tree-reference.mjs <dir>
//
// Needs Node 20.6+ (module.register). Ez Tree's sources import without file extensions and import JSON without import
// attributes (they are written for a bundler), so the script registers a small resolve/load hook for them.
//
// Ez Tree is MIT licensed, Copyright (c) 2024 Daniel Greenheck (see THIRD_PARTY_NOTICES.md).

import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { register } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const checkout = process.argv[2];
if (!checkout || !existsSync(join(checkout, 'src/lib/tree.js'))) {
  console.error('usage: node build/ez-tree-reference.mjs <ez-tree checkout at the pinned commit, after npm ci>');
  process.exit(1);
}

const pin = 'dcf309bd86bd521083d9c70f01f2de45fdc7c457';
const stride = 97;

// Bundler-style specifiers: './rng' → './rng.js', JSON as a module.
const hooks = `
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
export async function resolve(specifier, context, next) {
  if (specifier.startsWith('.') && context.parentURL) {
    for (const suffix of ['', '.js', '/index.js']) {
      const url = new URL(specifier + suffix, context.parentURL);
      if (suffix === '' && !/\\.[a-z]+$/.test(specifier)) continue;
      if (existsSync(fileURLToPath(url))) return { url: url.href, shortCircuit: true };
    }
  }
  return next(specifier, context);
}
export async function load(url, context, next) {
  if (url.endsWith('.json')) {
    return { format: 'module', source: 'export default ' + readFileSync(fileURLToPath(url), 'utf8'), shortCircuit: true };
  }
  return next(url, context);
}`;
register('data:text/javascript,' + encodeURIComponent(hooks), import.meta.url);

const lib = (file) => import(pathToFileURL(resolve(checkout, 'src/lib', file)).href);
const { Tree } = await lib('tree.js');
const { TreePreset } = await lib('presets/index.js');
const RNG = (await lib('rng.js')).default;

// ---------------------------------------------------------------------------------------------------------------------

const FNV_OFFSET = 2166136261;

function fnv(hash, u32) {
  for (let i = 0; i < 4; i++) {
    hash ^= (u32 >>> (8 * i)) & 255;
    hash = Math.imul(hash, 16777619) >>> 0;
  }
  return hash;
}

const quantize = (v) => Math.round(v * 1e4) | 0;
const round6 = (v) => Math.round(v * 1e6) / 1e6;

function hashValues(values, map = quantize) {
  let h = FNV_OFFSET;
  for (let i = 0; i < values.length; i++) h = fnv(h, map(values[i], i) >>> 0);
  return h;
}

function surface(geometry, flipV) {
  const position = geometry.attributes.position.array;
  const normal = geometry.attributes.normal.array;
  const uv = geometry.attributes.uv.array;
  const index = geometry.index.array;
  const vertices = position.length / 3;
  if (vertices > 65535) throw new Error(`${vertices} vertices overflow Ez Tree's Uint16 indices`);

  const samples = [];
  for (let v = 0; v < vertices; v += stride) {
    samples.push([
      round6(position[3 * v]), round6(position[3 * v + 1]), round6(position[3 * v + 2]),
      round6(normal[3 * v]), round6(normal[3 * v + 1]), round6(normal[3 * v + 2]),
    ]);
  }

  return {
    vertices,
    indices: index.length,
    indexHash: hashValues(index, (v) => v),
    positionHash: hashValues(position),
    normalHash: hashValues(normal),
    // The engine's UV origin is top-left: leaf cards store (u, 1 - v); bark V is 0/1 either way.
    uvHash: hashValues(uv, (v, i) => quantize(flipV && i % 2 === 1 ? Math.fround(1 - v) : v)),
    samples,
  };
}

const presets = [];
for (const [name, json] of Object.entries(TreePreset)) {
  if (name === 'Trellis') continue; // not ported (ADR 0149, open question 3)

  const tree = new Tree();
  tree.loadPreset(name); // options.copy(preset) + generate(): grows the skeleton once

  const skeleton = tree.skeleton.branches.map((b) => {
    const tip = b.sections[b.sections.length - 1].origin;
    return [b.sections.length, quantize(tip.x), quantize(tip.y), quantize(tip.z)];
  });

  const levels = [...Tree.defaultLODLevels].sort((a, b) => (a.distance ?? 0) - (b.distance ?? 0));
  const lods = levels.map((level) => {
    const g = tree.createGeometry(level.detail ?? {});
    return { bark: surface(g.branches, false), leaves: surface(g.leaves, true) };
  });

  presets.push({
    name,
    file: name.toLowerCase().replace(' ', '_'),
    seed: json.seed,
    branches: tree.skeleton.branches.length,
    leaves: tree.skeleton.leaves.length,
    skeleton,
    lods,
  });
  console.log(`${name}: ${tree.skeleton.branches.length} branches, ${tree.skeleton.leaves.length} leaves, ` +
    lods.map((l) => `${(l.bark.indices + l.leaves.indices) / 3}`).join(' / ') + ' triangles');
}

// RNG vectors: the first draws for a few seeds, and a hash of one million draws (as uint32: result × 2^32 is exact).
const rng = {};
for (const seed of [0, 1, 35729, 44166, 65535, -12345]) {
  const r = new RNG(seed);
  rng[seed] = Array.from({ length: 8 }, () => r.random());
}
const million = new RNG(44166);
let millionHash = FNV_OFFSET;
for (let i = 0; i < 1_000_000; i++) millionHash = fnv(millionHash, Math.round(million.random() * 4294967296) >>> 0);

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'Tests', 'Content', 'Trees', 'ez-tree-reference.json');
mkdirSync(dirname(out), { recursive: true });
const header = {
  ezTree: pin,
  three: JSON.parse(readFileSync(join(checkout, 'node_modules/three/package.json'), 'utf8')).version,
  stride,
  rng,
  rngMillion: { seed: 44166, hash: millionHash },
};
// One preset per line keeps diffs readable when the pin moves.
const head = JSON.stringify(header, null, 2);
writeFileSync(out, head.slice(0, -2) + ',\n  "presets": [\n    ' + presets.map((p) => JSON.stringify(p)).join(',\n    ') + '\n  ]\n}\n');
console.log(`wrote ${out}`);
