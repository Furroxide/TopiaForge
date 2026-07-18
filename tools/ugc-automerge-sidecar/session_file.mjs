import {
  closeSync,
  constants,
  existsSync,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';

import { readBoundedDescriptor } from './bounded_read.mjs';

export const DEFAULT_MAX_SESSION_BYTES = 64 * 1024;

export class SessionFileError extends Error {
  constructor(code, message, options = undefined) {
    super(message, options);
    this.name = 'SessionFileError';
    this.code = code;
  }
}

const defaultFileSystem = {
  closeSync,
  fstatSync,
  existsSync,
  lstatSync,
  openSync,
  readSync,
  renameSync,
  rmSync,
  writeFileSync,
};

/// Reads a session lease through a stable regular-file descriptor. The path is
/// checked again after the bounded read so a same-name replacement cannot make
/// a stale publisher accept the old lease it happened to open.
export function readSessionFile(
  sessionPath,
  {
    maxBytes = DEFAULT_MAX_SESSION_BYTES,
    fileSystem = defaultFileSystem,
    platform = process.platform,
  } = {},
) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes <= 0) {
    throw new TypeError('Session maxBytes must be a positive safe integer.');
  }

  let before;
  try {
    before = fileSystem.lstatSync(sessionPath);
  } catch (error) {
    throw sessionReadError(error?.code === 'ENOENT' ? 'missing' : 'unreadable', sessionPath, error);
  }
  if (!before.isFile()) {
    throw new SessionFileError(
      'unsafe-type',
      `Session lease must be a regular file: ${sessionPath}`,
    );
  }

  let descriptor;
  try {
    descriptor = fileSystem.openSync(
      sessionPath,
      constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0),
    );
  } catch (error) {
    throw sessionReadError('unreadable', sessionPath, error);
  }

  let bytes;
  let opened;
  try {
    opened = fileSystem.fstatSync(descriptor);
    requireSameRegularFile(before, opened, sessionPath);
    if (opened.size > maxBytes) {
      throw new SessionFileError(
        'too-large',
        `Session lease exceeds the ${maxBytes}-byte limit: ${sessionPath}`,
      );
    }
    bytes = readBoundedDescriptor(descriptor, {
      maxBytes,
      chunkBytes: 4096,
      fileSystem,
      createTooLargeError: () => new SessionFileError(
        'too-large',
        `Session lease exceeds the ${maxBytes}-byte limit: ${sessionPath}`,
      ),
    });
    const afterRead = fileSystem.fstatSync(descriptor);
    if (afterRead.size !== opened.size || afterRead.mtimeMs !== opened.mtimeMs) {
      throw new SessionFileError(
        'changed',
        `Session lease changed while it was being read: ${sessionPath}`,
      );
    }
  } finally {
    fileSystem.closeSync(descriptor);
  }

  let finalPath;
  try {
    finalPath = fileSystem.lstatSync(sessionPath);
  } catch (error) {
    throw sessionReadError(error?.code === 'ENOENT' ? 'missing' : 'unreadable', sessionPath, error);
  }
  requireSameRegularFile(opened, finalPath, sessionPath);
  if (platform !== 'win32' && (finalPath.mode & 0o077) !== 0) {
    throw new SessionFileError(
      'permissions',
      `Session lease permissions must not allow group or other access: ${sessionPath}`,
    );
  }

  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch (error) {
    throw new SessionFileError(
      'invalid-utf8',
      `Session lease is not valid UTF-8: ${sessionPath}`,
      { cause: error },
    );
  }
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  let session;
  try {
    session = JSON.parse(text);
  } catch (error) {
    throw new SessionFileError(
      'invalid-json',
      `Session lease is not valid JSON: ${sessionPath}`,
      { cause: error },
    );
  }
  if (typeof session !== 'object' || session === null || Array.isArray(session)) {
    throw new SessionFileError(
      'invalid-shape',
      `Session lease JSON must be an object: ${sessionPath}`,
    );
  }
  return session;
}

function requireSameRegularFile(expected, actual, sessionPath) {
  if (
    !actual.isFile() ||
    actual.dev !== expected.dev ||
    actual.ino !== expected.ino
  ) {
    throw new SessionFileError(
      'changed',
      `Session lease was replaced while it was being opened: ${sessionPath}`,
    );
  }
}

function sessionReadError(code, sessionPath, cause) {
  return new SessionFileError(
    code,
    code === 'missing'
      ? `Session lease was deleted: ${sessionPath}`
      : `Session lease could not be read: ${sessionPath}`,
    { cause },
  );
}

// Replaces the live connection snapshot with a same-directory rename so readers
// never observe partial JSON. Windows occasionally refuses rename-over-existing;
// retry there after removing the old regular file.
export function writeSessionFile(
  sessionPath,
  session,
  {
    fileSystem = defaultFileSystem,
    platform = process.platform,
    reportError = (message) => process.stderr.write(`${message}\n`),
  } = {},
) {
  if (!sessionPath) return true;

  const tmp = path.join(
    path.dirname(sessionPath),
    `.${path.basename(sessionPath)}.${process.pid}.${Date.now()}.tmp`,
  );
  try {
    fileSystem.writeFileSync(tmp, JSON.stringify(session, null, 2), {
      encoding: 'utf8',
      flag: 'wx',
      mode: 0o600,
    });
    try {
      fileSystem.renameSync(tmp, sessionPath);
    } catch (error) {
      const retryable =
        platform === 'win32' &&
        fileSystem.existsSync(sessionPath) &&
        ['EACCES', 'EEXIST', 'EPERM'].includes(error?.code);
      if (!retryable) throw error;
      fileSystem.rmSync(sessionPath, { force: true });
      fileSystem.renameSync(tmp, sessionPath);
    }
    return true;
  } catch (error) {
    reportError(`Could not write session file: ${error.message}`);
    return false;
  } finally {
    if (fileSystem.existsSync(tmp)) {
      try {
        fileSystem.rmSync(tmp, { force: true });
      } catch {
        // Best-effort cleanup must not mask the original write outcome.
      }
    }
  }
}

// A session marker is a success signal consumed by the launcher/CLI. Never
// announce it until the optional lease file has been committed successfully.
export function establishPublisherSession(
  sessionPath,
  session,
  publicSession,
  {
    writeSession = writeSessionFile,
    announce = (line) => process.stdout.write(line),
  } = {},
) {
  if (!writeSession(sessionPath, session)) return false;
  announce(`TOPIAFORGE_UGC_SESSION ${JSON.stringify(publicSession)}\n`);
  return true;
}
