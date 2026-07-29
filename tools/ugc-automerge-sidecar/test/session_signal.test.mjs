import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  exitWithin,
  startSessionFixture,
} from '../test_support/child_process.mjs';

const fixture = fileURLToPath(
  new URL('../test_support/signal_cleanup_child.mjs', import.meta.url),
);

test(
  'signal shutdown preserves the shared lease for safe cleanup',
  {
    skip: process.platform === 'win32'
      ? 'Windows terminates child processes instead of delivering POSIX SIGTERM.'
      : false,
  },
  async (t) => {
    const context = await startFixture(t);
    context.child.kill('SIGTERM');

    const [code] = await exitWithin(context, {
      message: 'Fixture did not stop after SIGTERM.',
    });
    assert.equal(code, 0);
    assert.equal(fs.existsSync(context.sessionPath), true);
    assert.match(context.output(), /SIGNALLED lease-preserved/);
  },
);

test(
  'signal cleanup preserves a replacement publisher session',
  {
    skip: process.platform === 'win32'
      ? 'Windows terminates child processes instead of delivering POSIX SIGTERM.'
      : false,
  },
  async (t) => {
    const context = await startFixture(t);
    fs.writeFileSync(
      context.sessionPath,
      JSON.stringify({ publisherLeaseToken: 'replacement-token' }),
    );
    context.child.kill('SIGTERM');

    const [code] = await exitWithin(context, {
      message: 'Fixture did not stop after SIGTERM.',
    });
    assert.equal(code, 0);
    assert.equal(fs.existsSync(context.sessionPath), true);
    assert.equal(
      JSON.parse(fs.readFileSync(context.sessionPath, 'utf8')).publisherLeaseToken,
      'replacement-token',
    );
    assert.match(context.output(), /SIGNALLED lease-preserved/);
  },
);

async function startFixture(t) {
  return startSessionFixture(t, {
    fixture,
    prefix: 'topiaforge-signal-',
  });
}
