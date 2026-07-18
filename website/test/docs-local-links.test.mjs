import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { routeForOutput } from '../scripts/docs/catalog.mjs';
import { LocalDocumentationLinkRewriter } from '../scripts/docs/local-links.mjs';
import { TemplateSnippetRenderer } from '../scripts/docs/template-snippets.mjs';
import {
  escapeRegExp,
  isExternalOrEmbedded,
  isRegularFile,
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

test('regular-file checks reject directories and symlinks', (t) => {
  const repositoryRoot = createRepository(t);
  const regular = path.join(repositoryRoot, 'regular.json');
  const directory = path.join(repositoryRoot, 'directory');
  const symlink = path.join(repositoryRoot, 'linked.json');
  fs.writeFileSync(regular, '{}');
  fs.mkdirSync(directory);
  fs.symlinkSync(regular, symlink);

  assert.equal(isRegularFile(regular), true);
  assert.equal(isRegularFile(directory), false);
  assert.equal(isRegularFile(symlink), false);
  assert.equal(isRegularFile(path.join(repositoryRoot, 'missing.json')), false);
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

test('documentation links do not publish directories or symlinks', (t) => {
  const repositoryRoot = createRepository(t);
  fs.mkdirSync(path.join(repositoryRoot, 'docs'));
  fs.mkdirSync(path.join(repositoryRoot, 'evidence'));
  const evidence = path.join(repositoryRoot, 'evidence', 'matrix.json');
  fs.writeFileSync(evidence, '{}');
  fs.symlinkSync(evidence, path.join(repositoryRoot, 'evidence', 'linked.json'));
  const failures = [];
  const rewriter = new LocalDocumentationLinkRewriter(
    repositoryRoot,
    new Map(),
    failures,
  );

  rewriter.rewrite(
    'docs/source.md',
    '[directory](../evidence) [symlink](../evidence/linked.json)',
  );

  assert.equal(failures.length, 2);
  assert.ok(failures.every((failure) => failure.includes('not a regular file')));
  assert.deepEqual([...rewriter.publishedAssets], []);
});

test('template snippets reject directories and symlinks before reading', (t) => {
  const repositoryRoot = createRepository(t);
  const templateRoot = path.join(repositoryRoot, 'templates', 'mod', 'minimal');
  const snippetDirectory = path.join(templateRoot, 'snippet-directory');
  const regularSnippet = path.join(templateRoot, 'regular.cs');
  fs.mkdirSync(snippetDirectory, { recursive: true });
  fs.writeFileSync(regularSnippet, 'internal sealed class Sample {}\n');
  fs.symlinkSync(regularSnippet, path.join(templateRoot, 'linked.cs'));
  const failures = [];
  const renderer = new TemplateSnippetRenderer(repositoryRoot, failures);

  assert.equal(renderer.render('docs/source.md', 'templates/mod/minimal/snippet-directory'), '');
  assert.equal(renderer.render('docs/source.md', 'templates/mod/minimal/linked.cs'), '');
  assert.equal(failures.length, 2);
  assert.ok(failures.every((failure) => failure.includes('not a regular file')));
});

function createRepository(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-doc-links-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
