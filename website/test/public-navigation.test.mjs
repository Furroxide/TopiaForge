import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { pages } from '../scripts/docs/catalog.mjs';

const configPath = fileURLToPath(new URL('../astro.config.mjs', import.meta.url));
const configSource = readFileSync(configPath, 'utf8');

test('creator guides are published and discoverable in the public sidebar', () => {
  const routesBySource = new Map(
    pages.map((page) => [page.sourcePath, page.outputPath]),
  );

  assert.equal(routesBySource.get('docs/CreatorTools.md'), 'guides/creator-tools.md');
  assert.equal(routesBySource.get('docs/Sandbox.md'), 'guides/sandbox.md');
  assert.match(configSource, /slug: 'guides\/creator-tools'/u);
  assert.match(configSource, /slug: 'guides\/sandbox'/u);
});
