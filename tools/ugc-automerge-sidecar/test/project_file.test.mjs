import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { gzipSync } from 'node:zlib';

import { newestProjectFile, readProject } from '../project_file.mjs';

test('watch discovery ignores snapshot symlinks', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'robotopia-project-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const outside = path.join(root, 'outside-secret.json');
  const watch = path.join(root, 'watch');
  fs.mkdirSync(watch);
  fs.writeFileSync(outside, JSON.stringify({ secret: true }));
  const safe = path.join(watch, 'safe.JSON');
  fs.writeFileSync(safe, JSON.stringify({ scenes: {} }));
  fs.symlinkSync(outside, path.join(watch, 'newest.json'));

  assert.equal(newestProjectFile(watch), safe);
  assert.deepEqual(readProject(safe), { scenes: {} });
});

test('direct project reads reject symlinks', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'robotopia-project-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const target = path.join(root, 'target.json');
  const linked = path.join(root, 'linked.json');
  fs.writeFileSync(target, '{}');
  fs.symlinkSync(target, linked);

  assert.throws(
    () => readProject(linked),
    /Project snapshot must be a regular file/,
  );
});

test('project reads enforce compressed and expanded byte limits', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'robotopia-project-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const plain = path.join(root, 'oversize.json');
  const compressed = path.join(root, 'bomb.json.gz');
  fs.writeFileSync(plain, JSON.stringify({ padding: 'x'.repeat(256) }));
  fs.writeFileSync(
    compressed,
    gzipSync(JSON.stringify({ padding: 'x'.repeat(4096) })),
  );

  assert.throws(
    () => readProject(plain, { maxBytes: 64 }),
    /64-byte input limit/,
  );
  assert.throws(
    () => readProject(compressed, { maxBytes: 256 }),
    /256-byte expanded limit/,
  );
});
