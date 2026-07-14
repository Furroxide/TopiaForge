import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const fixture = fileURLToPath(
  new URL('../test_support/lease_monitor_child.mjs', import.meta.url),
);

test('detached publisher exits when cleanup deletes its session lease', async (t) => {
  const context = await startPublisherFixture(t);
  fs.rmSync(context.sessionPath);

  const [code] = await exitWithin(context.child);
  assert.equal(code, 0);
  assert.match(context.output(), /REVOKED session file deleted/);
});

test('stale publisher exits when a new publisher changes the lease', async (t) => {
  const context = await startPublisherFixture(t);
  fs.writeFileSync(
    context.sessionPath,
    JSON.stringify({ publisherLeaseToken: 'replacement-token' }),
  );

  const [code] = await exitWithin(context.child);
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

  const [code] = await exitWithin(context.child);
  assert.equal(code, 0);
  assert.match(context.output(), /REVOKED session (file replaced|lease changed)/);
});

async function startPublisherFixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'robotopia-lease-'));
  const sessionPath = path.join(root, 'session.json');
  const child = spawn(process.execPath, [fixture, sessionPath], {
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '';
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', (chunk) => {
    output += chunk;
  });
  child.stderr.on('data', (chunk) => {
    output += chunk;
  });
  t.after(() => {
    if (child.exitCode == null) child.kill();
    fs.rmSync(root, { recursive: true, force: true });
  });

  await waitForOutput(child, () => output, 'READY');
  return { child, sessionPath, output: () => output };
}

async function waitForOutput(child, output, expected) {
  const deadline = Date.now() + 3000;
  while (!output().includes(expected)) {
    if (child.exitCode != null) {
      throw new Error(`Publisher fixture exited early (${child.exitCode}): ${output()}`);
    }
    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for ${expected}: ${output()}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

async function exitWithin(child) {
  if (child.exitCode != null) return [child.exitCode, child.signalCode];
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(
      () => reject(new Error('Publisher did not stop after lease revocation.')),
      3000,
    );
    child.once('exit', (...result) => {
      clearTimeout(timeout);
      resolve(result);
    });
  });
}
