import { randomUUID } from 'node:crypto';
import {
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
} from 'node:fs';
import { dirname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

import { dartReferencePackages } from './build-dart-reference.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const requiredFiles = [
  '.nojekyll',
  'index.html',
  'api/csharp/index.html',
  'api/dart/index.html',
  ...dartReferencePackages.map((entry) => `api/dart/${entry.name}/index.html`),
  'pagefind/pagefind.js',
  'pagefind/pagefind-entry.json',
  'registry/index.json',
  'source/docs/capability-matrix.json',
  'source/tests/live-game-acceptance.json',
];

export function assemblePages({ site, feeds, output }) {
  const siteRoot = resolve(site);
  const feedsRoot = resolve(feeds);
  const outputRoot = resolve(output);
  assertSeparateTrees(siteRoot, feedsRoot, outputRoot);
  if (existsSync(outputRoot)) {
    throw new Error(`Pages output must not already exist: ${outputRoot}`);
  }

  const siteEntries = inventory(siteRoot, 'website');
  const feedEntries = inventory(feedsRoot, 'feeds');
  const paths = new Map(siteEntries.map((entry) => [entry.relativePath, 'website']));
  for (const entry of feedEntries) {
    const owner = paths.get(entry.relativePath);
    if (owner) {
      throw new Error(`Pages path collision at ${entry.relativePath} (${owner} and feeds).`);
    }
    paths.set(entry.relativePath, 'feeds');
  }

  mkdirSync(dirname(outputRoot), { recursive: true });
  const staging = `${outputRoot}.staging-${process.pid}-${randomUUID()}`;
  try {
    mkdirSync(staging);
    copyInventory(siteEntries, staging);
    copyInventory(feedEntries, staging);
    validatePagesPayload(staging);
    renameSync(staging, outputRoot);
  } catch (error) {
    rmSync(staging, { recursive: true, force: true });
    throw error;
  }

  return {
    fileCount: [...siteEntries, ...feedEntries].filter((entry) => entry.type === 'file').length,
    hasManualReleases: existsSync(resolve(outputRoot, 'manual-releases.json')),
  };
}

export function inventory(root, label = 'input') {
  if (!existsSync(root)) {
    throw new Error(`${label} tree does not exist: ${root}`);
  }
  const rootStats = lstatSync(root);
  if (rootStats.isSymbolicLink() || !rootStats.isDirectory()) {
    throw new Error(`${label} tree must be a real directory: ${root}`);
  }

  const entries = [];
  const pending = [{ absolutePath: root, relativePath: '' }];
  while (pending.length > 0) {
    const current = pending.pop();
    for (const name of readdirSync(current.absolutePath).sort()) {
      const absolutePath = resolve(current.absolutePath, name);
      const relativePath = current.relativePath
        ? `${current.relativePath}/${name}`
        : name;
      const stats = lstatSync(absolutePath);
      if (stats.isSymbolicLink()) {
        throw new Error(`${label} tree contains a symbolic link: ${relativePath}`);
      }
      if (stats.isDirectory()) {
        const entry = { type: 'directory', absolutePath, relativePath };
        entries.push(entry);
        pending.push(entry);
      } else if (stats.isFile()) {
        if (stats.nlink !== 1) {
          throw new Error(`${label} tree contains a hard-linked file: ${relativePath}`);
        }
        entries.push({ type: 'file', absolutePath, relativePath });
      } else {
        throw new Error(`${label} tree contains a special file: ${relativePath}`);
      }
    }
  }
  return entries.sort((left, right) => left.relativePath.localeCompare(right.relativePath));
}

