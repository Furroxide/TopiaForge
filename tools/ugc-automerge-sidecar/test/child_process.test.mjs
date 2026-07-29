import assert from 'node:assert/strict';
import test from 'node:test';

import {
  exitWithin,
  spawnCaptured,
  waitForOutput,
} from '../test_support/child_process.mjs';

test('captured child helpers wait for bounded output and process exit', async () => {
  const context = spawnCaptured(
    process.execPath,
    ['-e', 'process.stdout.write("READY\\n"); process.stderr.write("DONE\\n")'],
    { maxOutputBytes: 64 },
  );
  await waitForOutput(context, 'READY');
  const [code] = await exitWithin(context);
  assert.equal(code, 0);
  assert.match(context.output(), /READY/);
  assert.match(context.output(), /DONE/);
});

test('captured child helpers terminate output that exceeds the configured bound', async () => {
  const context = spawnCaptured(
    process.execPath,
    ['-e', 'process.stdout.write("x".repeat(4096))'],
    { maxOutputBytes: 32 },
  );
  await assert.rejects(
    exitWithin(context),
    /Child-process output exceeded the 32-byte test limit/,
  );
  assert.equal(Buffer.byteLength(context.output()), 32);
});

test('captured child helpers reject invalid output limits before spawning', () => {
  assert.throws(
    () => spawnCaptured(process.execPath, ['--version'], { maxOutputBytes: 0 }),
    /maxOutputBytes must be a positive safe integer/,
  );
});
