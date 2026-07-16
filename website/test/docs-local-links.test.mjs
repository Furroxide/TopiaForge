import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { routeForOutput } from '../scripts/docs/catalog.mjs';
import { LocalDocumentationLinkRewriter } from '../scripts/docs/local-links.mjs';
import {
  escapeRegExp,
  isExternalOrEmbedded,
  isWithinRoot,
} from '../scripts/lib/link-utils.mjs';

test('documentation routes preserve the Starlight home and page conventions', () => {
  assert.equal(routeForOutput('index.md'), '/');
  assert.equal(routeForOutput('guides/first-mod.md'), '/guides/first-mod/');
});

test('shared link helpers classify targets, roots, and literal anchors', () => {
  assert.equal(isExternalOrEmbedded('https://robotopia.gg/'), true);
  assert.equal(isExternalOrEmbedded('/guides/first-mod/'), false);
  assert.equal(isWithinRoot('/repo', '/repo/docs/page.md'), true);
  assert.equal(isWithinRoot('/repo', '/outside/page.md'), false);
  assert.equal(escapeRegExp('section[1].'), 'section\\[1\\]\\.');
});

test('documentation links rewrite published pages and mirror source evidence', (t) => {
  const repositoryRoot = createRepository(t);
  fs.mkdirSync(path.join(repositoryRoot, 'docs'));
  fs.mkdirSync(path.join(repositoryRoot, 'evidence'));
  fs.writeFileSync(path.join(repositoryRoot, 'docs', 'target.md'), '# Target\n');
  fs.writeFileSync(path.join(repositoryRoot, 'evidence', 'matrix.json'), '{}');
  const failures = [];
  const rewriter = new LocalDocumentationLinkRewriter(
    repositoryRoot,
    new Map([['docs/target.md', '/guides/target/']]),
    failures,
  );

  const rendered = rewriter.rewrite(
    'docs/source.md',
    '[guide](target.md#section) [evidence](../evidence/matrix.json)',
  );

  assert.equal(
    rendered,
    '[guide](/guides/target/#section) [evidence](/source/evidence/matrix.json)',
  );
  assert.deepEqual(failures, []);
  assert.deepEqual([...rewriter.publishedAssets.keys()], ['evidence/matrix.json']);
});

test('documentation links reject unpublished, escaped, and malformed targets', (t) => {
  const parent = createRepository(t);
  const repositoryRoot = path.join(parent, 'repository');
  fs.mkdirSync(path.join(repositoryRoot, 'docs'), { recursive: true });
  fs.writeFileSync(path.join(repositoryRoot, 'docs', 'unpublished.md'), '# Hidden\n');
  fs.writeFileSync(path.join(parent, 'outside.json'), '{}');
  const failures = [];
  const rewriter = new LocalDocumentationLinkRewriter(
    repositoryRoot,
    new Map(),
    failures,
  );

  rewriter.rewrite(
    'docs/source.md',
    '[hidden](unpublished.md) [escape](../../outside.json) [bad](%ZZ.json)',
  );

  assert.equal(failures.length, 3);
  assert.match(failures[0], /not published by Starlight/);
  assert.match(failures[1], /escapes the repository/);
  assert.match(failures[2], /invalid URL encoding/);
});

function createRepository(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-doc-links-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
