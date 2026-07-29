import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const DEFAULT_TIMEOUT_MS = 3000;
const DEFAULT_MAX_OUTPUT_BYTES = 128 * 1024;

export function spawnCaptured(
  command,
  arguments_,
  {
    spawnOptions = {},
    maxOutputBytes = DEFAULT_MAX_OUTPUT_BYTES,
  } = {},
) {
  if (!Number.isSafeInteger(maxOutputBytes) || maxOutputBytes <= 0) {
    throw new TypeError('maxOutputBytes must be a positive safe integer.');
  }

  const child = spawn(command, arguments_, {
    ...spawnOptions,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const chunks = [];
  let outputBytes = 0;
  let failure = null;

  const capture = (chunk) => {
    if (failure) {
      return;
    }
    const bytes = Buffer.from(chunk);
    const remaining = maxOutputBytes - outputBytes;
    if (remaining > 0) {
      const captured = bytes.subarray(0, remaining);
      chunks.push(captured);
      outputBytes += captured.length;
    }
    if (bytes.length > remaining) {
      failure = new Error(
        `Child-process output exceeded the ${maxOutputBytes}-byte test limit.`,
      );
      child.kill('SIGKILL');
    }
  };
  child.stdout.on('data', capture);
  child.stderr.on('data', capture);
  child.once('error', (error) => {
    failure ??= error;
  });

  return {
    child,
    output: () => Buffer.concat(chunks, outputBytes).toString('utf8'),
    failure: () => failure,
  };
}

export async function startSessionFixture(
  t,
  {
    fixture,
    prefix,
    ready = 'READY',
    maxOutputBytes = DEFAULT_MAX_OUTPUT_BYTES,
  },
) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  const sessionPath = path.join(root, 'session.json');
  const context = spawnCaptured(process.execPath, [fixture, sessionPath], {
    maxOutputBytes,
  });
  t.after(() => {
    if (context.child.exitCode == null && context.child.signalCode == null) {
      context.child.kill('SIGKILL');
    }
    fs.rmSync(root, { recursive: true, force: true });
  });

  await waitForOutput(context, ready);
  return { ...context, sessionPath };
}

export async function waitForOutput(
  context,
  expected,
  { timeoutMs = DEFAULT_TIMEOUT_MS, label = 'Fixture' } = {},
) {
  const deadline = Date.now() + timeoutMs;
  while (!context.output().includes(expected)) {
    throwIfCaptureFailed(context);
    if (context.child.exitCode != null || context.child.signalCode != null) {
      throw new Error(
        `${label} exited early (${context.child.exitCode ?? context.child.signalCode}): ${context.output()}`,
      );
    }
    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for ${expected}: ${context.output()}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

export async function exitWithin(
  context,
  {
    timeoutMs = DEFAULT_TIMEOUT_MS,
    message = 'Fixture did not stop within the test timeout.',
  } = {},
) {
  throwIfCaptureFailed(context);
  if (context.child.exitCode != null || context.child.signalCode != null) {
    return [context.child.exitCode, context.child.signalCode];
  }

  const result = await new Promise((resolve, reject) => {
    const onExit = (...exitResult) => {
      clearTimeout(timeout);
      resolve(exitResult);
    };
    const timeout = setTimeout(() => {
      context.child.removeListener('exit', onExit);
      reject(new Error(message));
    }, timeoutMs);
    context.child.once('exit', onExit);
  });
  throwIfCaptureFailed(context);
  return result;
}

function throwIfCaptureFailed(context) {
  const failure = context.failure();
  if (failure) {
    throw failure;
  }
}