export function validatePagesPayload(root) {
  for (const path of requiredFiles) {
    requireRegularFile(root, path);
  }
  for (const retired of ['index.json', 'CNAME']) {
    if (existsSync(resolve(root, retired))) {
      throw new Error(`Retired Pages root file must not be published: ${retired}`);
    }
  }

  requireSearchMarker(root, 'index.html', 'data-pagefind-body');
  requireSearchMarker(root, 'api/csharp/index.html', 'data-pagefind-body');
  requireSearchMarker(root, 'api/dart/index.html', 'data-pagefind-body');
  for (const entry of dartReferencePackages) {
    requireSearchMarker(
      root,
      `api/dart/${entry.name}/${entry.name}/index.html`,
      'data-pagefind-body',
    );
  }

  validateRegistry(readJson(root, 'registry/index.json'));
  for (const evidence of [
    'source/docs/capability-matrix.json',
    'source/tests/live-game-acceptance.json',
  ]) {
    readJson(root, evidence);
  }

  const manualPath = resolve(root, 'manual-releases.json');
  if (existsSync(manualPath)) {
    requireRegularFile(root, 'manual-releases.json');
    validateManualCatalog(readJson(root, 'manual-releases.json'));
  }
}

export function validateRegistry(value) {
  requireObject(value, 'registry/index.json');
  if (value.formatVersion !== 2 || !Array.isArray(value.mods)) {
    throw new Error('registry/index.json must be a formatVersion 2 registry with a mods array.');
  }
  if (
    value.$schema !== 'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.registry-index.schema.json'
    || value.name !== 'TopiaForge Mod Registry'
    || !isIsoTimestamp(value.generatedAt)
  ) {
    throw new Error('registry/index.json has invalid schema, name, or generation metadata.');
  }
  if (value.sourceRepository !== 'https://github.com/furroxide/TopiaForge') {
    throw new Error('registry/index.json must identify the official TopiaForge repository.');
  }
  for (const [index, mod] of value.mods.entries()) {
    requireObject(mod, `registry mod ${index}`);
    requireObject(mod.manifest, `registry mod ${index} manifest`);
    if (mod.origin !== 'first-party') {
      throw new Error(`registry mod ${index} is not a first-party release asset.`);
    }
    if (typeof mod.manifest.id !== 'string' || mod.manifest.id.trim() === '') {
      throw new Error(`registry mod ${index} has no manifest id.`);
    }
    if (
      mod.manifest.schemaVersion !== 5
      || typeof mod.manifest.version !== 'string'
      || mod.manifest.version.trim() === ''
    ) {
      throw new Error(`registry mod ${index} has invalid manifest metadata.`);
    }
    if (!isPublicHttpsUrl(mod.downloadUrl)) {
      throw new Error(`registry mod ${index} has an invalid download URL.`);
    }
    if (!isSha256(mod.packageSha256)) {
      throw new Error(`registry mod ${index} has an invalid package digest.`);
    }
    if (mod.history !== undefined) {
      if (!Array.isArray(mod.history)) {
        throw new Error(`registry mod ${index} has invalid history metadata.`);
      }
      for (const [historyIndex, history] of mod.history.entries()) {
        requireObject(history, `registry mod ${index} history ${historyIndex}`);
        if (
          typeof history.version !== 'string'
          || history.version.trim() === ''
          || !isPublicHttpsUrl(history.downloadUrl)
          || !isSha256(history.packageSha256)
        ) {
          throw new Error(`registry mod ${index} history ${historyIndex} is invalid.`);
        }
      }
    }
  }
}

export function validateManualCatalog(value) {
  requireObject(value, 'manual-releases.json');
  if (value.formatVersion !== 2 || value.manualOnly !== true) {
    throw new Error('manual-releases.json must be a manual-only formatVersion 2 catalog.');
  }
  if (
    value.$schema !== 'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.manual-releases.schema.json'
    || !isIsoTimestamp(value.generatedAt)
  ) {
    throw new Error('manual-releases.json has invalid schema or generation metadata.');
  }
  if (!isPublicHttpsUrl(value.releaseUrl)) {
    throw new Error('manual-releases.json has an invalid release URL.');
  }
  requireObject(value.platforms, 'manual-releases.json platforms');
  const platforms = Object.keys(value.platforms).sort();
  if (platforms.join(',') !== 'linux,macos,windows') {
    throw new Error('manual-releases.json must contain exactly windows, macos, and linux.');
  }
  for (const platform of platforms) {
    const artifact = value.platforms[platform];
    requireObject(artifact, `manual release ${platform}`);
    if (!isPublicHttpsUrl(artifact.url) || !isSha256(artifact.sha256)) {
      throw new Error(`manual release ${platform} has invalid URL or digest metadata.`);
    }
    if (!Number.isSafeInteger(artifact.size) || artifact.size <= 0) {
      throw new Error(`manual release ${platform} has an invalid size.`);
    }
  }
}

