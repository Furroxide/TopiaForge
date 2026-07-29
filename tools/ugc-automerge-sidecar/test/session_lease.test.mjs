import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  exitWithin,
  startSessionFixture,
} from '../test_support/child_process.mjs';

const fixture = fileURLToPath(
  new URL('../test_support/lease_monitor_child.mjs', import.meta.url),
);

test('detached publisher exits when cleanup deletes its session lease', async (t) => {
  const context = await startPublisherFixture(t);
  fs.rmSync(context.sessionPath);

  const [code] = await exitWithin(context, {
    message: 'Publisher did not stop after lease revocation.',
  });
  assert.equal(code, 0);
  assert.match(context.output(), /REVOKED session file deleted/);
});

test('stale publisher exits when a new publisher changes the lease', async (t) => {
  const context = await startPublisherFixture(t);
  fs.writeFileSync(
    context.sessionPath,
    JSON.stringify({ publisherLeaseToken: 'replacement-token' }),
  );

  const [code] = await exitWithin(context, {
    message: 'Publisher did not stop after lease revocation.',
  });
  assert.equal(code, 0);
  assert.match(context.output(), /REVOKED session lease changed/);
});

test('publisher exits when the lease path is replaced during a read', async (t) => {
  const context = await startPublisherFixture(t);
  const replacement = `${context.sessionPath}.replacement`;
  fs.writeFileSync(
    replacement,
    JSON.stringify({ publisherLeaseToken: 'replacement-token' }),
    { mode: 0o600 },
  );
  fs.renameSync(replacement, context.sessionPath);

  const [code] = await exitWithin(context, {
    message: 'Publisher did not stop after lease revocation.',
  });
  assert.equal(code, 0);
  assert.match(context.output(), /REVOKED session (file replaced|lease changed)/);
});

async function startPublisherFixture(t) {
  return startSessionFixture(t, {
    fixture,
    prefix: 'topiaforge-lease-',
  });
}
