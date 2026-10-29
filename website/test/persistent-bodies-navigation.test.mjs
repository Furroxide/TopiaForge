import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { pages, routeForOutput } from '../scripts/docs/catalog.mjs';
import { LocalDocumentationLinkRewriter } from '../scripts/docs/local-links.mjs';

test('persistent body guidance is published, linked and discoverable', () => {
  const routes = new Map(pages.map(page => [page.sourcePath, routeForOutput(page.outputPath)]));
  assert.equal(routes.get('docs/PersistentBodies.md'), '/guides/persistent-bodies/');
  const failures = [];
  const root = new URL('../../', import.meta.url);
  const links = new LocalDocumentationLinkRewriter(fileURLToPath(root), routes, failures);
  const source = readFileSync(new URL('../../docs/RobotKit.md', import.meta.url), 'utf8');
  assert.ok(links.rewrite('docs/RobotKit.md', source).includes('](/guides/persistent-bodies/)'));
  assert.deepEqual(failures, []);
  const config = readFileSync(new URL('../astro.config.mjs', import.meta.url), 'utf8');
  assert.match(config, /label: 'Build mods',[\s\S]*?slug: 'guides\/persistent-bodies'/u);
});