function copyInventory(entries, output) {
  for (const entry of entries) {
    const destination = resolve(output, ...entry.relativePath.split('/'));
    if (entry.type === 'directory') {
      mkdirSync(destination);
    } else {
      mkdirSync(dirname(destination), { recursive: true });
      copyFileSync(entry.absolutePath, destination);
    }
  }
}

function assertSeparateTrees(site, feeds, output) {
  const paths = [site, feeds, output];
  for (let left = 0; left < paths.length; left++) {
    for (let right = left + 1; right < paths.length; right++) {
      if (pathsOverlap(paths[left], paths[right])) {
        throw new Error('Website, feed, and output trees must be separate directories.');
      }
    }
  }
}

function pathsOverlap(left, right) {
  if (left === right) return true;
  const leftToRight = relative(left, right);
  const rightToLeft = relative(right, left);
  return isWithin(leftToRight) || isWithin(rightToLeft);
}

function isWithin(value) {
  return value !== '' && value !== '..' && !value.startsWith(`..${sep}`);
}

function requireRegularFile(root, path) {
  const absolutePath = resolve(root, ...path.split('/'));
  if (!existsSync(absolutePath) || !lstatSync(absolutePath).isFile()) {
    throw new Error(`Pages payload is missing required file: ${path}`);
  }
}

function requireSearchMarker(root, path, marker) {
  requireRegularFile(root, path);
  if (!readFileSync(resolve(root, ...path.split('/')), 'utf8').includes(marker)) {
    throw new Error(`${path} is not represented in the unified search index.`);
  }
}

function readJson(root, path) {
  try {
    return JSON.parse(readFileSync(resolve(root, ...path.split('/')), 'utf8'));
  } catch (error) {
    throw new Error(`${path} is not valid JSON: ${error.message}`);
  }
}

function requireObject(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be a JSON object.`);
  }
}

function isPublicHttpsUrl(value) {
  if (typeof value !== 'string' || value.length > 4096) return false;
  try {
    const url = new URL(value);
    return url.protocol === 'https:'
      && url.username === ''
      && url.password === ''
      && url.search === ''
      && url.hash === '';
  } catch {
    return false;
  }
}

function isSha256(value) {
  return typeof value === 'string' && /^[0-9a-f]{64}$/u.test(value);
}

function isIsoTimestamp(value) {
  return typeof value === 'string'
    && /^\d{4}-\d{2}-\d{2}T/u.test(value)
    && Number.isFinite(Date.parse(value));
}

function parseCliArgs(args) {
  const parsed = {};
  for (let index = 0; index < args.length; index += 2) {
    const flag = args[index];
    const value = args[index + 1];
    if (!['--site', '--feeds', '--output'].includes(flag) || !value) {
      throw new Error('Usage: node scripts/assemble-pages.mjs --site path --feeds path --output path');
    }
    parsed[flag.slice(2)] = value;
  }
  if (!parsed.site || !parsed.feeds || !parsed.output) {
    throw new Error('Usage: node scripts/assemble-pages.mjs --site path --feeds path --output path');
  }
  return parsed;
}

if (process.argv[1] && resolve(process.argv[1]) === scriptPath) {
  try {
    const result = assemblePages(parseCliArgs(process.argv.slice(2)));
    console.log(
      `Pages payload: pass (${result.fileCount} files, manual releases: ${result.hasManualReleases ? 'yes' : 'no'})`,
    );
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  }
}
