import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { readBoundedDescriptor } from '../bounded_read.mjs';

test('bounded descriptor reads preserve bytes across small chunks', (t) => {
  const { descriptor } = openFixture(t, Buffer.from('Robotopia'));
  const bytes = readBoundedDescriptor(descriptor, {
    maxBytes: 32,
    chunkBytes: 3,
  });
  assert.equal(bytes.toString('utf8'), 'Robotopia');
});

test('bounded descriptor reads use the caller error when the stream grows past the limit', (t) => {
  const { descriptor } = openFixture(t, Buffer.from('too large'));
  assert.throws(
    () => readBoundedDescriptor(descriptor, {
      maxBytes: 4,
      chunkBytes: 3,
      createTooLargeError: () => new RangeError('fixture limit'),
    }),
    (error) => error instanceof RangeError && error.message === 'fixture limit',
  );
});

test('bounded descriptor reads reject invalid limits before allocating', () => {
  assert.throws(
    () => readBoundedDescriptor(0, { maxBytes: 0 }),
    /maxBytes must be a positive safe integer/,
  );
  assert.throws(
    () => readBoundedDescriptor(0, { maxBytes: 1, chunkBytes: 0 }),
    /chunkBytes must be a positive safe integer/,
  );
});

function openFixture(t, bytes) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-bounded-read-'));
  const file = path.join(root, 'input.bin');
  fs.writeFileSync(file, bytes);
  const descriptor = fs.openSync(file, 'r');
  t.after(() => {
    fs.closeSync(descriptor);
    fs.rmSync(root, { recursive: true, force: true });
  });
  return { descriptor };
}
