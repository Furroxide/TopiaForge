import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { checkRepositoryData } from '../scripts/lib/repository-data.mjs';

test('repository-data audit parses JSON and bounded YAML aliases', (t) => {
  const repositoryRoot = createRepository(t);
  fs.writeFileSync(path.join(repositoryRoot, 'valid.json'), '{"safe":true}\n');
  fs.writeFileSync(
    path.join(repositoryRoot, 'valid.yml'),
    'defaults: &defaults\n  enabled: true\nprofile:\n  <<: *defaults\n',
  );

  assert.deepEqual(
    checkRepositoryData({
      repositoryRoot,
      sourcePaths: ['valid.json', 'valid.yml'],
    }),
    { failures: [], parsedCount: 2 },
  );
});

test('repository-data audit reports malformed JSON and duplicate YAML keys', (t) => {
  const repositoryRoot = createRepository(t);
  fs.writeFileSync(path.join(repositoryRoot, 'invalid.json'), '{');
  fs.writeFileSync(path.join(repositoryRoot, 'invalid.yaml'), 'key: one\nkey: two\n');

  const result = checkRepositoryData({
    repositoryRoot,
    sourcePaths: ['invalid.json', 'invalid.yaml'],
  });
  assert.equal(result.parsedCount, 0);
  assert.equal(result.failures.length, 2);
  assert.match(result.failures[0], /invalid\.json/);
  assert.match(result.failures[1], /Map keys must be unique/);
});

test('repository-data audit rejects links and oversized files', (t) => {
  const repositoryRoot = createRepository(t);
  fs.writeFileSync(path.join(repositoryRoot, 'target.json'), '{}');
  fs.symlinkSync('target.json', path.join(repositoryRoot, 'linked.json'));
  fs.writeFileSync(path.join(repositoryRoot, 'large.json'), '{"padding":"xxxx"}');

  const result = checkRepositoryData({
    repositoryRoot,
    sourcePaths: ['linked.json', 'large.json'],
    maxFileBytes: 8,
  });
  assert.equal(result.failures.length, 2);
  assert.match(result.failures[0], /must be a regular file/);
  assert.match(result.failures[1], /exceeds the 8-byte/);
});

test('repository-data audit rejects sources outside the repository', (t) => {
  const parent = createRepository(t);
  const repositoryRoot = path.join(parent, 'repository');
  fs.mkdirSync(repositoryRoot);
  fs.writeFileSync(path.join(parent, 'outside.json'), '{}');

  const result = checkRepositoryData({
    repositoryRoot,
    sourcePaths: ['../outside.json'],
  });
  assert.deepEqual(result, {
    failures: ['../outside.json: source escapes the repository'],
    parsedCount: 0,
  });
});

function createRepository(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-data-audit-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
