import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  assemblePages,
  inventory,
} from '../scripts/assemble-pages.mjs';

test('assembles a fresh bootstrap payload without a release catalog', (t) => {
  const fixture = createFixture(t);
  const result = assemblePages(fixture);

  assert.equal(result.hasManualReleases, false);
  assert.equal(fs.existsSync(path.join(fixture.output, '.nojekyll')), true);
  assert.equal(fs.existsSync(path.join(fixture.output, 'manual-releases.json')), false);
  assert.equal(fs.existsSync(path.join(fixture.output, 'index.json')), false);
  assert.equal(fs.existsSync(path.join(fixture.output, 'registry/index.json')), true);
});

test('accepts a complete stable manual release catalog', (t) => {
  const fixture = createFixture(t, { manual: true });
  const result = assemblePages(fixture);
  assert.equal(result.hasManualReleases, true);
  assert.equal(fs.existsSync(path.join(fixture.output, 'manual-releases.json')), true);
});

test('rejects path collisions and leaves no partial output', (t) => {
  const fixture = createFixture(t);
  write(fixture.site, 'collision.txt', 'site');
  write(fixture.feeds, 'collision.txt', 'feeds');

  assert.throws(() => assemblePages(fixture), /path collision/u);
  assert.equal(fs.existsSync(fixture.output), false);
});

test('rejects links, special inputs, retired index.json, and stale output', (t) => {
  const linked = createFixture(t);
  const target = path.join(linked.site, 'target.txt');
  fs.writeFileSync(target, 'target');
  fs.symlinkSync(target, path.join(linked.site, 'linked.txt'));
  assert.throws(() => inventory(linked.site), /symbolic link/u);

  const retired = createFixture(t);
  write(retired.site, 'index.json', '{}');
  assert.throws(() => assemblePages(retired), /Retired Pages root file/u);
  assert.equal(fs.existsSync(retired.output), false);

  const stale = createFixture(t);
  fs.mkdirSync(stale.output);
  assert.throws(() => assemblePages(stale), /must not already exist/u);
});

test('rejects malformed registry and incomplete release shapes', (t) => {
  const registry = createFixture(t);
  writeJson(registry.feeds, 'registry/index.json', {
    ...validRegistry(),
    mods: [{ origin: 'community' }],
  });
  assert.throws(() => assemblePages(registry), /manifest must be a JSON object/u);

  const manual = createFixture(t, { manual: true });
  const catalog = JSON.parse(
    fs.readFileSync(path.join(manual.feeds, 'manual-releases.json'), 'utf8'),
  );
  delete catalog.platforms.linux;
  writeJson(manual.feeds, 'manual-releases.json', catalog);
  assert.throws(() => assemblePages(manual), /exactly windows, macos, and linux/u);
});

function createFixture(t, { manual = false } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-pages-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const site = path.join(root, 'site');
  const feeds = path.join(root, 'feeds');
  const output = path.join(root, 'output');
  fs.mkdirSync(site);
  fs.mkdirSync(feeds);

  write(site, 'index.html', '<main data-pagefind-body>Portal</main>');
  write(site, 'api/csharp/index.html', '<article id="_content" data-pagefind-body>C#</article>');
  write(site, 'api/dart/index.html', '<main data-pagefind-body>Dart</main>');
  for (const packageName of ['launcher_domain', 'launcher_data', 'launcher_ui']) {
    write(site, `api/dart/${packageName}/index.html`, '<meta charset="utf-8">');
    write(
      site,
      `api/dart/${packageName}/${packageName}/index.html`,
      '<div id="dartdoc-main-content" data-pagefind-body>Dart library</div>',
    );
  }
  write(site, 'pagefind/pagefind.js', 'export const options = {};');
  writeJson(site, 'pagefind/pagefind-entry.json', { languages: {} });
  writeJson(site, 'source/docs/capability-matrix.json', { capabilities: [] });
  writeJson(site, 'source/tests/live-game-acceptance.json', { tests: [] });

  write(feeds, '.nojekyll', '');
  writeJson(feeds, 'registry/index.json', validRegistry());
  if (manual) {
    writeJson(feeds, 'manual-releases.json', validManualCatalog());
  }
  return { site, feeds, output };
}

function validRegistry() {
  return {
    $schema: 'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.registry-index.schema.json',
    formatVersion: 2,
    name: 'TopiaForge Mod Registry',
    generatedAt: '2026-07-16T12:00:00.000Z',
    sourceRepository: 'https://github.com/furroxide/TopiaForge',
    mods: [],
  };
}

function validManualCatalog() {
  return {
    $schema: 'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.manual-releases.schema.json',
    formatVersion: 2,
    manualOnly: true,
    generatedAt: '2026-07-16T12:00:00.000Z',
    releaseUrl: 'https://github.com/furroxide/TopiaForge/releases/tag/v1.0.0',
    platforms: Object.fromEntries(
      ['windows', 'macos', 'linux'].map((platform) => [
        platform,
        {
          url: `https://github.com/furroxide/TopiaForge/releases/download/v1.0.0/${platform}.zip`,
          sha256: 'a'.repeat(64),
          size: 100,
        },
      ]),
    ),
  };
}

function writeJson(root, relativePath, value) {
  write(root, relativePath, `${JSON.stringify(value)}\n`);
}

function write(root, relativePath, content) {
  const destination = path.join(root, ...relativePath.split('/'));
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  fs.writeFileSync(destination, content);
}
