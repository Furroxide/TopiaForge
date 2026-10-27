import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { pages, routeForOutput } from '../scripts/docs/catalog.mjs';
import { LocalDocumentationLinkRewriter } from '../scripts/docs/local-links.mjs';

const configPath = fileURLToPath(new URL('../astro.config.mjs', import.meta.url));
const configSource = readFileSync(configPath, 'utf8');

test('creator guides are published and discoverable in the public sidebar', () => {
  const routesBySource = new Map(
    pages.map((page) => [page.sourcePath, page.outputPath]),
  );

  // The standalone Creator Tools guide went with its package; Sandbox owns the workbench.
  assert.equal(routesBySource.get('docs/CreatorTools.md'), undefined);
  assert.equal(routesBySource.get('docs/Sandbox.md'), 'guides/sandbox.md');
  assert.doesNotMatch(configSource, /slug: 'guides\/creator-tools'/u);
  assert.match(configSource, /slug: 'guides\/sandbox'/u);
});

test('saved map guidance is published and discoverable for players', () => {
  const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));
  const sourceToRoute = new Map(pages.map((page) => [page.sourcePath, routeForOutput(page.outputPath)]));
  const route = sourceToRoute.get('docs/CreatorCloud.md');
  assert.equal(route, '/guides/saved-published-maps/');
  const failures = [];
  const links = new LocalDocumentationLinkRewriter(repositoryRoot, sourceToRoute, failures);
  const source = readFileSync(new URL('../../docs/MapWorlds.md', import.meta.url), 'utf8');
  assert.ok(links.rewrite('docs/MapWorlds.md', source).includes(`](${route})`));
  assert.deepEqual(failures, []);
  assert.match(configSource, /label: 'Play',[\s\S]*?slug: 'guides\/saved-published-maps'/u);
});

test('map world guidance connects manifest and diagnostics links to the author sidebar', () => {
  const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));
  const sourceToRoute = new Map(
    pages.map((page) => [page.sourcePath, routeForOutput(page.outputPath)]),
  );
  const route = sourceToRoute.get('docs/MapWorlds.md');
  assert.equal(route, '/guides/map-worlds/');
  const failures = [];
  const links = new LocalDocumentationLinkRewriter(repositoryRoot, sourceToRoute, failures);
  for (const source of ['docs/ManifestV6.md', 'docs/Diagnostics.md']) {
    const content = readFileSync(new URL(`../../${source}`, import.meta.url), 'utf8');
    assert.ok(content.includes('(MapWorlds.md)'), source);
    assert.ok(links.rewrite(source, content).includes(`](${route})`), source);
  }
  assert.deepEqual(failures, []);
  const authorGuides = /label: 'Build mods',\s*items: \[([\s\S]*?)\],/u.exec(configSource);
  assert.ok(authorGuides?.[1].includes(`slug: '${route.slice(1, -1)}'`),
    'map world guide is discoverable in the author sidebar');
});

test('Players guide publication connects incoming links and the author sidebar', () => {
  const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));
  const preparation = spawnSync(
    process.execPath,
    ['website/scripts/prepare-docs.mjs', '--check'],
    { cwd: repositoryRoot, encoding: 'utf8' },
  );
  assert.ifError(preparation.error);
  assert.equal(preparation.status, 0, preparation.stdout + preparation.stderr);

  const sourceToRoute = new Map(
    pages.map((page) => [page.sourcePath, routeForOutput(page.outputPath)]),
  );
  const route = sourceToRoute.get('docs/Players.md');
  assert.equal(route, '/guides/players/');
  const failures = [];
  const links = new LocalDocumentationLinkRewriter(repositoryRoot, sourceToRoute, failures);
  for (const source of ['docs/Multiplayer.md', 'docs/TestingMods.md']) {
    const content = readFileSync(new URL(`../../${source}`, import.meta.url), 'utf8');
    assert.ok(content.includes('[Act on players](Players.md)'), source);
    assert.ok(links.rewrite(source, content).includes(`[Act on players](${route})`), source);
  }
  assert.deepEqual(failures, []);

  const authorGuides = /label: 'Build mods',\s*items: \[([\s\S]*?)\],/u.exec(configSource);
  assert.ok(authorGuides, 'author guide sidebar exists');
  assert.ok(authorGuides[1].includes(`slug: '${route.slice(1, -1)}'`),
    'Players guide is discoverable in the author sidebar');
});
