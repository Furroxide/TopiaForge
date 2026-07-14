import { randomUUID } from 'node:crypto';
import { readSessionFile, SessionFileError } from './session_file.mjs';

export function createSessionLeaseToken() {
  return randomUUID();
}

export function startSessionLeaseMonitor(
  sessionPath,
  expectedToken,
  {
    intervalMilliseconds = 1000,
    onRevoked,
    readSession = readSessionFile,
    reportError = (message) => process.stderr.write(`${message}\n`),
  } = {},
) {
  if (!sessionPath || !expectedToken) {
    return { stop() {} };
  }
  if (typeof onRevoked !== 'function') {
    throw new TypeError('A session lease monitor requires onRevoked.');
  }

  let stopped = false;
  let timer;
  const stop = () => {
    if (stopped) return;
    stopped = true;
    if (timer) clearInterval(timer);
  };
  const revoke = (reason) => {
    if (stopped) return;
    stop();
    Promise.resolve(onRevoked(reason)).catch((error) => {
      reportError(`Session lease shutdown failed: ${error.message}`);
    });
  };
  const check = () => {
    let session;
    try {
      session = readSession(sessionPath);
    } catch (error) {
      revoke(
        error instanceof SessionFileError && error.code === 'missing'
          ? 'session file deleted'
          : error instanceof SessionFileError && error.code === 'changed'
            ? 'session file replaced'
            : 'session file unreadable',
      );
      return;
    }
    if (session?.publisherLeaseToken !== expectedToken) {
      revoke('session lease changed');
    }
  };

  timer = setInterval(check, intervalMilliseconds);
  return { stop };
}
