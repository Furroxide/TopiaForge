import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  checkMarkdownLinks,
  extractMarkdownTargets,
  githubAnchors,
} from '../scripts/lib/markdown-links.mjs';

test('GitHub-style anchors normalize markup, Unicode, and duplicates', () => {
  const anchors = githubAnchors(`
# Mixed Case Heading
## [Crème brûlée](https://example.test)
## Repeat
## Repeat
\`\`\`
# Ignored
\`\`\`
`);

  assert.deepEqual([...anchors], [
    'mixed-case-heading',
    'crème-brûlée',
    'repeat',
    'repeat-1',
  ]);
});

test('target extraction covers inline, reference, and HTML links but skips code', () => {
  const targets = extractMarkdownTargets(`
[inline](guide.md#start "title")
[reference]: <reference guide.md>
<img src="images/robot.png">
\`[ignored](missing.md)\`
\`\`\`
[also ignored](missing.md)
\`\`\`
`);

  assert.deepEqual(targets.map(({ target }) => target), [
    'guide.md#start',
    '<reference guide.md>',
    'images/robot.png',
  ]);
});

test('source-link audit accepts repository links and validates duplicate anchors', (t) => {
  const repositoryRoot = createRepository(t);
  fs.mkdirSync(path.join(repositoryRoot, 'docs'));
  fs.mkdirSync(path.join(repositoryRoot, 'assets'));
  fs.writeFileSync(path.join(repositoryRoot, 'assets', 'robot.png'), 'image');
  fs.writeFileSync(path.join(repositoryRoot, 'README.md'), '# Home\n');
  fs.writeFileSync(
    path.join(repositoryRoot, 'docs', 'guide.md'),
    '# Start\n## Repeat\n## Repeat\n',
  );
  fs.writeFileSync(
    path.join(repositoryRoot, 'docs', 'source.md'),
    [
      '[guide](guide.md#repeat-1)',
      '[root](/README.md#home)',
      '![asset](../assets/robot.png)',
      '[external](https://example.test)',
      '`[ignored](missing.md)`',
      '```',
      '[ignored too](missing.md)',
      '```',
    ].join('\n'),
  );

  assert.deepEqual(
    checkMarkdownLinks({
      repositoryRoot,
      sourcePaths: ['README.md', 'docs/guide.md', 'docs/source.md'],
    }),
    [],
  );
});

test('source-link audit reports bad encoding, missing files, anchors, and escapes', (t) => {
  const parent = createRepository(t);
  const repositoryRoot = path.join(parent, 'repository');
  fs.mkdirSync(repositoryRoot);
  fs.writeFileSync(path.join(parent, 'outside.md'), '# Outside\n');
  fs.writeFileSync(path.join(repositoryRoot, 'guide.md'), '# Existing\n');
  fs.writeFileSync(
    path.join(repositoryRoot, 'source.md'),
    [
      '[missing](missing.md)',
      '[anchor](guide.md#absent)',
      '[encoding](%ZZ.md)',
      '[escape](../outside.md)',
    ].join('\n'),
  );

  const failures = checkMarkdownLinks({
    repositoryRoot,
    sourcePaths: ['source.md'],
  });
  assert.equal(failures.length, 4);
  assert.match(failures[0], /missing target/);
  assert.match(failures[1], /missing anchor #absent/);
  assert.match(failures[2], /invalid URL encoding/);
  assert.match(failures[3], /escapes the repository/);
});

test('source-link audit allows directories but rejects symlink sources and targets', (t) => {
  const repositoryRoot = createRepository(t);
  const directory = path.join(repositoryRoot, 'directory');
  const target = path.join(repositoryRoot, 'target.md');
  const source = path.join(repositoryRoot, 'source.md');
  fs.mkdirSync(directory);
  fs.writeFileSync(target, '# Target\n');
  fs.symlinkSync(target, path.join(repositoryRoot, 'linked-target.md'));
  fs.writeFileSync(
    source,
    '[directory](directory) [symlink](linked-target.md#target)',
  );
  fs.symlinkSync(source, path.join(repositoryRoot, 'linked-source.md'));

  const failures = checkMarkdownLinks({
    repositoryRoot,
    sourcePaths: ['source.md', 'linked-source.md'],
  });

  assert.equal(failures.length, 2);
  assert.match(failures[0], /target is not a regular file: linked-target\.md#target/);
  assert.match(failures[1], /source is not a regular file/);
});

function createRepository(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-markdown-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
