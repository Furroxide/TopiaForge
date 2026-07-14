import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const fixture = fileURLToPath(
  new URL('../test_support/signal_cleanup_child.mjs', import.meta.url),
);

test('signal shutdown preserves the shared lease for safe cleanup', async (t) => {
  const context = await startFixture(t);
  context.child.kill('SIGTERM');

  const [code] = await exitWithin(context.child);
  assert.equal(code, 0);
  assert.equal(fs.existsSync(context.sessionPath), true);
  assert.match(context.output(), /SIGNALLED lease-preserved/);
});

test('signal cleanup preserves a replacement publisher session', async (t) => {
  const context = await startFixture(t);
  fs.writeFileSync(
    context.sessionPath,
    JSON.stringify({ publisherLeaseToken: 'replacement-token' }),
  );
  context.child.kill('SIGTERM');

  const [code] = await exitWithin(context.child);
  assert.equal(code, 0);
  assert.equal(fs.existsSync(context.sessionPath), true);
  assert.equal(
    JSON.parse(fs.readFileSync(context.sessionPath, 'utf8')).publisherLeaseToken,
    'replacement-token',
  );
  assert.match(context.output(), /SIGNALLED lease-preserved/);
});

async function startFixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-signal-'));
  const sessionPath = path.join(root, 'session.json');
  const child = spawn(process.execPath, [fixture, sessionPath], {
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '';
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', (chunk) => { output += chunk; });
  child.stderr.on('data', (chunk) => { output += chunk; });
  t.after(() => {
    if (child.exitCode == null) child.kill('SIGKILL');
    fs.rmSync(root, { recursive: true, force: true });
  });
  await waitForOutput(child, () => output, 'READY');
  return { child, sessionPath, output: () => output };
}

async function waitForOutput(child, output, expected) {
  const deadline = Date.now() + 3000;
  while (!output().includes(expected)) {
    if (child.exitCode != null) {
      throw new Error(`Fixture exited early (${child.exitCode}): ${output()}`);
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
      () => reject(new Error('Fixture did not stop after SIGTERM.')),
      3000,
    );
    child.once('exit', (...result) => {
      clearTimeout(timeout);
      resolve(result);
    });
  });
}
